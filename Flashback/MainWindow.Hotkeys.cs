using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace Flashback;

public partial class MainWindow
{
    // Each trimmer field shows a friendly key name; the stored binding lives here.
    private readonly Dictionary<TextBox, TrimAction> trimBoxes = new();
    private readonly Dictionary<TrimAction, string> trimBindings = new();
    private string trimBindingBeforeEdit = "";

    private void LoadTrimShortcuts(Settings source)
    {
        if (trimBoxes.Count == 0)
            foreach (var group in TrimShortcuts.All.GroupBy(s => s.Group))
            {
                var heading = new TextBlock { Text = group.Key, Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 10, 0, 6) };
                TrimShortcutRows.Children.Add(heading);
                foreach (var shortcut in group)
                {
                    var box = new TextBox { Style = (Style)FindResource("ShortcutBox") };
                    AutomationProperties.SetName(box, shortcut.Label + " shortcut");
                    box.GotKeyboardFocus += Hotkey_GotFocus; box.LostKeyboardFocus += Hotkey_LostFocus; box.PreviewKeyDown += Hotkey_KeyDown;
                    var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
                    DockPanel.SetDock(box, Dock.Right); row.Children.Add(box);
                    row.Children.Add(new TextBlock { Text = shortcut.Label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                    TrimShortcutRows.Children.Add(row); trimBoxes[box] = shortcut.Action;
                }
            }
        foreach (var (action, binding) in TrimShortcuts.Resolve(source)) trimBindings[action] = binding;
        foreach (var (box, action) in trimBoxes) box.Text = TrimShortcuts.Display(trimBindings[action]);
    }
    private void ReadTrimShortcuts(Settings target)
    {
        target.TrimAddHotkey = trimBindings[TrimAction.AddSection];
        target.TrimKeys = trimBindings.Where(b => b.Key != TrimAction.AddSection && b.Value != TrimShortcuts.Info(b.Key).Default)
            .ToDictionary(b => b.Key.ToString(), b => b.Value);
    }
    private void ResetTrimKeys_Click(object sender, RoutedEventArgs e)
    {
        LoadTrimShortcuts(new Settings { Hotkey = settings.Hotkey, PauseHotkey = settings.PauseHotkey });
        Tell("Trimmer keys reset to their defaults. Click Apply settings to keep them.");
    }
    private bool IsShortcutBox(TextBox box) => ReferenceEquals(box, HotkeyBox) || ReferenceEquals(box, PauseHotkeyBox) || trimBoxes.ContainsKey(box);
    private void SetShortcut(TextBox box, Key key, ModifierKeys mods, bool finished)
    {
        if (trimBoxes.TryGetValue(box, out var action))
        {
            trimBindings[action] = TrimShortcuts.Format(key, mods);
            box.Text = TrimShortcuts.Display(trimBindings[action]);
        }
        else box.Text = (mods.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "") + (mods.HasFlag(ModifierKeys.Alt) ? "Alt+" : "") + (mods.HasFlag(ModifierKeys.Shift) ? "Shift+" : "") + key;
        if (!finished) return;
        Keyboard.Focus(ApplyButton);
        try
        {
            var draft = new Settings { Hotkey = HotkeyBox.Text, PauseHotkey = PauseHotkeyBox.Text }; ReadTrimShortcuts(draft);
            TrimShortcuts.Validate(draft); Tell("Shortcut selected. Click Apply settings to use it.");
        }
        catch (System.ArgumentException ex) { Tell(ex.Message, true); }
    }
    private void CancelShortcut(TextBox box)
    {
        if (trimBoxes.TryGetValue(box, out var action)) { trimBindings[action] = trimBindingBeforeEdit; box.Text = TrimShortcuts.Display(trimBindingBeforeEdit); }
        else box.Text = shortcutBeforeEdit;
    }
}
