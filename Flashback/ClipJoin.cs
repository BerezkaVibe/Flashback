using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Flashback;

// Joins a second video onto the end of the first, for continuing a timeline with another clip. When
// both match (size, frame rate and audio tracks, as Flashback's own recordings do) the streams are
// copied straight across, which takes moments. Otherwise the pair is re-encoded so the second matches
// the first: scaled to fit and letterboxed, at the first's frame rate, with its sound mixed to stereo.
internal static class ClipJoin
{
    internal static bool Matches(ClipMedia a, ClipMedia b) =>
        a.Width == b.Width && a.Height == b.Height && Math.Abs(a.FrameRate - b.FrameRate) < .01 && a.HasAudio == b.HasAudio && a.AudioTracks == b.AudioTracks;

    internal static async Task<string> JoinAsync(string first, string second, string destination, IProgress<double>? progress, CancellationToken token, bool syntheticEncoder = false)
    {
        first = Path.GetFullPath(first); second = Path.GetFullPath(second); destination = Path.GetFullPath(destination);
        if (File.Exists(destination)) throw new IOException("Choose a new filename. Existing files are never overwritten.");
        var a = ClipMedia.Read(first); var b = ClipMedia.Read(second);
        if (a.Width <= 0 || a.Height <= 0 || b.Width <= 0 || b.Height <= 0) throw new IOException("One of these videos has no readable picture size.");
        double total = a.Duration + b.Duration;
        await ExportServices.Gate.WaitAsync(token);
        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        string list = Path.Combine(Path.GetTempPath(), "Flashback-join-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            Storage.EnsureWritable(Path.GetDirectoryName(destination)!);
            var args = new List<string>();
            if (Matches(a, b))
            {
                // Same kind of video: copy both into one file without re-encoding.
                static string Quote(string path) => "file '" + path.Replace("'", "'\\''") + "'";
                await File.WriteAllLinesAsync(list, new[] { Quote(first), Quote(second) }, token);
                args.AddRange(new[] { "-f", "concat", "-safe", "0", "-i", list, "-map", "0", "-c", "copy", "-movflags", "+faststart", "-f", "mp4", temp });
                await ExportServices.RunAsync(args, token, progress, total);
            }
            else
            {
                var encoder = syntheticEncoder ? null : await VideoEncoder.SelectAsync(Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe"), Storage.Load(out _).Encoder, null, null, token);
                string fps = ExportServices.Number(a.FrameRate > 0 ? a.FrameRate : 30), last = encoder?.IsAmd == true ? "format=nv12,hwupload" : "format=yuv420p";
                string Fit(int input) => $"[{input}:v:0]scale={a.Width}:{a.Height}:force_original_aspect_ratio=decrease,pad={a.Width}:{a.Height}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,fps={fps},format=yuv420p[v{input}]";
                // The joined file keeps the first clip's audio layout. A recording with separate desktop and
                // microphone tracks keeps all three; a clip without them joins with its sound as the
                // combined and desktop tracks and a silent microphone. Everything is made one shape so
                // the pieces join cleanly.
                bool sound = a.HasAudio || b.HasAudio;
                int tracks = a.HasSeparateTracks ? 3 : 1;
                string Sound(int input, ClipMedia m, int track)
                {
                    int from = m.HasSeparateTracks ? track : track == 1 ? 0 : track;
                    return m.HasAudio && from < m.AudioTracks && !(track == 2 && !m.HasSeparateTracks)
                        ? $"[{input}:a:{from}]aresample=48000,aformat=sample_rates=48000:channel_layouts=stereo[a{input}t{track}]"
                        : $"anullsrc=r=48000:cl=stereo,atrim=duration={ExportServices.Number(m.Duration)}[a{input}t{track}]";
                }
                var filters = new List<string> { Fit(0), Fit(1) };
                if (sound)
                {
                    for (int track = 0; track < tracks; track++) { filters.Add(Sound(0, a, track)); filters.Add(Sound(1, b, track)); }
                    string Inputs(int input) => $"[v{input}]" + string.Concat(Enumerable.Range(0, tracks).Select(track => $"[a{input}t{track}]"));
                    filters.Add($"{Inputs(0)}{Inputs(1)}concat=n=2:v=1:a={tracks}[j]" + string.Concat(Enumerable.Range(0, tracks).Select(track => $"[a{track}]")));
                }
                else filters.Add("[v0][v1]concat=n=2:v=1:a=0[j]");
                filters.Add($"[j]{last}[v]");
                if (encoder?.IsAmd == true) args.AddRange(new[] { "-init_hw_device", $"d3d11va=exportgpu:{encoder.Adapter.Index}", "-filter_hw_device", "exportgpu" });
                args.AddRange(new[] { "-i", first, "-i", second, "-filter_complex", string.Join(';', filters), "-map", "[v]" });
                if (sound)
                {
                    for (int track = 0; track < tracks; track++) args.AddRange(new[] { "-map", $"[a{track}]" });
                    args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" });
                }
                if (encoder == null) args.AddRange(new[] { "-c:v", "libx264", "-preset", "ultrafast", "-crf", "18", "-threads", "2" });
                else args.AddRange(encoder.EncodingArguments(new Settings(), export: true));
                args.AddRange(new[] { "-bf", "0", "-movflags", "+faststart", "-f", "mp4", temp });
                await ExportServices.RunAsync(args, token, progress, total);
            }
            var joined = ClipMedia.Read(temp);
            if (Math.Abs(joined.Duration - total) > Math.Max(.35, 3 / Math.Max(1, a.FrameRate))) throw new IOException("The joined video came out the wrong length, so it wasn't kept.");
            token.ThrowIfCancellationRequested(); File.Move(temp, destination);
            progress?.Report(1);
            return destination;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            try { if (File.Exists(list)) File.Delete(list); } catch { }
            ExportServices.Gate.Release();
        }
    }
}
