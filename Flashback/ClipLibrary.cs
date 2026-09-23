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
}
