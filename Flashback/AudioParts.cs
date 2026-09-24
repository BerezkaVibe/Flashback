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
}
