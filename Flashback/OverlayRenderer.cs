using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Flashback;

// Draws text and image items. The preview and the export use the same drawing, so what you see
// in the trimmer is what the export gets. Content is built centred on (0, 0) in reference pixels
// (1080-line frame) and then placed, scaled and rotated onto the frame.
internal static class OverlayRenderer
{
    // Where an item sits on a frame of the given size at a given state.
    internal static Matrix Placement(OverlayItem item, OverlayState state, double frameW, double frameH)
    {
        double k = frameH / OverlayItem.Reference * item.Scale * state.Scale;
        var m = Matrix.Identity;
        m.Scale(k, k); m.Rotate(item.Rotation); m.Translate((item.X + state.Dx) * frameW, (item.Y + state.Dy) * frameH);
        return m;
    }

    // A visual in frame pixels, ready to show over the video or render to a picture.
    internal static DrawingVisual Visual(OverlayItem item, double t, double frameW, double frameH, Drawing? content = null)
    {
        var state = item.StateAt(t);
        content ??= Content(item, state.Chars, t, frameW / frameH);
        var visual = new DrawingVisual { Opacity = Math.Clamp(state.Opacity, 0, 1) };
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new MatrixTransform(Placement(item, state, frameW, frameH)));
            dc.DrawDrawing(content);
        }
        if (item.Shadow.On) visual.Effect = Shadow(item, state, frameH);
        return visual;
    }
    internal static DropShadowEffect Shadow(OverlayItem item, OverlayState state, double frameH)
    {
        double k = frameH / OverlayItem.Reference * item.Scale * state.Scale;
        var s = item.Shadow;
        return new DropShadowEffect { Color = ParseColor(s.Color, Colors.Black), Opacity = Math.Clamp(s.Opacity, 0, 1), ShadowDepth = Math.Max(0, s.Distance * k), BlurRadius = Math.Max(0, s.Blur * k), Direction = s.Angle, RenderingBias = RenderingBias.Quality };
    }
    // The item's outline on the frame (before shadow), for handles and export bounds.
    internal static Rect Bounds(OverlayItem item, OverlayState state, double frameW, double frameH, Drawing content)
    {
        var b = content.Bounds;
        if (b.IsEmpty) return Rect.Empty;
        var m = Placement(item, state, frameW, frameH);
        var r = Rect.Transform(b, m);
        if (item.Shadow.On)
        {
            double k = frameH / OverlayItem.Reference * item.Scale * state.Scale, pad = (item.Shadow.Distance + item.Shadow.Blur * 1.5 + 2) * k;
            r.Inflate(pad, pad);
        }
        return r;
    }

    internal static Drawing Content(OverlayItem item, int chars, double t, double aspect) =>
        item.Kind == OverlayKind.Image ? ImageContent(item, t) : TextContent(item, chars, aspect);

    // ---- Text ----
    internal sealed record TextLayout(List<(string Text, double X, double Y, double Width, double Height)> Lines, Rect Block, Typeface Face, double Size);
    internal static TextLayout Layout(OverlayItem item, double aspect)
    {
        var face = new Typeface(new FontFamily(string.IsNullOrWhiteSpace(item.Font) ? "Segoe UI" : item.Font), item.Italic ? FontStyles.Italic : FontStyles.Normal, item.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        double size = item.Size, lineHeight = size * face.FontFamily.LineSpacing * item.LineSpacing;
        double wrap = item.WrapWidth > 0 ? item.WrapWidth * aspect * OverlayItem.Reference / Math.Max(.01, item.Scale) : double.PositiveInfinity;
        var lines = new List<string>();
        foreach (var raw in item.DisplayText.Replace("\r", "").Split('\n'))
        {
            if (double.IsInfinity(wrap) || Measure(raw, face, size, item.LetterSpacing) <= wrap) { lines.Add(raw); continue; }
            // Word wrap; a single long word stays on its own line.
            string current = "";
            foreach (var word in raw.Split(' '))
            {
                string next = current.Length == 0 ? word : current + " " + word;
                if (current.Length > 0 && Measure(next, face, size, item.LetterSpacing) > wrap) { lines.Add(current); current = word; }
                else current = next;
            }
            lines.Add(current);
        }
        var widths = lines.Select(l => Measure(l, face, size, item.LetterSpacing)).ToList();
        double width = widths.Count == 0 ? 0 : widths.Max(), height = lines.Count * lineHeight;
        var placed = new List<(string, double, double, double, double)>();
        for (int i = 0; i < lines.Count; i++)
        {
            double x = item.Align switch { OverlayAlign.Left => -width / 2, OverlayAlign.Right => width / 2 - widths[i], _ => -widths[i] / 2 };
            placed.Add((lines[i], x, -height / 2 + i * lineHeight, widths[i], lineHeight));
        }
        return new TextLayout(placed, new Rect(-width / 2, -height / 2, width, height), face, size);
    }
    private static FormattedText Formatted(string text, Typeface face, double size) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, Brushes.White, 1) { Trimming = TextTrimming.None };
    private static double Measure(string text, Typeface face, double size, double spacing) =>
        text.Length == 0 ? 0 : Formatted(text, face, size).WidthIncludingTrailingWhitespace + spacing * (text.Length - 1);

    private static Drawing TextContent(OverlayItem item, int chars, double aspect)
    {
        var layout = Layout(item, aspect);
        var group = new DrawingGroup();
        if (layout.Lines.Count == 0 || layout.Block.Width <= 0) { group.Freeze(); return group; }
        var glyphs = new GeometryGroup { FillRule = FillRule.Nonzero };
        var visibleLines = new List<(double X, double Y, double Width, double Height)>();
        int left = chars;
        foreach (var (text, x, y, _, h) in layout.Lines)
        {
            int count = Math.Clamp(left, 0, text.Length); left -= text.Length + 1;
            string shown = text[..count];
            // Centre the glyphs vertically in the line box when lines are spaced out.
            var sample = Formatted(text.Length > 0 ? text : " ", layout.Face, layout.Size);
            double top = y + (h - sample.Height) / 2;
            if (shown.Length > 0)
            {
                if (Math.Abs(item.LetterSpacing) < .01) glyphs.Children.Add(Formatted(shown, layout.Face, layout.Size).BuildGeometry(new Point(x, top)));
                else
                    for (int i = 0; i < shown.Length; i++)
                    {
                        double offset = i == 0 ? 0 : Formatted(shown[..i], layout.Face, layout.Size).WidthIncludingTrailingWhitespace;
                        glyphs.Children.Add(Formatted(shown[i].ToString(), layout.Face, layout.Size).BuildGeometry(new Point(x + offset + i * item.LetterSpacing, top)));
                    }
                visibleLines.Add((x, y, Measure(shown, layout.Face, layout.Size, item.LetterSpacing), h));
            }
        }
        using (var dc = group.Open())
        {
            var block = layout.Block;
            // The shape covers the whole text even while it types in, except per-line bars, which grow.
            if (item.Shape != OverlayShape.None && (visibleLines.Count > 0 || item.Shape != OverlayShape.Lines))
            {
                var shape = ShapeGeometry(item, block, visibleLines);
                var pen = item.ShapeBorderWidth > 0 ? new Pen(Brush(item.ShapeBorderColor), item.ShapeBorderWidth) { LineJoin = PenLineJoin.Round } : null;
                dc.DrawGeometry(Brush(item.ShapeColor), pen, shape);
            }
            if (glyphs.Children.Count > 0)
            {
                // Outline first at twice the width, then the fill on top, so the outline sits outside the letters.
                if (item.OutlineWidth > 0)
                    dc.DrawGeometry(null, new Pen(Brush(item.OutlineColor), item.OutlineWidth * 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, glyphs);
                dc.DrawGeometry(FillBrush(item, block), null, glyphs);
            }
            // Keep the drawing's bounds steady while text types in, so the item doesn't jump.
            dc.DrawRectangle(Brushes.Transparent, null, Rect.Inflate(block, item.OutlineWidth, item.OutlineWidth));
        }
        group.Freeze();
        return group;
    }
    private static Brush FillBrush(OverlayItem item, Rect block)
    {
        if (string.IsNullOrWhiteSpace(item.Fill2)) return Brush(item.Fill);
        // A gradient across the whole text block at the chosen angle.
        double a = item.GradientAngle * Math.PI / 180, dx = Math.Cos(a), dy = Math.Sin(a);
        double reach = Math.Abs(dx) * block.Width / 2 + Math.Abs(dy) * block.Height / 2;
        var c = new Point(block.X + block.Width / 2, block.Y + block.Height / 2);
        var brush = new LinearGradientBrush(ParseColor(item.Fill, Colors.White), ParseColor(item.Fill2, Colors.White), new Point(c.X - dx * reach, c.Y - dy * reach), new Point(c.X + dx * reach, c.Y + dy * reach))
        { MappingMode = BrushMappingMode.Absolute };
        brush.Freeze(); return brush;
    }
    // The padded rectangle around the text block.
    internal static Rect Padded(OverlayItem item, Rect block) => Rect.Inflate(block, item.Padding, item.Padding * .6);
    internal static Geometry ShapeGeometry(OverlayItem item, Rect block, IReadOnlyList<(double X, double Y, double Width, double Height)> lines)
    {
        var box = Padded(item, block);
        Geometry g;
        switch (item.Shape)
        {
            case OverlayShape.Lines:
            {
                // A bar behind each line; neighbouring bars merge into one shape.
                var group = new GeometryGroup { FillRule = FillRule.Nonzero };
                foreach (var (x, y, w, h) in lines)
                    group.Children.Add(new RectangleGeometry(new Rect(x - item.Padding, y - item.Padding * .15, w + item.Padding * 2, h + item.Padding * .3), item.Corner, item.Corner));
                g = item.ShapeBorderWidth > 0 && group.Children.Count > 1 ? group.GetOutlinedPathGeometry() : group;
                break;
            }
            case OverlayShape.Pill: g = new RectangleGeometry(box, box.Height / 2, box.Height / 2); break;
            case OverlayShape.Ellipse: g = new EllipseGeometry(new Point(box.X + box.Width / 2, box.Y + box.Height / 2), box.Width / 2 * 1.3, box.Height / 2 * 1.35); break;
            case OverlayShape.Bubble:
            {
                var body = new RectangleGeometry(box, Math.Min(item.Corner * 1.5 + 6, box.Height / 2), Math.Min(item.Corner * 1.5 + 6, box.Height / 2));
                g = new CombinedGeometry(GeometryCombineMode.Union, body, Tail(item, box)).GetFlattenedPathGeometry();
                break;
            }
            case OverlayShape.Arrow:
            {
                double half = box.Height / 2, head = box.Height * .75, cy = box.Y + half;
                g = Polygon(new[] { new Point(box.Left, box.Top), new Point(box.Right, box.Top), new Point(box.Right, cy - half * 1.5), new Point(box.Right + head, cy),
                    new Point(box.Right, cy + half * 1.5), new Point(box.Right, box.Bottom), new Point(box.Left, box.Bottom) });
                break;
            }
            case OverlayShape.Custom:
                g = Polygon(item.Points.Select(p => new Point(box.X + (p.X + .5) * box.Width, box.Y + (p.Y + .5) * box.Height)).ToArray());
                break;
            default: g = new RectangleGeometry(box, item.Corner, item.Corner); break;
        }
        g.Freeze(); return g;
    }
    // The bubble's tail: a triangle from the body towards the tip.
    private static Geometry Tail(OverlayItem item, Rect box)
    {
        var center = new Point(box.X + box.Width / 2, box.Y + box.Height / 2);
        var tip = new Point(item.TailX, item.TailY);
        var dir = tip - center; if (dir.Length < 1) dir = new Vector(0, 1);
        dir.Normalize(); var across = new Vector(-dir.Y, dir.X);
        double width = Math.Max(10, Math.Min(box.Width, box.Height) * .28);
        // The base sits inside the body so the two merge cleanly.
        var baseCenter = center + dir * Math.Min(box.Width, box.Height) * .25;
        return Polygon(new[] { baseCenter + across * width / 2, tip, baseCenter - across * width / 2 });
    }
    internal static Point TailTip(OverlayItem item) => new(item.TailX, item.TailY);
    private static Geometry Polygon(Point[] points)
    {
        var g = new StreamGeometry();
        using (var c = g.Open()) { c.BeginFigure(points[0], true, true); c.PolyLineTo(points.Skip(1).ToArray(), true, true); }
        g.Freeze(); return g;
    }

    // ---- Images ----
    internal const double ImageBox = 540;
    private static Drawing ImageContent(OverlayItem item, double t)
    {
        var group = new DrawingGroup();
        var picture = ProcessedFrame(item, t);
        if (picture == null)
        {
            // Missing file: a placeholder the size of a small picture.
            using var missing = group.Open();
            missing.DrawRoundedRectangle(Brush("#80252D36"), new Pen(Brush("#E5484D"), 3) { DashStyle = DashStyles.Dash }, new Rect(-160, -90, 320, 180), 10, 10);
            group.Freeze(); return group;
        }
        var size = ImageSize(picture.PixelWidth, picture.PixelHeight);
        var rect = new Rect(-size.Width / 2, -size.Height / 2, size.Width, size.Height);
        double radius = item.ImageCorner / 100 * Math.Min(rect.Width, rect.Height);
        using (var dc = group.Open())
        {
            var clip = new RectangleGeometry(rect, radius, radius);
            dc.PushClip(clip);
            dc.DrawImage(picture, rect);
            dc.Pop();
            if (item.BorderWidth > 0)
            {
                // The border sits outside the picture's edge.
                var outer = Rect.Inflate(rect, item.BorderWidth / 2, item.BorderWidth / 2);
                dc.DrawRoundedRectangle(null, new Pen(Brush(item.BorderColor), item.BorderWidth), outer, radius + item.BorderWidth / 2, radius + item.BorderWidth / 2);
            }
        }
        group.Freeze(); return group;
    }
    // A picture fits a 540-pixel square at scale 1 (half the height of a 1080p frame).
    internal static Size ImageSize(double w, double h) { double f = ImageBox / Math.Max(1, Math.Max(w, h)); return new Size(w * f, h * f); }

    private sealed record Source(BitmapSource[] Frames, double[] Starts, double Length);
    private static readonly Dictionary<string, (DateTime Stamp, Source? Source)> sources = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<(string, int, OverlayKey, string, double, bool, bool, double, double, double, double), BitmapSource> processed = new();
    // Loads a picture or animated GIF once; GIF frames are composited into full frames.
    private static Source? Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(path); if (!File.Exists(path)) return null; } catch { return null; }
        lock (sources)
        {
            if (sources.TryGetValue(path, out var hit) && hit.Stamp == stamp) return hit.Source;
            Source? loaded;
            try { loaded = Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase) ? GifFrames.Load(path) is { } gif ? new Source(gif.Frames, gif.Starts, gif.Length) : null : Still(path); }
            catch { loaded = null; }
            if (sources.Count > 24) { sources.Clear(); lock (processed) processed.Clear(); }
            sources[path] = (stamp, loaded);
            return loaded;
        }
    }
    private static Source Still(string path)
    {
        var image = new BitmapImage();
        image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile; image.UriSource = new Uri(path); image.EndInit(); image.Freeze();
        // Very large photos are shrunk; nothing larger than the frame is ever needed.
        BitmapSource frame = image;
        if (Math.Max(image.PixelWidth, image.PixelHeight) > 4096)
        {
            double f = 4096.0 / Math.Max(image.PixelWidth, image.PixelHeight);
            frame = new TransformedBitmap(image, new ScaleTransform(f, f)); frame.Freeze();
        }
        return new Source(new[] { frame }, new[] { 0.0 }, 0);
    }
    internal static bool IsAnimated(OverlayItem item) => item.Kind == OverlayKind.Image && Load(item.ImagePath) is { Frames.Length: > 1 };
    internal static bool Exists(OverlayItem item) => Load(item.ImagePath) != null;
    // Which GIF frame shows t seconds into the item, and the times its frames change.
    internal static int FrameAt(OverlayItem item, double t)
    {
        if (Load(item.ImagePath) is not { Frames.Length: > 1 } s || s.Length <= 0) return 0;
        double local = t % s.Length; if (local < 0) local += s.Length;
        int i = Array.BinarySearch(s.Starts, local);
        return i >= 0 ? i : Math.Max(0, ~i - 1);
    }
    internal static IEnumerable<double> FrameChanges(OverlayItem item)
    {
        if (Load(item.ImagePath) is not { Frames.Length: > 1 } s || s.Length <= 0) yield break;
        for (double loop = 0; loop < item.Length; loop += s.Length)
            foreach (var start in s.Starts) if (loop + start < item.Length) yield return loop + start;
    }
    internal static (int Width, int Height)? PictureSize(OverlayItem item) => Load(item.ImagePath) is { } s ? (s.Frames[0].PixelWidth, s.Frames[0].PixelHeight) : null;

    internal static BitmapSource? ProcessedFrame(OverlayItem item, double t)
    {
        if (Load(item.ImagePath) is not { } s) return null;
        int index = s.Frames.Length > 1 ? FrameAt(item, t) : 0;
        var key = (item.ImagePath, index, item.Key, item.KeyColor, Math.Round(item.KeyTolerance, 3), item.FlipX, item.FlipY, item.CropLeft, item.CropTop, item.CropRight, item.CropBottom);
        lock (processed)
        {
            if (processed.TryGetValue(key, out var hit)) return hit;
            var result = Process(s.Frames[index], item);
            if (processed.Count > 600) processed.Clear();
            processed[key] = result;
            return result;
        }
    }
    // Background removal, then flip, then crop (in the flipped picture, as shown).
    private static BitmapSource Process(BitmapSource frame, OverlayItem item)
    {
        BitmapSource result = frame;
        if (item.Key != OverlayKey.None) result = RemoveBackground(frame, item);
        if (item.FlipX || item.FlipY) { result = new TransformedBitmap(result, new ScaleTransform(item.FlipX ? -1 : 1, item.FlipY ? -1 : 1)); }
        if (item.CropLeft + item.CropTop + item.CropRight + item.CropBottom > 0)
        {
            int w = result.PixelWidth, h = result.PixelHeight;
            int x = (int)Math.Round(item.CropLeft * w), y = (int)Math.Round(item.CropTop * h);
            int cw = Math.Max(1, (int)Math.Round(w * (1 - item.CropLeft - item.CropRight))), ch = Math.Max(1, (int)Math.Round(h * (1 - item.CropTop - item.CropBottom)));
            result = new CroppedBitmap(result, new Int32Rect(Math.Min(x, w - 1), Math.Min(y, h - 1), Math.Min(cw, w - x), Math.Min(ch, h - y)));
        }
        if (!result.IsFrozen) result.Freeze();
        return result;
    }
    private static BitmapSource RemoveBackground(BitmapSource frame, OverlayItem item)
    {
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var pixels = new byte[stride * h]; bgra.CopyPixels(pixels, stride, 0);
        var key = item.Key switch { OverlayKey.White => Colors.White, OverlayKey.Black => Colors.Black, OverlayKey.Green => Color.FromRgb(0, 177, 64), _ => ParseColor(item.KeyColor, Colors.Lime) };
        double tol = item.KeyTolerance, soft = .12;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            double b = pixels[i] / 255.0, g = pixels[i + 1] / 255.0, r = pixels[i + 2] / 255.0;
            double d;
            if (item.Key == OverlayKey.Green)
            {
                // Greenness rather than distance, so shaded and lit parts of a screen both go.
                d = 1 - Math.Clamp((g - Math.Max(r, b)) * 2.5, 0, 1);
            }
            else
            {
                double dr = r - key.R / 255.0, dg = g - key.G / 255.0, db = b - key.B / 255.0;
                d = Math.Min(1, Math.Sqrt(dr * dr + dg * dg + db * db) / Math.Sqrt(3) * 2);
            }
            double keep = Math.Clamp((d - tol) / soft, 0, 1);
            if (keep >= 1) continue;
            pixels[i + 3] = (byte)(pixels[i + 3] * keep);
            // Take the green spill off the soft edge.
            if (item.Key == OverlayKey.Green) pixels[i + 1] = (byte)Math.Min(pixels[i + 1], Math.Max(pixels[i], pixels[i + 2]));
        }
        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze(); return result;
    }

    // ---- Helpers ----
    internal static Color ParseColor(string? text, Color fallback)
    {
        try { return text != null && ColorConverter.ConvertFromString(text) is Color c ? c : fallback; } catch { return fallback; }
    }
    private static readonly Dictionary<string, Brush> brushes = new();
    internal static Brush Brush(string? color)
    {
        color ??= "#FFFFFF";
        lock (brushes)
        {
            if (brushes.TryGetValue(color, out var b)) return b;
            var brush = new SolidColorBrush(ParseColor(color, Colors.White)); brush.Freeze();
            if (brushes.Count > 200) brushes.Clear();
            return brushes[color] = brush;
        }
    }

    // Renders one state of an item into a picture covering box (frame pixels).
    internal static BitmapSource Render(OverlayItem item, double t, int frameW, int frameH, Int32Rect box)
    {
        var visual = Visual(item, t, frameW, frameH);
        var host = new ContainerVisual { Transform = new TranslateTransform(-box.X, -box.Y) };
        host.Children.Add(visual);
        var bitmap = new RenderTargetBitmap(box.Width, box.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var straight = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        straight.Freeze();
        return straight;
    }
}

