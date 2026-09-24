using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Flashback;

// An editable zoom-ramp chart, in the spirit of a mouse-acceleration graph: seconds along the bottom,
// zoom level (log scale, 1x to 8x) up the side. The last point sets the max zoom and the ramp length.
// Ctrl-drag scales the whole curve's height, Alt-drag stretches its timing; double-click adds a point
// and right-click removes one. Built-in presets are drawn faintly behind the curve.
internal sealed class ZoomCurveEditor : FrameworkElement
{
    private ZoomCurve curve = ZoomCurve.Smooth;
    private double maxZoom = 2, xMax = 1;
    internal ZoomCurve Curve { get => curve; set { curve = value; if (dragging < 0) FitRange(); InvalidateVisual(); } }
    internal double MaxZoom { get => maxZoom; set { maxZoom = value; InvalidateVisual(); } }
    // Seconds into the ramp to mark with a dot, or NaN to hide it.
    internal double Marker { get => marker; set { if (value.Equals(marker)) return; marker = value; InvalidateVisual(); } }
    private double marker = double.NaN;
    internal IReadOnlyList<ZoomPreset> Standards = ZoomPreset.BuiltIns;
    // Raised continuously while dragging, once when a drag begins (for undo), and when it ends.
    internal event Action? Changed, EditStarted, EditFinished;

