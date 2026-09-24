using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Flashback;

// Compares exporting with the source decoded on the CPU against the graphics card, with and without a
// game-like load on the GPU (a CUDA filter holding it near 100%). Writes export-decode.txt.
internal static class ExportDecodeDiagnostics
{
    internal static async Task RunAsync()
    {
        Directory.CreateDirectory(Storage.Root);
        string ffmpeg = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe");
        string clip = Path.Combine(Storage.Root, "decode-source.mp4");
        // A minute of busy 1080p60 at a game-recording bitrate, encoded the way Flashback records.
        if (!File.Exists(clip))
            await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=60:duration=60,noise=alls=18:allf=t", "-f", "lavfi", "-i", "sine=frequency=300:sample_rate=48000:duration=60",
                "-c:v", "h264_nvenc", "-preset", "p4", "-b:v", "40M", "-maxrate", "40M", "-bufsize", "80M", "-g", "120", "-c:a", "aac", "-shortest", clip);
        Storage.Save(new Settings { OutputFolder = Path.Combine(Storage.Root, "clips") });
        var report = new StringBuilder($"Source: 60 s 1080p60 H.264 at 40 Mbps · {Environment.ProcessorCount} logical CPUs\n");
        var edits = new ShareExportOptions
        {
            SlowRegions = new[] { new SpeedRegion(10, 14, .5) },
            ZoomRegions = new[] { new ZoomRegion(20, 30, .5, .5, 2, ZoomPreset.BuiltIns[0].Curve) },
            Overlays = new[] { OverlayItem.NewText(0, 40, null) with { Text = "TEST CAPTION", Shadow = new OverlayShadow(true) } },
        };
        foreach (bool loaded in new[] { false, true })
        {
            Process? load = null;
            if (loaded)
            {
                // Keeps the GPU's shader cores busy like a game, leaving its video engines free.
                var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-init_hw_device", "cuda=cu", "-filter_hw_device", "cu", "-f", "lavfi", "-i", "color=gray:s=640x360:r=240",
                    "-vf", "format=yuv420p,hwupload,scale_cuda=w=3840:h=2160,bilateral_cuda=sigmaS=10:sigmaR=0.2:window_size=15", "-t", "100000", "-f", "null", "-" }) info.ArgumentList.Add(a);
                load = Process.Start(info)!; _ = load.StandardError.ReadToEndAsync();
                await Task.Delay(3000);
            }
            report.AppendLine(loaded ? "\nWith the GPU busy (game-like load at ~99%):" : "\nGPU idle:");
            try
            {
                foreach (var (name, hw) in new[] { ("CPU decode", (string?)null), ("GPU decode (d3d11va)", "d3d11va"), ("GPU decode (cuda)", "cuda") })
                    foreach (var (job, sections, options) in new[] { ("trim 30 s", new[] { new KeepSection(15, 45) }, new ShareExportOptions()), ("30 s with speed, zoom and text", new[] { new KeepSection(5, 35) }, edits) })
                    {
                        ExportServices.HardwareDecode = hw;
                        string output = Path.Combine(Storage.Root, $"decode-{Guid.NewGuid():N}.mp4");
                        var timer = Stopwatch.StartNew();
                        try
                        {
                            await ExportServices.PreciseAsync(clip, output, sections, null, CancellationToken.None, false, options);
                            var cpu = ExportServices.LastProcessCpu.TotalSeconds;
                            report.AppendLine($"  {name,-22} {job,-32} {timer.Elapsed.TotalSeconds,6:0.0} s   ffmpeg CPU {cpu,6:0.0} s ({cpu / timer.Elapsed.TotalSeconds:0.0} cores)");
                        }
                        catch (Exception ex) { report.AppendLine($"  {name,-22} {job,-32} failed: {ex.Message.Split('\n')[0]}"); }
                        finally { try { File.Delete(output); } catch { } }
                        File.WriteAllText(Path.Combine(Storage.Root, "export-decode.txt"), report.ToString());
                    }
            }
            finally { ExportServices.HardwareDecode = "d3d11va"; if (load != null) { try { load.Kill(true); } catch { } load.Dispose(); } }
        }
        File.WriteAllText(Path.Combine(Storage.Root, "export-decode.txt"), report.ToString());
    }
}
