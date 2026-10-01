using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Flashback;

// The window's layouts. The default layout is the window as it has always been. A console layout swaps in a
// command-window look on top of it: a frameless window whose pages are tabs in a titlebar, a monospaced font,
// square controls (Themes/ConsoleStyles.xaml, merged into this window only) and a Status page. Nothing about
// the pages themselves changes, so every control works the same in each layout, and the editor window keeps
// its own look.
public partial class MainWindow
{
    private LayoutDefinition layout = LayoutCatalog.Default;
    private ResourceDictionary? consoleStyles;
    private Size? defaultSize;
    internal LayoutDefinition ActiveLayout => layout;
    internal bool ConsoleActive => layout.Console;

    private static readonly string[] LayoutBrushes = { "AppBackground", "Surface", "ControlSurface", "Outline", "TitlebarBackground", "Accent", "Ink", "Muted", "Dim", "SelectionSurface", "HoverSurface", "OnAccent" };

    // Library buttons that show an icon in the default layout and a word in a console layout, by accessible name.
    private static readonly Dictionary<string, string> ConsoleWords = new()
    {
        ["Search by game or filename"] = "[find]", ["Refresh clips"] = "[refresh]", ["Open clips folder"] = "[folder]",
        ["Play clip"] = "[play]", ["Edit, trim and combine sections"] = "[edit]", ["Show clip in folder"] = "[show]",
        ["Open in external editor"] = "[external]", ["Delete clip"] = "[delete]"
    };
    private readonly Dictionary<Button, (object? Content, double Width)> iconButtons = new();
    private (string Text, FontFamily Font, double Size)? emptyIcon;

    internal void ApplyLayout(LayoutDefinition next)
    {
        bool wasConsole = layout.Console;
        if (next.Console) ApplyConsoleLayout(next, wasConsole); else ApplyDefaultLayout(wasConsole);
        layout = next;
        UpdateNavigation(); UpdateQuickAudio(); UpdateConsoleChrome();
        if (recorder != null) UpdateConsoleStatus();
    }

    private void FreeWindowSize()
    {
        // Window.Width and Height are coerced against the current MinWidth and MinHeight, so the old limits go
        // first or a smaller layout could not shrink the window.
        MinWidth = 0; MinHeight = 0; MaxWidth = double.PositiveInfinity; MaxHeight = double.PositiveInfinity;
        if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
    }

    private void ApplyDefaultLayout(bool wasConsole)
    {
        if (consoleStyles != null) Resources.MergedDictionaries.Remove(consoleStyles);
        foreach (var key in LayoutBrushes) Resources.Remove(key);
        FreeWindowSize();
        WindowStyle = WindowStyle.SingleBorderWindow; ResizeMode = ResizeMode.CanResize;
        var size = wasConsole && defaultSize is { } kept ? kept : new Size(960, 760);
        Width = size.Width; Height = size.Height; MinWidth = 760; MinHeight = 640;
        FontFamily = new FontFamily("Segoe UI"); FontSize = 14;
        WindowOuterBorder.BorderThickness = new Thickness(0);
        ConsoleTitlebar.Visibility = Visibility.Collapsed;
        DefaultSidebar.Visibility = DefaultHeader.Visibility = DefaultPageHeader.Visibility = Visibility.Visible;
        SidebarColumn.Width = new GridLength(76);
        MainContentGrid.Margin = new Thickness(28, 24, 28, 18);
        SettingsFooter.Margin = new Thickness(174, 12, 0, 0);
        if (MainTabs.SelectedItem == StatusTab) MainTabs.SelectedItem = LibraryTab;
        SetConsoleWords(false);
    }

