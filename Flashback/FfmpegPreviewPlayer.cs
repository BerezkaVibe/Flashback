using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FFmpeg.AutoGen;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Flashback;

// The FFmpeg preview player: FFmpeg's libraries in the app, decoding on the graphics card (FfmpegPresenter)
// and every audio track mixed in the app (FfmpegAudioMixer), played through WASAPI.
// - A decoding thread keeps a few frames ready ahead of the playhead, and jumps to the exact frame on a seek
//   (the latest seek wins, so scrubbing never queues up, and a newer one interrupts an older one).
// - The sound is the clock while playing: what the speakers have actually played, so picture and sound
//   stay together at any speed. Without sound it's a stopwatch.
// - The sound output keeps running through seeks, speed changes and short pauses (silence while paused), so
//   none of them wait for the audio device to start again.
// - Speed parts play at their speed from exactly where they begin, the sound switching speed at the edge.
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
    private long lastShown;
    private readonly Queue<(double Time, IntPtr Frame)> ahead = new();
    private IntPtr still; // a paused seek's frame, waiting to be shown
    private int stillScheduled;
    private IntPtr shown;
    private const int Ahead = 4;
    // The decoding thread's last frames: enough to step back past what the editor's 33 ms tick overshoots a
    // freeze frame by, at up to about 3× (FfmpegVideoReader's pool has room for them).
    private readonly FfmpegRecentFrames recent = new(12);
    // How long the sound output idles on silence after a pause before it's let go.
    // (Longer than the longest freeze frame hold, when the editor pauses the player for the hold.)
    private static readonly TimeSpan KeepOutput = TimeSpan.FromSeconds(12);
    private DispatcherTimer? releaseOutput;

    // ---- The clock ----
    // While paused: anchor. Playing with sound: wherever the speakers have got to in the sound (see
    // FfmpegSoundStream). Playing without sound: a stopwatch from anchor, through the speed parts.
    private FfmpegSoundStream? sound;
    private double anchor;
    private SpeedMap speeds = new(1, Array.Empty<SpeedRegion>(), 0);
    private readonly Stopwatch clock = new();
    private bool playing, endedRaised;

    private double volume = .5; private bool muted;
    private IReadOnlyDictionary<int, double>? trackVolumes;
    private IReadOnlyDictionary<int, IReadOnlyList<FfmpegAudioMixer.GainPart>>? trackGains;

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
            else { if (trackVolumes != null) mixer.SetVolumes(trackVolumes); mixer.SetGains(trackGains); tempo = new FfmpegTempo(mixer); }
            speeds = speeds with { Duration = Duration };
            if (mixer != null) sound = new FfmpegSoundStream(mixer, tempo!, speeds);
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
        Pause(); ReleaseOutput();
        IsOpen = false; stop = true; wake.Set();
        decoder?.Join(2000); decoder = null; recent.Clear();
        lock (frameLock) { while (ahead.Count > 0) Free(ahead.Dequeue().Frame); Free(Interlocked.Exchange(ref still, IntPtr.Zero)); }
        Free(shown); shown = IntPtr.Zero;
        lock (audioLock) { sound = null; tempo?.Dispose(); tempo = null; mixer?.Dispose(); mixer = null; }
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
            anchor = at; endedRaised = false; startedAt = at;
            RequestSeek(at);
            if (playing) Restart(at);
        }
    }
    private double Now()
    {
        if (!playing) return anchor;
        if (output is { } o && sound != null)
        {
            // What the speakers have played, in the mix's sample frames (the device may run at another rate).
            try { return sound.MediaAt(o.GetPosition() / (double)o.OutputWaveFormat.AverageBytesPerSecond * FfmpegAudioMixer.Rate) ?? anchor; }
            catch { return anchor; }
        }
        return speeds.Advance(anchor, clock.Elapsed.TotalSeconds);
    }
    // The preview's speed (1 is normal); speed parts play at this times their own speed.
    internal double SpeedRatio
    {
        get => speeds.Rate;
        set
        {
            double next = Math.Clamp(value, .1, 4);
            if (Math.Abs(next - speeds.Rate) > 1e-9) Respeed(speeds with { Rate = next });
        }
    }
    // Where playing stops by itself, exactly (the next freeze frame, which the editor then holds); NaN for
    // nowhere. Playing from it or past it goes on.
    internal double StopAt { get; set; } = double.NaN;
    private double startedAt;
    // The clip's speed parts, played at their speed. The same list again changes nothing.
    internal void SetSpeedParts(IReadOnlyList<SpeedRegion> parts)
    {
        if (!ReferenceEquals(parts, speeds.Parts)) Respeed(speeds with { Parts = parts });
    }
    internal double Volume { get => volume; set { volume = Math.Clamp(value, 0, 1); } }
    internal bool IsMuted { get => muted; set => muted = value; }
    // Which audio tracks the preview plays and how loud (the editor's lanes); null plays the whole mix. Gains:
    // each track's cut-outs and volume parts.
    internal void SetTrackVolumes(IReadOnlyDictionary<int, double>? volumes, IReadOnlyDictionary<int, IReadOnlyList<FfmpegAudioMixer.GainPart>>? gains = null)
    {
        trackVolumes = volumes; trackGains = gains;
        if (mixer == null) return;
        lock (audioLock) { mixer.SetVolumes(volumes ?? new Dictionary<int, double> { [0] = 1 }); mixer.SetGains(gains); }
    }

    internal void Play()
    {
        if (!IsOpen || playing) return;
        if (anchor >= Duration - .01) anchor = 0;
        playing = true; endedRaised = false; startedAt = anchor;
        RequestSeek(anchor, keepPlaying: true);
        releaseOutput?.Stop();
        bool fresh = false;
        if (sound != null && output == null)
            try
            {
                lock (audioLock) sound.NewOutput();
                output = new WasapiOut(AudioClientShareMode.Shared, true, 60);
                output.Init(new Feed(this));
                fresh = true;
            }
            catch { output?.Dispose(); output = null; }
        // The sound is ready before the output starts, so its first buffer is the clip's sound, not silence.
        Restart(anchor);
        if (fresh) try { output!.Play(); } catch { ReleaseOutput(); Restart(anchor); }
        CompositionTarget.Rendering += Render;
    }
    internal void Pause()
    {
        if (!playing) return;
        anchor = Now();
        playing = false; playingFlag = false;
        if (sound != null) lock (audioLock) sound.Stop();
        clock.Reset();
        CompositionTarget.Rendering -= Render;
        RequestSeek(anchor);
        // The output idles on silence for a moment, so playing again straight away starts at once.
        if (output != null)
        {
            releaseOutput ??= new DispatcherTimer(KeepOutput, DispatcherPriority.Background, (_, _) => { releaseOutput!.Stop(); if (!playing) ReleaseOutput(); }, View.Dispatcher);
            releaseOutput.Stop(); releaseOutput.Start();
        }
    }
    private void ReleaseOutput()
    {
        releaseOutput?.Stop();
        if (output == null) return;
        var o = output; output = null;
        try { o.Stop(); } catch { }
        o.Dispose();
    }
    // Plays on from a moment.
    private void Restart(double at)
    {
        playingFlag = true;
        if (output != null) { lock (audioLock) sound!.Restart(at, speeds); return; }
        anchor = at; clock.Restart();
    }
    // A new speed, from now on.
    private void Respeed(SpeedMap next)
    {
        if (!playing) { speeds = next; return; }
        double at = Now(); speeds = next;
        if (output != null) { lock (audioLock) sound!.Respeed(speeds); return; }
        anchor = at; clock.Restart();
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
            int frames = count / 8, got = 0;
            if (mix.Length < frames * 2) mix = new float[frames * 2];
            try { lock (player.audioLock) got = player.sound?.Read(mix, frames) ?? 0; }
            catch (Exception ex) { player.View.Dispatcher.BeginInvoke(() => player.Failed?.Invoke(ex)); }
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
        lock (frameLock) { Volatile.Write(ref seekRequest, seconds); while (ahead.Count > 0) Free(ahead.Dequeue().Frame); }
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
                    // A newer seek to earlier on gives this one up, unless nothing has been shown for a while
                    // (dragging back steadily still shows pictures as it goes). One further on lets it carry on.
                    bool Newer()
                    {
                        double next = Volatile.Read(ref seekRequest);
                        return !double.IsNaN(next) && next < target && Stopwatch.GetElapsedTime(Interlocked.Read(ref lastShown)).TotalMilliseconds < 150;
                    }
                    // Just behind (or on) what was last decoded: the frames are still here.
                    if (recent.From(target, reader!.FrameRate) is { } kept)
                    {
                        if (playingFlag) lock (frameLock) foreach (var f in kept) ahead.Enqueue(f);
                        else { ShowStill(kept[0].Frame); foreach (var f in kept.Skip(1)) Free(f.Frame); }
                        continue;
                    }
                    recent.Clear();
                    if (reader.Seek(target, Newer))
                    {
                        recent.Add(reader.FrameTime, reader.Frame);
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
                        recent.Add(reader.FrameTime, reader.Frame);
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
        Interlocked.Exchange(ref lastShown, Stopwatch.GetTimestamp());
        Free(Interlocked.Exchange(ref still, frame));
        if (Interlocked.Exchange(ref stillScheduled, 1) == 1) return;
        View.Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref stillScheduled, 0);
            var next = Interlocked.Exchange(ref still, IntPtr.Zero);
            if (next != IntPtr.Zero && presenter != null) Show(next);
        }, DispatcherPriority.Render);
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
        // Reaching the stop: the picture and sound stop right there, on its frame.
        if (StopAt > startedAt + 1e-6 && now >= StopAt - 1e-6)
        {
            Pause(); anchor = StopAt; RequestSeek(StopAt);
            return;
        }
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
            endedRaised = true;
            Pause(); anchor = Duration;
            Ended?.Invoke();
        }
    }
    public void Dispose() { Close(); wake.Dispose(); }
}
