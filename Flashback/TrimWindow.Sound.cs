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
        Timeline.VideoHasSound = VideoHasSound; Timeline.VideoSoundPicked += OpenVideoSoundPopup;
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
        // In the finished view the two clicks mark how long it plays there (slowed parts and holds included).
        if (Timeline.Finished) end = start + Math.Max(FrameStep, Timeline.EndView(end) - Timeline.ToView(start));
        AddSound(dialog.FileName, start, end);
    }
    // hold: how far into a freeze's hold at start it begins.
    private void AddSound(string path, double start, double end, double hold = 0)
    {
        Snapshot();
        var sound = new SoundItem(path, start, end, Row: FreeSoundRow(Timeline.Sounds, start, end)) { Hold = hold };
        SetSounds(Timeline.Sounds.Append(sound).ToArray());
        if (!Timeline.LanesExpanded) LanesToggleRequested();
        UpdateExportHint(); UpdateSummary();
        OpenSoundPopup(sound);
        StatusLabel.Text = $"Added {Path.GetFileName(path)}. It plays at its own speed over the finished video; change its speed here, or match the part under it.";
    }
    // Dropping a sound file adds it at the playhead for as long as it lasts (to the end of the clip).
    internal async Task<bool> DropSoundAsync(string path)
    {
        if (source.Length == 0 || exportCancellation != null || !SoundExtensions.Contains(Path.GetExtension(path).ToLowerInvariant())) return false;
        double start = Math.Min(playhead, Math.Max(0, media.Duration - .1));
        double length = await SoundLengthAsync(path) ?? 30;
        if (Timeline.Finished)
        {
            // Plays from the playhead to the end of the finished video at most, even from inside a hold.
            double from = Timeline.PositionView;
            AddSound(path, playhead, playhead + Math.Max(FrameStep, Math.Min(length, Timeline.Map.Total - from)), Timeline.HoldOffset);
        }
        else AddSound(path, start, Math.Min(media.Duration, start + length));
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
        // Its own speed: normal under slowed parts and freezes, or slowed (or sped up) to match them.
        // The slider runs in doublings so 0.5× and 2× sit either side of 1×.
        Slider("Speed", Math.Log2(SoundItem.MinSpeed), Math.Log2(SoundItem.MaxSpeed), Math.Log2(sound.Speed), v => $"{SoundSpeed(v):0.##}×", v => Edit("speed", s => s with { Speed = SoundSpeed(v) }));
        static double SoundSpeed(double log) { double speed = Math.Pow(2, log); return Math.Abs(speed - 1) < .04 ? 1 : Math.Round(speed, 2); }
        var pitch = new CheckBox { Content = "Keep its pitch", IsChecked = sound.KeepPitch, Margin = new Thickness(0, 4, 0, 2), ToolTip = "Off: slower sounds deeper and faster sounds higher, like a tape" };
        pitch.Click += (_, _) => Edit("pitch", s => s with { KeepPitch = pitch.IsChecked == true });
        panel.Children.Add(pitch);
        double under = RegionSpeedAt(sound.Start) * ExportSpeedValue;
        var match = new Button { Content = $"Match the speed under it ({under:0.##}×)", FontSize = 11, MinHeight = 26, Height = 26, Margin = new Thickness(0, 2, 0, 4), HorizontalAlignment = HorizontalAlignment.Stretch,
            ToolTip = "Play it at the speed of the video where it starts, so it slows down or speeds up with that part" };
        match.SetResourceReference(StyleProperty, "TrimButton");
        match.Click += (_, _) => { Edit("match", s => s with { Speed = Math.Clamp(Math.Round(under, 2), SoundItem.MinSpeed, SoundItem.MaxSpeed) }); if (popupSound is { } now) OpenSoundPopup(now); };
        panel.Children.Add(match);
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

    // ---- Videos' own sound ----
    // A video over the clip that has sound shows it on the audio timeline, linked to the video. Its pop-up
    // sets how loud it is, or unlinks it into a sound of its own (to move, trim, fade or speed up apart from
    // the video); the video then plays silent.
    private readonly Dictionary<string, bool> videosWithSound = new(StringComparer.OrdinalIgnoreCase);
    private bool VideoHasSound(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (videosWithSound.TryGetValue(path, out bool has)) return has;
        try { has = File.Exists(path) && ClipMedia.Read(path).HasAudio; } catch { has = false; }
        return videosWithSound[path] = has;
    }
    private void OpenVideoSoundPopup(int index)
    {
        if (exportCancellation != null || index < 0 || index >= Timeline.Overlays.Count || Timeline.Overlays[index] is not { Kind: OverlayKind.Video } video) return;
        FocusPart(video); popupSound = null;
        string path = video.VideoPath;
        // The video as it is now, if it's still there (edits replace it at the same place in the list).
        OverlayItem? Current() => index < Timeline.Overlays.Count && Timeline.Overlays[index] is { Kind: OverlayKind.Video, SoundUnlinked: false } o && o.VideoPath == path ? o : null;
        var panel = SoundControls; panel.Children.Clear();
        string control = ""; long lastChange = 0;
        void Edit(string name, Func<OverlayItem, OverlayItem> change)
        {
            if (Current() is not { } current) return;
            if (name != control || Stopwatch.GetElapsedTime(lastChange).TotalSeconds > 1.2) Snapshot();
            control = name; lastChange = Stopwatch.GetTimestamp();
            var next = change(current).Validated(); ReplaceOverlay(index, next); FocusPart(next); UpdateExportHint();
            if (Timeline.SelectedOverlay == index) LoadOverlayUi();
        }
        panel.Children.Add(new TextBlock { Text = "♪ " + video.Label, FontSize = 12, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 2) });
        var note = new TextBlock { Text = "The video's own sound. It's linked to the video, so it moves and trims with it.", FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); panel.Children.Add(note);
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var name = new TextBlock { Text = "Volume", Width = 74, FontSize = 11, VerticalAlignment = VerticalAlignment.Center }; name.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        static string Level(double v) => v < .5 ? "Muted" : $"{v:0}%";
        var shown = new TextBlock { Width = 44, TextAlignment = TextAlignment.Right, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Text = Level(video.VideoVolume * 100) }; shown.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        var slider = new Slider { Minimum = 0, Maximum = 200, Value = video.VideoVolume * 100, IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center, ToolTip = "How loud the video's sound is in the export (the preview plays it up to 100%)" };
        slider.ValueChanged += (_, e) => { shown.Text = Level(e.NewValue); Edit("volume", o => o with { VideoVolume = Math.Round(e.NewValue) / 100 }); };
        DockPanel.SetDock(name, Dock.Left); DockPanel.SetDock(shown, Dock.Right);
        row.Children.Add(name); row.Children.Add(shown); row.Children.Add(slider); panel.Children.Add(row);
        Button AddButton(string text, string tip, Action click)
        {
            var b = new Button { Content = text, FontSize = 11, MinHeight = 26, Height = 26, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch, ToolTip = tip };
            b.SetResourceReference(StyleProperty, "TrimButton"); b.Click += (_, _) => click(); panel.Children.Add(b); return b;
        }
        AddButton("Unlink from the video", "Make it a sound of its own: move it, trim it, fade it or change its speed apart from the video", () => UnlinkVideoSound(index, path));
        var settings = AddButton("Video settings", "Open the video's own settings", () => { SoundPopup.IsOpen = false; OpenOverlay(index); });
        settings.Background = Brushes.Transparent; settings.BorderBrush = Brushes.Transparent;
        SoundPopup.IsOpen = true;
    }
    private void UnlinkVideoSound(int index, string path)
    {
        if (index >= Timeline.Overlays.Count || Timeline.Overlays[index] is not { Kind: OverlayKind.Video, SoundUnlinked: false } video || video.VideoPath != path) return;
        // It starts where the video does and plays as long as the video shows, so it's where it was; the row is
        // free where it plays.
        var map = Timeline.Map;
        double at = map.ToOutput(video.From(), false), length = map.Spans(video.From(), video.To()).Sum(s => s.To - s.From);
        int row = 0;
        while (Timeline.Sounds.Any(s => s.Row == row && map.ToOutput(s.Start) + s.Hold < at + length - 1e-9 && map.ToOutput(s.Start) + s.Hold + s.Length > at + 1e-9)) row++;
        if (SoundItem.FromVideo(video, map, row) is not { } sound) { StatusLabel.Text = "That video isn't in the finished video, so there's no sound to unlink."; return; }
        Snapshot();
        ReplaceOverlay(index, video with { SoundUnlinked = true });
        SetSounds(Timeline.Sounds.Append(sound).ToArray());
        if (Timeline.SelectedOverlay == index) LoadOverlayUi();
        UpdateExportHint(); UpdateSummary(); ProjectChanged();
        OpenSoundPopup(sound);
        StatusLabel.Text = $"Unlinked the sound of {video.Label}. It's a sound of its own now: move it, trim it, fade it or change its speed; the video plays silent."
            + (Math.Abs(sound.Speed - 1) > 1e-9 ? $" It plays at {sound.Speed:0.##}× to stay with the video's speed." : "");
    }

    // ---- Preview ----
    // Each sound plays through its own player, kept in step with the playhead while the preview plays.
    private readonly Dictionary<string, MediaPlayer> soundPlayers = new();
    private readonly HashSet<string> soundsPlaying = new();
    private void SyncSounds()
    {
        // (Not while the players are put away in the background; they come back with the editor.)
        if (!previewEnabled || playersReleased) return;
        var wanted = new HashSet<string>();
        // Sounds play along the finished video, as they're exported: from where they're anchored, for real
        // seconds, through holds and speed parts alike. Footage that isn't kept has no sound over it.
        var map = Timeline.Sounds.Count > 0 ? Timeline.Map : null;
        bool kept = map != null && (sections.Count == 0 ? playhead >= Timeline.Start - 1e-6 && playhead <= Timeline.End + 1e-6 : map.SectionOf(playhead) >= 0);
        double now = map != null ? map.ToOutput(playhead) + HoldAt(playhead) : 0;
        for (int i = 0; i < Timeline.Sounds.Count; i++)
        {
            var s = Timeline.Sounds[i]; string key = i + "|" + s.Path; wanted.Add(key);
            if (!soundPlayers.TryGetValue(key, out var player))
            {
                if (PreviewSoundPath(s.Path) is not { } file) continue;
                player = new MediaPlayer(); try { player.Open(new Uri(file)); } catch { continue; }
                soundPlayers[key] = player;
            }
            double at = map!.ToOutput(s.Start) + s.Hold, local = now - at, speed = Math.Clamp(s.Speed, SoundItem.MinSpeed, SoundItem.MaxSpeed);
            bool inside = kept && local >= 0 && local < s.Length;
            if (playing && inside)
            {
                double fade = 1;
                if (s.FadeIn > 0) fade = Math.Min(fade, local / s.FadeIn);
                if (s.FadeOut > 0) fade = Math.Min(fade, (s.Length - local) / s.FadeOut);
                player.Volume = Math.Clamp(s.Volume * Math.Clamp(fade, 0, 1) * PreviewLoudness, 0, 1);
                player.SpeedRatio = PreviewRate * speed;
                var expected = TimeSpan.FromSeconds(s.Offset + local * speed);
                if (!soundsPlaying.Contains(key)) { player.Position = expected; player.Play(); soundsPlaying.Add(key); }
                else if (Math.Abs((player.Position - expected).TotalSeconds) > .3) player.Position = expected;
            }
            else if (soundsPlaying.Remove(key)) player.Pause();
        }
        foreach (var gone in soundPlayers.Keys.Where(k => !wanted.Contains(k)).ToList()) { soundPlayers[gone].Close(); soundPlayers.Remove(gone); soundsPlaying.Remove(gone); }
    }
    // A sound from a video file (an unlinked video's sound, or a video picked as a sound) plays in the preview
    // from just its sound, copied out once into a small file in the background, so the preview doesn't also
    // decode a picture nobody sees. It joins in once that's ready. The copies are kept for next time, under
    // about 1 GB, the longest unused dropped first.
    private static readonly string SoundCacheRoot = Path.Combine(Path.GetTempPath(), "Flashback-sound-cache");
    private static readonly Dictionary<string, Task<string?>> soundCopies = new(StringComparer.OrdinalIgnoreCase);
    private static string? PreviewSoundPath(string path)
    {
        if (!VideoExtensions.Contains(Path.GetExtension(path).ToLowerInvariant())) return path;
        if (!soundCopies.TryGetValue(path, out var copy) || (copy.IsCompletedSuccessfully && copy.Result is { } done && !File.Exists(done))) soundCopies[path] = copy = Task.Run(() => CopySoundAsync(path));
        // (If its sound can't be copied out, it plays from the video itself.)
        return copy.IsCompletedSuccessfully ? copy.Result ?? path : null;
    }
    private static async Task<string?> CopySoundAsync(string video)
    {
        try
        {
            var info = new FileInfo(video);
            if (!info.Exists) return null;
            string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{info.FullName.ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}")))[..32];
            Directory.CreateDirectory(SoundCacheRoot);
            string output = Path.Combine(SoundCacheRoot, key + ".m4a");
            if (File.Exists(output)) { try { File.SetLastWriteTimeUtc(output, DateTime.UtcNow); } catch { } return output; }
            string work = Path.Combine(SoundCacheRoot, key + "." + Guid.NewGuid().ToString("N")[..8] + ".m4a");
            // Copied as it is, which takes a moment; a sound that doesn't fit the file is made AAC instead.
            foreach (var codec in new[] { new[] { "-c:a", "copy" }, new[] { "-c:a", "aac", "-b:a", "192k" } })
            {
                var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                foreach (var a in new[] { "-y", "-v", "error", "-i", video, "-map", "0:a:0", "-vn", "-sn", "-dn" }.Concat(codec).Append(work)) start.ArgumentList.Add(a);
                using var p = Process.Start(start)!;
                await p.StandardError.ReadToEndAsync(); await p.WaitForExitAsync();
                if (p.ExitCode == 0 && File.Exists(work) && new FileInfo(work).Length > 0) { File.Move(work, output, true); TrimSoundCache(); return output; }
            }
            try { File.Delete(work); } catch { }
        }
        catch { }
        return null;
    }
    private static void TrimSoundCache()
    {
        try
        {
            var files = new DirectoryInfo(SoundCacheRoot).GetFiles().OrderBy(f => f.LastWriteTimeUtc).ToList();
            long total = files.Sum(f => f.Length);
            foreach (var f in files)
            {
                if (total <= 1L << 30) break;
                if (DateTime.UtcNow - f.LastWriteTimeUtc < TimeSpan.FromMinutes(10)) continue;
                try { f.Delete(); total -= f.Length; } catch { }
            }
        }
        catch { }
    }
    private void StopSounds() { foreach (var (key, player) in soundPlayers) player.Pause(); soundsPlaying.Clear(); }
    private void CloseSounds() { foreach (var player in soundPlayers.Values) player.Close(); soundPlayers.Clear(); soundsPlaying.Clear(); }
}
