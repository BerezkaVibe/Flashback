using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Flashback;

public partial class TrimWindow
{
    private string? projectPath;
    private TrimProject? savedProject;
    private DispatcherTimer? projectSaveTimer;
    private bool restoringProject;
    private long sourceBytes, sourceWriteTicks;
    private bool sharingEnabled;
    private int preferredPrecision=1;

    private TrimProject CurrentProject() => new(1,source,sourceBytes,sourceWriteTicks,sections.ToArray(),Timeline.Start,Timeline.End,playhead,(sharingEnabled ? preferredPrecision : ExportMode.SelectedIndex)==1);
    private void ProjectChanged()
    {
        if (restoringProject || projectPath==null || AutoSaveProject?.IsChecked!=true) return;
        projectSaveTimer ??= new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(500) };
        projectSaveTimer.Tick -= ProjectSave_Tick; projectSaveTimer.Tick += ProjectSave_Tick;
        projectSaveTimer.Stop(); projectSaveTimer.Start();
    }
    private void ProjectSave_Tick(object? sender,EventArgs e) => FlushProject();
    private bool FlushProject()
    {
        projectSaveTimer?.Stop();
        if (projectPath==null || source.Length==0 || AutoSaveProject.IsChecked!=true) return true;
        try { SaveProject(projectPath); return true; }
        catch(Exception ex) { ImportError("Project could not be saved: "+ex.Message); return false; }
    }
    internal void SaveProject(string path)
    {
        var project=CurrentProject();
        bool unchanged=projectPath==path && savedProject!=null && project.Source==savedProject.Source && project.Start==savedProject.Start && project.End==savedProject.End && project.Position==savedProject.Position && project.RoughCut==savedProject.RoughCut && project.Sections.SequenceEqual(savedProject.Sections);
        project.Validate();
        if(!unchanged) project.Save(path);
        projectPath=path; savedProject=project;
        StatusLabel.Text="Project saved: "+Path.GetFileName(path);
    }
    internal bool OpenProject(string path,bool confirm=true)
    {
        if (exportCancellation!=null) return false;
        var project=TrimProject.Read(path); // Validate everything before replacing the current edit.
        if (source.Equals(project.Source,StringComparison.OrdinalIgnoreCase) && confirm && (sections.Count>0 || Timeline.Start>0 || Timeline.End<media.Duration)
            && MessageBox.Show(this,"Replace the current selection with this project?","Open project",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes) return false;
        if (!LoadClip(project.Source,confirm)) return false;
        if(!string.Equals(projectPath,path,StringComparison.OrdinalIgnoreCase) && !FlushProject()) return false;
        restoringProject=true;
        try
        {
            projectSaveTimer?.Stop(); sections.Clear(); undo.Clear(); redo.Clear();
            foreach(var section in project.Sections.OrderBy(s=>s.Start)) sections.Add(section);
            SetRange(project.Start,project.End); SeekTo(project.Position); Timeline.Fit(); Timeline.Reveal(project.Position);
            ExportMode.SelectedIndex=project.RoughCut ? 1 : 0; projectPath=path; savedProject=project;
            StatusLabel.Text="Opened project: "+Path.GetFileName(path); return true;
        }
        finally { restoringProject=false; }
    }
    private void SaveProject_Click(object sender,RoutedEventArgs e)
    {
        if(source.Length==0 || exportCancellation!=null) return;
        if(projectPath==null) { SaveProjectAs_Click(sender,e); return; }
        try { SaveProject(projectPath); } catch(Exception ex) { ImportError(ex.Message); }
    }
    private void SaveProjectAs_Click(object sender,RoutedEventArgs e)
    {
        if(source.Length==0 || exportCancellation!=null) return;
        var dialog=new Microsoft.Win32.SaveFileDialog { Filter="Flashback trim project|*.flashtrim",DefaultExt=".flashtrim",AddExtension=true,OverwritePrompt=true,FileName=Path.GetFileNameWithoutExtension(source)+".flashtrim",InitialDirectory=Path.GetDirectoryName(source) };
        if(dialog.ShowDialog(this)==true) try { SaveProject(dialog.FileName); } catch(Exception ex) { ImportError(ex.Message); }
    }
    private void OpenProject_Click(object sender,RoutedEventArgs e)
    {
        if(exportCancellation!=null) return;
        var dialog=new Microsoft.Win32.OpenFileDialog { Filter="Flashback trim project|*.flashtrim",CheckFileExists=true };
        if(dialog.ShowDialog(this)==true) try { OpenProject(dialog.FileName); } catch(Exception ex) { ImportError(ex.Message); }
    }
    private void RecentMenu_Opened(object sender,RoutedEventArgs e)
    {
        RecentMenu.Items.Clear();
        foreach(string path in RecentTrimFiles.Read())
        {
            var item=new MenuItem { Header=Path.GetFileName(path).Replace("_","__"),ToolTip=path };
            item.Click+=(_,_)=> { try { LoadClip(path); } catch(Exception ex) { ImportError(ex.Message); } }; RecentMenu.Items.Add(item);
        }
        if(RecentMenu.Items.Count==0) RecentMenu.Items.Add(new MenuItem { Header="No recent videos",IsEnabled=false });
    }
    private void Redo_Click(object sender,RoutedEventArgs e) => Restore(redo,undo);
    private void ZoomIn_Click(object sender,RoutedEventArgs e) { Timeline.Zoom(2,playhead); Timeline.Focus(); }
    private void ZoomOut_Click(object sender,RoutedEventArgs e) { Timeline.Zoom(.5,playhead); Timeline.Focus(); }
    private void ZoomFit_Click(object sender,RoutedEventArgs e) { Timeline.Fit(); Timeline.Focus(); }

    private ShareExportOptions ExportOptions()
    {
        if (!double.TryParse(SizeLimit.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out double mb)) throw new ArgumentException("Enter a file size in MB, or 0 for no limit.");
        var options=new ShareExportOptions(mb,SharePreset.SelectedIndex switch { 1=>1080,2=>720,_=>0 },SharePreset.SelectedIndex switch {1=>60,2=>30,_=>0});
        options.Validate(); return options;
    }
    private void Share_Changed(object sender,SelectionChangedEventArgs e) => UpdateExportHint();
    private void SizeLimit_Changed(object sender,TextChangedEventArgs e) => UpdateExportHint();
    private void UpdateExportHint()
    {
        if(SizeLimit==null || SharePreset==null || ModeHint==null || ExportMode==null) return;
        try
        {
            var options=ExportOptions(); bool convert=SharePreset.SelectedIndex>0 || options.TargetMb>0;
            if(convert!=sharingEnabled)
            {
                sharingEnabled=convert;
                if(convert) { preferredPrecision=ExportMode.SelectedIndex; ExportMode.SelectedIndex=0; }
                else ExportMode.SelectedIndex=preferredPrecision;
            }
            ExportMode.IsEnabled=exportCancellation==null && !convert;
            double duration=sections.Count>0 ? sections.Sum(s=>s.Duration) : Math.Max(.1,Timeline.End-Timeline.Start);
            ModeHint.Text=options.TargetMb>0 ? $"Up to {options.TargetMb:0.##} MB · video budget {options.VideoBitrate(duration,media.HasAudio)/1_000_000d:0.##} Mbps · re-encodes"
                : convert ? "H.264 / AAC MP4 · re-encodes selected sections only."
                : ExportMode.SelectedIndex==1 ? "Fast export; edges may extend to nearby keyframes." : "Exact edges; re-encodes on export.";
        }
        catch(ArgumentException ex) { ModeHint.Text=ex.Message; }
    }
    private async Task WaitForCaptureAsync(CancellationToken token)
    {
        while(WaitForRecording.IsChecked==true && Application.Current.Windows.OfType<MainWindow>().Any(w=>w.RecordingActive))
        { StatusLabel.Text="Export queued. Pause recording or uncheck the wait option to begin."; await Task.Delay(500,token); }
    }
    private async void Snapshot_Click(object sender,RoutedEventArgs e)
    {
        if(source.Length==0 || exportCancellation!=null) return;
        var dialog=new Microsoft.Win32.SaveFileDialog { Filter="PNG image|*.png|JPEG image|*.jpg",DefaultExt=".png",AddExtension=true,OverwritePrompt=false,InitialDirectory=Path.GetDirectoryName(source),FileName=Path.GetFileNameWithoutExtension(source)+" — frame "+playhead.ToString("0.000",CultureInfo.InvariantCulture) };
        if(dialog.ShowDialog(this)!=true) return;
        Pause(); exportCancellation=new(); exportFinished=new(TaskCreationOptions.RunContinuationsAsynchronously); CancelExportButton.Visibility=Visibility.Visible; UpdateSummary();
        try { await ExportServices.SnapshotAsync(source,dialog.FileName,playhead,exportCancellation.Token); StatusLabel.Text="Saved snapshot: "+Path.GetFileName(dialog.FileName); }
        catch(OperationCanceledException) { StatusLabel.Text="Snapshot canceled."; }
        catch(Exception ex) { StatusLabel.Text=ex.Message; }
        finally { exportCancellation.Dispose(); exportCancellation=null; CancelExportButton.Visibility=Visibility.Collapsed; UpdateSummary(); exportFinished.TrySetResult(); if(closeAfterCancel) Close(); }
    }
}
