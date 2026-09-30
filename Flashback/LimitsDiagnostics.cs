using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Flashback;

// --limits-test: what 0.9.6 changed. Exports at the new limits (16× and 0.05× speed, a 60 s freeze, 500%
// volume, 12× zoom over 8 s, loud and fast sounds); settings saving by themselves; waveforms in added sounds;
// and typing exact values beside the sliders. Writes limits-results.txt and screenshots.
internal static class LimitsDiagnostics
{
    internal static async Task RunAsync()
    {
        Directory.CreateDirectory(Storage.Root);
        string results = Path.Combine(Storage.Root, "limits-results.txt"); File.Delete(results);
        void Check(bool ok, string message)
        {
            File.AppendAllText(results, (ok ? "PASS " : "FAIL ") + message + Environment.NewLine);
            if (!ok) throw new Exception(message);
        }
        var clip = Path.Combine(Storage.Root, "Twenty seconds.mp4");
        await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30:duration=20", "-f", "lavfi", "-i", "sine=frequency=500:sample_rate=48000:duration=20",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", clip);
        // A loud sound (a waveform that fills its bar) and a quiet one.
        var loud = Path.Combine(Storage.Root, "Loud.m4a");
        await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "aevalsrc=0.9*sin(2*PI*440*t)*(0.5+0.5*sin(2*PI*0.5*t)):s=48000:d=6", "-c:a", "aac", loud);

        await ExportsAsync(clip, loud, Check);
        await SettingsAsync(Check);
        await EditorAsync(clip, loud, Check);
    }

    private static async Task ExportsAsync(string clip, string loud, Action<bool, string> Check)
    {
        var sections = new[] { new KeepSection(0, 20) };
        string folder = Path.Combine(Storage.Root, "limits"); Directory.CreateDirectory(folder);
        var eight = ZoomPreset.BuiltIns[1].Curve.Scaled(8 / ZoomPreset.BuiltIns[1].Curve.Length);
        var cases = new (string Name, ShareExportOptions Options)[]
        {
            ("16x speed", new ShareExportOptions { SlowRegions = new[] { new SpeedRegion(2, 18, 16) } }),
            ("0.05x speed", new ShareExportOptions { SlowRegions = new[] { new SpeedRegion(1, 1.5, .05) } }),
            ("60 s freeze", new ShareExportOptions { Freezes = new[] { new FreezeFrame(5, 60) } }),
            ("500% volume", new ShareExportOptions { VolumeRegions = new[] { new VolumeRegion(0, 10, 20, 5) } }),
            ("12x zoom over 8 s", new ShareExportOptions { ZoomRegions = new[] { new ZoomRegion(2, 16, .5, .5, 12, eight) } }),
            ("loud fast and slow sounds", new ShareExportOptions { Sounds = new[] { new SoundItem(loud, 1, 4, Volume: 5) { Speed = 8 }, new SoundItem(loud, 6, 16, Volume: 5, Row: 1) { Speed = .1 } } }),
            ("everything at once", new ShareExportOptions { SlowRegions = new[] { new SpeedRegion(2, 6, 16), new SpeedRegion(12, 12.5, .05) }, Freezes = new[] { new FreezeFrame(8, 60) },
                VolumeRegions = new[] { new VolumeRegion(0, 14, 20, 5) }, ZoomRegions = new[] { new ZoomRegion(7, 11, .3, .6, 12, eight) }, Sounds = new[] { new SoundItem(loud, 15, 18, Volume: 5) { Speed = 8 } } }),
        };
        foreach (var (name, options) in cases)
        {
            var output = Path.Combine(folder, name + ".mp4"); File.Delete(output);
            double expected = options.OutputSeconds(sections);
            var watch = Stopwatch.StartNew();
            await ExportServices.PreciseAsync(clip, output, sections, null, CancellationToken.None, true, options);
            var got = ClipMedia.Read(output);
            Check(Math.Abs(got.Duration - expected) < .25, $"Export with {name}: {got.Duration:0.00} s (expected {expected:0.00}) in {watch.Elapsed.TotalSeconds:0.0} s");
        }
        // 500% on the second half: the sine is about 14 dB louder there (a limiter keeps it off full scale).
        var boosted = Path.Combine(folder, "500% volume.mp4");
        double before = await PeakAsync(boosted, 2, 6), after = await PeakAsync(boosted, 12, 6);
        Check(after - before > 10 && after <= 0, $"500% makes that part about 14 dB louder without going over full scale ({before:0.0} dB, then {after:0.0} dB)");
        // At 12× the middle of the zoom shows a small patch of the picture blown up, so it looks nothing like the frame before.
        double change = await DifferenceAsync(Path.Combine(folder, "12x zoom over 8 s.mp4"), 1, 12);
        Check(change > 20, $"A 12× zoom changes the picture ({change:0} average difference)");
    }
    private static async Task<double> PeakAsync(string file, double from, double length)
    {
        string text = await FfmpegTextAsync("-ss", Num(from), "-t", Num(length), "-i", file, "-vn", "-af", "volumedetect", "-f", "null", "-");
        var m = Regex.Match(text, @"max_volume:\s*(-?[\d.]+) dB");
        return m.Success ? double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : double.NaN;
    }
    // The mean absolute difference (0-255) between the frames at two times, in grey at 64×36.
    private static async Task<double> DifferenceAsync(string file, double a, double b)
    {
        string text = await FfmpegTextAsync("-ss", Num(a), "-i", file, "-ss", Num(b), "-i", file, "-filter_complex",
            "[0:v]trim=end_frame=1,scale=64:36,format=gray[x];[1:v]trim=end_frame=1,scale=64:36,format=gray[y];[x][y]blend=all_mode=difference,signalstats,metadata=print:key=lavfi.signalstats.YAVG",
            "-frames:v", "1", "-f", "null", "-");
        var m = Regex.Match(text, @"YAVG=([\d.]+)");
        return m.Success ? double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
    }
    private static string Num(double v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static async Task<string> FfmpegTextAsync(params string[] args)
    {
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "-hide_banner", "-nostdin" }.Concat(args)) info.ArgumentList.Add(arg);
        using var p = Process.Start(info)!;
        var output = p.StandardOutput.ReadToEndAsync(); string text = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync(); return text + await output;
    }

    // Settings save a moment after a change, with no Apply button.
    private static async Task SettingsAsync(Action<bool, string> Check)
    {
        Storage.Save(new Settings { OutputFolder = Path.Combine(Storage.Root, "clips") });
        var main = new MainWindow(true);
        try
        {
            await Task.Delay(300);
            main.SeparateTracksCheck.IsChecked = true;
            main.LengthSlider.Value = 120;
            main.AutoUpdateCheck.IsChecked = false;
            Check(!Storage.Load(out _).SeparateAudioTracks, "A change isn't saved in the same instant (changes in a row save together)");
            var wait = Stopwatch.StartNew();
            while (!Storage.Load(out _).SeparateAudioTracks && wait.Elapsed.TotalSeconds < 5) await Task.Delay(100);
            var saved = Storage.Load(out _);
            Check(saved.SeparateAudioTracks && saved.ReplaySeconds == 120 && !saved.InstallUpdatesAutomatically, $"Settings save by themselves {wait.Elapsed.TotalSeconds:0.0} s after changing them, with no Apply button");
            Check(main.ApplyButton.Visibility != Visibility.Visible, "The Apply settings button is gone");
        }
        // Not quit: that ends the app. It was never shown, so it just stays unused.
        finally { }
    }

    private static async Task EditorAsync(string clip, string loud, Action<bool, string> Check)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, flags)!.Invoke(target, args);
        var trim = new TrimWindow(clip, true) { Width = 1400, Height = 900 };
        try
        {
            trim.Show();
            var tl = trim.Timeline;
            tl.LanesExpanded = true;
            Call(trim, "SetSounds", (IReadOnlyList<SoundItem>)new[] { new SoundItem(loud, 1, 7) });
            var wait = Stopwatch.StartNew();
            while (SoundPeaks.Get(loud) == null && wait.Elapsed.TotalSeconds < 15) await Task.Delay(100);
            Check(SoundPeaks.Get(loud) is { Length: > 400 }, $"An added sound's waveform loads in the background ({wait.Elapsed.TotalSeconds:0.0} s)");
            await Task.Delay(500);
            // In the bar: rows near the middle are the waveform's colour, and it swells and fades as the sound does.
            var shot = Snapshot(tl);
            var bar = (Rect)Call(tl, "SoundRect", tl.Sounds[0])!;
            Save(shot, "limits-timeline.png");
            int Height(double x)
            {
                int n = 0;
                for (int y = (int)bar.Top + 2; y < (int)bar.Bottom - 2; y++) { var c = Pixel(shot, (int)x, y); if (c.G > 150) n++; }
                return n;
            }
            var heights = Enumerable.Range(1, 19).Select(i => Height(bar.X + bar.Width * i / 20)).ToArray();
            Check(heights.Max() >= bar.Height * .45 && heights.Max() - heights.Min() >= 3, $"The sound's bar shows its waveform, rising and falling with it (heights {string.Join(",", heights)} of {bar.Height:0})");

            // Typing exact values beside the sliders.
            ValueBox Box(DependencyObject root, string row) => Descendants(root).OfType<ValueBox>().First(b => b.Parent is FrameworkElement p && Descendants(p).OfType<TextBlock>().Any(t => t.Text == row));
            Call(trim, "OpenSoundPopup", tl.Sounds[0]); await Task.Delay(200);
            Box(trim.SoundPopup.Child, "Volume").Type("237");
            Check(Math.Abs(tl.Sounds[0].Volume - 2.37) < 1e-9, $"Typing 237 sets a sound's volume to exactly 237% ({tl.Sounds[0].Volume * 100:0.#}%)");
            Box(trim.SoundPopup.Child, "Speed").Type("6.5×");
            Check(Math.Abs(tl.Sounds[0].Speed - 6.5) < 1e-9, $"Typing 6.5× sets a sound's speed ({tl.Sounds[0].Speed}×)");
            Box(trim.SoundPopup.Child, "Volume").Type("900");
            Check(Math.Abs(tl.Sounds[0].Volume - 5) < 1e-9 && Box(trim.SoundPopup.Child, "Volume").Text == "500%", "A typed value past the limit stops at the limit (500%)");
            Save(Snapshot((FrameworkElement)trim.SoundPopup.Child), "limits-sound-popup.png");
            trim.SoundPopup.IsOpen = false;

            tl.VolumeRegions = new[] { new VolumeRegion(0, 2, 4, 1) };
            Call(trim, "OpenVolumePopup", tl.VolumeRegions[0]); await Task.Delay(200);
            trim.VolumeLabel.Type("437%");
            Check(Math.Abs(tl.VolumeRegions[0].Gain - 4.37) < 1e-9, $"Typing 437% sets a volume part exactly, between the slider's steps ({tl.VolumeRegions[0].Gain * 100:0.#}%)");
            Save(Snapshot((FrameworkElement)trim.VolumePopup.Child), "limits-volume-popup.png");
            trim.VolumePopup.IsOpen = false;

            tl.SlowRegions = new[] { new SpeedRegion(8, 10, .5) };
            Call(trim, "SlowTagClicked", 0); await Task.Delay(200);
            trim.RegionSpeedLabel.Type("12.25");
            Check(Math.Abs(tl.SlowRegions[0].Speed - 12.25) < 1e-9, $"Typing 12.25 sets a speed part to 12.25× ({tl.SlowRegions[0].Speed}×)");
            trim.RegionSpeedLabel.Type("0.05x");
            Check(Math.Abs(tl.SlowRegions[0].Speed - .05) < 1e-9, $"Typing 0.05x slows a part to 0.05× ({tl.SlowRegions[0].Speed}×)");
            Save(Snapshot((FrameworkElement)trim.SlowPopup.Child), "limits-speed-popup.png");
            trim.SlowPopup.IsOpen = false;
            tl.SlowRegions = Array.Empty<SpeedRegion>();

            tl.Freezes = new[] { new FreezeFrame(12, 1) };
            Call(trim, "OpenFreezePopup", 0); await Task.Delay(200);
            Descendants(trim.FreezeControls).OfType<ValueBox>().First().Type("42.5");
            Check(Math.Abs(tl.Freezes[0].Seconds - 42.5) < 1e-9, $"Typing 42.5 makes a freeze hold 42.5 s ({tl.Freezes[0].Seconds} s)");
            Save(Snapshot(trim.FreezeControls), "limits-freeze.png");
            tl.Freezes = Array.Empty<FreezeFrame>();

            tl.ZoomRegions = new[] { new ZoomRegion(3, 8, .5, .5, 2, ZoomPreset.BuiltIns[1].Curve) };
            Call(trim, "OpenZoom", 0); await Task.Delay(300);
            trim.ZoomMaxBox.Type("11.5");
            trim.ZoomTimeBox.Type("7 s");
            Check(Math.Abs(tl.ZoomRegions[0].MaxZoom - 11.5) < 1e-9 && Math.Abs(tl.ZoomRegions[0].In.Length - 7) < 1e-6, $"Typing sets a zoom to 11.5× over 7 s ({tl.ZoomRegions[0].MaxZoom}×, {tl.ZoomRegions[0].In.Length:0.##} s)");
            Save(Snapshot(trim), "limits-zoom.png");

            trim.DesktopMixLabel.Type("333");
            Check(Math.Abs(trim.DesktopMix.Value - 333) < 1e-9, $"Typing 333 sets the desktop mix between its slider steps ({trim.DesktopMix.Value}%)");
        }
        finally { trim.Close(); }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var d in Descendants(child)) yield return d;
    }
    // The element as it shows: its whole window (or pop-up) is drawn, then cut down to the element.
    internal static BitmapSource Snapshot(FrameworkElement element)
    {
        element.UpdateLayout();
        Visual root = element;
        for (DependencyObject? up = element; up != null; up = VisualTreeHelper.GetParent(up)) if (up is Visual v) root = v;
        var size = root is FrameworkElement r ? new Size(r.ActualWidth, r.ActualHeight) : new Size(element.ActualWidth, element.ActualHeight);
        var whole = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(size.Width)), Math.Max(1, (int)Math.Ceiling(size.Height)), 96, 96, PixelFormats.Pbgra32);
        whole.Render(root);
        var at = ReferenceEquals(root, element) ? new Point() : element.TransformToAncestor(root).Transform(new Point());
        var crop = new Int32Rect((int)Math.Round(at.X), (int)Math.Round(at.Y), 0, 0);
        crop.Width = Math.Max(1, Math.Min((int)element.ActualWidth, whole.PixelWidth - crop.X)); crop.Height = Math.Max(1, Math.Min((int)element.ActualHeight, whole.PixelHeight - crop.Y));
        return new CroppedBitmap(whole, crop);
    }
    private static Color Pixel(BitmapSource image, int x, int y)
    {
        var px = new byte[4]; image.CopyPixels(new Int32Rect(Math.Clamp(x, 0, image.PixelWidth - 1), Math.Clamp(y, 0, image.PixelHeight - 1), 1, 1), px, 4, 0);
        return Color.FromRgb(px[2], px[1], px[0]);
    }
    internal static void Save(BitmapSource image, string name)
    {
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(Path.Combine(Storage.Root, name)); png.Save(file);
    }
}
