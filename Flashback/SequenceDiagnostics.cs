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
using System.Windows.Media.Imaging;

namespace Flashback;

// The finished-video timeline: the mapping between the recording and the finished video, parts sliding
// with their footage as freezes come and go, sounds under holds and at their own speed in real exports,
// and the live editor playing the finished video across holds and section jumps.
internal static class SequenceDiagnostics
{
    internal static async Task RunAsync()
    {
        Directory.CreateDirectory(Storage.Root);
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAILED: " + name);
            checks.Add(name); File.WriteAllText(Path.Combine(Storage.Root, "sequence-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        }
        static bool Near(double a, double b, double within = 1e-6) => Math.Abs(a - b) <= within;

        // ---- The mapping ----
        // Sections 3-6 s then 0-2 s; 4-5 s plays at half speed; freezes at 3.5 s (1 s) and 1 s (2 s).
        // Finished: 3-3.5 | hold 1 | 3.5-4 | 4-5 over 2 s | 5-6 | 0-1 | hold 2 | 1-2 = 9 s.
        var ranges = new[] { new KeepSection(3, 6), new KeepSection(0, 2) };
        var options = new ShareExportOptions { SlowRegions = new[] { new SpeedRegion(4, 5, .5) }, Freezes = new[] { new FreezeFrame(1, 2), new FreezeFrame(3.5, 1) } };
        var map = new SequenceMap(ranges, options);
        Check(Near(map.Total, 9) && Near(map.Total, options.OutputSeconds(ranges)), $"The finished video is as long as the export ({map.Total:0.##} s)");
        Check(Near(map.ToOutput(3.2), .2) && Near(map.ToOutput(3.5), .5) && Near(map.ToOutput(3.6), 1.6) && Near(map.ToOutput(4.5), 3) && Near(map.ToOutput(.5), 5.5) && Near(map.ToOutput(1), 6) && Near(map.ToOutput(1.5), 8.5),
            "Moments of the recording land where they play: after holds, stretched by slow parts, in section order");
        Check(map.ToSource(6.7) is var (heldAt, into) && Near(heldAt, 1) && Near(into, .7) && map.ToSource(3) is var (slowAt, none) && Near(slowAt, 4.5) && none == 0,
            "A moment of the finished video shows the right frame, and how far into a hold it is");
        Check(map.ToSource(8) is var (endAt, endHold) && Near(endAt, 1) && Near(endHold, 2), "The very end of a hold is still the held frame, fully held");
        Check(new[] { 3.1, 3.7, 4.2, 4.9, 5.5, .3, 1.4, 1.9 }.All(t => Near(map.ToSource(map.ToOutput(t)).Source, t)), "Recording → finished → recording comes back to the same moment");
        var spans = map.Spans(3.2, 3.8);
        Check(spans.Count == 1 && Near(spans[0].From, .2) && Near(spans[0].To, 1.8), "A part around a freeze covers its whole hold");
        spans = map.Spans(1.5, 3.5);
        Check(spans.Count == 2 && Near(spans[0].From, 0) && Near(spans[0].To, .5) && Near(spans[1].From, 8.5) && Near(spans[1].To, 9), "A part across a section jump is drawn in two places");
        Check(Near(map.SourceIn(1, 9), 2) && Near(map.SourceIn(0, 5), 6) && map.SectionAt(4.5) == 0 && map.SectionAt(5.5) == 1, "The end of a section stays in that section");

        // ---- Parts partway through a freeze's hold ----
        // A freeze at 2 s holds 3 s. Part A lives 1-2 s into the hold; B runs from 1 s into the hold's first
        // 1.5 s; C spans the hold (frozen unless it keeps going).
        var holds = new[] { new FreezeFrame(2, 3) };
        Moment M(double at, double hold = 0) => new(at, hold);
        Check(HoldTiming.Compare(M(2), M(2, 1)) < 0 && HoldTiming.Compare(M(2, 3), M(2.1)) < 0 && HoldTiming.Compare(M(2, 1), M(2, 1)) == 0, "Moments inside a hold come after its moment and before the footage after it");
        Check(HoldTiming.Covers(M(2, 1), M(2, 2), M(2, 1.5)) && !HoldTiming.Covers(M(2, 1), M(2, 2), M(2, .5)) && !HoldTiming.Covers(M(2, 1), M(2, 2), M(2, 2)), "A part inside a hold shows only for its stretch of it");
        Check(HoldTiming.Covers(M(1), M(2), M(1.9)) && !HoldTiming.Covers(M(1), M(2), M(2)) && HoldTiming.Covers(M(1), M(2.5), M(2, 2.9)), "A part ending at a freeze's moment stops before the hold; one ending after it covers the hold");
        Check(Near(HoldTiming.Clock(M(1), M(2, 1.5), M(2, .5), holds, false), 1.5) && Near(HoldTiming.Duration(M(1), M(2, 1.5), holds, false), 2.5) && Near(HoldTiming.Duration(M(2, 1), M(2, 2), holds, false), 1),
            "A part's own clock runs through a hold it starts or stops in");
        Check(Near(HoldTiming.Clock(M(1), M(3), M(2, 2), holds, false), 1) && Near(HoldTiming.Clock(M(1), M(3), M(2, 2), holds, true), 3) && Near(HoldTiming.Duration(M(1), M(3), holds, true), 5),
            "A part spanning a hold stands still in it, unless it keeps going through freezes");
        Check(HoldTiming.Overlaps(M(2, 1), M(2, 2), M(1), M(2, 1.5)) && !HoldTiming.Overlaps(M(2, 1), M(2, 2), M(1), M(2, 1)), "Parts overlap only where they share hold time");
        var oldZoom = JsonSerializer.Deserialize<ZoomRegion>("{\"Start\":1,\"End\":2,\"X\":0.5,\"Y\":0.5,\"MaxZoom\":2,\"In\":{\"Points\":[{\"Item1\":0,\"Item2\":0},{\"Item1\":0.5,\"Item2\":1}]}}", new JsonSerializerOptions { IncludeFields = true })!;
        Check(oldZoom.ThroughFreezes && oldZoom.StartHold == 0 && new OverlayItem().ThroughFreezes, "Zooms and videos saved before keep going through freezes; nothing else changes");
        var holdMap = new SequenceMap(new[] { new KeepSection(0, 6) }, new ShareExportOptions { Freezes = holds });
        var inside = holdMap.Spans(M(2, 1), M(2, 2));
        Check(inside.Count == 1 && Near(inside[0].From, 3) && Near(inside[0].To, 4) && Near(holdMap.Spans(M(1), M(2, 1.5))[0].To, 3.5), "The finished video shows a part just where it is in the hold");

        // ---- Everything slides with the video ----
        var folder = Path.Combine(Storage.Root, "sequence"); Directory.CreateDirectory(folder);
        var source = Path.Combine(folder, "Source.mp4");
        await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30:duration=10", "-f", "lavfi", "-i", "sine=frequency=500:sample_rate=48000:duration=10",
            "-c:v", "libx264", "-threads", "2", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", source);
        var music = Path.Combine(folder, "Tune.m4a");
        await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "sine=frequency=2000:sample_rate=48000:duration=6", "-c:a", "aac", music);
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, flags)!.Invoke(target, args);
        var trim = new TrimWindow(source, true) { Width = 1400, Height = 900 };
        try
        {
            var content = (FrameworkElement)trim.Content;
            void Layout() { content.Measure(new Size(1400, 900)); content.Arrange(new Rect(0, 0, 1400, 900)); content.UpdateLayout(); }
            Layout();
            var tl = trim.Timeline;
            // The preview volume: 100% is how it always played, 200% twice that, the speaker mutes.
            Check(Near(trim.Player.Volume, .5) && trim.PreviewVolumeLabel.Text == "100%", "The preview volume starts at 100%, as loud as it has always played");
            trim.PreviewVolumeSlider.Value = 200;
            Check(Near(trim.Player.Volume, 1) && Near(trim.OverlayView.Loudness, 1) && trim.PreviewVolumeLabel.Text == "200%", "The preview volume boosts to 200%");
            trim.PreviewVolumeSlider.Value = 102;
            Check(trim.PreviewVolumeSlider.Value == 100, "The preview volume clicks into place at 100%");
            Call(trim, "PreviewMute_Click", trim, new RoutedEventArgs());
            Check(trim.Player.Volume == 0 && (string)trim.PreviewVolumeIcon.Content == "", "The speaker button mutes the preview");
            Call(trim, "PreviewMute_Click", trim, new RoutedEventArgs());
            await Shot(content, "sequence-volume.png");
            tl.ZoomRegions =new[] { new ZoomRegion(6, 7, .5, .5, 2, ZoomPreset.BuiltIns[2].Curve) };
            Call(trim, "SetSounds", (IReadOnlyList<SoundItem>)new[] { new SoundItem(music, 6, 7) });
            trim.ToggleFinishedView(); Layout();
            Check(tl.Finished && Near(tl.Total, 10), "The finished view turns on, as long as the clip with nothing changed");
            double zoomAt = tl.ToView(6), end = tl.Total;
            Call(trim, "AddFreeze", 3.0, 10.0, true); Layout();
            Check(Near(tl.Total, end + 10) && Near(tl.ToView(6), zoomAt + 10), $"A 10 s freeze makes the finished video 10 s longer and slides what's after it along ({tl.ToView(6):0.##} s)");
            var rect = (Rect)Call(tl, "SoundRect", trim.Timeline.Sounds[0])!;
            double perSecond = (tl.ActualWidth - 40) / tl.Total;
            Check(Near(rect.X, 20 + 16 * perSecond, 1) && Near(rect.Width, perSecond, 1), "Music slides with its footage and keeps its length");
            await Shot(content, "sequence-freeze.png");
            // Dragging the end of the hold (as the timeline reports it) changes how long it holds.
            var holdChanged = (Action<int, double>)typeof(TrimTimeline).GetField("FreezeHoldChanged", flags)!.GetValue(tl)!;
            holdChanged(0, 4); Layout();
            Check(Near(tl.Freezes[0].Seconds, 4) && Near(tl.Total, end + 4) && Near(tl.ToView(6), zoomAt + 4), "Dragging a hold's end slit shortens it, and everything after slides back");
            Call(trim, "RemoveFreeze", 0); Layout();
            Check(Near(tl.Total, end) && Near(tl.ToView(6), zoomAt), "Removing the freeze shrinks the finished video back");
            // Parked inside a hold: the playhead sits partway along it on the frozen frame.
            Call(trim, "AddFreeze", 3.0, 2.0, true); Layout(); tl.Fit(); Layout();
            double X(double view) => 20 + view / tl.Total * (tl.ActualWidth - 40);
            tl.MoveTo(X(4.2));
            Check(Near(trim.Playhead, 3, .02) && Near(tl.HoldOffset, 1.2, .05) && Near(tl.PositionView, 4.2, .05), $"Clicking inside a hold parks on the frozen frame that far in ({tl.HoldOffset:0.00} s)");
            Call(trim, "StepView", 1.0);
            // The hold runs 3-5 s of the finished video, so 5.2 s is 3.2 s of the recording.
            Check(Near(tl.PositionView, 5.2, .05) && Near(trim.Playhead, 3.2, .05) && tl.HoldOffset == 0, $"Stepping a second moves a second along the finished video, out of the hold ({tl.PositionView:0.00} s, recording {trim.Playhead:0.00} s)");
            Call(trim, "SeekView", 3.5);
            Check(trim.PositionLabel.Text == $"{KeepSection.TimeText(3.5)} / {KeepSection.TimeText(tl.Total)}", $"The time shows the finished video's ({trim.PositionLabel.Text})");
            // Text placed with two clicks inside the hold (3-5 s of the finished video) keeps its place in it.
            double Track() => (double)typeof(TrimTimeline).GetProperty("TrackTop", flags)!.GetValue(tl)! + 12;
            var click = typeof(TrimTimeline).GetMethod("PlaceCutPoint", flags)!;
            tl.OverlayMode = OverlayKind.Text;
            click.Invoke(tl, new object[] { new Point(X(3.5), Track()) }); click.Invoke(tl, new object[] { new Point(X(4.5), Track()) }); Layout();
            var holdText = tl.Overlays.LastOrDefault();
            Check(holdText is { Kind: OverlayKind.Text } && Near(holdText.Start, 3) && Near(holdText.End, 3) && Near(holdText.StartHold, .5, .03) && Near(holdText.EndHold, 1.5, .03),
                $"Two clicks inside a hold add text that starts and stops partway through it ({holdText?.Start:0.##}+{holdText?.StartHold:0.##} → {holdText?.End:0.##}+{holdText?.EndHold:0.##})");
            var view = trim.OverlayView; view.Freezes = tl.Freezes; view.Time = 3;
            bool Shown(OverlayItem o) => (bool)typeof(OverlayLayer).GetMethod("Visible", flags)!.Invoke(view, new object[] { o })!;
            view.HoldTime = 1; bool mid = Shown(holdText!); view.HoldTime = .2; bool early = Shown(holdText!); view.HoldTime = 1.8; bool late = Shown(holdText!);
            Check(mid && !early && !late, "The preview shows it only during its stretch of the hold");
            await Shot(content, "sequence-hold-text.png");
            trim.ToggleFinishedView(); Layout();
            var chip = (Rect)Call(tl, "OverlayRect", holdText!)!;
            Check(chip.Width < 10 && Math.Abs(chip.X - tl.XAt(3)) < 12, $"In the whole recording, a part inside a hold is a small chip at the freeze ({chip.X:0}, {chip.Width:0} px)");
            trim.ToggleFinishedView(); Layout(); tl.Fit(); Layout();
            // A zoom placed inside the hold zooms only there.
            tl.ZoomMode = true;
            click.Invoke(tl, new object[] { new Point(X(3.2), Track()) }); click.Invoke(tl, new object[] { new Point(X(4.8), Track()) }); Layout();
            var holdZoom = tl.ZoomRegions.FirstOrDefault(z => z.InHolds());
            Check(holdZoom != null && holdZoom.ZoomAt(new Moment(3, 1.2), tl.Freezes) > 1.2 && holdZoom.ZoomAt(new Moment(3, .1), tl.Freezes) == 1, "A zoom placed inside a hold zooms in there");
            // Moving the freeze takes them along; removing it puts them into the footage at its moment.
            var freezeMoved = (Action<int, double>)typeof(TrimTimeline).GetField("FreezeMoved", flags)!.GetValue(tl)!;
            freezeMoved(0, 3.4);
            Check(tl.Overlays.Any(o => Near(o.Start, 3.4) && Near(o.StartHold, .5, .03)) && tl.ZoomRegions.Any(z => Near(z.Start, 3.4) && z.InHolds()), "Moving a freeze takes the parts in its hold with it");
            Call(trim, "RemoveFreeze", 0); Layout();
            Check(tl.Overlays.Any(o => Near(o.Start, 3.4) && Near(o.End, 4.4, .03) && !o.InHolds()), "Removing the freeze puts its text into the footage there, as long as it was");
            Call(trim, "SetOverlays", (object)Array.Empty<OverlayItem>()); tl.ZoomRegions = tl.ZoomRegions.Where(z => !Near(z.Start, 3.4)).ToArray();
            Call(trim, "AddFreeze", 3.0, 2.0, true); Layout(); tl.Fit(); Layout();
            // A new part can't reach across a jump between sections.
            trim.StartBox.Text = "6"; trim.EndBox.Text = "9"; Call(trim, "Add_Click", trim, new RoutedEventArgs());
            trim.StartBox.Text = "0"; trim.EndBox.Text = "2"; Call(trim, "Add_Click", trim, new RoutedEventArgs());
            trim.MoveSection(1, 0); Layout(); tl.Fit(); Layout();
            // (The freeze at 3 s isn't in either section, so it isn't in the finished video.)
            Check(tl.Sections[0].Start == 6 && Near(tl.Total, 3 + 2), $"Reordered sections play in order in the finished view ({tl.Total:0.##} s)");
            double trackTop = (double)typeof(TrimTimeline).GetProperty("TrackTop", flags)!.GetValue(tl)!;
            Check((int)Call(tl, "FreezeAt", new Point(tl.XAt(3), trackTop + 12))! == -1, "A freeze outside the kept sections isn't shown or grabbable in the finished view");
            tl.CutMode = true;
            var place =typeof(TrimTimeline).GetMethod("PlaceCutPoint", flags)!;
            place.Invoke(tl, new object[] { new Point(tl.XAt(8), 26) }); place.Invoke(tl, new object[] { new Point(tl.XAt(1), 26) });
            var cut = tl.Cuts[^1];
            Check(Near(cut.Start, 8, .05) && Near(cut.End, 9, .05), $"A part placed across a section jump stops at the section's end ({cut.Start:0.##}-{cut.End:0.##})");
            tl.CutMode = false; await Shot(content, "sequence-sections.png");
            // Full screen: F11 hands the timeline and tools to a slim dock over the video; Esc gives them back.
            trim.HandleKey(Key.F11, ModifierKeys.None, true); Layout();
            Check(trim.IsFullscreen && trim.EditorMenu.Visibility != Visibility.Visible && ReferenceEquals(tl.Parent, trim.DockTimeline) && ReferenceEquals(trim.ListControls.Parent, trim.DockTools) && trim.WindowStyle == WindowStyle.None,
                "F11 goes full screen, with the timeline and tools in the dock");
            trim.UpdateDock(new Point(700, 900 - 20)); Layout();
            Check(trim.DockShown && trim.FullscreenDock.ActualHeight <= 96 && tl.OverlaysFolded | tl.Overlays.Count < 2, $"The dock shows near the bottom, and it's slim ({trim.FullscreenDock.ActualHeight:0} px)");
            await Shot(content, "sequence-fullscreen.png");
            trim.CutToolButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(tl.CutMode, "The dock's tool icons work");
            trim.HandleKey(Key.Escape, ModifierKeys.None, true);
            trim.UpdateDock(new Point(700, 100)); await Task.Delay(1700);
            Check(!tl.CutMode && !trim.DockShown && trim.IsFullscreen, $"Away from the bottom the dock fades away (Esc first put the tool away) [cut {tl.CutMode}, shown {trim.DockShown}, full {trim.IsFullscreen}, placing {tl.IsPlacing}, dragging {tl.IsDragging}, popups {trim.SlowPopup.IsOpen}{trim.FreezePopup.IsOpen}{trim.SpeedPopup.IsOpen}, over {trim.FullscreenDock.IsMouseOver}]");
            trim.HandleKey(Key.Escape, ModifierKeys.None, true); Layout();
            Check(!trim.IsFullscreen && trim.EditorMenu.Visibility == Visibility.Visible && ReferenceEquals(tl.Parent, trim.TrimContent) && ReferenceEquals(trim.ListControls.Parent, trim.ToolsHome) && trim.WindowStyle != WindowStyle.None,
                "Esc leaves full screen and puts everything back");
            trim.ToggleFinishedView(); Layout();
            Check(!tl.Finished && Near(tl.Total, 10), "Switching back shows the whole recording");
        }
        finally { trim.Close(); }

        // ---- Sounds in the export ----
        async Task<byte[]> Pcm(string file, double at, double length)
        {
            var raw = Path.Combine(folder, $"a-{Guid.NewGuid():N}.pcm");
            await EditorDiagnostics.Ffmpeg("-y", "-ss", ExportServices.Number(at), "-t", ExportServices.Number(length), "-i", file, "-ac", "1", "-ar", "48000", "-f", "s16le", raw);
            var bytes = File.ReadAllBytes(raw); File.Delete(raw); return bytes;
        }
        async Task<string> Export(string name, ShareExportOptions with)
        {
            var output = Path.Combine(folder, name + ".mp4"); if (File.Exists(output)) File.Delete(output);
            await ExportServices.PreciseAsync(source, output, new[] { new KeepSection(0, 6) }, null, CancellationToken.None, true, with);
            return output;
        }
        var freeze = new[] { new FreezeFrame(1, 2) };
        // Music that starts half a second into a hold plays there (the hold itself is silent).
        var underHold = await Export("Music in a hold", new ShareExportOptions { Freezes = freeze, Sounds = new[] { new SoundItem(music, 1, 2) { Hold = .5 } } });
        var inHold = await Pcm(underHold, 1.7, .6); var beforeHold = await Pcm(underHold, .3, .6);
        Check(Tone(inHold, 2000) > 1000 && Tone(beforeHold, 2000) < Tone(inHold, 2000) * .1, "Music placed during a freeze's hold plays in the hold");
        // Music anchored at 2 s plays at 2 s, and 2 s later once a freeze comes before it.
        var plain = await Export("Music anchored", new ShareExportOptions { Sounds = new[] { new SoundItem(music, 2, 3) } });
        var pushed = await Export("Music pushed by a freeze", new ShareExportOptions { Freezes = freeze, Sounds = new[] { new SoundItem(music, 2, 3) } });
        Check(Tone(await Pcm(plain, 2.2, .6), 2000) > 1000 && Tone(await Pcm(pushed, 4.2, .6), 2000) > 1000 && Tone(await Pcm(pushed, 2.2, .6), 2000) < 300,
            "A freeze before a sound pushes it later, so it stays with its footage");
        // Half speed like a tape: an octave down and twice as long. Keeping pitch: the same note, twice as long.
        var tape = await Export("Half speed", new ShareExportOptions { Sounds = new[] { new SoundItem(music, 0, 2) { Speed = .5, KeepPitch = false } } });
        var kept = await Export("Half speed kept pitch", new ShareExportOptions { Sounds = new[] { new SoundItem(music, 0, 2) { Speed = .5 } } });
        var tapeEarly = await Pcm(tape, .3, .8); var tapeLate = await Pcm(tape, 1.3, .5); var keptLate = await Pcm(kept, 1.3, .5);
        Check(Tone(tapeEarly, 1000) > Tone(tapeEarly, 2000) * 4 && Tone(tapeLate, 1000) > 1000, $"A sound at half speed without keeping pitch sounds an octave lower for twice as long ({Tone(tapeEarly, 1000):0} vs {Tone(tapeEarly, 2000):0})");
        Check(Tone(keptLate, 2000) > Tone(keptLate, 1000) * 4 && Tone(keptLate, 2000) > 1000, "Keeping its pitch, a slowed sound stays on the same note");
        // Where normal speed meets a slow part (and back), the clip's own sound carries on without a gap:
        // a steady tone, 2-3 s at half speed, so the joins are at 2 s and 4 s of the export.
        var slowed = await Export("Slow part joins", new ShareExportOptions { SlowRegions = new[] { new SpeedRegion(2, 3, .5) } });
        foreach (double join in new[] { 2.0, 4.0 })
        {
            var pcm = await Pcm(slowed, join - .3, .6);
            var (gap, where) = LongestQuiet(pcm);
            Check(gap <= 10, $"No gap in the sound where normal speed and a slow part meet at {join:0} s (quietest run {gap:0} ms at {join - .3 + where / 1000:0.000} s)");
        }
        // ---- Parts in freeze holds, in the export ----
        // A 3 s freeze at 2 s: the export holds from 2 s to 5 s, then carries on from 2 s of the recording.
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
        // White: the test pattern has no white, so it can only be the square.
        static bool White(byte[] p) => p[0] > 225 && p[1] > 225 && p[2] > 225;
        static double Change(byte[] a, byte[] b) => a.Zip(b).Average(p => Math.Abs(p.First - p.Second));
        var holdFreeze = new[] { new FreezeFrame(2, 3) };
        OverlayItem Square(double x) => OverlayItem.NewShape(0, 1, null) with { ShapeColor = "#FFFFFFFF", ShapeWidth = 240, ShapeHeight = 240, Corner = 0, X = x, Y = .5 };
        // Left: 1-2 s into the hold only. Right: from the footage until 1 s into the hold.
        var holdParts = await Export("Parts in a hold", new ShareExportOptions { Freezes = holdFreeze, Overlays = new[] { Square(.25) with { Start = 2, End = 2, StartHold = 1, EndHold = 2 }, Square(.75) with { Start = 0, End = 2, EndHold = 1, Layer = 1 } } });
        Check(Math.Abs(ClipMedia.Read(holdParts).Duration - 9) < .2, $"Parts in a hold don't change the export's length ({ClipMedia.Read(holdParts).Duration:0.00} s)");
        Check(!White(await Rgb(holdParts, 2.5, 160, 180)) && White(await Rgb(holdParts, 3.5, 160, 180)) && !White(await Rgb(holdParts, 4.5, 160, 180)), "A picture placed 1-2 s into a hold shows only then");
        Check(White(await Rgb(holdParts, 1, 480, 180)) && White(await Rgb(holdParts, 2.5, 480, 180)) && !White(await Rgb(holdParts, 3.5, 480, 180)), "A picture from the footage that ends 1 s into a hold goes then");
        // A zoom inside the hold, 0.5-2.5 s in: full frame before it, zoomed near its end, full frame after.
        var zoomInHold = await Export("Zoom in a hold", new ShareExportOptions { Freezes = holdFreeze, ZoomRegions = new[] { new ZoomRegion(2, 2, .5, .5, 2, ZoomCurve.Smooth) { StartHold = .5, EndHold = 2.5 } } });
        double zoomedApart = Change(await Gray(zoomInHold, 2.2, 0, 0, 640, 360), await Gray(zoomInHold, 4.2, 0, 0, 640, 360)), backApart = Change(await Gray(zoomInHold, 2.2, 0, 0, 640, 360), await Gray(zoomInHold, 4.8, 0, 0, 640, 360));
        Check(zoomedApart > 12 && backApart < 4 && Math.Abs(ClipMedia.Read(zoomInHold).Duration - 9) < .2, $"A zoom inside a hold zooms the frozen picture just there ({zoomedApart:0.0} zoomed, {backApart:0.0} after)");
        // A slow zoom spanning the freeze keeps zooming through the hold, unless that's turned off.
        var slowRamp = new ZoomCurve(new[] { (0.0, 0.0), (3.0, 1.0) });
        var through = await Export("Zoom through a freeze", new ShareExportOptions { Freezes = holdFreeze, ZoomRegions = new[] { new ZoomRegion(1.5, 5, .5, .5, 3, slowRamp) } });
        var still = await Export("Zoom held by a freeze", new ShareExportOptions { Freezes = holdFreeze, ZoomRegions = new[] { new ZoomRegion(1.5, 5, .5, .5, 3, slowRamp) { ThroughFreezes = false } } });
        double throughChange = Change(await Gray(through, 2.2, 0, 0, 640, 360), await Gray(through, 4.8, 0, 0, 640, 360)), stillChange = Change(await Gray(still, 2.2, 0, 0, 640, 360), await Gray(still, 4.8, 0, 0, 640, 360));
        Check(throughChange > 8 && stillChange < 3, $"A zoom keeps zooming through a freeze by default, and holds still when told to ({throughChange:0.0} vs {stillChange:0.0})");
        // A video keeps playing through the freeze, with its sound; turned off, it holds still and silent.
        var inset = Path.Combine(folder, "Inset.mp4");
        if (!File.Exists(inset))
            await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=30:duration=8", "-f", "lavfi", "-i", "sine=frequency=1500:sample_rate=48000:duration=8",
                "-c:v", "libx264", "-threads", "2", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", inset);
        var pip = OverlayItem.NewVideo(0, 6, inset, 320, 240) with { X = .5, Y = .5, Scale = .6, ImageCorner = 0 };
        var playing = await Export("Video through a freeze", new ShareExportOptions { Freezes = holdFreeze, Overlays = new[] { pip } });
        var heldVideo = await Export("Video held by a freeze", new ShareExportOptions { Freezes = holdFreeze, Overlays = new[] { pip with { ThroughFreezes = false } } });
        double playingChange = Change(await Gray(playing, 2.3, 290, 150, 60, 60), await Gray(playing, 4.7, 290, 150, 60, 60)), heldChange = Change(await Gray(heldVideo, 2.3, 290, 150, 60, 60), await Gray(heldVideo, 4.7, 290, 150, 60, 60));
        var playingSound = await Pcm(playing, 3, 1); var heldSound = await Pcm(heldVideo, 3, 1);
        Check(playingChange > 6 && heldChange < 3, $"A video keeps playing through a freeze by default, and holds still when told to ({playingChange:0.0} vs {heldChange:0.0})");
        Check(Tone(playingSound, 1500) > 300 && Tone(heldSound, 1500) < Tone(playingSound, 1500) * .1, $"Its sound plays through the hold too ({Tone(playingSound, 1500):0} vs {Tone(heldSound, 1500):0})");
        var project = TrimProject.Create(source, Array.Empty<KeepSection>(), 0, 6, 0, false) with { Sounds = new[] { new SoundItem(music, 0, 2) { Speed = 99, Hold = -3 } } };
        var cleaned = JsonSerializer.Deserialize<TrimProject>(JsonSerializer.Serialize(project))!;
        var cleanedSound = ((TrimProject)typeof(TrimProject).GetMethod("Cleaned", flags)!.Invoke(cleaned, null)!).Sounds![0];
        Check(cleanedSound.Speed == SoundItem.MaxSpeed && cleanedSound.Hold == 0 && JsonSerializer.Deserialize<SoundItem>("{\"Path\":\"a\",\"Start\":0,\"End\":1}")!.Speed == 1, "Sound speeds and holds are saved, checked, and default to normal for older projects");

        await LiveAsync(source, Check);
    }
    // The longest run of near-silence in 16-bit mono 48 kHz audio, in 5 ms slices: its length and start (ms).
    private static (double Ms, double At) LongestQuiet(byte[] pcm)
    {
        int slice = 240, n = pcm.Length / 2 / slice;
        var rms = new double[n];
        for (int s = 0; s < n; s++) { double sum = 0; for (int i = 0; i < slice; i++) { double v = BitConverter.ToInt16(pcm, (s * slice + i) * 2); sum += v * v; } rms[s] = Math.Sqrt(sum / slice); }
        double typical = rms.OrderBy(v => v).ElementAt(n / 2);
        int best = 0, bestAt = 0, run = 0;
        for (int s = 0; s < n; s++) { run = rms[s] < typical * .2 ? run + 1 : 0; if (run > best) { best = run; bestAt = s - run + 1; } }
        return (best * 5, bestAt * 5);
    }
    // Sliders: the coloured fill runs right up to the dot with no gap, and the track ends where the dot's
    // travel does. Renders sliders at several values and reads the pixels along the track. slider-test.png.
    internal static void Sliders()
    {
        Directory.CreateDirectory(Storage.Root);
        var accent = ((SolidColorBrush)Application.Current.FindResource("Accent")).Color;
        var track = ((SolidColorBrush)Application.Current.FindResource("Outline")).Color;
        var values = new[] { 0, .3, .7, 1 };
        var panel = new System.Windows.Controls.StackPanel { Width = 240, Background = new SolidColorBrush(Color.FromRgb(17, 20, 25)) };
        foreach (double v in values) panel.Children.Add(new System.Windows.Controls.Slider { Minimum = 0, Maximum = 1, Value = v, Width = 200, Margin = new Thickness(20, 4, 20, 4) });
        panel.Measure(new Size(240, 400)); panel.Arrange(new Rect(panel.DesiredSize)); panel.UpdateLayout();
        int w = (int)panel.ActualWidth, h = (int)panel.ActualHeight;
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32); bitmap.Render(panel);
        var pixels = new byte[w * h * 4]; bitmap.CopyPixels(pixels, w * 4, 0);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(Storage.Root, "slider-test.png"))) png.Save(file);
        bool Like(int x, int y, Color c) { int i = (y * w + x) * 4; return Math.Abs(pixels[i + 2] - c.R) < 24 && Math.Abs(pixels[i + 1] - c.G) < 24 && Math.Abs(pixels[i] - c.B) < 24; }
        var lines = new List<string>(); bool ok = true;
        for (int s = 0; s < values.Length; s++)
        {
            var slider = (System.Windows.Controls.Slider)panel.Children[s];
            var at = slider.TranslatePoint(new Point(0, slider.ActualHeight / 2), panel);
            int y = (int)at.Y, left = (int)at.X, right = left + (int)slider.ActualWidth - 1;
            double dot = left + 13 + values[s] * (slider.ActualWidth - 26);
            // Along the middle of the track: accent from the track's start to the dot, then grey to its end.
            int firstAccent = Enumerable.Range(left, right - left + 1).FirstOrDefault(x => Like(x, y, accent), -1);
            int lastAccent = Enumerable.Range(left, right - left + 1).LastOrDefault(x => Like(x, y, accent), -1);
            bool gap = Enumerable.Range(Math.Max(firstAccent, 0), Math.Max(0, lastAccent - firstAccent)).Any(x => !Like(x, y, accent));
            bool greyPastEnds = Enumerable.Range(left, 6).Any(x => Like(x, y, track)) || Enumerable.Range(right - 5, 6).Any(x => Like(x, y, track));
            bool fine = !gap && !greyPastEnds && Math.Abs(lastAccent - (dot + 6)) <= 2;
            ok &= fine;
            lines.Add($"{values[s]:0.0}: accent {firstAccent - left}..{lastAccent - left} px, dot centre {dot - left:0} px, gap {gap}, track past the ends {greyPastEnds} → {(fine ? "PASS" : "FAIL")}");
        }
        File.WriteAllLines(Path.Combine(Storage.Root, "slider-test.txt"), lines);
        if (!ok) throw new Exception("FAILED: sliders don't line up with their dot\n" + string.Join("\n", lines));
    }
    private static double Tone(byte[] pcm, double hz)
    {
        double re = 0, im = 0; int n = pcm.Length / 2;
        for (int i = 0; i < n; i++) { double s = BitConverter.ToInt16(pcm, i * 2), a = 2 * Math.PI * hz * i / 48000; re += s * Math.Cos(a); im += s * Math.Sin(a); }
        return Math.Sqrt(re * re + im * im) / Math.Max(1, n);
    }
    private static async Task Shot(FrameworkElement content, string name)
    {
        await Task.Delay(50); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(Storage.Root, name)); png.Save(file);
    }

    // ---- The live editor playing the finished video ----
    private static async Task LiveAsync(string source, Action<bool, string> Check)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var live = new TrimWindow(source) { WindowState = WindowState.Maximized };
        try
        {
            live.Show();
            var until = Stopwatch.StartNew();
            while (!live.Player.NaturalDuration.HasTimeSpan && until.Elapsed.TotalSeconds < 12) await Task.Delay(100);
            Check(live.Player.NaturalDuration.HasTimeSpan, $"The editor opens the clip ({live.StatusLabel.Text}; {live.Player.Source})");
            // Sections 6-8 s then 1-3 s; 7-8 s at half speed; a 1 s freeze at 2 s. Finished: 1 + 2 + 1 + 1 + 1 = 6 s.
            live.StartBox.Text = "1"; live.EndBox.Text = "3"; typeof(TrimWindow).GetMethod("Add_Click", flags)!.Invoke(live, new object[] { live, new RoutedEventArgs() });
            live.StartBox.Text = "6"; live.EndBox.Text = "8"; typeof(TrimWindow).GetMethod("Add_Click", flags)!.Invoke(live, new object[] { live, new RoutedEventArgs() });
            live.MoveSection(1, 0);
            live.Timeline.SlowRegions = new[] { new SpeedRegion(7, 8, .5) };
            // Playing into a slow part doesn't skip any of it: the player's position, sampled finely,
            // never runs ahead of where the speed can have taken it.
            live.SeekTo(6.4); await Task.Delay(500);
            var trace = new List<(double Wall, double At)>(); var watch = Stopwatch.StartNew();
            live.HandleKey(Key.Space, ModifierKeys.None, true);
            while (watch.Elapsed.TotalSeconds < 2.6) { trace.Add((watch.Elapsed.TotalSeconds, live.Player.Position.TotalSeconds)); await Task.Delay(15); }
            live.HandleKey(Key.Space, ModifierKeys.None, true);
            double skip = trace.Zip(trace.Skip(1)).Where(p => p.First.At >= 6.8 && p.First.At < 7.6).Select(p => (p.Second.At - p.First.At) - (p.Second.Wall - p.First.Wall) * (p.First.At >= 7 ? .5 : 1)).DefaultIfEmpty(0).Max();
            File.WriteAllLines(Path.Combine(Storage.Root, "slow-entry-trace.txt"), trace.Select(t => $"{t.Wall:0.000} {t.At:0.000}"));
            Check(trace[^1].At > 7.3 && skip < .08, $"Playing into a slow part skips none of it (largest jump ahead {skip * 1000:0} ms, reached {trace[^1].At:0.00} s)");
            typeof(TrimWindow).GetMethod("AddFreeze", flags)!.Invoke(live, new object[] { 2.0, 1.0, true });
            live.ToggleFinishedView(); await Task.Delay(400);
            Check(Math.Abs(live.Timeline.Total - 6) < .05, $"The finished view lays out sections, the slow part and the hold ({live.Timeline.Total:0.##} s)");
            await Shot((FrameworkElement)live.Content, "sequence-live.png");
            typeof(TrimWindow).GetMethod("SeekView", flags)!.Invoke(live, new object[] { 0.0 }); await Task.Delay(400);
            live.HandleKey(Key.Space, ModifierKeys.None, true);
            var seen = new List<(double View, double Hold, double Source)>();
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < 9)
            {
                await Task.Delay(80);
                seen.Add((live.Timeline.PositionView, live.Timeline.HoldOffset, live.Playhead));
                if (seen[^1].View > 5.8) break;
            }
            live.HandleKey(Key.Space, ModifierKeys.None, true);
            int backwards = seen.Zip(seen.Skip(1)).Count(p => p.Second.View < p.First.View - .15);
            double reached = seen.Max(s => s.View);
            File.WriteAllText(Path.Combine(Storage.Root, "sequence-playback.txt"), string.Join(Environment.NewLine, seen.Select(s => $"{s.View:0.000} hold {s.Hold:0.000} source {s.Source:0.000}")));
            Check(reached > 5.3 && backwards == 0, $"Playing the finished video moves the playhead steadily forward across the section jump (reached {reached:0.00} s, {backwards} steps back)");
            Check(seen.Any(s => s.Hold > .2 && Math.Abs(s.Source - 2) < .05), "Playing through a freeze holds the frame while the playhead moves along the hold");
            Check(seen.Any(s => s.Source >= 6 && s.Source < 8) && seen.Any(s => s.Source >= 1 && s.Source < 3), "Both sections play, in their order");
        }
        finally { live.Close(); }
    }
}
