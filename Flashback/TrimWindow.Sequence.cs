using System;
using System.Collections.Generic;
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

    // ---- The FFmpeg player's timeline ----
    // It plays the timeline itself: the finished video in the finished view, the whole recording otherwise,
    // with speed parts, freezes, music and sound files; the playhead follows it.
    private bool engineHolding;
    private (int, bool, object, string, double)? timelineKey;
    private (double, double) timelineRange = (double.NaN, double.NaN);
    // Hands it the timeline when anything on it changed (cheap when nothing did); if the pieces changed while
    // playing, it carries on from the playhead's place on the new ones.
    private void SyncTimeline(bool force = false)
    {
        if (Player.Engine is not { } engine || source.Length == 0) return;
        // (The range marks are plain fields the timeline only notices while drawing the finished view.)
        if ((Timeline.Start, Timeline.End) != timelineRange) { timelineRange = (Timeline.Start, Timeline.End); Timeline.Remap(); }
        var map = Timeline.Map;
        var key = (map.Version, Timeline.Finished, (object)Timeline.Sounds, source, media.Duration);
        if (!force && timelineKey is { } known && known.Equals(key)) return;
        timelineKey = key;
        bool moved = engine.SetSequence(TimelinePieces(map), TimelineSounds(map));
        if (moved && playing) engine.Position = EngineTime(engine, playhead, parkedHold);
    }
    // The playhead and hold where it is.
    private void FollowEngine(FfmpegPreviewPlayer engine)
    {
        var (at, hold, _, holding) = engine.Where;
        parkedHold = hold; engineHolding = playing && holding;
        SetPlayhead(at);
    }
    // Where a moment of the recording (and how far into a hold) plays on its timeline; in the finished view,
    // in the section given or the one showing there.
    private double EngineTime(FfmpegPreviewPlayer engine, double at, double hold, int section = -1)
    {
        if (section < 0 && Timeline.Finished) section = Timeline.Map.SectionAt(Timeline.Map.ToOutput(at) + hold);
        return engine.TimeOf(at, hold, section);
    }
    private List<PlayPiece> TimelinePieces(SequenceMap map)
    {
        var list = new List<PlayPiece>();
        double exportSpeed = Timeline.ExportSpeed > 0 ? Timeline.ExportSpeed : 1;
        if (Timeline.Finished)
        {
            // The finished video's pieces, played at their speed parts' speeds (the export speed isn't
            // previewed), holds as long as they're set.
            for (int i = 0; i < map.Pieces.Count; i++)
            {
                var p = map.Pieces[i];
                list.Add(p.Freeze ? new PlayPiece(p.Start, p.Start, 1, p.Hold, map.PieceStart(i), map.PieceSection(i))
                                  : new PlayPiece(p.Start, p.End, p.Speed / exportSpeed, 0, map.PieceStart(i), map.PieceSection(i)));
            }
            return list;
        }
        // The whole recording, cut wherever the speed, a freeze or the kept footage changes. Music plays over
        // the kept footage, from where it lands in the finished video.
        var cuts = new SortedSet<double> { 0, media.Duration };
        foreach (var r in Timeline.SlowRegions) { cuts.Add(r.Start); cuts.Add(r.End); }
        foreach (var f in Timeline.Freezes) cuts.Add(f.At);
        foreach (var p in map.Pieces) if (!p.Freeze) { cuts.Add(p.Start); cuts.Add(p.End); }
        var points = cuts.Where(t => t >= 0 && t <= media.Duration).ToList();
        (double SoundAt, int Section) Kept(double from, double inside, bool freeze)
        {
            for (int i = 0; i < map.Pieces.Count; i++)
            {
                var p = map.Pieces[i];
                if (freeze ? p.Freeze && Math.Abs(p.Start - from) < 1e-9 : !p.Freeze && inside >= p.Start && inside < p.End)
                    return (map.PieceStart(i) + (freeze ? 0 : (from - p.Start) / p.Speed), map.PieceSection(i));
            }
            return (double.NaN, -1);
        }
        for (int k = 0; k + 1 < points.Count; k++)
        {
            double a = points[k], b = points[k + 1];
            if (b - a < 1e-6) continue;
            foreach (var f in Timeline.Freezes.Where(f => Math.Abs(f.At - a) < 1e-9))
            {
                var (heldAt, heldSection) = Kept(a, a, freeze: true);
                list.Add(new PlayPiece(a, a, 1, f.Seconds, heldAt, heldSection));
            }
            var (soundAt, section) = Kept(a, (a + b) / 2, freeze: false);
            list.Add(new PlayPiece(a, b, RegionSpeedAt((a + b) / 2), 0, soundAt, section));
        }
        return list;
    }
    // Music and sound files where the export puts them.
    private SoundClip[] TimelineSounds(SequenceMap map) => Timeline.Sounds.Where(s => s.End > s.Start)
        .Select(s => new SoundClip(s.Path, map.ToOutput(s.Start) + Math.Max(0, s.Hold), s.Length, s.Offset, Math.Clamp(s.Speed, SoundItem.MinSpeed, SoundItem.MaxSpeed), s.KeepPitch, s.Volume, s.FadeIn, s.FadeOut, s.Duck, s.DuckLevel))
        .ToArray();
    private string PositionText() => Timeline.Finished
        ? $"{KeepSection.TimeText(Timeline.PositionView)} / {KeepSection.TimeText(Timeline.Map.Total)}"
        : $"{KeepSection.TimeText(playhead)} / {KeepSection.TimeText(media.Duration)}";
}
