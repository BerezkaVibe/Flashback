using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Flashback;

// --mixer-test: records 4 seconds of the per-app mix from whatever is playing and
// reports the app streams and signal level. Needs at least one app playing sound.
internal static class MixerDiagnostics
{
    internal static async Task RunAsync()
    {
        Directory.CreateDirectory(Storage.Root);
        var report = new List<string> { $"Supported: {AppMixSource.Supported} (build {Environment.OSVersion.Version.Build})" };
        var apps = AppMixSource.ActiveApps("");
        report.Add("Apps with audio sessions: " + (apps.Count == 0 ? "none" : string.Join(", ", apps)));
        long origin = Stopwatch.GetTimestamp();
        await using (var mix = new AppMixSource("", apps.ToDictionary(a => a.ToLowerInvariant(), _ => 100)))
        {
            mix.Start(() => origin);
            await using var pipe = new NamedPipeClientStream(".", mix.PipeName, PipeDirection.In, PipeOptions.Asynchronous);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await pipe.ConnectAsync(timeout.Token);
            var buffer = new byte[65536]; long bytes = 0; double sumSquares = 0; long samples = 0; float peak = 0;
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < 4)
            {
                int read = await pipe.ReadAsync(buffer, timeout.Token);
                if (read == 0) break;
                for (int i = 0; i + 3 < read; i += 4) { float v = BitConverter.ToSingle(buffer, i); sumSquares += v * v; samples++; peak = Math.Max(peak, Math.Abs(v)); }
                bytes += read;
            }
            double expected = (clock.Elapsed.TotalSeconds - .1) * AppMixSource.SampleRate * AppMixSource.Channels * 4;
            report.Add($"Streams: {mix.SyncReport}");
            report.Add($"Bytes: {bytes} of about {expected:0} expected ({bytes / Math.Max(1, expected):P0}); RMS {Math.Sqrt(sumSquares / Math.Max(1, samples)):0.0000}; peak {peak:0.000}");
            // Every app at 0%: after the 100 ms mix latency the output must be silent.
            mix.SetLevels(apps.ToDictionary(a => a.ToLowerInvariant(), _ => 0));
            float mutedPeak = 0; var muted = Stopwatch.StartNew();
            while (muted.Elapsed.TotalSeconds < 2)
            {
                int read = await pipe.ReadAsync(buffer, timeout.Token);
                if (read == 0) break;
                if (muted.Elapsed.TotalSeconds < .5) continue;
                for (int i = 0; i + 3 < read; i += 4) mutedPeak = Math.Max(mutedPeak, Math.Abs(BitConverter.ToSingle(buffer, i)));
            }
            report.Add($"All apps at 0%: peak {mutedPeak:0.000000} ({(mutedPeak < 1e-6 ? "silent" : "NOT silent")})");
            report.Add($"Failure: {mix.Failure?.Message ?? "none"}");
        }
        File.WriteAllLines(Path.Combine(Storage.Root, "mixer-results.txt"), report);
    }
}
