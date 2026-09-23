using System;
using System.Collections.Generic;
using System.Windows.Input;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Flashback;

public partial class TrimWindow : Window
{
    private string source = "";
    private ClipMedia media = new(1,false);
    private readonly ObservableCollection<KeepSection> sections = new();
    private readonly DispatcherTimer clock;
    private CancellationTokenSource? exportCancellation;
    private TaskCompletionSource? exportFinished;
    private bool playing, updating, closeAfterCancel, closed, pendingSeek, seekAwaiting, resumeAfterDrag;
    private double playhead;
    private long seekIssued;
    private readonly bool previewEnabled;
    private Dictionary<TrimAction,string> keys = TrimShortcuts.Resolve(new Settings());
    internal double Playhead => playhead;
    private readonly Stack<KeepSection[]> undo = new(), redo = new();
    private int previewSection = -1;
    public event Action<ClipResult>? Exported;
    public TrimWindow(string? path = null, bool renderOnly = false)
    {
        InitializeComponent(); WindowTheme.Attach(this); previewEnabled = !renderOnly;
        LoadKeys();
        Activated += (_, _) => LoadKeys();
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Timeline.Duration = media.Duration; Timeline.End = media.Duration; Timeline.FrameRate = media.FrameRate;
        Timeline.ViewChanged+=RefreshTimelineZoom; RefreshTimelineZoom();
        Timeline.RangeChanged += SetRange; Timeline.LaneToggled += LaneToggled; Timeline.SectionPicked += SectionPicked;
        Timeline.CutAdded += CutAdded; Timeline.CutRemoved += CutRemoved; Timeline.LanesToggleRequested += LanesToggleRequested;
        Timeline.SeekRequested += t => SeekTo(t, Timeline.IsDragging);
        Timeline.SpeedStepRequested += StepPreviewRate;
        LoadSpeedPresets();
        // Scrubbing pauses the preview; letting go picks playback back up if it was playing.
        Timeline.DragStarted += () => { resumeAfterDrag = playing; Pause(); };
        Timeline.DragCompleted += () => { if (resumeAfterDrag) { resumeAfterDrag = false; StartPlayback(playhead); } else FlushSeek(); };
        EndBox.Text = KeepSection.TimeText(media.Duration);
        SectionsList.ItemsSource = sections;
        sections.CollectionChanged += (_, _) => UpdateSummary(); UpdateSummary(); SetPlayhead(0);
        clock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        clock.Tick += (_, _) => RefreshPlayback();
        if (path != null) LoadClip(path,false);
        Closing += (_, e) =>
        {
            if (exportCancellation != null) { closeAfterCancel = true; exportCancellation.Cancel(); e.Cancel = true; return; }
            if(!FlushProject()) { e.Cancel=true; return; } Pause(); clock.Stop(); Player.Close();
        };
        Closed += (_, _) => closed = true;
        IsVisibleChanged += (_, _) => { if (!IsVisible) { Pause(); clock.Stop(); } else if (previewEnabled && source.Length>0) clock.Start(); };
    }
    internal string SourcePath => source;
    internal bool LoadClip(string path, bool confirmChanges = true)
    {
        if (exportCancellation != null) { ImportError("Finish or cancel the export before opening another video."); return false; }
        // Read and validate first so a bad drop cannot erase an existing edit.
        var imported=TrimImport.Read(new[] { path });
        if (string.Equals(source,imported.Path,StringComparison.OrdinalIgnoreCase)) return true;
        bool edited=source.Length>0 && (sections.Count>0 || Timeline.Start>.001 || Math.Abs(Timeline.End-media.Duration)>.001);
        if (confirmChanges && edited && !ThemedDialog.Confirm(this,"Open another video?","This discards your current trim selection. Your original video is unchanged.","Open video")) return false;
        if(!FlushProject()) return false; projectPath=null; savedProject=null; projectSaveTimer?.Stop(); Pause(); Player.Close(); source=imported.Path; media=imported.Media; var sourceInfo=new FileInfo(source); sourceBytes=sourceInfo.Length; sourceWriteTicks=sourceInfo.LastWriteTimeUtc.Ticks;
        sections.Clear(); undo.Clear(); redo.Clear(); ResetCrop(); ResetCuts();
        Timeline.Duration=media.Duration; Timeline.FrameRate=media.FrameRate; Timeline.Fit(); RecentTrimFiles.Remember(source); SetRange(0,media.Duration); SetPlayhead(0);
        pendingSeek=false; seekAwaiting=false; Player.SpeedRatio=PreviewRate;
        SourceLabel.Text=Path.GetFileName(source); SourceLabel.ToolTip=source;
        TrimContent.Visibility=Visibility.Visible; EmptyState.Visibility=Visibility.Collapsed;
        StatusLabel.Text="";
        if (previewEnabled) { Player.Source=new Uri(source); Player.Play(); Player.Pause(); clock.Start(); }
        LoadLanes();
        return true;
    }
    private void ImportError(string message) { StatusLabel.Text=message; ImportStatus.Text=message; }
    private void OpenVideo_Click(object sender,RoutedEventArgs e)
    {
        if (exportCancellation != null) { ImportError("Finish or cancel the export before opening another video."); return; }
        var dialog=new Microsoft.Win32.OpenFileDialog { Title="Open video to trim",Filter="Videos|*.mp4;*.m4v;*.mov",CheckFileExists=true,Multiselect=false };
        if (dialog.ShowDialog(this)!=true) return;
        try { LoadClip(dialog.FileName); } catch(Exception ex) { ImportError(ex.Message); }
    }
    private void Video_DragOver(object sender,DragEventArgs e)
    {
        e.Effects=exportCancellation==null && TrimImport.CanDrop(TrimImport.Files(e.Data)) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled=true;
    }
    private void Video_Drop(object sender,DragEventArgs e)
    {
        e.Handled=true;
        try
        {
            var paths=TrimImport.Files(e.Data);
            if (paths.Length!=1) throw new ArgumentException("Drop one video at a time.");
            if (LoadClip(paths[0])) e.Effects=DragDropEffects.Copy;
        }
        catch(Exception ex) { e.Effects=DragDropEffects.None; ImportError(ex.Message); }
    }
    internal void RefreshPlayback()
    {
        FlushSeek();
        // A paused decoder may report an old position while a seek is pending.
        // Never let it overwrite the user's playhead or I/O marks.
        if (!playing || Timeline.IsDragging) return;
        double actual = Player.Position.TotalSeconds;
        if (seekAwaiting && (actual < playhead-.05 || actual > playhead+System.Diagnostics.Stopwatch.GetElapsedTime(seekIssued).TotalSeconds*4+.5)) return;
        seekAwaiting = false;
        if (previewSection == -2 && actual >= Timeline.End-.02) { SetPlayhead(Timeline.End); Pause(); return; }
        if (previewSection >= 0 && previewSection < sections.Count && actual >= sections[previewSection].End-.02)
        {
            if (previewSection+1 < sections.Count) StartPlayback(sections[previewSection+1].Start, previewSection+1);
            else { SetPlayhead(sections[previewSection].End); Pause(); }
            return;
        }
        SetPlayhead(actual);
    }
    private void SetPlayhead(double time)
    {
        playhead = Math.Clamp(time, 0, media.Duration); Timeline.Position = playhead; if (!Timeline.IsDragging) Timeline.Reveal(playhead); Timeline.InvalidateVisual();
        PositionLabel.Text = $"{KeepSection.TimeText(playhead)} / {KeepSection.TimeText(media.Duration)}";
        if (Timeline.Cuts.Count > 0 || CensorOverlay.Visibility == Visibility.Visible) ApplyPreviewCuts();
    }
    internal void SeekTo(double time, bool defer = false)
    {
        Pause(); SetPlayhead(time); pendingSeek = true;
        if (!defer) FlushSeek();
    }
    private void FlushSeek()
    {
        if (!pendingSeek) return;
        pendingSeek = false; seekAwaiting = true;
        seekIssued=System.Diagnostics.Stopwatch.GetTimestamp();
        if (previewEnabled) Player.Position = TimeSpan.FromSeconds(playhead);
    }
    private void SetRange(double start, double end)
    {
        updating = true;
        StartBox.Text = KeepSection.TimeText(start); EndBox.Text = KeepSection.TimeText(end);
        updating = false; Timeline.Start = start; Timeline.End = end; Timeline.InvalidateVisual(); UpdateSummary();
    }
    private void UpdateSummary()
    {
        if (ExportButton == null || media == null) return;
        Timeline.Sections = sections; Timeline.InvalidateVisual();
        SectionsList.Visibility=SectionsRow.Visibility=sections.Count>0 ? Visibility.Visible : Visibility.Collapsed;
        TotalLabel.Text = sections.Count > 0 ? $"{sections.Count} sections · {sections.Sum(s => s.Duration):0.##} s" : $"Selected range · {Math.Max(0,Timeline.End-Timeline.Start):0.##} s";
        ProjectChanged(); UpdateExportHint();
        ExportButton.IsEnabled = source.Length>0 && exportCancellation == null && (sections.Count > 0 || Timeline.End-Timeline.Start >= .1);
    }
    // Scrubbing renders frames for paused seeks, but left on during playback it lets
    // Media Foundation's audio run ahead of video after a seek.
    private void Pause() { Player.Pause(); Player.ScrubbingEnabled = true; playing = false; previewSection = -1; PlayToggle.Content = "\uE768"; }
    // The first Play after opening restarts from zero unless the player has already been run once
    // since MediaOpened, so prime it here before applying the pending position.
    private void Player_Opened(object sender, RoutedEventArgs e) { Player.SpeedRatio=PreviewRate; Player.Play(); Player.Pause(); pendingSeek=true; FlushSeek(); if (playing) Player.Play(); }
    private void Player_Ended(object sender, RoutedEventArgs e) { Pause(); SetPlayhead(media.Duration); }
    private void Player_Failed(object sender, ExceptionRoutedEventArgs e)
    { Pause(); StatusLabel.Text = "Preview unavailable. You can still mark times and export. " + e.ErrorException.Message; }
    private void StartPlayback(double start, int section = -1)
    {
        // Seek while paused, then play, so audio and video restart from the same point.
        Player.Pause();
        SetPlayhead(start); previewSection=section;
        pendingSeek=true; FlushSeek();
        Player.ScrubbingEnabled=false;
        Player.Play(); playing=true; PlayToggle.Content="\uE769";
    }
    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if(exportCancellation!=null) return;
        if (playing) Pause();
        else StartPlayback(playhead >= media.Duration-.01 ? 0 : playhead);
    }
    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if(exportCancellation!=null) return;
        if (sections.Count == 0) { StartPlayback(Timeline.Start,-2); return; }
        StartPlayback(sections[0].Start, 0);
    }
    private void MarkStart_Click(object sender, RoutedEventArgs e)
    {
        if(exportCancellation!=null) return;
        double start=Math.Min(playhead,Math.Max(0,media.Duration-.1));
        SetRange(start,Math.Max(Timeline.End,Math.Min(media.Duration,start+.1)));
    }
    private void MarkEnd_Click(object sender, RoutedEventArgs e)
    {
        if(exportCancellation!=null) return;
        double end=Math.Max(Math.Min(.1,media.Duration),playhead);
        SetRange(Math.Min(Timeline.Start,Math.Max(0,end-.1)),end);
    }
    // With a section selected the same button updates it; otherwise it adds a new one.
    private void Add_Click(object sender, RoutedEventArgs e) => ChangeSection(SectionsList.SelectedItem is KeepSection);
    private void Update_Click(object sender, RoutedEventArgs e) => ChangeSection(true);
    private void ChangeSection(bool replace)
    {
        if (exportCancellation!=null) return;
        try
        {
            var list = sections.ToList();
            if (replace) { if (SectionsList.SelectedItem is not KeepSection old) throw new ArgumentException("Select a section to update."); list.Remove(old); }
            var item = new KeepSection(KeepSection.Parse(StartBox.Text), KeepSection.Parse(EndBox.Text)); list.Add(item);
            list = ClipEditor.Validate(list, media.Duration);
            double previousPosition=playhead;
            Snapshot(); Pause(); sections.Clear(); foreach (var s in list) sections.Add(s); SectionsList.SelectedIndex = -1; SeekTo(previousPosition);
            StatusLabel.Text = $"Section {list.IndexOf(item) + 1} {(replace ? "updated" : "added")}. Sections are joined in order.";
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }
    private void Section_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (SectionsList.SelectedItem is KeepSection s)
        { Pause(); StartBox.Text = KeepSection.TimeText(s.Start); EndBox.Text = KeepSection.TimeText(s.End); SeekTo(s.Start); }
        Timeline.SelectedSection = SectionsList.SelectedIndex; Timeline.InvalidateVisual(); UpdateAddButton();
    }
    private void Remove_Click(object sender, RoutedEventArgs e) { if(exportCancellation!=null) return; Pause(); if (SectionsList.SelectedItem is KeepSection s) { Snapshot(); sections.Remove(s); } }
    private void Clear_Click(object sender, RoutedEventArgs e) { if(exportCancellation!=null) return; Pause(); Snapshot(); sections.Clear(); }
    private void Snapshot() { if (undo.Count >= 50) undo.Clear(); undo.Push(sections.ToArray()); redo.Clear(); }
    private void Undo_Click(object sender, RoutedEventArgs e) => Restore(undo, redo);
    private void Restore(Stack<KeepSection[]> from, Stack<KeepSection[]> to)
    {
        if (exportCancellation != null || from.Count == 0) return;
        Pause(); to.Push(sections.ToArray()); var saved = from.Pop(); sections.Clear(); foreach(var s in saved) sections.Add(s); if(sections.Count>0) SectionsList.SelectedIndex=0;
    }
    private void Split_Click(object sender, RoutedEventArgs e)
    {
        if (exportCancellation != null) return;
        double t = playhead; var s = sections.FirstOrDefault(s => t-s.Start >= .1 && s.End-t >= .1);
        if (s == null || sections.Count >= 30) { StatusLabel.Text = "Place the playhead inside a kept section to split it."; return; }
        Snapshot(); int i = sections.IndexOf(s); sections[i] = new(s.Start,t); sections.Insert(i+1,new(t,s.End)); SectionsList.SelectedIndex=i+1;
    }
    private void RangeText_Changed(object sender, TextChangedEventArgs e)
    {
        if (updating || Timeline == null || StartBox == null || EndBox == null) return;
        try
        {
            var start=KeepSection.Parse(StartBox.Text); var end=KeepSection.Parse(EndBox.Text);
            if(start>=0 && end>=start && end<=Timeline.Duration)
            { Timeline.Start=start; Timeline.End=end; Timeline.InvalidateVisual(); UpdateSummary(); }
        }
        catch(ArgumentException) { }
    }
    private void Trim_KeyDown(object sender, KeyEventArgs e)
    {
        var key=e.Key==Key.System ? e.SystemKey : e.Key;
        bool editing=Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or ComboBox or ComboBoxItem or MenuItem;
        if (HandleKey(key,Keyboard.Modifiers,Timeline.IsKeyboardFocusWithin,editing,e.IsRepeat)) e.Handled=true;
    }
    internal bool HandleKey(Key key, ModifierKeys modifiers, bool timelineFocused, bool editingText = false, bool repeated = false)
    {
        if (exportCancellation!=null) return false;
        var action=TrimShortcuts.Find(keys,key,modifiers,timelineFocused);
        // Open and save work anywhere, including while typing a timestamp.
        if (action is TrimAction.OpenVideo or TrimAction.SaveProject) { if(!repeated) RunAction(action.Value); return true; }
        if (editingText) return false;
        if (action==TrimAction.ShortcutGuide) { if(!repeated) RunAction(action.Value); return true; }
        if (action==null)
        {
            if (repeated) return key is not (Key.Left or Key.Right or Key.Up or Key.Down);
            if (source.Length>0 && timelineFocused && modifiers==ModifierKeys.None && key is Key.Home or Key.End) { SeekTo(key==Key.Home ? 0 : media.Duration); return true; }
            return false;
        }
        if (source.Length==0) return false;
        if (repeated && !TrimShortcuts.Info(action.Value).Repeats) return true;
        RunAction(action.Value); return true;
    }
    private void ExportMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        UpdateExportHint(); ProjectChanged();
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (source.Length==0 || exportCancellation != null) return;
        ShareExportOptions options;
        try { options=ExportOptions(); } catch(ArgumentException ex) { StatusLabel.Text=ex.Message; return; }
        bool rough=ExportMode.SelectedIndex==1 && !NeedsReencode(options);
        KeepSection[] exportRanges;
        try { exportRanges=ExportRanges(); }
        catch (ArgumentException ex) { StatusLabel.Text=ex.Message; return; }
        string ext = options.Extension, kind = options.Format switch { ExportFormat.Gif => "GIF animation", ExportFormat.Mp3 => "MP3 audio", ExportFormat.Mov => "MOV video", _ => "MP4 video" };
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Export combined clip", Filter = $"{kind}|*{ext}", DefaultExt = ext, AddExtension = true, OverwritePrompt = false,
            InitialDirectory = Path.GetDirectoryName(source), FileName = Path.GetFileNameWithoutExtension(source) + " — edit " + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ext };
        if (dialog.ShowDialog(this) != true) return;
        await RunExportAsync(dialog.FileName, exportRanges, options, rough);
    }
    private void CancelExport_Click(object sender, RoutedEventArgs e) => exportCancellation?.Cancel();
    // Export options open as a pop-up over the page, so the video never shrinks to make room.
    private long optionsClosedAt;
    private void ExportOptions_Click(object sender, RoutedEventArgs e)
    {
        // A click on the button while the pop-up is open closes it first; don't reopen it.
        if (System.Diagnostics.Stopwatch.GetElapsedTime(optionsClosedAt).TotalMilliseconds < 250) return;
        ExportOptionsPopup.IsOpen = true; ExportOptionsChevron.Text = "";
    }
    private void ExportOptionsPopup_Closed(object? sender, EventArgs e) { optionsClosedAt = System.Diagnostics.Stopwatch.GetTimestamp(); ExportOptionsChevron.Text = ""; }
    public async Task CloseForQuitAsync()
    {
        if (exportCancellation != null) { var done = exportFinished!.Task; exportCancellation.Cancel(); await done; }
        if (!closed) Close();
    }
}







