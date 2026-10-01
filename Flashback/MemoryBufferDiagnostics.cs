using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Flashback;

// --memory-buffer-test: the replay buffer held in memory. A real stream goes through the pipe in odd-sized
// pieces and must come out as the same bytes cut into whole chunks at the keyframes; then a recording in memory
// writes nothing to the buffer folder, stays bounded, saves clips that play, keeps its buffer across a restart,
// and a buffer too big for memory stays on disk. Writes memory-buffer-results.txt.
internal static class MemoryBufferDiagnostics
{
    internal static async Task RunAsync()
    {
        Directory.CreateDirectory(Storage.Root);
        var report = new StringBuilder(); int failures = 0;
        void Check(bool ok, string text) { report.AppendLine((ok ? "PASS " : "FAIL ") + text); if (!ok) failures++; File.WriteAllText(Path.Combine(Storage.Root, "memory-buffer-results.txt"), report.ToString()); }

        // ---- The stream and its chunks ----
        string ts = Path.Combine(Storage.Root, "stream.ts");
        await EditorDiagnostics.Ffmpeg("-y", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "12",
            "-c:v", "libx264", "-preset", "ultrafast", "-g", "60", "-bf", "0", "-force_key_frames", "expr:gte(t,n_forced*2)", "-c:a", "aac", "-b:a", "128k",
            "-mpegts_flags", "+pat_pmt_at_frames", "-muxdelay", "0", "-muxpreload", "0", "-f", "mpegts", ts);
        var original = await File.ReadAllBytesAsync(ts);
        using (var buffer = new MemoryBuffer())
        {
            string path = buffer.StartEpoch(30);
            using (var client = new NamedPipeClientStream(".", path[@"\\.\pipe\".Length..], PipeDirection.Out))
            {
                await client.ConnectAsync(5000);
                // Pieces that don't line up with packets: the reader must carry the rest of a packet to the next read.
                var sizes = new[] { 1000, 7, 4096, 183, 65536, 13, 188 * 5 + 1 }; int at = 0, i = 0;
                while (at < original.Length) { int n = Math.Min(sizes[i++ % sizes.Length], original.Length - at); await client.WriteAsync(original.AsMemory(at, n)); at += n; if (i % 9 == 0) await Task.Delay(1); }
            }
            await buffer.EndAsync(TimeSpan.FromSeconds(5));
            var segments = buffer.Segments();
            Check(segments.Count is >= 5 and <= 7, $"The stream is cut into chunks at its keyframes ({segments.Count} chunks)");
            Check(segments.Zip(segments.Skip(1)).All(p => Math.Abs(p.First.End - p.Second.Start) < 1e-6), "Each chunk ends where the next begins");
            Check(segments.Take(segments.Count - 1).All(s => Math.Abs(s.Duration - 2) < .06) && Math.Abs(segments[0].Start) < 1e-6 && Math.Abs(segments[^1].End - 12) < .12, $"Chunks are two seconds long and the stream is as long as it was (ends at {segments[^1].End:0.00} s)");
            using var joined = new MemoryStream();
            await buffer.WriteAsync(segments.Select(s => s.Name), joined);
            // Whatever comes before the first keyframe (a few audio packets) is let go; everything from there on is kept.
            var all = joined.ToArray();
            Check(all.Length > 0 && original.AsSpan(original.Length - all.Length).SequenceEqual(all) && original.Length - all.Length < 188 * 40, $"The chunks put together are the stream from its first keyframe, byte for byte ({all.Length} of {original.Length} bytes)");
            // Each chunk stands alone: it begins with the program tables, then a keyframe's picture.
            bool alone = true;
            foreach (var s in segments)
            {
                using var one = new MemoryStream(); await buffer.WriteAsync(new[] { s.Name }, one); var bytes = one.ToArray();
                alone &= bytes.Length % 188 == 0 && bytes[0] == 0x47 && (((bytes[1] & 0x1F) << 8) | bytes[2]) == 0 && bytes[188] == 0x47 && (((bytes[189] & 0x1F) << 8) | bytes[190]) is not 0;
            }
            Check(alone, "Each chunk begins with the stream's tables, so it can be played from its start");
            long before = buffer.Bytes;
            buffer.Keep(segments.Skip(3).Select(s => s.Name).ToHashSet(), segments[^1].Name);
            Check(buffer.Segments().Count == segments.Count - 3 && buffer.Bytes < before && !buffer.Has(segments[0].Name), $"Old chunks are let go and their memory is counted out ({before} to {buffer.Bytes} bytes)");
        }

        // ---- A recording in memory ----
        var settings = new Settings { DesktopAudio = true, MicrophoneAudio = false, FrameRate = 30, ReplaySeconds = 30, OutputFolder = Path.Combine(Storage.Root, "clips"), BufferInMemory = false };
        // The same recording kept on disk, to compare the clips with.
        double diskSecond = 0;
        await using (var recorder = new Recorder())
        {
            await recorder.StartAsync(settings, synthetic: true); await Task.Delay(7000);
            await recorder.SaveAsync("Disk buffer", DateTimeOffset.Now);
            using var t = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var restarted = Stopwatch.StartNew(); await recorder.RestartAfterCaptureLossAsync(settings, true, t.Token); double restartSeconds = restarted.Elapsed.TotalSeconds;
            await Task.Delay(7000);
            diskSecond = (await recorder.SaveAsync("Disk buffer", DateTimeOffset.Now)).Duration;
            report.AppendLine($"INFO on disk, the clip after a restart is {diskSecond:0.0} s (the restart took {restartSeconds:0.0} s): {recorder.LastSaveReport}");
            await recorder.StopAsync();
        }
        settings.BufferInMemory = true;
        await using (var recorder = new Recorder())
        {
            await recorder.StartAsync(settings, synthetic: true);
            Check(recorder.IsRecording && recorder.BufferInMemory && recorder.LastStartupReport.Contains("held in memory"), "A recording with the buffer in memory starts, and says so");
            await Task.Delay(7000);
            var session = recorder.SessionFolder!;
            Check(Directory.EnumerateFiles(session, "*", SearchOption.AllDirectories).All(f => !f.EndsWith(".ts") && !f.EndsWith("segments.csv")), "No segment files are written to the buffer folder while it records");
            Check(recorder.BufferBytes > 100_000 && recorder.MemoryChunks >= 2, $"The buffer fills in memory ({recorder.BufferBytes / 1024} KB, {recorder.MemoryChunks} chunks)");
            var first = await recorder.SaveAsync("Memory buffer", DateTimeOffset.Now);
            Check(File.Exists(first.Path) && first.Duration is > 4 and < 12, $"A clip saved from memory exists ({first.Duration:0.0} s)");
            // A restart that keeps the buffer, as after a lost display: footage from before and after is one clip.
            using var restartTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await recorder.RestartAfterCaptureLossAsync(settings, true, restartTimeout.Token);
            Check(recorder.IsRecording && recorder.BufferInMemory, "The recording restarts in memory");
            await Task.Delay(7000);
            var second = await recorder.SaveAsync("Memory buffer", DateTimeOffset.Now);
            report.AppendLine($"INFO in memory: {recorder.LastSaveReport}");
            var (video, sound) = await ContinuityDiagnostics.StartsAsync(recorder.FfmpegPath, first.Path);
            Check(Math.Abs(video - sound) < .15, $"The first clip's picture and sound start together (picture {video:0.000} s, sound {sound:0.000} s)");
            Check(await DecodesAsync(recorder.FfmpegPath, first.Path), "The first clip plays through without errors");
            var (video2, sound2) = await ContinuityDiagnostics.StartsAsync(recorder.FfmpegPath, second.Path);
            Check(Math.Abs(video2 - sound2) < .15, $"The clip from across the restart starts picture and sound together (picture {video2:0.000} s, sound {sound2:0.000} s)");
            Check(Directory.EnumerateDirectories(session, "save-*").Count() == 0, "The temporary files made for saving are gone afterwards");
            Check(File.Exists(second.Path) && Math.Abs(second.Duration - diskSecond) < 2.5 && await DecodesAsync(recorder.FfmpegPath, second.Path), $"A clip saved after the restart plays, and is as long as the disk buffer's ({second.Duration:0.0} s against {diskSecond:0.0} s)");
            // Keep going past the replay length: the buffer stays bounded.
            var wait = Stopwatch.StartNew();
            while (recorder.RecordedSeconds < 52 && wait.Elapsed.TotalSeconds < 70) await Task.Delay(500);
            Check(recorder.BufferedSeconds <= 31 && recorder.MemoryChunks <= 30, $"Old footage is let go as new arrives ({recorder.BufferedSeconds:0.0} s held in {recorder.MemoryChunks} chunks, {recorder.BufferBytes / 1024} KB)");
            var third = await recorder.SaveAsync("Memory buffer", DateTimeOffset.Now);
            Check(third.Duration is > 25 and < 34 && await DecodesAsync(recorder.FfmpegPath, third.Path), $"A full replay saves from memory ({third.Duration:0.0} s)");
            await recorder.StopAsync();
            Check(!recorder.IsRecording && recorder.BufferBytes == 0, "Pausing lets go of the memory");
        }

        // ---- Too big for memory ----
        var huge = new Settings { Height = 2160, FrameRate = 120, Quality = "High", ReplaySeconds = 300, BufferInMemory = true };
        Check(!huge.BufferFitsInMemory && huge.EstimatedBufferMb > 4096 && new Settings { ReplaySeconds = 60 }.BufferFitsInMemory, $"A buffer larger than memory may hold is kept on disk ({huge.EstimatedBufferMb:0} MB, limit {MemoryBuffer.LimitMb:0} MB)");
        Check(new Settings { BufferInMemory = true }.RequiresBufferRestart(new Settings()) && !new Settings().BufferInMemory, "Changing where the buffer is kept restarts it, and disk is the default");
        if (failures > 0) throw new Exception($"{failures} memory buffer checks failed; see memory-buffer-results.txt");
    }

    private static async Task<bool> DecodesAsync(string ffmpeg, string clip)
    {
        var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-i", clip, "-f", "null", "-" }) info.ArgumentList.Add(arg);
        using var decode = Process.Start(info)!;
        var error = decode.StandardError.ReadToEndAsync(); var output = decode.StandardOutput.ReadToEndAsync();
        await decode.WaitForExitAsync(); var errors = await error; await output;
        return decode.ExitCode == 0 && string.IsNullOrWhiteSpace(errors);
    }
}
