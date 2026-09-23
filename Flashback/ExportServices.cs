using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Flashback;

internal sealed record ShareExportOptions(double TargetMb = 0, int Height = 0, int Fps = 0)
{
    internal long MaxBytes => (long)(TargetMb * 1_000_000);
    internal void Validate()
    {
        if (!double.IsFinite(TargetMb) || TargetMb < 0 || TargetMb > 10000 || (TargetMb > 0 && TargetMb < 1))
            throw new ArgumentException("Choose a file limit from 1 to 10,000 MB, or 0 for no size limit.");
        if (Height is not (0 or 720 or 1080) || Fps is not (0 or 30 or 60)) throw new ArgumentException("Choose a supported export size and frame rate.");
    }
    internal int VideoBitrate(double seconds, bool audio)
    {
        Validate();
        double rate = MaxBytes * .92 * 8 / seconds - (audio ? 128000 : 0);
        if (!double.IsFinite(rate) || rate < 100000) throw new ArgumentException("That size limit is too small. Shorten the selection or raise the limit.");
        return (int)Math.Min(rate, 100_000_000);
    }
}

internal static class ExportServices
{
    // Only explicit editor work uses this gate. Capture and replay saving never wait here.
    internal static readonly SemaphoreSlim Gate = new(1, 1);
    internal static string Number(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture);

