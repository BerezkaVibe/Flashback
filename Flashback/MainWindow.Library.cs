using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualBasic.FileIO;

namespace Flashback;

public partial class MainWindow
{
    private List<LibraryClip> library = new();
    private bool loadingLibrary;

    private TrimWindow? trimWindow;
    private LibraryClip? SelectedClip => LibraryList.SelectedItem as LibraryClip;
    private async void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (e.Source != MainTabs) return; UpdateNavigation(); if (settings != null && LibraryList != null && LibraryTab.IsSelected) await ReloadLibraryAsync(); }
    private async void RefreshClips_Click(object sender, RoutedEventArgs e) => await ReloadLibraryAsync();
    private async Task ReloadLibraryAsync()
    {
        if (loadingLibrary || WindowState == WindowState.Minimized) return;
        loadingLibrary = true; LibrarySummary.Text = "Loading saved clips…";
        string? selected = SelectedClip?.Path;
        try
        {
            string folder = settings.OutputFolder;
            library = await Task.Run(() => ClipLibrary.Load(folder));
            FilterLibrary();
            LibraryList.SelectedItem = library.FirstOrDefault(c => c.Path == selected);
        }
        catch (Exception ex) { LibrarySummary.Text = "Could not load clips: " + ex.Message; }
        finally { loadingLibrary = false; }
    }
    private void FilterLibrary()
    {
        if (LibraryList == null) return;
        string query = ClipSearch.Text.Trim();
        var matches = library.Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || c.Game.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        LibraryList.ItemsSource = matches;
        LibraryEmpty.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LibraryEmptyTitle.Text = query.Length > 0 ? "No matching clips" : "Your plays, all in one place";
        LibrarySummary.Text = library.Count == 0 ? "No clips yet."
            : $"{matches.Count} clips";
    }
    private void ClipSearch_Changed(object sender, TextChangedEventArgs e) => FilterLibrary();
    private void Library_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (LibraryActions != null) { LibraryActions.IsEnabled = SelectedClip != null; UpdateEditorLabel(); } }
    private void UpdateEditorLabel()
    {
        EditorLabel.Text = string.IsNullOrWhiteSpace(settings.ExternalEditorPath) ? "No external editor selected" : Path.GetFileNameWithoutExtension(settings.ExternalEditorPath);
        EditorLabel.ToolTip = settings.ExternalEditorPath;
        ExternalEditButton.IsEnabled = SelectedClip != null && File.Exists(settings.ExternalEditorPath);
    }
    private void WithSelected(Action<LibraryClip> action)
    {
        try { if (SelectedClip is { } c) { if (!File.Exists(c.Path)) throw new FileNotFoundException("The clip was moved or deleted. Refresh the list."); action(c); } }
        catch (Exception ex) { Tell(ex.Message, true); }
    }
    private void LibraryPlay_Click(object sender, RoutedEventArgs e) => WithSelected(c => Process.Start(new ProcessStartInfo(c.Path) { UseShellExecute = true }));
    private void Library_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject target || InRename(target)) return;
        if (ItemsControl.ContainerFromElement(LibraryList, target) is ListBoxItem) LibraryTrim_Click(sender, e);
    }

    // Rename: clicking the file name (or F2) turns it into a text box in place.
    // Enter or clicking away saves; Esc cancels.
    private static bool InRename(DependencyObject? element)
    {
        for (; element != null; element = element is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
            if (element is TextBox || element is FrameworkElement { Tag: "rename" }) return true;
        return false;
    }
    private void ClipName_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBlock label) { e.Handled = true; BeginRename(label); }
    }
    private void Library_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F2 || SelectedClip is not { } clip || LibraryList.ItemContainerGenerator.ContainerFromItem(clip) is not ListBoxItem item) return;
        if (FindRenameLabel(item) is { } label) { e.Handled = true; BeginRename(label); }
    }
    private static TextBlock? FindRenameLabel(DependencyObject parent)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is TextBlock { Tag: "rename" } found) return found;
            if (FindRenameLabel(child) is { } deeper) return deeper;
        }
        return null;
    }
    private void BeginRename(TextBlock label)
    {
        if (label.DataContext is not LibraryClip clip || label.Parent is not Grid holder || holder.Children[1] is not TextBox box) return;
        LibraryList.SelectedItem = clip;
        box.Text = clip.Name; box.Tag = clip;
        label.Visibility = Visibility.Collapsed; box.Visibility = Visibility.Visible;
        box.Focus(); box.SelectAll();
    }
    private async void ClipRename_KeyDown(object sender, KeyEventArgs e)
    {
        // Up/Down would move the list selection underneath the box.
        if (e.Key is Key.Up or Key.Down) { e.Handled = true; return; }
        if (e.Key is not (Key.Enter or Key.Escape)) return;
        e.Handled = true; await FinishRenameAsync((TextBox)sender, e.Key == Key.Enter);
    }
    private async void ClipRename_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => await FinishRenameAsync((TextBox)sender, true);
    private async Task FinishRenameAsync(TextBox box, bool save)
    {
        if (box.Visibility != Visibility.Visible) return;
        box.Visibility = Visibility.Collapsed;
        if (box.Parent is Grid holder) holder.Children[0].Visibility = Visibility.Visible;
        if (!save || box.Tag is not LibraryClip clip || box.Text.Trim() == clip.Name) return;
        try
        {
            if (trimWindow != null && string.Equals(trimWindow.SourcePath, clip.Path, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Close this clip in the editor before renaming it.");
            string renamed = await Task.Run(() => ClipLibrary.Rename(clip.Path, box.Text));
            if (lastClip == clip.Path) lastClip = renamed;
            await ReloadLibraryAsync();
            LibraryList.SelectedItem = library.FirstOrDefault(c => c.Path == renamed);
            Tell("Renamed to " + Path.GetFileName(renamed) + ".");
        }
        catch (IOException ex) when (ex is not FileNotFoundException && ex.HResult == unchecked((int)0x80070020)) { Tell("The clip is open in another app. Close it and try again.", true); }
        catch (Exception ex) { Tell("Could not rename the clip: " + ex.Message, true); }
    }
    private void LibraryReveal_Click(object sender, RoutedEventArgs e) => WithSelected(c =>
    { var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true }; start.ArgumentList.Add("/select,"); start.ArgumentList.Add(c.Path); Process.Start(start); });
    private void LibraryTrim_Click(object sender, RoutedEventArgs e) => WithSelected(c => OpenTrimmer(c.Path));
    private void OpenTrim_Click(object sender,RoutedEventArgs e) => OpenTrimmer();
    private bool OpenTrimmer(string? path = null)
    {
        ClearError();
        try
        {
            if (trimWindow != null)
            {
                trimWindow.Show(); trimWindow.WindowState=WindowState.Normal; trimWindow.Activate();
                return path==null || trimWindow.LoadClip(path);
            }
            trimWindow = new TrimWindow(path) { Owner = this };
            trimWindow.Exported += async result => { SetLastClip(result); await ReloadLibraryAsync(); };
            trimWindow.Closed += (_, _) => trimWindow = null;
            trimWindow.Show(); return true;
        }
        catch(Exception ex) { Tell("Could not open video: "+ex.Message,true); return false; }
    }
    private void ImportVideo_DragOver(object sender,DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects=TrimImport.CanDrop(TrimImport.Files(e.Data)) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled=true;
    }
    private void ImportVideo_Drop(object sender,DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Handled=true; e.Effects=DragDropEffects.None;
        var paths=TrimImport.Files(e.Data);
        if (paths.Length!=1) { Tell("Drop one video at a time.",true); return; }
        if (OpenTrimmer(paths[0])) e.Effects=DragDropEffects.Copy;
    }
    private async void LibraryDelete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedClip is not { } clip) return;
        if (trimWindow != null) { Tell("Close the editor before deleting a clip."); return; }
        if (!ThemedDialog.Confirm(this, "Delete this clip?", Path.GetFileName(clip.Path) + " goes to the Recycle Bin.", "Move to Recycle Bin", "Keep")) return;
        try
        {
            // Let Windows show any further prompts, including volumes without a Recycle Bin.
            FileSystem.DeleteFile(clip.Path, UIOption.AllDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
            if (lastClip == clip.Path) lastClip = null;
            await ReloadLibraryAsync(); Tell("Clip deleted.");
        }
        catch (OperationCanceledException) { Tell("Deletion canceled."); }
        catch (Exception ex) { Tell("Could not delete the clip: " + ex.Message, true); }
    }
    private void ChooseEditor_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Choose a desktop video editor", Filter = "Application|*.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try { var next = settings.Copy(); next.ExternalEditorPath = dialog.FileName; Storage.Save(next); settings = next; UpdateEditorLabel(); Tell("Editor selected. Open in editor sends it the selected clip."); }
        catch (Exception ex) { Tell(ex.Message, true); }
    }
    private void ExternalEdit_Click(object sender, RoutedEventArgs e) => WithSelected(c =>
    {
        if (!File.Exists(settings.ExternalEditorPath)) throw new FileNotFoundException("Choose your editor again; the selected app is missing.");
        var start = new ProcessStartInfo(settings.ExternalEditorPath) { UseShellExecute = true };
        start.ArgumentList.Add(c.Path); Process.Start(start);
    });
}




