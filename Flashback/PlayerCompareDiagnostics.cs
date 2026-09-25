using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Flashback;

// --player-compare-test: the Windows and FFmpeg preview players side by side on the same 1080p60 clip, whose
// every frame shows its own number (three bands of brightness, each a digit counting in twenties, 10 levels
// apart so color conversion can't blur them), so the frame actually on screen can be read back. For each: how long it takes to open, whether
// seeks land on the exact frame and how fast, how smoothly it plays (frames shown per second, repeats, the
// longest gap), how fast a scrub settles, and the app's CPU and memory paused and playing.
// Writes player-compare.txt.
internal static class PlayerCompareDiagnostics
{
    internal static async Task RunAsync()
    {
        Directory.CreateDirectory(Storage.Root);
        var report = new StringBuilder();
        void Line(string text) { report.AppendLine(text); File.WriteAllText(Path.Combine(Storage.Root, "player-compare.txt"), report.ToString()); }
        string folder = Path.Combine(Storage.Root, "player-compare"); Directory.CreateDirectory(folder);
        string clip = Path.Combine(folder, "Numbered 1080p60.mp4");
        if (!File.Exists(clip))
            await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "color=black:s=320x180:r=60:d=30,format=gray,geq=lum='if(lt(X\\,107)\\,30+10*mod(N\\,20)\\,if(lt(X\\,214)\\,30+10*mod(floor(N/20)\\,20)\\,30+10*floor(N/400)))',scale=1920:1080:flags=neighbor",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=30", "-c:v", "libx264", "-preset", "fast", "-g", "120", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", clip);
        Line($"Flashback {typeof(TrimWindow).Assembly.GetName().Version} · {Environment.ProcessorCount} logical processors · 1080p60 clip, keyframe every 2 s");
        foreach (bool ffmpegPlayer in new[] { false, true })
        {
            string name = ffmpegPlayer ? "FFmpeg player" : "Windows player";
            if (ffmpegPlayer && !FfmpegLibrary.Available) { Line($"{name}: not available ({FfmpegLibrary.Error})"); continue; }
            TrimWindow.UseFfmpegPreview = ffmpegPlayer;
            var open = Stopwatch.StartNew();
            var trim = new TrimWindow(clip) { Width = 1400, Height = 900 };
            try
            {
                trim.Show();
                while (!trim.Player.NaturalDuration.HasTimeSpan && open.Elapsed.TotalSeconds < 15) await Task.Delay(10);
                double openMs = open.Elapsed.TotalMilliseconds;
                await Task.Delay(800);
                Line(""); Line($"== {name} ==" + (trim.Player.Engine is { } engine ? $" ({engine.Report})" : ""));
                Line($"Open: {openMs:0} ms");
                // The frame number on screen, read from the picture (null if it can't be read back).
                int? Shown()
                {
                    var visual = trim.Player.Visual; int w = (int)visual.ActualWidth, h = (int)visual.ActualHeight;
                    if (w < 40 || h < 20) return null;
                    var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
                    int Gray(double fx) { var px = new byte[4]; bitmap.CopyPixels(new Int32Rect((int)(w * fx), h / 2, 1, 1), px, 4, 0); return px[1]; }
                    int Digit(double fx) => (int)Math.Round((Gray(fx) - 30) / 10.0);
                    int a = Digit(1 / 6.0), b = Digit(.5), c = Digit(5 / 6.0);
                    if (a is < 0 or > 19 || b is < 0 or > 19 || c is < 0 or > 4) return null;
                    return c * 400 + b * 20 + a;
                }
                async Task<(bool Exact, double Ms)> Land(int frame, double timeout = 1500)
                {
                    var wait = Stopwatch.StartNew(); int? last = null;
                    while (wait.Elapsed.TotalMilliseconds < timeout) { last = Shown(); if (last == frame) return (true, wait.Elapsed.TotalMilliseconds); await Task.Delay(4); }
                    return (false, wait.Elapsed.TotalMilliseconds);
                }
                // Seeks while paused.
                var rng = new Random(8); var seekMs = new List<double>(); int exact = 0, seeks = 30;
                for (int i = 0; i < seeks; i++)
                {
                    int frame = rng.Next(0, 1790); trim.SeekTo((frame + .5) / 60);
                    var (hit, ms) = await Land(frame); if (hit) { exact++; seekMs.Add(ms); }
                }
                seekMs.Sort();
                Line(Shown() == null ? "Seeks: the picture can't be read back for this player" :
                    $"Seeks: {exact}/{seeks} on the exact frame · to screen median {(seekMs.Count > 0 ? seekMs[seekMs.Count / 2] : 0):0} ms, worst {(seekMs.Count > 0 ? seekMs[^1] : 0):0} ms");
                // A scrub: 60 seeks 16 ms apart, then how long until the last one shows.
                for (int i = 0; i < 60; i++) { trim.SeekTo((300 + i * 3 + .5) / 60, defer: i < 59); await Task.Delay(16); }
                var (settledHit, settledMs) = await Land(300 + 59 * 3, 3000);
                Line($"Scrub (60 moves over 1 s): last position {(settledHit ? $"on screen {settledMs:0} ms after letting go" : "not shown exactly")}");
                // CPU and memory: paused, then playing (without reading the picture back, which costs CPU itself).
                var process = Process.GetCurrentProcess();
                async Task<string> Load(double seconds)
                {
                    process.Refresh(); var cpu = process.TotalProcessorTime; var clock = Stopwatch.StartNew();
                    await Task.Delay(TimeSpan.FromSeconds(seconds)); process.Refresh();
                    return $"CPU {(process.TotalProcessorTime - cpu).TotalSeconds / clock.Elapsed.TotalSeconds * 100:0.0}% of one core, working set {process.WorkingSet64 / 1048576.0:0} MiB, private {process.PrivateMemorySize64 / 1048576.0:0} MiB";
                }
                trim.SeekTo(2); await Task.Delay(500);
                Line($"Paused: {await Load(4)}");
                trim.HandleKey(Key.Space, ModifierKeys.None, true); await Task.Delay(500);
                Line($"Playing: {await Load(8)}");
                // Smoothness: the frame on screen sampled every refresh for 3 s.
                var seen = new List<(double At, int Frame)>(); var watch = Stopwatch.StartNew();
                while (watch.Elapsed.TotalSeconds < 3) { if (Shown() is int f) seen.Add((watch.Elapsed.TotalSeconds, f)); await Task.Delay(1); }
                trim.HandleKey(Key.Space, ModifierKeys.None, true);
                var changes = seen.Zip(seen.Skip(1)).Where(p => p.Second.Frame != p.First.Frame).ToList();
                int distinct = seen.Select(s => s.Frame).Distinct().Count(), backwards = changes.Count(p => p.Second.Frame < p.First.Frame);
                double longest = changes.Count < 2 ? 0 : changes.Zip(changes.Skip(1)).Max(p => p.Second.Second.At - p.First.Second.At) * 1000;
                double span = seen.Count > 1 ? seen[^1].Frame - seen[0].Frame : 0;
                Line(seen.Count == 0 ? "Playing smoothness: the picture can't be read back" :
                    $"Playing smoothness: {distinct / 3.0:0} different frames a second seen (sampling limits this), {span / 3.0:0} frames of clip a second, longest time on one picture {longest:0} ms, {backwards} steps back");
            }
            catch (Exception ex) { Line($"{name}: FAILED {ex.Message}"); }
            finally { trim.Close(); TrimWindow.UseFfmpegPreview = null; }
            await Task.Delay(500);
        }
    }
}
