using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Flashback;

// Zoom: sections of the clip that zoom in on a target, with an editable ramp curve and presets.
// Zoom may overlap cuts and speed parts; zoom sections don't overlap each other.
public partial class TrimWindow
{
    private List<ZoomPreset> savedZoomPresets = new();
    private bool loadingZoom, editingZoomOut;

    private void InitZoom()
    {
        Timeline.ZoomAdded += ZoomAdded; Timeline.ZoomTagClicked += OpenZoom; Timeline.ZoomRemoved += RemoveZoom;
        ZoomGraph.EditStarted += Snapshot; ZoomGraph.Changed += ZoomGraphChanged; ZoomGraph.EditFinished += ZoomEdited;
        ZoomTarget.EditStarted += Snapshot; ZoomTarget.Changed += ZoomTargetChanged; ZoomTarget.EditFinished += ZoomEdited;
        savedZoomPresets = ZoomPreset.LoadSaved();
        LoadZoomPresets(null);
    }
    private void ZoomTool_Click(object sender, RoutedEventArgs e)
    {
        if (source.Length == 0 || exportCancellation != null) return;
        Timeline.ZoomMode = !Timeline.ZoomMode; ShowCutTool(); SlowPopup.IsOpen = false;
        StatusLabel.Text = !Timeline.ZoomMode ? "Zoom tool off."
            : "Click two spots to zoom in on that part; the marker locks onto the playhead and speed-part edges. Click a zoom's tag to edit it · Esc leaves the tool.";
    }
    private ZoomRegion? SelectedZoomRegion => Timeline.SelectedZoom >= 0 && Timeline.SelectedZoom < Timeline.ZoomRegions.Count ? Timeline.ZoomRegions[Timeline.SelectedZoom] : null;
    private ZoomPreset CurrentPreset => (ZoomPresetBox.SelectedItem as ComboBoxItem)?.Tag as ZoomPreset ?? ZoomPreset.BuiltIns[0];

