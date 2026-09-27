using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FFmpeg.AutoGen;

namespace Flashback;

// A video shown over the clip in the preview (picture-in-picture), decoded by FFmpeg, so it plays whatever
// the export can (AV1, VP9, HEVC and the rest, which the Windows player often can't without extensions).
// Just the picture: its sound is mixed into the FFmpeg player's timeline like music. A decoding thread
// keeps the frame for the moment asked for (Show) in a bitmap the overlay draws; while playing it moves on
// at the rate given between the editor's updates. Pictures are kept to at most 1280 wide, plenty for an inset.
internal sealed unsafe class FfmpegInsetVideo : IDisposable
{
    private const int MaxWidth = 1280;
    private readonly FfmpegVideoReader reader;
    private readonly Dispatcher dispatcher;
    private readonly WriteableBitmap bitmap;
    private readonly int width, height;
    private readonly byte[] pixels;
    private readonly object pixelLock = new();
    private SwsContext* scale;
    private readonly Thread thread;
    private readonly AutoResetEvent wake = new(false);
    private volatile bool stop;
    private int copyScheduled;
    // What to show: the video's own time, whether it's playing and how fast, and when that was set.
    private double wantAt; private bool wantPlaying; private double wantRate = 1; private long wantSince;
    private readonly object wantLock = new();
    internal ImageSource Picture => bitmap;

    internal FfmpegInsetVideo(string path, Dispatcher dispatcher)
    {
        this.dispatcher = dispatcher;
        reader = new FfmpegVideoReader(path);
        double shrink = Math.Min(1, MaxWidth / (double)Math.Max(1, reader.Width));
        width = Math.Max(2, (int)Math.Round(reader.Width * shrink)); height = Math.Max(2, (int)Math.Round(reader.Height * shrink));
        bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        pixels = new byte[width * height * 4];
        thread = new Thread(Loop) { IsBackground = true, Name = "FFmpeg inset video" };
        thread.Start();
    }
    // The moment of the video to show (seconds into the file), and whether it's playing on from there and how fast.
    internal void Show(double seconds, bool playing, double rate)
    {
        lock (wantLock) { wantAt = Math.Max(0, seconds); wantPlaying = playing && rate > 0; wantRate = rate; wantSince = Stopwatch.GetTimestamp(); }
        wake.Set();
    }
    private double Wanted(out bool playing)
    {
        lock (wantLock)
        {
            playing = wantPlaying;
            return wantPlaying ? wantAt + Stopwatch.GetElapsedTime(wantSince).TotalSeconds * wantRate : wantAt;
        }
    }
    private void Loop()
    {
        double shown = double.NaN;
        try
        {
            while (!stop)
            {
                double want = Math.Min(Wanted(out bool playing), Math.Max(0, reader.Duration - .001));
                double half = .5 / reader.FrameRate;
                bool due = double.IsNaN(shown) || want < shown - half || want >= shown + half;
                if (due)
                {
                    // Close ahead: decode on to it; otherwise (back, or far ahead) the reader seeks exactly.
                    if (!double.IsNaN(shown) && want > shown && want - shown < 1)
                    {
                        bool any = false;
                        while (reader.FrameTime + 1 / reader.FrameRate <= want + half && reader.Next()) any = true;
                        if (any) Publish();
                    }
                    else if (reader.Seek(want)) Publish();
                    shown = reader.FrameTime >= 0 ? reader.FrameTime : want;
                    // (At the end it stays on the last frame.)
                    if (want >= reader.Duration - 1 / reader.FrameRate) shown = want;
                }
                wake.WaitOne(playing ? 4 : 250);
            }
        }
        catch { }
    }
    // The reader's frame into the bitmap (on the UI thread, the latest only).
    private void Publish()
    {
        var frame = reader.Frame;
        if (frame == null || frame->width <= 0) return;
        lock (pixelLock)
        {
            scale = ffmpeg.sws_getCachedContext(scale, frame->width, frame->height, (AVPixelFormat)frame->format, width, height, AVPixelFormat.AV_PIX_FMT_BGRA, (int)SwsFlags.SWS_BILINEAR, null, null, null);
            fixed (byte* p = pixels)
            {
                var destination = new byte_ptrArray4(); destination[0] = p;
                var stride = new int_array4(); stride[0] = width * 4;
                ffmpeg.sws_scale(scale, frame->data, frame->linesize, 0, frame->height, destination, stride);
            }
        }
        if (Interlocked.Exchange(ref copyScheduled, 1) == 1) return;
        dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref copyScheduled, 0);
            lock (pixelLock) bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        }, DispatcherPriority.Render);
    }
    public void Dispose()
    {
        stop = true; wake.Set();
        thread.Join(1000);
        reader.Dispose();
        lock (pixelLock) if (scale != null) { ffmpeg.sws_freeContext(scale); scale = null; }
        wake.Dispose();
    }
}
