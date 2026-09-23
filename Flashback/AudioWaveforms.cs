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
            while ((read = await stream.ReadAsync(buffer, token)) > 0)
            {
                int i = 0;
                if (carry >= 0) { Add((short)(carry | buffer[0] << 8)); i = 1; carry = -1; }
                for (; i + 1 < read; i += 2) Add((short)(buffer[i] | buffer[i + 1] << 8));
                if (i < read) carry = buffer[i];
            }
            await process.WaitForExitAsync(token);
        }
        catch { try { process.Kill(true); } catch { } throw; }
        await errors;
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
