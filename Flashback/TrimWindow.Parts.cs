using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace Flashback;

// The timeline part you clicked last (a cut, speed part, zoom, or text or picture) is outlined, and
// Delete or Backspace removes it, the same as right-clicking it. Clicking elsewhere, or picking a
// section, hands Delete back to removing the selected section.
public partial class TrimWindow
{
    private enum PartKind { None, Cut, Speed, Zoom, Overlay }
    private PartKind focusedKind;
    private void InitParts()
    {
        Timeline.PartClicked += part => FocusPart(part);
        // A click on text or a picture in the preview focuses it; keyboard focus moves to the
        // timeline so Delete removes the item rather than editing the panel's text.
        OverlayView.AddHandler(UIElement.MouseLeftButtonDownEvent, new MouseButtonEventHandler((_, _) =>
        {
            if (SelectedOverlayItem is { } item) { FocusPart(item); Timeline.Focus(); }
        }), true);
    }
    private void FocusPart(object? part)
    {
        focusedKind = part switch { CutRegion => PartKind.Cut, SpeedRegion => PartKind.Speed, ZoomRegion => PartKind.Zoom, OverlayItem => PartKind.Overlay, _ => PartKind.None };
        Timeline.FocusedPart = part;
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
            case PartKind.Overlay:
            {
                int i = Timeline.SelectedOverlay >= 0 ? Timeline.SelectedOverlay : Timeline.Overlays.ToList().FindIndex(o => ReferenceEquals(o, part));
                if (i < 0) return true;
                RemoveOverlay(i); return true;
            }
        }
        return kind != PartKind.None;
    }
}
