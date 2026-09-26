using System;
using System.Collections.Generic;
using System.Linq;

namespace Flashback;

// A stretch of what the preview plays, in play order: footage from Start to End of the recording at a speed
// (its speed part's), or a freeze frame holding the moment Start for Hold seconds. SoundAt is where it
// begins on the finished video's timeline, which music and sound files are placed on; NaN for footage that
// isn't kept (the whole recording plays through it, but no sound file plays over it). Section: the kept
// range it comes from (-1 when it doesn't matter).
internal readonly record struct PlayPiece(double Start, double End, double Speed, double Hold, double SoundAt, int Section)
{
    internal bool Freeze => Hold > 0;
    internal double Seconds => Freeze ? Hold : (End - Start) / Speed;
}

// A music or sound file on the finished video's timeline, as the export mixes it: from At for Length seconds,
// starting Offset seconds into the file, at its own speed (pitch kept or not), at its volume with its fades,
// lowering the clip's own sound to DuckLevel while it plays when Duck is on.
internal sealed record SoundClip(string Path, double At, double Length, double Offset = 0, double Speed = 1, bool KeepPitch = true, double Volume = 1, double FadeIn = 0, double FadeOut = 0, bool Duck = false, double DuckLevel = .35);

// The preview's timeline: its pieces laid end to end, in seconds of playing at 1× ("sequence time").
internal sealed class PlaySequence
{
    internal IReadOnlyList<PlayPiece> Pieces { get; }
    private readonly long[] starts; // where each piece begins, in sample frames at 48 kHz (exact, so sound and clock agree)
    internal long TotalFrames { get; }
    internal double Total => TotalFrames / (double)FfmpegAudioMixer.Rate;

    internal PlaySequence(IReadOnlyList<PlayPiece> pieces)
    {
        Pieces = pieces.Where(p => p.Freeze || (p.End > p.Start && p.Speed > 0)).ToArray();
        starts = new long[Pieces.Count + 1];
        double at = 0;
        for (int i = 0; i < Pieces.Count; i++) { starts[i] = (long)Math.Round(at * FfmpegAudioMixer.Rate); at += Pieces[i].Seconds; }
        starts[^1] = TotalFrames = (long)Math.Round(at * FfmpegAudioMixer.Rate);
    }
    // The whole recording at normal speed.
    internal static PlaySequence Whole(double duration) => new(new[] { new PlayPiece(0, Math.Max(.001, duration), 1, 0, 0, -1) });

    internal long StartFrame(int piece) => starts[piece];
    internal double StartOf(int piece) => starts[piece] / (double)FfmpegAudioMixer.Rate;
    internal double EndOf(int piece) => starts[piece + 1] / (double)FfmpegAudioMixer.Rate;
    // The piece playing at a moment (the last one at the very end).
    internal int PieceAt(double seconds)
    {
        if (Pieces.Count == 0) return -1;
        long frame = (long)Math.Round(seconds * FfmpegAudioMixer.Rate);
        int lo = 0, hi = Pieces.Count - 1;
        while (lo < hi) { int mid = (lo + hi + 1) / 2; if (starts[mid] <= frame) lo = mid; else hi = mid - 1; }
        return lo;
    }
    // What shows at a moment: the recording's moment, how far into a freeze's hold (a hold just finished
    // counts as all of it, as the editor shows it), the section, and whether a hold is playing.
    internal (double Source, double Hold, int Section, bool Holding) Where(double seconds)
    {
        int i = PieceAt(seconds);
        if (i < 0) return (0, 0, -1, false);
        var p = Pieces[i]; double into = Math.Clamp(seconds - StartOf(i), 0, p.Seconds);
        if (p.Freeze) return (p.Start, into, p.Section, into < p.Hold - 1e-9);
        if (into < 1e-9 && i > 0 && Pieces[i - 1].Freeze && Math.Abs(Pieces[i - 1].Start - p.Start) < 1e-9) return (p.Start, Pieces[i - 1].Hold, p.Section, false);
        return (Math.Min(p.End, p.Start + into * p.Speed), 0, p.Section, false);
    }
    // Where a moment of the recording plays (in `section` when it's 0 or more): into a freeze's hold when
    // hold is more than 0, at the start of the hold when a freeze is exactly there, otherwise in the footage.
    // A moment that doesn't play goes to the start of the next piece that does (or the end).
    internal double TimeOf(double source, double hold = 0, int section = -1)
    {
        double next = double.MaxValue; int nextPiece = -1;
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < Pieces.Count; i++)
            {
                var p = Pieces[i];
                if (pass == 0 && section >= 0 && p.Section != section) continue;
                if (p.Freeze) { if (Math.Abs(p.Start - source) < 1e-9 && hold < p.Hold - 1e-9) return StartOf(i) + Math.Max(0, hold); continue; }
                if (source >= p.Start - 1e-9 && source < p.End - 1e-9) return StartOf(i) + Math.Max(0, source - p.Start) / p.Speed;
                if (p.Start > source && p.Start < next) { next = p.Start; nextPiece = i; }
            }
            if (section < 0) break;
        }
        return nextPiece >= 0 ? StartOf(nextPiece) : Total;
    }
}

