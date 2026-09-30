using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Media;
using System.Windows.Threading;
using FFmpeg.AutoGen;

namespace Flashback;

// A video shown over the clip in the preview (picture-in-picture), decoded by FFmpeg, so it plays whatever
// the export can (AV1, VP9, HEVC and the rest, which the Windows player often can't without extensions).
// Just the picture: its sound is mixed into the FFmpeg player's timeline like music.
// A thread of its own opens the file (so a large one doesn't hold up the editor) and keeps the frame for the
// moment asked for (Show); while playing it moves on at the rate given between the editor's updates.
// Like the preview, it decodes on the graphics card where it can, and an FfmpegPresenter of its own turns
// each frame into a picture there, scaled to about the size it's drawn at (DrawnWidth), so a frame never
// comes back to the CPU. Videos the graphics card can't decode are decoded and scaled on the CPU instead.
internal sealed unsafe class FfmpegInsetVideo : IDisposable
{
    private const int MinWidth = 160, MaxWidth = 2560;
    // Frames held beyond the decoder's own: the reader's current and next, one waiting for the screen and
    // the one on it (the preview player holds many more).
    private const int ExtraFrames = 4;
    private readonly string path;
    private readonly Dispatcher dispatcher;
    private volatile FfmpegVideoReader? reader;
    private volatile FfmpegPresenter? presenter;
    private int videoWidth, videoHeight; private double frameRate = 30;
    private readonly Thread thread;
    private readonly AutoResetEvent wake = new(false);
    private volatile bool stop;
    private bool disposed;
    // The latest decoded frame waiting for the UI thread (a reference of its own), and the one on screen,
    // kept so the picture can be redone at a new size while paused.
    private IntPtr waiting, onScreen;
    private int presentScheduled;
    private int pictureWidth, drawnWidth = 1280;
    // What to show: the video's own time, whether it's playing and how fast, and when that was set.
    private double wantAt; private bool wantPlaying; private double wantRate = 1; private long wantSince;
    private readonly object wantLock = new();

    // The picture, once the first frame is on it (null until then).
    internal ImageSource? Picture => HasPicture ? presenter?.Source : null;
    internal bool HasPicture { get; private set; }
    // The moment of the video decoded last (seconds into the file; NaN before the first), for the tests.
    internal double ShownTime { get; private set; } = double.NaN;
    internal bool OnGraphicsCard => presenter?.OnGraphicsCard == true && reader?.Hardware == true;
    // Raised on the UI thread when the picture first appears or becomes a new one (a new size, or the
    // graphics card handing over to the CPU), so the overlay draws it.
    internal event Action? PictureChanged;

