using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Flashback;

internal static class StartupDiagnostics
{
    internal static async Task RunAsync()
    {
        Directory.CreateDirectory(Storage.Root);
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception(message);
            File.AppendAllText(Path.Combine(Storage.Root, "test-progress.txt"), "PASS " + message + Environment.NewLine);
        }
        const string deviceError = "[h264_nvenc] OpenEncodeSessionEx failed: no encode device (1): (no details)";
        await RecoveryDiagnostics.RunAsync(Check);
        var attempts = new System.Collections.Generic.List<bool>();
        await Recorder.StartWithGpuRetryAsync(transfer =>
        {
            attempts.Add(transfer);
            return transfer ? Task.CompletedTask : Task.FromException(new InvalidOperationException(deviceError));
        }, () => true);
        Check(attempts.SequenceEqual(new[] { false, true }), "The reported NVENC device failure retries once using GPU transfer");
        foreach (bool retryAllowed in new[] { false, true })
        {
            attempts.Clear();
            try
            {
                await Recorder.StartWithGpuRetryAsync(transfer => { attempts.Add(transfer); return Task.FromException(new InvalidOperationException(deviceError)); }, () => retryAllowed);
                throw new Exception("Encoder failure was swallowed");
            }
            catch (InvalidOperationException)
            { Check(attempts.Count == (retryAllowed ? 2 : 1), retryAllowed ? "A failing fallback stops after two attempts" : "A path already using CUDA is not retried"); }
        }
        attempts.Clear();
        try
        {
            await Recorder.StartWithGpuRetryAsync(transfer => { attempts.Add(transfer); return Task.FromException(new InvalidOperationException("Desktop audio could not start")); }, () => true);
            throw new Exception("Audio failure was swallowed");
        }
        catch (InvalidOperationException) { Check(attempts.Count == 1, "Audio errors do not trigger GPU retries"); }
        var nativeArgs = Recorder.BuildArguments(new Settings { Height = 0 }, Storage.Root, null, false, transfer: true);
        Check(nativeArgs.Any(a => a.Contains("hwdownload,format=bgra,hwupload_cuda")) && !nativeArgs.Any(a => a.Contains("scale_cuda=")), "Native resolution can transfer to NVIDIA without resizing");
        Check(nativeArgs.Contains("h264_nvenc") && !nativeArgs.Contains("libx264"), "The GPU fallback retains hardware encoding");
        var topology = new[] {
            new CaptureDisplay(0, 0, @"\\.\DISPLAY6", "NVIDIA RTX 3050 Laptop", 0x10de, false),
            new CaptureDisplay(1, 0, @"\\.\DISPLAY1", "AMD Radeon Graphics", 0x1002, true),
            new CaptureDisplay(1, 1, @"\\.\DISPLAY2", "AMD Radeon Graphics", 0x1002, true),
            new CaptureDisplay(2, 0, @"\\.\DISPLAY3", "Intel Graphics", 0x8086, true)
        };
        var hybrid = CaptureDisplay.Resolve(@"\\.\DISPLAY1", topology);
        Check(hybrid.AdapterIndex == 1 && hybrid.OutputIndex == 0, "Hybrid laptop selects AMD's connected output even when NVIDIA is adapter zero");
        Check(CaptureDisplay.Resolve(@"\\.\DISPLAY3", topology).OutputIndex == 0, "Monitor selection uses adapter-local output indices");
        foreach (var missingName in new[] { @"\\.\DISPLAY6", @"\\.\DISPLAY99" })
        {
            try { CaptureDisplay.Resolve(missingName, topology); throw new Exception("Unavailable output was selected"); }
            catch (InvalidOperationException) { Check(true, "Detached or missing capture output is rejected: " + missingName); }
        }
        var hybridArgs = Recorder.BuildArguments(new Settings { Height = 0, Encoder = "NVIDIA NVENC" }, Storage.Root, null, false, display: hybrid);
        Check(hybridArgs.Contains("d3d11va=capture:1") && hybridArgs.Contains("-filter_hw_device") && hybridArgs.Contains("capture"), "Capture is explicitly bound to AMD's D3D11 device");
        Check(hybridArgs.Any(a => a.Contains("ddagrab=output_idx=0:") && a.Contains("hwupload_cuda")), "Hybrid capture transfers to NVIDIA on the first attempt");
        var directArgs = Recorder.BuildArguments(new Settings { Height = 0 }, Storage.Root, null, false, display: hybrid with { VendorId = 0x10de });
        Check(!directArgs.Any(a => a.Contains("hwdownload")), "Native NVIDIA capture avoids unnecessary host copies");
        Check(CaptureDisplay.Resolve(0).DeviceName == System.Windows.Forms.Screen.AllScreens[0].DeviceName, "Live DXGI enumeration matches the selected Windows display");
        var settings = new Settings { ReplaySeconds = 300, FrameRate = 30, DesktopAudio = false, OverlayEnabled = false, OutputFolder = Path.Combine(Storage.Root, "clips") };
        Storage.Save(settings);
        var window = new MainWindow(renderOnly: true, syntheticCapture: true);
        try
        {
            Check(!window.TopSaveButton.IsEnabled, "Top save button is disabled while paused");
            Check(window.HeaderVersion.Text.Contains(typeof(MainWindow).Assembly.GetName().Version!.ToString(3)), "Header derives the full release version");
            window.ToggleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var deadline = Stopwatch.StartNew();
            while (!window.TopSaveButton.IsEnabled && deadline.Elapsed.TotalSeconds < 15) await Task.Delay(100);
            Check(window.TopSaveButton.IsEnabled, "Start buffer enables top save before 5 minutes fill");
            Check(window.HeaderCapture.Text.Contains("640 × 360"), "Header shows actual recording dimensions");
            window.MainTabs.SelectedItem = window.LibraryTab;
            window.TopSaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            deadline.Restart();
            while ((!window.TopSaveButton.IsEnabled || !Directory.EnumerateFiles(settings.OutputFolder, "*.mp4", SearchOption.AllDirectories).Any()) && deadline.Elapsed.TotalSeconds < 15) await Task.Delay(100);
            var clip = Directory.EnumerateFiles(settings.OutputFolder, "*.mp4", SearchOption.AllDirectories).Single();
            var duration = ClipMedia.Read(clip).Duration;
            Check(duration >= 2 && duration < 15, $"Top save on Saved clips tab exports all available footage with a 300s setting ({duration:0.000}s)");
            Check(window.TopSaveButton.IsEnabled, "Save button is restored after export");
            var beforeGeneration = window.CaptureGenerationForTest;
            window.InjectCaptureLossForTest(audioPipeFirst: true, releaseFrame: true);
            deadline.Restart();
            while (!window.RecoveringForTest && deadline.Elapsed.TotalSeconds < 8) await Task.Delay(50);
            Check(window.RecoveringForTest && window.TopSaveButton.IsEnabled && window.ToggleButton.IsEnabled, "Background recovery keeps save and pause available without a reconnect counter");
            deadline.Restart();
            while ((window.CaptureGenerationForTest == beforeGeneration || !window.TopSaveButton.IsEnabled) && deadline.Elapsed.TotalSeconds < 15) await Task.Delay(100);
            Check(window.TopSaveButton.IsEnabled && window.CaptureGenerationForTest > beforeGeneration, "Audio pipe failure with the reported DDA ReleaseFrame error automatically reconnects without pressing Start");
            window.TopSaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            deadline.Restart();
            while ((!window.TopSaveButton.IsEnabled || Directory.GetFiles(settings.OutputFolder, "*.mp4", SearchOption.AllDirectories).Length < 2) && deadline.Elapsed.TotalSeconds < 15) await Task.Delay(100);
            var continued = Directory.GetFiles(settings.OutputFolder, "*.mp4", SearchOption.AllDirectories).Single(p => p != clip);
            Check(ClipMedia.Read(continued).Duration > 0, "Replay after reconnect saves available footage since the previous save");
            var decodeInfo = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-v", "error", "-i", continued, "-f", "null", "-" }) decodeInfo.ArgumentList.Add(arg);
            using (var decoder = Process.Start(decodeInfo)!)
            {
                var errors = decoder.StandardError.ReadToEndAsync();
                await decoder.WaitForExitAsync();
                Check(decoder.ExitCode == 0 && string.IsNullOrWhiteSpace(await errors), "Replay spanning capture sessions fully decodes");
            }
            window.InjectCaptureLossForTest();
            deadline.Restart();
            while (!window.RecoveringForTest && deadline.Elapsed.TotalSeconds < 8) await Task.Delay(50);
            window.ToggleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            deadline.Restart();
            while ((window.TopSaveButton.IsEnabled || window.RecoveringForTest) && deadline.Elapsed.TotalSeconds < 10) await Task.Delay(100);
            Check(!window.TopSaveButton.IsEnabled, "Pause disables both save controls");
            await Task.Delay(1500);
            Check(!window.TopSaveButton.IsEnabled && window.StatusTitle.Text.Equals("Paused", StringComparison.OrdinalIgnoreCase), "Pause during reconnection prevents automatic restart");
            await using var missing = new Recorder(Path.Combine(Storage.Root, "missing-ffmpeg.exe"));
            try { await missing.StartAsync(settings); throw new Exception("Missing recorder was accepted"); }
            catch (InvalidOperationException ex)
            {
                Check(ex.Message.Contains("preparing the replay buffer") && ex.InnerException is FileNotFoundException, "Startup errors preserve the original cause and identify the failed stage");
                Check(File.ReadAllText(Path.Combine(Storage.Root, "last-error.txt")).Contains("FileNotFoundException"), "Startup diagnostic log retains exception details");
            }
        }
        finally { await window.QuitAsync(); }
    }
}

