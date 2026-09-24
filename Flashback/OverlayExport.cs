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
    internal sealed record Clip(OverlayItem Item, Int32Rect Box, IReadOnlyList<(double Time, string File)> Frames, string Blank, Pip? Video = null, Motion? Move = null)
    {
        internal bool Region => Item.IsRegion;
    }
    // A keyframed item that only moves: one picture, carried along its keyframes by ffmpeg. Dx and Dy
    // are from the item's centre to the picture's corner, in frame pixels.
    internal sealed record Motion(IReadOnlyList<OverlayKeyframe> Keys, double Dx, double Dy);
    // A picture-in-picture video: its crop in the source, its size and centre on the frame, and the
    // mask (and border) pictures drawn at that size.
    internal sealed record Pip(int CropX, int CropY, int CropW, int CropH, int Width, int Height, double CenterX, double CenterY, string Mask, string? Border, int Pad, bool HasSound);

    internal static Task<List<Clip>> RenderAsync(IReadOnlyList<OverlayItem> items, int width, int height, double frameRate, string folder, CancellationToken token)
    {
        Directory.CreateDirectory(folder);
        var done = new TaskCompletionSource<List<Clip>>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Still items that sit next to each other in the layer order become one picture, redrawn only
        // when one of them comes or goes: fewer pictures to draw and far fewer layers for ffmpeg.
        var units = new List<Func<int, Clip?>>(); var run = new List<OverlayItem>();
        void Flush()
        {
            if (run.Count == 1) { var only = run[0]; units.Add(n => Cached(new[] { only }, width, height, frameRate, dir => RenderItem(only, 1, width, height, frameRate, dir, token))); }
            else if (run.Count > 1) { var group = run.ToList(); units.Add(n => Cached(group, width, height, frameRate, dir => RenderGroup(group, 1, width, height, dir, token))); }
            run.Clear();
        }
        foreach (var item in items)
        {
            if (item.Length <= 0) continue;
            // Up to six to a group, so changing one item redraws only a small group.
            if (IsStill(item) && run.Count < 6 && (run.Count == 0 || run[0].StickToVideo == item.StickToVideo)) { run.Add(item); continue; }
            Flush();
            if (IsStill(item)) run.Add(item);
            else if (item.Kind == OverlayKind.Video) { var video = item; units.Add(n => RenderItem(video, n, width, height, frameRate, folder, token)); }
            else { var single = item; units.Add(n => Cached(new[] { single }, width, height, frameRate, dir => RenderItem(single, 1, width, height, frameRate, dir, token))); }
        }
        Flush();
        TrimCacheSoon();
        // WPF drawing needs single-threaded apartments; several draw side by side (animated items are
        // hundreds of pictures each) while the window stays responsive.
        var results = new Clip?[units.Count]; int next = -1, running = 0; Exception? failure = null;
        int threads = Math.Clamp(Math.Min(units.Count, Environment.ProcessorCount / 2), 1, 6);
        if (units.Count == 0) { done.SetResult(new List<Clip>()); return done.Task; }
        for (int t = 0; t < threads; t++)
        {
            Interlocked.Increment(ref running);
            var thread = new Thread(() =>
            {
                try
                {
                    for (int i = Interlocked.Increment(ref next); i < units.Count && failure == null; i = Interlocked.Increment(ref next))
                        results[i] = units[i](i + 1);
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

    // ---- Remembered pictures ----
    // Pictures drawn for an export are kept, named by exactly what they show (the items, relative to where
    // they start, the frame size and rate, and the picture files' dates). The next export of the same
    // edit (or one where only a few things changed, or things only moved along the timeline) reuses them.
    private static readonly string CacheRoot = Path.Combine(Path.GetTempPath(), "Flashback-overlay-cache");
    private static readonly System.Text.Json.JsonSerializerOptions CacheJson = new() { IncludeFields = true };
    private sealed record Saved(Int32Rect Box, List<(double Time, string File)> Frames, string Blank, Motion? Move);
    private static Clip? Cached(IReadOnlyList<OverlayItem> members, int width, int height, double frameRate, Func<string, Clip?> render)
    {
        double origin = members.Min(m => m.Start), end = members.Max(m => m.End);
        var relative = members.Select(m => m with { Start = m.Start - origin, End = m.End - origin, Layer = 0 }).ToList();
        string pictures = string.Join("|", members.Where(m => m.Kind == OverlayKind.Image).Select(m => { try { return m.ImagePath + "@" + File.GetLastWriteTimeUtc(m.ImagePath).Ticks; } catch { return m.ImagePath; } }));
        string json = System.Text.Json.JsonSerializer.Serialize(new { Version = 1, width, height, frameRate, relative, pictures }, CacheJson);
        string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..32];
        string dir = Path.Combine(CacheRoot, key), manifest = Path.Combine(dir, "clip.json");
        Clip Rebuild(Saved saved, string at)
        {
            var stand = members.Count == 1 ? members[0] : new OverlayItem { Kind = OverlayKind.Shape, Shape = OverlayShape.Box, Start = origin, End = end, StickToVideo = members[0].StickToVideo };
            return new Clip(stand, saved.Box, saved.Frames.Select(f => (f.Time, Path.Combine(at, f.File))).ToList(), Path.Combine(at, saved.Blank), Move: saved.Move == null ? null : saved.Move with { Keys = members[0].Keys });
        }
        try
        {
            if (File.Exists(manifest) && System.Text.Json.JsonSerializer.Deserialize<Saved>(File.ReadAllText(manifest), CacheJson) is { } found
                && found.Frames.All(f => File.Exists(Path.Combine(dir, f.File))) && File.Exists(Path.Combine(dir, found.Blank)))
            {
                try { Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow); } catch { }
                return Rebuild(found, dir);
            }
        }
        catch { }
        string work = dir + "." + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(work);
        var clip = render(work);
        if (clip == null) { try { Directory.Delete(work, true); } catch { } return null; }
        var saved = new Saved(clip.Box, clip.Frames.Select(f => (f.Time, Path.GetFileName(f.File))).ToList(), Path.GetFileName(clip.Blank), clip.Move);
        File.WriteAllText(Path.Combine(work, "clip.json"), System.Text.Json.JsonSerializer.Serialize(saved, CacheJson));
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); Directory.Move(work, dir); return Rebuild(saved, dir); }
        catch { return Rebuild(saved, work); }
    }
    // Keeps the remembered pictures under about 2 GB, dropping the longest unused first.
    private static void TrimCacheSoon() => Task.Run(() =>
    {
        try
        {
            if (!Directory.Exists(CacheRoot)) return;
            var folders = new DirectoryInfo(CacheRoot).GetDirectories().Select(d => (Dir: d, Bytes: d.EnumerateFiles().Sum(f => f.Length))).OrderBy(d => d.Dir.LastWriteTimeUtc).ToList();
            long total = folders.Sum(d => d.Bytes);
            foreach (var (d, bytes) in folders)
            {
                if (total <= 2L << 30) break;
                if (DateTime.UtcNow - d.LastWriteTimeUtc < TimeSpan.FromMinutes(10)) continue;
                try { d.Delete(true); total -= bytes; } catch { }
            }
        }
        catch { }
    });

    // Looks the same for its whole stretch: no entrance or exit, no keyframed motion, no GIF frames.
    private static bool IsStill(OverlayItem item) => !item.IsRegion && item.Kind != OverlayKind.Video && !item.Animated().Any() && !OverlayRenderer.FrameChanges(item).Any();
    // Moves between keyframes without changing size, turn or opacity, and has no entrance or exit.
    private static bool OnlyMoves(OverlayItem item) => item.Keys.Count >= 2 && !item.IsRegion && item.Kind != OverlayKind.Video && item.In == OverlayMotion.None && item.Out == OverlayMotion.None
        && !OverlayRenderer.FrameChanges(item).Any() && item.Keys.All(k => Math.Abs(k.Scale - item.Keys[0].Scale) < 1e-6 && Math.Abs(k.Rotation - item.Keys[0].Rotation) < 1e-6 && Math.Abs(k.Opacity - item.Keys[0].Opacity) < 1e-6);

    private static Clip? RenderGroup(List<OverlayItem> members, int n, int width, int height, string folder, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        double aspect = width / (double)Math.Max(1, height), start = members.Min(m => m.Start), end = members.Max(m => m.End);
        var union = Rect.Empty;
        foreach (var m in members) { var posed = m.Posed(0); var s = posed.StateAt(0); union.Union(OverlayRenderer.Bounds(posed, s, width, height, OverlayRenderer.Content(posed, s.Chars, 0, aspect))); }
        union.Intersect(new Rect(0, 0, width, height));
        if (union.IsEmpty || union.Width < 1 || union.Height < 1) return null;
        int x = (int)Math.Floor(union.X), y = (int)Math.Floor(union.Y), right = (int)Math.Ceiling(union.Right), bottom = (int)Math.Ceiling(union.Bottom);
        if (right - x < 2 || bottom - y < 2) return null;
        var box = new Int32Rect(x, y, Math.Min(width - x, right - x), Math.Min(height - y, bottom - y));
        string blank = Path.Combine(folder, $"item{n}-blank.png");
        FastPng.Save(BitmapSource.Create(box.Width, box.Height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[box.Width * box.Height * 4], box.Width * 4), blank);
        // A new picture wherever the set of items showing changes.
        var changes = members.SelectMany(m => new[] { m.Start, m.End }).Where(t => t >= start - 1e-9 && t < end - 1e-6).Distinct().OrderBy(t => t).ToList();
        var frames = new List<(double, string)>(); string? shown = null;
        foreach (double t in changes)
        {
            token.ThrowIfCancellationRequested();
            var visible = members.Where(m => m.Start <= t + 1e-9 && m.End > t + 1e-9).ToList();
            string key = string.Join(",", visible.Select(m => members.IndexOf(m)));
            if (key == shown) continue;
            shown = key;
            if (visible.Count == 0) { frames.Add((t - start, blank)); continue; }
            var host = new System.Windows.Media.ContainerVisual { Transform = new System.Windows.Media.TranslateTransform(-box.X, -box.Y) };
            foreach (var m in visible) host.Children.Add(OverlayRenderer.Visual(m, t - m.Start, width, height));
            var bitmap = new RenderTargetBitmap(box.Width, box.Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(host);
            string file = Path.Combine(folder, $"item{n}-{frames.Count}.png");
            FastPng.Save(bitmap, file);
            frames.Add((t - start, file));
        }
        var stand = new OverlayItem { Kind = OverlayKind.Shape, Shape = OverlayShape.Box, Start = start, End = end, StickToVideo = members[0].StickToVideo };
        return new Clip(stand, box, frames, blank);
    }

    private static Clip? RenderItem(OverlayItem item, int n, int width, int height, double frameRate, string folder, CancellationToken token)
    {
        double step = 1 / Math.Clamp(frameRate, 1, 60), aspect = width / (double)Math.Max(1, height);
        {
            token.ThrowIfCancellationRequested();
            if (item.Length <= 0) return null;
            if (item.Kind == OverlayKind.Video) return VideoClip(item, width, height, folder, n);
            if (OnlyMoves(item))
            {
                // One picture where the first keyframe puts it; ffmpeg moves it between keyframes.
                var first = item.Posed(item.Keys[0].T); var s0 = first.StateAt(0);
                var bounds = OverlayRenderer.Bounds(first, s0, width, height, OverlayRenderer.Content(first, s0.Chars, 0, aspect));
                if (bounds.IsEmpty || bounds.Width < 1 || bounds.Height < 1) return null;
                int bx = (int)Math.Floor(bounds.X), by = (int)Math.Floor(bounds.Y);
                var moving = new Int32Rect(bx, by, (int)Math.Ceiling(bounds.Right) - bx, (int)Math.Ceiling(bounds.Bottom) - by);
                string still = Path.Combine(folder, $"item{n}-0.png"), none = Path.Combine(folder, $"item{n}-blank.png");
                FastPng.Save(OverlayRenderer.Render(item, item.Keys[0].T, width, height, moving), still);
                FastPng.Save(BitmapSource.Create(moving.Width, moving.Height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[moving.Width * moving.Height * 4], moving.Width * 4), none);
                return new Clip(item, moving, new[] { (0.0, still) }, none, Move: new Motion(item.Keys, bx - first.X * width, by - first.Y * height));
            }
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
                FastPng.Save(OverlayRenderer.Render(drawn, looks[i].Time, width, height, box), file);
                frames.Add((looks[i].Time, file));
            }
            string blank = Path.Combine(folder, $"item{n}-blank.png");
            var empty = BitmapSource.Create(box.Width, box.Height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[box.Width * box.Height * 4], box.Width * 4);
            FastPng.Save(empty, blank);
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
            FastPng.Save(bitmap, path);
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

    // Where a moving picture's corner is at each moment of a piece (t runs from the piece's start):
    // eased from keyframe to keyframe the way the preview eases them, held before the first and after the last.
    internal static (string X, string Y) MoveExpressions(Clip clip, double pieceStart, int width, int height)
    {
        var move = clip.Move!; var keys = move.Keys;
        string N(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
        string T = $"(t+{N(pieceStart - clip.Item.Start)})";
        string Build(Func<OverlayKeyframe, double> value, double size, double offset)
        {
            double At(OverlayKeyframe k) => value(k) * size + offset;
            string expr = N(At(keys[^1]));
            for (int i = keys.Count - 2; i >= 0; i--)
            {
                var a = keys[i]; var b = keys[i + 1];
                string p = $"clip(({T}-{N(a.T)})/{N(Math.Max(1e-6, b.T - a.T))},0,1)";
                expr = $"if(lt({T},{N(b.T)}),{N(At(a))}+({N(At(b) - At(a))})*{p}*{p}*(3-2*{p}),{expr})";
            }
            return expr;
        }
        return (Build(k => k.X, width, move.Dx), Build(k => k.Y, height, move.Dy));
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
