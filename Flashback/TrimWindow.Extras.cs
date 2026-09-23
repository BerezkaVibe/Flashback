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
    // Compress for Discord targets Nitro Basic's 50 MB upload limit.
    private const double DiscordLimitMb = 50;
    // Shown only when the export would not already fit, estimated from the clip's bitrate.
    private void UpdateDiscordButton()
    {
        if (DiscordButton == null) return;
        double selected = sections.Count > 0 ? sections.Sum(s => s.Duration) : Math.Max(0, Timeline.End - Timeline.Start);
        double estimate = media.Duration > 0 ? sourceBytes * selected / media.Duration : 0;
        DiscordButton.Visibility = source.Length > 0 && estimate > DiscordLimitMb * 1_000_000 ? Visibility.Visible : Visibility.Collapsed;
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
        if (names.Length == 0 || !previewEnabled) return;
        string path = source;
        try
        {
            var peaks = await Task.WhenAll(names.Select(n => AudioWaveforms.LoadAsync(path, n.Item2, token)));
            if (token.IsCancellationRequested || path != source) return;
            Timeline.Lanes = names.Select((n, i) => new AudioLane(n.Item1, peaks[i], LaneMuted(i), media.HasSeparateTracks)).ToArray();
        }
        catch (OperationCanceledException) { }
        catch { /* Waveforms are a visual aid; trimming works without them. */ }
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
        if (CurrentCrop() is { } c) StatusLabel.Text = $"Cropping to {c.Width & ~1} × {c.Height & ~1}.";
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

    // One click: an MP4 under 50 MB next to the original, then copied for pasting.
    private async void DiscordExport_Click(object sender, RoutedEventArgs e)
    {
        if (source.Length == 0 || exportCancellation != null) return;
        KeepSection[] ranges;
        try { ranges = ExportRanges(); } catch (ArgumentException ex) { StatusLabel.Text = ex.Message; return; }
        double limit = ((FrameworkElement)sender).Tag is string tag && double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var mb) ? mb : DiscordLimitMb;
        double seconds = ranges.Sum(r => r.Duration);
        double budget = limit * 1_000_000 * .92 * 8 / seconds - 128000;
        if (budget < 400_000) { StatusLabel.Text = $"That's too long to fit under {limit:0} MB. Keep it under about {limit * 1_000_000 * .92 * 8 / 528000:0} seconds."; return; }
        // Plenty of room keeps the recording's own resolution and frame rate; tighter budgets step down.
        var format = budget >= 12_000_000 ? ExportFormat.Mp4 : budget >= 4_500_000 ? ExportFormat.Mp4Hd60 : ExportFormat.Mp4Sd30;
        // Never spend more than ~24 Mbps: beyond that a bigger file looks no better than the recording.
        double target = Math.Min(limit, Math.Ceiling(seconds * (24_000_000 + 128000) / 8 / 1_000_000 / .92));
        ShareExportOptions options;
        try { options = ShareExportOptions.For(format, Math.Max(1, target)) with { Crop = CurrentCrop(), DesktopVolume = DesktopMix.Value / 100, MicrophoneVolume = MicrophoneMix.Value / 100 }; options.Validate(); }
        catch (ArgumentException ex) { StatusLabel.Text = ex.Message; return; }
        string folder = Path.GetDirectoryName(source)!, name = Path.GetFileNameWithoutExtension(source) + " — discord";
        string destination = Path.Combine(folder, name + ".mp4");
        for (int i = 2; File.Exists(destination); i++) destination = Path.Combine(folder, $"{name} {i}.mp4");
        var result = await RunExportAsync(destination, ranges, options, rough: false);
        if (result == null) return;
        try
        {
            Clipboard.SetFileDropList(new StringCollection { result.Path });
            StatusLabel.Text = $"Copied {Path.GetFileName(result.Path)} ({new FileInfo(result.Path).Length / 1_000_000d:0.#} MB). Paste it into Discord with Ctrl+V.";
        }
        catch { StatusLabel.Text = "Saved " + Path.GetFileName(result.Path) + ", but it couldn't be copied. Drag it into Discord instead."; }
    }
    private KeepSection[] ExportRanges() =>
        ClipEditor.Validate(sections.Count > 0 ? sections : new[] { new KeepSection(KeepSection.Parse(StartBox.Text), KeepSection.Parse(EndBox.Text)) }, media.Duration).ToArray();

    // Shared by Export and Export for Discord. Returns null when canceled or failed.
    private async Task<ClipResult?> RunExportAsync(string destination, KeepSection[] ranges, ShareExportOptions options, bool rough)
    {
        Pause(); exportCancellation = new(); exportFinished = new(TaskCreationOptions.RunContinuationsAsynchronously); SetEditingEnabled(false);
        ExportProgress.Value = 0; CancelExportButton.Visibility = Visibility.Visible; UpdateSummary(); StatusLabel.Text = "Exporting your selected sections…";
        try
        {
            var progress = new Progress<double>(p => ExportProgress.Value = p);
            if (!rough) await WaitForCaptureAsync(exportCancellation.Token);
            StatusLabel.Text = "Exporting selected sections…";
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
        Timeline.IsEnabled = ExportMode.IsEnabled = SharePreset.IsEnabled = SizeLimit.IsEnabled = RangeControls.IsEnabled = ListControls.IsEnabled = SectionsList.IsEnabled = TrackMixPanel.IsEnabled = DiscordButton.IsEnabled = enabled;
}