    internal FfmpegInsetVideo(string path, Dispatcher dispatcher)
    {
        if (!FfmpegLibrary.Available) throw new InvalidOperationException("FFmpeg's libraries aren't available. " + FfmpegLibrary.Error);
        this.path = path; this.dispatcher = dispatcher;
        thread = new Thread(Loop) { IsBackground = true, Name = "FFmpeg inset video" };
        thread.Start();
    }
    // The moment of the video to show (seconds into the file), and whether it's playing on from there and how fast.
    internal void Show(double seconds, bool playing, double rate)
    {
        lock (wantLock) { wantAt = Math.Max(0, seconds); wantPlaying = playing && rate > 0; wantRate = rate; wantSince = Stopwatch.GetTimestamp(); }
        wake.Set();
    }
    // How wide the video is drawn on screen, in pixels (UI thread). The picture follows in steps of a
    // quarter of the video's width, so it's sharp without redoing it for every small change.
    internal double DrawnWidth
    {
        set
        {
            drawnWidth = (int)Math.Ceiling(Math.Max(1, value));
            if (HasPicture && videoWidth > 0 && WidthFor(drawnWidth) != pictureWidth) SchedulePresent();
        }
    }
    private int WidthFor(int drawn)
    {
        int step = Math.Max(MinWidth, videoWidth / 4), most = Math.Min(videoWidth, MaxWidth);
        int w = (int)Math.Ceiling(drawn / (double)step) * step;
        return Math.Max(2, Math.Min(Math.Max(w, Math.Min(MinWidth, most)), most) & ~1);
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
        ThreadNames.Name("FFmpeg inset video");
        double shown = double.NaN;
        try
        {
            // The graphics-card devices and the file are opened here, off the UI thread.
            var p = new FfmpegPresenter(graphicsCard: true);
            presenter = p;
            if (stop) return;
            var r = new FfmpegVideoReader(path, p.OnGraphicsCard ? p.HardwareDevice : null, ExtraFrames);
            videoWidth = r.Width; videoHeight = r.Height; frameRate = r.FrameRate > 0 ? r.FrameRate : 30;
            reader = r;
            if (stop) return;
            while (!stop)
            {
                double want = Math.Min(Wanted(out bool playing), Math.Max(0, r.Duration - .001));
                // The frame on screen shows from its time until the next's; a moment outside that needs another.
                double slack = r.Slack, span = 1 / r.FrameRate;
                bool due = double.IsNaN(shown) || want < shown - slack || want >= shown + span - slack;
                if (due)
                {
                    // Close ahead: decode on to it; otherwise (back, or far ahead) the reader seeks exactly.
                    if (!double.IsNaN(shown) && want > shown && want - shown < 1)
                    {
                        bool any = false;
                        while (r.FrameTime + span <= want + slack && r.Next()) any = true;
                        if (any) Publish(r);
                    }
                    else if (r.Seek(want, () => stop)) Publish(r);
                    shown = r.FrameTime >= 0 ? r.FrameTime : want;
                    // (At the end it stays on the last frame.)
                    if (want >= r.Duration - 1 / r.FrameRate) shown = want;
                    ShownTime = shown;
                }
                wake.WaitOne(playing ? 4 : 250);
            }
        }
        catch { }
    }
    // The reader's frame goes to the UI thread to be shown (only the latest waits).
    private void Publish(FfmpegVideoReader r)
    {
        var frame = r.Frame;
        if (frame == null || frame->width <= 0) return;
        var copy = (IntPtr)ffmpeg.av_frame_clone(frame);
        Free(Interlocked.Exchange(ref waiting, copy));
        SchedulePresent();
    }
    private void SchedulePresent()
    {
        if (Interlocked.Exchange(ref presentScheduled, 1) == 1) return;
        dispatcher.BeginInvoke(Present, DispatcherPriority.Render);
    }
    // On the UI thread: the waiting frame (or the one on screen again, at a new size) onto the picture.
    private void Present()
    {
        Interlocked.Exchange(ref presentScheduled, 0);
        if (disposed || presenter is not { } p || videoWidth <= 0) return;
        var next = Interlocked.Exchange(ref waiting, IntPtr.Zero);
        if (next != IntPtr.Zero) { Free(onScreen); onScreen = next; }
        if (onScreen == IntPtr.Zero) return;
        bool changed = false;
        // A video the graphics card can't decode comes as CPU frames: a CPU presenter shows those. (The decoder
        // keeps its own hold on the graphics-card device, so letting go of the old presenter is safe.)
        if (p.OnGraphicsCard && ((AVFrame*)onScreen)->format != (int)AVPixelFormat.AV_PIX_FMT_D3D11)
        {
            var old = p; presenter = p = new FfmpegPresenter(graphicsCard: false); old.Dispose();
            pictureWidth = 0;
        }
        int width = WidthFor(drawnWidth);
        if (width != pictureWidth)
        {
            if (pictureWidth == 0) p.SourceChanged += () => PictureChanged?.Invoke();
            int height = Math.Max(2, (int)Math.Round(width * (double)videoHeight / Math.Max(1, videoWidth)) & ~1);
            p.Prepare(videoWidth, videoHeight, frameRate, width, height);
            pictureWidth = width; changed = true;
        }
        p.Present((AVFrame*)onScreen);
        if (!HasPicture) { HasPicture = true; changed = true; }
        if (changed) PictureChanged?.Invoke();
    }
    private static void Free(IntPtr frame)
    {
        if (frame == IntPtr.Zero) return;
        var f = (AVFrame*)frame; ffmpeg.av_frame_free(&f);
    }
    // (UI thread.)
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; stop = true; wake.Set();
        thread.Join(1000);
        Free(Interlocked.Exchange(ref waiting, IntPtr.Zero)); Free(onScreen); onScreen = IntPtr.Zero;
        reader?.Dispose(); presenter?.Dispose();
        wake.Dispose();
    }
}
