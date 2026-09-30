using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Flashback;

// Decodes one audio track to 8 kHz mono and keeps a peak per 10 ms for the timeline.
// Runs once per opened clip at below-normal priority; nothing is kept on disk.
internal static class AudioWaveforms
{
    // Each audio track's name (its title or handler name), in track order, from ffmpeg's summary of the file.
    internal static async Task<IReadOnlyList<string>> TitlesAsync(string path, CancellationToken token)
    {
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-hide_banner", "-nostdin", "-i", path }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Could not read the audio tracks.");
        string text = await process.StandardError.ReadToEndAsync(token).ConfigureAwait(false);
        await process.WaitForExitAsync(token).ConfigureAwait(false);
        var titles = new List<string>(); bool audio = false;
        foreach (var line in text.Split('\n'))
        {
            if (line.Contains("Stream #", StringComparison.Ordinal)) { audio = line.Contains(": Audio:", StringComparison.Ordinal); if (audio) titles.Add(""); continue; }
            var m = System.Text.RegularExpressions.Regex.Match(line, @"^\s+(title|handler_name)\s*:\s?(.*?)\s*$");
            // A title wins over a handler name.
            if (audio && m.Success && (titles[^1].Length == 0 || m.Groups[1].Value == "title")) titles[^1] = m.Groups[2].Value;
        }
        return titles;
    }

    internal static async Task<float[]> LoadAsync(string path, int track, CancellationToken token)
    {
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-threads", "1", "-i", path, "-map", $"0:a:{track}", "-ac", "1", "-ar", "8000", "-f", "s16le", "-" })
            info.ArgumentList.Add(arg);
        using var job = new ChildProcessJob();
        using var process = Process.Start(info) ?? throw new IOException("Could not read the audio.");
        job.Add(process);
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        var errors = process.StandardError.ReadToEndAsync();
        var peaks = new List<float>(8192);
        var stream = process.StandardOutput.BaseStream; var buffer = new byte[16000];
        int inBlock = 0; float peak = 0; int carry = -1;
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                int i = 0;
                if (carry >= 0) { Add((short)(carry | buffer[0] << 8)); i = 1; carry = -1; }
                for (; i + 1 < read; i += 2) Add((short)(buffer[i] | buffer[i + 1] << 8));
                if (i < read) carry = buffer[i];
            }
            await process.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch { try { process.Kill(true); } catch { } throw; }
        await errors.ConfigureAwait(false);
        if (inBlock > 0) peaks.Add(peak);
        return peaks.ToArray();

        void Add(short sample)
        {
            peak = Math.Max(peak, Math.Abs(sample / 32768f));
            if (++inBlock < 80) return; // 80 samples at 8 kHz = 10 ms
            peaks.Add(MathF.Sqrt(peak)); peak = 0; inBlock = 0; // square root lifts quiet speech so it stays visible
        }
    }
}

// Waveforms for sound files and videos added to the editor, loaded once per file in the background (one
// at a time, at low priority) and kept while the app runs. Get returns null until a file's is ready, then
// Loaded fires so the timeline can draw it.
internal static class SoundPeaks
{
    private static readonly Dictionary<string, float[]> cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> loading = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim oneAtATime = new(1, 1);
    internal static event Action? Loaded;
    internal static float[]? Get(string path)
    {
        if (!PerformanceOptions.Waveforms || string.IsNullOrEmpty(path)) return null;
        lock (cache)
        {
            if (cache.TryGetValue(path, out var peaks)) return peaks;
            if (!loading.Add(path)) return null;
        }
        _ = Task.Run(async () =>
        {
            float[] peaks;
            await oneAtATime.WaitAsync().ConfigureAwait(false);
            try { peaks = File.Exists(path) ? await AudioWaveforms.LoadAsync(path, 0, CancellationToken.None).ConfigureAwait(false) : Array.Empty<float>(); }
            catch { peaks = Array.Empty<float>(); } // No sound, or unreadable: nothing to draw.
            finally { oneAtATime.Release(); }
            lock (cache)
            {
                if (cache.Count > 64) cache.Clear();
                cache[path] = peaks; loading.Remove(path);
            }
            Loaded?.Invoke();
        });
        return null;
    }
}
