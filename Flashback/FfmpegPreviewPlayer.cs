using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FFmpeg.AutoGen;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Flashback;

// The FFmpeg preview player: FFmpeg's libraries in the app, decoding on the graphics card (FfmpegPresenter)
// and every audio track mixed in the app (FfmpegAudioMixer), played through WASAPI.
// - A decoding thread keeps a few frames ready ahead of the playhead, and jumps to the exact frame on a seek
//   (the latest seek wins, so scrubbing never queues up).
// - The sound is the clock while playing: what the speakers have actually played, so picture and sound
//   stay together at any speed. Without sound it's a stopwatch.
// - Each screen refresh shows the frame for that moment.
// It answers like the Windows player (Position, SpeedRatio, Volume, Opened and Ended) so the editor can use
// either; see PreviewPlayer.
internal sealed unsafe class FfmpegPreviewPlayer : IDisposable
{
    internal Image View { get; } = new() { Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
    internal event Action? Opened, Ended;
    internal event Action<Exception>? Failed;
    internal bool IsOpen { get; private set; }
    internal double Duration { get; private set; }
    internal int VideoWidth { get; private set; }
    internal int VideoHeight { get; private set; }
    internal string Report => $"{presenter?.Path ?? "closed"}; {(reader?.Hardware == true ? "decoding on the graphics card" : "decoding on the CPU")}; {mixer?.TrackCount ?? 0} audio tracks";

    private FfmpegPresenter? presenter;
    private FfmpegVideoReader? reader;
    private FfmpegAudioMixer? mixer;
    private FfmpegTempo? tempo;
    private readonly object audioLock = new(), frameLock = new();
    private WasapiOut? output;
    private Thread? decoder;
    private volatile bool stop, playingFlag;
    private readonly AutoResetEvent wake = new(false);
    private double seekRequest = double.NaN;
    private readonly Queue<(double Time, IntPtr Frame)> ahead = new();
    private IntPtr still; // a paused seek's frame, waiting to be shown
    private int stillScheduled;
    private IntPtr shown;
    private const int Ahead = 4;
    // The clock: where it started (media seconds), the sound frames played since then, and the speed.
    private double anchor, speed = 1;
    private readonly Stopwatch clock = new();
    private long audioStart;
    private bool playing, endedRaised;
    private double volume = .5; private bool muted;
    private IReadOnlyDictionary<int, double>? trackVolumes;

    internal void Open(string path)
    {
        Close();
        try
        {
            presenter = new FfmpegPresenter(graphicsCard: true);
            try { reader = new FfmpegVideoReader(path, presenter.HardwareDevice); }
            catch when (presenter.OnGraphicsCard) { presenter.Dispose(); presenter = new FfmpegPresenter(graphicsCard: false); reader = new FfmpegVideoReader(path); }
            VideoWidth = reader.Width; VideoHeight = reader.Height; Duration = reader.Duration;
            presenter.Prepare(reader.Width, reader.Height, reader.FrameRate);
            presenter.SourceChanged += () => View.Source = presenter.Source;
            View.Source = presenter.Source;
            mixer = new FfmpegAudioMixer(path);
            if (mixer.TrackCount == 0) { mixer.Dispose(); mixer = null; }
            else { if (trackVolumes != null) mixer.SetVolumes(trackVolumes); tempo = new FfmpegTempo(mixer); }
            stop = false; anchor = 0; endedRaised = false;
            decoder = new Thread(DecodeLoop) { IsBackground = true, Name = "FFmpeg preview decoder", Priority = ThreadPriority.AboveNormal };
            decoder.Start();
            RequestSeek(0);
            IsOpen = true;
            View.Dispatcher.BeginInvoke(() => { if (IsOpen) Opened?.Invoke(); });
        }
        catch (Exception ex)
        {
            Close();
            View.Dispatcher.BeginInvoke(() => Failed?.Invoke(ex));
        }
    }
    internal void Close()
    {
        Pause();
        IsOpen = false; stop = true; wake.Set();
        decoder?.Join(2000); decoder = null;
        lock (frameLock) { while (ahead.Count > 0) Free(ahead.Dequeue().Frame); Free(Interlocked.Exchange(ref still, IntPtr.Zero)); }
        Free(shown); shown = IntPtr.Zero;
        lock (audioLock) { tempo?.Dispose(); tempo = null; mixer?.Dispose(); mixer = null; }
        reader?.Dispose(); reader = null;
        View.Source = null; presenter?.Dispose(); presenter = null;
    }
    private static void Free(IntPtr frame) { if (frame == IntPtr.Zero) return; var f = (AVFrame*)frame; ffmpeg.av_frame_free(&f); }

    // ---- Where it is ----
    internal double Position
    {
        get => Now();
        set
        {
            double at = Math.Clamp(value, 0, Math.Max(0, Duration));
            bool was = playing;
            if (was) StopClock();
            anchor = at; endedRaised = false;
            RequestSeek(at);
            if (was) StartClock();
        }
    }
    private double Now()
    {
        if (!playing) return anchor;
        if (output != null) return anchor + Math.Max(0, output.GetPosition() / 8 - audioStart) * speed / FfmpegAudioMixer.Rate;
        return anchor + clock.Elapsed.TotalSeconds * speed;
    }
    internal double SpeedRatio
    {
        get => speed;
        set
        {
            double next = Math.Clamp(value, .1, 4);
            if (Math.Abs(next - speed) < 1e-9) return;
            bool was = playing;
            if (was) { anchor = Now(); StopClock(); }
            speed = next;
            if (was) StartClock();
        }
    }
    internal double Volume { get => volume; set { volume = Math.Clamp(value, 0, 1); } }
    internal bool IsMuted { get => muted; set => muted = value; }
    // Which audio tracks the preview plays and how loud (the editor's lanes); null plays the whole mix.
    internal void SetTrackVolumes(IReadOnlyDictionary<int, double>? volumes)
    {
        trackVolumes = volumes;
        if (mixer == null) return;
        lock (audioLock) mixer.SetVolumes(volumes ?? new Dictionary<int, double> { [0] = 1 });
    }

    internal void Play()
    {
        if (!IsOpen || playing) return;
        if (anchor >= Duration - .01) anchor = 0;
        playing = true; endedRaised = false;
        RequestSeek(anchor, keepPlaying: true);
        StartClock();
        CompositionTarget.Rendering += Render;
    }
    internal void Pause()
    {
        if (!playing) return;
        anchor = Now();
        StopClock(); playing = false; playingFlag = false;
        CompositionTarget.Rendering -= Render;
        RequestSeek(anchor);
    }
    private void StartClock()
    {
        playing = true; playingFlag = true;
        if (mixer != null)
        {
            lock (audioLock) { mixer.Seek(anchor); tempo!.SetSpeed(speed); tempo.Reset(); }
            try
            {
                output = new WasapiOut(AudioClientShareMode.Shared, true, 60);
                output.Init(new Feed(this));
                audioStart = output.GetPosition() / 8;
                output.Play();
                return;
            }
            catch { output?.Dispose(); output = null; }
        }
        clock.Restart();
    }
    private void StopClock()
    {
        if (output != null) { var o = output; output = null; try { o.Stop(); } catch { } o.Dispose(); }
        clock.Reset();
    }

    // ---- Sound ----
    private sealed class Feed : IWaveProvider
    {
        private readonly FfmpegPreviewPlayer player;
        private float[] mix = new float[4800 * 2];
        internal Feed(FfmpegPreviewPlayer player) => this.player = player;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(FfmpegAudioMixer.Rate, 2);
        public int Read(byte[] buffer, int offset, int count)
        {
            int frames = count / 8;
            if (mix.Length < frames * 2) mix = new float[frames * 2];
            int got;
            lock (player.audioLock) got = player.tempo?.Read(mix, frames) ?? 0;
            // 0.5 is as recorded (the Windows player's default), so the preview can go up to twice as loud.
            float gain = player.muted ? 0 : (float)(player.volume * 2);
            for (int i = 0; i < got * 2; i++) mix[i] *= gain;
            if (got < frames) Array.Clear(mix, got * 2, (frames - got) * 2);
            Buffer.BlockCopy(mix, 0, buffer, offset, frames * 8);
            return frames * 8;
        }
    }

    // ---- Pictures ----
    private void RequestSeek(double seconds, bool keepPlaying = false)
    {
        lock (frameLock) { seekRequest = seconds; while (ahead.Count > 0) Free(ahead.Dequeue().Frame); }
        playingFlag = keepPlaying || playing;
        wake.Set();
    }
    private void DecodeLoop()
    {
        try
        {
            while (!stop)
            {
                double target;
                lock (frameLock) { target = seekRequest; seekRequest = double.NaN; }
                if (!double.IsNaN(target))
                {
                    if (reader!.Seek(target))
                    {
                        var copy = (IntPtr)ffmpeg.av_frame_clone(reader.Frame);
                        if (playingFlag) lock (frameLock) ahead.Enqueue((reader.FrameTime, copy));
                        else ShowStill(copy);
                    }
                    continue;
                }
                int count; lock (frameLock) count = ahead.Count;
                if (playingFlag && count < Ahead)
                {
                    if (reader!.Next())
                    {
                        var copy = (IntPtr)ffmpeg.av_frame_clone(reader.Frame); double time = reader.FrameTime;
                        lock (frameLock) { if (double.IsNaN(seekRequest)) ahead.Enqueue((time, copy)); else Free(copy); }
                        continue;
                    }
                }
                wake.WaitOne(playingFlag ? 5 : 200);
            }
        }
        catch (Exception ex) { View.Dispatcher.BeginInvoke(() => Failed?.Invoke(ex)); }
    }
    // A paused seek's frame goes on screen; during a scrub only the latest one does.
    private void ShowStill(IntPtr frame)
    {
        Free(Interlocked.Exchange(ref still, frame));
        if (Interlocked.Exchange(ref stillScheduled, 1) == 1) return;
        View.Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref stillScheduled, 0);
            var next = Interlocked.Exchange(ref still, IntPtr.Zero);
            if (next != IntPtr.Zero && presenter != null) Show(next);
        }, System.Windows.Threading.DispatcherPriority.Render);
    }
    private void Show(IntPtr frame)
    {
        presenter!.Present((AVFrame*)frame);
        Free(shown); shown = frame;
    }
    // Each screen refresh while playing: the latest frame that's due.
    private void Render(object? sender, EventArgs e)
    {
        if (!playing || presenter == null) return;
        double now = Now();
        IntPtr due = IntPtr.Zero;
        lock (frameLock)
            while (ahead.Count > 0 && ahead.Peek().Time <= now + .004)
            {
                if (due != IntPtr.Zero) Free(due);
                due = ahead.Dequeue().Frame;
            }
        if (due != IntPtr.Zero) { Show(due); wake.Set(); }
        if (now >= Duration - .005 && !endedRaised)
        {
            endedRaised = true; anchor = Duration;
            StopClock(); playing = false; playingFlag = false; CompositionTarget.Rendering -= Render;
            Ended?.Invoke();
        }
    }
    public void Dispose() { Close(); wake.Dispose(); }
}
