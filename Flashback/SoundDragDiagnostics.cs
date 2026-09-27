using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

namespace Flashback;

// --sound-drag-test: drags a sound between rows with the real mouse. Moving a sound onto a new row grows the
// timeline upwards (the preview gives way), which used to carry it further down a row at a time.
internal static class SoundDragDiagnostics
{
    internal static async Task RunAsync()
    {
        Directory.CreateDirectory(Storage.Root);
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception(message);
            File.AppendAllText(Path.Combine(Storage.Root, "sound-drag-results.txt"), "PASS " + message + Environment.NewLine);
        }
        var source = Path.Combine(Storage.Root, "Source.mp4");
        await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30:duration=10", "-f", "lavfi", "-i", "sine=frequency=500:sample_rate=48000:duration=10",
            "-c:v", "libx264", "-threads", "2", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", source);
        var music = Path.Combine(Storage.Root, "Tune.m4a");
        await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "sine=frequency=2000:sample_rate=48000:duration=3", "-c:a", "aac", music);
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, flags)!.Invoke(target, args);
        SystemTestNative.GetCursorPos(out var oldCursor);
        var trim = new TrimWindow(source, true) { Width = 1400, Height = 900, Topmost = true };
        try
        {
            trim.Show(); trim.Activate(); SystemTestNative.SetForegroundWindow(new WindowInteropHelper(trim).Handle);
            var tl = trim.Timeline;
            tl.LanesExpanded = true;
            Call(trim, "SetSounds", (IReadOnlyList<SoundItem>)new[] { new SoundItem(music, 1, 3), new SoundItem(music, 5, 7) });
            await Task.Delay(800);
            double scale = PresentationSource.FromVisual(tl)!.CompositionTarget.TransformToDevice.M22;
            var bar = (Rect)Call(tl, "SoundRect", tl.Sounds[1])!;
            var press = tl.PointToScreen(new Point(bar.X + bar.Width / 2, bar.Y + bar.Height / 2));
            int Row() => tl.Sounds[1].Row;
            async Task MoveBy(double dips)
            {
                // Small steps, as a hand would, so each one sees the layout the last one left.
                for (int i = 1; i <= 4; i++) { SystemTestNative.SetCursorPos((int)press.X, (int)(press.Y + dips * scale * i / 4)); await Task.Delay(40); }
                await Task.Delay(200);
            }
            async Task Step(double from, double to)
            {
                for (double d = from; Math.Abs(d - to) > .01; d += Math.Sign(to - from) * Math.Min(2, Math.Abs(to - d)))
                { SystemTestNative.SetCursorPos((int)press.X, (int)(press.Y + d * scale)); await Task.Delay(25); }
                SystemTestNative.SetCursorPos((int)press.X, (int)(press.Y + to * scale)); await Task.Delay(250);
            }
            SystemTestNative.SetCursorPos((int)press.X, (int)press.Y); await Task.Delay(100);
            SystemTestNative.MouseButton(true); await Task.Delay(100);
            try
            {
                await MoveBy(6);
                Check(Row() == 0, $"Moving a sound a few pixels down keeps it on its row (row {Row()})");
                await Step(6, 14);
                Check(Row() == 1, $"Moving its middle past the next row's edge takes it onto a new row (row {Row()})");
                await Task.Delay(400);
                Check(Row() == 1 && tl.Sounds[0].Row == 0, $"The timeline growing for the new row doesn't carry it further down (row {Row()})");
                await Step(14, 90);
                Check(Row() == 1, $"Dragging far below makes one new row at most (row {Row()})");
                await Step(90, 2);
                Check(Row() == 0, $"Dragging back up returns it to the first row (row {Row()})");
            }
            finally { SystemTestNative.MouseButton(false); }
            await Task.Delay(200);
            Check(tl.Sounds.Count == 2 && Row() == 0, "Letting go leaves it on the first row");
        }
        finally { SystemTestNative.SetCursorPos(oldCursor.X, oldCursor.Y); trim.Close(); }
    }
}
