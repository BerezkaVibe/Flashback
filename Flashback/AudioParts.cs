using System;
using System.Collections.Generic;
using System.Linq;

namespace Flashback;

// A stretch of one audio lane played louder or quieter (Gain 1 leaves it as recorded, 0 mutes it,
// 2 doubles it). Lanes match the trimmer: the combined track, or desktop then microphone.
internal sealed record VolumeRegion(int Lane, double Start, double End, double Gain);

// A music or sound file laid over the clip. Start is the moment of the recording it's anchored to (plus
// Hold seconds into a freeze's hold, when it starts during one), so it slides with the video when freezes,
// speed parts or sections change before it. End - Start is how long it plays, in real seconds. Offset is
// where in the file it starts. Speed plays the file faster or slower (KeepPitch keeps its pitch). It is
// mixed into the finished export; ducking lowers the clip's own sound while it plays. Row stacks sounds
// that overlap.
internal sealed record SoundItem(string Path, double Start, double End, double Offset = 0, double Volume = 1, double FadeIn = 0, double FadeOut = 0, bool Duck = false, double DuckLevel = .35, int Row = 0)
{
    public double Hold { get; init; }
    public double Speed { get; init; } = 1;
    public bool KeepPitch { get; init; } = true;
    internal double Length => End - Start;
    internal string Label => System.IO.Path.GetFileName(Path);
    internal const double MinSpeed = .25, MaxSpeed = 4;

    // A video's sound unlinked into a sound of its own, where it played: from the video's start for as long as
    // the video shows in the finished video, from the same place in the file and as loud. It plays at the speed
    // of the footage under the video, so it stays with the picture over a slowed or sped-up part (freezes'
    // holds don't count: the sound just carries on through them).
    internal static SoundItem? FromVideo(OverlayItem video, SequenceMap map, int row)
    {
        double length = map.Spans(video.From(), video.To()).Sum(s => s.To - s.From);
        if (length <= .01) return null;
        double footage = 0, played = 0;
        foreach (var p in map.Pieces.Where(p => !p.Freeze))
        {
            double a = Math.Max(video.Start, p.Start), b = Math.Min(video.End, p.End);
            if (b - a > 1e-9) { footage += b - a; played += (b - a) / p.Speed; }
        }
        double speed = played > 1e-9 ? Math.Clamp(footage / played, MinSpeed, MaxSpeed) : 1;
        speed = Math.Abs(speed - 1) < .02 ? 1 : Math.Round(speed, 2);
        return new SoundItem(video.VideoPath, video.Start, video.Start + length, video.VideoOffset, video.VideoVolume, Row: row) { Hold = video.StartHold, Speed = speed };
    }
}

// An app's own layer in a clip recorded with app layers: where it made sound, as bars named for the app.
internal static class AppLayer
{
    // Waveform peaks (square-rooted, one per 10 ms; see AudioWaveforms) above this are sound, about -50 dB.
    private const float Heard = .055f;
    // Gaps shorter than this join the bars either side; bars shorter than MinBar (clicks) are dropped.
    private const double Join = 1.5, MinBar = .15;
    // The stretches of the clip (seconds) where the layer has sound, split where its app changes and named
    // for the app then. Without peaks (waveforms off, or not read yet) each app's whole stretch is a bar.
    internal static List<(double From, double To, string App)> Bars(float[] peaks, IReadOnlyList<(string App, double From)> apps, double duration)
    {
        var runs = new List<(double From, double To)>();
        if (peaks.Length == 0) runs.Add((0, duration));
        else
        {
            int start = -1, last = -1;
            for (int i = 0; i < peaks.Length; i++)
            {
                if (peaks[i] < Heard) continue;
                if (start >= 0 && (i - last) / 100.0 > Join) { runs.Add((start / 100.0, (last + 1) / 100.0)); start = -1; }
                if (start < 0) start = i;
                last = i;
            }
            if (start >= 0) runs.Add((start / 100.0, (last + 1) / 100.0));
            runs.RemoveAll(r => r.To - r.From < MinBar);
        }
        var ordered = apps.OrderBy(a => a.From).ToList();
        var bars = new List<(double, double, string)>();
        foreach (var (from, to) in runs)
            for (int a = 0; a < ordered.Count; a++)
            {
                double a0 = a == 0 ? 0 : ordered[a].From, a1 = a + 1 < ordered.Count ? ordered[a + 1].From : double.MaxValue;
                double b0 = Math.Max(from, a0), b1 = Math.Min(Math.Min(to, a1), duration);
                if (b1 - b0 >= MinBar / 2) bars.Add((b0, b1, ordered[a].App));
            }
        return bars;
    }
}
