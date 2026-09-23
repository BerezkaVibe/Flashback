using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Flashback;

internal static class ContinuityDiagnostics
{
    internal static async Task RunAsync(bool hardware = false)
    {
        Directory.CreateDirectory(Storage.Root);
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception(message);
            File.AppendAllText(Path.Combine(Storage.Root, "continuity-results.txt"), "PASS " + message + "\n");
        }
        await RecoveryDiagnostics.RunAsync(Check);
        Check(VideoFrameBridge.RetryDelay(0).TotalMilliseconds == 200 && VideoFrameBridge.RetryDelay(20).TotalSeconds == 2, "Display reconnect starts at 200 ms and caps retry delay at two seconds without a retry limit");
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
        Check(clips[1].Duration < 1.5 && clips[2].Duration < 1.5, "Successful saves reset the replay range without discarding footage after the previous press");
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
