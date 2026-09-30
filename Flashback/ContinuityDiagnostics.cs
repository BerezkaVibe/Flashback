using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Flashback;

internal static class ContinuityDiagnostics
{
    // When a clip's picture and sound start (their first packets' times), read without decoding.
    internal static async Task<(double Video, double Audio)> StartsAsync(string ffmpeg, string clip)
    {
        var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-i", clip, "-map", "0:v:0", "-map", "0:a:0", "-c", "copy", "-f", "framemd5", "-" }) info.ArgumentList.Add(arg);
        using var probe = Process.Start(info)!;
        var errors = probe.StandardError.ReadToEndAsync();
        var bases = new double[2]; var first = new[] { double.NaN, double.NaN };
        string? line;
        while ((line = await probe.StandardOutput.ReadLineAsync()) != null)
        {
            // "#tb 0: 1/90000" gives each stream's time base; packet lines are "stream, dts, pts, duration, size, hash".
            if (line.StartsWith("#tb "))
            {
                var parts = line[4..].Split(':', 2); var fraction = parts[1].Trim().Split('/');
                bases[int.Parse(parts[0])] = double.Parse(fraction[0], System.Globalization.CultureInfo.InvariantCulture) / double.Parse(fraction[1], System.Globalization.CultureInfo.InvariantCulture);
                continue;
            }
            if (line.StartsWith('#')) continue;
            var cells = line.Split(',');
            if (cells.Length < 3) continue;
            int stream = int.Parse(cells[0].Trim());
            double pts = long.Parse(cells[2].Trim()) * bases[stream];
            if (double.IsNaN(first[stream]) || pts < first[stream]) first[stream] = pts;
        }
        await probe.WaitForExitAsync(); await errors;
        return (first[0], first[1]);
    }

    internal static async Task RunAsync(bool hardware = false)
    {
        Directory.CreateDirectory(Storage.Root);
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception(message);
            File.AppendAllText(Path.Combine(Storage.Root, "continuity-results.txt"), "PASS " + message + "\n");
        }
        await RecoveryDiagnostics.RunAsync(Check);
        Check(VideoFrameBridge.RetryDelay(0).TotalMilliseconds == 0 && VideoFrameBridge.RetryDelay(1).TotalMilliseconds == 50 && VideoFrameBridge.RetryDelay(20).TotalSeconds == 1, "Display reconnect retries immediately and caps retry delay at one second without a retry limit");
        await using var recorder = new Recorder { UseBridgeForTests = true };
        string? fault = null; recorder.Faulted += (_, error) => fault = error;
        var settings = hardware ? Storage.Load(out _) : new Settings { MicrophoneAudio = true };
        settings.FrameRate = hardware ? 60 : 30; settings.Height = 1080; settings.ReplaySeconds = 30;
        settings.DesktopAudio = true; settings.OutputFolder = Path.Combine(Storage.Root, "clips");
        await recorder.StartAsync(settings, synthetic: !hardware);
        await Task.Delay(2500);
        long generation = recorder.Generation;
        recorder.InterruptVideoForTest(TimeSpan.FromSeconds(4));
        await Task.Delay(2500);
        var firstTask = recorder.SaveAsync("Continuity", DateTimeOffset.Now);
        await Task.Delay(400);
        var secondTask = recorder.SaveAsync("Continuity", DateTimeOffset.Now);
        await Task.Delay(400);
        var thirdTask = recorder.SaveAsync("Continuity", DateTimeOffset.Now);
        var clips = await Task.WhenAll(firstTask, secondTask, thirdTask);
        Check(clips.All(c => File.Exists(c.Path)) && clips.Select(c => c.Path).Distinct().Count() == 3, "Rapid save requests each produce a distinct clip while capture continues");
        // Clips start on the keyframe at or before where they're asked to (so picture and sound start together),
        // so one saved right after another reaches back up to a two-second segment into it.
        Check(clips[1].Duration < 3.6 && clips[2].Duration < 3.6, $"Successful saves reset the replay range without discarding footage after the previous press ({clips[1].Duration:0.00} s, {clips[2].Duration:0.00} s)");
        foreach (var clip in clips)
        {
            var (video, sound) = await StartsAsync(recorder.FfmpegPath, clip.Path);
            Check(Math.Abs(video - sound) < .15, $"A saved clip's picture and sound start together (picture {video:0.000} s, sound {sound:0.000} s)");
        }
        var info = new ProcessStartInfo(recorder.FfmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-hide_banner", "-i", clips[0].Path, "-vf", "blackdetect=d=0.5:pix_th=0.1", "-af", "silencedetect=n=-55dB:d=0.2", "-f", "null", "-" }) info.ArgumentList.Add(arg);
        using (var verify = Process.Start(info)!)
        {
            var output = await verify.StandardError.ReadToEndAsync(); await verify.WaitForExitAsync();
            File.WriteAllText(Path.Combine(Storage.Root, "media-verification.txt"), output);
            Check(verify.ExitCode == 0 && output.Contains("black_start:"), "Saved video decodes and contains a black placeholder during a forced display outage");
            Check(output.Contains("Audio: aac") && (hardware || !output.Contains("silence_start:")), hardware ? "Live audio track remains present during the display outage" : "Mixed audio remains audible throughout the display outage");
        }
        for (int i = 0; i < 5; i++)
        {
            int before = recorder.VideoConnections;
            recorder.InterruptVideoForTest(TimeSpan.FromMilliseconds(200));
            var wait = Stopwatch.StartNew();
            while (recorder.VideoConnections <= before && wait.Elapsed.TotalSeconds < 15) await Task.Delay(100);
            Check(recorder.VideoConnections > before && recorder.IsRecording && fault == null, "Display reconnect " + (i + 2) + " leaves the recorder running");
            File.AppendAllText(Path.Combine(Storage.Root,"reconnect-timing.txt"), $"Connection {i+2}: {wait.Elapsed.TotalMilliseconds:0} ms (includes requested 200 ms outage)\n");
            await Task.Delay(500);
        }
        Check(recorder.Generation == generation && recorder.RecordedSeconds > 5, "Repeated display failures never restart the audio or encoding session");
        await Task.Delay(3500);
        var resumed = await recorder.SaveAsync("Resumed", DateTimeOffset.Now);
        Check(File.Exists(resumed.Path), "Saving remains available after repeated display recovery");
        File.WriteAllText(Path.Combine(Storage.Root, "capture-report.txt"), recorder.LastStartupReport + $"\nMeasured FPS: {recorder.MeasuredFps}\nConnections: {recorder.VideoConnections}\n");
        await recorder.StopAsync();
        Check(!recorder.IsRecording, "Stop cancels capture retries and shuts down the recording session");
    }
}
