using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Flashback;

public record KeepSection(double Start, double End)
{
    public double Duration => End - Start;
    public string Label => $"{TimeText(Start)} → {TimeText(End)}     ·     {Duration:0.###} seconds";
    public string CompactLabel => $"{ShortTime(Start)} → {ShortTime(End)} · {Duration:0.##} s";
    private static string ShortTime(double seconds) => TimeSpan.FromSeconds(seconds).ToString(seconds>=3600 ? @"h\:mm\:ss\.fff" : @"m\:ss\.fff");
    public static string TimeText(double seconds) => TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss\.fff");
    public static double Parse(string text)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && double.IsFinite(seconds)) return seconds;
        var fields = text.Trim().Split(':');
        if (fields.Length is < 2 or > 3) throw new ArgumentException("Use seconds, m:ss, or h:mm:ss. Decimals are allowed.");
        double total = 0;
        for (int i = 0; i < fields.Length; i++)
        {
            if (!double.TryParse(fields[i], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
                || !double.IsFinite(value) || value < 0 || i > 0 && value >= 60)
                throw new ArgumentException("Invalid time. Use seconds or h:mm:ss.fff.");
            total = total * 60 + value;
        }
        return total;
    }
}

// Reads MP4 headers without decoding video or bundling a second large executable.
public record ClipMedia(double Duration, bool HasAudio)
{
    public double FrameRate { get; init; } = 60;
    // Flashback records 1 track, or 3 when desktop and microphone are kept separate: combined, desktop, microphone.
    public int AudioTracks { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public bool HasSeparateTracks => AudioTracks >= 3;
    public static ClipMedia Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        uint U32() => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(reader.ReadBytes(4));
        ulong U64() => System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(reader.ReadBytes(8));
        IEnumerable<(string Type, long Start, long End)> Atoms(long start, long end)
        {
            int count = 0;
            while (start + 8 <= end && ++count < 100000)
            {
                stream.Position = start; ulong size = U32(); string type = Encoding.ASCII.GetString(reader.ReadBytes(4));
                int header = 8; if (size == 1) { size = U64(); header = 16; }
                if (size == 0) size = (ulong)(end - start);
                if (size < (ulong)header || size > (ulong)(end - start)) throw new IOException("Invalid MP4 header.");
                yield return (type, start + header, start + (long)size);
                start += (long)size;
            }
        }
        double duration = 0, frameRate = 60; bool audio = false; int audioTracks = 0, width = 0, height = 0;
        foreach (var movie in Atoms(0, stream.Length).Where(a => a.Type == "moov"))
            foreach (var track in Atoms(movie.Start, movie.End).Where(a => a.Type == "trak"))
            {
                // tkhd ends with the track's display width and height as 16.16 fixed point.
                int trackWidth = 0, trackHeight = 0;
                foreach (var header in Atoms(track.Start, track.End).Where(a => a.Type == "tkhd" && a.End - a.Start >= 84))
                { stream.Position = header.End - 8; trackWidth = (int)(U32() >> 16); trackHeight = (int)(U32() >> 16); }
                foreach (var media in Atoms(track.Start, track.End).Where(a => a.Type == "mdia"))
                {
                    string kind = ""; double trackDuration = 0; uint trackScale = 0;
                    foreach (var atom in Atoms(media.Start, media.End))
                    {
                        if (atom.Type == "hdlr" && atom.End - atom.Start >= 12)
                        { stream.Position = atom.Start + 8; kind = Encoding.ASCII.GetString(reader.ReadBytes(4)); }
                        if (atom.Type == "mdhd" && atom.End - atom.Start >= 20)
                        {
                            stream.Position = atom.Start; var version = reader.ReadByte();
                            stream.Position = atom.Start + (version == 1 ? 20 : 12);
                            uint scale = U32(); trackScale = scale; ulong ticks = version == 1 ? U64() : U32();
                            if (scale > 0) trackDuration = ticks / (double)scale;
                        }
                    }
                    if (kind == "vide")
                    {
                        duration = Math.Max(duration, trackDuration);
                        if (trackWidth > 0 && trackHeight > 0) { width = trackWidth; height = trackHeight; }
                        foreach (var minf in Atoms(media.Start,media.End).Where(a=>a.Type=="minf"))
                        foreach (var stbl in Atoms(minf.Start,minf.End).Where(a=>a.Type=="stbl"))
                        foreach (var stts in Atoms(stbl.Start,stbl.End).Where(a=>a.Type=="stts"))
                        {
                            if (stts.End-stts.Start < 8) continue;
                            stream.Position=stts.Start+4; uint entries=U32();
                            if (entries>100000 || entries>(stts.End-stream.Position)/8) throw new IOException("Invalid MP4 frame timing table.");
                            double frames=0, ticks=0;
                            for (int i=0;i<entries;i++) { uint count=U32(), delta=U32(); frames+=count; ticks+=(double)count*delta; }
                            double rate=ticks>0 ? frames*trackScale/ticks : 0;
                            if (double.IsFinite(rate) && rate>=1 && rate<=1000) frameRate=rate;
                        }
                    }
                    if (kind == "soun") { audio = true; audioTracks++; }
                }
            }
        if (!double.IsFinite(duration) || duration <= 0) throw new IOException("This clip has no readable MP4 video duration.");
        return new(duration, audio) { FrameRate=frameRate, AudioTracks=audioTracks, Width=width, Height=height };
    }
}

public static class ClipEditor
{
    public static List<KeepSection> Validate(IEnumerable<KeepSection> sections, double duration)
    {
        var list = sections.OrderBy(s => s.Start).ToList();
        if (list.Count is < 1 or > 30) throw new ArgumentException("Keep between 1 and 30 sections.");
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            if (!double.IsFinite(s.Start) || !double.IsFinite(s.End) || s.Start < 0 || s.End > duration + .001 || s.Duration < .1)
                throw new ArgumentException("Each section must be within the clip and at least 0.1 seconds long.");
            if (i > 0 && s.Start < list[i - 1].End) throw new ArgumentException("Sections cannot overlap. Remove or adjust the overlapping section first.");
        }
        return list;
    }
    public static Task<ClipResult> ExportAsync(string source, string destination, IEnumerable<KeepSection> sections,
        IProgress<double>? progress, CancellationToken cancellation, bool syntheticEncoder = false)
        => ExportServices.PreciseAsync(source, destination, sections, progress, cancellation, syntheticEncoder);
}
