using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
namespace Flashback;

internal static class TrimInteractionDiagnostics
{
    internal static async Task RunAsync()
    {
        Directory.CreateDirectory(Storage.Root);
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception(message);
            File.AppendAllText(Path.Combine(Storage.Root,"trim-interaction-results.txt"),"PASS "+message+"\n");
        }
        string path=Path.Combine(Storage.Root,"timeline-test.mp4");
        await EditorDiagnostics.Ffmpeg("-y","-f","lavfi","-i","testsrc2=size=640x360:rate=30:duration=12","-c:v","libx264","-threads","2","-preset","ultrafast","-pix_fmt","yuv420p",path);
        Check(Math.Abs(ClipMedia.Read(path).FrameRate-30)<.001,"Read source 30 FPS from MP4 timing table for frame stepping");
        Storage.Save(new Settings { OutputFolder=Path.Combine(Storage.Root,"clips") });
        var trim=new TrimWindow(path,true);
        try
        {
            trim.SeekTo(3); trim.RefreshPlayback();
            trim.HandleKey(Key.I,ModifierKeys.None,true);
            Check(Math.Abs(KeepSection.Parse(trim.StartBox.Text)-3)<.001,"I marks requested seek even when paused decoder still reports zero");
            trim.SeekTo(5); trim.RefreshPlayback(); trim.HandleKey(Key.O,ModifierKeys.None,true);
            Check(Math.Abs(KeepSection.Parse(trim.EndBox.Text)-5)<.001,"O marks requested seek after timer refresh");
            trim.HandleKey(Key.Right,ModifierKeys.None,true);
            Check(Math.Abs(trim.Playhead-(5+1.0/30))<1e-6,"Right arrow advances exactly one source frame");
            trim.HandleKey(Key.Left,ModifierKeys.None,true);
            Check(Math.Abs(trim.Playhead-5)<1e-6,"Left arrow reverses exactly one source frame");
            trim.HandleKey(Key.Right,ModifierKeys.Shift,true);
            Check(Math.Abs(trim.Playhead-5.5)<1e-6,"Shift arrow advances half a second");
            trim.HandleKey(Key.Left,ModifierKeys.Control,true);
            Check(Math.Abs(trim.Playhead-4.5)<1e-6,"Ctrl arrow reverses one second");
            Check(!trim.HandleKey(Key.Right,ModifierKeys.None,false),"Arrows retain normal navigation outside timeline focus");
            Check(!trim.HandleKey(Key.I,ModifierKeys.None,true,true),"Editing timestamp text does not trigger I/O shortcuts");
            trim.HandleKey(Key.OemPlus,ModifierKeys.Shift,true);
            Check(trim.SectionsList.Items.Count==1,"Main keyboard plus adds the marked section");
            trim.SeekTo(7); trim.HandleKey(Key.I,ModifierKeys.None,true); trim.SeekTo(9); trim.HandleKey(Key.O,ModifierKeys.None,true);
            trim.HandleKey(Key.Add,ModifierKeys.None,true);
            Check(trim.SectionsList.Items.Count==2,"Numpad plus adds a second marked section");
            trim.HandleKey(Key.Z,ModifierKeys.Control,true);
            Check(trim.SectionsList.Items.Count==1,"Undo restores section list after plus shortcut");
            trim.HandleKey(Key.Y,ModifierKeys.Control,true);
            Check(trim.SectionsList.Items.Count==2,"Redo restores the added section");
            trim.HandleKey(Key.Home,ModifierKeys.None,true); trim.HandleKey(Key.Left,ModifierKeys.Control,true);
            Check(trim.Playhead==0,"Keyboard scrubbing clamps to start");
            trim.HandleKey(Key.End,ModifierKeys.None,true); trim.HandleKey(Key.Right,ModifierKeys.Control,true);
            Check(trim.Playhead==12,"Keyboard scrubbing clamps to end");
            var content=(FrameworkElement)trim.Content;
            EditorDiagnostics.Render(content,1120,900,"trim-wide.png");
            Check(trim.PreviewFrame.ActualWidth==trim.Timeline.ActualWidth && trim.PreviewFrame.ActualWidth>1000,"Preview spans the same full width as the unified timeline");
            Check(trim.PreviewFrame.ActualHeight>370,"Preview retains a large video area beside the compact zoom overview");
            trim.Timeline.Start=2; trim.Timeline.End=10;
            trim.Timeline.BeginDrag(new Point(trim.Timeline.XAt(6),38)); trim.Timeline.MoveTo(trim.Timeline.XAt(7)); trim.Timeline.EndDrag();
            Check(Math.Abs(trim.Playhead-7)<.001 && trim.Timeline.Start==2 && trim.Timeline.End==10,"Click-drag in track scrubs without moving trim edges");
            trim.Timeline.BeginDrag(new Point(trim.Timeline.XAt(2)+10,38)); trim.Timeline.MoveTo(trim.Timeline.XAt(3)+10); trim.Timeline.EndDrag();
            Check(Math.Abs(trim.Timeline.Start-3)<.001 && Math.Abs(trim.Playhead-3)<.001,"Large handle hit area preserves grab offset and previews the dragged edge");
            EditorDiagnostics.Render(content,860,700,"trim-small.png");
            Check(trim.ExportButton.TranslatePoint(new Point(),content).X+trim.ExportButton.ActualWidth<=content.ActualWidth+.1,"Export stays inside minimum window width");
            Check(trim.PreviewFrame.ActualHeight>170 && trim.Timeline.ActualHeight>=56,"Minimum layout retains preview and large timeline targets");
            trim.ExportMode.SelectedIndex=1;
            Check(trim.ModeHint.Text.Contains("keyframes"),"Rough mode describes its keyframe limitation next to export");
            trim.SeekTo(4);trim.HandleKey(Key.OemPeriod,ModifierKeys.None,false);trim.HandleKey(Key.OemComma,ModifierKeys.None,false);
            Check(Math.Abs(trim.Playhead-4)<.001,"Comma and period step source frames outside timeline focus");
            trim.HandleKey(Key.J,ModifierKeys.None,false);
            Check(trim.PreviewRate==.75,"J slows preview one step without changing the source");
            for(int i=0;i<8;i++)trim.HandleKey(Key.L,ModifierKeys.Shift,false);
            Check(trim.PreviewRate==4,"Fast preview clamps to 4x");
            trim.SpeedSlider.Value=trim.SpeedSlider.Minimum;
            Check(trim.PreviewRate==.1 && (string)trim.SpeedButton.Content=="0.1×","Speed slider reaches 0.1x and labels the button");
            trim.SpeedSlider.Value=Math.Log(1.62);
            Check(trim.PreviewRate==1.6,"Speed slider rounds to tidy steps");
            trim.HandleKey(Key.L,ModifierKeys.None,false);
            Check(trim.PreviewRate==2,"Stepping from a slider speed moves to the next preset");
            trim.HandleKey(Key.Space,ModifierKeys.None,false); trim.HandleKey(Key.Space,ModifierKeys.None,false);
            Check(trim.PreviewRate==2,"Space keeps the chosen speed");
            trim.SpeedSlider.Value=0;
            Check(trim.PreviewRate==1 && (string)trim.SpeedButton.Content=="1×","Speed returns to normal");
            var cutAdded=typeof(TrimWindow).GetMethod("CutAdded",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
            var before=trim.Timeline.Cuts.Count;
            cutAdded.Invoke(trim,new object[]{new CutRegion(-1,1,2)});
            Check(trim.Timeline.Cuts.Count==before+1,"Cut out adds a cut");
            trim.HandleKey(Key.Z,ModifierKeys.Control,false);
            Check(trim.Timeline.Cuts.Count==before,"Undo removes the last cut out");
            trim.HandleKey(Key.Y,ModifierKeys.Control,false);
            Check(trim.Timeline.Cuts.Count==before+1,"Redo brings the cut out back");
            // Click-click cutting: first click starts, second finishes; a drag moves the playhead instead.
            var tl=trim.Timeline; tl.Fit(); trim.UpdateLayout(); trim.SeekTo(6); tl.CutMode=true;
            var place=typeof(TrimTimeline).GetMethod("PlaceCutPoint",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
            int cutsBefore=tl.Cuts.Count;
            place.Invoke(tl,new object[]{new Point(tl.XAt(3),26)});
            Check(tl.HasPendingCut && tl.Cuts.Count==cutsBefore,"First cut click only marks the start");
            place.Invoke(tl,new object[]{new Point(tl.XAt(6)+2,26)});
            var made=tl.Cuts[tl.Cuts.Count-1];
            Check(!tl.HasPendingCut && made.Lane==-1 && Math.Abs(made.Start-3)<.02 && Math.Abs(made.End-6)<1e-9,"Second click finishes the cut and locks onto the playhead within 3 px");
            place.Invoke(tl,new object[]{new Point(tl.XAt(8),26)}); trim.HandleKey(Key.Escape,ModifierKeys.None,true);
            Check(!tl.HasPendingCut && !tl.CutMode,"One Esc leaves the cut tool and drops a half-placed cut");
            trim.HandleKey(Key.X,ModifierKeys.None,false); Check(tl.CutMode,"X turns on the cut tool");
            trim.HandleKey(Key.R,ModifierKeys.None,false); Check(tl.SlowMode && !tl.CutMode,"R switches to the speed tool");
            trim.HandleKey(Key.R,ModifierKeys.None,false); Check(!tl.SlowMode,"R again leaves the speed tool");
            // Slow motion: two clicks add a 0.5x part; it can't overlap a cut; its tag changes speed; undo works.
            tl.SlowMode=true; int slowBefore=tl.SlowRegions.Count;
            place.Invoke(tl,new object[]{new Point(tl.XAt(7),26)}); place.Invoke(tl,new object[]{new Point(tl.XAt(8.5),26)});
            Check(tl.SlowRegions.Count==slowBefore+1 && tl.SlowRegions[^1].Speed==.5 && Math.Abs(tl.SlowRegions[^1].Start-7)<.02,"Two clicks add a 0.5x slow-motion part");
            place.Invoke(tl,new object[]{new Point(tl.XAt(8),26)}); place.Invoke(tl,new object[]{new Point(tl.XAt(9.5),26)}); place.Invoke(tl,new object[]{new Point(tl.XAt(5),26)}); place.Invoke(tl,new object[]{new Point(tl.XAt(6.5),26)});
            Check(tl.SlowRegions.Count==slowBefore+1,"Slow motion can't overlap a cut or another slow part");
            var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
            typeof(TrimWindow).GetMethod("SlowTagClicked",flags)!.Invoke(trim,new object[]{tl.SlowRegions.Count-1});
            typeof(TrimWindow).GetMethod("SetSlowSpeed",flags)!.Invoke(trim,new object[]{.25,false});
            Check(tl.SlowRegions[^1].Speed==.25,"The speed tag changes a part's speed");
            trim.RegionSpeedSlider.Value=Math.Log(3.04);
            Check(tl.SlowRegions[^1].Speed==3,"The speed slider reaches faster speeds, rounded to tidy steps");
            trim.RegionSpeedSlider.Value=trim.RegionSpeedSlider.Minimum;
            Check(Math.Abs(tl.SlowRegions[^1].Speed-.1)<1e-9,"The speed slider goes down to 0.1x");
            trim.HandleKey(Key.Z,ModifierKeys.Control,false);
            Check(tl.SlowRegions[^1].Speed==.25,"Undo reverts a whole slider drag in one step");
            trim.SlowPopup.IsOpen=false;
            cutsBefore=tl.Cuts.Count; tl.CutMode=true;
            place.Invoke(tl,new object[]{new Point(tl.XAt(7.2),26)}); place.Invoke(tl,new object[]{new Point(tl.XAt(7.8),26)});
            Check(tl.Cuts.Count==cutsBefore,"Cuts can't be placed over slow motion");
            tl.CutMode=false;
            trim.HandleKey(Key.Z,ModifierKeys.Control,false);
            Check(tl.SlowRegions[^1].Speed==.5,"Undo reverts a slow-motion speed change");
            trim.HandleKey(Key.Z,ModifierKeys.Control,false);
            Check(tl.SlowRegions.Count==slowBefore,"Undo removes an added slow-motion part");
            // Zoom: snaps to a speed part's edge, opens its editor, aims with the target box, refuses overlapping zooms, undoes.
            trim.SeekTo(2); typeof(TrimWindow).GetMethod("SlowAdded",flags)!.Invoke(trim,new object[]{7.0,8.5});
            trim.HandleKey(Key.Q,ModifierKeys.None,false); Check(tl.ZoomMode && !tl.SlowMode && !tl.CutMode,"Q switches to the zoom tool");
            place.Invoke(tl,new object[]{new Point(tl.XAt(7)+2,26)}); place.Invoke(tl,new object[]{new Point(tl.XAt(9),26)});
            Check(tl.ZoomRegions.Count==1 && tl.ZoomRegions[0].Start==7 && Math.Abs(tl.ZoomRegions[0].End-9)<.05,"Zoom placement snaps to a speed part's edge");
            Check(trim.ZoomPanel.Visibility==Visibility.Visible && tl.SelectedZoom==0,"A new zoom opens its editor beside the video");
            trim.ZoomTarget.Set(.8,.3,3);
            typeof(TrimWindow).GetMethod("ZoomTargetChanged",flags)!.Invoke(trim,null);
            Check(Math.Abs(tl.ZoomRegions[0].X-.8)<1e-9 && Math.Abs(tl.ZoomRegions[0].MaxZoom-3)<1e-9 && trim.ZoomGraph.MaxZoom==3,"The target box aims the zoom and sets max zoom, and the graph follows");
            place.Invoke(tl,new object[]{new Point(tl.XAt(8),26)}); place.Invoke(tl,new object[]{new Point(tl.XAt(10),26)});
            Check(tl.ZoomRegions.Count==1,"Zooms can't overlap each other");
            trim.HandleKey(Key.Escape,ModifierKeys.None,true); Check(!tl.ZoomMode,"Esc leaves the zoom tool");
            typeof(TrimWindow).GetMethod("ZoomRemove_Click",flags)!.Invoke(trim,new object[]{trim,new RoutedEventArgs()});
            Check(tl.ZoomRegions.Count==0 && trim.ZoomPanel.Visibility!=Visibility.Visible,"Remove zoom closes the editor");
            trim.HandleKey(Key.Z,ModifierKeys.Control,false);
            Check(tl.ZoomRegions.Count==1,"Undo brings a removed zoom back");
            trim.HandleKey(Key.Z,ModifierKeys.Control,false); trim.HandleKey(Key.Z,ModifierKeys.Control,false); trim.HandleKey(Key.Z,ModifierKeys.Control,false);
            // Text and pictures: two clicks add one on the lowest free layer, the panel edits it, layers restack, undo works.
            int zoomsLeft=tl.ZoomRegions.Count, slowLeft=tl.SlowRegions.Count;
            trim.HandleKey(Key.T,ModifierKeys.None,false); Check(tl.OverlayMode==OverlayKind.Text && !tl.ZoomMode && !tl.CutMode,"T switches to the text tool");
            place.Invoke(tl,new object[]{new Point(tl.XAt(2),26)}); place.Invoke(tl,new object[]{new Point(tl.XAt(4),26)});
            Check(tl.Overlays.Count==1 && tl.Overlays[0].Kind==OverlayKind.Text && Math.Abs(tl.Overlays[0].Start-2)<.05 && Math.Abs(tl.Overlays[0].End-4)<.05,"Two clicks add text for that stretch");
            Check(trim.OverlayPanel.Visibility==Visibility.Visible && tl.SelectedOverlay==0 && trim.ZoomPanel.Visibility!=Visibility.Visible,"New text opens its editor beside the video");
            Check(tl.ActualHeight>0 && tl.Height>tl.PreferredHeight-1,"The timeline grows a slim layer row for text");
            place.Invoke(tl,new object[]{new Point(tl.XAt(3),30)}); place.Invoke(tl,new object[]{new Point(tl.XAt(5),30)});
            Check(tl.Overlays.Count==2 && tl.Overlays[0].Layer==0 && tl.Overlays[1].Layer==1,"Overlapping text goes on the layer above");
            var textBox=(System.Windows.Controls.TextBox)typeof(TrimWindow).GetField("overlayTextBox",flags)!.GetValue(trim)!;
            textBox.Text="Hello there";
            Check(tl.Overlays[1].Text=="Hello there" && trim.OverlayView.Items[1].Text=="Hello there","Typing in the panel changes the text on the video");
            trim.SeekTo(3.5); EditorDiagnostics.Render(content,1120,900,"trim-overlays.png");
            typeof(TrimWindow).GetMethod("Restack",flags)!.Invoke(trim,new object[]{-1});
            Check(tl.Overlays[1].Layer==0 && tl.Overlays[0].Layer==1,"Send back trades layers with the overlapping item");
            var newer=OverlayItem.NewText(0,1,tl.Overlays[1]);
            Check(newer.Text=="Your text" && newer.Font==tl.Overlays[1].Font,"New text starts with the last text's style");
            trim.HandleKey(Key.Escape,ModifierKeys.None,true); Check(tl.OverlayMode==null,"Esc leaves the text tool");
            for(int i=0;i<4;i++) trim.HandleKey(Key.Z,ModifierKeys.Control,false);
            Check(tl.Overlays.Count==0 && trim.OverlayView.Items.Count==0 && tl.ZoomRegions.Count==zoomsLeft && tl.SlowRegions.Count==slowLeft,"Undo steps back through restacking, typing and adding text");
            trim.HandleKey(Key.P,ModifierKeys.None,false); Check(tl.OverlayMode==OverlayKind.Image,"P switches to the picture tool");
            trim.HandleKey(Key.P,ModifierKeys.None,false); Check(tl.OverlayMode==null,"P again leaves the picture tool");
            var sticker=Path.Combine(Storage.Root,"interaction-sticker.png");
            var pixels=new byte[64*64*4]; for(int i=0;i<pixels.Length;i+=4){pixels[i+1]=200;pixels[i+3]=255;}
            using(var file=File.Create(sticker)){var png=new System.Windows.Media.Imaging.PngBitmapEncoder();png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(System.Windows.Media.Imaging.BitmapSource.Create(64,64,96,96,System.Windows.Media.PixelFormats.Bgra32,null,pixels,256)));png.Save(file);}
            trim.SeekTo(6);
            Check(trim.DropPicture(sticker) && tl.Overlays.Count==1 && tl.Overlays[0].Kind==OverlayKind.Image && Math.Abs(tl.Overlays[0].Start-6)<.01 && tl.Overlays[0].Scale<1,"Dropping a picture adds it at the playhead at its own size");
            trim.HandleKey(Key.Z,ModifierKeys.Control,false);
            Check(tl.Overlays.Count==0 && trim.OverlayPanel.Visibility!=Visibility.Visible,"Undo removes the dropped picture and closes its editor");
            trim.Timeline.SelectedSection=-1;
            double spanBefore=trim.Timeline.VisibleDuration;
            trim.Timeline.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice,0,120){RoutedEvent=UIElement.MouseWheelEvent});
            Check(trim.Timeline.VisibleDuration<spanBefore-.01,"Scroll wheel zooms the timeline in");
            trim.Timeline.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice,0,-120){RoutedEvent=UIElement.MouseWheelEvent});
            Check(Math.Abs(trim.Timeline.VisibleDuration-spanBefore)<.01,"Scrolling back zooms out by the same amount");
            bool wasMuted=trim.Player.IsMuted;trim.HandleKey(Key.M,ModifierKeys.None,false);
            Check(trim.Player.IsMuted!=wasMuted,"M toggles preview audio");
            for(int i=0;i<15;i++)trim.HandleKey(Key.Down,ModifierKeys.Alt,false);
            Check(trim.Player.Volume==0,"Preview volume clamps at zero");
            int beforeRepeat=trim.SectionsList.Items.Count;
            trim.HandleKey(Key.OemPlus,ModifierKeys.Shift,false,false,true);
            Check(!trim.HandleKey(Key.M,ModifierKeys.None,false,true) && trim.SectionsList.Items.Count==beforeRepeat,"Typing and held add-section keys do not trigger editor actions");
            trim.HandleKey(Key.PageUp,ModifierKeys.None,false);
            Check(trim.SectionsList.SelectedIndex==0,"Page Up selects the first kept section");
            trim.HandleKey(Key.Down,ModifierKeys.None,false);
            Check(trim.SectionsList.SelectedIndex==1,"Down selects the next kept section");
            trim.HandleKey(Key.Home,ModifierKeys.Shift,false);
            Check(Math.Abs(trim.Playhead-trim.Timeline.Start)<.001,"Shift Home seeks to the marked in point");
            trim.HandleKey(Key.Up,ModifierKeys.Control,false);
            Check(trim.Timeline.ViewDuration<12,"Ctrl Up zooms the timeline");
            trim.HandleKey(Key.Z,ModifierKeys.None,false);
            Check(trim.Timeline.ViewDuration==0,"Z restores the full timeline");
            trim.Timeline.Zoom(4,6);
            Check(trim.TimelineZoomLabel.Text.Contains("4×") && trim.TimelineFitButton.IsEnabled,"Timeline zoom is explicitly labeled and exposes Fit");
            double markedStart=trim.Timeline.Start,markedEnd=trim.Timeline.End,oldPlayhead=trim.Playhead;
            trim.Timeline.PanTo(7);
            Check(trim.Timeline.ViewStart==7 && trim.Timeline.Start==markedStart && trim.Timeline.End==markedEnd && trim.Playhead==oldPlayhead,"Panning preserves trim marks and playhead");
            EditorDiagnostics.Render(content,1120,900,"trim-zoomed.png");
            Check(Math.Abs(trim.Timeline.ZoomFactor-4)<.001,"Timeline reports a 4x zoom for its scrollbar and badge");
            EditorDiagnostics.Render(content,860,700,"trim-zoomed-small.png");
            trim.Timeline.Fit();
            Check(trim.TimelineZoomLabel.Text=="1×" && !trim.TimelineFitButton.IsEnabled,"Fit restores the full-clip indicator");
            Check(trim.SeekTimecode("00:00:08.500") && trim.Playhead==8.5 && !trim.SeekTimecode("99") && !trim.SeekTimecode("garbage"),"Go-to-time validates input and preserves the current position on invalid input");
            EditorDiagnostics.Render(ShortcutGuide.Content(TrimShortcuts.Resolve(new Settings())),680,720,"shortcut-guide.png");
        }
        finally { trim.Close(); }
        var settings=Storage.Load(out _); settings.TrimAddHotkey="Ctrl+K"; Storage.Save(settings);
        Check(Storage.Load(out _).TrimAddHotkey=="Ctrl+K" && !settings.RequiresBufferRestart(new Settings { OutputFolder=settings.OutputFolder }),"Custom trim shortcut persists without restarting recording");
        var custom=new TrimWindow(path,true);
        try { custom.StartBox.Text="1"; custom.EndBox.Text="2"; custom.HandleKey(Key.K,ModifierKeys.Control,true); Check(custom.SectionsList.Items.Count==1,"Custom section shortcut is used by the trimmer"); }
        finally { custom.Close(); }
        Check(TrimShortcuts.Format(Key.OemPlus,ModifierKeys.Shift)=="+" && TrimShortcuts.Format(Key.Add,ModifierKeys.None)=="+","Both plus keys normalize to the visible default binding");
        bool rejected=false; try { new Settings { TrimAddHotkey="I" }.Validate(); } catch(ArgumentException) { rejected=true; }
        Check(rejected,"Custom section shortcut cannot shadow built-in trim marks");
        var empty=new TrimWindow(renderOnly:true);
        try
        {
            Check(empty.EmptyState.Visibility==Visibility.Visible && empty.TrimContent.Visibility==Visibility.Collapsed,"Standalone trimmer opens without selecting a saved clip");
            EditorDiagnostics.Render((FrameworkElement)empty.Content,1000,760,"trim-empty.png");
            var data=new DataObject(DataFormats.FileDrop,new[] { path });
            Check(TrimImport.CanDrop(TrimImport.Files(data)),"Explorer file-drop data accepts one supported video");
            Check(!TrimImport.CanDrop(new[]{path,path}) && !TrimImport.CanDrop(new[]{"not-a-video.txt"}),"Multiple files and unsupported types are rejected at drag-over");
            Check(empty.LoadClip(path,false) && empty.SourcePath==Path.GetFullPath(path) && empty.TrimContent.Visibility==Visibility.Visible,"Dropped external video loads directly into the editor");
            empty.SeekTo(4); empty.HandleKey(Key.I,ModifierKeys.None,true);
            string broken=Path.Combine(Storage.Root,"broken.mp4"); File.WriteAllText(broken,"invalid video");
            bool invalid=false; try { empty.LoadClip(broken,false); } catch(IOException) { invalid=true; }
            Check(invalid && empty.SourcePath==Path.GetFullPath(path) && empty.Timeline.Start==4,"Invalid replacement file preserves the open source and trim range");
            string second=Path.Combine(Storage.Root,"second.mov"); File.Copy(path,second,true);
            Check(empty.LoadClip(second,false) && empty.Timeline.Start==0 && empty.SectionsList.Items.Count==0 && empty.Playhead==0,"Valid replacement resets the previous edit and playhead");
            Check(!empty.HandleKey(Key.Z,ModifierKeys.Control,false,true),"Text undo remains local to timestamp fields");
        }
        finally { empty.Close(); }
    }
    internal static async Task PreviewAsync()
    {
        Directory.CreateDirectory(Storage.Root);
        string path=Path.Combine(Storage.Root,"timeline-test.mp4");
        var live=new TrimWindow(path);
        try
        {
            live.Show();
            var until=System.Diagnostics.Stopwatch.StartNew();
            while (!live.Player.NaturalDuration.HasTimeSpan && until.Elapsed.TotalSeconds<12) await Task.Delay(100);
            if (!live.Player.NaturalDuration.HasTimeSpan) throw new Exception("Windows preview did not open the fixture.");
            live.SeekTo(3); await Task.Delay(400); live.RefreshPlayback(); live.HandleKey(Key.I,ModifierKeys.None,true);
            File.AppendAllText(Path.Combine(Storage.Root,"seek-log.txt"),$"Paused seek 3: {live.Player.Position.TotalSeconds}\n");
            if (Math.Abs(KeepSection.Parse(live.StartBox.Text)-3)>.001) throw new Exception("Live I mark lost the seek.");
            live.SeekTo(6); await Task.Delay(400); live.HandleKey(Key.O,ModifierKeys.None,true);
            File.AppendAllText(Path.Combine(Storage.Root,"seek-log.txt"),$"Paused seek 6: {live.Player.Position.TotalSeconds}\n");
            if (Math.Abs(KeepSection.Parse(live.EndBox.Text)-6)>.001) throw new Exception("Live O mark lost the seek.");
            live.HandleKey(Key.Space,ModifierKeys.None,true); await Task.Delay(1400);
            if (live.Playhead<6.7 || live.Playhead>8.5) throw new Exception($"Playback did not resume from requested seek: UI {live.Playhead}, decoder {live.Player.Position.TotalSeconds}, button {live.PlayToggle.Content}, visible {live.IsVisible}; {live.StatusLabel.Text}");
            live.HandleKey(Key.Space,ModifierKeys.None,true);
            live.SeekTo(7); live.HandleKey(Key.Right,ModifierKeys.None,true); await Task.Delay(400); live.RefreshPlayback();
            if (Math.Abs(live.Playhead-(7+1.0/30))>.001) throw new Exception("Live paused frame step was overwritten.");
            File.WriteAllText(Path.Combine(Storage.Root,"live-preview-results.txt"),"PASS Windows preview opens, seeks, marks I/O, resumes playback and preserves paused frame stepping.\n");
        }
        finally { live.Close(); }
    }
}