    internal static async Task RunAsync(IEnumerable<string> arguments, CancellationToken token, IProgress<double>? progress = null, double duration = 1)
    {
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-nostats", "-progress", "pipe:1" }.Concat(arguments)) info.ArgumentList.Add(arg);
        token.ThrowIfCancellationRequested();
        using var job = new ChildProcessJob();
        using var process = Process.Start(info) ?? throw new IOException("Could not start the exporter.");
        job.Add(process); process.PriorityClass = ProcessPriorityClass.BelowNormal;
        var errors = process.StandardError.ReadToEndAsync();
        var output = Task.Run(async () => {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
                if (line.StartsWith("out_time_us=") && long.TryParse(line[12..], out var time))
                    progress?.Report(Math.Clamp(time / 1_000_000d / duration, 0, .99));
        });
        try { await process.WaitForExitAsync(token); }
        catch { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); await output; await errors; throw; }
        await output; string error = await errors;
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new IOException("Export failed: " + error);
    }

    internal static async Task<ClipResult> PreciseAsync(string source, string destination, IEnumerable<KeepSection> sections,
        IProgress<double>? progress, CancellationToken token, bool syntheticEncoder = false, ShareExportOptions? options = null)
    {
        source = Path.GetFullPath(source); destination = Path.GetFullPath(destination);
        if (source.Equals(destination, StringComparison.OrdinalIgnoreCase) || File.Exists(destination)) throw new IOException("Choose a new filename. Existing files are never overwritten.");
        var media = ClipMedia.Read(source); var ranges = ClipEditor.Validate(sections, media.Duration);
        options ??= new(); options.Validate(); double total = ranges.Sum(s => s.Duration);
        int bitrate = options.TargetMb > 0 ? options.VideoBitrate(total, media.HasAudio) : 0;
        await Gate.WaitAsync(token);
        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            Storage.EnsureWritable(Path.GetDirectoryName(destination)!);
            var encoder = syntheticEncoder ? null : await VideoEncoder.SelectAsync(Path.Combine(AppContext.BaseDirectory,"tools","ffmpeg.exe"), Storage.Load(out _).Encoder, null, null, token);
            // Seek each input at the demuxer before decoding. Only retained sections enter the graph.
            // Thread counts are capped per input; no preview decoder or thumbnail job is started here.
            var inputs = new List<string>(); var filters = new List<string>(); var labels = "";
            for (int i = 0; i < ranges.Count; i++)
            {
                var range = ranges[i];
                inputs.AddRange(new[] { "-threads", "1", "-ss", Number(range.Start), "-t", Number(range.Duration), "-i", source });
                filters.Add($"[{i}:v:0]trim=duration={Number(range.Duration)},setpts=PTS-STARTPTS[v{i}]");
                labels += $"[v{i}]";
                if (media.HasAudio) { filters.Add($"[{i}:a:0]atrim=duration={Number(range.Duration)},asetpts=PTS-STARTPTS[a{i}]"); labels += $"[a{i}]"; }
            }
            filters.Add(labels + $"concat=n={ranges.Count}:v=1:a={(media.HasAudio ? 1 : 0)}[joined]" + (media.HasAudio ? "[a]" : ""));
            var conversion = new List<string>();
            if (options.Height > 0) conversion.Add($"scale=w=-2:h='min(ih,{options.Height})':flags=fast_bilinear");
            if (options.Fps > 0) conversion.Add("fps=" + Number(Math.Min(options.Fps, media.FrameRate)));
            conversion.Add(encoder?.IsAmd == true ? "format=nv12,hwupload" : "format=yuv420p");
            filters.Add("[joined]" + string.Join(',', conversion) + "[v]");
            // One bounded retry handles encoder/container overhead. Oversize output is never published as a success.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var args = new List<string>();
                if (encoder?.IsAmd == true) args.AddRange(new[] { "-init_hw_device", $"d3d11va=exportgpu:{encoder.Adapter.Index}", "-filter_hw_device", "exportgpu" });
                args.AddRange(inputs); args.AddRange(new[] { "-filter_complex_threads", "1", "-filter_complex", string.Join(';',filters), "-map", "[v]" });
                if (media.HasAudio) args.AddRange(new[] { "-map", "[a]", "-c:a", "aac", "-b:a", "128k" });
                if (syntheticEncoder) args.AddRange(new[] { "-c:v", "libx264", "-preset", "ultrafast", "-threads", "2" });
                else if (bitrate == 0) args.AddRange(encoder!.EncodingArguments(new Settings(), export: true));
                else args.AddRange(encoder!.IsAmd
                    ? new[] { "-c:v", "h264_amf", "-usage", "transcoding", "-quality", "speed", "-rc", "cbr", "-preanalysis", "0", "-vbaq", "0" }
                    : new[] { "-c:v", "h264_nvenc", "-preset", "p1", "-rc", "cbr", "-rc-lookahead", "0", "-multipass", "disabled" });
                if (bitrate > 0) args.AddRange(new[] { "-b:v", bitrate.ToString(CultureInfo.InvariantCulture), "-maxrate", bitrate.ToString(CultureInfo.InvariantCulture), "-bufsize", (bitrate * 2L).ToString(CultureInfo.InvariantCulture) });
                else if (syntheticEncoder) args.AddRange(new[] { "-crf", "20" });
                args.AddRange(new[] { "-bf", "0", "-fps_mode", "vfr", "-movflags", "+faststart", "-f", "mp4", temp });
                await RunAsync(args, token, progress, total);
                if (options.TargetMb == 0 || new FileInfo(temp).Length <= options.MaxBytes) break;
                long actual = new FileInfo(temp).Length;
                File.Delete(temp);
                if (attempt == 1) throw new IOException("The encoder could not meet this file limit. Choose a larger limit or a shorter selection.");
                bitrate = Math.Max(100000, (int)(bitrate * (double)options.MaxBytes / actual * .85));
            }
            var verified = ClipMedia.Read(temp);
            double tolerance = Math.Max(.25, ranges.Count / media.FrameRate + .05);
            if (Math.Abs(verified.Duration - total) > tolerance || verified.HasAudio != media.HasAudio) throw new IOException("Export timing or audio did not match the selected sections.");
            token.ThrowIfCancellationRequested(); File.Move(temp, destination);
            var result = new ClipResult(destination, verified.Duration, new DirectoryInfo(Path.GetDirectoryName(destination)!).Name, DateTimeOffset.Now);
            try { ClipLibrary.Remember(result); } catch { }
            progress?.Report(1); return result;
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } finally { Gate.Release(); } }
    }

    internal static async Task SnapshotAsync(string source, string destination, double time, CancellationToken token)
    {
        string ext = Path.GetExtension(destination).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg")) throw new ArgumentException("Choose PNG or JPG.");
        if (File.Exists(destination)) throw new IOException("Choose a new filename. Existing files are never overwritten.");
        var media = ClipMedia.Read(source);
        time = Math.Clamp(time, 0, Math.Max(0, media.Duration - 1 / media.FrameRate));
        string temp = destination + "." + Guid.NewGuid().ToString("N") + ext;
        await Gate.WaitAsync(token);
        try
        {
            await RunAsync(new[] { "-ss", Number(time), "-i", source, "-frames:v", "1", "-an", "-c:v", ext==".png" ? "png" : "mjpeg", "-q:v", "2", "-threads", "1", "-update", "1", temp }, token);
            if (!File.Exists(temp) || new FileInfo(temp).Length == 0) throw new IOException("No snapshot frame was available.");
            token.ThrowIfCancellationRequested(); File.Move(temp,destination);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } finally { Gate.Release(); } }
    }
}
