using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Flashback;

// Splicing: sections play in the order of their chips, which drag to rearrange; and more clips can be
// added onto the end of the timeline, so parts of several videos can be cut together in any order.
public partial class TrimWindow
{
    private Point? chipPress; private KeepSection? chipDragged;
    private void InitSectionOrder()
    {
        SectionsList.AllowDrop = true;
        SectionsList.PreviewMouseLeftButtonDown += (_, e) =>
        {
            chipPress = e.GetPosition(SectionsList);
            chipDragged = (e.OriginalSource as FrameworkElement)?.DataContext as KeepSection;
        };
        SectionsList.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed || chipDragged is not { } dragged || chipPress is not { } press || sections.Count < 2 || exportCancellation != null) return;
            if (Math.Abs(e.GetPosition(SectionsList).X - press.X) < 8) return;
            chipDragged = null;
            DragDrop.DoDragDrop(SectionsList, new DataObject(typeof(KeepSection), dragged), DragDropEffects.Move);
        };
        SectionsList.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(typeof(KeepSection)) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
        SectionsList.Drop += (_, e) =>
        {
            e.Handled = true;
            if (e.Data.GetData(typeof(KeepSection)) is not KeepSection moving || !sections.Contains(moving)) return;
            MoveSection(sections.IndexOf(moving), DropIndex(e.GetPosition(SectionsList)));
        };
    }
    // Where a dropped chip goes: before the first chip whose middle is right of the pointer.
    private int DropIndex(Point p)
    {
        for (int i = 0; i < sections.Count; i++)
            if (SectionsList.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement chip)
            {
                var box = chip.TransformToAncestor(SectionsList).TransformBounds(new Rect(chip.RenderSize));
                if (p.X < box.X + box.Width / 2) return i;
            }
        return sections.Count;
    }
    internal void MoveSection(int from, int to)
    {
        if (from < 0 || from >= sections.Count) return;
        if (to > from) to--;
        to = Math.Clamp(to, 0, sections.Count - 1);
        if (to == from) return;
        Pause(); Snapshot();
        sections.Move(from, to); SectionsList.SelectedIndex = -1; Timeline.InvalidateVisual();
        StatusLabel.Text = $"That section now plays {Ordinal(to + 1)}. Sections play in the order of their chips.";
        UpdateExportHint(); ProjectChanged();
    }
    private static string Ordinal(int n) => n switch { 1 => "first", 2 => "second", 3 => "third", 4 => "fourth", 5 => "fifth", _ => $"{n}th" };

    // ---- More clips ----
    private void AddClip_Click(object sender, RoutedEventArgs e)
    {
        if (source.Length == 0 || exportCancellation != null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Add a clip to the end", Filter = "Videos|*.mp4;*.m4v;*.mov", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) _ = AddClipAsync(dialog.FileName);
    }
    // The clip joins the end of the working video in a new file beside the original (which stays as it
    // is); every edit keeps its place, and the new part becomes a section of its own when there are sections.
    internal async Task<bool> AddClipAsync(string path)
    {
        if (source.Length == 0 || exportCancellation != null) return false;
        ClipMedia added;
        try { added = ClipMedia.Read(path); } catch (Exception ex) { StatusLabel.Text = "That video couldn't be read: " + ex.Message; return false; }
        bool quick = ClipJoin.Matches(media, added);
        string folder = Path.GetDirectoryName(source)!, name = Path.GetFileNameWithoutExtension(source) + " + " + Path.GetFileNameWithoutExtension(path);
        if (name.Length > 120) name = name[..120];
        string destination = Path.Combine(folder, name + ".mp4");
        for (int n = 2; File.Exists(destination); n++) destination = Path.Combine(folder, $"{name} ({n}).mp4");
        var before = CurrentProject(); double oldDuration = media.Duration;
        Pause(); exportCancellation = new(); exportFinished = new(TaskCreationOptions.RunContinuationsAsynchronously); SetEditingEnabled(false);
        ExportProgress.Value = 0; CancelExportButton.Visibility = Visibility.Visible; UpdateSummary();
        StatusLabel.Text = quick ? "Adding the clip…" : "Converting the clip to match, then adding it… (videos of a different size or frame rate take a while)";
        string? joined = null;
        try
        {
            var progress = new Progress<double>(p => ExportProgress.Value = p);
            joined = await ClipJoin.JoinAsync(source, path, destination, progress, exportCancellation.Token);
        }
        catch (OperationCanceledException) { StatusLabel.Text = "Adding the clip was canceled. Nothing changed."; }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
        finally
        {
            exportCancellation.Dispose(); exportCancellation = null; SetEditingEnabled(true);
            CancelExportButton.Visibility = Visibility.Collapsed; ExportProgress.Value = 0; UpdateSummary(); exportFinished.TrySetResult();
        }
        if (joined == null || closed) return false;
        // Open the joined video and put the edit back: everything keeps its time.
        var edits = before;
        if (!LoadClip(joined, confirmChanges: false, offerRecovery: false)) return false;
        var info = new FileInfo(joined);
        var sectionsAfter = edits.Sections.Length > 0 ? edits.Sections.Append(new KeepSection(oldDuration, media.Duration)).ToArray() : edits.Sections;
        bool wholeRange = edits.End >= oldDuration - .01;
        ApplyProject(edits with
        {
            Source = info.FullName, SourceBytes = info.Length, SourceWriteTicks = info.LastWriteTimeUtc.Ticks, Sections = sectionsAfter,
            End = wholeRange ? media.Duration : edits.End,
        });
        undo.Clear(); redo.Clear();
        StatusLabel.Text = $"Added {Path.GetFileName(path)} to the end ({KeepSection.TimeText(oldDuration)} onward). Working video: {Path.GetFileName(joined)}; the originals are unchanged.";
        ProjectChanged();
        return true;
    }
    private bool OverTimeline(DragEventArgs e)
    {
        var p = e.GetPosition(Timeline);
        return source.Length > 0 && p.X >= 0 && p.Y >= 0 && p.X <= Timeline.ActualWidth && p.Y <= Timeline.ActualHeight;
    }
}
