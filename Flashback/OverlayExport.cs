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
    // Region clips are white masks for a blur or pixelate effect; Pip clips carry a video instead of frames.
    internal sealed record Clip(OverlayItem Item, Int32Rect Box, IReadOnlyList<(double Time, string File)> Frames, string Blank, Pip? Video = null)
    {
        internal bool Region => Item.IsRegion;
    }
    // A picture-in-picture video: its crop in the source, its size and centre on the frame, and the
    // mask (and border) pictures drawn at that size.
    internal sealed record Pip(int CropX, int CropY, int CropW, int CropH, int Width, int Height, double CenterX, double CenterY, string Mask, string? Border, int Pad, bool HasSound);

    internal static Task<List<Clip>> RenderAsync(IReadOnlyList<OverlayItem> items, int width, int height, double frameRate, string folder, CancellationToken token)
    {
        Directory.CreateDirectory(folder);
        var done = new TaskCompletionSource<List<Clip>>(TaskCreationOptions.RunContinuationsAsynchronously);
        // WPF drawing needs single-threaded apartments; several draw items side by side (animated
        // items are hundreds of pictures each) while the window stays responsive.
        var results = new Clip?[items.Count]; int next = -1, running = 0; Exception? failure = null;
        int threads = Math.Clamp(Math.Min(items.Count, Environment.ProcessorCount / 2), 1, 6);
        for (int t = 0; t < threads; t++)
        {
            Interlocked.Increment(ref running);
            var thread = new Thread(() =>
            {
                try
                {
                    for (int i = Interlocked.Increment(ref next); i < items.Count && failure == null; i = Interlocked.Increment(ref next))
                        results[i] = RenderItem(items[i], i + 1, width, height, frameRate, folder, token);
                }
                catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); }
                finally
                {
                    if (Interlocked.Decrement(ref running) == 0)
                    {
                        if (failure != null) done.SetException(failure);
                        else done.SetResult(results.Where(c => c != null).Select(c => c!).ToList());
                    }
                }
            }) { IsBackground = true, Name = "Overlay export", Priority = ThreadPriority.BelowNormal };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        return done.Task;
    }

    private static Clip? RenderItem(OverlayItem item, int n, int width, int height, double frameRate, string folder, CancellationToken token)
    {
        double step = 1 / Math.Clamp(frameRate, 1, 60), aspect = width / (double)Math.Max(1, height);
        {
            token.ThrowIfCancellationRequested();
            if (item.Length <= 0) return null;
            if (item.Kind == OverlayKind.Video) return VideoClip(item, width, height, folder, n);
            // Blur and pixelate shapes only need their outline: no shadow, just the mask.
            var drawn = item.IsRegion ? item with { Shadow = new OverlayShadow() } : item;
            // Every moment the item's look can change: through its entrance and exit, and at GIF frames.
            var times = new List<double> { 0 };
            foreach (var (from, to) in item.Animated()) { for (double t = from; t < to - 1e-9; t += step) times.Add(t); times.Add(to); }
            times.AddRange(OverlayRenderer.FrameChanges(item));
            times = times.Where(t => t >= 0 && t < item.Length - 1e-6).OrderBy(t => t).Aggregate(new List<double>(), (list, t) => { if (list.Count == 0 || t - list[^1] > 1e-6) list.Add(t); return list; });
            var looks = new List<(double Time, object Key)>();
            foreach (double t in times)
            {
                var posed = drawn.Posed(t); var s = posed.StateAt(t); int frame = item.Kind == OverlayKind.Image ? OverlayRenderer.FrameAt(item, t) : 0;
                // What the picture depends on: entrance/exit state, GIF frame and keyframed pose.
                var key = (Math.Round(s.Opacity, 3), Math.Round(s.Scale, 4), Math.Round(s.Dx, 5), Math.Round(s.Dy, 5), s.Chars, frame,
                    Math.Round(posed.X, 5), Math.Round(posed.Y, 5), Math.Round(posed.Scale, 4), Math.Round(posed.Rotation, 2));
                if (looks.Count > 0 && Equals(looks[^1].Key, key)) continue;
                looks.Add((t, key));
            }
            // One box that holds the item wherever it moves, kept inside the frame.
            var union = Rect.Empty;
            foreach (var (t, _) in looks)
            {
                var posed = drawn.Posed(t); var s = posed.StateAt(t);
                if (s.Opacity <= 0 && !item.IsRegion) continue;
                union.Union(OverlayRenderer.Bounds(posed, s, width, height, OverlayRenderer.Content(posed, s.Chars, t, aspect)));
            }
            union.Intersect(new Rect(0, 0, width, height));
            if (union.IsEmpty || union.Width < 1 || union.Height < 1) return null;
            int x = (int)Math.Floor(union.X), y = (int)Math.Floor(union.Y);
            int right = (int)Math.Ceiling(union.Right), bottom = (int)Math.Ceiling(union.Bottom);
            // Blur and pixelate boxes line up with the video's colour samples, which come in pairs.
            if (item.IsRegion) { x &= ~1; y &= ~1; right = Math.Min(width & ~1, (right + 1) & ~1); bottom = Math.Min(height & ~1, (bottom + 1) & ~1); }
            if (right - x < 2 || bottom - y < 2) return null;
            var box = new Int32Rect(x, y, Math.Min(width - x, right - x), Math.Min(height - y, bottom - y));
            var frames = new List<(double, string)>();
            for (int i = 0; i < looks.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                string file = Path.Combine(folder, $"item{n}-{i}.png");
                Save(OverlayRenderer.Render(drawn, looks[i].Time, width, height, box), file);
                frames.Add((looks[i].Time, file));
            }
            string blank = Path.Combine(folder, $"item{n}-blank.png");
            var empty = BitmapSource.Create(box.Width, box.Height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[box.Width * box.Height * 4], box.Width * 4);
            Save(empty, blank);
            return new Clip(item, box, frames, blank);
        }
    }
    // Picture-in-picture: the video itself goes through ffmpeg; here its size, crop and centre are
    // worked out, and its mask and border are drawn at that size.
    private static Clip? VideoClip(OverlayItem item, int width, int height, string folder, int n)
    {
        if (!File.Exists(item.VideoPath) || item.VideoWidth <= 0 || item.VideoHeight <= 0) return null;
        double k = height / OverlayItem.Reference * item.Scale;
        var rect = OverlayRenderer.VideoRect(item);
        int Even(double v) => Math.Max(2, (int)Math.Round(v / 2) * 2);
        int w = Even(rect.Width * k), h = Even(rect.Height * k);
        int cropX = (int)Math.Round(item.CropLeft * item.VideoWidth) & ~1, cropY = (int)Math.Round(item.CropTop * item.VideoHeight) & ~1;
        int cropW = Math.Min(item.VideoWidth - cropX, Even(item.VideoWidth * (1 - item.CropLeft - item.CropRight))), cropH = Math.Min(item.VideoHeight - cropY, Even(item.VideoHeight * (1 - item.CropTop - item.CropBottom)));
        var scale = new System.Windows.Media.ScaleTransform(w / rect.Width, h / rect.Height);
        var shape = OverlayRenderer.MaskGeometry(item, rect);
        string Draw(string name, int canvasW, int canvasH, int pad, Action<System.Windows.Media.DrawingContext> draw)
        {
            var visual = new System.Windows.Media.DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.PushTransform(new System.Windows.Media.TranslateTransform(pad + w / 2.0, pad + h / 2.0)); dc.PushTransform(scale);
                draw(dc);
            }
            var bitmap = new RenderTargetBitmap(canvasW, canvasH, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(visual);
            string path = Path.Combine(folder, $"video{n}-{name}.png");
            Save(new FormatConvertedBitmap(bitmap, System.Windows.Media.PixelFormats.Bgra32, null, 0), path);
            return path;
        }
        string mask = Draw("mask", w, h, 0, dc => dc.DrawGeometry(System.Windows.Media.Brushes.White, null, shape));
        string? border = null; int padding = 0;
        if (item.BorderWidth > 0)
        {
            padding = (int)Math.Ceiling(item.BorderWidth * Math.Max(w / rect.Width, h / rect.Height)) + 2;
            border = Draw("border", w + padding * 2, h + padding * 2, padding, dc => OverlayRenderer.DrawBorder(dc, item, shape));
        }
        bool sound = false; try { sound = ClipMedia.Read(item.VideoPath).HasAudio; } catch { }
        return new Clip(item, new Int32Rect(0, 0, w, h), Array.Empty<(double, string)>(), mask,
            new Pip(cropX, cropY, cropW, cropH, w, h, item.X * width, item.Y * height, mask, border, padding, sound));
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