// The preview's sound along its timeline, at 1× (the player's own speed is applied after): the clip's sound
// through each piece at its speed, silent in holds, with music and sound files mixed over it where they sit
// on the finished video, as the export mixes them.
internal sealed class SequenceAudio : IFfmpegSound, IDisposable
{
    private const int Rate = FfmpegAudioMixer.Rate, Channels = FfmpegAudioMixer.Channels;
    private readonly FfmpegAudioMixer? clip;
    private readonly FfmpegTempo? clipTempo;
    private PlaySequence sequence;
    private int piece;
    private long frame; // sample frames into the sequence
    private float[] scratch = new float[4096 * Channels], duck = new float[4096];
    private readonly List<Layer> layers = new();
    // Opens a sound file (tests hand in their own).
    private readonly Func<string, FfmpegAudioMixer> open;

    private sealed class Layer : IDisposable
    {
        internal SoundClip Clip = null!;
        internal FfmpegAudioMixer? Mixer;
        internal FfmpegTempo? Tempo;
        internal double Next = double.NaN; // the finished-video moment its next sample belongs to (NaN: must seek)
        public void Dispose() { Tempo?.Dispose(); Mixer?.Dispose(); }
    }

    internal SequenceAudio(FfmpegAudioMixer? clip, PlaySequence sequence, Func<string, FfmpegAudioMixer>? open = null)
    {
        this.clip = clip; this.sequence = sequence;
        this.open = open ?? (path => new FfmpegAudioMixer(path));
        if (clip != null) clipTempo = new FfmpegTempo(clip);
        Seek(0);
    }
    public double Position => frame / (double)Rate;
    internal PlaySequence Sequence => sequence;
    internal int LayerCount => layers.Count;

    // A new timeline (the sound carries on from the same sequence moment; the caller seeks if it moved).
    internal void SetSequence(PlaySequence next) { sequence = next; Seek(Math.Min(Position, next.Total)); }
    // The music and sound files. Ones already playing carry on where they are (a volume or fade change doesn't
    // interrupt them); a file that can't be opened is left out.
    internal void SetSounds(IReadOnlyList<SoundClip> sounds)
    {
        var old = layers.ToList(); layers.Clear();
        foreach (var s in sounds)
        {
            if (s.Length <= .01) continue;
            var same = old.FirstOrDefault(l => l.Clip.Path == s.Path && Math.Abs(l.Clip.At - s.At) < 1e-6 && Math.Abs(l.Clip.Offset - s.Offset) < 1e-6 && Math.Abs(l.Clip.Speed - s.Speed) < 1e-6 && l.Clip.KeepPitch == s.KeepPitch);
            if (same != null) { old.Remove(same); same.Clip = s; layers.Add(same); continue; }
            var layer = new Layer { Clip = s };
            try
            {
                layer.Mixer = open(s.Path);
                if (layer.Mixer.TrackCount == 0) { layer.Dispose(); continue; }
                layer.Tempo = new FfmpegTempo(layer.Mixer, s.KeepPitch);
                layers.Add(layer);
            }
            catch { layer.Dispose(); }
        }
        foreach (var l in old) l.Dispose();
    }
    internal void Seek(double seconds)
    {
        frame = Math.Clamp((long)Math.Round(seconds * Rate), 0, sequence.TotalFrames);
        piece = Math.Max(0, sequence.PieceAt(Position));
        EnterPiece(seek: true);
        foreach (var l in layers) l.Next = double.NaN;
    }
    // Sets the clip's sound up for the current piece: from the moment it starts at (or where the sequence is in
    // it), at its speed. Footage following straight on from the last piece's carries on without a seek.
    private void EnterPiece(bool seek)
    {
        if (clip == null || piece >= sequence.Pieces.Count) return;
        var p = sequence.Pieces[piece];
        if (p.Freeze) return;
        double at = p.Start + (Position - sequence.StartOf(piece)) * p.Speed;
        if (seek || Math.Abs(clip.Position - at) > 1.5 / Rate) clip.Seek(at);
        clipTempo!.SetSpeed(p.Speed);
    }

