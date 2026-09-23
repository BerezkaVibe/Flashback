using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Flashback;

public sealed class Settings
{
    public int ReplaySeconds { get; set; } = 60;
    public int FrameRate { get; set; } = 60;
    public int Height { get; set; } = 1080;
    public string Quality { get; set; } = "Balanced";
    public string Palette { get; set; } = "Charcoal";
    public string AccentColor { get; set; } = "Mint";
    public string Encoder { get; set; } = "Automatic";
    public int DisplayIndex { get; set; }
    public bool DesktopAudio { get; set; } = true;
    public string AudioDeviceId { get; set; } = "";
    public bool MicrophoneAudio { get; set; }
    public bool DesktopMuted { get; set; }
    public bool MicrophoneMuted { get; set; }
    public string MicrophoneDeviceId { get; set; } = "";
    public bool DesktopLocked { get; set; }
    public bool MicrophoneLocked { get; set; }
    public int DesktopVolume { get; set; } = 100;
    public int MicrophoneVolume { get; set; } = 100;
    public int AudioBitrate { get; set; } = 160;
    public bool CheckForUpdates { get; set; } = true;
    public bool SeparateAudioTracks { get; set; }
    public bool AutoStartWithGames { get; set; }
    public bool ShowCursor { get; set; }
    public string OutputFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Flashback");
    public string GameOverride { get; set; } = "";
    public string Hotkey { get; set; } = "Ctrl+Shift+F8";
    public string PauseHotkey { get; set; } = "Ctrl+Shift+F9";
    public string TrimAddHotkey { get; set; } = "+";
    public Dictionary<string, string> TrimKeys { get; set; } = new();
    public bool StartWithWindows { get; set; }
    public bool StartBufferOnLaunch { get; set; }
    public bool Notifications { get; set; }
    public string ExternalEditorPath { get; set; } = "";
    public bool OverlayEnabled { get; set; } = true;
    public bool FullscreenNotifications { get; set; } = true;
    public bool ShowSavingOverlay { get; set; } = true;
    public string OverlayCorner { get; set; } = "Top right";
    public int OverlaySeconds { get; set; } = 3;
    public Settings Copy() => JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(this))!;
    public void Validate()
    {
        if (ReplaySeconds < 30 || ReplaySeconds > 300 || ReplaySeconds % 5 != 0) throw new ArgumentException("Choose a replay length from 30 seconds to 5 minutes, in 5-second steps.");
        if (!new[] { 30, 60, 90, 120 }.Contains(FrameRate)) throw new ArgumentException("Choose 30, 60, 90 or 120 FPS.");
        if (!new[] { 0, 720, 1080, 1440, 2160 }.Contains(Height)) throw new ArgumentException("Choose a supported resolution.");
        if (!new[] { "Compact", "Balanced", "High" }.Contains(Quality)) throw new ArgumentException("Choose a quality preset.");
        if (DisplayIndex < 0 || DisplayIndex > 15) throw new ArgumentException("Invalid display index.");
        if (string.IsNullOrWhiteSpace(OutputFolder) || !Path.IsPathFullyQualified(OutputFolder)) throw new ArgumentException("Choose an absolute output folder.");
        if (OutputFolder.IndexOfAny(new[] { '\r', '\n' }) >= 0) throw new ArgumentException("Invalid output folder.");
        Hotkeys.Parse(Hotkey);
        if (!VideoEncoder.Preferences.Contains(Encoder)) throw new ArgumentException("Choose a supported hardware encoder.");
        var pause = Hotkeys.Parse(PauseHotkey);
        if (pause == Hotkeys.Parse(Hotkey)) throw new ArgumentException("Save and pause need different hotkeys.");
        TrimShortcuts.Validate(this);
        if (!new[] { "Top right", "Top left", "Bottom right", "Bottom left" }.Contains(OverlayCorner)) throw new ArgumentException("Choose an overlay corner.");
        if (DesktopVolume < 0 || DesktopVolume > 200 || MicrophoneVolume < 0 || MicrophoneVolume > 200) throw new ArgumentException("Choose a recording volume from 0% to 200%.");
        if (!AudioBitrates.Contains(AudioBitrate)) throw new ArgumentException("Choose a supported audio quality.");
        if (OverlaySeconds < 2 || OverlaySeconds > 8) throw new ArgumentException("Choose an overlay duration from 2 to 8 seconds.");
    }
    public bool RequiresBufferRestart(Settings other) => ReplaySeconds != other.ReplaySeconds || FrameRate != other.FrameRate
        || Height != other.Height || Quality != other.Quality || Encoder != other.Encoder || DisplayIndex != other.DisplayIndex
        || DesktopAudio != other.DesktopAudio || AudioDeviceId != other.AudioDeviceId
        || MicrophoneAudio != other.MicrophoneAudio || MicrophoneDeviceId != other.MicrophoneDeviceId
        || ShowCursor != other.ShowCursor || OutputFolder != other.OutputFolder || AudioBitrate != other.AudioBitrate || SeparateAudioTracks != other.SeparateAudioTracks;
    public static readonly int[] AudioBitrates = { 128, 160, 192, 256, 320 };
    public int BitrateMbps => (int)Math.Round((Quality switch { "Compact" => 8, "High" => 24, _ => 14 }) * (Height switch { 720 => 0.6, 1440 => 1.7, 2160 => 3.0, 0 => 2.0, _ => 1.0 }) * Math.Max(0.65, FrameRate / 60.0));
    public double EstimatedBufferMb => BitrateMbps * (ReplaySeconds + 12) / 8.0 * 1.1;
}

