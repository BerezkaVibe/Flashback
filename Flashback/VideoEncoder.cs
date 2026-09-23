using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Flashback;

internal sealed record VideoAdapter(int Index, string Name, uint VendorId);
internal enum AmdConversion { Direct3D11, Amf, Compatibility }
internal sealed record VideoEncoder(VideoAdapter Adapter, AmdConversion Conversion = AmdConversion.Direct3D11)
{
    internal bool IsAmd => Adapter.VendorId == 0x1002;
    internal string Name => IsAmd ? "AMD AMF" : "NVIDIA NVENC";
    internal string Codec => IsAmd ? "h264_amf" : "h264_nvenc";
    internal static readonly string[] Preferences = { "Automatic", "NVIDIA NVENC", "AMD AMF" };
    private static readonly ConcurrentDictionary<string, bool> verified = new();
    private static readonly SemaphoreSlim probeLock = new(1, 1);

    internal static IReadOnlyList<VideoEncoder> Candidates(string preference, IReadOnlyList<VideoAdapter> adapters, CaptureDisplay? display)
    {
        if (!Preferences.Contains(preference)) throw new ArgumentException("Choose a supported hardware encoder.");
        return adapters.Where(a => a.VendorId is 0x10de or 0x1002)
            .Select(a => new VideoEncoder(a))
            .Where(e => preference == "Automatic" || e.Name == preference)
            // AMD surfaces must belong to its encoding adapter. NVIDIA retains the
            // existing CUDA transfer route for displays on integrated graphics.
            .Where(e => !e.IsAmd || display == null || e.Adapter.Index == display.AdapterIndex)
            .OrderBy(e => display?.AdapterIndex == e.Adapter.Index ? 0 : 1)
            .ThenBy(e => e.IsAmd ? 1 : 0).ThenBy(e => e.Adapter.Index).ToArray();
    }
    internal static async Task<VideoEncoder> SelectAsync(string ffmpeg, string preference, CaptureDisplay? display, Action<string>? report, CancellationToken token)
    {
        if (!File.Exists(ffmpeg)) throw new FileNotFoundException("The bundled recorder is missing. Extract the entire ZIP including tools.", ffmpeg);
        var candidates = Candidates(preference, CaptureDisplay.EnumerateAdapters(), display);
        return await SelectCandidatesAsync(candidates, async encoder =>
        {
            var key = Path.GetFullPath(ffmpeg) + File.GetLastWriteTimeUtc(ffmpeg).Ticks + encoder;
            await probeLock.WaitAsync(token);
            try
            {
                if (verified.ContainsKey(key)) return null;
                var error = await ProbeAsync(ffmpeg, encoder, token);
                if (error == null) verified[key] = true;
                return error;
            }
            finally { probeLock.Release(); }
        }, report, token);
    }
    internal static async Task<VideoEncoder> SelectCandidatesAsync(IReadOnlyList<VideoEncoder> candidates, Func<VideoEncoder, Task<string?>> probe, Action<string>? report, CancellationToken token)
    {
        var failures = new List<string>();
        foreach (var candidate in candidates.SelectMany(c => c.IsAmd
            ? new[] { c with { Conversion = AmdConversion.Direct3D11 }, c with { Conversion = AmdConversion.Amf }, c with { Conversion = AmdConversion.Compatibility } }
            : new[] { c }))
        {
            token.ThrowIfCancellationRequested();
            var error = await probe(candidate);
            token.ThrowIfCancellationRequested();
            if (error == null) { report?.Invoke($"Hardware encoder: {candidate.Name}; DXGI adapter {candidate.Adapter.Index} ({candidate.Adapter.Name}); conversion: {(candidate.IsAmd ? candidate.Conversion : "encoder native")}." + (candidate.IsAmd && candidate.Conversion == AmdConversion.Compatibility ? " Compatibility conversion uses CPU resizing/color conversion; H.264 encoding remains on the AMD GPU." : "")); return candidate; }
            var detail = $"{candidate.Name} on {candidate.Adapter.Name}{(candidate.IsAmd ? " (" + candidate.Conversion + " conversion)" : "")}: {error}";
            failures.Add(detail); report?.Invoke(detail);
        }
        throw new InvalidOperationException("No usable hardware H.264 encoder was found. Flashback supports NVIDIA NVENC and AMD AMF on Windows. Update the GPU driver and check that the GPU has a hardware video encoder. For AMD, select a display connected to the AMD GPU. CPU encoding is not enabled.\n" + string.Join("\n", failures));
    }
    internal string[] EncodingArguments(Settings settings, bool export = false)
    {
        if (IsAmd) return new[] { "-c:v", Codec, "-usage", "ultralowlatency", "-quality", "speed", "-profile:v", "high", "-rc", "vbr_peak", "-b:v", (export ? 24 : settings.BitrateMbps) + "M", "-maxrate", (export ? 32 : settings.BitrateMbps) + "M", "-bufsize", (export ? 64 : settings.BitrateMbps * 2) + "M", "-preanalysis", "0", "-vbaq", "0", "-preencode", "0", "-async_depth", "4", "-forced_idr", "1" };
        if (export) return new[] { "-c:v", Codec, "-preset", "p1", "-rc", "vbr", "-cq", "20", "-b:v", "0", "-rc-lookahead", "0", "-multipass", "disabled" };
        return new[] { "-c:v", Codec, "-preset", "p1", "-tune", "ll", "-profile:v", "high", "-rc", "vbr", "-cq", settings.Quality == "High" ? "19" : settings.Quality == "Compact" ? "27" : "23", "-b:v", settings.BitrateMbps + "M", "-maxrate", settings.BitrateMbps + "M", "-bufsize", settings.BitrateMbps * 2 + "M", "-rc-lookahead", "0", "-spatial-aq", "0", "-temporal-aq", "0", "-multipass", "disabled" };
    }
    internal static string AmdScale(int width, int height) => $"vpp_amf=w={width}:h={height}:format=nv12:scale_type=bilinear";
    internal string CaptureConversion(int width, int height, bool resize) => Conversion switch
    {
        AmdConversion.Direct3D11 => $"scale_d3d11=width={width}:height={height}:format=nv12",
        AmdConversion.Amf => AmdScale(width, height),
        _ => "hwdownload,format=bgra," + (resize ? $"scale={width}:{height}:flags=fast_bilinear," : "") + "format=nv12,hwupload"
    };
    internal static List<string> ProbeArguments(VideoEncoder encoder)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-init_hw_device", $"d3d11va=encode:{encoder.Adapter.Index}", "-filter_hw_device", "encode", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30,format=bgra", "-vf", encoder.IsAmd ? "hwupload," + encoder.CaptureConversion(320, 180, true) : "hwupload", "-frames:v", "12", "-an" };
        args.AddRange(encoder.EncodingArguments(new Settings { Height = 720, FrameRate = 30 }));
        args.AddRange(new[] { "-bf", "0", "-f", "null", "-" });
        return args;
    }
    internal static async Task<string?> ProbeAsync(string ffmpeg, VideoEncoder encoder, CancellationToken token)
    {
        var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in ProbeArguments(encoder)) start.ArgumentList.Add(arg);
        using var job = new ChildProcessJob();
        using var process = Process.Start(start) ?? throw new IOException("Could not check the hardware encoder.");
        job.Add(process); process.PriorityClass = ProcessPriorityClass.BelowNormal;
        var errors = process.StandardError.ReadToEndAsync(); var output = process.StandardOutput.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            await process.WaitForExitAsync(); await errors; await output;
            token.ThrowIfCancellationRequested(); return "Hardware encoder check timed out.";
        }
        var error = await errors; await output;
        return process.ExitCode == 0 ? null : string.IsNullOrWhiteSpace(error) ? "Hardware encoder check failed." : error.Trim();
    }
}
