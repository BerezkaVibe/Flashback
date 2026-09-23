using System;
using System.IO;
using System.Windows;
namespace Flashback;

internal static class TrimImport
{
    internal static bool Supported(string path) => Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".m4v" or ".mov";
    internal static string[] Files(IDataObject data)
    {
        try { return data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] paths ? paths : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }
    internal static bool CanDrop(string[] paths) => paths.Length == 1 && Supported(paths[0]);
    internal static (string Path, ClipMedia Media) Read(string[] paths)
    {
        if (paths.Length != 1) throw new ArgumentException("Open one video at a time.");
        string path=Path.GetFullPath(paths[0]);
        if (!Supported(path)) throw new ArgumentException("Choose an MP4, M4V or MOV video.");
        if (!File.Exists(path)) throw new FileNotFoundException("This video is no longer available.",path);
        return (path,ClipMedia.Read(path));
    }
}
