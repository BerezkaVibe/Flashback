using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Flashback;

// Text and pictures: placed on the timeline with two clicks like the other tools, shown on slim
// layers above the video track, edited in a panel beside the video and moved, scaled and rotated
// right on the preview. They may overlap cuts, speed parts, zooms and each other.
public partial class TrimWindow
{
    private OverlayItem? lastTextStyle;
    private bool loadingOverlay;
    private OverlayKind? panelKind;
    private readonly List<(FrameworkElement Element, Func<OverlayItem, bool> When)> overlayRows = new();
    private readonly List<Action<OverlayItem>> overlayRefresh = new();
    private string lastOverlayControl = ""; private long lastOverlayChange;
    internal static readonly string[] PictureExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };

    private void InitOverlays()
    {
        Timeline.OverlayAdded += OverlayAdded; Timeline.OverlayPicked += OpenOverlay; Timeline.OverlayRemoved += RemoveOverlay;
        Timeline.OverlayEditStarted += () => { Snapshot(); lastOverlayControl = "timeline"; };
        Timeline.OverlayMoved += (_, _) => { OverlayView.Items = Timeline.Overlays; if (OverlayPanel.Visibility == Visibility.Visible) ShowOverlayTitle(); };
        Timeline.OverlayEditFinished += () => { OverlayView.Items = Timeline.Overlays; UpdateExportHint(); if (OverlayPanel.Visibility == Visibility.Visible) LoadOverlayUi(); };
        OverlayView.Picked += OpenOverlay;
        OverlayView.EditStarted += () => { Snapshot(); lastOverlayControl = "view"; };
        OverlayView.Changed += item => { if (Timeline.SelectedOverlay >= 0) { ReplaceOverlay(Timeline.SelectedOverlay, item.Validated()); LoadOverlayUi(); } };
        OverlayView.WheelAdjusted += WheelAdjust;
        OverlayView.CroppingChanged += on => StatusLabel.Text = on ? "Cropping: drag the edges or corners, or drag inside to move the crop. Double-click or Esc when done." : "Crop done.";
    }
    // Scroll over an item on the preview: scale, Shift for rotation, Ctrl for opacity. A run of
    // scrolling is one undo step.
    private void WheelAdjust(int index, string control, Func<OverlayItem, OverlayItem> change)
    {
        if (exportCancellation != null) return;
        if (index != Timeline.SelectedOverlay) OpenOverlay(index);
        EditOverlay(control, change);
        LoadOverlayUi();
        if (SelectedOverlayItem is { } o)
            StatusLabel.Text = control switch { "wheelRotation" => $"Rotation {o.Rotation:0}°", "wheelOpacity" => $"Opacity {o.Opacity * 100:0}%", _ => $"Scale {o.Scale:0.##}×" }
                + " · scroll to scale, Shift+scroll to rotate, Ctrl+scroll for opacity";
    }
    private void TextTool_Click(object sender, RoutedEventArgs e) => OverlayTool(OverlayKind.Text);
    private void ImageTool_Click(object sender, RoutedEventArgs e) => OverlayTool(OverlayKind.Image);
    private void OverlayTool(OverlayKind kind)
    {
        if (source.Length == 0 || exportCancellation != null) return;
        Timeline.OverlayMode = Timeline.OverlayMode == kind ? null : kind; ShowCutTool(); SlowPopup.IsOpen = false;
        string what = kind == OverlayKind.Text ? "text" : "a picture or GIF";
        StatusLabel.Text = Timeline.OverlayMode == null ? "Tool off."
            : $"Click two spots to add {what} for that part; the marker locks onto the playhead and other parts' edges. Esc leaves the tool.";
    }

    private OverlayItem? SelectedOverlayItem => Timeline.SelectedOverlay >= 0 && Timeline.SelectedOverlay < Timeline.Overlays.Count ? Timeline.Overlays[Timeline.SelectedOverlay] : null;
    private void SetOverlays(IReadOnlyList<OverlayItem> list)
    {
        Timeline.Overlays = list; OverlayView.Items = list;
        if (Timeline.SelectedOverlay >= list.Count) CloseOverlay();
    }
    private void ReplaceOverlay(int index, OverlayItem next)
    {
        if (index < 0 || index >= Timeline.Overlays.Count) return;
        var list = Timeline.Overlays.ToArray(); list[index] = next; SetOverlays(list);
        if (next.Kind == OverlayKind.Text) lastTextStyle = next;
        ShowOverlayTitle();
    }
    private void ResetOverlays() { Timeline.OverlayMode = null; CloseOverlay(); SetOverlays(Array.Empty<OverlayItem>()); OverlayView.VideoWidth = media.Width > 0 ? media.Width : 1920; OverlayView.VideoHeight = media.Height > 0 ? media.Height : 1080; }

    private void OverlayAdded(double start, double end, OverlayKind kind)
    {
        OverlayItem item;
        if (kind == OverlayKind.Text) item = OverlayItem.NewText(start, end, lastTextStyle);
        else
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Add a picture", Filter = "Pictures and GIFs|" + string.Join(";", PictureExtensions.Select(x => "*" + x)), CheckFileExists = true };
            if (dialog.ShowDialog(this) != true) { StatusLabel.Text = "No picture added."; return; }
            if (NewPicture(start, end, dialog.FileName) is not { } picture) return;
            item = picture;
        }
        AddOverlay(item);
    }
    private OverlayItem? NewPicture(double start, double end, string path)
    {
        var item = OverlayItem.NewImage(start, end, path);
        if (OverlayRenderer.PictureSize(item) is not { } size) { StatusLabel.Text = "That picture couldn't be opened. Try a PNG, JPG or GIF."; return null; }
        // Small pictures start at their own size on the export; big ones fit half the frame's height.
        double height = media.Height > 0 ? media.Height : 1080;
        double natural = Math.Max(size.Width, size.Height) * OverlayItem.Reference / (OverlayRenderer.ImageBox * height);
        return item with { Scale = Math.Clamp(natural, OverlayItem.MinScale, 1) };
    }
    private void AddOverlay(OverlayItem item)
    {
        Snapshot();
        item = item with { Layer = OverlayOrder.FreeLayer(Timeline.Overlays, item.Start, item.End) };
        // New text that would land right on top of other text showing at the same time moves up a line.
        if (item.Kind == OverlayKind.Text)
            for (int tries = 0; tries < 6 && Timeline.Overlays.Any(o => o.Kind == OverlayKind.Text && o.End > item.Start && o.Start < item.End && Math.Abs(o.Y - item.Y) < .08 && Math.Abs(o.X - item.X) < .25); tries++)
                item = item with { Y = item.Y > .3 ? item.Y - .12 : item.Y + .12 };
        SetOverlays(Timeline.Overlays.Append(item).ToArray());
        OpenOverlay(Timeline.Overlays.Count - 1);
        UpdateExportHint();
        StatusLabel.Text = $"{(item.Kind == OverlayKind.Text ? "Text" : "Picture")} added from {KeepSection.TimeText(item.Start)} to {KeepSection.TimeText(item.End)}. Drag it on the video to place it.";
        if (item.Kind == OverlayKind.Text && overlayTextBox != null) { overlayTextBox.Focus(); overlayTextBox.SelectAll(); }
    }
    // Dropping a picture on the trimmer adds it at the playhead for three seconds.
    internal bool DropPicture(string path)
    {
        if (source.Length == 0 || exportCancellation != null || !PictureExtensions.Contains(Path.GetExtension(path).ToLowerInvariant())) return false;
        double start = Math.Min(playhead, Math.Max(0, media.Duration - .1)), end = Math.Min(media.Duration, start + 3);
        if (NewPicture(start, end, path) is { } picture) AddOverlay(picture);
        return true;
    }
    private void OpenOverlay(int index)
    {
        if (index < 0 || index >= Timeline.Overlays.Count || exportCancellation != null) return;
        if (ZoomPanel.Visibility == Visibility.Visible) CloseZoom();
        SlowPopup.IsOpen = false;
        Timeline.SelectedOverlay = index; OverlayView.Selected = index; FocusPart(Timeline.Overlays[index]);
        var item = Timeline.Overlays[index];
        if (panelKind != item.Kind) BuildOverlayPanel(item.Kind);
        OverlayPanel.Visibility = Visibility.Visible;
        LoadOverlayUi();
        // Show the item: jump in past its entrance if the playhead is elsewhere.
        if (playhead < item.Start || playhead >= item.End)
            SeekTo(Math.Min(item.End - .01, item.Start + Math.Min(item.Length / 2, item.In != OverlayMotion.None ? item.InLength + .05 : .05)));
    }
    private void CloseOverlay()
    {
        Timeline.SelectedOverlay = -1; OverlayView.Selected = -1;
        if (focusedKind == PartKind.Overlay) FocusPart(null);
        OverlayPanel.Visibility = Visibility.Collapsed;
    }
    private void OverlayDone_Click(object sender, RoutedEventArgs e) => CloseOverlay();
    private void RemoveOverlay(int index)
    {
        if (index < 0 || index >= Timeline.Overlays.Count) return;
        Snapshot();
        bool open = index == Timeline.SelectedOverlay;
        var list = OverlayOrder.Compact(Timeline.Overlays.Where((_, n) => n != index).ToArray());
        if (open) CloseOverlay(); else if (Timeline.SelectedOverlay > index) { Timeline.SelectedOverlay--; OverlayView.Selected = Timeline.SelectedOverlay; }
        SetOverlays(list); UpdateExportHint();
        StatusLabel.Text = "Removed. Undo brings it back.";
    }
    private void ShowOverlayTitle()
    {
        if (SelectedOverlayItem is not { } o) return;
        OverlayHeading.Text = o.Kind == OverlayKind.Text ? "Text" : "Picture";
        OverlayTitle.Text = $"{KeepSection.TimeText(o.Start)} – {KeepSection.TimeText(o.End)} · layer {o.Layer + 1}";
    }
    private void LoadOverlayUi()
    {
        if (SelectedOverlayItem is not { } o) { CloseOverlay(); return; }
        if (panelKind != o.Kind) BuildOverlayPanel(o.Kind);
        loadingOverlay = true;
        try
        {
            foreach (var refresh in overlayRefresh) refresh(o);
            foreach (var (element, when) in overlayRows) element.Visibility = when(o) ? Visibility.Visible : Visibility.Collapsed;
            ShowOverlayTitle();
        }
        finally { loadingOverlay = false; }
    }
    // Every change from the panel goes through here. Changes in quick succession to one control
    // (a slider drag, typing) make one undo step.
    private void EditOverlay(string control, Func<OverlayItem, OverlayItem> change)
    {
        if (loadingOverlay || SelectedOverlayItem is not { } o) return;
        if (control != lastOverlayControl || Stopwatch.GetElapsedTime(lastOverlayChange).TotalSeconds > 1.2) Snapshot();
        lastOverlayControl = control; lastOverlayChange = Stopwatch.GetTimestamp();
        ReplaceOverlay(Timeline.SelectedOverlay, change(o).Validated());
        foreach (var (element, when) in overlayRows) element.Visibility = when(SelectedOverlayItem!) ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- The panel ----
    private TextBox? overlayTextBox;
    private void BuildOverlayPanel(OverlayKind kind)
    {
        panelKind = kind; overlayRows.Clear(); overlayRefresh.Clear(); OverlayControls.Children.Clear(); overlayTextBox = null;
        bool text = kind == OverlayKind.Text;
        if (text)
        {
            var box = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 56, MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 13, Padding = new Thickness(6, 4, 6, 4) };
            System.Windows.Automation.AutomationProperties.SetName(box, "Text");
            box.TextChanged += (_, _) => EditOverlay("text", o => o with { Text = box.Text });
            overlayRefresh.Add(o => { if (box.Text != o.Text) box.Text = o.Text; });
            OverlayControls.Children.Add(box); overlayTextBox = box;
            var fonts = FontChoices();
            var font = new ComboBox { ItemsSource = fonts, MinHeight = 30, IsEditable = false };
            font.ItemTemplate = FontTemplate();
            font.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
            font.SelectionChanged += (_, _) => { if (font.SelectedItem is string f) EditOverlay("font", o => o with { Font = f }); };
            overlayRefresh.Add(o => font.SelectedItem = fonts.FirstOrDefault(f => f.Equals(o.Font, StringComparison.OrdinalIgnoreCase)) ?? o.Font);
            Row("Font", font);
            SliderRow("Size", "size", 8, 300, o => o.Size, (o, v) => o with { Size = Math.Round(v) }, v => $"{v:0}");
            var style = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            Toggle(style, "B", "Bold", o => o.Bold, (o, on) => o with { Bold = on }, bold: true);
            Toggle(style, "I", "Italic", o => o.Italic, (o, on) => o with { Italic = on }, italic: true);
            Toggle(style, "AA", "All caps", o => o.Caps, (o, on) => o with { Caps = on });
            style.Children.Add(new Border { Width = 10 });
            foreach (var (glyph, name, align) in new[] { ("", "Align left", OverlayAlign.Left), ("", "Centre", OverlayAlign.Center), ("", "Align right", OverlayAlign.Right) })
                Toggle(style, glyph, name, o => o.Align == align, (o, _) => o with { Align = align }, glyph: true);
            OverlayControls.Children.Add(style);
            SliderRow("Letters", "letters", -10, 40, o => o.LetterSpacing, (o, v) => o with { LetterSpacing = Math.Round(v, 1) }, v => $"{v:0.#}", tip: "Space between letters");
            SliderRow("Lines", "lines", .6, 2.5, o => o.LineSpacing, (o, v) => o with { LineSpacing = Math.Round(v, 2) }, v => $"{v:0.00}", tip: "Space between lines");
            SliderRow("Wrap", "wrap", 0, 1, o => o.WrapWidth, (o, v) => o with { WrapWidth = v < .02 ? 0 : Math.Round(v, 3) }, v => v < .02 ? "Off" : $"{v * 100:0}%", tip: "Wrap long lines at this width of the frame. You can also drag the side handles on the video.");

            Header("Color");
            var fill = Swatch("fill", o => o.Fill, (o, c) => o with { Fill = c });
            var gradient = new CheckBox { Content = "Gradient", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            gradient.Click += (_, _) => EditOverlay("gradient", o => o with { Fill2 = gradient.IsChecked == true ? (o.Fill2 ?? "#FFE600") : null });
            overlayRefresh.Add(o => gradient.IsChecked = o.Fill2 != null);
            var fill2 = Swatch("fill2", o => o.Fill2 ?? "#FFE600", (o, c) => o with { Fill2 = c });
            var fillRow = new StackPanel { Orientation = Orientation.Horizontal };
            fillRow.Children.Add(fill); fillRow.Children.Add(gradient);
            var fill2Holder = new Border { Child = fill2, Margin = new Thickness(8, 0, 0, 0) }; fillRow.Children.Add(fill2Holder);
            overlayRows.Add((fill2Holder, o => o.Fill2 != null));
            Row("Fill", fillRow);
            When(SliderRow("Angle", "angle", 0, 360, o => o.GradientAngle, (o, v) => o with { GradientAngle = Math.Round(v) }, v => $"{v:0}°"), o => o.Fill2 != null);
            var outlineRow = new StackPanel { Orientation = Orientation.Horizontal };
            outlineRow.Children.Add(Swatch("outline", o => o.OutlineColor, (o, c) => o with { OutlineColor = c }));
            Row("Outline", outlineRow);
            SliderRow("Width", "outlineWidth", 0, 20, o => o.OutlineWidth, (o, v) => o with { OutlineWidth = Math.Round(v, 1) }, v => v < .05 ? "Off" : $"{v:0.#}", tip: "Outline thickness");

            Header("Background");
            Combo("Shape", "shape", new (string, OverlayShape)[] { ("None", OverlayShape.None), ("Bar behind each line", OverlayShape.Lines), ("Box", OverlayShape.Box), ("Pill", OverlayShape.Pill), ("Ellipse", OverlayShape.Ellipse), ("Speech bubble", OverlayShape.Bubble), ("Arrow", OverlayShape.Arrow), ("Custom shape", OverlayShape.Custom) },
                o => o.Shape, (o, v) => o with { Shape = v });
            bool Shaped(OverlayItem o) => o.Shape != OverlayShape.None;
            When(Row("Color", Swatch("shapeColor", o => o.ShapeColor, (o, c) => o with { ShapeColor = c }, alpha: true)), Shaped);
            When(SliderRow("Padding", "padding", 0, 120, o => o.Padding, (o, v) => o with { Padding = Math.Round(v) }, v => $"{v:0}"), Shaped);
            When(SliderRow("Corners", "corner", 0, 80, o => o.Corner, (o, v) => o with { Corner = Math.Round(v) }, v => $"{v:0}"), o => o.Shape is OverlayShape.Lines or OverlayShape.Box or OverlayShape.Bubble);
            var borderRow = new StackPanel { Orientation = Orientation.Horizontal };
            borderRow.Children.Add(Swatch("shapeBorder", o => o.ShapeBorderColor, (o, c) => o with { ShapeBorderColor = c }));
            When(Row("Border", borderRow), Shaped);
            When(SliderRow("Width", "shapeBorderWidth", 0, 16, o => o.ShapeBorderWidth, (o, v) => o with { ShapeBorderWidth = Math.Round(v, 1) }, v => v < .05 ? "Off" : $"{v:0.#}", tip: "Border thickness"), Shaped);
            When(Hint("Drag the pink dot on the video to point the tail."), o => o.Shape == OverlayShape.Bubble);
            When(Hint("Drag the pink corners on the video. Double-click an edge to add a corner; right-click one to remove it."), o => o.Shape == OverlayShape.Custom);
            var resetShape = new Button { Content = "Reset corners", FontSize = 11, MinHeight = 26, Height = 26, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
            resetShape.SetResourceReference(StyleProperty, "TrimButton");
            resetShape.Click += (_, _) => EditOverlay("resetShape", o => o with { Points = OverlayItem.DefaultPoints });
            OverlayControls.Children.Add(resetShape); overlayRows.Add((resetShape, o => o.Shape == OverlayShape.Custom));
        }
        else
        {
            var file = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            overlayRefresh.Add(o => { file.Text = Path.GetFileName(o.ImagePath); file.ToolTip = o.ImagePath; });
            var replace = new Button { Content = "Replace…", FontSize = 11, MinHeight = 26, Height = 26, Margin = new Thickness(8, 0, 0, 0) };
            replace.SetResourceReference(StyleProperty, "TrimButton");
            replace.Click += (_, _) =>
            {
                var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Replace picture", Filter = "Pictures and GIFs|" + string.Join(";", PictureExtensions.Select(x => "*" + x)), CheckFileExists = true };
                if (dialog.ShowDialog(this) != true) return;
                if (OverlayRenderer.PictureSize(OverlayItem.NewImage(0, 1, dialog.FileName)) == null) { StatusLabel.Text = "That picture couldn't be opened."; return; }
                EditOverlay("replace", o => o with { ImagePath = dialog.FileName }); LoadOverlayUi();
            };
            var fileRow = new DockPanel(); DockPanel.SetDock(replace, Dock.Right); fileRow.Children.Add(replace); fileRow.Children.Add(file);
            OverlayControls.Children.Add(fileRow);
            var flips = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            Toggle(flips, "", "Flip left to right", o => o.FlipX, (o, on) => o with { FlipX = on }, glyph: true);
            Toggle(flips, "", "Flip upside down", o => o.FlipY, (o, on) => o with { FlipY = on }, glyph: true, rotate: 90);
            OverlayControls.Children.Add(flips);
            // Cropping happens on the video: double-click the picture, or use the button.
            SmallButton(flips, "Crop", "Crop on the video · or double-click the picture", () => { if (SelectedOverlayItem is { } o && (playhead < o.Start || playhead >= o.End)) OpenOverlay(Timeline.SelectedOverlay); OverlayView.SetCropping(true); });
            var resetCrop = new Button { Content = "Reset crop", FontSize = 11, MinHeight = 26, Height = 26, Margin = new Thickness(0, 0, 4, 4), ToolTip = "Show the whole picture again" };
            resetCrop.SetResourceReference(StyleProperty, "TrimButton");
            resetCrop.Click += (_, _) => EditOverlay("resetCrop", o => o with { CropLeft = 0, CropTop = 0, CropRight = 0, CropBottom = 0 });
            flips.Children.Add(resetCrop); overlayRows.Add((resetCrop, o => o.CropLeft + o.CropTop + o.CropRight + o.CropBottom > 0));
            Header("Frame");
            SliderRow("Corners", "imageCorner", 0, 50, o => o.ImageCorner, (o, v) => o with { ImageCorner = Math.Round(v) }, v => v < .5 ? "Square" : v > 49.5 ? "Round" : $"{v:0}%");
            var borderRow = new StackPanel { Orientation = Orientation.Horizontal };
            borderRow.Children.Add(Swatch("border", o => o.BorderColor, (o, c) => o with { BorderColor = c }));
            Row("Border", borderRow);
            SliderRow("Width", "borderWidth", 0, 30, o => o.BorderWidth, (o, v) => o with { BorderWidth = Math.Round(v, 1) }, v => v < .05 ? "Off" : $"{v:0.#}", tip: "Border thickness");
            Header("Remove background");
            Combo("Remove", "key", new (string, OverlayKey)[] { ("Off", OverlayKey.None), ("Green screen", OverlayKey.Green), ("White", OverlayKey.White), ("Black", OverlayKey.Black), ("Pick a color", OverlayKey.Custom) }, o => o.Key, (o, v) => o with { Key = v });
            When(Row("Color", Swatch("keyColor", o => o.KeyColor, (o, c) => o with { KeyColor = c })), o => o.Key == OverlayKey.Custom);
            When(SliderRow("Amount", "keyTolerance", 0, .9, o => o.KeyTolerance, (o, v) => o with { KeyTolerance = Math.Round(v, 3) }, v => $"{v * 100:0}%", tip: "How close to the color a pixel must be to disappear"), o => o.Key != OverlayKey.None);
        }

        Header("Shadow");
        var shadow = new CheckBox { Content = "Drop shadow" };
        shadow.Click += (_, _) => EditOverlay("shadowOn", o => o with { Shadow = o.Shadow with { On = shadow.IsChecked == true } });
        overlayRefresh.Add(o => shadow.IsChecked = o.Shadow.On);
        OverlayControls.Children.Add(shadow);
        bool Shadowed(OverlayItem o) => o.Shadow.On;
        When(Row("Color", Swatch("shadowColor", o => o.Shadow.Color, (o, c) => o with { Shadow = o.Shadow with { Color = c } })), Shadowed);
        When(SliderRow("Strength", "shadowOpacity", 0, 1, o => o.Shadow.Opacity, (o, v) => o with { Shadow = o.Shadow with { Opacity = Math.Round(v, 2) } }, v => $"{v * 100:0}%"), Shadowed);
        When(SliderRow("Distance", "shadowDistance", 0, 40, o => o.Shadow.Distance, (o, v) => o with { Shadow = o.Shadow with { Distance = Math.Round(v, 1) } }, v => $"{v:0.#}"), Shadowed);
        When(SliderRow("Blur", "shadowBlur", 0, 40, o => o.Shadow.Blur, (o, v) => o with { Shadow = o.Shadow with { Blur = Math.Round(v, 1) } }, v => v < .05 ? "Sharp" : $"{v:0.#}"), Shadowed);
        When(SliderRow("Angle", "shadowAngle", 0, 360, o => o.Shadow.Angle, (o, v) => o with { Shadow = o.Shadow with { Angle = Math.Round(v) } }, v => $"{v:0}°"), Shadowed);

        Header("Placement");
        SliderRow("Scale", "scale", Math.Log(OverlayItem.MinScale), Math.Log(OverlayItem.MaxScale), o => Math.Log(o.Scale), (o, v) => o with { Scale = Math.Round(Math.Exp(v), 3) }, v => $"{Math.Exp(v):0.##}×", tip: "Or drag a corner on the video");
        SliderRow("Rotation", "rotation", -180, 180, o => ((o.Rotation % 360) + 540) % 360 - 180, (o, v) => o with { Rotation = Math.Round(v) }, v => $"{v:0}°", tip: "Or drag the round handle on the video; hold Shift for 15° steps");
        SliderRow("Opacity", "opacity", 0, 1, o => o.Opacity, (o, v) => o with { Opacity = Math.Round(v, 2) }, v => $"{v * 100:0}%");
        var stick = new CheckBox { Content = "Stick to the video when it zooms", Margin = new Thickness(0, 6, 0, 0), ToolTip = "On: it zooms along with the picture, as if part of it. Off: it stays put on screen while the video zooms." };
        stick.Click += (_, _) => EditOverlay("stick", o => o with { StickToVideo = stick.IsChecked == true });
        overlayRefresh.Add(o => stick.IsChecked = o.StickToVideo);
        OverlayControls.Children.Add(stick);
        var arrange = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        SmallButton(arrange, "Centre", "Move to the middle of the frame", () => EditOverlay("centre", o => o with { X = .5, Y = .5 }));
        SmallButton(arrange, "Bring forward", "Move up a layer, in front of what overlaps it", () => Restack(1));
        SmallButton(arrange, "Send back", "Move down a layer, behind what overlaps it", () => Restack(-1));
        OverlayControls.Children.Add(arrange);

        Header("Animation");
        var motions = new List<(string, OverlayMotion)> { ("None", OverlayMotion.None), ("Fade", OverlayMotion.Fade), ("Pop", OverlayMotion.Pop), ("Slide up", OverlayMotion.SlideUp), ("Slide down", OverlayMotion.SlideDown), ("Slide left", OverlayMotion.SlideLeft), ("Slide right", OverlayMotion.SlideRight) };
        if (text) motions.Add(("Typewriter", OverlayMotion.Typewriter));
        Combo("In", "in", motions.ToArray(), o => o.In, (o, v) => o with { In = v });
        When(SliderRow("Length", "inLength", .05, 3, o => o.InLength, (o, v) => o with { InLength = Math.Round(v, 2) }, v => $"{v:0.##} s", tip: "How long the entrance takes"), o => o.In != OverlayMotion.None);
        Combo("Out", "out", motions.Select(m => m.Item2 == OverlayMotion.Typewriter ? ("Typewriter (erase)", m.Item2) : m).ToArray(), o => o.Out, (o, v) => o with { Out = v });
        When(SliderRow("Length", "outLength", .05, 3, o => o.OutLength, (o, v) => o with { OutLength = Math.Round(v, 2) }, v => $"{v:0.##} s", tip: "How long the exit takes"), o => o.Out != OverlayMotion.None);

        var remove = new Button { Content = text ? "Remove text" : "Remove picture", FontSize = 11, Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-8, 12, 0, 4) };
        remove.SetResourceReference(StyleProperty, "TrimButton");
        remove.Click += (_, _) => RemoveOverlay(Timeline.SelectedOverlay);
        OverlayControls.Children.Add(remove);
    }
    private void Restack(int direction)
    {
        int i = Timeline.SelectedOverlay; if (i < 0) return;
        var before = Timeline.Overlays[i].Layer;
        var list = OverlayOrder.Restack(Timeline.Overlays, i, direction);
        if (list.SequenceEqual(Timeline.Overlays)) { StatusLabel.Text = direction > 0 ? "It's already in front." : "It's already at the back."; return; }
        Snapshot(); SetOverlays(list); ShowOverlayTitle();
        StatusLabel.Text = direction > 0 ? "Brought forward." : "Sent back.";
    }

    // ---- Panel building blocks ----
    private void Header(string text)
    {
        var header = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 2) };
        header.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        OverlayControls.Children.Add(header);
    }
    private TextBlock Hint(string text)
    {
        var hint = new TextBlock { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        OverlayControls.Children.Add(hint); return hint;
    }
    private void When(FrameworkElement element, Func<OverlayItem, bool> when) => overlayRows.Add((element, when));
    private Grid Row(string label, UIElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 5, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        var name = new TextBlock { Text = label, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        grid.Children.Add(name); Grid.SetColumn(control, 1); grid.Children.Add(control);
        OverlayControls.Children.Add(grid); return grid;
    }
    private Grid SliderRow(string label, string control, double min, double max, Func<OverlayItem, double> get, Func<OverlayItem, double, OverlayItem> set, Func<double, string> format, string? tip = null)
    {
        var slider = new Slider { Minimum = min, Maximum = max, IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center, SmallChange = (max - min) / 100, LargeChange = (max - min) / 10 };
        System.Windows.Automation.AutomationProperties.SetName(slider, label);
        var value = new TextBlock { Width = 44, TextAlignment = TextAlignment.Right, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        value.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        slider.ValueChanged += (_, _) => { value.Text = format(slider.Value); EditOverlay(control, o => set(o, slider.Value)); };
        overlayRefresh.Add(o => { slider.Value = Math.Clamp(get(o), min, max); value.Text = format(slider.Value); });
        var holder = new DockPanel(); DockPanel.SetDock(value, Dock.Right); holder.Children.Add(value); holder.Children.Add(slider);
        var row = Row(label, holder); if (tip != null) row.ToolTip = tip;
        return row;
    }
    private void Combo<T>(string label, string control, (string Name, T Value)[] options, Func<OverlayItem, T> get, Func<OverlayItem, T, OverlayItem> set)
    {
        var box = new ComboBox { MinHeight = 28, FontSize = 12 };
        foreach (var (name, _) in options) box.Items.Add(name);
        System.Windows.Automation.AutomationProperties.SetName(box, label);
        box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) EditOverlay(control, o => set(o, options[box.SelectedIndex].Value)); };
        overlayRefresh.Add(o => box.SelectedIndex = Array.FindIndex(options, x => EqualityComparer<T>.Default.Equals(x.Value, get(o))));
        Row(label, box);
    }
    private ColorSwatch Swatch(string control, Func<OverlayItem, string> get, Func<OverlayItem, string, OverlayItem> set, bool alpha = false)
    {
        var swatch = new ColorSwatch(alpha);
        swatch.Changed += c => EditOverlay(control, o => set(o, c));
        overlayRefresh.Add(o => swatch.Value = get(o));
        return swatch;
    }
    private void Toggle(Panel panel, string content, string name, Func<OverlayItem, bool> get, Func<OverlayItem, bool, OverlayItem> set, bool bold = false, bool italic = false, bool glyph = false, double rotate = 0)
    {
        var button = new Button { Content = content, Width = 32, Height = 28, MinHeight = 28, Padding = new Thickness(0), Margin = new Thickness(0, 0, 4, 0), ToolTip = name, FontSize = glyph ? 13 : 12,
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal, FontStyle = italic ? FontStyles.Italic : FontStyles.Normal };
        button.FontFamily = new FontFamily(glyph ? "Segoe MDL2 Assets" : "Segoe UI");
        if (rotate != 0) button.LayoutTransform = new RotateTransform(rotate);
        button.SetResourceReference(StyleProperty, "IconButton");
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        button.Click += (_, _) => { if (SelectedOverlayItem is { } o) { EditOverlay(name, x => set(x, !get(o))); LoadOverlayUi(); } };
        overlayRefresh.Add(o =>
        {
            if (get(o)) { button.SetResourceReference(Control.BorderBrushProperty, "Accent"); button.SetResourceReference(Control.ForegroundProperty, "Accent"); }
            else { button.ClearValue(Control.BorderBrushProperty); button.ClearValue(Control.ForegroundProperty); }
        });
        panel.Children.Add(button);
    }
    private void SmallButton(Panel panel, string text, string tip, Action click)
    {
        var button = new Button { Content = text, FontSize = 11, MinHeight = 26, Height = 26, Margin = new Thickness(0, 0, 4, 4), ToolTip = tip };
        button.SetResourceReference(StyleProperty, "TrimButton");
        button.Click += (_, _) => click();
        panel.Children.Add(button);
    }
    private static List<string>? fontChoices;
    // Meme-friendly fonts first, then every installed font.
    private static List<string> FontChoices()
    {
        if (fontChoices != null) return fontChoices;
        var installed = Fonts.SystemFontFamilies.Select(f => f.Source).Where(s => !string.IsNullOrWhiteSpace(s) && !s.Contains("MDL2") && !s.Contains("Fluent Icons")).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        var favourites = new[] { "Impact", "Arial Black", "Arial", "Segoe UI", "Bahnschrift", "Comic Sans MS", "Georgia", "Times New Roman", "Courier New", "Verdana", "Trebuchet MS", "Segoe Script", "Ink Free" }
            .Where(f => installed.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
        return fontChoices = favourites.Concat(installed.Except(favourites, StringComparer.OrdinalIgnoreCase)).ToList();
    }
    private static DataTemplate FontTemplate()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
        text.SetBinding(TextBlock.FontFamilyProperty, new System.Windows.Data.Binding());
        text.SetValue(TextBlock.FontSizeProperty, 14.0);
        return new DataTemplate { VisualTree = text };
    }
}
