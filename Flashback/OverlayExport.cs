using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Flashback;

// Turns text and image items into see-through pictures for the exporter. Each item is drawn once for
// every distinct look it has (one picture when it stays still; one per frame while it animates or a
// GIF changes) into a box that covers everywhere it goes. ffmpeg then lays the pictures over the video
// at that box for the item's stretch of time.
internal static class OverlayExport
{
    internal sealed record Clip(OverlayItem Item, Int32Rect Box, IReadOnlyList<(double Time, string File)> Frames, string Blank);

    internal static Task<List<Clip>> RenderAsync(IReadOnlyList<OverlayItem> items, int width, int height, double frameRate, string folder, CancellationToken token)
    {
        var done = new TaskCompletionSource<List<Clip>>(TaskCreationOptions.RunContinuationsAsynchronously);
        // WPF drawing needs a single-threaded apartment; this keeps the window responsive meanwhile.
        var thread = new Thread(() =>
        {
            try { done.SetResult(Render(items, width, height, frameRate, folder, token)); }
            catch (Exception ex) { done.SetException(ex); }
        }) { IsBackground = true, Name = "Overlay export" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static List<Clip> Render(IReadOnlyList<OverlayItem> items, int width, int height, double frameRate, string folder, CancellationToken token)
    {
        Directory.CreateDirectory(folder);
        var clips = new List<Clip>();
        double step = 1 / Math.Clamp(frameRate, 1, 60), aspect = width / (double)Math.Max(1, height);
        int n = 0;
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            n++;
            if (item.Length <= 0) continue;
            // Every moment the item's look can change: through its entrance and exit, and at GIF frames.
            var times = new List<double> { 0 };
            foreach (var (from, to) in item.Animated()) { for (double t = from; t < to - 1e-9; t += step) times.Add(t); times.Add(to); }
            times.AddRange(OverlayRenderer.FrameChanges(item));
            times = times.Where(t => t >= 0 && t < item.Length - 1e-6).OrderBy(t => t).Aggregate(new List<double>(), (list, t) => { if (list.Count == 0 || t - list[^1] > 1e-6) list.Add(t); return list; });
            var looks = new List<(double Time, OverlayState State, int Frame)>();
            foreach (double t in times)
            {
                var s = item.StateAt(t); int frame = item.Kind == OverlayKind.Image ? OverlayRenderer.FrameAt(item, t) : 0;
                var rounded = new OverlayState(Math.Round(s.Opacity, 3), Math.Round(s.Scale, 4), Math.Round(s.Dx, 5), Math.Round(s.Dy, 5), s.Chars);
                if (looks.Count > 0 && looks[^1].State == rounded && looks[^1].Frame == frame) continue;
                looks.Add((t, rounded, frame));
            }
            // One box that holds the item wherever it moves, kept inside the frame.
            var union = Rect.Empty;
            foreach (var (t, _, _) in looks)
            {
                var s = item.StateAt(t);
                if (s.Opacity <= 0) continue;
                union.Union(OverlayRenderer.Bounds(item, s, width, height, OverlayRenderer.Content(item, s.Chars, t, aspect)));
            }
            union.Intersect(new Rect(0, 0, width, height));
            if (union.IsEmpty || union.Width < 1 || union.Height < 1) continue;
            int x = (int)Math.Floor(union.X), y = (int)Math.Floor(union.Y);
            var box = new Int32Rect(x, y, Math.Min(width - x, (int)Math.Ceiling(union.Right) - x), Math.Min(height - y, (int)Math.Ceiling(union.Bottom) - y));
            var frames = new List<(double, string)>();
            for (int i = 0; i < looks.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                string file = Path.Combine(folder, $"item{n}-{i}.png");
                Save(OverlayRenderer.Render(item, looks[i].Time, width, height, box), file);
                frames.Add((looks[i].Time, file));
            }
            string blank = Path.Combine(folder, $"item{n}-blank.png");
            var empty = BitmapSource.Create(box.Width, box.Height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[box.Width * box.Height * 4], box.Width * 4);
            Save(empty, blank);
            clips.Add(new Clip(item, box, frames, blank));
        }
        return clips;
    }
    private static void Save(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }

    // A concat list that plays the item's pictures over one exported piece (source time
    // pieceStart to pieceEnd), and the stretch of the piece where the item shows. Null when the
    // item isn't in this piece.
    internal static (string List, double From, double To)? PieceList(Clip clip, double pieceStart, double pieceEnd, string folder, string name)
    {
        var item = clip.Item;
        double from = Math.Max(0, pieceStart - item.Start), to = Math.Min(item.Length, pieceEnd - item.Start);
        if (to - from <= 1e-6) return null;
        double lead = Math.Max(0, item.Start - pieceStart);
        string N(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
        string Quote(string path) => "'" + path.Replace('\\', '/').Replace("'", "'\\''") + "'";
        var text = new StringBuilder("ffconcat version 1.0\n");
        if (lead > 1e-6) text.Append($"file {Quote(clip.Blank)}\nduration {N(lead)}\n");
        string last = clip.Blank;
        for (int i = 0; i < clip.Frames.Count; i++)
        {
            double a = Math.Max(clip.Frames[i].Time, from), b = Math.Min(i + 1 < clip.Frames.Count ? clip.Frames[i + 1].Time : item.Length, to);
            if (b - a <= 1e-6) continue;
            text.Append($"file {Quote(clip.Frames[i].File)}\nduration {N(b - a)}\n");
            last = clip.Frames[i].File;
        }
        // The concat reader ignores the last duration unless the last file is listed again.
        text.Append($"file {Quote(last)}\n");
        string path = Path.Combine(folder, name + ".ffconcat");
        File.WriteAllText(path, text.ToString());
        return (path, lead, lead + (to - from));
    }
}
