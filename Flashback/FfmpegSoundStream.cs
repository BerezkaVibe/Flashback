using System;
using System.Collections.Generic;

namespace Flashback;

// The FFmpeg player's sound on its way to the speakers, and the clock it keeps. It plays the preview's
// timeline (SequenceAudio: pieces at their speeds, holds, music and sound files) at the preview's speed,
// and remembers where in the output each stretch of the timeline starts, so the clock can tell from what's
// been played where the timeline is.
// Everything but MediaAt runs under the player's sound lock.
internal sealed class FfmpegSoundStream
{
    private readonly SequenceAudio audio;
    private readonly FfmpegTempo tempo;
    // Each stretch: the output sample frame it starts at, the timeline moment it begins with, its speed.
    private readonly record struct Run(long Frame, double Media, double Speed);
    private readonly List<Run> runs = new();
    private readonly object runLock = new();
    private double rate = 1;
    // Sample frames given to the output so far (silence included).
    internal long Submitted { get; private set; }
    // Playing the timeline's sound (false: silence).
    internal bool Feeding { get; private set; }
    internal SequenceAudio Audio => audio;

    internal FfmpegSoundStream(SequenceAudio audio) { this.audio = audio; tempo = new FfmpegTempo(audio); }

    // A new output starts counting from zero.
    internal void NewOutput() { Submitted = 0; Feeding = false; lock (runLock) runs.Clear(); }
    // Plays on from a timeline moment at a speed, after what's already on its way to the speakers.
    internal void Restart(double at, double speed)
    {
        rate = speed;
        audio.Seek(at); tempo.SetSpeed(rate); Feeding = true;
        lock (runLock) { runs.Clear(); runs.Add(new Run(Submitted, audio.Position, tempo.Speed)); }
    }
    // The speed changed: what's already made plays out at the old speed, then the new one from where it got to.
    internal void Respeed(double speed)
    {
        rate = speed;
        if (!Feeding) return;
        tempo.Finish();
        long starts = Submitted + tempo.PendingFrames;
        double at = audio.Position;
        tempo.Start(rate);
        lock (runLock) runs.Add(new Run(starts, at, tempo.Speed));
    }
    internal void Stop() => Feeding = false;
    // The next `frames` of sound (stereo) into `buffer`; returns how many are the timeline's (the rest is left
    // for silence: paused, or past the end).
    internal int Read(float[] buffer, int frames)
    {
        int got = 0;
        try { if (Feeding) got = tempo.Read(buffer, 0, frames, double.PositiveInfinity); }
        catch { Feeding = false; Submitted += frames; throw; }
        Submitted += frames;
        return got;
    }
    // Where the timeline is once the speakers have played `played` sample frames of this output; null before
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
