using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Flashback;

// A saved edit. Version 2 adds cut-outs, speed parts, zooms, text and pictures, and the crop;
// version 1 files (sections only) still open.
internal sealed record TrimProject(int Version, string Source, long SourceBytes, long SourceWriteTicks,
    KeepSection[] Sections, double Start, double End, double Position, bool RoughCut)
{
    public CutRegion[]? Cuts { get; init; }
    public SpeedRegion[]? Speed { get; init; }
    public ZoomRegion[]? Zoom { get; init; }
    public OverlayItem[]? Overlays { get; init; }
    public VolumeRegion[]? Volumes { get; init; }
    public SoundItem[]? Sounds { get; init; }
    // Crop as a fraction of the frame: x, y, width, height.
    public double[]? Crop { get; init; }
    public DateTime? Saved { get; init; }

    internal const int CurrentVersion = 2;
    // Tuples (curve points, shape corners) are fields, so they need including.
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };
    internal static TrimProject Create(string source, KeepSection[] sections, double start, double end, double position, bool rough)
    {
        var file = new FileInfo(source);
        return new(CurrentVersion, file.FullName, file.Length, file.LastWriteTimeUtc.Ticks, sections, start, end, position, rough);
    }
    internal static TrimProject Read(string path)
    {
        if (new FileInfo(path).Length > 8_000_000) throw new IOException("This project is too large.");
        var project = JsonSerializer.Deserialize<TrimProject>(File.ReadAllText(path), Json) ?? throw new IOException("Invalid project.");
        project.Validate(); return project.Cleaned();
    }
    // True when there is anything to keep beyond the untouched clip.
    internal bool HasEdits(double duration) => Sections.Length > 0 || Start > .001 || End < duration - .001 || Cuts?.Length > 0 || Speed?.Length > 0 || Zoom?.Length > 0 || Overlays?.Length > 0 || Volumes?.Length > 0 || Sounds?.Length > 0 || Crop != null;
    internal void Validate()
    {
        if (Version is < 1 or > CurrentVersion || string.IsNullOrWhiteSpace(Source) || !Path.IsPathFullyQualified(Source) || Sections == null) throw new IOException("Unsupported project.");
        var file = new FileInfo(Source);
        if (!file.Exists || file.Length != SourceBytes || file.LastWriteTimeUtc.Ticks != SourceWriteTicks)
            throw new IOException("The project's original video is missing or has changed. Open the video separately to start a new edit.");
        var media = ClipMedia.Read(Source);
        if (Sections.Length > 0) ClipEditor.Validate(Sections,media.Duration);
        if (!double.IsFinite(Start) || !double.IsFinite(End) || !double.IsFinite(Position) || Start < 0 || End < Start || End > media.Duration || Position < 0 || Position > media.Duration)
            throw new IOException("The project contains invalid times.");
    }
    // Drops any part with impossible times and puts every value back in range.
    private TrimProject Cleaned()
    {
        static bool Ok(double a, double b) => double.IsFinite(a) && double.IsFinite(b) && a >= 0 && b > a;
        return this with
        {
            Cuts = Cuts?.Where(c => c != null && Ok(c.Start, c.End) && c.Lane >= -1).ToArray(),
            Speed = Speed?.Where(s => s != null && Ok(s.Start, s.End) && (s.Freeze || s.Speed >= ShareExportOptions.MinRegionSpeed - 1e-9 && s.Speed <= ShareExportOptions.MaxRegionSpeed + 1e-9)).ToArray(),
            Zoom = Zoom?.Where(z => z?.In?.Points is { Count: >= 2 } && Ok(z.Start, z.End)).Select(z => z with { In = z.In.Validated(), Out = z.Out?.Validated(), MaxZoom = Math.Clamp(z.MaxZoom, 1.1, ZoomRegion.Limit), X = Math.Clamp(z.X, 0, 1), Y = Math.Clamp(z.Y, 0, 1) }).ToArray(),
            Overlays = Overlays?.Where(o => o != null && Ok(o.Start, o.End)).Select(o => o.Validated()).ToArray(),
            Volumes = Volumes?.Where(v => v != null && Ok(v.Start, v.End) && v.Lane >= 0).Select(v => v with { Gain = Math.Clamp(v.Gain, 0, 2) }).ToArray(),
            Sounds = Sounds?.Where(s => s != null && Ok(s.Start, s.End) && !string.IsNullOrWhiteSpace(s.Path)).Select(s => s with { Volume = Math.Clamp(s.Volume, 0, 2), Offset = Math.Max(0, s.Offset), FadeIn = Math.Clamp(s.FadeIn, 0, 30), FadeOut = Math.Clamp(s.FadeOut, 0, 30), DuckLevel = Math.Clamp(s.DuckLevel, 0, 1) }).ToArray(),
            Crop = Crop is { Length: 4 } c && c.All(double.IsFinite) && c[2] > .01 && c[3] > .01 ? c : null,
        };
    }
    internal string Serialize() => JsonSerializer.Serialize(this with { Saved = null }, Json);
    internal void Save(string path, bool checkExtension = true)
    {
        if (checkExtension && !Path.GetExtension(path).Equals(".flashtrim",StringComparison.OrdinalIgnoreCase)) throw new IOException("Use the .flashtrim extension for Flashback projects.");
        Validate();
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp,JsonSerializer.Serialize(this with { Saved = DateTime.Now },Json)); File.Move(temp,path,true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

// Unsaved edits are kept here, one file per clip, so a closed or crashed trimmer can pick up where it
// left off. Only the ten most recent clips are kept.
internal static class TrimRecovery
{
    private static string Folder => Path.Combine(Storage.Root, "recovery");
    private static string PathFor(string source) =>
        Path.Combine(Folder, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source.ToLowerInvariant())))[..24] + ".flashtrim");
    internal static void Keep(TrimProject project)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            project.Save(PathFor(project.Source), checkExtension: false);
            foreach (var old in new DirectoryInfo(Folder).GetFiles("*.flashtrim").OrderByDescending(f => f.LastWriteTimeUtc).Skip(10)) try { old.Delete(); } catch { }
        }
        catch { /* Recovery is a safety net; editing carries on without it. */ }
    }
    internal static TrimProject? Find(string source)
    {
        try { string path = PathFor(source); return File.Exists(path) ? TrimProject.Read(path) : null; }
        catch { Forget(source); return null; }
    }
    internal static void Forget(string source) { try { File.Delete(PathFor(source)); } catch { } }
}

internal static class RecentTrimFiles
{
    private static string PathName => Path.Combine(Storage.Root,"recent-trim-files.json");
    internal static string[] Read()
    {
        try { return (JsonSerializer.Deserialize<string[]>(File.ReadAllText(PathName)) ?? Array.Empty<string>()).Where(File.Exists).Take(10).ToArray(); }
        catch { return Array.Empty<string>(); }
    }
    internal static void Remember(string path)
    {
        try { Directory.CreateDirectory(Storage.Root); File.WriteAllText(PathName,JsonSerializer.Serialize(new[]{Path.GetFullPath(path)}.Concat(Read()).Distinct(StringComparer.OrdinalIgnoreCase).Take(10))); }
        catch { }
    }
}
