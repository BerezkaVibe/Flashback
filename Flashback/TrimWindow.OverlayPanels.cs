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
        // Or draw it: a wiggle for freehand, a straight line, and joined lines for corners clicked one by one.
        var draw = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
        foreach (var tool in new[] { OverlayLayer.DrawTool.Freehand, OverlayLayer.DrawTool.Line, OverlayLayer.DrawTool.Corners })
        {
            var button = new Button { Content = DrawGlyph(tool, 18, 14), Width = 34, Height = 30, MinHeight = 30, Padding = new Thickness(0), Margin = new Thickness(0, 0, 4, 4), ToolTip = DrawTip(tool) };
            button.SetResourceReference(StyleProperty, "IconButton");
            AutomationProperties.SetName(button, DrawTip(tool));
            button.Click += (_, _) => { SetDrawTool(tool); StartDrawing(tool); };
            draw.Children.Add(button);
        }
        OverlayControls.Children.Add(draw);

        // Fill: a colour, see-through with just the outline, or a blur or pixelation of the video.
        var fill = new ComboBox { MinHeight = 28, FontSize = 12 };
        foreach (var name in new[] { "Color", "Outline only", "Blur what's under it", "Pixelate what's under it" }) fill.Items.Add(name);
        AutomationProperties.SetName(fill, "Fill");
        static bool SeeThrough(OverlayItem o) => OverlayRenderer.ParseColor(o.ShapeColor, Colors.Black).A == 0;
        fill.SelectionChanged += (_, _) => EditOverlay("region", o =>
        {
            var c = OverlayRenderer.ParseColor(o.ShapeColor, Colors.Red); string rgb = $"{c.R:X2}{c.G:X2}{c.B:X2}";
            return fill.SelectedIndex switch
            {
                // Outline only keeps the colour for the outline when there wasn't one yet.
                1 => o with { Region = OverlayRegion.Solid, ShapeColor = "#00" + rgb, ShapeBorderWidth = Math.Max(o.ShapeBorderWidth, 8), ShapeBorderColor = o.ShapeBorderWidth > 0 || c.A == 0 ? o.ShapeBorderColor : "#FF" + rgb },
                2 => o with { Region = OverlayRegion.Blur },
                3 => o with { Region = OverlayRegion.Pixelate },
                _ => o with { Region = OverlayRegion.Solid, ShapeColor = c.A == 0 ? "#CC" + rgb : o.ShapeColor },
            };
        });
        overlayRefresh.Add(o => fill.SelectedIndex = o.Region switch { OverlayRegion.Blur => 2, OverlayRegion.Pixelate => 3, _ => SeeThrough(o) ? 1 : 0 });
        When(Row("Fill", fill), o => !o.IsLine);
        When(SliderRow("Amount", "regionStrength", 2, 120, o => o.RegionStrength, (o, v) => o with { RegionStrength = Math.Round(v) }, v => $"{v:0}", tip: "How strong the blur is, or how big the blocks are"), o => o.IsRegion);
        When(Hint("Hides whatever is under the shape: faces, names, messages. Move and resize it like any other shape."), o => o.IsRegion);
        bool Solid(OverlayItem o) => !o.IsRegion && !o.IsLine;
        When(Row("Color", Swatch("shapeColor", o => o.ShapeColor, (o, c) => o with { ShapeColor = c }, alpha: true)), o => (Solid(o) && !SeeThrough(o)) || o.IsLine);
        When(OpacityOfColor("shapeOpacity", o => o.ShapeColor, (o, c) => o with { ShapeColor = c }), o => (Solid(o) && !SeeThrough(o)) || o.IsLine);
        var borderRow = new StackPanel { Orientation = Orientation.Horizontal };
        borderRow.Children.Add(Swatch("shapeBorder", o => o.ShapeBorderColor, (o, c) => o with { ShapeBorderColor = c }));
        When(Row("Outline", borderRow), Solid);
        When(SliderRow("Width", "shapeBorderWidth", 0, 24, o => o.ShapeBorderWidth, (o, v) => o with { ShapeBorderWidth = Math.Round(v, 1) }, v => v < .05 ? "Off" : $"{v:0.#}", tip: "Outline thickness"), Solid);
        var dashes = new (string, OverlayDash)[] { ("Solid", OverlayDash.Solid), ("Dashed", OverlayDash.Dashed), ("Dotted", OverlayDash.Dotted) };
        When(ComboRow("Style", "dash", dashes, o => o.Dash, (o, v) => o with { Dash = v }), o => Solid(o) && o.ShapeBorderWidth > 0);

        // Lines: thickness, dashes and how each end finishes.
        var ends = new (string, OverlayCap)[] { ("Plain", OverlayCap.None), ("Arrow", OverlayCap.Arrow), ("Dot", OverlayCap.Dot) };
        When(SliderRow("Thickness", "lineWidth", 1, 60, o => o.LineWidth, (o, v) => o with { LineWidth = Math.Round(v, 1) }, v => $"{v:0.#}"), o => o.IsLine);
        When(ComboRow("Style", "lineDash", dashes, o => o.Dash, (o, v) => o with { Dash = v }), o => o.IsLine);
        When(ComboRow("Start", "startCap", ends, o => o.StartCap, (o, v) => o with { StartCap = v }), o => o.IsLine);
        When(ComboRow("End", "endCap", ends, o => o.EndCap, (o, v) => o with { EndCap = v }), o => o.IsLine);
        var closed = new CheckBox { Content = "Close into a shape", Margin = new Thickness(0, 6, 0, 0), ToolTip = "On: the ends join and it can be filled. Off: it's a line with ends you can finish in arrows or dots." };
        closed.Click += (_, _) => { EditOverlay("closed", o => o with { Closed = closed.IsChecked == true }); LoadOverlayUi(); };
        overlayRefresh.Add(o => closed.IsChecked = o.Closed);
        OverlayControls.Children.Add(closed); When(closed, o => o.IsDrawn && o.Points.Count >= 3);
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
    private Grid ComboRow<T>(string label, string control, (string Name, T Value)[] options, Func<OverlayItem, T> get, Func<OverlayItem, T, OverlayItem> set)
    {
        var box = new ComboBox { MinHeight = 28, FontSize = 12 };
        foreach (var (name, _) in options) box.Items.Add(name);
        AutomationProperties.SetName(box, label);
        box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) EditOverlay(control, o => set(o, options[box.SelectedIndex].Value)); };
        overlayRefresh.Add(o => box.SelectedIndex = Array.FindIndex(options, x => EqualityComparer<T>.Default.Equals(x.Value, get(o))));
        return Row(label, box);
    }
    // Pictures of the three ways to draw: a wiggle, a straight line, and joined lines.
    private static Geometry DrawIcon(OverlayLayer.DrawTool tool) => Geometry.Parse(tool switch
    {
        OverlayLayer.DrawTool.Freehand => "M0,10 C3,2 6,2 8,7 S13,12 16,3",
        OverlayLayer.DrawTool.Line => "M1,12 L15,2",
        _ => "M1,12 L5,3 L11,9 L15,1",
    });
    private static string DrawTip(OverlayLayer.DrawTool tool) => tool switch
    {
        OverlayLayer.DrawTool.Freehand => "Draw freehand: drag on the video; finish near the start to close it into a shape",
        OverlayLayer.DrawTool.Line => "Straight line: drag on the video (Shift snaps the angle)",
        _ => "Joined lines: click each corner on the video; double-click or Enter to finish, click the first corner to close",
    };
    private static System.Windows.Shapes.Path DrawGlyph(OverlayLayer.DrawTool tool, double width = 14, double height = 12) => Glyph(DrawIcon(tool), width, height);
    private static System.Windows.Shapes.Path Glyph(Geometry data, double width, double height)
    {
        var icon = new System.Windows.Shapes.Path { Data = data, Stretch = Stretch.Uniform, Width = width, Height = height, StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Ink");
        return icon;
    }

    // ---- Shapes and lines ----
    // One tool for shapes and drawn lines. Like the other tools: click two spots on the timeline for when it
    // shows. Then it's a shape from the gallery, or drawn on the video the chosen way (freehand, a straight
    // line or joined lines). The button uses the way last picked from the arrow inside it; D starts it with
    // shapes and W with drawing.
    private OverlayLayer.DrawTool drawTool = OverlayLayer.DrawTool.Freehand;
    private bool drawPending, drawChosen;
    // Pictures of a triangle, a square and a circle.
    private static readonly Geometry ShapesIcon = Geometry.Parse("M8,0.75 L12,6.25 H4 Z M0.75,7.25 H7.75 V14.25 H0.75 Z M8.75,11 A3.25,3.25 0 1 1 15.25,11 A3.25,3.25 0 1 1 8.75,11 Z");
    private void ShapeTool_Click(object sender, RoutedEventArgs e)
    {
        if (Timeline.OverlayMode == OverlayKind.Shape) { Timeline.OverlayMode = null; drawPending = false; ShowCutTool(); StatusLabel.Text = "Tool off."; return; }
        if (drawChosen) UseDrawTool(true); else OverlayTool(OverlayKind.Shape);
    }
    private void ShapeKey() { drawChosen = false; ShowShapeWay(); OverlayTool(OverlayKind.Shape); }
    private void DrawTool_Click(object sender, RoutedEventArgs e) { drawChosen = true; ShowShapeWay(); UseDrawTool(!(drawPending && Timeline.OverlayMode == OverlayKind.Shape)); }
    private void UseDrawTool(bool on)
    {
        if (source.Length == 0 || exportCancellation != null) return;
        Timeline.OverlayMode = on ? OverlayKind.Shape : null; drawPending = on;
        ShowCutTool(); SlowPopup.IsOpen = false;
        StatusLabel.Text = on ? "Click two spots on the timeline for when the drawing shows, then draw it on the video. Esc leaves the tool." : "Tool off.";
    }
    private void ShapeToolChoice_Down(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var menu = new ContextMenu { PlacementTarget = ShapeToolButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        MenuItem Choice(System.Windows.Shapes.Path glyph, string name, string tip, bool chosen, Action pick)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            glyph.Margin = new Thickness(0, 0, 8, 0); header.Children.Add(glyph);
            header.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            var item = new MenuItem { Header = header, ToolTip = tip, IsChecked = chosen };
            AutomationProperties.SetName(item, name);
            item.Click += (_, _) => pick();
            menu.Items.Add(item); return item;
        }
        Choice(Glyph(ShapesIcon, 22, 16), "Shapes and stickers", "A shape, sticker, or a blurred or pixelated area, picked in the panel", !drawChosen, () =>
        {
            drawChosen = false; ShowShapeWay();
            if (!(Timeline.OverlayMode == OverlayKind.Shape && !drawPending)) OverlayTool(OverlayKind.Shape);
        });
        menu.Items.Add(new Separator());
        foreach (var tool in new[] { OverlayLayer.DrawTool.Freehand, OverlayLayer.DrawTool.Line, OverlayLayer.DrawTool.Corners })
            Choice(DrawGlyph(tool, 22, 16), DrawName(tool), DrawTip(tool), drawChosen && tool == drawTool, () => { drawChosen = true; SetDrawTool(tool); UseDrawTool(true); });
        menu.IsOpen = true;
    }
    private static string DrawName(OverlayLayer.DrawTool tool) => tool switch { OverlayLayer.DrawTool.Freehand => "Draw freehand", OverlayLayer.DrawTool.Line => "Straight line", _ => "Joined lines" };
    private void SetDrawTool(OverlayLayer.DrawTool tool)
    {
        drawTool = tool; ShowShapeWay();
        // An open shape switches to the new way right away.
        if (OverlayView.Drawing is { } drawing && drawing != tool) OverlayView.SetDrawing(tool);
    }
    // The button shows the way it will use: the shapes, or the chosen way to draw.
    private void ShowShapeWay()
    {
        ShapeToolIcon.Data = drawChosen ? DrawIcon(drawTool) : ShapesIcon;
        ShapeToolButton.ToolTip = (drawChosen ? DrawTip(drawTool) + " · " + TrimShortcuts.Display(keys[TrimAction.DrawTool])
            : "Shapes: click two spots to add a shape, sticker, or a blurred or pixelated area · " + TrimShortcuts.Display(keys[TrimAction.ShapeTool])) + " · the arrow picks a shape or a way to draw";
    }
    // Draw on the video for the selected shape; the drawing replaces its outline.
    private void StartDrawing(OverlayLayer.DrawTool tool)
    {
        if (SelectedOverlayItem is not { Kind: OverlayKind.Shape } o) return;
        if (playhead < o.Start || playhead >= o.End) OpenOverlay(Timeline.SelectedOverlay);
        Pause(); OverlayView.SetDrawing(tool);
        StatusLabel.Text = tool switch
        {
            OverlayLayer.DrawTool.Freehand => "Drag on the video to draw. Finish near where you started to close it into a shape. Esc cancels.",
            OverlayLayer.DrawTool.Line => "Drag on the video to draw a line; hold Shift to keep it at neat angles. Esc cancels.",
            _ => "Click each corner on the video. Double-click or Enter to finish, click the first corner to close it, right-click takes a corner back.",
        };
    }
    // A finished drawing (in frame pixels) becomes the shape: its box, its points within the box, and
    // whether it closes. Freehand strokes are thinned to the points that shape them.
    private void ShapeDrawn(OverlayLayer.DrawTool tool, IReadOnlyList<Point> drawn, bool closed)
    {
        if (SelectedOverlayItem is not { Kind: OverlayKind.Shape } || drawn.Count < 2) return;
        double frameW = OverlayView.VideoWidth, frameH = OverlayView.VideoHeight, k = OverlayItem.Reference / frameH;
        var pts = tool == OverlayLayer.DrawTool.Freehand ? Thin(drawn.ToList(), 1.2 / k) : drawn.ToList();
        double left = pts.Min(p => p.X), right = pts.Max(p => p.X), top = pts.Min(p => p.Y), bottom = pts.Max(p => p.Y);
        double min = 12 / k, w = Math.Max(right - left, min), h = Math.Max(bottom - top, min), cx = (left + right) / 2, cy = (top + bottom) / 2;
        var points = pts.Select(p => (Math.Round((p.X - cx) / w, 4), Math.Round((p.Y - cy) / h, 4))).ToArray();
        var shape = tool == OverlayLayer.DrawTool.Freehand ? OverlayShape.Freehand : OverlayShape.Polyline;
        Snapshot(); lastOverlayControl = "draw";
        ReplaceOverlay(Timeline.SelectedOverlay, (SelectedOverlayItem! with
        {
            Shape = shape, Closed = closed, Points = points, ShapeWidth = w * k, ShapeHeight = h * k, X = cx / frameW, Y = cy / frameH,
            Scale = 1, Rotation = 0, Keys = Array.Empty<OverlayKeyframe>(), ShapeCorners = OverlayItem.NoCorners,
            // A straight line gets an arrow at its end to start with.
            EndCap = tool == OverlayLayer.DrawTool.Line && SelectedOverlayItem!.EndCap == OverlayCap.None && SelectedOverlayItem.StartCap == OverlayCap.None ? OverlayCap.Arrow : SelectedOverlayItem!.EndCap,
        }).Validated());
        LoadOverlayUi();
        StatusLabel.Text = closed ? "Shape drawn. Pick a fill, or Outline only to circle something." : "Line drawn. Set its thickness, style and ends in the panel.";
    }
    // Ramer–Douglas–Peucker: keeps the points that matter to the stroke's shape, within tolerance pixels.
    private static List<Point> Thin(List<Point> pts, double tolerance)
    {
        if (pts.Count < 3) return pts;
        var keep = new bool[pts.Count]; keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>(); stack.Push((0, pts.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop(); double far = 0; int at = -1;
            for (int i = a + 1; i < b; i++)
            {
                double d = Distance(pts[i], pts[a], pts[b]);
                if (d > far) { far = d; at = i; }
            }
            if (at >= 0 && far > tolerance) { keep[at] = true; stack.Push((a, at)); stack.Push((at, b)); }
        }
        var result = pts.Where((_, i) => keep[i]).ToList();
        return result.Count > 300 ? Thin(result, tolerance * 1.5) : result;
        static double Distance(Point p, Point a, Point b)
        {
            var ab = b - a; if (ab.Length < 1e-9) return (p - a).Length;
            double t = Math.Clamp(((p - a) * ab) / ab.LengthSquared, 0, 1);
            return (p - (a + ab * t)).Length;
        }
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

    // A video part that lasts as long as its file has left to play (from where it starts in the file), so all
    // of it plays. Its own time runs through the holds it keeps going through, so those count towards it.
    private void FitVideoLength()
    {
        if (SelectedOverlayItem is not { Kind: OverlayKind.Video } item) return;
        double wanted = VideoLength(item.VideoPath) - item.VideoOffset;
        if (wanted <= FrameStep) { StatusLabel.Text = "That video's length couldn't be read."; return; }
        double end = item.Start + wanted;
        for (int i = 0; i < 6; i++)
        {
            double shown = (item with { End = end, EndHold = 0 }).Duration(Timeline.Freezes);
            if (Math.Abs(shown - wanted) < 1e-3) break;
            end -= shown - wanted;
        }
        end = Math.Max(item.Start + FrameStep, end);
        bool clipped = end > media.Duration + 1e-6;
        Snapshot();
        if (!TryRetime(item, item.Start, Math.Min(end, media.Duration), out _, out var error, endHold: 0)) { undo.Pop(); StatusLabel.Text = error; return; }
        AfterRetime();
        StatusLabel.Text = clipped ? $"The video runs past the end of the clip, so it plays until the clip ends ({wanted:0.#} s of video)." : $"It now plays the whole video: {wanted:0.#} s.";
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
