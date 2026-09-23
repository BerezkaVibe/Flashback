using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Flashback;

internal static class AudioDiagnostics
{
    internal static async Task RunHardwareAsync()
    {
        Directory.CreateDirectory(Storage.Root);
        await using var recorder = new Recorder();
        var settings = Storage.Load(out _);
        settings.DesktopAudio = true; settings.MicrophoneAudio = true; settings.ReplaySeconds = 30;
        settings.FrameRate = 30; settings.Height = 720; settings.OutputFolder = Path.Combine(Storage.Root, "clips");
        await recorder.StartAsync(settings);
        var clip = await recorder.SaveAsync("hardware-speakers-and-microphone", DateTimeOffset.Now);
        await recorder.StopAsync();
        if (!ClipMedia.Read(clip.Path).HasAudio) throw new Exception("Mixed hardware clip has no audio track");
        var start = new ProcessStartInfo(recorder.FfmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-v", "error", "-i", clip.Path, "-f", "null", "-" }) start.ArgumentList.Add(arg);
        using var decoder = Process.Start(start)!;
        var errors = decoder.StandardError.ReadToEndAsync();
        await decoder.WaitForExitAsync();
        if (decoder.ExitCode != 0 || !string.IsNullOrWhiteSpace(await errors)) throw new Exception("Hardware clip did not fully decode: " + await errors);
        File.WriteAllText(Path.Combine(Storage.Root, "hardware-audio-results.txt"), "PASS Live NVENC capture with simultaneous speaker and microphone pipes exports a fully decodable video and audio clip.\n" + recorder.LastStartupReport);
    }
    internal static async Task RunAsync()
    {
        Directory.CreateDirectory(Storage.Root);
        void Check(bool ok, string detail)
        {
            if (!ok) throw new Exception(detail);
            File.AppendAllText(Path.Combine(Storage.Root, "audio-results.txt"), "PASS " + detail + Environment.NewLine);
        }
        foreach (var (speaker, mic) in new[] { (false, false), (true, false), (false, true), (true, true) })
        {
            var settings = new Settings { DesktopAudio = speaker, MicrophoneAudio = mic, FrameRate = 30, ReplaySeconds = 30, OutputFolder = Path.Combine(Storage.Root, "clips") };
            await using var recorder = new Recorder();
            await recorder.StartAsync(settings, synthetic: true);
            var clip = await recorder.SaveAsync($"speakers-{speaker}-mic-{mic}", DateTimeOffset.Now);
            await recorder.StopAsync();
            Check(File.Exists(clip.Path) && clip.Duration >= 2, $"Replay exports with speakers={speaker}, mic={mic}");
            var info = new ProcessStartInfo(recorder.FfmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-v", "error", "-i", clip.Path, "-map", "0:a:0", "-t", "1", "-ac", "1", "-ar", "48000", "-f", "f32le", "pipe:1" }) info.ArgumentList.Add(arg);
            using var decoder = Process.Start(info)!;
            var errors = decoder.StandardError.ReadToEndAsync();
            using var bytes = new MemoryStream();
            await decoder.StandardOutput.BaseStream.CopyToAsync(bytes);
            await decoder.WaitForExitAsync();
            if (!speaker && !mic) { Check(decoder.ExitCode != 0 && bytes.Length == 0 && (await errors).Contains("matches no streams"), "Disabling both sources produces video with no audio track"); continue; }
            Check(decoder.ExitCode == 0 && bytes.Length >= 48000 * 4 && string.IsNullOrWhiteSpace(await errors), "Audio track fully decodes for " + Path.GetFileName(clip.Path));
            var raw = bytes.ToArray();
            double Tone(double frequency)
            {
                double real = 0, imaginary = 0;
                int count = raw.Length / 4;
                for (int i = 0; i < count; i++) { float sample = BitConverter.ToSingle(raw, i * 4); real += sample * Math.Cos(2 * Math.PI * frequency * i / 48000); imaginary += sample * Math.Sin(2 * Math.PI * frequency * i / 48000); }
                return 2 * Math.Sqrt(real * real + imaginary * imaginary) / count;
            }
            double desktop = Tone(440), voice = Tone(880);
            Check((speaker ? desktop > .02 : desktop < .005) && (mic ? voice > .02 : voice < .005), $"Correct sources audible after export: speaker tone={desktop:0.0000}, mic tone={voice:0.0000}");
        }
        // Exercise the actual Windows microphone and playback pipes concurrently.
        // Samples are counted and discarded; no ambient recording is written to disk.
        await Task.WhenAll(new[] { false, true }.Select(async mic =>
        {
            var configured = Storage.Load(out _);
            await using var source = new AudioLoopback(mic ? configured.MicrophoneDeviceId : configured.AudioDeviceId, microphone: mic) { Muted = true };
            await using var pipe = new NamedPipeClientStream(".", source.PipeName, PipeDirection.In, PipeOptions.Asynchronous);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            long origin=Stopwatch.GetTimestamp(); source.Start(()=>origin); await pipe.ConnectAsync(timeout.Token);
            long count = 0; var packet = new byte[16384];
            while (count < source.Format.AverageBytesPerSecond)
            {
                int read = await pipe.ReadAsync(packet, timeout.Token);
                if (read == 0) throw new IOException("Capture pipe closed early");
                if (packet.Take(read).Any(b => b != 0)) throw new Exception("Muted audio pipe emitted nonzero samples");
                count += read;
            }
            Check(source.Failure == null && !source.StreamStalled, $"Live {(mic ? "microphone" : "speaker")} pipe supplies one second of samples: {source.DeviceName} ({source.Format.SampleRate} Hz, {source.Format.Channels} channels)");
            Check(count >= source.Format.AverageBytesPerSecond, $"Muted {(mic ? "microphone" : "speaker")} preserves sample timing with silence");
            source.Muted = false;
            Check(await pipe.ReadAsync(packet, timeout.Token) > 0 && source.Failure == null, "Unmuting continues the existing audio pipe without restarting it");
        }));
    }
}

