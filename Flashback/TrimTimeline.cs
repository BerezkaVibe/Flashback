using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
namespace Flashback;

// One waveform lane under the video track. Peaks hold 100 samples per second (0..1).
internal sealed record AudioLane(string Name, float[] Peaks, bool Muted, bool Toggleable);
// A stretch where the picture (Lane -1) or one audio lane is removed without removing time.
internal sealed record CutRegion(int Lane, double Start, double End);

internal sealed class TrimTimeline : FrameworkElement
{
    internal double Duration = 1, Start, End, Position, FrameRate = 60;
    internal double ViewStart, ViewDuration;
    internal double VisibleDuration => ViewDuration > 0 ? Math.Min(ViewDuration,Duration) : Duration;
    internal double ZoomFactor => Duration / Math.Max(.001, VisibleDuration);
    private double Span => VisibleDuration;
    internal event Action? ViewChanged;
    private void RefreshView() { InvalidateVisual(); ViewChanged?.Invoke(); }
    internal void Fit() { ViewStart=0; ViewDuration=0; RefreshView(); }
    internal void PanTo(double start) { ViewStart=Math.Clamp(start,0,Math.Max(0,Duration-Span)); RefreshView(); }
    // Zooms down to a dozen frames, so individual frames get their own ticks.
    private double MinimumSpan => Math.Min(Duration, Math.Max(.2, 12 / Math.Max(1, FrameRate)));
    internal void Zoom(double factor, double center)
    {
        double old=Span, next=Math.Clamp(old/factor,MinimumSpan,Duration);
        double ratio=Math.Clamp((center-ViewStart)/old,0,1);
        ViewStart=Math.Clamp(center-ratio*next,0,Math.Max(0,Duration-next)); ViewDuration=next>=Duration-1e-9 ? 0 : next; RefreshView();
    }
    internal void Reveal(double time)
    {
        if (time<ViewStart || time>ViewStart+Span) PanTo(time-Span/2);
    }
    internal IReadOnlyList<KeepSection> Sections = Array.Empty<KeepSection>();
    private IReadOnlyList<AudioLane> lanes = Array.Empty<AudioLane>();
    internal IReadOnlyList<AudioLane> Lanes { get => lanes; set { lanes = value; lanesVersion++; Height = PreferredHeight; InvalidateVisual(); } }
    private int lanesVersion;
    internal event Action<double, double>? RangeChanged;
    internal event Action<double>? SeekRequested;
    internal event Action? DragStarted, DragCompleted;
    internal event Action<int>? LaneToggled;
    // Double-clicking a kept block selects that section.
    internal event Action<int>? SectionPicked;
    internal int SelectedSection = -1;
    // Cut tool: dragging over the video track or an audio lane marks a cut; clicking one removes it.
    internal bool CutMode;
    private IReadOnlyList<CutRegion> cuts = Array.Empty<CutRegion>();
    internal IReadOnlyList<CutRegion> Cuts { get => cuts; set { cuts = value; cutsVersion++; InvalidateVisual(); } }
    private int cutsVersion, cutLane;
    private double cutAnchor, cutEnd;
    private CutRegion? cutCandidate;
    internal event Action<CutRegion>? CutAdded, CutRemoved;
    private enum Drag { None, Start, End, Playhead, Pan, Cut }
    private Drag drag;
    private double grabOffset;
    private const double Inset = 16, RulerHeight = 22, TrackTop = 26, TrackHeight = 32, LaneHeight = 24, LaneGap = 4, ScrollHeight = 6;
    private double LanesTop => TrackTop + TrackHeight + 6;
    // Audio lanes fold under a small "Audio" strip so they only take room (and load) when opened.
    internal bool LanesExpanded { get => lanesExpanded; set { lanesExpanded = value; lanesVersion++; Height = PreferredHeight; InvalidateVisual(); } }
    private bool lanesExpanded;
    internal event Action? LanesToggleRequested;
    private double ToggleHeight => lanes.Count > 0 ? 18 : 0;
    private double LaneTop(int i) => LanesTop + ToggleHeight + i * (LaneHeight + LaneGap);
    private double ScrollTop => LanesTop + ToggleHeight + (lanesExpanded ? lanes.Count * (LaneHeight + LaneGap) : 0) + 2;
    internal double PreferredHeight => Math.Max(80, ScrollTop + ScrollHeight + 4);
    private static readonly Brush Track = Brush("#252D36"), Kept = Brush("#456D5D"), Accent = Brush("#9CE2C1"), Ink = Brush("#EDF0F3"), Muted = Brush("#9DA6B1");
    private static readonly Brush EndAccent = Brush("#F3BF84"), Tick = Brush("#4A5561"), LaneFill = Brush("#1A2027"), Wave = Brush("#7FA8C9"), WaveMuted = Brush("#3A444F");
    private static Brush Brush(string color) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!; b.Freeze(); return b; }
    private static readonly Pen TickPen = new(Tick, 1), MajorPen = new(Muted, 1), InkPen = new(Ink, 2);
    static TrimTimeline() { TickPen.Freeze(); MajorPen.Freeze(); InkPen.Freeze(); }
    internal bool IsDragging => drag != Drag.None && drag != Drag.Pan;
    internal double TimeAt(double x) => Math.Clamp(ViewStart + (x-Inset) / Math.Max(1, ActualWidth-2*Inset) * Span, ViewStart, Math.Min(Duration,ViewStart+Span));
    internal double XAt(double t) => Inset + Math.Clamp((t-ViewStart) / Math.Max(.001, Span), 0, 1) * Math.Max(1, ActualWidth-2*Inset);
    private double Snap(double t) => Math.Clamp(Math.Round(t * FrameRate) / FrameRate, 0, Duration);
    public TrimTimeline() { Focusable = true; Cursor = Cursors.Hand; Height = PreferredHeight; }

    // The ruler, sections and waveforms only change with the view; playback redraws
    // just the playhead on top of a cached drawing.
    private DrawingGroup? cache;
    private object? cacheKey;
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (Duration <= 0 || ActualWidth <= 2*Inset) return;
        var key = (ViewStart, Span, ActualWidth, ActualHeight, Start, End, Duration, FrameRate, lanesVersion, IsKeyboardFocusWithin, SectionsKey(), SelectedSection, cutsVersion);
        if (cache == null || !Equals(cacheKey, key))
        {
            cache = new DrawingGroup();
            using (var layer = cache.Open()) DrawStatic(layer);
            cache.Freeze(); cacheKey = key;
        }
        dc.DrawDrawing(cache);
        if (drag == Drag.Cut && Math.Abs(cutEnd - cutAnchor) > 1e-6)
            DrawCut(dc, cutLane, Math.Min(cutAnchor, cutEnd), Math.Max(cutAnchor, cutEnd), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double playhead = XAt(Position);
        if (Position >= ViewStart - 1e-9 && Position <= ViewStart + Span + 1e-9)
        {
            dc.DrawLine(InkPen, new Point(playhead, 6), new Point(playhead, ScrollTop - 2));
            dc.DrawRoundedRectangle(Ink, null, new Rect(playhead-5, 4, 10, 7), 2, 2);
        }
    }
    private int SectionsKey() { int hash = Sections.Count; foreach (var s in Sections) hash = HashCode.Combine(hash, s.Start, s.End); return hash; }
    private void DrawStatic(DrawingContext dc)
    {
        double width = ActualWidth-2*Inset, dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        DrawRuler(dc, width, dpi);
        dc.DrawRoundedRectangle(Track, null, new Rect(Inset, TrackTop, width, TrackHeight), 6, 6);
        for (int i = 0; i < Sections.Count; i++)
        {
            var s = Sections[i];
            if (s.End<ViewStart || s.Start>ViewStart+Span) continue;
            var block = new Rect(XAt(s.Start), TrackTop+3, Math.Max(1, XAt(s.End)-XAt(s.Start)), TrackHeight-6);
            dc.DrawRoundedRectangle(Kept, i == SelectedSection ? new Pen(Ink, 1.5) : null, block, 3, 3);
            // Number each kept block so it matches its chip below the timeline.
            var number = Text((i + 1).ToString(CultureInfo.InvariantCulture), 11, Ink, dpi);
            if (block.Width >= number.Width + 8) dc.DrawText(number, new Point(block.X + 5, block.Y + (block.Height - number.Height) / 2));
        }
        dc.DrawRoundedRectangle(null, new Pen(Accent, 1.5), new Rect(XAt(Start), TrackTop-2, Math.Max(1, XAt(End)-XAt(Start)), TrackHeight+4), 3, 3);
        foreach (var edge in new[] { (Time: Start, Color: Accent), (Time: End, Color: EndAccent) })
        {
            if (edge.Time<ViewStart || edge.Time>ViewStart+Span) continue;
            double x = XAt(edge.Time);
            dc.DrawRoundedRectangle(edge.Color, null, new Rect(x-6, TrackTop-5, 12, TrackHeight+10), 3, 3);
            dc.DrawLine(new Pen(Track, 1), new Point(x, TrackTop+8), new Point(x, TrackTop+TrackHeight-8));
        }
        DrawLaneToggle(dc, dpi);
        if (lanesExpanded) for (int i = 0; i < lanes.Count; i++) DrawLane(dc, lanes[i], LaneTop(i), width, dpi);
        foreach (var cut in cuts) DrawCut(dc, cut.Lane, cut.Start, cut.End, dpi);
        if (ZoomFactor > 1.001)
        {
            // A slim scrollbar shows where the zoomed view sits in the whole clip.
            dc.DrawRoundedRectangle(Track, null, new Rect(Inset, ScrollTop, width, ScrollHeight), 3, 3);
            double left = Inset + ViewStart / Duration * width, thumb = Math.Max(12, Span / Duration * width);
            dc.DrawRoundedRectangle(Accent, null, new Rect(Math.Min(left, Inset + width - thumb), ScrollTop, thumb, ScrollHeight), 3, 3);
            var badge = Text($"{ZoomFactor:0.#}×", 11, Accent, dpi);
            dc.DrawText(badge, new Point(ActualWidth - Inset - badge.Width, 2));
        }
        if (IsKeyboardFocusWithin) dc.DrawRoundedRectangle(null, new Pen(Muted, 1), new Rect(1, 1, Math.Max(1, ActualWidth-2), Math.Max(1,ActualHeight-2)), 7, 7);
    }
    private static readonly double[] Steps = { .01, .02, .05, .1, .2, .5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600 };
    // Major ticks at least ~90 px apart get labels; minor ticks fill in down to frames when zoomed far in.
    private void DrawRuler(DrawingContext dc, double width, double dpi)
    {
        double pixelsPerSecond = width / Span, frame = 1 / Math.Max(1, FrameRate);
        double major = Steps.FirstOrDefault(s => s * pixelsPerSecond >= 90, Steps[^1]);
        if (frame * pixelsPerSecond >= 90) major = Math.Max(frame, major);
        double minor = frame * pixelsPerSecond >= 10 && major <= .5 ? frame
            : Steps.Where(s => s < major && s * pixelsPerSecond >= 8 && IsMultiple(major, s)).DefaultIfEmpty(major).Min();
        int decimals = major >= 1 ? 0 : major >= .1 ? 1 : 2;
        var labelRight = double.MinValue;
        // Integer tick counts avoid drift when stepping by a frame (1/60 s).
        for (long k = (long)Math.Ceiling(ViewStart / minor - 1e-9), last = (long)Math.Floor((ViewStart + Span) / minor + 1e-9); k <= last; k++)
        {
            double t = k * minor, x = XAt(t);
            bool isMajor = minor == major || IsMultiple(t, major);
            dc.DrawLine(isMajor ? MajorPen : TickPen, new Point(x, isMajor ? 12 : 17), new Point(x, RulerHeight));
            if (!isMajor) continue;
            var label = Text(TimeLabel(t, decimals), 10, Muted, dpi);
            double lx = Math.Clamp(x + 3, 0, ActualWidth - label.Width);
            if (lx <= labelRight + 6) continue;
            dc.DrawText(label, new Point(lx, 0)); labelRight = lx + label.Width;
        }
    }
    private static bool IsMultiple(double value, double step) { double r = value / step; return Math.Abs(r - Math.Round(r)) < 1e-6 * Math.Max(1, Math.Abs(r)); }
    private string TimeLabel(double t, int decimals)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, t));
        string whole = Duration >= 3600 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
        return decimals == 0 ? whole : whole + (time.TotalSeconds % 1).ToString("F" + decimals, CultureInfo.InvariantCulture)[1..];
    }
    private FormattedText Text(string text, double size, Brush brush, double dpi) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, dpi);
    private void DrawLane(DrawingContext dc, AudioLane lane, double top, double width, double dpi)
    {
        dc.DrawRoundedRectangle(LaneFill, null, new Rect(Inset, top, width, LaneHeight), 4, 4);
        var peaks = lane.Peaks; double mid = top + LaneHeight / 2;
        if (peaks.Length > 0)
        {
            // One vertical stroke per pixel column, using the loudest peak in that column.
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
                for (int px = 0; px < (int)width; px++)
                {
                    int a = (int)((ViewStart + px / width * Span) * 100), b = Math.Max(a + 1, (int)((ViewStart + (px + 1) / width * Span) * 100));
                    float peak = 0; for (int i = Math.Max(0, a); i < Math.Min(peaks.Length, b); i++) peak = Math.Max(peak, peaks[i]);
                    double h = Math.Max(.5, peak * (LaneHeight / 2 - 2));
                    g.BeginFigure(new Point(Inset + px + .5, mid - h), false, false); g.LineTo(new Point(Inset + px + .5, mid + h), true, false);
                }
            geometry.Freeze();
            var pen = new Pen(lane.Muted ? WaveMuted : Wave, 1); pen.Freeze();
            dc.DrawGeometry(null, pen, geometry);
        }
        var name = Text(lane.Name + (lane.Muted ? " · muted" : ""), 10, lane.Muted ? Muted : Ink, dpi);
        var chip = new Rect(Inset + 4, top + (LaneHeight - 16) / 2, name.Width + 12, 16);
        dc.DrawRoundedRectangle(Track, lane.Toggleable ? new Pen(lane.Muted ? Tick : Accent, 1) : null, chip, 4, 4);
        dc.DrawText(name, new Point(chip.X + 6, chip.Y + 1));
    }
    private static readonly Brush CutFill = Brush("#99B42C38"), CutEdge = Brush("#E5484D");
    // Band rectangle for the video track (-1) or an audio lane.
    private Rect Band(int lane) => lane < 0 ? new Rect(Inset, TrackTop, ActualWidth - 2 * Inset, TrackHeight)
        : new Rect(Inset, LaneTop(lane), ActualWidth - 2 * Inset, LaneHeight);
    private int BandAt(Point p)
    {
        if (p.Y >= TrackTop - 4 && p.Y <= TrackTop + TrackHeight + 4) return -1;
        if (lanesExpanded) for (int i = 0; i < lanes.Count; i++) { var b = Band(i); if (p.Y >= b.Top && p.Y <= b.Bottom) return i; }
        return -2;
    }
    private Rect ToggleArea => new(Inset, LanesTop, 170, ToggleHeight);
    private void DrawLaneToggle(DrawingContext dc, double dpi)
    {
        if (lanes.Count == 0) return;
        int audioCuts = cuts.Count(c => c.Lane >= 0);
        string text = (lanesExpanded ? "▾ Audio" : "▸ Audio") + (lanes.Count > 1 ? $" · {lanes.Count} tracks" : "") + (!lanesExpanded && audioCuts > 0 ? $" · {audioCuts} muted" : "");
        dc.DrawText(Text(text, 11, lanesExpanded ? Ink : Muted, dpi), new Point(Inset + 2, LanesTop + 1));
    }
    private void DrawCut(DrawingContext dc, int lane, double start, double end, double dpi)
    {
        if (end < ViewStart || start > ViewStart + Span || lane >= lanes.Count || (lane >= 0 && !lanesExpanded)) return;
        var band = Band(lane); double x = XAt(start), w = Math.Max(2, XAt(end) - x);
        var area = new Rect(x, band.Top, w, band.Height);
        dc.DrawRoundedRectangle(CutFill, new Pen(CutEdge, 1), area, 3, 3);
        var label = Text(lane < 0 ? "blacked out" : "muted", 10, Ink, dpi);
        if (w >= label.Width + 8) dc.DrawText(label, new Point(x + 4, band.Top + (band.Height - label.Height) / 2));
    }
    private int LaneAt(Point p)
    {
        for (int i = 0; i < lanes.Count; i++)
        {
            if (!lanesExpanded) break;
            double top = LaneTop(i);
            if (p.Y >= top && p.Y <= top + LaneHeight && p.X <= Inset + 110) return i;
        }
        return -1;
    }
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Duration<=0 || ActualWidth<=2*Inset) return;
        Focus(); var point = e.GetPosition(this);
        if (lanes.Count > 0 && ToggleArea.Contains(point)) { LanesToggleRequested?.Invoke(); e.Handled = true; return; }
        if (CutMode && BandAt(point) is int band and > -2)
        {
            double t = Snap(TimeAt(point.X));
            cutCandidate = cuts.FirstOrDefault(c => c.Lane == band && t >= c.Start && t <= c.End);
            drag = Drag.Cut; cutLane = band; cutAnchor = cutEnd = t; CaptureMouse(); e.Handled = true; return;
        }
        int lane = LaneAt(point);
        if (lane >= 0 && lanes[lane].Toggleable) { LaneToggled?.Invoke(lane); e.Handled = true; return; }
        if (e.ClickCount == 2 && point.Y >= TrackTop && point.Y <= TrackTop + TrackHeight)
        {
            double t = TimeAt(point.X);
            for (int i = 0; i < Sections.Count; i++)
                if (t >= Sections[i].Start && t <= Sections[i].End) { SectionPicked?.Invoke(i); e.Handled = true; return; }
        }
        if (ZoomFactor > 1.001 && point.Y >= ScrollTop - 3)
        {
            // Dragging the scrollbar pans; clicking beside the thumb centers it there.
            drag = Drag.Pan; CaptureMouse(); PanToPointer(point.X); e.Handled = true; return;
        }
        BeginDrag(point); CaptureMouse(); DragStarted?.Invoke(); MoveTo(point.X); e.Handled=true;
    }
    private void PanToPointer(double x) => PanTo((x - Inset) / Math.Max(1, ActualWidth - 2 * Inset) * Duration - Span / 2);
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
        var p=e.GetPosition(this);
        if (IsMouseCaptured)
        {
            if (drag == Drag.Cut) { cutEnd = Snap(TimeAt(p.X)); InvalidateVisual(); }
            else if (drag == Drag.Pan) PanToPointer(p.X); else MoveTo(p.X);
            return;
        }
        if (lanes.Count > 0 && ToggleArea.Contains(p)) { Cursor = Cursors.Hand; ToolTip = lanesExpanded ? "Hide the audio tracks" : "Show the audio tracks"; return; }
        if (CutMode && BandAt(p) > -2)
        {
            Cursor = Cursors.Cross;
            ToolTip = "Drag to cut out this stretch; click a cut to restore it";
            return;
        }
        int lane = LaneAt(p);
        Cursor = lane >= 0 && lanes[lane].Toggleable ? Cursors.Hand
            : p.Y>=TrackTop-7 && p.Y<=TrackTop+TrackHeight+7 && Math.Min(Math.Abs(p.X-XAt(Start)),Math.Abs(p.X-XAt(End)))<=14 ? Cursors.SizeWE : Cursors.Hand;
        ToolTip = lane >= 0 ? (lanes[lane].Toggleable ? $"Click to {(lanes[lane].Muted ? "include" : "mute")} {lanes[lane].Name.ToLowerInvariant()} audio in exports" : "Record with separate tracks to adjust desktop and microphone audio separately")
            : "Click to seek; drag the edges to trim. Wheel pans · Ctrl+wheel zooms";
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsMouseCaptured) return;
        if (drag == Drag.Cut)
        {
            double a = Math.Min(cutAnchor, cutEnd), b = Math.Max(cutAnchor, cutEnd);
            // A drag makes a cut; a plain click on an existing cut restores that stretch.
            if (b - a >= .05) CutAdded?.Invoke(new CutRegion(cutLane, a, b));
            else if (cutCandidate != null) CutRemoved?.Invoke(cutCandidate);
            cutCandidate = null; ReleaseMouseCapture(); InvalidateVisual(); e.Handled = true; return;
        }
        if (drag != Drag.Pan) MoveTo(e.GetPosition(this).X);
        ReleaseMouseCapture(); e.Handled=true;
    }
    protected override void OnLostMouseCapture(MouseEventArgs e)
    { base.OnLostMouseCapture(e); EndDrag(); }
    internal void EndDrag() { if (drag == Drag.None) return; bool quiet = drag is Drag.Pan or Drag.Cut; drag=Drag.None; if (!quiet) DragCompleted?.Invoke(); }
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
