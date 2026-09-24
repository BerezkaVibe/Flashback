using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Flashback;

// Audio parts: volume changes on the recorded lanes, and music or sound files on their own rows
// under them. Both live in the fold-away audio area, and their tools only show while it's open.
public partial class TrimWindow
{
    internal static readonly string[] SoundExtensions = { ".mp3", ".wav", ".m4a", ".aac", ".ogg", ".flac", ".wma", ".opus" };
    private static readonly double[] VolumeChoiceValues = { 0, 25, 50, 150, 200 };
    private VolumeRegion? popupVolume;
    private SoundItem? popupSound;
    private Action? volumeTiming;
    private bool volumeSnapshotted, loadingVolume;

    private void InitAudioParts()
    {
        Timeline.VolumeAdded += VolumeAdded; Timeline.VolumeTagClicked += OpenVolumePopup; Timeline.VolumeRemoved += RemoveVolume;
        Timeline.SoundAdded += SoundAdded; Timeline.SoundPicked += OpenSoundPopup; Timeline.SoundRemoved += RemoveSound;
        Timeline.SoundEditStarted += () => Snapshot();
        Timeline.SoundMoved += (old, next) => { ReplaceSound(old, next); FocusPart(next); };
        Timeline.SoundEditFinished += () => { UpdateExportHint(); UpdateSummary(); };
        Timeline.LanesExpandedChanged += ShowAudioTools;
        VolumeTimingHost.Children.Add(TimingEditor(() => popupVolume is { } v && Timeline.VolumeRegions.Contains(v) ? v : null, out volumeTiming));
        foreach (double value in VolumeChoiceValues)
        {
            var b = new Button { Content = $"{value:0}%", FontSize = 11, MinHeight = 24, Height = 24, Padding = new Thickness(0), Margin = new Thickness(1, 0, 1, 0) };
            b.SetResourceReference(StyleProperty, "TrimButton");
            b.Click += (_, _) => SetVolumeGain(value / 100, fromSlider: false);
            VolumeChoices.Children.Add(b);
        }
    }
    // The volume and sound tools slide in with the audio lanes and away when they fold.
    private void ShowAudioTools()
    {
        double target = Timeline.LanesExpanded ? 1 : 0;
        if (SystemParameters.ClientAreaAnimation && PerformanceOptions.Animations && IsLoaded)
            AudioToolsScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        else { AudioToolsScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); AudioToolsScale.ScaleX = target; }
        AudioTools.IsHitTestVisible = Timeline.LanesExpanded;
        if (!Timeline.LanesExpanded && (Timeline.VolumeMode || Timeline.SoundMode)) { Timeline.VolumeMode = false; Timeline.SoundMode = false; ShowCutTool(); }
    }
    private void VolumeTool_Click(object sender, RoutedEventArgs e)
    {
        if (source.Length == 0 || exportCancellation != null) return;
        if (Timeline.Lanes.Count == 0) { StatusLabel.Text = "This clip has no sound to change."; return; }
        if (!Timeline.LanesExpanded) LanesToggleRequested();
        Timeline.VolumeMode = !Timeline.VolumeMode; ShowCutTool();
        StatusLabel.Text = Timeline.VolumeMode ? "Click two spots on an audio lane to make that part louder or quieter · Esc leaves the tool." : "Tool off.";
    }
    private void SoundTool_Click(object sender, RoutedEventArgs e)
    {
        if (source.Length == 0 || exportCancellation != null) return;
        if (!Timeline.LanesExpanded && Timeline.Lanes.Count + Timeline.Sounds.Count > 0) LanesToggleRequested();
        Timeline.SoundMode = !Timeline.SoundMode; ShowCutTool();
        StatusLabel.Text = Timeline.SoundMode ? "Click two spots where the music or sound should play, then pick the file · Esc leaves the tool." : "Tool off.";
    }

    // ---- Volume parts ----
    private void VolumeAdded(int lane, double start, double end)
    {
        if (Timeline.VolumeRegions.Any(v => v.Lane == lane && v.End > start + 1e-9 && v.Start < end - 1e-9)) { StatusLabel.Text = "That overlaps another volume change on this lane. Click its tag to change it."; return; }
        Snapshot();
        var region = new VolumeRegion(lane, start, end, 1.5);
        Timeline.VolumeRegions = Timeline.VolumeRegions.Append(region).OrderBy(v => v.Lane).ThenBy(v => v.Start).ToArray();
        UpdateExportHint(); ProjectChanged();
        OpenVolumePopup(region);
        StatusLabel.Text = "Volume part added at 150%. Pick a level, or drag its ends on the lane.";
    }
    private void OpenVolumePopup(VolumeRegion region)
    {
        if (exportCancellation != null) return;
        FocusPart(region); popupVolume = region; volumeSnapshotted = false;
        VolumePopupTitle.Text = region.Lane < Timeline.Lanes.Count ? $"{Timeline.Lanes[region.Lane].Name} volume" : "Volume";
        loadingVolume = true; VolumeSlider.Value = region.Gain * 100; loadingVolume = false;
        VolumeLabel.Text = $"{region.Gain * 100:0}%";
        volumeTiming?.Invoke();
        VolumePopup.IsOpen = true;
    }
    private void VolumeSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (loadingVolume || VolumeLabel == null) return;
        SetVolumeGain(Math.Round(e.NewValue) / 100, fromSlider: true);
    }
    private void SetVolumeGain(double gain, bool fromSlider)
    {
        if (popupVolume is not { } current || !Timeline.VolumeRegions.Contains(current)) return;
        if (!fromSlider || !volumeSnapshotted) { Snapshot(); volumeSnapshotted = fromSlider; }
        var next = current with { Gain = Math.Clamp(gain, 0, 2) };
        Timeline.VolumeRegions = Timeline.VolumeRegions.Select(v => v == current ? next : v).ToArray();
        popupVolume = next; FocusPart(next);
        VolumeLabel.Text = $"{next.Gain * 100:0}%";
        if (!fromSlider) { loadingVolume = true; VolumeSlider.Value = next.Gain * 100; loadingVolume = false; }
        UpdateExportHint(); ProjectChanged();
    }
    private void VolumeRemove_Click(object sender, RoutedEventArgs e) { VolumePopup.IsOpen = false; if (popupVolume is { } v) RemoveVolume(v); }
    private void RemoveVolume(VolumeRegion region)
    {
        if (!Timeline.VolumeRegions.Contains(region)) return;
        Snapshot(); Timeline.VolumeRegions = Timeline.VolumeRegions.Where(v => v != region).ToArray();
        UpdateExportHint(); ProjectChanged(); StatusLabel.Text = "Volume change removed.";
    }

    // ---- Sounds ----
    private void SetSounds(IReadOnlyList<SoundItem> list) { Timeline.Sounds = list; SyncSounds(); }
    private void ReplaceSound(SoundItem old, SoundItem next)
    {
        SetSounds(Timeline.Sounds.Select(s => s == old ? next : s).ToArray());
        if (popupSound == old) popupSound = next;
        ProjectChanged();
    }
    private static int FreeSoundRow(IEnumerable<SoundItem> sounds, double start, double end)
    {
        var list = sounds.ToList();
        for (int row = 0; ; row++) if (!list.Any(s => s.Row == row && s.End > start + 1e-9 && s.Start < end - 1e-9)) return row;
    }
    private void SoundAdded(double start, double end)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Add music or a sound", Filter = "Sound files|" + string.Join(";", SoundExtensions.Select(x => "*" + x)) + ";*.mp4;*.m4v;*.mov|All files|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) { StatusLabel.Text = "No sound added."; return; }
        AddSound(dialog.FileName, start, end);
    }
    private void AddSound(string path, double start, double end)
    {
        Snapshot();
        var sound = new SoundItem(path, start, end, Row: FreeSoundRow(Timeline.Sounds, start, end));
        SetSounds(Timeline.Sounds.Append(sound).ToArray());
        if (!Timeline.LanesExpanded) LanesToggleRequested();
        UpdateExportHint(); UpdateSummary();
        OpenSoundPopup(sound);
        StatusLabel.Text = $"Added {Path.GetFileName(path)}. It's mixed over the finished export, so speed parts don't change it.";
    }
    // Dropping a sound file adds it at the playhead for as long as it lasts (to the end of the clip).
    internal async Task<bool> DropSoundAsync(string path)
    {
        if (source.Length == 0 || exportCancellation != null || !SoundExtensions.Contains(Path.GetExtension(path).ToLowerInvariant())) return false;
        double start = Math.Min(playhead, Math.Max(0, media.Duration - .1));
        double length = await SoundLengthAsync(path) ?? 30;
        AddSound(path, start, Math.Min(media.Duration, start + length));
        return true;
    }
    // How long a sound file lasts, read from ffmpeg's summary of it.
    internal static async Task<double?> SoundLengthAsync(string path)
    {
        try
        {
            var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var a in new[] { "-hide_banner", "-i", path }) info.ArgumentList.Add(a);
            using var p = Process.Start(info)!;
            string text = await p.StandardError.ReadToEndAsync(); await p.WaitForExitAsync();
            var m = Regex.Match(text, @"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)");
            return m.Success ? int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : null;
        }
        catch { return null; }
    }
    private void RemoveSound(SoundItem sound)
    {
        if (!Timeline.Sounds.Contains(sound)) return;
        Snapshot(); SetSounds(Timeline.Sounds.Where(s => s != sound).ToArray());
        SoundPopup.IsOpen = false; UpdateExportHint(); UpdateSummary(); ProjectChanged();
        StatusLabel.Text = "Sound removed. Undo brings it back.";
    }
    // The sound's settings, built in code: volume, fades, ducking, where in the file it starts, and when.
    private void OpenSoundPopup(SoundItem sound)
    {
        if (exportCancellation != null) return;
        FocusPart(sound); popupSound = sound;
        var panel = SoundControls; panel.Children.Clear();
        string control = ""; long lastChange = 0;
        void Edit(string name, Func<SoundItem, SoundItem> change)
        {
            if (popupSound is not { } current || !Timeline.Sounds.Contains(current)) return;
            if (name != control || Stopwatch.GetElapsedTime(lastChange).TotalSeconds > 1.2) Snapshot();
            control = name; lastChange = Stopwatch.GetTimestamp();
            var next = change(current); ReplaceSound(current, next); FocusPart(next); UpdateExportHint();
        }
        var title = new TextBlock { Text = "♪ " + sound.Label, FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(title);
        FrameworkElement Slider(string label, double min, double max, double value, Func<double, string> format, Action<double> set)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var name = new TextBlock { Text = label, Width = 74, FontSize = 11, VerticalAlignment = VerticalAlignment.Center }; name.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            var shown = new TextBlock { Width = 44, TextAlignment = TextAlignment.Right, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Text = format(value) }; shown.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            var slider = new Slider { Minimum = min, Maximum = max, Value = value, IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center };
            slider.ValueChanged += (_, e) => { shown.Text = format(e.NewValue); set(e.NewValue); };
            DockPanel.SetDock(name, Dock.Left); DockPanel.SetDock(shown, Dock.Right);
            row.Children.Add(name); row.Children.Add(shown); row.Children.Add(slider);
            panel.Children.Add(row); return row;
        }
        Slider("Volume", 0, 200, sound.Volume * 100, v => $"{v:0}%", v => Edit("volume", s => s with { Volume = Math.Round(v) / 100 }));
        Slider("Fade in", 0, 5, sound.FadeIn, v => v < .05 ? "Off" : $"{v:0.0} s", v => Edit("fadeIn", s => s with { FadeIn = v < .05 ? 0 : Math.Round(v, 1) }));
        Slider("Fade out", 0, 5, sound.FadeOut, v => v < .05 ? "Off" : $"{v:0.0} s", v => Edit("fadeOut", s => s with { FadeOut = v < .05 ? 0 : Math.Round(v, 1) }));
        var duck = new CheckBox { Content = "Lower the clip's sound while this plays", IsChecked = sound.Duck, Margin = new Thickness(0, 6, 0, 2) };
        FrameworkElement? duckLevel = null;
        duck.Click += (_, _) => { Edit("duck", s => s with { Duck = duck.IsChecked == true }); if (duckLevel != null) duckLevel.Visibility = duck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed; };
        panel.Children.Add(duck);
        duckLevel = Slider("Clip sound", 0, 100, sound.DuckLevel * 100, v => $"{v:0}%", v => Edit("duckLevel", s => s with { DuckLevel = Math.Round(v) / 100 }));
        duckLevel.Visibility = sound.Duck ? Visibility.Visible : Visibility.Collapsed;
        var offsetRow = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
        var offsetName = new TextBlock { Text = "Start from", Width = 74, FontSize = 11, VerticalAlignment = VerticalAlignment.Center }; offsetName.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var offset = new TextBox { Text = KeepSection.TimeText(sound.Offset), Height = 28, FontFamily = new FontFamily("Consolas"), FontSize = 12, ToolTip = "Where in the file the sound starts playing" };
        void CommitOffset()
        {
            try { double t = Math.Max(0, KeepSection.Parse(offset.Text)); Edit("offset", s => s with { Offset = t }); }
            catch (ArgumentException ex) { StatusLabel.Text = ex.Message; }
        }
        offset.KeyDown += (_, k) => { if (k.Key == System.Windows.Input.Key.Enter) { CommitOffset(); k.Handled = true; } };
        offset.LostKeyboardFocus += (_, _) => CommitOffset();
        DockPanel.SetDock(offsetName, Dock.Left); offsetRow.Children.Add(offsetName); offsetRow.Children.Add(offset);
        panel.Children.Add(offsetRow);
        panel.Children.Add(TimingEditor(() => popupSound is { } s && Timeline.Sounds.Contains(s) ? s : null, out var refresh)); refresh();
        var remove = new Button { Content = "Remove sound", FontSize = 11, MinHeight = 26, Height = 26, Margin = new Thickness(0, 6, 0, 0), Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Stretch };
        remove.SetResourceReference(StyleProperty, "TrimButton");
        remove.Click += (_, _) => { if (popupSound is { } s) RemoveSound(s); };
        panel.Children.Add(remove);
        SoundPopup.IsOpen = true;
    }

    // ---- Preview ----
    // Each sound plays through its own player, kept in step with the playhead while the preview plays.
    private readonly Dictionary<string, MediaPlayer> soundPlayers = new();
    private readonly HashSet<string> soundsPlaying = new();
    private void SyncSounds()
    {
        if (!previewEnabled) return;
        var wanted = new HashSet<string>();
        for (int i = 0; i < Timeline.Sounds.Count; i++)
        {
            var s = Timeline.Sounds[i]; string key = i + "|" + s.Path; wanted.Add(key);
            if (!soundPlayers.TryGetValue(key, out var player))
            {
                player = new MediaPlayer(); try { player.Open(new Uri(s.Path)); } catch { continue; }
                soundPlayers[key] = player;
            }
            bool inside = playhead >= s.Start && playhead < s.End;
            if (playing && inside)
            {
                double local = playhead - s.Start, fade = 1;
                if (s.FadeIn > 0) fade = Math.Min(fade, local / s.FadeIn);
                if (s.FadeOut > 0) fade = Math.Min(fade, (s.End - playhead) / s.FadeOut);
                player.Volume = Math.Clamp(s.Volume * Math.Clamp(fade, 0, 1), 0, 1);
                player.SpeedRatio = PreviewRate;
                var expected = TimeSpan.FromSeconds(s.Offset + local);
                if (!soundsPlaying.Contains(key)) { player.Position = expected; player.Play(); soundsPlaying.Add(key); }
                else if (Math.Abs((player.Position - expected).TotalSeconds) > .3) player.Position = expected;
            }
            else if (soundsPlaying.Remove(key)) player.Pause();
        }
        foreach (var gone in soundPlayers.Keys.Where(k => !wanted.Contains(k)).ToList()) { soundPlayers[gone].Close(); soundPlayers.Remove(gone); soundsPlaying.Remove(gone); }
    }
    private void StopSounds() { foreach (var (key, player) in soundPlayers) player.Pause(); soundsPlaying.Clear(); }
    private void CloseSounds() { foreach (var player in soundPlayers.Values) player.Close(); soundPlayers.Clear(); soundsPlaying.Clear(); }
}
