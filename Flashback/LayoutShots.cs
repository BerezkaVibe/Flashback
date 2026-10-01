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
