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

// Per-app recording mix. Used when an app has a custom level, or when each app is recorded on its own
// layer; otherwise the whole playback device is captured by AudioLoopback at no extra cost.
// Each app that owns an audio session gets its own Windows process-loopback stream.
// Packets land on accumulators indexed by their capture time, so every app shares
// the video clock; a writer emits the mix 100 ms behind real time.
//
// With layers, an app gets one of Slots layers the first time it makes a sound, and its sound goes there
// instead of the main output, which then carries only the apps without a layer ("Other apps"). When all
// layers are taken, a new app takes the one whose app has been quiet longest (10 s at least); otherwise
// it joins Other apps. Which app had which layer when is kept in a LayerLog, for naming the clip's tracks.
public sealed class AppMixSource : IRecordingAudio
{
    internal const int SampleRate = 48000, Channels = 2, Slots = 6;
    private const int RingFrames = SampleRate * 2, Latency = SampleRate / 10;
    // Quieter than this (about -60 dB) isn't a sound for giving an app a layer.
    private const float Audible = .001f;
    // Windows 10 build 20348 and Windows 11 support process loopback capture.
    internal static bool Supported => Environment.OSVersion.Version.Build >= 20348;
    private readonly string deviceId;
    private readonly ConcurrentDictionary<int, AppStream> streams = new();
    private readonly object mix = new();
    // Output 0 is the main mix; 1..Slots are the layers.
    private readonly Output[] outputs;
    private readonly LayerLog? log;
    private readonly double logOffset;
    private readonly string?[] layerApps = new string?[Slots + 1];
    private readonly Dictionary<string, long> lastSound = new(StringComparer.OrdinalIgnoreCase);
    private long emitted; // frames written to the pipes, counted from the timeline origin
    private readonly CancellationTokenSource cancel = new();
    private Func<long>? timelineOrigin;
    private Task? writer;
    private volatile Dictionary<string, double> levels;
    private bool disposed;
    private long lastWrite;
    public WaveFormat Format { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);
    public string RawFormat => "f32le";
    public string PipeName => outputs[0].PipeName;
    public string InputPath => outputs[0].InputPath;
    // The layers' pipes, in layer order (none without layers).
    internal IReadOnlyList<string> LayerInputPaths => outputs.Skip(1).Select(o => o.InputPath).ToArray();
    internal bool Layered => outputs.Length > 1;
    public string DeviceName => Layered ? $"App layers ({streams.Count} apps)" : $"App mix ({streams.Count} apps)";
    public string DeviceId => deviceId;
    public bool Muted { get; set; }
    public double Gain { get; set; } = 1;
    public bool Hold { get; set; } // Process streams follow their app, not a device.
    public bool DeviceChanged => false;
    public bool StreamStalled => lastWrite != 0 && Stopwatch.GetElapsedTime(Interlocked.Read(ref lastWrite)).TotalSeconds > 5;
    public Exception? Failure { get; private set; }
    public string SyncReport => $"App mix: {streams.Count} app streams; {string.Join(", ", streams.Values.Select(s => s.Name).Distinct())}"
        + (Layered ? $"; layers: {string.Join(", ", layerApps.Skip(1).Select((a, i) => $"{i + 1} {a ?? "free"}"))}" : "");

    // log and logOffset (layers only): where to note which app has which layer, and the recorder's timeline
    // time this source's time starts at.
    public AppMixSource(string deviceId, IReadOnlyDictionary<string, int> appLevels, LayerLog? log = null, double logOffset = 0)
    {
        if (!Supported) throw new PlatformNotSupportedException("Per-app recording levels need Windows 11 or Windows 10 build 20348 or later.");
        this.deviceId = deviceId; levels = Normalize(appLevels); this.log = log; this.logOffset = logOffset;
        outputs = Enumerable.Range(0, log != null ? Slots + 1 : 1).Select(_ => new Output()).ToArray();
    }
    private static Dictionary<string, double> Normalize(IReadOnlyDictionary<string, int> source) =>
        source.ToDictionary(p => p.Key.ToLowerInvariant(), p => Math.Clamp(p.Value, 0, 200) / 100.0);
    public void SetLevels(IReadOnlyDictionary<string, int> appLevels) => levels = Normalize(appLevels);
    private double LevelFor(string name) => levels.TryGetValue(name.ToLowerInvariant(), out var level) ? level : 1;

