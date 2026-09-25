using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace Flashback;

public partial class TrimWindow
{
    // Preview speed only; exports always keep the source speed.
    internal double PreviewRate { get; private set; } = 1;
    internal const double MinRate = .1, MaxRate = 4;
    private static readonly double[] previewRates={.1,.25,.5,.75,1,1.25,1.5,2,3,4};
    private System.Windows.Threading.DispatcherTimer? realign;
    private void SetPreviewRate(double rate)
    {
        rate=Math.Clamp(Math.Round(rate,2),MinRate,MaxRate);
        bool changed=rate!=PreviewRate; PreviewRate=rate; Player.SpeedRatio=rate;
        // A live rate change can leave audio offset from video; once the speed settles,
        // restart from the playhead to realign (a spun wheel or dragged slider restarts once).
        if (changed && playing)
        {
            realign??=new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(250),System.Windows.Threading.DispatcherPriority.Normal,(_,_)=> { realign!.Stop(); if (playing) StartPlayback(playhead, previewSection); },Dispatcher);
            realign.Stop(); realign.Start();
        }
        ShowPreviewRate();
        StatusLabel.Text=$"Preview {rate:0.##}× · {(userMuted ? "muted" : $"volume {PreviewVolumeSlider.Value:0}%")}";
    }
    private void StepFrame(int direction)
    {
        // In the finished view frames are counted along the finished video, so a hold steps frame by frame too.
        double at=Timeline.Finished ? Timeline.PositionView : playhead;
        double frame=direction>0 ? Math.Floor(at*media.FrameRate+1e-6)+1 : Math.Ceiling(at*media.FrameRate-1e-6)-1;
        SeekView(frame/media.FrameRate);
    }
    private void NavigateSection(int direction, bool edge=false)
    {
        if(sections.Count==0) return;
        int index=SectionsList.SelectedIndex;
        if(edge) index=direction<0 ? 0 : sections.Count-1;
        else if(index<0) index=direction<0 ? sections.Count-1 : 0;
        else index=Math.Clamp(index+direction,0,sections.Count-1);
        SectionsList.SelectedIndex=index; SectionsList.ScrollIntoView(sections[index]); SeekTo(sections[index].Start);
    }
    internal bool SeekTimecode(string text)
    {
        try
        {
            double time=KeepSection.Parse(text);
            if(time<0 || time>media.Duration) return false;
            SeekTo(time); return true;
        }
        catch(ArgumentException) { return false; }
    }
    private void Timecode_Click(object sender,RoutedEventArgs e)
    {
        if(source.Length==0 || exportCancellation!=null) return;
        Pause();
        var field=new TextBox {Text=KeepSection.TimeText(playhead),Margin=new Thickness(0,10,0,10)};
        var error=new TextBlock {Text="Seconds or h:mm:ss.fff",Margin=new Thickness(0,0,0,10)};
        var go=new Button {Content="Go",IsDefault=true,HorizontalAlignment=HorizontalAlignment.Right,MinWidth=80};
        var panel=new StackPanel {Margin=new Thickness(20)}; panel.Children.Add(new TextBlock {Text="Jump to time"});panel.Children.Add(field);panel.Children.Add(error);panel.Children.Add(go);
        var dialog=new Window {Title="Go to time · Flashback",Owner=this,Width=380,SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner,Content=panel};
        WindowTheme.Attach(dialog);
        go.Click+=(_,_)=> { if(SeekTimecode(field.Text)) {dialog.DialogResult=true;} else error.Text="Enter a time within the video."; };
        dialog.PreviewKeyDown+=(_,key)=> {if(key.Key==Key.Escape) {dialog.Close();key.Handled=true;}};
        dialog.Loaded+=(_,_)=> {field.Focus();field.SelectAll();}; dialog.ShowDialog(); Timeline.Focus();
    }
    private void ShortcutGuide_Click(object sender,RoutedEventArgs e) => ShortcutGuide.Show(this,keys);
    private void LoadKeys()
    {
        keys=TrimShortcuts.Resolve(Storage.Load(out _));
        string Key(TrimAction action) => TrimShortcuts.Display(keys[action]);
        UpdateAddButton();

        MarkStartKey.Text=Key(TrimAction.MarkStart); MarkStartButton.ToolTip="Set start at playhead · "+MarkStartKey.Text; AutomationProperties.SetName(MarkStartButton,"Set start, shortcut "+MarkStartKey.Text);
        CutToolButton.ToolTip="Cut out: black out video or mute audio for part of the clip without removing time · "+Key(TrimAction.CutTool);
        SlowToolButton.ToolTip="Speed: click two spots to slow down or speed up that part · "+Key(TrimAction.SpeedTool);
        ZoomToolButton.ToolTip="Zoom: click two spots to zoom in on that part · "+Key(TrimAction.ZoomTool);
        TextToolButton.ToolTip="Text: click two spots to add a caption for that part · "+Key(TrimAction.TextTool);
        ImageToolButton.ToolTip="File: click two spots to add a picture, GIF, video or music file for that part · "+Key(TrimAction.ImageTool);
        ShowShapeWay();
        VolumeToolButton.ToolTip="Volume: click two spots on an audio lane to make that part louder or quieter · "+Key(TrimAction.VolumeTool);
        SoundToolButton.ToolTip="Sound: click two spots to add music or a sound file · "+Key(TrimAction.SoundTool);
        MarkEndKey.Text=Key(TrimAction.MarkEnd); MarkEndButton.ToolTip="Set end at playhead · "+MarkEndKey.Text; AutomationProperties.SetName(MarkEndButton,"Set end, shortcut "+MarkEndKey.Text);
        foreach (var (menu,action) in new (MenuItem,TrimAction)[] { (OpenVideoMenu,TrimAction.OpenVideo),(SaveEditMenu,TrimAction.SaveProject),(ExportMenu,TrimAction.Export),(SnapshotMenu,TrimAction.Snapshot),
            (UndoMenu,TrimAction.Undo),(RedoMenu,TrimAction.Redo),(AddSectionMenu,TrimAction.AddSection),(SplitMenu,TrimAction.Split),(RemoveMenu,TrimAction.RemoveSection),
            (ZoomInMenu,TrimAction.ZoomIn),(ZoomOutMenu,TrimAction.ZoomOut),(GoToTimeMenu,TrimAction.GoToTime),(FullscreenMenu,TrimAction.Fullscreen),(ShortcutGuideMenu,TrimAction.ShortcutGuide) })
            menu.InputGestureText=Key(action);
    }
    // Steps along the preset speeds, starting from whatever speed the slider left.
    private void StepPreviewRate(int steps)
    {
        double rate=PreviewRate;
        for (int s=0; s<Math.Abs(steps); s++)
            rate = steps>0 ? previewRates.FirstOrDefault(r => r>rate+1e-9, MaxRate) : previewRates.LastOrDefault(r => r<rate-1e-9, MinRate);
        SetPreviewRate(rate);
    }
    private void RunAction(TrimAction action)
    {
        var none=new RoutedEventArgs();
        switch(action)
        {
            // Space keeps the speed picked in the speed control.
            case TrimAction.PlayPause or TrimAction.PlayPauseKeepSpeed: Play_Click(this,none); break;
            case TrimAction.Slower: StepPreviewRate(-1); break;
            case TrimAction.Faster: StepPreviewRate(1); break;
            case TrimAction.MuchSlower: StepPreviewRate(-2); break;
            case TrimAction.MuchFaster: StepPreviewRate(2); break;
            case TrimAction.Mute: userMuted=!userMuted; ApplyPreviewCuts(); ApplyPreviewVolume(); SetPreviewRate(PreviewRate); break;
            case TrimAction.VolumeUp or TrimAction.VolumeDown:
                PreviewVolumeSlider.Value=Math.Clamp(PreviewVolumeSlider.Value+(action==TrimAction.VolumeUp ? 10 : -10),0,200); SetPreviewRate(PreviewRate); break;
            case TrimAction.PreviousFrame or TrimAction.FrameBack: StepFrame(-1); break;
            case TrimAction.NextFrame or TrimAction.FrameForward: StepFrame(1); break;
            case TrimAction.HalfSecondBack: StepView(-.5); break;
            case TrimAction.HalfSecondForward: StepView(.5); break;
            case TrimAction.SecondBack: StepView(-1); break;
            case TrimAction.SecondForward: StepView(1); break;
            case TrimAction.TenSecondsBack: StepView(-10); break;
            case TrimAction.TenSecondsForward: StepView(10); break;
            case TrimAction.VideoStart: SeekView(0); break;
            case TrimAction.VideoEnd: SeekView(Timeline.Finished ? Timeline.Map.Total : media.Duration); break;
            case TrimAction.MarkedStart: SeekTo(Timeline.Start); break;
            case TrimAction.MarkedEnd: SeekTo(Timeline.End); break;
            case TrimAction.GoToTime: Timecode_Click(this,none); break;
            case TrimAction.Fullscreen: ToggleFullscreen(); break;
            case TrimAction.CutTool: CutTool_Click(this,none); break;
            case TrimAction.SpeedTool: SlowTool_Click(this,none); break;
            case TrimAction.ZoomTool: ZoomTool_Click(this,none); break;
            case TrimAction.TextTool: TextTool_Click(this,none); break;
            case TrimAction.ImageTool: ImageTool_Click(this,none); break;
            case TrimAction.ShapeTool: ShapeKey(); break;
            case TrimAction.DrawTool: DrawTool_Click(this,none); break;
            case TrimAction.FreezeFrame: FreezeHere_Click(this,none); break;
            case TrimAction.VolumeTool: VolumeTool_Click(this,none); break;
            case TrimAction.SoundTool: SoundTool_Click(this,none); break;
            case TrimAction.CopyPart: CopyPart(); break;
            case TrimAction.PastePart: PastePart(); break;
            case TrimAction.MarkStart: MarkStart_Click(this,none); break;
            case TrimAction.MarkEnd: MarkEnd_Click(this,none); break;
            case TrimAction.AddSection: Add_Click(this,none); break;
            case TrimAction.Split: Split_Click(this,none); break;
            case TrimAction.RemoveSection: if(!DeleteFocusedPart()) Remove_Click(this,none); break;
            case TrimAction.PreviousSection: NavigateSection(-1); break;
            case TrimAction.NextSection: NavigateSection(1); break;
            case TrimAction.FirstSection: NavigateSection(-1,true); break;
            case TrimAction.LastSection: NavigateSection(1,true); break;
            case TrimAction.Undo: Restore(undo,redo); break;
            case TrimAction.Redo: Restore(redo,undo); break;
            case TrimAction.ZoomIn: Timeline.Zoom(2,playhead); break;
            case TrimAction.ZoomOut: Timeline.Zoom(.5,playhead); break;
            case TrimAction.ZoomToggle: if(Timeline.ViewDuration>0 && Timeline.ViewDuration<media.Duration) Timeline.Fit(); else Timeline.Zoom(4,playhead); break;
            case TrimAction.Snapshot: Snapshot_Click(this,none); break;
            case TrimAction.Export: Export_Click(this,none); break;
            case TrimAction.OpenVideo: OpenVideo_Click(this,none); break;
            case TrimAction.SaveProject: SaveEdit(); break;
            case TrimAction.ShortcutGuide: ShortcutGuide_Click(this,none); break;
        }
    }
}

