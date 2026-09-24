using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Flashback;

internal enum ExportFormat { Mp4, Mp4Hd60, Mp4Sd30, Mov, Gif, Mp3 }
// A stretch of the source that exports as one piece, at one speed (or frozen on its first frame).
internal readonly record struct ExportPiece(double Start, double End, double Speed, bool Freeze = false, bool FreezeSound = true)
{
    internal double Seconds => (End - Start) / Speed;
}
// Source-pixel rectangle; width and height are kept even for 4:2:0 video.
internal sealed record CropRect(int X, int Y, int Width, int Height)
{
    internal string Filter => $"crop={Width & ~1}:{Height & ~1}:{X & ~1}:{Y & ~1}";
}

internal sealed record ShareExportOptions(double TargetMb = 0, int Height = 0, int Fps = 0)
{
    internal ExportFormat Format { get; init; } = ExportFormat.Mp4;
    internal CropRect? Crop { get; init; }
    // Applied only to clips recorded with separate desktop and microphone tracks.
    internal double DesktopVolume { get; init; } = 1;
    internal double MicrophoneVolume { get; init; } = 1;
    internal bool CustomMix => Math.Abs(DesktopVolume - 1) > .001 || Math.Abs(MicrophoneVolume - 1) > .001;
    // Lane -1 blacks out the picture; audio lanes follow the trimmer: combined, or desktop then microphone.
    internal IReadOnlyList<CutRegion> Cuts { get; init; } = Array.Empty<CutRegion>();
    // Slow motion: 1 is normal speed, 0.5 plays at half speed. Audio keeps its pitch.
    internal double Speed { get; init; } = 1;
    internal static readonly double[] Speeds = { 1, .75, .5, .25 };
    // Speed parts from the timeline (slower or faster), in source time. They never overlap cuts.
    internal IReadOnlyList<SpeedRegion> SlowRegions { get; init; } = Array.Empty<SpeedRegion>();
    internal const double MinRegionSpeed = .1, MaxRegionSpeed = 4;
    // Quick picks in the speed pop-up; the slider covers everything from 0.1x to 4x.
    internal static readonly double[] RegionSpeeds = { .25, .5, .75, 1.5, 2 };
    // Zoomed stretches, in source time. They may overlap speed parts and cuts.
    internal IReadOnlyList<ZoomRegion> ZoomRegions { get; init; } = Array.Empty<ZoomRegion>();
    // Text and pictures over the video, in source time. They may overlap everything else.
    internal IReadOnlyList<OverlayItem> Overlays { get; init; } = Array.Empty<OverlayItem>();
    // Louder or quieter stretches of the audio lanes, and music or sound files mixed over the export.
    internal IReadOnlyList<VolumeRegion> VolumeRegions { get; init; } = Array.Empty<VolumeRegion>();
    internal IReadOnlyList<SoundItem> Sounds { get; init; } = Array.Empty<SoundItem>();
    // Kept ranges split at speed-part edges; each piece carries its combined speed. A freeze piece
    // holds its first frame for as long as it lasts (at the export's overall speed).
    internal List<ExportPiece> Pieces(IEnumerable<KeepSection> ranges)
    {
        var pieces = new List<ExportPiece>();
        void Add(double a, double b, SpeedRegion? r)
        {
            if (b - a > .005) pieces.Add(new ExportPiece(a, b, (r is { Freeze: false } ? r.Speed : 1) * Speed, r?.Freeze == true, r?.FreezeSound ?? true));
        }
        foreach (var range in ranges)
        {
            double cursor = range.Start;
            foreach (var region in SlowRegions.Where(r => r.End > range.Start && r.Start < range.End).OrderBy(r => r.Start))
            {
                Add(cursor, Math.Max(cursor, region.Start), null);
                Add(Math.Max(cursor, region.Start), Math.Min(region.End, range.End), region);
                cursor = Math.Max(cursor, Math.Min(region.End, range.End));
            }
            Add(cursor, range.End, null);
        }
        return pieces;
    }
    internal double OutputSeconds(IEnumerable<KeepSection> ranges) => Pieces(ranges).Sum(p => p.Seconds);
    // Where a moment of the source lands in the finished export (the start of the next kept part
    // when it falls in a removed stretch).
    internal static double OutputTime(IReadOnlyList<ExportPiece> pieces, double time)
    {
        double at = 0;
        foreach (var p in pieces)
        {
            if (time <= p.Start) return at;
            if (time < p.End) return at + (time - p.Start) / p.Speed;
            at += p.Seconds;
        }
        return at;
    }
    // atempo takes 0.5 to 2 per stage, so very slow or fast speeds chain stages.
    internal static string AudioTempo(double speed)
    {
        var stages = new List<string>(); double left = speed;
        while (left < .5 - 1e-9) { stages.Add("atempo=0.5"); left /= .5; }
        while (left > 2 + 1e-9) { stages.Add("atempo=2"); left /= 2; }
        if (Math.Abs(left - 1) > 1e-9) stages.Add("atempo=" + ExportServices.Number(left));
        return string.Join(",", stages);
    }
    internal bool HasVideo => Format != ExportFormat.Mp3;
    internal bool IsGif => Format == ExportFormat.Gif;
    internal string Extension => Format switch { ExportFormat.Mov => ".mov", ExportFormat.Gif => ".gif", ExportFormat.Mp3 => ".mp3", _ => ".mp4" };
    // Formats that change size, frame rate or container always re-encode.
    internal static ShareExportOptions For(ExportFormat format, double targetMb = 0) => format switch
    {
        ExportFormat.Mp4Hd60 => new(targetMb, 1080, 60) { Format = format },
        ExportFormat.Mp4Sd30 => new(targetMb, 720, 30) { Format = format },
        ExportFormat.Gif => new(0, 480, 15) { Format = format },
        _ => new(format == ExportFormat.Mp3 ? 0 : targetMb) { Format = format }
    };
    internal long MaxBytes => (long)(TargetMb * 1_000_000);
    internal void Validate()
    {
        if (!double.IsFinite(TargetMb) || TargetMb < 0 || TargetMb > 10000 || (TargetMb > 0 && TargetMb < 1))
            throw new ArgumentException("Choose a file limit from 1 to 10,000 MB, or 0 for no size limit.");
        if (Height is not (0 or 480 or 720 or 1080) || Fps is not (0 or 15 or 30 or 60)) throw new ArgumentException("Choose a supported export size and frame rate.");
        if (Crop is { } c && (c.X < 0 || c.Y < 0 || c.Width < 32 || c.Height < 32)) throw new ArgumentException("The crop area is too small.");
        if (DesktopVolume is < 0 or > 2 || MicrophoneVolume is < 0 or > 2) throw new ArgumentException("Choose a track volume from 0% to 200%.");
        if (!Speeds.Contains(Speed)) throw new ArgumentException("Choose a supported speed.");
        if (SlowRegions.Any(r => !r.Freeze && (r.Speed < MinRegionSpeed - 1e-9 || r.Speed > MaxRegionSpeed + 1e-9) || r.End <= r.Start)) throw new ArgumentException("A speed part is outside 0.1× to 4×.");
        if (VolumeRegions.Any(v => v.Gain is < 0 or > 2 || v.End <= v.Start)) throw new ArgumentException("A volume part is outside 0% to 200%.");
        if (Sounds.FirstOrDefault(s => !File.Exists(s.Path)) is { } lost) throw new ArgumentException($"The sound {Path.GetFileName(lost.Path)} can't be found. Remove it or add it again.");
        if (Overlays.FirstOrDefault(o => o.Kind == OverlayKind.Video && !File.Exists(o.VideoPath)) is { } lostVideo) throw new ArgumentException($"The video {Path.GetFileName(lostVideo.VideoPath)} can't be found. Remove it or add it again.");
        if (Overlays.FirstOrDefault(o => o.Kind == OverlayKind.Image && !File.Exists(o.ImagePath)) is { } missing) throw new ArgumentException($"The picture {Path.GetFileName(missing.ImagePath)} can't be found. Remove it or add it again.");
    }
    internal int VideoBitrate(double seconds, bool audio)
    {
        Validate();
        double rate = MaxBytes * .92 * 8 / seconds - (audio ? 128000 : 0);
        if (!double.IsFinite(rate) || rate < 100000) throw new ArgumentException("That size limit is too small. Shorten the selection or raise the limit.");
        return (int)Math.Min(rate, 100_000_000);
    }
}

