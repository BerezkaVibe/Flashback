using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media;
namespace Flashback;

// One waveform lane under the video track. Peaks hold 100 samples per second (0..1).
internal sealed record AudioLane(string Name, float[] Peaks, bool Muted, bool Toggleable);
// A stretch where the picture (Lane -1) or one audio lane is removed without removing time.
internal sealed record CutRegion(int Lane, double Start, double End);
// A stretch played back slower (video and audio together), in source time.
internal sealed record SpeedRegion(double Start, double End, double Speed);

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
    // Cut tool: a thin cutter line follows the pointer over the video track or an audio lane.
    // Click once to start a cut and again to finish it; dragging still moves the playhead, and the
    // cutter locks onto the playhead when within a few pixels. Right-click a cut to restore it.
    internal bool CutMode { get => cutMode; set { cutMode = value; if (value) { slowMode = false; zoomMode = false; overlayMode = null; } pendingCut = null; cutHover = null; InvalidateVisual(); } }
    // Speed tool: the same two clicks mark a stretch that plays slower or faster (video and audio together).
    internal bool SlowMode { get => slowMode; set { slowMode = value; if (value) { cutMode = false; zoomMode = false; overlayMode = null; } pendingCut = null; cutHover = null; InvalidateVisual(); } }
    // Zoom tool: the same two clicks mark a zoomed stretch. It may overlap cuts and speed parts,
    // and snaps to speed-part edges as well as the playhead.
    internal bool ZoomMode { get => zoomMode; set { zoomMode = value; if (value) { cutMode = false; slowMode = false; overlayMode = null; } pendingCut = null; cutHover = null; InvalidateVisual(); } }
    // Text and image tools: the same two clicks add an item on the lowest free layer above the video.
    internal OverlayKind? OverlayMode { get => overlayMode; set { overlayMode = value; if (value != null) { cutMode = false; slowMode = false; zoomMode = false; } pendingCut = null; cutHover = null; InvalidateVisual(); } }
    private OverlayKind? overlayMode;
    private bool cutMode, slowMode, zoomMode, cutPress;
    private bool Placing => cutMode || slowMode || zoomMode || overlayMode != null;
    // Parts that cover the whole picture and sound, and snap to other parts' edges.
    private bool WholeClipTool => slowMode || zoomMode || overlayMode != null;
    private IReadOnlyList<ZoomRegion> zoomRegions = Array.Empty<ZoomRegion>();
    internal IReadOnlyList<ZoomRegion> ZoomRegions
    {
        get => zoomRegions;
        set
        {
            // Aiming or reshaping a zoom leaves its timeline box and tag alone; only redraw when those change.
            bool same = value.Count == zoomRegions.Count && value.Zip(zoomRegions).All(p => p.First.Start == p.Second.Start && p.First.End == p.Second.End && Math.Abs(p.First.MaxZoom - p.Second.MaxZoom) < .05);
            zoomRegions = value;
            if (!same) { zoomVersion++; InvalidateVisual(); }
        }
    }
    private int zoomVersion;
    internal int SelectedZoom { get => selectedZoom; set { selectedZoom = value; zoomVersion++; InvalidateVisual(); } }
    private int selectedZoom = -1;
    internal event Action<double, double>? ZoomAdded;
    internal event Action<int>? ZoomTagClicked, ZoomRemoved;
    private readonly List<Rect> zoomTags = new();
    private IReadOnlyList<SpeedRegion> slowRegions = Array.Empty<SpeedRegion>();
    internal IReadOnlyList<SpeedRegion> SlowRegions { get => slowRegions; set { slowRegions = value; slowVersion++; InvalidateVisual(); } }
    private int slowVersion;
    internal int SelectedSlow { get => selectedSlow; set { selectedSlow = value; slowVersion++; InvalidateVisual(); } }
    private int selectedSlow = -1;
    // Clicking a region's speed tag asks to change it; right-click in slow mode removes a region.
    internal event Action<double, double>? SlowAdded;
    internal event Action<int>? SlowTagClicked, SlowRemoved;
    private readonly List<Rect> slowTags = new();
    private (int Lane, double Time)? pendingCut, cutHover;
    private Point pressPoint;
    private const double PlayheadLock = 3;
    private IReadOnlyList<CutRegion> cuts = Array.Empty<CutRegion>();
    internal IReadOnlyList<CutRegion> Cuts { get => cuts; set { cuts = value; cutsVersion++; InvalidateVisual(); } }
    private int cutsVersion;
    internal bool HasPendingCut => pendingCut != null;
    internal void CancelPendingCut() { pendingCut = null; InvalidateVisual(); }
    private double CutTimeAt(double x)
    {
        if (Math.Abs(x - XAt(Position)) <= PlayheadLock) return Position;
        // Every tool also locks onto the ends of other parts, cut-outs included, so a part can sit
        // right against a cut-out it isn't allowed to overlap.
        foreach (double edge in PartEdges())
            if (Math.Abs(x - XAt(edge)) <= PlayheadLock) return edge;
        return Snap(TimeAt(x));
    }
    private IEnumerable<double> PartEdges(OverlayItem? except = null) =>
        slowRegions.SelectMany(r => new[] { r.Start, r.End }).Concat(zoomRegions.SelectMany(r => new[] { r.Start, r.End }))
            .Concat(overlays.Where(o => !ReferenceEquals(o, except)).SelectMany(o => new[] { o.Start, o.End }))
            .Concat(cuts.Where(c => c.Lane < 0 || lanesExpanded).SelectMany(c => new[] { c.Start, c.End }));
    private bool LockedAt(double t) => Math.Abs(t - Position) < 1e-9 || PartEdges().Any(e => Math.Abs(t - e) < 1e-9);

    // Text and image items sit on slim layers above the video track; the top row is in front.
    private IReadOnlyList<OverlayItem> overlays = Array.Empty<OverlayItem>();
    internal IReadOnlyList<OverlayItem> Overlays
    {
        get => overlays;
        set
        {
            // Style edits (color, size, rotation…) don't change the timeline, so it isn't redrawn for them.
            bool same = value.Count == overlays.Count && value.Zip(overlays).All(p => ReferenceEquals(p.First, p.Second) || (p.First.Start == p.Second.Start && p.First.End == p.Second.End && p.First.Layer == p.Second.Layer && p.First.Kind == p.Second.Kind && p.First.Label == p.Second.Label));
            overlays = value;
            if (same) return;
            overlayVersion++; Height = PreferredHeight; InvalidateVisual();
        }
    }
    private int overlayVersion;
    internal int SelectedOverlay { get => selectedOverlay; set { selectedOverlay = value; overlayVersion++; InvalidateVisual(); } }
    private int selectedOverlay = -1;
    internal event Action<double, double, OverlayKind>? OverlayAdded;
    internal event Action<int>? OverlayPicked, OverlayRemoved;
    // Dragging an item: started (for undo), each change, and done.
    internal event Action? OverlayEditStarted, OverlayEditFinished;
    internal event Action<int, OverlayItem>? OverlayMoved;
    private const double RowHeight = 14, RowGap = 2;
    private int Rows => overlays.Count == 0 ? 0 : overlays.Max(o => o.Layer) + 1 + (extraRow ? 1 : 0);
    private bool extraRow;
    private double RowsSpan => Rows == 0 ? 0 : Rows * (RowHeight + RowGap) + 1;
    private double TrackTop => 8 + RowsSpan;
    private double RowTop(int layer) => 8 + (Rows - 1 - layer) * (RowHeight + RowGap);
    private Rect OverlayRect(OverlayItem o) { double x = XAt(o.Start); return new Rect(x, RowTop(o.Layer), Math.Max(3, XAt(o.End) - x), RowHeight); }
    private int OverlayAt(Point p)
    {
        // Front-most first, so the item on top wins when rows are tight.
        for (int i = overlays.Count - 1; i >= 0; i--)
            if (overlays[i].End >= ViewStart && overlays[i].Start <= ViewStart + Span && Rect.Inflate(OverlayRect(overlays[i]), 2, 1).Contains(p)) return i;
        return -1;
    }
    private int RowAt(double y) => Rows == 0 ? -1 : (int)Math.Clamp(Math.Floor((8 + Rows * (RowHeight + RowGap) - y) / (RowHeight + RowGap)), -1, Rows);
    internal event Action<CutRegion>? CutAdded, CutRemoved;
    // The part last clicked (a cut, speed part, zoom or text/picture item), outlined in white;
    // Delete removes it. Clicking a part's body selects it and still moves the playhead.
    private object? focusedPart;
    internal object? FocusedPart { get => focusedPart; set { focusedPart = value; cutsVersion++; slowVersion++; zoomVersion++; overlayVersion++; InvalidateVisual(); } }
    internal event Action<object?>? PartClicked;
    internal int StackedAtLastClick { get; private set; }
    // The selected cut-out, speed part or zoom can be lengthened or shortened by dragging its ends.
    internal event Action<object>? PartResizeStarted;
    internal event Action<object, double, double>? PartResized;
    internal event Action? PartResizeFinished;
    private object? resizing; private bool resizingStart; private double resizeStart, resizeEnd;
    // One-shot pick: the next click on the timeline sets a time (for "click where it should start").
    internal Action<double>? PickTime { get => pickTime; set { pickTime = value; Cursor = value != null ? Cursors.Cross : Cursors.Hand; } }
    private Action<double>? pickTime;
    // Cut-outs carry a small scissors tag that opens their times and a restore button.
    internal event Action<CutRegion>? CutTagClicked;
    private readonly List<(Rect Tag, CutRegion Cut)> cutTags = new();
    private (double Start, double End, Rect Band)? FocusedSpan() => focusedPart switch
    {
        CutRegion c when cuts.Contains(c) && (c.Lane < 0 || (lanesExpanded && c.Lane < lanes.Count)) => (c.Start, c.End, Band(c.Lane)),
        SpeedRegion s => slowRegions.FirstOrDefault(r => r.Start == s.Start && r.End == s.End) is { } r ? (r.Start, r.End, Band(-1)) : null,
        ZoomRegion z => zoomRegions.FirstOrDefault(r => r.Start == z.Start && r.End == z.End) is { } r ? (r.Start, r.End, Band(-1)) : null,
        _ => null
    };
    // Which end of the selected part is under the pointer: true for the start, false for the end.
    private bool? FocusedEdgeAt(Point p)
    {
        if (FocusedSpan() is not { } span || p.Y < span.Band.Top - 4 || p.Y > span.Band.Bottom + 4) return null;
        double a = XAt(span.Start), b = XAt(span.End);
        bool nearA = Math.Abs(p.X - a) <= 6 && span.Start >= ViewStart && span.Start <= ViewStart + Span, nearB = Math.Abs(p.X - b) <= 6 && span.End >= ViewStart && span.End <= ViewStart + Span;
        if (nearA && nearB) return Math.Abs(p.X - a) <= Math.Abs(p.X - b);
        return nearA ? true : nearB ? false : null;
    }
    // A time from the pointer that locks onto the playhead and other parts' edges (not the moving part's own).
    private double LockedTime(double x, double? ownStart = null, double? ownEnd = null)
    {
        if (Math.Abs(x - XAt(Position)) <= PlayheadLock) return Position;
        foreach (double edge in PartEdges())
            if ((ownStart == null || Math.Abs(edge - ownStart.Value) > 1e-9) && (ownEnd == null || Math.Abs(edge - ownEnd.Value) > 1e-9) && Math.Abs(x - XAt(edge)) <= PlayheadLock) return edge;
        return Snap(TimeAt(x));
    }
    private bool IsFocused(object part) => focusedPart switch
    {
        CutRegion c => part is CutRegion p && p == c,
        SpeedRegion s => part is SpeedRegion p && Math.Abs(p.Start - s.Start) < 1e-9 && Math.Abs(p.End - s.End) < 1e-9,
        ZoomRegion z => part is ZoomRegion p && Math.Abs(p.Start - z.Start) < 1e-9 && Math.Abs(p.End - z.End) < 1e-9,
        OverlayItem o => part is OverlayItem p && ReferenceEquals(p, o),
        _ => false
    };
    // The part under a point: on the video track the zoom, then speed part, then cut; on a lane its cut.
    // Parts can be stacked (a zoom over a speed part), so clicking the selected one again picks the
    // next one down, and so on round.
    private object? PartAt(Point p)
    {
        int band = BandAt(p); double t = TimeAt(p.X);
        var under = new List<object>();
        if (band == -1)
        {
            if (zoomRegions.FirstOrDefault(r => t >= r.Start && t < r.End) is { } z) under.Add(z);
            if (slowRegions.FirstOrDefault(r => t >= r.Start && t < r.End) is { } s) under.Add(s);
            if (cuts.FirstOrDefault(c => c.Lane < 0 && t >= c.Start && t < c.End) is { } c) under.Add(c);
        }
        else if (band >= 0 && cuts.FirstOrDefault(c => c.Lane == band && t >= c.Start && t < c.End) is { } laneCut) under.Add(laneCut);
        StackedAtLastClick = under.Count;
        if (under.Count == 0) return null;
        int current = under.FindIndex(IsFocused);
        return under[(current + 1) % under.Count];
    }
    private enum Drag { None, Start, End, Playhead, Pan, OverlayMove, OverlayStart, OverlayEnd, PartEdge }
    private Drag drag;
    private double grabOffset;
    // The item being dragged: its index, its state before the drag and whether it has moved yet.
    private int dragOverlay = -1; private OverlayItem? dragOriginal; private bool overlayDragMoved;
    private OverlayItem[] dragStartList = Array.Empty<OverlayItem>();
    private const double Inset = 20, TrackHeight = 32, LaneHeight = 24, LaneGap = 3, ScrollHeight = 6;
    private double LanesTop => TrackTop + TrackHeight + 4;
    // Audio lanes fold away behind a chevron beside the video track, so they only take room (and load) when opened.
    internal bool LanesExpanded
    {
        get => lanesExpanded;
        set
        {
            if (lanesExpanded == value) return;
            lanesExpanded = value;
            // Slide the lanes open or shut; skip the motion when Windows animations are off.
            if (SystemParameters.ClientAreaAnimation && PerformanceOptions.Animations && IsLoaded)
                BeginAnimation(LaneRevealProperty, new DoubleAnimation(value ? 1 : 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            else { BeginAnimation(LaneRevealProperty, null); LaneReveal = value ? 1 : 0; }
        }
    }
    private bool lanesExpanded, toggleHover;
    private static readonly DependencyProperty LaneRevealProperty = DependencyProperty.Register(nameof(LaneReveal), typeof(double), typeof(TrimTimeline),
        new PropertyMetadata(0.0, (d, _) => { var t = (TrimTimeline)d; t.lanesVersion++; t.Height = t.PreferredHeight; t.InvalidateVisual(); }));
    // 0 folded, 1 open; in between while the lanes slide.
    private double LaneReveal { get => (double)GetValue(LaneRevealProperty); set => SetValue(LaneRevealProperty, value); }
    internal event Action? LanesToggleRequested;
    private double LaneTop(int i) => LanesTop + i * (LaneHeight + LaneGap);
    private double LanesSpan => lanes.Count * (LaneHeight + LaneGap);
    private double ScrollTop => LanesTop + LanesSpan * LaneReveal + 2;
    internal double PreferredHeight => Math.Max(56, ScrollTop + ScrollHeight + 4);
    private static readonly Brush Track = Brush("#252D36"), Kept = Brush("#456D5D"), Accent = Brush("#9CE2C1"), Ink = Brush("#EDF0F3"), Muted = Brush("#9DA6B1");
    private static readonly Brush EndAccent = Brush("#F3BF84"), Tick = Brush("#4A5561"), LaneFill = Brush("#1A2027"), Wave = Brush("#7FA8C9"), WaveMuted = Brush("#3A444F");
    private static Brush Brush(string color) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!; b.Freeze(); return b; }
    private static readonly Pen TickPen = new(Tick, 1), MajorPen = new(Muted, 1), InkPen = new(Ink, 2);
    static TrimTimeline() { TickPen.Freeze(); MajorPen.Freeze(); InkPen.Freeze(); }
    internal bool IsDragging => drag != Drag.None && drag != Drag.Pan;
    internal double TimeAt(double x) => Math.Clamp(ViewStart + (x-Inset) / Math.Max(1, ActualWidth-2*Inset) * Span, ViewStart, Math.Min(Duration,ViewStart+Span));
    internal double XAt(double t) => Inset + Math.Clamp((t-ViewStart) / Math.Max(.001, Span), 0, 1) * Math.Max(1, ActualWidth-2*Inset);
    private double Snap(double t) => Math.Clamp(Math.Round(t * FrameRate) / FrameRate, 0, Duration);
    public TrimTimeline()
    {
        Focusable = true; FocusVisualStyle = null; Cursor = Cursors.Hand; Height = PreferredHeight;
        staticLayer.CacheMode = new BitmapCache { SnapsToDevicePixels = true };
        AddVisualChild(staticLayer); AddVisualChild(liveLayer);
    }

    // Two layers: the ruler, sections and waveforms are drawn once into a GPU-cached layer and
    // only redrawn when the view changes. Playback and scrubbing touch just the thin playhead
    // layer, so the waveform is never re-rasterized while the playhead moves.
    private readonly DrawingVisual staticLayer = new(), liveLayer = new();
    private object? cacheKey;
    protected override int VisualChildrenCount => 2;
    protected override Visual GetVisualChild(int index) => index == 0 ? staticLayer : liveLayer;
    protected override HitTestResult? HitTestCore(PointHitTestParameters p) =>
        new Rect(RenderSize).Contains(p.HitPoint) ? new PointHitTestResult(this, p.HitPoint) : null;
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (Duration <= 0 || ActualWidth <= 2*Inset)
        { using (staticLayer.RenderOpen()) { } using (liveLayer.RenderOpen()) { } cacheKey = null; return; }
        var key = (ViewStart, Span, ActualWidth, ActualHeight, Start, End, Duration, FrameRate, lanesVersion, SectionsKey(), SelectedSection, cutsVersion, toggleHover, slowVersion, zoomVersion, (overlayVersion, extraRow));
        if (!Equals(cacheKey, key))
        {
            using (var layer = staticLayer.RenderOpen()) DrawStatic(layer);
            cacheKey = key;
        }
        using var live = liveLayer.RenderOpen();
        if (Placing)
        {
            if (pendingCut is { } from)
            {
                double to = cutHover?.Time ?? from.Time;
                if (Math.Abs(to - from.Time) > 1e-6) DrawCut(live, from.Lane, Math.Min(from.Time, to), Math.Max(from.Time, to), 0, slowMode, zoomMode, preview: true);
                DrawCutter(live, from.Lane, from.Time);
            }
            if (cutHover is { } hover) DrawCutter(live, pendingCut?.Lane ?? hover.Lane, hover.Time);
        }
        double playhead = XAt(Position);
        if (Position >= ViewStart - 1e-9 && Position <= ViewStart + Span + 1e-9)
        {
            live.DrawLine(InkPen, new Point(playhead, 2), new Point(playhead, ScrollTop - 2));
            live.DrawRoundedRectangle(Ink, null, new Rect(playhead-5, 0, 10, 7), 2, 2);
        }
    }
    // Everything that will not be exported (outside the sections, or outside Start-End when there are
    // none) is shaded, so the part being kept stands out.
    private static readonly Brush Dim = Brush("#A00A0D11");
    private void DimOutsideKept(DrawingContext dc, Rect band, double radius)
    {
        var kept = Sections.Count > 0 ? Sections.Select(s => (s.Start, s.End)).OrderBy(k => k.Start).ToList() : new List<(double Start, double End)> { (Start, End) };
        kept.Add((double.MaxValue, double.MaxValue));
        dc.PushClip(new RectangleGeometry(band, radius, radius));
        double from = ViewStart;
        foreach (var (a, b) in kept)
        {
            double x0 = XAt(from), x1 = XAt(Math.Min(a, ViewStart + Span));
            if (x1 > x0 + .5) dc.DrawRectangle(Dim, null, new Rect(x0, band.Top, x1 - x0, band.Height));
            from = Math.Max(from, Math.Min(b, ViewStart + Span));
        }
        dc.Pop();
    }
    private int SectionsKey() { int hash = Sections.Count; foreach (var s in Sections) hash = HashCode.Combine(hash, s.Start, s.End); return hash; }
    private void DrawStatic(DrawingContext dc)
    {
        double width = ActualWidth-2*Inset, dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.DrawRoundedRectangle(Track, null, new Rect(Inset, TrackTop, width, TrackHeight), 6, 6);
        for (int i = 0; i < Sections.Count; i++)
        {
            var s = Sections[i];
            if (s.End<ViewStart || s.Start>ViewStart+Span) continue;
            var block = new Rect(XAt(s.Start), TrackTop+3, Math.Max(1, XAt(s.End)-XAt(s.Start)), TrackHeight-6);
            dc.DrawRoundedRectangle(Kept, i == SelectedSection ? new Pen(Ink, 1.5) : null, block, 3, 3);
            // Number each kept block (bottom-left, clear of the ruler) so it matches its chip below the timeline.
            var number = Text((i + 1).ToString(CultureInfo.InvariantCulture), 11, Ink, dpi);
            if (block.Width >= number.Width + 8) dc.DrawText(number, new Point(block.X + 5, block.Bottom - number.Height - 1));
        }
        DrawRuler(dc, width, dpi);
        DimOutsideKept(dc, new Rect(Inset, TrackTop, width, TrackHeight), 6);
        dc.DrawRoundedRectangle(null, new Pen(Accent, 1.5), new Rect(XAt(Start), TrackTop-2, Math.Max(1, XAt(End)-XAt(Start)), TrackHeight+4), 3, 3);
        foreach (var edge in new[] { (Time: Start, Color: Accent), (Time: End, Color: EndAccent) })
        {
            if (edge.Time<ViewStart || edge.Time>ViewStart+Span) continue;
            double x = XAt(edge.Time);
            // A slim bar; the grab area stays wide (see BeginDrag).
            dc.DrawRoundedRectangle(edge.Color, null, new Rect(x-3, TrackTop-3, 6, TrackHeight+6), 3, 3);
        }
        DrawLaneToggle(dc, dpi);
        cutTags.Clear();
        foreach (var cut in cuts) if (cut.Lane < 0) DrawCut(dc, cut.Lane, cut.Start, cut.End, dpi, focused: IsFocused(cut), cut: cut);
        DrawOverlayWash(dc);
        DrawSlowRegions(dc, dpi);
        DrawZoomRegions(dc, dpi);
        DrawOverlayRows(dc, dpi);
        DrawFocusGrips(dc);
        if (LaneReveal > 0 && lanes.Count > 0)
        {
            // Lanes slide out from under the video track and fade in as they open.
            dc.PushClip(new RectangleGeometry(new Rect(0, LanesTop, ActualWidth, LanesSpan * LaneReveal)));
            dc.PushOpacity(LaneReveal);
            for (int i = 0; i < lanes.Count; i++) { DrawLane(dc, lanes[i], LaneTop(i), width, dpi); DimOutsideKept(dc, new Rect(Inset, LaneTop(i), width, LaneHeight), 4); }
            foreach (var cut in cuts) if (cut.Lane >= 0) DrawCut(dc, cut.Lane, cut.Start, cut.End, dpi, focused: IsFocused(cut), cut: cut);
            dc.Pop(); dc.Pop();
        }
        if (ZoomFactor > 1.001)
        {
            // A slim scrollbar shows where the zoomed view sits in the whole clip.
            dc.DrawRoundedRectangle(Track, null, new Rect(Inset, ScrollTop, width, ScrollHeight), 3, 3);
            double left = Inset + ViewStart / Duration * width, thumb = Math.Max(12, Span / Duration * width);
            dc.DrawRoundedRectangle(Accent, null, new Rect(Math.Min(left, Inset + width - thumb), ScrollTop, thumb, ScrollHeight), 3, 3);
        }
    }
    private static readonly double[] Steps = { .01, .02, .05, .1, .2, .5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600 };
    // The ruler sits inside the top of the video track. Major ticks at least ~80 px apart get small labels;
    // minor ticks fill in down to frames when zoomed far in. Labels step aside from the trim handles.
    private void DrawRuler(DrawingContext dc, double width, double dpi)
    {
        double pixelsPerSecond = width / Span, frame = 1 / Math.Max(1, FrameRate);
        double major = Steps.FirstOrDefault(s => s * pixelsPerSecond >= 80, Steps[^1]);
        if (frame * pixelsPerSecond >= 80) major = Math.Max(frame, major);
        double minor = frame * pixelsPerSecond >= 10 && major <= .5 ? frame
            : Steps.Where(s => s < major && s * pixelsPerSecond >= 8 && IsMultiple(major, s)).DefaultIfEmpty(major).Min();
        int decimals = major >= 1 ? 0 : major >= .1 ? 1 : 2;
        var labelRight = double.MinValue;
        var handles = new[] { Start, End }.Where(t => t >= ViewStart && t <= ViewStart + Span).Select(XAt).ToArray();
        bool Blocked(double left, double w) => handles.Any(h => left < h + 8 && left + w > h - 8);
        double top = TrackTop + 1;
        // Integer tick counts avoid drift when stepping by a frame (1/60 s).
        for (long k = (long)Math.Ceiling(ViewStart / minor - 1e-9), last = (long)Math.Floor((ViewStart + Span) / minor + 1e-9); k <= last; k++)
        {
            double t = k * minor, x = XAt(t);
            bool isMajor = minor == major || IsMultiple(t, major);
            dc.DrawLine(isMajor ? MajorPen : TickPen, new Point(x, top), new Point(x, top + (isMajor ? 6 : 3)));
            if (!isMajor) continue;
            var label = Text(TimeLabel(t, decimals), 9, Muted, dpi);
            // Right of the tick by default; if a handle covers it, try the left side, then just past
            // the handle, and only fade it when there is nowhere to go.
            double lx = x + 3; bool faded = false;
            if (Blocked(lx, label.Width)) lx = x - 3 - label.Width;
            if (Blocked(lx, label.Width)) { var h = handles.First(h => Math.Abs(h - x) < label.Width + 12); lx = h + 9; faded = Blocked(lx, label.Width); }
            lx = Math.Clamp(lx, Inset + 2, ActualWidth - Inset - label.Width - 2);
            if (lx <= labelRight + 6) continue;
            if (faded) dc.PushOpacity(.35);
            dc.DrawText(label, new Point(lx, top)); labelRight = lx + label.Width;
            if (faded) dc.Pop();
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
    private readonly Dictionary<string, ((float[], double, double, double, double) Key, StreamGeometry Geometry)> waveCache = new();
    private void DrawLane(DrawingContext dc, AudioLane lane, double top, double width, double dpi)
    {
        dc.DrawRoundedRectangle(LaneFill, null, new Rect(Inset, top, width, LaneHeight), 4, 4);
        var peaks = lane.Peaks; double mid = top + LaneHeight / 2;
        // The outline only changes with the view, so edits elsewhere on the timeline reuse it.
        var waveKey = (peaks, ViewStart, Span, width, top);
        if (peaks.Length > 0 && waveCache.TryGetValue(lane.Name, out var cached) && cached.Key.Equals(waveKey)) dc.DrawGeometry(lane.Muted ? WaveMuted : Wave, null, cached.Geometry);
        else if (peaks.Length > 0)
        {
            // One filled outline (loudest peak per pixel column) rather than a stroke per column:
            // a single fill is far cheaper for WPF to rasterize than thousands of thin lines.
            int columns = Math.Max(1, (int)width);
            var upper = new Point[columns]; var lower = new Point[columns];
            for (int px = 0; px < columns; px++)
            {
                int a = (int)((ViewStart + px / width * Span) * 100), b = Math.Max(a + 1, (int)((ViewStart + (px + 1) / width * Span) * 100));
                float peak = 0; for (int i = Math.Max(0, a); i < Math.Min(peaks.Length, b); i++) peak = Math.Max(peak, peaks[i]);
                double h = Math.Max(.5, peak * (LaneHeight / 2 - 2));
                upper[px] = new Point(Inset + px + .5, mid - h); lower[columns - 1 - px] = new Point(Inset + px + .5, mid + h);
            }
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(upper[0], true, true);
                g.PolyLineTo(upper, false, false); g.PolyLineTo(lower, false, false);
            }
            geometry.Freeze();
            waveCache[lane.Name] = (waveKey, geometry);
            dc.DrawGeometry(lane.Muted ? WaveMuted : Wave, null, geometry);
        }
        // A single mixed track needs no name; separate tracks get a small tag to tell them apart.
        if (lanes.Count == 1 && !lane.Toggleable) return;
        var name = Text(lane.Name + (lane.Muted ? " · muted" : ""), 10, lane.Muted ? Muted : Ink, dpi);
        var chip = new Rect(Inset + 3, top + (LaneHeight - 15) / 2, name.Width + 10, 15);
        dc.DrawRoundedRectangle(Track, lane.Toggleable ? new Pen(lane.Muted ? Tick : Accent, 1) : null, chip, 4, 4);
        dc.DrawText(name, new Point(chip.X + 5, chip.Y + (chip.Height - name.Height) / 2));
    }
    private static readonly Brush CutFill = Brush("#99B42C38"), CutEdge = Brush("#E5484D");
    // Band rectangle for the video track (-1) or an audio lane.
    private Rect Band(int lane) => lane < 0 ? new Rect(Inset, TrackTop, ActualWidth - 2 * Inset, TrackHeight)
        : new Rect(Inset, LaneTop(lane), ActualWidth - 2 * Inset, LaneHeight);
    private int BandAt(Point p)
    {
        if (p.Y >= TrackTop - 4 && p.Y <= TrackTop + TrackHeight + 4) return -1;
        // The layer rows count as the video track for text and image tools.
        if (overlayMode != null && p.Y >= 4 && p.Y < TrackTop) return -1;
        if (lanesExpanded) for (int i = 0; i < lanes.Count; i++) { var b = Band(i); if (p.Y >= b.Top && p.Y <= b.Bottom) return i; }
        return -2;
    }
    // The audio toggle lives in the left margin beside the video track, so folding costs no height.
    private Rect ToggleArea => new(0, TrackTop, Inset - 7, TrackHeight);
    private static readonly Typeface Glyphs = new("Segoe MDL2 Assets");
    // Just a chevron that turns down as the lanes open; a small red dot marks muted audio while folded.
    private void DrawLaneToggle(DrawingContext dc, double dpi)
    {
        if (lanes.Count == 0) return;
        var strong = lanesExpanded || toggleHover ? Ink : Muted;
        var chevron = new FormattedText("", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Glyphs, 9, strong, dpi);
        var center = new Point((Inset - 7) / 2, TrackTop + TrackHeight / 2);
        if (toggleHover) dc.DrawRoundedRectangle(Track, null, new Rect(0, TrackTop, Inset - 7, TrackHeight), 3, 3);
        dc.PushTransform(new RotateTransform(90 * LaneReveal, center.X, center.Y));
        dc.DrawText(chevron, new Point(center.X - chevron.Width / 2, center.Y - chevron.Height / 2));
        dc.Pop();
        if (!lanesExpanded && cuts.Any(c => c.Lane >= 0)) dc.DrawEllipse(CutEdge, null, new Point(center.X, TrackTop + TrackHeight - 5), 2, 2);
    }
    private static readonly Brush SlowFill = Brush("#558B7CF6"), SlowEdge = Brush("#A99BFA"), SlowInk = Brush("#15121F");
    // Zoom is teal; drawn translucent over slow motion, the two blend into a blue-violet.
    private static readonly Brush ZoomFill = Brush("#552DD4BF"), ZoomEdge = Brush("#5EEAD4"), ZoomInk = Brush("#0B1F1C");
    private static readonly Typeface Glyph = new("Segoe MDL2 Assets");
    // A cut-out (with its scissors tag when there's room), or the preview of a part being placed.
    private void DrawCut(DrawingContext dc, int lane, double start, double end, double dpi, bool slow = false, bool zoom = false, bool focused = false, bool preview = false, CutRegion? cut = null)
    {
        if (end < ViewStart || start > ViewStart + Span || lane >= lanes.Count || (lane >= 0 && LaneReveal <= 0)) return;
        var band = Band(lane); double x = XAt(start), w = Math.Max(2, XAt(end) - x);
        var area = new Rect(x, band.Top, w, band.Height);
        if (preview && overlayMode != null && lane < 0) { dc.DrawRoundedRectangle(OverlayWash, new Pen(OverlayFill, 1), area, 3, 3); return; }
        dc.DrawRoundedRectangle(zoom ? ZoomFill : slow ? SlowFill : CutFill, new Pen(focused ? Ink : zoom ? ZoomEdge : slow ? SlowEdge : CutEdge, focused ? 1.5 : 1), area, 3, 3);
        if (cut != null && w >= 20)
        {
            var icon = new FormattedText("", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Glyph, 8, Ink, dpi);
            var tag = new Rect(x + 2, band.Top + 2, icon.Width + 8, 13);
            dc.DrawRoundedRectangle(CutEdge, null, tag, 3, 3);
            dc.DrawText(icon, new Point(tag.X + 4, tag.Y + (tag.Height - icon.Height) / 2));
            cutTags.Add((tag, cut));
        }
    }
    // Small handles on the ends of the selected part show they can be dragged.
    private void DrawFocusGrips(DrawingContext dc)
    {
        if (FocusedSpan() is not { } span) return;
        foreach (double t in new[] { span.Start, span.End })
            if (t >= ViewStart && t <= ViewStart + Span)
                dc.DrawRoundedRectangle(Ink, null, new Rect(XAt(t) - 2, span.Band.Top + 4, 4, Math.Max(6, span.Band.Height - 8)), 2, 2);
    }
    // Zoom regions: teal boxes with a magnifier tag (top-right) that selects the region for editing.
    private void DrawZoomRegions(DrawingContext dc, double dpi)
    {
        zoomTags.Clear();
        for (int i = 0; i < zoomRegions.Count; i++)
        {
            var r = zoomRegions[i];
            if (r.End < ViewStart || r.Start > ViewStart + Span) { zoomTags.Add(Rect.Empty); continue; }
            double x = XAt(r.Start), w = Math.Max(2, XAt(r.End) - x);
            dc.DrawRoundedRectangle(ZoomFill, new Pen(i == selectedZoom || IsFocused(r) ? Ink : ZoomEdge, i == selectedZoom || IsFocused(r) ? 1.5 : 1), new Rect(x, TrackTop, w, TrackHeight), 3, 3);
            var icon = new FormattedText("\uE71E", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Glyph, 8, ZoomInk, dpi);
            var label = Text(r.MaxZoom.ToString("0.#", CultureInfo.InvariantCulture) + "×", 10, ZoomInk, dpi);
            double width = icon.Width + label.Width + 11;
            var tag = new Rect(Math.Max(x + 1, x + w - width - 3), TrackTop + 2, width, 13);
            dc.DrawRoundedRectangle(ZoomEdge, null, tag, 3, 3);
            dc.DrawText(icon, new Point(tag.X + 4, tag.Y + (tag.Height - icon.Height) / 2));
            dc.DrawText(label, new Point(tag.X + 6 + icon.Width, tag.Y + (tag.Height - label.Height) / 2));
            zoomTags.Add(tag);
        }
    }
    private int ZoomTagAt(Point p) { for (int i = 0; i < zoomTags.Count; i++) if (zoomTags[i].Contains(p)) return i; return -1; }
    // Text and images are amber. On the video track a faint amber wash marks where any are shown,
    // blending with the speed and zoom colors; the items themselves sit on the rows above.
    private static readonly Brush OverlayFill = Brush("#F59E0B"), OverlayDim = Brush("#B7791F"), OverlayWash = Brush("#40F59E0B"), OverlayInk = Brush("#2A1A02"), RowFill = Brush("#161B21");
    private static readonly Pen OverlayCutterPen = new(Brush("#FBBF24"), 1.5);
    private void DrawOverlayWash(DrawingContext dc)
    {
        // Merge the spans first so overlapping items don't darken the wash.
        double end = double.MinValue, start = 0;
        foreach (var o in overlays.OrderBy(o => o.Start))
        {
            if (o.Start > end) { if (end > start) Wash(start, end); start = o.Start; end = o.End; }
            else end = Math.Max(end, o.End);
        }
        if (end > start) Wash(start, end);
        void Wash(double a, double b)
        {
            if (b < ViewStart || a > ViewStart + Span) return;
            double x = XAt(a); dc.DrawRoundedRectangle(OverlayWash, null, new Rect(x, TrackTop, Math.Max(2, XAt(b) - x), TrackHeight), 3, 3);
        }
    }
    private void DrawOverlayRows(DrawingContext dc, double dpi)
    {
        if (Rows == 0) return;
        double width = ActualWidth - 2 * Inset;
        for (int r = 0; r < Rows; r++) dc.DrawRoundedRectangle(RowFill, null, new Rect(Inset, RowTop(r), width, RowHeight), 3, 3);
        dc.PushClip(new RectangleGeometry(new Rect(Inset, 0, width, TrackTop)));
        for (int i = 0; i < overlays.Count; i++)
        {
            var o = overlays[i];
            if (o.End < ViewStart || o.Start > ViewStart + Span) continue;
            var rect = OverlayRect(o); bool selected = i == selectedOverlay || IsFocused(o);
            dc.DrawRoundedRectangle(selected ? OverlayFill : OverlayDim, selected ? new Pen(Ink, 1.2) : null, rect, 3, 3);
            if (rect.Width < 16) continue;
            var icon = new FormattedText(o.Kind == OverlayKind.Image ? "" : "", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Glyph, 8, OverlayInk, dpi);
            dc.DrawText(icon, new Point(rect.X + 4, rect.Y + (RowHeight - icon.Height) / 2));
            var label = Text(o.Label, 9, OverlayInk, dpi);
            label.MaxTextWidth = Math.Max(1, rect.Width - icon.Width - 11); label.MaxLineCount = 1; label.Trimming = TextTrimming.CharacterEllipsis;
            if (rect.Width > icon.Width + 20) dc.DrawText(label, new Point(rect.X + icon.Width + 7, rect.Y + (RowHeight - label.Height) / 2));
        }
        dc.Pop();
    }
    // Speed parts: a purple box on the video track with a small speed tag (bottom-right) that opens
    // the speed choices when clicked. Tag rectangles are kept for hit-testing.
    private void DrawSlowRegions(DrawingContext dc, double dpi)
    {
        slowTags.Clear();
        for (int i = 0; i < slowRegions.Count; i++)
        {
            var r = slowRegions[i];
            if (r.End < ViewStart || r.Start > ViewStart + Span) { slowTags.Add(Rect.Empty); continue; }
            double x = XAt(r.Start), w = Math.Max(2, XAt(r.End) - x);
            dc.DrawRoundedRectangle(SlowFill, new Pen(i == selectedSlow || IsFocused(r) ? Ink : SlowEdge, i == selectedSlow || IsFocused(r) ? 1.5 : 1), new Rect(x, TrackTop, w, TrackHeight), 3, 3);
            var label = Text(r.Speed.ToString("0.##", CultureInfo.InvariantCulture) + "×", 10, SlowInk, dpi);
            var tag = new Rect(Math.Max(x + 1, x + w - label.Width - 11), TrackTop + TrackHeight - 15, label.Width + 8, 13);
            dc.DrawRoundedRectangle(SlowEdge, null, tag, 3, 3);
            dc.DrawText(label, new Point(tag.X + 4, tag.Y + (tag.Height - label.Height) / 2));
            slowTags.Add(tag);
        }
    }
    private int SlowTagAt(Point p) { for (int i = 0; i < slowTags.Count; i++) if (slowTags[i].Contains(p)) return i; return -1; }
    // The cutter: red (purple for speed, teal for zoom), or white while it is locked onto the playhead.
    private static readonly Pen CutterPen = new(Brush("#E5484D"), 1.5), SlowCutterPen = new(Brush("#A99BFA"), 1.5), ZoomCutterPen = new(Brush("#5EEAD4"), 1.5), LockedCutterPen = new(Brush("#EDF0F3"), 1.5);
    private void DrawCutter(DrawingContext dc, int lane, double t)
    {
        if (t < ViewStart || t > ViewStart + Span || lane >= lanes.Count || (lane >= 0 && LaneReveal <= 0)) return;
        var band = Band(lane); double x = XAt(t);
        // Text and image markers reach up through the layer rows too.
        double top = overlayMode != null && lane < 0 ? 4 : band.Top - 3;
        dc.DrawLine(LockedAt(t) ? LockedCutterPen : overlayMode != null ? OverlayCutterPen : zoomMode ? ZoomCutterPen : slowMode ? SlowCutterPen : CutterPen, new Point(x, top), new Point(x, band.Bottom + 3));
    }
    private void PlaceCutPoint(Point p)
    {
        int band = pendingCut?.Lane ?? BandAt(p);
        if (band < -1) return;
        if (WholeClipTool) band = -1; // speed parts, zoom, text and images cover the whole picture and sound
        double t = CutTimeAt(p.X);
        if (pendingCut is not { } from) pendingCut = (band, t);
        else
        {
            pendingCut = null;
            double a = Math.Min(from.Time, t), b = Math.Max(from.Time, t);
            if (b - a >= 1 / Math.Max(1, FrameRate) - 1e-9)
            {
                if (overlayMode is { } kind) OverlayAdded?.Invoke(a, b, kind);
                else if (zoomMode) ZoomAdded?.Invoke(a, b);
                else if (slowMode) SlowAdded?.Invoke(a, b); else CutAdded?.Invoke(new CutRegion(from.Lane, a, b));
            }
        }
        InvalidateVisual();
    }
    private void UpdateCutHover(Point p)
    {
        int band = pendingCut?.Lane ?? BandAt(p);
        (int, double)? next = Placing && band > -2 ? (WholeClipTool ? -1 : band, CutTimeAt(p.X)) : null;
        if (!Equals(next, cutHover)) { cutHover = next; InvalidateVisual(); }
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
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Duration<=0 || ActualWidth<=2*Inset) return;
        Focus(); var point = e.GetPosition(this);
        if (pickTime != null) { var pick = pickTime; PickTime = null; pick(LockedTime(point.X)); e.Handled = true; return; }
        if (lanes.Count > 0 && ToggleArea.Contains(point)) { LanesToggleRequested?.Invoke(); e.Handled = true; return; }
        if (!Placing && pendingCut == null && FocusedEdgeAt(point) is bool atStart && FocusedSpan() is { } span)
        {
            drag = Drag.PartEdge; resizing = focusedPart; resizingStart = atStart; resizeStart = span.Start; resizeEnd = span.End;
            pressPoint = point; overlayDragMoved = false; CaptureMouse(); e.Handled = true; return;
        }
        if (pendingCut == null && cutTags.FindIndex(t => t.Tag.Contains(point)) is int cutTag and >= 0) { CutTagClicked?.Invoke(cutTags[cutTag].Cut); e.Handled = true; return; }
        if (pendingCut == null && OverlayAt(point) is int item and >= 0)
        {
            // Press on an item: a click opens it, a drag moves it (or its edges) and changes its layer.
            var rect = OverlayRect(overlays[item]);
            drag = point.X - rect.Left <= 5 && rect.Width > 14 ? Drag.OverlayStart : rect.Right - point.X <= 5 && rect.Width > 14 ? Drag.OverlayEnd : Drag.OverlayMove;
            dragOverlay = item; dragOriginal = overlays[item]; dragStartList = overlays.ToArray(); overlayDragMoved = false; pressPoint = point;
            CaptureMouse(); e.Handled = true; return;
        }
        if (pendingCut == null && ZoomTagAt(point) is int zoomTag and >= 0) { ZoomTagClicked?.Invoke(zoomTag); e.Handled = true; return; }
        if (pendingCut == null && SlowTagAt(point) is int tag and >= 0) { SlowTagClicked?.Invoke(tag); e.Handled = true; return; }
        if (Placing && (pendingCut != null || BandAt(point) > -2))
        {
            // Wait to see whether this is a click (place a cut point) or a drag (move the playhead).
            cutPress = true; pressPoint = point; CaptureMouse(); e.Handled = true; return;
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
        PartClicked?.Invoke(PartAt(point));
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
        if (IsMouseCaptured && drag == Drag.PartEdge && resizing != null)
        {
            if (!overlayDragMoved && (p - pressPoint).Length <= 2) return;
            if (!overlayDragMoved) { overlayDragMoved = true; PartResizeStarted?.Invoke(resizing); }
            double t = LockedTime(p.X, resizeStart, resizeEnd), frame = 1 / Math.Max(1, FrameRate);
            if (resizingStart) PartResized?.Invoke(resizing, Math.Clamp(t, 0, resizeEnd - frame), resizeEnd);
            else PartResized?.Invoke(resizing, resizeStart, Math.Clamp(t, resizeStart + frame, Duration));
            return;
        }
        if (IsMouseCaptured && drag is Drag.OverlayMove or Drag.OverlayStart or Drag.OverlayEnd)
        {
            if (!overlayDragMoved && (p - pressPoint).Length <= 3) return;
            if (!overlayDragMoved) { overlayDragMoved = true; OverlayEditStarted?.Invoke(); }
            DragOverlay(p); return;
        }
        if (IsMouseCaptured)
        {
            if (cutPress)
            {
                if ((p - pressPoint).Length <= 3) return;
                cutPress = false; BeginDrag(pressPoint); DragStarted?.Invoke();
            }
            if (drag == Drag.Pan) PanToPointer(p.X); else MoveTo(p.X);
            UpdateCutHover(p);
            return;
        }
        UpdateCutHover(p);
        bool hover = lanes.Count > 0 && ToggleArea.Contains(p);
        if (hover != toggleHover) { toggleHover = hover; InvalidateVisual(); }
        if (hover) { Cursor = Cursors.Hand; ToolTip = lanesExpanded ? "Hide the audio tracks" : "Show the audio tracks"; return; }
        if (pickTime != null) { Cursor = Cursors.Cross; ToolTip = "Click where it should go · Esc cancels"; return; }
        if (!Placing && pendingCut == null && FocusedEdgeAt(p) is bool edge) { Cursor = Cursors.SizeWE; ToolTip = edge ? "Drag to change when it starts" : "Drag to change when it ends"; return; }
        if (pendingCut == null && cutTags.Any(t => t.Tag.Contains(p))) { Cursor = Cursors.Hand; ToolTip = "Change this cut-out's times, or restore it"; return; }
        if (pendingCut == null && OverlayAt(p) is int hoverItem and >= 0)
        {
            var rect = OverlayRect(overlays[hoverItem]);
            Cursor = rect.Width > 14 && (p.X - rect.Left <= 5 || rect.Right - p.X <= 5) ? Cursors.SizeWE : Cursors.SizeAll;
            ToolTip = $"{overlays[hoverItem].Label} · click to edit, drag to move, drag up or down to change layer, drag an edge to change its length · right-click removes";
            return;
        }
        if (pendingCut == null && ZoomTagAt(p) >= 0) { Cursor = Cursors.Hand; ToolTip = "Edit this zoom · also selects it, so you can drag its ends"; return; }
        if (pendingCut == null && SlowTagAt(p) >= 0) { Cursor = Cursors.Hand; ToolTip = "Change this part's speed · also selects it, so you can drag its ends"; return; }
        if (Placing && (pendingCut != null || BandAt(p) > -2))
        {
            Cursor = Cursors.Cross;
            ToolTip = overlayMode is { } kind
                ? pendingCut == null ? $"Click to start the {(kind == OverlayKind.Text ? "text" : "picture")}; it snaps to the playhead and other parts' edges" : $"Click where the {(kind == OverlayKind.Text ? "text" : "picture")} ends · Esc leaves the tool"
                : zoomMode
                ? pendingCut == null ? "Click to start a zoom; it snaps to the playhead and speed-part edges. Right-click a zoom to remove it" : "Click to finish the zoom · Esc leaves the tool"
                : slowMode
                ? pendingCut == null ? "Click to start a speed part; drag to move the playhead. Right-click a speed part to remove it" : "Click to finish the speed part · Esc leaves the tool"
                : pendingCut == null ? "Click to start a cut; drag to move the playhead. Right-click a cut to restore it" : "Click to finish the cut · Esc cancels";
            return;
        }
        int lane = LaneAt(p);
        Cursor = lane >= 0 && lanes[lane].Toggleable ? Cursors.Hand
            : p.Y>=TrackTop-7 && p.Y<=TrackTop+TrackHeight+7 && Math.Min(Math.Abs(p.X-XAt(Start)),Math.Abs(p.X-XAt(End)))<=14 ? Cursors.SizeWE : Cursors.Hand;
        ToolTip = lane >= 0 ? (lanes[lane].Toggleable ? $"Click to {(lanes[lane].Muted ? "include" : "mute")} {lanes[lane].Name.ToLowerInvariant()} audio in exports" : "Record with separate tracks to adjust desktop and microphone audio separately")
            : "Click to seek; drag the edges to trim. Wheel zooms · Shift+wheel pans · Ctrl+wheel changes speed";
    }
    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (toggleHover || (cutHover != null && !IsMouseCaptured)) { toggleHover = false; if (!IsMouseCaptured) cutHover = null; InvalidateVisual(); }
    }
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (pendingCut == null && OverlayAt(e.GetPosition(this)) is int item and >= 0) { OverlayRemoved?.Invoke(item); e.Handled = true; return; }
        if (!Placing) return;
        // Right-click cancels a half-placed cut, or restores the cut (or slow part) under the pointer.
        if (pendingCut != null) { CancelPendingCut(); e.Handled = true; return; }
        var p = e.GetPosition(this); int band = BandAt(p); double t = TimeAt(p.X);
        if (zoomMode)
        {
            int zoom = zoomRegions.ToList().FindIndex(r => t >= r.Start && t <= r.End);
            if (zoom >= 0 && band > -2) { ZoomRemoved?.Invoke(zoom); e.Handled = true; }
            return;
        }
        if (slowMode)
        {
            int slow = slowRegions.ToList().FindIndex(r => t >= r.Start && t <= r.End);
            if (slow >= 0 && band > -2) { SlowRemoved?.Invoke(slow); e.Handled = true; }
            return;
        }
        var hit = cuts.FirstOrDefault(c => c.Lane == band && t >= c.Start && t <= c.End);
        if (hit != null) { CutRemoved?.Invoke(hit); e.Handled = true; }
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsMouseCaptured) return;
        if (drag is Drag.OverlayMove or Drag.OverlayStart or Drag.OverlayEnd)
        {
            int picked = dragOverlay; bool moved = overlayDragMoved;
            ReleaseMouseCapture(); e.Handled = true;
            if (!moved && picked >= 0) OverlayPicked?.Invoke(picked);
            return;
        }
        if (cutPress) { cutPress = false; ReleaseMouseCapture(); PlaceCutPoint(pressPoint); e.Handled = true; return; }
        if (drag != Drag.Pan) MoveTo(e.GetPosition(this).X);
        ReleaseMouseCapture(); e.Handled=true;
    }
    protected override void OnLostMouseCapture(MouseEventArgs e)
    { base.OnLostMouseCapture(e); EndDrag(); }
    internal void EndDrag()
    {
        if (drag == Drag.None) return;
        if (drag == Drag.PartEdge)
        {
            drag = Drag.None; resizing = null;
            if (overlayDragMoved) { overlayDragMoved = false; PartResizeFinished?.Invoke(); }
            return;
        }
        if (drag is Drag.OverlayMove or Drag.OverlayStart or Drag.OverlayEnd)
        {
            drag = Drag.None; dragOverlay = -1; dragOriginal = null;
            if (overlayDragMoved) { overlayDragMoved = false; Overlays = OverlayOrder.Compact(overlays); OverlayEditFinished?.Invoke(); }
            return;
        }
        bool quiet = drag is Drag.Pan; drag=Drag.None; if (!quiet) DragCompleted?.Invoke();
    }
    // Moves the dragged item in time (snapping to the playhead and other parts' edges) and between
    // layers; an item never overlaps another on the same layer.
    private void DragOverlay(Point p)
    {
        if (dragOriginal is not { } o || dragOverlay < 0 || dragOverlay >= overlays.Count) return;
        double perPixel = Span / Math.Max(1, ActualWidth - 2 * Inset), shift = (p.X - pressPoint.X) * perPixel, frame = 1 / Math.Max(1, FrameRate);
        double SnapEdge(double t, out bool locked)
        {
            locked = true;
            foreach (double edge in PartEdges(o).Prepend(Position)) if (Math.Abs(XAt(edge) - XAt(t)) <= PlayheadLock) return edge;
            locked = false; return Snap(t);
        }
        // Work from the items as they were when the drag began, so layer swaps don't pile up.
        var list = dragStartList.Length == overlays.Count ? dragStartList.ToArray() : overlays.ToArray();
        var others = list.Where((_, i) => i != dragOverlay).ToList();
        bool Free(int layer, double a, double b) => !others.Any(x => x.Layer == layer && x.End > a + 1e-9 && x.Start < b - 1e-9);
        var next = o;
        if (drag == Drag.OverlayMove)
        {
            double length = o.Length, start = Math.Clamp(o.Start + shift, 0, Math.Max(0, Duration - length));
            // Whichever edge is closer to something to lock onto wins.
            double a = SnapEdge(start, out bool lockA), b = SnapEdge(start + length, out bool lockB) - length;
            start = Math.Clamp(lockA || !lockB ? a : b, 0, Math.Max(0, Duration - length));
            double end = start + length;
            int top = others.Count == 0 ? 0 : others.Max(x => x.Layer) + 1;
            int layer = o.Layer;
            // Above the top row brings it to the front; down on the video track sends it to the back;
            // onto another row puts it there, trading places with whatever overlaps it.
            int target = p.Y >= TrackTop ? -1 : Math.Min(RowAt(p.Y), top);
            bool Overlaps(OverlayItem x) => x.End > start + 1e-9 && x.Start < end - 1e-9;
            if (target < 0 && !(o.Layer == 0 && Free(0, start, end)))
            {
                if (Free(0, start, end)) layer = 0;
                else { for (int i = 0; i < list.Length; i++) if (i != dragOverlay) list[i] = list[i] with { Layer = list[i].Layer + 1 }; layer = 0; }
            }
            else if (target >= 0 && target != o.Layer)
            {
                var blockers = Enumerable.Range(0, list.Length).Where(i => i != dragOverlay && list[i].Layer == target && Overlaps(list[i])).ToList();
                bool swapFits = blockers.All(bi => !Enumerable.Range(0, list.Length).Any(j => j != dragOverlay && !blockers.Contains(j) && list[j].Layer == o.Layer && list[j].End > list[bi].Start + 1e-9 && list[j].Start < list[bi].End - 1e-9));
                if (blockers.Count == 0) layer = target;
                else if (swapFits) { foreach (int bi in blockers) list[bi] = list[bi] with { Layer = o.Layer }; layer = target; }
            }
            others = list.Where((_, i) => i != dragOverlay).ToList();
            if (!Free(layer, start, end)) layer = Enumerable.Range(0, list.Length + 1).First(l => Free(l, start, end));
            next = o with { Start = start, End = end, Layer = layer };
        }
        else
        {
            var neighbours = others.Where(x => x.Layer == o.Layer).ToList();
            if (drag == Drag.OverlayStart)
            {
                double limit = neighbours.Where(x => x.End <= o.Start + 1e-9).Select(x => x.End).DefaultIfEmpty(0).Max();
                next = o with { Start = Math.Clamp(SnapEdge(o.Start + shift, out _), limit, o.End - frame) };
            }
            else
            {
                double limit = neighbours.Where(x => x.Start >= o.End - 1e-9).Select(x => x.Start).DefaultIfEmpty(Duration).Min();
                next = o with { End = Math.Clamp(SnapEdge(o.End + shift, out _), o.Start + frame, limit) };
            }
        }
        list[dragOverlay] = next;
        overlays = list; overlayVersion++; Height = PreferredHeight; InvalidateVisual();
        OverlayMoved?.Invoke(dragOverlay, next);
    }
    // Wheel zooms around the pointer, Shift+wheel pans, Ctrl+wheel steps the preview speed.
    // Deltas are scaled rather than counted, so touchpads and free-spinning wheels stay smooth.
    internal event Action<int>? SpeedStepRequested;
    private int speedWheel;
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.Control)
        {
            speedWheel += e.Delta;
            while (Math.Abs(speedWheel) >= 120) { int step = Math.Sign(speedWheel); speedWheel -= step * 120; SpeedStepRequested?.Invoke(step); }
        }
        else if (mods == ModifierKeys.Shift) PanTo(ViewStart - e.Delta / 120.0 * Span * .15);
        else Zoom(Math.Pow(1.5, e.Delta / 120.0), TimeAt(e.GetPosition(this).X));
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
