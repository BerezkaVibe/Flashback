using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Flashback;

// Freeze frames: the picture at one moment holds for a while, then the clip plays on from that same
// moment. They show as a slit on the video track; the tag opens their settings and the slit drags.
public partial class TrimWindow
{
    private void InitFreezes()
    {
        Timeline.FreezeTagClicked += OpenFreezePopup;
        Timeline.FreezeEditStarted += Snapshot;
        Timeline.FreezeMoved += (i, at) =>
        {
            if (i < 0 || i >= Timeline.Freezes.Count) return;
            // Two freezes can't share a moment.
            if (Timeline.Freezes.Where((_, n) => n != i).Any(f => Math.Abs(f.At - at) < FrameStep)) return;
            var moved = Timeline.Freezes[i] with { At = at };
            Timeline.Freezes = Timeline.Freezes.Select((f, n) => n == i ? moved : f).ToArray();
            FocusPart(moved); StatusLabel.Text = $"Freeze frame at {KeepSection.TimeText(at)}.";
        };
        Timeline.FreezeEditFinished += () =>
        {
            Timeline.Freezes = Timeline.Freezes.OrderBy(f => f.At).ToArray();
            UpdateExportHint(); UpdateSummary(); ProjectChanged();
        };
    }
    private double FrameStep => 1 / Math.Max(1, media.FrameRate);
    private void FreezeHere_Click(object sender, RoutedEventArgs e) => AddFreeze(playhead, 2);
    // Adds a freeze at a moment (or, if one is already there, opens it).
    private void AddFreeze(double at, double seconds, bool snapshot = true)
    {
        if (source.Length == 0 || exportCancellation != null) return;
        at = Math.Clamp(at, 0, Math.Max(0, media.Duration - FrameStep));
        int existing = Timeline.Freezes.ToList().FindIndex(f => Math.Abs(f.At - at) < FrameStep);
        if (existing >= 0) { OpenFreezePopup(existing); return; }
        if (snapshot) Snapshot();
        var freeze = new FreezeFrame(at, Math.Clamp(seconds, FreezeFrame.MinSeconds, FreezeFrame.MaxSeconds));
        Timeline.Freezes = Timeline.Freezes.Append(freeze).OrderBy(f => f.At).ToArray();
        FocusPart(freeze); UpdateExportHint(); UpdateSummary(); ProjectChanged();
        StatusLabel.Text = $"Freeze frame at {KeepSection.TimeText(at)}: the picture holds for {freeze.Seconds:0.#} s, then plays on. Click its tag to change how long.";
    }
    private void RemoveFreeze(int index)
    {
        if (index < 0 || index >= Timeline.Freezes.Count) return;
        Snapshot();
        Timeline.Freezes = Timeline.Freezes.Where((_, n) => n != index).ToArray();
        FreezePopup.IsOpen = false; UpdateExportHint(); UpdateSummary();
        StatusLabel.Text = "Freeze frame removed. Undo brings it back.";
    }
    private void OpenFreezePopup(int index)
    {
        if (index < 0 || index >= Timeline.Freezes.Count || exportCancellation != null) return;
        var freeze = Timeline.Freezes[index];
        FocusPart(freeze);
        FreezeControls.Children.Clear();
        var title = new DockPanel { Margin = new Thickness(2, 0, 2, 8) };
        var at = new TextBlock { Text = "at " + KeepSection.TimeText(freeze.At), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        at.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); DockPanel.SetDock(at, Dock.Right);
        title.Children.Add(at); title.Children.Add(new TextBlock { Text = "Freeze frame", FontSize = 12, FontWeight = FontWeights.SemiBold });
        FreezeControls.Children.Add(title);
        var label = new TextBlock { FontSize = 12, Margin = new Thickness(2, 0, 2, 4) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        var slider = new Slider { Minimum = FreezeFrame.MinSeconds, Maximum = FreezeFrame.MaxSeconds, Value = freeze.Seconds, SmallChange = .1, LargeChange = 1, IsMoveToPointEnabled = true, TickFrequency = .1, IsSnapToTickEnabled = true };
        System.Windows.Automation.AutomationProperties.SetName(slider, "How long the freeze holds");
        void Show() => label.Text = $"Holds for {slider.Value.ToString("0.#", CultureInfo.InvariantCulture)} s, then plays on";
        Show();
        bool snapped = false;
        slider.ValueChanged += (_, _) =>
        {
            int i = Timeline.Freezes.ToList().FindIndex(f => Math.Abs(f.At - freeze.At) < 1e-9);
            if (i < 0) return;
            if (!snapped) { Snapshot(); snapped = true; }
            var next = Timeline.Freezes[i] with { Seconds = Math.Round(slider.Value, 1) };
            Timeline.Freezes = Timeline.Freezes.Select((f, n) => n == i ? next : f).ToArray();
            FocusPart(next); Show(); UpdateExportHint(); UpdateSummary(); ProjectChanged();
        };
        FreezeControls.Children.Add(label); FreezeControls.Children.Add(slider);
        var quick = new UniformGrid { Rows = 1, Margin = new Thickness(0, 6, 0, 0) };
        foreach (double s in new[] { .5, 1, 2, 3, 5 })
        {
            var pick = new Button { Content = s.ToString("0.#", CultureInfo.InvariantCulture) + " s", MinHeight = 26, Height = 26, Margin = new Thickness(2, 0, 2, 0), FontSize = 11, Background = System.Windows.Media.Brushes.Transparent };
            pick.SetResourceReference(StyleProperty, "TrimButton");
            pick.Click += (_, _) => slider.Value = s;
            quick.Children.Add(pick);
        }
        FreezeControls.Children.Add(quick);
        var actions = new UniformGrid { Rows = 1, Margin = new Thickness(0, 8, 0, 0) };
        var move = new Button { Content = "Move to playhead", MinHeight = 26, Height = 26, FontSize = 11, Margin = new Thickness(2, 0, 2, 0), ToolTip = "Freeze the frame at the playhead instead (or drag the slit on the timeline)" };
        move.SetResourceReference(StyleProperty, "TrimButton");
        move.Click += (_, _) =>
        {
            int i = Timeline.Freezes.ToList().FindIndex(f => Math.Abs(f.At - freeze.At) < 1e-9);
            if (i < 0 || Timeline.Freezes.Where((_, n) => n != i).Any(f => Math.Abs(f.At - playhead) < FrameStep)) return;
            Snapshot();
            var moved = Timeline.Freezes[i] with { At = playhead };
            Timeline.Freezes = Timeline.Freezes.Select((f, n) => n == i ? moved : f).OrderBy(f => f.At).ToArray();
            FreezePopup.IsOpen = false; FocusPart(moved); UpdateExportHint(); UpdateSummary();
            StatusLabel.Text = $"Freeze frame moved to {KeepSection.TimeText(playhead)}.";
        };
        var remove = new Button { Content = "Remove", MinHeight = 26, Height = 26, FontSize = 11, Margin = new Thickness(2, 0, 2, 0), Background = System.Windows.Media.Brushes.Transparent, BorderBrush = System.Windows.Media.Brushes.Transparent };
        remove.SetResourceReference(StyleProperty, "TrimButton");
        remove.Click += (_, _) => { int i = Timeline.Freezes.ToList().FindIndex(f => Math.Abs(f.At - freeze.At) < 1e-9); RemoveFreeze(i); };
        actions.Children.Add(move); actions.Children.Add(remove);
        FreezeControls.Children.Add(actions);
        var hint = new TextBlock { Text = "Nothing is skipped: the export gets this much extra time, then carries on from the same frame. The hold is silent; music keeps playing.", FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 8, 2, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        FreezeControls.Children.Add(hint);
        FreezePopup.IsOpen = true;
        StatusLabel.Text = "Freeze frame selected: set how long it holds, or drag the slit on the timeline. Delete removes it.";
    }
}
