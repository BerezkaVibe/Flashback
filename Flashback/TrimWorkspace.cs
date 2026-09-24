using System;
using System.Collections.Generic;
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
            && !ThemedDialog.Confirm(this,"Open project?","This replaces your current trim selection.","Open project")) return false;
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
    private void RefreshTimelineZoom()
    {
        double zoom=Timeline.ZoomFactor; bool zoomed=zoom>1.001;
        TimelineZoomLabel.Text=zoomed ? $"{zoom:0.#}×" : "1×";
        TimelineZoomLabel.SetResourceReference(TextBlock.ForegroundProperty,zoomed ? "Accent" : "Muted");
        TimelineFitButton.IsEnabled=zoomed;
        System.Windows.Automation.AutomationProperties.SetName(Timeline,$"Clip timeline. Showing {KeepSection.TimeText(Timeline.ViewStart)} to {KeepSection.TimeText(Timeline.ViewStart+Timeline.VisibleDuration)} of {KeepSection.TimeText(Timeline.Duration)}");
    }

    private ShareExportOptions ExportOptions()
    {
        if (!double.TryParse(SizeLimit.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out double mb)) throw new ArgumentException("Enter a file size in MB, or 0 for no limit.");
        var format=(ExportFormat)Math.Max(0,SharePreset.SelectedIndex);
        var options=ShareExportOptions.For(format,mb) with { Crop=CurrentCrop(), DesktopVolume=DesktopMix.Value/100, MicrophoneVolume=MicrophoneMix.Value/100, Cuts=Timeline.Cuts, Speed=ExportSpeedValue, SlowRegions=Timeline.SlowRegions };
        options.Validate(); return options;
    }
    // What the export keeps, before validation: the sections, or the marked range.
    private IEnumerable<KeepSection> SelectedRanges() => sections.Count>0 ? sections : new[] { new KeepSection(Timeline.Start, Math.Max(Timeline.Start+.1, Timeline.End)) };
    // Length of the finished export after any speed changes.
    private double OutputLength(IEnumerable<KeepSection> ranges) => new ShareExportOptions { Speed=ExportSpeedValue, SlowRegions=Timeline.SlowRegions }.OutputSeconds(ranges);
    private double ExportSpeedValue => ShareExportOptions.Speeds[Math.Clamp(ExportSpeed?.SelectedIndex ?? 0, 0, ShareExportOptions.Speeds.Length-1)];
    private void ExportSpeed_Changed(object sender,SelectionChangedEventArgs e) => UpdateExportHint();
    // Anything beyond copying the original MP4 streams needs a re-encode.
    private bool NeedsReencode(ShareExportOptions options) =>
        options.Format!=ExportFormat.Mp4 || options.TargetMb>0 || options.Crop!=null || options.Cuts.Count>0 || (media.HasSeparateTracks && options.CustomMix) || options.Speed!=1 || options.SlowRegions.Count>0;
    private void Share_Changed(object sender,SelectionChangedEventArgs e) => UpdateExportHint();
    private void SizeLimit_Changed(object sender,TextChangedEventArgs e) => UpdateExportHint();
    private void UpdateExportHint()
    {
        if(SizeLimit==null || SharePreset==null || ModeHint==null || ExportMode==null) return;
        try
        {
            var options=ExportOptions(); bool convert=NeedsReencode(options);
            if(convert!=sharingEnabled)
            {
                sharingEnabled=convert;
                if(convert) { preferredPrecision=ExportMode.SelectedIndex; ExportMode.SelectedIndex=0; }
                else ExportMode.SelectedIndex=preferredPrecision;
            }
            ExportMode.IsEnabled=exportCancellation==null && !convert;
            double duration=Math.Max(.1,options.OutputSeconds(SelectedRanges()));
            string crop=(options.Speed!=1 || options.SlowRegions.Count>0 ? $" · speed changes, {duration:0.#} s long" : "")+(options.Crop is { } c ? $" · cropped to {c.Width & ~1} × {c.Height & ~1}" : "")+(options.Cuts.Count>0 ? $" · {options.Cuts.Count} cut{(options.Cuts.Count==1 ? "" : "s")}" : "");
            ModeHint.Text=options.Format switch
            {
                ExportFormat.Gif => "Animated GIF · 480p, 15 fps, no sound. Best for short moments"+crop+".",
                ExportFormat.Mp3 => "Audio only · MP3. Size limit and crop don't apply.",
                _ when options.TargetMb>0 => $"Up to {options.TargetMb:0.##} MB · video budget {options.VideoBitrate(duration,media.HasAudio)/1_000_000d:0.##} Mbps · re-encodes"+crop,
                _ when convert => (options.Format==ExportFormat.Mov ? "MOV" : "MP4")+" · re-encodes the selected sections"+crop+".",
                _ => ExportMode.SelectedIndex==1 ? "Fast export; edges may extend to nearby keyframes." : "Exact edges; re-encodes on export."
            };
        }
        catch(ArgumentException ex) { ModeHint.Text=ex.Message; }
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
