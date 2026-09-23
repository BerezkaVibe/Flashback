using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Flashback;
internal static class UiDiagnostics
{
    internal static async Task RunAsync()
    {
        Directory.CreateDirectory(Storage.Root);
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception(message);
            File.AppendAllText(Path.Combine(Storage.Root, "ui-results.txt"), "PASS " + message + "\n");
        }
        Storage.Save(new Settings { MicrophoneAudio = true, OutputFolder = Path.Combine(Storage.Root, "clips") });
        var window = new MainWindow(true);
        var content = (FrameworkElement)window.Content; window.Content = null;
        var canvas = new Border { Child = content, Background = (Brush)new BrushConverter().ConvertFromString("#111419")! };
        window.Content=canvas; // Keep resource invalidation connected to the app's window tree.
        System.Windows.Documents.TextElement.SetFontFamily(canvas, new FontFamily("Segoe UI"));
        System.Windows.Documents.TextElement.SetFontSize(canvas, 14);
        System.Windows.Documents.TextElement.SetForeground(canvas, (Brush)Application.Current.FindResource("Ink"));
        void Layout(int width = 960, int height = 720)
        { canvas.Width = width; canvas.Height = height; canvas.Measure(new Size(width, height)); canvas.Arrange(new Rect(0, 0, width, height)); canvas.UpdateLayout(); }
        void Render(string name, int width = 960, int height = 720)
        {
            Layout(width, height);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(canvas);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(Storage.Root, name)); png.Save(file);
        }
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Layout();
        Color ResourceColor(string key)=>((SolidColorBrush)Application.Current.FindResource(key)).Color;
        bool Hits(FrameworkElement element,DependencyObject target,Point point)
        {
            // The render-only tree is detached from a live HWND; test template geometry.
            var hit=VisualTreeHelper.HitTest(element,point)?.VisualHit;
            while(hit!=null) {if(hit==target)return true;hit=VisualTreeHelper.GetParent(hit);}return false;
        }
        window.SetRecordingAppearance(true);
        Check(((SolidColorBrush)window.RecordDot.Fill).Color==ResourceColor("RecordingLight") && ((SolidColorBrush)window.ToggleButton.BorderBrush).Color==ResourceColor("RecordingLight"),"Active recording shows a red light and outline");
        window.SetRecordingAppearance(false);
        Check(((SolidColorBrush)window.RecordDot.Fill).Color==ResourceColor("IdleLight"),"Stopped recording shows a grey light");
        Check(window.MainTabs.SelectedIndex == 0 && window.StatusTitle.ActualHeight > 0, "Capture content is visible on first launch");
        Check(window.LibraryTab.IsSelected && window.HotkeyLabel.TranslatePoint(new Point(), canvas).Y < window.MainTabs.TranslatePoint(new Point(), canvas).Y, "Home contains clips with recording status and shortcut in the header");
        Check(((SolidColorBrush)window.StatusTitle.Foreground).Color.R > 140, "Capture text inherits the readable dark theme");
        Render("capture.png"); Render("capture-small.png", 744, 601);
        Check(window.ToggleButton.TranslatePoint(new Point(), canvas).X + window.ToggleButton.ActualWidth <= canvas.ActualWidth, "Recording controls stay inside the minimum window width");
        Click(window.SettingsNav); Layout();
        Check(window.MainTabs.SelectedIndex == 1 && window.PageTitle.Text == "Settings" && window.RecordingSettings.IsSelected && window.DisplayBox.ActualHeight > 0, "Gear opens settings on the video tab");
        window.RecordingSettings.IsSelected = true; Layout();
        Check(window.DisplayBox.ActualHeight > 0 && window.EncoderBox.ActualHeight > 0, "Display and hardware encoder choices remain accessible in settings");
        Check(window.LengthSlider.Value==60 && window.LengthSlider.TickFrequency==5 && window.LengthSlider.IsSnapToTickEnabled,"Replay slider shows the saved duration and snaps to five seconds");
        var header=window.RecordingSettings;
        Check(header.ActualHeight>=40 && Hits(header,header,new Point(4,4)),"Settings tab rows are clickable across their full height");
        var dropdown=(ToggleButton)window.DisplayBox.Template.FindName("DropToggle",window.DisplayBox);
        Check(window.DisplayBox.ActualHeight>=44 && Hits(window.DisplayBox,dropdown,new Point(4,4)),"Dropdown edges are clickable and rows are at least 44 pixels high");
        Check(((SolidColorBrush)dropdown.Background).Color==ResourceColor("AppBackground"),"Video/display dropdown follows the app background");
        var thumb=Find<Thumb>(window.LengthSlider).First();
        Check(thumb.ActualWidth>=26 && thumb.ActualHeight>=22 && Hits(thumb,thumb,new Point(2,2)),"Replay thumb has a larger transparent drag target");
        window.LengthSlider.Value=35;Layout();
        Check(window.ReplayLengthLabel.Text.Contains("35"),"Replay slider immediately labels five-second values");
        Render("video-settings.png");
        window.AudioSettings.IsSelected = true; Layout();
        var check=window.AudioCheck;
        Check(check.ActualHeight>=36 && Hits(check,check,new Point(3,2)),"Audio switches accept clicks above and below their visible track");
        Render("audio-settings.png");
        var before = Storage.Load(out _);
        window.GameBox.Text = "Unsaved edit";
        Click(window.MicrophoneButton);
        var muted = Storage.Load(out _);
        Check(muted.MicrophoneMuted && !muted.RequiresBufferRestart(before) && muted.GameOverride != "Unsaved edit", "Quick microphone mute persists without restarting capture or applying unrelated draft settings");
        Check(AutomationProperties.GetName(window.MicrophoneButton).Contains("Unmute"), "Microphone action announces its muted state");
        Click(window.SpeakerButton); Click(window.MicrophoneButton);
        Check(Storage.Load(out _).DesktopMuted && !Storage.Load(out _).MicrophoneMuted, "Speaker and microphone mute states are independent and reversible");
        foreach (int bytes in new[] { 2, 3, 4, 24, 4096 })
        {
            var packet = Enumerable.Repeat((byte)127, bytes).ToArray();
            AudioLoopback.ApplyMute(packet, false); Check(packet.All(b => b == 127), "Unmuted PCM is unchanged: " + bytes);
            AudioLoopback.ApplyMute(packet, true); Check(packet.Length == bytes && packet.All(b => b == 0), "Muted PCM preserves length with digital silence: " + bytes);
        }
        Click(window.ClipsNav); await Task.Delay(250); Layout();
        Check(window.LibraryTab.IsSelected && window.LibraryEmpty.Visibility == Visibility.Visible && !window.LibraryActions.IsEnabled, "Clips navigation shows its empty state and disables unavailable actions");
        Render("clips-empty.png");
        window.LibraryList.ItemsSource = new[] { new LibraryClip(@"C:\Clips\Siege\Siege — 2026-09-19_14-22-08 — 60s.mp4", "Rainbow Six Siege", DateTime.Now, 140000000) };
        window.LibraryEmpty.Visibility = Visibility.Collapsed; window.LibrarySummary.Text = "1 clip · Double-click to trim"; window.LibraryList.SelectedIndex = 0;
        Check(window.LibraryActions.IsEnabled, "Selecting a saved clip enables play, trim, reveal and delete");
        Render("clips.png"); Render("clips-small.png", 744, 601);
        Appearance.Apply("Midnight","Rose");Layout();
        var selected=(ListBoxItem)window.LibraryList.ItemContainerGenerator.ContainerFromIndex(0);
        Check(Find<Border>(selected).Any(b=>b.Background is SolidColorBrush brush && brush.Color==ResourceColor("SelectionSurface")),"Selected clip highlight updates to the current accent and palette");
        Render("clips-rose.png");Appearance.Apply("Charcoal","Mint");Layout();
        var sampleFile = Path.Combine(Storage.Root, "sample-clip.txt");
        if (File.Exists(sampleFile))
        {
            string path = File.ReadAllText(sampleFile).Trim();
            var thumbnail = await ShellThumbnail.GetAsync(path);
            Check(thumbnail != null && thumbnail.PixelWidth >= 100 && thumbnail.IsFrozen, "Explorer thumbnail loads off the UI thread as an immutable bitmap");
            window.LibraryList.ItemsSource = new[] { new LibraryClip(path, "Rainbow Six Siege", DateTime.Now, new FileInfo(path).Length) };
            Layout();
            foreach (var preview in Find<Image>(window.LibraryList)) preview.Source = thumbnail;
            Render("clips-thumbnail.png"); Render("clips-thumbnail-small.png", 744, 601);
        }
        Click(window.CaptureNav); Layout(); Check(window.MainTabs.SelectedIndex == 0, "Capture navigation returns to replay controls");
        await window.QuitAsync();
    }
    private static System.Collections.Generic.IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match;
            foreach (var descendant in Find<T>(child)) yield return descendant;
        }
    }
}





