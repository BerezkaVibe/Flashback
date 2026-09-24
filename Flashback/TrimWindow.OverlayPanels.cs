using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Flashback;

// The parts of the overlay panel for shapes, videos and keyframes, and typing text right on the video.
public partial class TrimWindow
{
    // ---- Shapes ----
    private static readonly (string Name, OverlayShape Shape)[] ShapeChoices =
    {
        ("Rectangle", OverlayShape.Box), ("Pill", OverlayShape.Pill), ("Oval", OverlayShape.Ellipse), ("Triangle", OverlayShape.Triangle),
        ("Diamond", OverlayShape.Diamond), ("Hexagon", OverlayShape.Hexagon), ("Star", OverlayShape.Star), ("Heart", OverlayShape.Heart),
        ("Burst", OverlayShape.Burst), ("Circle outline", OverlayShape.Ring), ("Arrow", OverlayShape.Arrow), ("Speech bubble", OverlayShape.Bubble),
        ("Tick", OverlayShape.Check), ("Cross", OverlayShape.Cross), ("Custom shape (drag its corners)", OverlayShape.Custom),
    };
    // Shapes a picture or video can be cut to.
    private static readonly (string, OverlayShape)[] MaskChoices =
    {
        ("Rectangle", OverlayShape.Box), ("Oval", OverlayShape.Ellipse), ("Pill", OverlayShape.Pill), ("Triangle", OverlayShape.Triangle), ("Diamond", OverlayShape.Diamond),
        ("Hexagon", OverlayShape.Hexagon), ("Star", OverlayShape.Star), ("Heart", OverlayShape.Heart), ("Burst", OverlayShape.Burst),
    };
    private static Geometry ShapeIcon(OverlayShape shape) =>
        OverlayRenderer.ShapeIn(shape, new Rect(-12, -9, 24, 18), new OverlayItem { Kind = OverlayKind.Shape, Shape = shape, Corner = 4, TailX = -7, TailY = 15, ShapeWidth = 24, ShapeHeight = 18 });
    private void BuildShapePanel()
    {
        // A gallery of shapes; stickers like the heart, star, burst, tick and cross are shapes too.
        var gallery = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
        foreach (var (name, kind) in ShapeChoices)
        {
            var icon = new System.Windows.Shapes.Path { Data = ShapeIcon(kind), Stretch = Stretch.Uniform, Width = 20, Height = 16 };
            icon.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Ink");
            var button = new Button { Content = icon, Width = 34, Height = 30, MinHeight = 30, Padding = new Thickness(0), Margin = new Thickness(0, 0, 4, 4), ToolTip = name };
            button.SetResourceReference(StyleProperty, "IconButton");
            AutomationProperties.SetName(button, name);
            button.Click += (_, _) => { EditOverlay("shapeKind", o => o with { Shape = kind, ShapeCorners = OverlayItem.NoCorners, Points = OverlayItem.DefaultPoints }); LoadOverlayUi(); };
            overlayRefresh.Add(o =>
            {
                if (o.Shape == kind) { button.SetResourceReference(Control.BorderBrushProperty, "Accent"); icon.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Accent"); }
                else { button.ClearValue(Control.BorderBrushProperty); icon.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Ink"); }
            });
            gallery.Children.Add(button);
        }
        OverlayControls.Children.Add(gallery);

        Combo("Fill", "region", new (string, OverlayRegion)[] { ("Color", OverlayRegion.Solid), ("Blur what's under it", OverlayRegion.Blur), ("Pixelate what's under it", OverlayRegion.Pixelate) }, o => o.Region, (o, v) => o with { Region = v });
        When(SliderRow("Amount", "regionStrength", 2, 120, o => o.RegionStrength, (o, v) => o with { RegionStrength = Math.Round(v) }, v => $"{v:0}", tip: "How strong the blur is, or how big the blocks are"), o => o.IsRegion);
        When(Hint("Hides whatever is under the shape: faces, names, messages. Move and resize it like any other shape."), o => o.IsRegion);
        bool Solid(OverlayItem o) => !o.IsRegion;
        When(Row("Color", Swatch("shapeColor", o => o.ShapeColor, (o, c) => o with { ShapeColor = c }, alpha: true)), Solid);
        When(OpacityOfColor("shapeOpacity", o => o.ShapeColor, (o, c) => o with { ShapeColor = c }), Solid);
        var borderRow = new StackPanel { Orientation = Orientation.Horizontal };
        borderRow.Children.Add(Swatch("shapeBorder", o => o.ShapeBorderColor, (o, c) => o with { ShapeBorderColor = c }));
        When(Row("Border", borderRow), Solid);
        When(SliderRow("Width", "shapeBorderWidth", 0, 24, o => o.ShapeBorderWidth, (o, v) => o with { ShapeBorderWidth = Math.Round(v, 1) }, v => v < .05 ? "Off" : $"{v:0.#}", tip: "Border thickness"), Solid);
        When(SliderRow("Corners", "corner", 0, 120, o => o.Corner, (o, v) => o with { Corner = Math.Round(v) }, v => $"{v:0}"), o => o.Shape is OverlayShape.Box or OverlayShape.Bubble);

        Header("Size");
        SliderRow("Width", "shapeWidth", 20, 2000, o => o.ShapeWidth, (o, v) => o with { ShapeWidth = Math.Round(v) }, v => $"{v:0}", tip: "Or drag the side handles on the video");
        SliderRow("Height", "shapeHeight", 20, 2000, o => o.ShapeHeight, (o, v) => o with { ShapeHeight = Math.Round(v) }, v => $"{v:0}", tip: "Or drag the bottom handle on the video");
        var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        SmallButton(buttons, "Reshape", "Drag the shape's corners on the video · or double-click the shape", () =>
        {
            if (SelectedOverlayItem is { } o && (playhead < o.Start || playhead >= o.End)) OpenOverlay(Timeline.SelectedOverlay);
            OverlayView.SetShapeEditing(true);
        });
        var reset = SmallButton(buttons, "Reset shape", "Put the corners back", () => EditOverlay("resetShape", o => o with { Points = OverlayItem.DefaultPoints, ShapeCorners = OverlayItem.NoCorners }));
        overlayRows.Add((reset, o => o.CornersMoved || !o.Points.SequenceEqual(OverlayItem.DefaultPoints)));
        OverlayControls.Children.Add(buttons);
        When(Hint("Double-click the shape on the video, then drag the pink corners (bubble tails drag too). Esc when done."), o => o.Shape != OverlayShape.Custom);
        When(Hint("Double-click the shape on the video, then drag the pink corners. Double-click an edge to add a corner; right-click one to remove it."), o => o.Shape == OverlayShape.Custom);
    }
    // A colour's alpha as its own opacity slider.
    private Grid OpacityOfColor(string control, Func<OverlayItem, string> get, Func<OverlayItem, string, OverlayItem> set) =>
        SliderRow("Opacity", control, 0, 1, o => OverlayRenderer.ParseColor(get(o), Colors.Black).A / 255.0,
            (o, v) => { var c = OverlayRenderer.ParseColor(get(o), Colors.Black); return set(o, $"#{(byte)Math.Round(v * 255):X2}{c.R:X2}{c.G:X2}{c.B:X2}"); }, v => $"{v * 100:0}%");

    // ---- Pictures and videos ----
    private void ReplacePicture()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Replace picture", Filter = "Pictures and GIFs|" + string.Join(";", PictureExtensions.Select(x => "*" + x)), CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        if (OverlayRenderer.PictureSize(OverlayItem.NewImage(0, 1, dialog.FileName)) == null) { StatusLabel.Text = "That picture couldn't be opened."; return; }
        EditOverlay("replace", o => o with { ImagePath = dialog.FileName }); LoadOverlayUi();
    }
    private void ReplaceVideo()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Replace video", Filter = "Videos|" + string.Join(";", VideoExtensions.Select(x => "*" + x)), CheckFileExists = true };
        if (dialog.ShowDialog(this) != true || NewMedia(0, 1, dialog.FileName) is not { Kind: OverlayKind.Video } added) return;
        EditOverlay("replace", o => o with { VideoPath = added.VideoPath, VideoWidth = added.VideoWidth, VideoHeight = added.VideoHeight, VideoOffset = 0, CropLeft = 0, CropTop = 0, CropRight = 0, CropBottom = 0 });
        LoadOverlayUi();
    }
    private readonly Dictionary<string, double> videoLengths = new(StringComparer.OrdinalIgnoreCase);
    private double VideoLength(string path)
    {
        if (videoLengths.TryGetValue(path, out var length)) return length;
        try { length = ClipMedia.Read(path).Duration; } catch { length = 0; }
        return videoLengths[path] = length;
    }
    // Where in the video the part starts playing; it can start as late as leaves the part's length to play.
    private void VideoStartRow()
    {
        var slider = new Slider { Minimum = 0, Maximum = 1, IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(slider, "Starts at");
        var value = new TextBlock { Width = 44, TextAlignment = TextAlignment.Right, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        value.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        slider.ValueChanged += (_, _) => { value.Text = $"{slider.Value:0.0} s"; EditOverlay("videoOffset", o => o with { VideoOffset = Math.Round(slider.Value, 2) }); };
        overlayRefresh.Add(o =>
        {
            double room = Math.Max(0, VideoLength(o.VideoPath) - o.Length);
            slider.Maximum = Math.Max(.01, room); slider.IsEnabled = room > .01;
            slider.Value = Math.Clamp(o.VideoOffset, 0, slider.Maximum); value.Text = $"{slider.Value:0.0} s";
        });
        var holder = new DockPanel(); DockPanel.SetDock(value, Dock.Right); holder.Children.Add(value); holder.Children.Add(slider);
        Row("Starts at", holder).ToolTip = "Where in the video it starts playing";
    }

    // ---- Keyframes ----
    // The item as it looks at the playhead, and a change to that look (a keyframe there when it has any).
    private OverlayItem Now(OverlayItem o) => o.Posed(Math.Clamp(playhead - o.Start, 0, o.Length));
    private OverlayItem Keyed(OverlayItem o, Func<OverlayItem, OverlayItem> change) => o.Adjusted(Math.Clamp(playhead - o.Start, 0, o.Length), change);
    private void MotionSection()
    {
        Header("Motion");
        var keys = new WrapPanel();
        (IReadOnlyList<OverlayKeyframe>?, double) shown = (null, double.NaN);
        overlayRefresh.Add(o =>
        {
            if (ReferenceEquals(shown.Item1, o.Keys) && shown.Item2 == o.Start) return;
            shown = (o.Keys, o.Start); keys.Children.Clear();
            foreach (var key in o.Keys)
            {
                var chip = new Button { Content = $"◆ {KeepSection.TimeText(o.Start + key.T)}", FontSize = 11, MinHeight = 24, Height = 24, Margin = new Thickness(0, 0, 4, 4), ToolTip = "Jump to this keyframe · right-click to remove it" };
                chip.SetResourceReference(StyleProperty, "TrimButton");
                chip.Click += (_, _) => { if (SelectedOverlayItem is { } item) SeekTo(item.Start + key.T); };
                chip.MouseRightButtonUp += (_, e) => { e.Handled = true; EditOverlay("removeKey", x => x with { Keys = x.Keys.Where(k => k != key).ToArray() }); LoadOverlayUi(); StatusLabel.Text = "Keyframe removed."; };
                keys.Children.Add(chip);
            }
        });
        OverlayControls.Children.Add(keys);
        var buttons = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        SmallButton(buttons, "Add keyframe", "Remember where it is, its size, turn and opacity at the playhead. At another moment, move it: it glides between keyframes.", AddKeyframe);
        var clear = SmallButton(buttons, "Clear", "Remove every keyframe; it keeps how it looks at the playhead", () =>
        {
            EditOverlay("clearKeys", o => Now(o) with { Keys = Array.Empty<OverlayKeyframe>() }); LoadOverlayUi(); StatusLabel.Text = "Keyframes cleared.";
        });
        overlayRows.Add((clear, o => o.Keys.Count > 0));
        OverlayControls.Children.Add(buttons);
        When(Hint("Add a keyframe, move the playhead, then move, scale, turn or fade it: it glides from one keyframe to the next."), o => o.Keys.Count == 0);
        When(Hint("Changes on the video or in Placement set a keyframe at the playhead."), o => o.Keys.Count > 0);
    }
    private void AddKeyframe()
    {
        if (SelectedOverlayItem is not { } o) return;
        if (playhead < o.Start - 1e-6 || playhead > o.End + 1e-6) { StatusLabel.Text = "Move the playhead inside this part first."; return; }
        double t = Math.Clamp(playhead - o.Start, 0, o.Length);
        bool first = o.Keys.Count == 0;
        EditOverlay("addKey", x => x.WithKeyAt(t, x.Posed(t)));
        LoadOverlayUi();
        StatusLabel.Text = first ? "Keyframe added. Move the playhead, then move, scale or turn it on the video to set the next one." : $"Keyframe set at {KeepSection.TimeText(playhead)}.";
    }

    // ---- Typing on the video ----
    // Double-clicking text puts a box right over it; what's typed shows as it's typed. Enter or a click
    // away finishes (Shift+Enter starts a new line).
    private TextBox? inlineEditor;
    private void EditTextOnVideo(int index)
    {
        if (exportCancellation != null) return;
        if (index != Timeline.SelectedOverlay) OpenOverlay(index);
        if (SelectedOverlayItem is not { Kind: OverlayKind.Text } o || OverlayView.Parent is not Grid host) return;
        CloseInlineEditor();
        var bounds = OverlayView.ScreenBounds(index); if (bounds.IsEmpty) return;
        var box = new TextBox
        {
            Text = o.Text, AcceptsReturn = false, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            MinWidth = Math.Max(140, bounds.Width), MaxWidth = Math.Max(160, OverlayView.ActualWidth - Math.Max(0, bounds.X)),
            Margin = new Thickness(Math.Clamp(bounds.X, 0, Math.Max(0, OverlayView.ActualWidth - 140)), Math.Clamp(bounds.Y, 0, Math.Max(0, OverlayView.ActualHeight - 40)), 0, 0),
            FontSize = 16, Padding = new Thickness(6, 4, 6, 4), Foreground = Brushes.White, CaretBrush = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x12, 0x16, 0x1C)), BorderThickness = new Thickness(1.5),
        };
        box.SetResourceReference(Control.BorderBrushProperty, "Accent");
        AutomationProperties.SetName(box, "Type the text");
        Grid.SetRow(box, Grid.GetRow(OverlayView)); Grid.SetColumn(box, Grid.GetColumn(OverlayView));
        box.TextChanged += (_, _) => { if (inlineEditor == box) { EditOverlay("text", x => x with { Text = box.Text }); if (overlayTextBox != null && overlayTextBox.Text != box.Text) LoadOverlayUi(); } };
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { int at = box.CaretIndex; box.Text = box.Text.Insert(at, "\n"); box.CaretIndex = at + 1; e.Handled = true; }
            else if (e.Key is Key.Enter or Key.Escape) { CloseInlineEditor(); e.Handled = true; }
        };
        box.LostKeyboardFocus += (_, _) => { if (inlineEditor == box) CloseInlineEditor(); };
        host.Children.Add(box); inlineEditor = box;
        box.Focus(); box.SelectAll();
        StatusLabel.Text = "Type the text · Shift+Enter for a new line · Enter or click away when done.";
    }
    private void CloseInlineEditor()
    {
        if (inlineEditor is not { } box) return;
        inlineEditor = null;
        (box.Parent as Panel)?.Children.Remove(box);
        StatusLabel.Text = "Text updated.";
    }
}
