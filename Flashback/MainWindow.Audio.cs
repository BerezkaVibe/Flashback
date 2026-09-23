using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Flashback;

public partial class MainWindow
{
    private record BitrateChoice(int Value, string Label);
    private void LoadAudioOptions()
    {
        AudioBitrateBox.DisplayMemberPath = "Label"; AudioBitrateBox.SelectedValuePath = "Value";
        AudioBitrateBox.ItemsSource = Settings.AudioBitrates.Select(b => new BitrateChoice(b, $"{b} kbps{(b == 160 ? " · default" : b >= 256 ? " · high" : "")}")).ToList();
        AudioBitrateBox.SelectedValue = settings.AudioBitrate;
        DesktopVolumeSlider.Value = settings.DesktopVolume; MicrophoneVolumeSlider.Value = settings.MicrophoneVolume;
        LoadAppMix();
    }
    private readonly System.Collections.Generic.Dictionary<string, Slider> appMixSliders = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Generic.Dictionary<string, ImageSource?> appIcons = new(StringComparer.OrdinalIgnoreCase);
    private bool appMixOpen;
    // One compact row per app playing sound now, plus any app given a level before.
    // The list stays folded under a single line until opened.
    private void LoadAppMix()
    {
        AppMixRows.Children.Clear(); appMixSliders.Clear();
        if (!AppMixSource.Supported) { AppMixToggle.Visibility = Visibility.Collapsed; AppMixNote.Text = "Per-app levels need Windows 11 or a recent Windows 10 build."; AppMixNote.Visibility = Visibility.Visible; return; }
        var running = AppMixSource.ActiveAppPaths(AudioDeviceBox.SelectedValue as string ?? settings.AudioDeviceId);
        var apps = running.Keys.Concat(settings.AppVolumes.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(a => a, StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps)
        {
            int level = settings.AppVolumes.TryGetValue(app.ToLowerInvariant(), out var saved) ? saved : 100;
            var label = new TextBlock { Width = 36, TextAlignment = TextAlignment.Right, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Text = level + "%" };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            var slider = new Slider { Minimum = 0, Maximum = 200, TickFrequency = 5, IsSnapToTickEnabled = true, SmallChange = 5, LargeChange = 25, IsMoveToPointEnabled = true, Height = 24, Value = level };
            AutomationProperties.SetName(slider, app + " recording level");
            slider.ValueChanged += (_, _) => { label.Text = $"{slider.Value:0}%"; UpdateAppMixToggle(); };
            var icon = AppIcon(app, running.TryGetValue(app, out var path) ? path : null);
            var name = new TextBlock { Text = char.ToUpperInvariant(app[0]) + app[1..], Width = 92, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = running.ContainsKey(app) ? app : app + " (not running)" };
            var row = new DockPanel { Height = 26 };
            DockPanel.SetDock(icon, Dock.Left); DockPanel.SetDock(label, Dock.Right); DockPanel.SetDock(name, Dock.Left);
            row.Children.Add(icon); row.Children.Add(label); row.Children.Add(name); row.Children.Add(slider);
            AppMixRows.Children.Add(row); appMixSliders[app] = slider;
        }
        UpdateAppMixToggle();
    }
    private FrameworkElement AppIcon(string app, string? path)
    {
        if (path != null && !appIcons.ContainsKey(path))
        {
            ImageSource? image = null;
            try
            {
                using var extracted = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (extracted != null)
                {
                    image = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(extracted.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromWidthAndHeight(16, 16));
                    image.Freeze();
                }
            }
            catch { }
            appIcons[path] = image;
        }
        if (path != null && appIcons[path] is { } source) return new Image { Source = source, Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        // Apps that are not running, or hide their path, get a plain speaker glyph.
        var glyph = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13, Width = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return glyph;
    }
    private void UpdateAppMixToggle()
    {
        int count = appMixSliders.Count, adjusted = appMixSliders.Values.Count(s => (int)Math.Round(s.Value) != 100);
        AppMixToggle.Content = (appMixOpen ? "▾ " : "▸ ") + (count == 0 ? "Per-app levels · no apps playing sound" : $"Per-app levels · {count} app{(count == 1 ? "" : "s")}" + (adjusted > 0 ? $" · {adjusted} adjusted" : ""));
        AppMixRows.Visibility = AppMixNote.Visibility = appMixOpen ? Visibility.Visible : Visibility.Collapsed;
    }
    private void AppMixToggle_Click(object sender, RoutedEventArgs e) { appMixOpen = !appMixOpen; UpdateAppMixToggle(); }
    internal void OpenAppMixForRender() { appMixOpen = true; UpdateAppMixToggle(); }
    // Only apps moved away from 100% are saved.
    private System.Collections.Generic.Dictionary<string, int> ReadAppMix() =>
        appMixSliders.Where(p => (int)Math.Round(p.Value.Value) != 100).ToDictionary(p => p.Key.ToLowerInvariant(), p => (int)Math.Round(p.Value.Value));
    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DesktopVolumeLabel == null || MicrophoneVolumeLabel == null) return;
        DesktopVolumeLabel.Text = $"{DesktopVolumeSlider.Value:0}%"; MicrophoneVolumeLabel.Text = $"{MicrophoneVolumeSlider.Value:0}%";
    }
    private void UpdateAudioLocks()
    {
        UpdateLockButton(SpeakerLockButton, settings.DesktopAudio, settings.DesktopLocked, "speaker");
        UpdateLockButton(MicrophoneLockButton, settings.MicrophoneAudio, settings.MicrophoneLocked, "microphone");
    }
    private void UpdateLockButton(Button button, bool enabled, bool locked, string source)
    {
        button.Content = locked ? "" : "";
        button.Foreground = (Brush)FindResource(locked ? "Accent" : "Muted");
        button.IsEnabled = !busy && enabled;
        SetActionName(button, locked ? $"Unlock {source} device" : $"Lock {source} device");
        AutomationProperties.SetItemStatus(button, locked ? "Locked" : "Unlocked");
    }
    // Locking pins the device in use right now and holds it through disconnects.
    private void AudioLock_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        bool mic = ReferenceEquals(sender, MicrophoneLockButton);
        try
        {
            var next = settings.Copy();
            bool locking = !(mic ? next.MicrophoneLocked : next.DesktopLocked);
            string current = mic ? next.MicrophoneDeviceId : next.AudioDeviceId;
            if (locking && string.IsNullOrEmpty(current))
            {
                current = recorder.LiveDeviceId(mic) ?? AudioLoopback.DefaultDeviceId(mic) ?? throw new InvalidOperationException("No audio device is available to lock.");
                if (mic) next.MicrophoneDeviceId = current; else next.AudioDeviceId = current;
            }
            if (mic) next.MicrophoneLocked = locking; else next.DesktopLocked = locking;
            Storage.Save(next);
            settings = next;
            recorder.SetAudioLive(next);
            if (mic) LoadMicrophones(next.MicrophoneDeviceId); else LoadAudioDevices(next.AudioDeviceId);
            var name = ((mic ? MicrophoneDeviceBox : AudioDeviceBox).SelectedItem as PlaybackDevice)?.Name ?? "this device";
            Tell(locking ? $"{(mic ? "Microphone" : "Speaker")} locked to {name}. It stays in use even if Windows switches or it disconnects."
                         : $"{(mic ? "Microphone" : "Speaker")} unlocked. It still uses {name}; choose Follow Windows default in Settings > Audio to switch with Windows.");
            Refresh();
        }
        catch (Exception ex) { Tell(ex.Message, true); }
    }
}
