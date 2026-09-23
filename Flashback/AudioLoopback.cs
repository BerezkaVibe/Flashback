using System;
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

public sealed class AudioLoopback : IAsyncDisposable
{
    private readonly MMDeviceEnumerator enumerator = new();
    private readonly MMDevice device = null!;
    private readonly TimestampedAudioCapture capture = null!;
    private readonly WasapiOut? silence;
    private readonly NamedPipeServerStream pipe = null!;
    private readonly CancellationTokenSource cancel = new();
    private readonly Channel<byte[]> packets = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(2048) { SingleReader = true, SingleWriter = true });
    private Task? pump;
    private bool disposed;
    public bool Muted { get => Volatile.Read(ref muted); set => Volatile.Write(ref muted, value); }
    private bool muted;
    internal static void ApplyMute(byte[] samples, bool mute) { if (mute) Array.Clear(samples); }
    private long lastPacketTick;
    private Func<long>? timelineOrigin;
    private AudioClockNormalizer? clockNormalizer;
    private long queuedBytes;
    internal const string BacklogMessage="Audio input backlog requires reconnection.";
    internal string SyncReport => clockNormalizer==null ? "Clock pending" : $"Clock error {clockNormalizer.ErrorMilliseconds:0.000} ms; corrected {clockNormalizer.CorrectedFrames} frames; gaps {clockNormalizer.GapFrames}; queued {Interlocked.Read(ref queuedBytes)} bytes";
    private AudioDeviceWatch? deviceWatch;
    public string DeviceName { get; } = "";
    public bool DeviceChanged => deviceWatch?.Changed == true;
    public const string DeviceChangedMessage = "The Windows playback device changed. Reconnecting audio capture.";
    public const string StreamStalledMessage = "The desktop audio stream stopped delivering samples. Reconnecting audio capture.";
    public const string MicrophoneChangedMessage = "The microphone device changed. Reconnecting audio capture.";
    public const string MicrophoneStalledMessage = "The microphone stream stopped delivering samples. Reconnecting audio capture.";
    public bool StreamStalled => Volatile.Read(ref lastPacketTick) != 0 && Stopwatch.GetElapsedTime(Volatile.Read(ref lastPacketTick)).TotalSeconds > 5;
    public string PipeName { get; } = "Flashback-audio-" + Guid.NewGuid().ToString("N");
    public string InputPath => @"\\.\pipe\" + PipeName;
    public WaveFormat Format { get; } = null!;
    public string RawFormat { get; }
    public Exception? Failure { get; private set; }
    public AudioLoopback(string deviceId = "", bool microphone = false)
    {
        string sourceName = microphone ? "Microphone" : "Desktop audio";
        string stage = "selecting the Windows audio device";
        try
        {
        var flow = microphone ? DataFlow.Capture : DataFlow.Render;
        var role = microphone ? Role.Communications : Role.Multimedia;
        device = string.IsNullOrEmpty(deviceId) ? enumerator.GetDefaultAudioEndpoint(flow, role) : enumerator.GetDevice(deviceId);
        if (device.DataFlow != flow) throw new InvalidOperationException("The selected device has the wrong audio direction. Choose it again in Settings.");
        if (device.State != DeviceState.Active) throw new InvalidOperationException("The selected audio device is disconnected. Choose a connected device in Settings.");
        DeviceName = device.FriendlyName;
        deviceWatch = new AudioDeviceWatch(device.ID, string.IsNullOrEmpty(deviceId), flow, role);
        enumerator.RegisterEndpointNotificationCallback(deviceWatch);
        stage = "reading the playback device format";
        // Capture.WaveFormat converts extensible formats to WAVEFORMATEX and loses
        // the channel mask. Keep the actual device mix format for WASAPI playback,
        // particularly for surround/spatial devices that reject that conversion.
        using (var client = device.AudioClient) Format = client.MixFormat;
        stage = "initializing Windows audio capture";
        capture = new TimestampedAudioCapture(device, microphone, Format);
        var format = Format;
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
            format is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
        RawFormat = isFloat && format.BitsPerSample == 32 ? "f32le" : format.BitsPerSample switch
        { 16 => "s16le", 24 => "s24le", 32 => "s32le", _ => throw new InvalidOperationException("Unsupported playback audio format. Turn off desktop audio or use a standard Windows playback format.") };
        // A silent render stream keeps loopback packets coming during quiet periods,
        // so audio time does not collapse and drift relative to the video.
        if (!microphone)
        {
            silence = new WasapiOut(device, AudioClientShareMode.Shared, false, 100);
            stage = "initializing the playback device";
            silence.Init(new SilenceProvider(format));
            silence.PlaybackStopped += (_, e) => { if (!disposed && e.Exception != null) Failure = e.Exception; };
        }
        stage = "opening the audio stream";
        pipe = new NamedPipeServerStream(PipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 0, 256 * 1024);
        capture.DataAvailable += (_, e) =>
        {
            if (disposed || e.BytesRecorded == 0) return;
            Volatile.Write(ref lastPacketTick, Stopwatch.GetTimestamp());
            try
            {
                var bytes=e.Buffer;
                ApplyMute(bytes,Muted);
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
            catch(Exception ex) { Failure=ex; }        };
        capture.RecordingStopped += (_, e) => { if (!disposed && e.Exception != null) Failure = e.Exception; };
        }
        catch (Exception ex)
        {
            if (deviceWatch != null) try { enumerator.UnregisterEndpointNotificationCallback(deviceWatch); } catch { }
            pipe?.Dispose(); capture?.Dispose(); silence?.Dispose(); device?.Dispose(); enumerator.Dispose(); cancel.Dispose();
            throw new InvalidOperationException($"{sourceName} failed while {stage} (0x{ex.HResult:X8}). {ex.Message} Check the selected device{(microphone ? " and Windows microphone permissions" : "")}, or turn off {sourceName.ToLowerInvariant()} in Settings.", ex);
        }
    }
    private void QueuePacket(byte[] bytes)
    {
        long queued=Interlocked.Add(ref queuedBytes,bytes.Length);
        if(queued>Format.AverageBytesPerSecond*2L || !packets.Writer.TryWrite(bytes))
        { Interlocked.Add(ref queuedBytes,-bytes.Length); throw new IOException(BacklogMessage); }
    }    public void Start(Func<long>? timelineOrigin = null)
    {
        this.timelineOrigin = timelineOrigin;
        Volatile.Write(ref lastPacketTick, Stopwatch.GetTimestamp());
        pump = Task.Run(async () =>
        {
            try
            {
                await pipe.WaitForConnectionAsync(cancel.Token);
                silence?.Play();
                capture.StartRecording();
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
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        if (deviceWatch != null) try { enumerator.UnregisterEndpointNotificationCallback(deviceWatch); } catch { }
        cancel.Cancel();
        packets.Writer.TryComplete();
        try { capture.StopRecording(); } catch { }
        try { silence?.Stop(); } catch { }
        pipe.Dispose();
        if (pump != null) { try { await pump; } catch { } }
        capture.Dispose(); silence?.Dispose(); device.Dispose(); enumerator.Dispose(); cancel.Dispose();
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
}
public sealed record PlaybackDevice(string Id, string Name);



