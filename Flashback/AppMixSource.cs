using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace Flashback;

// Per-app recording mix. Used only when an app has a custom level; otherwise the
// whole playback device is captured by AudioLoopback at no extra cost.
// Each app that owns an audio session gets its own Windows process-loopback stream.
// Packets land on one accumulator indexed by their capture time, so every app shares
// the video clock; a writer emits the mix 100 ms behind real time.
public sealed class AppMixSource : IRecordingAudio
{
    internal const int SampleRate = 48000, Channels = 2;
    private const int RingFrames = SampleRate * 2, Latency = SampleRate / 10;
    // Windows 10 build 20348 and Windows 11 support process loopback capture.
    internal static bool Supported => Environment.OSVersion.Version.Build >= 20348;
    private readonly string deviceId;
    private readonly ConcurrentDictionary<int, AppStream> streams = new();
    private readonly object mix = new();
    private readonly float[] ring = new float[RingFrames * Channels];
    private long emitted; // frames written to the pipe, counted from the timeline origin
    private readonly NamedPipeServerStream pipe;
    private readonly CancellationTokenSource cancel = new();
    private readonly Channel<byte[]> packets = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(512) { SingleReader = true, SingleWriter = true });
    private Func<long>? timelineOrigin;
    private Task? pump, writer;
    private volatile Dictionary<string, double> levels;
    private bool disposed;
    private long lastWrite;
    public WaveFormat Format { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);
    public string RawFormat => "f32le";
    public string PipeName { get; } = "Flashback-mix-" + Guid.NewGuid().ToString("N");
    public string InputPath => @"\\.\pipe\" + PipeName;
    public string DeviceName => $"App mix ({streams.Count} apps)";
    public string DeviceId => deviceId;
    public bool Muted { get; set; }
    public double Gain { get; set; } = 1;
    public bool Hold { get; set; } // Process streams follow their app, not a device.
    public bool DeviceChanged => false;
    public bool StreamStalled => lastWrite != 0 && Stopwatch.GetElapsedTime(Interlocked.Read(ref lastWrite)).TotalSeconds > 5;
    public Exception? Failure { get; private set; }
    public string SyncReport => $"App mix: {streams.Count} app streams; {string.Join(", ", streams.Values.Select(s => s.Name).Distinct())}";

    public AppMixSource(string deviceId, IReadOnlyDictionary<string, int> appLevels)
    {
        if (!Supported) throw new PlatformNotSupportedException("Per-app recording levels need Windows 11 or Windows 10 build 20348 or later.");
        this.deviceId = deviceId; levels = Normalize(appLevels);
        pipe = new NamedPipeServerStream(PipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 0, 256 * 1024);
    }
    private static Dictionary<string, double> Normalize(IReadOnlyDictionary<string, int> source) =>
        source.ToDictionary(p => p.Key.ToLowerInvariant(), p => Math.Clamp(p.Value, 0, 200) / 100.0);
    public void SetLevels(IReadOnlyDictionary<string, int> appLevels) => levels = Normalize(appLevels);
    private double LevelFor(string name) => levels.TryGetValue(name.ToLowerInvariant(), out var level) ? level : 1;

    public void Start(Func<long>? timelineOrigin = null)
    {
        this.timelineOrigin = timelineOrigin;
        Interlocked.Exchange(ref lastWrite, Stopwatch.GetTimestamp());
        pump = Task.Run(async () =>
        {
            try
            {
                await pipe.WaitForConnectionAsync(cancel.Token);
                writer = Task.Factory.StartNew(WriteLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                await foreach (var bytes in packets.Reader.ReadAllAsync(cancel.Token)) await pipe.WriteAsync(bytes, cancel.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!disposed) Failure = ex; }
        });
    }

    // Emits the mix every 10 ms and looks for new or closed app sessions every 2 s.
    private void WriteLoop()
    {
        long nextScan = 0;
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                if (Stopwatch.GetTimestamp() >= nextScan) { ScanSessions(); nextScan = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2; }
                long origin = timelineOrigin?.Invoke() ?? 0;
                if (origin != 0)
                {
                    long target = (long)(Stopwatch.GetElapsedTime(origin).TotalSeconds * SampleRate) - Latency;
                    if (target > emitted) Emit(target);
                }
                Thread.Sleep(10);
            }
            catch (Exception ex) { if (!disposed) Failure = ex; return; }
        }
    }
    private void Emit(long target)
    {
        int frames = (int)Math.Min(target - emitted, RingFrames / 2);
        var bytes = new byte[frames * Channels * 4];
        float gain = Muted ? 0 : (float)Gain;
        lock (mix)
        {
            for (int f = 0; f < frames; f++)
            {
                int slot = (int)((emitted + f) % RingFrames) * Channels;
                for (int c = 0; c < Channels; c++)
                {
                    float value = Math.Clamp(ring[slot + c] * gain, -1f, 1f); ring[slot + c] = 0;
                    BitConverter.TryWriteBytes(bytes.AsSpan((f * Channels + c) * 4), value);
                }
            }
            emitted += frames;
        }
        if (!packets.Writer.TryWrite(bytes)) throw new IOException(AudioLoopback.BacklogMessage);
        Interlocked.Exchange(ref lastWrite, Stopwatch.GetTimestamp());
    }
    // Adds one app packet at its capture time. Each stream keeps a running cursor and
    // only jumps to its timestamp after drifting more than 10 ms, so packets join cleanly.
    private void Accumulate(AppStream stream, byte[] data, double packetTime)
    {
        long origin = timelineOrigin?.Invoke() ?? 0;
        if (origin == 0 || data.Length == 0) return;
        int frames = data.Length / (Channels * 4);
        double arrival = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        if (!double.IsFinite(packetTime) || Math.Abs(packetTime - arrival) > 1) packetTime = arrival - frames / (double)SampleRate;
        long position = (long)Math.Round((packetTime - origin / (double)Stopwatch.Frequency) * SampleRate);
        if (stream.Cursor < 0 || Math.Abs(position - stream.Cursor) > SampleRate / 100) stream.Cursor = position;
        long start = stream.Cursor; stream.Cursor += frames;
        float level = (float)LevelFor(stream.Name);
        if (level <= 0) return;
        var samples = MemoryMarshal.Cast<byte, float>(data.AsSpan());
        lock (mix)
        {
            for (int f = 0; f < frames; f++)
            {
                long frame = start + f;
                if (frame < emitted || frame >= emitted + RingFrames) continue; // too late, or too far ahead
                int slot = (int)(frame % RingFrames) * Channels;
                for (int c = 0; c < Channels; c++) ring[slot + c] += samples[f * Channels + c] * level;
            }
        }
    }

    // Opens a stream for every process with an audio session on the playback device,
    // and closes streams whose process has exited.
    private void ScanSessions()
    {
        var seen = new HashSet<int>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = string.IsNullOrEmpty(deviceId) ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : enumerator.GetDevice(deviceId);
            var sessions = device.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                using var session = sessions[i];
                if (session.IsSystemSoundsSession || session.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired) continue;
                int pid = (int)session.GetProcessID;
                if (pid <= 0 || pid == Environment.ProcessId) continue;
                seen.Add(pid);
                if (!streams.ContainsKey(pid)) TryOpen(pid);
            }
        }
        catch { /* Keep existing streams if the device list is briefly unavailable. */ }
        foreach (var (pid, stream) in streams)
            if (!seen.Contains(pid) && stream.Exited) { if (streams.TryRemove(pid, out var gone)) gone.Dispose(); }
    }
    private void TryOpen(int pid)
    {
        string name;
        try { using var process = Process.GetProcessById(pid); name = process.ProcessName; } catch { return; }
        if (name.Equals("Flashback", StringComparison.OrdinalIgnoreCase)) return; // Never record our own preview audio.
        try
        {
            var client = ProcessLoopback.Activate(pid);
            var capture = new TimestampedAudioCapture(client, AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback | ProcessLoopback.AutoConvert, Format);
            var stream = new AppStream(pid, name, capture);
            capture.DataAvailable += (_, e) => { if (!disposed) Accumulate(stream, e.Buffer, e.TimestampSeconds); };
            if (streams.TryAdd(pid, stream)) capture.StartRecording(); else stream.Dispose();
        }
        catch { /* Protected or short-lived processes are skipped; they stay silent in the mix. */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true; cancel.Cancel(); packets.Writer.TryComplete();
        foreach (var stream in streams.Values) stream.Dispose();
        streams.Clear();
        pipe.Dispose();
        if (pump != null) try { await pump; } catch { }
        if (writer != null) try { await writer; } catch { }
        cancel.Dispose();
    }

    private sealed class AppStream(int pid, string name, TimestampedAudioCapture capture) : IDisposable
    {
        internal string Name { get; } = name;
        internal long Cursor = -1;
        internal bool Exited { get { try { using var p = Process.GetProcessById(pid); return p.HasExited; } catch { return true; } } }
        public void Dispose() { try { capture.Dispose(); } catch { } }
    }

    // Lists apps that currently own an audio session on a playback device, for the mixer UI.
    internal static IReadOnlyList<string> ActiveApps(string deviceId)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = string.IsNullOrEmpty(deviceId) ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : enumerator.GetDevice(deviceId);
            var sessions = device.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                using var session = sessions[i];
                if (session.IsSystemSoundsSession || session.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired) continue;
                int pid = (int)session.GetProcessID;
                if (pid <= 0 || pid == Environment.ProcessId) continue;
                try { using var process = Process.GetProcessById(pid); if (!process.ProcessName.Equals("Flashback", StringComparison.OrdinalIgnoreCase)) names.Add(process.ProcessName); } catch { }
            }
        }
        catch { }
        return names.ToList();
    }
}