    public void Start(Func<long>? timelineOrigin = null)
    {
        this.timelineOrigin = timelineOrigin;
        Interlocked.Exchange(ref lastWrite, Stopwatch.GetTimestamp());
        // The writer starts once the main pipe is open; the layers' pipes open right after it, and what's
        // written to them meanwhile waits in their queues.
        foreach (var output in outputs) output.Pump = Task.Run(async () =>
        {
            try
            {
                await output.Pipe.WaitForConnectionAsync(cancel.Token);
                if (output == outputs[0]) writer = Task.Factory.StartNew(WriteLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                await foreach (var bytes in output.Packets.Reader.ReadAllAsync(cancel.Token)) await output.Pipe.WriteAsync(bytes, cancel.Token);
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
        float gain = Muted ? 0 : (float)Gain;
        var packets = new byte[outputs.Length][];
        lock (mix)
        {
            for (int o = 0; o < outputs.Length; o++)
            {
                var ring = outputs[o].Ring; var bytes = packets[o] = new byte[frames * Channels * 4];
                for (int f = 0; f < frames; f++)
                {
                    int slot = (int)((emitted + f) % RingFrames) * Channels;
                    for (int c = 0; c < Channels; c++)
                    {
                        float value = Math.Clamp(ring[slot + c] * gain, -1f, 1f); ring[slot + c] = 0;
                        BitConverter.TryWriteBytes(bytes.AsSpan((f * Channels + c) * 4), value);
                    }
                }
            }
            emitted += frames;
        }
        for (int o = 0; o < outputs.Length; o++)
            if (!outputs[o].Packets.Writer.TryWrite(packets[o])) throw new IOException(AudioLoopback.BacklogMessage);
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
        int layer = Layered ? LayerFor(stream.Label, samples, start) : 0;
        var ring = outputs[layer].Ring;
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
    // The layer an app's sound goes to (0: Other apps). An app gets one when it's first heard.
    private int LayerFor(string app, ReadOnlySpan<float> samples, long at)
    {
        bool audible = false;
        foreach (float v in samples) if (Math.Abs(v) > Audible) { audible = true; break; }
        lock (layerApps)
        {
            if (audible) lastSound[app] = at;
            for (int l = 1; l <= Slots; l++) if (string.Equals(layerApps[l], app, StringComparison.OrdinalIgnoreCase)) return l;
            if (!audible) return 0;
            int free = Array.FindIndex(layerApps, 1, a => a == null);
            if (free < 0)
            {
                // All taken: the one quiet longest, if it's been quiet 10 s.
                long QuietSince(int l) => lastSound.TryGetValue(layerApps[l]!, out var t) ? t : long.MinValue;
                int quietest = Enumerable.Range(1, Slots).OrderBy(QuietSince).First();
                if (at - QuietSince(quietest) < SampleRate * 10) return 0;
                free = quietest;
            }
            Assign(free, app, at);
            return free;
        }
    }
    private void Assign(int layer, string? app, long at)
    {
        double time = logOffset + Math.Max(0, at) / (double)SampleRate;
        if (layerApps[layer] != null) log?.Close(layer, time);
        layerApps[layer] = app;
        if (app != null) log?.Open(layer, app, time);
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
        // An app that has closed gives its layer back.
        if (Layered)
            lock (layerApps)
                for (int l = 1; l <= Slots; l++)
                    if (layerApps[l] is { } app && !streams.Values.Any(s => string.Equals(s.Label, app, StringComparison.OrdinalIgnoreCase))) Assign(l, null, emitted);
    }
    private void TryOpen(int pid)
    {
        string name;
        try { using var process = Process.GetProcessById(pid); name = process.ProcessName; } catch { return; }
        if (name.Equals("Flashback", StringComparison.OrdinalIgnoreCase)) return; // Never record our own preview audio.
        // Layers are named for people: "Spotify", "Google Chrome" rather than "chrome" (levels stay by process name).
        string label = Layered ? FriendlyName(pid, name) : name;
        try
        {
            var client = ProcessLoopback.Activate(pid);
            var capture = new TimestampedAudioCapture(client, AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback | ProcessLoopback.AutoConvert, Format);
            var stream = new AppStream(pid, name, label, capture);
            capture.DataAvailable += (_, e) => { if (!disposed) Accumulate(stream, e.Buffer, e.TimestampSeconds); };
            if (streams.TryAdd(pid, stream)) capture.StartRecording(); else stream.Dispose();
        }
        catch { /* Protected or short-lived processes are skipped; they stay silent in the mix. */ }
    }
    // The app's own description of itself (its file description), or its process name.
    internal static string FriendlyName(int pid, string fallback)
    {
        try
        {
            if (ExecutablePath(pid) is { } path && FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 and < 60 } described) return described.Trim();
        }
        catch { }
        return fallback;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true; cancel.Cancel();
        foreach (var output in outputs) output.Packets.Writer.TryComplete();
        foreach (var stream in streams.Values) stream.Dispose();
        streams.Clear();
        if (Layered) lock (layerApps) for (int l = 1; l <= Slots; l++) if (layerApps[l] != null) Assign(l, null, emitted);
        foreach (var output in outputs) output.Pipe.Dispose();
        foreach (var output in outputs) if (output.Pump != null) try { await output.Pump; } catch { }
        if (writer != null) try { await writer; } catch { }
        cancel.Dispose();
    }

    private sealed class Output
    {
        internal readonly float[] Ring = new float[RingFrames * Channels];
        internal readonly string PipeName = "Flashback-mix-" + Guid.NewGuid().ToString("N");
        internal string InputPath => @"\\.\pipe\" + PipeName;
        internal readonly NamedPipeServerStream Pipe;
        internal readonly Channel<byte[]> Packets = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(512) { SingleReader = true, SingleWriter = true });
        internal Task? Pump;
        internal Output() => Pipe = new NamedPipeServerStream(PipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 0, 256 * 1024);
    }
    private sealed class AppStream(int pid, string name, string label, TimestampedAudioCapture capture) : IDisposable
    {
        internal string Name { get; } = name;
        internal string Label { get; } = label;
        internal long Cursor = -1;
        internal bool Exited { get { try { using var p = Process.GetProcessById(pid); return p.HasExited; } catch { return true; } } }
        public void Dispose() { try { capture.Dispose(); } catch { } }
    }

    // Lists apps that currently own an audio session on a playback device, for the mixer UI.
    internal static IReadOnlyList<string> ActiveApps(string deviceId) => ActiveAppPaths(deviceId).Keys.ToList();
    // App name -> executable path (for its icon), for apps with an audio session on the device.
    internal static SortedDictionary<string, string?> ActiveAppPaths(string deviceId)
    {
        var names = new SortedDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
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
                try
                {
                    using var process = Process.GetProcessById(pid);
                    if (!process.ProcessName.Equals("Flashback", StringComparison.OrdinalIgnoreCase)) names.TryAdd(process.ProcessName, ExecutablePath(pid));
                }
                catch { }
            }
        }
        catch { }
        return names;
    }
    // Limited-query access works for most apps, including anti-cheat protected games.
    internal static string? ExecutablePath(int pid)
    {
        IntPtr handle = OpenProcess(0x1000, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new System.Text.StringBuilder(1024); int size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally { CloseHandle(handle); }
    }
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, int flags, System.Text.StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}

// Which app had which layer when, on the recorder's timeline (seconds): kept across restarts of the
// capture while the replay buffer lives, for naming a saved clip's layer tracks. Old entries are dropped.
public sealed class LayerLog
{
    internal sealed record Span(int Layer, string App, double From, double To);
    private readonly List<Span> spans = new();
    internal void Open(int layer, string app, double at) { lock (spans) spans.Add(new Span(layer, app, at, double.PositiveInfinity)); }
    internal void Close(int layer, double at)
    {
        lock (spans)
        {
            int i = spans.FindLastIndex(s => s.Layer == layer && double.IsPositiveInfinity(s.To));
            if (i >= 0) spans[i] = spans[i] with { To = Math.Max(spans[i].From, at) };
            spans.RemoveAll(s => s.To < at - 900);
        }
    }
    internal void Clear() { lock (spans) spans.Clear(); }
    internal IReadOnlyList<Span> Snapshot() { lock (spans) return spans.ToArray(); }
    // A layer's track title for a stretch of the timeline: its apps and when each starts, from the stretch's
    // start, as "Apps: Spotify@0.00;Google Chrome@12.40" ("Apps:" when it had none).
    internal string Title(int layer, double from, double to)
    {
        var parts = Snapshot().Where(s => s.Layer == layer && s.To > from && s.From < to).OrderBy(s => s.From)
            .Select(s => $"{Clean(s.App)}@{Math.Max(0, s.From - from).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}");
        return "Apps: " + string.Join(";", parts);
    }
    private static string Clean(string app)
    {
        var text = new string(app.Where(c => !char.IsControl(c) && c is not ';' and not '@' and not '=' and not '\\').ToArray()).Trim();
        return text.Length == 0 ? "App" : text;
    }
    // The apps (and when each starts) in a layer track's title; empty for any other title.
    internal static IReadOnlyList<(string App, double From)> Parse(string? title)
    {
        if (title == null || !title.StartsWith("Apps:", StringComparison.Ordinal)) return Array.Empty<(string, double)>();
        var list = new List<(string, double)>();
        foreach (var part in title[5..].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int at = part.LastIndexOf('@');
            if (at > 0 && double.TryParse(part[(at + 1)..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var from)) list.Add((part[..at], from));
        }
        return list;
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