public static class Storage
{
    public static string Root { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Flashback");
    public static string ConfigPath => Path.Combine(Root, "settings.json");
    public static Settings Load(out string? warning)
    {
        warning = null;
        try
        {
            if (!File.Exists(ConfigPath)) return new();
            var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(ConfigPath)) ?? new();
            try { TrimShortcuts.Validate(s); }
            catch (ArgumentException)
            {
                s.TrimAddHotkey="+"; s.TrimKeys.Clear();
                warning="Trimmer shortcuts were reset to their defaults because two of them shared a key. Other preferences were preserved.";
            }
            s.Validate();
            return s;
        }
        catch (Exception ex) { warning = "Could not load settings; defaults restored. " + ex.Message; return new(); }
    }
    public static void Save(Settings settings)
    {
        settings.Validate();
        Directory.CreateDirectory(Root);
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, ConfigPath, true);
    }
    public static void EnsureWritable(string folder)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, ".flashback-write-test-" + Guid.NewGuid().ToString("N"));
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
    }
    public static string SafeName(string name)
    {
        var clean = new string(name.Select(c => char.IsControl(c) || Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim().Trim('.');
        clean = Regex.Replace(clean, @"\s+", " ");
        if (string.IsNullOrWhiteSpace(clean)) clean = "Desktop";
        if (Regex.IsMatch(clean, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])($|\.)", RegexOptions.IgnoreCase)) clean = "_" + clean;
        return clean[..Math.Min(clean.Length, 90)].TrimEnd('.', ' ');
    }
    public static string ClipName(string game, DateTimeOffset capturedAt, double duration)
        => $"{SafeName(game)} — {capturedAt:yyyy-MM-dd_HH-mm-ss-fff} — {duration:0.#}s.mp4";
}

public record Segment(string Name, double Start, double End)
{
    public double Duration => End - Start;
}

public static class SegmentIndex
{
    // Names are generated internally and intentionally restricted before touching the filesystem.
    public static List<Segment> Parse(string text)
    {
        var result = new List<Segment>();
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Trim().Split(',');
            if (parts.Length != 3) continue;
            var name = parts[0].Trim('"');
            if (!Regex.IsMatch(name, @"^part-\d{9}\.ts$")) continue;
            if (double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double start)
                && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double end)
                && double.IsFinite(start) && double.IsFinite(end) && start >= 0 && end > start)
                result.Add(new(name, start, end));
        }
        return result.OrderBy(s => s.Start).ToList();
    }
    public static List<Segment> Select(IReadOnlyList<Segment> all, double requestedEnd, int seconds)
    {
        var boundary = all.FirstOrDefault(s => s.End >= requestedEnd - 0.001)?.End ?? all.LastOrDefault()?.End ?? 0;
        return all.Where(s => s.End <= boundary + 0.001 && s.End > boundary - seconds + 0.001).ToList();
    }
}

public record ClipResult(string Path, double Duration, string Game, DateTimeOffset SavedAt);