    private void ApplyConsoleLayout(LayoutDefinition next, bool wasConsole)
    {
        if (!wasConsole && WindowState == WindowState.Normal) defaultSize = new Size(ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height);
        consoleStyles ??= new ResourceDictionary { Source = new Uri("Themes/ConsoleStyles.xaml", UriKind.Relative) };
        if (!Resources.MergedDictionaries.Contains(consoleStyles)) Resources.MergedDictionaries.Add(consoleStyles);
        ApplyConsoleColors(next);
        FreeWindowSize();
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResizeWithGrip;
        Width = next.Width; Height = next.Height; MinWidth = next.MinWidth; MinHeight = next.MinHeight;
        FontFamily = new FontFamily(next.FontFamily); FontSize = next.FontSize;
        WindowOuterBorder.BorderThickness = new Thickness(1);
        ConsoleTitlebar.Visibility = Visibility.Visible;
        DefaultSidebar.Visibility = DefaultHeader.Visibility = DefaultPageHeader.Visibility = Visibility.Collapsed;
        SidebarColumn.Width = new GridLength(0);
        MainContentGrid.Margin = new Thickness(next.Margin, next.Margin, next.Margin, Math.Max(0, next.Margin - 1));
        SettingsFooter.Margin = new Thickness(0, 10, 0, 0);
        InternalLogBox.MinHeight = next.LogHeight;
        ConsoleTitle.Text = "flashback";
        ConsoleTabStatus.Visibility = next.Tabs.Contains("status") ? Visibility.Visible : Visibility.Collapsed;
        ConsoleTabClips.Visibility = next.Tabs.Contains("clips") ? Visibility.Visible : Visibility.Collapsed;
        ConsoleTabSettings.Visibility = Visibility.Visible;
        ConsoleTabEditor.Visibility = next.Tabs.Contains("editor") ? Visibility.Visible : Visibility.Collapsed;
        // Land on a page this layout has.
        bool onStatus = MainTabs.SelectedItem == StatusTab, onClips = MainTabs.SelectedItem == LibraryTab;
        if (!wasConsole) MainTabs.SelectedItem = next.Tabs.Contains("status") ? StatusTab : next.Tabs.Contains("clips") ? LibraryTab : MainTabs.Items[1];
        else if (onStatus && !next.Tabs.Contains("status")) MainTabs.SelectedItem = next.Tabs.Contains("clips") ? LibraryTab : MainTabs.Items[1];
        else if (onClips && !next.Tabs.Contains("clips")) MainTabs.SelectedItem = next.Tabs.Contains("status") ? StatusTab : MainTabs.Items[1];
        SetConsoleWords(true);
    }

