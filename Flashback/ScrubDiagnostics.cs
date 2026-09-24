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
            foreach (double rate in new[] { .1, 4.0 })
            {
                trim.SpeedSlider.Value = Math.Log(rate); trim.SeekTo(10); await Task.Delay(400);
                trim.HandleKey(System.Windows.Input.Key.Space, System.Windows.Input.ModifierKeys.None, true); await Task.Delay(2000);
                report.Add($"Play 2 s at {rate}x: decoder moved {trim.Player.Position.TotalSeconds - 10:0.00} s (expect ~{2 * rate:0.0}), playhead {trim.Playhead:0.00}");
                trim.HandleKey(System.Windows.Input.Key.Space, System.Windows.Input.ModifierKeys.None, true);
            }
            trim.SpeedSlider.Value = Math.Log(.5); trim.SeekTo(duration / 2);
            trim.Topmost = true; trim.Activate(); trim.SpeedPopup.IsOpen = true; await Task.Delay(700);
            var corner = trim.SpeedButton.PointToScreen(new Point(0, 0));
            using (var bmp = new System.Drawing.Bitmap(900, 330))
            {
                using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen((int)corner.X - 450, (int)corner.Y - 250, 0, 0, bmp.Size);
                bmp.Save(Path.Combine(Storage.Root, "trim-speed.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            trim.SpeedPopup.IsOpen = false;
            trim.ExportOptionsPopup.IsOpen = true; await Task.Delay(500);
            var options = trim.ExportOptionsButton.PointToScreen(new Point(0, 0));
            using (var bmp = new System.Drawing.Bitmap(900, 420))
            {
                using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen((int)options.X - 600, (int)options.Y - 340, 0, 0, bmp.Size);
                bmp.Save(Path.Combine(Storage.Root, "trim-options.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            trim.ExportOptionsPopup.IsOpen = false; trim.Topmost = false;
            trim.SpeedSlider.Value = 0;
            // Put the start handle right on a labelled second so the shot shows the label stepping aside.
            trim.SeekTo(4); trim.HandleKey(System.Windows.Input.Key.I, System.Windows.Input.ModifierKeys.None, true);
            trim.SeekTo(duration / 2); await Task.Delay(500); Shot(trim, "trim-audio-folded.png");
            // Two kept sections and a blacked-out stretch, to show the shading of what gets cut.
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var (a, b) in new[] { (5.0, 15.0), (25.0, 32.0) })
            { trim.StartBox.Text = a.ToString(); trim.EndBox.Text = b.ToString(); typeof(TrimWindow).GetMethod("Add_Click", flags)!.Invoke(trim, new object[] { trim, new RoutedEventArgs() }); }
            typeof(TrimWindow).GetMethod("CutAdded", flags)!.Invoke(trim, new object[] { new CutRegion(-1, 8, 10) });
            typeof(TrimWindow).GetMethod("SlowAdded", flags)!.Invoke(trim, new object[] { 11.0, 14.0 });
            trim.SeekTo(12); await Task.Delay(500); Shot(trim, "trim-sections.png");
            // A zoom overlapping the speed part: editor open (full frame + target box), then closed (zoomed preview).
            typeof(TrimWindow).GetMethod("ZoomAdded", flags)!.Invoke(trim, new object[] { 12.5, 17.0 });
            trim.ZoomTarget.Set(.62, .4, 2.2); typeof(TrimWindow).GetMethod("ZoomTargetChanged", flags)!.Invoke(trim, null);
            await Task.Delay(600); Shot(trim, "trim-zoom-editor.png");
            typeof(TrimWindow).GetMethod("ZoomDone_Click", flags)!.Invoke(trim, new object[] { trim, new RoutedEventArgs() });
            trim.SeekTo(15); await Task.Delay(600); Shot(trim, "trim-zoom-preview.png");
            // The speed pop-up opens where the pointer is (it normally sits on the part's tag).
            trim.Topmost = true; trim.Activate();
            typeof(TrimWindow).GetMethod("SlowTagClicked", flags)!.Invoke(trim, new object[] { 0 });
            trim.RegionSpeedSlider.Value = Math.Log(1.5); await Task.Delay(700);
            var mouse = System.Windows.Forms.Cursor.Position;
            using (var bmp = new System.Drawing.Bitmap(420, 200))
            {
                using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(mouse.X - 20, mouse.Y - 10, 0, 0, bmp.Size);
                bmp.Save(Path.Combine(Storage.Root, "trim-speed-part.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            trim.SlowPopup.IsOpen = false; trim.Topmost = false;
            typeof(TrimWindow).GetMethod("Clear_Click", flags)!.Invoke(trim, new object[] { trim, new RoutedEventArgs() });
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
                // Frame gaps count from here, not from before the plain pass above.
                last = Stopwatch.GetTimestamp();
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
