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
// A freeze frame: the picture at At (source time) holds for Seconds, then the clip plays on from the
// same moment. It adds time to the export instead of replacing footage; the hold is silent.
internal sealed record FreezeFrame(double At, double Seconds)
{
    internal const double MinSeconds = .2, MaxSeconds = 10;
}

internal sealed class TrimTimeline : FrameworkElement
{
    internal double Duration = 1, Start, End, Position, FrameRate = 60;
    // The view: ViewStart and ViewDuration are in view time, which is the recording's time, or with the
    // finished view on, the finished video's (sections in play order, speed parts stretched, freezes held).
    internal double ViewStart, ViewDuration;
    internal double VisibleDuration => ViewDuration > 0 ? Math.Min(ViewDuration,Total) : Total;
    internal double ZoomFactor => Total / Math.Max(.001, VisibleDuration);
    private double Span => VisibleDuration;
    internal event Action? ViewChanged;
    private void RefreshView() { InvalidateVisual(); ViewChanged?.Invoke(); }
    internal void Fit() { ViewStart=0; ViewDuration=0; RefreshView(); }
    internal void PanTo(double start) { ViewStart=Math.Clamp(start,0,Math.Max(0,Total-Span)); RefreshView(); }
    // Zooms down to a dozen frames, so individual frames get their own ticks.
    private double MinimumSpan => Math.Min(Total, Math.Max(.2, 12 / Math.Max(1, FrameRate)));
    // Zoom around a moment of the recording (the playhead, say).
    internal void Zoom(double factor, double center) => ZoomView(factor, center == Position ? PositionView : ToView(center));
    private void ZoomView(double factor, double center)
    {
        double old=Span, next=Math.Clamp(old/factor,MinimumSpan,Total);
        double ratio=Math.Clamp((center-ViewStart)/old,0,1);
        ViewStart=Math.Clamp(center-ratio*next,0,Math.Max(0,Total-next)); ViewDuration=next>=Total-1e-9 ? 0 : next; RefreshView();
    }
    internal void Reveal(double time)
    {
        double v = time == Position ? PositionView : ToView(time);
        if (v<ViewStart || v>ViewStart+Span) PanTo(v-Span/2);
    }

