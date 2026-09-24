using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Flashback;

// Shows text and image items over the preview, drawn exactly as the export draws them, and lets the
// selected item be moved, scaled and rotated in place. Items stuck to the video follow the preview's
// zoom; the others stay put. Dragging snaps to the centre lines, edges and safe margins of the frame.
internal sealed class OverlayLayer : FrameworkElement
{
    internal double VideoWidth = 1920, VideoHeight = 1080;
    private IReadOnlyList<OverlayItem> items = Array.Empty<OverlayItem>();
    internal IReadOnlyList<OverlayItem> Items { get => items; set { items = value; Refresh(); } }
    internal int Selected { get => selected; set { selected = value; hovering = value >= 0 && IsMouseOver && ItemAt(Mouse.GetPosition(this)) == value; Refresh(); } }
    private int selected = -1;
    private double time;
    internal double Time { get => time; set { if (Math.Abs(time - value) < 1e-9) return; time = value; Refresh(onlyIfChanged: true); } }
    // The preview's zoom (in this element's coordinates) and the part of the video it shows.
    private Matrix zoom = Matrix.Identity; private Rect? zoomClip;
    internal void SetZoom(Matrix matrix, Rect? clip) { if (zoom == matrix && zoomClip == clip) return; zoom = matrix; zoomClip = clip; Refresh(); }
    // Only the selected item can be dragged; clicks on others select them.
    internal event Action<int>? Picked;
    internal event Action? EditStarted, EditFinished;
    internal event Action<OverlayItem>? Changed;

    private readonly ContainerVisual stuck = new(), screen = new();
    private readonly DrawingVisual handles = new();
    private readonly Dictionary<OverlayItem, (int Chars, int Frame, double Aspect, Drawing Content)> contents = new(ReferenceEqualityComparer.Instance);
    public OverlayLayer()
    {
        AddVisualChild(stuck); AddVisualChild(screen); AddVisualChild(handles);
        Focusable = false; SizeChanged += (_, _) => Refresh();
    }
    protected override int VisualChildrenCount => 3;
    protected override Visual GetVisualChild(int index) => index switch { 0 => stuck, 1 => screen, _ => handles };

    private Rect Video
    {
        get
        {
            double scale = Math.Min(ActualWidth / Math.Max(1, VideoWidth), ActualHeight / Math.Max(1, VideoHeight));
            double w = VideoWidth * scale, h = VideoHeight * scale;
            return new Rect((ActualWidth - w) / 2, (ActualHeight - h) / 2, w, h);
        }
    }
    // Frame pixels to this element, for an item (with the preview zoom when it is stuck to the video).
    private Matrix FrameToScreen(OverlayItem item)
    {
        var v = Video; var m = Matrix.Identity;
        m.Scale(v.Width / VideoWidth, v.Height / VideoHeight); m.Translate(v.X, v.Y);
        return item.StickToVideo ? m * zoom : m;
    }
    private bool Visible(OverlayItem o) => time >= o.Start - 1e-9 && time < o.End;
    internal Drawing ContentFor(OverlayItem item, OverlayState state)
    {
        double local = time - item.Start, aspect = VideoWidth / Math.Max(1, VideoHeight);
        int frame = item.Kind == OverlayKind.Image ? OverlayRenderer.FrameAt(item, local) : 0;
        if (contents.TryGetValue(item, out var hit) && hit.Chars == state.Chars && hit.Frame == frame && hit.Aspect == aspect) return hit.Content;
        var content = OverlayRenderer.Content(item, state.Chars, local, aspect);
        contents[item] = (state.Chars, frame, aspect, content);
        return content;
    }
    // What was last drawn: each visible item with its look. Playback only redraws when this changes.
    private List<(OverlayItem, OverlayState, int)> drawn = new();
    private void Refresh(bool onlyIfChanged = false)
    {
        var showing = items.Where(Visible).Select(o => (o, o.StateAt(time - o.Start), o.Kind == OverlayKind.Image ? OverlayRenderer.FrameAt(o, time - o.Start) : 0)).ToList();
        if (onlyIfChanged && showing.SequenceEqual(drawn)) return;
        drawn = showing;
        stuck.Children.Clear(); screen.Children.Clear();
        // Forget drawings for items that were edited or removed.
        foreach (var gone in contents.Keys.Where(k => !items.Contains(k)).ToList()) contents.Remove(gone);
        var v = Video;
        stuck.Transform = new MatrixTransform(zoom);
        stuck.Clip = new RectangleGeometry(zoomClip ?? v); screen.Clip = new RectangleGeometry(v);
        foreach (var item in OverlayOrder.BackToFront(items))
        {
            if (!Visible(item) || ActualWidth <= 0) continue;
            double local = time - item.Start;
            var state = item.StateAt(local);
            var visual = OverlayRenderer.Visual(item, local, VideoWidth, VideoHeight, ContentFor(item, state));
            var m = Matrix.Identity; m.Scale(v.Width / VideoWidth, v.Height / VideoHeight); m.Translate(v.X, v.Y);
            visual.Transform = new MatrixTransform(m);
            (item.StickToVideo ? stuck : screen).Children.Add(visual);
        }
        DrawHandles();
    }