// Animated GIF frames, composited the way browsers show them (offsets, transparency, disposal).
internal static class GifFrames
{
    internal sealed record Animation(BitmapSource[] Frames, double[] Starts, double Length);
    internal static Animation? Load(string path)
    {
        var decoder = new GifBitmapDecoder(new Uri(path), BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0) return null;
        int width = Meta<ushort>(decoder.Metadata, "/logscrdesc/Width") is ushort lw and > 0 ? lw : decoder.Frames[0].PixelWidth;
        int height = Meta<ushort>(decoder.Metadata, "/logscrdesc/Height") is ushort lh and > 0 ? lh : decoder.Frames[0].PixelHeight;
        int stride = width * 4;
        var canvas = new byte[stride * height];
        var frames = new List<BitmapSource>(); var starts = new List<double>(); double time = 0;
        foreach (var frame in decoder.Frames.Take(1000))
        {
            var meta = frame.Metadata as BitmapMetadata;
            int left = Meta<ushort>(meta, "/imgdesc/Left") ?? 0, top = Meta<ushort>(meta, "/imgdesc/Top") ?? 0;
            int delay = Meta<ushort>(meta, "/grctlext/Delay") ?? 10, disposal = Meta<byte>(meta, "/grctlext/Disposal") ?? 0;
            byte[]? previous = disposal == 3 ? (byte[])canvas.Clone() : null;
            var part = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            int pw = part.PixelWidth, ph = part.PixelHeight, pstride = pw * 4;
            var pixels = new byte[pstride * ph]; part.CopyPixels(pixels, pstride, 0);
            for (int y = 0; y < ph; y++)
            {
                int cy = top + y; if (cy < 0 || cy >= height) continue;
                for (int x = 0; x < pw; x++)
                {
                    int cx = left + x; if (cx < 0 || cx >= width) continue;
                    int s = y * pstride + x * 4; if (pixels[s + 3] == 0) continue;
                    Buffer.BlockCopy(pixels, s, canvas, cy * stride + cx * 4, 4);
                }
            }
            var snapshot = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, canvas, stride); snapshot.Freeze();
            frames.Add(snapshot); starts.Add(time);
            // Browsers treat very short delays as 100 ms.
            time += (delay < 2 ? 10 : delay) / 100.0;
            if (disposal == 2)
                for (int y = Math.Max(0, top); y < Math.Min(height, top + ph); y++)
                    Array.Clear(canvas, y * stride + Math.Max(0, left) * 4, Math.Max(0, Math.Min(width, left + pw) - Math.Max(0, left)) * 4);
            else if (previous != null) canvas = previous;
        }
        return new Animation(frames.ToArray(), starts.ToArray(), time);
    }
    private static T? Meta<T>(BitmapMetadata? meta, string query) where T : struct
    {
        try { return meta?.GetQuery(query) is T value ? value : null; } catch { return null; }
    }
}
