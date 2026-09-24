using System;
using System.Linq;

namespace Flashback;

// The finished-video view: the timeline shows the export as it plays (sections in order, speed parts
// stretched or squeezed, freezes holding), and playing, seeking and stepping all happen in that time.
public partial class TrimWindow
{
    private void InitSequence() => Timeline.ViewToggled += ToggleFinishedView;
    internal void ToggleFinishedView()
    {
        if (source.Length == 0) return;
        bool wasPlaying = playing;
        Pause();
        Timeline.Finished = !Timeline.Finished;
        // Entering the finished view from footage that isn't kept moves to where the next kept footage plays.
        if (Timeline.Finished) { var (at, hold) = Timeline.Map.ToSource(Timeline.Map.ToOutput(playhead)); SeekTo(at, hold: hold); }
        else SeekTo(playhead);
        Timeline.Fit(); Timeline.Reveal(playhead);
        // Typed times follow the view, so show the open part's times in the new one.
        zoomTiming?.Invoke(); slowTiming?.Invoke(); cutTiming?.Invoke(); volumeTiming?.Invoke();
        SoundPopup.IsOpen = false;
        if (OverlayPanel.Visibility == System.Windows.Visibility.Visible) LoadOverlayUi();
        StatusLabel.Text = Timeline.Finished ? "Showing the finished video: sections in play order, speed parts and freezes at the length they export." : "Showing the whole recording.";
        if (wasPlaying) Resume();
    }
    // Seeks to a moment of whatever the timeline shows (the finished video's time when that view is on).
    private void SeekView(double view)
    {
        if (!Timeline.Finished) { SeekTo(view); return; }
        var map = Timeline.Map;
        var (at, hold) = map.ToSource(Math.Clamp(view, 0, map.Total));
        SeekTo(at, hold: hold);
    }
    private void StepView(double seconds) { if (Timeline.Finished) SeekView(Timeline.PositionView + seconds); else SeekTo(playhead + seconds); }
    // Plays from the playhead: the recording as it is, or in the finished view the export as it plays.
    private void Resume()
    {
        if (!Timeline.Finished) { StartPlayback(playhead >= media.Duration - .01 ? 0 : playhead); return; }
        var map = Timeline.Map;
        double view = Timeline.PositionView;
        if (view >= map.Total - .01) view = 0;
        var (at, hold) = map.ToSource(view);
        // At the very end of a hold, it's done: play on without holding again.
        if (map.Hold(at) is { } held && view >= held.To - 1e-6) { freezeDone = at; hold = 0; }
        StartPlayback(at, sections.Count > 0 ? Math.Max(0, map.SectionAt(view)) : -2, hold);
    }
    // How far into a freeze's hold the playhead is: while holding, the time held so far; parked on a
    // finished hold, all of it; parked inside one by a seek, where it was put.
    private double HoldAt(double time)
    {
        if (freezing is { } held) return Math.Min(held.Part.Seconds, System.Diagnostics.Stopwatch.GetElapsedTime(held.Began).TotalSeconds * PreviewRate);
        if (freezeDone is double done && Math.Abs(done - time) < 1e-6 && Timeline.Freezes.FirstOrDefault(f => Math.Abs(f.At - done) < 1e-9) is { } past) return past.Seconds;
        return Timeline.Freezes.Any(f => Math.Abs(f.At - time) < 1e-9) ? parkedHold : 0;
    }
    private double parkedHold;
    private string PositionText() => Timeline.Finished
        ? $"{KeepSection.TimeText(Timeline.PositionView)} / {KeepSection.TimeText(Timeline.Map.Total)}"
        : $"{KeepSection.TimeText(playhead)} / {KeepSection.TimeText(media.Duration)}";
}
