using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace Flashback;

// --layout-test: layouts and the layout importer. Files are read and refused the way they should be, links are
// only taken from GitHub, the console layouts apply and the default layout comes back exactly, and the choice is
// kept in the settings. Writes layout-results.txt.
internal static class LayoutDiagnostics
{
    internal static async Task RunAsync()
    {
        Directory.CreateDirectory(Storage.Root);
        var report = new StringBuilder(); int failures = 0;
        void Check(bool ok, string text) { report.AppendLine((ok ? "PASS " : "FAIL ") + text); if (!ok) failures++; File.WriteAllText(Path.Combine(Storage.Root, "layout-results.txt"), report.ToString()); }
        Directory.CreateDirectory(LayoutCatalog.Folder);
        foreach (var old in Directory.GetFiles(LayoutCatalog.Folder)) File.Delete(old);

        // ---- Reading layout files ----
        var minimal = LayoutParser.Parse("{ \"name\": \"Mini\" }");
        Check(minimal.Layout is { Name: "Mini", Width: 480, Height: 530, FontSize: 11 } && minimal.Layout.Tabs.Length == 4, "A layout with only a name gets the compact console values");
        var full = LayoutParser.Parse("""
            { "name": "Streamer Compact", "author": "someone", "baseStyle": "Clipper",
              "window": { "width": 500, "height": 550, "minWidth": 440, "minHeight": 480, "frameless": true },
              "typography": { "fontFamily": "Consolas", "fontSize": 13 }, "spacing": { "margin": 12, "logHeight": 90 },
              "navigation": { "type": "TopBar", "tabs": ["clips", "settings", "editor"] },
              "theme": { "palette": "matrix", "accent": "GREEN", "colors": { "background": "#101010", "Accent": "#ff8800", "nonsense": "#000000" } } }
            """);
        var f = full.Layout;
        Check(f != null && f.Width == 500 && f.Height == 550 && f.FontSize == 13 && f.Margin == 12 && f.LogHeight == 90 && f.Palette == "Matrix" && f.Accent == "Green"
            && f.Tabs.SequenceEqual(new[] { "clips", "settings", "editor" }) && f.Colors["background"] == "#101010" && f.Colors["accent"] == "#FF8800" && f.Colors.Count == 2 && f.Author == "someone",
            $"A full layout is read in every part ({full.Error})");
        Check(full.Notes.Any(n => n.Contains("nonsense")), "A color the app doesn't use is ignored with a note");
        var clamped = LayoutParser.Parse("{ \"name\": \"Big\", \"window\": { \"width\": 99999, \"height\": 5 }, \"typography\": { \"fontSize\": 99 } }");
        Check(clamped.Layout is { Width: 1400, Height: 380, FontSize: 18 } && clamped.Notes.Count >= 3, "Sizes outside the limits are held to them, with notes");
        foreach (var (json, expect, label) in new[]
        {
            ("{ not json", "JSON", "invalid JSON"), ("[1,2]", "one JSON object", "a list instead of an object"), ("{ \"author\": \"x\" }", "\"name\"", "a missing name"),
            ("{ \"name\": \"Console (Compact)\" }", "built-in", "a built-in layout's name"), ("{ \"name\": \"A\", \"baseStyle\": \"classic\" }", "baseStyle", "a base style that isn't console"),
            ("{ \"name\": \"A\", \"theme\": { \"palette\": \"Neon\" } }", "palette", "an unknown palette"), ("{ \"name\": \"A\", \"theme\": { \"accent\": \"Pink\" } }", "accent", "an unknown accent"),
            ("{ \"name\": \"A\", \"theme\": { \"colors\": { \"accent\": \"red\" } } }", "#", "a color that isn't hex"), ("{ \"name\": \"A\", \"navigation\": { \"tabs\": [\"clips\"] } }", "settings", "tabs without settings"),
            ("{ \"name\": \"A\", \"navigation\": { \"tabs\": [\"settings\", \"admin\"] } }", "only", "a tab that doesn't exist"), ("{ \"name\": \"A\", \"typography\": { \"fontFamily\": \"C:\\\\x\" } }", "font", "a font that is a path"),
            ("{ \"name\": \"A\", \"window\": { \"width\": \"wide\" } }", "number", "a size that isn't a number"), ("{ \"name\": \"A\\u0001\" }", "characters", "a name with control characters")
        })
        {
            var r = LayoutParser.Parse(json);
            Check(r.Layout == null && r.Error != null && r.Error.Contains(expect, StringComparison.OrdinalIgnoreCase), $"Refused: {label} ({r.Error})");
        }
        var huge = Path.Combine(Storage.Root, "huge.json"); File.WriteAllText(huge, "{ \"name\": \"Huge\", \"author\": \"" + new string('x', LayoutParser.MaxBytes) + "\" }");
        Check(LayoutParser.ReadFile(huge).Layout == null, "A file larger than the limit is refused");
        File.Delete(huge);
        Check(LayoutParser.ReadFile(Path.Combine(Storage.Root, "missing.json")).Layout == null, "A missing file is refused without an exception");

        // ---- Links ----
        string? Raw(string u) => LayoutDownloader.Resolve(u)?.ToString();
        Check(Raw("https://github.com/someone/layouts/blob/main/neon/Neon.json") == "https://raw.githubusercontent.com/someone/layouts/main/neon/Neon.json", "A GitHub file page link becomes its raw link");
        Check(Raw("https://raw.githubusercontent.com/someone/layouts/main/a.json") == "https://raw.githubusercontent.com/someone/layouts/main/a.json" && Raw("https://gist.githubusercontent.com/u/abc/raw/a.json") != null, "Raw and gist links are kept");
        Check(new[] { "http://raw.githubusercontent.com/a/b/main/c.json", "https://example.com/c.json", "https://github.com.evil.com/a/b/blob/main/c.json", "file:///C:/x.json", "ftp://github.com/a", "https://user:pw@github.com/a/b/blob/main/c.json", "https://github.com/someone/layouts", "not a link", "" }.All(u => Raw(u) == null),
            "Anything but https links to GitHub's own hosts is refused");

        // ---- Template and folder ----
        var template = LayoutCatalog.Compact with { Name = "Roundtrip", Author = "me", Palette = "Gruvbox", Accent = "Amber", Tabs = new[] { "status", "settings" } };
        var back = LayoutParser.Parse(LayoutCatalog.ToJson(template)).Layout;
        Check(back != null && back.Name == "Roundtrip" && back.Palette == "Gruvbox" && back.Accent == "Amber" && back.Tabs.SequenceEqual(template.Tabs) && back.Width == template.Width && back.FontFamily == "Consolas", "A saved template reads back as the same layout");
        LayoutCatalog.Save(back!, LayoutCatalog.ToJson(template));
        File.WriteAllText(Path.Combine(LayoutCatalog.Folder, "broken.json"), "{ \"name\": ");
        File.WriteAllText(Path.Combine(LayoutCatalog.Folder, "zdupe.json"), "{ \"name\": \"roundtrip\" }");
        var problems = new List<string>(); var listed = LayoutCatalog.Load(problems);
        Check(listed.Count == 4 && listed.Any(l => l.Name == "Roundtrip" && !l.BuiltIn) && problems.Count == 2 && problems.Any(p => p.StartsWith("broken.json")), $"The folder's layouts join the built-in ones, and files that can't be used are reported ({problems.Count} problems)");
        Check(LayoutCatalog.Find("no such layout").Name == LayoutCatalog.DefaultName && LayoutCatalog.Clean("") == LayoutCatalog.DefaultName && LayoutCatalog.Clean(null) == LayoutCatalog.DefaultName, "An unknown or empty layout name means the default layout");

        // ---- Settings ----
        var saved = new Settings { Layout = "Roundtrip", ConsolePalette = "Dracula", ConsoleAccent = "Rose" };
        Storage.Save(saved); var loaded = Storage.Load(out _);
        Check(loaded.Layout == "Roundtrip" && loaded.ConsolePalette == "Dracula" && loaded.ConsoleAccent == "Rose" && new Settings().Layout == LayoutCatalog.DefaultName, "The layout and console colors are kept in the settings, and the default layout is the default");
        var damaged = new Settings { Layout = "\u0007bad", ConsolePalette = "Nope", ConsoleAccent = null! }; damaged.Repair();
        Check(damaged.Layout == LayoutCatalog.DefaultName && damaged.ConsolePalette == "Monochrome" && damaged.ConsoleAccent == "White", "A damaged layout setting is put back to the defaults");

        // ---- The window ----
        Storage.Save(new Settings { OutputFolder = Path.Combine(Storage.Root, "clips") });
        var window = new MainWindow(true) { ShowInTaskbar = false, Left = -4000, Top = 0 };
        window.Show(); await Task.Delay(300); window.UpdateLayout();
        double startWidth = window.Width, startHeight = window.Height;
        Check(!window.ConsoleActive && window.ActiveLayout.Name == LayoutCatalog.DefaultName && window.WindowStyle == WindowStyle.SingleBorderWindow && window.ConsoleTitlebar.Visibility == Visibility.Collapsed && window.DefaultSidebar.Visibility == Visibility.Visible, "The window opens in the default layout");
        window.ChooseLayout(LayoutCatalog.Compact); await Task.Delay(300); window.UpdateLayout();
        Check(window.ConsoleActive && window.WindowStyle == WindowStyle.None && Math.Abs(window.Width - 480) < 1 && Math.Abs(window.Height - 530) < 1 && window.MinWidth == 440 && window.ConsoleTitlebar.Visibility == Visibility.Visible
            && window.DefaultSidebar.Visibility == Visibility.Collapsed && window.FontFamily.Source == "Consolas" && window.MainTabs.SelectedItem == window.StatusTab, "Console (Compact) is a 480 x 530 frameless window with its titlebar tabs, opening on Status");
        Check(window.Resources.MergedDictionaries.Count == 1 && window.Resources["AppBackground"] is SolidColorBrush && Application.Current.Resources["AppBackground"] is SolidColorBrush app && app.Color != ((SolidColorBrush)window.Resources["AppBackground"]).Color, "The console colors are on this window only");
        Check(window.StatusVideoText.Text.Length > 0 && window.StatusBufferBar.Text.Contains("STOPPED") && !window.StatusSaveButton.IsEnabled && window.StatusToggleButton.Content as string == "[ start buffer ]", "The Status page shows the capture and a stopped buffer");
        window.ConsoleTabSettings.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Check(window.MainTabs.SelectedIndex == 1, "The settings tab in the titlebar opens Settings");
        window.ConsoleTabClips.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Check(window.MainTabs.SelectedItem == window.LibraryTab, "The clips tab in the titlebar opens the clip list");
        window.ChooseLayout(LayoutCatalog.Spacious); await Task.Delay(300); window.UpdateLayout();
        Check(Math.Abs(window.Width - 580) < 1 && Math.Abs(window.Height - 640) < 1 && window.FontSize == 12, "Console (Spacious) is a 580 x 640 window");
        var custom = LayoutParser.Parse("{ \"name\": \"Custom\", \"window\": { \"width\": 640, \"height\": 600 }, \"navigation\": { \"tabs\": [\"clips\", \"settings\"] }, \"theme\": { \"colors\": { \"background\": \"#102030\", \"accent\": \"#FF8800\" } } }").Layout!;
        window.ChooseLayout(custom); await Task.Delay(300); window.UpdateLayout();
        Check(Math.Abs(window.Width - 640) < 1 && window.ConsoleTabStatus.Visibility == Visibility.Collapsed && window.ConsoleTabEditor.Visibility == Visibility.Collapsed && window.MainTabs.SelectedItem != window.StatusTab
            && ((SolidColorBrush)window.Resources["AppBackground"]).Color == Color.FromRgb(0x10, 0x20, 0x30) && ((SolidColorBrush)window.Resources["Accent"]).Color == Color.FromRgb(0xFF, 0x88, 0x00), "A custom layout sets its own size, tabs and colors, and leaves Status when it has no Status tab");
        window.ChooseLayout(LayoutCatalog.Default); await Task.Delay(300); window.UpdateLayout();
        Check(!window.ConsoleActive && window.WindowStyle == WindowStyle.SingleBorderWindow && Math.Abs(window.Width - startWidth) < 1 && Math.Abs(window.Height - startHeight) < 1 && window.MinWidth == 760 && window.MinHeight == 640
            && window.DefaultSidebar.Visibility == Visibility.Visible && window.DefaultHeader.Visibility == Visibility.Visible && window.DefaultPageHeader.Visibility == Visibility.Visible && window.ConsoleTitlebar.Visibility == Visibility.Collapsed
            && window.Resources.MergedDictionaries.Count == 0 && !window.Resources.Contains("AppBackground") && window.FontFamily.Source == "Segoe UI" && window.FontSize == 14 && window.MainTabs.SelectedItem != window.StatusTab, "Back to the default layout, the window is as it was");
        var copy = (Settings)typeof(MainWindow).GetMethod("ReadControls", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(window, null)!;
        Check(copy.Layout == LayoutCatalog.DefaultName, "Saving the settings page keeps the layout choice");
        window.ChooseLayout(LayoutCatalog.Compact);
        copy = (Settings)typeof(MainWindow).GetMethod("ReadControls", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(window, null)!;
        Check(copy.Layout == LayoutCatalog.CompactName && Storage.Load(out _).Layout == LayoutCatalog.CompactName, "Choosing a layout saves it, and the settings page keeps it");
        window.Close();
        if (failures > 0) throw new Exception($"{failures} layout checks failed; see layout-results.txt");
    }
}
