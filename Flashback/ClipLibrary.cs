using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Flashback;

public record LibraryClip(string Path, string Game, DateTime Saved, long Bytes)
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public string DateLabel => Saved.ToString("yyyy-MM-dd  HH:mm");
    public string SizeLabel => Bytes < 1048576 ? $"{Bytes / 1024.0:0} KB" : $"{Bytes / 1048576.0:0.0} MB";
}

public static class ClipLibrary
{
    // Scan only on request. No thumbnail workers, video decoders or folder polling.
    public static List<LibraryClip> Load(string folder)
    {
        var clips = new Dictionary<string, LibraryClip>(StringComparer.OrdinalIgnoreCase);
        void Add(string path, string? game = null)
        {
            try
            {
                var file = new FileInfo(path);
                if (file.Exists && file.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
                    clips[file.FullName] = new(file.FullName, game ?? file.Directory?.Name ?? "Clips", file.LastWriteTime, file.Length);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (Directory.Exists(folder))
            foreach (var path in Directory.EnumerateFiles(folder, "*.mp4", new EnumerationOptions
            { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })) Add(path);
        var history = System.IO.Path.Combine(Storage.Root, "clips.jsonl");
        if (File.Exists(history))
        {
            using var stream = new FileStream(history, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                try { if (JsonSerializer.Deserialize<ClipResult>(line) is { } c) Add(c.Path, c.Game); }
                catch (JsonException) { }
                catch (ArgumentException) { }
            }
        }
        return clips.Values.OrderByDescending(c => c.Saved).ToList();
    }
    public static void Remember(ClipResult clip)
    {
        Directory.CreateDirectory(Storage.Root);
        File.AppendAllText(System.IO.Path.Combine(Storage.Root, "clips.jsonl"), JsonSerializer.Serialize(clip) + Environment.NewLine);
    }
    // Renames a clip in place, keeping its extension and folder. The saved-clip history is updated
    // too, so clips kept outside the clips folder (trimmer exports) stay in the library.
    public static string Rename(string path, string name)
    {
        name = name.Trim();
        string extension = System.IO.Path.GetExtension(path);
        if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) name = name[..^extension.Length].TrimEnd();
        if (name.Length == 0) throw new ArgumentException("Type a name for the clip.");
        if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("Clip names can't contain \\ / : * ? \" < > |");
        if (name.EndsWith('.')) throw new ArgumentException("Clip names can't end with a period.");
        string target = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, name + extension);
        if (string.Equals(target, path, StringComparison.Ordinal)) return path;
        // A case-only change is the same file; anything else must not overwrite another clip.
        if (File.Exists(target) && !string.Equals(target, path, StringComparison.OrdinalIgnoreCase)) throw new IOException("Another clip in this folder already has that name.");
        File.Move(path, target);
        var history = System.IO.Path.Combine(Storage.Root, "clips.jsonl");
        try
        {
            if (File.Exists(history))
            {
                var lines = File.ReadAllLines(history).Select(line =>
                {
                    try { return JsonSerializer.Deserialize<ClipResult>(line) is { } c && string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase) ? JsonSerializer.Serialize(c with { Path = target }) : line; }
                    catch (JsonException) { return line; }
                }).ToArray();
                var temp = history + ".tmp"; File.WriteAllLines(temp, lines); File.Move(temp, history, true);
            }
        }
        catch (IOException) { }
        return target;
    }
}
