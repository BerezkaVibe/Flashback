using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Flashback;

// Shows text, pictures, videos and shapes over the preview, drawn exactly as the export draws them,
// and lets the selected item be moved, scaled and rotated in place. Items stuck to the video follow
// the preview's zoom; the others stay put. Dragging snaps to the centre lines, edges and safe margins.
internal sealed class OverlayLayer : FrameworkElement
{
    internal double VideoWidth = 1920, VideoHeight = 1080;
    private IReadOnlyList<OverlayItem> items = Array.Empty<OverlayItem>();
    internal IReadOnlyList<OverlayItem> Items { get => items; set { items = value; ForgetPlayers(); Refresh(); SyncVideos(true); } }
    internal int Selected { get => selected; set { if (value != selected) { SetCropping(false); SetShapeEditing(false); if (Drawing != null) SetDrawing(null); } selected = value; hovering = value >= 0 && IsMouseOver && ItemAt(Mouse.GetPosition(this)) == value; Refresh(); } }
    private int selected = -1;
    private double time;
    internal double Time { get => time; set { if (Math.Abs(time - value) < 1e-9) return; time = value; Refresh(onlyIfChanged: true); SyncVideos(); } }
    // The preview's zoom (in this element's coordinates) and the part of the video it shows.
    private Matrix zoom = Matrix.Identity; private Rect? zoomClip;
    internal void SetZoom(Matrix matrix, Rect? clip) { if (zoom == matrix && zoomClip == clip) return; zoom = matrix; zoomClip = clip; Refresh(); }
    // The preview's video, which blur and pixelate shapes show treated inside them.
    private UIElement? source;
    internal UIElement? Source { get => source; set { if (source == value) return; source = value; Refresh(); } }
    // Only the selected item can be dragged; clicks on others select them.
    internal event Action<int>? Picked;
    internal event Action? EditStarted, EditFinished;
    internal event Action<OverlayItem>? Changed;
    // Double-clicking text asks for it to be edited on the video.
    internal event Action<int>? TextEditRequested;

    private readonly ContainerVisual stuck = new(), screen = new();
    private readonly DrawingVisual handles = new();
    private readonly Dictionary<OverlayItem, (int Chars, int Frame, double Aspect, Drawing Content)> contents = new(ReferenceEqualityComparer.Instance);
    public OverlayLayer()
    {
        AddVisualChild(stuck); AddVisualChild(screen); AddVisualChild(handles);
        Focusable = false; SizeChanged += (_, _) => Refresh();
    }
    protected override int VisualChildrenCount => 3;
    protected override Visual GetVisualChild(int index) => index switch { 0 => stuck, 1 => screen, _ => handles };

    private Rect Video => Fit(new Size(ActualWidth, ActualHeight));
    private Rect Fit(Size area)
    {
        double scale = Math.Min(area.Width / Math.Max(1, VideoWidth), area.Height / Math.Max(1, VideoHeight));
        double w = VideoWidth * scale, h = VideoHeight * scale;
        return new Rect((area.Width - w) / 2, (area.Height - h) / 2, w, h);
    }
    // Frame pixels to this element, without the preview zoom.
    private Matrix FrameToElement()
    {
        var v = Video; var m = Matrix.Identity;
        m.Scale(v.Width / VideoWidth, v.Height / VideoHeight); m.Translate(v.X, v.Y);
        return m;
    }
    // Frame pixels to this element, for an item (with the preview zoom when it is stuck to the video).
    private Matrix FrameToScreen(OverlayItem item) => item.StickToVideo ? FrameToElement() * zoom : FrameToElement();
    private bool Visible(OverlayItem o) => time >= o.Start - 1e-9 && time < o.End;
    private double LocalTime(OverlayItem o) => Math.Clamp(time - o.Start, 0, o.Length);
    internal Drawing ContentFor(OverlayItem item, OverlayState state)
    {
        double local = time - item.Start, aspect = VideoWidth / Math.Max(1, VideoHeight);
        int frame = item.Kind == OverlayKind.Image ? OverlayRenderer.FrameAt(item, local) : 0;
        if (contents.TryGetValue(item, out var hit) && hit.Chars == state.Chars && hit.Frame == frame && hit.Aspect == aspect) return hit.Content;
        // Moving, scaling, rotating, fading or retiming an item doesn't change its drawing, so reuse it.
        var look = Look(item);
        if (!looks.TryGetValue((look, state.Chars, frame, aspect), out var content))
        {
            content = OverlayRenderer.Content(item, state.Chars, local, aspect);
            if (looks.Count > 96) looks.Clear();
            looks[(look, state.Chars, frame, aspect)] = content;
        }
        contents[item] = (state.Chars, frame, aspect, content);
        return content;
    }
    private readonly Dictionary<(OverlayItem, int, int, double), Drawing> looks = new();
    private static OverlayItem Look(OverlayItem o) => o with { Start = 0, End = 0, X = 0, Y = 0, Scale = 1, Rotation = 0, Opacity = 1, Layer = 0, StickToVideo = true, In = OverlayMotion.None, Out = OverlayMotion.None, InLength = 0, OutLength = 0, Shadow = NoShadow, Keys = Array.Empty<OverlayKeyframe>(), VideoOffset = 0, VideoVolume = 1 };
    private static readonly OverlayShadow NoShadow = new();
    private readonly Dictionary<OverlayItem, OverlayItem> plain = new(ReferenceEqualityComparer.Instance);
    private OverlayItem Plain(OverlayItem o)
    {
        if (plain.TryGetValue(o, out var p)) return p;
        if (plain.Count > 64) plain.Clear();
        return plain[o] = o with { In = OverlayMotion.None, Out = OverlayMotion.None, Shadow = NoShadow };
    }
    // What was last drawn: each visible item with its look. Playback only redraws when this changes.
    private List<(OverlayItem, OverlayState, int, int)> drawn = new();
    private void Refresh(bool onlyIfChanged = false)
    {
        // Keyframed items move every frame, so they count as changed at each new time.
        var showing = items.Where(Visible).Select(o => (o, o.StateAt(time - o.Start), o.Kind == OverlayKind.Image ? OverlayRenderer.FrameAt(o, time - o.Start) : 0, o.Keys.Count > 1 ? (int)Math.Round(time * 1000) : 0)).ToList();
        if (onlyIfChanged && showing.SequenceEqual(drawn)) return;
        drawn = showing;
        stuck.Children.Clear(); screen.Children.Clear();
        // Forget drawings for items that were edited or removed.
        var current = new HashSet<OverlayItem>(items, ReferenceEqualityComparer.Instance);
        foreach (var gone in contents.Keys.Where(k => !current.Contains(k)).ToList()) contents.Remove(gone);
        foreach (var gone in built.Keys.Where(k => !current.Contains(k)).ToList()) built.Remove(gone);
        var v = Video;
        stuck.Transform = new MatrixTransform(zoom);
        stuck.Clip = new RectangleGeometry(zoomClip ?? v); screen.Clip = new RectangleGeometry(v);
        var toElement = FrameToElement();
        foreach (var original in OverlayOrder.BackToFront(items))
        {
            if (!Visible(original) || ActualWidth <= 0) continue;
            // With preview effects off (Settings > Performance), items show plainly: no shadow and no
            // entrance or exit motion. Exports are unaffected.
            var item = PerformanceOptions.PreviewEffects ? original : Plain(original);
            double local = time - item.Start;
            var state = item.StateAt(local);
            // An item's visual is kept until something it shows changes, so a keyframed item moving
            // doesn't rebuild the blur, pixelation and drawings around it every frame.
            var key = (state, item.Kind == OverlayKind.Image ? OverlayRenderer.FrameAt(item, local) : 0, item.Keys.Count > 1 ? item.Posed(local) : null,
                toElement, item.IsRegion ? zoom : Matrix.Identity, item.IsRegion ? Source : null, PerformanceOptions.PreviewEffects);
            if (!built.TryGetValue(original, out var cached) || !cached.Key.Equals(key))
            {
                Visual visual;
                if (item.IsRegion) visual = RegionVisual(item, local, toElement);
                else
                {
                    var content = ContentFor(original, state);
                    if (item.Kind == OverlayKind.Video) content = WithVideo(item, content, PlayerFor(original));
                    var drawing = OverlayRenderer.Visual(item, local, VideoWidth, VideoHeight, content);
                    drawing.Transform = new MatrixTransform(toElement);
                    visual = drawing;
                }
                built[original] = cached = (key, visual);
            }
            (item.StickToVideo ? stuck : screen).Children.Add(cached.Visual);
        }
        DrawHandles();
    }
    private readonly Dictionary<OverlayItem, (object Key, Visual Visual)> built = new(ReferenceEqualityComparer.Instance);

