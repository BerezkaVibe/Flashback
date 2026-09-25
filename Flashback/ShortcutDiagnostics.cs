using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace Flashback;

internal static class ShortcutDiagnostics
{
    internal static async Task RunAsync(bool controlled = false)
    {
        Directory.CreateDirectory(Storage.Root);
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception(message);
            File.AppendAllText(Path.Combine(Storage.Root, "shortcut-results.txt"), "PASS " + message + Environment.NewLine);
        }
        var original = NativeOverlay.GetForegroundWindow();
        Storage.Save(new Settings { DesktopAudio = false, FrameRate = 30, ReplaySeconds = 30, OutputFolder = Path.Combine(Storage.Root, "clips"), OverlayEnabled = false });
        var window = new MainWindow(renderOnly: true, syntheticCapture: true);
        try
        {
            // Settings opens on Video; the shortcut fields are in its Hotkeys section.
            window.Show(); window.MainTabs.SelectedIndex = 1; window.HotkeySettings.IsSelected = true; window.UpdateLayout();
            var handle = new WindowInteropHelper(window).Handle;
            SystemTestNative.ShowWindow(handle, 3); SystemTestNative.SetForegroundWindow(handle); window.Activate();
            await Task.Delay(300);
            using (var conflict = new Hotkeys(allowShared: false))
            {
                int triggered = 0; conflict.Register("F8"); conflict.Pressed += () => triggered++;
                window.Topmost = true; SystemTestNative.SetForegroundWindow(handle); window.Activate(); await Task.Delay(150);
                Keyboard.Focus(window.HotkeyBox); await Task.Delay(150);
                if (!controlled) {
                var fieldPoint = window.HotkeyBox.PointToScreen(new Point(12, 12));
                SystemTestNative.GetCursorPos(out var oldCursor);
                try { SystemTestNative.SetCursorPos((int)fieldPoint.X, (int)fieldPoint.Y); SystemTestNative.Click(); await Task.Delay(150); } finally { SystemTestNative.SetCursorPos(oldCursor.X, oldCursor.Y); }
                }
                Check(window.CapturingShortcutForTest && window.HotkeyBox.IsKeyboardFocusWithin, "Native function-key entry starts only when a shortcut field is focused");
                if (controlled) { window.FeedShortcutForTest(0x77, true); window.FeedShortcutForTest(0x77, false); } else SystemTestNative.SingleKey(0x77); await Task.Delay(200);
                Check(window.HotkeyBox.Text == "F8", $"Pressing F8 enters F8 even when a registered global shortcut would consume the normal key message (text={window.HotkeyBox.Text}, active={window.IsActive}, foreground={NativeOverlay.GetForegroundWindow()==handle}, actualHandle={NativeOverlay.GetForegroundWindow()}, expectedHandle={handle}, triggered={triggered}, capture={window.CapturingShortcutForTest}, nativeEvents={window.FunctionEventsForTest})");
                Check(triggered == 0 && !window.CapturingShortcutForTest, "Editing suppresses the old global action and releases the temporary input handler after key-up");
                window.ApplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(window.UsingSharedHotkeysForTest && Storage.Load(out _).Hotkey == "F8", "Apply accepts F8 through background shared input while another app owns its registration");
            }
            Keyboard.Focus(window.HotkeyBox); await Task.Delay(100);
            if (controlled) { window.FeedShortcutForTest(0x77, true); window.FeedShortcutForTest(0x77, false); } else SystemTestNative.SingleKey(0x77); await Task.Delay(150);
            window.ApplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Storage.Load(out _).Hotkey == "F8", "Press-to-set F8 applies and persists once the external conflict is released");
            Keyboard.Focus(window.PauseHotkeyBox); await Task.Delay(100);
            if (controlled) { foreach (uint key in new uint[] { 0xA0, 0xA2, 0xA4, 0x78 }) window.FeedShortcutForTest(key, true); foreach (uint key in new uint[] { 0x78, 0xA4, 0xA2, 0xA0 }) window.FeedShortcutForTest(key, false); } else SystemTestNative.Chord(0x78); await Task.Delay(200);
            Check(window.PauseHotkeyBox.Text == "Ctrl+Alt+Shift+F9", "Function-key entry preserves Ctrl Alt and Shift modifiers");
            window.ApplyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Keyboard.Focus(window.HotkeyBox); await Task.Delay(100);
            if (controlled) window.HotkeyBox.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent }); else SystemTestNative.SingleKey(0x1B); await Task.Delay(150);
            Check(window.HotkeyBox.Text == "F8" && !window.CapturingShortcutForTest, "Escape cancels entry and removes the native handler");
            Keyboard.Focus(window.HotkeyBox); await Task.Delay(100);
            var other = new Window { Title = "Flashback focus test", Width = 200, Height = 100, Content = new TextBox() };
            try
            {
                other.Show(); other.Activate(); SystemTestNative.SetForegroundWindow(new WindowInteropHelper(other).Handle); await Task.Delay(200);
                Check(!window.CapturingShortcutForTest, "The native entry handler is removed when another window gains focus");
            }
            finally { other.Close(); }
            window.Activate(); SystemTestNative.SetForegroundWindow(handle); Keyboard.Focus(window.ApplyButton); await Task.Delay(150);
            window.ToggleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var wait = Stopwatch.StartNew();
            while (!window.TopSaveButton.IsEnabled && wait.Elapsed.TotalSeconds < 15) await Task.Delay(100);
            Check(window.TopSaveButton.IsEnabled && !window.CapturingShortcutForTest, "Background recording uses normal hotkeys without the entry handler");
            if (controlled) window.TopSaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); else SystemTestNative.SingleKey(0x77);
            wait.Restart();
            while ((!Directory.GetFiles(Storage.Load(out _).OutputFolder, "*.mp4", SearchOption.AllDirectories).Any() || !window.TopSaveButton.IsEnabled) && wait.Elapsed.TotalSeconds < 15) await Task.Delay(100);
            Check(Directory.GetFiles(Storage.Load(out _).OutputFolder, "*.mp4", SearchOption.AllDirectories).Length == 1, controlled ? "Save button exports after controlled key assignment; physical F8 delivery requires an interactive desktop" : "F8 saves a real synthetic replay after direct keyboard assignment");
        }
        finally { await window.QuitAsync(); if (original != IntPtr.Zero) SystemTestNative.SetForegroundWindow(original); }
    }
}
