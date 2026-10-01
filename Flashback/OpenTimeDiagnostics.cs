using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Flashback;

// Phase marks while the editor opens, kept only for --open-time-test (a no-op otherwise).
internal static class OpenTiming
{
    internal static List<(double At, string What)>? Marks;
    private static Stopwatch? clock;
    internal static void Begin() { Marks = new(); clock = Stopwatch.StartNew(); }
    internal static void Mark(string what) { if (Marks != null) lock (Marks) Marks.Add((clock!.Elapsed.TotalMilliseconds, what)); }
}

// --open-time-test: how long the editor takes to open a clip, phase by phase, and how long the window is frozen
// meanwhile. The clip is FLASHBACK_OPEN_CLIP, or a generated 1080p60 one. Writes open-time-report.txt.
internal static class OpenTimeDiagnostics
{
    internal static async Task RunAsync()
    {
        Directory.CreateDirectory(Storage.Root);
        var report = new StringBuilder();
        string? clip = Environment.GetEnvironmentVariable("FLASHBACK_OPEN_CLIP");
        if (string.IsNullOrEmpty(clip) || !File.Exists(clip))
        {
            clip = Path.Combine(Storage.Root, "open-time-clip.mp4");
            await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=60:duration=30", "-f", "lavfi", "-i", "sine=frequency=300:sample_rate=48000:duration=30",
                "-c:v", "libx264", "-preset", "ultrafast", "-threads", "4", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", clip);
        }
        report.AppendLine($"Clip: {clip} ({new FileInfo(clip).Length / 1048576.0:0.0} MB)");
        foreach (int round in new[] { 1, 2 })
        {
            // The UI thread's own heartbeat: every gap well over its 10 ms tick is a freeze.
            var worst = 0.0; var freezes = new List<(double At, double Gap)>(); var beat = Stopwatch.StartNew(); double lastBeat = 0;
            var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { double now = beat.Elapsed.TotalMilliseconds, gap = now - lastBeat; lastBeat = now; if (gap > 60) freezes.Add((now, gap)); worst = Math.Max(worst, gap); };
            OpenTiming.Begin(); timer.Start(); beat.Restart(); lastBeat = 0;
            OpenTiming.Mark("start");
            var trim = new TrimWindow(clip) { Width = 1400, Height = 900 };
            OpenTiming.Mark("window made");
            bool rendered = false; trim.ContentRendered += (_, _) => { rendered = true; OpenTiming.Mark("content rendered"); };
            trim.Show(); OpenTiming.Mark("Show() returned");
            var until = Stopwatch.StartNew();
            while (!rendered && until.Elapsed.TotalSeconds < 20) await Task.Delay(5);
            while (!trim.Player.NaturalDuration.HasTimeSpan && until.Elapsed.TotalSeconds < 20) await Task.Delay(5);
            OpenTiming.Mark($"clip opened in the player ({(trim.Player.UsesFfmpeg ? "FFmpeg" : "Windows")})");
            await Task.Delay(9000); // through the waveform load that follows
            timer.Stop();
            report.AppendLine($"--- open {round}: UI frozen at most {worst:0} ms; stalls over 60 ms: {string.Join(", ", freezes.Select(f => $"{f.Gap:0} ms at {f.At:0}"))}");
            double prev = 0;
            foreach (var (at, what) in OpenTiming.Marks!) { report.AppendLine($"{at,8:0} ms  (+{at - prev,6:0})  {what}"); prev = at; }
            OpenTiming.Marks = null;
            trim.Close();
            await Task.Delay(1500);
        }
        File.WriteAllText(Path.Combine(Storage.Root, "open-time-report.txt"), report.ToString());
    }
}
