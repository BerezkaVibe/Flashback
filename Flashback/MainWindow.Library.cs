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
    { if (e.OriginalSource is DependencyObject target && ItemsControl.ContainerFromElement(LibraryList, target) is ListBoxItem) LibraryTrim_Click(sender, e); }
    private void LibraryReveal_Click(object sender, RoutedEventArgs e) => WithSelected(c =>
    { var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true }; start.ArgumentList.Add("/select,"); start.ArgumentList.Add(c.Path); Process.Start(start); });
    private void LibraryTrim_Click(object sender, RoutedEventArgs e) => WithSelected(c => OpenTrimmer(c.Path));
    private void OpenTrim_Click(object sender,RoutedEventArgs e) => OpenTrimmer();
    private bool OpenTrimmer(string? path = null)
    {
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
        if (trimWindow != null) { Tell("Close the trimmer before deleting a clip."); return; }
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