internal static class ExportServices
{
    // Only explicit editor work uses this gate. Capture and replay saving never wait here.
    internal static readonly SemaphoreSlim Gate = new(1, 1);
    internal static string Number(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture);
    // The source is decoded on the graphics card, which makes exports about 3.5x faster and uses about a
    // third of the CPU. D3D11 works on NVIDIA, AMD and Intel and keeps its speed while a game loads the GPU
    // (CUDA decoding slowed to a crawl in that test). Null decodes on the CPU.
    internal static string? HardwareDecode = "d3d11va";
    // CPU time the last exporter process used, for measurements.
    internal static TimeSpan LastProcessCpu;

    internal static async Task RunAsync(IEnumerable<string> arguments, CancellationToken token, IProgress<double>? progress = null, double duration = 1)
    {
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-nostats", "-progress", "pipe:1" }.Concat(arguments)) info.ArgumentList.Add(arg);
        token.ThrowIfCancellationRequested();
        using var job = new ChildProcessJob();
        using var process = Process.Start(info) ?? throw new IOException("Could not start the exporter.");
        job.Add(process); process.PriorityClass = ProcessPriorityClass.BelowNormal;
        var errors = process.StandardError.ReadToEndAsync();
        var output = Task.Run(async () => {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
                if (line.StartsWith("out_time_us=") && long.TryParse(line[12..], out var time))
                    progress?.Report(Math.Clamp(time / 1_000_000d / duration, 0, .99));
        });
        try { await process.WaitForExitAsync(token); }
        catch { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); await output; await errors; throw; }
        await output; string error = await errors;
        try { LastProcessCpu = process.TotalProcessorTime; } catch { }
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new IOException("Export failed: " + error);
    }

    internal static async Task<ClipResult> PreciseAsync(string source, string destination, IEnumerable<KeepSection> sections,
        IProgress<double>? progress, CancellationToken token, bool syntheticEncoder = false, ShareExportOptions? options = null)
    {
        source = Path.GetFullPath(source); destination = Path.GetFullPath(destination);
        if (source.Equals(destination, StringComparison.OrdinalIgnoreCase) || File.Exists(destination)) throw new IOException("Choose a new filename. Existing files are never overwritten.");
        var media = ClipMedia.Read(source); var ranges = ClipEditor.Validate(sections, media.Duration);
        options ??= new(); options.Validate(); double total = ranges.Sum(s => s.Duration);
        bool video = options.HasVideo, audio = media.HasAudio && !options.IsGif;
        if (!video && !media.HasAudio) throw new ArgumentException("This clip has no audio to export as MP3.");
        bool encodeVideo = video && !options.IsGif;
        var pieces = options.Pieces(ranges);
        // GPU decoding can be turned off in Settings > Storage.
        string? decode = HardwareDecode != null && Storage.Load(out _).ExportGpuDecode ? HardwareDecode : null;
        double output = pieces.Sum(p => p.Seconds);
        int bitrate = options.TargetMb > 0 && encodeVideo ? options.VideoBitrate(output, audio) : 0;
        await Gate.WaitAsync(token);
        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        string overlayFolder = Path.Combine(Path.GetTempPath(), "Flashback-overlays", Guid.NewGuid().ToString("N"));
        try
        {
            Storage.EnsureWritable(Path.GetDirectoryName(destination)!);
            var encoder = syntheticEncoder || !encodeVideo ? null : await VideoEncoder.SelectAsync(Path.Combine(AppContext.BaseDirectory,"tools","ffmpeg.exe"), Storage.Load(out _).Encoder, null, null, token);
            // Seek each input at the demuxer before decoding. Only retained sections enter the graph.
            // Thread counts are capped per input; no preview decoder or thumbnail job is started here.
            var inputs = new List<string>(); var filters = new List<string>(); var labels = "";
            // Pictures, masks, videos and sounds are extra inputs after the pieces.
            var extraInputs = new List<string>(); int extraCount = 0;
            int AddInput(params string[] args) { extraInputs.AddRange(args); return pieces.Count + extraCount++; }
            // Text, pictures and shapes are drawn to see-through pictures first; each piece reads the ones it shows.
            var clips = video && options.Overlays.Count > 0
                ? await OverlayExport.RenderAsync(OverlayOrder.BackToFront(options.Overlays).Select(o => o.Validated()).ToList(), media.Width > 0 ? media.Width : 1920, media.Height > 0 ? media.Height : 1080, media.FrameRate, overlayFolder, token)
                : new List<OverlayExport.Clip>();
            int frameW = media.Width > 0 ? media.Width : 1920, frameH = media.Height > 0 ? media.Height : 1080;
            bool mixTracks = audio && media.HasSeparateTracks && (options.CustomMix || options.Cuts.Any(c => c.Lane >= 0) || options.VolumeRegions.Any(v => v.Lane >= 0));
            int step = 0;
            for (int i = 0; i < pieces.Count; i++)
            {
                var piece = pieces[i]; double start = piece.Start, end = piece.End, speed = piece.Speed, length = end - start;
                string trim = $"atrim=duration={Number(length)},asetpts=PTS-STARTPTS";
                // Speed changes stretch or squeeze this piece after its cuts are applied in source time.
                string slowVideo = speed != 1 ? $",setpts=PTS/{Number(speed)}" : "", slowAudio = speed != 1 ? "," + ShareExportOptions.AudioTempo(speed) : "";
                // Cuts inside this piece, as times relative to its start: black video or silence.
                string? Window(int lane)
                {
                    var parts = options.Cuts.Where(c => c.Lane == lane && c.End > start && c.Start < end)
                        .Select(c => $"between(t,{Number(Math.Max(0, c.Start - start))},{Number(Math.Min(length, c.End - start))})").ToList();
                    return parts.Count == 0 ? null : string.Join("+", parts);
                }
                // Volume parts on a lane, then its cut-outs (a cut-out wins where they overlap).
                string Gain(int lane) => string.Concat(options.VolumeRegions.Where(v => v.Lane == lane && v.End > start && v.Start < end)
                    .Select(v => $",volume={Number(v.Gain)}:enable='between(t,{Number(Math.Max(0, v.Start - start))},{Number(Math.Min(length, v.End - start))})'"));
                string Mute(int lane) => Gain(lane) + (Window(lane) is { } w ? $",volume=0:enable='{w}'" : "") + (piece.Freeze && !piece.FreezeSound ? ",volume=0" : "");
                if (video && decode is { } hw) inputs.AddRange(new[] { "-hwaccel", hw });
                inputs.AddRange(new[] { "-threads", "1", "-ss", Number(start), "-t", Number(length), "-i", source });
                var pipSounds = new List<string>();
                if (video)
                {
                    string blackout = Window(-1) is { } w ? $",drawbox=x=0:y=0:w=iw:h=ih:color=black:t=fill:enable='{w}'" : "";
                    // Zoom renders per frame in source time, before any speed change.
                    string zoom = "";
                    var zooms = options.ZoomRegions.Where(z => z.End > start && z.Start < end).ToList();
                    if (zooms.Count > 0)
                    {
                        var (z, x, y) = ZoomRegion.Expressions(zooms, start, media.FrameRate);
                        zoom = $",zoompan=z='{z}':x='{x}':y='{y}':d=1:s={media.Width}x{media.Height}:fps={Number(media.FrameRate)}";
                    }
                    // A freeze holds the piece's first frame for its whole length.
                    string source0 = piece.Freeze ? $"trim=end_frame=1,setpts=PTS-STARTPTS,tpad=stop_mode=clone:stop_duration={Number(length)}" : $"trim=duration={Number(length)},setpts=PTS-STARTPTS";
                    // Blackout, then things stuck to the video, then zoom, then things fixed on screen, then speed.
                    string label = $"b{i}";
                    filters.Add($"[{i}:v:0]{source0}{blackout}[{label}]");
                    void Overlay(bool stuck)
                    {
                        // Clips are already back to front.
                        foreach (var clip in clips.Where(c => c.Item.StickToVideo == stuck))
                        {
                            string next = $"o{step++}";
                            if (clip.Video is { } pip)
                            {
                                // Picture-in-picture: the video is cropped, sized, cut to its mask, framed, turned and faded here.
                                var item = clip.Item;
                                double from = Math.Max(0, start - item.Start), to = Math.Min(item.Length, end - item.Start), lead = Math.Max(0, item.Start - start);
                                if (to - from <= .01) { step--; continue; }
                                int input = AddInput("-ss", Number(item.VideoOffset + from), "-t", Number(to - from), "-i", item.VideoPath);
                                int mask = AddInput("-loop", "1", "-t", Number(lead + to - from + 1), "-i", pip.Mask);
                                var chain = new List<string> { $"[{input}:v:0]setpts=PTS-STARTPTS,crop={pip.CropW}:{pip.CropH}:{pip.CropX}:{pip.CropY},scale={pip.Width}:{pip.Height},format=rgba[{next}v]",
                                    $"[{mask}:v]format=rgba,alphaextract[{next}k]", $"[{next}v][{next}k]alphamerge[{next}m]" };
                                string framed = $"{next}m";
                                if (pip.Border is { } border)
                                {
                                    int frame = AddInput("-loop", "1", "-t", Number(lead + to - from + 1), "-i", border);
                                    chain.Add($"[{framed}]pad={pip.Width + pip.Pad * 2}:{pip.Height + pip.Pad * 2}:{pip.Pad}:{pip.Pad}:color=0x00000000[{next}q]");
                                    chain.Add($"[{frame}:v]format=rgba[{next}f]"); chain.Add($"[{next}q][{next}f]overlay=eof_action=pass[{next}b]");
                                    framed = $"{next}b";
                                }
                                double angle = item.Rotation * Math.PI / 180;
                                string turn = Math.Abs(angle) > 1e-4 ? $",rotate=a={Number(angle)}:ow=rotw({Number(angle)}):oh=roth({Number(angle)}):c=0x00000000" : "";
                                string fade = item.Opacity < .999 ? $",colorchannelmixer=aa={Number(item.Opacity)}" : "";
                                string wait = lead > 1e-3 ? $",tpad=start_duration={Number(lead)}:color=0x00000000" : "";
                                chain.Add($"[{framed}]null{turn}{fade}{wait}[{next}p]");
                                chain.Add($"[{label}][{next}p]overlay=x={Number(pip.CenterX)}-overlay_w/2:y={Number(pip.CenterY)}-overlay_h/2:eof_action=pass:enable='gte(t,{Number(lead)})*lt(t,{Number(lead + to - from)})'[{next}]");
                                filters.AddRange(chain);
                                if (audio && pip.HasSound && item.VideoVolume > 0)
                                {
                                    filters.Add($"[{input}:a:0]asetpts=PTS-STARTPTS,volume={Number(item.VideoVolume)},adelay=delays={Number(lead * 1000)}:all=1[{next}s]");
                                    pipSounds.Add($"[{next}s]");
                                }
                                label = next; continue;
                            }
                            if (OverlayExport.PieceList(clip, start, end, overlayFolder, $"piece{i}-{step}") is not { } list) { step--; continue; }
                            int frames = AddInput("-f", "concat", "-safe", "0", "-i", list.List);
                            string when = $"enable='gte(t,{Number(list.From)})*lt(t,{Number(list.To)})'";
                            if (clip.Region)
                            {
                                // Blur or pixelate: treat a copy of the box and keep it only inside the shape's mask.
                                var box = clip.Box; double k = frameH / OverlayItem.Reference, strength = clip.Item.RegionStrength * k;
                                string effect = clip.Item.Region == OverlayRegion.Blur
                                    ? $"gblur=sigma={Number(Math.Clamp(strength / 2, .5, Math.Min(box.Width, box.Height) / 2.0))}"
                                    : $"scale={Math.Max(1, (int)Math.Round(box.Width / Math.Max(2, strength)))}:{Math.Max(1, (int)Math.Round(box.Height / Math.Max(2, strength)))}:flags=area,scale={box.Width}:{box.Height}:flags=neighbor";
                                filters.Add($"[{label}]split[{next}a][{next}c];[{next}c]crop={box.Width}:{box.Height}:{box.X}:{box.Y},{effect}[{next}e];[{frames}:v]format=rgba,alphaextract[{next}k];[{next}e][{next}k]alphamerge[{next}r];[{next}a][{next}r]overlay=x={box.X}:y={box.Y}:eof_action=pass:{when}[{next}]");
                            }
                            else filters.Add($"[{frames}:v]format=rgba[{next}p];[{label}][{next}p]overlay=x={clip.Box.X}:y={clip.Box.Y}:eof_action=pass:{when}[{next}]");
                            label = next;
                        }
                    }
                    Overlay(true);
                    if (zoom.Length > 0) { filters.Add($"[{label}]{zoom[1..]}[z{i}]"); label = $"z{i}"; }
                    Overlay(false);
                    filters.Add($"[{label}]{(slowVideo.Length > 0 ? slowVideo[1..] : "null")}[v{i}]"); labels += $"[v{i}]";
                }
                // A picture-in-picture video's own sound joins this piece's sound before any speed change.
                string WithPip(string piece) => pipSounds.Count == 0 ? piece : $"{piece}[{i}pa];[{i}pa]{string.Concat(pipSounds)}amix=inputs={pipSounds.Count + 1}:duration=first:normalize=0";
                if (mixTracks)
                {
                    // Rebuild the mix from the desktop and microphone tracks with the chosen volumes and cuts.
                    filters.Add(WithPip($"[{i}:a:1]{trim},volume={Number(options.DesktopVolume)}{Mute(0)}[d{i}];[{i}:a:2]{trim},volume={Number(options.MicrophoneVolume)}{Mute(1)}[m{i}];[d{i}][m{i}]amix=inputs=2:duration=longest:normalize=0,alimiter=limit=0.95:level=0") + $"{slowAudio}[a{i}]");
                    labels += $"[a{i}]";
                }
                else if (audio) { filters.Add(WithPip($"[{i}:a:0]{trim}{Mute(0)}") + $"{slowAudio}[a{i}]"); labels += $"[a{i}]"; }
            }
            filters.Add(labels + $"concat=n={pieces.Count}:v={(video ? 1 : 0)}:a={(audio ? 1 : 0)}" + (video ? "[joined]" : "") + (audio ? "[a]" : ""));
            // Music and sound files are mixed over the finished timeline, so speed parts leave them alone.
            string finalAudio = "[a]";
            var sounds = options.IsGif ? new List<SoundItem>() : options.Sounds.Where(s => s.End > s.Start).ToList();
            if (sounds.Count > 0)
            {
                string game = "[a]";
                if (!audio)
                {
                    int silence = AddInput("-f", "lavfi", "-t", Number(output), "-i", "anullsrc=r=48000:cl=stereo");
                    filters.Add($"[{silence}:a]anull[a]"); audio = true;
                }
                var mix = new List<string>(); var ducks = new List<string>();
                foreach (var (sound, n) in sounds.Select((s, n) => (s, n)))
                {
                    double at = ShareExportOptions.OutputTime(pieces, sound.Start), until = ShareExportOptions.OutputTime(pieces, sound.End), length = Math.Min(until - at, sound.Length);
                    if (length <= .05) continue;
                    int input = AddInput("-ss", Number(sound.Offset), "-t", Number(length), "-i", sound.Path);
                    string fades = (sound.FadeIn > 0 ? $",afade=t=in:d={Number(Math.Min(sound.FadeIn, length))}" : "") + (sound.FadeOut > 0 ? $",afade=t=out:st={Number(Math.Max(0, length - sound.FadeOut))}:d={Number(Math.Min(sound.FadeOut, length))}" : "");
                    filters.Add($"[{input}:a:0]aresample=48000,asetpts=PTS-STARTPTS,volume={Number(sound.Volume)}{fades},adelay=delays={Number(at * 1000)}:all=1[snd{n}]");
                    mix.Add($"[snd{n}]");
                    if (sound.Duck) ducks.Add($",volume={Number(sound.DuckLevel)}:enable='between(t,{Number(at)},{Number(at + length)})'");
                }
                if (mix.Count > 0)
                {
                    if (ducks.Count > 0) { filters.Add($"[a]anull{string.Concat(ducks)}[ducked]"); game = "[ducked]"; }
                    filters.Add($"{game}{string.Concat(mix)}amix=inputs={mix.Count + 1}:duration=first:normalize=0,alimiter=limit=0.95:level=0[mixed]");
                    finalAudio = "[mixed]";
                }
            }
            inputs.AddRange(extraInputs);
            if (video)
            {
                var conversion = new List<string>();
                if (options.Crop != null) conversion.Add(options.Crop.Filter);
                if (options.IsGif)
                {
                    // Palette pass keeps GIF colors clean; diff mode favors moving areas.
                    filters.Add($"[joined]{(conversion.Count > 0 ? string.Join(",", conversion) + "," : "")}fps=15,scale=w=-2:h='min(ih,480)':flags=lanczos,split[g0][g1];[g0]palettegen=max_colors=128:stats_mode=diff[pal];[g1][pal]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle[v]");
                }
                else
                {
                    if (options.Height > 0) conversion.Add($"scale=w=-2:h='min(ih,{options.Height})':flags=fast_bilinear");
                    if (options.Fps > 0) conversion.Add("fps=" + Number(Math.Min(options.Fps, media.FrameRate)));
                    conversion.Add(encoder?.IsAmd == true ? "format=nv12,hwupload" : "format=yuv420p");
                    filters.Add("[joined]" + string.Join(',', conversion) + "[v]");
                }
            }
            // If the graphics card can't decode this video, the export runs again decoding on the CPU.
            async Task Run(List<string> args)
            {
                try { await RunAsync(args, token, progress, output); }
                catch (IOException) when (args.Contains("-hwaccel") && !token.IsCancellationRequested)
                {
                    for (int i = args.IndexOf("-hwaccel"); i >= 0; i = args.IndexOf("-hwaccel")) args.RemoveRange(i, 2);
                    if (File.Exists(temp)) File.Delete(temp);
                    await RunAsync(args, token, progress, output);
                }
            }
            // One bounded retry handles encoder/container overhead. Oversize output is never published as a success.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var args = new List<string>();
                if (encoder?.IsAmd == true) args.AddRange(new[] { "-init_hw_device", $"d3d11va=exportgpu:{encoder.Adapter.Index}", "-filter_hw_device", "exportgpu" });
                args.AddRange(inputs); args.AddRange(new[] { "-filter_complex_threads", "1", "-filter_complex", string.Join(';',filters) });
                if (video) args.AddRange(new[] { "-map", "[v]" });
                if (!video) { args.AddRange(new[] { "-map", finalAudio, "-c:a", "libmp3lame", "-q:a", "2", "-f", "mp3", temp }); await Run(args); break; }
                if (options.IsGif) { args.AddRange(new[] { "-loop", "0", "-f", "gif", temp }); await Run(args); break; }
                if (audio) args.AddRange(new[] { "-map", finalAudio, "-c:a", "aac", "-b:a", "128k" });
                if (syntheticEncoder) args.AddRange(new[] { "-c:v", "libx264", "-preset", "ultrafast", "-threads", "2" });
                else if (bitrate == 0) args.AddRange(encoder!.EncodingArguments(new Settings(), export: true));
                else args.AddRange(encoder!.IsAmd
                    ? new[] { "-c:v", "h264_amf", "-usage", "transcoding", "-quality", "speed", "-rc", "cbr", "-preanalysis", "0", "-vbaq", "0" }
                    : new[] { "-c:v", "h264_nvenc", "-preset", "p1", "-rc", "cbr", "-rc-lookahead", "0", "-multipass", "disabled" });
                if (bitrate > 0) args.AddRange(new[] { "-b:v", bitrate.ToString(CultureInfo.InvariantCulture), "-maxrate", bitrate.ToString(CultureInfo.InvariantCulture), "-bufsize", (bitrate * 2L).ToString(CultureInfo.InvariantCulture) });
                else if (syntheticEncoder) args.AddRange(new[] { "-crf", "20" });
                args.AddRange(new[] { "-bf", "0", "-fps_mode", "vfr", "-movflags", "+faststart", "-f", options.Format == ExportFormat.Mov ? "mov" : "mp4", temp });
                await Run(args);
                if (options.TargetMb == 0 || new FileInfo(temp).Length <= options.MaxBytes) break;
                long actual = new FileInfo(temp).Length;
                File.Delete(temp);
                if (attempt == 1) throw new IOException("The encoder could not meet this file limit. Choose a larger limit or a shorter selection.");
                bitrate = Math.Max(100000, (int)(bitrate * (double)options.MaxBytes / actual * .85));
            }
            double duration = output;
            if (encodeVideo)
            {
                var verified = ClipMedia.Read(temp);
                double tolerance = Math.Max(.25, pieces.Count / media.FrameRate / pieces.Min(p => p.Speed) + .05);
                if (Math.Abs(verified.Duration - output) > tolerance || verified.HasAudio != audio) throw new IOException("Export timing or audio did not match the selected sections.");
                duration = verified.Duration;
            }
            else if (!File.Exists(temp) || new FileInfo(temp).Length == 0) throw new IOException("The export produced an empty file.");
            token.ThrowIfCancellationRequested(); File.Move(temp, destination);
            var result = new ClipResult(destination, duration, new DirectoryInfo(Path.GetDirectoryName(destination)!).Name, DateTimeOffset.Now);
            if (encodeVideo) try { ClipLibrary.Remember(result); } catch { }
            progress?.Report(1); return result;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            finally { try { if (Directory.Exists(overlayFolder)) Directory.Delete(overlayFolder, true); } catch { } Gate.Release(); }
        }
    }

    internal static async Task SnapshotAsync(string source, string destination, double time, CancellationToken token)
    {
        string ext = Path.GetExtension(destination).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg")) throw new ArgumentException("Choose PNG or JPG.");
        if (File.Exists(destination)) throw new IOException("Choose a new filename. Existing files are never overwritten.");
        var media = ClipMedia.Read(source);
        time = Math.Clamp(time, 0, Math.Max(0, media.Duration - 1 / media.FrameRate));
        string temp = destination + "." + Guid.NewGuid().ToString("N") + ext;
        await Gate.WaitAsync(token);
        try
        {
            await RunAsync(new[] { "-ss", Number(time), "-i", source, "-frames:v", "1", "-an", "-c:v", ext==".png" ? "png" : "mjpeg", "-q:v", "2", "-threads", "1", "-update", "1", temp }, token);
            if (!File.Exists(temp) || new FileInfo(temp).Length == 0) throw new IOException("No snapshot frame was available.");
            token.ThrowIfCancellationRequested(); File.Move(temp,destination);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } finally { Gate.Release(); } }
    }
}