    // The console colors for a layout go on this window only, over the default layout's brushes.
    internal void ApplyConsoleColors(LayoutDefinition target)
    {
        var map = Appearance.ConsoleColors(settings.ConsolePalette, settings.ConsoleAccent, target.Colors);
        foreach (var (key, hex) in map)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); brush.Freeze();
            Resources[key] = brush;
        }
    }

    // Icons become words in the library, and back.
    private void SetConsoleWords(bool console)
    {
        foreach (var button in ClipSearch.Parent is Panel top ? top.Children.OfType<Button>().Concat(LibraryActions.Children.OfType<Button>()) : LibraryActions.Children.OfType<Button>())
        {
            string name = AutomationProperties.GetName(button);
            if (!ConsoleWords.TryGetValue(name, out var word)) continue;
            if (console)
            {
                if (!iconButtons.ContainsKey(button)) iconButtons[button] = (button.Content, button.Width);
                button.Content = word; button.SetResourceReference(StyleProperty, "TextButton"); button.Width = double.NaN;
            }
            else if (iconButtons.TryGetValue(button, out var original))
            {
                button.Content = original.Content; button.SetResourceReference(StyleProperty, "IconButton"); button.ClearValue(WidthProperty);
                iconButtons.Remove(button);
            }
        }
        if (LibraryEmpty.Children.OfType<TextBlock>().FirstOrDefault() is { } icon)
        {
            if (console) { emptyIcon ??= (icon.Text, icon.FontFamily, icon.FontSize); icon.Text = "[ no clips ]"; icon.FontFamily = FontFamily; icon.FontSize = 16; }
            else if (emptyIcon is { } kept) { icon.Text = kept.Text; icon.FontFamily = kept.Font; icon.FontSize = kept.Size; }
        }
    }

    // Titlebar tabs: the page each opens, and the current one drawn in the accent color.
    private void ConsoleTab_Click(object sender, RoutedEventArgs e)
    {
        switch ((string)((Button)sender).Tag)
        {
            case "status": MainTabs.SelectedItem = StatusTab; break;
            case "clips": MainTabs.SelectedItem = LibraryTab; break;
            case "settings": MainTabs.SelectedIndex = 1; break;
            case "editor": OpenTrimmer(); break;
        }
    }

    private void UpdateConsoleChrome()
    {
        if (ConsoleTabStatus == null) return;
        var current = MainTabs.SelectedItem == StatusTab ? ConsoleTabStatus : MainTabs.SelectedItem == LibraryTab ? ConsoleTabClips : MainTabs.SelectedIndex == 1 ? ConsoleTabSettings : null;
        foreach (var button in new[] { ConsoleTabStatus, ConsoleTabClips, ConsoleTabSettings, ConsoleTabEditor })
        {
            bool on = ReferenceEquals(button, current);
            if (on) { button.SetResourceReference(BackgroundProperty, "Accent"); button.SetResourceReference(ForegroundProperty, "OnAccent"); }
            else { button.Background = Brushes.Transparent; button.SetResourceReference(ForegroundProperty, "Muted"); }
            AutomationProperties.SetItemStatus(button, on ? "Selected" : "");
        }
    }

    private void Titlebar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch (InvalidOperationException) { } }
    }
    private void ConsoleMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    // Closing sends the window to the tray, the same as the default layout's close button.
    private void ConsoleClose_Click(object sender, RoutedEventArgs e) => Close();

    private string stemsShown = "";
    // The Status page: what is being recorded, with what, and the buffer filling, in plain text.
    private void UpdateConsoleStatus()
    {
        if (!layout.Console || StatusVideoText == null) return;
        bool active = recorder.IsRecording, recovering = recovery.IsRunning, starting = busy && recordingRequested && !active && !recovering;
        StatusVideoText.Text = HeaderCapture.Text;
        StatusTriggerText.Text = settings.Hotkey.Replace("+", " + ") + "  (pause " + settings.PauseHotkey.Replace("+", " + ") + ")";
        StatusEncoderText.Text = HeaderVersion.Text;
        int total = settings.ReplaySeconds, held = (int)Math.Round(Math.Min(total, recorder.BufferedSeconds));
        StatusBufferLabel.Text = $"buffer ({total}s)";
        if (active)
        {
            const int width = 30; int filled = Math.Clamp((int)Math.Round((double)held / Math.Max(1, total) * width), 0, width);
            StatusBufferBar.Text = $"[{new string('=', filled)}{new string('.', width - filled)}] {held}s / {total}s";
            StatusToggleButton.Content = "[ pause buffer ]"; StatusSaveButton.IsEnabled = recordingRequested;
        }
        else
        {
            StatusBufferBar.Text = recovering ? "[ RECOVERING ]" : starting ? "[ STARTING ]" : "[ STOPPED ]";
            StatusToggleButton.Content = recovering ? "[ pause buffer ]" : "[ start buffer ]"; StatusSaveButton.IsEnabled = recovering && recordingRequested;
        }
        StatusToggleButton.IsEnabled = ToggleButton.IsEnabled;
        StatusOutputPath.Text = "out: " + settings.OutputFolder; StatusOutputPath.ToolTip = settings.OutputFolder;
        UpdateConsoleStems(active);
        InternalLogBox.Text = string.Join(Environment.NewLine, messageLog.Reverse().Take(30));
        ConsoleUpdateButton.Visibility = UpdateButton.Visibility; ConsoleUpdateButton.IsEnabled = UpdateButton.IsEnabled;
        ConsoleUpdateButton.ToolTip = UpdateButton.ToolTip;
    }

    // One line per audio source: its state and its device. (No meters: the recorder doesn't measure levels.)
    private void UpdateConsoleStems(bool active)
    {
        string Device(ComboBox box, string fallback) => (box.SelectedItem as PlaybackDevice)?.Name is { Length: > 0 } n ? n : fallback;
        var rows = new List<(string Tag, string State, string Device, bool Off)>();
        if (settings.DesktopAudio) rows.Add(("desk", settings.DesktopMuted ? "muted" : active ? "rec" : "standby", Device(AudioDeviceBox, "Windows default"), settings.DesktopMuted));
        if (settings.MicrophoneAudio) rows.Add(("mic", settings.MicrophoneMuted ? "muted" : active ? "rec" : "standby", Device(MicrophoneDeviceBox, "Windows default"), settings.MicrophoneMuted));
        StatusAudioText.Text = rows.Count == 0 ? "off" : string.Join(" + ", rows.Select(r => r.Tag));
        string key = string.Join("|", rows.Select(r => string.Join("/", r.Tag, r.State, r.Device)));
        if (key == stemsShown) return;
        stemsShown = key; AudioStemsPanel.Children.Clear();
        if (rows.Count == 0) { AudioStemsPanel.Children.Add(Text("no audio is recorded", "Dim")); return; }
        foreach (var (tag, state, device, off) in rows)
        {
            var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
            var name = Text(tag.PadRight(5), "Dim"); name.Width = 52; DockPanel.SetDock(name, Dock.Left);
            var st = Text(state.ToUpperInvariant().PadRight(9), off ? "Dim" : state == "rec" ? "Accent" : "Ink"); st.Width = 78; DockPanel.SetDock(st, Dock.Left);
            var dev = Text(device, "Dim"); dev.TextTrimming = TextTrimming.CharacterEllipsis; dev.ToolTip = device;
            row.Children.Add(name); row.Children.Add(st); row.Children.Add(dev); AudioStemsPanel.Children.Add(row);
        }
    }
    private static TextBlock Text(string text, string brush)
    {
        var block = new TextBlock { Text = text };
        block.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return block;
    }
}
