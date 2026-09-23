using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Flashback;

public partial class MainWindow
{
    private void Navigate_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = int.Parse((string)((Button)sender).Tag);
    private void FocusSearch_Click(object sender, RoutedEventArgs e) => ClipSearch.Focus();
    private void UpdateNavigation()
    {
        if (PageTitle == null || MainTabs == null) return;
        int selected = MainTabs.SelectedIndex;
        PageTitle.Text = selected switch { 1 => "Settings", 2 => "Clips", _ => "Clips" };
        PageSubtitle.Text = selected switch { 1 => "Make it yours.", 2 => "Find it. Trim it. Keep it.", _ => "" };
        foreach (var button in new[] { CaptureNav, ClipsNav, SettingsNav })
        {
            bool current = (string)button.Tag == selected.ToString();
            button.Background = current ? (Brush)FindResource("ControlSurface") : Brushes.Transparent;
            button.SetResourceReference(Control.ForegroundProperty,current ? "Accent" : "Muted");
            AutomationProperties.SetItemStatus(button, current ? "Selected" : "");
        }
    }
    private static void SetActionName(Button button, string name)
    { button.ToolTip = name; AutomationProperties.SetName(button, name); }
    private void UpdateQuickAudio()
    {
        UpdateAudioButton(SpeakerButton, settings.DesktopAudio, settings.DesktopMuted, "Speaker", "\uE767", "\uE74F");
        UpdateAudioButton(MicrophoneButton, settings.MicrophoneAudio, settings.MicrophoneMuted, "Microphone", "\uE720", "\uF781");
    }
    private void UpdateAudioButton(Button button, bool enabled, bool muted, string source, string on, string off)
    {
        button.Content = enabled && !muted ? on : off;
        button.Foreground = (Brush)FindResource(enabled && !muted ? "Accent" : "Muted");
        button.IsEnabled = !busy;
        SetActionName(button, !enabled ? $"Set up {source.ToLowerInvariant()} recording" : muted ? $"Unmute {source.ToLowerInvariant()} recording" : $"Mute {source.ToLowerInvariant()} recording");
        AutomationProperties.SetItemStatus(button, !enabled ? "Not enabled" : muted ? "Muted" : "On");
    }
    private void QuickAudio_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        bool mic = ReferenceEquals(sender, MicrophoneButton);
        if (!(mic ? settings.MicrophoneAudio : settings.DesktopAudio))
        {
            MainTabs.SelectedIndex = 1; AudioSettings.IsExpanded = true; UpdateLayout();
            (mic ? MicrophoneCheck : AudioCheck).BringIntoView();
            (mic ? MicrophoneCheck : AudioCheck).Focus();
            Tell("Enable this audio source and apply settings. The top icon then mutes or unmutes it without clearing your buffer.");
            return;
        }
        try
        {
            var next = settings.Copy();
            if (mic) next.MicrophoneMuted = !next.MicrophoneMuted; else next.DesktopMuted = !next.DesktopMuted;
            Storage.Save(next);
            settings = next;
            recorder.SetAudioMuted(next.DesktopMuted, next.MicrophoneMuted);
            bool muted = mic ? next.MicrophoneMuted : next.DesktopMuted;
            Tell((mic ? "Microphone" : "Speaker") + (muted ? " muted in new footage." : " unmuted in new footage.") + " Existing footage is unchanged.");
            Refresh();
        }
        catch (Exception ex) { Tell(ex.Message, true); }
    }
}




