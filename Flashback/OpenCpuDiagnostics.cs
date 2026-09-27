using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Flashback;

// --open-cpu-test: where the CPU goes in the seconds after the editor opens a clip (with a video over it and
// music, as in --background-test), and then playing it. Every second for 35 s it writes each busy thread's share of one core, by
// its name (.NET and the player name their threads; FFmpeg's own and the system's show as unnamed), to
// open-cpu-report.txt, with the moments the window showed, the clip opened and the parts were added.
internal static class OpenCpuDiagnostics
{
    [DllImport("kernel32.dll")] private static extern IntPtr OpenThread(uint access, bool inherit, uint id);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern int GetThreadDescription(IntPtr thread, out IntPtr description);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    private static string NameOf(int id)
    {
        IntPtr handle = OpenThread(0x0040, false, (uint)id); // THREAD_QUERY_INFORMATION (the limited right is refused here)
        if (handle == IntPtr.Zero) return "?";
        try
        {
            if (GetThreadDescription(handle, out var text) < 0 || text == IntPtr.Zero) return "(unnamed)";
            string name = Marshal.PtrToStringUni(text) ?? ""; LocalFree(text);
            return name.Length == 0 ? "(unnamed)" : name;
        }
        finally { CloseHandle(handle); }
    }

    internal static async Task RunAsync()
    {
        Directory.CreateDirectory(Storage.Root);
        ThreadNames.Name("UI");
        var report = new StringBuilder();
        void Line(string text) { report.AppendLine(text); File.WriteAllText(Path.Combine(Storage.Root, "open-cpu-report.txt"), report.ToString()); }
        string folder = Path.Combine(Storage.Root, "open"); Directory.CreateDirectory(folder);
        string clip = Path.Combine(folder, "Clip 1080p60.mp4"), inset = Path.Combine(folder, "Inset.mp4"), music = Path.Combine(folder, "Music.m4a");
        await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=60:duration=40", "-f", "lavfi", "-i", "sine=frequency=300:sample_rate=48000:duration=40",
            "-c:v", "libx264", "-preset", "ultrafast", "-threads", "4", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", clip);
        await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=60", "-t", "40", "-f", "lavfi", "-i", "sine=frequency=900:sample_rate=48000:duration=40",
            "-c:v", "libx264", "-preset", "ultrafast", "-g", "250", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", inset);
        await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "sine=frequency=500:sample_rate=48000:duration=40", "-c:a", "aac", music);
        await Task.Delay(1500); // let the encoders' work settle

        var process = Process.GetCurrentProcess();
        var clock = Stopwatch.StartNew();
        Dictionary<int, TimeSpan> Snapshot() { process.Refresh(); var d = new Dictionary<int, TimeSpan>(); foreach (ProcessThread t in process.Threads) try { d[t.Id] = t.TotalProcessorTime; } catch { } return d; }
        var last = Snapshot(); var lastTotal = process.TotalProcessorTime; double lastAt = 0;
        var names = new Dictionary<int, string>();
        void Mark(string what) => Line($"{clock.Elapsed.TotalSeconds,5:0.0} s  -- {what}");
        void Slice()
        {
            var now = Snapshot(); double at = clock.Elapsed.TotalSeconds, span = at - lastAt;
            var total = process.TotalProcessorTime;
            var busy = now.Select(kv => (Id: kv.Key, Share: (kv.Value - (last.TryGetValue(kv.Key, out var was) ? was : TimeSpan.Zero)).TotalSeconds / span * 100))
                .Where(x => x.Share >= 2).OrderByDescending(x => x.Share)
                .Select(x => { if (!names.TryGetValue(x.Id, out var n)) names[x.Id] = n = NameOf(x.Id); return $"{n} {x.Share:0}%"; });
            Line($"{at,5:0.0} s  {(total - lastTotal).TotalSeconds / span * 100,4:0}% total · {process.Threads.Count} threads · {string.Join(", ", busy)}");
            last = now; lastTotal = total; lastAt = at;
        }
        var sampler = Task.Run(async () => { ThreadNames.Name("Sampler"); while (clock.Elapsed.TotalSeconds < 36) { await Task.Delay(1000); System.Windows.Application.Current.Dispatcher.Invoke(Slice); } });

        Line($"Flashback {typeof(TrimWindow).Assembly.GetName().Version} · {Environment.ProcessorCount} logical processors · 1080p60 clip with a 1080p60 video over it and music");
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var trim = new TrimWindow(clip) { Width = 1400, Height = 900 };
        try
        {
            Mark("window made"); trim.Show(); Mark("window shown");
            var until = Stopwatch.StartNew(); while (!trim.Player.NaturalDuration.HasTimeSpan && until.Elapsed.TotalSeconds < 15) await Task.Delay(20);
            Mark($"clip opened ({(trim.Player.UsesFfmpeg ? "FFmpeg" : "Windows")} player)");
            typeof(TrimWindow).GetMethod("AddOverlay", flags)!.Invoke(trim, new object[] { OverlayItem.NewVideo(0, 40, inset, 1920, 1080) with { X = .8, Y = .25, Scale = .5 } });
            typeof(TrimWindow).GetMethod("CloseOverlay", flags)!.Invoke(trim, null);
            typeof(TrimWindow).GetMethod("SetSounds", flags)!.Invoke(trim, new object[] { new[] { new SoundItem(music, 0, 40) } });
            Mark("video over the clip and music added");
            await Task.Delay(1000); trim.SeekTo(2); Mark("moved to 2 s");
            // Then playing, to see the cost of playback once the start-up work is done.
            await Task.Delay(6000); trim.HandleKey(System.Windows.Input.Key.Space, System.Windows.Input.ModifierKeys.None, true); Mark("playing");
            await sampler;
        }
        finally { trim.Close(); }
    }
}