    private void ZoomAdded(double start, double end)
    {
        if (Timeline.ZoomRegions.Any(r => r.End > start && r.Start < end)) { StatusLabel.Text = "That overlaps another zoom. Click its tag to edit it instead."; return; }
        if (OverlapsVideoCut(start, end)) { StatusLabel.Text = "A zoom can't overlap a video cut-out. Pick a stretch outside the red cut-outs."; return; }
        Snapshot();
        var preset = CurrentPreset;
        var region = new ZoomRegion(start, end, .5, .5, preset.MaxZoom, preset.Curve);
        Timeline.ZoomRegions = Timeline.ZoomRegions.Append(region).OrderBy(r => r.Start).ToArray();
        UpdateExportHint();
        OpenZoom(Timeline.ZoomRegions.ToList().IndexOf(region));
        StatusLabel.Text = $"Zoom added from {KeepSection.TimeText(start)} to {KeepSection.TimeText(end)}. Aim it with the box on the video.";
    }
    // Selecting a zoom opens its editor beside the video and shows its target box.
    private void OpenZoom(int index)
    {
        if (index < 0 || index >= Timeline.ZoomRegions.Count || exportCancellation != null) return;
        if (OverlayPanel.Visibility == Visibility.Visible) CloseOverlay();
        Timeline.SelectedZoom = index; editingZoomOut = false; FocusPart(Timeline.ZoomRegions[index]);
        var r = Timeline.ZoomRegions[index];
        ZoomPanel.Visibility = ZoomTarget.Visibility = Visibility.Visible;
        ZoomTarget.VideoWidth = media.Width > 0 ? media.Width : 1920; ZoomTarget.VideoHeight = media.Height > 0 ? media.Height : 1080;
        LoadZoomUi();
        // Show the moment the zoom is fully in, so aiming the box is easy.
        SeekTo(Math.Min(r.End - .01, r.Start + Math.Min(r.In.Length, (r.End - r.Start) / 2)));
    }
    private void CloseZoom()
    {
        Timeline.SelectedZoom = -1;
        if (focusedKind == PartKind.Zoom) FocusPart(null);
        ZoomPanel.Visibility = ZoomTarget.Visibility = Visibility.Collapsed;
        ApplyZoomPreview();
    }
    private void ZoomDone_Click(object sender, RoutedEventArgs e) => CloseZoom();
    private void LoadZoomUi()
    {
        if (SelectedZoomRegion is not { } r) { CloseZoom(); return; }
        loadingZoom = true;
        editingZoomOut &= r.ZoomOut && r.Out != null;
        ZoomGraph.MaxZoom = r.MaxZoom; ZoomGraph.Curve = editingZoomOut ? r.OutCurve : r.In;
        ZoomTarget.Set(r.X, r.Y, r.MaxZoom);
        ZoomOutCheck.IsChecked = r.ZoomOut; ZoomOutSameCheck.IsChecked = r.Out == null;
        ZoomOutSameCheck.Visibility = r.ZoomOut ? Visibility.Visible : Visibility.Collapsed;
        ZoomCurveTabs.Visibility = r.ZoomOut && r.Out != null ? Visibility.Visible : Visibility.Collapsed;
        ZoomInTab.SetResourceReference(Control.BorderBrushProperty, editingZoomOut ? "Outline" : "Accent");
        ZoomOutTab.SetResourceReference(Control.BorderBrushProperty, editingZoomOut ? "Accent" : "Outline");
        ZoomTitle.Text = $"{KeepSection.TimeText(r.Start)} – {KeepSection.TimeText(r.End)}";
        ShowZoomReadout(r);
        // Show which preset this matches, or Custom after hand edits.
        var match = ZoomPresetBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is ZoomPreset p && Matches(p, r));
        ZoomPresetBox.SelectedItem = match ?? ZoomPresetBox.Items[0];
        ZoomDeletePreset.IsEnabled = CurrentPreset is { BuiltIn: false } && match != null;
        loadingZoom = false;
    }
    private static bool Matches(ZoomPreset p, ZoomRegion r) =>
        Math.Abs(p.MaxZoom - r.MaxZoom) < .01 && p.Curve.Points.Count == r.In.Points.Count && p.Curve.Points.Zip(r.In.Points).All(z => Math.Abs(z.First.T - z.Second.T) < .005 && Math.Abs(z.First.F - z.Second.F) < .005);
    private void ShowZoomReadout(ZoomRegion r) =>
        ZoomReadout.Text = $"Max {r.MaxZoom:0.0}× · reaches it in {r.In.Length:0.##} s" + (r.ZoomOut ? $" · zooms out over {r.OutCurve.Length:0.##} s" : " · then cuts back") + (r.MaxZoom > 3 ? " · softer above 3×" : "");
    private void UpdateSelectedZoom(Func<ZoomRegion, ZoomRegion> change)
    {
        int i = Timeline.SelectedZoom;
        if (i < 0 || i >= Timeline.ZoomRegions.Count) return;
        Timeline.ZoomRegions = Timeline.ZoomRegions.Select((r, n) => n == i ? change(r) : r).ToArray();
        ShowZoomReadout(Timeline.ZoomRegions[i]); ApplyZoomPreview(); ProjectChanged();
    }
    private void ZoomGraphChanged()
    {
        if (loadingZoom) return;
        UpdateSelectedZoom(r => (editingZoomOut ? r with { Out = ZoomGraph.Curve } : r with { In = ZoomGraph.Curve }) with { MaxZoom = ZoomGraph.MaxZoom });
        ZoomTarget.Set(ZoomTarget.X, ZoomTarget.Y, ZoomGraph.MaxZoom);
    }
    private void ZoomTargetChanged()
    {
        if (loadingZoom) return;
        UpdateSelectedZoom(r => r with { X = ZoomTarget.X, Y = ZoomTarget.Y, MaxZoom = ZoomTarget.MaxZoom });
        ZoomGraph.MaxZoom = ZoomTarget.MaxZoom;
    }
    // After a hand edit, the preset shows Custom (or the preset it happens to match).
    private void ZoomEdited() { UpdateExportHint(); LoadZoomUi(); }

    private void LoadZoomPresets(string? select)
    {
        loadingZoom = true;
        ZoomPresetBox.Items.Clear();
        ZoomPresetBox.Items.Add(new ComboBoxItem { Content = "Custom", IsEnabled = false });
        foreach (var p in ZoomPreset.BuiltIns.Concat(savedZoomPresets))
            ZoomPresetBox.Items.Add(new ComboBoxItem { Content = p.BuiltIn ? p.Name : p.Name + "  ·  saved", Tag = p });
        ZoomPresetBox.SelectedItem = ZoomPresetBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is ZoomPreset p && p.Name == select) ?? ZoomPresetBox.Items[1];
        loadingZoom = false;
    }
    private void ZoomPreset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loadingZoom || ZoomPresetBox.SelectedItem is not ComboBoxItem { Tag: ZoomPreset preset }) return;
        ZoomDeletePreset.IsEnabled = !preset.BuiltIn;
        if (SelectedZoomRegion is not { } r) return;
        Snapshot();
        UpdateSelectedZoom(z => z with { MaxZoom = preset.MaxZoom, In = preset.Curve, Out = z.Out == null ? null : preset.Curve });
        LoadZoomUi(); UpdateExportHint();
    }
    private void ZoomSavePreset_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedZoomRegion is not { } r) return;
        var name = AskName("Save zoom preset", "Preset name", "My zoom");
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        if (ZoomPreset.BuiltIns.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) { StatusLabel.Text = "That name belongs to a built-in preset. Pick another."; return; }
        savedZoomPresets.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        savedZoomPresets.Add(new ZoomPreset(name, r.MaxZoom, r.In.Points.ToArray()));
        try { ZoomPreset.SaveAll(savedZoomPresets); StatusLabel.Text = $"Saved the \"{name}\" zoom preset."; }
        catch (Exception ex) { StatusLabel.Text = "Could not save the preset: " + ex.Message; }
        LoadZoomPresets(name); LoadZoomUi();
    }
    private void ZoomDeletePreset_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentPreset is not { BuiltIn: false } preset) return;
        savedZoomPresets.Remove(preset);
        try { ZoomPreset.SaveAll(savedZoomPresets); StatusLabel.Text = $"Deleted the \"{preset.Name}\" preset."; }
        catch (Exception ex) { StatusLabel.Text = "Could not delete the preset: " + ex.Message; }
        LoadZoomPresets(null); LoadZoomUi();
    }
    private string? AskName(string title, string prompt, string suggestion)
    {
        var field = new TextBox { Text = suggestion, Margin = new Thickness(0, 10, 0, 12) };
        var save = new Button { Content = "Save", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 80 };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = prompt }); panel.Children.Add(field); panel.Children.Add(save);
        var dialog = new Window { Title = title + " · Flashback", Owner = this, Width = 360, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
        WindowTheme.Attach(dialog);
        save.Click += (_, _) => dialog.DialogResult = true;
        dialog.PreviewKeyDown += (_, k) => { if (k.Key == Key.Escape) { dialog.Close(); k.Handled = true; } };
        dialog.Loaded += (_, _) => { field.Focus(); field.SelectAll(); };
        return dialog.ShowDialog() == true ? field.Text : null;
    }
    private void ZoomOutToggle_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedZoomRegion is null) return;
        Snapshot();
        bool zoomOut = ZoomOutCheck.IsChecked == true, same = ZoomOutSameCheck.IsChecked != false;
        // Unticking "Same curve" starts the zoom-out curve as a copy of the zoom-in, ready to reshape.
        UpdateSelectedZoom(r => r with { ZoomOut = zoomOut, Out = zoomOut && !same ? r.Out ?? r.In : null });
        editingZoomOut = zoomOut && !same && ReferenceEquals(sender, ZoomOutSameCheck);
        LoadZoomUi(); UpdateExportHint();
    }
    private void ZoomTab_Click(object sender, RoutedEventArgs e) { editingZoomOut = ReferenceEquals(sender, ZoomOutTab); LoadZoomUi(); }
    private void ZoomRemove_Click(object sender, RoutedEventArgs e) => RemoveZoom(Timeline.SelectedZoom);
    private void RemoveZoom(int index)
    {
        if (index < 0 || index >= Timeline.ZoomRegions.Count) return;
        Snapshot();
        Timeline.ZoomRegions = Timeline.ZoomRegions.Where((_, n) => n != index).ToArray();
        CloseZoom(); UpdateExportHint(); StatusLabel.Text = "Zoom removed.";
    }
    private void ResetZoom() { Timeline.ZoomRegions = Array.Empty<ZoomRegion>(); Timeline.ZoomMode = false; CloseZoom(); }

    // Live preview: the video zooms as it plays. While a zoom is being aimed (paused, editor open),
    // the full frame shows with the target box instead.
    private void ApplyZoomPreview()
    {
        if (Player == null) return;
        var zoom = Timeline.ZoomRegions.FirstOrDefault(r => playhead >= r.Start && playhead < r.End);
        bool aiming = ZoomPanel.Visibility == Visibility.Visible && !playing;
        double z = zoom == null || aiming ? 1 : zoom.ZoomAt(playhead);
        if (SelectedZoomRegion is { } selected)
            ZoomGraph.Marker = playhead < selected.Start || playhead >= selected.End ? double.NaN
                : editingZoomOut ? selected.End - playhead : playhead - selected.Start;
        if (z <= 1.0001 || media.Width <= 0) { if (Player.RenderTransform != Transform.Identity) { Player.RenderTransform = Transform.Identity; Player.Clip = null; } OverlayView.SetZoom(Matrix.Identity, null); return; }
        double scale = Math.Min(Player.ActualWidth / media.Width, Player.ActualHeight / media.Height);
        double w = media.Width * scale, h = media.Height * scale, vx = (Player.ActualWidth - w) / 2, vy = (Player.ActualHeight - h) / 2;
        var (left, top) = zoom!.ViewAt(z);
        double x0 = vx + left * w, y0 = vy + top * h;
        Player.RenderTransform = new MatrixTransform(z, 0, 0, z, vx - x0 * z, vy - y0 * z);
        // Clip to the part being shown (in the player's own coordinates), so the zoomed picture stays
        // inside the video's frame instead of spilling over the letterbox bars.
        Player.Clip = new RectangleGeometry(new Rect(x0, y0, w / z, h / z));
        // Text and pictures stuck to the video zoom along with it.
        OverlayView.SetZoom(((MatrixTransform)Player.RenderTransform).Matrix, new Rect(x0, y0, w / z, h / z));
    }
}
