using System;
using System.Collections.Generic;
using FFmpeg.AutoGen;

namespace Flashback;

// The last frames a reader decoded, in order, ending with the one it's on. A seek back a frame or two (the
// editor stopping at a freeze frame just after playing past it, say) is answered from here, instead of going
// back to the keyframe and decoding up to it again, which stalled the picture for up to a few hundred ms.
// Frames kept are references (av_frame_clone), so frames also queued or on screen aren't kept twice.
internal sealed unsafe class FfmpegRecentFrames : IDisposable
{
    private readonly List<(double Time, IntPtr Frame)> frames = new();
    private readonly int capacity;
    internal FfmpegRecentFrames(int capacity) => this.capacity = capacity;
    internal int Count => frames.Count;

    // The reader's newest frame; it must follow on from the last one added (Clear after a seek).
    internal void Add(double time, AVFrame* frame)
    {
        frames.Add((time, (IntPtr)ffmpeg.av_frame_clone(frame)));
        if (frames.Count > capacity) { Free(frames[0].Frame); frames.RemoveAt(0); }
    }
    internal void Clear() { foreach (var f in frames) Free(f.Frame); frames.Clear(); }
    // The frame showing at a moment (the last one starting at or before it, give or take half a frame, as
    // FfmpegVideoReader.Seek picks) and those after it, as new references for the caller to free; null when
    // it isn't here.
    internal List<(double Time, IntPtr Frame)>? From(double seconds, double frameRate)
    {
        double half = .5 / frameRate;
        if (frames.Count == 0 || seconds < frames[0].Time - half || seconds >= frames[^1].Time + half) return null;
        int at = frames.FindLastIndex(f => f.Time <= seconds + half);
        if (at < 0) return null;
        var list = new List<(double, IntPtr)>(frames.Count - at);
        for (int i = at; i < frames.Count; i++) list.Add((frames[i].Time, (IntPtr)ffmpeg.av_frame_clone((AVFrame*)frames[i].Frame)));
        return list;
    }
    private static void Free(IntPtr frame) { if (frame == IntPtr.Zero) return; var f = (AVFrame*)frame; ffmpeg.av_frame_free(&f); }
    public void Dispose() => Clear();
}
