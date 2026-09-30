using System;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace Flashback;

public partial class MainWindow
{
    // Settings save by themselves a moment after a change, so a switch that shows on is on. The wait lets a
    // slider drag or a few clicks in a row land as one save (and one buffer restart when recording needs it).
    // Text boxes save when you leave them, so a half-typed folder is never created.
    private DispatcherTimer? autoSave;
    private void HookAutoSave()
    {
        autoSave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        autoSave.Tick += async (_, _) =>
        {
            // A save or a clip in progress, or a shortcut half entered: try again shortly.
            if (busy || recorder.IsSaving || shortcutCapture != null) return;
            autoSave.Stop();
            await SaveSettingsAsync(true);
        };
        SettingsPanel.AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => SaveSoon()));
        SettingsPanel.AddHandler(System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent, new RoutedEventHandler((_, _) => SaveSoon()));
        // The side tabs are a selector too; only the drop-downs count.
        SettingsPanel.AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, e) => { if (e.OriginalSource is ComboBox) SaveSoon(); }));
        SettingsPanel.AddHandler(RangeBase.ValueChangedEvent, new RoutedPropertyChangedEventHandler<double>((_, _) => SaveSoon()));
        SettingsPanel.AddHandler(UIElement.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((_, e) => { if (e.OriginalSource is TextBox box && !IsShortcutBox(box)) SaveSoon(); }));
        // Browse fills the folder box without focusing it.
        SettingsPanel.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, e) => { if (e.OriginalSource is TextBox box && !box.IsKeyboardFocusWithin && !IsShortcutBox(box)) SaveSoon(); }));
    }
    private void SaveSoon()
    {
        if (autoSave == null || quitting) return;
        autoSave.Stop(); autoSave.Start();
    }
    private static bool Unchanged(Settings a, Settings b) => JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);
}
