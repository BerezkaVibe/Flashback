namespace Flashback;

// A stretch of one audio lane played louder or quieter (Gain 1 leaves it as recorded, 0 mutes it,
// 2 doubles it). Lanes match the trimmer: the combined track, or desktop then microphone.
internal sealed record VolumeRegion(int Lane, double Start, double End, double Gain);

// A music or sound file laid over the clip. Start and End are where it plays on the timeline (source
// time); Offset is where in the file it starts. It is mixed into the finished export, so speed parts
// don't speed it up. Ducking lowers the clip's own sound while it plays. Row stacks sounds that overlap.
internal sealed record SoundItem(string Path, double Start, double End, double Offset = 0, double Volume = 1, double FadeIn = 0, double FadeOut = 0, bool Duck = false, double DuckLevel = .35, int Row = 0)
{
    internal double Length => End - Start;
    internal string Label => System.IO.Path.GetFileName(Path);
}
