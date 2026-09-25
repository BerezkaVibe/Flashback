using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace Flashback;

// --background-test: the editor while it isn't in front. Plays a 1080p60 clip with a video over it and
// music, then checks and measures: in front (paused, playing), in the background (paused, and playing
// again on return), minimized (players closed, memory trimmed) and back (reopened at the same spot).
// Reports the app's CPU (share of one core) and memory for each in background-report.txt, so builds can
// be compared (for example before and after a different preview player).
internal static class BackgroundDiagnostics
{
    internal static async Task RunAsync()
    {
        Directory.CreateDirectory(Storage.Root);
        var checks = new List<string>(); var report = new StringBuilder();
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAILED: " + name);
            checks.Add(name); File.WriteAllText(Path.Combine(Storage.Root, "background-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        }
        void Line(string text) { report.AppendLine(text); File.WriteAllText(Path.Combine(Storage.Root, "background-report.txt"), report.ToString()); }
        string folder = Path.Combine(Storage.Root, "background"); Directory.CreateDirectory(folder);
        string clip = Path.Combine(folder, "Clip 1080p60.mp4"), inset = Path.Combine(folder, "Inset.mp4"), music = Path.Combine(folder, "Music.m4a");
        await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=60:duration=40", "-f", "lavfi", "-i", "sine=frequency=300:sample_rate=48000:duration=40",
            "-c:v", "libx264", "-preset", "ultrafast", "-threads", "4", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", clip);
        await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "mandelbrot=size=640x360:rate=30", "-t", "40", "-f", "lavfi", "-i", "sine=frequency=900:sample_rate=48000:duration=40",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", inset);
        await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "sine=frequency=500:sample_rate=48000:duration=40", "-c:a", "aac", music);
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        bool wasLight = TrimWindow.LightInBackground;
        TrimWindow.LightInBackground = true;
        var trim = new TrimWindow(clip) { Width = 1400, Height = 900 };
        try
        {
            trim.Show();
            async Task Opened() { var until = Stopwatch.StartNew(); while (!trim.Player.NaturalDuration.HasTimeSpan && until.Elapsed.TotalSeconds < 15) await Task.Delay(50); }
            await Opened();
            Check(trim.Player.NaturalDuration.HasTimeSpan, "The editor opens the clip");
            typeof(TrimWindow).GetMethod("AddOverlay", flags)!.Invoke(trim, new object[] { OverlayItem.NewVideo(0, 40, inset, 640, 360) with { X = .8, Y = .25, Scale = .5 } });
            typeof(TrimWindow).GetMethod("CloseOverlay", flags)!.Invoke(trim, null);
            typeof(TrimWindow).GetMethod("SetSounds", flags)!.Invoke(trim, new object[] { new[] { new SoundItem(music, 0, 40) } });
            await Task.Delay(1000);
            var process = Process.GetCurrentProcess();
            // CPU over a stretch, as a share of one core, and memory at its end.
            async Task Sample(string state, double seconds = 5)
            {
                process.Refresh(); var cpu = process.TotalProcessorTime; var clock = Stopwatch.StartNew();
                await Task.Delay(TimeSpan.FromSeconds(seconds));
                process.Refresh();
                double share = (process.TotalProcessorTime - cpu).TotalSeconds / clock.Elapsed.TotalSeconds * 100;
                Line($"{state}: CPU {share:0.0}% of one core · working set {process.WorkingSet64 / 1048576.0:0} MiB · private {process.PrivateMemorySize64 / 1048576.0:0} MiB · managed {GC.GetTotalMemory(false) / 1048576.0:0} MiB");
            }
            Line($"Flashback {typeof(TrimWindow).Assembly.GetName().Version} · {Environment.ProcessorCount} logical processors · 1080p60 clip with a video over it and music");
            trim.SeekTo(2); await Task.Delay(500);
            await Sample("In front, paused");
            trim.HandleKey(Key.Space, ModifierKeys.None, true); await Task.Delay(500);
            Check(trim.IsPlaying, "The preview plays");
            await Sample("In front, playing");
            // Tabbing into another app mid-playback pauses it; coming back plays on from there.
            trim.WentToBackground();
            double pausedAt = trim.Playhead;
            Check(!trim.IsPlaying && trim.PausedInBackground, "Going to the background mid-playback pauses the preview");
            await Sample("In the background, paused");
            Check(Math.Abs(trim.Playhead - pausedAt) < .05, "The playhead stays put in the background");
            trim.CameToFront(); await Task.Delay(700);
            Check(trim.IsPlaying && !trim.PausedInBackground && trim.Playhead >= pausedAt - .05, "Coming back plays on from the same spot");
            trim.HandleKey(Key.Space, ModifierKeys.None, true); await Task.Delay(300);
            // Minimized: the players are closed and memory trimmed (after its 3 s wait); back, they reopen where they were.
            process.Refresh(); long before = process.WorkingSet64;
            double at = trim.Playhead;
            trim.WindowState = WindowState.Minimized; await Task.Delay(4500);
            Check(trim.PlayersReleased && trim.Player.Source == null, "Minimizing closes the video players");
            await Sample("Minimized (players closed, memory trimmed)");
            process.Refresh(); long minimized = process.WorkingSet64;
            var reopen = Stopwatch.StartNew();
            trim.WindowState = WindowState.Normal; trim.Activate(); trim.CameToFront();
            await Opened(); double reopenMs = reopen.Elapsed.TotalMilliseconds; await Task.Delay(500);
            Check(!trim.PlayersReleased && trim.Player.NaturalDuration.HasTimeSpan && Math.Abs(trim.Playhead - at) < .05 && Math.Abs(trim.Player.Position.TotalSeconds - at) < .2,
                $"Coming back reopens the preview at the same spot ({trim.Player.Position.TotalSeconds:0.00} s, playhead {trim.Playhead:0.00} s, was {at:0.00} s)");
            Line($"Minimizing freed {(before - minimized) / 1048576.0:0} MiB of working set; reopening the preview took {reopenMs:0} ms");
            await Sample("In front again, paused");
            // A full-screen game in front works the same way, from the 2 s check while in the background.
            trim.ReleasePlayers();
            Check(trim.PlayersReleased, "Players can be put away while a game is in front");
            trim.CameToFront(); await Opened();
            Check(!trim.PlayersReleased && trim.Player.NaturalDuration.HasTimeSpan, "and come back with the editor");
        }
        finally { trim.Close(); TrimWindow.LightInBackground = wasLight; }
    }
}
