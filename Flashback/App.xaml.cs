using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Flashback;

public partial class App : Application
{
    private Mutex? singleton;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var data = Array.IndexOf(e.Args, "--data-dir");
        if (data >= 0 && data + 1 < e.Args.Length) Storage.Root = Path.GetFullPath(e.Args[data + 1]);
        if (e.Args.Contains("--clock-test") || e.Args.Contains("--soak-test") || e.Args.Contains("--sync-media-test") || e.Args.Contains("--backlog-test"))
        {
            try { if(e.Args.Contains("--clock-test")) ReliabilityDiagnostics.Clocks(); else if(e.Args.Contains("--sync-media-test")) await ReliabilityDiagnostics.SyncMediaAsync(); else if(e.Args.Contains("--backlog-test")) await ReliabilityDiagnostics.BacklogRecoveryAsync(); else await ReliabilityDiagnostics.SoakAsync(); Shutdown(0); }
            catch(Exception ex) {Directory.CreateDirectory(Storage.Root);File.WriteAllText(Path.Combine(Storage.Root,"test-failure.txt"),ex.ToString());Shutdown(1);}
            return;
        }        if (e.Args.Contains("--editor-workspace-test"))
        {
            try { await EditorWorkspaceDiagnostics.RunAsync(); Shutdown(0); }
            catch (Exception ex) { Directory.CreateDirectory(Storage.Root); File.WriteAllText(Path.Combine(Storage.Root,"test-failure.txt"),ex.ToString()); Shutdown(1); }
            return;
        }        if (e.Args.Contains("--trim-preview-test"))
        {
            try { await TrimInteractionDiagnostics.PreviewAsync(); Shutdown(0); }
            catch (Exception ex) { Directory.CreateDirectory(Storage.Root); File.WriteAllText(Path.Combine(Storage.Root, "test-failure.txt"), ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--trim-interaction-test"))
        {
            try { await TrimInteractionDiagnostics.RunAsync(); Shutdown(0); }
            catch (Exception ex) { Directory.CreateDirectory(Storage.Root); File.WriteAllText(Path.Combine(Storage.Root, "test-failure.txt"), ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--frame-history-test"))
        {
            try { FrameHistoryDiagnostics.Run(); Shutdown(0); }
            catch (Exception ex) { Directory.CreateDirectory(Storage.Root); File.WriteAllText(Path.Combine(Storage.Root, "test-failure.txt"), ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--performance-test") || e.Args.Contains("--performance-compatibility-test"))
        {
            try { await PerformanceDiagnostics.RunAsync(e.Args.Contains("--performance-compatibility-test")); Shutdown(0); }
            catch (Exception ex) { Directory.CreateDirectory(Storage.Root); File.WriteAllText(Path.Combine(Storage.Root, "test-failure.txt"), ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--motion-test") || e.Args.Contains("--motion-hardware-test"))
        {
            try { await MotionDiagnostics.RunAsync(e.Args.Contains("--motion-hardware-test")); Shutdown(0); }
            catch (Exception ex) { Directory.CreateDirectory(Storage.Root); File.WriteAllText(Path.Combine(Storage.Root, "test-failure.txt"), ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--continuity-test") || e.Args.Contains("--continuity-hardware-test"))
        {
            try { await ContinuityDiagnostics.RunAsync(e.Args.Contains("--continuity-hardware-test")); Shutdown(0); }
            catch (Exception ex) { Directory.CreateDirectory(Storage.Root); File.WriteAllText(Path.Combine(Storage.Root, "test-failure.txt"), ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--shared-hotkey-test") || e.Args.Contains("--hotkey-owner"))
        {
            try { if (e.Args.Contains("--hotkey-owner")) await SharedHotkeyDiagnostics.OwnerAsync(); else await SharedHotkeyDiagnostics.RunAsync(); Shutdown(0); }
            catch (Exception ex) { Directory.CreateDirectory(Storage.Root); File.WriteAllText(Path.Combine(Storage.Root, "test-failure.txt"), ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--mixer-test"))
        {
            try { await MixerDiagnostics.RunAsync(); Shutdown(0); }
            catch (Exception ex) { Directory.CreateDirectory(Storage.Root); File.WriteAllText(Path.Combine(Storage.Root, "test-failure.txt"), ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--playback-test"))
        {
            try { await PlaybackDiagnostics.RunAsync(); Shutdown(0); }
            catch (Exception ex) { Directory.CreateDirectory(Storage.Root); File.WriteAllText(Path.Combine(Storage.Root, "test-failure.txt"), ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--ui-test"))
        {
            try { await UiDiagnostics.RunAsync(); Shutdown(0); }
            catch (Exception ex) { Directory.CreateDirectory(Storage.Root); File.WriteAllText(Path.Combine(Storage.Root, "test-failure.txt"), ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--startup-ui-test"))
        {
            try { await StartupDiagnostics.RunAsync(); Shutdown(0); }
            catch (Exception ex) { Directory.CreateDirectory(Storage.Root); File.WriteAllText(Path.Combine(Storage.Root, "test-failure.txt"), ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--encoder-test"))
        {
            try { await EncoderDiagnostics.RunAsync(); Shutdown(0); }
            catch (Exception ex) { Directory.CreateDirectory(Storage.Root); File.WriteAllText(Path.Combine(Storage.Root, "test-failure.txt"), ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--audio-test") || e.Args.Contains("--audio-hardware-test"))
        {
            try { if (e.Args.Contains("--audio-hardware-test")) await AudioDiagnostics.RunHardwareAsync(); else await AudioDiagnostics.RunAsync(); Shutdown(0); }
            catch (Exception ex) { Directory.CreateDirectory(Storage.Root); File.WriteAllText(Path.Combine(Storage.Root, "test-failure.txt"), ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--shortcut-ui-test") || e.Args.Contains("--shortcut-logic-test"))
        {
            try { await ShortcutDiagnostics.RunAsync(e.Args.Contains("--shortcut-logic-test")); Shutdown(0); }
            catch (Exception ex) { Directory.CreateDirectory(Storage.Root); File.WriteAllText(Path.Combine(Storage.Root, "test-failure.txt"), ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--edit-test"))
        {
            try { await EditorDiagnostics.RunAsync(e.Args.Contains("--editor-ui-test")); Shutdown(0); }
            catch (Exception ex) { Directory.CreateDirectory(Storage.Root); File.WriteAllText(Path.Combine(Storage.Root, "test-failure.txt"), ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--system-test"))
        {
            try { Shutdown(await SystemDiagnostics.RunAsync(e.Args.Contains("--probes"), e.Args.Contains("--long-buffer")) ? 0 : 1); }
            catch (Exception ex) { Directory.CreateDirectory(Storage.Root); File.WriteAllText(Path.Combine(Storage.Root, "test-failure.txt"), ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--overlay-test"))
        {
            try { await OverlayDiagnostics.RunAsync(); Shutdown(0); }
            catch (Exception ex) { Directory.CreateDirectory(Storage.Root); File.WriteAllText(Path.Combine(Storage.Root, "test-failure.txt"), ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--self-test") || e.Args.Contains("--capture-test"))
        {
            try { await Diagnostics.RunAsync(e.Args.Contains("--capture-test")); Shutdown(0); }
            catch (Exception ex) { Directory.CreateDirectory(Storage.Root); File.WriteAllText(Path.Combine(Storage.Root, "test-failure.txt"), ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--render-ui"))
        {
            var preview = new MainWindow(true);
            var content = (FrameworkElement)preview.Content;
            preview.Content = null;
            var canvas = new System.Windows.Controls.Border { Background = new SolidColorBrush(Color.FromRgb(17, 20, 25)), Child = content, Width = 880, Height = 780 };
            System.Windows.Documents.TextElement.SetFontFamily(canvas, new FontFamily("Segoe UI"));
            System.Windows.Documents.TextElement.SetFontSize(canvas, 14);
            System.Windows.Documents.TextElement.SetForeground(canvas, new SolidColorBrush(Color.FromRgb(237, 240, 243)));
            canvas.Measure(new Size(880, 780)); canvas.Arrange(new Rect(0, 0, 880, 780)); canvas.UpdateLayout();
            var image = new RenderTargetBitmap(880, 780, 96, 96, PixelFormats.Pbgra32);
            image.Render(canvas);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
            Directory.CreateDirectory(Storage.Root);
            using (var file = File.Create(Path.Combine(Storage.Root, "ui-preview.png"))) png.Save(file);
            preview.MainTabs.SelectedIndex = 1; canvas.UpdateLayout();
            var settingsImage = new RenderTargetBitmap(880, 780, 96, 96, PixelFormats.Pbgra32);
            settingsImage.Render(canvas);
            var settingsPng = new PngBitmapEncoder(); settingsPng.Frames.Add(BitmapFrame.Create(settingsImage));
            using (var file = File.Create(Path.Combine(Storage.Root, "ui-settings.png"))) settingsPng.Save(file);
            preview.AudioSettings.IsSelected = true; canvas.UpdateLayout(); preview.AudioDeviceBox.BringIntoView(); await Task.Delay(150); canvas.UpdateLayout();
            var audioSettingsImage = new RenderTargetBitmap(880, 780, 96, 96, PixelFormats.Pbgra32); audioSettingsImage.Render(canvas);
            var audioSettingsPng = new PngBitmapEncoder(); audioSettingsPng.Frames.Add(BitmapFrame.Create(audioSettingsImage));
            using (var file = File.Create(Path.Combine(Storage.Root, "ui-audio-settings.png"))) audioSettingsPng.Save(file);
            preview.OpenAppMixForRender(); canvas.UpdateLayout(); await Task.Delay(150); canvas.UpdateLayout();
            var mixerImage = new RenderTargetBitmap(880, 780, 96, 96, PixelFormats.Pbgra32); mixerImage.Render(canvas);
            var mixerPng = new PngBitmapEncoder(); mixerPng.Frames.Add(BitmapFrame.Create(mixerImage));
            using (var file = File.Create(Path.Combine(Storage.Root, "ui-audio-mixer.png"))) mixerPng.Save(file);
            preview.HotkeySettings.IsSelected = true; canvas.UpdateLayout(); await Task.Delay(150); canvas.UpdateLayout();
            var hotkeyImage = new RenderTargetBitmap(880, 780, 96, 96, PixelFormats.Pbgra32); hotkeyImage.Render(canvas);
            var hotkeyPng = new PngBitmapEncoder(); hotkeyPng.Frames.Add(BitmapFrame.Create(hotkeyImage));
            using (var file = File.Create(Path.Combine(Storage.Root, "ui-hotkey-settings.png"))) hotkeyPng.Save(file);
            preview.MainTabs.SelectedItem = preview.LibraryTab; await Task.Delay(300); canvas.UpdateLayout();
            var libraryImage = new RenderTargetBitmap(880, 780, 96, 96, PixelFormats.Pbgra32); libraryImage.Render(canvas);
            var libraryPng = new PngBitmapEncoder(); libraryPng.Frames.Add(BitmapFrame.Create(libraryImage));
            using (var file = File.Create(Path.Combine(Storage.Root, "ui-library.png"))) libraryPng.Save(file);
            await preview.QuitAsync(); return;
        }
        singleton = new Mutex(true, @"Local\Flashback.ReplayRecorder", out bool created);
        if (!created) { MessageBox.Show("Flashback is already running. Open it from the system tray.", "Flashback"); Shutdown(); return; }
        var main = new MainWindow(); MainWindow = main;
        if (!e.Args.Contains("--tray")) main.Show();
        await main.AutoStartAsync();
    }
    protected override void OnExit(ExitEventArgs e) { singleton?.Dispose(); base.OnExit(e); }
}






