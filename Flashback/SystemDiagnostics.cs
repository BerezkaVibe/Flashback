using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.Wave;

namespace Flashback;

internal static class SystemDiagnostics
{
    internal static async Task<bool> RunAsync(bool probes = false, bool longBuffer = false)
    {
        Directory.CreateDirectory(Storage.Root);
        var results = new List<object>();
        bool allPassed = true;
        void Result(string test, bool passed, string detail)
        {
            allPassed &= passed;
            results.Add(new { test, passed, detail });
            File.WriteAllText(Path.Combine(Storage.Root, "system-results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        }
        var originalForeground = NativeOverlay.GetForegroundWindow();
        var label = new TextBlock { Foreground = Brushes.White, FontSize = 28, Margin = new Thickness(60), TextWrapping = TextWrapping.Wrap };
        var scene = new Window { Title = "Flashback local recording test", Background = new SolidColorBrush(Color.FromRgb(18, 28, 38)), WindowState = WindowState.Maximized, Topmost = true, Content = label };
        var animation = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        var elapsed = Stopwatch.StartNew(); int frame = 0, clicks = 0;
        animation.Tick += (_, _) => { label.Text = $"Flashback local test\n\nAnimated desktop fixture · frame {++frame}\n{elapsed.Elapsed.TotalSeconds:0.00} seconds\n\nThis window and test recording close automatically."; label.Margin = new Thickness(60 + 25 * Math.Sin(elapsed.Elapsed.TotalSeconds), 60, 60, 60); };
        scene.PreviewMouseDown += (_, _) => clicks++;
        SystemTestNative.SetThreadExecutionState(0x80000003);
        OverlayWindow? flag = null;
        try
        {
            scene.Show(); scene.Activate(); animation.Start(); await Task.Delay(1000);
            var handle = new WindowInteropHelper(scene).Handle;
            // The test runner is launched hidden; explicitly reveal its dedicated interactive fixture.
            // STARTUPINFO's show command can override a process's first Window.Show call.
            SystemTestNative.ShowWindow(handle, 3);
            SystemTestNative.SetForegroundWindow(handle);
            await Task.Delay(500);
            if (probes)
            {
                await CaptureProbesAsync(Result);
                return allPassed;
            }
            using (var hotkeys = new Hotkeys())
            {
                int saves = 0, pauses = 0;
                hotkeys.Register("Ctrl+Alt+Shift+F10", "Ctrl+Alt+Shift+F11");
                hotkeys.Pressed += () => saves++; hotkeys.PausePressed += () => pauses++;
                // Successfully registered global shortcuts should work even with another app in front.
                // These reserved, non-text key combinations are owned by this test for its duration.
                SystemTestNative.Chord(0x79); await Task.Delay(200); SystemTestNative.Chord(0x7A); await Task.Delay(200);
                Result("Real global hotkey delivery", saves == 1 && pauses == 1, $"Save events: {saves}; pause events: {pauses}");
                hotkeys.Register("F23", "F24");
                SystemTestNative.SingleKey(0x86); await Task.Delay(200); SystemTestNative.SingleKey(0x87); await Task.Delay(200);
                Result("Single-key save and pause shortcuts deliver globally", saves == 2 && pauses == 2, $"Save events: {saves}; pause events: {pauses}");
            }
            var beforeFlag = NativeOverlay.GetForegroundWindow();
            flag = new OverlayWindow();
            flag.Present(SaveFeedback.Saving, "Testing a real desktop overlay", new Settings(), SaveOverlay.CurrentMonitor());
            await Task.Delay(200);
            Result("Visible overlay preserves focus", flag.IsVisible && NativeOverlay.GetForegroundWindow() == beforeFlag, "Compared the foreground window immediately before and after showing the flag.");
            SystemTestNative.GetWindowRect(flag.Handle, out var rectangle);
            var pt = new SystemTestNative.Point { X = (rectangle.Left + rectangle.Right) / 2, Y = (rectangle.Top + rectangle.Bottom) / 2 };
            var under = SystemTestNative.GetAncestor(SystemTestNative.WindowFromPoint(pt), 2);
            if (under == handle || under == flag.Handle)
            {
                SystemTestNative.GetCursorPos(out var oldCursor);
                try { SystemTestNative.SetCursorPos(pt.X, pt.Y); SystemTestNative.Click(); await Task.Delay(250); }
                finally { SystemTestNative.SetCursorPos(oldCursor.X, oldCursor.Y); }
                Result("Overlay click-through", clicks == 1 && NativeOverlay.GetForegroundWindow() == handle, $"Fixture received {clicks} click(s) underneath the overlay.");
            }
            else Result("Overlay click-through", false, "An unrelated window covered the test area; click was not sent.");
            flag.Present(SaveFeedback.Saved, "Overlay timer test", new Settings { OverlaySeconds = 2 }, SaveOverlay.CurrentMonitor());
            bool closed = false; flag.Closed += (_, _) => closed = true;
            var dismissal = Stopwatch.StartNew();
            while (!closed && dismissal.Elapsed.TotalSeconds < 4) await Task.Delay(50);
            Result("Visible overlay auto-dismissal", closed, $"Configured 2s; observed closure after {dismissal.Elapsed.TotalSeconds:0.00}s.");
            if (!closed) flag.Close(); flag = null;

            // Test actual WASAPI independently of whether the physical display supplies video frames.
            try
            {
                using var deviceEnumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                using var chosenDevice = deviceEnumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
                await using var loopback = new AudioLoopback(chosenDevice.ID);
                using var tone = new WasapiOut(); tone.Init(new TestTone());
                var wave = Path.Combine(Storage.Root, "desktop-audio-test.wav");
                using var job = new ChildProcessJob();
                using var recording = Launch(new[] { "-hide_banner", "-loglevel", "warning", "-y", "-probesize", "32", "-analyzeduration", "0", "-f", loopback.RawFormat, "-ar", loopback.Format.SampleRate.ToString(), "-ac", loopback.Format.Channels.ToString(), "-i", loopback.InputPath, "-t", "6", "-c:a", "pcm_s16le", wave });
                job.Add(recording); var errors = recording.StandardError.ReadToEndAsync(); var output = recording.StandardOutput.ReadToEndAsync();
                tone.Play(); loopback.Start();
                var otherScreen = System.Windows.Forms.Screen.AllScreens.Last();
                var focusWindow = new Window { Title = "Flashback audio focus test", Width = 260, Height = 120, Left = otherScreen.WorkingArea.Left + 40, Top = otherScreen.WorkingArea.Top + 40, Content = new System.Windows.Controls.TextBlock { Text = "Audio continues across focus changes", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20) } };
                await Task.Delay(2500);
                focusWindow.Show(); focusWindow.Activate();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { await recording.WaitForExitAsync(deadline.Token); }
                finally { focusWindow.Close(); tone.Stop(); if (!recording.HasExited) recording.Kill(true); }
                var error = await errors; await output;
                if (recording.ExitCode != 0) throw new Exception(error);
                using var reader = new WaveFileReader(wave); byte[] samples = new byte[reader.Length]; reader.Read(samples, 0, samples.Length);
                double energy = 0; for (int i = 0; i + 1 < samples.Length; i += 2) { double sample = BitConverter.ToInt16(samples, i) / 32768.0; energy += sample * sample; }
                double rms = Math.Sqrt(energy / (samples.Length / 2));
                Result("Actual desktop audio loopback", reader.TotalTime.TotalSeconds >= 5.9 && rms > .001, $"Duration {reader.TotalTime.TotalSeconds:0.000}s; RMS {rms:0.000000}; file {wave}");
                double tailEnergy = 0; int tailStart = samples.Length * 2 / 3 / 2 * 2;
                for (int i = tailStart; i + 1 < samples.Length; i += 2) { double sample = BitConverter.ToInt16(samples, i) / 32768.0; tailEnergy += sample * sample; }
                Result("Pinned playback audio survives window focus change", Math.Sqrt(tailEnergy / ((samples.Length - tailStart) / 2)) > .001 && !loopback.DeviceChanged, $"Audio source: {loopback.DeviceName}; available displays: {System.Windows.Forms.Screen.AllScreens.Length}");
            }
            catch (Exception ex) { Result("Actual desktop audio loopback", false, ex.Message); }

            await using var recorder = new Recorder();
            try
            {
                int replay = longBuffer ? 300 : 30;
                var settings = new Settings { ReplaySeconds = replay, OutputFolder = Path.Combine(Storage.Root, "clips"), DesktopAudio = true, Notifications = false };
                await recorder.StartAsync(settings);
                await Task.Delay(6000);
                var clip = await recorder.SaveAsync("Local test fixture", DateTimeOffset.Now);
                Result("Live desktop replay with audio", File.Exists(clip.Path), clip.Path);
                using var decode = Launch(new[] { "-hide_banner", "-loglevel", "error", "-i", clip.Path, "-f", "null", "-" });
                var error = decode.StandardError.ReadToEndAsync(); var output = decode.StandardOutput.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await decode.WaitForExitAsync(timeout.Token); } finally { if (!decode.HasExited) decode.Kill(true); }
                var diagnostics = await error; await output;
                Result("Live replay playback decoding", decode.ExitCode == 0 && string.IsNullOrWhiteSpace(diagnostics), diagnostics);
                double beforeStatic = recorder.RecordedSeconds;
                int stillSeconds = longBuffer ? 20 : 6;
                animation.Stop(); await Task.Delay(stillSeconds * 1000);
                Result("Capture continues on a static desktop", recorder.IsRecording && recorder.RecordedSeconds >= beforeStatic + stillSeconds - 2,
                    $"Video clock advanced {recorder.RecordedSeconds - beforeStatic:0.00}s during a {stillSeconds}s static interval.");
                var still = await recorder.SaveAsync("Static desktop", DateTimeOffset.Now);
                Result("Save while the desktop is static", File.Exists(still.Path), still.Path);
                animation.Start();
                var fill = Stopwatch.StartNew();
                // Saves never repeat footage, so a full-length clip needs a whole replay recorded after the last save.
                while (recorder.IsRecording && recorder.RecordedSeconds < Math.Max(replay + 7, recorder.SavedThrough + replay + 2) && fill.Elapsed.TotalSeconds < replay + 25) await Task.Delay(200);
                var full = await recorder.SaveAsync("Full buffer test", DateTimeOffset.Now);
                Result("Full hardware replay duration", Math.Abs(full.Duration - replay) <= .1, $"{full.Duration:0.000}s; {full.Path}");
                int chunks = Directory.EnumerateFiles(Path.Combine(Storage.Root, "buffer"), "*.ts", SearchOption.AllDirectories).Count();
                Result("Hardware buffer retention", chunks <= replay / 2 + 7, $"{chunks} temporary chunks retained for a {replay}s buffer.");
                settings.Height = 720; settings.FrameRate = 30; settings.DesktopAudio = false;
                await recorder.StartAsync(settings); await Task.Delay(2500);
                var compact = await recorder.SaveAsync("720p 30 FPS no audio", DateTimeOffset.Now);
                Result("720p 30 FPS audio-off recording", File.Exists(compact.Path), compact.Path);
                settings.Height = 0; settings.FrameRate = 120;
                await recorder.StartAsync(settings); await Task.Delay(2500);
                var native = await recorder.SaveAsync("Native 120 FPS", DateTimeOffset.Now);
                Result("Native resolution 120 FPS recording", File.Exists(native.Path), native.Path);
                await recorder.StopAsync();
                settings.FrameRate = 60; settings.ReplaySeconds = 300; settings.DesktopAudio = true;
                // Exercise the actual fallback pipeline with a simulated first-device
                // rejection. This PC does not have a second integrated display adapter.
                await Recorder.StartWithGpuRetryAsync(async transfer =>
                {
                    if (!transfer) throw new InvalidOperationException("OpenEncodeSessionEx failed: no encode device (1)");
                    await recorder.StartAttemptAsync(settings, synthetic: false, transfer: transfer);
                }, () => true);
                var retryClip = await recorder.SaveAsync("GPU transfer retry", DateTimeOffset.Now);
                Result("Native GPU transfer retry with audio and partial replay", (recorder.UsesGpuTransfer || recorder.UsesFrameBridge) && retryClip.Duration >= 1.9 && retryClip.Duration < 15 && File.Exists(retryClip.Path), $"GPU transfer {recorder.UsesGpuTransfer}, frame bridge {recorder.UsesFrameBridge}; {retryClip.Duration:0.000}s; {retryClip.Path}");
                using var retryDecode = Launch(new[] { "-hide_banner", "-loglevel", "error", "-i", retryClip.Path, "-f", "null", "-" });
                var retryErrors = retryDecode.StandardError.ReadToEndAsync(); var retryOutput = retryDecode.StandardOutput.ReadToEndAsync();
                using var retryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await retryDecode.WaitForExitAsync(retryTimeout.Token); } finally { if (!retryDecode.HasExited) retryDecode.Kill(true); }
                var retryLog = await retryErrors; await retryOutput;
                Result("GPU transfer replay fully decodes", retryDecode.ExitCode == 0 && string.IsNullOrWhiteSpace(retryLog), retryLog);
                var beforeRecovery = recorder.RecordedSeconds; var savedBefore = recorder.SavedThrough;
                // Stop the actual encoder as if DXGI access was lost, then use the
                // same restart path as the UI without changing Windows display mode.
                recorder.InjectCaptureLossForTest(audioPipeFirst: true, releaseFrame: true);
                await recorder.RestartAfterCaptureLossAsync(settings, false, CancellationToken.None);
                var recovered = await recorder.SaveAsync("Recovered hardware replay", DateTimeOffset.Now);
                Result("Hardware audio/video buffer survives capture reconnection", recorder.LastRestartPreservedBuffer && recovered.Duration > beforeRecovery - savedBefore + 1, $"Unsaved before the loss {beforeRecovery - savedBefore:0.000}s; replay {recovered.Duration:0.000}s; {recovered.Path}");
                using var recoveredDecode = Launch(new[] { "-hide_banner", "-loglevel", "error", "-i", recovered.Path, "-f", "null", "-" });
                var recoveredErrors = recoveredDecode.StandardError.ReadToEndAsync(); var recoveredOutput = recoveredDecode.StandardOutput.ReadToEndAsync();
                await recoveredDecode.WaitForExitAsync();
                Result("Hardware replay across capture sessions fully decodes", recoveredDecode.ExitCode == 0 && string.IsNullOrWhiteSpace(await recoveredErrors), await recoveredOutput);
            }
            catch (Exception ex) { Result("Live desktop replay with audio", false, ex.Message); }
            finally { await recorder.StopAsync(); }
        }
        finally
        {
            flag?.Close(); animation.Stop(); scene.Close();
            SystemTestNative.SetThreadExecutionState(0x80000000);
            if (originalForeground != IntPtr.Zero) SystemTestNative.SetForegroundWindow(originalForeground);
        }
        return allPassed;
    }
    private static Process Launch(string[] args)
    {
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info)!;
    }
    private static async Task CaptureProbesAsync(Action<string, bool, string> result)
    {
        var baseFilter = "ddagrab=output_idx=0:framerate=60:draw_mouse=0";
        var probes = new Dictionary<string, string>
        {
            ["cuda-direct"] = baseFilter + ",hwmap=derive_device=cuda,scale_cuda=1920:1080",
            ["cuda-copy"] = baseFilter + ",hwdownload,format=bgra,hwupload_cuda,scale_cuda=1920:1080",
            ["cpu-scale"] = baseFilter + ",hwdownload,format=bgra,scale=1920:1080:flags=fast_bilinear,format=nv12"
        };
        foreach (var probe in probes)
        {
            using var process = Launch(new[] { "-hide_banner", "-loglevel", "verbose", "-y", "-filter_complex", probe.Value + "[v]", "-map", "[v]", "-c:v", "h264_nvenc", "-preset", "p1", "-tune", "ll", "-bf", "0", "-profile:v", "high", "-t", "3", Path.Combine(Storage.Root, probe.Key + ".mp4") });
            var error = process.StandardError.ReadToEndAsync(); var output = process.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); }
            var log = await error; await output;
            File.WriteAllText(Path.Combine(Storage.Root, probe.Key + ".log"), log);
            result(probe.Key, process.ExitCode == 0, string.Join("\n", log.Split('\n').Where(l => l.Contains("texture") || l.Contains("w:") || l.Contains("Video:") || l.Contains("Unsupported") || l.Contains("Error")).TakeLast(12)));
        }
    }
    private sealed class TestTone : IWaveProvider
    {
        private long position;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public int Read(byte[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i += 8)
            {
                var value = (float)(.05 * Math.Sin(2 * Math.PI * 523.25 * position / 48000.0));
                if (position % 48000 >= 24000) value = 0;
                var bytes = BitConverter.GetBytes(value); Buffer.BlockCopy(bytes, 0, buffer, offset + i, 4); Buffer.BlockCopy(bytes, 0, buffer, offset + i + 4, 4); position++;
            }
            return count;
        }
    }
}

