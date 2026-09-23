using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Flashback;

public partial class TrimWindow
{
    internal double PreviewRate { get; private set; } = 1;
    private static readonly double[] previewRates={.25,.5,1,1.5,2,4};
    private void SetPreviewRate(double rate)
    {
        PreviewRate=rate; Player.SpeedRatio=rate;
        StatusLabel.Text=$"Preview {rate:0.##}× · {(Player.IsMuted ? "muted" : $"volume {Player.Volume:P0}")}";
    }
    private void StepFrame(int direction)
    {
        double frame=direction>0 ? Math.Floor(playhead*media.FrameRate+1e-6)+1 : Math.Ceiling(playhead*media.FrameRate-1e-6)-1;
        SeekTo(frame/media.FrameRate);
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
    private void ShortcutGuide_Click(object sender,RoutedEventArgs e) => ShortcutGuide.Show(this,addSectionHotkey);
    private bool HandleExtraKey(Key key,ModifierKeys modifiers)
    {
        if(modifiers==ModifierKeys.Control)
        {
            switch(key)
            {
                case Key.Up: Timeline.Zoom(2,playhead); return true;
                case Key.Down: Timeline.Zoom(.5,playhead); return true;
                case Key.Home: SeekTo(0);return true;
                case Key.End: SeekTo(media.Duration);return true;
            }
        }
        if(modifiers==(ModifierKeys.Control|ModifierKeys.Shift))
        {
            if(key==Key.Z) {Restore(redo,undo);return true;}
            if(key is Key.Left or Key.Right) {SeekTo(playhead+(key==Key.Left ? -10 : 10));return true;}
        }
        if(modifiers==ModifierKeys.Shift && key is Key.Home or Key.End) {SeekTo(key==Key.Home ? Timeline.Start : Timeline.End);return true;}
        if(modifiers==ModifierKeys.Alt && key is Key.Up or Key.Down)
        {Player.Volume=Math.Clamp(Player.Volume+(key==Key.Up ? .1 : -.1),0,1); SetPreviewRate(PreviewRate);return true;}
        if(modifiers is ModifierKeys.None or ModifierKeys.Shift && key is Key.J or Key.L)
        {
            int i=Array.IndexOf(previewRates,PreviewRate),step=modifiers==ModifierKeys.Shift ? 2 : 1;
            SetPreviewRate(previewRates[Math.Clamp(i+(key==Key.J ? -step : step),0,previewRates.Length-1)]);return true;
        }
        if(modifiers!=ModifierKeys.None) return false;
        switch(key)
        {
            case Key.OemComma: StepFrame(-1);break;
            case Key.OemPeriod: StepFrame(1);break;
            case Key.K: Play_Click(this,new RoutedEventArgs());break;
            case Key.M: Player.IsMuted=!Player.IsMuted;SetPreviewRate(PreviewRate);break;
            case Key.B: Split_Click(this,new RoutedEventArgs());break;
            case Key.Back: Remove_Click(this,new RoutedEventArgs());break;
            case Key.Up: NavigateSection(-1);break;
            case Key.Down: NavigateSection(1);break;
            case Key.PageUp: NavigateSection(-1,true);break;
            case Key.PageDown: NavigateSection(1,true);break;
            case Key.Z: if(Timeline.ViewDuration>0 && Timeline.ViewDuration<media.Duration) Timeline.Fit();else Timeline.Zoom(4,playhead);break;
            case Key.G: Timecode_Click(this,new RoutedEventArgs());break;
            case Key.C: Snapshot_Click(this,new RoutedEventArgs());break;
            case Key.E: Export_Click(this,new RoutedEventArgs());break;
            default:return false;
        }
        return true;
    }
}

internal static class ShortcutGuide
{
    internal static StackPanel Content(string add)
    {
        var panel=new StackPanel {Margin=new Thickness(22)};
        panel.Children.Add(new TextBlock {Text="Trimmer shortcuts",FontSize=22,Margin=new Thickness(0,0,0,12)});
        foreach(var (keys,action) in new[]{
            ("Space / K","Play/pause (Space resets speed; K keeps it)"),("J / L","Slower / faster preview · Shift changes two steps"),("M · Alt+↑ / ↓","Mute preview · volume up / down"),
            (", / .","Previous / next frame"),("← / →","One frame when timeline is focused"),("Shift+← / → · Ctrl+← / →","Half second · one second (timeline focus)"),("Ctrl+Shift+← / →","Back / forward 10 seconds"),
            ("I / O", "Set start / end at playhead"),(add,"Add marked section"),("B / S · Delete / Backspace","Split section · remove selected section"),("↑ / ↓ · Page Up / Down","Previous / next section · first / last section"),
            ("Shift+Home / End","Jump to marked start / end"),("Ctrl+Home / End · G","Video start / end · go to time"),("Ctrl+↑ / ↓ · Z","Zoom in / out · toggle zoom / fit"),("Wheel · Ctrl+wheel","Pan timeline · zoom around pointer"),
            ("E / C","Export sections / save snapshot"),("Ctrl+O / Ctrl+S","Open video / save trim project"),("Ctrl+Z · Ctrl+Y / Ctrl+Shift+Z","Undo · redo"),("Shift+/ (?)","Show this guide")})
        {
            var row=new Grid {Margin=new Thickness(0,5,0,5)};row.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(235)});row.ColumnDefinitions.Add(new ColumnDefinition());
            var keyLabel=new TextBlock {Text=keys,FontSize=12,TextWrapping=TextWrapping.Wrap};keyLabel.SetResourceReference(TextBlock.ForegroundProperty,"Accent");
            var description=new TextBlock {Text=action,FontSize=12,TextWrapping=TextWrapping.Wrap};Grid.SetColumn(description,1);row.Children.Add(keyLabel);row.Children.Add(description);panel.Children.Add(row);
        }
        panel.Children.Add(new TextBlock {Text="Local to the trimmer. Typing in fields and choosing dropdown options keeps normal keyboard behavior. Preview speed, mute and volume never change recorded or exported audio.",TextWrapping=TextWrapping.Wrap,FontSize=11,Margin=new Thickness(0,14,0,0)});
        return panel;
    }
    internal static void Show(Window owner,string add)
    {
        var dialog=new Window {Owner=owner,Title="Shortcuts · Flashback",Width=700,Height=Math.Min(740,SystemParameters.WorkArea.Height),MinWidth=620,WindowStartupLocation=WindowStartupLocation.CenterOwner,
            Content=new ScrollViewer {Content=Content(add),VerticalScrollBarVisibility=ScrollBarVisibility.Auto}};
        WindowTheme.Attach(dialog);dialog.PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){dialog.Close();e.Handled=true;}};dialog.ShowDialog();
    }
}

public partial class MainWindow
{
    private void TrimGuide_Click(object sender,RoutedEventArgs e)=>ShortcutGuide.Show(this,settings.TrimAddHotkey);
}