    public int Read(float[] buffer, int frames)
    {
        int written = 0;
        while (written < frames && piece < sequence.Pieces.Count)
        {
            var p = sequence.Pieces[piece];
            long end = sequence.StartFrame(piece + 1);
            int n = (int)Math.Min(frames - written, end - frame);
            if (n > 0)
            {
                // The clip's own sound: silent in a hold; otherwise exactly this piece's length of it. The speed
                // filter comes up a little short at the end of what it's given, so a piece at another speed is
                // given a moment more of the recording and cut to its length (as the export does).
                int got = p.Freeze || clipTempo == null ? 0 : clipTempo.Read(buffer, written, n, Math.Abs(p.Speed - 1) < 1e-9 ? p.End : p.End + .25);
                if (got < n) Array.Clear(buffer, (written + got) * Channels, (n - got) * Channels);
                if (!double.IsNaN(p.SoundAt)) MixLayers(buffer, written, n, p.SoundAt + (frame - sequence.StartFrame(piece)) / (double)Rate);
                written += n; frame += n;
            }
            if (frame >= end) { piece++; EnterPiece(seek: false); }
        }
        return written;
    }
    // Music and sound files over `n` frames from buffer[offset], the first on the finished video at `at`;
    // ducking sounds lower the clip's sound under them first.
    private void MixLayers(float[] buffer, int offset, int n, double at)
    {
        double to = at + n / (double)Rate;
        if (duck.Length < n) duck = new float[n * 2];
        bool ducked = false;
        foreach (var l in layers)
            if (l.Clip.Duck && l.Clip.At < to && l.Clip.At + l.Clip.Length > at)
            {
                if (!ducked) { Array.Fill(duck, 1f, 0, n); ducked = true; }
                int a = Math.Clamp((int)Math.Ceiling((l.Clip.At - at) * Rate - 1e-6), 0, n), b = Math.Clamp((int)Math.Ceiling((l.Clip.At + l.Clip.Length - at) * Rate - 1e-6), 0, n);
                for (int i = a; i < b; i++) duck[i] *= (float)l.Clip.DuckLevel;
            }
        if (ducked) for (int i = 0; i < n; i++) { buffer[(offset + i) * 2] *= duck[i]; buffer[(offset + i) * 2 + 1] *= duck[i]; }
        foreach (var l in layers)
        {
            var c = l.Clip;
            if (c.At >= to || c.At + c.Length <= at) { l.Next = double.NaN; continue; }
            int a = Math.Clamp((int)Math.Ceiling((c.At - at) * Rate - 1e-6), 0, n), b = Math.Clamp((int)Math.Ceiling((c.At + c.Length - at) * Rate - 1e-6), 0, n);
            if (b <= a) continue;
            double from = at + a / (double)Rate;
            // Joining partway (a seek, or the timeline jumped): from the right moment of the file.
            if (double.IsNaN(l.Next) || Math.Abs(l.Next - from) > 1.5 / Rate)
            {
                l.Mixer!.Seek(c.Offset + Math.Max(0, from - c.At) * c.Speed);
                l.Tempo!.SetSpeed(c.Speed);
            }
            int count = b - a;
            if (scratch.Length < count * Channels) scratch = new float[count * Channels * 2];
            int got = l.Tempo!.Read(scratch, 0, count, double.PositiveInfinity);
            for (int i = 0; i < got; i++)
            {
                double local = from + i / (double)Rate - c.At;
                double fade = 1;
                if (c.FadeIn > 0) fade = Math.Min(fade, local / c.FadeIn);
                if (c.FadeOut > 0) fade = Math.Min(fade, (c.Length - local) / c.FadeOut);
                float level = (float)(c.Volume * Math.Clamp(fade, 0, 1));
                buffer[(offset + a + i) * 2] += scratch[i * 2] * level;
                buffer[(offset + a + i) * 2 + 1] += scratch[i * 2 + 1] * level;
            }
            l.Next = from + count / (double)Rate;
        }
    }
    public void Dispose() { foreach (var l in layers) l.Dispose(); layers.Clear(); clipTempo?.Dispose(); }
}
