using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace Flashback;

// The timeline part you clicked last (a cut, speed part, zoom, or text or picture) is outlined, and
// Delete or Backspace removes it, the same as right-clicking it. Clicking elsewhere, or picking a
// section, hands Delete back to removing the selected section.
public partial class TrimWindow
{
    private enum PartKind { None, Cut, Speed, Zoom, Overlay, Volume, Sound, Freeze }
    private PartKind focusedKind;
    private void InitParts()
    {
        Timeline.PartClicked += part =>
        {
            FocusPart(part);
            if (part != null) StatusLabel.Text = $"Selected the {PartName(part)}: drag its ends to change its length, Delete removes it" + (Timeline.StackedAtLastClick > 1 ? ". Click again for what's stacked under it." : ".");
        };
        // A click on text or a picture in the preview focuses it; keyboard focus moves to the
        // timeline so Delete removes the item rather than editing the panel's text.
        OverlayView.AddHandler(UIElement.MouseLeftButtonDownEvent, new MouseButtonEventHandler((_, _) =>
        {
            if (SelectedOverlayItem is { } item) { FocusPart(item); Timeline.Focus(); }
        }), true);
    }
    private void FocusPart(object? part)
    {
        focusedKind = part switch { CutRegion => PartKind.Cut, SpeedRegion => PartKind.Speed, ZoomRegion => PartKind.Zoom, OverlayItem => PartKind.Overlay, VolumeRegion => PartKind.Volume, SoundItem => PartKind.Sound, FreezeFrame => PartKind.Freeze, _ => PartKind.None };
        Timeline.FocusedPart = part;
    }
    // The part being edited or last clicked, in its current form.
    private object? CurrentPart() => focusedKind switch
    {
        PartKind.Overlay => SelectedOverlayItem ?? Timeline.FocusedPart,
        PartKind.Zoom => SelectedZoomRegion ?? (Timeline.FocusedPart is ZoomRegion z ? Timeline.ZoomRegions.FirstOrDefault(r => r.Start == z.Start && r.End == z.End) : null),
        PartKind.Speed => Timeline.SelectedSlow >= 0 && Timeline.SelectedSlow < Timeline.SlowRegions.Count ? Timeline.SlowRegions[Timeline.SelectedSlow]
            : Timeline.FocusedPart is SpeedRegion s ? Timeline.SlowRegions.FirstOrDefault(r => r.Start == s.Start && r.End == s.End) : null,
        PartKind.Cut => Timeline.FocusedPart is CutRegion c && Timeline.Cuts.Contains(c) ? c : null,
        PartKind.Volume => Timeline.FocusedPart is VolumeRegion v && Timeline.VolumeRegions.Contains(v) ? v : null,
        PartKind.Sound => Timeline.FocusedPart is SoundItem s ? CurrentVersion(s) : null,
        PartKind.Freeze => Timeline.FocusedPart is FreezeFrame f ? Timeline.Freezes.FirstOrDefault(x => Math.Abs(x.At - f.At) < 1e-9) : null,
        _ => null
    };
    // Copy and paste: Ctrl+C copies the clicked part (kept while Flashback runs, even across clips);
    // Ctrl+V puts a copy at the playhead with the same length, where it fits.
    private static object? copiedPart;
    private void CopyPart()
    {
        if (CurrentPart() is not { } part) { StatusLabel.Text = "Click a cut-out, speed part, zoom, text or picture first, then copy it."; return; }
        copiedPart = part;
        StatusLabel.Text = $"Copied the {PartName(part)}. Ctrl+V pastes it at the playhead.";
    }
    private static string PartName(object part) => part switch { CutRegion { Lane: < 0 } => "video cut-out", CutRegion => "audio cut-out", SpeedRegion => "speed part", ZoomRegion => "zoom", VolumeRegion => "volume change", SoundItem => "sound", FreezeFrame => "freeze frame", OverlayItem { Kind: OverlayKind.Image } => "picture", OverlayItem { Kind: OverlayKind.Video } => "video", OverlayItem { Kind: OverlayKind.Shape } => "shape", _ => "text" };
    private void PastePart()
    {
        if (copiedPart is not { } part) { StatusLabel.Text = "Nothing copied yet. Click a part and press Ctrl+C."; return; }
        // A freeze is a single moment, so it goes right at the playhead.
        if (part is FreezeFrame copiedFreeze) { AddFreeze(playhead, copiedFreeze.Seconds); return; }
        var (from, to) = SpanOf(part);
        double start = Math.Min(playhead, Math.Max(0, media.Duration - .05)), end = Math.Min(media.Duration, start + (to - from));
        if (end - start < 1 / Math.Max(1, media.FrameRate)) { StatusLabel.Text = "There's no room after the playhead to paste it."; return; }
        bool Overlaps(double a, double b) => b > start + 1e-9 && a < end - 1e-9;
        switch (part)
        {
            case CutRegion cut:
                if (cut.Lane >= Timeline.Lanes.Count) { StatusLabel.Text = "This clip has no matching audio track for that cut-out."; return; }
                CutAdded(cut with { Start = start, End = end }); break;
            case SpeedRegion speed:
                if (OverlapsVideoCut(start, end)) { StatusLabel.Text = "A speed part can't overlap a video cut-out."; return; }
                if (Timeline.SlowRegions.Any(r => Overlaps(r.Start, r.End))) { StatusLabel.Text = "It would overlap another speed part. Move the playhead and paste again."; return; }
                Snapshot();
                Timeline.SlowRegions = Timeline.SlowRegions.Append(speed with { Start = start, End = end }).OrderBy(r => r.Start).ToArray();
                UpdateExportHint(); UpdateSummary(); StatusLabel.Text = $"Pasted a {speed.Speed:0.##}× speed part."; break;
            case ZoomRegion zoom:
                if (OverlapsVideoCut(start, end)) { StatusLabel.Text = "A zoom can't overlap a video cut-out."; return; }
                if (Timeline.ZoomRegions.Any(r => Overlaps(r.Start, r.End))) { StatusLabel.Text = "It would overlap another zoom. Move the playhead and paste again."; return; }
                Snapshot();
                var pasted = zoom with { Start = start, End = end };
                Timeline.ZoomRegions = Timeline.ZoomRegions.Append(pasted).OrderBy(r => r.Start).ToArray();
                UpdateExportHint(); OpenZoom(Timeline.ZoomRegions.ToList().IndexOf(pasted)); StatusLabel.Text = "Pasted the zoom."; break;
            case VolumeRegion volume:
                if (volume.Lane >= Timeline.Lanes.Count) { StatusLabel.Text = "This clip has no matching audio lane for that volume change."; return; }
                if (Timeline.VolumeRegions.Any(v => v.Lane == volume.Lane && Overlaps(v.Start, v.End))) { StatusLabel.Text = "It would overlap another volume change on that lane."; return; }
                Snapshot(); Timeline.VolumeRegions = Timeline.VolumeRegions.Append(volume with { Start = start, End = end }).ToArray();
                UpdateExportHint(); ProjectChanged(); StatusLabel.Text = "Pasted the volume change."; break;
            case SoundItem sound:
                Snapshot(); SetSounds(Timeline.Sounds.Append(sound with { Start = start, End = end, Row = FreeSoundRow(Timeline.Sounds, start, end) }).ToArray());
                UpdateExportHint(); ProjectChanged(); StatusLabel.Text = "Pasted the sound."; break;
            case OverlayItem item:
                AddOverlay(item with { Start = start, End = end, Layer = 0 });
                if (Timeline.Overlays.Count > 0 && ReferenceEquals(SelectedOverlayItem, Timeline.Overlays[^1])) StatusLabel.Text = $"Pasted the {PartName(item)}.";
                break;
        }
    }
    // Arrow keys move the selected text or picture a pixel at a time (Shift: ten).
    private bool Nudge(Key key, ModifierKeys modifiers)
    {
        if (modifiers is not (ModifierKeys.None or ModifierKeys.Shift)) return false;
        // A freeze frame steps a frame at a time (ten with Shift).
        if (focusedKind == PartKind.Freeze && key is Key.Left or Key.Right && CurrentPart() is FreezeFrame freeze)
        {
            int i = Timeline.Freezes.ToList().IndexOf(freeze);
            double at = Math.Clamp(freeze.At + (key == Key.Left ? -1 : 1) * (modifiers == ModifierKeys.Shift ? 10 : 1) * FrameStep, 0, Math.Max(0, media.Duration - FrameStep));
            if (i < 0 || Timeline.Freezes.Where((_, n) => n != i).Any(f => Math.Abs(f.At - at) < FrameStep / 2)) return true;
            if (lastOverlayControl != "nudgeFreeze" || System.Diagnostics.Stopwatch.GetElapsedTime(lastOverlayChange).TotalSeconds > 1.2) Snapshot();
            lastOverlayControl = "nudgeFreeze"; lastOverlayChange = System.Diagnostics.Stopwatch.GetTimestamp();
            var moved = freeze with { At = at };
            Timeline.Freezes = Timeline.Freezes.Select((f, n) => n == i ? moved : f).OrderBy(f => f.At).ToArray();
            FocusPart(moved); UpdateExportHint(); ProjectChanged();
            StatusLabel.Text = $"Freeze frame at {KeepSection.TimeText(at)} · arrows move it a frame, Shift ten.";
            return true;
        }
        if (focusedKind != PartKind.Overlay || SelectedOverlayItem is null || key is not (Key.Left or Key.Right or Key.Up or Key.Down)) return false;
        double step = modifiers == ModifierKeys.Shift ? 10 : 1, w = Math.Max(1, media.Width > 0 ? media.Width : 1920), h = Math.Max(1, media.Height > 0 ? media.Height : 1080);
        double dx = key == Key.Left ? -step : key == Key.Right ? step : 0, dy = key == Key.Up ? -step : key == Key.Down ? step : 0;
        // With keyframes, the nudge sets a keyframe at the playhead, like dragging does.
        EditOverlay("nudge", o => Keyed(o, x => x with { X = x.X + dx / w, Y = x.Y + dy / h }));
        LoadOverlayUi();
        StatusLabel.Text = "Arrow keys nudge it a pixel; Shift moves ten. Click the timeline to step frames again.";
        return true;
    }
    // Returns false when nothing is focused, so Delete falls back to removing a section. A part that
    // has since gone (undo, say) still swallows the key rather than removing a section by surprise.
    private bool DeleteFocusedPart()
    {
        var part = Timeline.FocusedPart; var kind = focusedKind;
        FocusPart(null);
        switch (kind)
        {
            case PartKind.Cut when part is CutRegion cut && Timeline.Cuts.Contains(cut):
                CutRemoved(cut); return true;
            case PartKind.Speed when part is SpeedRegion s:
            {
                // Match by position: its speed may have changed since it was clicked.
                int i = Timeline.SelectedSlow >= 0 ? Timeline.SelectedSlow : Timeline.SlowRegions.ToList().FindIndex(r => r.Start == s.Start && r.End == s.End);
                if (i < 0) return true;
                SlowPopup.IsOpen = false; SlowRemoved(i); return true;
            }
            case PartKind.Zoom when part is ZoomRegion z:
            {
                int i = Timeline.SelectedZoom >= 0 ? Timeline.SelectedZoom : Timeline.ZoomRegions.ToList().FindIndex(r => r.Start == z.Start && r.End == z.End);
                if (i < 0) return true;
                RemoveZoom(i); return true;
            }
            case PartKind.Volume when part is VolumeRegion volume:
                if (Timeline.VolumeRegions.Contains(volume)) RemoveVolume(volume); return true;
            case PartKind.Sound when part is SoundItem sound:
                if (CurrentVersion(sound) is SoundItem now) RemoveSound(now); return true;
            case PartKind.Freeze when part is FreezeFrame freeze:
            {
                int i = Timeline.Freezes.ToList().FindIndex(f => Math.Abs(f.At - freeze.At) < 1e-9);
                if (i >= 0) RemoveFreeze(i); return true;
            }
            case PartKind.Overlay:            {
                int i = Timeline.SelectedOverlay >= 0 ? Timeline.SelectedOverlay : Timeline.Overlays.ToList().FindIndex(o => ReferenceEquals(o, part));
                if (i < 0) return true;
                RemoveOverlay(i); return true;
            }
        }
        return kind != PartKind.None;
    }
}