    // ---- Finished view ----
    // Off: the timeline is the whole recording. On: it's the finished video, laid out by Map, and every
    // part slides with its footage. Parts are always stored in recording time.
    private bool finished;
    internal bool Finished { get => finished; set { if (finished == value) return; finished = value; ViewStart = 0; ViewDuration = 0; mapVersion++; Height = PreferredHeight; RefreshView(); } }
    // Laid out again (lazily) when the sections, range, speed parts, freezes or export speed change.
    internal SequenceMap Map
    {
        get
        {
            if (built != null && !remap) return built;
            remap = false;
            IReadOnlyList<KeepSection> ranges = Sections.Count > 0 ? Sections.ToArray() : new[] { new KeepSection(Start, Math.Min(Duration, Math.Max(Start + .1, End))) };
            built = new SequenceMap(ranges, new ShareExportOptions { Speed = exportSpeed, SlowRegions = slowRegions, Freezes = freezes });
            mapVersion++;
            if (finished && ViewStart + Span > Total) ViewStart = Math.Max(0, Total - Span);
            return built;
        }
    }
    private SequenceMap? built; private bool remap = true; private (int, double, double, double) builtShape;
    private SequenceMap? sequence => finished ? Map : null;
    internal void Remap() { remap = true; if (finished) InvalidateVisual(); }
    internal double ExportSpeed { get => exportSpeed; set { if (exportSpeed == value) return; exportSpeed = value; Remap(); } }
    private double exportSpeed = 1;
    private int mapVersion;
    private bool Mapped => finished && sequence != null && sequence.Total > 0;
    // How long the timeline is in view time.
    internal double Total => Mapped ? sequence!.Total : Duration;
    // Seconds into a freeze's hold the playhead is (the recording's time stands still meanwhile).
    internal double HoldOffset { get => holdOffset; set { if (Math.Abs(holdOffset - value) < 1e-9) return; holdOffset = value; InvalidateVisual(); } }
    private double holdOffset;
    internal double ToView(double source) => Mapped ? sequence!.ToOutput(source) : source;
    internal (double Source, double Hold) FromView(double view) => Mapped ? sequence!.ToSource(view) : (view, 0);
    internal double PositionView => ToView(Position) + (Mapped ? holdOffset : 0);
    internal event Action? ViewToggled;
    // Whether any of a span of the recording is in view.
    private bool Shows(double start, double end)
    {
        if (!Mapped) return !(end < ViewStart || start > ViewStart + Span);
        if (end - start <= 1e-9) { double v = sequence!.ToOutput(start); return v >= ViewStart && v <= ViewStart + Span; }
        foreach (var (a, b) in sequence!.Spans(start, end)) if (b >= ViewStart && a <= ViewStart + Span) return true;
        return false;
    }
    // Where a span of the recording is drawn: one stretch, or in the finished view one per place it plays.
    private IEnumerable<(double X0, double X1)> XSpans(double start, double end)
    {
        if (!Mapped) { yield return (XAt(start), XAt(end)); yield break; }
        foreach (var (a, b) in sequence!.Spans(start, end))
            if (b >= ViewStart && a <= ViewStart + Span) yield return (VX(a), VX(b));
    }
    // Parts that can start or stop partway through a freeze's hold (text, pictures, shapes, videos, zooms):
    // in the finished view just the part of the hold they cover; in the whole recording, one that lives
    // inside a hold is a narrow chip beside the freeze's slit.
    private IEnumerable<(double X0, double X1)> XSpans(Moment from, Moment to)
    {
        if (!Mapped)
        {
            if (Math.Abs(from.At - to.At) < 1e-9) { if (Shows(from.At, from.At)) { double x = XAt(from.At); yield return (x + 2, x + 8); } yield break; }
            foreach (var s in XSpans(from.At, to.At)) yield return s;
            yield break;
        }
        foreach (var (a, b) in sequence!.Spans(from, to))
            if (b >= ViewStart && a <= ViewStart + Span) yield return (VX(a), VX(b));
    }
    private bool Shows(Moment from, Moment to) => XSpans(from, to).Any();
    // Where a moment (maybe partway into a hold) is in view time.
    internal double ViewOf(Moment moment, bool end) => Mapped ? sequence!.ToOutput(moment, end) : moment.At;
    // The moment of the finished video (or recording) under the pointer, hold time included.
    private Moment MomentAt(double x) { var (source, hold) = FromView(VT(x)); return new Moment(source, hold); }
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
    internal bool CutMode { get => cutMode; set { ClearTools(); cutMode = value; } }
    // Speed tool: the same two clicks mark a stretch that plays slower or faster (video and audio together).
    internal bool SlowMode { get => slowMode; set { ClearTools(); slowMode = value; } }
    // Zoom tool: the same two clicks mark a zoomed stretch. It may overlap cuts and speed parts,
    // and snaps to speed-part edges as well as the playhead.
    internal bool ZoomMode { get => zoomMode; set { ClearTools(); zoomMode = value; } }
    // Text, picture/video and shape tools: the same two clicks add an item on the lowest free layer above the video.
    internal OverlayKind? OverlayMode { get => overlayMode; set { ClearTools(); overlayMode = value; overlayVersion++; Height = PreferredHeight; } }
    // Volume tool: two clicks on an audio lane make that stretch louder or quieter.
    internal bool VolumeMode { get => volumeMode; set { ClearTools(); volumeMode = value; } }
    // Sound tool: two clicks mark where a music or sound file plays.
    internal bool SoundMode { get => soundMode; set { ClearTools(); soundMode = value; } }
    private void ClearTools() { cutMode = slowMode = zoomMode = volumeMode = soundMode = false; bool wasOverlay = overlayMode != null; overlayMode = null; pendingCut = null; cutHover = null; if (wasOverlay) { overlayVersion++; Height = PreferredHeight; } InvalidateVisual(); }
    private OverlayKind? overlayMode;
    private bool cutMode, slowMode, zoomMode, volumeMode, soundMode, cutPress;
    private bool Placing => cutMode || slowMode || zoomMode || volumeMode || soundMode || overlayMode != null;
    // Parts that cover the whole picture and sound, and snap to other parts' edges.
    private bool WholeClipTool => slowMode || zoomMode || soundMode || overlayMode != null;
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
    internal IReadOnlyList<SpeedRegion> SlowRegions { get => slowRegions; set { slowRegions = value; slowVersion++; Remap(); InvalidateVisual(); } }
    private int slowVersion;
    internal int SelectedSlow { get => selectedSlow; set { selectedSlow = value; slowVersion++; InvalidateVisual(); } }
    private int selectedSlow = -1;
    // Clicking a region's speed tag asks to change it; right-click in slow mode removes a region.
    internal event Action<double, double>? SlowAdded;
    internal event Action<int>? SlowTagClicked, SlowRemoved;
    private readonly List<Rect> slowTags = new();
    // Freeze frames: a slit on the video track with a tag showing how long it holds. Click the tag (or the
    // slit) for its settings; drag the slit to move it.
    private IReadOnlyList<FreezeFrame> freezes = Array.Empty<FreezeFrame>();
    internal IReadOnlyList<FreezeFrame> Freezes { get => freezes; set { freezes = value; slowVersion++; Remap(); InvalidateVisual(); } }
    internal event Action<int>? FreezeTagClicked;
    internal event Action? FreezeEditStarted, FreezeEditFinished;
    internal event Action<int, double>? FreezeMoved;
    // Finished view: the end of a freeze's hold was dragged to a new length.
    internal event Action<int, double>? FreezeHoldChanged;
    private readonly List<Rect> freezeTags = new();
    private int dragFreeze = -1; private bool freezeDragMoved;
    private (int Lane, double Time)? pendingCut, cutHover;
    private Point pressPoint;
    private const double PlayheadLock = 3;
    private IReadOnlyList<CutRegion> cuts = Array.Empty<CutRegion>();
    internal IReadOnlyList<CutRegion> Cuts { get => cuts; set { cuts = value; cutsVersion++; InvalidateVisual(); } }
    private int cutsVersion;
    // Volume parts sit on the audio lanes; sounds (music, effects) on their own rows under the lanes.
    private IReadOnlyList<VolumeRegion> volumeRegions = Array.Empty<VolumeRegion>();
    internal IReadOnlyList<VolumeRegion> VolumeRegions { get => volumeRegions; set { volumeRegions = value; cutsVersion++; InvalidateVisual(); } }
    private IReadOnlyList<SoundItem> sounds = Array.Empty<SoundItem>();
    internal IReadOnlyList<SoundItem> Sounds { get => sounds; set { sounds = value; lanesVersion++; Height = PreferredHeight; InvalidateVisual(); } }
    internal event Action<int, double, double>? VolumeAdded;
    internal event Action<double, double>? SoundAdded;
    internal event Action<VolumeRegion>? VolumeTagClicked, VolumeRemoved;
    internal event Action<SoundItem>? SoundPicked, SoundRemoved;
    // Dragging a sound: started (for undo), each change, and done.
    internal event Action? SoundEditStarted, SoundEditFinished;
    internal event Action<SoundItem, SoundItem>? SoundMoved;
    private readonly List<(Rect Tag, VolumeRegion Part)> volumeTags = new();
    private const double SoundHeight = 18;
    private int SoundRows => sounds.Count == 0 ? 0 : sounds.Max(s => s.Row) + 1;
    private double SoundTop(int row) => LanesTop + lanes.Count * (LaneHeight + LaneGap) + row * (SoundHeight + LaneGap);
    // Where a sound plays in view time: from its anchor (plus any hold offset) for its real length.
    private (double From, double To) SoundView(SoundItem s) { if (!Mapped) return (s.Start, s.End); double at = sequence!.ToOutput(s.Start) + s.Hold; return (at, at + s.Length); }
    private Rect SoundRect(SoundItem s) { var (a, b) = SoundView(s); double x = VX(a); return new Rect(x, SoundTop(s.Row), Math.Max(3, VX(b) - x), SoundHeight); }
    private bool SoundShows(SoundItem s) { var (a, b) = SoundView(s); return b >= ViewStart && a <= ViewStart + Span; }
    private SoundItem? SoundAt(Point p) => lanesExpanded ? sounds.LastOrDefault(s => SoundShows(s) && Rect.Inflate(SoundRect(s), 2, 1).Contains(p)) : null;
    internal bool HasPendingCut => pendingCut != null;
    // A tool is out (or a part half placed), so the fullscreen dock stays up.
    internal bool IsPlacing => Placing || pendingCut != null;
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
    internal int SelectedOverlay { get => selectedOverlay; set { selectedOverlay = value; overlayVersion++; if (value >= 0) OverlaysOpen = true; InvalidateVisual(); } }
    private int selectedOverlay = -1;
    internal event Action<double, double, OverlayKind>? OverlayAdded;
    internal event Action<int>? OverlayPicked, OverlayRemoved;
    // Dragging an item: started (for undo), each change, and done.
    internal event Action? OverlayEditStarted, OverlayEditFinished;
    internal event Action<int, OverlayItem>? OverlayMoved;
    private const double RowHeight = 14, RowGap = 2, FoldHeight = 10;
    private int LayerRows => overlays.Count == 0 ? 0 : overlays.Max(o => o.Layer) + 1 + (extraRow ? 1 : 0);
    private bool extraRow;
    // With more than one layer, the rows fold into one slim strip of ticks until they're being edited:
    // the chevron, a tick, selecting an item or an overlay tool opens them; clicking the video track
    // folds them again.
    private bool overlaysOpen;
    internal bool OverlaysOpen
    {
        get => overlaysOpen;
        set { if (overlaysOpen == value) return; overlaysOpen = value; overlayVersion++; Height = PreferredHeight; InvalidateVisual(); }
    }
    internal bool OverlaysFolded => Folded;
    private bool Folded => !overlaysOpen && overlayMode == null && LayerRows > 1;
    private int Rows => Folded ? 1 : LayerRows;
    private double RowsSpan => Rows == 0 ? 0 : Folded ? FoldHeight + RowGap + 1 : Rows * (RowHeight + RowGap) + 1;
    private double TrackTop => 8 + RowsSpan;
    private double RowTop(int layer) => Folded ? 8 : 8 + (Rows - 1 - layer) * (RowHeight + RowGap);
    // An item's bar on its row: one per place it plays in the finished view.
    private IEnumerable<Rect> OverlayRects(OverlayItem o) => XSpans(o.From(), o.To()).Select(s => new Rect(s.X0, RowTop(o.Layer), Math.Max(3, s.X1 - s.X0), Folded ? FoldHeight : RowHeight));
    private Rect OverlayRect(OverlayItem o) => OverlayRects(o).DefaultIfEmpty(new Rect(XAt(o.Start), RowTop(o.Layer), 3, Folded ? FoldHeight : RowHeight)).First();
    private Rect FoldToggleArea => new(0, 6, Inset - 5, Math.Max(12, RowsSpan));
    private int OverlayAt(Point p)
    {
        // Front-most first, so the item on top wins when rows are tight.
        for (int i = overlays.Count - 1; i >= 0; i--)
            if (OverlayRects(overlays[i]).Any(r => Rect.Inflate(r, 2, 1).Contains(p))) return i;
        return -1;
    }
    private int RowAt(double y) => Rows == 0 ? -1 : (int)Math.Clamp(Math.Floor((8 + Rows * (RowHeight + RowGap) - y) / (RowHeight + RowGap)), -1, Rows);
    internal event Action<CutRegion>? CutAdded, CutRemoved;
    // A second click finished placing a part (cut, speed, zoom, text, picture, shape, volume or sound).
    internal event Action? PartPlaced;
    // The part last clicked (a cut, speed part, zoom or text/picture item), outlined in white;
    // Delete removes it. Clicking a part's body selects it and still moves the playhead.
    private object? focusedPart;
    internal object? FocusedPart { get => focusedPart; set { focusedPart = value; cutsVersion++; slowVersion++; zoomVersion++; overlayVersion++; InvalidateVisual(); } }
    internal event Action<object?>? PartClicked;
    internal int StackedAtLastClick { get; private set; }
    // The selected cut-out, speed part or zoom can be lengthened or shortened by dragging its ends.
    internal event Action<object>? PartResizeStarted;
    internal event Action<object, double, double>? PartResized;
    // The same, for a zoom whose ends can sit partway into holds (finished view).
    internal event Action<object, Moment, Moment>? PartResizedAt;
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
        ZoomRegion z => CurrentZoom(z) is { } r ? (r.Start, r.End, Band(-1)) : null,
        VolumeRegion v when volumeRegions.Contains(v) && lanesExpanded && v.Lane < lanes.Count => (v.Start, v.End, Band(v.Lane)),
        SoundItem s when lanesExpanded && sounds.FirstOrDefault(x => x.Start == s.Start && x.End == s.End && x.Row == s.Row) is { } now => (now.Start, now.End, SoundRect(now) with { X = Inset, Width = ActualWidth - 2 * Inset }),
        _ => null
    };
    // The zoom as it is in the list now (matched by where it starts and ends, hold time included).
    private ZoomRegion? CurrentZoom(ZoomRegion z) => zoomRegions.FirstOrDefault(r => SameSpan(r, z));
    internal static bool SameSpan(ZoomRegion a, ZoomRegion b) => Math.Abs(a.Start - b.Start) < 1e-9 && Math.Abs(a.End - b.End) < 1e-9 && Math.Abs(a.StartHold - b.StartHold) < 1e-9 && Math.Abs(a.EndHold - b.EndHold) < 1e-9;
    // Which end of the selected part is under the pointer: true for the start, false for the end.
    private bool? FocusedEdgeAt(Point p)
    {
        if (FocusedSpan() is not { } span || p.Y < span.Band.Top - 4 || p.Y > span.Band.Bottom + 4 || FocusedEdges() is not { } edges) return null;
        double a = VX(edges.A), b = VX(edges.B);
        bool nearA = Math.Abs(p.X - a) <= 6 && InView(edges.A), nearB = Math.Abs(p.X - b) <= 6 && InView(edges.B);
        if (nearA && nearB) return Math.Abs(p.X - a) <= Math.Abs(p.X - b);
        return nearA ? true : nearB ? false : null;
    }
    // Where the selected part's ends are, in view time (a sound's from where it plays).
    private (double A, double B)? FocusedEdges()
    {
        if (FocusedSpan() is not { } span) return null;
        if (Mapped && focusedPart is SoundItem s && sounds.FirstOrDefault(x => x.Start == s.Start && x.End == s.End && x.Row == s.Row) is { } now) return SoundView(now);
        // A zoom's ends can sit partway into a hold.
        if (Mapped && focusedPart is ZoomRegion z && CurrentZoom(z) is { } zoom) return (sequence!.ToOutput(zoom.From(), false), sequence.ToOutput(zoom.To(), true));
        return (EdgeView(span.Start, false), EdgeView(span.End, true));
    }
    // Where an edge of a part of the recording falls in view time: an end edge stays with the footage before it.
    private double EdgeView(double t, bool end) => !Mapped ? t : end ? sequence!.EndToOutput(t) : sequence!.ToOutput(t);
    private double EdgeX(double t, bool end) => VX(EdgeView(t, end));
    internal double EndView(double t) => EdgeView(t, true);
    // The recording's moment for a view time, kept inside the section the anchor (a part's other end) is in.
    internal double SourceNear(double view, double anchor)
    {
        if (!Mapped) return view;
        int section = sequence!.SectionOf(anchor);
        if (section < 0) return FromView(view).Source;
        var (from, to) = sequence.SectionSpan(section);
        return sequence.SourceIn(section, Math.Clamp(view, from, to));
    }
    private bool InView(double v) => v >= ViewStart - 1e-9 && v <= ViewStart + Span + 1e-9;
    // In the finished view a part can't reach across a jump between sections: a time outside the section
    // its anchor is in becomes the nearest moment of that section under the pointer.
    private double SectionTime(double t, double x, double anchor)
    {
        if (!Mapped) return t;
        int section = sequence!.SectionOf(anchor);
        if (section < 0) return t;
        var range = sequence.Range(section);
        if (t >= range.Start - 1e-9 && t <= range.End + 1e-9) return t;
        var (from, to) = sequence.SectionSpan(section);
        return sequence.SourceIn(section, Math.Clamp(VT(x), from, to));
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
        ZoomRegion z => part is ZoomRegion p && SameSpan(p, z),
        OverlayItem o => part is OverlayItem p && ReferenceEquals(p, o),
        VolumeRegion v => part is VolumeRegion p && p == v,
        SoundItem s => part is SoundItem p && p.Start == s.Start && p.End == s.End && p.Row == s.Row,
        FreezeFrame f => part is FreezeFrame p && Math.Abs(p.At - f.At) < 1e-9,
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
            // (Zooms by where they're drawn, so one inside a hold is found in either view.)
            if (zoomRegions.FirstOrDefault(r => XSpans(r.From(), r.To()).Any(s => p.X >= s.X0 && p.X < s.X1)) is { } z) under.Add(z);
            if (slowRegions.FirstOrDefault(r => t >= r.Start && t < r.End) is { } s) under.Add(s);
            if (cuts.FirstOrDefault(c => c.Lane < 0 && t >= c.Start && t < c.End) is { } c) under.Add(c);
        }
        else if (band >= 0)
        {
            if (cuts.FirstOrDefault(c => c.Lane == band && t >= c.Start && t < c.End) is { } laneCut) under.Add(laneCut);
            if (volumeRegions.FirstOrDefault(v => v.Lane == band && t >= v.Start && t < v.End) is { } volume) under.Add(volume);
        }
        StackedAtLastClick = under.Count;
        if (under.Count == 0) return null;
        int current = under.FindIndex(IsFocused);
        return under[(current + 1) % under.Count];
    }
    private enum Drag { None, Start, End, Playhead, Pan, OverlayMove, OverlayStart, OverlayEnd, PartEdge, SoundMove, SoundStart, SoundEnd, FreezeMove, FreezeEnd }
    private SoundItem? soundOriginal, soundCurrent; private bool soundDragMoved;
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
            lanesExpanded = value; LanesExpandedChanged?.Invoke();
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
    internal event Action? LanesToggleRequested, LanesExpandedChanged;
    private double LaneTop(int i) => LanesTop + i * (LaneHeight + LaneGap);
    private double LanesSpan => lanes.Count * (LaneHeight + LaneGap) + SoundRows * (SoundHeight + LaneGap);
    // The fold-away audio area holds the recorded lanes and any added sounds.
    private bool HasAudioArea => lanes.Count > 0 || sounds.Count > 0;
    private double ScrollTop => LanesTop + LanesSpan * LaneReveal + 2;
    internal double PreferredHeight => Math.Max(56, ScrollTop + ScrollHeight + 4);
    private static readonly Brush Track = Brush("#252D36"), Kept = Brush("#456D5D"), Accent = Brush("#9CE2C1"), Ink = Brush("#EDF0F3"), Muted = Brush("#9DA6B1");
    private static readonly Brush EndAccent = Brush("#F3BF84"), Tick = Brush("#4A5561"), LaneFill = Brush("#1A2027"), Wave = Brush("#7FA8C9"), WaveMuted = Brush("#3A444F");
    private static Brush Brush(string color) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!; b.Freeze(); return b; }
    private static readonly Pen TickPen = new(Tick, 1), MajorPen = new(Muted, 1), InkPen = new(Ink, 2);
    static TrimTimeline() { TickPen.Freeze(); MajorPen.Freeze(); InkPen.Freeze(); }
    internal bool IsDragging => drag != Drag.None && drag != Drag.Pan;
    // View time and x. TimeAt and XAt take and give the recording's time, whichever view is on.
    private double VT(double x) => Math.Clamp(ViewStart + (x-Inset) / Math.Max(1, ActualWidth-2*Inset) * Span, ViewStart, Math.Min(Total,ViewStart+Span));
    private double VX(double v) => Inset + Math.Clamp((v-ViewStart) / Math.Max(.001, Span), 0, 1) * Math.Max(1, ActualWidth-2*Inset);
    internal double TimeAt(double x) => Mapped ? FromView(VT(x)).Source : VT(x);
    internal double XAt(double t) => VX(ToView(t));
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
        if (finished)
        {
            // Sections and the range are plain fields; notice when they've changed and lay out again first.
            var shape = (SectionsKey(), Start, End, Duration);
            if (!shape.Equals(builtShape)) { builtShape = shape; remap = true; }
            _ = Map;
        }
        var key =(ViewStart, Span, ActualWidth, ActualHeight, Start, End, Duration, FrameRate, lanesVersion, SectionsKey(), SelectedSection, cutsVersion, toggleHover, slowVersion, zoomVersion, (overlayVersion, extraRow, finished, mapVersion, viewHover));
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
                if ((pendingHold > 0 || hoverHold > 0) && Mapped)
                {
                    // Partway into a hold: the stretch it will cover in the finished video.
                    Moment first = new(from.Time, pendingHold), second = new(to, cutHover != null ? hoverHold : pendingHold);
                    var (a, b) = HoldTiming.Compare(first, second) <= 0 ? (first, second) : (second, first);
                    if (HoldTiming.Compare(a, b) < 0) DrawCut(live, from.Lane, 0, 0, 0, slowMode, zoomMode, preview: true, spans: XSpans(a, b).ToList());
                }
                else if (Math.Abs(to - from.Time) > 1e-6) DrawCut(live, from.Lane, Math.Min(from.Time, to), Math.Max(from.Time, to), 0, slowMode, zoomMode, preview: true);
                DrawCutter(live, from.Lane, from.Time, pendingHold);
            }
            if (cutHover is { } hover) DrawCutter(live, pendingCut?.Lane ?? hover.Lane, hover.Time, hoverHold);
        }
        double playheadAt = PositionView, playhead = VX(playheadAt);
        if (playheadAt >= ViewStart - 1e-9 && playheadAt <= ViewStart + Span + 1e-9)
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
        // The finished view only shows what's kept.
        if (Mapped) return;
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
        // Kept blocks: where each section sits in the recording, or in the finished view where it plays
        // (in order, holds and speed included; with no sections the whole finished video is kept).
        var blocks = new List<(int Index, double X0, double X1)>();
        if (Mapped)
        {
            if (Sections.Count == 0) blocks.Add((-1, VX(0), VX(Total)));
            else for (int i = 0; i < Sections.Count; i++) { var (a, b) = sequence!.SectionSpan(i); if (b >= ViewStart && a <= ViewStart + Span) blocks.Add((i, VX(a), VX(b))); }
        }
        else for (int i = 0; i < Sections.Count; i++) { var s = Sections[i]; if (Shows(s.Start, s.End)) blocks.Add((i, XAt(s.Start), XAt(s.End))); }
        foreach (var (i, x0, x1) in blocks)
        {
            var block = new Rect(x0, TrackTop+3, Math.Max(1, x1-x0), TrackHeight-6);
            dc.DrawRoundedRectangle(Kept, i >= 0 && i == SelectedSection ? new Pen(Ink, 1.5) : null, block, 3, 3);
            if (i < 0) continue;
            // Number each kept block (bottom-left, clear of the ruler) so it matches its chip below the timeline.
            var number = Text((i + 1).ToString(CultureInfo.InvariantCulture), 11, Ink, dpi);
            if (block.Width >= number.Width + 8) dc.DrawText(number, new Point(block.X + 5, block.Bottom - number.Height - 1));
        }
        DrawRuler(dc, width, dpi);
        DimOutsideKept(dc, new Rect(Inset, TrackTop, width, TrackHeight), 6);
        // The marked range and its handles belong to the recording view.
        if (!Mapped) dc.DrawRoundedRectangle(null, new Pen(Accent, 1.5), new Rect(XAt(Start), TrackTop-2, Math.Max(1, XAt(End)-XAt(Start)), TrackHeight+4), 3, 3);
        foreach (var edge in new[] { (Time: Start, Color: Accent), (Time: End, Color: EndAccent) })
        {
            if (Mapped || !Shows(edge.Time, edge.Time)) continue;
            double x = XAt(edge.Time);
            // A slim bar; the grab area stays wide (see BeginDrag).
            dc.DrawRoundedRectangle(edge.Color, null, new Rect(x-3, TrackTop-3, 6, TrackHeight+6), 3, 3);
        }
        DrawViewToggle(dc, dpi);
        DrawLaneToggle(dc, dpi);
        cutTags.Clear();
        foreach (var cut in cuts) if (cut.Lane < 0) DrawCut(dc, cut.Lane, cut.Start, cut.End, dpi, focused: IsFocused(cut), cut: cut);
        DrawOverlayWash(dc);
        DrawSlowRegions(dc, dpi);
        DrawFreezes(dc, dpi);
        DrawZoomRegions(dc, dpi);
        DrawOverlayRows(dc, dpi);
        DrawFocusGrips(dc);
        if (LaneReveal > 0 && HasAudioArea)
        {
            // Lanes slide out from under the video track and fade in as they open.
            dc.PushClip(new RectangleGeometry(new Rect(0, LanesTop, ActualWidth, LanesSpan * LaneReveal)));
            dc.PushOpacity(LaneReveal);
            for (int i = 0; i < lanes.Count; i++) { DrawLane(dc, lanes[i], LaneTop(i), width, dpi); DimOutsideKept(dc, new Rect(Inset, LaneTop(i), width, LaneHeight), 4); }
            foreach (var cut in cuts) if (cut.Lane >= 0) DrawCut(dc, cut.Lane, cut.Start, cut.End, dpi, focused: IsFocused(cut), cut: cut);
            DrawVolumes(dc, dpi);
            DrawSounds(dc, width, dpi);
            dc.Pop(); dc.Pop();
        }
        if (ZoomFactor > 1.001)
        {
            // A slim scrollbar shows where the zoomed view sits in the whole clip.
            dc.DrawRoundedRectangle(Track, null, new Rect(Inset, ScrollTop, width, ScrollHeight), 3, 3);
            double left = Inset + ViewStart / Total * width, thumb = Math.Max(12, Span / Total * width);
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
        var handles = Mapped ? Array.Empty<double>() : new[] { Start, End }.Where(t => t >= ViewStart && t <= ViewStart + Span).Select(XAt).ToArray();
        bool Blocked(double left, double w) => handles.Any(h => left < h + 8 && left + w > h - 8);
        double top = TrackTop + 1;
        // Integer tick counts avoid drift when stepping by a frame (1/60 s).
        for (long k = (long)Math.Ceiling(ViewStart / minor - 1e-9), last = (long)Math.Floor((ViewStart + Span) / minor + 1e-9); k <= last; k++)
        {
            // Ticks count view time: the recording's, or the finished video's.
            double t = k * minor, x = VX(t);
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
        string whole = Total >= 3600 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
        return decimals == 0 ? whole : whole + (time.TotalSeconds % 1).ToString("F" + decimals, CultureInfo.InvariantCulture)[1..];
    }
    // Labels are laid out once and reused: a timeline with a hundred layers redraws its labels on every
    // zoom and scroll, and laying out text is the slow part. Width limits are reset on each reuse.
    private FormattedText Text(string text, double size, Brush brush, double dpi)
    {
        var key = (text, size, brush, dpi);
        if (texts.TryGetValue(key, out var cached)) { cached.MaxTextWidth = 0; cached.MaxLineCount = int.MaxValue; cached.Trimming = TextTrimming.None; return cached; }
        if (texts.Count > 3000) texts.Clear();
        return texts[key] = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, UiFace, size, brush, dpi);
    }
    private static readonly Typeface UiFace = new("Segoe UI");
    private FormattedText GlyphText(string glyph, Brush brush, double dpi)
    {
        var key = (glyph, -8.0, brush, dpi);
        if (texts.TryGetValue(key, out var cached)) return cached;
        return texts[key] = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Glyph, 8, brush, dpi);
    }
    private readonly Dictionary<(string, double, Brush, double), FormattedText> texts = new();
    private readonly Dictionary<string, ((float[], double, double, double, double) Key, StreamGeometry Geometry)> waveCache = new();
    private void DrawLane(DrawingContext dc, AudioLane lane, double top, double width, double dpi)
    {
        dc.DrawRoundedRectangle(LaneFill, null, new Rect(Inset, top, width, LaneHeight), 4, 4);
        var peaks = lane.Peaks; double mid = top + LaneHeight / 2;
        // The outline only changes with the view, so edits elsewhere on the timeline reuse it.
        var waveKey = (peaks, ViewStart, Span, width, top + (Mapped ? sequence!.Version * 1e6 : 0));
        if (peaks.Length > 0 && waveCache.TryGetValue(lane.Name, out var cached) && cached.Key.Equals(waveKey)) dc.DrawGeometry(lane.Muted ? WaveMuted : Wave, null, cached.Geometry);
        else if (peaks.Length > 0)
        {
            // One filled outline (loudest peak per pixel column) rather than a stroke per column:
            // a single fill is far cheaper for WPF to rasterize than thousands of thin lines.
            int columns = Math.Max(1, (int)width);
            var upper = new Point[columns]; var lower = new Point[columns];
            for (int px = 0; px < columns; px++)
            {
                // In the finished view each column reads the recording where that moment of the finished
                // video comes from (speed parts squeeze or stretch it; a hold is flat).
                float peak = 0;
                if (Mapped)
                {
                    var (from, held) = sequence!.ToSource(ViewStart + px / width * Span); var (to, _) = sequence!.ToSource(ViewStart + (px + 1) / width * Span);
                    if (held <= 0) { int a = (int)(Math.Min(from, to) * 100), b = Math.Max(a + 1, (int)(Math.Max(from, to) * 100)); if (b - a > 400) b = a + 1; for (int i = Math.Max(0, a); i < Math.Min(peaks.Length, b); i++) peak = Math.Max(peak, peaks[i]); }
                }
                else
                {
                    int a = (int)((ViewStart + px / width * Span) * 100), b = Math.Max(a + 1, (int)((ViewStart + (px + 1) / width * Span) * 100));
                    for (int i = Math.Max(0, a); i < Math.Min(peaks.Length, b); i++) peak = Math.Max(peak, peaks[i]);
                }
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
    // The gutter beside the video track holds two small toggles: the view (top) and the audio (bottom).
    private Rect ToggleArea => HasAudioArea ? new(0, TrackTop + TrackHeight / 2, Inset - 7, TrackHeight / 2) : Rect.Empty;
    private Rect ViewToggleArea => new(0, TrackTop, Inset - 7, HasAudioArea ? TrackHeight / 2 : TrackHeight);
    private bool viewHover;
    private void DrawViewToggle(DrawingContext dc, double dpi)
    {
        var area = ViewToggleArea;
        if (viewHover) dc.DrawRoundedRectangle(Track, null, area, 3, 3);
        var icon = new FormattedText("\uE8AB", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Glyphs, 9, finished ? Accent : viewHover ? Ink : Muted, dpi);
        dc.DrawText(icon, new Point(area.X + (area.Width - icon.Width) / 2, area.Y + (area.Height - icon.Height) / 2));
    }
    private static readonly Typeface Glyphs = new("Segoe MDL2 Assets");
    // Just a chevron that turns down as the lanes open; a small red dot marks muted audio while folded.
    private void DrawLaneToggle(DrawingContext dc, double dpi)
    {
        if (!HasAudioArea) return;
        var strong = lanesExpanded || toggleHover ? Ink : Muted;
        var chevron = new FormattedText("", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Glyphs, 9, strong, dpi);
        var center = new Point((Inset - 7) / 2, ToggleArea.Y + ToggleArea.Height / 2);
        if (toggleHover) dc.DrawRoundedRectangle(Track, null, ToggleArea, 3, 3);
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
    // spans: where to draw it instead of start..end (a part being placed partway into a freeze's hold).
    private void DrawCut(DrawingContext dc, int lane, double start, double end, double dpi, bool slow = false, bool zoom = false, bool focused = false, bool preview = false, CutRegion? cut = null, IEnumerable<(double X0, double X1)>? spans = null)
    {
        if (lane >= lanes.Count || (lane >= 0 && LaneReveal <= 0) || (spans == null && !Shows(start, end))) return;
        var band = Band(lane); bool tagged = false;
        foreach (var (x0, x1) in spans ?? XSpans(start, end))
        {
        double x = x0, w = Math.Max(2, x1 - x0);
        var area = new Rect(x, band.Top, w, band.Height);
        if (preview && overlayMode != null && lane < 0) { dc.DrawRoundedRectangle(OverlayWash, new Pen(OverlayFill, 1), area, 3, 3); continue; }
        if (preview && (volumeMode || soundMode)) { dc.DrawRoundedRectangle(SoundFill, new Pen(SoundEdge, 1), area, 3, 3); continue; }
        dc.DrawRoundedRectangle(zoom ? ZoomFill : slow ? SlowFill : CutFill, new Pen(focused ? Ink : zoom ? ZoomEdge : slow ? SlowEdge : CutEdge, focused ? 1.5 : 1), area, 3, 3);
        if (cut != null && w >= 20 && !tagged)
        {
            tagged = true;
            var icon = new FormattedText("", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Glyph, 8, Ink, dpi);
            var tag = new Rect(x + 2, band.Top + 2, icon.Width + 8, 13);
            dc.DrawRoundedRectangle(CutEdge, null, tag, 3, 3);
            dc.DrawText(icon, new Point(tag.X + 4, tag.Y + (tag.Height - icon.Height) / 2));
            cutTags.Add((tag, cut));
        }
        }
    }
    // Volume parts: green on their audio lane, with a tag showing the level (click it to change it).
    private static readonly Brush SoundFill = Brush("#5A4ADE80"), SoundEdge = Brush("#4ADE80"), SoundInk = Brush("#0A1F12"), SoundRow = Brush("#161B21");
    private void DrawVolumes(DrawingContext dc, double dpi)
    {
        volumeTags.Clear();
        foreach (var v in volumeRegions)
        {
            if (v.Lane >= lanes.Count || !Shows(v.Start, v.End)) continue;
            var band = Band(v.Lane); double x = 0, w = 0;
            bool focused = IsFocused(v);
            foreach (var (x0, x1) in XSpans(v.Start, v.End))
            {
                x = x0; w = Math.Max(2, x1 - x0);
                dc.DrawRoundedRectangle(SoundFill, new Pen(focused ? Ink : SoundEdge, focused ? 1.5 : 1) { DashStyle = focused ? null : DashStyles.Dash }, new Rect(x, band.Top, w, band.Height), 3, 3);
            }
            var label = Text($"{v.Gain * 100:0}%", 10, SoundInk, dpi);
            var tag = new Rect(Math.Max(x + 1, x + w - label.Width - 11), band.Bottom - 15, label.Width + 8, 13);
            dc.DrawRoundedRectangle(SoundEdge, null, tag, 3, 3);
            dc.DrawText(label, new Point(tag.X + 4, tag.Y + (tag.Height - label.Height) / 2));
            volumeTags.Add((tag, v));
        }
    }
    // Sounds: green bars on their own rows under the lanes, with fade-in and fade-out wedges.
    private void DrawSounds(DrawingContext dc, double width, double dpi)
    {
        for (int r = 0; r < SoundRows; r++) dc.DrawRoundedRectangle(SoundRow, null, new Rect(Inset, SoundTop(r), width, SoundHeight), 4, 4);
        foreach (var s in sounds)
        {
            if (!SoundShows(s)) continue;
            var rect = SoundRect(s); bool focused = IsFocused(s);
            dc.DrawRoundedRectangle(SoundFill, new Pen(focused ? Ink : SoundEdge, focused ? 1.5 : 1), rect, 4, 4);
            double pps = rect.Width / Math.Max(1e-6, s.Length);
            if (s.FadeIn > 0) dc.DrawGeometry(SoundEdge, null, Wedge(new Point(rect.Left, rect.Bottom), new Point(rect.Left + Math.Min(rect.Width, s.FadeIn * pps), rect.Top), new Point(rect.Left, rect.Top)));
            if (s.FadeOut > 0) dc.DrawGeometry(SoundEdge, null, Wedge(new Point(rect.Right, rect.Bottom), new Point(rect.Right - Math.Min(rect.Width, s.FadeOut * pps), rect.Top), new Point(rect.Right, rect.Top)));
            var label = Text("♪ " + s.Label + (s.Duck ? " · ducks" : ""), 10, Ink, dpi);
            label.MaxTextWidth = Math.Max(1, rect.Width - 8); label.MaxLineCount = 1; label.Trimming = TextTrimming.CharacterEllipsis;
            if (rect.Width > 24) dc.DrawText(label, new Point(rect.X + 5, rect.Y + (rect.Height - label.Height) / 2));
        }
        static Geometry Wedge(Point a, Point b, Point c) { var g = new StreamGeometry(); using (var x = g.Open()) { x.BeginFigure(a, true, true); x.LineTo(b, false, false); x.LineTo(c, false, false); } g.Freeze(); return g; }
    }
    // Small handles on the ends of the selected part show they can be dragged.
    private void DrawFocusGrips(DrawingContext dc)
    {
        if (FocusedSpan() is not { } span || FocusedEdges() is not { } edges) return;
        foreach (double v in new[] { edges.A, edges.B })
            if (InView(v))
                dc.DrawRoundedRectangle(Ink, null, new Rect(VX(v) - 2, span.Band.Top + 4, 4, Math.Max(6, span.Band.Height - 8)), 2, 2);
    }
    // Zoom regions: teal boxes with a magnifier tag (top-right) that selects the region for editing.
    private void DrawZoomRegions(DrawingContext dc, double dpi)
    {
        zoomTags.Clear();
        for (int i = 0; i < zoomRegions.Count; i++)
        {
            var r = zoomRegions[i];
            if (!Shows(r.From(), r.To())) { zoomTags.Add(Rect.Empty); continue; }
            double x = 0, w = 0;
            foreach (var (x0, x1) in XSpans(r.From(), r.To()))
            {
                x = x0; w = Math.Max(2, x1 - x0);
                dc.DrawRoundedRectangle(ZoomFill, new Pen(i == selectedZoom || IsFocused(r) ? Ink : ZoomEdge, i == selectedZoom || IsFocused(r) ? 1.5 : 1), new Rect(x, TrackTop, w, TrackHeight), 3, 3);
            }
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
    private static readonly Pen SoundCutterPen = new(Brush("#4ADE80"), 1.5);
    private static readonly Brush MediaFill = Brush("#60A5FA"), MediaDim = Brush("#3B72B8"), ShapeFillBrush = Brush("#F472B6"), ShapeDim = Brush("#B04C86");
    private bool InSoundRows(Point p) => lanesExpanded && SoundRows > 0 && p.Y >= SoundTop(0) - 2 && p.Y <= SoundTop(SoundRows) + 2;
    private void DrawOverlayWash(DrawingContext dc)
    {
        // Merge the spans first so overlapping items don't darken the wash.
        double end = double.MinValue, start = 0;
        foreach (var o in overlays.Where(o => !o.InHolds()).OrderBy(o => o.Start))
        {
            if (o.Start > end) { if (end > start) Wash(XSpans(start, end)); start = o.Start; end = o.End; }
            else end = Math.Max(end, o.End);
        }
        if (end > start) Wash(XSpans(start, end));
        // Items that start or stop partway through a hold wash just that much of it.
        foreach (var o in overlays.Where(o => o.InHolds())) Wash(XSpans(o.From(), o.To()));
        void Wash(IEnumerable<(double X0, double X1)> spans)
        {
            foreach (var (x0, x1) in spans) dc.DrawRoundedRectangle(OverlayWash, null, new Rect(x0, TrackTop, Math.Max(2, x1 - x0), TrackHeight), 3, 3);
        }
    }
    private void DrawOverlayRows(DrawingContext dc, double dpi)
    {
        if (Rows == 0) return;
        double width = ActualWidth - 2 * Inset;
        if (LayerRows > 1)
        {
            // The chevron beside the layers folds or opens them.
            var chevron = new FormattedText(Folded ? "" : "", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Glyph, 8, Muted, dpi);
            dc.DrawText(chevron, new Point((Inset - 5 - chevron.Width) / 2, Folded ? 8 + (FoldHeight - chevron.Height) / 2 : 9));
        }
        if (Folded)
        {
            // Folded: every item is a tick on one slim strip, in its kind's colour.
            dc.DrawRoundedRectangle(RowFill, null, new Rect(Inset, 8, width, FoldHeight), 3, 3);
            dc.PushClip(new RectangleGeometry(new Rect(Inset, 0, width, TrackTop)));
            foreach (var o in OverlayOrder.BackToFront(overlays))
            {
                var (fill, dim) = o.Kind switch { OverlayKind.Image or OverlayKind.Video => (MediaFill, MediaDim), OverlayKind.Shape => (ShapeFillBrush, ShapeDim), _ => (OverlayFill, OverlayDim) };
                foreach (var rect in OverlayRects(o))
                {
                    dc.DrawRoundedRectangle(dim, null, new Rect(rect.X, rect.Y + 3, rect.Width, rect.Height - 6), 2, 2);
                    dc.DrawRoundedRectangle(fill, null, new Rect(rect.X, rect.Y + 1, Math.Min(3, rect.Width), rect.Height - 2), 1, 1);
                }
            }
            dc.Pop();
            return;
        }
        for (int r = 0; r < Rows; r++) dc.DrawRoundedRectangle(RowFill, null, new Rect(Inset, RowTop(r), width, RowHeight), 3, 3);
        dc.PushClip(new RectangleGeometry(new Rect(Inset, 0, width, TrackTop)));
        for (int i = 0; i < overlays.Count; i++)
        {
            var o = overlays[i];
            bool selected = i == selectedOverlay || IsFocused(o);
            foreach (var rect in OverlayRects(o))
            {
            // Colour by kind: text amber, pictures and videos blue, shapes pink.
            var (fill, dim) = o.Kind switch { OverlayKind.Image or OverlayKind.Video => (MediaFill, MediaDim), OverlayKind.Shape => (ShapeFillBrush, ShapeDim), _ => (OverlayFill, OverlayDim) };
            dc.DrawRoundedRectangle(selected ? fill : dim, selected ? new Pen(Ink, 1.2) : null, rect, 3, 3);
            // Keyframes show as small diamonds.
            foreach (var key in o.Keys)
            {
                double kx = Mapped && o.InHolds() ? VX(sequence!.ToOutput(o.From(), false) + key.T) : XAt(o.Start + key.T); if (kx < rect.Left - 1 || kx > rect.Right + 1) continue;
                var diamond = new StreamGeometry();
                using (var g = diamond.Open()) { g.BeginFigure(new Point(kx, rect.Top + 2), true, true); g.PolyLineTo(new[] { new Point(kx + 4, rect.Top + RowHeight / 2), new Point(kx, rect.Bottom - 2), new Point(kx - 4, rect.Top + RowHeight / 2) }, false, false); }
                diamond.Freeze(); dc.DrawGeometry(Ink, new Pen(OverlayInk, .8), diamond);
            }
            if (rect.Width < 16) continue;
            string glyph = o.Kind switch { OverlayKind.Image => "\uE8B9", OverlayKind.Video => "\uE714", OverlayKind.Shape => "\uE739", _ => "\uE8D2" };
            var icon = GlyphText(glyph, OverlayInk, dpi);
            dc.DrawText(icon, new Point(rect.X + 4, rect.Y + (RowHeight - icon.Height) / 2));
            var label = Text(o.Label, 9, OverlayInk, dpi);
            label.MaxTextWidth = Math.Max(1, rect.Width - icon.Width - 11); label.MaxLineCount = 1; label.Trimming = TextTrimming.CharacterEllipsis;
            if (rect.Width > icon.Width + 20) dc.DrawText(label, new Point(rect.X + icon.Width + 7, rect.Y + (RowHeight - label.Height) / 2));
            }
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
            if (!Shows(r.Start, r.End)) { slowTags.Add(Rect.Empty); continue; }
            double x = 0, w = 0;
            foreach (var (x0, x1) in XSpans(r.Start, r.End))
            {
                x = x0; w = Math.Max(2, x1 - x0);
                dc.DrawRoundedRectangle(SlowFill, new Pen(i == selectedSlow || IsFocused(r) ? Ink : SlowEdge, i == selectedSlow || IsFocused(r) ? 1.5 : 1), new Rect(x, TrackTop, w, TrackHeight), 3, 3);
            }
            var label = Text(r.Speed.ToString("0.##", CultureInfo.InvariantCulture) + "×", 10, SlowInk, dpi);
            var tag = new Rect(Math.Max(x + 1, x + w - label.Width - 11), TrackTop + TrackHeight - 15, label.Width + 8, 13);
            dc.DrawRoundedRectangle(SlowEdge, null, tag, 3, 3);
            dc.DrawText(label, new Point(tag.X + 4, tag.Y + (tag.Height - label.Height) / 2));
            slowTags.Add(tag);
        }
    }
    private int SlowTagAt(Point p) { for (int i = 0; i < slowTags.Count; i++) if (slowTags[i].Contains(p)) return i; return -1; }
    private int FreezeAt(Point p) => FreezeHit(p).Index;
    // A freeze under the pointer, and whether it's the end of its hold (finished view only).
    private (int Index, bool End) FreezeHit(Point p)
    {
        bool onTrack = p.Y >= TrackTop - 3 && p.Y <= TrackTop + TrackHeight + 3;
        for (int i = freezes.Count - 1; i >= 0; i--)
        {
            if (i < freezeTags.Count && freezeTags[i].Contains(p)) return (i, false);
            if (!onTrack) continue;
            if (Mapped)
            {
                // Only freezes in what's kept are in the finished video.
                if (sequence!.Hold(freezes[i].At) is not { } hold) continue;
                if (InView(hold.To) && Math.Abs(p.X - VX(hold.To)) <= 4) return (i, true);
                if (InView(hold.From) && Math.Abs(p.X - VX(hold.From)) <= 4) return (i, false);
            }
            else if (Shows(freezes[i].At, freezes[i].At) && Math.Abs(p.X - XAt(freezes[i].At)) <= 4) return (i, false);
        }
        return (-1, false);
    }
    private static readonly Brush FreezeWash = Brush("#337DD3FC");
    private void DrawFreezes(DrawingContext dc, double dpi)
    {
        freezeTags.Clear();
        foreach (var f in freezes)
        {
            bool focused = IsFocused(f);
            if (Mapped)
            {
                // In the finished view the hold is a stretch of its own, with a slit at each end (and a
                // freeze outside what's kept isn't there at all).
                if (sequence!.Hold(f.At) is not { } hold || hold.To < ViewStart || hold.From > ViewStart + Span) { freezeTags.Add(Rect.Empty); continue; }
                double x0 = VX(hold.From), x1 = VX(hold.To);
                dc.DrawRoundedRectangle(FreezeWash, focused ? new Pen(Ink, 1.2) : null, new Rect(x0, TrackTop, Math.Max(2, x1 - x0), TrackHeight), 3, 3);
                if (InView(hold.To)) dc.DrawRoundedRectangle(FreezeInk, null, new Rect(x1 - 1.5, TrackTop - 3, 3, TrackHeight + 6), 1.5, 1.5);
                if (!InView(hold.From)) { freezeTags.Add(Rect.Empty); continue; }
            }
            else if (!Shows(f.At, f.At)) { freezeTags.Add(Rect.Empty); continue; }
            double x = XAt(f.At);
            dc.DrawRoundedRectangle(FreezeInk, focused ? new Pen(Ink, 1) : null, new Rect(x - 1.5, TrackTop - 3, 3, TrackHeight + 6), 1.5, 1.5);
            var label = Text("❄ " + f.Seconds.ToString("0.#", CultureInfo.InvariantCulture) + " s", 10, FreezeText, dpi);
            double w = label.Width + 8, left = x + 3 + w > Inset + ActualWidth - 2 * Inset ? x - 3 - w : x + 3;
            var tag = new Rect(left, TrackTop + 2, w, 13);
            dc.DrawRoundedRectangle(FreezeInk, focused ? new Pen(Ink, 1.2) : null, tag, 3, 3);
            dc.DrawText(label, new Point(tag.X + 4, tag.Y + (tag.Height - label.Height) / 2));
            freezeTags.Add(tag);
        }
    }
    private static readonly Brush FreezeInk = Brush("#7DD3FC"), FreezeText = Brush("#0B2530");
    // The cutter: red (purple for speed, teal for zoom), or white while it is locked onto the playhead.
    private static readonly Pen CutterPen = new(Brush("#E5484D"), 1.5), SlowCutterPen = new(Brush("#A99BFA"), 1.5), ZoomCutterPen = new(Brush("#5EEAD4"), 1.5), LockedCutterPen = new(Brush("#EDF0F3"), 1.5);
    private void DrawCutter(DrawingContext dc, int lane, double t, double hold = 0)
    {
        if (!Shows(t, t) || lane >= lanes.Count || (lane >= 0 && LaneReveal <= 0)) return;
        // (Partway into a freeze's hold, in the finished view.)
        var band = Band(lane); double x = hold > 0 && Mapped ? VX(sequence!.ToOutput(new Moment(t, hold), false)) : XAt(t);
        // Text and image markers reach up through the layer rows too.
        double top = overlayMode != null && lane < 0 ? 4 : band.Top - 3;
        dc.DrawLine(LockedAt(t) ? LockedCutterPen : volumeMode || soundMode ? SoundCutterPen : overlayMode != null ? OverlayCutterPen : zoomMode ? ZoomCutterPen : slowMode ? SlowCutterPen : CutterPen, new Point(x, top), new Point(x, band.Bottom + 3));
    }
    private void PlaceCutPoint(Point p)
    {
        int band = pendingCut?.Lane ?? BandAt(p);
        if (band < -1 && !(soundMode && InSoundRows(p))) return;
        // Volume parts go on an audio lane; sounds can be placed from anywhere on the timeline.
        if (volumeMode && (band < 0 || band >= lanes.Count)) return;
        if (WholeClipTool) band = -1; // speed parts, zoom, text and images cover the whole picture and sound
        double t = CutTimeAt(p.X), hold = HoldAtPointer(p.X, ref t);
        if (pendingCut is not { } from) { pendingCut = (band, t); pendingHold = hold; }
        else
        {
            pendingCut = null;
            if (hold <= 0) t = SectionTime(t, p.X, from.Time);
            // Text, pictures, shapes, videos and zooms can start or stop partway through a freeze's hold.
            if (pendingHold > 0 || hold > 0)
            {
                Moment first = new(from.Time, pendingHold), second = new(t, hold);
                var (a0, b0) = HoldTiming.Compare(first, second) <= 0 ? (first, second) : (second, first);
                if (sequence!.ToOutput(b0, true) - sequence.ToOutput(a0, false) >= 1 / Math.Max(1, FrameRate) - 1e-9)
                {
                    if (overlayMode is { } overlayKind) OverlayAddedAt?.Invoke(a0, b0, overlayKind); else ZoomAddedAt?.Invoke(a0, b0);
                    PartPlaced?.Invoke();
                    if (!Placing) { cutHover = null; Cursor = Cursors.Hand; ToolTip = null; }
                }
                InvalidateVisual(); return;
            }
            double a = Math.Min(from.Time, t), b = Math.Max(from.Time, t);
            if (b - a >= 1 / Math.Max(1, FrameRate) - 1e-9)
            {
                if (volumeMode) VolumeAdded?.Invoke(from.Lane, a, b);
                else if (soundMode) SoundAdded?.Invoke(a, b);
                else if (overlayMode is { } kind) OverlayAdded?.Invoke(a, b, kind);
                else if (zoomMode) ZoomAdded?.Invoke(a, b);
                else if (slowMode) SlowAdded?.Invoke(a, b); else CutAdded?.Invoke(new CutRegion(from.Lane, a, b));
                // One part per use: the tool puts itself away so a stray click doesn't start another.
                PartPlaced?.Invoke();
                if (!Placing) { cutHover = null; Cursor = Cursors.Hand; ToolTip = null; }
            }
        }
        InvalidateVisual();
    }
    // In the finished view, text, picture, shape, video and zoom tools can mark a point partway through a
    // freeze's hold: the seconds into it (t becomes the freeze's moment), or 0 anywhere else. It locks onto the
    // playhead when that's parked in the same hold.
    private double HoldAtPointer(double x, ref double t)
    {
        if (!Mapped || !(overlayMode != null || zoomMode)) return 0;
        var m = MomentAt(x);
        if (m.Hold <= 1e-9 || sequence!.Hold(m.At) is not { } held) return 0;
        t = m.At;
        if (holdOffset > 0 && Math.Abs(Position - m.At) < 1e-9 && Math.Abs(x - VX(PositionView)) <= PlayheadLock) return holdOffset;
        // Snapped to hundredths, and the very end of the hold is its end.
        double into = Math.Round(m.Hold * 100) / 100;
        return into >= held.To - held.From - .005 ? held.To - held.From : into;
    }
    private double pendingHold, hoverHold;
    // Parts placed partway into holds in the finished view: text, pictures, shapes and videos, and zooms.
    internal event Action<Moment, Moment, OverlayKind>? OverlayAddedAt;
    internal event Action<Moment, Moment>? ZoomAddedAt;
    private void UpdateCutHover(Point p)
    {
        int band = pendingCut?.Lane ?? BandAt(p);
        if (soundMode && band < -1 && InSoundRows(p)) band = -1;
        bool fits = band > -2 && (!volumeMode || (band >= 0 && band < lanes.Count));
        double at = CutTimeAt(p.X), hold = Placing && fits ? HoldAtPointer(p.X, ref at) : 0;
        (int, double)? next = Placing && fits ? (WholeClipTool ? -1 : band, at) : null;
        if (!Equals(next, cutHover) || Math.Abs(hold - hoverHold) > 1e-9) { cutHover = next; hoverHold = hold; InvalidateVisual(); }
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
        if (ViewToggleArea.Contains(point)) { ViewToggled?.Invoke(); e.Handled = true; return; }
        if (HasAudioArea && ToggleArea.Contains(point)) { LanesToggleRequested?.Invoke(); e.Handled = true; return; }
        // (In the finished view a sound's ends trim through its own drag, which works in real seconds.)
        if (!Placing && pendingCut == null && !(Mapped && focusedPart is SoundItem) && FocusedEdgeAt(point) is bool atStart && FocusedSpan() is { } span)
        {
            drag = Drag.PartEdge; resizing = focusedPart; resizingStart = atStart; resizeStart = span.Start; resizeEnd = span.End;
            pressPoint = point; overlayDragMoved = false; CaptureMouse(); e.Handled = true; return;
        }
        if (pendingCut == null && cutTags.FindIndex(t => t.Tag.Contains(point)) is int cutTag and >= 0) { CutTagClicked?.Invoke(cutTags[cutTag].Cut); e.Handled = true; return; }
        if (pendingCut == null && lanesExpanded && volumeTags.FindIndex(t => t.Tag.Contains(point)) is int volumeTag and >= 0) { VolumeTagClicked?.Invoke(volumeTags[volumeTag].Part); e.Handled = true; return; }
        if (pendingCut == null && SoundAt(point) is { } sound)
        {
            // Press on a sound: a click opens it, a drag moves it (or its ends) and between rows.
            var rect = SoundRect(sound);
            drag = point.X - rect.Left <= 5 && rect.Width > 14 ? Drag.SoundStart : rect.Right - point.X <= 5 && rect.Width > 14 ? Drag.SoundEnd : Drag.SoundMove;
            soundOriginal = soundCurrent = sound; soundDragMoved = false; pressPoint = point;
            CaptureMouse(); e.Handled = true; return;
        }
        if (pendingCut == null && LayerRows > 1 && FoldToggleArea.Contains(point)) { OverlaysOpen = Folded; e.Handled = true; return; }
        if (pendingCut == null && Folded && point.Y < TrackTop)
        {
            // A tick opens the layers and that item.
            OverlaysOpen = true;
            if (OverlayAt(point) is int tick and >= 0) OverlayPicked?.Invoke(tick);
            e.Handled = true; return;
        }
        if (pendingCut == null && OverlayAt(point) is int item and >= 0)
        {
            // Press on an item: a click opens it, a drag moves it (or its edges) and changes its layer.
            var rect = OverlayRect(overlays[item]);
            drag = point.X - rect.Left <= 5 && rect.Width > 14 ? Drag.OverlayStart : rect.Right - point.X <= 5 && rect.Width > 14 ? Drag.OverlayEnd : Drag.OverlayMove;
            dragOverlay = item; dragOriginal = overlays[item]; dragStartList = overlays.ToArray(); overlayDragMoved = false; pressPoint = point;
            CaptureMouse(); e.Handled = true; return;
        }
        if (pendingCut == null && ZoomTagAt(point) is int zoomTag and >= 0) { ZoomTagClicked?.Invoke(zoomTag); e.Handled = true; return; }
        if (pendingCut == null && !Placing && FreezeHit(point) is { Index: >= 0 } freezeHit) { drag = freezeHit.End ? Drag.FreezeEnd : Drag.FreezeMove; dragFreeze = freezeHit.Index; freezeDragMoved = false; pressPoint = point; CaptureMouse(); e.Handled = true; return; }
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
        // Back on the video track, open layers fold away again.
        if (overlaysOpen && !Placing && point.Y >= TrackTop && point.Y <= TrackTop + TrackHeight) OverlaysOpen = false;
        PartClicked?.Invoke(PartAt(point));
        BeginDrag(point); CaptureMouse(); DragStarted?.Invoke(); MoveTo(point.X); e.Handled=true;
    }
    private void PanToPointer(double x) => PanTo((x - Inset) / Math.Max(1, ActualWidth - 2 * Inset) * Total - Span / 2);
    internal void BeginDrag(Point point)
    {
        double left = Mapped || Start<ViewStart || Start>ViewStart+Span ? double.MaxValue : Math.Abs(point.X-XAt(Start)), right = Mapped || End<ViewStart || End>ViewStart+Span ? double.MaxValue : Math.Abs(point.X-XAt(End));
        bool handles = point.Y >= TrackTop-7 && point.Y <= TrackTop+TrackHeight+7;
        drag = handles && Math.Min(left,right)<=14 ? (left<=right ? Drag.Start : Drag.End) : Drag.Playhead;
        grabOffset = drag == Drag.Start ? point.X-XAt(Start) : drag == Drag.End ? point.X-XAt(End) : 0;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p=e.GetPosition(this);
        if (IsMouseCaptured && drag == Drag.FreezeEnd && dragFreeze >= 0 && dragFreeze < freezes.Count && sequence?.Hold(freezes[dragFreeze].At) is { } holding)
        {
            // Dragging the end of a freeze's hold makes it hold longer or shorter (tenths of a second).
            if (!freezeDragMoved && Math.Abs(p.X - pressPoint.X) <= 3) return;
            if (!freezeDragMoved) { freezeDragMoved = true; FreezeEditStarted?.Invoke(); }
            double seconds = Math.Clamp(Math.Round((VT(p.X) - holding.From) * 10) / 10, FreezeFrame.MinSeconds, FreezeFrame.MaxSeconds);
            FreezeHoldChanged?.Invoke(dragFreeze, seconds); return;
        }
        if (IsMouseCaptured && drag == Drag.FreezeMove && dragFreeze >= 0 && dragFreeze < freezes.Count)
        {
            if (!freezeDragMoved && Math.Abs(p.X - pressPoint.X) <= 3) return;
            if (!freezeDragMoved) { freezeDragMoved = true; FreezeEditStarted?.Invoke(); }
            FreezeMoved?.Invoke(dragFreeze, Math.Clamp(LockedTime(p.X), 0, Duration)); return;
        }
        if (IsMouseCaptured && drag is Drag.SoundMove or Drag.SoundStart or Drag.SoundEnd && soundOriginal is { } original && soundCurrent is { } current)
        {
            if (!soundDragMoved && (p - pressPoint).Length <= 3) return;
            if (!soundDragMoved) { soundDragMoved = true; SoundEditStarted?.Invoke(); }
            double shift = (p.X - pressPoint.X) * Span / Math.Max(1, ActualWidth - 2 * Inset), frame = 1 / Math.Max(1, FrameRate);
            var others = sounds.Where(s => s != current).ToList();
            bool Free(int row, double a, double b) => !others.Any(s => s.Row == row && s.End > a + 1e-9 && s.Start < b - 1e-9);
            SoundItem next;
            if (Mapped)
            {
                // Finished view: the sound moves or trims in finished time and re-anchors to the footage
                // (or freeze hold) now under its start.
                var (from, to) = SoundView(original);
                if (drag == Drag.SoundMove)
                {
                    var (src, hold) = FromView(Math.Clamp(from + shift, 0, Math.Max(0, Total - original.Length)));
                    if (hold <= 0) src = Snap(src);
                    int row = Math.Clamp((int)Math.Floor((p.Y - SoundTop(0)) / (SoundHeight + LaneGap)), 0, SoundRows);
                    // Rows are kept free of overlaps where the sounds play (in finished time).
                    double a = ToView(src) + hold, b = a + original.Length;
                    bool FreeView(int r) => !others.Any(s => s.Row == r && SoundView(s).To > a + 1e-9 && SoundView(s).From < b - 1e-9);
                    if (!FreeView(row)) row = FreeView(original.Row) ? original.Row : Enumerable.Range(0, sounds.Count + 1).First(FreeView);
                    next = original with { Start = src, End = src + original.Length, Hold = hold, Row = row };
                }
                else if (drag == Drag.SoundStart)
                {
                    double v = Math.Clamp(from + shift, 0, to - frame), trim = v - from;
                    var (src, hold) = FromView(v);
                    next = original with { Start = src, End = src + original.Length - trim, Hold = hold, Offset = Math.Max(0, original.Offset + trim * original.Speed) };
                }
                else next = original with { End = original.Start + (Math.Clamp(to + shift, from + frame, Total) - from) };
            }
            else if (drag == Drag.SoundMove)
            {
                double length = original.Length, start = Math.Clamp(original.Start + shift, 0, Math.Max(0, Duration - length));
                double a = LockedTime(XAt(start)), b = LockedTime(XAt(start + length)) - length;
                start = Math.Clamp(Math.Abs(a - start) <= Math.Abs(b - start) ? a : b, 0, Math.Max(0, Duration - length));
                int row = (int)Math.Floor((p.Y - SoundTop(0)) / (SoundHeight + LaneGap));
                row = Math.Clamp(row, 0, SoundRows);
                if (!Free(row, start, start + length)) row = Free(original.Row, start, start + length) ? original.Row : Enumerable.Range(0, sounds.Count + 1).First(r => Free(r, start, start + length));
                next = original with { Start = start, End = start + length, Row = row };
            }
            else
            {
                var sameRow = others.Where(s => s.Row == original.Row).ToList();
                if (drag == Drag.SoundStart)
                {
                    double limit = sameRow.Where(s => s.End <= original.Start + 1e-9).Select(s => s.End).DefaultIfEmpty(0).Max();
                    double start = Math.Clamp(LockedTime(XAt(original.Start + shift), original.Start, original.End), limit, original.End - frame);
                    // Trimming the front skips into the file by the same amount, so the sound stays in place.
                    next = original with { Start = start, Offset = Math.Max(0, original.Offset + (start - original.Start)) };
                }
                else
                {
                    double limit = sameRow.Where(s => s.Start >= original.End - 1e-9).Select(s => s.Start).DefaultIfEmpty(Duration).Min();
                    next = original with { End = Math.Clamp(LockedTime(XAt(original.End + shift), original.Start, original.End), original.Start + frame, limit) };
                }
            }
            if (next != current) { SoundMoved?.Invoke(current, next); soundCurrent = next; }
            return;
        }
        if (IsMouseCaptured && drag == Drag.PartEdge && resizing != null)
        {
            if (!overlayDragMoved && (p - pressPoint).Length <= 2) return;
            if (!overlayDragMoved) { overlayDragMoved = true; PartResizeStarted?.Invoke(resizing); }
            // Finished view: a zoom's end can stop partway into a freeze's hold.
            if (Mapped && resizing is ZoomRegion zoom)
            {
                var (source, hold) = FromView(VT(p.X));
                var moved = hold > 1e-9 ? new Moment(source, Math.Round(hold * 100) / 100) : new Moment(SectionTime(LockedTime(p.X, resizeStart, resizeEnd), p.X, resizingStart ? resizeEnd : resizeStart));
                var (from, to) = resizingStart ? (moved, zoom.To()) : (zoom.From(), moved);
                if (HoldTiming.Compare(from, to) < 0) PartResizedAt?.Invoke(resizing, from, to);
                return;
            }
            double t = SectionTime(LockedTime(p.X, resizeStart, resizeEnd), p.X, resizingStart ? resizeEnd : resizeStart), frame = 1 / Math.Max(1, FrameRate);
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
        bool hover = HasAudioArea && ToggleArea.Contains(p);
        if (hover != toggleHover) { toggleHover = hover; InvalidateVisual(); }
        if (hover) { Cursor = Cursors.Hand; ToolTip = lanesExpanded ? "Hide the audio tracks" : "Show the audio tracks"; return; }
        if (pickTime != null) { Cursor = Cursors.Cross; ToolTip = "Click where it should go · Esc cancels"; return; }
        if (ViewToggleArea.Contains(p) != viewHover) { viewHover = !viewHover; InvalidateVisual(); }
        if (viewHover) { Cursor = Cursors.Hand; ToolTip = finished ? "Showing the finished video · click for the whole recording" : "Showing the whole recording · click for the finished video"; return; }
        if (!Placing && pendingCut == null && !(Mapped && focusedPart is SoundItem) && FocusedEdgeAt(p) is bool edge) { Cursor = Cursors.SizeWE; ToolTip = edge ? "Drag to change when it starts" : "Drag to change when it ends"; return; }
        if (pendingCut == null && cutTags.Any(t => t.Tag.Contains(p))) { Cursor = Cursors.Hand; ToolTip = "Change this cut-out's times, or restore it"; return; }
        if (pendingCut == null && lanesExpanded && volumeTags.Any(t => t.Tag.Contains(p))) { Cursor = Cursors.Hand; ToolTip = "Change this part's volume · also selects it, so you can drag its ends"; return; }
        if (pendingCut == null && SoundAt(p) is { } hoverSound)
        {
            var rect = SoundRect(hoverSound);
            Cursor = rect.Width > 14 && (p.X - rect.Left <= 5 || rect.Right - p.X <= 5) ? Cursors.SizeWE : Cursors.SizeAll;
            ToolTip = $"{hoverSound.Label} · click for volume and fades, drag to move, drag an end to trim · right-click removes";
            return;
        }
        if (pendingCut == null && OverlayAt(p) is int hoverItem and >= 0)
        {
            var rect = OverlayRect(overlays[hoverItem]);
            Cursor = rect.Width > 14 && (p.X - rect.Left <= 5 || rect.Right - p.X <= 5) ? Cursors.SizeWE : Cursors.SizeAll;
            ToolTip = $"{overlays[hoverItem].Label} · click to edit, drag to move, drag up or down to change layer, drag an edge to change its length · right-click removes";
            return;
        }
        if (pendingCut == null && ZoomTagAt(p) >= 0) { Cursor = Cursors.Hand; ToolTip = "Edit this zoom · also selects it, so you can drag its ends"; return; }
        if (pendingCut == null && !Placing && FreezeAt(p) >= 0) { Cursor = Cursors.SizeWE; ToolTip = "Freeze frame · click for how long it holds, drag to move it"; return; }
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
            : !Mapped && p.Y>=TrackTop-7 && p.Y<=TrackTop+TrackHeight+7 && Math.Min(Math.Abs(p.X-XAt(Start)),Math.Abs(p.X-XAt(End)))<=14 ? Cursors.SizeWE : Cursors.Hand;
        ToolTip = lane >= 0 ? (lanes[lane].Toggleable ? $"Click to {(lanes[lane].Muted ? "include" : "mute")} {lanes[lane].Name.ToLowerInvariant()} audio in exports" : "Record with separate tracks to adjust desktop and microphone audio separately")
            : "Click to seek; drag the edges to trim. Wheel zooms · Shift+wheel pans · Ctrl+wheel changes speed";
    }
    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (toggleHover || viewHover || (cutHover != null && !IsMouseCaptured)) { toggleHover = viewHover = false; if (!IsMouseCaptured) cutHover = null; InvalidateVisual(); }
    }
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (pendingCut == null && OverlayAt(e.GetPosition(this)) is int item and >= 0) { OverlayRemoved?.Invoke(item); e.Handled = true; return; }
        if (pendingCut == null && SoundAt(e.GetPosition(this)) is { } sound) { SoundRemoved?.Invoke(sound); e.Handled = true; return; }
        if (volumeMode && pendingCut == null)
        {
            var at = e.GetPosition(this); int lane = BandAt(at); double when = TimeAt(at.X);
            if (volumeRegions.FirstOrDefault(v => v.Lane == lane && when >= v.Start && when <= v.End) is { } volume) { VolumeRemoved?.Invoke(volume); e.Handled = true; return; }
        }
        if (!Placing) return;
        // Right-click cancels a half-placed cut, or restores the cut (or slow part) under the pointer.
        if (pendingCut != null) { CancelPendingCut(); e.Handled = true; return; }
        var p = e.GetPosition(this); int band = BandAt(p); double t = TimeAt(p.X);
        if (zoomMode)
        {
            int zoom = zoomRegions.ToList().FindIndex(r => XSpans(r.From(), r.To()).Any(s => p.X >= s.X0 - 1 && p.X <= s.X1 + 1));
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
        if (IsMouseCaptured && drag is Drag.FreezeMove or Drag.FreezeEnd)
        {
            int picked = dragFreeze; bool moved = freezeDragMoved;
            ReleaseMouseCapture(); e.Handled = true;
            if (!moved && picked >= 0) FreezeTagClicked?.Invoke(picked);
            return;
        }
        if (!IsMouseCaptured) return;
        if (drag is Drag.SoundMove or Drag.SoundStart or Drag.SoundEnd)
        {
            var clicked = soundOriginal; bool moved = soundDragMoved;
            ReleaseMouseCapture(); e.Handled = true;
            if (!moved && clicked != null) SoundPicked?.Invoke(clicked);
            return;
        }
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
        if (drag is Drag.FreezeMove or Drag.FreezeEnd)
        {
            drag = Drag.None; dragFreeze = -1;
            if (freezeDragMoved) { freezeDragMoved = false; FreezeEditFinished?.Invoke(); }
            return;
        }
        if (drag is Drag.SoundMove or Drag.SoundStart or Drag.SoundEnd)
        {
            drag = Drag.None; soundOriginal = soundCurrent = null;
            if (soundDragMoved) { soundDragMoved = false; SoundEditFinished?.Invoke(); }
            return;
        }
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
        // An edge moved by the drag. In the finished view the move is in finished time, then back to the
        // footage under it (an edge stays in the section its other edge is in).
        double Shifted(double t, bool end, double anchor)
        {
            if (!Mapped) return t + shift;
            double v = Math.Clamp(EdgeView(t, end) + shift, 0, Total);
            return SectionTime(FromView(v).Source, VX(v), anchor);
        }
        // Work from the items as they were when the drag began, so layer swaps don't pile up.
        var list = dragStartList.Length == overlays.Count ? dragStartList.ToArray() : overlays.ToArray();
        var others = list.Where((_, i) => i != dragOverlay).ToList();
        // Items on a layer never overlap, freeze holds included.
        bool Free(int layer, Moment a, Moment b) => !others.Any(x => x.Layer == layer && HoldTiming.Overlaps(x.From(), x.To(), a, b));
        // A moment of the finished video: partway into a hold (to hundredths), or frame-snapped footage.
        Moment ViewMoment(double v)
        {
            var (source, hold) = FromView(Math.Clamp(v, 0, Total));
            return hold > 1e-9 ? new Moment(source, Math.Round(hold * 100) / 100) : new Moment(Snap(source));
        }
        // A part that lives inside a hold is a chip in the whole-recording view; it moves in the finished view.
        if (!Mapped && Math.Abs(o.Start - o.End) < 1e-9) return;
        var next = o;
        if (drag == Drag.OverlayMove)
        {
            Moment from, to;
            if (Mapped)
            {
                // Finished view: it slides in finished time, into, out of and through holds, and keeps its length there.
                double a0 = sequence!.ToOutput(o.From(), false), length = sequence.ToOutput(o.To(), true) - a0;
                double v = Math.Clamp(a0 + shift, 0, Math.Max(0, Total - length));
                if (Math.Abs(VX(v) - VX(PositionView)) <= PlayheadLock) v = PositionView;
                else if (Math.Abs(VX(v + length) - VX(PositionView)) <= PlayheadLock) v = PositionView - length;
                from = ViewMoment(v); to = ViewMoment(v + length);
            }
            else
            {
                double length = o.Length, start = Math.Clamp(o.Start + shift, 0, Math.Max(0, Duration - length));
                // Whichever edge is closer to something to lock onto wins.
                double a = SnapEdge(start, out bool lockA), b = SnapEdge(start + length, out bool lockB) - length;
                start = Math.Clamp(lockA || !lockB ? a : b, 0, Math.Max(0, Duration - length));
                from = new Moment(start); to = new Moment(start + length);
            }
            int top = others.Count == 0 ? 0 : others.Max(x => x.Layer) + 1;
            int layer = o.Layer;
            // Above the top row brings it to the front; down on the video track sends it to the back;
            // onto another row puts it there, trading places with whatever overlaps it.
            int target = p.Y >= TrackTop ? -1 : Math.Min(RowAt(p.Y), top);
            bool Overlaps(OverlayItem x) => HoldTiming.Overlaps(x.From(), x.To(), from, to);
            if (target < 0 && !(o.Layer == 0 && Free(0, from, to)))
            {
                if (Free(0, from, to)) layer = 0;
                else { for (int i = 0; i < list.Length; i++) if (i != dragOverlay) list[i] = list[i] with { Layer = list[i].Layer + 1 }; layer = 0; }
            }
            else if (target >= 0 && target != o.Layer)
            {
                var blockers = Enumerable.Range(0, list.Length).Where(i => i != dragOverlay && list[i].Layer == target && Overlaps(list[i])).ToList();
                bool swapFits = blockers.All(bi => !Enumerable.Range(0, list.Length).Any(j => j != dragOverlay && !blockers.Contains(j) && list[j].Layer == o.Layer && list[j].Overlaps(list[bi])));
                if (blockers.Count == 0) layer = target;
                else if (swapFits) { foreach (int bi in blockers) list[bi] = list[bi] with { Layer = o.Layer }; layer = target; }
            }
            others = list.Where((_, i) => i != dragOverlay).ToList();
            if (!Free(layer, from, to)) layer = Enumerable.Range(0, list.Length + 1).First(l => Free(l, from, to));
            next = o with { Start = from.At, StartHold = from.Hold, End = to.At, EndHold = to.Hold, Layer = layer };
        }
        else if (Mapped)
        {
            // Finished view: an edge moves in finished time, and can stop partway into a hold.
            double a0 = sequence!.ToOutput(o.From(), false), b0 = sequence.ToOutput(o.To(), true);
            var edge = drag == Drag.OverlayStart ? ViewMoment(Math.Min(a0 + shift, b0 - frame)) : ViewMoment(Math.Max(b0 + shift, a0 + frame));
            // In the section its other end is in, like any part.
            if (edge.Hold <= 0) edge = new Moment(SectionTime(edge.At, VX(drag == Drag.OverlayStart ? a0 + shift : b0 + shift), drag == Drag.OverlayStart ? o.End : o.Start));
            var candidate = drag == Drag.OverlayStart ? o with { Start = edge.At, StartHold = edge.Hold } : o with { End = edge.At, EndHold = edge.Hold };
            if (HoldTiming.Compare(candidate.From(), candidate.To()) < 0 && Free(o.Layer, candidate.From(), candidate.To())) next = candidate;
        }
        else
        {
            var neighbours = others.Where(x => x.Layer == o.Layer).ToList();
            if (drag == Drag.OverlayStart)
            {
                double limit = neighbours.Where(x => x.End <= o.Start + 1e-9).Select(x => x.End).DefaultIfEmpty(0).Max();
                next = o with { Start = Math.Clamp(SnapEdge(Shifted(o.Start, false, o.End), out _), limit, o.End - frame), StartHold = 0 };
            }
            else
            {
                double limit = neighbours.Where(x => x.Start >= o.End - 1e-9).Select(x => x.Start).DefaultIfEmpty(Duration).Min();
                next = o with { End = Math.Clamp(SnapEdge(Shifted(o.End, true, o.Start), out _), o.Start + frame, limit), EndHold = 0 };
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
        else ZoomView(Math.Pow(1.5, e.Delta / 120.0), VT(e.GetPosition(this).X));
        e.Handled=true;
    }
    internal void MoveTo(double x)
    {
        double t;
        if (Mapped)
        {
            // Finished view: inside a freeze's hold the playhead parks on the frozen frame, that far in.
            var (source, hold) = FromView(VT(x));
            holdOffset = hold; t = hold > 0 ? source : Snap(source);
        }
        else { holdOffset = 0; t = Snap(TimeAt(x-grabOffset)); }
        if (drag == Drag.Start) { Start=Math.Min(t,Math.Max(0,End-.1)); RangeChanged?.Invoke(Start,End); t=Start; }
        else if (drag == Drag.End) { End=Math.Max(t,Math.Min(Duration,Start+.1)); RangeChanged?.Invoke(Start,End); t=End; }
        Position=t; InvalidateVisual(); SeekRequested?.Invoke(t);
    }
}
