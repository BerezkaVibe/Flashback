using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Flashback;

// Settings > Appearance: choosing a layout, and bringing in layouts other people made.
public partial class MainWindow
{
    private List<LayoutDefinition> layoutList = LayoutCatalog.Load();
    private List<string> layoutProblems = new();
    private bool layoutBusy;

    private void ShowLayoutStatus(string text, bool error = false, bool good = false)
    {
        LayoutStatus.Text = text; LayoutStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        if (error) LayoutStatus.Foreground = new SolidColorBrush(Color.FromRgb(244, 154, 154));
        else if (good) LayoutStatus.Foreground = new SolidColorBrush(Color.FromRgb(117, 212, 163));
        else LayoutStatus.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
    }

    private void ShowLayoutNote(LayoutDefinition shown)
    {
        LayoutNote.Text = shown.Name == LayoutCatalog.DefaultName ? "The window as Flashback has always been: a sidebar, clips and settings."
            : shown.BuiltIn ? "A command-window look: a frameless window with the pages as tabs in the titlebar, a monospaced font and a Status page."
            : $"A custom layout{(shown.Author.Length > 0 ? " by " + shown.Author : "")}, from {Path.GetFileName(shown.Path)}."
              + (shown.HasColors ? " It sets its own colors, so the palette and accent below don't change it." : "");
        RemoveButtonState();
    }
    private void RemoveButtonState() => LayoutRemoveButton.IsEnabled = !layout.BuiltIn;

    // Layout files can be added in the folder by hand, so the list is read again whenever it is opened.
    private void RescanLayouts()
    {
        var current = layout.Name;
        var fresh = LayoutCatalog.Load(layoutProblems = new());
        if (fresh.Select(l => l.Name).SequenceEqual(layoutList.Select(l => l.Name))) { layoutList = fresh; return; }
        layoutList = fresh;
        bool was = appearanceReady; appearanceReady = false;
        LayoutBox.ItemsSource = layoutList.Select(l => l.Name).ToList(); LayoutBox.SelectedItem = layoutList.Any(l => l.Name == current) ? current : LayoutCatalog.DefaultName;
        appearanceReady = was;
        if (layoutProblems.Count > 0) ShowLayoutStatus("Some layout files could not be used: " + string.Join(" ", layoutProblems), true);
    }

