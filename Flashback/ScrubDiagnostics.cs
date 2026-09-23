using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Flashback;

// --scrub-test <clip>: drags the playhead across a real clip with the audio folded and
// then opened, and reports how smoothly the picture and the window keep up.
internal static class ScrubDiagnostics
{
    internal static async Task RunAsync(string path)
    {
        var report = new List<string> { "Clip: " + path };
        var trim = new TrimWindow(path);
        try
        {
            trim.WindowState = WindowState.Maximized; trim.Show();
            var until = Stopwatch.StartNew();
            while (!trim.Player.NaturalDuration.HasTimeSpan && until.Elapsed.TotalSeconds < 15) await Task.Delay(50);
            double duration = trim.Player.NaturalDuration.TimeSpan.TotalSeconds;
            await Task.Delay(800);
            var tl = trim.Timeline;
            foreach (double at in new[] { 20.0, 6.0 })
            {
                trim.SeekTo(at); await Task.Delay(400);
                trim.HandleKey(System.Windows.Input.Key.Space, System.Windows.Input.ModifierKeys.None, true); await Task.Delay(1400);
                report.Add($"Play after seek to {at}: decoder {trim.Player.Position.TotalSeconds:0.00}, playhead {trim.Playhead:0.00} (expect ~{at + 1.4:0.0})");
                trim.HandleKey(System.Windows.Input.Key.Space, System.Windows.Input.ModifierKeys.None, true);
            }
            trim.SeekTo(duration / 2); await Task.Delay(500); Shot(trim, "trim-audio-folded.png");
            await Sweep("audio folded");
            typeof(TrimWindow).GetMethod("LanesToggleRequested", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(trim, null);
            until.Restart();
            while (tl.Lanes.Any(l => l.Peaks.Length == 0) && until.Elapsed.TotalSeconds < 20) await Task.Delay(100);
            await Task.Delay(300);
            trim.SeekTo(duration / 2); await Task.Delay(500); Shot(trim, "trim-audio-open.png");
            await Sweep($"audio open, {tl.Lanes.Count} lane(s) with waveform");

            async Task Sweep(string label)
            {
                double y = 40, x0 = tl.XAt(duration / 3), x1 = tl.XAt(duration * 2 / 3);
                var gaps = new List<double>(); long last = Stopwatch.GetTimestamp();
                EventHandler tick = (_, _) => { var now = Stopwatch.GetTimestamp(); gaps.Add(Stopwatch.GetElapsedTime(last, now).TotalMilliseconds); last = now; };
                tl.BeginDrag(new Point(x0, y)); tl.CaptureMouse();
                var cpu = Process.GetCurrentProcess().TotalProcessorTime;
                var threads = Process.GetCurrentProcess().Threads.Cast<ProcessThread>().ToDictionary(t => t.Id, t => { try { return t.TotalProcessorTime; } catch { return TimeSpan.Zero; } });
                // A first pass without picture sampling, so the CPU figures are the trimmer's own.
                var plain = Stopwatch.StartNew();
                while (plain.Elapsed.TotalSeconds < 4)
                {
                    double f = plain.Elapsed.TotalSeconds / 4, x = x0 + (x1 - x0) * (f < .5 ? f * 2 : 2 - f * 2);
                    tl.MoveTo(x); await Task.Delay(8);
                }
                double plainCpu = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds / plain.Elapsed.TotalMilliseconds * 100;
                var busiest = Process.GetCurrentProcess().Threads.Cast<ProcessThread>()
                    .Select(t => { try { return (t.Id, Ms: (t.TotalProcessorTime - (threads.TryGetValue(t.Id, out var b) ? b : TimeSpan.Zero)).TotalMilliseconds); } catch { return (t.Id, Ms: 0.0); } })
                    .OrderByDescending(t => t.Ms).Take(4).Select(t => $"{t.Ms / plain.Elapsed.TotalMilliseconds * 100:0}%");
                report.Add($"[{label}] plain drag CPU {plainCpu:0}% of one core; busiest threads {string.Join(", ", busiest)}; timeline {tl.ActualWidth:0}x{tl.ActualHeight:0}");
                cpu = Process.GetCurrentProcess().TotalProcessorTime;
                CompositionTarget.Rendering += tick;
                string shown = Hash(trim.Player); int changes = 0; var sweep = Stopwatch.StartNew();
                while (sweep.Elapsed.TotalSeconds < 4)
                {
                    double f = sweep.Elapsed.TotalSeconds / 4, x = x0 + (x1 - x0) * (f < .5 ? f * 2 : 2 - f * 2);
                    tl.MoveTo(x); await Task.Delay(8);
                    var h = Hash(trim.Player); if (h != shown) { changes++; shown = h; }
                }
                CompositionTarget.Rendering -= tick;
                double cpuPct = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds / sweep.Elapsed.TotalMilliseconds * 100;
                var settle = Stopwatch.StartNew(); double settled = 0;
                tl.MoveTo(x0 + (x1 - x0) * .37);
                while (settle.ElapsedMilliseconds < 3000) { await Task.Delay(5); var h = Hash(trim.Player); if (h != shown) { shown = h; settled = settle.Elapsed.TotalMilliseconds; } }
                tl.ReleaseMouseCapture();
                gaps.Sort();
                report.Add($"[{label}] picture {changes / 4.0:0.#} updates/s, caught up {settled:0} ms after stop; " +
                           $"UI frames median {gaps[gaps.Count / 2]:0} ms, p95 {gaps[(int)(gaps.Count * .95)]:0} ms, worst {gaps[^1]:0} ms; " +
                           $"CPU {cpuPct:0}% of one core");
                var draw = typeof(TrimTimeline).GetMethod("DrawStatic", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var w = Stopwatch.StartNew();
                for (int i = 0; i < 50; i++) { var v = new DrawingVisual(); using (var dc = v.RenderOpen()) draw.Invoke(tl, new object[] { dc }); }
                report.Add($"[{label}] timeline rebuild {w.Elapsed.TotalMilliseconds / 50:0.00} ms");
            }
        }
        finally { trim.Close(); }
        Directory.CreateDirectory(Storage.Root);
        File.WriteAllLines(Path.Combine(Storage.Root, "scrub-results.txt"), report);
    }
    // A real screen grab of the window, video included.
    private static void Shot(Window w, string name)
    {
        var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        SystemTestNative.GetWindowRect(h, out var r);
        using var bmp = new System.Drawing.Bitmap(r.Right - r.Left, r.Bottom - r.Top);
        // PrintWindow with full-content rendering captures the window even when something covers it.
        using (var g = System.Drawing.Graphics.FromImage(bmp)) { var dc = g.GetHdc(); PrintWindow(h, dc, 2); g.ReleaseHdc(dc); }
        Directory.CreateDirectory(Storage.Root);
        bmp.Save(Path.Combine(Storage.Root, name), System.Drawing.Imaging.ImageFormat.Png);
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    private static string Hash(FrameworkElement e)
    {
        var bmp = new RenderTargetBitmap(160, 90, 96, 96, PixelFormats.Pbgra32);
        var v = new DrawingVisual();
        using (var dc = v.RenderOpen()) dc.DrawRectangle(new VisualBrush(e), null, new Rect(0, 0, 160, 90));
        bmp.Render(v);
        var px = new byte[160 * 90 * 4]; bmp.CopyPixels(px, 160 * 4, 0);
        return Convert.ToBase64String(System.Security.Cryptography.SHA1.HashData(px));
    }
}
