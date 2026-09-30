using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;
using Vortice.DXGI;
using static Vortice.DXGI.DXGI;

namespace Flashback;

// "Report a bug": a zip on the desktop with what's needed to look into a problem (the PC, the settings, the
// app's recent messages and its own logs, and the newest clip's details), then a GitHub issue opened with the
// version and PC filled in, for the zip to be attached to. No clips, pictures or sound are included.
internal static class BugReport
{
    internal const string IssueUrl = "https://github.com/" + Updater.Repository + "/issues/new";
    // Flashback's own logs in its data folder (the ones a normal run writes, not test output).
    private static readonly string[] Logs =
    {
        "settings.json", "crash.log", "last-error.txt", "last-recovery.txt", "last-display-recovery.txt", "last-start-timing.txt",
        "last-capture-performance.txt", "capture-gaps.log", "encoder-checks.txt"
    };

    internal static async Task<string> CreateAsync(IEnumerable<string> messages, string? lastClip, string ffmpeg, string? folder = null)
    {
        string name = $"Flashback-bug-report-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.zip";
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string path = Path.Combine(folder ?? (Directory.Exists(desktop) ? desktop : Storage.Root), name);
        string system = SystemSummary();
        string clip = lastClip != null && File.Exists(lastClip) ? await ClipDetailsAsync(ffmpeg, lastClip) : "No saved clip found.";
        await Task.Run(() =>
        {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            Add(zip, "system.txt", system);
            Add(zip, "messages.txt", string.Join(Environment.NewLine, messages));
            Add(zip, "newest-clip.txt", clip);
            foreach (var log in Logs)
            {
                var file = Path.Combine(Storage.Root, log);
                if (!File.Exists(file)) continue;
                // Big logs: the end is what matters.
                var text = ReadShared(file);
                Add(zip, log, text.Length > 200_000 ? text[^200_000..] : text);
            }
        });
        return path;
    }
    private static void Add(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }
    private static string ReadShared(string file)
    {
        try { using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); using var reader = new StreamReader(stream); return reader.ReadToEnd(); }
        catch (Exception ex) { return "Couldn't read: " + ex.Message; }
    }

    // One line each for the version, Windows, the processor and the graphics cards: also what goes in the issue.
    internal static string SystemSummary()
    {
        var lines = new List<string>
        {
            $"Flashback {typeof(BugReport).Assembly.GetName().Version?.ToString(3)} ({Path.GetFileName(AppContext.BaseDirectory.TrimEnd('\\'))})",
            $"Windows {WindowsVersion()}",
            $"Processor: {Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null)?.ToString()?.Trim() ?? "unknown"} ({Environment.ProcessorCount} threads)",
            $"Memory: {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0:0} GB",
        };
        lines.AddRange(GraphicsCards().Select(g => "Graphics: " + g));
        return string.Join(Environment.NewLine, lines);
    }
    private static string WindowsVersion()
    {
        const string key = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        string display = Registry.GetValue(key, "DisplayVersion", null) as string ?? "";
        // Windows 11 still says 10.0; its builds start at 22000.
        var v = Environment.OSVersion.Version;
        return $"{(v.Major == 10 && v.Build >= 22000 ? 11 : v.Major)} {display} (build {v.Build}.{Registry.GetValue(key, "UBR", 0)})".Replace("  ", " ");
    }
    private static IEnumerable<string> GraphicsCards()
    {
        var cards = new List<string>();
        try
        {
            using var factory = CreateDXGIFactory1<IDXGIFactory1>();
            for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
                using (adapter)
                {
                    var d = adapter.Description1;
                    if ((d.Flags & AdapterFlags.Software) != 0) continue;
                    cards.Add($"{d.Description} ({d.DedicatedVideoMemory / 1048576} MB)");
                }
        }
        catch (Exception ex) { cards.Add("unknown (" + ex.Message + ")"); }
        return cards;
    }
    // ffmpeg's description of the newest clip (streams, lengths, when picture and sound start), not the clip.
    private static async Task<string> ClipDetailsAsync(string ffmpeg, string clip)
    {
        try
        {
            var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var arg in new[] { "-hide_banner", "-nostdin", "-i", clip }) info.ArgumentList.Add(arg);
            using var probe = Process.Start(info)!;
            var output = probe.StandardOutput.ReadToEndAsync();
            string text = await probe.StandardError.ReadToEndAsync();
            await probe.WaitForExitAsync(); await output;
            // The file's name only: the folder can hold the Windows user name.
            text = text.Replace(clip, Path.GetFileName(clip));
            var (video, sound) = await ContinuityDiagnostics.StartsAsync(ffmpeg, clip);
            return text + Environment.NewLine + $"First picture at {video:0.000} s, first sound at {sound:0.000} s";
        }
        catch (Exception ex) { return "Couldn't read the newest clip: " + ex.Message; }
    }
    // The new-issue page with the version and PC filled in, and a place to describe the problem.
    internal static string IssueLink(string zipName)
    {
        string body = "**What happened?**\n\n\n**What did you expect?**\n\n\n**Steps to make it happen again (if you know them)**\n\n\n"
            + $"---\nPlease drag **{zipName}** from your desktop into this box to attach it.\n\n```\n{SystemSummary()}\n```\n";
        return $"{IssueUrl}?labels=bug&title={Uri.EscapeDataString("Bug: ")}&body={Uri.EscapeDataString(body)}";
    }
}
