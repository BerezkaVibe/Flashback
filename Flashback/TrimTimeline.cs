using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
namespace Flashback;

internal sealed class TrimTimeline : FrameworkElement
{
    internal double Duration = 1, Start, End, Position, FrameRate = 60;
    internal double ViewStart, ViewDuration;
    internal double VisibleDuration => ViewDuration > 0 ? Math.Min(ViewDuration,Duration) : Duration;
    private double Span => VisibleDuration;
    internal event Action? ViewChanged;
    private void RefreshView() { InvalidateVisual(); ViewChanged?.Invoke(); }
    internal void Fit() { ViewStart=0; ViewDuration=0; RefreshView(); }
    internal void PanTo(double start) { ViewStart=Math.Clamp(start,0,Math.Max(0,Duration-Span)); RefreshView(); }
    internal void Zoom(double factor, double center)
    {
        double old=Span, next=Math.Clamp(old/factor,Math.Min(1,Duration),Duration);
        double ratio=Math.Clamp((center-ViewStart)/old,0,1);
        ViewStart=Math.Clamp(center-ratio*next,0,Math.Max(0,Duration-next)); ViewDuration=next; RefreshView();
    }
    internal void Reveal(double time)
    {
        if (time<ViewStart || time>ViewStart+Span) PanTo(time-Span/2);
    }
    internal IReadOnlyList<KeepSection> Sections = Array.Empty<KeepSection>();
    internal event Action<double, double>? RangeChanged;
    internal event Action<double>? SeekRequested;
    internal event Action? DragStarted, DragCompleted;
    private enum Drag { None, Start, End, Playhead }
    private Drag drag;
    private double grabOffset;
    private const double Inset = 16, TrackTop = 22, TrackHeight = 32;
    private static readonly Brush Track = Brush("#252D36"), Kept = Brush("#456D5D"), Accent = Brush("#9CE2C1"), Ink = Brush("#EDF0F3"), Muted = Brush("#9DA6B1");
    private static readonly Brush EndAccent = Brush("#F3BF84");
    private static Brush Brush(string color) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!; b.Freeze(); return b; }
    internal bool IsDragging => drag != Drag.None;
    internal double TimeAt(double x) => Math.Clamp(ViewStart + (x-Inset) / Math.Max(1, ActualWidth-2*Inset) * Span, ViewStart, Math.Min(Duration,ViewStart+Span));
    internal double XAt(double t) => Inset + Math.Clamp((t-ViewStart) / Math.Max(.001, Span), 0, 1) * Math.Max(1, ActualWidth-2*Inset);
    private double Snap(double t) => Math.Clamp(Math.Round(t * FrameRate) / FrameRate, 0, Duration);
    public TrimTimeline() { Focusable = true; Cursor = Cursors.Hand; }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (Duration <= 0 || ActualWidth <= 2*Inset) return;
        double width = ActualWidth-2*Inset;
        dc.DrawRoundedRectangle(Track, null, new Rect(Inset, TrackTop, width, TrackHeight), 6, 6);
        foreach (var s in Sections)
            if (s.End>=ViewStart && s.Start<=ViewStart+Span)
            dc.DrawRectangle(Kept, null, new Rect(XAt(s.Start), TrackTop+3, Math.Max(1, XAt(s.End)-XAt(s.Start)), TrackHeight-6));
        dc.DrawRoundedRectangle(null, new Pen(Accent, 1.5), new Rect(XAt(Start), TrackTop-2, Math.Max(1, XAt(End)-XAt(Start)), TrackHeight+4), 3, 3);
        foreach (var edge in new[] { (Time: Start, Color: Accent), (Time: End, Color: EndAccent) })
        {
            if (edge.Time<ViewStart || edge.Time>ViewStart+Span) continue;
            double x = XAt(edge.Time);
            dc.DrawRoundedRectangle(edge.Color, null, new Rect(x-6, TrackTop-5, 12, TrackHeight+10), 3, 3);
            dc.DrawLine(new Pen(Track, 1), new Point(x, TrackTop+8), new Point(x, TrackTop+TrackHeight-8));
        }
        int ticks = Math.Max(2, (int)(width / 140));
        for (int i=0; i<=ticks; i++)
        {
            double t = ViewStart+i*Span/ticks, x = XAt(t);
            var text = new FormattedText(TimeSpan.FromSeconds(t).ToString(Span<10 ? @"m\:ss\.ff" : Duration>=3600 ? @"h\:mm\:ss" : @"m\:ss"), CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Muted, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(Math.Clamp(x-text.Width/2, 0, ActualWidth-text.Width), 64));
        }
        double playhead = XAt(Position);
        dc.DrawLine(new Pen(Ink, 2), new Point(playhead, 8), new Point(playhead, 59));
        dc.DrawRoundedRectangle(Ink, null, new Rect(playhead-5, 6, 10, 7), 2, 2);
        if (IsKeyboardFocusWithin) dc.DrawRoundedRectangle(null, new Pen(Muted, 1), new Rect(1, 1, Math.Max(1, ActualWidth-2), Math.Max(1,ActualHeight-2)), 7, 7);
    }
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Duration<=0 || ActualWidth<=2*Inset) return;
        Focus(); var point = e.GetPosition(this);
        BeginDrag(point); CaptureMouse(); DragStarted?.Invoke(); MoveTo(point.X); e.Handled=true;
    }
    internal void BeginDrag(Point point)
    {
        double left = Start<ViewStart || Start>ViewStart+Span ? double.MaxValue : Math.Abs(point.X-XAt(Start)), right = End<ViewStart || End>ViewStart+Span ? double.MaxValue : Math.Abs(point.X-XAt(End));
        bool handles = point.Y >= TrackTop-7 && point.Y <= TrackTop+TrackHeight+7;
        drag = handles && Math.Min(left,right)<=14 ? (left<=right ? Drag.Start : Drag.End) : Drag.Playhead;
        grabOffset = drag == Drag.Start ? point.X-XAt(Start) : drag == Drag.End ? point.X-XAt(End) : 0;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured) MoveTo(e.GetPosition(this).X);
        else
        {
            var p=e.GetPosition(this);
            Cursor = p.Y>=TrackTop-7 && p.Y<=TrackTop+TrackHeight+7 && Math.Min(Math.Abs(p.X-XAt(Start)),Math.Abs(p.X-XAt(End)))<=14 ? Cursors.SizeWE : Cursors.Hand;
        }
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    { base.OnMouseLeftButtonUp(e); if (IsMouseCaptured) { MoveTo(e.GetPosition(this).X); ReleaseMouseCapture(); e.Handled=true; } }
    protected override void OnLostMouseCapture(MouseEventArgs e)
    { base.OnLostMouseCapture(e); EndDrag(); }
    internal void EndDrag() { if (drag == Drag.None) return; drag=Drag.None; DragCompleted?.Invoke(); }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Keyboard.Modifiers==ModifierKeys.Control) Zoom(e.Delta>0 ? 1.5 : 1/1.5,TimeAt(e.GetPosition(this).X));
        else PanTo(ViewStart-Math.Sign(e.Delta)*Span*.15);
        e.Handled=true;
    }
    internal void MoveTo(double x)
    {
        double t = Snap(TimeAt(x-grabOffset));
        if (drag == Drag.Start) { Start=Math.Min(t,Math.Max(0,End-.1)); RangeChanged?.Invoke(Start,End); t=Start; }
        else if (drag == Drag.End) { End=Math.Max(t,Math.Min(Duration,Start+.1)); RangeChanged?.Invoke(Start,End); t=End; }
        Position=t; InvalidateVisual(); SeekRequested?.Invoke(t);
    }
}
