using System;
using System.Collections.Generic;
using System.Linq;
using FFmpeg.AutoGen;

namespace Flashback;

// The FFmpeg player's pictures along the editor's timeline (its decoding thread's side): the frame showing
// at a moment of the timeline, and while playing each frame after it with the moment it's due, through
// pieces at their speeds, freeze frames and jumps to other sections. A jump is a seek, done as soon as the
// previous piece's frames are decoded, so it's ready before it's due.
internal sealed unsafe class FfmpegSequenceFrames
{
    private readonly FfmpegVideoReader reader;
    // The last frames decoded, so stepping back a little (or onto the same frame) doesn't go back to the keyframe.
    private readonly FfmpegRecentFrames recent;
    private PlaySequence decoding = PlaySequence.Whole(1);
    private int piece;
    // Every piece's frames are queued.
    internal bool Done => piece >= decoding.Pieces.Count;

    internal FfmpegSequenceFrames(FfmpegVideoReader reader, FfmpegRecentFrames recent) { this.reader = reader; this.recent = recent; }

    // A seek. Paused: returns the frame showing there (the caller's to free), or zero. Playing: queues it and
    // carries on from there with Step. `queue(due, frame)` takes a frame (false: a newer seek came, so it's
    // freed here); `cancel` gives the seek up.
    internal IntPtr Seek(PlaySequence sequence, double target, bool playing, Func<bool>? cancel, Func<double, IntPtr, bool> queue)
    {
        decoding = sequence; piece = decoding.Pieces.Count;
        if (decoding.Pieces.Count == 0) return IntPtr.Zero;
        int i = decoding.PieceAt(target);
        var p = decoding.Pieces[i];
        double source = p.Freeze ? p.Start : p.Start + Math.Max(0, target - decoding.StartOf(i)) * p.Speed;
        var frames = FramesAt(source, cancel);
        if (frames == null) return IntPtr.Zero;
        if (!playing) { foreach (var f in frames.Skip(1)) Free(f.Frame); return frames[0].Frame; }
        piece = i;
        Queue(frames, i, target, queue);
        if (p.Freeze) Enter(i + 1, queue);
        return IntPtr.Zero;
    }
    // Decodes the next frame of the piece playing; past its end, on to the next piece.
    internal void Step(Func<double, IntPtr, bool> queue)
    {
        if (Done) return;
        var p = decoding.Pieces[piece];
        if (p.Freeze || !reader.Next()) { Enter(piece + 1, queue); return; }
        recent.Add(reader.FrameTime, reader.Frame);
        if (reader.FrameTime >= p.End - .5 / reader.FrameRate) { Enter(piece + 1, queue); return; }
        Queue(new List<(double, IntPtr)> { (reader.FrameTime, (IntPtr)ffmpeg.av_frame_clone(reader.Frame)) }, piece, double.NaN, queue);
    }
    // Starts piece i: the frame showing at its start (decoded already when it follows straight on, a seek when
    // it's another section), then its frames; a freeze just the one, then on.
    private void Enter(int i, Func<double, IntPtr, bool> queue)
    {
        for (; i < decoding.Pieces.Count; i++)
        {
            piece = i;
            var p = decoding.Pieces[i];
            var frames = FramesAt(p.Start, null);
            if (frames == null) { piece = decoding.Pieces.Count; return; }
            Queue(frames, i, decoding.StartOf(i), queue);
            if (!p.Freeze) return;
        }
        piece = decoding.Pieces.Count;
    }
    // The frame showing at a moment of the recording and any decoded after it (from the recent ones, or a seek).
    private List<(double Time, IntPtr Frame)>? FramesAt(double source, Func<bool>? cancel)
    {
        if (recent.From(source, reader.FrameRate) is { } kept) return kept;
        recent.Clear();
        if (!reader.Seek(source, cancel)) return null;
        recent.Add(reader.FrameTime, reader.Frame);
        return new List<(double, IntPtr)> { (reader.FrameTime, (IntPtr)ffmpeg.av_frame_clone(reader.Frame)) };
    }
    // Queues frames of piece i: the first due at `first` (NaN: where its time falls in the piece), the rest
    // where their times fall; ones past the piece (or anything after a freeze's first) are let go.
    private void Queue(List<(double Time, IntPtr Frame)> frames, int i, double first, Func<double, IntPtr, bool> queue)
    {
        var p = decoding.Pieces[i]; double from = decoding.StartOf(i), half = .5 / reader.FrameRate;
        for (int k = 0; k < frames.Count; k++)
        {
            var (time, frame) = frames[k];
            bool keep = k == 0 || (!p.Freeze && time < p.End - half);
            double due = k == 0 && !double.IsNaN(first) ? first : from + Math.Max(0, time - p.Start) / p.Speed;
            if (!keep || !queue(due, frame)) Free(frame);
        }
    }
    private static void Free(IntPtr frame) { if (frame == IntPtr.Zero) return; var f = (AVFrame*)frame; ffmpeg.av_frame_free(&f); }
}
