using System;
using System.Collections.Generic;

namespace Flashback;

// The preview's speed at each moment of the clip: its speed (1 is normal) times each speed part's own.
internal sealed record SpeedMap(double Rate, IReadOnlyList<SpeedRegion> Parts, double Duration)
{
    private const double Nudge = .5 / FfmpegAudioMixer.Rate;
    internal double SpeedAt(double at)
    {
        at += Nudge;
        foreach (var p in Parts) if (at >= p.Start && at < p.End) return Math.Clamp(Rate * p.Speed, .1, 4);
        return Rate;
    }
    // The next speed part edge after a moment (the clip's end when there's none).
    internal double NextEdge(double at)
    {
        at += Nudge;
        double edge = Duration > 0 ? Duration : double.PositiveInfinity;
        foreach (var p in Parts) { if (p.Start > at) edge = Math.Min(edge, p.Start); else if (p.End > at) edge = Math.Min(edge, p.End); }
        return edge;
    }
    // Where `seconds` of playing on from `from` reaches, through the speed parts.
    internal double Advance(double from, double seconds)
    {
        double at = from;
        for (int guard = 0; seconds > 1e-9 && guard < 10000; guard++)
        {
            double speed = SpeedAt(at), edge = NextEdge(at), reach = at + seconds * speed;
            if (reach <= edge) return reach;
            seconds -= (edge - at) / speed; at = edge;
        }
        return at;
    }
}

// The FFmpeg player's sound on its way to the speakers, and the clock it keeps. It plays the mix on from a
// moment, through the speed parts (switching speed exactly at each edge), and remembers where in the output
// each stretch of the clip starts, so the clock can tell from what's been played where the clip is.
// Everything but MediaAt runs under the player's sound lock.
internal sealed class FfmpegSoundStream
{
    private readonly FfmpegAudioMixer mixer;
    private readonly FfmpegTempo tempo;
    // Each stretch: the output sample frame it starts at, the moment of the clip it begins with, its speed.
    private readonly record struct Run(long Frame, double Media, double Speed);
    private readonly List<Run> runs = new();
    private readonly object runLock = new();
    private SpeedMap map;
    private double stretchEnd = double.PositiveInfinity;
    // Sample frames given to the output so far (silence included).
    internal long Submitted { get; private set; }
    // Playing the clip's sound (false: silence).
    internal bool Feeding { get; private set; }

    internal FfmpegSoundStream(FfmpegAudioMixer mixer, FfmpegTempo tempo, SpeedMap map) { this.mixer = mixer; this.tempo = tempo; this.map = map; }

    // A new output starts counting from zero.
    internal void NewOutput() { Submitted = 0; Feeding = false; lock (runLock) runs.Clear(); }
    // Plays on from a moment, after what's already on its way to the speakers.
    internal void Restart(double at, SpeedMap speeds)
    {
        map = speeds;
        mixer.Seek(at); tempo.SetSpeed(map.SpeedAt(at)); stretchEnd = map.NextEdge(at); Feeding = true;
        lock (runLock) { runs.Clear(); runs.Add(new Run(Submitted, at, tempo.Speed)); }
    }
    // The speed changed: what's already made plays out at the old speed, then the new one from where it got to.
    internal void Respeed(SpeedMap speeds)
    {
        map = speeds;
        if (!Feeding) return;
        tempo.Finish(); NextStretch(Submitted);
    }
    internal void Stop() => Feeding = false;
    private void NextStretch(long frame)
    {
        double at = mixer.Position;
        long starts = frame + tempo.PendingFrames;
        tempo.Start(map.SpeedAt(at)); stretchEnd = map.NextEdge(at);
        lock (runLock) runs.Add(new Run(starts, at, tempo.Speed));
    }
    // The next `frames` of sound (stereo) into `buffer`; returns how many are the clip's (the rest is left for
    // silence: paused, or past the end).
    internal int Read(float[] buffer, int frames)
    {
        int got = 0;
        if (Feeding)
            try
            {
                const double sample = 1.0 / FfmpegAudioMixer.Rate;
                for (int guard = 0; got < frames && guard < 64; guard++)
                {
                    got += tempo.Read(buffer, got, frames - got, stretchEnd);
                    // Short: the clip ended, or this stretch did (a speed part's edge) and the next begins.
                    if (got >= frames || (map.Duration > 0 && mixer.Position >= map.Duration - sample) || mixer.Position < stretchEnd - sample) break;
                    NextStretch(Submitted + got);
                }
            }
            catch { Feeding = false; Submitted += frames; throw; }
        Submitted += frames;
        return got;
    }
    // Where the clip is once the speakers have played `played` sample frames of this output; null before
    // anything was started. Before a restart's sound reaches the speakers, it's the moment it starts from.
    internal double? MediaAt(double played)
    {
        lock (runLock)
        {
            if (runs.Count == 0) return null;
            int i = runs.Count - 1;
            while (i > 0 && runs[i].Frame > played) i--;
            var r = runs[i];
            if (played < r.Frame) return r.Media;
            if (i > 0) runs.RemoveRange(0, i);
            return r.Media + (played - r.Frame) * r.Speed / FfmpegAudioMixer.Rate;
        }
    }
}