    private void Layout_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!appearanceReady || LayoutBox.SelectedItem is not string name) return;
        var chosen = LayoutCatalog.Find(name, layoutList);
        ChooseLayout(chosen);
    }

    // Applies a layout and remembers it. A custom layout brings its own palette and accent as a start.
    internal void ChooseLayout(LayoutDefinition chosen)
    {
        bool was = appearanceReady; appearanceReady = false;
        try
        {
            settings.Layout = chosen.Name;
            if (chosen.Console && !chosen.BuiltIn) { settings.ConsolePalette = chosen.Palette; settings.ConsoleAccent = chosen.Accent; }
            if (LayoutBox.SelectedItem as string != chosen.Name) LayoutBox.SelectedItem = chosen.Name;
            RefreshPaletteChoices(chosen.Console);
            ApplyLayout(chosen); ShowLayoutNote(chosen);
            try { Storage.Save(settings); } catch (Exception ex) { Tell("Layout changed but could not be saved: " + ex.Message, true); }
        }
        finally { appearanceReady = was; }
    }

    // A layout read from a file or a link: kept in the Layouts folder and switched to.
    private void AdoptLayout(LayoutResult result, string json, string from)
    {
        if (result.Layout == null) { ShowLayoutStatus($"{from} could not be used: {result.Error}", true); return; }
        try
        {
            LayoutCatalog.Save(result.Layout, json);
            layoutList = LayoutCatalog.Load(layoutProblems = new());
            bool was = appearanceReady; appearanceReady = false;
            LayoutBox.ItemsSource = layoutList.Select(l => l.Name).ToList(); appearanceReady = was;
            var added = LayoutCatalog.Find(result.Layout.Name, layoutList);
            ChooseLayout(added);
            ShowLayoutStatus($"Added \"{added.Name}\" and switched to it." + (result.Notes.Count > 0 ? " " + string.Join(" ", result.Notes) : ""), false, true);
        }
        catch (Exception ex) { ShowLayoutStatus("The layout could not be saved: " + ex.Message, true); }
    }

    private void LayoutImport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Import a layout", Filter = "Layout files|*.json", CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        var result = LayoutParser.ReadFile(dialog.FileName);
        string json = result.Layout == null ? "" : File.ReadAllText(dialog.FileName, Encoding.UTF8);
        AdoptLayout(result, json, Path.GetFileName(dialog.FileName));
    }

    private void LayoutFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(LayoutCatalog.Folder); Process.Start(new ProcessStartInfo(LayoutCatalog.Folder) { UseShellExecute = true }); }
        catch (Exception ex) { ShowLayoutStatus("The folder could not be opened: " + ex.Message, true); }
    }

    // A layout file to start from: the current console layout (or the compact one) with a name that is free.
    private void LayoutTemplate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var basis = layout.Console ? layout : LayoutCatalog.Compact;
            string name = "My layout"; int n = 2;
            while (layoutList.Any(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)) || File.Exists(Path.Combine(LayoutCatalog.Folder, Storage.SafeName(name) + ".json"))) name = "My layout " + n++;
            var own = basis with { Name = name, Author = "", Palette = settings.ConsolePalette, Accent = settings.ConsoleAccent };
            var path = LayoutCatalog.Save(own, LayoutCatalog.ToJson(own));
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            RescanLayouts();
            ShowLayoutStatus($"Saved {Path.GetFileName(path)} in the layouts folder. Edit it, then choose it from the Layout list.", false, true);
        }
        catch (Exception ex) { ShowLayoutStatus("The template could not be saved: " + ex.Message, true); }
    }

    private void LayoutRemove_Click(object sender, RoutedEventArgs e)
    {
        if (layout.BuiltIn || layout.Path == null) return;
        if (!ThemedDialog.Confirm(this, "Remove layout", $"Delete the layout file \"{Path.GetFileName(layout.Path)}\"? Flashback goes back to the default layout.", "Delete")) return;
        try
        {
            File.Delete(layout.Path);
            layoutList = LayoutCatalog.Load(layoutProblems = new());
            bool was = appearanceReady; appearanceReady = false; LayoutBox.ItemsSource = layoutList.Select(l => l.Name).ToList(); appearanceReady = was;
            ChooseLayout(LayoutCatalog.Default);
            ShowLayoutStatus("Layout removed.", false, true);
        }
        catch (Exception ex) { ShowLayoutStatus("The layout could not be removed: " + ex.Message, true); }
    }

    private void LayoutUrl_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; LayoutDownload_Click(sender, e); } }

    private async void LayoutDownload_Click(object sender, RoutedEventArgs e)
    {
        if (layoutBusy) return;
        var uri = LayoutDownloader.Resolve(LayoutUrlBox.Text);
        if (uri == null) { ShowLayoutStatus("Paste a link to a layout file on GitHub (github.com, raw.githubusercontent.com or gist.githubusercontent.com, over https).", true); return; }
        layoutBusy = true; LayoutDownloadButton.IsEnabled = false; ShowLayoutStatus("Downloading…");
        try
        {
            string json = await LayoutDownloader.FetchAsync(uri);
            var result = LayoutParser.Parse(json);
            AdoptLayout(result, json, "That file");
            if (result.Layout != null) LayoutUrlBox.Clear();
        }
        catch (Exception ex) { ShowLayoutStatus("The download failed: " + (ex is TaskCanceledException ? "it took too long." : ex.Message), true); }
        finally { layoutBusy = false; LayoutDownloadButton.IsEnabled = true; }
    }
}
