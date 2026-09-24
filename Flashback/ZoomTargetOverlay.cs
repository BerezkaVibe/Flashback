using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Flashback;

// The zoom target on the preview: a teal box showing what the view shows at max zoom.
// Drag inside it to aim; drag a corner to resize, which sets the max zoom (1.1x to 8x).
internal sealed class ZoomTargetOverlay : FrameworkElement
{
    internal double VideoWidth = 1920, VideoHeight = 1080;
    internal double X { get; private set; } = .5;
    internal double Y { get; private set; } = .5;
    internal double MaxZoom { get; private set; } = 2;
    internal event Action? Changed, EditStarted, EditFinished;
    internal void Set(double x, double y, double maxZoom) { MaxZoom = Math.Clamp(maxZoom, 1.1, ZoomRegion.Limit); (X, Y) = Clamp(x, y); InvalidateVisual(); }
    private (double, double) Clamp(double x, double y) => (Math.Clamp(x, .5 / MaxZoom, 1 - .5 / MaxZoom), Math.Clamp(y, .5 / MaxZoom, 1 - .5 / MaxZoom));

    private static readonly Brush Shade = Frozen("#99000000"), Edge = Frozen("#5EEAD4"), Ink = Frozen("#0B1F1C");
    private static Brush Frozen(string c) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(c)!; b.Freeze(); return b; }
    public ZoomTargetOverlay() { Cursor = Cursors.SizeAll; }
    private Rect Video
    {
        get
        {
            double scale = Math.Min(ActualWidth / Math.Max(1, VideoWidth), ActualHeight / Math.Max(1, VideoHeight));
            double w = VideoWidth * scale, h = VideoHeight * scale;
            return new Rect((ActualWidth - w) / 2, (ActualHeight - h) / 2, w, h);
        }
    }
    private Rect Box
    {
        get
        {
            var v = Video; double w = v.Width / MaxZoom, h = v.Height / MaxZoom;
            return new Rect(v.X + X * v.Width - w / 2, v.Y + Y * v.Height - h / 2, w, h);
        }
    }
    protected override void OnRender(DrawingContext dc)
    {
        var v = Video; var box = Box;
        // Shade everything outside the box, so the zoomed view stands out.
        var outside = new GeometryGroup { FillRule = FillRule.EvenOdd };
        outside.Children.Add(new RectangleGeometry(v)); outside.Children.Add(new RectangleGeometry(box));
        dc.DrawGeometry(Shade, null, outside);
        dc.DrawRectangle(null, new Pen(Edge, 2), box);
        foreach (var c in Corners(box)) dc.DrawRectangle(Edge, null, new Rect(c.X - 5, c.Y - 5, 10, 10));
        var label = new FormattedText(MaxZoom.ToString("0.0", CultureInfo.InvariantCulture) + "×", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 11, Ink, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var tag = new Rect(box.X + 6, box.Y + 6, label.Width + 10, label.Height + 2);
        dc.DrawRoundedRectangle(Edge, null, tag, 3, 3); dc.DrawText(label, new Point(tag.X + 5, tag.Y + 1));
    }
    private static Point[] Corners(Rect r) => new[] { r.TopLeft, r.TopRight, r.BottomLeft, r.BottomRight };

    private enum Mode { None, Move, Resize }
    private Mode mode; private Point grab; private double grabX, grabY;
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var at = e.GetPosition(this); var box = Box;
        mode = Array.Exists(Corners(box), c => (c - at).Length <= 10) ? Mode.Resize : box.Contains(at) ? Mode.Move : Mode.Move;
        if (!box.Contains(at) && mode == Mode.Move)
        {
            // Clicking outside the box aims it there directly.
            var v = Video; (X, Y) = Clamp((at.X - v.X) / v.Width, (at.Y - v.Y) / v.Height); box = Box;
        }
        grab = at; grabX = X; grabY = Y;
        EditStarted?.Invoke(); CaptureMouse(); Changed?.Invoke(); InvalidateVisual(); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var at = e.GetPosition(this); var v = Video;
        if (mode == Mode.None) { Cursor = Array.Exists(Corners(Box), c => (c - at).Length <= 10) ? Cursors.SizeNWSE : Cursors.SizeAll; return; }
        if (mode == Mode.Move) (X, Y) = Clamp(grabX + (at.X - grab.X) / v.Width, grabY + (at.Y - grab.Y) / v.Height);
        else
        {
            // Resize around the centre: the box's half-width sets the zoom (aspect stays the video's).
            double centerX = v.X + X * v.Width, centerY = v.Y + Y * v.Height;
            double half = Math.Max(Math.Abs(at.X - centerX) / v.Width, Math.Abs(at.Y - centerY) / v.Height);
            MaxZoom = Math.Clamp(.5 / Math.Max(1e-3, half), 1.1, ZoomRegion.Limit); (X, Y) = Clamp(X, Y);
        }
        Changed?.Invoke(); InvalidateVisual();
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { base.OnMouseLeftButtonUp(e); Finish(); }
    protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); Finish(); }
    private void Finish() { if (mode == Mode.None) return; mode = Mode.None; ReleaseMouseCapture(); EditFinished?.Invoke(); }
}