internal static class SystemTestNative
{
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Explicit)] private struct Data { [FieldOffset(0)] public KeyboardInput Keyboard; [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public Data Data; }
    internal static void Chord(ushort key)
    {
        ushort[] keys = { 0x11, 0x12, 0x10, key };
        var sequence = keys.Select(k => new Input { Type = 1, Data = new Data { Keyboard = new KeyboardInput { Key = k } } })
            .Concat(keys.Reverse().Select(k => new Input { Type = 1, Data = new Data { Keyboard = new KeyboardInput { Key = k, Flags = 2 } } })).ToArray();
        if (SendInput((uint)sequence.Length, sequence, Marshal.SizeOf<Input>()) != sequence.Length) throw new Exception("Windows did not accept the test keyboard input.");
    }
    internal static void Click()
    {
        Input[] sequence = { new() { Data = new Data { Mouse = new MouseInput { Flags = 2 } } }, new() { Data = new Data { Mouse = new MouseInput { Flags = 4 } } } };
        if (SendInput(2, sequence, Marshal.SizeOf<Input>()) != 2) throw new Exception("Windows did not accept the test mouse input.");
    }
    internal static void SingleKey(ushort key)
    {
        Input[] sequence = { new() { Type = 1, Data = new Data { Keyboard = new KeyboardInput { Key = key } } }, new() { Type = 1, Data = new Data { Keyboard = new KeyboardInput { Key = key, Flags = 2 } } } };
        if (SendInput(2, sequence, Marshal.SizeOf<Input>()) != 2) throw new Exception("Windows did not accept the test key.");
    }
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr h, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point p);
    [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(Point p);
    [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr h, uint flag);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr h, int command);
    [DllImport("kernel32.dll")] internal static extern uint SetThreadExecutionState(uint flags);
}
