using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;

namespace Flashback;

// --app-layers-test: records a real clip with "Record each app's sound on its own layer" on while another
// app (PowerShell) plays a quiet tone, then checks the clip has that app's layer with its sound in it, and
// that the editor shows the layer with its waveform. Writes app-layers-results.txt and a screenshot.
internal static class AppLayersDiagnostics
{
    internal static async Task RunAsync()
    {
        Directory.CreateDirectory(Storage.Root);
        string results = Path.Combine(Storage.Root, "app-layers-results.txt"); File.Delete(results);
        void Check(bool ok, string message)
        {
            File.AppendAllText(results, (ok ? "PASS " : "FAIL ") + message + Environment.NewLine);
            if (!ok) throw new Exception(message);
        }
        Check(AppMixSource.Supported, "Windows supports listening to each app on its own");
        // Quiet (about -30 dB), so it barely reaches the speakers.
        var tone = Path.Combine(Storage.Root, "tone.wav");
        await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "aevalsrc=0.03*sin(2*PI*440*t)*(0.6+0.4*sin(2*PI*0.7*t)):s=48000:d=9", "-c:a", "pcm_s16le", tone);
        var settings = new Settings { SeparateAppAudio = true, DesktopAudio = true, MicrophoneAudio = false, ReplaySeconds = 30, Height = 720, FrameRate = 30, OutputFolder = Path.Combine(Storage.Root, "clips") };
        Storage.Save(settings);
        ClipResult clip;
        Process? player = null;
        await using (var recorder = new Recorder())
        {
            await recorder.StartAsync(settings, synthetic: false);
            Check(recorder.IsRecording, "Recording starts with app layers on");
            await Task.Delay(1500);
            player = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -Command \"(New-Object Media.SoundPlayer '{tone}').PlaySync()\"") { UseShellExecute = false, CreateNoWindow = true });
            await Task.Delay(8000);
            clip = await recorder.SaveAsync("App layers", DateTimeOffset.Now);
            await recorder.StopAsync();
        }
        try { if (player is { HasExited: false }) player.Kill(); } catch { }
        Check(File.Exists(clip.Path), $"A clip was saved ({clip.Duration:0.0} s)");

        var titles = await AudioWaveforms.TitlesAsync(clip.Path, default);
        Check(titles.Count >= 3 && titles[1] == "Other apps", $"The clip has the whole mix, Other apps and app layers ({titles.Count} tracks: {string.Join(" | ", titles.Take(4))}…)");
        int layer = Enumerable.Range(3, Math.Max(0, titles.Count - 3)).FirstOrDefault(t => LayerLog.Parse(titles[t])?.Any(a => a.App.Contains("powershell", StringComparison.OrdinalIgnoreCase)) == true);
        Check(layer >= 3, "PowerShell, which played the tone, got a layer of its own: " + (layer >= 3 ? titles[layer] : string.Join(" | ", titles.Skip(3))));
        double peak = await PeakAsync(clip.Path, layer);
        Check(peak > -45, $"Its layer has the tone in it (peak {peak:0.0} dB)");

        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var trim = new TrimWindow(clip.Path) { Width = 1400, Height = 900 };
        try
        {
            trim.Show();
            await Task.Delay(1500);
            if (!trim.Timeline.LanesExpanded) typeof(TrimWindow).GetMethod("LanesToggleRequested", flags)!.Invoke(trim, null);
            var wait = Stopwatch.StartNew();
            AudioLane? app = null;
            while (wait.Elapsed.TotalSeconds < 20 && (app = trim.Timeline.Lanes.FirstOrDefault(l => l.IsAppLayer && l.Name.Contains("powershell", StringComparison.OrdinalIgnoreCase))) is not { Peaks.Length: > 0 }) await Task.Delay(200);
            Check(app != null, "The editor shows PowerShell's layer: " + string.Join(", ", trim.Timeline.Lanes.Select(l => l.Name)));
            Check(app!.Peaks.Length > 0 && app.Peaks.Max() > .05, $"Its waveform is loaded (loudest {app.Peaks.Max():0.00})");
            await Task.Delay(500);
            var shot = LimitsDiagnostics.Snapshot(trim.Timeline);
            LimitsDiagnostics.Save(shot, "app-layers-timeline.png");
        }
        finally { trim.Close(); }
    }
    private static async Task<double> PeakAsync(string file, int track)
    {
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-hide_banner", "-nostdin", "-i", file, "-map", $"0:a:{track}", "-af", "volumedetect", "-f", "null", "-" }) info.ArgumentList.Add(arg);
        using var p = Process.Start(info)!;
        string text = await p.StandardError.ReadToEndAsync(); await p.WaitForExitAsync();
        var m = Regex.Match(text, @"max_volume:\s*(-?[\d.]+) dB");
        return m.Success ? double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : double.NegativeInfinity;
    }
}
