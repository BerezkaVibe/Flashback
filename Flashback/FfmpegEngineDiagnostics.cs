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
// their volumes, the rest silent, lined up after a seek, and speed changes keeping the pitch.
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
        string frames = Path.Combine(folder, "Frames.mp4"), tracks = Path.Combine(folder, "Tracks.mp4"), big = Path.Combine(folder, "Big 1080p60.mp4");
        await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "color=black:s=320x180:r=60:d=10,format=gray,geq=lum='30+mod(N\\,200)'", "-c:v", "libx264", "-preset", "ultrafast", "-g", "120", "-pix_fmt", "yuv420p", frames);
        await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "color=black:s=160x90:r=30:d=6", "-f", "lavfi", "-i", "aevalsrc=0.3*sin(2*PI*if(lt(t\\,3)\\,300\\,600)*t):s=48000:d=6",
            "-f", "lavfi", "-i", "sine=frequency=900:sample_rate=48000:duration=6", "-f", "lavfi", "-i", "sine=frequency=1500:sample_rate=48000:duration=6",
            "-filter_complex", "[1:a][2:a][3:a]amix=inputs=3:normalize=0[all]", "-map", "0:v", "-map", "[all]", "-map", "1:a", "-map", "2:a", "-map", "3:a", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-ac", "2", "-ar", "48000", "-shortest", tracks);
        await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=60:duration=8", "-c:v", "libx264", "-preset", "ultrafast", "-g", "120", "-pix_fmt", "yuv420p", big);
        Engine(frames, tracks, big, Check, Note);
        Note("Passed: " + checks.Count + " checks");
    }
    private static unsafe void Engine(string frames, string tracks, string big, Action<bool, string> Check, Action<string> Note)
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
    }
}
