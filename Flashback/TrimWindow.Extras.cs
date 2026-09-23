using System;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Flashback;

public partial class TrimWindow
{
    private CancellationTokenSource? waveformLoad;
    private double lastCompressMb;
    // Size of the selected part of the original, from the clip's own bitrate.
    private double SelectionMb()
    {
        double selected = sections.Count > 0 ? sections.Sum(s => s.Duration) : Math.Max(0, Timeline.End - Timeline.Start);
        return media.Duration > 0 ? sourceBytes * selected / media.Duration / 1_000_000 : 0;
    }
    private void Compress_Click(object sender, RoutedEventArgs e)
    {
        if (source.Length == 0 || exportCancellation != null) return;
        double original = Math.Max(2, Math.Ceiling(SelectionMb()));
        CompressSlider.Maximum = original;
        CompressSlider.Value = Math.Clamp(lastCompressMb > 0 ? lastCompressMb : Math.Round(original / 2), 1, original);
        UpdateCompressLabels(); CompressPopup.IsOpen = true;
    }
    private void CompressSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (CompressLabel != null && CompressHint != null && Timeline != null) UpdateCompressLabels(); }
    private void UpdateCompressLabels()
    {
        double original = CompressSlider.Maximum, target = CompressSlider.Value;
        CompressLabel.Text = $"{target:0} MB of {original:0} MB ({target / original:P0})";
        double seconds = sections.Count > 0 ? sections.Sum(s => s.Duration) : Math.Max(.1, Timeline.End - Timeline.Start);
        double mbps = (target * 1_000_000 * .92 * 8 / seconds - 128000) / 1_000_000;
        CompressHint.Text = mbps < .4 ? "Too small for this length. Raise the size or shorten the selection."
            : $"About {mbps:0.#} Mbps for video. {(mbps >= 12 ? "Keeps the original resolution." : mbps >= 4.5 ? "Exports at 1080p 60fps." : "Exports at 720p 30fps.")} Discord allows 10 MB free, 50 MB with Nitro Basic and 500 MB with Nitro.";
    }
    private async void CompressGo_Click(object sender, RoutedEventArgs e)
    {
        CompressPopup.IsOpen = false; lastCompressMb = CompressSlider.Value;
        await CompressAsync(CompressSlider.Value);
    }

    private void UpdateAddButton()
    {
        int selected = SectionsList.SelectedIndex;
        AddSectionButton.Content = selected >= 0 ? $"Update section {selected + 1}" : "Add section  " + TrimShortcuts.Display(keys[TrimAction.AddSection]);
        AddSectionButton.ToolTip = selected >= 0 ? "Replace the selected section with the marked range" : "Keep the marked range as a new section";
    }
    private void RemoveChip_Click(object sender, RoutedEventArgs e)
    {
        if (exportCancellation != null || ((FrameworkElement)sender).Tag is not KeepSection section) return;
        Pause(); Snapshot(); sections.Remove(section); SectionsList.SelectedIndex = -1;
        StatusLabel.Text = "Section removed. Undo brings it back.";
    }
    private void SectionPicked(int index) { if (index >= 0 && index < sections.Count) SectionsList.SelectedIndex = index; }

    // Audio lanes: separate recordings show desktop and microphone, others one combined lane.
    private async void LoadLanes()
    {
        waveformLoad?.Cancel(); waveformLoad = new CancellationTokenSource(); var token = waveformLoad.Token;
        TrackMixPanel.Visibility = media.HasSeparateTracks ? Visibility.Visible : Visibility.Collapsed;
        DesktopMix.Value = 100; MicrophoneMix.Value = 100;
        var names = media.HasSeparateTracks ? new[] { ("Desktop", 1), ("Microphone", 2) } : media.HasAudio ? new[] { ("Audio", 0) } : Array.Empty<(string, int)>();
        Timeline.Lanes = names.Select(n => new AudioLane(n.Item1, Array.Empty<float>(), false, media.HasSeparateTracks)).ToArray();
        laneTracks = names.Select(n => n.Item2).ToArray(); waveformsLoaded = false;
        if (Timeline.LanesExpanded) await LoadWaveformsAsync(token);
    }
    private int[] laneTracks = Array.Empty<int>();
    private bool waveformsLoaded;
    // Waveforms decode only once the audio dropdown is opened, and never on the UI thread.
    private async Task LoadWaveformsAsync(CancellationToken token)
    {
        if (waveformsLoaded || laneTracks.Length == 0 || !previewEnabled) return;
        waveformsLoaded = true; string path = source; var tracks = laneTracks;
        try
        {
            var peaks = await Task.Run(() => Task.WhenAll(tracks.Select(t => AudioWaveforms.LoadAsync(path, t, token))), token);
            if (token.IsCancellationRequested || path != source) return;
            Timeline.Lanes = Timeline.Lanes.Select((l, i) => l with { Peaks = peaks[i], Muted = LaneMuted(i) }).ToArray();
        }
        catch (OperationCanceledException) { }
        catch { /* Waveforms are a visual aid; trimming works without them. */ }
    }
    private async void LanesToggleRequested()
    {
        Timeline.LanesExpanded = !Timeline.LanesExpanded;
        if (Timeline.LanesExpanded && waveformLoad != null) await LoadWaveformsAsync(waveformLoad.Token);
    }

    // Cut out: black out the picture or mute one audio lane for a stretch, keeping its time.
    private bool userMuted;
    private void CutTool_Click(object sender, RoutedEventArgs e)
    {
        if (source.Length == 0 || exportCancellation != null) return;
        Timeline.CutMode = !Timeline.CutMode;
        ShowCutTool();
        if (Timeline.CutMode && Timeline.Lanes.Count > 0 && !Timeline.LanesExpanded) LanesToggleRequested();
        StatusLabel.Text = Timeline.CutMode ? "Drag across the video to black it out, or across an audio lane to mute it. Click a cut to restore it." : "Cut out tool off.";
    }
    private void CutAdded(CutRegion cut)
    {
        // Overlapping cuts on the same lane merge into one.
        var same = Timeline.Cuts.Where(c => c.Lane == cut.Lane && c.End >= cut.Start && c.Start <= cut.End).ToList();
        var merged = new CutRegion(cut.Lane, same.Select(c => c.Start).Append(cut.Start).Min(), same.Select(c => c.End).Append(cut.End).Max());
        Timeline.Cuts = Timeline.Cuts.Except(same).Append(merged).OrderBy(c => c.Lane).ThenBy(c => c.Start).ToArray();
        string what = cut.Lane < 0 ? "Video blacked out" : $"{Timeline.Lanes[cut.Lane].Name} muted";
        StatusLabel.Text = $"{what} from {KeepSection.TimeText(merged.Start)} to {KeepSection.TimeText(merged.End)}.";
        ApplyPreviewCuts(); UpdateExportHint();
    }
    private void CutRemoved(CutRegion cut)
    {
        Timeline.Cuts = Timeline.Cuts.Where(c => c != cut).ToArray();
        StatusLabel.Text = "Cut removed."; ApplyPreviewCuts(); UpdateExportHint();
    }
    // The preview shows cuts as they will export: black picture, or silence (all audio).
    private void ApplyPreviewCuts()
    {
        bool black = false, mute = false;
        foreach (var c in Timeline.Cuts)
            if (playhead >= c.Start && playhead < c.End) { if (c.Lane < 0) black = true; else mute = true; }
        CensorOverlay.Visibility = black ? Visibility.Visible : Visibility.Collapsed;
        Player.IsMuted = userMuted || mute;
    }
    private void ResetCuts() { Timeline.Cuts = Array.Empty<CutRegion>(); Timeline.CutMode = false; ShowCutTool(); }
    // Lit in the accent color while the tool is on; otherwise it looks like the other icons.
    private void ShowCutTool()
    {
        if (Timeline.CutMode) { CutToolButton.SetResourceReference(Control.BorderBrushProperty, "Accent"); CutToolButton.SetResourceReference(Control.ForegroundProperty, "Accent"); }
        else { CutToolButton.ClearValue(Control.BorderBrushProperty); CutToolButton.ClearValue(Control.ForegroundProperty); }
    }
    private bool LaneMuted(int lane) => media.HasSeparateTracks && (lane == 0 ? DesktopMix.Value : MicrophoneMix.Value) < .5;
    private void LaneToggled(int lane)
    {
        if (!media.HasSeparateTracks || exportCancellation != null) return;
        var slider = lane == 0 ? DesktopMix : MicrophoneMix;
        slider.Value = slider.Value < .5 ? 100 : 0;
    }
    private void Mix_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DesktopMixLabel == null || MicrophoneMixLabel == null) return;
        DesktopMixLabel.Text = $"{DesktopMix.Value:0}%"; MicrophoneMixLabel.Text = $"{MicrophoneMix.Value:0}%";
        if (Timeline.Lanes.Count == 2) Timeline.Lanes = Timeline.Lanes.Select((l, i) => l with { Muted = LaneMuted(i) }).ToArray();
        UpdateExportHint();
    }

    // Crop edits happen over the preview; the chosen area applies to every export format.
    private void Crop_Click(object sender, RoutedEventArgs e)
    {
        if (source.Length == 0 || exportCancellation != null) return;
        if (media.Width <= 0 || media.Height <= 0) { StatusLabel.Text = "Crop isn't available for this video."; return; }
        Pause();
        CropArea.VideoWidth = media.Width; CropArea.VideoHeight = media.Height;
        CropArea.Crop ??= new Rect(.05, .05, .9, .9);
        CropArea.Editing = true; CropArea.Visibility = CropBar.Visibility = Visibility.Visible;
        StatusLabel.Text = "Drag the corners or edges to crop, or drag inside to move it.";
    }
    private void CropAspect_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is string tag && double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var aspect)) CropArea.Aspect = aspect;
    }
    private void CropReset_Click(object sender, RoutedEventArgs e)
    {
        CropArea.Crop = null; CropArea.Aspect = 0; CropArea.Editing = false;
        CropArea.Visibility = CropBar.Visibility = Visibility.Collapsed; RefreshCropState();
        StatusLabel.Text = "Crop removed.";
    }
    private void CropDone_Click(object sender, RoutedEventArgs e)
    {
        CropArea.Editing = false; CropBar.Visibility = Visibility.Collapsed; RefreshCropState();
        if (CurrentCrop() is { } c) StatusLabel.Text = $"Cropping to {c.Width & ~1} Ã— {c.Height & ~1}.";
    }
    private void RefreshCropState()
    {
        bool cropped = CurrentCrop() != null;
        CropArea.Visibility = cropped || CropArea.Editing ? Visibility.Visible : Visibility.Collapsed;
        CropButton.SetResourceReference(Control.ForegroundProperty, cropped ? "Accent" : "Ink");
        UpdateExportHint();
    }
    private CropRect? CurrentCrop()
    {
        if (CropArea.Crop is not { } r || media.Width <= 0 || media.Height <= 0) return null;
        if (r.Width > .995 && r.Height > .995) return null;
        return new CropRect((int)Math.Round(r.X * media.Width), (int)Math.Round(r.Y * media.Height), (int)Math.Round(r.Width * media.Width), (int)Math.Round(r.Height * media.Height));
    }
    private void ResetCrop() { CropArea.Crop = null; CropArea.Aspect = 0; CropArea.Editing = false; CropArea.Visibility = CropBar.Visibility = Visibility.Collapsed; CropButton.SetResourceReference(Control.ForegroundProperty, "Ink"); }

    // Saves an MP4 at the chosen size next to the original.
    private async Task CompressAsync(double limit)
    {
        if (source.Length == 0 || exportCancellation != null) return;
        KeepSection[] ranges;
        try { ranges = ExportRanges(); } catch (ArgumentException ex) { StatusLabel.Text = ex.Message; return; }
        double seconds = ranges.Sum(r => r.Duration);
        double budget = limit * 1_000_000 * .92 * 8 / seconds - 128000;
        if (budget < 400_000) { StatusLabel.Text = $"That's too long to fit under {limit:0} MB. Keep it under about {limit * 1_000_000 * .92 * 8 / 528000:0} seconds."; return; }
        // Plenty of room keeps the recording's own resolution and frame rate; tighter budgets step down.
        var format = budget >= 12_000_000 ? ExportFormat.Mp4 : budget >= 4_500_000 ? ExportFormat.Mp4Hd60 : ExportFormat.Mp4Sd30;
        // Never spend more than ~24 Mbps: beyond that a bigger file looks no better than the recording.
        double target = Math.Min(limit, Math.Ceiling(seconds * (24_000_000 + 128000) / 8 / 1_000_000 / .92));
        ShareExportOptions options;
        try { options = ShareExportOptions.For(format, Math.Max(1, target)) with { Crop = CurrentCrop(), DesktopVolume = DesktopMix.Value / 100, MicrophoneVolume = MicrophoneMix.Value / 100, Cuts = Timeline.Cuts }; options.Validate(); }
        catch (ArgumentException ex) { StatusLabel.Text = ex.Message; return; }
        string folder = Path.GetDirectoryName(source)!, name = Path.GetFileNameWithoutExtension(source) + $" â€” {limit:0} MB";
        string destination = Path.Combine(folder, name + ".mp4");
        for (int i = 2; File.Exists(destination); i++) destination = Path.Combine(folder, $"{name} {i}.mp4");
        var result = await RunExportAsync(destination, ranges, options, rough: false);
        if (result == null) return;
        StatusLabel.Text = $"Saved {Path.GetFileName(result.Path)} ({new FileInfo(result.Path).Length / 1_000_000d:0.#} MB).";
    }

    // Copy: export with the current options into a small cache and put the file on the
    // clipboard, so a clip can be pasted into Discord or a chat without a save dialog.
    private async void CopyExport_Click(object sender, RoutedEventArgs e)
    {
        if (source.Length == 0 || exportCancellation != null) return;
        ShareExportOptions options; KeepSection[] ranges;
        try { options = ExportOptions(); ranges = ExportRanges(); } catch (ArgumentException ex) { StatusLabel.Text = ex.Message; return; }
        bool rough = ExportMode.SelectedIndex == 1 && !NeedsReencode(options);
        string folder = Path.Combine(Storage.Root, "Copied");
        try { Directory.CreateDirectory(folder); PruneCopies(folder); } catch (Exception ex) { StatusLabel.Text = "Couldn't prepare the copy folder. " + ex.Message; return; }
        string destination = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(source)} — clip {DateTime.Now:HH-mm-ss}{options.Extension}");
        var result = await RunExportAsync(destination, ranges, options, rough);
        if (result == null) return;
        try
        {
            Clipboard.SetFileDropList(new StringCollection { result.Path });
            StatusLabel.Text = $"Copied ({new FileInfo(result.Path).Length / 1_000_000d:0.#} MB). Paste it anywhere with Ctrl+V.";
        }
        catch { StatusLabel.Text = "The clip is ready but couldn't be placed on the clipboard. Try Copy again."; }
    }
    // Keeps the ten newest copies and anything from the last day; older ones are removed.
    private static void PruneCopies(string folder)
    {
        var old = new DirectoryInfo(folder).GetFiles().OrderByDescending(f => f.LastWriteTimeUtc).Skip(10).Where(f => DateTime.UtcNow - f.LastWriteTimeUtc > TimeSpan.FromDays(1));
        foreach (var file in old) try { file.Delete(); } catch { }
    }
    private KeepSection[] ExportRanges() =>
        ClipEditor.Validate(sections.Count > 0 ? sections : new[] { new KeepSection(KeepSection.Parse(StartBox.Text), KeepSection.Parse(EndBox.Text)) }, media.Duration).ToArray();

    // Shared by Export and Compress. Returns null when canceled or failed.
    private async Task<ClipResult?> RunExportAsync(string destination, KeepSection[] ranges, ShareExportOptions options, bool rough)
    {
        Pause(); exportCancellation = new(); exportFinished = new(TaskCreationOptions.RunContinuationsAsynchronously); SetEditingEnabled(false);
        ExportProgress.Value = 0; CancelExportButton.Visibility = Visibility.Visible; UpdateSummary(); StatusLabel.Text = "Exporting your selected sectionsâ€¦";
        try
        {
            var progress = new Progress<double>(p => ExportProgress.Value = p);
            StatusLabel.Text = "Exporting selected sectionsâ€¦";
            var result = rough
                ? await FastClipExport.ExportAsync(source, destination, ranges, progress, exportCancellation.Token)
                : await ExportServices.PreciseAsync(source, destination, ranges, progress, exportCancellation.Token, options: options);
            StatusLabel.Text = "Saved: " + Path.GetFileName(result.Path);
            if (options.HasVideo && !options.IsGif) Exported?.Invoke(result);
            return result;
        }
        catch (OperationCanceledException) { StatusLabel.Text = "Export canceled. The original clip is unchanged."; ExportProgress.Value = 0; return null; }
        catch (Exception ex) { StatusLabel.Text = ex.Message; return null; }
        finally
        {
            exportCancellation.Dispose(); exportCancellation = null; SetEditingEnabled(true);
            CancelExportButton.Visibility = Visibility.Collapsed; UpdateSummary(); exportFinished.TrySetResult(); if (closeAfterCancel) Close();
        }
    }
    private void SetEditingEnabled(bool enabled) =>
        Timeline.IsEnabled = ExportMode.IsEnabled = SharePreset.IsEnabled = SizeLimit.IsEnabled = RangeControls.IsEnabled = ListControls.IsEnabled = SectionsList.IsEnabled = TrackMixPanel.IsEnabled = CompressButton.IsEnabled = CopyButton.IsEnabled = enabled;

    // Preview speed: a log-scale slider from 0.1x to 4x (so 1x sits near the middle) plus quick presets.
    private bool syncingSpeed;
    private long speedClosedAt;
    private void Speed_Click(object sender, RoutedEventArgs e)
    {
        if (System.Diagnostics.Stopwatch.GetElapsedTime(speedClosedAt).TotalMilliseconds < 250) return;
        ShowPreviewRate(); SpeedPopup.IsOpen = true;
    }
    private void SpeedPopup_Closed(object? sender, EventArgs e) => speedClosedAt = System.Diagnostics.Stopwatch.GetTimestamp();
    private void LoadSpeedPresets()
    {
        foreach (double rate in new[] { .25, .5, 1, 2, 4 })
        {
            var preset = new Button { Content = RateText(rate), Tag = rate, Style = (Style)FindResource("TrimButton"), MinHeight = 26, Height = 26, Padding = new Thickness(0), Margin = new Thickness(2, 0, 2, 0), FontSize = 11, Background = System.Windows.Media.Brushes.Transparent };
            preset.Click += (_, _) => SetPreviewRate(rate);
            SpeedPresets.Children.Add(preset);
        }
        ShowPreviewRate();
    }
    private void SpeedSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (syncingSpeed || SpeedButton == null) return;
        // Round to tidy steps: 0.05 below normal speed, 0.1 above it.
        double rate = Math.Exp(e.NewValue);
        rate = rate < 1 ? Math.Round(rate / .05) * .05 : Math.Round(rate, 1);
        SetPreviewRate(Math.Abs(rate - 1) < .03 ? 1 : rate);
    }
    private void ShowPreviewRate()
    {
        if (SpeedButton == null) return;
        string text = RateText(PreviewRate);
        SpeedButton.Content = text; SpeedLabel.Text = text;
        SpeedButton.SetResourceReference(Control.ForegroundProperty, PreviewRate == 1 ? "Ink" : "Accent");
        syncingSpeed = true; SpeedSlider.Value = Math.Log(PreviewRate); syncingSpeed = false;
        foreach (Button preset in SpeedPresets.Children)
            preset.SetResourceReference(Control.BorderBrushProperty, Math.Abs((double)preset.Tag - PreviewRate) < 1e-9 ? "Accent" : "Outline");
    }
    private static string RateText(double rate) => rate.ToString("0.##", CultureInfo.InvariantCulture) + "×";
}
