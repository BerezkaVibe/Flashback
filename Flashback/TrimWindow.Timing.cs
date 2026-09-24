using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Flashback;

// Changing when a part starts and ends without redoing it: drag the selected part's ends on the
// timeline, type a time, or press a pick button and click the timeline. Works for cut-outs, speed
// parts, zooms, text and pictures, with the usual rules (no overlapping what it can't overlap).
public partial class TrimWindow
{
    private object? resizeCurrent;
    private (double Start, double End) resizeFrom;
    private Action? zoomTiming, slowTiming, cutTiming;
    private CutRegion? popupCut;

    private void InitTiming()
    {
        Timeline.PartResizeStarted += part => { Snapshot(); resizeCurrent = CurrentVersion(part); resizeFrom = resizeCurrent is { } p ? SpanOf(p) : default; };
        // While dragging, a move that would break a rule is skipped, so the end stops at the obstacle.
        Timeline.PartResized += (_, start, end) => { if (resizeCurrent != null && TryRetime(resizeCurrent, start, end, out var next, out string? _)) resizeCurrent = next; };
        Timeline.PartResizeFinished += () =>
        {
            // A drag that ended where it began leaves no undo step behind.
            if (resizeCurrent == null || SpanOf(resizeCurrent) == resizeFrom) { if (undo.Count > 0) undo.Pop(); }
            resizeCurrent = null; AfterRetime();
        };
        Timeline.CutTagClicked += OpenCutPopup;
        ZoomTimingHost.Children.Add(TimingEditor(() => SelectedZoomRegion, out zoomTiming));
        SlowTimingHost.Children.Add(TimingEditor(() => CurrentPart() as SpeedRegion, out slowTiming));
        CutTimingHost.Children.Add(TimingEditor(() => popupCut, out cutTiming));
    }
    private static (double Start, double End) SpanOf(object part) => part switch
    {
        CutRegion c => (c.Start, c.End), SpeedRegion s => (s.Start, s.End), ZoomRegion z => (z.Start, z.End), OverlayItem o => (o.Start, o.End), VolumeRegion v => (v.Start, v.End), SoundItem d => (d.Start, d.End), FreezeFrame f => (f.At, f.At + .001), _ => (0, 0)
    };
    // The part as it is in the lists now (records are replaced on every edit).
    private object? CurrentVersion(object part) => part switch
    {
        CutRegion c => Timeline.Cuts.Contains(c) ? c : null,
        SpeedRegion s => Timeline.SlowRegions.FirstOrDefault(r => r.Start == s.Start && r.End == s.End),
        ZoomRegion z => Timeline.ZoomRegions.FirstOrDefault(r => r.Start == z.Start && r.End == z.End),
        OverlayItem o => Timeline.Overlays.Contains(o) ? o : null,
        VolumeRegion v => Timeline.VolumeRegions.Contains(v) ? v : null,
        SoundItem s => Timeline.Sounds.FirstOrDefault(x => x.Start == s.Start && x.End == s.End && x.Row == s.Row && x.Path == s.Path),
        _ => null
    };
    // Moves a part to start..end if the rules allow; otherwise explains why not.
    private bool TryRetime(object current, double start, double end, out object? next, out string? error)
    {
        next = null; error = null;
        double frame = 1 / Math.Max(1, media.FrameRate);
        start = Math.Clamp(start, 0, media.Duration); end = Math.Clamp(end, 0, media.Duration);
        if (end - start < frame - 1e-9) { error = "It needs to be at least a frame long, with the end after the start."; return false; }
        bool Hits(double a, double b) => b > start + 1e-9 && a < end - 1e-9;
        switch (current)
        {
            case CutRegion cut:
                if (Timeline.Cuts.Any(o => o != cut && o.Lane == cut.Lane && Hits(o.Start, o.End))) { error = "It would overlap another cut-out on that track."; return false; }
                if (cut.Lane < 0 && OverlapsParts(start, end) is { } blocked) { error = $"A video cut-out can't overlap {blocked}."; return false; }
                var newCut = cut with { Start = start, End = end };
                Timeline.Cuts = Timeline.Cuts.Select(o => o == cut ? newCut : o).ToArray();
                if (popupCut == cut) popupCut = newCut;
                next = newCut; ApplyPreviewCuts(); break;
            case SpeedRegion speed:
                if (Timeline.SlowRegions.Any(o => o != speed && Hits(o.Start, o.End))) { error = "It would overlap another speed part."; return false; }
                if (OverlapsVideoCut(start, end)) { error = "A speed part can't overlap a video cut-out."; return false; }
                var newSpeed = speed with { Start = start, End = end };
                Timeline.SlowRegions = Timeline.SlowRegions.Select(o => o == speed ? newSpeed : o).ToArray();
                next = newSpeed; break;
            case ZoomRegion zoom:
                if (Timeline.ZoomRegions.Any(o => o != zoom && Hits(o.Start, o.End))) { error = "It would overlap another zoom."; return false; }
                if (OverlapsVideoCut(start, end)) { error = "A zoom can't overlap a video cut-out."; return false; }
                var newZoom = zoom with { Start = start, End = end };
                Timeline.ZoomRegions = Timeline.ZoomRegions.Select(o => o == zoom ? newZoom : o).ToArray();
                next = newZoom; ApplyZoomPreview(); break;
            case OverlayItem item:
                if (OverlapsVideoCut(start, end)) { error = "Text and pictures can't overlap a video cut-out."; return false; }
                if (Timeline.Overlays.Any(o => !ReferenceEquals(o, item) && o.Layer == item.Layer && Hits(o.Start, o.End))) { error = "It would overlap another item on its layer. Drag it to another row first."; return false; }
                int index = Timeline.Overlays.ToList().FindIndex(o => ReferenceEquals(o, item));
                if (index < 0) return false;
                var newItem = item with { Start = start, End = end };
                ReplaceOverlay(index, newItem);
                next = newItem; break;
            case VolumeRegion volume:
                if (Timeline.VolumeRegions.Any(o => o != volume && o.Lane == volume.Lane && Hits(o.Start, o.End))) { error = "It would overlap another volume change on that lane."; return false; }
                var newVolume = volume with { Start = start, End = end };
                Timeline.VolumeRegions = Timeline.VolumeRegions.Select(o => o == volume ? newVolume : o).ToArray();
                if (popupVolume == volume) popupVolume = newVolume;
                next = newVolume; break;
            case SoundItem sound:
                if (Timeline.Sounds.Any(o => o != sound && o.Row == sound.Row && Hits(o.Start, o.End))) { error = "It would overlap another sound on its row."; return false; }
                var newSound = sound with { Start = start, End = end, Hold = Math.Abs(start - sound.Start) < 1e-9 ? sound.Hold : 0 };
                ReplaceSound(sound, newSound);
                next = newSound; break;
            default: return false;
        }
        FocusPart(next);
        return true;
    }
    private void AfterRetime()
    {
        UpdateExportHint(); UpdateSummary(); ProjectChanged();
        zoomTiming?.Invoke(); slowTiming?.Invoke(); cutTiming?.Invoke(); volumeTiming?.Invoke();
        if (ZoomPanel.Visibility == Visibility.Visible) LoadZoomUi();
        if (OverlayPanel.Visibility == Visibility.Visible) LoadOverlayUi();
    }
    // A part's start and end as the timeline shows them: the recording's times, or in the finished view
    // where it plays in the finished video.
    private (double Start, double End) ShownSpan(object part)
    {
        var (s, e) = SpanOf(part);
        if (!Timeline.Finished) return (s, e);
        if (part is SoundItem sound) { double at = Timeline.ToView(sound.Start) + sound.Hold; return (at, at + sound.Length); }
        return (Timeline.ToView(s), Timeline.EndView(e));
    }
    // Sets one end of a part (typed, or picked on the timeline) as one undo step. view: the time is in
    // the finished video's time.
    private void SetPartTime(object? part, bool isStart, double time, bool view = false)
    {
        if (part == null || CurrentVersion(part) is not { } current) { StatusLabel.Text = "That part is no longer there."; return; }
        var (start, end) = SpanOf(current); double typed = time;
        Snapshot();
        bool done; string? error = null;
        if (view && current is SoundItem sound)
        {
            // A sound plays for real seconds from where its start is anchored, so work in finished time.
            var (from, to) = ShownSpan(sound);
            double a = isStart ? time : from, b = isStart ? to : time;
            var (at, hold) = Timeline.FromView(Math.Max(0, a));
            var next = sound with { Start = at, Hold = hold, End = at + (b - a) };
            done = b - a >= FrameStep - 1e-9 && !Timeline.Sounds.Any(o => o != sound && o.Row == sound.Row && ShownSpan(o).End > a + 1e-9 && ShownSpan(o).Start < b - 1e-9);
            if (done) { ReplaceSound(sound, next); FocusPart(next); }
            else error = b - a < FrameStep - 1e-9 ? "It needs to be at least a frame long, with the end after the start." : "It would overlap another sound on its row.";
        }
        else
        {
            if (view) time = Timeline.SourceNear(time, isStart ? end : start);
            if (isStart) start = time; else end = time;
            done = TryRetime(current, start, end, out _, out error);
        }
        if (!done) { undo.Pop(); StatusLabel.Text = error; }
        else StatusLabel.Text = $"{(isStart ? "Starts" : "Ends")} at {KeepSection.TimeText(typed)}.";
        AfterRetime();
    }
    // Start and end fields with pick buttons, and the length. refresh re-reads the part.
    private FrameworkElement TimingEditor(Func<object?> part, out Action refresh)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var boxes = new TextBox[2];
        // The part the boxes are showing, so a typed time never lands on a different part.
        object? shown = null;
        for (int row = 0; row < 2; row++)
        {
            bool isStart = row == 0;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = isStart ? "Starts" : "Ends", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            var box = new TextBox { Height = 28, FontFamily = new FontFamily("Consolas"), FontSize = 12, Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0, 2, 4, 2), VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "Type a time like 1:05.5 or 65.5 and press Enter" };
            System.Windows.Automation.AutomationProperties.SetName(box, isStart ? "Start time" : "End time");
            void Commit()
            {
                // Style edits replace the part's record, so match by kind and times rather than identity.
                if (shown == null || part() is not { } p || p.GetType() != shown.GetType() || SpanOf(p) != SpanOf(shown)) return;
                try { double t = KeepSection.Parse(box.Text); if (Math.Abs(t - (isStart ? ShownSpan(p).Start : ShownSpan(p).End)) > 1e-6) SetPartTime(p, isStart, t, Timeline.Finished); }
                catch (ArgumentException ex) { StatusLabel.Text = ex.Message; }
                if (part() is { } now) { shown = now; var (s, e) = ShownSpan(now); box.Text = KeepSection.TimeText(isStart ? s : e); }
            }
            box.KeyDown += (_, k) => { if (k.Key == Key.Enter) { Commit(); k.Handled = true; } };
            box.LostKeyboardFocus += (_, _) => Commit();
            var pick = new Button { Content = "", Width = 28, Height = 28, MinHeight = 28, FontSize = 12, Padding = new Thickness(0),
                ToolTip = isStart ? "Click here, then click the timeline where it should start" : "Click here, then click the timeline where it should end" };
            pick.SetResourceReference(StyleProperty, "IconButton");
            System.Windows.Automation.AutomationProperties.SetName(pick, isStart ? "Pick start on the timeline" : "Pick end on the timeline");
            pick.Click += (_, _) => PickPartTime(part(), isStart);
            Grid.SetRow(label, row); Grid.SetRow(box, row); Grid.SetColumn(box, 1); Grid.SetRow(pick, row); Grid.SetColumn(pick, 2);
            grid.Children.Add(label); grid.Children.Add(box); grid.Children.Add(pick);
            boxes[row] = box;
        }
        var length = new TextBlock { FontSize = 11, Margin = new Thickness(50, 2, 0, 0), ToolTip = "You can also drag its ends on the timeline" };
        length.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(length, 2); Grid.SetColumnSpan(length, 3); grid.Children.Add(length);
        refresh = () =>
        {
            if (part() is not { } p) return;
            shown = p;
            var (s, e) = ShownSpan(p);
            if (!boxes[0].IsKeyboardFocused) boxes[0].Text = KeepSection.TimeText(s);
            if (!boxes[1].IsKeyboardFocused) boxes[1].Text = KeepSection.TimeText(e);
            length.Text = $"Lasts {e - s:0.00} s";
        };
        return grid;
    }
    // The next click on the timeline sets this end of the part. Pop-ups close so the click reaches it.
    private void PickPartTime(object? part, bool isStart)
    {
        if (part == null) return;
        SlowPopup.IsOpen = false; CutPopup.IsOpen = false; VolumePopup.IsOpen = false; SoundPopup.IsOpen = false;
        // A sound's ends are in real seconds, so in the finished view a picked moment goes over as finished time.
        Timeline.PickTime = t => { Timeline.PickTime = null; bool view = Timeline.Finished && part is SoundItem; SetPartTime(part, isStart, view ? Timeline.ToView(t) : t, view); };
        Timeline.Focus();
        StatusLabel.Text = $"Click the timeline where it should {(isStart ? "start" : "end")}. It locks onto the playhead and other parts' edges · Esc cancels.";
    }
    private void OpenCutPopup(CutRegion cut)
    {
        if (exportCancellation != null) return;
        FocusPart(cut); popupCut = cut;
        CutPopupTitle.Text = cut.Lane < 0 ? "Video cut-out" : cut.Lane < Timeline.Lanes.Count ? $"{Timeline.Lanes[cut.Lane].Name} audio cut-out" : "Audio cut-out";
        cutTiming?.Invoke();
        CutPopup.IsOpen = true;
    }
    private void CutRestore_Click(object sender, RoutedEventArgs e)
    {
        CutPopup.IsOpen = false;
        if (popupCut is { } cut && Timeline.Cuts.Contains(cut)) CutRemoved(cut);
        popupCut = null;
    }
}
