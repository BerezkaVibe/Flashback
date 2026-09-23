using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Flashback;

public sealed class AudioLoopback : IRecordingAudio
{
    private readonly MMDeviceEnumerator enumerator = new();
    private readonly string deviceId;
    private readonly bool microphone;
    private MMDevice? device;
    private TimestampedAudioCapture? capture;
    private WasapiOut? silence;
    private readonly NamedPipeServerStream pipe = null!;
    private readonly CancellationTokenSource cancel = new();
    private readonly Channel<byte[]> packets = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(2048) { SingleReader = true, SingleWriter = false });
    // Live packets and held silence both feed the clock and the pipe queue.
    private readonly object feed = new();
    private Task? pump, keeper;
    private bool disposed;
    public bool Muted { get => Volatile.Read(ref muted); set => Volatile.Write(ref muted, value); }
    private bool muted;
    // 1.0 is unchanged; applied to captured samples before they are queued.
    public double Gain { get => Volatile.Read(ref gain); set => Volatile.Write(ref gain, Math.Clamp(value, 0, 2)); }
    private double gain = 1;
    // A held (locked) source never switches devices and records silence while its device is missing.
    public bool Hold
    {
        get => deviceWatch?.Hold == true;
        set { if (deviceWatch != null) { deviceWatch.Hold = value; if (value) deviceWatch.FollowDefault = false; } }
    }
    public bool Holding => Volatile.Read(ref holding);
    private bool holding;
    private int lostSignal;
    internal static void ApplyMute(byte[] samples, bool mute) { if (mute) Array.Clear(samples); }
    private long lastPacketTick;
    private Func<long>? timelineOrigin;
    private AudioClockNormalizer? clockNormalizer;
    private long queuedBytes;
    internal const string BacklogMessage="Audio input backlog requires reconnection.";
    public string SyncReport => clockNormalizer==null ? "Clock pending" : $"Clock error {clockNormalizer.ErrorMilliseconds:0.000} ms; corrected {clockNormalizer.CorrectedFrames} frames; gaps {clockNormalizer.GapFrames}; queued {Interlocked.Read(ref queuedBytes)} bytes{(Holding ? "; holding for device" : "")}";
    private readonly AudioDeviceWatch? deviceWatch;
    public string DeviceName { get; } = "";
    public string DeviceId => deviceId;
    public bool DeviceChanged => deviceWatch?.Changed == true;
    public const string DeviceChangedMessage = "The Windows playback device changed. Reconnecting audio capture.";
    public const string StreamStalledMessage = "The desktop audio stream stopped delivering samples. Reconnecting audio capture.";
    public const string MicrophoneChangedMessage = "The microphone device changed. Reconnecting audio capture.";
    public const string MicrophoneStalledMessage = "The microphone stream stopped delivering samples. Reconnecting audio capture.";
    public bool StreamStalled => !Hold && Volatile.Read(ref lastPacketTick) != 0 && Stopwatch.GetElapsedTime(Volatile.Read(ref lastPacketTick)).TotalSeconds > 5;
    public string PipeName { get; } = "Flashback-audio-" + Guid.NewGuid().ToString("N");
    public string InputPath => @"\\.\pipe\" + PipeName;
    public WaveFormat Format { get; } = null!;
    public string RawFormat { get; } = "";
    public Exception? Failure { get; private set; }
    public AudioLoopback(string deviceId = "", bool microphone = false, bool hold = false)
    {
        this.microphone = microphone;
        string sourceName = microphone ? "Microphone" : "Desktop audio";
        string stage = "selecting the Windows audio device";
        try
        {
        var flow = microphone ? DataFlow.Capture : DataFlow.Render;
        var role = microphone ? Role.Communications : Role.Multimedia;
        device = string.IsNullOrEmpty(deviceId) ? enumerator.GetDefaultAudioEndpoint(flow, role) : enumerator.GetDevice(deviceId);
        if (device.DataFlow != flow) throw new InvalidOperationException("The selected device has the wrong audio direction. Choose it again in Settings.");
        if (device.State != DeviceState.Active) throw new InvalidOperationException("The selected audio device is disconnected. Choose a connected device in Settings.");
        this.deviceId = device.ID;
        DeviceName = device.FriendlyName;
        deviceWatch = new AudioDeviceWatch(device.ID, string.IsNullOrEmpty(deviceId), flow, role);
        Hold = hold;
        enumerator.RegisterEndpointNotificationCallback(deviceWatch);
        stage = "reading the playback device format";
        // Capture.WaveFormat converts extensible formats to WAVEFORMATEX and loses
        // the channel mask. Keep the actual device mix format for WASAPI playback,
        // particularly for surround/spatial devices that reject that conversion.
        using (var client = device.AudioClient) Format = client.MixFormat;
        var format = Format;
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
            format is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
        RawFormat = isFloat && format.BitsPerSample == 32 ? "f32le" : format.BitsPerSample switch
        { 16 => "s16le", 24 => "s24le", 32 => "s32le", _ => throw new InvalidOperationException("Unsupported playback audio format. Turn off desktop audio or use a standard Windows playback format.") };
        stage = "initializing Windows audio capture";
        OpenDevice(device);
        stage = "opening the audio stream";
        pipe = new NamedPipeServerStream(PipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 0, 256 * 1024);
        }
        catch (Exception ex)
        {
            if (deviceWatch != null) try { enumerator.UnregisterEndpointNotificationCallback(deviceWatch); } catch { }
            pipe?.Dispose(); capture?.Dispose(); silence?.Dispose(); device?.Dispose(); enumerator.Dispose(); cancel.Dispose();
            throw new InvalidOperationException($"{sourceName} failed while {stage} (0x{ex.HResult:X8}). {ex.Message} Check the selected device{(microphone ? " and Windows microphone permissions" : "")}, or turn off {sourceName.ToLowerInvariant()} in Settings.", ex);
        }
    }
    private void OpenDevice(MMDevice source)
    {
        var next = new TimestampedAudioCapture(source, microphone, Format);
        next.DataAvailable += OnPacket;
        next.RecordingStopped += (sender, e) => { if (!disposed && e.Exception != null) Lost(sender, e.Exception); };
        capture = next;
        // A silent render stream keeps loopback packets coming during quiet periods,
        // so audio time does not collapse and drift relative to the video.
        if (!microphone)
        {
            var keepAlive = new WasapiOut(source, AudioClientShareMode.Shared, false, 100);
            keepAlive.Init(new SilenceProvider(Format));
            keepAlive.PlaybackStopped += (sender, e) => { if (!disposed && e.Exception != null) Lost(sender, e.Exception); };
            silence = keepAlive;
        }
    }
    private void Lost(object? sender, Exception error)
    {
        if (!ReferenceEquals(sender, capture) && !ReferenceEquals(sender, silence)) return;
        if (Hold) Interlocked.Exchange(ref lostSignal, 1); else Failure = error;
    }
    private void OnPacket(object? sender, AudioPacketEventArgs e)
    {
        if (disposed || e.BytesRecorded == 0 || !ReferenceEquals(sender, capture)) return;
        Volatile.Write(ref lastPacketTick, Stopwatch.GetTimestamp());
        try
        {
            var bytes=e.Buffer;
            ApplyMute(bytes,Muted);
            ApplyGain(bytes,RawFormat,Gain);
            lock(feed)
            {
                if(timelineOrigin!=null)
                {
                    long origin=timelineOrigin();
                    if(origin==0) return;
                    clockNormalizer ??= new(Format.SampleRate,Format.BlockAlign,RawFormat);
                    var normalized=clockNormalizer.Process(bytes,e.TimestampSeconds,origin/(double)Stopwatch.Frequency,Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency);
                    if(normalized.Silence is {Length:>0} gap) QueuePacket(gap);
                    bytes=normalized.Samples;
                }
                if(bytes.Length>0) QueuePacket(bytes);
            }
        }
        catch(Exception ex) { Failure=ex; }
    }
    internal static void ApplyGain(byte[] samples, string format, double gain)
    {
        if (Math.Abs(gain - 1) < .001) return;
        if (gain <= 0) { Array.Clear(samples); return; }
        var span = samples.AsSpan();
        switch (format)
        {
            case "f32le":
                for (int i = 0; i + 3 < span.Length; i += 4) BinaryPrimitives.WriteSingleLittleEndian(span[i..], (float)Math.Clamp(BinaryPrimitives.ReadSingleLittleEndian(span[i..]) * gain, -1, 1));
                break;
            case "s16le":
                for (int i = 0; i + 1 < span.Length; i += 2) BinaryPrimitives.WriteInt16LittleEndian(span[i..], (short)Math.Clamp(Math.Round(BinaryPrimitives.ReadInt16LittleEndian(span[i..]) * gain), short.MinValue, short.MaxValue));
                break;
            case "s24le":
                for (int i = 0; i + 2 < span.Length; i += 3)
                {
                    int value = (span[i] | span[i + 1] << 8 | span[i + 2] << 16) << 8 >> 8;
                    int scaled = (int)Math.Clamp(Math.Round(value * gain), -8388608, 8388607);
                    span[i] = (byte)scaled; span[i + 1] = (byte)(scaled >> 8); span[i + 2] = (byte)(scaled >> 16);
                }
                break;
            default:
                for (int i = 0; i + 3 < span.Length; i += 4) BinaryPrimitives.WriteInt32LittleEndian(span[i..], (int)Math.Clamp(Math.Round(BinaryPrimitives.ReadInt32LittleEndian(span[i..]) * gain), int.MinValue, int.MaxValue));
                break;
        }
    }
    private void QueuePacket(byte[] bytes)
    {
        long queued=Interlocked.Add(ref queuedBytes,bytes.Length);
        if(queued>Format.AverageBytesPerSecond*2L || !packets.Writer.TryWrite(bytes))
        { Interlocked.Add(ref queuedBytes,-bytes.Length); throw new IOException(BacklogMessage); }
    }
    public void Start(Func<long>? timelineOrigin = null)
    {
        this.timelineOrigin = timelineOrigin;
        Volatile.Write(ref lastPacketTick, Stopwatch.GetTimestamp());
        pump = Task.Run(async () =>
        {
            try
            {
                await pipe.WaitForConnectionAsync(cancel.Token);
                silence?.Play();
                capture?.StartRecording();
                if (timelineOrigin != null) keeper = Task.Run(KeepAsync);
                await foreach (var bytes in packets.Reader.ReadAllAsync(cancel.Token))
                {
                    ApplyMute(bytes, Muted);
                    await pipe.WriteAsync(bytes, cancel.Token);
                    Interlocked.Add(ref queuedBytes,-bytes.Length);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!disposed) Failure = ex; }
        });
    }
    // While held, a missing or silent device becomes real-time silence on the same
    // clock, and the same device is reopened as soon as Windows reports it again.
    private async Task KeepAsync()
    {
        long lastAttempt = 0;
        while (!cancel.IsCancellationRequested)
        {
            try { await Task.Delay(100, cancel.Token); } catch (OperationCanceledException) { return; }
            try
            {
                if (!Hold) continue;
                if (!holding)
                {
                    bool stalled = Stopwatch.GetElapsedTime(Volatile.Read(ref lastPacketTick)).TotalSeconds > 2;
                    if (deviceWatch!.TakeLost() || Interlocked.Exchange(ref lostSignal, 0) != 0 || stalled) Release();
                    else continue;
                }
                FillSilence();
                bool returned = deviceWatch!.TakeReturned();
                if (!returned && Stopwatch.GetElapsedTime(lastAttempt).TotalSeconds < 1) continue;
                lastAttempt = Stopwatch.GetTimestamp();
                TryReopen();
            }
            catch (Exception ex) { if (!disposed) Failure = ex; return; }
        }
    }
    private void Release()
    {
        Volatile.Write(ref holding, true);
        var oldCapture = capture; var oldSilence = silence; var oldDevice = device;
        capture = null; silence = null; device = null;
        try { oldCapture?.Dispose(); } catch { }
        try { oldSilence?.Stop(); oldSilence?.Dispose(); } catch { }
        oldDevice?.Dispose();
    }
    private void FillSilence()
    {
        lock (feed)
        {
            long origin = timelineOrigin!();
            if (origin == 0) return;
            clockNormalizer ??= new(Format.SampleRate, Format.BlockAlign, RawFormat);
            var gap = clockNormalizer.FillTo(origin / (double)Stopwatch.Frequency, Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
            if (gap.Length > 0) QueuePacket(gap);
        }
        Volatile.Write(ref lastPacketTick, Stopwatch.GetTimestamp());
    }
    private void TryReopen()
    {
        MMDevice? next = null; bool owned = false;
        try
        {
            next = enumerator.GetDevice(deviceId);
            if (next.State != DeviceState.Active) { next.Dispose(); return; }
            WaveFormat mix; using (var client = next.AudioClient) mix = client.MixFormat;
            // A different mix format cannot share the running stream; a full reconnect adopts it.
            if (mix.SampleRate != Format.SampleRate || mix.Channels != Format.Channels || mix.BitsPerSample != Format.BitsPerSample)
                throw new IOException(microphone ? MicrophoneChangedMessage : DeviceChangedMessage);
            device = next; owned = true;
            OpenDevice(next);
            Volatile.Write(ref lastPacketTick, Stopwatch.GetTimestamp());
            silence?.Play(); capture!.StartRecording();
            Volatile.Write(ref holding, false);
        }
        catch (IOException) { if (!owned) next?.Dispose(); throw; }
        catch { if (owned) Release(); else next?.Dispose(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        if (deviceWatch != null) try { enumerator.UnregisterEndpointNotificationCallback(deviceWatch); } catch { }
        cancel.Cancel();
        packets.Writer.TryComplete();
        try { capture?.StopRecording(); } catch { }
        try { silence?.Stop(); } catch { }
        pipe.Dispose();
        if (pump != null) { try { await pump; } catch { } }
        if (keeper != null) { try { await keeper; } catch { } }
        capture?.Dispose(); silence?.Dispose(); device?.Dispose(); enumerator.Dispose(); cancel.Dispose();
    }
    public static IReadOnlyList<PlaybackDevice> GetPlaybackDevices(bool microphone = false)
    {
        var devices = new List<PlaybackDevice>();
        using var source = new MMDeviceEnumerator();
        foreach (var endpoint in source.EnumerateAudioEndPoints(microphone ? DataFlow.Capture : DataFlow.Render, DeviceState.Active))
        {
            using (endpoint) devices.Add(new PlaybackDevice(endpoint.ID, endpoint.FriendlyName));
        }
        return devices;
    }
    public static string? DefaultDeviceId(bool microphone = false)
    {
        try
        {
            using var source = new MMDeviceEnumerator();
            using var endpoint = source.GetDefaultAudioEndpoint(microphone ? DataFlow.Capture : DataFlow.Render, microphone ? Role.Communications : Role.Multimedia);
            return endpoint.ID;
        }
        catch { return null; }
    }
}
public sealed record PlaybackDevice(string Id, string Name);
