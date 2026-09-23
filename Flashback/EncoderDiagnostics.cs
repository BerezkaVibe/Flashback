using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Flashback;

internal static class EncoderDiagnostics
{
    internal static async Task RunAsync()
    {
        Directory.CreateDirectory(Storage.Root);
        void Check(bool ok, string detail)
        {
            if (!ok) throw new Exception(detail);
            File.AppendAllText(Path.Combine(Storage.Root, "encoder-results.txt"), "PASS " + detail + Environment.NewLine);
        }
        var amd = new VideoAdapter(1, "AMD Radeon RX 7600", 0x1002);
        var nvidia = new VideoAdapter(0, "NVIDIA GeForce RTX 3070 Ti", 0x10de);
        var intel = new VideoAdapter(2, "Intel Graphics", 0x8086);
        var display = new CaptureDisplay(1, 0, "test", amd.Name, amd.VendorId, true);
        var topology = new[] { nvidia, amd, intel };
        var auto = VideoEncoder.Candidates("Automatic", topology, display);
        Check(auto.Count == 2 && auto[0].IsAmd, "RX 7600 connected display prefers AMD AMF without requiring NVIDIA");
        Check(VideoEncoder.Candidates("Automatic", new[] { amd }, display).Single().Codec == "h264_amf", "AMD-only PC selects AMF");
        Check(VideoEncoder.Candidates("NVIDIA NVENC", topology, display).Single().Codec == "h264_nvenc", "Explicit NVIDIA choice retains hybrid CUDA capture support");
        Check(VideoEncoder.Candidates("AMD AMF", topology, display).Single().Adapter.Index == 1, "Explicit AMD choice binds the correct adapter index");
        Check(VideoEncoder.Candidates("AMD AMF", topology, display with { AdapterIndex = 2, VendorId = 0x8086 }).Count == 0, "AMD rejects a display on an unrelated GPU rather than capturing the wrong display");
        Check(VideoEncoder.Candidates("Automatic", new[] { intel }, display).Count == 0, "Unsupported hardware does not silently choose software encoding");
        var probeOrder = new List<string>();
        var picked = await VideoEncoder.SelectCandidatesAsync(auto, candidate => { probeOrder.Add(candidate.Name); return Task.FromResult<string?>(candidate.IsAmd ? "Unsupported hardware" : null); }, null, CancellationToken.None);
        Check(!picked.IsAmd && probeOrder.SequenceEqual(new[] { "AMD AMF", "AMD AMF", "AMD AMF", "NVIDIA NVENC" }), "Automatic mode tries AMD conversion routes before another hardware encoder");
        var conversionOrder = new List<AmdConversion>();
        var compatible = await VideoEncoder.SelectCandidatesAsync(new[] { new VideoEncoder(amd) }, candidate =>
        {
            conversionOrder.Add(candidate.Conversion);
            return Task.FromResult<string?>(candidate.Conversion == AmdConversion.Compatibility ? null : "AMFConverter-Init() failed with error 10");
        }, null, CancellationToken.None);
        Check(compatible.IsAmd && compatible.Conversion == AmdConversion.Compatibility && conversionOrder.SequenceEqual(new[] { AmdConversion.Direct3D11, AmdConversion.Amf, AmdConversion.Compatibility }), "The supplied RX 7600 converter rejection can recover without rejecting the H.264 encoder");
        foreach (var mode in Enum.GetValues<AmdConversion>())
        {
            var path = new VideoEncoder(amd, mode);
            var probe = VideoEncoder.ProbeArguments(path);
            var capture = Recorder.BuildArguments(new Settings { Height = 720 }, Storage.Root, null, false, display: display, encoder: path);
            string stage = mode switch { AmdConversion.Direct3D11 => "scale_d3d11=", AmdConversion.Amf => "vpp_amf=", _ => "hwdownload,format=bgra," };
            Check(probe.Any(a => a.Contains(stage)) && capture.Any(a => a.Contains(stage)) && probe.Contains("h264_amf") && capture.Contains("h264_amf"), $"{mode} startup probe and real capture use the same conversion route and AMD hardware encoder");
        }
        try { await VideoEncoder.SelectCandidatesAsync(auto, _ => Task.FromResult<string?>("No video encoder"), null, CancellationToken.None); throw new Exception("Unsupported GPU accepted"); }
        catch (InvalidOperationException ex) { Check(ex.Message.Contains("No usable hardware") && ex.Message.Contains("CPU encoding is not enabled"), "Failed probes produce actionable compatibility details"); }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); int calls = 0;
            try { await VideoEncoder.SelectCandidatesAsync(auto, _ => { calls++; return Task.FromResult<string?>(null); }, null, cancelled.Token); throw new Exception("Cancellation ignored"); }
            catch (OperationCanceledException) { Check(calls == 0, "Cancellation prevents launching encoder probes"); }
        }
        var encoder = new VideoEncoder(amd);
        foreach (var height in new[] { 0, 720 })
        {
            var settings = new Settings { Height = height, Encoder = "AMD AMF" };
            var args = Recorder.BuildArguments(settings, Storage.Root, null, false, display: display, encoder: encoder);
            Check(args.Contains("h264_amf") && !args.Any(a => a.Contains("cuda") || a.Contains("nvenc") || a.Contains("libx264") || a.Contains("hwdownload") || a.Contains("scale=")), $"AMD {height}p capture contains no NVIDIA dependency or CPU scaling/encoding");
            Check(args.Contains("d3d11va=capture:1") && args.Any(a => a.Contains("ddagrab=output_idx=0:")), "AMD capture binds its adapter and local output index");
            Check(args.Any(a => a.Contains("scale_d3d11") && a.Contains("format=nv12")), "AMD capture converts BGRA to NV12 using D3D11, including native resolution");
            Check(args.Contains("-force_key_frames") && args.Contains("-forced_idr") && args.Contains("-segment_time"), "AMD replay retains independent segment keyframes");
        }
        foreach (var quality in new[] { "Compact", "Balanced", "High" })
        {
            var settings = new Settings { Encoder = "AMD AMF", Quality = quality };
            var args = encoder.EncodingArguments(settings);
            Check(args.Contains(settings.BitrateMbps + "M") && args.Contains("speed") && args.Contains("ultralowlatency"), $"AMD {quality} quality uses its bitrate cap and low-latency speed preset");
        }
        var preferences = new Settings { Encoder = "AMD AMF" };
        Storage.Save(preferences);
        Check(Storage.Load(out _).Encoder == "AMD AMF" && preferences.RequiresBufferRestart(new Settings()), "Encoder selection persists and changes restart the buffer");
        Check(new Settings().Encoder == "Automatic", "Existing settings migrate to automatic hardware selection");
        var ffmpeg = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe");
        var selected = await VideoEncoder.SelectAsync(ffmpeg, "Automatic", null, line => File.AppendAllText(Path.Combine(Storage.Root, "encoder-probe.txt"), line + "\n"), CancellationToken.None);
        Check(selected.Codec is "h264_nvenc" or "h264_amf", "Live installed GPU passes a real twelve-frame hardware encode probe: " + selected.Name);
        Storage.Save(new Settings());
    }
}
