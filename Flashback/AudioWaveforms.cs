using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Flashback;

// Decodes one audio track to 8 kHz mono and keeps a peak per 10 ms for the timeline.
// Runs at below-normal priority (lowest when loading ahead); finished waveforms are kept on disk (LoadAllAsync).
internal static class AudioWaveforms
{
    // Each audio track's name (its title or handler name), in track order, from ffmpeg's summary of the file.
    internal static async Task<IReadOnlyList<string>> TitlesAsync(string path, CancellationToken token)
    {
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-hide_banner", "-nostdin", "-i", path }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Could not read the audio tracks.");
        OpenTiming.Mark("TitlesAsync: ffmpeg started");
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

    internal static async Task<float[]> LoadAsync(string path, int track, CancellationToken token, ProcessPriorityClass priority = ProcessPriorityClass.BelowNormal)
    {
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-threads", "1", "-i", path, "-map", $"0:a:{track}", "-ac", "1", "-ar", "8000", "-f", "s16le", "-" })
            info.ArgumentList.Add(arg);
        using var job = new ChildProcessJob();
        using var process = Process.Start(info) ?? throw new IOException("Could not read the audio.");
        job.Add(process);
        try { process.PriorityClass = priority; } catch { }
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

    // Peaks for several tracks of one file. A file's audio has to be read through the whole file (a video's
    // pictures sit between its sound), which costs far more than decoding, so every track not already kept
    // comes from one pass instead of one process each, and finished waveforms are kept on disk for next time.
    internal static async Task<float[][]> LoadAllAsync(string path, IReadOnlyList<int> tracks, CancellationToken token, ProcessPriorityClass priority = ProcessPriorityClass.BelowNormal)
    {
        var result = new float[tracks.Count][];
        string? key = CacheKey(path);
        var missing = new List<int>();
        for (int i = 0; i < tracks.Count; i++)
        {
            if (key != null && ReadCached(key, tracks[i]) is { } kept) result[i] = kept; else missing.Add(i);
        }
        if (missing.Count == 0) return result;
        var wanted = missing.Select(i => tracks[i]).Distinct().ToList();
        var fresh = new Dictionary<int, float[]>();
        if (wanted.Count > 1 && wanted.Count <= 32)
        {
            try { var merged = await LoadMergedAsync(path, wanted, token, priority).ConfigureAwait(false); for (int w = 0; w < wanted.Count; w++) fresh[wanted[w]] = merged[w]; }
            catch (OperationCanceledException) { throw; }
            catch { fresh.Clear(); } // Fall back to a pass for each track.
        }
        if (fresh.Count == 0)
        {
            var each = await Task.WhenAll(wanted.Select(t => LoadAsync(path, t, token, priority))).ConfigureAwait(false);
            for (int w = 0; w < wanted.Count; w++) fresh[wanted[w]] = each[w];
        }
        foreach (int i in missing) { result[i] = fresh[tracks[i]]; if (key != null) WriteCached(key, tracks[i], result[i]); }
        if (key != null) Prune();
        return result;
    }

    // Every wanted track decoded to 8 kHz mono and merged into one stream with a channel for each, so the
    // file is read and demuxed once.
    private static async Task<float[][]> LoadMergedAsync(string path, IReadOnlyList<int> tracks, CancellationToken token, ProcessPriorityClass priority)
    {
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        int n = tracks.Count;
        string graph = string.Join(";", tracks.Select((t, i) => $"[0:a:{t}]aresample=8000,aformat=channel_layouts=mono[a{i}]")) + ";" + string.Concat(Enumerable.Range(0, n).Select(i => $"[a{i}]")) + $"amerge=inputs={n}[o]";
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-threads", "1", "-i", path, "-filter_complex", graph, "-map", "[o]", "-f", "s16le", "-" }) info.ArgumentList.Add(arg);
        using var job = new ChildProcessJob();
        using var process = Process.Start(info) ?? throw new IOException("Could not read the audio.");
        job.Add(process);
        try { process.PriorityClass = priority; } catch { }
        var errors = process.StandardError.ReadToEndAsync();
        var peaks = Enumerable.Range(0, n).Select(_ => new List<float>(8192)).ToArray();
        var peak = new float[n]; int inBlock = 0, channel = 0; int carry = -1;
        var stream = process.StandardOutput.BaseStream; var buffer = new byte[16384 * 2];
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
        if (process.ExitCode != 0 || peaks[0].Count == 0) throw new IOException("The audio tracks could not be read together.");
        if (inBlock > 0) for (int c = 0; c < n; c++) peaks[c].Add(peak[c]);
        return peaks.Select(p => p.ToArray()).ToArray();

        void Add(short sample)
        {
            peak[channel] = Math.Max(peak[channel], Math.Abs(sample / 32768f));
            if (++channel < n) return;
            channel = 0;
            if (++inBlock < 80) return; // 80 samples at 8 kHz = 10 ms
            for (int c = 0; c < n; c++) { peaks[c].Add(MathF.Sqrt(peak[c])); peak[c] = 0; }
            inBlock = 0;
        }
    }

    // Kept waveforms: one small file per track, named for the file's path, size and time, so an edited or
    // replaced file never shows an old one. The newest few hundred are kept.
    private const int CacheVersion = 1;
    private static string CacheFolder => Path.Combine(Storage.Root, "waveforms");
    private static string? CacheKey(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return null;
            var hash = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes($"{file.FullName.ToLowerInvariant()}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{CacheVersion}"));
            return Convert.ToHexString(hash, 0, 10);
        }
        catch { return null; }
    }
    private static float[]? ReadCached(string key, int track)
    {
        try
        {
            var file = Path.Combine(CacheFolder, $"{key}-{track}.pk");
            if (!File.Exists(file)) return null;
            var bytes = File.ReadAllBytes(file);
            if (bytes.Length % 4 != 0) return null;
            var peaks = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, peaks, 0, bytes.Length);
            return peaks;
        }
        catch { return null; }
    }
    private static void WriteCached(string key, int track, float[] peaks)
    {
        try
        {
            Directory.CreateDirectory(CacheFolder);
            var bytes = new byte[peaks.Length * 4]; Buffer.BlockCopy(peaks, 0, bytes, 0, bytes.Length);
            var file = Path.Combine(CacheFolder, $"{key}-{track}.pk"); var temp = file + ".tmp";
            File.WriteAllBytes(temp, bytes); File.Move(temp, file, true);
        }
        catch { /* A waveform that cannot be kept is just made again next time. */ }
    }
    private static void Prune()
    {
        try
        {
            var files = new DirectoryInfo(CacheFolder).GetFiles("*.pk");
            if (files.Length <= 600) return;
            foreach (var old in files.OrderBy(f => f.LastWriteTimeUtc).Take(files.Length - 400)) try { old.Delete(); } catch { }
        }
        catch { }
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
            try { peaks = File.Exists(path) ? (await AudioWaveforms.LoadAllAsync(path, new[] { 0 }, CancellationToken.None).ConfigureAwait(false))[0] : Array.Empty<float>(); }
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
