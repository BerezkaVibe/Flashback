using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Flashback;

// --layout-shots: each layout's pages drawn to PNG files (layout-<layout>-<page>.png) in the data folder, to
// look at. FLASHBACK_LAYOUT_FILE adds a layout file to the set.
internal static class LayoutShots
{
    internal static async Task RunAsync()
    {
        Directory.CreateDirectory(Storage.Root);
        var layouts = LayoutCatalog.BuiltIns.ToList();
        if (Environment.GetEnvironmentVariable("FLASHBACK_LAYOUT_FILE") is { Length: > 0 } file && LayoutParser.ReadFile(file).Layout is { } custom) layouts.Add(custom);
        foreach (var layout in layouts)
        {
            var window = new MainWindow(true) { ShowInTaskbar = false, Left = -4000, Top = 0 };
            window.Show();
            window.ChooseLayout(layout); await Task.Delay(500); window.UpdateLayout();
            string slug = new string(layout.Name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
            foreach (var (name, index, page) in new[] { ("status", 2, ""), ("clips", 0, ""), ("settings-video", 1, "RecordingSettings"), ("settings-audio", 1, "AudioSettings"), ("settings-notifications", 1, "Notifications"), ("settings-appearance", 1, "AppearanceSettings") })
            {
                if (!layout.Console && index == 2) continue;
                window.MainTabs.SelectedIndex = index;
                if (page.Length > 0) { var tab = window.FindName(page) as TabItem ?? window.SettingsPanel.Items.OfType<TabItem>().FirstOrDefault(t => (t.Header as string) == page); if (tab != null) tab.IsSelected = true; }
                await Task.Delay(300); window.UpdateLayout();
                var root = (FrameworkElement)window.Content;
                int w = (int)Math.Ceiling(window.ActualWidth), h = (int)Math.Ceiling(window.ActualHeight);
                var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(Storage.Root, $"layout-{slug}-{name}.png")); png.Save(stream);
            }
            window.Close();
        }
    }
}

// --palette-sheet: the default layout's Appearance page in a set of palette and accent pairs, side by side
// (palette-sheet.png), and the same for the console layout (palette-sheet-console.png).
internal static class PaletteSheet
{
    internal static async Task RunAsync()
    {
        Directory.CreateDirectory(Storage.Root);
        await Sheet(false, new[] { ("Charcoal", "Mint"), ("Midnight", "Lavender"), ("Ocean", "Sky"), ("Teal", "Aqua"), ("Forest", "Neon Green"), ("Violet", "Violet"), ("Plum", "Hot Pink"), ("Crimson", "Red"), ("Ember", "Orange"), ("Synthwave", "Magenta"), ("Nord", "Electric Blue"), ("Cocoa", "Gold") }, "palette-sheet.png");
        await Sheet(true, new[] { ("Monochrome", "White"), ("Matrix", "Green"), ("Dracula", "Purple"), ("Ocean", "Sky"), ("Forest", "Neon Green"), ("Plum", "Hot Pink"), ("Crimson", "Red"), ("Ember", "Orange"), ("Synthwave", "Magenta"), ("Nord", "Electric Blue"), ("Cocoa", "Gold"), ("Teal", "Aqua") }, "palette-sheet-console.png");
    }
    private static async Task Sheet(bool console, (string Palette, string Accent)[] pairs, string file)
    {
        var window = new MainWindow(true) { ShowInTaskbar = false, Left = -4000, Top = 0 };
        window.Show(); window.ChooseLayout(console ? LayoutCatalog.Compact : LayoutCatalog.Default); await Task.Delay(400);
        window.MainTabs.SelectedIndex = 1; window.AppearanceSettings.IsSelected = true;
        var tiles = new System.Collections.Generic.List<BitmapSource>(); const double scale = .5;
        foreach (var (palette, accent) in pairs)
        {
            if (console) { window.PaletteBox.SelectedValue = palette; window.AccentBox.SelectedValue = accent; } else { Appearance.Apply(palette, accent); }
            await Task.Delay(250); window.UpdateLayout();
            int w = (int)Math.Ceiling(window.ActualWidth), h = (int)Math.Ceiling(window.ActualHeight);
            var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32); bitmap.Render((FrameworkElement)window.Content);
            tiles.Add(new TransformedBitmap(bitmap, new ScaleTransform(console ? 1 : scale, console ? 1 : scale)));
            tiles[^1].Freeze();
        }
        int columns = console ? 4 : 3, rows = (tiles.Count + columns - 1) / columns, tw = tiles.Max(t => t.PixelWidth), th = tiles.Max(t => t.PixelHeight);
        var sheet = new DrawingVisual();
        using (var dc = sheet.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, columns * (tw + 8) + 8, rows * (th + 8) + 8));
            for (int i = 0; i < tiles.Count; i++) dc.DrawImage(tiles[i], new Rect(8 + i % columns * (tw + 8), 8 + i / columns * (th + 8), tiles[i].PixelWidth, tiles[i].PixelHeight));
        }
        var result = new RenderTargetBitmap(columns * (tw + 8) + 8, rows * (th + 8) + 8, 96, 96, PixelFormats.Pbgra32); result.Render(sheet);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(result));
        using (var stream = File.Create(Path.Combine(Storage.Root, file))) png.Save(stream);
        window.Close();
    }
}
