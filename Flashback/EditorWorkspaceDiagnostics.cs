using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Flashback;

internal static class EditorWorkspaceDiagnostics
{
    internal static async Task RunAsync()
    {
        Directory.CreateDirectory(Storage.Root);
        void Check(bool ok,string message) { if(!ok) throw new Exception(message); File.AppendAllText(Path.Combine(Storage.Root,"workspace-results.txt"),"PASS "+message+"\n"); }
        string source=Path.Combine(Storage.Root,"long-source.mp4");
        await EditorDiagnostics.Ffmpeg("-y","-f","lavfi","-i","testsrc2=size=320x180:rate=30:duration=60","-f","lavfi","-i","sine=frequency=880:sample_rate=48000:duration=60","-c:v","libx264","-preset","ultrafast","-threads","2","-g","60","-pix_fmt","yuv420p","-c:a","aac",source);
        Storage.Save(new Settings { OutputFolder=Path.Combine(Storage.Root,"clips") });
        var trim=new TrimWindow(source,true);
        try
        {
            Check(trim.ExportMode.SelectedIndex==1 && trim.ModeHint.Text.Contains("keyframes"),"Rough cut is the default and describes its boundary behavior");
            trim.SeekTo(48); trim.HandleKey(Key.I,ModifierKeys.None,true); trim.SeekTo(51); trim.HandleKey(Key.O,ModifierKeys.None,true); trim.HandleKey(Key.Add,ModifierKeys.None,true);
            string project=Path.Combine(Storage.Root,"example.flashtrim"); trim.SaveProject(project);
            trim.SeekTo(10); trim.HandleKey(Key.I,ModifierKeys.None,true); trim.SeekTo(12); trim.HandleKey(Key.O,ModifierKeys.None,true);
            Check(trim.OpenProject(project,false) && trim.Timeline.Start==48 && trim.Timeline.End==51 && trim.SectionsList.Items.Count==1,"Project restores source, range, playhead and kept sections");
            trim.SeekTo(49); trim.HandleKey(Key.I,ModifierKeys.None,true); trim.Close();
            Check(TrimProject.Read(project).Start==49,"Closing immediately flushes a pending project autosave");
            var changed=TrimProject.Read(project) with {SourceBytes=1}; string invalid=Path.Combine(Storage.Root,"invalid.flashtrim"); File.WriteAllText(invalid,JsonSerializer.Serialize(changed));
            bool rejected=false; try { TrimProject.Read(invalid); } catch(IOException) { rejected=true; }
            Check(rejected,"Projects reject a changed original instead of silently applying stale cut times");
        }
        finally { trim.Close(); }
        var ui=new TrimWindow(source,true);
        try
        {
            var content=(FrameworkElement)ui.Content;
            EditorDiagnostics.Render(content,1120,900,"trim-default.png");
            ui.Timeline.Zoom(4,30);
            Check(Math.Abs(ui.Timeline.ViewDuration-15)<.001 && Math.Abs(ui.Timeline.TimeAt(ui.Timeline.XAt(30))-30)<.001,"Zoom preserves timeline hit testing around the playhead");
            ui.Timeline.Reveal(58); Check(ui.Timeline.ViewStart+ui.Timeline.ViewDuration>=58,"Keyboard seeking reveals offscreen positions"); ui.Timeline.Fit();
            ui.SharePreset.SelectedIndex=2; ui.SizeLimit.Text="10";
            Check(!ui.ExportMode.IsEnabled && ui.ModeHint.Text.Contains("Mbps"),"Share preset explains re-encoding and estimated bitrate");
            ui.SizeLimit.Text="NaN"; Check(ui.ModeHint.Text.Contains("limit"),"Invalid target sizes are reported without starting an encoder");
            ui.SizeLimit.Text="10";
            Appearance.Apply("Midnight","Lavender"); EditorDiagnostics.Render(content,860,700,"trim-midnight-small.png");
            Check(ui.PreviewFrame.ActualHeight>200 && ui.ExportButton.TranslatePoint(new Point(),content).X+ui.ExportButton.ActualWidth<=content.ActualWidth,"Small layout keeps preview and export accessible");
            Appearance.Apply("Slate","Rose"); EditorDiagnostics.Render(content,1120,900,"trim-slate.png");
            Check(((SolidColorBrush)Application.Current.FindResource("Accent")).Color.ToString()=="#FFF5AAC9","Accent changes update the shared brush");
            Check(!new Settings().RequiresBufferRestart(new Settings {Palette="Midnight",AccentColor="Blue"}),"Appearance changes do not restart recording");
        }
        finally { ui.Close(); Appearance.Apply("Charcoal","Mint"); }
        string output=Path.Combine(Storage.Root,"target-size.mp4");
        await ExportServices.PreciseAsync(source,output,new[]{new KeepSection(47.25,50.25),new KeepSection(55.25,58.25)},null,CancellationToken.None,options:new ShareExportOptions(1,720,30));
        Check(new FileInfo(output).Length<=1_000_000 && ClipMedia.Read(output).HasAudio && Math.Abs(ClipMedia.Read(output).Duration-6)<.1,"Hardware share export meets 1 MB limit and preserves joined audio/video duration");
        await EditorDiagnostics.Ffmpeg("-i",output,"-f","null","-"); Check(true,"Size-limited share output fully decodes");
        foreach(string ext in new[]{"png","jpg"})
        {
            string snapshot=Path.Combine(Storage.Root,"snapshot."+ext); await ExportServices.SnapshotAsync(source,snapshot,59.9,CancellationToken.None);
            using var stream=File.OpenRead(snapshot); var image=BitmapDecoder.Create(stream,BitmapCreateOptions.None,BitmapCacheOption.OnLoad);
            Check(image.Frames[0].PixelWidth==320 && image.Frames[0].PixelHeight==180,"Full-resolution "+ext+" snapshot decodes");
        }
        string cancelPath=Path.Combine(Storage.Root,"queued-cancel.mp4");
        await ExportServices.Gate.WaitAsync();
        try
        {
            using var cancel=new CancellationTokenSource(); var task=ExportServices.PreciseAsync(source,cancelPath,new[]{new KeepSection(0,2)},null,cancel.Token); cancel.Cancel();
            bool canceled=false; try { await task; } catch(OperationCanceledException) { canceled=true; }
            Check(canceled && !File.Exists(cancelPath),"Canceling an export queued behind another job leaves no output");
        }
        finally { ExportServices.Gate.Release(); }
        // Same codec/preset and retained duration; compare input seek with the previous decode-from-zero approach.
        var timer=Stopwatch.StartNew();
        await EditorDiagnostics.Ffmpeg("-i",source,"-vf","trim=start=55:end=58,setpts=PTS-STARTPTS","-af","atrim=start=55:end=58,asetpts=PTS-STARTPTS","-c:v","libx264","-preset","ultrafast","-threads","2","-c:a","aac",Path.Combine(Storage.Root,"baseline.mp4"));
        double baseline=timer.Elapsed.TotalMilliseconds; timer.Restart();
        await ExportServices.PreciseAsync(source,Path.Combine(Storage.Root,"seeked.mp4"),new[]{new KeepSection(55,58)},null,CancellationToken.None,true);
        double seeked=timer.Elapsed.TotalMilliseconds;
        Check(Math.Abs(ClipMedia.Read(Path.Combine(Storage.Root,"seeked.mp4")).Duration-3)<.1,"Late-file seek export preserves requested duration");
        File.WriteAllText(Path.Combine(Storage.Root,"benchmark.json"),JsonSerializer.Serialize(new {baselineMs=baseline,seekedMs=seeked,note="Single local synthetic run; process startup included. Not a gameplay benchmark."}));
        var main=new MainWindow(true);
        try
        {
            main.MainTabs.SelectedIndex=1;
            main.PaletteBox.SelectedItem="Midnight"; main.AccentBox.SelectedItem="Blue";
            Check(Storage.Load(out _).Palette=="Midnight" && Storage.Load(out _).AccentColor=="Blue","Appearance selection persists immediately");
            main.AppearanceSettings.IsSelected=true;
            EditorDiagnostics.Render((FrameworkElement)main.Content,1000,860,"appearance-settings.png");
        }
        finally { await main.QuitAsync(); }
    }
    private static T? GetParent<T>(this DependencyObject child) where T:DependencyObject
    { for(var p=LogicalTreeHelper.GetParent(child);p!=null;p=LogicalTreeHelper.GetParent(p)) if(p is T match) return match; return null; }
}


