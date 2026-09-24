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
            tl.ZoomRegions = new[] { new ZoomRegion(6, 7, .5, .5, 2, ZoomPreset.BuiltIns[2].Curve) };
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
        var project = TrimProject.Create(source, Array.Empty<KeepSection>(), 0, 6, 0, false) with { Sounds = new[] { new SoundItem(music, 0, 2) { Speed = 99, Hold = -3 } } };
        var cleaned = JsonSerializer.Deserialize<TrimProject>(JsonSerializer.Serialize(project))!;
        var cleanedSound = ((TrimProject)typeof(TrimProject).GetMethod("Cleaned", flags)!.Invoke(cleaned, null)!).Sounds![0];
        Check(cleanedSound.Speed == SoundItem.MaxSpeed && cleanedSound.Hold == 0 && JsonSerializer.Deserialize<SoundItem>("{\"Path\":\"a\",\"Start\":0,\"End\":1}")!.Speed == 1, "Sound speeds and holds are saved, checked, and default to normal for older projects");

        await LiveAsync(source, Check);
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
