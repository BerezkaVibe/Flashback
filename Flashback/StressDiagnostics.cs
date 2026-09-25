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
    // Dragging the playhead: how long each mouse move takes to handle, and how long until the moved
    // playhead is on screen, on the two-minute 1440p60 clip with and without a heavy edit.
    internal static async Task ScrubLatencyAsync(bool heavyOnly = false)
    {
        Directory.CreateDirectory(Storage.Root);
        var report = new StringBuilder();
        void Line(string text) { report.AppendLine(text); File.WriteAllText(Path.Combine(Storage.Root, "scrub-latency.txt"), report.ToString()); }
        string folder = Path.Combine(Storage.Root, "stress-big"); Directory.CreateDirectory(folder);
        string clip = Path.Combine(folder, "Two minutes 1440p60.mp4");
        if (!File.Exists(clip))
            await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "testsrc2=size=2560x1440:rate=60:duration=120", "-f", "lavfi", "-i", "sine=frequency=300:sample_rate=48000:duration=120",
                "-c:v", "libx264", "-preset", "ultrafast", "-threads", "8", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", clip);
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var live = new TrimWindow(clip) { WindowState = WindowState.Maximized };
        try
        {
            live.Show();
            var until = Stopwatch.StartNew();
            while (!live.Player.NaturalDuration.HasTimeSpan && until.Elapsed.TotalSeconds < 15) await Task.Delay(100);
            await Task.Delay(800);
            var tl = live.Timeline;
            var dragField = typeof(TrimTimeline).GetField("drag", flags)!;
            var playheadDrag = Enum.Parse(dragField.FieldType, "Playhead");
            var moveTo = typeof(TrimTimeline).GetMethod("MoveTo", flags | BindingFlags.Public)!;
            async Task<string> Drag(string label)
            {
                // A mouse sends a move about every 8 ms; each is handled at input priority, as real ones are.
                var handle = new List<double>(); var onScreen = new List<double>();
                long pending = 0;
                void Rendered(object? s, EventArgs e) { if (pending != 0) { onScreen.Add(Stopwatch.GetElapsedTime(pending).TotalMilliseconds); pending = 0; } }
                System.Windows.Media.CompositionTarget.Rendering += Rendered;
                dragField.SetValue(tl, playheadDrag);
                double width = tl.ActualWidth; int moves = 0;
                var run = Stopwatch.StartNew();
                while (run.Elapsed.TotalSeconds < 4)
                {
                    double x = 30 + (width - 60) * (0.5 + 0.45 * Math.Sin(run.Elapsed.TotalSeconds * 1.7));
                    long queued = Stopwatch.GetTimestamp();
                    await live.Dispatcher.InvokeAsync(() =>
                    {
                        var t = Stopwatch.StartNew(); moveTo.Invoke(tl, new object[] { x }); handle.Add(t.Elapsed.TotalMilliseconds);
                        if (pending == 0) pending = queued;
                    }, DispatcherPriority.Input);
                    moves++;
                    await Task.Delay(8);
                }
                dragField.SetValue(tl, Enum.Parse(dragField.FieldType, "None"));
                System.Windows.Media.CompositionTarget.Rendering -= Rendered;
                handle.Sort(); onScreen.Sort();
                double P(List<double> v, double q) => v.Count == 0 ? 0 : v[Math.Min(v.Count - 1, (int)(v.Count * q))];
                return $"{label}: {moves} moves · handling median {P(handle, .5):0.00} ms, 95% {P(handle, .95):0.00} ms, worst {P(handle, 1):0.0} ms · to screen median {P(onScreen, .5):0.0} ms, 95% {P(onScreen, .95):0.0} ms, worst {P(onScreen, 1):0.0} ms";
            }
            if (!heavyOnly) Line(await Drag("Empty edit"));
            // The big edit's timeline parts, plus a spread of overlays, blur and pixelation and music.
            var rng = new Random(5);
            var items = Enumerable.Range(0, 120).Select(i => { double s = rng.NextDouble() * 110; return (i % 2 == 0 ? OverlayItem.NewText(s, s + 8, null) with { Text = "CAPTION " + i, X = rng.NextDouble(), Y = rng.NextDouble() } : OverlayItem.NewShape(s, s + 8, null) with { Shape = OverlayShape.Star, X = rng.NextDouble(), Y = rng.NextDouble() }) with { Layer = i }; }).ToList();
            items.Add(OverlayItem.NewShape(0, 120, null) with { Region = OverlayRegion.Pixelate, RegionStrength = 20, ShapeWidth = 700, ShapeHeight = 500, X = .3, Y = .5, Layer = 200 });
            items.Add(OverlayItem.NewShape(0, 120, null) with { Region = OverlayRegion.Blur, RegionStrength = 30, ShapeWidth = 700, ShapeHeight = 500, X = .7, Y = .5, Layer = 201 });
            typeof(TrimWindow).GetMethod("SetOverlays", flags)!.Invoke(live, new object[] { items.ToArray() });
            tl.Freezes = Enumerable.Range(0, 30).Select(i => new FreezeFrame(2 + i * 3.9, 1)).ToArray();
            tl.SlowRegions = Enumerable.Range(0, 8).Select(i => new SpeedRegion(i * 15 + 5, i * 15 + 7, .5)).ToArray();
            tl.ZoomRegions = Enumerable.Range(0, 6).Select(i => new ZoomRegion(i * 20 + 9, i * 20 + 13, .5, .5, 2, ZoomPreset.BuiltIns[1].Curve)).ToArray();
            tl.Cuts = Enumerable.Range(0, 6).Select(i => new CutRegion(-1, i * 20 + 15, i * 20 + 16)).ToArray();
            await Task.Delay(500);
            // Where a seek's time goes, piece by piece.
            double Each(int count, Action<int> step) { var t = Stopwatch.StartNew(); for (int i = 0; i < count; i++) step(i); return t.Elapsed.TotalMilliseconds / count; }
            var seekRng = new Random(9);
            var setPlayhead = typeof(TrimWindow).GetMethod("SetPlayhead", flags)!; var pause = typeof(TrimWindow).GetMethod("Pause", flags)!; var zoomPreview = typeof(TrimWindow).GetMethod("ApplyZoomPreview", flags)!;
            Line($"Seek pieces (ms each): whole seek {Each(200, _ => live.SeekTo(seekRng.NextDouble() * 118, true)):0.00} · pause {Each(200, _ => pause.Invoke(live, null)):0.00} · set playhead {Each(200, _ => setPlayhead.Invoke(live, new object[] { seekRng.NextDouble() * 118 })):0.00} · overlay time {Each(200, _ => live.OverlayView.Time = seekRng.NextDouble() * 118):0.00} · zoom preview {Each(200, _ => zoomPreview.Invoke(live, null)):0.00} · timeline render {Each(20, i => { tl.Position = seekRng.NextDouble() * 118; tl.InvalidateVisual(); tl.UpdateLayout(); }):0.00}");
            Line(await Drag("Heavy edit (122 items, blur and pixelate, 30 freezes, speed, zoom, cuts)"));
            // The same with the audio area open: videos over the clip with their sound on the audio timeline, one
            // unlinked into a sound of its own, and music.
            string inset = Path.Combine(folder, "Inset.mp4");
            if (!File.Exists(inset))
                await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "mandelbrot=size=640x360:rate=30", "-t", "30", "-f", "lavfi", "-i", "sine=frequency=900:sample_rate=48000:duration=30",
                    "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", inset);
            var withVideos = items.Concat(Enumerable.Range(0, 5).Select(i => OverlayItem.NewVideo(i * 22 + 3, i * 22 + 20, inset, 640, 360) with { X = .15 + .17 * i, Y = .8, Scale = .25, Layer = 300 + i, SoundUnlinked = i == 4 })).ToArray();
            typeof(TrimWindow).GetMethod("SetOverlays", flags)!.Invoke(live, new object[] { withVideos });
            typeof(TrimWindow).GetMethod("SetSounds", flags)!.Invoke(live, new object[] { new[] { new SoundItem(inset, 91, 108), new SoundItem(inset, 10, 40, Row: 1) } });
            tl.LanesExpanded = true;
            await Task.Delay(800);
            Line($"Timeline render, audio area open with videos' sound (ms each): {Each(20, i => { tl.Position = seekRng.NextDouble() * 118; tl.InvalidateVisual(); tl.UpdateLayout(); }):0.00}");
            Line(await Drag("Heavy edit + 5 videos with sound (4 linked on the audio timeline, 1 unlinked), music, audio area open"));
            Line($"Managed memory: {GC.GetTotalMemory(true) / 1048576.0:0.0} MiB, working set {Process.GetCurrentProcess().WorkingSet64 / 1048576.0:0.0} MiB");
        }
        finally { live.Close(); }
    }

    // The big edit's ingredients on a short, small clip, added one at a time, each export checked for
    // length: finds which combination throws the timing off. Writes export-matrix.txt.
    internal static async Task ExportMatrixAsync()
    {
        Directory.CreateDirectory(Storage.Root);
        var report = new StringBuilder();
        void Line(string text) { report.AppendLine(text); File.WriteAllText(Path.Combine(Storage.Root, "export-matrix.txt"), report.ToString()); }
        string folder = Path.Combine(Storage.Root, "matrix"); Directory.CreateDirectory(folder);
        string clip = Path.Combine(folder, "Thirty seconds.mp4"), inset = Path.Combine(folder, "Inset.mp4"), music = Path.Combine(folder, "Music.m4a");
        await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=60:duration=30", "-f", "lavfi", "-i", "sine=frequency=300:sample_rate=48000:duration=30",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", clip);
        await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "mandelbrot=size=320x180:rate=30", "-t", "10", "-f", "lavfi", "-i", "sine=frequency=900:sample_rate=48000:duration=10",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", inset);
        await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "sine=frequency=500:sample_rate=48000:duration=12", "-c:a", "aac", music);
        var rng = new Random(11);
        double R(double a, double b) => a + rng.NextDouble() * (b - a);
        var sections = Enumerable.Range(0, 6).Select(i => new KeepSection(i * 5, i * 5 + 4.2)).OrderBy(_ => rng.Next()).ToArray();
        var freezes = Enumerable.Range(0, 8).Select(i => new FreezeFrame(1 + i * 3.6, R(.5, 2))).ToArray();
        var slow = Enumerable.Range(0, 4).Select(i => new SpeedRegion(i * 7 + 2, i * 7 + 3.5, i % 2 == 0 ? .5 : 2)).ToArray();
        var zooms = Enumerable.Range(0, 3).Select(i => new ZoomRegion(i * 9 + 4, i * 9 + 7, .5, .5, 2, ZoomPreset.BuiltIns[1].Curve)).ToArray();
        var overlays = new List<OverlayItem>
        {
            OverlayItem.NewText(0, 30, null) with { Text = "STILL" },
            OverlayItem.NewShape(3, 20, null) with { Region = OverlayRegion.Pixelate, RegionStrength = 20, ShapeWidth = 500, ShapeHeight = 400, X = .3, Layer = 1 },
            OverlayItem.NewVideo(5, 15, inset, 320, 180) with { X = .75, Y = .75, Scale = .5, Layer = 2 },
            OverlayItem.NewText(2, 12, null) with { Text = "MOVES", Keys = new[] { new OverlayKeyframe(0, .2, .5, 1, 0, 1), new OverlayKeyframe(10, .8, .5, 1, 0, 1) }, Layer = 3 },
            OverlayItem.NewText(8, 14, null) with { Text = "GROWS", Keys = new[] { new OverlayKeyframe(0, .2, .3, 1, 0, 1), new OverlayKeyframe(6, .8, .3, 1.5, 20, 1) }, Layer = 4 },
        };
        var sounds = new[] { new SoundItem(music, 0, 12), new SoundItem(music, 14, 26, Duck: true) };
        var volumes = new[] { new VolumeRegion(0, 6, 9, .3) };
        var steps = new (string Name, ShareExportOptions Options)[]
        {
            ("sections only", new ShareExportOptions()),
            ("+ freezes", new ShareExportOptions { Freezes = freezes }),
            ("+ speed parts", new ShareExportOptions { Freezes = freezes, SlowRegions = slow }),
            ("+ zooms", new ShareExportOptions { Freezes = freezes, SlowRegions = slow, ZoomRegions = zooms }),
            ("+ overlays", new ShareExportOptions { Freezes = freezes, SlowRegions = slow, ZoomRegions = zooms, Overlays = overlays }),
            ("+ music and volume", new ShareExportOptions { Freezes = freezes, SlowRegions = slow, ZoomRegions = zooms, Overlays = overlays, Sounds = sounds, VolumeRegions = volumes }),
            ("speed parts only", new ShareExportOptions { SlowRegions = slow }),
            ("zooms only", new ShareExportOptions { ZoomRegions = zooms }),
            ("freezes + zooms", new ShareExportOptions { Freezes = freezes, ZoomRegions = zooms }),
            ("freezes + speed", new ShareExportOptions { Freezes = freezes, SlowRegions = slow }),
        };
        foreach (var (name, options) in steps)
        {
            var output = Path.Combine(folder, $"{name.Replace("+", "and")}.mp4"); if (File.Exists(output)) File.Delete(output);
            double expected = options.OutputSeconds(sections);
            var watch = Stopwatch.StartNew();
            try
            {
                await ExportServices.PreciseAsync(clip, output, sections, null, CancellationToken.None, true, options);
                var got = ClipMedia.Read(output);
                Line($"{name}: OK, {got.Duration:0.00} s (expected {expected:0.00}) in {watch.Elapsed.TotalSeconds:0.0} s");
            }
            catch (Exception ex) { Line($"{name}: FAILED in {watch.Elapsed.TotalSeconds:0.0} s: {ex.Message[..Math.Min(400, ex.Message.Length)]}"); }
        }
    }

    // Export speed against the number of filter threads, on a 30 s 1440p60 edit. Writes export-threads.txt.
    internal static async Task ThreadsAsync()
    {
        Directory.CreateDirectory(Storage.Root);
        var report = new StringBuilder();
        void Line(string text) { report.AppendLine(text); File.WriteAllText(Path.Combine(Storage.Root, "export-threads.txt"), report.ToString()); }
        string folder = Path.Combine(Storage.Root, "threads"); Directory.CreateDirectory(folder);
        string clip = Path.Combine(folder, "Thirty seconds 1440p60.mp4");
        if (!File.Exists(clip))
            await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "testsrc2=size=2560x1440:rate=60:duration=30", "-f", "lavfi", "-i", "sine=frequency=300:sample_rate=48000:duration=30",
                "-c:v", "libx264", "-preset", "ultrafast", "-threads", "8", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", clip);
        var rng = new Random(4);
        var items = Enumerable.Range(0, 30).Select(i => { double s = rng.NextDouble() * 25; return OverlayItem.NewText(s, s + 5, null) with { Text = "CAPTION " + i, X = rng.NextDouble(), Y = rng.NextDouble(), Layer = i }; }).ToList();
        items.Add(OverlayItem.NewText(2, 12, null) with { Text = "MOVES", Keys = new[] { new OverlayKeyframe(0, .2, .5, 1, 0, 1), new OverlayKeyframe(10, .8, .5, 1, 0, 1) }, Layer = 40 });
        items.Add(OverlayItem.NewShape(5, 20, null) with { Region = OverlayRegion.Blur, RegionStrength = 25, ShapeWidth = 600, ShapeHeight = 400, X = .3, Layer = 41 });
        var options = new ShareExportOptions
        {
            Overlays = items, Freezes = new[] { new FreezeFrame(6, 1), new FreezeFrame(18, 1) },
            ZoomRegions = new[] { new ZoomRegion(3, 8, .5, .5, 2, ZoomPreset.BuiltIns[1].Curve), new ZoomRegion(15, 20, .3, .6, 1.8, ZoomPreset.BuiltIns[1].Curve) },
            SlowRegions = new[] { new SpeedRegion(10, 12, .5) },
        };
        var sections = new[] { new KeepSection(0, 14), new KeepSection(16, 29) };
        Line($"{Environment.ProcessorCount} logical processors");
        foreach (int threads in new[] { 1, 4, 8 })
        {
            ExportServices.FilterThreads = threads;
            var output = Path.Combine(folder, $"Threads {threads}.mp4"); if (File.Exists(output)) File.Delete(output);
            var watch = Stopwatch.StartNew();
            await ExportServices.PreciseAsync(clip, output, sections, null, CancellationToken.None, false, options);
            Line($"{threads} filter thread(s): {watch.Elapsed.TotalSeconds:0.0} s for {options.OutputSeconds(sections):0.0} s of 1440p60, CPU {ExportServices.LastProcessCpu.TotalSeconds:0} s");
        }
        ExportServices.FilterThreads = Math.Clamp(Environment.ProcessorCount / 3, 1, 4);
    }

    // The big one: two minutes of 1440p60 with a lot going on. Writes stress-big-report.txt.
    internal static async Task RunBigAsync()
    {
        Directory.CreateDirectory(Storage.Root);
        var report = new StringBuilder();
        void Line(string text) { report.AppendLine(text); File.WriteAllText(Path.Combine(Storage.Root, "stress-big-report.txt"), report.ToString()); }
        string folder = Path.Combine(Storage.Root, "stress-big"); Directory.CreateDirectory(folder);
        string clip = Path.Combine(folder, "Two minutes 1440p60.mp4"), inset = Path.Combine(folder, "Inset.mp4"), music = Path.Combine(folder, "Music.m4a");
        var made = Stopwatch.StartNew();
        await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "testsrc2=size=2560x1440:rate=60:duration=120", "-f", "lavfi", "-i", "sine=frequency=300:sample_rate=48000:duration=120",
            "-c:v", "libx264", "-preset", "ultrafast", "-threads", "8", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", clip);
        await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "mandelbrot=size=640x360:rate=30", "-t", "30", "-f", "lavfi", "-i", "sine=frequency=900:sample_rate=48000:duration=30",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", inset);
        await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "sine=frequency=500:sample_rate=48000:duration=40", "-c:a", "aac", music);
        Line($"Made the test clip ({made.Elapsed.TotalSeconds:0} s)");
        Storage.Save(new Settings { OutputFolder = Path.Combine(Storage.Root, "clips") });
        var rng = new Random(11);
        double R(double a, double b) => a + rng.NextDouble() * (b - a);
        var items = new List<OverlayItem>();
        var shapes = new[] { OverlayShape.Box, OverlayShape.Star, OverlayShape.Heart, OverlayShape.Burst, OverlayShape.Ellipse, OverlayShape.Hexagon, OverlayShape.Check, OverlayShape.Cross, OverlayShape.Bubble, OverlayShape.Arrow };
        // Still captions, stickers, shapes and drawings, all over the two minutes.
        for (int i = 0; i < 90; i++)
        {
            double start = R(0, 110), end = Math.Min(120, start + R(3, 15));
            items.Add((i % 3) switch
            {
                0 => OverlayItem.NewText(start, end, null) with { Text = $"CAPTION {i}\nsecond line", X = R(.2, .8), Y = R(.1, .9), Shadow = new OverlayShadow(i % 2 == 0), Shape = i % 4 == 0 ? OverlayShape.Box : OverlayShape.None },
                1 => OverlayItem.NewShape(start, end, null) with { Shape = shapes[i % shapes.Length], ShapeColor = i % 2 == 0 ? "#CCFF3B30" : "#00FF3B30", ShapeBorderWidth = i % 2 == 0 ? 0 : 8, ShapeBorderColor = "#FFFFE600", Dash = (OverlayDash)(i % 3), X = R(.1, .9), Y = R(.1, .9), ShapeWidth = R(150, 500), ShapeHeight = R(120, 400) },
                _ => OverlayItem.NewShape(start, end, null) with { Shape = i % 2 == 0 ? OverlayShape.Freehand : OverlayShape.Polyline, Closed = false, LineWidth = R(4, 14), EndCap = OverlayCap.Arrow, StartCap = OverlayCap.Dot, ShapeColor = "#FF00E5FF", X = R(.2, .8), Y = R(.2, .8),
                    Points = Enumerable.Range(0, i % 2 == 0 ? 200 : 6).Select(k => (Math.Round(R(-.5, .5), 4), Math.Round(R(-.5, .5), 4))).ToArray() },
            });
        }
        // Blur and pixelate areas.
        for (int i = 0; i < 10; i++)
        {
            double start = R(0, 100);
            items.Add(OverlayItem.NewShape(start, Math.Min(120, start + R(8, 20)), null) with { Region = i % 2 == 0 ? OverlayRegion.Blur : OverlayRegion.Pixelate, RegionStrength = R(10, 40), X = R(.2, .8), Y = R(.2, .8), ShapeWidth = R(300, 900), ShapeHeight = R(200, 700), Shape = i % 3 == 0 ? OverlayShape.Ellipse : OverlayShape.Box });
        }
        // Inset videos.
        for (int i = 0; i < 4; i++) { double start = R(0, 90); items.Add(OverlayItem.NewVideo(start, start + 20, inset, 640, 360) with { X = R(.2, .8), Y = R(.2, .8), Scale = R(.3, .6), Mask = i % 2 == 0 ? OverlayShape.Hexagon : OverlayShape.Box }); }
        // Keyframed items: twelve that only move, four that also grow and turn.
        for (int i = 0; i < 16; i++)
        {
            double start = R(0, 105), length = R(5, 10);
            bool grow = i >= 12;
            items.Add(OverlayItem.NewText(start, start + length, null) with { Text = $"MOVING {i}", Keys = new[] { new OverlayKeyframe(0, .15, R(.2, .8), 1, 0, 1), new OverlayKeyframe(length / 2, .5, R(.2, .8), grow ? 1.5 : 1, grow ? 30 : 0, 1), new OverlayKeyframe(length, .85, R(.2, .8), 1, 0, 1) } });
        }
        // Entrances and exits.
        for (int i = 0; i < 12; i++) { double start = R(0, 110); items.Add(OverlayItem.NewText(start, start + 6, null) with { Text = "TYPED IN " + i, In = i % 2 == 0 ? OverlayMotion.Typewriter : OverlayMotion.Pop, InLength = 1, Out = OverlayMotion.Fade, X = R(.2, .8), Y = R(.2, .8) }); }
        items = items.Select((o, i) => o with { Layer = i }).ToList();
        var freezes = Enumerable.Range(0, 30).Select(i => new FreezeFrame(2 + i * 3.9, R(.5, 2))).ToArray();
        var sections = Enumerable.Range(0, 12).Select(i => new KeepSection(i * 10, i * 10 + 8.5)).OrderBy(_ => rng.Next()).ToArray();
        var slow = Enumerable.Range(0, 8).Select(i => new SpeedRegion(i * 15 + 5, i * 15 + 7, i % 2 == 0 ? .5 : 2)).ToArray();
        var zooms = Enumerable.Range(0, 6).Select(i => new ZoomRegion(i * 20 + 9, i * 20 + 13, R(.3, .7), R(.3, .7), 2, ZoomPreset.BuiltIns[1].Curve)).ToArray();
        var sounds = new[] { new SoundItem(music, 0, 40), new SoundItem(music, 40, 80, Duck: true, Row: 0), new SoundItem(music, 80, 118, FadeIn: 2, FadeOut: 3) };
        var volumes = Enumerable.Range(0, 5).Select(i => new VolumeRegion(0, i * 24 + 3, i * 24 + 8, i % 2 == 0 ? .3 : 1.6)).ToArray();
        Line($"Edit: {items.Count} overlay items, {freezes.Length} freezes, {sections.Length} sections in shuffled order, {slow.Length} speed parts, {zooms.Length} zooms, {sounds.Length} music tracks, {volumes.Length} volume parts");

        // Preparing the overlays alone.
        var media = ClipMedia.Read(clip);
        string prepFolder = Path.Combine(folder, "prep"); if (Directory.Exists(prepFolder)) Directory.Delete(prepFolder, true);
        // From scratch (nothing remembered), then again as is, then after changing one caption and moving another.
        string cache = Path.Combine(Path.GetTempPath(), "Flashback-overlay-cache"); try { if (Directory.Exists(cache)) Directory.Delete(cache, true); } catch { }
        async Task<(double Seconds, int Layers)> Prep(IReadOnlyList<OverlayItem> list)
        {
            if (Directory.Exists(prepFolder)) Directory.Delete(prepFolder, true);
            var watch = Stopwatch.StartNew();
            var made = await OverlayExport.RenderAsync(OverlayOrder.BackToFront(list).Select(o => o.Validated()).ToList(), media.Width, media.Height, media.FrameRate, prepFolder, CancellationToken.None);
            return (watch.Elapsed.TotalSeconds, made.Count);
        }
        var cold = await Prep(items);
        long cacheBytes = Directory.Exists(cache) ? new DirectoryInfo(cache).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
        Line($"Export prep from scratch: {cold.Seconds:0.0} s → {cold.Layers} layers, {cacheBytes / 1048576.0:0} MiB of pictures");
        var warm = await Prep(items);
        Line($"Export prep again, nothing changed: {warm.Seconds:0.00} s");
        var tweaked = items.ToList(); tweaked[0] = tweaked[0] with { Text = "CHANGED" }; tweaked[3] = tweaked[3] with { Start = tweaked[3].Start + 1, End = tweaked[3].End + 1 };
        var changed = await Prep(tweaked);
        Line($"Export prep after changing one caption and moving another: {changed.Seconds:0.00} s");
        try { Directory.Delete(prepFolder, true); } catch { }

        // The editor with all of it.
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var open = Stopwatch.StartNew();
        var trim = new TrimWindow(clip, true) { Width = 1600, Height = 1000 };
        try
        {
            var content = (FrameworkElement)trim.Content;
            content.Measure(new Size(1600, 1000)); content.Arrange(new Rect(0, 0, 1600, 1000)); content.UpdateLayout();
            typeof(TrimWindow).GetMethod("SetOverlays", flags)!.Invoke(trim, new object[] { items.ToArray() });
            trim.Timeline.Freezes = freezes; trim.Timeline.SlowRegions = slow; trim.Timeline.ZoomRegions = zooms; trim.Timeline.VolumeRegions = volumes;
            typeof(TrimWindow).GetMethod("SetSounds", flags)!.Invoke(trim, new object[] { sounds });
            content.UpdateLayout();
            Line($"Open the editor and load the edit: {open.ElapsedMilliseconds} ms");
            void Settle() => content.UpdateLayout();
            double Time(int count, Action<int> step) { Settle(); var t = Stopwatch.StartNew(); for (int i = 0; i < count; i++) { step(i); Settle(); } return t.Elapsed.TotalMilliseconds / count; }
            var setPlayhead = typeof(TrimWindow).GetMethod("SetPlayhead", flags)!;
            double tick = Time(600, i => setPlayhead.Invoke(trim, new object[] { 30 + i / 60.0 }));
            var scrubRng = new Random(3);
            double scrub = Time(200, _ => trim.SeekTo(scrubRng.NextDouble() * 118));
            Line($"Playback tick: {tick:0.00} ms · scrub: {scrub:0.00} ms");
            var bmp = new RenderTargetBitmap((int)trim.Timeline.ActualWidth, (int)trim.Timeline.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            var tl = Stopwatch.StartNew(); for (int i = 0; i < 10; i++) { trim.Timeline.OverlaysOpen = i % 2 == 0; content.UpdateLayout(); bmp.Clear(); bmp.Render(trim.Timeline); }
            Line($"Timeline redraw with {items.Count} layers (folding and unfolding): {tl.Elapsed.TotalMilliseconds / 10:0.0} ms");
            var edit = typeof(TrimWindow).GetMethod("EditOverlay", flags)!; typeof(TrimWindow).GetMethod("OpenOverlay", flags)!.Invoke(trim, new object[] { 5 });
            double slider = Time(100, i => edit.Invoke(trim, new object[] { "opacity", (Func<OverlayItem, OverlayItem>)(o => o with { Opacity = .5 + i % 50 / 100.0 }) }));
            Line($"Panel slider edit with {items.Count} items: {slider:0.00} ms");
            Line($"Memory: {GC.GetTotalMemory(true) / 1048576.0:0} MiB managed, working set {Process.GetCurrentProcess().WorkingSet64 / 1048576.0:0} MiB");
        }
        finally { trim.Close(); }

        // The whole export.
        var options = new ShareExportOptions { Overlays = items, Freezes = freezes, SlowRegions = slow, ZoomRegions = zooms, Sounds = sounds, VolumeRegions = volumes };
        double expected = options.OutputSeconds(sections);
        var output = Path.Combine(folder, "Big export.mp4");
        var export = Stopwatch.StartNew();
        try
        {
            await ExportServices.PreciseAsync(clip, output, sections, null, CancellationToken.None, false, options);
            var result = ClipMedia.Read(output);
            Line($"Export: {export.Elapsed.TotalSeconds:0.0} s for {expected:0.0} s of 1440p60; came out {result.Duration:0.00} s (expected {expected:0.00}), {result.Width}x{result.Height}, audio {result.HasAudio}");
        }
        catch (Exception ex) { Line($"Export FAILED after {export.Elapsed.TotalSeconds:0.0} s: {ex.Message[..Math.Min(600, ex.Message.Length)]}"); }
    }

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
