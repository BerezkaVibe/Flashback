using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Flashback;

// Blur and pixelate shapes, picture-in-picture videos, keyframes, freeze frames, volume parts and music:
// real exports checked pixel by pixel and sample by sample, then the live editor with screenshots.
internal static class MediaPartsDiagnostics
{
    internal static async Task RunAsync(bool liveOnly = false)
    {
        Directory.CreateDirectory(Storage.Root);
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAILED: " + name);
            checks.Add(name); File.WriteAllText(Path.Combine(Storage.Root, "media-parts-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        }
        var folder = Path.Combine(Storage.Root, "media parts"); Directory.CreateDirectory(folder);
        var source = Path.Combine(folder, "Busy source.mp4");
        await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30:duration=6", "-f", "lavfi", "-i", "sine=frequency=500:sample_rate=48000:duration=6",
            "-c:v", "libx264", "-threads", "2", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", source);
        var inset = Path.Combine(folder, "Magenta inset.mp4");
        await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "color=0xFF00FF:s=320x240:r=30:d=6", "-f", "lavfi", "-i", "sine=frequency=1500:sample_rate=48000:duration=6",
            "-c:v", "libx264", "-threads", "2", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", inset);
        // The live editor alone, without the export checks, for quick looks.
        if (liveOnly) { await LiveAsync(source, inset, Check); return; }
        var noisy = Path.Combine(folder, "Noisy source.mp4");
        await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30:duration=4,noise=alls=50:allf=t", "-c:v", "libx264", "-threads", "2", "-preset", "ultrafast", "-crf", "12", "-pix_fmt", "yuv420p", noisy);
        var music = Path.Combine(folder, "Tune.m4a");
        await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "sine=frequency=2000:sample_rate=48000:duration=4", "-c:a", "aac", music);

        async Task<byte[]> Gray(string file, double at, int x, int y, int w, int h)
        {
            var raw = Path.Combine(folder, $"g-{Guid.NewGuid():N}.raw");
            await EditorDiagnostics.Ffmpeg("-y", "-ss", ExportServices.Number(at), "-i", file, "-frames:v", "1", "-vf", $"crop={w}:{h}:{x}:{y},format=gray", "-f", "rawvideo", raw);
            var bytes = File.ReadAllBytes(raw); File.Delete(raw); return bytes;
        }
        async Task<byte[]> Rgb(string file, double at, int x, int y)
        {
            var raw = Path.Combine(folder, $"p-{Guid.NewGuid():N}.rgb");
            await EditorDiagnostics.Ffmpeg("-y", "-ss", ExportServices.Number(at), "-i", file, "-frames:v", "1", "-vf", $"format=rgb24,crop=1:1:{x}:{y}", "-f", "rawvideo", "-pix_fmt", "rgb24", raw);
            var bytes = File.ReadAllBytes(raw); File.Delete(raw); return bytes;
        }
        // How busy a patch is: the average difference between neighbouring pixels.
        static double Detail(byte[] g, int w) { double sum = 0; int n = 0; for (int i = 1; i < g.Length; i++) if (i % w != 0) { sum += Math.Abs(g[i] - g[i - 1]); n++; } return sum / Math.Max(1, n); }
        bool Near(byte[] rgb, int r, int g, int b) => Math.Abs(rgb[0] - r) < 60 && Math.Abs(rgb[1] - g) < 60 && Math.Abs(rgb[2] - b) < 60;

        // ---- Blur and pixelate ----
        // Shapes are sized in 1080-line pixels: on a 360-line clip, 480x360 is a 160x120 patch.
        var blur = OverlayItem.NewShape(0, 6, null) with { Region = OverlayRegion.Blur, RegionStrength = 30, ShapeWidth = 480, ShapeHeight = 360, X = .25, Y = .5, Corner = 0 };
        var pixelate = blur with { Region = OverlayRegion.Pixelate, X = .75, RegionStrength = 48 };
        var treated = Path.Combine(folder, "Blur and pixelate.mp4");
        await ExportServices.PreciseAsync(noisy, treated, new[] { new KeepSection(0, 4) }, null, CancellationToken.None, true, new ShareExportOptions { Overlays = new[] { blur, pixelate } });
        double before = Detail(await Gray(noisy, 2, 100, 140, 120, 80), 120), blurred = Detail(await Gray(treated, 2, 100, 140, 120, 80), 120);
        double outsideBefore = Detail(await Gray(noisy, 2, 280, 10, 80, 40), 80), outsideAfter = Detail(await Gray(treated, 2, 280, 10, 80, 40), 80);
        Check(blurred < before * .5 && Math.Abs(outsideAfter - outsideBefore) < outsideBefore * .25 + 1, $"A blur shape softens only what's inside it (detail {before:0.0} → {blurred:0.0}, outside {outsideBefore:0.0} → {outsideAfter:0.0})");
        var blocks = await Gray(treated, 2, 424, 124, 32, 32);
        int same = 0; for (int y = 0; y < 32; y++) for (int x = 1; x < 32; x++) if (Math.Abs(blocks[y * 32 + x] - blocks[y * 32 + x - 1]) < 6) same++;
        Check(same > 32 * 31 * .8, $"A pixelate shape turns what's inside it into blocks ({same * 100 / (32 * 31)}% of neighbours match)");

        // ---- Picture-in-picture ----
        var pip = OverlayItem.NewVideo(1, 4, inset, 320, 240) with { X = .5, Y = .5, Scale = 1, ImageCorner = 0, Mask = OverlayShape.Ellipse };
        var inPicture = Path.Combine(folder, "Picture in picture.mp4");
        await ExportServices.PreciseAsync(source, inPicture, new[] { new KeepSection(0, 5) }, null, CancellationToken.None, true, new ShareExportOptions { Overlays = new[] { pip } });
        // At scale 1 a 320x240 video fits a 540-line square on 1080 lines: 540x405, so 180x135 on 360 lines.
        Check(Near(await Rgb(inPicture, 2, 320, 180), 255, 0, 255) && !Near(await Rgb(inPicture, .5, 320, 180), 255, 0, 255) && !Near(await Rgb(inPicture, 4.5, 320, 180), 255, 0, 255),
            "A video shows over the clip only during its part");
        Check(!Near(await Rgb(inPicture, 2, 320 - 86, 180 - 64), 255, 0, 255), "An oval mask cuts the video's corners away");
        var pcm = Path.Combine(folder, "pip.pcm");
        await EditorDiagnostics.Ffmpeg("-y", "-ss", "2", "-t", "1", "-i", inPicture, "-ac", "1", "-ar", "48000", "-f", "s16le", pcm);
        Check(Tone(File.ReadAllBytes(pcm), 1500) > Tone(File.ReadAllBytes(pcm), 3100) * 4, "The video's own sound mixes in while it plays");

        // ---- Keyframes ----
        var dot = OverlayItem.NewShape(0, 4, null) with { ShapeColor = "#FF00FF00", ShapeWidth = 60, ShapeHeight = 60, Corner = 0, Y = .5 };
        dot = dot with { Keys = new[] { new OverlayKeyframe(1, .2, .5, 1, 0, 1), new OverlayKeyframe(3, .8, .5, 1, 0, 1) } };
        var moving = Path.Combine(folder, "Keyframes.mp4");
        await ExportServices.PreciseAsync(source, moving, new[] { new KeepSection(0, 4) }, null, CancellationToken.None, true, new ShareExportOptions { Overlays = new[] { dot } });
        Check(Near(await Rgb(moving, .5, 128, 180), 0, 255, 0) && Near(await Rgb(moving, 2, 320, 180), 0, 255, 0) && Near(await Rgb(moving, 3.5, 512, 180), 0, 255, 0) && !Near(await Rgb(moving, 3.5, 128, 180), 0, 255, 0),
            "Keyframes hold before the first, glide through the middle and hold after the last");
        Check(Math.Abs(dot.Posed(2).X - .5) < 1e-9 && dot.Adjusted(2, o => o with { X = .6 }).Keys.Count == 3 && dot.Adjusted(2, o => o with { X = .6 }).X == dot.X,
            "Moving a keyframed item adds a keyframe and leaves the rest alone");

        // Shapes and videos stuck to the video zoom with it, like text and pictures: at x 0.6 under a 2x
        // zoom on the centre they move out to 0.7; fixed on screen they stay at 0.6.
        var steady = new ZoomRegion(0, 4, .5, .5, 2, ZoomPreset.BuiltIns[2].Curve);
        foreach (var (item, color) in new[] { (dot with { Keys = Array.Empty<OverlayKeyframe>(), X = .6 }, (0, 255, 0)), (pip with { Start = 0, End = 4, X = .6, Scale = .15, Mask = OverlayShape.Box }, (255, 0, 255)) })
            foreach (bool stuck in new[] { true, false })
            {
                var output = Path.Combine(folder, $"Zoomed {item.Kind} {stuck}.mp4");
                await ExportServices.PreciseAsync(source, output, new[] { new KeepSection(0, 3) }, null, CancellationToken.None, true, new ShareExportOptions { ZoomRegions = new[] { steady }, Overlays = new[] { item with { StickToVideo = stuck } } });
                int x = (int)(640 * (stuck ? .7 : .6));
                Check(Near(await Rgb(output, 2, x, 180), color.Item1, color.Item2, color.Item3), $"{(item.Kind == OverlayKind.Video ? "Videos" : "Shapes")} {(stuck ? "stuck to the video follow a zoom" : "fixed on screen ignore a zoom")}");
            }
        // ---- Freeze frame ----
        // A freeze at 1 s holding 1.5 s: the export is 1.5 s longer, still during the hold, and after it
        // the clip carries on from 1 s, so nothing is skipped.
        var frozen = Path.Combine(folder, "Freeze.mp4");
        await ExportServices.PreciseAsync(source, frozen, new[] { new KeepSection(0, 4) }, null, CancellationToken.None, true, new ShareExportOptions { Freezes = new[] { new FreezeFrame(1, 1.5) }, Overlays = new[] { dot with { Keys = Array.Empty<OverlayKeyframe>(), X = .5 } } });
        var a = await Gray(frozen, 1.2, 0, 0, 640, 360); var b = await Gray(frozen, 2.3, 0, 0, 640, 360);
        var afterHold = await Gray(frozen, 3.5, 0, 0, 640, 360); var sourceAfter = await Gray(source, 2.0, 0, 0, 640, 360); var sourceHeld = await Gray(source, 1.0, 0, 0, 640, 360);
        double Difference(byte[] x, byte[] y) => x.Zip(y).Average(p => Math.Abs(p.First - p.Second));
        double still = Difference(a, b), resumed = Difference(afterHold, sourceAfter), held = Difference(a, sourceHeld);
        Check(Math.Abs(ClipMedia.Read(frozen).Duration - 5.5) < .2 && still < 2, $"A freeze frame holds the picture and adds its time (length {ClipMedia.Read(frozen).Duration:0.00} s, still {still:0.0})");
        Check(resumed < 6 && held < 20, $"After a freeze the clip plays on from the same moment, skipping nothing (resumed {resumed:0.0}, held {held:0.0})");
        Check(Near(await Rgb(frozen, 1.8, 320, 180), 0, 255, 0), "Shapes and text show on the held frame");
        var pcmHold = await Pcm(frozen, 1.3, .8);
        Check(Loudness(pcmHold) < 300, "The hold is silent");
        // Sections play in the order they're listed: 3-4 s, then 0-1 s.
        var swapped = Path.Combine(folder, "Swapped.mp4");
        await ExportServices.PreciseAsync(source, swapped, new[] { new KeepSection(3, 4), new KeepSection(0, 1) }, null, CancellationToken.None, true, new ShareExportOptions());
        double first = Difference(await Gray(swapped, .5, 0, 0, 640, 360), await Gray(source, 3.5, 0, 0, 640, 360)), second = Difference(await Gray(swapped, 1.5, 0, 0, 640, 360), await Gray(source, .5, 0, 0, 640, 360));
        Check(Math.Abs(ClipMedia.Read(swapped).Duration - 2) < .15 && first < 6 && second < 6, $"Sections play in the order they're listed ({first:0.0}, {second:0.0})");
        Check(Math.Abs(ShareExportOptions.OutputTime(new ShareExportOptions().Pieces(new[] { new KeepSection(3, 4), new KeepSection(0, 1) }), .5) - 1.5) < 1e-9, "Music and sounds land where their moment plays in a reordered export");
        // ---- More clips ----
        var copy = Path.Combine(folder, "Second take.mp4"); File.Copy(source, copy, true);
        var twice = await ClipJoin.JoinAsync(source, copy, Path.Combine(folder, "Joined same.mp4"), null, CancellationToken.None, syntheticEncoder: true);
        Check(ClipJoin.Matches(ClipMedia.Read(source), ClipMedia.Read(copy)) && Math.Abs(ClipMedia.Read(twice).Duration - 12) < .3 && ClipMedia.Read(twice).HasAudio, "Matching clips join end to end by copying");
        var mixed = await ClipJoin.JoinAsync(source, inset, Path.Combine(folder, "Joined mixed.mp4"), null, CancellationToken.None, syntheticEncoder: true);
        var mixedMedia = ClipMedia.Read(mixed);
        Check(mixedMedia.Width == 640 && mixedMedia.Height == 360 && Math.Abs(mixedMedia.Duration - 12) < .3 && Near(await Rgb(mixed, 9, 320, 180), 255, 0, 255) && !Near(await Rgb(mixed, 9, 10, 180), 255, 0, 255),
            "A clip of another size is converted to match: fitted and letterboxed");
        // ---- Volume parts and music ----
        var quiet = Path.Combine(folder, "Volume part.mp4");
        await ExportServices.PreciseAsync(source, quiet, new[] { new KeepSection(0, 4) }, null, CancellationToken.None, true, new ShareExportOptions { VolumeRegions = new[] { new VolumeRegion(0, 1, 2, 0) } });
        Check(Loudness(await Pcm(quiet, 1.2, .6)) < 300 && Loudness(await Pcm(quiet, 2.4, .6)) > 3000, "A volume part at 0% mutes only its stretch");
        var scored = Path.Combine(folder, "Music.mp4");
        await ExportServices.PreciseAsync(source, scored, new[] { new KeepSection(0, 4) }, null, CancellationToken.None, true,
            new ShareExportOptions { Sounds = new[] { new SoundItem(music, 1, 3, Duck: true, DuckLevel: .2) } });
        var during = await Pcm(scored, 1.5, 1); var after = await Pcm(scored, 3.3, .5);
        Check(Tone(during, 2000) > Tone(during, 500) && Tone(after, 2000) < Tone(after, 500) * .1, "Music plays over its part, ducking the clip's sound, and stops after");
        var silentSource = Path.Combine(folder, "Silent.mp4");
        await EditorDiagnostics.Ffmpeg("-y", "-i", source, "-an", "-c:v", "copy", silentSource);
        var scoredSilent = Path.Combine(folder, "Music on silence.mp4");
        await ExportServices.PreciseAsync(silentSource, scoredSilent, new[] { new KeepSection(0, 4) }, null, CancellationToken.None, true, new ShareExportOptions { Sounds = new[] { new SoundItem(music, 0, 2) } });
        Check(ClipMedia.Read(scoredSilent).HasAudio && Tone(await Pcm(scoredSilent, .5, 1), 2000) > 1000, "Music gives a silent clip a soundtrack");

        async Task<byte[]> Pcm(string file, double at, double length)
        {
            var raw = Path.Combine(folder, $"a-{Guid.NewGuid():N}.pcm");
            await EditorDiagnostics.Ffmpeg("-y", "-ss", ExportServices.Number(at), "-t", ExportServices.Number(length), "-i", file, "-ac", "1", "-ar", "48000", "-f", "s16le", raw);
            var bytes = File.ReadAllBytes(raw); File.Delete(raw); return bytes;
        }

        await LiveAsync(source, inset, Check);
    }
    private static short Loudness(byte[] pcm) { short max = 0; for (int i = 0; i + 1 < pcm.Length; i += 2) max = Math.Max(max, Math.Abs(BitConverter.ToInt16(pcm, i))); return max; }
    // The strength of one frequency in 16-bit mono audio at 48 kHz.
    private static double Tone(byte[] pcm, double hz)
    {
        double re = 0, im = 0; int n = pcm.Length / 2;
        for (int i = 0; i < n; i++) { double s = BitConverter.ToInt16(pcm, i * 2), a = 2 * Math.PI * hz * i / 48000; re += s * Math.Cos(a); im += s * Math.Sin(a); }
        return Math.Sqrt(re * re + im * im) / Math.Max(1, n);
    }

    // ---- The live editor ----
    private static async Task LiveAsync(string source, string inset, Action<bool, string> Check)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var live = new TrimWindow(source) { WindowState = WindowState.Maximized };
        try
        {
            live.Show();
            var until = Stopwatch.StartNew();
            while (!live.Player.NaturalDuration.HasTimeSpan && until.Elapsed.TotalSeconds < 12) await Task.Delay(100);
            Check(live.Player.NaturalDuration.HasTimeSpan, "The editor opens the clip");
            var frame = Path.Combine(Storage.Root, "media parts", "frame.png");
            await EditorDiagnostics.Ffmpeg("-y", "-ss", "2", "-i", source, "-frames:v", "1", frame);
            var standIn = new System.Windows.Controls.Image { Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(frame)), Stretch = Stretch.Uniform, IsHitTestVisible = false };
            var cell = (System.Windows.Controls.Grid)live.OverlayView.Parent;
            cell.Children.Insert(cell.Children.IndexOf(live.OverlayView), standIn);
            live.OverlayView.Source = standIn; current = (live, standIn);
            var items = new[]
            {
                OverlayItem.NewShape(0, 6, null) with { Region = OverlayRegion.Blur, RegionStrength = 30, ShapeWidth = 480, ShapeHeight = 360, X = .25, Y = .35, Corner = 30 },
                OverlayItem.NewShape(0, 6, null) with { Region = OverlayRegion.Pixelate, RegionStrength = 40, ShapeWidth = 420, ShapeHeight = 360, X = .75, Y = .35, Shape = OverlayShape.Ellipse, Layer = 1 },
                OverlayItem.NewVideo(0, 6, inset, 320, 240) with { X = .78, Y = .76, Scale = .6, Mask = OverlayShape.Hexagon, BorderWidth = 6, Layer = 2 },
                OverlayItem.NewText(0, 6, null) with { Text = "GLIDING", Y = .8, Keys = new[] { new OverlayKeyframe(0, .2, .8, 1, -10, 1), new OverlayKeyframe(4, .55, .8, 1.3, 10, 1) }, Layer = 3 },
                OverlayItem.NewShape(0, 6, null) with { Shape = OverlayShape.Heart, ShapeColor = "#E6FF2D55", ShapeWidth = 200, ShapeHeight = 180, X = .5, Y = .3, Layer = 4 },
            };
            typeof(TrimWindow).GetMethod("SetOverlays", flags)!.Invoke(live, new object[] { items });
            Check(live.Timeline.OverlaysFolded, "With several layers, the timeline folds them into one slim strip");
            double foldedHeight = live.Timeline.Height;
            live.SeekTo(2); await Task.Delay(500); await Shot(live, "parts-timeline-folded.png");
            typeof(TrimWindow).GetMethod("OpenOverlay", flags)!.Invoke(live, new object[] { 1 });
            Check(!live.Timeline.OverlaysFolded && live.Timeline.Height > foldedHeight + 30, "Opening an item unfolds the layers");
            await Shot(live, "parts-timeline-open.png");
            live.Timeline.OverlaysOpen = false; typeof(TrimWindow).GetMethod("CloseOverlay", flags)!.Invoke(live, null);            live.SeekTo(1); await Task.Delay(900);
            var view = live.OverlayView;
            var textAt1 = view.ScreenBounds(3);
            live.SeekTo(3); await Task.Delay(900);
            var textAt3 = view.ScreenBounds(3);
            Check(textAt3.X > textAt1.X + 40, $"Keyframed text moves across the preview ({textAt1.X:0} → {textAt3.X:0})");
            var players = (System.Collections.IDictionary)typeof(OverlayLayer).GetField("players", flags)!.GetValue(view)!;
            Check(players.Count == 1, "The picture-in-picture video gets its own player");
            var player = players.Values.Cast<MediaPlayer>().Single();
            until.Restart(); while (!player.NaturalDuration.HasTimeSpan && until.Elapsed.TotalSeconds < 8) await Task.Delay(100);
            await Task.Delay(400);
            Check(Math.Abs(player.Position.TotalSeconds - 3) < .2, $"The inset video is paused at the playhead's moment ({player.Position.TotalSeconds:0.00} s)");
            await Shot(live, "parts-preview-paused.png");
            live.HandleKey(Key.Space, ModifierKeys.None, true); await Task.Delay(1500);
            double drift = Math.Abs(player.Position.TotalSeconds - live.Playhead);
            await Shot(live, "parts-preview-playing.png");
            live.HandleKey(Key.Space, ModifierKeys.None, true);
            Check(live.Playhead > 3.8 && drift < .4, $"The inset video plays along with the clip (drift {drift:0.00} s)");
            // With the preview zoomed, blur and pixelate shapes still line up with what they cover.
            live.Timeline.ZoomRegions = new[] { new ZoomRegion(0, 6, .3, .35, 2, ZoomPreset.BuiltIns[2].Curve) };
            live.SeekTo(2); await Task.Delay(900); standIn.RenderTransform = live.Player.RenderTransform; standIn.Clip = live.Player.Clip; await Shot(live, "parts-preview-zoomed.png"); standIn.RenderTransform = Transform.Identity; standIn.Clip = null;
            live.Timeline.ZoomRegions = Array.Empty<ZoomRegion>(); live.SeekTo(2); await Task.Delay(300);
            // Cropping the inset video on the preview.
            typeof(TrimWindow).GetMethod("OpenOverlay", flags)!.Invoke(live, new object[] { 2 });
            view.SetCropping(true); await Task.Delay(300);
            Check(view.Cropping, "Videos crop on the preview like pictures");
            await Shot(live, "parts-preview-crop.png"); view.SetCropping(false);
            // Typing text right on the video.
            typeof(TrimWindow).GetMethod("EditTextOnVideo", flags)!.Invoke(live, new object[] { 3 });
            var editor = (System.Windows.Controls.TextBox)typeof(TrimWindow).GetField("inlineEditor", flags)!.GetValue(live)!;
            editor.Text = "TYPED HERE"; await Task.Delay(300);
            await Shot(live, "parts-preview-typing.png");
            Check(live.Timeline.Overlays[3].Text == "TYPED HERE", "Text typed on the video updates the caption as it's typed");
            typeof(TrimWindow).GetMethod("CloseInlineEditor", flags)!.Invoke(live, null);
            // The shape panel.
            typeof(TrimWindow).GetMethod("OpenOverlay", flags)!.Invoke(live, new object[] { 0 });
            await Task.Delay(300); await Shot(live, "parts-panel-shape.png");
            typeof(TrimWindow).GetMethod("OpenOverlay", flags)!.Invoke(live, new object[] { 2 });
            await Task.Delay(300); await Shot(live, "parts-panel-video.png");
            // Drawing: a freehand loop, a straight arrow and joined lines, as the draw tool makes them.
            var drawn = new[]
            {
                (OverlayLayer.DrawTool.Freehand, Enumerable.Range(0, 40).Select(i => new Point(160 + 90 * Math.Cos(i * Math.PI / 20), 200 + 70 * Math.Sin(i * Math.PI / 20) + (i % 3) * 2)).ToArray(), true),
                (OverlayLayer.DrawTool.Line, new[] { new Point(300, 60), new Point(470, 150) }, false),
                (OverlayLayer.DrawTool.Corners, new[] { new Point(380, 300), new Point(450, 230), new Point(520, 290), new Point(600, 220) }, false),
            };
            var start = live.Timeline.Overlays.ToList();
            foreach (var (tool, points, closed) in drawn)
            {
                var blank = OverlayItem.NewShape(0, 6, null) with { Layer = start.Count };
                start.Add(blank);
                typeof(TrimWindow).GetMethod("SetOverlays", flags)!.Invoke(live, new object[] { start.ToArray() });
                typeof(TrimWindow).GetMethod("OpenOverlay", flags)!.Invoke(live, new object[] { start.Count - 1 });
                typeof(TrimWindow).GetMethod("ShapeDrawn", flags)!.Invoke(live, new object[] { tool, points, closed });
                start = live.Timeline.Overlays.ToList();
            }
            int loop = start.Count - 3, arrow = start.Count - 2, joined = start.Count - 1;
            start[loop] = start[loop] with { ShapeColor = "#00FF3B30", ShapeBorderColor = "#FFFFE600", ShapeBorderWidth = 8, Dash = OverlayDash.Dotted };
            start[arrow] = start[arrow] with { ShapeColor = "#FF00E5FF", LineWidth = 12, Dash = OverlayDash.Dashed };
            start[joined] = start[joined] with { ShapeColor = "#FFFFFFFF", LineWidth = 8, StartCap = OverlayCap.Dot, EndCap = OverlayCap.Arrow };
            typeof(TrimWindow).GetMethod("SetOverlays", flags)!.Invoke(live, new object[] { start.ToArray() });
            Check(start[loop] is { Shape: OverlayShape.Freehand, Closed: true } && start[arrow] is { Shape: OverlayShape.Polyline, IsLine: true, EndCap: OverlayCap.Arrow, Points.Count: 2 }
                && start[joined] is { Shape: OverlayShape.Polyline, IsLine: true, Points.Count: 4 }, "Drawing makes a closed freehand shape, a straight arrow and joined lines");
            Check(start[loop].Points.Count < 40 && start[loop].Points.Count >= 8, $"Freehand strokes are thinned to the points that shape them ({start[loop].Points.Count} of 40)");
            // The line's box fits its corners, so its handles and click area cover it.
            var fitted = start[joined].Points;
            Check(fitted.Min(p => p.X) > -.51 && fitted.Max(p => p.X) < .51 && fitted.Min(p => p.Y) > -.51 && fitted.Max(p => p.Y) < .51, "A drawn line's box fits around it");
            typeof(TrimWindow).GetMethod("OpenOverlay", flags)!.Invoke(live, new object[] { joined });
            await Shot(live, "parts-drawing.png");
            var capped = OverlayRenderer.Content(start[joined], 0, 0, 16.0 / 9).Bounds; var plainEnds = OverlayRenderer.Content(start[joined] with { StartCap = OverlayCap.None, EndCap = OverlayCap.None }, 0, 0, 16.0 / 9).Bounds;
            Check(capped.Width > plainEnds.Width + 4 || capped.Height > plainEnds.Height + 4, $"Line ends draw arrows and dots ({plainEnds.Size} → {capped.Size})");            // Every drawn thing is drawn into the export the same way.
            foreach (var i in new[] { loop, arrow, joined })
                Check(!OverlayRenderer.Content(start[i], 0, 0, 16.0 / 9).Bounds.IsEmpty, $"{start[i].Label} renders for the export");            // Sections move by their chips; adding a clip keeps every edit and continues the timeline.
            live.StartBox.Text = "1"; live.EndBox.Text = "2"; typeof(TrimWindow).GetMethod("Add_Click", flags)!.Invoke(live, new object[] { live, new RoutedEventArgs() });
            live.StartBox.Text = "4"; live.EndBox.Text = "5"; typeof(TrimWindow).GetMethod("Add_Click", flags)!.Invoke(live, new object[] { live, new RoutedEventArgs() });
            live.MoveSection(1, 0);
            var order = live.SectionsList.Items.Cast<KeepSection>().ToList();
            Check(order.Count == 2 && order[0].Start == 4 && order[1].Start == 1, "Dragging a section's chip changes the order sections play in");
            int overlaysBefore = live.Timeline.Overlays.Count; var firstOverlay = live.Timeline.Overlays[0];
            Check(await live.AddClipAsync(inset), "A clip is added onto the end of the timeline");
            Check(Math.Abs(live.Timeline.Duration - 12) < .3 && live.Timeline.Overlays.Count == overlaysBefore && live.Timeline.Overlays[0].Start == firstOverlay.Start
                && live.SectionsList.Items.Count == 3 && live.SectionsList.Items.Cast<KeepSection>().Last().Start >= 5.9, "After adding a clip every edit keeps its place, and the new part joins as a section");
            await Task.Delay(600); await Shot(live, "parts-added-clip.png");            // A zoomed-in timeline keeps its layers lined up with the video track.
            for (int i = 0; i < 3; i++) typeof(TrimWindow).GetMethod("ZoomIn_Click", flags)!.Invoke(live, new object[] { live, new RoutedEventArgs() });
            await Task.Delay(300); await Shot(live, "parts-timeline-zoomed.png");
        }
        finally { live.Close(); }
    }
    // A picture of the window's content. Windows video can't be captured this way, so the test shows a
    // still frame of the clip in the player's place (and blur and pixelate shapes treat that).
    private static void ScreenShot(Window w, string name)
    {
        var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        SystemTestNative.GetWindowRect(h, out var r);
        using var bmp = new System.Drawing.Bitmap(r.Right - r.Left, r.Bottom - r.Top);
        // Without a visible desktop (a locked or background session) there is nothing to grab.
        try { using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(r.Left, r.Top, 0, 0, bmp.Size); }
        catch (System.ComponentModel.Win32Exception) { return; }
        bmp.Save(Path.Combine(Storage.Root, name), System.Drawing.Imaging.ImageFormat.Png);
    }
    private static async Task Shot(Window w, string name)
    {
        var content = (FrameworkElement)w.Content; w.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(Storage.Root, name))) png.Save(file);
        // Then the real window on screen, with the real video under the shapes.
        if (current is { } c)
        {
            c.StandIn.Visibility = Visibility.Collapsed; c.Window.OverlayView.Source = c.Window.Player;
            w.Topmost = true; w.Activate(); await Task.Delay(600);
            ScreenShot(w, name.Replace(".png", "-screen.png"));
            w.Topmost = false; c.StandIn.Visibility = Visibility.Visible; c.Window.OverlayView.Source = c.StandIn;
        }
    }
    private static (TrimWindow Window, UIElement StandIn)? current;
}
