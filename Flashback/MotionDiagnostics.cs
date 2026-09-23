using System;
using System.IO;
using System.Threading.Tasks;
using System.Diagnostics;

namespace Flashback;
internal static class MotionDiagnostics
{
    internal static async Task RunAsync(bool hardware = false)
    {
        Directory.CreateDirectory(Storage.Root);
        await using var recorder = new Recorder { UseBridgeForTests = true, SyncPatternForTests = true };
        string? fault = null; recorder.Faulted += (_, error) => fault = error;
        var settings = hardware ? Storage.Load(out _) : new Settings();
        settings.FrameRate = 60; settings.ReplaySeconds = 30; settings.Height = 1080;
        settings.DesktopAudio = true; settings.MicrophoneAudio = hardware && settings.MicrophoneAudio; settings.DesktopMuted = false;
        settings.OutputFolder = Path.Combine(Storage.Root, "clips");
        await recorder.StartAsync(settings, synthetic: !hardware);
        if (hardware)
        {
            await Task.Delay(1500);
            var info = new ProcessStartInfo(@"D:\ffmpeg\ffplay.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-autoexit", "-fs", "-volume", "35", "-window_title", "Flashback audio/video sync test", Path.Combine(Storage.Root, "sync-source.mp4") }) info.ArgumentList.Add(arg);
            using var player = Process.Start(info)!;
            var log = player.StandardError.ReadToEndAsync();
            try { await player.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25)); }
            finally { if (!player.HasExited) player.Kill(true); }
            File.WriteAllText(Path.Combine(Storage.Root, "player-log.txt"), await log);
            await Task.Delay(1000);
        }
        else await Task.Delay(12500);
        if (fault != null) throw new Exception(fault);
        var clip = await recorder.SaveAsync("Motion and sync", DateTimeOffset.Now);
        File.WriteAllText(Path.Combine(Storage.Root, "frame-report.txt"), recorder.VideoStats);
        File.WriteAllText(Path.Combine(Storage.Root, "motion-clip.txt"), clip.Path);
        await recorder.StopAsync();
    }
}
