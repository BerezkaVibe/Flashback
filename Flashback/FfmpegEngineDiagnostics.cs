using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FFmpeg.AutoGen;

namespace Flashback;

// --ffmpeg-engine-test: the FFmpeg preview player's engine without the window. Needs the libraries in the
// ffmpeg folder. Frames: a clip whose every frame's brightness is its number; seeks (across keyframes) must
// land on the exact frame, picture included, decoding on the CPU and, where it works, on the graphics card
// (frames copied back to check them). Sound: a clip with separate tracks of known tones; chosen tracks at
// their volumes, the rest silent, lined up after a seek, and speed changes keeping the pitch; a lane's cut-outs
// and volume parts; the editor's timeline (pieces at their speeds, a hold, a jump to another section) played
// through without a gap, with music placed, faded and ducking as the export mixes it; the clock following
// speed changes and seeks. Scrubbing: a seek gives up when a newer one comes, and the next still lands exactly.
// Writes ffmpeg-engine-results.json and ffmpeg-engine-report.txt.
internal static class FfmpegEngineDiagnostics
{
    internal static async Task RunAsync()
    {
        Directory.CreateDirectory(Storage.Root);
        var checks = new List<string>(); var report = new List<string>();
        void Check(bool ok, string name)
        {
            if (!ok) throw new Exception("FAILED: " + name);
            checks.Add(name); File.WriteAllText(Path.Combine(Storage.Root, "ffmpeg-engine-results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        }
        void Note(string line) { report.Add(line); File.WriteAllLines(Path.Combine(Storage.Root, "ffmpeg-engine-report.txt"), report); }
        Check(FfmpegLibrary.Available, "FFmpeg's libraries load from the ffmpeg folder " + FfmpegLibrary.Error);
        string folder = Path.Combine(Storage.Root, "ffmpeg-engine"); Directory.CreateDirectory(folder);
        string frames = Path.Combine(folder, "Frames.mp4"), tracks = Path.Combine(folder, "Tracks.mp4"), big = Path.Combine(folder, "Big 1080p60.mp4"), music = Path.Combine(folder, "Music.m4a"), numbered = Path.Combine(folder, "Numbered.mp4");
        await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "color=black:s=320x180:r=60:d=10,format=gray,geq=lum='30+mod(N\\,200)'", "-c:v", "libx264", "-preset", "ultrafast", "-g", "120", "-pix_fmt", "yuv420p", frames);
        await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "color=black:s=160x90:r=30:d=6", "-f", "lavfi", "-i", "aevalsrc=0.3*sin(2*PI*if(lt(t\\,3)\\,300\\,600)*t):s=48000:d=6",
            "-f", "lavfi", "-i", "sine=frequency=900:sample_rate=48000:duration=6", "-f", "lavfi", "-i", "sine=frequency=1500:sample_rate=48000:duration=6",
            "-filter_complex", "[1:a][2:a][3:a]amix=inputs=3:normalize=0[all]", "-map", "0:v", "-map", "[all]", "-map", "1:a", "-map", "2:a", "-map", "3:a", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-ac", "2", "-ar", "48000", "-shortest", tracks);
        await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=60:duration=8", "-c:v", "libx264", "-preset", "ultrafast", "-g", "120", "-pix_fmt", "yuv420p", big);
        await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=48000:duration=3", "-c:a", "aac", music);
        // Every frame shows its number in three bands of brightness (each a digit counting in twenties).
        await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "color=black:s=320x180:r=60:d=15,format=gray,geq=lum='if(lt(X\\,107)\\,30+10*mod(N\\,20)\\,if(lt(X\\,214)\\,30+10*mod(floor(N/20)\\,20)\\,30+10*floor(N/400)))'", "-c:v", "libx264", "-preset", "ultrafast", "-g", "120", "-pix_fmt", "yuv420p", numbered);
        Engine(frames, tracks, big, music, numbered, Check, Note);
        Note("Passed: " + checks.Count + " checks");
    }
    private static unsafe void Engine(string frames, string tracks, string big, string music, string numbered, Action<bool, string> Check, Action<string> Note)
    {
        // ---- Frames ----
        using var presenter = new FfmpegPresenter(graphicsCard: true);
        Note($"Graphics-card path: {presenter.Path}");
        foreach (bool graphicsCard in new[] { false, true })
        {
            if (graphicsCard && !presenter.OnGraphicsCard) { Note("Graphics-card decoding isn't available here; only the CPU path was checked."); continue; }
            string how = graphicsCard ? "on the graphics card" : "on the CPU";
            using var video = new FfmpegVideoReader(frames, graphicsCard ? presenter.HardwareDevice : null);
            var copy = ffmpeg.av_frame_alloc();
            int Luma()
            {
                var frame = video.Frame;
                if (frame->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11) { ffmpeg.av_frame_unref(copy); FfmpegLibrary.Check(ffmpeg.av_hwframe_transfer_data(copy, frame, 0), "Couldn't copy a frame back"); frame = copy; }
                return frame->data[0][frame->linesize[0] * 90 + 160];
            }
            static int Expected(int n) => (int)Math.Round(16 + (30 + n % 200) * 219 / 255.0);
            var rng = new Random(3); int wrong = 0; var times = new List<double>();
            foreach (double t in new[] { 0, 1.999, 2.0, 2.01, 5.5, 9.98 }.Concat(Enumerable.Range(0, 60).Select(_ => rng.NextDouble() * 9.9)))
            {
                var sw = Stopwatch.StartNew(); bool got = video.Seek(t); times.Add(sw.Elapsed.TotalMilliseconds);
                int n = (int)Math.Floor(t * 60 + .5 + 1e-9);
                if (!got || Math.Abs(video.FrameTime - n / 60.0) > 1e-3 || Math.Abs(Luma() - Expected(n)) > 2) wrong++;
            }
            times.Sort();
            Check(wrong == 0, $"66 seeks decoding {how} land on the exact frame, picture included ({wrong} wrong)");
            Note($"Seeks decoding {how} (320x180): median {times[times.Count / 2]:0.0} ms, worst {times[^1]:0.0} ms");
            video.Seek(3); int steps = 0; bool ordered = true; double last = video.FrameTime;
            while (steps < 120 && video.Next()) { steps++; ordered &= Math.Abs(video.FrameTime - last - 1 / 60.0) < 1e-3 && Math.Abs(Luma() - Expected((int)Math.Round(video.FrameTime * 60))) <= 2; last = video.FrameTime; }
            Check(steps == 120 && ordered, $"Playing on decoding {how} shows every frame in order");
            video.Seek(5); int asked = 0;
            bool gaveUp = !video.Seek(3.9, () => ++asked > 20);
            Check(gaveUp && video.Seek(2.95) && Math.Abs(video.FrameTime - 177 / 60.0) < 1e-3 && Math.Abs(Luma() - Expected(177)) <= 2, $"Decoding {how}, a seek gives up when a newer one comes, and the next lands exactly");
            // Pictures along a timeline: 10-11 s, a 0.5 s hold, 11-11.5 s at 0.5×, then a jump back to 3-3.5 s at 2×.
            using (var numberedVideo = new FfmpegVideoReader(numbered, graphicsCard ? presenter.HardwareDevice : null))
            using (var kept = new FfmpegRecentFrames(12))
            {
                var numberCopy = ffmpeg.av_frame_alloc();
                int NumberOf(IntPtr f)
                {
                    var fr = (AVFrame*)f;
                    if (fr->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11) { ffmpeg.av_frame_unref(numberCopy); FfmpegLibrary.Check(ffmpeg.av_hwframe_transfer_data(numberCopy, fr, 0), "Couldn't copy a frame back"); fr = numberCopy; }
                    int Digit(double fx) { int l = fr->data[0][fr->linesize[0] * 90 + (int)(320 * fx)]; return (int)Math.Round(((l - 16) * 255 / 219.0 - 30) / 10); }
                    return Digit(5 / 6.0) * 400 + Digit(.5) * 20 + Digit(1 / 6.0);
                }
                void FreeFrame(IntPtr f) { var fr = (AVFrame*)f; ffmpeg.av_frame_free(&fr); }
                var timeline = new PlaySequence(new[] { new PlayPiece(10, 11, 1, 0, 0, 0), new PlayPiece(11, 11, 1, .5, 1, 0), new PlayPiece(11, 11.5, .5, 0, 1.5, 0), new PlayPiece(3, 3.5, 2, 0, 2.5, 1) });
                var along = new FfmpegSequenceFrames(numberedVideo, kept);
                var got = new List<(double Due, int Number)>();
                bool Take(double due, IntPtr f) { got.Add((due, NumberOf(f))); FreeFrame(f); return true; }
                along.Seek(timeline, 0, true, null, Take);
                for (int guard = 0; !along.Done && guard < 1000; guard++) along.Step(Take);
                int off = got.Count(g => g.Number != (int)Math.Floor(timeline.Where(g.Due + 1e-9).Source * 60 + .5 + 1e-6));
                var jump = got.Where(g => g.Due >= 2.5 - 1e-9).ToList();
                Check(got.Count > 100 && off == 0 && got.Count(g => g.Due >= 1 - 1e-9 && g.Due < 1.5 - 1e-9) == 1 && jump.Count == 30 && jump[0].Number == 180,
                    $"Decoding {how}, each frame along a timeline (a hold, 0.5×, a jump to another section at 2×) is the one showing at its moment ({got.Count} frames, {off} wrong)");
                var done2 = numberCopy; ffmpeg.av_frame_free(&done2);
            }
            if (graphicsCard)
            {
                // Scrubbing on the graphics card: keyframe seeks keep using the clip's one pool of frames, and
                // the pictures shown don't pile up.
                presenter.Prepare(video.Width, video.Height, video.FrameRate);
                var scrub = new Random(9); var process = Process.GetCurrentProcess(); process.Refresh(); long before = process.PrivateMemorySize64;
                for (int i = 0; i < 300; i++) if (video.Seek(scrub.NextDouble() * 9.9)) presenter.Present(video.Frame);
                process.Refresh(); double grew = (process.PrivateMemorySize64 - before) / 1048576.0;
                Check(video.PoolsMade == 1 && presenter.CachedViews <= 64, $"300 scrub seeks on the graphics card keep one pool of frames ({video.PoolsMade} made, {presenter.CachedViews} views kept)");
                Note($"300 graphics-card scrub seeks: private memory {grew:+0;-0} MiB");
            }
            var done = copy; ffmpeg.av_frame_free(&done);
            using var large = new FfmpegVideoReader(big, graphicsCard ? presenter.HardwareDevice : null);
            var seeks = new List<double>(); var r = new Random(5);
            for (int i = 0; i < 30; i++) { var sw = Stopwatch.StartNew(); large.Seek(r.NextDouble() * 7.9); seeks.Add(sw.Elapsed.TotalMilliseconds); }
            seeks.Sort(); var play = Stopwatch.StartNew(); large.Seek(0); int count = 0; while (count < 240 && large.Next()) count++;
            Note($"1080p60 decoding {how}: seek median {seeks[15]:0} ms, worst {seeks[^1]:0} ms; playing decodes {count / play.Elapsed.TotalSeconds:0} frames a second");
        }

        // ---- Sound ----
        static double Tone(float[] b, int from, int length, double hz)
        { double re = 0, im = 0; for (int i = 0; i < length; i++) { double s = b[(from + i) * 2], x = 2 * Math.PI * hz * i / 48000; re += s * Math.Cos(x); im += s * Math.Sin(x); } return Math.Sqrt(re * re + im * im) / length * 2; }
        static double Rms(float[] b, int from, int length) { double e = 0; for (int i = 0; i < length; i++) e += b[(from + i) * 2] * b[(from + i) * 2]; return Math.Sqrt(e / length); }
        static int F(double seconds) => (int)Math.Round(seconds * 48000);
        using (var mix = new FfmpegAudioMixer(tracks))
        {
            Check(mix.TrackCount == 4, $"Finds the clip's 4 audio tracks ({mix.TrackCount})");
            var buf = new float[48000 * 2];
            mix.Seek(1); mix.Read(buf, 24000);
            double w300 = Tone(buf, 0, 24000, 300), w1500 = Tone(buf, 0, 24000, 1500);
            mix.SetVolumes(new Dictionary<int, double> { [1] = 1, [3] = .5 }); mix.Seek(1); mix.Read(buf, 24000);
            double t300 = Tone(buf, 0, 24000, 300), t900 = Tone(buf, 0, 24000, 900), t1500 = Tone(buf, 0, 24000, 1500);
            Check(Math.Abs(t300 - w300) < .03 && t900 < .01 && Math.Abs(t1500 - w1500 * .5) < .03, $"Plays the chosen tracks at their volumes and not the others ({t300:0.00}, {t900:0.00}, {t1500:0.00})");
            mix.Seek(2.9); mix.Read(buf, 9600);
            Check(Tone(buf, 0, 4000, 300) > .15 && Tone(buf, 5600, 4000, 600) > .15 && Tone(buf, 5600, 4000, 300) < .05, "Stays lined up with the picture after a seek");
            mix.SetVolumes(new Dictionary<int, double> { [0] = 1 });
            using var tempo = new FfmpegTempo(mix);
            mix.Seek(.5); tempo.SetSpeed(2); int got = tempo.Read(buf, 24000);
            Check(got == 24000 && Math.Abs(mix.Position - 1.5) < .1 && Tone(buf, 4000, 16000, 300) > .15 && Tone(buf, 4000, 16000, 600) < .05, $"2× keeps the pitch and covers twice the time ({mix.Position - .5:0.00} s)");
        }
        using (var mix = new FfmpegAudioMixer(tracks))
        {
            // A lane's cut-out and volume part.
            mix.SetVolumes(new Dictionary<int, double> { [1] = 1 });
            mix.SetGains(new Dictionary<int, IReadOnlyList<FfmpegAudioMixer.GainPart>> { [1] = new[] { new FfmpegAudioMixer.GainPart(1, 1.5, 0), new FfmpegAudioMixer.GainPart(2, 2.5, .5) } });
            var buf = new float[F(3) * 2];
            mix.Seek(.5); mix.Read(buf, F(2.5));
            double full = Tone(buf, F(.1), F(.3), 300), half = Tone(buf, F(1.6), F(.3), 300);
            Check(full > .15 && Rms(buf, F(.5) + 2, F(.49)) < 1e-6 && Rms(buf, F(.5) - 480, 470) > .1 && Math.Abs(half - full * .5) < .02, $"A lane's cut-out is silent from its first sample and its volume part at its level ({full:0.00}, {half:0.00})");
            mix.SetGains(null);
            // The timeline: 0.5-1 at 1×, 1-2 at 2×, a 0.5 s hold at 2, 2-3 at 1×, then a jump to 4-5 at 0.5× (another
            // section); music from 0.2 s for 1.6 s (through the hold), half volume, fading in, ducking the clip to 0.5.
            var seq = new PlaySequence(new[] { new PlayPiece(.5, 1, 1, 0, 0, 0), new PlayPiece(1, 2, 2, 0, .5, 0), new PlayPiece(2, 2, 1, .5, 1, 0), new PlayPiece(2, 3, 1, 0, 1.5, 0), new PlayPiece(4, 5, .5, 0, 2.5, 1) });
            var w = seq.Where(1.2);
            Check(Math.Abs(seq.Total - 4.5) < 1e-9 && w.Source == 2 && Math.Abs(w.Hold - .2) < 1e-9 && w.Holding && Math.Abs(seq.TimeOf(4.5) - 3.5) < 1e-9 && Math.Abs(seq.TimeOf(2, .3) - 1.3) < 1e-9, "The timeline lays pieces end to end and tells what shows when");
            using var audio = new SequenceAudio(mix, seq);
            var o = new float[F(5) * 2]; var chunk = new float[480 * 2]; int total = 0, n;
            while ((n = audio.Read(chunk, 480)) > 0) { Array.Copy(chunk, 0, o, total * 2, n * 2); total += n; }
            double quietest = 1; foreach (var (from, to) in new[] { (.01, .99), (1.51, 4.49) }) for (int at = F(from); at + 240 < F(to); at += 120) quietest = Math.Min(quietest, Rms(o, at, 240));
            Check(total == F(4.5) && Rms(o, F(1.02), F(.46)) < 1e-6 && quietest > .05 && Tone(o, F(.6), F(.3), 300) > .17 && Tone(o, F(2.6), F(1.8), 600) > .17 && Tone(o, F(2.6), F(1.8), 300) < .03,
                $"Its sound: exactly its length, pieces at their speeds with the pitch kept, silent in the hold, no gaps where pieces meet (quietest 5 ms {quietest:0.00})");
            audio.SetSounds(new[] { new SoundClip(music, .2, 1.6, Offset: .5, Volume: .5, FadeIn: .2, Duck: true, DuckLevel: .5) });
            audio.Seek(0); total = 0;
            while ((n = audio.Read(chunk, 480)) > 0) { Array.Copy(chunk, 0, o, total * 2, n * 2); total += n; }
            double during = Tone(o, F(.5), F(.4), 1000), inHold = Tone(o, F(1.05), F(.4), 1000);
            Check(Tone(o, 0, F(.18), 1000) < .005 && during > .03 && Math.Abs(inHold - during) < .01 && Tone(o, F(1.85), F(.4), 1000) < .005 && Tone(o, F(.2), 480, 1000) < Tone(o, F(.39), 480, 1000) * .3,
                $"Music plays where it sits, through the hold, fading in, and stops ({during:0.000}, {inHold:0.000})");
            Check(Math.Abs(Tone(o, F(.5), F(.4), 300) - Tone(o, F(1.85), F(.4), 300) * .5) < .02, "Music ducks the clip's sound while it plays");
            // At the preview's speed: 2× then 1×, then a seek.
            var stream = new FfmpegSoundStream(audio);
            stream.NewOutput(); stream.Restart(0, 2);
            while (stream.Submitted < F(1)) stream.Read(chunk, 480);
            stream.Respeed(1);
            while (stream.Submitted < F(3)) stream.Read(chunk, 480);
            double m1 = stream.MediaAt(F(.5)) ?? -1, m2 = stream.MediaAt(F(2)) ?? -1, m3 = stream.MediaAt(F(2.5)) ?? -1;
            Check(Math.Abs(m1 - 1) < .02 && Math.Abs(m3 - m2 - .5) < .02, $"The clock follows the timeline at 2× then 1× ({m1:0.00}, {m2:0.00}, {m3:0.00})");
            long before = stream.Submitted; stream.Restart(3, 1);
            Check(stream.MediaAt(before - 100) == 3 && Math.Abs((stream.MediaAt(before + 4800) ?? -1) - 3.1) < 1e-6, "A seek while playing holds the picture until its sound is heard");
        }
    }
}
