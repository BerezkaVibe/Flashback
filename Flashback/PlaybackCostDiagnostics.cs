using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Flashback;

// Plays a clip in the trimmer's preview for 20 seconds and reports the app's CPU use, for comparing
// preview players. Writes playback-cost.txt.
internal static class PlaybackCostDiagnostics
{
    internal static async Task RunAsync(string clip)
    {
        Directory.CreateDirectory(Storage.Root);
        var trim = new TrimWindow(clip) { Width = 1280, Height = 900 };
        trim.Show();
        try
        {
            var until = Stopwatch.StartNew();
            while (!trim.Player.NaturalDuration.HasTimeSpan && until.Elapsed.TotalSeconds < 15) await Task.Delay(50);
            await Task.Delay(1000);
            trim.SeekTo(1); await Task.Delay(500);
            var process = Process.GetCurrentProcess(); var cpu = process.TotalProcessorTime; var clock = Stopwatch.StartNew();
            trim.HandleKey(System.Windows.Input.Key.Space, System.Windows.Input.ModifierKeys.None, true);
            await Task.Delay(20000);
            process.Refresh();
            double used = (process.TotalProcessorTime - cpu).TotalSeconds, seconds = clock.Elapsed.TotalSeconds;
            File.WriteAllText(Path.Combine(Storage.Root, "playback-cost.txt"), $"Windows preview player, 1080p60 at {trim.Player.ActualWidth:0}x{trim.Player.ActualHeight:0}: app CPU {used:0.0} s over {seconds:0} s = {used / seconds * 100:0} % of one core; playhead reached {trim.Playhead:0.0} s\n");
        }
        finally { trim.Close(); }
    }
}
