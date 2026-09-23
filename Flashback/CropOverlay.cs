using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace Flashback;

// Shows 1-based numbers for the section chips.
internal sealed class PlusOneConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is int i ? (i + 1).ToString(culture) : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

// Crop rectangle drawn over the letterboxed video. Crop is normalized (0..1) to the
// video frame, so it survives window resizes and maps to source pixels on export.
internal sealed class CropOverlay : FrameworkElement
{
    internal int VideoWidth = 1920, VideoHeight = 1080;
    internal Rect? Crop { get => crop; set { crop = value; InvalidateVisual(); } }
    private Rect? crop;
    internal bool Editing { get => editing; set { editing = value; IsHitTestVisible = value; InvalidateVisual(); } }
    private bool editing;
    // Width / height of the output; 0 keeps the rectangle free.
    internal double Aspect { get => aspect; set { aspect = value; if (crop is { } r && value > 0) Crop = Fit(r, r.X + r.Width / 2, r.Y + r.Height / 2); } }
    private double aspect;
    internal event Action? CropChanged;
    private enum Grip { None, Move, Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight, New }
    private Grip grip; private Point anchor; private Rect original;
    private static readonly Brush Shade = Frozen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)));
    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
    public CropOverlay() { IsHitTestVisible = false; Focusable = false; }

    private Rect Frame()
    {
        double scale = Math.Min(ActualWidth / Math.Max(1, VideoWidth), ActualHeight / Math.Max(1, VideoHeight));
        double w = VideoWidth * scale, h = VideoHeight * scale;
        return new Rect((ActualWidth - w) / 2, (ActualHeight - h) / 2, w, h);
    }
    private Rect ToScreen(Rect n) { var f = Frame(); return new Rect(f.X + n.X * f.Width, f.Y + n.Y * f.Height, n.Width * f.Width, n.Height * f.Height); }
    private Point ToNormal(Point p) { var f = Frame(); return new Point(Math.Clamp((p.X - f.X) / f.Width, 0, 1), Math.Clamp((p.Y - f.Y) / f.Height, 0, 1)); }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (crop is not { } n) return;
        var f = Frame(); var r = ToScreen(n);
        dc.DrawRectangle(Shade, null, new Rect(f.X, f.Y, f.Width, Math.Max(0, r.Y - f.Y)));
        dc.DrawRectangle(Shade, null, new Rect(f.X, r.Bottom, f.Width, Math.Max(0, f.Bottom - r.Bottom)));
        dc.DrawRectangle(Shade, null, new Rect(f.X, r.Y, Math.Max(0, r.X - f.X), r.Height));
        dc.DrawRectangle(Shade, null, new Rect(r.Right, r.Y, Math.Max(0, f.Right - r.Right), r.Height));
        var accent = (Brush?)TryFindResource("Accent") ?? Brushes.White;
        dc.DrawRectangle(null, new Pen(accent, 1.5), r);
        if (!editing) return;
        var thin = new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 1);
        for (int i = 1; i < 3; i++)
        {
            dc.DrawLine(thin, new Point(r.X + r.Width * i / 3, r.Y), new Point(r.X + r.Width * i / 3, r.Bottom));
            dc.DrawLine(thin, new Point(r.X, r.Y + r.Height * i / 3), new Point(r.Right, r.Y + r.Height * i / 3));
        }
        foreach (var c in new[] { r.TopLeft, r.TopRight, r.BottomLeft, r.BottomRight })
            dc.DrawRectangle(accent, null, new Rect(c.X - 5, c.Y - 5, 10, 10));
    }
    private Grip HitTest(Point p)
    {
        if (crop is not { } n) return Grip.New;
        var r = ToScreen(n); const double near = 10;
        bool left = Math.Abs(p.X - r.Left) <= near, right = Math.Abs(p.X - r.Right) <= near, top = Math.Abs(p.Y - r.Top) <= near, bottom = Math.Abs(p.Y - r.Bottom) <= near;
        bool insideX = p.X >= r.Left - near && p.X <= r.Right + near, insideY = p.Y >= r.Top - near && p.Y <= r.Bottom + near;
        if (top && left) return Grip.TopLeft; if (top && right) return Grip.TopRight;
        if (bottom && left) return Grip.BottomLeft; if (bottom && right) return Grip.BottomRight;
        if (left && insideY) return Grip.Left; if (right && insideY) return Grip.Right;
        if (top && insideX) return Grip.Top; if (bottom && insideX) return Grip.Bottom;
        return r.Contains(p) ? Grip.Move : Grip.New;
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!editing) return;
        var p = e.GetPosition(this); grip = HitTest(p); anchor = ToNormal(p);
        original = crop ?? new Rect(anchor, new Size(0, 0));
        CaptureMouse(); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!editing) return;
        var p = e.GetPosition(this);
        if (!IsMouseCaptured)
        {
            Cursor = HitTest(p) switch
            {
                Grip.Move => Cursors.SizeAll, Grip.Left or Grip.Right => Cursors.SizeWE, Grip.Top or Grip.Bottom => Cursors.SizeNS,
                Grip.TopLeft or Grip.BottomRight => Cursors.SizeNWSE, Grip.TopRight or Grip.BottomLeft => Cursors.SizeNESW, _ => Cursors.Cross
            };
            return;
        }
        var n = ToNormal(p); double dx = n.X - anchor.X, dy = n.Y - anchor.Y; var o = original;
        double l = o.Left, t = o.Top, rt = o.Right, b = o.Bottom;
        switch (grip)
        {
            case Grip.Move:
                double x = Math.Clamp(o.X + dx, 0, 1 - o.Width), y = Math.Clamp(o.Y + dy, 0, 1 - o.Height);
                Crop = new Rect(x, y, o.Width, o.Height); CropChanged?.Invoke(); return;
            case Grip.New: l = Math.Min(anchor.X, n.X); rt = Math.Max(anchor.X, n.X); t = Math.Min(anchor.Y, n.Y); b = Math.Max(anchor.Y, n.Y); break;
            default:
                if (grip is Grip.Left or Grip.TopLeft or Grip.BottomLeft) l = Math.Min(n.X, rt - .05);
                if (grip is Grip.Right or Grip.TopRight or Grip.BottomRight) rt = Math.Max(n.X, l + .05);
                if (grip is Grip.Top or Grip.TopLeft or Grip.TopRight) t = Math.Min(n.Y, b - .05);
                if (grip is Grip.Bottom or Grip.BottomLeft or Grip.BottomRight) b = Math.Max(n.Y, t + .05);
                break;
        }
        var next = new Rect(l, t, Math.Max(.05, rt - l), Math.Max(.05, b - t));
        Crop = aspect > 0 ? Fit(next, grip is Grip.Left or Grip.TopLeft or Grip.BottomLeft ? next.Right : next.Left, grip is Grip.Top or Grip.TopLeft or Grip.TopRight ? next.Bottom : next.Top, anchored: true, fromRight: grip is Grip.Left or Grip.TopLeft or Grip.BottomLeft, fromBottom: grip is Grip.Top or Grip.TopLeft or Grip.TopRight) : next;
        CropChanged?.Invoke();
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured) { ReleaseMouseCapture(); if (crop is { Width: < .05 } or { Height: < .05 }) Crop = original.Width > 0 ? original : null; CropChanged?.Invoke(); e.Handled = true; }
    }
    // Keeps the output aspect ratio, shrinking to fit inside the frame.
    private Rect Fit(Rect r, double x, double y, bool anchored = false, bool fromRight = false, bool fromBottom = false)
    {
        double ratio = aspect * VideoHeight / Math.Max(1, VideoWidth); // normalized width / height
        double w = r.Width, h = w / ratio;
        if (h > r.Height && !anchored) { h = r.Height; w = h * ratio; }
        if (h > 1) { h = 1; w = h * ratio; } if (w > 1) { w = 1; h = w / ratio; }
        double left = anchored ? (fromRight ? x - w : x) : x - w / 2, top = anchored ? (fromBottom ? y - h : y) : y - h / 2;
        left = Math.Clamp(left, 0, 1 - w); top = Math.Clamp(top, 0, 1 - h);
        return new Rect(left, top, w, h);
    }
}
