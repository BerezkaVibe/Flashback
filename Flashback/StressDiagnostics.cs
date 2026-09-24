using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Flashback;

// A heavy edit on a 1080p60 clip, to find slow spots in the newer tools: many drawings and lines,
// big blur and pixelate areas, two inset videos, keyframed items, many freeze frames and reordered
// sections. Times the busiest paths, plays it live, exports it and joins clips. Writes stress-report.txt.
internal static class StressDiagnostics
{
    internal static async Task RunAsync()
    {
        Directory.CreateDirectory(Storage.Root);
        var report = new StringBuilder();
        void Line(string text) { report.AppendLine(text); File.WriteAllText(Path.Combine(Storage.Root, "stress-report.txt"), report.ToString()); }
        string folder = Path.Combine(Storage.Root, "stress"); Directory.CreateDirectory(folder);
        string clip = Path.Combine(folder, "Heavy 1080p60.mp4"), inset = Path.Combine(folder, "Inset.mp4"), small = Path.Combine(folder, "Small 720p30.mp4");
        await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=60:duration=20", "-f", "lavfi", "-i", "sine=frequency=300:sample_rate=48000:duration=20",
            "-c:v", "libx264", "-preset", "ultrafast", "-threads", "4", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", clip);
        await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "mandelbrot=size=640x360:rate=30", "-t", "20", "-f", "lavfi", "-i", "sine=frequency=900:sample_rate=48000:duration=20",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", inset);
        await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "testsrc=size=1280x720:rate=30:duration=10", "-f", "lavfi", "-i", "sine=frequency=600:sample_rate=48000:duration=10",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", small);
        Storage.Save(new Settings { OutputFolder = Path.Combine(Storage.Root, "clips") });
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var rng = new Random(7);

        // ---- The heavy scene ----
        var items = new List<OverlayItem>();
        OverlayItem Drawn(OverlayShape shape, int points, bool closed, double x, double y) => OverlayItem.NewShape(0, 20, null) with
        {
            Shape = shape, Closed = closed, X = x, Y = y, ShapeWidth = 360, ShapeHeight = 240, LineWidth = 8, Dash = (OverlayDash)(points % 3),
            StartCap = closed ? OverlayCap.None : OverlayCap.Dot, EndCap = closed ? OverlayCap.None : OverlayCap.Arrow, ShapeColor = closed ? "#00FF3B30" : "#FFFFE600", ShapeBorderWidth = closed ? 6 : 0, ShapeBorderColor = "#FF00E5FF",
            Points = Enumerable.Range(0, points).Select(i => (Math.Round(.5 * Math.Cos(i * 2 * Math.PI / points) * (0.8 + .2 * Math.Sin(i * .7)), 4), Math.Round(.5 * Math.Sin(i * 2 * Math.PI / points), 4))).ToArray(),
        };
        for (int i = 0; i < 6; i++) items.Add(Drawn(OverlayShape.Freehand, 300, i % 2 == 0, .15 + i * .14, .25));
        for (int i = 0; i < 6; i++) items.Add(Drawn(OverlayShape.Polyline, 12, false, .15 + i * .14, .75));
        items.Add(OverlayItem.NewShape(0, 20, null) with { Region = OverlayRegion.Blur, RegionStrength = 30, ShapeWidth = 700, ShapeHeight = 500, X = .3, Y = .5 });
        items.Add(OverlayItem.NewShape(0, 20, null) with { Region = OverlayRegion.Pixelate, RegionStrength = 18, ShapeWidth = 900, ShapeHeight = 700, X = .7, Y = .5, Shape = OverlayShape.Ellipse });
        items.Add(OverlayItem.NewShape(0, 20, null) with { Region = OverlayRegion.Pixelate, RegionStrength = 10, ShapeWidth = 500, ShapeHeight = 400, X = .5, Y = .2 });
        items.Add(OverlayItem.NewVideo(0, 20, inset, 640, 360) with { X = .8, Y = .8, Scale = .5, Mask = OverlayShape.Hexagon, BorderWidth = 4 });
        items.Add(OverlayItem.NewVideo(0, 20, inset, 640, 360) with { X = .2, Y = .8, Scale = .4, VideoOffset = 3 });
        for (int i = 0; i < 4; i++)
            items.Add(OverlayItem.NewText(0, 20, null) with { Text = "KEYFRAMED " + i, Y = .1 + i * .08, Keys = new[] { new OverlayKeyframe(0, .1, .1 + i * .08, 1, 0, 1), new OverlayKeyframe(10, .9, .2 + i * .08, 1.4, 20, .7), new OverlayKeyframe(20, .2, .1 + i * .08, 1, 0, 1) } });
        items = items.Select((o, i) => o with { Layer = i }).ToList();
        var freezes = Enumerable.Range(0, 12).Select(i => new FreezeFrame(1 + i * 1.5, 1)).ToArray();
        var sections = new[] { new KeepSection(10, 14), new KeepSection(0, 3), new KeepSection(15, 19), new KeepSection(4, 8) };

        // ---- Editor paths, timed on the UI thread ----
        var trim = new TrimWindow(clip, true) { Width = 1600, Height = 1000 };
        try
        {
            var content = (FrameworkElement)trim.Content;
            content.Measure(new Size(1600, 1000)); content.Arrange(new Rect(0, 0, 1600, 1000)); content.UpdateLayout();
            void Settle() => content.UpdateLayout();
            double Time(int count, Action<int> step) { Settle(); var t = Stopwatch.StartNew(); for (int i = 0; i < count; i++) { step(i); Settle(); } return t.Elapsed.TotalMilliseconds / count; }
            var setPlayhead = typeof(TrimWindow).GetMethod("SetPlayhead", flags)!;
            var setOverlays = typeof(TrimWindow).GetMethod("SetOverlays", flags)!;
            double baseTick = Time(300, i => setPlayhead.Invoke(trim, new object[] { i / 60.0 }));
            setOverlays.Invoke(trim, new object[] { items.ToArray() });
            trim.Timeline.Freezes = freezes;
            foreach (var s in sections) { trim.StartBox.Text = s.Start.ToString(System.Globalization.CultureInfo.InvariantCulture); trim.EndBox.Text = s.End.ToString(System.Globalization.CultureInfo.InvariantCulture); typeof(TrimWindow).GetMethod("Add_Click", flags)!.Invoke(trim, new object[] { trim, new RoutedEventArgs() }); trim.SectionsList.SelectedIndex = -1; }
            trim.MoveSection(2, 0);
            double heavyTick = Time(300, i => setPlayhead.Invoke(trim, new object[] { 2 + i / 60.0 }));
            Line($"Playback tick, empty edit: {baseTick:0.00} ms · heavy edit ({items.Count} items, {freezes.Length} freezes): {heavyTick:0.00} ms");
            // Rendering the preview and the timeline (software, a stand-in for the per-frame drawing cost).
            double RenderCost(FrameworkElement e, int count)
            {
                var bmp = new RenderTargetBitmap(Math.Max(1, (int)e.ActualWidth), Math.Max(1, (int)e.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                var t = Stopwatch.StartNew(); for (int i = 0; i < count; i++) { bmp.Clear(); bmp.Render(e); } return t.Elapsed.TotalMilliseconds / count;
            }
            Line($"Preview render: {RenderCost(trim.OverlayView, 20):0.0} ms · timeline render: {RenderCost(trim.Timeline, 20):0.0} ms");
            // Dragging a corner of a line: each move replaces the item and refreshes the panel.
            var open = typeof(TrimWindow).GetMethod("OpenOverlay", flags)!; var replace = typeof(TrimWindow).GetMethod("ReplaceOverlay", flags)!; var loadUi = typeof(TrimWindow).GetMethod("LoadOverlayUi", flags)!;
            open.Invoke(trim, new object[] { 6 });
            double corner = Time(200, i => { var o = trim.Timeline.Overlays[6]; var pts = o.Points.ToArray(); pts[3] = (pts[3].X + (i % 2 == 0 ? .01 : -.01), pts[3].Y); replace.Invoke(trim, new object[] { 6, o with { Points = pts } }); loadUi.Invoke(trim, null); });
            Line($"Drag a line corner: {corner:0.00} ms per move");
            open.Invoke(trim, new object[] { 0 });
            double stretch = Time(200, i => { var o = trim.Timeline.Overlays[0]; replace.Invoke(trim, new object[] { 0, o with { ShapeWidth = 300 + i % 50 } }); loadUi.Invoke(trim, null); });
            Line($"Stretch a freehand drawing: {stretch:0.00} ms per move");
            // A very long freehand stroke becoming a shape.
            var drawn = typeof(TrimWindow).GetMethod("ShapeDrawn", flags)!;
            var stroke = Enumerable.Range(0, 3000).Select(i => new Point(960 + 400 * Math.Cos(i * .01) + rng.NextDouble() * 3, 540 + 300 * Math.Sin(i * .013))).ToArray();
            var draw = Stopwatch.StartNew(); drawn.Invoke(trim, new object[] { OverlayLayer.DrawTool.Freehand, stroke, false }); Settle();
            Line($"Turn a 3000-point stroke into a drawing: {draw.Elapsed.TotalMilliseconds:0.0} ms, kept {trim.Timeline.Overlays[0].Points.Count} points");
            // Freezes: moving one along the timeline.
            double freezeDrag = Time(200, i => { trim.Timeline.Freezes = trim.Timeline.Freezes.Select((f, n) => n == 3 ? f with { At = 5.5 + i % 20 / 100.0 } : f).ToArray(); });
            Line($"Move a freeze frame: {freezeDrag:0.00} ms per move");
            double sectionMove = Time(100, i => trim.MoveSection(i % 2, 3));
            Line($"Reorder a section: {sectionMove:0.00} ms");
            Line($"Memory after editing: {GC.GetTotalMemory(true) / 1048576.0:0} MiB managed, working set {Process.GetCurrentProcess().WorkingSet64 / 1048576.0:0} MiB");
        }
        finally { trim.Close(); }

        // ---- Live playback of the heavy edit ----
        var live = new TrimWindow(clip) { WindowState = WindowState.Maximized };
        try
        {
            live.Show();
            var until = Stopwatch.StartNew();
            while (!live.Player.NaturalDuration.HasTimeSpan && until.Elapsed.TotalSeconds < 15) await Task.Delay(100);
            typeof(TrimWindow).GetMethod("SetOverlays", flags)!.Invoke(live, new object[] { items.ToArray() });
            live.Timeline.Freezes = freezes;
            async Task<(double Cpu, double WorstGap, double Frames)> Play(double from, double seconds)
            {
                live.SeekTo(from); await Task.Delay(500);
                var process = Process.GetCurrentProcess(); process.Refresh(); var cpu0 = process.TotalProcessorTime;
                double worst = 0; long last = Stopwatch.GetTimestamp(); int ticks = 0;
                var probe = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
                probe.Tick += (_, _) => { double gap = Stopwatch.GetElapsedTime(last).TotalMilliseconds; last = Stopwatch.GetTimestamp(); worst = Math.Max(worst, gap); ticks++; };
                var wall = Stopwatch.StartNew();
                live.HandleKey(System.Windows.Input.Key.Space, System.Windows.Input.ModifierKeys.None, true); probe.Start();
                await Task.Delay(TimeSpan.FromSeconds(seconds));
                probe.Stop(); live.HandleKey(System.Windows.Input.Key.Space, System.Windows.Input.ModifierKeys.None, true);
                process.Refresh();
                return ((process.TotalProcessorTime - cpu0).TotalMilliseconds / wall.Elapsed.TotalMilliseconds * 100 / Environment.ProcessorCount, worst, ticks / wall.Elapsed.TotalSeconds);
            }
            var heavy = await Play(4.2, 6);
            Line($"Live playback, heavy edit: {heavy.Cpu:0.0}% of the CPU, worst UI stall {heavy.WorstGap:0} ms, UI ticks {heavy.Frames:0}/s, playhead reached {live.Playhead:0.00} s");
            typeof(TrimWindow).GetMethod("SetOverlays", flags)!.Invoke(live, new object[] { Array.Empty<OverlayItem>() });
            live.Timeline.Freezes = Array.Empty<FreezeFrame>();
            var plain = await Play(4.2, 6);
            Line($"Live playback, empty edit: {plain.Cpu:0.0}% of the CPU, worst UI stall {plain.WorstGap:0} ms, UI ticks {plain.Frames:0}/s");
        }
        finally { live.Close(); }

        // ---- Export the heavy edit ----
        var options = new ShareExportOptions { Overlays = items, Freezes = freezes };
        double expected = options.OutputSeconds(sections);
        var output = Path.Combine(folder, "Heavy export.mp4");
        var exportTime = Stopwatch.StartNew();
        try
        {
            await ExportServices.PreciseAsync(clip, output, sections, null, CancellationToken.None, false, options);
            var result = ClipMedia.Read(output);
            Line($"Export heavy edit: {exportTime.Elapsed.TotalSeconds:0.0} s for {expected:0.0} s of video; came out {result.Duration:0.00} s (expected {expected:0.00}), audio {result.HasAudio}");
        }
        catch (Exception ex) { Line($"Export heavy edit FAILED after {exportTime.Elapsed.TotalSeconds:0.0} s: {ex.Message}"); }

        // ---- Joining clips ----
        var same = Path.Combine(folder, "Take two.mp4"); File.Copy(clip, same, true);
        var joinTime = Stopwatch.StartNew();
        await ClipJoin.JoinAsync(clip, same, Path.Combine(folder, "Joined same.mp4"), null, CancellationToken.None);
        Line($"Join two matching 20 s 1080p60 clips: {joinTime.Elapsed.TotalSeconds:0.00} s");
        joinTime.Restart();
        await ClipJoin.JoinAsync(clip, small, Path.Combine(folder, "Joined different.mp4"), null, CancellationToken.None);
        Line($"Join a 720p30 clip onto a 1080p60 one (converted): {joinTime.Elapsed.TotalSeconds:0.0} s for 30 s of video");
    }
}
