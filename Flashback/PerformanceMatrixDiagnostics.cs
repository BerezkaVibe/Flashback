using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Flashback;

// Records 12 seconds with each setting that could go in a "lighter" mode and reports CPU and memory
// (app plus recorder) against the default. Writes performance-matrix.txt.
internal static class PerformanceMatrixDiagnostics
{
    internal static async Task RunAsync(int only = -1)
    {
        Directory.CreateDirectory(Storage.Root);
        var baseline = Storage.Load(out _); baseline.OutputFolder = Path.Combine(Storage.Root, "clips");
        baseline.FrameRate = 60; baseline.Height = 1080; baseline.ReplaySeconds = 30; baseline.DesktopAudio = true; baseline.MicrophoneAudio = false; baseline.AppVolumes = new(); baseline.ShowCursor = false; baseline.MicNoiseReduction = "Off";
        var configs = new List<(string Name, Settings Settings)>
        {
            ("Default: 1080p 60 fps, desktop audio", baseline.Copy()),
            ("1080p 30 fps", Change(baseline, s => s.FrameRate = 30)),
            ("720p 30 fps", Change(baseline, s => { s.Height = 720; s.FrameRate = 30; })),
            ("Per-app audio mixer on", Change(baseline, s => s.AppVolumes = new() { ["explorer"] = 50 })),
            ("Microphone with strong noise suppression", Change(baseline, s => { s.MicrophoneAudio = true; s.MicNoiseReduction = "Strong"; })),
            ("Cursor shown", Change(baseline, s => s.ShowCursor = true)),
        };
        var report = new StringBuilder();
        for (int n = 0; n < configs.Count; n++)
        {
            if (only >= 0 && n != only) continue;
            var (name, settings) = configs[n];
            try
            {
                await using var recorder = new Recorder();
                await recorder.StartAsync(settings); await Task.Delay(2000);
                var before = recorder.Performance; var timer = Stopwatch.StartNew(); long peak = before.Memory;
                for (int i = 0; i < 24; i++) { await Task.Delay(500); peak = Math.Max(peak, recorder.Performance.Memory); }
                var after = recorder.Performance;
                report.AppendLine($"{name,-44} CPU {(after.Cpu - before.Cpu) / timer.Elapsed.TotalSeconds * 100,5:0} % of one core · memory {peak / 1048576.0,5:0} MiB");
                await recorder.StopAsync();
            }
            catch (Exception ex) { report.AppendLine($"{name,-44} failed: {ex.Message.Split('\n')[0]}"); }
            File.AppendAllText(Path.Combine(Storage.Root, "performance-matrix.txt"), report.ToString()); report.Clear();
        }
    }
    private static Settings Change(Settings s, Action<Settings> change) { var copy = s.Copy(); change(copy); return copy; }
}
