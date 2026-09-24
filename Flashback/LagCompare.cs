using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Flashback;

// Plays a clip with more and more parts applied and writes how the editor holds up: CPU, how often the
// window paints, how late the UI thread answers, and how far the playhead got. Only uses what every
// recent version has, so two builds can be compared on the same machine. Writes lag-compare.txt.
internal static class LagCompare
{
    internal static async Task RunAsync(string? clip, string? project = null)
    {
        Directory.CreateDirectory(Storage.Root);
        if (clip == null || !File.Exists(clip))
        {
            clip = Path.Combine(Storage.Root, "Lag source.mp4");
            if (!File.Exists(clip))
                await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "testsrc2=size=2560x1440:rate=60:duration=20", "-f", "lavfi", "-i", "sine=frequency=500:sample_rate=48000:duration=20",
                    "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", clip);
        }
        var music = Path.Combine(Storage.Root, "Lag tune.m4a");
        if (!File.Exists(music)) await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "sine=frequency=2000:sample_rate=48000:duration=20", "-c:a", "aac", music);
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        void Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, flags | BindingFlags.Public)!.Invoke(target, args);
        var lines = new List<string> { $"{typeof(App).Assembly.GetName().Version} · {Path.GetFileName(clip)}" };
        var live = new TrimWindow(clip) { WindowState = WindowState.Maximized };
        try
        {
            live.Show();
            var until = Stopwatch.StartNew();
            while (!live.Player.NaturalDuration.HasTimeSpan && until.Elapsed.TotalSeconds < 15) await Task.Delay(100);
            await Task.Delay(1000);
            async Task Measure(string name, double start = 1)
            {
                live.SeekTo(start); await Task.Delay(700);
                int frames = 0; void Count(object? s, EventArgs e) => frames++;
                var late = new List<double>(); bool running = true;
                // How long the UI thread takes to answer: a background-priority check every 50 ms.
                async Task Probe() { while (running) { var asked = Stopwatch.GetTimestamp(); await live.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background); late.Add(Stopwatch.GetElapsedTime(asked).TotalMilliseconds); await Task.Delay(50); } }
                var process = Process.GetCurrentProcess(); var cpu = process.TotalProcessorTime; var clock = Stopwatch.StartNew(); double from = live.Playhead;
                CompositionTarget.Rendering += Count;
                var probing = Probe();
                live.HandleKey(Key.Space, ModifierKeys.None, true);
                await Task.Delay(8000);
                live.HandleKey(Key.Space, ModifierKeys.None, true);
                running = false; await probing; CompositionTarget.Rendering -= Count;
                process.Refresh(); double seconds = clock.Elapsed.TotalSeconds;
                late.Sort();
                lines.Add($"{name}: CPU {(process.TotalProcessorTime - cpu).TotalSeconds / seconds * 100:0}% of a core · paints {frames / seconds:0}/s · UI answers in {late[late.Count / 2]:0.0} ms typical, {late[(int)(late.Count * .95)]:0.0} ms at the 95th, {late[^1]:0} ms worst · playhead {from:0.0} → {live.Playhead:0.0} s");
                File.WriteAllLines(Path.Combine(Storage.Root, "lag-compare.txt"), lines);
            }
            await Measure("Plain");
            // A saved edit of this clip, played from 5 s.
            if (project != null)
            {
                live.ApplyProject(TrimProject.Read(project)); await Task.Delay(500);
                await Measure("Saved edit", 5);
                await Measure("Saved edit again", 5);
                if (live.GetType().GetMethod("ToggleFinishedView", flags | BindingFlags.Public) is { } view)
                {
                    view.Invoke(live, null); await Task.Delay(300);
                    await Measure("Saved edit, finished view", 5);
                }
                return;
            }
            Call(live, "SetOverlays", new object[] { new[] { OverlayItem.NewText(0, 20, null) with { Text = "LAG CHECK" } } });
            await Measure("Text");
            live.Timeline.ZoomRegions = new[] { new ZoomRegion(0, 20, .5, .5, 2, ZoomPreset.BuiltIns[2].Curve) };
            await Measure("Text + zoom");
            live.Timeline.ZoomRegions = Array.Empty<ZoomRegion>();
            live.Timeline.SlowRegions = new[] { new SpeedRegion(3, 5, .5) };
            await Measure("Text + speed part");
            live.Timeline.SlowRegions = Array.Empty<SpeedRegion>();
            Call(live, "SetSounds", (IReadOnlyList<SoundItem>)new[] { new SoundItem(music, 0, 15) });
            await Measure("Text + music");
            live.Timeline.SlowRegions = new[] { new SpeedRegion(3, 5, .5) };
            Call(live, "AddFreeze", 4.0, 1.0, true);
            live.Timeline.ZoomRegions = new[] { new ZoomRegion(1, 12, .5, .5, 2, ZoomPreset.BuiltIns[2].Curve) };
            Call(live, "SetOverlays", new object[] { new[] { OverlayItem.NewText(0, 20, null) with { Text = "LAG CHECK" }, OverlayItem.NewShape(0, 20, null) with { Layer = 1, X = .3 } } });
            live.Timeline.VolumeRegions = new[] { new VolumeRegion(0, 2, 6, .5) };
            await Measure("Everything");
            // Builds with the finished-video view: the same, in that view.
            if (live.GetType().GetMethod("ToggleFinishedView", flags | BindingFlags.Public) is { } toggle)
            {
                toggle.Invoke(live, null); await Task.Delay(300);
                await Measure("Everything, finished view");
            }
        }
        finally { live.Close(); }
    }
}