// Windows application loopback: an IAudioClient that captures one process tree.
internal static class ProcessLoopback
{
    internal const AudioClientStreamFlags AutoConvert = (AudioClientStreamFlags)0x88000000; // AUTOCONVERTPCM | SRC_DEFAULT_QUALITY
    private const string VirtualDevice = "VAD\\Process_Loopback";
    private static readonly Guid AudioClientId = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

    internal static AudioClient Activate(int processId)
    {
        // AUDIOCLIENT_ACTIVATION_PARAMS { type = PROCESS_LOOPBACK, { pid, INCLUDE_TARGET_PROCESS_TREE } } in a VT_BLOB PROPVARIANT.
        IntPtr blob = Marshal.AllocHGlobal(12), variant = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.WriteInt32(blob, 0, 1); Marshal.WriteInt32(blob, 4, processId); Marshal.WriteInt32(blob, 8, 0);
            for (int i = 0; i < 24; i += 4) Marshal.WriteInt32(variant, i, 0);
            Marshal.WriteInt16(variant, 0, 65); Marshal.WriteInt32(variant, 8, 12); Marshal.WriteIntPtr(variant, 16, blob);
            var handler = new Completion();
            ActivateAudioInterfaceAsync(VirtualDevice, AudioClientId, variant, handler, out _);
            if (!handler.Done.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Windows did not start per-app audio capture.");
            return new AudioClient((IAudioClient)handler.Result!);
        }
        finally { Marshal.FreeHGlobal(variant); Marshal.FreeHGlobal(blob); }
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IActivateAudioInterfaceCompletionHandler { void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation); }
    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IActivateAudioInterfaceAsyncOperation { void GetActivateResult(out int result, [MarshalAs(UnmanagedType.IUnknown)] out object activated); }
    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAgileObject { }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class Completion : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        internal readonly ManualResetEventSlim Done = new();
        internal object? Result;
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            try { operation.GetActivateResult(out int hr, out var activated); if (hr >= 0) Result = activated; }
            catch { }
            finally { Done.Set(); }
        }
    }
    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string path, [MarshalAs(UnmanagedType.LPStruct)] Guid iid,
        IntPtr activationParams, IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation operation);
}
