using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Flashback;

// Times the trimmer's busiest paths with a realistic edit: a 1080p60 clip with audio lanes open,
// speed and zoom parts, and several text and picture items (one a GIF). Writes trim-performance.txt.
internal static class TrimPerformanceDiagnostics
{
    internal static async Task RunAsync()
    {
        Directory.CreateDirectory(Storage.Root);
        string clip = Path.Combine(Storage.Root, "perf-clip.mp4"), gif = Path.Combine(Storage.Root, "perf.gif"), png = Path.Combine(Storage.Root, "perf.png");
        await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=60:duration=20", "-f", "lavfi", "-i", "sine=frequency=300:sample_rate=48000:duration=20",
            "-c:v", "libx264", "-preset", "ultrafast", "-threads", "4", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", clip);
        await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "testsrc=size=400x300:rate=20:duration=3", gif);
        await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "color=white:s=1200x900,drawbox=x=300:y=200:w=600:h=500:color=0x3366FF:t=fill", "-frames:v", "1", png);
        Storage.Save(new Settings { OutputFolder = Path.Combine(Storage.Root, "clips") });
        var report = new StringBuilder();
        var open = Stopwatch.StartNew();
        var trim = new TrimWindow(clip, true) { Width = 1280, Height = 900 };
        report.AppendLine($"Open trimmer with a clip: {open.ElapsedMilliseconds} ms");
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        try
        {
            var content = (FrameworkElement)trim.Content;
            content.Measure(new Size(1280, 900)); content.Arrange(new Rect(0, 0, 1280, 900)); content.UpdateLayout();
            report.AppendLine($"First layout: {open.ElapsedMilliseconds} ms");
            var panel = Stopwatch.StartNew();
            typeof(TrimWindow).GetMethod("BuildOverlayPanel", flags)!.Invoke(trim, new object[] { OverlayKind.Text });
            report.AppendLine($"Build the text panel: {panel.ElapsedMilliseconds} ms");
            var tl = trim.Timeline;
            // Audio lanes open with real waveforms.
            await (Task)typeof(TrimWindow).GetMethod("LoadWaveformsAsync", flags)!.Invoke(trim, new object[] { System.Threading.CancellationToken.None })!;
            tl.LanesExpanded = true;
            var sw = Stopwatch.StartNew();
            void Settle() { content.UpdateLayout(); }
            double Time(int count, Action<int> step) { Settle(); var t = Stopwatch.StartNew(); for (int i = 0; i < count; i++) { step(i); Settle(); } return t.Elapsed.TotalMilliseconds / count; }

            typeof(TrimWindow).GetMethod("SlowAdded", flags)!.Invoke(trim, new object[] { 2.0, 5.0 });
            typeof(TrimWindow).GetMethod("ZoomAdded", flags)!.Invoke(trim, new object[] { 4.0, 9.0 });
            var add = typeof(TrimWindow).GetMethod("AddOverlay", flags)!;
            add.Invoke(trim, new object[] { OverlayItem.NewText(1, 12, null) with { Text = "BIG MEME CAPTION\nsecond line", Shape = OverlayShape.Bubble, Shadow = new OverlayShadow(true), In = OverlayMotion.Typewriter, InLength = 1 } });
            add.Invoke(trim, new object[] { OverlayItem.NewText(3, 15, null) with { Text = "Subtitle bar", Shape = OverlayShape.Lines, Y = .9, Fill2 = "#FFE600" } });
            add.Invoke(trim, new object[] { OverlayItem.NewImage(2, 14, gif) with { X = .8, Y = .3, Key = OverlayKey.White } });
            add.Invoke(trim, new object[] { OverlayItem.NewImage(0, 18, png) with { X = .2, Y = .3, Scale = .5, ImageCorner = 20, BorderWidth = 6, Shadow = new OverlayShadow(true), Rotation = 12 } });
            typeof(TrimWindow).GetMethod("CloseOverlay", flags)!.Invoke(trim, null);
            report.AppendLine($"Setup: {sw.ElapsedMilliseconds} ms");

            // Playback ticks: the playhead moves a frame at a time through everything.
            var setPlayhead = typeof(TrimWindow).GetMethod("SetPlayhead", flags)!;
            double tick = Time(600, i => setPlayhead.Invoke(trim, new object[] { i / 60.0 * 1.5 }));
            report.AppendLine($"Playback tick (playhead + timeline + preview layers): {tick:0.000} ms");
            // Scrubbing: jumps across the clip.
            var rng = new Random(1);
            double scrub = Time(300, _ => trim.SeekTo(rng.NextDouble() * 18));
            report.AppendLine($"Scrub seek: {scrub:0.000} ms");
            // Editing the selected text's style from the panel (slider drag).
            typeof(TrimWindow).GetMethod("OpenOverlay", flags)!.Invoke(trim, new object[] { 0 });
            var edit = typeof(TrimWindow).GetMethod("EditOverlay", flags)!;
            double slider = Time(200, i => edit.Invoke(trim, new object[] { "size", (Func<OverlayItem, OverlayItem>)(o => o with { Size = 60 + i % 40 }) }));
            report.AppendLine($"Panel slider edit (size): {slider:0.000} ms");
            double opacity = Time(200, i => edit.Invoke(trim, new object[] { "opacity", (Func<OverlayItem, OverlayItem>)(o => o with { Opacity = .5 + i % 50 / 100.0 }) }));
            report.AppendLine($"Panel slider edit (opacity): {opacity:0.000} ms");
            // Dragging text around on the preview: each move updates the item and the panel.
            var replace = typeof(TrimWindow).GetMethod("ReplaceOverlay", flags)!; var loadUi = typeof(TrimWindow).GetMethod("LoadOverlayUi", flags)!;
            double drag = Time(200, i => { replace.Invoke(trim, new object[] { 0, tl.Overlays[0] with { X = .3 + i % 40 / 100.0 } }); loadUi.Invoke(trim, null); });
            report.AppendLine($"Preview drag (move text): {drag:0.000} ms");
            // Zoom target drags redraw the timeline too.
            var zoomChanged = typeof(TrimWindow).GetMethod("ZoomTargetChanged", flags)!;
            typeof(TrimWindow).GetMethod("OpenZoom", flags)!.Invoke(trim, new object[] { 0 });
            double zoomDrag = Time(200, i => { trim.ZoomTarget.Set(.3 + i % 40 / 100.0, .5, 2); zoomChanged.Invoke(trim, null); });
            report.AppendLine($"Zoom target drag: {zoomDrag:0.000} ms");
            typeof(TrimWindow).GetMethod("CloseZoom", flags)!.Invoke(trim, null);
            // Hover hit-testing over the preview.
            var view = trim.OverlayView;
            double hover = Time(500, i => VisualTreeHelper.HitTest(view, new Point(i % 400 + 200, 200 + i % 300)));
            report.AppendLine($"Preview hover hit test: {hover:0.000} ms");
            report.AppendLine($"Managed memory: {GC.GetTotalMemory(true) / 1048576.0:0.0} MiB, working set {Process.GetCurrentProcess().WorkingSet64 / 1048576.0:0.0} MiB");
        }
        finally { trim.Close(); }
        // Opening it again in the same session, as happens after the main window has started.
        var reopen = Stopwatch.StartNew();
        var again = new TrimWindow(clip, true) { Width = 1280, Height = 900 };
        var againContent = (FrameworkElement)again.Content;
        againContent.Measure(new Size(1280, 900)); againContent.Arrange(new Rect(0, 0, 1280, 900)); againContent.UpdateLayout();
        report.AppendLine($"Reopen trimmer with a clip, laid out: {reopen.ElapsedMilliseconds} ms");
        var load = Stopwatch.StartNew(); ClipMedia.Read(clip); report.AppendLine($"Read clip header: {load.Elapsed.TotalMilliseconds:0.0} ms");
        again.Close();
        File.WriteAllText(Path.Combine(Storage.Root, "trim-performance.txt"), report.ToString());
    }
}
