using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Flashback;

public sealed class Recorder : IAsyncDisposable
{
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly SemaphoreSlim files = new(1, 1);
    private readonly SemaphoreSlim saver = new(1, 1);
    private readonly ChildProcessJob job = new();
    private readonly Queue<string> log = new();
    private readonly object logLock = new();
    private Process? process;
    private IRecordingAudio? audio;
    private AudioLoopback? microphone;
    private VideoFrameBridge? video;
    internal bool UseBridgeForTests { get; set; }
    internal bool DisableNativeForTests { get; set; }
    internal bool SyncPatternForTests { get; set; }
    internal int VideoConnections => video?.Connections ?? 0;
    internal (double Cpu, long Memory) Performance => (Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds + (process?.TotalProcessorTime.TotalSeconds ?? 0) + (video?.ProducerCpu ?? 0), Process.GetCurrentProcess().WorkingSet64 + (process?.WorkingSet64 ?? 0) + (video?.ProducerMemory ?? 0));
    internal string VideoStats => video == null ? "No frame bridge" : $"Native GPU capture: {video.UsingNative}; GPU busy polls: {video.GpuBusyReads}; submitted: {video.GpuSubmittedFrames}; average capture call ms: {video.CaptureMilliseconds/Math.Max(1,video.CaptureCalls):0.00}; worst capture call ms: {video.MaxCaptureMilliseconds:0.00}; received frames: {video.ReceivedFrames}; repeated frames: {video.RepeatedFrames}; history drops: {video.DroppedFrames}; slow pipe writes: {video.SlowWrites}; worst pipe write ms: {video.MaxWriteMilliseconds:0.00}; timeline seconds: {Stopwatch.GetElapsedTime(video.TimelineOrigin).TotalSeconds:0.00}; display gaps: {video.Recoveries}; last gap ms: {video.LastGapMilliseconds:0}; longest gap ms: {video.LongestGapMilliseconds:0}\nAudio: {audio?.SyncReport}\nMic: {microphone?.SyncReport}";
    internal void InterruptVideoForTest(TimeSpan duration) => video?.InterruptForTest(duration);
    private Exception? injectedFailure;
    private CancellationTokenSource? maintenanceCancel;
    private Task? maintenance;
    private Task? stdoutReader;
    private Task? stderrReader;
    private string? session;
    private List<Segment> retainedSegments = new();
    private double timelineOffset;
    private int nextSegmentNumber;
    public bool LastRestartPreservedBuffer { get; private set; }
    private Settings settings = new();
    private double progressSeconds;
    private double measuredFps;
    private long progressTick;
    private bool stopping;
    private int saving;
    private double savedThrough;
    public bool IsRecording => process != null && !process.HasExited && !stopping;
    public bool IsSaving => Volatile.Read(ref saving) != 0;
    public double BufferedSeconds { get; private set; }
    public long BufferBytes { get; private set; }
    public double RecordedSeconds => Volatile.Read(ref progressSeconds);
    public double MeasuredFps => Volatile.Read(ref measuredFps);
    public (int Width, int Height) RecordingSize { get; private set; }
    public string LastError { get; private set; } = "";
    public bool UsesGpuTransfer { get; private set; }
    private VideoEncoder? selectedEncoder;
    public string EncoderName => selectedEncoder?.Name ?? "Hardware";
    public bool UsesCompatibilityConversion => selectedEncoder is { IsAmd: true, Conversion: AmdConversion.Compatibility };
    public string LastStartupReport { get; private set; } = "";
    public long Generation { get; private set; }
    public event Action<long, string>? Faulted;
    public string FfmpegPath { get; }
    public void SetAudioMuted(bool desktopMuted, bool microphoneMuted)
    {
        settings.DesktopMuted = desktopMuted; settings.MicrophoneMuted = microphoneMuted;
        if (audio != null) audio.Muted = desktopMuted;
        if (microphone != null) microphone.Muted = microphoneMuted;
    }
    // Locks and volumes apply to the running sources without restarting the buffer.
    public void SetAudioLive(Settings next)
    {
        settings.DesktopLocked = next.DesktopLocked; settings.MicrophoneLocked = next.MicrophoneLocked;
        settings.DesktopVolume = next.DesktopVolume; settings.MicrophoneVolume = next.MicrophoneVolume; settings.AppVolumes = new(next.AppVolumes);
        if (audio is AppMixSource mixer) mixer.SetLevels(next.AppVolumes);
        if (audio != null) { audio.Hold = next.DesktopLocked; audio.Gain = next.DesktopVolume / 100.0; }
        if (microphone != null) { microphone.Hold = next.MicrophoneLocked; microphone.Gain = next.MicrophoneVolume / 100.0; }
    }
    internal string? LiveDeviceId(bool mic) => (mic ? microphone : audio)?.DeviceId;
    public Recorder(string? ffmpeg = null)
    {
        FfmpegPath = ffmpeg ?? Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe");
    }
    // Startup timing: each stage is stamped into the startup report and the last one is kept on disk.
    private long startClock;
    internal string StartTimings { get; private set; } = "";
    private void Mark(string stage)
    {
        var line = $"[{Stopwatch.GetElapsedTime(startClock).TotalMilliseconds,6:0} ms] {stage}";
        StartTimings += line + "\n"; LastStartupReport += line + "\n";
    }
    public async Task StartAsync(Settings next, bool synthetic = false, CancellationToken cancellationToken = default, bool preserveBuffer = false)
    {
        startClock = Stopwatch.GetTimestamp(); StartTimings = "";
        await lifecycle.WaitAsync(cancellationToken);
        Generation++;
        LastStartupReport = $"Flashback {typeof(Recorder).Assembly.GetName().Version} · {DateTimeOffset.Now:O}\n{GraphicsDiagnostics.DescribeDisplays()}\n";
        try
        {
            next.Validate();
            if (!File.Exists(FfmpegPath)) throw new InvalidOperationException("Could not start the buffer while preparing the replay buffer. The bundled recorder is missing. Extract the entire ZIP including tools.", new FileNotFoundException("The bundled recorder is missing.", FfmpegPath));
            Mark("display report");
            var encoder = synthetic ? null : await Task.Run(() => VideoEncoder.SelectAsync(FfmpegPath, next.Encoder, CaptureDisplay.Resolve(next.DisplayIndex), line => LastStartupReport += line + "\n", cancellationToken), cancellationToken);
            Mark("encoder selected");
            preserveBuffer = preserveBuffer && selectedEncoder == encoder;
            selectedEncoder = encoder;
            await StartWithGpuRetryAsync(transfer => StartAttemptAsync(next, synthetic, transfer, cancellationToken, preserveBuffer), () => !synthetic && selectedEncoder?.IsAmd != true && !UsesGpuTransfer);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LastStartupReport += ex + "\n";
            var failure = ex.ToString();
            if (selectedEncoder != null && (failure.Contains(selectedEncoder.Codec, StringComparison.Ordinal) || failure.Contains("OpenEncodeSession", StringComparison.Ordinal) || failure.Contains("Error while opening encoder", StringComparison.OrdinalIgnoreCase)))
                VideoEncoder.Forget(FfmpegPath, selectedEncoder);
            try { Directory.CreateDirectory(Storage.Root); File.WriteAllText(Path.Combine(Storage.Root, "last-error.txt"), LastStartupReport); } catch { }
            if (selectedEncoder is { IsAmd: false } && IsNvencDeviceError(ex.ToString()))
                throw new InvalidOperationException("NVIDIA's video encoder could not open a usable device. Flashback tried the available GPU capture paths. Update or reinstall the NVIDIA graphics driver, restart Windows, and try again. If it still fails, send the copied error details.", ex);
            throw;
        }
        finally { lifecycle.Release(); }
    }
    internal static bool IsNvencDeviceError(string error) => error.Contains("OpenEncodeSessionEx failed: no encode device", StringComparison.OrdinalIgnoreCase)
        || error.Contains("OpenEncodeSessionEx failed: unsupported device", StringComparison.OrdinalIgnoreCase);
    internal static async Task StartWithGpuRetryAsync(Func<bool, Task> start, Func<bool> canRetry)
    {
        try { await start(false); }
        catch (InvalidOperationException ex) when (IsNvencDeviceError(ex.ToString()) && canRetry())
        {
            // One retry only: native display frames may belong to an Intel/AMD
            // adapter, while CUDA upload selects an NVIDIA encoding device.
            await start(true);
        }
    }
    internal async Task StartAttemptAsync(Settings next, bool synthetic, bool transfer, CancellationToken cancellationToken = default, bool preserveBuffer = false)
    {
        string stage = "preparing the replay buffer";
        try
        {
            if (IsSaving && !preserveBuffer) throw new InvalidOperationException("Wait for the current clip to finish saving.");
            // Stream-copy requires compatible dimensions and encoding settings.
            preserveBuffer = preserveBuffer && !next.RequiresBufferRestart(settings)
                && RecordingSize == (synthetic ? (640, 360) : OutputSize(next));
            await StopCoreAsync(preserveBuffer);
            Mark("previous session stopped");
            LastRestartPreservedBuffer = retainedSegments.Count > 0;
            cancellationToken.ThrowIfCancellationRequested();
            next.Validate();
            if (!File.Exists(FfmpegPath)) throw new FileNotFoundException("The bundled recorder is missing. Extract the entire ZIP, including the tools folder.", FfmpegPath);
            Storage.EnsureWritable(next.OutputFolder);
            settings = next.Copy();
            var cache = Path.Combine(Storage.Root, "buffer");
            Directory.CreateDirectory(cache);
            // Only our GUID-named session directories are eligible for stale cleanup.
            foreach (var old in Directory.GetDirectories(cache, "session-*"))
            {
                if (!string.Equals(old, session, StringComparison.OrdinalIgnoreCase) && Guid.TryParseExact(Path.GetFileName(old)[8..], "N", out _)) TryDeleteDirectory(old);
            }
            session ??= Path.Combine(cache, "session-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(session);
            EnsureSpace(session, settings.EstimatedBufferMb + 512);
            LastError = ""; stopping = false; BufferedSeconds = 0; BufferBytes = 0;
            Volatile.Write(ref progressSeconds, timelineOffset); Volatile.Write(ref progressTick, Stopwatch.GetTimestamp());
            Volatile.Write(ref measuredFps, 0);
            lock (logLock) log.Clear();
            Mark("buffer folder ready");
            // Opening audio devices can take a few hundred milliseconds; keep it off the UI thread.
            var s = settings;
            await Task.Run(() =>
            {
                stage = "initializing desktop audio";
                if (s.DesktopAudio && !synthetic)
                {
                    // Per-app streams only when an app has a custom level; the whole device otherwise.
                    audio = s.MixerActive && AppMixSource.Supported
                        ? new AppMixSource(s.AudioDeviceId, s.AppVolumes) { Muted = s.DesktopMuted, Gain = s.DesktopVolume / 100.0 }
                        : new AudioLoopback(s.AudioDeviceId, hold: s.DesktopLocked) { Muted = s.DesktopMuted, Gain = s.DesktopVolume / 100.0 };
                    LastStartupReport += $"Audio source: {audio.DeviceName}; {(string.IsNullOrEmpty(s.AudioDeviceId) ? "follow Windows default" : "fixed playback device")}\n";
                }
                stage = "initializing microphone";
                if (s.MicrophoneAudio && !synthetic)
                {
                    microphone = new AudioLoopback(s.MicrophoneDeviceId, microphone: true, hold: s.MicrophoneLocked) { Muted = s.MicrophoneMuted, Gain = s.MicrophoneVolume / 100.0 };
                    LastStartupReport += $"Microphone: {microphone.DeviceName}; {(string.IsNullOrEmpty(s.MicrophoneDeviceId) ? "follow Windows communications default" : "fixed input device")}\n";
                }
            }, cancellationToken);
            Mark("audio devices opened");
            stage = "configuring display capture";
            RecordingSize = synthetic ? (640, 360) : OutputSize(settings);
            var display = synthetic ? null : CaptureDisplay.Resolve(settings.DisplayIndex);
            if (display != null)
                LastStartupReport += $"Capture: {display.DeviceName} -> DXGI adapter {display.AdapterIndex} ({display.AdapterName}, vendor 0x{display.VendorId:X4}), output {display.OutputIndex}.\n";
            if (!synthetic || UseBridgeForTests)
            {
                var size = RecordingSize;
                var originalDisplay = display?.DeviceName;
                video = new VideoFrameBridge(size.Width, size.Height, synthetic ? "bgra" : "nv12", settings.FrameRate, output =>
                {
                    var currentDisplay = synthetic ? null : CaptureDisplay.Resolve(originalDisplay!, CaptureDisplay.Enumerate());
                    return StartProcess(BuildProducerArguments(settings, synthetic, currentDisplay, selectedEncoder, size, output, SyncPatternForTests), session!);
                }, line =>
                {
                    lock (logLock) { log.Enqueue(line); while (log.Count > 100) log.Dequeue(); }
                    if (line.StartsWith("Display capture unavailable", StringComparison.Ordinal))
                        try { File.WriteAllText(Path.Combine(Storage.Root, "last-display-recovery.txt"), $"{DateTimeOffset.Now:O}\n{ErrorTail()}"); } catch { }
                }, synthetic || DisableNativeForTests ? null : framePool => new GpuDesktopCapture(CaptureDisplay.Resolve(originalDisplay!, CaptureDisplay.Enumerate()), size.Width, size.Height, settings.FrameRate, settings.ShowCursor, framePool));
            }
            var args = BuildArguments(settings, session, audio, synthetic, transfer, display, nextSegmentNumber, microphone, selectedEncoder, video, SyncPatternForTests);
            UsesGpuTransfer = args.Any(a => a.Contains("hwupload_cuda", StringComparison.Ordinal));
            LastStartupReport += $"Encoding path: {(synthetic ? "synthetic test" : video != null ? selectedEncoder?.IsAmd == true ? "NV12 frame bridge to AMD AMF" : "NV12 frame bridge to NVIDIA NVENC; GPU resize requested (compatibility fallback logged separately)" : UsesGpuTransfer ? "CUDA transfer to NVIDIA" : "direct NVIDIA display device")}.\n";
            Mark("capture configured");
            stage = "starting the recording engine";
            // Launching ffmpeg can take seconds while antivirus scans it; don't freeze the window.
            process = await Task.Run(() => StartProcess(args, session));
            Mark("encoder process launched");
            var running = process;
            stderrReader = ReadErrorsAsync(running);
            stdoutReader = ReadProgressAsync(running);
            video?.Start();
            audio?.Start(video == null ? null : () => Interlocked.Read(ref video.TimelineOrigin));
            microphone?.Start(video == null ? null : () => Interlocked.Read(ref video.TimelineOrigin));
            Mark("capture and audio started");
            // Announce recording once the encoder reports real encoded time (it reports every 0.25 s),
            // rather than waiting the full 2 s for the first segment file to be finalized.
            var timeout = Stopwatch.StartNew();
            while (timeout.Elapsed < TimeSpan.FromSeconds(20))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (running.HasExited)
                {
                    // Drain stderr before classifying the failure; process exit can
                    // otherwise race the final NVENC diagnostic lines.
                    if (stderrReader != null) await stderrReader;
                    throw new InvalidOperationException("Recording could not start. " + ErrorTail());
                }
                if (audio?.DeviceChanged == true) throw new IOException(AudioLoopback.DeviceChangedMessage);
                if (audio?.StreamStalled == true) throw new IOException(AudioLoopback.StreamStalledMessage);
                if (audio?.Failure != null) throw new InvalidOperationException("Desktop audio could not start. " + audio.Failure.Message + " Turn off desktop audio in Settings to record video while resolving the playback-device problem.", audio.Failure);
                CheckMicrophone();
                if (Volatile.Read(ref progressSeconds) > timelineOffset + .2 || ReadCurrentSegments().Count != 0)
                {
                    Mark("encoding confirmed");
                    try { File.WriteAllText(Path.Combine(Storage.Root, "last-start-timing.txt"), StartTimings); } catch { }
                    LastStartupReport += $"Started successfully: {EncoderName}, {(video != null ? "continuous frame bridge" : UsesGpuTransfer ? "CUDA transfer" : "capture device")}.\n";
                    maintenanceCancel = new();
                    maintenance = MaintenanceAsync(maintenanceCancel.Token, Generation);
                    return;
                }
                await Task.Delay(50, cancellationToken);
            }
            throw new TimeoutException("No video frames arrived. Wake your display and unlock Windows, then check the selected display and graphics driver.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await StopCoreAsync();
            throw;
        }
        catch (Exception ex)
        {
            await FinishFailedProcessAsync(cancellationToken);
            var detail = $"Could not start the buffer while {stage}. {FailureDetails(ex.Message, ErrorTail())}";
            LastStartupReport += $"Attempt: {(transfer ? "CUDA transfer retry" : "default capture path")}\nStage: {stage}\n{ex}\n{ErrorTail()}\n";
            await StopCoreAsync(preserveBuffer);
            throw new InvalidOperationException(detail, ex);
        }
    }
    internal static (int Width, int Height) OutputSize(Settings s)
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (s.DisplayIndex < 0 || s.DisplayIndex >= screens.Length)
            throw new ArgumentException("The selected display is no longer connected. Choose a display in Settings.");
        var bounds = screens[s.DisplayIndex].Bounds;
        double scale = s.Height == 0 ? 1 : Math.Min(1, s.Height / (double)bounds.Height);
        return (Math.Max(2, (int)(bounds.Width * scale / 2) * 2), Math.Max(2, (int)(bounds.Height * scale / 2) * 2));
    }
    internal static List<string> BuildArguments(Settings s, string dir, IRecordingAudio? audio, bool synthetic, bool transfer = false, CaptureDisplay? display = null, int startSegmentNumber = 0, AudioLoopback? microphone = null, VideoEncoder? encoder = null, VideoFrameBridge? bridge = null, bool syncTest = false)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "warning", "-nostats", "-y", "-filter_complex_threads", "2", "-stats_period", "0.25", "-progress", "pipe:1" };
        var audioInputs = new List<int>();
        string? videoFilter = null;
        if (bridge != null)
        {
            var size = synthetic ? (Width: 640, Height: 360) : OutputSize(s);
            if (!synthetic) args.AddRange(new[] { "-init_hw_device", $"d3d11va=encode:{encoder!.Adapter.Index}", "-filter_hw_device", "encode" });
            args.AddRange(new[] { "-thread_queue_size", "4", "-probesize", "32", "-analyzeduration", "0", "-f", "rawvideo", "-pixel_format", bridge.PixelFormat, "-video_size", $"{size.Width}x{size.Height}", "-framerate", s.FrameRate.ToString(), "-i", bridge.InputPath });
            if (synthetic)
            {
                if (s.DesktopAudio) { args.AddRange(new[] { "-re", "-f", "lavfi", "-i", syncTest ? "aevalsrc=if(lt(mod(t\\,2)\\,0.1)\\,0.3*sin(2*PI*1000*t)\\,0):s=48000" : "sine=frequency=440:sample_rate=48000" }); audioInputs.Add(1); }
                if (s.MicrophoneAudio) { args.AddRange(new[] { "-re", "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000" }); audioInputs.Add(1 + (s.DesktopAudio ? 1 : 0)); }
                args.AddRange(new[] { "-map", "0:v" });
            }
            else
            {
                foreach (var input in new IRecordingAudio?[] { audio, microphone }.Where(a => a != null))
                {
                    args.AddRange(new[] { "-thread_queue_size", "256", "-probesize", "32", "-analyzeduration", "0", "-f", input!.RawFormat, "-ar", input.Format.SampleRate.ToString(), "-ac", input.Format.Channels.ToString(), "-i", input.InputPath });
                    audioInputs.Add(audioInputs.Count + 1);
                }
                videoFilter = "[0:v]hwupload[video]";
            }
        }
        else if (synthetic)
        {
            args.AddRange(new[] { "-re", "-f", "lavfi", "-i", $"testsrc2=size=640x360:rate={s.FrameRate}" });
            if (s.DesktopAudio) { args.AddRange(new[] { "-re", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000" }); audioInputs.Add(1); }
            if (s.MicrophoneAudio) { args.AddRange(new[] { "-re", "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=44100" }); audioInputs.Add(1 + (s.DesktopAudio ? 1 : 0)); }
            args.AddRange(new[] { "-map", "0:v" });
        }
        else
        {
            display ??= CaptureDisplay.Resolve(s.DisplayIndex);
            encoder ??= new VideoEncoder(new VideoAdapter(display.AdapterIndex, display.AdapterName, display.VendorId == 0x1002 && s.Encoder != "NVIDIA NVENC" ? 0x1002u : 0x10deu));
            if (encoder.IsAmd && encoder.Adapter.Index != display.AdapterIndex) throw new InvalidOperationException("AMD capture requires a display connected to the selected AMD adapter.");
            args.AddRange(new[] { "-init_hw_device", $"d3d11va=capture:{display.AdapterIndex}", "-filter_hw_device", "capture" });
            foreach (var input in new IRecordingAudio?[] { audio, microphone }.Where(a => a != null))
            {
                args.AddRange(new[] { "-thread_queue_size", "256", "-probesize", "32", "-analyzeduration", "0", "-f", input!.RawFormat, "-ar", input.Format.SampleRate.ToString(), "-ac", input.Format.Channels.ToString(), "-i", input.InputPath });
                audioInputs.Add(audioInputs.Count);
            }
            // Desktop Duplication repeats still frames at the requested rate.
            // AMD keeps capture and resize on its adapter. NVIDIA resizing retains
            // the CUDA path because scale_d3d11 failed on the tested RTX 3070 Ti.
            var screens = System.Windows.Forms.Screen.AllScreens;
            if (s.DisplayIndex >= screens.Length) throw new ArgumentException("The selected display is no longer connected. Choose a display in Settings.");
            string filter = $"ddagrab=output_idx={display.OutputIndex}:framerate={s.FrameRate}:draw_mouse={(s.ShowCursor ? 1 : 0)}:output_fmt=bgra:dup_frames=1";
            var bounds = screens[s.DisplayIndex].Bounds;
            var (width, height) = OutputSize(s);
            bool resize = width != bounds.Width || height != bounds.Height;
            if (encoder.IsAmd)
            {
                filter += "," + encoder.CaptureConversion(width, height, resize);
            }
            else
            {
                if (resize || transfer || display.NeedsNvidiaTransfer) filter += ",hwdownload,format=bgra,hwupload_cuda";
                if (resize) filter += $",scale_cuda={width}:{height}:interp_algo=bilinear";
            }
            filter += "[video]";
            videoFilter = filter;
        }
        var filters = new List<string>();
        if (videoFilter != null) filters.Add(videoFilter);
        if (audioInputs.Count == 2)
        {
            // Normalize each independent device clock before mixing to one stereo track.
            for (int i = 0; i < audioInputs.Count; i++)
                filters.Add($"[{audioInputs[i]}:a:0]aresample=48000:async=1000:first_pts=0,aformat=channel_layouts=stereo[a{i}]");
            if (s.SeparateAudioTracks)
            {
                // Track 1 stays the combined mix so every player and Discord hears everything;
                // tracks 2 and 3 keep desktop and microphone apart for editing.
                filters.Add("[a0]asplit=2[a0mix][desktop];[a1]asplit=2[a1mix][voice]");
                filters.Add("[a0mix][a1mix]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0,alimiter=limit=0.95:level=0:latency=1[mixed]");
            }
            else filters.Add("[a0][a1]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0,alimiter=limit=0.95:level=0:latency=1[mixed]");
        }
        if (filters.Count > 0) args.AddRange(new[] { "-filter_complex", string.Join(";", filters) });
        if (!synthetic) args.AddRange(new[] { "-map", "[video]" });
        if (audioInputs.Count > 0) args.AddRange(new[] { "-map", audioInputs.Count == 2 ? "[mixed]" : $"{audioInputs[0]}:a:0" });
        if (audioInputs.Count == 2 && s.SeparateAudioTracks)
            args.AddRange(new[] { "-map", "[desktop]", "-map", "[voice]", "-metadata:s:a:0", "title=Combined", "-metadata:s:a:1", "title=Desktop", "-metadata:s:a:2", "title=Microphone" });
        if (synthetic)
            args.AddRange(new[] { "-c:v", "libx264", "-preset", "ultrafast", "-crf", "25", "-threads", "2", "-pix_fmt", "yuv420p" });
        else
            args.AddRange(encoder!.EncodingArguments(s));
        args.AddRange(new[] { "-g", (s.FrameRate * 2).ToString(), "-bf", "0", "-force_key_frames", "expr:gte(t,n_forced*2)", "-fps_mode", "cfr", "-r", s.FrameRate.ToString() });
        if (audioInputs.Count > 0)
        {
            args.AddRange(new[] { "-c:a", "aac", "-b:a", s.AudioBitrate + "k", "-ac", "2", "-ar", "48000" });
            if (audioInputs.Count == 1) args.AddRange(new[] { "-af", "aresample=async=1000:first_pts=0" });
        }
        args.AddRange(new[] { "-f", "segment", "-segment_time", "2", "-segment_time_delta", "0.02", "-segment_format", "mpegts", "-segment_list", "segments.csv", "-segment_list_type", "csv", "-segment_list_size", (s.ReplaySeconds / 2 + 12).ToString(), "-reset_timestamps", "1", "part-%09d.ts" });
        args.InsertRange(args.Count - 1, new[] { "-segment_start_number", startSegmentNumber.ToString(CultureInfo.InvariantCulture) });
        return args;
    }
    private static List<string> BuildProducerArguments(Settings s, bool synthetic, CaptureDisplay? display, VideoEncoder? encoder, (int Width, int Height) size, string output, bool syncTest = false)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "warning", "-nostdin", "-y", "-filter_complex_threads", "2" };
        if (synthetic) args.AddRange(new[] { "-re", "-readrate_initial_burst", "0", "-f", "lavfi", "-i", $"testsrc2=size={size.Width}x{size.Height}:rate={s.FrameRate}" + (syncTest ? ",drawbox=x=0:y=0:w=64:h=64:color=black:t=fill,drawbox=x=0:y=0:w=64:h=64:color=white:t=fill:enable='lt(mod(t,2),0.1)'" : ""), "-pix_fmt", "bgra" });
        else
        {
            args.AddRange(new[] { "-init_hw_device", $"d3d11va=capture:{display!.AdapterIndex}", "-filter_hw_device", "capture" });
            string filter = $"ddagrab=output_idx={display.OutputIndex}:framerate={s.FrameRate}:draw_mouse={(s.ShowCursor ? 1 : 0)}:output_fmt=bgra:dup_frames=1";
            // Fixed output dimensions keep the persistent encoder alive across display mode changes.
            filter += encoder!.IsAmd
                ? "," + encoder.CaptureConversion(size.Width, size.Height, true) + ",hwdownload,format=nv12"
                : $",hwdownload,format=bgra,scale={size.Width}:{size.Height}:flags=fast_bilinear,format=nv12";
            args.AddRange(new[] { "-filter_complex", filter + "[frames]", "-map", "[frames]" });
        }
        args.AddRange(new[] { "-c:v", "rawvideo", "-threads", "1", "-f", "rawvideo", output });
        return args;
    }
    private Process StartProcess(IEnumerable<string> arguments, string directory)
    {
        var start = new ProcessStartInfo(FfmpegPath) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        var p = Process.Start(start) ?? throw new IOException("Could not start the recording engine.");
        try { job.Add(p); p.PriorityClass = ProcessPriorityClass.Normal; }
        catch { try { p.Kill(true); } catch { } p.Dispose(); throw; }
        return p;
    }
    private async Task ReadErrorsAsync(Process p)
    {
        try
        {
            while (await p.StandardError.ReadLineAsync() is { } line)
                lock (logLock) { log.Enqueue(line); while (log.Count > 100) log.Dequeue(); }
        }
        catch { }
    }
    private async Task ReadProgressAsync(Process p)
    {
        try
        {
            while (await p.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.StartsWith("out_time_us=") && long.TryParse(line[12..], out var us))
                { Volatile.Write(ref progressSeconds, timelineOffset + us / 1000000.0); Volatile.Write(ref progressTick, Stopwatch.GetTimestamp()); }
                else if (line.StartsWith("fps=") && double.TryParse(line[4..], NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) && double.IsFinite(fps) && fps >= 0)
                    Volatile.Write(ref measuredFps, fps);
            }
        }
        catch { }
    }
    private string ErrorTail() { lock (logLock) return string.Join(Environment.NewLine, log.TakeLast(12)); }
    internal static string FailureDetails(string message, string stderr) => string.IsNullOrWhiteSpace(stderr) || message.Contains(stderr, StringComparison.Ordinal) ? message : message + Environment.NewLine + stderr;
    private void CheckMicrophone()
    {
        if (microphone?.DeviceChanged == true) throw new IOException(AudioLoopback.MicrophoneChangedMessage);
        if (microphone?.StreamStalled == true) throw new IOException(AudioLoopback.MicrophoneStalledMessage);
        if (microphone?.Failure != null) throw new IOException("Microphone capture stopped. " + microphone.Failure.Message, microphone.Failure);
    }
    private async Task FinishFailedProcessAsync(CancellationToken token)
    {
        // A broken audio pipe can precede FFmpeg's final DXGI diagnostic and exit.
        // Give that diagnostic a bounded chance to arrive before classifying recovery.
        try
        {
            if (process is { HasExited: false } running)
            {
                try { await running.WaitForExitAsync(token).WaitAsync(TimeSpan.FromMilliseconds(750), token); }
                catch (TimeoutException) { }
                if (!running.HasExited) running.Kill(true);
            }
            if (stderrReader != null) await stderrReader.WaitAsync(TimeSpan.FromSeconds(2), token);
        }
        catch (Exception) when (!token.IsCancellationRequested) { }
        catch (OperationCanceledException) { }
    }
    private List<Segment> ReadSegments() => retainedSegments.Concat(ReadCurrentSegments().Select(s => s with { Start = s.Start + timelineOffset, End = s.End + timelineOffset })).ToList();
    private List<Segment> ReadCurrentSegments()
    {
        if (session == null) return new();
        try
        {
            using var stream = new FileStream(Path.Combine(session, "segments.csv"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return SegmentIndex.Parse(reader.ReadToEnd());
        }
        catch (IOException) { return new(); }
    }
    private async Task MaintenanceAsync(CancellationToken token, long generation)
    {
        var lagMonitor=new EncoderLagMonitor();
        try
        {
            while (true)
            {
                await Task.Delay(1000, token);
                if (Interlocked.Exchange(ref injectedFailure, null) is { } failure) throw failure;
                if (process == null || process.HasExited)
                {
                    if (stderrReader != null) await stderrReader;
                    token.ThrowIfCancellationRequested();
                    throw new IOException("The recorder stopped. " + ErrorTail());
                }
                if (audio?.DeviceChanged == true) throw new IOException(AudioLoopback.DeviceChangedMessage);
                if (audio?.StreamStalled == true) throw new IOException(AudioLoopback.StreamStalledMessage);
                if (audio?.Failure != null) throw new IOException("Audio capture stopped. " + audio.Failure.Message);
                CheckMicrophone();
                if (video is { TimelineOrigin: >0 } bridge && lagMonitor.Observe(Stopwatch.GetElapsedTime(bridge.TimelineOrigin).TotalSeconds,bridge.EncoderLagSeconds)) throw new IOException(EncoderLagMonitor.Message);
                if (video?.Failure is { } videoError) throw new IOException("Video input pipe failed.", videoError);
                if (Stopwatch.GetElapsedTime(Volatile.Read(ref progressTick)).TotalSeconds > 15)
                    throw new IOException("No new video frames arrived. Wake your display and unlock Windows, then start the buffer again.");
                await files.WaitAsync(token);
                try
                {
                    var all = ReadSegments();
                    if (all.Count == 0) continue;
                    var cutoff = IsSaving ? double.NegativeInfinity : Math.Max(savedThrough - 2, all[^1].End - settings.ReplaySeconds - 8);
                    var names = all.Where(s => s.End > cutoff).Select(s => s.Name).ToHashSet();
                    // Never delete the current, unfinalized segment (its sequence is larger).
                    var latest = all[^1].Name;
                    foreach (var path in Directory.EnumerateFiles(session!, "part-*.ts"))
                        if (string.CompareOrdinal(Path.GetFileName(path), latest) < 0 && !names.Contains(Path.GetFileName(path)))
                            try { File.Delete(path); } catch (IOException) { }
                    retainedSegments = retainedSegments.Where(s => s.End > cutoff).ToList();
                    BufferedSeconds = Math.Max(0, Math.Min(settings.ReplaySeconds, all[^1].End - Math.Max(savedThrough, all.First(s => names.Contains(s.Name)).Start)));
                    BufferBytes = Directory.EnumerateFiles(session!, "*.ts").Sum(p => new FileInfo(p).Length);
                    EnsureSpace(session!, 256);
                }
                finally { files.Release(); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            string timing=$"Encoder backlog: {video?.EncoderLagSeconds:0.000}s\n{VideoStats}";
            await FinishFailedProcessAsync(token);
            if (token.IsCancellationRequested) return;
            string currentErrors;
            lock (logLock) currentErrors = string.Join(Environment.NewLine, log);
            LastError = FailureDetails(ex.Message, currentErrors);
            LastStartupReport += $"Recording interrupted at {DateTimeOffset.Now:O}: {ex}\n{timing}\n{ErrorTail()}\n";
            try { Directory.CreateDirectory(Storage.Root); File.WriteAllText(Path.Combine(Storage.Root, "last-error.txt"), LastStartupReport); } catch { }
            Faulted?.Invoke(generation, LastError);
        }
    }
    public async Task<ClipResult> SaveAsync(string game, DateTimeOffset pressedAt)
    {
        if (session == null) throw new InvalidOperationException("Start the replay buffer before saving a clip.");
        double end = RecordedSeconds + Math.Clamp(Stopwatch.GetElapsedTime(Volatile.Read(ref progressTick)).TotalSeconds, 0, 1);
        // Recording is announced as soon as frames encode; a save in the first moments still gets
        // the whole first two-second segment rather than a sliver.
        end = Math.Max(end, timelineOffset + 2);
        Interlocked.Increment(ref saving);
        await saver.WaitAsync().ConfigureAwait(false);
        string? staging = null;
        string? partial = null;
        try
        {
            if (session == null) throw new InvalidOperationException("Start the replay buffer before saving a clip.");
            // Include the current segment so the moment of the key press is never lost.
            // This intentionally adds at most one GOP (about two seconds) of tail.
            double start = Math.Max(savedThrough, end - settings.ReplaySeconds);
            var deadline = Stopwatch.StartNew();
            List<Segment> selected;
            while (true)
            {
                var segments = ReadSegments();
                if (segments.Count != 0 && (segments[^1].End >= end || !IsRecording))
                {
                    end = Math.Min(end, segments[^1].End);
                    selected = segments.Where(s => s.End > start && s.Start < end).ToList();
                    break;
                }
                if (deadline.Elapsed.TotalSeconds > 10) throw new TimeoutException("The current video segment did not finish. Try restarting the replay buffer.");
                await Task.Delay(100).ConfigureAwait(false);
            }
            if (selected.Count == 0) throw new IOException("The replay buffer is still warming up.");
            start = Math.Max(start, selected[0].Start);
            double duration = end - start;
            if (duration < 0.05) throw new IOException("No new footage has arrived since the last clip.");
            game = Storage.SafeName(game);
            string destination = Path.Combine(settings.OutputFolder, game);
            Storage.EnsureWritable(destination);
            var output = Path.Combine(destination, Storage.ClipName(game, pressedAt, duration));
            if (File.Exists(output)) output = Path.Combine(destination, Path.GetFileNameWithoutExtension(output) + "-" + Guid.NewGuid().ToString("N")[..6] + ".mp4");
            partial = output + ".partial";
            EnsureSpace(destination, selected.Sum(s => new FileInfo(Path.Combine(session, s.Name)).Length) / 1048576.0 + 128);
            staging = Path.Combine(session, "save-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            await files.WaitAsync().ConfigureAwait(false);
            try
            {
                // Snapshot completed chunks while cleanup is locked. Never overwrite or move saved clips.
                foreach (var seg in selected)
                {
                    // A hard link keeps the chunk alive after buffer cleanup without rewriting
                    // hundreds of megabytes mid-game; copying is only the fallback.
                    string source = Path.Combine(session, seg.Name), target = Path.Combine(staging, seg.Name);
                    if (CreateHardLink(target, source, IntPtr.Zero)) continue;
                    using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                    using var copy = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                    await input.CopyToAsync(copy).ConfigureAwait(false);
                }
            }
            finally { files.Release(); }
            File.WriteAllLines(Path.Combine(staging, "clip.ffconcat"), new[] { "ffconcat version 1.0" }.Concat(selected.SelectMany(s => new[] { $"file '{s.Name}'", "duration " + s.Duration.ToString("0.000000", CultureInfo.InvariantCulture) })), new UTF8Encoding(false));
            using var mux = StartProcess(new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-f", "concat", "-safe", "1", "-ss", (start - selected[0].Start).ToString("0.000000", CultureInfo.InvariantCulture), "-i", "clip.ffconcat", "-t", duration.ToString("0.000000", CultureInfo.InvariantCulture), "-map", "0:v:0", "-map", "0:a?", "-c", "copy", "-bsf:a", "aac_adtstoasc", "-movflags", "+faststart", "-avoid_negative_ts", "disabled", "-f", "mp4", partial }, staging);
            var errors = mux.StandardError.ReadToEndAsync();
            var unused = mux.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try { await mux.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch { try { mux.Kill(true); } catch { } throw; }
            var error = await errors; await unused;
            if (mux.ExitCode != 0 || !File.Exists(partial) || new FileInfo(partial).Length < 1000) throw new IOException("The clip could not be finalized. " + error);
            File.Move(partial, output); partial = null;
            savedThrough = end;
            BufferedSeconds = Math.Max(0, RecordedSeconds - savedThrough);
            var result = new ClipResult(output, duration, game, pressedAt);
            try { File.WriteAllText(Path.Combine(Storage.Root, "last-capture-performance.txt"), $"{DateTimeOffset.Now:O}\n{VideoStats}\n"); } catch { }
            try { File.AppendAllText(Path.Combine(Storage.Root, "clips.jsonl"), System.Text.Json.JsonSerializer.Serialize(result) + Environment.NewLine); } catch { }
            return result;
        }
        finally
        {
            if (partial != null) try { File.Delete(partial); } catch { }
            if (staging != null) TryDeleteDirectory(staging);
            Interlocked.Decrement(ref saving); saver.Release();
        }
    }
    public async Task StopAsync()
    {
        Generation++;
        await saver.WaitAsync();
        await lifecycle.WaitAsync();
        try { await StopCoreAsync(); }
        finally { lifecycle.Release(); saver.Release(); }
    }
    internal async Task RestartAfterCaptureLossAsync(Settings next, bool synthetic, CancellationToken token)
    {
        // Let an in-flight save finish or report its failure before touching chunks.
        await saver.WaitAsync(token);
        try { await StartAsync(next, synthetic, token, preserveBuffer: true); }
        finally { saver.Release(); }
    }
    internal void InjectCaptureLossForTest(bool audioPipeFirst = false, bool releaseFrame = false)
    {
        lock (logLock) log.Enqueue(releaseFrame ? "[Parsed_ddagrab_0] DDA ReleaseFrame failed!" : "[Parsed_ddagrab_0] AcquireNextFrame failed: 887a0026");
        if (audioPipeFirst) Interlocked.Exchange(ref injectedFailure, new IOException("Audio capture stopped. Pipe is broken."));
        else process?.Kill(true);
    }
    internal void InjectEncoderBacklogForTest() => Interlocked.Exchange(ref injectedFailure,new IOException(EncoderLagMonitor.Message));
    private async Task StopCoreAsync(bool preserveBuffer = false)
    {
        stopping = true;
        maintenanceCancel?.Cancel();
        if (maintenance != null) { try { await maintenance; } catch { } maintenance = null; }
        maintenanceCancel?.Dispose(); maintenanceCancel = null;
        if (process != null)
        {
            try
            {
                if (!process.HasExited)
                {
                    await process.StandardInput.WriteLineAsync("q");
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    try { await process.WaitForExitAsync(timeout.Token); }
                    catch { process.Kill(true); await process.WaitForExitAsync(); }
                }
            }
            catch { try { process.Kill(true); } catch { } }
            if (stdoutReader != null) await stdoutReader;
            if (stderrReader != null) await stderrReader;
            process.Dispose(); process = null;
        }
        if (audio != null) { await audio.DisposeAsync(); audio = null; }
        if (microphone != null) { await microphone.DisposeAsync(); microphone = null; }
        if (video != null) { await video.DisposeAsync(); video = null; }
        injectedFailure = null;
        if (preserveBuffer && session != null)
        {
            retainedSegments = ReadSegments().Where(s => File.Exists(Path.Combine(session, s.Name))).ToList();
            // Advance past even an unfinished file, so no previous bytes are overwritten.
            nextSegmentNumber = Directory.EnumerateFiles(session, "part-*.ts")
                .Select(p => int.TryParse(Path.GetFileNameWithoutExtension(p).AsSpan(5), out var n) ? n + 1 : 0).DefaultIfEmpty(nextSegmentNumber).Max();
            timelineOffset = retainedSegments.LastOrDefault()?.End ?? timelineOffset;
            File.Delete(Path.Combine(session, "segments.csv"));
        }
        else
        {
            if (session != null) { TryDeleteDirectory(session); session = null; }
            retainedSegments.Clear(); timelineOffset = 0; nextSegmentNumber = 0; savedThrough = 0;
        }
        BufferedSeconds = 0; BufferBytes = 0; stopping = false;
    }
    private static void EnsureSpace(string path, double minimumMb)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (root == null || root.StartsWith(@"\\")) return;
        var drive = new DriveInfo(root);
        if (drive.IsReady && drive.AvailableFreeSpace < minimumMb * 1048576) throw new IOException("Not enough free disk space. Choose another clip folder or free some space.");
    }
    private static void TryDeleteDirectory(string path) { try { Directory.Delete(path, true); } catch { } }
    public async ValueTask DisposeAsync() { await StopAsync(); job.Dispose(); }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string newFile, string existingFile, IntPtr security);
}









