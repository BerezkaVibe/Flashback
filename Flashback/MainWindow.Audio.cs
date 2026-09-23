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
    }
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