    // ---- Blur and pixelate shapes ----
    // The preview's video, blurred or blown up into blocks, shows through the shape.
    private Visual RegionVisual(OverlayItem item, double local, Matrix toElement)
    {
        var posed = item.Posed(local); var state = posed.StateAt(local);
        var place = OverlayRenderer.Placement(posed, state, VideoWidth, VideoHeight);
        var shape = OverlayRenderer.ShapeGeometryOf(posed).Clone(); shape.Transform = new MatrixTransform(place);
        var holder = new ContainerVisual { Clip = shape, Opacity = Math.Clamp(state.Opacity, 0, 1), Transform = new MatrixTransform(toElement) };
        var fill = new DrawingVisual();
        double strength = Math.Max(2, item.RegionStrength * VideoHeight / OverlayItem.Reference);
        var bounds = shape.Bounds; bounds.Inflate(strength * 2, strength * 2);
        using (var dc = fill.RenderOpen())
        {
            if (Source is { } player && PerformanceOptions.PreviewEffects && player.RenderSize.Width > 0)
            {
                // The brush draws the player as it appears in its parent: centred when letterboxed, and
                // zoomed when the preview zooms.
                var shown = Rect.Transform(Fit(player.RenderSize), player.RenderTransform?.Value ?? Matrix.Identity); shown.Offset(VisualTreeHelper.GetOffset(player));
                var brush = new VisualBrush(player)
                {
                    ViewboxUnits = BrushMappingMode.Absolute, Viewbox = shown,
                    ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, VideoWidth, VideoHeight),
                    Stretch = Stretch.Fill, TileMode = TileMode.None,
                };
                // A shape that isn't stuck to the video still treats the zoomed picture under it.
                var under = !item.StickToVideo && !zoom.IsIdentity && toElement.HasInverse ? toElement * zoom * Inverse(toElement) : Matrix.Identity;
                dc.DrawRectangle(Brushes.Black, null, bounds);
                if (item.Region == OverlayRegion.Pixelate) DrawBlocks(dc, player, shown, under, shape.Bounds, strength);
                else
                {
                    if (!under.IsIdentity) brush.Transform = new MatrixTransform(under);
                    dc.DrawRectangle(brush, null, bounds);
                }
            }
            else dc.DrawRectangle(RegionPlaceholder, null, bounds);
        }
        if (Source != null && PerformanceOptions.PreviewEffects)
        {
            if (item.Region == OverlayRegion.Blur) fill.Effect = new BlurEffect { Radius = Math.Min(200, strength * 1.5), KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
        }
        holder.Children.Add(fill);
        return holder;
    }
    // Pixelation: each block is filled with the video at its centre, stretched from a single pixel, so the
    // blocks have hard edges however the preview is scaled. Very fine blocks are coarsened to keep it quick.
    private void DrawBlocks(DrawingContext dc, UIElement source, Rect shown, Matrix under, Rect box, double size)
    {
        double step = size;
        while (Math.Ceiling(box.Width / step) * Math.Ceiling(box.Height / step) > 900) step *= 1.25;
        double sx = shown.Width / VideoWidth, sy = shown.Height / VideoHeight;
        var back = under.HasInverse ? Inverse(under) : Matrix.Identity;
        for (double y = box.Top; y < box.Bottom; y += step)
            for (double x = box.Left; x < box.Right; x += step)
            {
                var at = back.Transform(new Point(x + step / 2, y + step / 2));
                var sample = new Rect(shown.X + at.X * sx - sx / 2, shown.Y + at.Y * sy - sy / 2, sx, sy);
                var brush = new VisualBrush(source) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = sample, Stretch = Stretch.Fill, TileMode = TileMode.None };
                dc.DrawRectangle(brush, null, new Rect(x - .3, y - .3, step + .6, step + .6));
            }
    }
    private static readonly Brush RegionPlaceholder = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0x60, 0x66, 0x70)));

    // ---- Picture-in-picture ----
    // Each video part plays in its own player, kept in step with the preview. A part's player is found by
    // its file and how many earlier parts use the same file, so editing a part keeps its player.
    private readonly Dictionary<(string Path, int Nth), MediaPlayer> players = new();
    private readonly HashSet<MediaPlayer> running = new();
    private bool playing; private double speed = 1;
    internal bool Playing { get => playing; set { if (playing == value) return; playing = value; SyncVideos(true); } }
    // How fast the main video is playing (0 while a freeze frame holds), which the videos follow.
    internal double PlaybackSpeed { get => speed; set { if (Math.Abs(speed - value) < 1e-6) return; speed = value; SyncVideos(true); } }
    private (string, int)? VideoKey(OverlayItem item)
    {
        if (item.Kind != OverlayKind.Video || string.IsNullOrWhiteSpace(item.VideoPath)) return null;
        int nth = 0;
        foreach (var o in items)
        {
            if (ReferenceEquals(o, item)) return (item.VideoPath, nth);
            if (o.Kind == OverlayKind.Video && string.Equals(o.VideoPath, item.VideoPath, StringComparison.OrdinalIgnoreCase)) nth++;
        }
        return (item.VideoPath, nth);
    }
    private MediaPlayer? PlayerFor(OverlayItem item)
    {
        if (VideoKey(item) is not { } key) return null;
        if (players.TryGetValue(key, out var player)) return player;
        player = new MediaPlayer { ScrubbingEnabled = true, Volume = 0 };
        try { player.Open(new Uri(item.VideoPath)); } catch { return null; }
        // A paused player only shows a picture once it has started, so start and stop it straight away.
        player.MediaOpened += (_, _) => { if (!running.Contains(player)) { player.Play(); player.Pause(); } SyncVideos(true); };
        players[key] = player;
        return player;
    }
    private void SyncVideos(bool force = false)
    {
        if (players.Count == 0 && !items.Any(o => o.Kind == OverlayKind.Video)) return;
        var used = new HashSet<MediaPlayer>();
        foreach (var item in items)
        {
            if (item.Kind != OverlayKind.Video || !Visible(item) || PlayerFor(item) is not { } player) continue;
            used.Add(player);
            var want = TimeSpan.FromSeconds(Math.Max(0, time - item.Start + item.VideoOffset));
            player.Volume = playing ? Math.Clamp(item.VideoVolume, 0, 1) : 0;
            if (playing && speed > 0)
            {
                if (Math.Abs(player.SpeedRatio - speed) > 1e-6) player.SpeedRatio = speed;
                if (running.Add(player)) { player.Position = want; player.Play(); }
                else if (Math.Abs((player.Position - want).TotalSeconds) > .35) player.Position = want;
            }
            else
            {
                if (running.Remove(player)) player.Pause();
                if (force || Math.Abs((player.Position - want).TotalSeconds) > 1 / 120.0) player.Position = want;
            }
        }
        foreach (var player in running.Where(p => !used.Contains(p)).ToList()) { player.Pause(); running.Remove(player); }
    }
    private void ForgetPlayers()
    {
        if (players.Count == 0) return;
        var wanted = items.Select(VideoKey).Where(k => k != null).Select(k => k!.Value).ToHashSet();
        foreach (var key in players.Keys.Where(k => !wanted.Contains(k)).ToList()) { running.Remove(players[key]); players[key].Close(); players.Remove(key); }
    }
    internal void CloseVideos() { foreach (var player in players.Values) player.Close(); players.Clear(); running.Clear(); }
    private static Drawing WithVideo(OverlayItem item, Drawing frame, MediaPlayer? player)
    {
        if (player == null) return frame;
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            dc.PushClip(OverlayRenderer.MaskGeometry(item, OverlayRenderer.VideoRect(item)));
            dc.DrawVideo(player, OverlayRenderer.VideoFullRect(item));
            dc.Pop();
            dc.DrawDrawing(frame);
        }
        return group;
    }

    // ---- Handles ----
    private static readonly Brush HandleFill = Frozen(new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B))), Guide = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x4F, 0xD8)));
    private static readonly Pen HandlePen = FrozenPen(new Pen(Brushes.White, 1.5)), BoxPen = FrozenPen(new Pen(HandleFill, 1.5) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) }), GuidePen = FrozenPen(new Pen(Guide, 1));
    private static Brush Frozen(Brush b) { b.Freeze(); return b; }
    private static Pen FrozenPen(Pen p) { p.Freeze(); return p; }
    private OverlayItem? Current => selected >= 0 && selected < items.Count ? items[selected] : null;
    private (double X, double Y)? guideX, guideY;
    // Where an item is on this element right now, keyframes included.
    private Matrix ItemToScreen(OverlayItem item, out Drawing content, out OverlayState state)
    {
        double local = LocalTime(item);
        state = item.StateAt(local);
        content = ContentFor(item, state);
        return OverlayRenderer.Placement(item.Posed(local), state, VideoWidth, VideoHeight) * FrameToScreen(item);
    }
    // The item's outline on this element, for placing an editor over it.
    internal Rect ScreenBounds(int index)
    {
        if (index < 0 || index >= items.Count) return Rect.Empty;
        var m = ItemToScreen(items[index], out var content, out _);
        return Rect.Transform(HandleBox(items[index], content), m);
    }
    private Point[] Corners(Rect r, Matrix m) => new[] { m.Transform(r.TopLeft), m.Transform(r.TopRight), m.Transform(r.BottomRight), m.Transform(r.BottomLeft) };
    private Rect HandleBox(OverlayItem item, Drawing content)
    {
        if (item.Kind == OverlayKind.Shape) return OverlayRenderer.ShapeBox(item);
        var b = content.Bounds;
        return b.IsEmpty ? new Rect(-40, -20, 80, 40) : b;
    }
    private static bool HasShape(OverlayItem item) => item.Kind == OverlayKind.Shape || item.Kind == OverlayKind.Text && item.Shape is not OverlayShape.None;
    // Shapes whose own corners drag: custom shapes and lines of clicked corners.
    private static bool HasPoints(OverlayItem item) => item.Shape is OverlayShape.Custom or OverlayShape.Polyline;
    private void DrawHandles()
    {
        using var dc = handles.RenderOpen();
        // Handles only show while the pointer is on the item or dragging it, so the rest of the
        // time the preview looks exactly like the export.
        if (Drawing != null) { DrawStroke(dc); return; }
        if (Current is not { } item || !Visible(item) || (!hovering && grip == Grip.None && !Cropping && !ShapeEditing && Drawing == null)) return;
        if (Cropping) { DrawCrop(dc, item); return; }
        if (ShapeEditing) { DrawShapeHandles(dc, item); return; }
        var m = ItemToScreen(item, out var content, out _);
        var box = HandleBox(item, content); var c = Corners(box, m);
        var outline = new StreamGeometry();
        using (var g = outline.Open()) { g.BeginFigure(c[0], false, true); g.PolyLineTo(c.Skip(1).ToArray(), true, false); }
        dc.DrawGeometry(null, BoxPen, outline);
        foreach (var p in c) dc.DrawRectangle(HandleFill, HandlePen, new Rect(p.X - 4.5, p.Y - 4.5, 9, 9));
        var (rotate, top) = RotateHandle(c);
        dc.DrawLine(BoxPen, top, rotate); dc.DrawEllipse(HandleFill, HandlePen, rotate, 5.5, 5.5);
        if (item.Kind == OverlayKind.Text)
            foreach (var side in SideHandles(c)) dc.DrawRoundedRectangle(HandleFill, HandlePen, new Rect(side.X - 3, side.Y - 7, 6, 14), 2, 2);
        if (item.Kind == OverlayKind.Shape)
        {
            // Side handles stretch a shape wider or taller.
            foreach (var side in SideHandles(c)) dc.DrawRoundedRectangle(HandleFill, HandlePen, new Rect(side.X - 3, side.Y - 7, 6, 14), 2, 2);
            foreach (var side in TopBottomHandles(c)) dc.DrawRoundedRectangle(HandleFill, HandlePen, new Rect(side.X - 7, side.Y - 3, 14, 6), 2, 2);
        }
        if (HasShape(item) && item.Shape == OverlayShape.Bubble)
            dc.DrawEllipse(Guide, HandlePen, m.Transform(OverlayRenderer.TailTip(item)), 5, 5);
        if (HasShape(item) && HasPoints(item))
            foreach (var p in CustomPoints(item, m)) dc.DrawEllipse(Guide, HandlePen, p, 4.5, 4.5);
        var v = Video;
        if (guideX is { } gx) dc.DrawLine(GuidePen, new Point(gx.X, v.Top), new Point(gx.X, v.Bottom));
        if (guideY is { } gy) dc.DrawLine(GuidePen, new Point(v.Left, gy.Y), new Point(v.Right, gy.Y));
    }
    private static (Point Handle, Point Top) RotateHandle(Point[] c)
    {
        var top = new Point((c[0].X + c[1].X) / 2, (c[0].Y + c[1].Y) / 2);
        var up = new Vector(c[0].X - c[3].X, c[0].Y - c[3].Y); if (up.Length < 1) up = new Vector(0, -1); up.Normalize();
        return (top + up * 22, top);
    }
    private static Point[] SideHandles(Point[] c) => new[] { Mid(c[0], c[3]), Mid(c[1], c[2]) };
    private static Point[] TopBottomHandles(Point[] c) => new[] { Mid(c[3], c[2]) };
    private Rect PaddedBox(OverlayItem item) => OverlayRenderer.Padded(item, OverlayRenderer.Layout(item, VideoWidth / Math.Max(1, VideoHeight)).Block);
    // The box a shape is drawn in: its own size for the Shapes tool, the padded text for a text highlight.
    private Rect ShapeFrame(OverlayItem item) => item.Kind == OverlayKind.Shape ? OverlayRenderer.ShapeBox(item) : PaddedBox(item);
    private IEnumerable<Point> CustomPoints(OverlayItem item, Matrix m)
    {
        var box = ShapeFrame(item);
        return item.Points.Select(p => m.Transform(new Point(box.X + (p.X + .5) * box.Width, box.Y + (p.Y + .5) * box.Height)));
    }

    // ---- Drawing ----
    // Draw a shape or line for the selected shape item: drag freehand, drag a straight line (Shift
    // snaps to 15° steps), or click corners one by one (double-click or Enter finishes; clicking the
    // first corner closes the shape; right-click takes the last corner back). Esc cancels.
    internal enum DrawTool { Freehand, Line, Corners }
    internal DrawTool? Drawing { get; private set; }
    internal event Action<bool>? DrawingChanged;
    // The drawn points in frame pixels, and whether they close into a shape.
    internal event Action<DrawTool, IReadOnlyList<Point>, bool>? Drawn;
    private readonly List<Point> stroke = new();
    private Point? rubber;
    internal void SetDrawing(DrawTool? tool)
    {
        if (tool != null && Current is not { Kind: OverlayKind.Shape }) tool = null;
        if (tool != null) { SetCropping(false); SetShapeEditing(false); }
        bool changed = Drawing != tool;
        Drawing = tool; stroke.Clear(); rubber = null; Cursor = tool != null ? Cursors.Pen : null;
        DrawHandles();
        if (changed) DrawingChanged?.Invoke(tool != null);
    }
    // Enter: finish a line of clicked corners where it is.
    internal void FinishDrawing(bool closed = false)
    {
        if (Drawing is not { } tool) return;
        if (IsMouseCaptured) ReleaseMouseCapture();
        int needed = closed ? 3 : 2;
        if (stroke.Count < needed || Current is not { } item) { SetDrawing(null); return; }
        var toScreen = FrameToScreen(item);
        if (!toScreen.HasInverse) { SetDrawing(null); return; }
        var back = Inverse(toScreen);
        var points = stroke.Select(back.Transform).ToList();
        Drawing = null; stroke.Clear(); rubber = null; Cursor = null; DrawHandles();
        DrawingChanged?.Invoke(false);
        Drawn?.Invoke(tool, points, closed);
    }
    private void DrawStroke(DrawingContext dc)
    {
        if (stroke.Count > 0)
        {
            var line = new StreamGeometry();
            using (var g = line.Open()) { g.BeginFigure(stroke[0], false, false); g.PolyLineTo(stroke.Skip(1).Concat(rubber is { } r ? new[] { r } : Array.Empty<Point>()).ToArray(), true, true); }
            dc.DrawGeometry(null, StrokePen, line);
            if (Drawing == DrawTool.Corners) foreach (var p in stroke) dc.DrawEllipse(HandleFill, HandlePen, p, 4, 4);
            // Coming back to the first corner closes the shape.
            if (Drawing == DrawTool.Corners && stroke.Count >= 3 && rubber is { } near && (near - stroke[0]).Length <= 10) dc.DrawEllipse(Guide, HandlePen, stroke[0], 7, 7);
        }
    }
    private static readonly Pen StrokePen = FrozenPen(new Pen(Guide, 2.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });
    private void DrawDown(Point p, MouseButtonEventArgs e)
    {
        switch (Drawing)
        {
            case DrawTool.Freehand: stroke.Clear(); stroke.Add(p); CaptureMouse(); break;
            case DrawTool.Line: stroke.Clear(); stroke.Add(p); stroke.Add(p); CaptureMouse(); break;
            case DrawTool.Corners:
                if (stroke.Count >= 3 && (p - stroke[0]).Length <= 10) { FinishDrawing(closed: true); return; }
                if (e.ClickCount == 2) { if (stroke.Count >= 2 && (stroke[^1] - p).Length < 6) stroke.RemoveAt(stroke.Count - 1); stroke.Add(p); FinishDrawing(); return; }
                stroke.Add(p); break;
        }
        DrawHandles();
    }
    private void DrawMove(Point p)
    {
        switch (Drawing)
        {
            case DrawTool.Freehand when IsMouseCaptured && (stroke.Count == 0 || (p - stroke[^1]).Length >= 2): stroke.Add(p); break;
            case DrawTool.Line when IsMouseCaptured && stroke.Count == 2:
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    var d = p - stroke[0]; double angle = Math.Round(Math.Atan2(d.Y, d.X) / (Math.PI / 12)) * (Math.PI / 12);
                    p = stroke[0] + new Vector(Math.Cos(angle), Math.Sin(angle)) * d.Length;
                }
                stroke[1] = p; break;
            case DrawTool.Corners: rubber = p; break;
            default: return;
        }
        DrawHandles();
    }
    private void DrawUp()
    {
        if (!IsMouseCaptured) return;
        if (Drawing == DrawTool.Freehand)
        {
            // A stroke that ends near where it began closes into a shape.
            bool closed = stroke.Count > 8 && (stroke[^1] - stroke[0]).Length <= Math.Max(18, PathLength(stroke) * .06);
            FinishDrawing(closed);
        }
        else if (Drawing == DrawTool.Line) { if (stroke.Count == 2 && (stroke[1] - stroke[0]).Length >= 6) FinishDrawing(); else { ReleaseMouseCapture(); stroke.Clear(); DrawHandles(); } }
    }
    private static double PathLength(List<Point> points) { double sum = 0; for (int i = 1; i < points.Count; i++) sum += (points[i] - points[i - 1]).Length; return sum; }

    // ---- Cropping ----
    // Double-click a picture or video to crop it on the preview: the cut-away parts show faintly around
    // a crop box whose edges and corners drag. The picture stays put while its edges are trimmed.
    internal bool Cropping { get; private set; }
    internal event Action<bool>? CroppingChanged;
    internal void SetCropping(bool on)
    {
        if (on && Current is not { Kind: OverlayKind.Image or OverlayKind.Video }) on = false;
        if (Cropping == on) return;
        Cropping = on; DrawHandles(); CroppingChanged?.Invoke(on);
    }
    // Double-click a shape to reshape it: each of its corners drags (a custom shape's own corners, and a
    // bubble's tail, drag too).
    internal bool ShapeEditing { get; private set; }
    internal event Action<bool>? ShapeEditingChanged;
    internal void SetShapeEditing(bool on)
    {
        if (on && (Current is not { } item || !HasShape(item))) on = false;
        if (ShapeEditing == on) return;
        ShapeEditing = on; DrawHandles(); ShapeEditingChanged?.Invoke(on);
    }
    private Point[] ShapeCornersOnScreen(OverlayItem item, Matrix m) => OverlayRenderer.ShapeCornerPoints(item, ShapeFrame(item)).Select(m.Transform).ToArray();
    private void DrawShapeHandles(DrawingContext dc, OverlayItem item)
    {
        var m = ItemToScreen(item, out _, out _);
        if (!HasPoints(item))
        {
            var c = ShapeCornersOnScreen(item, m);
            var outline = new StreamGeometry();
            using (var g = outline.Open()) { g.BeginFigure(c[0], false, true); g.PolyLineTo(c.Skip(1).ToArray(), true, false); }
            dc.DrawGeometry(null, GuideDash, outline);
            foreach (var p in c) dc.DrawRectangle(Guide, HandlePen, new Rect(p.X - 5, p.Y - 5, 10, 10));
        }
        else foreach (var p in CustomPoints(item, m)) dc.DrawEllipse(Guide, HandlePen, p, 5, 5);
        if (item.Shape == OverlayShape.Bubble) dc.DrawEllipse(Guide, HandlePen, m.Transform(OverlayRenderer.TailTip(item)), 5, 5);
    }
    private static readonly Pen GuideDash = FrozenPen(new Pen(Guide, 1.2) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) });
    private static readonly Brush Ghost = Frozen(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)));
    private const int EdgeLeft = 1, EdgeTop = 2, EdgeRight = 4, EdgeBottom = 8;
    private static Rect Kept(OverlayItem item, Rect full) =>
        new(full.Left + item.CropLeft * full.Width, full.Top + item.CropTop * full.Height, full.Width * (1 - item.CropLeft - item.CropRight), full.Height * (1 - item.CropTop - item.CropBottom));
    private Rect FullRect(OverlayItem item) => OverlayRenderer.FullRect(item, time - item.Start);
    private void DrawCrop(DrawingContext dc, OverlayItem item)
    {
        var m = ItemToScreen(item, out _, out _);
        var full = FullRect(item);
        if (full.IsEmpty) return;
        var kept = Kept(item, full);
        dc.PushTransform(new MatrixTransform(m));
        var away = new GeometryGroup { FillRule = FillRule.EvenOdd };
        away.Children.Add(new RectangleGeometry(full)); away.Children.Add(new RectangleGeometry(kept));
        dc.PushClip(away); dc.PushOpacity(.35);
        if (item.Kind == OverlayKind.Video) { if (PlayerFor(item) is { } player) dc.DrawVideo(player, full); else dc.DrawRectangle(Ghost, null, full); }
        else if (OverlayRenderer.Uncropped(item, time - item.Start).Picture is { } picture) dc.DrawImage(picture, full);
        dc.Pop(); dc.Pop();
        dc.Pop();
        var c = Corners(kept, m);
        var outline = new StreamGeometry();
        using (var g = outline.Open()) { g.BeginFigure(c[0], false, true); g.PolyLineTo(c.Skip(1).ToArray(), true, false); }
        dc.DrawGeometry(null, HandlePen, outline);
        dc.DrawGeometry(null, BoxPen, new RectangleGeometry(full, 0, 0, new MatrixTransform(m)));
        foreach (var p in c) dc.DrawRectangle(HandleFill, HandlePen, new Rect(p.X - 5, p.Y - 5, 10, 10));
        foreach (var p in SideHandles(c).Concat(new[] { Mid(c[0], c[1]), Mid(c[3], c[2]) })) dc.DrawEllipse(HandleFill, HandlePen, p, 4.5, 4.5);
    }
    private static Point Mid(Point a, Point b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);
    // Which crop edges are under a point (a corner is two), or -1 inside the box to move it.
    private int CropEdgesAt(OverlayItem item, Point p)
    {
        var m = ItemToScreen(item, out _, out _); if (!m.HasInverse) return 0;
        var full = FullRect(item);
        var kept = Kept(item, full); var local = Inverse(m).Transform(p);
        double tol = 9 / Math.Max(1e-6, Math.Sqrt(m.M11 * m.M11 + m.M12 * m.M12));
        bool inY = local.Y > kept.Top - tol && local.Y < kept.Bottom + tol, inX = local.X > kept.Left - tol && local.X < kept.Right + tol;
        int edges = 0;
        if (inY && Math.Abs(local.X - kept.Left) <= tol) edges |= EdgeLeft;
        if (inY && Math.Abs(local.X - kept.Right) <= tol) edges |= EdgeRight;
        if (inX && Math.Abs(local.Y - kept.Top) <= tol) edges |= EdgeTop;
        if (inX && Math.Abs(local.Y - kept.Bottom) <= tol) edges |= EdgeBottom;
        return edges != 0 ? edges : kept.Contains(local) ? -1 : 0;
    }
    // o is the item as posed now; the result moves its centre to follow the middle of the kept part.
    private OverlayItem CropTo(OverlayItem o, Point p)
    {
        var m = ItemToScreen(o, out _, out var state); if (!m.HasInverse) return o;
        var inverse = Inverse(m);
        var full = FullRect(o);
        var kept = Kept(o, full); var d = inverse.Transform(p) - inverse.Transform(press);
        double minW = full.Width * .04, minH = full.Height * .04;
        double left = kept.Left, top = kept.Top, right = kept.Right, bottom = kept.Bottom;
        if (cropEdges == -1)
        {
            double dx = Math.Clamp(d.X, full.Left - kept.Left, full.Right - kept.Right), dy = Math.Clamp(d.Y, full.Top - kept.Top, full.Bottom - kept.Bottom);
            left += dx; right += dx; top += dy; bottom += dy;
        }
        else
        {
            if ((cropEdges & EdgeLeft) != 0) left = Math.Clamp(left + d.X, full.Left, right - minW);
            if ((cropEdges & EdgeRight) != 0) right = Math.Clamp(right + d.X, left + minW, full.Right);
            if ((cropEdges & EdgeTop) != 0) top = Math.Clamp(top + d.Y, full.Top, bottom - minH);
            if ((cropEdges & EdgeBottom) != 0) bottom = Math.Clamp(bottom + d.Y, top + minH, full.Bottom);
        }
        // Keep the whole picture where it is: the item's centre follows the middle of the kept part.
        var posed = o.Posed(LocalTime(o));
        var centre = OverlayRenderer.Placement(posed, state, VideoWidth, VideoHeight).Transform(new Point((left + right) / 2, (top + bottom) / 2));
        return o.Shifted(centre.X / VideoWidth - state.Dx - posed.X, centre.Y / VideoHeight - state.Dy - posed.Y) with
        {
            CropLeft = (left - full.Left) / full.Width, CropRight = (full.Right - right) / full.Width,
            CropTop = (top - full.Top) / full.Height, CropBottom = (full.Bottom - bottom) / full.Height,
        };
    }

    // ---- Mouse ----
    private enum Grip { None, Move, Scale, Rotate, WrapLeft, WrapRight, Tail, Point, Crop, ShapeCorner, Width, Height }
    private int cropEdges;
    private Grip grip; private Point press; private OverlayItem? original; private int pointIndex; private bool moved;
    protected override HitTestResult? HitTestCore(PointHitTestParameters p) => Drawing != null || ItemAt(p.HitPoint) >= 0 || GripAt(p.HitPoint).Grip != Grip.None ? new PointHitTestResult(this, p.HitPoint) : null;
    private int ItemAt(Point p)
    {
        foreach (var (item, index) in items.Select((o, i) => (o, i)).OrderByDescending(x => x.o.Layer).ThenByDescending(x => x.o.Start))
        {
            if (!Visible(item)) continue;
            var m = ItemToScreen(item, out var content, out _);
            if (!m.HasInverse) continue;
            if (Rect.Inflate(HandleBox(item, content), 4, 4).Contains(Inverse(m).Transform(p))) return index;
        }
        return -1;
    }
    private static Matrix Inverse(Matrix m) { m.Invert(); return m; }
    private (Grip Grip, int Point) GripAt(Point p)
    {
        if (Current is not { } item || !Visible(item)) return (Grip.None, -1);
        if (Cropping) { int edges = CropEdgesAt(item, p); return edges != 0 ? (Grip.Crop, edges) : (Grip.None, -1); }
        var m = ItemToScreen(item, out var content, out _);
        var c = Corners(HandleBox(item, content), m);
        bool Near(Point a, double r = 8) => (a - p).Length <= r;
        if (ShapeEditing)
        {
            if (item.Shape == OverlayShape.Bubble && Near(m.Transform(OverlayRenderer.TailTip(item)))) return (Grip.Tail, -1);
            if (HasPoints(item)) { var pts = CustomPoints(item, m).ToList(); for (int i = 0; i < pts.Count; i++) if (Near(pts[i])) return (Grip.Point, i); }
            else { var sc = ShapeCornersOnScreen(item, m); for (int i = 0; i < 4; i++) if (Near(sc[i], 9)) return (Grip.ShapeCorner, i); }
            return (Grip.None, -1);
        }
        if (HasShape(item) && HasPoints(item))
        {
            var pts = CustomPoints(item, m).ToList();
            for (int i = 0; i < pts.Count; i++) if (Near(pts[i], 7)) return (Grip.Point, i);
        }
        if (HasShape(item) && item.Shape == OverlayShape.Bubble && Near(m.Transform(OverlayRenderer.TailTip(item)))) return (Grip.Tail, -1);
        if (Near(RotateHandle(c).Handle)) return (Grip.Rotate, -1);
        if (c.Any(x => Near(x))) return (Grip.Scale, -1);
        if (item.Kind == OverlayKind.Text) { var s = SideHandles(c); if (Near(s[0])) return (Grip.WrapLeft, -1); if (Near(s[1])) return (Grip.WrapRight, -1); }
        if (item.Kind == OverlayKind.Shape)
        {
            if (SideHandles(c).Any(s => Near(s))) return (Grip.Width, -1);
            if (TopBottomHandles(c).Any(s => Near(s))) return (Grip.Height, -1);
        }
        return (Grip.None, -1);
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var p = e.GetPosition(this);
        if (Drawing != null) { DrawDown(p, e); e.Handled = true; return; }
        var (g, point) = GripAt(p);
        if (Cropping)
        {
            // Double-click, or click outside the crop box, to finish cropping.
            if (g == Grip.None || e.ClickCount == 2) { SetCropping(false); if (g == Grip.None && ItemAt(p) < 0) return; if (e.ClickCount == 2) { e.Handled = true; return; } }
            else { grip = g; cropEdges = point; press = p; original = Current; moved = false; CaptureMouse(); e.Handled = true; return; }
            (g, point) = GripAt(p);
        }
        if (ShapeEditing)
        {
            if (g != Grip.None) { grip = g; pointIndex = point; press = p; original = Current; moved = false; CaptureMouse(); e.Handled = true; return; }
            // Double-clicking a custom shape's edge adds a corner there; otherwise a double-click,
            // or a click away from the shape, finishes reshaping.
            if (e.ClickCount == 2 && Current is { } custom && HasPoints(custom) && ItemAt(p) == selected) { AddPoint(custom, p); e.Handled = true; return; }
            SetShapeEditing(false);
            if (e.ClickCount == 2 || ItemAt(p) < 0) { e.Handled = e.ClickCount == 2; return; }
            (g, point) = GripAt(p);
        }
        if (g == Grip.None)
        {
            int hit = ItemAt(p);
            if (hit < 0) return;
            if (hit != selected) { Picked?.Invoke(hit); }
            g = Grip.Move;
            // Double-clicking a picture or video crops it; a shape reshapes; text is typed on the video.
            if (e.ClickCount == 2 && Current is { Kind: OverlayKind.Image or OverlayKind.Video }) { SetCropping(true); e.Handled = true; return; }
            if (e.ClickCount == 2 && Current is { Kind: OverlayKind.Shape }) { SetShapeEditing(true); e.Handled = true; return; }
            if (e.ClickCount == 2 && Current is { Kind: OverlayKind.Text }) { TextEditRequested?.Invoke(selected); e.Handled = true; return; }
        }
        if (Current == null) return;
        grip = g; pointIndex = point; press = p; original = Current; moved = false; hovering = true;
        CaptureMouse(); DrawHandles(); e.Handled = true;
    }
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (Drawing == DrawTool.Corners) { if (stroke.Count > 0) stroke.RemoveAt(stroke.Count - 1); DrawHandles(); e.Handled = true; return; }
        // Right-click a custom shape's corner to remove it (a shape keeps at least three).
        var (g, point) = GripAt(e.GetPosition(this));
        if (g == Grip.Point && Current is { } item && item.Points.Count > (item.IsLine ? 2 : 3))
        {
            EditStarted?.Invoke(); Changed?.Invoke(item with { Points = item.Points.Where((_, i) => i != point).ToArray() }); EditFinished?.Invoke(); e.Handled = true;
        }
    }
    private void AddPoint(OverlayItem item, Point p)
    {
        var m = ItemToScreen(item, out _, out _); if (!m.HasInverse) return;
        var box = ShapeFrame(item); var local = Inverse(m).Transform(p);
        var at = ((local.X - box.X) / box.Width - .5, (local.Y - box.Y) / box.Height - .5);
        // Insert between the two corners whose edge is nearest.
        var pts = item.Points.ToList(); int best = 0; double bestDistance = double.MaxValue;
        for (int i = 0; i < pts.Count; i++)
        {
            var a = pts[i]; var b = pts[(i + 1) % pts.Count];
            double d = DistanceToSegment(at, a, b);
            if (d < bestDistance) { bestDistance = d; best = i + 1; }
        }
        pts.Insert(best, at);
        EditStarted?.Invoke(); Changed?.Invoke(item with { Points = pts.ToArray() }); EditFinished?.Invoke();
    }
    private static double DistanceToSegment((double X, double Y) p, (double X, double Y) a, (double X, double Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, len = dx * dx + dy * dy;
        double t = len == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len, 0, 1);
        double x = a.X + t * dx - p.X, y = a.Y + t * dy - p.Y; return Math.Sqrt(x * x + y * y);
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        if (Drawing != null) { DrawMove(p); ToolTip = Drawing switch { DrawTool.Freehand => "Drag to draw · finish near the start to close the shape · Esc cancels", DrawTool.Line => "Drag to draw a line · Shift keeps it straight · Esc cancels", _ => "Click each corner · double-click or Enter to finish · click the first corner to close · right-click takes one back" }; return; }
        if (grip == Grip.None || original is not { } o)
        {
            var (g, edges) = GripAt(p);
            if (Cropping)
            {
                Cursor = g != Grip.Crop ? null : edges == -1 ? Cursors.SizeAll : edges is EdgeLeft or EdgeRight ? Cursors.SizeWE : edges is EdgeTop or EdgeBottom ? Cursors.SizeNS
                    : edges == (EdgeLeft | EdgeTop) || edges == (EdgeRight | EdgeBottom) ? Cursors.SizeNWSE : Cursors.SizeNESW;
                ToolTip = "Drag the edges to crop · double-click or Esc when done";
                return;
            }
            if (ShapeEditing)
            {
                Cursor = g == Grip.None ? null : Cursors.Cross;
                ToolTip = "Drag the pink corners to reshape · double-click or Esc when done";
                return;
            }
            bool over = g != Grip.None || (selected >= 0 && ItemAt(p) == selected);
            if (over != hovering) { hovering = over; DrawHandles(); }
            Cursor = g switch { Grip.Scale => Cursors.SizeNWSE, Grip.Rotate => Cursors.Hand, Grip.WrapLeft or Grip.WrapRight or Grip.Width => Cursors.SizeWE, Grip.Height => Cursors.SizeNS, Grip.Tail or Grip.Point => Cursors.Cross, _ => ItemAt(p) >= 0 ? Cursors.SizeAll : null };
            ToolTip = g == Grip.None && ItemAt(p) is >= 0 and var at ? HoverTip(items[at]) : null;
            return;
        }
        if (!moved && (p - press).Length < 2) return;
        if (!moved) { moved = true; EditStarted?.Invoke(); }
        // Edits work on the item as it looks now; with keyframes, moving, scaling or turning it sets a
        // keyframe at this moment instead of changing it everywhere.
        double local = LocalTime(o);
        var posed = o.Posed(local);
        var toScreen = FrameToScreen(o);
        var m = ItemToScreen(o, out var content, out var state);
        var center = m.Transform(new Point(0, 0));
        OverlayItem next = posed;
        switch (grip)
        {
            case Grip.Crop: Changed?.Invoke(CropTo(o, p)); return;
            case Grip.ShapeCorner:
            {
                if (!m.HasInverse || pointIndex < 0 || pointIndex > 3) break;
                var at = Inverse(m).Transform(p);
                var box = ShapeFrame(o);
                var home = new[] { box.TopLeft, box.TopRight, box.BottomRight, box.BottomLeft }[pointIndex];
                var corners = o.ShapeCorners.ToArray(); corners[pointIndex] = (Math.Round(at.X - home.X, 1), Math.Round(at.Y - home.Y, 1));
                next = posed with { ShapeCorners = corners };
                break;
            }
            case Grip.Move:
            {
                var delta = Inverse(toScreen).Transform(p - press);
                double x = posed.X + delta.X / VideoWidth, y = posed.Y + delta.Y / VideoHeight;
                (x, y) = SnapPosition(posed, content, state, x, y);
                next = posed with { X = x, Y = y };
                break;
            }
            case Grip.Scale:
            {
                double from = (press - center).Length, to = (p - center).Length;
                next = posed with { Scale = Math.Clamp(posed.Scale * to / Math.Max(1, from), OverlayItem.MinScale, OverlayItem.MaxScale) };
                break;
            }
            case Grip.Rotate:
            {
                double a0 = Math.Atan2(press.Y - center.Y, press.X - center.X), a1 = Math.Atan2(p.Y - center.Y, p.X - center.X);
                double angle = posed.Rotation + (a1 - a0) * 180 / Math.PI;
                angle = ((angle % 360) + 540) % 360 - 180;
                // Shift steps by 15°; otherwise it settles on straight angles when close.
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) angle = Math.Round(angle / 15) * 15;
                else foreach (double straight in new[] { -180.0, -90, 0, 90, 180 }) if (Math.Abs(angle - straight) < 3) angle = straight;
                next = posed with { Rotation = angle };
                break;
            }
            case Grip.WrapLeft or Grip.WrapRight:
            {
                if (!m.HasInverse) break;
                var at = Inverse(m).Transform(p);
                double half = Math.Max(20, Math.Abs(at.X));
                // Wrap width is kept as a fraction of the frame width, at this item's scale.
                double aspect = VideoWidth / Math.Max(1, VideoHeight);
                next = posed with { WrapWidth = Math.Clamp(2 * half * posed.Scale * state.Scale / (aspect * OverlayItem.Reference), .02, 1.5) };
                break;
            }
            case Grip.Width or Grip.Height:
            {
                if (!m.HasInverse) break;
                var at = Inverse(m).Transform(p);
                next = grip == Grip.Width ? posed with { ShapeWidth = Math.Clamp(Math.Round(2 * Math.Abs(at.X)), 12, 4000) } : posed with { ShapeHeight = Math.Clamp(Math.Round(2 * Math.Abs(at.Y)), 12, 4000) };
                break;
            }
            case Grip.Tail:
            {
                if (!m.HasInverse) break;
                var at = Inverse(m).Transform(p);
                next = posed with { TailX = at.X, TailY = at.Y };
                break;
            }
            case Grip.Point when o.Kind == OverlayKind.Shape && o.Shape == OverlayShape.Polyline:
            {
                // A line's corner (or end) moves freely; the box refits around the corners so the
                // handles and the clickable area follow, and the rest of the line stays put.
                if (!m.HasInverse || pointIndex < 0 || pointIndex >= o.Points.Count) break;
                var box = ShapeFrame(o); var at = Inverse(m).Transform(p);
                var corners = o.Points.Select(q => new Point(box.X + (q.X + .5) * box.Width, box.Y + (q.Y + .5) * box.Height)).ToArray();
                corners[pointIndex] = at;
                double left = corners.Min(q => q.X), right = corners.Max(q => q.X), top = corners.Min(q => q.Y), bottom = corners.Max(q => q.Y);
                double w = Math.Max(right - left, 12), h = Math.Max(bottom - top, 12);
                var centre = new Point((left + right) / 2, (top + bottom) / 2);
                var onFrame = OverlayRenderer.Placement(posed, state, VideoWidth, VideoHeight).Transform(centre);
                Changed?.Invoke(o.Shifted(onFrame.X / VideoWidth - state.Dx - posed.X, onFrame.Y / VideoHeight - state.Dy - posed.Y) with
                {
                    Points = corners.Select(q => (Math.Round((q.X - centre.X) / w, 4), Math.Round((q.Y - centre.Y) / h, 4))).ToArray(),
                    ShapeWidth = w, ShapeHeight = h, ShapeCorners = OverlayItem.NoCorners,
                });
                return;
            }
            case Grip.Point:
            {
                if (!m.HasInverse || pointIndex < 0 || pointIndex >= o.Points.Count) break;
                var box = ShapeFrame(o); var at = Inverse(m).Transform(p);
                var pts = o.Points.ToArray(); pts[pointIndex] = ((at.X - box.X) / box.Width - .5, (at.Y - box.Y) / box.Height - .5);
                next = posed with { Points = pts };
                break;
            }
        }
        Changed?.Invoke(grip is Grip.Move or Grip.Scale or Grip.Rotate ? o.Adjusted(local, _ => next)
            : next with { X = o.X, Y = o.Y, Scale = o.Scale, Rotation = o.Rotation, Opacity = o.Opacity, Keys = o.Keys });
    }
    private static string HoverTip(OverlayItem item) => "Drag to move · scroll to scale · Shift+scroll to rotate · Ctrl+scroll for opacity · double-click to " + item.Kind switch
    {
        OverlayKind.Text => "type", OverlayKind.Shape => "reshape", _ => "crop"
    };
    // Centre lines, frame edges and a 5% safe margin pull the item in when it comes within a few pixels.
    private (double X, double Y) SnapPosition(OverlayItem o, Drawing content, OverlayState state, double x, double y)
    {
        guideX = guideY = null;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return (x, y);
        var toScreen = FrameToScreen(o);
        double pixel = Math.Max(1e-6, toScreen.M11) * VideoWidth, pixelY = Math.Max(1e-6, toScreen.M22) * VideoHeight;
        double reach = 6;
        var bounds = Rect.Transform(content.Bounds.IsEmpty ? new Rect(0, 0, 0, 0) : content.Bounds, OverlayRenderer.Placement(o with { X = x, Y = y }, state, VideoWidth, VideoHeight));
        double halfW = bounds.Width / 2 / VideoWidth, halfH = bounds.Height / 2 / VideoHeight;
        double centreX = x + state.Dx, centreY = y + state.Dy;
        double? bestX = null, bestY = null; double gapX = reach, gapY = reach;
        foreach (double line in new[] { .5, 0, 1, .05, .95 })
            foreach (double offset in line == .5 ? new[] { 0.0 } : new[] { -halfW, halfW })
            {
                double gap = Math.Abs(centreX + offset - line) * pixel;
                if (gap < gapX) { gapX = gap; bestX = line - offset - state.Dx; guideX = (line, 0); }
            }
        foreach (double line in new[] { .5, 0, 1, .05, .95 })
            foreach (double offset in line == .5 ? new[] { 0.0 } : new[] { -halfH, halfH })
            {
                double gap = Math.Abs(centreY + offset - line) * pixelY;
                if (gap < gapY) { gapY = gap; bestY = line - offset - state.Dy; guideY = (0, line); }
            }
        // Guides are drawn in this element's coordinates.
        if (guideX is { } gx) guideX = (toScreen.Transform(new Point(gx.X * VideoWidth, 0)).X, 0);
        if (guideY is { } gy) guideY = (0, toScreen.Transform(new Point(0, gy.Y * VideoHeight)).Y);
        return (bestX ?? x, bestY ?? y);
    }
    // Over an item: the wheel scales it, Shift+wheel rotates it and Ctrl+wheel changes its opacity.
    internal event Action<int, string, Func<OverlayItem, OverlayItem>>? WheelAdjusted;
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        int index = ItemAt(e.GetPosition(this));
        if (index < 0 || grip != Grip.None) return;
        double notches = e.Delta / 120.0;
        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.Shift)
            WheelAdjusted?.Invoke(index, "wheelRotation", o => o with { Rotation = Math.Round((((o.Rotation + notches * 5) % 360) + 540) % 360 - 180, 1) });
        else if (mods == ModifierKeys.Control)
            WheelAdjusted?.Invoke(index, "wheelOpacity", o => o with { Opacity = Math.Clamp(Math.Round(o.Opacity + notches * .05, 2), 0, 1) });
        else if (mods == ModifierKeys.None)
            WheelAdjusted?.Invoke(index, "wheelScale", o => o with { Scale = Math.Clamp(Math.Round(o.Scale * Math.Pow(1.08, notches), 3), OverlayItem.MinScale, OverlayItem.MaxScale) });
        else return;
        e.Handled = true;
    }
    private bool hovering;
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); if (hovering && grip == Grip.None) { hovering = false; DrawHandles(); } }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { base.OnMouseLeftButtonUp(e); if (Drawing != null) { DrawUp(); return; } Finish(); }
    protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); Finish(); }
    private void Finish()
    {
        if (grip == Grip.None) return;
        grip = Grip.None; original = null; guideX = guideY = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (moved) { moved = false; EditFinished?.Invoke(); }
        hovering = IsMouseOver && (GripAt(Mouse.GetPosition(this)).Grip != Grip.None || ItemAt(Mouse.GetPosition(this)) == selected);
        DrawHandles();
    }
}
