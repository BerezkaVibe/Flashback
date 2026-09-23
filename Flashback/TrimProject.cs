using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Flashback;

internal sealed record TrimProject(int Version, string Source, long SourceBytes, long SourceWriteTicks,
    KeepSection[] Sections, double Start, double End, double Position, bool RoughCut)
{
    internal static TrimProject Create(string source, KeepSection[] sections, double start, double end, double position, bool rough)
    {
        var file = new FileInfo(source);
        return new(1, file.FullName, file.Length, file.LastWriteTimeUtc.Ticks, sections, start, end, position, rough);
    }
    internal static TrimProject Read(string path)
    {
        if (new FileInfo(path).Length > 1_000_000) throw new IOException("This trim project is too large.");
        var project = JsonSerializer.Deserialize<TrimProject>(File.ReadAllText(path)) ?? throw new IOException("Invalid trim project.");
        project.Validate(); return project;
    }
    internal void Validate()
    {
        if (Version != 1 || string.IsNullOrWhiteSpace(Source) || !Path.IsPathFullyQualified(Source) || Sections == null) throw new IOException("Unsupported trim project.");
        var file = new FileInfo(Source);
        if (!file.Exists || file.Length != SourceBytes || file.LastWriteTimeUtc.Ticks != SourceWriteTicks)
            throw new IOException("The project's original video is missing or has changed. Open the video separately to start a new edit.");
        var media = ClipMedia.Read(Source);
        if (Sections.Length > 0) ClipEditor.Validate(Sections,media.Duration);
        if (!double.IsFinite(Start) || !double.IsFinite(End) || !double.IsFinite(Position) || Start < 0 || End < Start || End > media.Duration || Position < 0 || Position > media.Duration)
            throw new IOException("The project contains invalid trim times.");
    }
    internal void Save(string path)
    {
        if (!Path.GetExtension(path).Equals(".flashtrim",StringComparison.OrdinalIgnoreCase)) throw new IOException("Use the .flashtrim extension for trim projects.");
        Validate();
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp,JsonSerializer.Serialize(this,new JsonSerializerOptions { WriteIndented=true })); File.Move(temp,path,true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
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
