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
// It plays the editor's timeline (PlaySequence): the kept sections in play order, speed parts at their speed,
// freeze frames holding, with music and sound files mixed over it as the export mixes them (SequenceAudio).
// Its time (Position) is seconds along that timeline at 1×; Where tells the editor what shows there.
// - A decoding thread keeps a few frames ready ahead of the playhead, mapped onto the timeline, and jumps to
//   the exact frame on a seek (the latest seek wins, so scrubbing never queues up, and a newer one interrupts
//   an older one). A jump to another section is decoded ahead, before it's due.
// - The sound is the clock while playing: what the speakers have actually played, so picture and sound
//   stay together at any speed. Without a sound device it's a stopwatch.
// - The sound output keeps running through seeks, speed changes and short pauses (silence while paused), so
//   none of them wait for the audio device to start again.
// - Each screen refresh shows the frame for that moment.
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
    // Frames kept ready while playing: enough lead to hide the keyframe seek of a jump to another section.
    private const int Ahead = 6;
    // The decoding thread's last frames, so stepping back a little doesn't go back to the keyframe
    // (FfmpegVideoReader's pool has room for them).
    private readonly FfmpegRecentFrames recent = new(12);
    // How long the sound output idles on silence after a pause before it's let go.
    private static readonly TimeSpan KeepOutput = TimeSpan.FromSeconds(12);
    private DispatcherTimer? releaseOutput;

    // ---- The timeline and the clock ----
    // While paused: anchor. Playing with sound: wherever the speakers have got to in the sound (see
    // FfmpegSoundStream). Playing without a sound device: a stopwatch from anchor.
    private volatile PlaySequence sequence = PlaySequence.Whole(1);
    private IReadOnlyList<PlayPiece> pieces = Array.Empty<PlayPiece>();
    private IReadOnlyList<SoundClip> soundClips = Array.Empty<SoundClip>();
    private SequenceAudio? audio;
    private FfmpegSoundStream? sound;
    private double anchor, rate = 1;
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
            else { if (trackVolumes != null) mixer.SetVolumes(trackVolumes); mixer.SetGains(trackGains); }
            // Until the editor hands over its timeline: the whole recording.
            pieces = Array.Empty<PlayPiece>(); soundClips = Array.Empty<SoundClip>(); sequence = PlaySequence.Whole(Duration);
            // (Music plays over a clip without sound of its own too.)
            audio = new SequenceAudio(mixer, sequence);
            audio.SetSounds(soundClips);
            sound = new FfmpegSoundStream(audio);
            stop = false; anchor = 0; endedRaised = false;
            frames = new FfmpegSequenceFrames(reader, recent);
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
        lock (audioLock) { sound = null; audio?.Dispose(); audio = null; mixer?.Dispose(); mixer = null; }
        reader?.Dispose(); reader = null;
        View.Source = null; presenter?.Dispose(); presenter = null;
    }
    private static void Free(IntPtr frame) { if (frame == IntPtr.Zero) return; var f = (AVFrame*)frame; ffmpeg.av_frame_free(&f); }

    // ---- Where it is ----
    // Seconds along the timeline at 1×.
    internal double Position
    {
        get => Now();
        set
        {
            double at = Math.Clamp(value, 0, sequence.Total);
            anchor = at; endedRaised = false;
            RequestSeek(at);
            if (playing) Restart(at);
        }
    }
    internal double Total => sequence.Total;
    // What shows now: the recording's moment, how far into a freeze's hold, the section, and whether a hold is
    // playing.
    internal (double Source, double Hold, int Section, bool Holding) Where => sequence.Where(Now());
    // Where a moment of the recording plays on the timeline (see PlaySequence.TimeOf).
    internal double TimeOf(double source, double hold = 0, int section = -1) => sequence.TimeOf(source, hold, section);
    private double Now()
    {
        if (!playing) return anchor;
        if (output is { } o && sound != null)
        {
            // What the speakers have played, in the mix's sample frames (the device may run at another rate).
            try { return Math.Min(sequence.Total, sound.MediaAt(o.GetPosition() / (double)o.OutputWaveFormat.AverageBytesPerSecond * FfmpegAudioMixer.Rate) ?? anchor); }
            catch { return anchor; }
        }
        return Math.Min(sequence.Total, anchor + clock.Elapsed.TotalSeconds * rate);
    }
    // The preview's speed (1 is normal); speed parts play at this times their own speed.
    internal double SpeedRatio
    {
        get => rate;
        set
        {
            double next = Math.Clamp(value, .1, 4);
            if (Math.Abs(next - rate) < 1e-9) return;
            if (!playing) { rate = next; return; }
            double at = Now(); rate = next;
            if (output != null) { lock (audioLock) sound!.Respeed(rate); return; }
            anchor = at; clock.Restart();
        }
    }
    // The editor's timeline and its music and sound files. New pieces take effect from the next seek (the
    // editor seeks straight after, as the playhead may land elsewhere on them: true when they changed); new
    // sounds at once, the ones playing carrying on.
    internal bool SetSequence(IReadOnlyList<PlayPiece> nextPieces, IReadOnlyList<SoundClip> nextSounds)
    {
        bool newPieces = !nextPieces.SequenceEqual(pieces);
        pieces = nextPieces.ToArray(); soundClips = nextSounds;
        if (newPieces) sequence = pieces.Count > 0 ? new PlaySequence(pieces) : PlaySequence.Whole(Duration);
        if (audio == null) return newPieces;
        lock (audioLock)
        {
            if (newPieces) audio.SetSequence(sequence);
            audio.SetSounds(soundClips);
        }
        return newPieces;
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
        if (anchor >= sequence.Total - .01) anchor = 0;
        playing = true; endedRaised = false;
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
        // The sound is ready before the output starts, so its first buffer is the timeline's sound, not silence.
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
    // Plays on from a moment of the timeline.
    private void Restart(double at)
    {
        playingFlag = true;
        if (output != null) { lock (audioLock) sound!.Restart(at, rate); return; }
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
        lock (frameLock) { Volatile.Write(ref seekRequest, seconds); while (ahead.Count > 0) Free(ahead.Dequeue().Frame); lastQueued = 0; }
        playingFlag = keepPlaying || playing;
        wake.Set();
    }
    // The pictures along the timeline (FfmpegSequenceFrames), on the decoding thread.
    private FfmpegSequenceFrames? frames;
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
                    var paused = frames!.Seek(sequence, target, playingFlag, Newer, QueueFrame);
                    if (paused != IntPtr.Zero) ShowStill(paused);
                    continue;
                }
                int count; lock (frameLock) count = ahead.Count;
                if (playingFlag && count < Ahead && !frames!.Done) { frames.Step(QueueFrame); continue; }
                wake.WaitOne(playingFlag ? 5 : 200);
            }
        }
        catch (Exception ex) { View.Dispatcher.BeginInvoke(() => Failed?.Invoke(ex)); }
    }
    // A frame due at a moment of the timeline joins the queue, unless a newer seek has come.
    private bool QueueFrame(double due, IntPtr frame)
    {
        lock (frameLock)
        {
            if (!double.IsNaN(seekRequest)) return false;
            ahead.Enqueue((Math.Max(due, lastQueued), frame)); lastQueued = Math.Max(due, lastQueued);
            return true;
        }
    }
    private double lastQueued;
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
        IntPtr due = IntPtr.Zero;
        lock (frameLock)
            while (ahead.Count > 0 && ahead.Peek().Time <= now + .004)
            {
                if (due != IntPtr.Zero) Free(due);
                due = ahead.Dequeue().Frame;
            }
        if (due != IntPtr.Zero) { Show(due); wake.Set(); }
        if (now >= sequence.Total - .005 && !endedRaised)
        {
            endedRaised = true;
            Pause(); anchor = sequence.Total;
            Ended?.Invoke();
        }
    }
    public void Dispose() { Close(); wake.Dispose(); }
}
