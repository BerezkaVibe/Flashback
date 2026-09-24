using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        // Keyframes set where the item is at this moment.
        item = item.Posed(t);
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

    internal static Drawing Content(OverlayItem item, int chars, double t, double aspect) => item.Kind switch
    {
        OverlayKind.Image => ImageContent(item, t),
        OverlayKind.Shape => ShapeContent(item),
        OverlayKind.Video => VideoFrame(item),
        _ => TextContent(item, chars, aspect)
    };

    // ---- Shapes ----
    // A solid shape in its colour with its border. A blur or pixelate shape is drawn as a white
    // mask: the preview fills it with the treated video, and the export uses it to cut the effect.
    private static Drawing ShapeContent(OverlayItem item)
    {
        var group = new DrawingGroup();
        var geometry = ShapeIn(item.Shape, ShapeBox(item), item);
        using (var dc = group.Open())
        {
            if (item.IsLine)
            {
                // An open line in the shape colour, with its ends finished as arrows or dots.
                var brush = Brush(item.ShapeColor);
                dc.DrawGeometry(null, LinePen(item, item.LineWidth, brush), geometry);
                DrawCaps(dc, item, geometry, brush);
            }
            else if (item.IsRegion) dc.DrawGeometry(Brushes.White, null, geometry);
            else
            {
                var pen = item.ShapeBorderWidth > 0 ? LinePen(item, item.ShapeBorderWidth, Brush(item.ShapeBorderColor)) : null;
                dc.DrawGeometry(Brush(item.ShapeColor), pen, geometry);
            }
        }
        group.Freeze(); return group;
    }
    // Lines and outlines: round ends and joins, solid, dashed or dotted (a zero-length dash with round
    // ends is a dot; gaps are measured in line widths).
    private static Pen LinePen(OverlayItem item, double width, Brush brush)
    {
        var pen = new Pen(brush, width) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, DashCap = PenLineCap.Round };
        pen.DashStyle = item.Dash switch { OverlayDash.Dashed => new DashStyle(new[] { 2.5, 2.0 }, 0), OverlayDash.Dotted => new DashStyle(new[] { 0.0, 2.0 }, 0), _ => DashStyles.Solid };
        pen.Freeze(); return pen;
    }
    private static void DrawCaps(DrawingContext dc, OverlayItem item, Geometry line, Brush brush)
    {
        if (item.StartCap == OverlayCap.None && item.EndCap == OverlayCap.None) return;
        // The line passes through its points, so its ends and their directions come from them.
        var box = ShapeBox(item);
        var points = item.Points.Select(p => new Point(box.X + (p.X + .5) * box.Width, box.Y + (p.Y + .5) * box.Height)).ToList();
        if (points.Count < 2) return;
        double w = item.LineWidth;
        Cap(item.StartCap, points[0], points.Skip(1));
        Cap(item.EndCap, points[^1], Enumerable.Reverse(points).Skip(1));
        void Cap(OverlayCap cap, Point tip, IEnumerable<Point> along)
        {
            if (cap == OverlayCap.None) return;
            if (cap == OverlayCap.Dot) { dc.DrawEllipse(brush, null, tip, w * 1.2, w * 1.2); return; }
            // The arrow points the way the line leaves this end, measured a head's length back.
            double head = Math.Max(12, w * 3.2);
            var from = along.FirstOrDefault(p => (p - tip).Length >= head * .6, along.LastOrDefault(tip));
            var dir = tip - from; if (dir.Length < 1e-6) return; dir.Normalize();
            var across = new Vector(-dir.Y, dir.X);
            var back = tip - dir * head;
            var arrow = Polygon(new[] { tip + dir * w * .3, back + across * head * .6, back - across * head * .6 });
            dc.DrawGeometry(brush, new Pen(brush, Math.Max(1, w * .3)) { LineJoin = PenLineJoin.Round }, arrow);
        }
    }
    // A drawn shape through its points (fractions of the box): smooth curves for freehand drawing,
    // straight segments for clicked corners. Open unless it's closed and has at least three points.
    private static Geometry DrawnPath(OverlayItem item, Rect box)
    {
        var pts = item.Points.Select(p => new Point(box.X + (p.X + .5) * box.Width, box.Y + (p.Y + .5) * box.Height)).ToList();
        bool closed = item.Closed && pts.Count >= 3;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(pts[0], closed, closed);
            if (item.Shape == OverlayShape.Polyline || pts.Count < 3) c.PolyLineTo(pts.Skip(1).ToArray(), true, true);
            else c.PolyBezierTo(Smooth(pts, closed), true, true);
        }
        g.Freeze(); return g;
    }
    // Catmull-Rom through the points, as cubic Bézier control points.
    private static List<Point> Smooth(List<Point> p, bool closed)
    {
        var result = new List<Point>();
        int n = p.Count, segments = closed ? n : n - 1;
        Point At(int i) => closed ? p[((i % n) + n) % n] : p[Math.Clamp(i, 0, n - 1)];
        for (int i = 0; i < segments; i++)
        {
            Point p0 = At(i - 1), p1 = At(i), p2 = At(i + 1), p3 = At(i + 2);
            result.Add(p1 + (p2 - p0) / 6); result.Add(p2 - (p3 - p1) / 6); result.Add(p2);
        }
        return result;
    }
    internal static Geometry ShapeGeometryOf(OverlayItem item) => ShapeIn(item.Shape, ShapeBox(item), item);

    // ---- Picture masks ----
    // Pictures and videos are cut to a shape; Box is the rectangle with rounded corners.
    internal static Geometry MaskGeometry(OverlayItem item, Rect rect)
    {
        if (item.Mask is OverlayShape.Box or OverlayShape.None)
        {
            double radius = item.ImageCorner / 100 * Math.Min(rect.Width, rect.Height);
            var box = new RectangleGeometry(rect, radius, radius); box.Freeze(); return box;
        }
        return ShapeIn(item.Mask, rect, item with { ShapeCorners = OverlayItem.NoCorners });
    }
    internal static void DrawBorder(DrawingContext dc, OverlayItem item, Geometry mask)
    {
        if (item.BorderWidth <= 0) return;
        // The border hugs the outside of the shape.
        var pen = new Pen(Brush(item.BorderColor), item.BorderWidth * 2) { LineJoin = PenLineJoin.Round };
        var outside = new CombinedGeometry(GeometryCombineMode.Exclude, mask.GetWidenedPathGeometry(pen), mask);
        dc.DrawGeometry(Brush(item.BorderColor), null, outside);
    }

    // ---- Picture-in-picture video ----
    // The video's size on a 1080-line frame at scale 1 (fits a 540-pixel square), before cropping.
    internal static Rect VideoRect(OverlayItem item)
    {
        double w = Math.Max(16, item.VideoWidth > 0 ? item.VideoWidth : 1920), h = Math.Max(16, item.VideoHeight > 0 ? item.VideoHeight : 1080);
        double f = ImageBox / Math.Max(w, h);
        double cw = w * f * (1 - item.CropLeft - item.CropRight), ch = h * f * (1 - item.CropTop - item.CropBottom);
        return new Rect(-cw / 2, -ch / 2, cw, ch);
    }
    // The whole (uncropped) video frame around the item's centre, which sits in the middle of the kept part.
    internal static Rect VideoFullRect(OverlayItem item)
    {
        double w = Math.Max(16, item.VideoWidth > 0 ? item.VideoWidth : 1920), h = Math.Max(16, item.VideoHeight > 0 ? item.VideoHeight : 1080);
        double f = ImageBox / Math.Max(w, h), fw = w * f, fh = h * f;
        double cx = ((item.CropLeft + 1 - item.CropRight) / 2 - .5) * fw, cy = ((item.CropTop + 1 - item.CropBottom) / 2 - .5) * fh;
        return new Rect(-cx - fw / 2, -cy - fh / 2, fw, fh);
    }
    // The uncropped frame of a picture or video, for cropping on the preview.
    internal static Rect FullRect(OverlayItem item, double t) => item.Kind == OverlayKind.Video ? VideoFullRect(item) : Uncropped(item, t).Full;
    // The video's outline and border; the preview draws the playing video inside it, the export uses ffmpeg.
    private static Drawing VideoFrame(OverlayItem item)
    {
        var group = new DrawingGroup();
        var rect = VideoRect(item); var mask = MaskGeometry(item, rect);
        using (var dc = group.Open()) { dc.DrawGeometry(Brushes.Transparent, null, mask); DrawBorder(dc, item, mask); }
        group.Freeze(); return group;
    }

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
    // A text item's highlight around its (padded) words.
    internal static Geometry ShapeGeometry(OverlayItem item, Rect block, IReadOnlyList<(double X, double Y, double Width, double Height)> lines) =>
        ShapeIn(item.Shape, Padded(item, block), item, lines, aroundText: true);
    // A shape item's own box, centred on the item.
    internal static Rect ShapeBox(OverlayItem item) => new(-item.ShapeWidth / 2, -item.ShapeHeight / 2, item.ShapeWidth, item.ShapeHeight);
    // The outline of a shape filling box. Shapes around text grow a little (an ellipse must clear the
    // words); shapes on their own fit the box exactly.
    internal static Geometry ShapeIn(OverlayShape shape, Rect box, OverlayItem item, IReadOnlyList<(double X, double Y, double Width, double Height)>? lines = null, bool aroundText = false)
    {
        Geometry g;
        var c = new Point(box.X + box.Width / 2, box.Y + box.Height / 2);
        double rx = box.Width / 2, ry = box.Height / 2;
        Point On(double angle, double r) => new(c.X + Math.Cos(angle) * rx * r, c.Y + Math.Sin(angle) * ry * r);
        Point[] Spikes(int points, double inner, double start = -Math.PI / 2) =>
            Enumerable.Range(0, points * 2).Select(i => On(start + i * Math.PI / points, i % 2 == 0 ? 1 : inner)).ToArray();
        switch (shape)
        {
            case OverlayShape.Star: g = Polygon(Spikes(5, .45)); break;
            case OverlayShape.Burst: g = Polygon(Spikes(12, .72)); break;
            case OverlayShape.Hexagon: g = Polygon(Enumerable.Range(0, 6).Select(i => On(i * Math.PI / 3, 1)).ToArray()); break;
            case OverlayShape.Diamond: g = Polygon(new[] { new Point(c.X, box.Top), new Point(box.Right, c.Y), new Point(c.X, box.Bottom), new Point(box.Left, c.Y) }); break;
            case OverlayShape.Triangle: g = Polygon(new[] { new Point(c.X, box.Top), new Point(box.Right, box.Bottom), new Point(box.Left, box.Bottom) }); break;
            case OverlayShape.Ring:
            {
                double thick = Math.Max(4, Math.Min(box.Width, box.Height) * .12);
                var ring = new GeometryGroup { FillRule = FillRule.EvenOdd };
                ring.Children.Add(new EllipseGeometry(c, rx, ry)); ring.Children.Add(new EllipseGeometry(c, Math.Max(1, rx - thick), Math.Max(1, ry - thick)));
                g = ring; break;
            }
            case OverlayShape.Heart:
            {
                // Two lobes meeting at a point, drawn in a unit box and stretched to fit.
                var heart = new StreamGeometry();
                Point U(double x, double y) => new(box.X + x * box.Width, box.Y + y * box.Height);
                using (var h = heart.Open())
                {
                    h.BeginFigure(U(.5, .28), true, true);
                    h.BezierTo(U(.5, .02), U(0, .02), U(0, .32), true, true);
                    h.BezierTo(U(0, .62), U(.35, .78), U(.5, 1), true, true);
                    h.BezierTo(U(.65, .78), U(1, .62), U(1, .32), true, true);
                    h.BezierTo(U(1, .02), U(.5, .02), U(.5, .28), true, true);
                }
                g = heart; break;
            }
            case OverlayShape.Check:
            {
                Point U(double x, double y) => new(box.X + x * box.Width, box.Y + y * box.Height);
                g = Polygon(new[] { U(0, .55), U(.14, .41), U(.38, .64), U(.86, .08), U(1, .22), U(.38, .92) });
                break;
            }
            case OverlayShape.Cross:
            {
                Point U(double x, double y) => new(box.X + x * box.Width, box.Y + y * box.Height);
                g = Polygon(new[] { U(.14, 0), U(.5, .36), U(.86, 0), U(1, .14), U(.64, .5), U(1, .86), U(.86, 1), U(.5, .64), U(.14, 1), U(0, .86), U(.36, .5), U(0, .14) });
                break;
            }
            case OverlayShape.Ellipse when !aroundText: g = new EllipseGeometry(c, rx, ry); break;
            case OverlayShape.Freehand or OverlayShape.Polyline: g = DrawnPath(item, box); break;
            default: g = TextShape(item, shape, box, lines ?? Array.Empty<(double, double, double, double)>()); break;
        }
        if (item.CornersMoved && shape != OverlayShape.Custom && shape != OverlayShape.Bubble) g = Warp(g, box, item.ShapeCorners);
        if (!g.IsFrozen) g.Freeze();
        return g;
    }
    private static Geometry TextShape(OverlayItem item, OverlayShape shape, Rect box, IReadOnlyList<(double X, double Y, double Width, double Height)> lines)
    {
        Geometry g;
        switch (shape)
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
                // The body follows dragged corners; the tail keeps pointing where it was aimed.
                Geometry body = new RectangleGeometry(box, Math.Min(item.Corner * 1.5 + 6, box.Height / 2), Math.Min(item.Corner * 1.5 + 6, box.Height / 2));
                if (item.CornersMoved) body = Warp(body, box, item.ShapeCorners);
                g = new CombinedGeometry(GeometryCombineMode.Union, body, Tail(item, box)).GetFlattenedPathGeometry();
                g.Freeze(); return g;
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
        return g;
    }
    // Where the shape's four corners are after dragging (top-left, top-right, bottom-right, bottom-left).
    internal static Point[] ShapeCornerPoints(OverlayItem item, Rect box) => new[]
    {
        new Point(box.Left + item.ShapeCorners[0].X, box.Top + item.ShapeCorners[0].Y), new Point(box.Right + item.ShapeCorners[1].X, box.Top + item.ShapeCorners[1].Y),
        new Point(box.Right + item.ShapeCorners[2].X, box.Bottom + item.ShapeCorners[2].Y), new Point(box.Left + item.ShapeCorners[3].X, box.Bottom + item.ShapeCorners[3].Y),
    };
    // Stretches a shape drawn in box so the box's corners land on the dragged corners. Curves are
    // flattened first, so rounded corners and ellipses bend smoothly with it.
    private static Geometry Warp(Geometry shape, Rect box, IReadOnlyList<(double X, double Y)> offsets)
    {
        var q = new[] { new Vector(offsets[0].X, offsets[0].Y), new Vector(offsets[1].X, offsets[1].Y), new Vector(offsets[2].X, offsets[2].Y), new Vector(offsets[3].X, offsets[3].Y) };
        Point Map(Point p)
        {
            double u = (p.X - box.Left) / Math.Max(1e-6, box.Width), v = (p.Y - box.Top) / Math.Max(1e-6, box.Height);
            return p + (1 - u) * (1 - v) * q[0] + u * (1 - v) * q[1] + u * v * q[2] + (1 - u) * v * q[3];
        }
        var flat = shape.GetFlattenedPathGeometry(.25, ToleranceType.Absolute);
        var result = new PathGeometry { FillRule = flat.FillRule };
        foreach (var figure in flat.Figures)
        {
            var points = new List<Point>();
            foreach (var segment in figure.Segments)
                switch (segment)
                {
                    case LineSegment line: points.Add(Map(line.Point)); break;
                    case PolyLineSegment poly: points.AddRange(poly.Points.Select(Map)); break;
                }
            result.Figures.Add(new PathFigure(Map(figure.StartPoint), new PathSegment[] { new PolyLineSegment(points, true) }, figure.IsClosed) { IsFilled = figure.IsFilled });
        }
        return result;
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
        // Sized from the whole picture, so cropping trims the edges rather than enlarging what's left.
        double f = ImageScale(item);
        var rect = new Rect(-picture.PixelWidth * f / 2, -picture.PixelHeight * f / 2, picture.PixelWidth * f, picture.PixelHeight * f);
        using (var dc = group.Open())
        {
            var clip = MaskGeometry(item, rect);
            dc.PushClip(clip);
            dc.DrawImage(picture, rect);
            dc.Pop();
            DrawBorder(dc, item, clip);
        }
        group.Freeze(); return group;
    }
    // Reference pixels per picture pixel: at scale 1 the whole (uncropped) picture fits a 540-pixel
    // square, half the height of a 1080p frame.
    internal static double ImageScale(OverlayItem item) => Load(item.ImagePath) is { } s ? ImageBox / Math.Max(1, Math.Max(s.Frames[0].PixelWidth, s.Frames[0].PixelHeight)) : 1;
    // The whole picture (flipped and keyed, not cropped) and where it sits around the item's centre.
    internal static (BitmapSource? Picture, Rect Full) Uncropped(OverlayItem item, double t)
    {
        var picture = ProcessedFrame(item with { CropLeft = 0, CropTop = 0, CropRight = 0, CropBottom = 0 }, t);
        if (picture == null) return (null, Rect.Empty);
        double f = ImageScale(item), w = picture.PixelWidth * f, h = picture.PixelHeight * f;
        // The item's centre is the middle of the kept part.
        double cx = ((item.CropLeft + 1 - item.CropRight) / 2 - .5) * w, cy = ((item.CropTop + 1 - item.CropBottom) / 2 - .5) * h;
        return (picture, new Rect(-cx - w / 2, -cy - h / 2, w, h));
    }

    private sealed record Source(BitmapSource[] Frames, double[] Starts, double Length);
    private static readonly Dictionary<string, (DateTime Stamp, Source? Source)> sources = new(StringComparer.OrdinalIgnoreCase);
    // When each file was last checked on disk; lookups in between trust the cache (they run every frame).
    private static readonly Dictionary<string, long> checkedAt = new(StringComparer.OrdinalIgnoreCase);
    // Frees decoded pictures, e.g. when the trimmer closes.
    internal static void ClearCaches() { lock (sources) { sources.Clear(); checkedAt.Clear(); } lock (processed) { processed.Clear(); processedBytes = 0; } }
    private static readonly Dictionary<(string, int, OverlayKey, string, double, bool, bool, double, double, double, double), BitmapSource> processed = new();
    private static long processedBytes;
    private const long ProcessedBudget = 160L * 1024 * 1024;
    // Loads a picture or animated GIF once; GIF frames are composited into full frames.
    private static Source? Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        lock (sources)
            if (sources.TryGetValue(path, out var recent) && checkedAt.TryGetValue(path, out long at) && Stopwatch.GetElapsedTime(at).TotalSeconds < 2) return recent.Source;
        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(path); if (!File.Exists(path)) { lock (sources) { sources.Remove(path); checkedAt.Remove(path); } return null; } } catch { return null; }
        lock (sources)
        {
            checkedAt[path] = Stopwatch.GetTimestamp();
            if (sources.TryGetValue(path, out var hit) && hit.Stamp == stamp) return hit.Source;
            Source? loaded;
            try { loaded = Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase) ? GifFrames.Load(path) is { } gif ? new Source(gif.Frames, gif.Starts, gif.Length) : null : Still(path); }
            catch { loaded = null; }
            if (sources.Count > 24) { sources.Clear(); lock (processed) { processed.Clear(); processedBytes = 0; } }
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
            // Keep edited pictures within a memory budget; slider drags make a new one each step.
            long bytes = (long)result.PixelWidth * result.PixelHeight * 4;
            if (processedBytes + bytes > ProcessedBudget) { processed.Clear(); processedBytes = 0; }
            processed[key] = result; processedBytes += bytes;
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
        // Long or large GIFs are kept at a lower resolution so they stay within about 256 MB.
        int count = Math.Min(1000, decoder.Frames.Count);
        double shrink = Math.Min(1, Math.Sqrt(256.0 * 1024 * 1024 / Math.Max(1.0, (double)stride * height * count)));
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
            BitmapSource snapshot = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, canvas, stride);
            if (shrink < .999) snapshot = new WriteableBitmap(new TransformedBitmap(snapshot, new ScaleTransform(shrink, shrink)));
            snapshot.Freeze();
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