    // ---- Handles ----
    private static readonly Brush HandleFill = Frozen(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B))), Guide = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x4F, 0xD8)));
    private static readonly Pen HandlePen = FrozenPen(new Pen(Brushes.White, 1.5)), BoxPen = FrozenPen(new Pen(HandleFill, 1.5) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) }), GuidePen = FrozenPen(new Pen(Guide, 1));
    private static Brush Frozen(Brush b) { b.Freeze(); return b; }
    private static Pen FrozenPen(Pen p) { p.Freeze(); return p; }
    private OverlayItem? Current => selected >= 0 && selected < items.Count ? items[selected] : null;
    private (double X, double Y)? guideX, guideY;
    private Matrix ItemToScreen(OverlayItem item, out Drawing content, out OverlayState state)
    {
        state = item.StateAt(Math.Clamp(time - item.Start, 0, item.Length));
        content = ContentFor(item, state);
        return OverlayRenderer.Placement(item, state, VideoWidth, VideoHeight) * FrameToScreen(item);
    }
    private Point[] Corners(Rect r, Matrix m) => new[] { m.Transform(r.TopLeft), m.Transform(r.TopRight), m.Transform(r.BottomRight), m.Transform(r.BottomLeft) };
    private Rect HandleBox(OverlayItem item, Drawing content)
    {
        var b = content.Bounds;
        return b.IsEmpty ? new Rect(-40, -20, 80, 40) : b;
    }
    private void DrawHandles()
    {
        using var dc = handles.RenderOpen();
        // Handles only show while the pointer is on the item or dragging it, so the rest of the
        // time the preview looks exactly like the export.
        if (Current is not { } item || !Visible(item) || (!hovering && grip == Grip.None)) return;
        var m = ItemToScreen(item, out var content, out _);
        var box = HandleBox(item, content); var c = Corners(box, m);
        var outline = new StreamGeometry();
        using (var g = outline.Open()) { g.BeginFigure(c[0], false, true); g.PolyLineTo(c.Skip(1).ToArray(), true, false); }
        dc.DrawGeometry(null, BoxPen, outline);
        foreach (var p in c) dc.DrawRectangle(HandleFill, HandlePen, new Rect(p.X - 4.5, p.Y - 4.5, 9, 9));
        var (rotate, top) = RotateHandle(c);
        dc.DrawLine(BoxPen, top, rotate); dc.DrawEllipse(HandleFill, HandlePen, rotate, 5.5, 5.5);
        if (item.Kind == OverlayKind.Text)
            foreach (var side in SideHandles(c)) dc.DrawRoundedRectangle(HandleFill, HandlePen, new Rect(side.X - 3, side.Y - 7, 6, 14), 2, 2);
        if (item.Kind == OverlayKind.Text && item.Shape == OverlayShape.Bubble)
            dc.DrawEllipse(Guide, HandlePen, m.Transform(OverlayRenderer.TailTip(item)), 5, 5);
        if (item.Kind == OverlayKind.Text && item.Shape == OverlayShape.Custom)
            foreach (var p in CustomPoints(item, m)) dc.DrawEllipse(Guide, HandlePen, p, 4.5, 4.5);
        var v = Video;
        if (guideX is { } gx) dc.DrawLine(GuidePen, new Point(gx.X, v.Top), new Point(gx.X, v.Bottom));
        if (guideY is { } gy) dc.DrawLine(GuidePen, new Point(v.Left, gy.Y), new Point(v.Right, gy.Y));
    }
    private static (Point Handle, Point Top) RotateHandle(Point[] c)
    {
        var top = new Point((c[0].X + c[1].X) / 2, (c[0].Y + c[1].Y) / 2);
        var up = new Vector(c[0].X - c[3].X, c[0].Y - c[3].Y); if (up.Length < 1) up = new Vector(0, -1); up.Normalize();
        return (top + up * 22, top);
    }
    private static Point[] SideHandles(Point[] c) => new[] { new Point((c[0].X + c[3].X) / 2, (c[0].Y + c[3].Y) / 2), new Point((c[1].X + c[2].X) / 2, (c[1].Y + c[2].Y) / 2) };
    private Rect PaddedBox(OverlayItem item) => OverlayRenderer.Padded(item, OverlayRenderer.Layout(item, VideoWidth / Math.Max(1, VideoHeight)).Block);
    private IEnumerable<Point> CustomPoints(OverlayItem item, Matrix m)
    {
        var box = PaddedBox(item);
        return item.Points.Select(p => m.Transform(new Point(box.X + (p.X + .5) * box.Width, box.Y + (p.Y + .5) * box.Height)));
    }

    // ---- Mouse ----
    private enum Grip { None, Move, Scale, Rotate, WrapLeft, WrapRight, Tail, Point }
    private Grip grip; private Point press; private OverlayItem? original; private int pointIndex; private bool moved;
    protected override HitTestResult? HitTestCore(PointHitTestParameters p) => ItemAt(p.HitPoint) >= 0 || GripAt(p.HitPoint).Grip != Grip.None ? new PointHitTestResult(this, p.HitPoint) : null;
    private int ItemAt(Point p)
    {
        foreach (var (item, index) in items.Select((o, i) => (o, i)).OrderByDescending(x => x.o.Layer).ThenByDescending(x => x.o.Start))
        {
            if (!Visible(item)) continue;
            var m = ItemToScreen(item, out var content, out _);
            if (!m.HasInverse) continue;
            if (Rect.Inflate(HandleBox(item, content), 4, 4).Contains(Inverse(m).Transform(p))) return index;
        }
        return -1;
    }
    private static Matrix Inverse(Matrix m) { m.Invert(); return m; }
    private (Grip Grip, int Point) GripAt(Point p)
    {
        if (Current is not { } item || !Visible(item)) return (Grip.None, -1);
        var m = ItemToScreen(item, out var content, out _);
        var c = Corners(HandleBox(item, content), m);
        bool Near(Point a, double r = 8) => (a - p).Length <= r;
        if (item.Kind == OverlayKind.Text && item.Shape == OverlayShape.Custom)
        {
            var pts = CustomPoints(item, m).ToList();
            for (int i = 0; i < pts.Count; i++) if (Near(pts[i], 7)) return (Grip.Point, i);
        }
        if (item.Kind == OverlayKind.Text && item.Shape == OverlayShape.Bubble && Near(m.Transform(OverlayRenderer.TailTip(item)))) return (Grip.Tail, -1);
        if (Near(RotateHandle(c).Handle)) return (Grip.Rotate, -1);
        if (c.Any(x => Near(x))) return (Grip.Scale, -1);
        if (item.Kind == OverlayKind.Text) { var s = SideHandles(c); if (Near(s[0])) return (Grip.WrapLeft, -1); if (Near(s[1])) return (Grip.WrapRight, -1); }
        return (Grip.None, -1);
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var p = e.GetPosition(this);
        var (g, point) = GripAt(p);
        if (g == Grip.None)
        {
            int hit = ItemAt(p);
            if (hit < 0) return;
            if (hit != selected) { Picked?.Invoke(hit); }
            g = Grip.Move;
            // Double-clicking a custom shape's edge adds a corner there.
            if (e.ClickCount == 2 && Current is { Kind: OverlayKind.Text, Shape: OverlayShape.Custom } custom) { AddPoint(custom, p); e.Handled = true; return; }
        }
        if (Current == null) return;
        grip = g; pointIndex = point; press = p; original = Current; moved = false; hovering = true;
        CaptureMouse(); DrawHandles(); e.Handled = true;
    }
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        // Right-click a custom shape's corner to remove it (a shape keeps at least three).
        var (g, point) = GripAt(e.GetPosition(this));
        if (g == Grip.Point && Current is { } item && item.Points.Count > 3)
        {
            EditStarted?.Invoke(); Changed?.Invoke(item with { Points = item.Points.Where((_, i) => i != point).ToArray() }); EditFinished?.Invoke(); e.Handled = true;
        }
    }
    private void AddPoint(OverlayItem item, Point p)
    {
        var m = ItemToScreen(item, out _, out _); if (!m.HasInverse) return;
        var box = PaddedBox(item); var local = Inverse(m).Transform(p);
        var at = ((local.X - box.X) / box.Width - .5, (local.Y - box.Y) / box.Height - .5);
        // Insert between the two corners whose edge is nearest.
        var pts = item.Points.ToList(); int best = 0; double bestDistance = double.MaxValue;
        for (int i = 0; i < pts.Count; i++)
        {
            var a = pts[i]; var b = pts[(i + 1) % pts.Count];
            double d = DistanceToSegment(at, a, b);
            if (d < bestDistance) { bestDistance = d; best = i + 1; }
        }
        pts.Insert(best, at);
        EditStarted?.Invoke(); Changed?.Invoke(item with { Points = pts.ToArray() }); EditFinished?.Invoke();
    }
    private static double DistanceToSegment((double X, double Y) p, (double X, double Y) a, (double X, double Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, len = dx * dx + dy * dy;
        double t = len == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len, 0, 1);
        double x = a.X + t * dx - p.X, y = a.Y + t * dy - p.Y; return Math.Sqrt(x * x + y * y);
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        if (grip == Grip.None || original is not { } o)
        {
            var (g, _) = GripAt(p);
            bool over = g != Grip.None || (selected >= 0 && ItemAt(p) == selected);
            if (over != hovering) { hovering = over; DrawHandles(); }
            Cursor = g switch { Grip.Scale => Cursors.SizeNWSE, Grip.Rotate => Cursors.Hand, Grip.WrapLeft or Grip.WrapRight => Cursors.SizeWE, Grip.Tail or Grip.Point => Cursors.Cross, _ => ItemAt(p) >= 0 ? Cursors.SizeAll : null };
            ToolTip = g == Grip.None && ItemAt(p) >= 0 ? "Drag to move · scroll to scale · Shift+scroll to rotate · Ctrl+scroll for opacity" : null;
            return;
        }
        if (!moved && (p - press).Length < 2) return;
        if (!moved) { moved = true; EditStarted?.Invoke(); }
        var toScreen = FrameToScreen(o);
        var m = ItemToScreen(o, out var content, out var state);
        var center = m.Transform(new Point(0, 0));
        OverlayItem next = o;
        switch (grip)
        {
            case Grip.Move:
            {
                var delta = Inverse(toScreen).Transform(p - press);
                double x = o.X + delta.X / VideoWidth, y = o.Y + delta.Y / VideoHeight;
                (x, y) = SnapPosition(o, content, state, x, y);
                next = o with { X = x, Y = y };
                break;
            }
            case Grip.Scale:
            {
                double from = (press - center).Length, to = (p - center).Length;
                next = o with { Scale = Math.Clamp(o.Scale * to / Math.Max(1, from), OverlayItem.MinScale, OverlayItem.MaxScale) };
                break;
            }
            case Grip.Rotate:
            {
                double a0 = Math.Atan2(press.Y - center.Y, press.X - center.X), a1 = Math.Atan2(p.Y - center.Y, p.X - center.X);
                double angle = o.Rotation + (a1 - a0) * 180 / Math.PI;
                angle = ((angle % 360) + 540) % 360 - 180;
                // Shift steps by 15°; otherwise it settles on straight angles when close.
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) angle = Math.Round(angle / 15) * 15;
                else foreach (double straight in new[] { -180.0, -90, 0, 90, 180 }) if (Math.Abs(angle - straight) < 3) angle = straight;
                next = o with { Rotation = angle };
                break;
            }
            case Grip.WrapLeft or Grip.WrapRight:
            {
                if (!m.HasInverse) break;
                var local = Inverse(m).Transform(p);
                double half = Math.Max(20, Math.Abs(local.X));
                // Wrap width is kept as a fraction of the frame width, at this item's scale.
                double aspect = VideoWidth / Math.Max(1, VideoHeight);
                next = o with { WrapWidth = Math.Clamp(2 * half * o.Scale * state.Scale / (aspect * OverlayItem.Reference), .02, 1.5) };
                break;
            }
            case Grip.Tail:
            {
                if (!m.HasInverse) break;
                var local = Inverse(m).Transform(p);
                next = o with { TailX = local.X, TailY = local.Y };
                break;
            }
            case Grip.Point:
            {
                if (!m.HasInverse || pointIndex < 0 || pointIndex >= o.Points.Count) break;
                var box = PaddedBox(o); var local = Inverse(m).Transform(p);
                var pts = o.Points.ToArray(); pts[pointIndex] = ((local.X - box.X) / box.Width - .5, (local.Y - box.Y) / box.Height - .5);
                next = o with { Points = pts };
                break;
            }
        }
        Changed?.Invoke(next);
    }
    // Centre lines, frame edges and a 5% safe margin pull the item in when it comes within a few pixels.
    private (double X, double Y) SnapPosition(OverlayItem o, Drawing content, OverlayState state, double x, double y)
    {
        guideX = guideY = null;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return (x, y);
        var toScreen = FrameToScreen(o);
        double pixel = Math.Max(1e-6, toScreen.M11) * VideoWidth, pixelY = Math.Max(1e-6, toScreen.M22) * VideoHeight;
        double reach = 6;
        var bounds = Rect.Transform(content.Bounds.IsEmpty ? new Rect(0, 0, 0, 0) : content.Bounds, OverlayRenderer.Placement(o with { X = x, Y = y }, state, VideoWidth, VideoHeight));
        double halfW = bounds.Width / 2 / VideoWidth, halfH = bounds.Height / 2 / VideoHeight;
        double centreX = x + state.Dx, centreY = y + state.Dy;
        double? bestX = null, bestY = null; double gapX = reach, gapY = reach;
        foreach (double line in new[] { .5, 0, 1, .05, .95 })
            foreach (double offset in line == .5 ? new[] { 0.0 } : new[] { -halfW, halfW })
            {
                double gap = Math.Abs(centreX + offset - line) * pixel;
                if (gap < gapX) { gapX = gap; bestX = line - offset - state.Dx; guideX = (line, 0); }
            }
        foreach (double line in new[] { .5, 0, 1, .05, .95 })
            foreach (double offset in line == .5 ? new[] { 0.0 } : new[] { -halfH, halfH })
            {
                double gap = Math.Abs(centreY + offset - line) * pixelY;
                if (gap < gapY) { gapY = gap; bestY = line - offset - state.Dy; guideY = (0, line); }
            }
        // Guides are drawn in this element's coordinates.
        if (guideX is { } gx) guideX = (toScreen.Transform(new Point(gx.X * VideoWidth, 0)).X, 0);
        if (guideY is { } gy) guideY = (0, toScreen.Transform(new Point(0, gy.Y * VideoHeight)).Y);
        return (bestX ?? x, bestY ?? y);
    }
    // Over an item: the wheel scales it, Shift+wheel rotates it and Ctrl+wheel changes its opacity.
    internal event Action<int, string, Func<OverlayItem, OverlayItem>>? WheelAdjusted;
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        int index = ItemAt(e.GetPosition(this));
        if (index < 0 || grip != Grip.None) return;
        double notches = e.Delta / 120.0;
        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.Shift)
            WheelAdjusted?.Invoke(index, "wheelRotation", o => o with { Rotation = Math.Round((((o.Rotation + notches * 5) % 360) + 540) % 360 - 180, 1) });
        else if (mods == ModifierKeys.Control)
            WheelAdjusted?.Invoke(index, "wheelOpacity", o => o with { Opacity = Math.Clamp(Math.Round(o.Opacity + notches * .05, 2), 0, 1) });
        else if (mods == ModifierKeys.None)
            WheelAdjusted?.Invoke(index, "wheelScale", o => o with { Scale = Math.Clamp(Math.Round(o.Scale * Math.Pow(1.08, notches), 3), OverlayItem.MinScale, OverlayItem.MaxScale) });
        else return;
        e.Handled = true;
    }
    private bool hovering;
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); if (hovering && grip == Grip.None) { hovering = false; DrawHandles(); } }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { base.OnMouseLeftButtonUp(e); Finish(); }
    protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); Finish(); }
    private void Finish()
    {
        if (grip == Grip.None) return;
        grip = Grip.None; original = null; guideX = guideY = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (moved) { moved = false; EditFinished?.Invoke(); }
        hovering = IsMouseOver && (GripAt(Mouse.GetPosition(this)).Grip != Grip.None || ItemAt(Mouse.GetPosition(this)) == selected);
        DrawHandles();
    }
}