    private const double Left = 34, Right = 10, Top = 10, Bottom = 22;
    private static readonly Brush Grid = Frozen("#252D36"), Label = Frozen("#9DA6B1"), Line = Frozen("#5EEAD4"), Faint = Frozen("#3A5A57"), PointFill = Frozen("#EDF0F3"), MarkerFill = Frozen("#F3BF84");
    private static Brush Frozen(string c) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(c)!; b.Freeze(); return b; }
    private Rect Plot => new(Left, Top, Math.Max(10, ActualWidth - Left - Right), Math.Max(10, ActualHeight - Top - Bottom));
    // Log scale keeps 1x-3x (where most zooms live) readable while still reaching 8x.
    private double YOf(double zoom) { var p = Plot; return p.Bottom - Math.Log2(Math.Clamp(zoom, 1, ZoomRegion.Limit)) / Math.Log2(ZoomRegion.Limit) * p.Height; }
    private double ZoomOfY(double y) { var p = Plot; return Math.Clamp(Math.Pow(2, (p.Bottom - y) / p.Height * Math.Log2(ZoomRegion.Limit)), 1, ZoomRegion.Limit); }
    private double XOf(double t) { var p = Plot; return p.Left + t / xMax * p.Width; }
    private double TOfX(double x) { var p = Plot; return (x - p.Left) / p.Width * xMax; }
    private double ZoomOfF(double f) => 1 + (maxZoom - 1) * f;
    // The time axis grows with the ramp (in half-second steps), but only between drags so it never jumps under the pointer.
    private void FitRange() => xMax = Math.Clamp(Math.Ceiling(curve.Length * 1.25 * 2) / 2, 1, ZoomRegion.MaxRamp + .5);

    protected override void OnRender(DrawingContext dc)
    {
        var p = Plot; double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var gridPen = new Pen(Grid, 1);
        foreach (double z in new[] { 1, 1.5, 2, 3, 4, 6, 8.0 })
        {
            double y = Math.Round(YOf(z)) + .5; dc.DrawLine(gridPen, new Point(p.Left, y), new Point(p.Right, y));
            if (z is 1 or 2 or 4 or 8) dc.DrawText(Text($"{z:0}×", dpi), new Point(p.Left - 26, y - 7));
        }
        for (double t = 0; t <= xMax + 1e-9; t += xMax > 2 ? 1 : .5)
        {
            double x = Math.Round(XOf(t)) + .5; dc.DrawLine(gridPen, new Point(x, p.Top), new Point(x, p.Bottom));
            dc.DrawText(Text(t.ToString("0.#", CultureInfo.InvariantCulture) + "s", dpi), new Point(x - 6, p.Bottom + 4));
        }
        dc.PushClip(new RectangleGeometry(new Rect(p.Left - 4, p.Top - 4, p.Width + 8, p.Height + 8)));
        foreach (var s in Standards) DrawCurve(dc, s.Curve, s.MaxZoom, new Pen(Faint, 1));
        DrawCurve(dc, curve, maxZoom, new Pen(Line, 2));
        foreach (var (t, f) in curve.Points) dc.DrawRectangle(PointFill, null, new Rect(XOf(t) - 3.5, YOf(ZoomOfF(f)) - 3.5, 7, 7));
        if (!double.IsNaN(marker)) dc.DrawEllipse(MarkerFill, null, new Point(XOf(Math.Min(marker, xMax)), YOf(ZoomOfF(curve.At(marker)))), 4, 4);
        dc.Pop();
    }
    private void DrawCurve(DrawingContext dc, ZoomCurve c, double max, Pen pen)
    {
        var g = new StreamGeometry();
        using (var s = g.Open())
        {
            s.BeginFigure(new Point(XOf(0), YOf(1)), false, false);
            for (int i = 1; i <= 80; i++) { double t = c.Length * i / 80; s.LineTo(new Point(XOf(t), YOf(1 + (max - 1) * c.At(t))), true, false); }
            s.LineTo(new Point(XOf(xMax), YOf(max)), true, false); // the plateau
        }
        g.Freeze(); dc.DrawGeometry(null, pen, g);
    }
    private FormattedText Text(string s, double dpi) => new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, Label, dpi);

    private int dragging = -1;
    private Point dragFrom; private ZoomCurve dragCurve = ZoomCurve.Smooth; private double dragMax;
    private int PointAt(Point at)
    {
        for (int i = curve.Points.Count - 1; i >= 0; i--)
            if (Math.Abs(XOf(curve.Points[i].T) - at.X) <= 7 && Math.Abs(YOf(ZoomOfF(curve.Points[i].F)) - at.Y) <= 7) return i;
        return -1;
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var at = e.GetPosition(this);
        if (e.ClickCount == 2 && PointAt(at) < 0) { AddPoint(at); e.Handled = true; return; }
        int i = PointAt(at);
        if (i <= 0) return; // the first point stays at (0, 1x)
        dragging = i; dragFrom = at; dragCurve = curve; dragMax = maxZoom;
        EditStarted?.Invoke(); CaptureMouse(); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var at = e.GetPosition(this);
        Cursor = dragging >= 0 || PointAt(at) > 0 ? Cursors.SizeAll : Cursors.Arrow;
        if (dragging < 0) return;
        var mods = Keyboard.Modifiers; var pts = dragCurve.Points.ToList(); int last = pts.Count - 1;
        if (mods.HasFlag(ModifierKeys.Control))
        {
            // Scale the whole curve's height: the max zoom follows the pointer proportionally.
            double startZoom = 1 + (dragMax - 1) * dragCurve.Points[dragging].F;
            double ratio = Math.Log2(ZoomOfY(at.Y)) / Math.Max(.05, Math.Log2(startZoom));
            maxZoom = Math.Clamp(Math.Pow(2, Math.Log2(dragMax) * ratio), 1.1, ZoomRegion.Limit);
        }
        else if (mods.HasFlag(ModifierKeys.Alt))
        {
            // Stretch the whole ramp in time around the dragged point.
            double scale = Math.Max(.05, TOfX(at.X)) / Math.Max(.01, dragCurve.Points[dragging].T);
            curve = new ZoomCurve(pts.Select(q => (q.T * scale, q.F)).ToArray()).Validated();
        }
        else if (dragging == last)
        {
            maxZoom = Math.Clamp(ZoomOfY(at.Y), 1.1, ZoomRegion.Limit);
            pts[last] = (Math.Clamp(TOfX(at.X), Math.Max(.05, pts[last - 1].T + .01), ZoomRegion.MaxRamp), 1);
            curve = new ZoomCurve(pts).Validated();
        }
        else
        {
            double f = (ZoomOfY(at.Y) - 1) / Math.Max(.01, maxZoom - 1);
            pts[dragging] = (Math.Clamp(TOfX(at.X), pts[dragging - 1].T + .01, pts[dragging + 1].T - .01), Math.Clamp(f, 0, 1));
            curve = new ZoomCurve(pts).Validated();
        }
        Changed?.Invoke(); InvalidateVisual();
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (dragging < 0) return;
        dragging = -1; ReleaseMouseCapture(); FitRange(); InvalidateVisual(); EditFinished?.Invoke();
    }
    protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); if (dragging >= 0) { dragging = -1; FitRange(); InvalidateVisual(); EditFinished?.Invoke(); } }
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        int i = PointAt(e.GetPosition(this));
        if (i <= 0 || i == curve.Points.Count - 1 || curve.Points.Count <= 2) return;
        EditStarted?.Invoke();
        curve = new ZoomCurve(curve.Points.Where((_, n) => n != i).ToArray()).Validated();
        Changed?.Invoke(); EditFinished?.Invoke(); InvalidateVisual(); e.Handled = true;
    }
    private void AddPoint(Point at)
    {
        double t = TOfX(at.X);
        if (t <= .01 || t >= curve.Length - .01 || curve.Points.Count >= 10) return;
        EditStarted?.Invoke();
        double f = Math.Clamp((ZoomOfY(at.Y) - 1) / Math.Max(.01, maxZoom - 1), 0, 1);
        curve = new ZoomCurve(curve.Points.Append((t, f)).ToArray()).Validated();
        Changed?.Invoke(); EditFinished?.Invoke(); InvalidateVisual();
    }
}
