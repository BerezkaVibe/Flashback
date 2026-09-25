using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Flashback;

public partial class MainWindow : Window
{
    private readonly Recorder recorder;
    internal bool RecordingActive => recordingRequested;
    private readonly SaveOverlay overlay = new();
    private readonly Hotkeys hotkeys;
    private readonly Forms.NotifyIcon tray;
    private readonly DispatcherTimer timer;
    private readonly Forms.ToolStripMenuItem trayToggle;
    private readonly Forms.ToolStripMenuItem traySave;
    private Settings settings;
    private bool busy, quitting;
    private string? lastClip;
    private string errorDetails = "";
    private readonly bool syntheticCapture;
    private readonly CaptureRecovery recovery = new();
    private bool recordingRequested;
    private string shortcutBeforeEdit = "";
    private ShortcutKeyCapture? shortcutCapture;
    internal bool CapturingShortcutForTest => shortcutCapture != null;
    internal bool UsingSharedHotkeysForTest => hotkeys.UsesSharedInput;
    internal int FunctionEventsForTest => shortcutCapture?.FunctionEvents ?? -1;
    internal bool FeedShortcutForTest(uint key, bool down) => shortcutCapture?.HandleKey(key, down) == true;
    public MainWindow(bool renderOnly = false, bool syntheticCapture = false)
    {
        this.syntheticCapture = syntheticCapture;
        InitializeComponent(); WindowTheme.Attach(this); MemoryTrim.Attach(this);
        settings = Storage.Load(out var warning);
        recorder = new Recorder();
        hotkeys = new Hotkeys();
        tray = new Forms.NotifyIcon { Text = "Flashback — paused", Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!), Visible = !renderOnly };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Flashback", null, (_, _) => ShowWindow());
        menu.Items.Add("Settings", null, (_, _) => { MainTabs.SelectedIndex = 1; ShowWindow(); });
        menu.Items.Add("Saved clips", null, (_, _) => { MainTabs.SelectedItem = LibraryTab; ShowWindow(); });
        traySave = new Forms.ToolStripMenuItem("Save replay", null, async (_, _) => await SaveReplayAsync());
        trayToggle = new Forms.ToolStripMenuItem("Start buffer", null, async (_, _) => await ToggleAsync());
        menu.Items.Add(traySave); menu.Items.Add(trayToggle);
        menu.Items.Add("Open clips folder", null, (_, _) => OpenFolder());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit Flashback", null, async (_, _) => await QuitAsync());
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => ShowWindow();
        hotkeys.Pressed += async () => await SaveReplayAsync();
        hotkeys.PausePressed += async () => await ToggleAsync();
        recorder.Faulted += (generation, error) => Dispatcher.BeginInvoke(async () =>
        {
            if (!quitting && recordingRequested && generation == recorder.Generation && !recovery.IsRunning)
                await RecoverCaptureAsync(error);
        });
        FpsBox.ItemsSource = new[] { 30, 60, 90, 120 }.Select(f => new Choice(f, f + " FPS")).ToList();
        ResolutionBox.ItemsSource = new[] { new Choice(720, "720p"), new Choice(1080, "1080p"), new Choice(1440, "1440p"), new Choice(2160, "2160p / 4K"), new Choice(0, "Native display resolution") };
        QualityBox.DisplayMemberPath = "Label"; QualityBox.SelectedValuePath = "Value";
        FpsBox.SelectionChanged += (_, _) => RefreshQualityLabels(); ResolutionBox.SelectionChanged += (_, _) => RefreshQualityLabels();
        EncoderBox.ItemsSource = VideoEncoder.Preferences;
        OverlayCornerBox.ItemsSource = new[] { "Top right", "Top left", "Bottom right", "Bottom left" };
        OverlayDurationBox.ItemsSource = Enumerable.Range(2, 7).Select(s => new Choice(s, s + " seconds")).ToList();
        OverlayDurationBox.DisplayMemberPath = "Label"; OverlayDurationBox.SelectedValuePath = "Value";
        DisplayBox.ItemsSource = DisplayChoices();
        foreach (var combo in new[] { FpsBox, ResolutionBox, DisplayBox }) { combo.DisplayMemberPath = "Label"; combo.SelectedValuePath = "Value"; }
        LoadControls();
        UpdateEditorLabel();
        if (!renderOnly)
        {
            StartUpdateChecks();
            try { hotkeys.Register(settings.Hotkey, settings.PauseHotkey); }
            catch (Exception ex) { warning = ex.Message; try { hotkeys.Register(settings.Hotkey); } catch { } }
            try
            {
                var history = Path.Combine(Storage.Root, "clips.jsonl");
                if (File.Exists(history))
                {
                    var item = File.ReadLines(history).TakeLast(50).Reverse().Select(line => { try { return System.Text.Json.JsonSerializer.Deserialize<ClipResult>(line); } catch { return null; } }).FirstOrDefault(c => c != null && File.Exists(c.Path));
                    if (item != null) SetLastClip(item);
                }
            }
            catch { }
        }
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += async (_, _) => { GameTracker.Update(); Refresh(); await AutoGameTickAsync(); };
        if (!renderOnly) timer.Start();
        if (warning != null) Tell(warning, true);
        Closing += OnClosing;
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) ClearError(); };
        Deactivated += (_, _) => { StopShortcutCapture(); try { hotkeys.Resume(); } catch { } };
        Activated += (_, _) =>
        {
            if (Keyboard.FocusedElement is TextBox box && IsShortcutBox(box)) BeginShortcutCapture(box);
        };
        Microsoft.Win32.SystemEvents.SessionSwitch += SessionSwitch;
        Microsoft.Win32.SystemEvents.PowerModeChanged += PowerChanged;
        UpdateNavigation(); Refresh();
        if (!renderOnly) Loaded += async (_, _) => await ReloadLibraryAsync();
        // Check the hardware encoder in the background (once per driver) so Record starts quickly.
        if (!renderOnly && !syntheticCapture) Loaded += (_, _) => _ = VideoEncoder.WarmAsync(recorder.FfmpegPath, settings.Copy());
    }
    public async Task AutoStartAsync() { if (settings.StartBufferOnLaunch) await ToggleAsync(); }
    private void LoadControls()
    {
        LoadAppearance();
        LoadAudioDevices(settings.AudioDeviceId);
        EncoderBox.SelectedItem = settings.Encoder;
        LoadMicrophones(settings.MicrophoneDeviceId);
        MicrophoneCheck.IsChecked = settings.MicrophoneAudio; LoadAudioOptions();
        LengthSlider.Value = settings.ReplaySeconds; FpsBox.SelectedValue = settings.FrameRate;
        ResolutionBox.SelectedValue = settings.Height; RefreshQualityLabels(); QualityBox.SelectedValue = settings.Quality; DisplayBox.SelectedValue = settings.DisplayIndex;
        AudioCheck.IsChecked = settings.DesktopAudio; CursorCheck.IsChecked = settings.ShowCursor;
        FolderBox.Text = settings.OutputFolder; GameBox.Text = settings.GameOverride; HotkeyBox.Text = settings.Hotkey;
        PauseHotkeyBox.Text = settings.PauseHotkey; LoadTrimShortcuts(settings);
        OverlayCheck.IsChecked = settings.OverlayEnabled;
        OverlayCornerBox.SelectedItem = settings.OverlayCorner; OverlayDurationBox.SelectedValue = settings.OverlaySeconds;
        LaunchCheck.IsChecked = settings.StartWithWindows; AutoBufferCheck.IsChecked = settings.StartBufferOnLaunch; NotifyCheck.IsChecked = settings.Notifications;
        UpdateCheck.IsChecked = settings.CheckForUpdates; GpuExportCheck.IsChecked = settings.ExportGpuDecode; ThumbnailsCheck.IsChecked = settings.ShowThumbnails; WaveformsCheck.IsChecked = settings.ShowWaveforms; PreviewEffectsCheck.IsChecked = settings.PreviewEffects; AnimationsCheck.IsChecked = settings.UiAnimations; SyncLighterMode(); PerformanceOptions.Apply(settings); SeparateTracksCheck.IsChecked = settings.SeparateAudioTracks; SeparateAppsCheck.IsChecked = settings.SeparateAppAudio;
        if (!AppMixSource.Supported) { SeparateAppsCheck.IsEnabled = false; SeparateAppsNote.Text = "Needs Windows 11 (or Windows 10 build 20348 or later)."; } AutoGameCheck.IsChecked = settings.AutoStartWithGames;
    }
    private void ReplayLength_Changed(object sender,RoutedPropertyChangedEventArgs<double> e)
    {
        if(ReplayLengthLabel==null) return;
        int seconds=(int)(Math.Round(e.NewValue/5)*5);
        ReplayLengthLabel.Text=seconds<60 ? $"{seconds} sec" : $"{seconds/60} min"+(seconds%60>0 ? $" {seconds%60} sec" : "");
    }
    private Settings ReadControls() { var next = ReadBasicControls(); ReadTrimShortcuts(next); next.AppVolumes = ReadAppMix(); return next; }
    private Settings ReadBasicControls() => new()
    {
        ReplaySeconds = (int)(Math.Round(LengthSlider.Value/5)*5), FrameRate = (int)FpsBox.SelectedValue, Height = (int)ResolutionBox.SelectedValue,
        Quality = (string)QualityBox.SelectedValue, DisplayIndex = (int)DisplayBox.SelectedValue,
        Encoder = (string)EncoderBox.SelectedItem, Palette=settings.Palette, AccentColor=settings.AccentColor,
        DesktopAudio = AudioCheck.IsChecked == true, ShowCursor = CursorCheck.IsChecked == true,
        AudioDeviceId = AudioDeviceBox.SelectedValue as string ?? settings.AudioDeviceId,
        MicrophoneAudio = MicrophoneCheck.IsChecked == true, AudioBitrate = AudioBitrateBox.SelectedValue as int? ?? settings.AudioBitrate, MicNoiseReduction = (MicNoiseBox.SelectedItem as ComboBoxItem)?.Tag as string ?? settings.MicNoiseReduction,
        DesktopVolume = (int)Math.Round(DesktopVolumeSlider.Value), MicrophoneVolume = (int)Math.Round(MicrophoneVolumeSlider.Value),
        DesktopLocked = settings.DesktopLocked && !string.IsNullOrEmpty(AudioDeviceBox.SelectedValue as string), MicrophoneLocked = settings.MicrophoneLocked && !string.IsNullOrEmpty(MicrophoneDeviceBox.SelectedValue as string),
        DesktopMuted = settings.DesktopMuted, MicrophoneMuted = settings.MicrophoneMuted,
        MicrophoneDeviceId = MicrophoneDeviceBox.SelectedValue as string ?? settings.MicrophoneDeviceId,
        OutputFolder = FolderBox.Text.Trim(), GameOverride = GameBox.Text.Trim(), Hotkey = HotkeyBox.Text.Trim(),
        StartWithWindows = LaunchCheck.IsChecked == true, CheckForUpdates = UpdateCheck.IsChecked == true, ExportGpuDecode = GpuExportCheck.IsChecked != false, ShowThumbnails = ThumbnailsCheck.IsChecked != false, ShowWaveforms = WaveformsCheck.IsChecked != false, PreviewEffects = PreviewEffectsCheck.IsChecked != false, UiAnimations = AnimationsCheck.IsChecked != false, SeparateAudioTracks = SeparateTracksCheck.IsChecked == true, SeparateAppAudio = SeparateAppsCheck.IsChecked == true, AutoStartWithGames = AutoGameCheck.IsChecked == true, StartBufferOnLaunch = AutoBufferCheck.IsChecked == true, Notifications = NotifyCheck.IsChecked == true,
        PauseHotkey = PauseHotkeyBox.Text, OverlayEnabled = OverlayCheck.IsChecked == true, ShowSavingOverlay = true,
        OverlayCorner = (string)OverlayCornerBox.SelectedItem, OverlaySeconds = (int)OverlayDurationBox.SelectedValue,
        ExternalEditorPath = settings.ExternalEditorPath
    };
    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (busy || recorder.IsSaving) return;
        busy = true; Refresh();
        var previous = settings.Copy();
        try
        {
            var next = ReadControls(); next.Validate(); Storage.EnsureWritable(next.OutputFolder);
            hotkeys.Register(next.Hotkey, next.PauseHotkey);
            try
            {
                if (next.StartWithWindows != previous.StartWithWindows) StartupRegistration.Set(next.StartWithWindows);
                Storage.Save(next);
            }
            catch
            {
                hotkeys.Register(previous.Hotkey, previous.PauseHotkey);
                if (next.StartWithWindows != previous.StartWithWindows) StartupRegistration.Set(previous.StartWithWindows);
                throw;
            }
            bool restart = recorder.IsRecording && next.RequiresBufferRestart(previous);
            settings = next; PerformanceOptions.Apply(next); if (!restart) recorder.SetAudioLive(next);
            overlay.Dispose();
            if (restart) { Tell("Restarting the buffer with your settings…"); await recorder.StartAsync(settings, syntheticCapture); }
            Tell("Settings saved." + (restart ? " The replay buffer is filling again." : "") + (hotkeys.UsesSharedInput ? " Shared shortcut active; the other app may respond too." : ""));
        }
        catch (Exception ex)
        {
            if (recordingRequested && CaptureRecovery.IsRecoverable(ex.ToString())) await RecoverCaptureAsync(ex.ToString());
            else { recordingRequested = recorder.IsRecording; ReportError(ex); }
        }
        finally { busy = false; Refresh(); }
    }
    private async Task ToggleAsync()
    {
        if (recovery.IsRunning) { await PauseCaptureAsync("Paused. Automatic reconnection cancelled."); return; }
        if (busy || recorder.IsSaving) return;
        busy = true; Refresh();
        try
        {
            if (recorder.IsRecording) { recordingRequested = false; await recorder.StopAsync(); Tell("Paused."); }
            else { recordingRequested = true; recovery.ResetBudget(); Tell(""); Refresh(); await recorder.StartAsync(settings, syntheticCapture); Tell(recorder.UsesCompatibilityConversion ? "Recording with AMD compatibility conversion. Encoding stays on the GPU; lowering FPS or resolution reduces CPU conversion work." : "Recording."); }
        }
        catch (Exception ex)
        {
            if (recordingRequested && CaptureRecovery.IsRecoverable(ex.ToString())) await RecoverCaptureAsync(ex.ToString());
            else { recordingRequested = false; ReportError(ex); }
        }
        finally { busy = false; Refresh(); }
    }
    internal async Task RecoverCaptureAsync(string error)
    {
        if (quitting || !recordingRequested || recovery.IsRunning) return;
        busy = true;
        string history = $"Capture interruption at {DateTimeOffset.Now:O}\n{error}\n{recorder.LastStartupReport}\n";
        try
        {
            if (!CaptureRecovery.IsRecoverable(error))
            {
                recordingRequested = false;
                await recorder.StopAsync();
                ReportError(new IOException("Recording stopped: " + error));
                return;
            }
            await recovery.RunAsync(async token =>
            {
                token.ThrowIfCancellationRequested();
                try { await recorder.RestartAfterCaptureLossAsync(settings, syntheticCapture, token); }
                finally { history += recorder.LastStartupReport + "\n"; }
            }, attempt =>
            {
                if (history.Length > 32768) history = history[^32768..];
                Refresh();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            recordingRequested = false;
            await recorder.StopAsync();
            if (!quitting) { ReportError(ex); errorDetails += "\nRecovery history:\n" + history; }
        }
        finally
        {
            try { File.WriteAllText(Path.Combine(Storage.Root, "last-recovery.txt"), history); } catch { }
            busy = false;
            if (!quitting) Refresh();
        }
    }
    internal async Task PauseCaptureAsync(string message)
    {
        recordingRequested = false;
        recovery.Cancel();
        if (!quitting) Refresh();
        try { await recovery.Completion; } catch { }
        await recorder.StopAsync();
        if (!quitting) { Tell(message); Refresh(); }
    }
    internal void InjectCaptureLossForTest(bool audioPipeFirst = false, bool releaseFrame = false) => recorder.InjectCaptureLossForTest(audioPipeFirst, releaseFrame);
    internal long CaptureGenerationForTest => recorder.Generation;
    internal bool RecoveringForTest => recovery.IsRunning;
    private void LoadAudioDevices(string selected)
    {
        var devices = new System.Collections.Generic.List<PlaybackDevice> { new("", "Follow Windows default") };
        try { devices.AddRange(AudioLoopback.GetPlaybackDevices()); } catch { }
        if (!devices.Any(d => d.Id == selected)) devices.Add(new(selected, "Saved audio device (disconnected)"));
        AudioDeviceBox.ItemsSource = devices;
        AudioDeviceBox.SelectedValue = selected;
    }
    private void LoadMicrophones(string selected)
    {
        var devices = new System.Collections.Generic.List<PlaybackDevice> { new("", "Windows default microphone") };
        try { devices.AddRange(AudioLoopback.GetPlaybackDevices(microphone: true)); } catch { }
        if (!devices.Any(d => d.Id == selected)) devices.Add(new(selected, "Saved microphone (disconnected)"));
        MicrophoneDeviceBox.ItemsSource = devices;
        MicrophoneDeviceBox.SelectedValue = selected;
    }
    private void RefreshAudio_Click(object sender, RoutedEventArgs e)
    {
        LoadAudioDevices(AudioDeviceBox.SelectedValue as string ?? settings.AudioDeviceId);
        LoadMicrophones(MicrophoneDeviceBox.SelectedValue as string ?? settings.MicrophoneDeviceId);
        var levels = ReadAppMix(); var saved = settings.AppVolumes; settings.AppVolumes = levels; LoadAppMix(); settings.AppVolumes = saved;
    }
    private async Task SaveReplayAsync()
    {
        if (quitting) return;
        GameTracker.Update();
        var monitor = SaveOverlay.CurrentMonitor();
        var game = string.IsNullOrWhiteSpace(settings.GameOverride) ? GameTracker.LastForeground : settings.GameOverride;
        var pressed = DateTimeOffset.Now;
        ShowOverlay(SaveFeedback.Saving, game + " · replay requested", monitor);
        ClipResult clip;
        try
        {


            var task = recorder.SaveAsync(game, pressed); Refresh();
            clip = await task;
        }
        catch (Exception ex)
        {
            if (!quitting) { Tell(ex.Message, true); ShowOverlay(SaveFeedback.Failed, ex.Message, monitor); }
            return;
        }
        finally { if (!quitting) Refresh(); }
        if (quitting) return;
        // This is reached only after SaveAsync has finalized and moved the MP4 into its destination.
        SetLastClip(clip); Tell($"Saved {clip.Duration:0} seconds to {clip.Game}.");
        ShowOverlay(SaveFeedback.Saved, $"{clip.Game} · {clip.Duration:0} seconds", monitor);
        try { if (settings.Notifications) tray.ShowBalloonTip(3500, "Replay saved", $"{clip.Game} · {clip.Duration:0}s", Forms.ToolTipIcon.Info); }
        catch { /* A notification failure must never turn a successful save into a save error. */ }
        if (LibraryTab.IsSelected && IsVisible && WindowState != WindowState.Minimized) await ReloadLibraryAsync();
    }
    private void ShowOverlay(SaveFeedback state, string detail, IntPtr monitor)
    {
        try { overlay.Show(state, detail, settings, monitor); }
        catch { /* Recording and saving remain independent of optional UI feedback. */ }
    }
    private void PreviewOverlay_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var preview = ReadControls(); preview.OverlayEnabled = true;
            overlay.Show(SaveFeedback.Preview, "Your replay has finished saving", preview, SaveOverlay.CurrentMonitor());
        }
        catch (Exception ex) { Tell("Overlay preview could not be shown: " + ex.Message, true); }
    }
    private void Hotkey_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        shortcutBeforeEdit = ((TextBox)sender).Text;
        if (trimBoxes.TryGetValue((TextBox)sender, out var action)) trimBindingBeforeEdit = trimBindings[action];
        BeginShortcutCapture((TextBox)sender);
        Tell("Press your shortcut now. Escape cancels; Tab moves to the next field.");
    }
    private void Hotkey_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        StopShortcutCapture();
        if (quitting) return;
        try { hotkeys.Resume(); } catch (Exception ex) { Tell(ex.Message, true); }
    }
    private void Hotkey_KeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key switch { Key.System => e.SystemKey, Key.ImeProcessed => e.ImeProcessedKey, Key.DeadCharProcessed => e.DeadCharProcessedKey, _ => e.Key };
        if (key == Key.None) return;
        if (key == Key.Tab) return;
        e.Handled = true;
        if (key == Key.Escape) { CancelShortcut((TextBox)sender); Keyboard.Focus(ApplyButton); return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Windows)) { Tell("Choose a single key or combine it with Ctrl, Alt or Shift."); return; }
        SetShortcut((TextBox)sender, key, mods, true);
    }
    private void BeginShortcutCapture(TextBox target)
    {
        StopShortcutCapture();
        hotkeys.Suspend();
        try
        {
            ShortcutKeyCapture? entry = null;
            entry = new ShortcutKeyCapture(() => IsActive && target.IsKeyboardFocusWithin,
                (key, mods, released) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!ReferenceEquals(shortcutCapture, entry) || !target.IsKeyboardFocusWithin || !IsActive) return;
                SetShortcut(target, key, mods, released);
            })));
            shortcutCapture = entry;
        }
        catch { /* Keep standard WPF key entry available if native entry is unavailable. */ }
    }
    private void StopShortcutCapture() { shortcutCapture?.Dispose(); shortcutCapture = null; }
    private void SetLastClip(ClipResult clip)
    {
        lastClip = clip.Path;
    }
    private void Refresh()
    {
        bool active = recorder.IsRecording;

        // Record -> Recording the moment it's pressed; the light pulses until frames are encoding.
        bool starting = busy && recordingRequested && !active && !recovery.IsRunning;
        StatusTitle.Text = active || recovery.IsRunning ? "Recording" : starting ? "Starting…" : "Paused";
        SetRecordingAppearance(active || recovery.IsRunning || starting);
        PulseRecordLight(starting);
        RecordLabel.Text = active || recovery.IsRunning || starting ? "Recording" : "Record";
        SetActionName(ToggleButton, active || recovery.IsRunning ? "Recording — click to pause" : starting ? "Starting recording" : "Record");
        ToggleButton.IsEnabled = recovery.IsRunning || !busy && !recorder.IsSaving;
        TopSaveButton.IsEnabled = recordingRequested && (active || recovery.IsRunning);


        SetActionName(TopSaveButton, "Save replay");
        UpdateQuickAudio();
        HeaderVersion.Text = (active ? recorder.EncoderName : settings.Encoder == "Automatic" ? "Auto GPU" : settings.Encoder) + " · v" + typeof(MainWindow).Assembly.GetName().Version!.ToString(3);
        try
        {
            var size = active ? recorder.RecordingSize : Recorder.OutputSize(settings);
            HeaderCapture.Text = active
                ? $"{size.Width} × {size.Height} · {recorder.MeasuredFps:0.#} FPS"
                : $"{size.Width} × {size.Height} · {settings.FrameRate} FPS set";
        }
        catch { HeaderCapture.Text = "Display unavailable"; }
        ApplyButton.IsEnabled = !busy && !recorder.IsSaving;
        SettingsPanel.IsEnabled = !busy && !recorder.IsSaving;
        HotkeyLabel.Text = settings.Hotkey.Replace("+", " + ");
        EstimateLabel.Text = $"Applied settings: about {settings.EstimatedBufferMb:0} MB maximum buffer, plus a temporary copy while saving. Each saved clip resets the replay range. New footage remains available while saving. Display interruptions use black video with continued audio.";
        trayToggle.Text = active || recovery.IsRunning ? "Pause buffer" : "Start buffer"; trayToggle.Enabled = ToggleButton.IsEnabled;
        traySave.Enabled = TopSaveButton.IsEnabled;
        tray.Text = active || recovery.IsRunning ? "Flashback — recording" : "Flashback — paused";
    }
    private void Tell(string message, bool error = false)
    {
        CopyErrorButton.Visibility = error ? Visibility.Visible : Visibility.Collapsed;
        if (error) errorDetails = $"Flashback {typeof(MainWindow).Assembly.GetName().Version} · {DateTimeOffset.Now:O}\n{message}";
        MessageLabel.Text = message;
        MessageLabel.ToolTip = message;
        MessageLabel.Foreground = new SolidColorBrush(error ? Color.FromRgb(244, 154, 154) : Color.FromRgb(157, 166, 177));
        if (error) try { tray.ShowBalloonTip(5000, "Flashback", message.Length > 220 ? message[..220] : message, Forms.ToolTipIcon.Warning); } catch { }
    }
    // An error stays until the window is closed to the tray, minimized or the editor is opened; then the
    // bottom line goes back to its usual text.
    private void ClearError()
    {
        if (CopyErrorButton.Visibility != Visibility.Visible) return;
        CopyErrorButton.Visibility = Visibility.Collapsed; errorDetails = "";
        MessageLabel.Text = "Ready when you are."; MessageLabel.ToolTip = null;
        MessageLabel.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
    }
    private void ReportError(Exception error)
    {
        Tell(error.Message, true);
        errorDetails += $"\nWindows: {Environment.OSVersion}\nRecording: display {settings.DisplayIndex + 1}, {settings.Height}p, {settings.FrameRate} FPS, speaker audio {settings.DesktopAudio}, microphone {settings.MicrophoneAudio}\n{error}";
        if (!string.IsNullOrWhiteSpace(recorder.LastStartupReport)) errorDetails += "\n" + recorder.LastStartupReport;
    }
    private void CopyError_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(errorDetails); MessageLabel.Text = "Error details copied. You can paste them into your message."; }
        catch { MessageLabel.Text = "Clipboard is busy. Try copying again."; }
    }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "Choose where Flashback saves clips", UseDescriptionForTitle = true, SelectedPath = FolderBox.Text };
        if (dialog.ShowDialog() == Forms.DialogResult.OK) FolderBox.Text = dialog.SelectedPath;
    }
    private void OpenFolder()
    {
        try { Directory.CreateDirectory(settings.OutputFolder); Process.Start(new ProcessStartInfo(settings.OutputFolder) { UseShellExecute = true }); }
        catch (Exception ex) { Tell(ex.Message, true); }
    }
    private async void Toggle_Click(object sender, RoutedEventArgs e) => await ToggleAsync();
    private async void Save_Click(object sender, RoutedEventArgs e) { SnapClapper(); await SaveReplayAsync(); }
    private void SnapClapper() { if (PerformanceOptions.Animations) ClapperTilt.BeginAnimation(RotateTransform.AngleProperty, new System.Windows.Media.Animation.DoubleAnimation(-14, 0, TimeSpan.FromMilliseconds(110)) { AutoReverse = true, BeginTime = TimeSpan.Zero }); }
    private void OpenFolder_Click(object sender, RoutedEventArgs e) => OpenFolder();
    private void Play_Click(object sender, RoutedEventArgs e) { try { if (lastClip != null) Process.Start(new ProcessStartInfo(lastClip) { UseShellExecute = true }); } catch (Exception ex) { Tell(ex.Message, true); } }
    public void ShowWindow() { Show(); WindowState = WindowState.Normal; Activate(); if (LibraryTab.IsSelected) _ = ReloadLibraryAsync(); }
    private void OnClosing(object? sender, CancelEventArgs e) { if (!quitting) { e.Cancel = true; Hide(); ClearError(); } }
    private void SessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock)
            Dispatcher.BeginInvoke(async () => await PauseCaptureAsync("Paused because Windows was locked. Start the buffer when you return."));
    }
    private void PowerChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Suspend)
            Dispatcher.BeginInvoke(async () => await PauseCaptureAsync("Paused for sleep. Start the buffer when you return."));
    }
    public async Task QuitAsync()
    {
        if (quitting) return;
        quitting = true;
        StopShortcutCapture();
        await PauseCaptureAsync("");
        if (trimWindow != null) await trimWindow.CloseForQuitAsync();
        overlay.Dispose();
        timer.Stop(); hotkeys.Dispose();
        Microsoft.Win32.SystemEvents.SessionSwitch -= SessionSwitch;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= PowerChanged;
        tray.Visible = false; tray.Dispose();
        await recorder.DisposeAsync();
        Application.Current.Shutdown();
    }
    private record Choice(int Value, string Label);
    private record QualityChoice(string Value, string Label);
    // Bitrate depends on resolution and FPS, so the labels follow those choices.
    private void RefreshQualityLabels()
    {
        if (QualityBox == null) return;
        int height = ResolutionBox.SelectedValue as int? ?? settings.Height, fps = FpsBox.SelectedValue as int? ?? settings.FrameRate;
        var selected = QualityBox.SelectedValue as string ?? settings.Quality;
        QualityBox.ItemsSource = new[] { "Compact", "Balanced", "High" }
            .Select(q => new QualityChoice(q, $"{q} · {new Settings { Quality = q, Height = height, FrameRate = fps }.BitrateMbps} Mbps")).ToList();
        QualityBox.SelectedValue = selected;
    }
    private List<Choice> DisplayChoices()
    {
        var screens = Forms.Screen.AllScreens; var names = MonitorNames.Read();
        return Enumerable.Range(0, Math.Max(screens.Length, settings.DisplayIndex + 1)).Select(i =>
        {
            if (i >= screens.Length) return new Choice(i, $"Display {i + 1} · not connected");
            var screen = screens[i];
            string name = names.TryGetValue(screen.DeviceName, out var n) ? n + " · " : "";
            return new Choice(i, $"Display {i + 1} · {name}{screen.Bounds.Width} × {screen.Bounds.Height}{(screen.Primary ? " · main" : "")}");
        }).ToList();
    }
}











