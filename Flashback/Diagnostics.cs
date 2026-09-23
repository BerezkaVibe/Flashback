using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Flashback;

public static class Diagnostics
{
    public static async Task RunAsync(bool hardware)
    {
        Directory.CreateDirectory(Storage.Root);
        var checks = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("FAILED: " + message);
            checks.Add(message);
            File.AppendAllText(Path.Combine(Storage.Root, "test-progress.txt"), "PASS " + message + Environment.NewLine);
        }
        var example = SegmentIndex.Parse("part-000000000.ts,0,2\npart-000000001.ts,2,4\npart-000000002.ts,4,6\n../../x,0,2\npart-000000003.ts,6,");
        Check(example.Count == 3, "Reject incomplete and unsafe segment index entries");
        Check(SegmentIndex.Select(example, 3.1, 30).Count == 2, "Early saves include the segment containing the hotkey moment");
        var longBuffer = Enumerable.Range(0, 200).Select(i => new Segment($"part-{i:000000000}.ts", i * 2, i * 2 + 2)).ToList();
        foreach (var length in Enumerable.Range(1, 10).Select(i => i * 30))
            Check(Math.Abs(SegmentIndex.Select(longBuffer, 397, length).Sum(s => s.Duration) - length) < .001, $"Select a {length}-second replay from a full buffer");
        Check(Storage.SafeName("CON") == "_CON" && Storage.SafeName("bad/name:*") == "bad_name__", "Sanitize game folder names and Windows reserved names");
        var s = new Settings { ReplaySeconds = 30, Height = hardware ? 1080 : 720, FrameRate = hardware ? 60 : 30, OutputFolder = Path.Combine(Storage.Root, "clips ' test"), DesktopAudio = true };
        Storage.Save(s);
        Check(Storage.Load(out _).ReplaySeconds == 30, "Persist and restore recording settings");
        using (var hotkeyA = new Hotkeys(allowShared: false))
        using (var hotkeyB = new Hotkeys(allowShared: false))
        {
            hotkeyA.Register("Ctrl+Shift+F11");
            bool conflict = false;
            try { hotkeyB.Register("Ctrl+Shift+F11"); } catch (InvalidOperationException) { conflict = true; }
            Check(conflict, "Report global hotkey conflicts");
        }
        var command = Recorder.BuildArguments(s, Storage.Root, null, false);
        Check((command.Contains("h264_nvenc") || command.Contains("h264_amf")) && !command.Any(a => a.Contains("libx264") || a.Contains("scale=")), "Production capture uses hardware H.264 with no CPU video scaling or encoding");
        var clips = new List<ClipResult>();
        await using var recorder = new Recorder();
        await recorder.StartAsync(s, synthetic: !hardware);
        Check(recorder.IsRecording, hardware ? "Live D3D11 / hardware H.264 / desktop audio startup" : "Synthetic capture startup");
        await Task.Delay(4500);
        var first = await recorder.SaveAsync("Game: test / one", DateTimeOffset.Now);
        clips.Add(first);
        Check(File.Exists(first.Path) && first.Duration > 2 && first.Duration < 30, "Save a partially filled buffer to a sanitized game folder");
        Check(recorder.IsRecording, "Saving a clip does not stop the capture process");
        var wait = Stopwatch.StartNew();
        while (recorder.RecordedSeconds < 37 && wait.Elapsed.TotalSeconds < 50) await Task.Delay(500);
        var secondTask = recorder.SaveAsync("Rainbow Six Siege", DateTimeOffset.Now);
        await Task.Delay(500);
        var overlapping = recorder.SaveAsync("Other", DateTimeOffset.Now);
        Check(File.Exists((await overlapping).Path), "Repeated hotkeys queue independent clip saves");
        var second = await secondTask; clips.Add(second);
        Check(second.Duration >= 29 && second.Duration <= 32.2, "A filled buffer saves the selected 30-second duration");
        Check(Directory.EnumerateFiles(Path.Combine(Storage.Root, "buffer"), "*.ts", SearchOption.AllDirectories).Count() <= 22, "Old segments are removed and disk buffer stays bounded");
        foreach (var clip in clips)
        {
            var pinfo = new ProcessStartInfo(recorder.FfmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-i", clip.Path, "-f", "null", "-" }) pinfo.ArgumentList.Add(arg);
            using var decode = Process.Start(pinfo)!;
            var error = decode.StandardError.ReadToEndAsync(); var output = decode.StandardOutput.ReadToEndAsync();
            await decode.WaitForExitAsync();
            var errors = await error; await output;
            Check(decode.ExitCode == 0 && string.IsNullOrWhiteSpace(errors), "Saved MP4 fully decodes without audio/video errors: " + Path.GetFileName(clip.Path));
        }
        await recorder.StopAsync();
        Check(!recorder.IsRecording && !Directory.EnumerateFiles(Path.Combine(Storage.Root, "buffer"), "*.ts", SearchOption.AllDirectories).Any(), "Pause stops the encoder and clears temporary segments");
        Check(clips.All(c => File.Exists(c.Path)), "Pausing preserves saved clips");
        // Exercise restart and cleanup of the same instance.
        s.DesktopAudio = false;
        s.ReplaySeconds = 300;
        await recorder.StartAsync(s, synthetic: !hardware);
        Check(recorder.IsRecording, "Recording restarts after pause, including audio-off mode");
        var silentClip = await recorder.SaveAsync("No audio", DateTimeOffset.Now);
        Check(File.Exists(silentClip.Path), "Save an MP4 with audio disabled");
        Check(silentClip.Duration >= 2 && silentClip.Duration < 15, "A 5-minute setting saves available footage immediately after startup without waiting for 5 minutes");
        clips.Add(silentClip);
        await recorder.StopAsync();
        File.WriteAllText(Path.Combine(Storage.Root, "test-results.json"), JsonSerializer.Serialize(new { mode = hardware ? "hardware" : "synthetic", checks, clips }, new JsonSerializerOptions { WriteIndented = true }));
    }
}