internal static class ShortcutGuide
{
    internal static StackPanel Content(IReadOnlyDictionary<TrimAction,string> keys)
    {
        var panel=new StackPanel {Margin=new Thickness(22)};
        panel.Children.Add(new TextBlock {Text="Editor shortcuts",FontSize=22,Margin=new Thickness(0,0,0,12)});
        foreach (var group in TrimShortcuts.All.GroupBy(s => s.Group))
        {
            var heading=new TextBlock {Text=group.Key,FontSize=12,Margin=new Thickness(0,12,0,4)}; heading.SetResourceReference(TextBlock.ForegroundProperty,"Muted"); panel.Children.Add(heading);
            foreach (var shortcut in group)
            {
                var row=new Grid {Margin=new Thickness(0,3,0,3)};row.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(235)});row.ColumnDefinitions.Add(new ColumnDefinition());
                var keyLabel=new TextBlock {Text=TrimShortcuts.Display(keys[shortcut.Action]),FontSize=12,TextWrapping=TextWrapping.Wrap};keyLabel.SetResourceReference(TextBlock.ForegroundProperty,"Accent");
                var description=new TextBlock {Text=shortcut.Label,FontSize=12,TextWrapping=TextWrapping.Wrap};Grid.SetColumn(description,1);row.Children.Add(keyLabel);row.Children.Add(description);panel.Children.Add(row);
            }
        }
        panel.Children.Add(new TextBlock {Text="Mouse wheel pans the timeline; Ctrl+wheel zooms around the pointer. Change any of these in Settings > Hotkeys. Preview speed, mute and volume never change recorded or exported audio.",TextWrapping=TextWrapping.Wrap,FontSize=11,Margin=new Thickness(0,14,0,0)});
        return panel;
    }
    internal static void Show(Window owner,IReadOnlyDictionary<TrimAction,string> keys)
    {
        var dialog=new Window {Owner=owner,Title="Shortcuts · Flashback",Width=700,Height=Math.Min(740,SystemParameters.WorkArea.Height),MinWidth=620,WindowStartupLocation=WindowStartupLocation.CenterOwner,
            Content=new ScrollViewer {Content=Content(keys),VerticalScrollBarVisibility=ScrollBarVisibility.Auto}};
        WindowTheme.Attach(dialog);dialog.PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){dialog.Close();e.Handled=true;}};dialog.ShowDialog();
    }
}
