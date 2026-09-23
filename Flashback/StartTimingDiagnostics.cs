using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Flashback;

// --start-timing-test [settings.json]: starts real recording twice (cold, then warm) with the given
// settings and reports how long each startup stage takes and how long the UI thread was blocked.
internal static class StartTimingDiagnostics
{
    internal static async Task RunAsync(string? settingsPath)
    {
        Directory.CreateDirectory(Storage.Root);
        if (settingsPath != null && File.Exists(settingsPath)) File.Copy(settingsPath, Path.Combine(Storage.Root, "settings.json"), true);
        var settings = Storage.Load(out _);
        settings.OutputFolder = Path.Combine(Storage.Root, "clips");
        Directory.CreateDirectory(settings.OutputFolder);
        var report = new List<string>();
        var recorder = new Recorder();
        foreach (var run in new[] { "cold start", "warm start" })
        {
            // A 15 ms UI timer: the longest gap between ticks is how long the window froze.
            double worst = 0; long last = Stopwatch.GetTimestamp();
            var ui = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Normal, (_, _) =>
            {
                var now = Stopwatch.GetTimestamp(); worst = Math.Max(worst, Stopwatch.GetElapsedTime(last, now).TotalMilliseconds); last = now;
            }, Dispatcher.CurrentDispatcher);
            var total = Stopwatch.StartNew();
            await recorder.StartAsync(settings);
            total.Stop(); ui.Stop();
            report.Add($"== {run}: {total.Elapsed.TotalMilliseconds:0} ms until Recording; UI thread blocked up to {worst:0} ms");
            report.Add(recorder.StartTimings.TrimEnd());
            await Task.Delay(1500);
            await recorder.StopAsync();
            await Task.Delay(1000);
        }
        File.WriteAllLines(Path.Combine(Storage.Root, "start-timing-results.txt"), report);
    }
}
