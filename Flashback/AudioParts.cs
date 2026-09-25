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
