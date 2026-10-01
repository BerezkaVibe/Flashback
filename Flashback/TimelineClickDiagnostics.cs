using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace Flashback;

// --timeline-click-test: clicks the timeline's editable parts with the real mouse, quickly and without holding
// the button, and checks that each one's settings open and stay open. (A settings window that opened when the
// button went down used to close again as it came up, so it only showed while the button was held.)
internal static class TimelineClickDiagnostics
{
    internal static async Task RunAsync()
    {
        Directory.CreateDirectory(Storage.Root);
        var report = new StringBuilder(); int failures = 0;
        void Check(bool ok, string text) { report.AppendLine((ok ? "PASS " : "FAIL ") + text); if (!ok) failures++; File.WriteAllText(Path.Combine(Storage.Root, "timeline-click-results.txt"), report.ToString()); }
        var source = Path.Combine(Storage.Root, "Source.mp4");
        await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30:duration=12", "-f", "lavfi", "-i", "sine=frequency=500:sample_rate=48000:duration=12",
            "-c:v", "libx264", "-threads", "2", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", source);
        var music = Path.Combine(Storage.Root, "Tune.m4a");
        await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "sine=frequency=2000:sample_rate=48000:duration=3", "-c:a", "aac", music);
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, flags)!.Invoke(target, args);
        object? Field(object target, string name) => target.GetType().GetField(name, flags)!.GetValue(target);
        SystemTestNative.GetCursorPos(out var oldCursor);
        var trim = new TrimWindow(source, true) { Width = 1400, Height = 900, Topmost = true };
        try
        {
            trim.Show(); trim.Activate(); SystemTestNative.SetForegroundWindow(new WindowInteropHelper(trim).Handle);
            var tl = trim.Timeline; tl.LanesExpanded = true;
            await Task.Delay(800);
            double scale = PresentationSource.FromVisual(tl)!.CompositionTarget.TransformToDevice.M22;
            // A real click: move there, press, let go straight away (a tap), then give the window time to react.
            async Task Click(Point inTimeline, int holdMs = 50)
            {
                var screen = tl.PointToScreen(inTimeline);
                // The real mouse: if a hand moves it meanwhile the click would land elsewhere, so put it back and look.
                for (int tries = 0; ; tries++)
                {
                    SystemTestNative.SetCursorPos((int)screen.X, (int)screen.Y); await Task.Delay(120);
                    SystemTestNative.GetCursorPos(out var now);
                    if (Math.Abs(now.X - screen.X) <= 2 && Math.Abs(now.Y - screen.Y) <= 2) break;
                    if (tries >= 5) throw new InvalidOperationException("The mouse kept being moved by someone, so the click test could not run. Leave the mouse alone while it runs.");
                }
                SystemTestNative.MouseButton(true); await Task.Delay(holdMs); SystemTestNative.MouseButton(false);
                await Task.Delay(500);
            }
            async Task Clear() { SystemTestNative.SetCursorPos((int)tl.PointToScreen(new Point(5, 5)).X, (int)tl.PointToScreen(new Point(5, 5)).Y + 400); await Task.Delay(100); }
            Point Center(Rect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

            // Speed part
            Call(trim, "SlowAdded", 2.0, 4.0); await Task.Delay(400);
            var slowTags = (List<Rect>)Field(tl, "slowTags")!;
            await Click(Center(slowTags[0]));
            Check(tl.SelectedSlow == 0 && trim.SlowPopup.IsOpen, $"A quick click on a speed part's tag opens its settings and they stay (selected {tl.SelectedSlow}, open {trim.SlowPopup.IsOpen})");
            trim.SlowPopup.IsOpen = false; await Clear();

            // Cut-out and volume part, on the audio lane
            Call(trim, "CutAdded", new CutRegion(0, 6, 7)); await Task.Delay(400);
            var cutTags = (System.Collections.IList)Field(tl, "cutTags")!;
            var cutTag = (Rect)cutTags[0]!.GetType().GetField("Item1")!.GetValue(cutTags[0])!;
            await Click(Center(cutTag));
            Check(trim.CutPopup.IsOpen, $"A quick click on a cut-out's tag opens its settings and they stay (open {trim.CutPopup.IsOpen})");
            trim.CutPopup.IsOpen = false; await Clear();
            Call(trim, "VolumeAdded", 0, 8.0, 10.0); await Task.Delay(400);
            var volumeTags = (System.Collections.IList)Field(tl, "volumeTags")!;
            var volumeTag = (Rect)volumeTags[0]!.GetType().GetField("Item1")!.GetValue(volumeTags[0])!;
            await Click(Center(volumeTag));
            Check(trim.VolumePopup.IsOpen, $"A quick click on a volume part's tag opens its settings and they stay (open {trim.VolumePopup.IsOpen})");
            trim.VolumePopup.IsOpen = false; await Clear();

            // Zoom part: its settings are a panel beside the preview
            Call(trim, "ZoomAdded", 1.0, 3.0); await Task.Delay(400);
            var zoomTags = (List<Rect>)Field(tl, "zoomTags")!;
            await Click(Center(zoomTags[0]));
            Check(trim.ZoomPanel.Visibility == Visibility.Visible, $"A quick click on a zoom's tag opens its settings (panel {trim.ZoomPanel.Visibility})");

            // A sound, a freeze frame
            Call(trim, "SetSounds", (IReadOnlyList<SoundItem>)new[] { new SoundItem(music, 1, 3) }); await Task.Delay(600);
            var bar = (Rect)Call(tl, "SoundRect", tl.Sounds[0])!;
            await Click(Center(bar));
            Check(trim.SoundPopup.IsOpen, $"A quick click on a sound opens its settings and they stay (open {trim.SoundPopup.IsOpen})");
            trim.SoundPopup.IsOpen = false; await Clear();
        }
        finally { SystemTestNative.SetCursorPos(oldCursor.X, oldCursor.Y); trim.Close(); }
        if (failures > 0) throw new Exception($"{failures} timeline click checks failed; see timeline-click-results.txt");
    }
}
