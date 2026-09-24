using System;
using System.Collections.Generic;
using System.Linq;

namespace Flashback;

internal enum OverlayKind { Text, Image }
// What sits behind the words: nothing, a bar behind each line, or one shape around all of them.
internal enum OverlayShape { None, Lines, Box, Pill, Ellipse, Bubble, Arrow, Custom }
internal enum OverlayMotion { None, Fade, Pop, SlideUp, SlideDown, SlideLeft, SlideRight, Typewriter }
internal enum OverlayAlign { Left, Center, Right }
// Background removal for images: a solid color turns see-through.
internal enum OverlayKey { None, Green, White, Black, Custom }

internal sealed record OverlayShadow(bool On = false, string Color = "#000000", double Opacity = .7, double Distance = 5, double Angle = 315, double Blur = 8);

// A caption or picture shown over part of the clip, in source time. Sizes are in pixels on a
// 1080-line frame, so an item looks the same at any export resolution. X and Y are the item's
// centre as a fraction of the frame. Items stuck to the video follow zoom parts; the rest stay
// fixed on screen. Layer orders items front to back (higher is in front).
internal sealed record OverlayItem
{
    internal const double Reference = 1080, MinScale = .1, MaxScale = 8;
    public OverlayKind Kind { get; init; }
    public double Start { get; init; }
    public double End { get; init; }
    public double X { get; init; } = .5;
    public double Y { get; init; } = .5;
    public double Scale { get; init; } = 1;
    public double Rotation { get; init; }
    public double Opacity { get; init; } = 1;
    public bool StickToVideo { get; init; } = true;
    public int Layer { get; init; }
    public OverlayMotion In { get; init; }
    public double InLength { get; init; } = .3;
    public OverlayMotion Out { get; init; }
    public double OutLength { get; init; } = .3;
    public OverlayShadow Shadow { get; init; } = new();

    // Text
    public string Text { get; init; } = "";
    public string Font { get; init; } = "Impact";
    public double Size { get; init; } = 72;
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public bool Caps { get; init; } = true;
    public OverlayAlign Align { get; init; } = OverlayAlign.Center;
    public double LetterSpacing { get; init; }
    public double LineSpacing { get; init; } = 1;
    // Wrap width as a fraction of the frame width; 0 keeps each typed line on one line.
    public double WrapWidth { get; init; }
    public string Fill { get; init; } = "#FFFFFF";
    // A second color turns the fill into a gradient at GradientAngle degrees.
    public string? Fill2 { get; init; }
    public double GradientAngle { get; init; } = 90;
    public string OutlineColor { get; init; } = "#000000";
    public double OutlineWidth { get; init; } = 5;
    public OverlayShape Shape { get; init; }
    public string ShapeColor { get; init; } = "#E6000000";
    public double Padding { get; init; } = 14;
    public double Corner { get; init; } = 10;
    public string ShapeBorderColor { get; init; } = "#FFFFFF";
    public double ShapeBorderWidth { get; init; }
    // Speech bubble tail tip, relative to the item's centre in reference pixels (before scale).
    public double TailX { get; init; } = -60;
    public double TailY { get; init; } = 110;
    // Custom shape corners, as fractions of the padded text box (-0.5..0.5 is the box itself).
    public IReadOnlyList<(double X, double Y)> Points { get; init; } = DefaultPoints;
    internal static readonly (double X, double Y)[] DefaultPoints = { (-.5, -.5), (.5, -.5), (.5, .5), (-.5, .5) };

    // Image
    public string ImagePath { get; init; } = "";
    public bool FlipX { get; init; }
    public bool FlipY { get; init; }
    // Crop margins as fractions of the picture (left, top, right, bottom).
    public double CropLeft { get; init; }
    public double CropTop { get; init; }
    public double CropRight { get; init; }
    public double CropBottom { get; init; }
    public double ImageCorner { get; init; }
    public double BorderWidth { get; init; }
    public string BorderColor { get; init; } = "#FFFFFF";
    public OverlayKey Key { get; init; }
    public string KeyColor { get; init; } = "#00FF00";
    public double KeyTolerance { get; init; } = .3;

    internal double Length => End - Start;
    internal string DisplayText => Caps ? Text.ToUpperInvariant() : Text;
    internal string Label => Kind == OverlayKind.Image ? System.IO.Path.GetFileName(ImagePath) : (Text.Replace('\n', ' ').Trim() is { Length: > 0 } t ? t : "Text");

    internal static OverlayItem NewText(double start, double end, OverlayItem? style) =>
        (style is { Kind: OverlayKind.Text } s ? s with { Rotation = 0, Scale = 1, X = .5, Y = .5, Layer = 0 } : new OverlayItem()) with { Kind = OverlayKind.Text, Start = start, End = end, Text = "Your text", Y = .82 };
    internal static OverlayItem NewImage(double start, double end, string path) =>
        new() { Kind = OverlayKind.Image, Start = start, End = end, ImagePath = path, Caps = false, OutlineWidth = 0 };

    // Keeps every value in range; used when loading and before export.
    internal OverlayItem Validated() => this with
    {
        X = Math.Clamp(Finite(X, .5), -.5, 1.5), Y = Math.Clamp(Finite(Y, .5), -.5, 1.5),
        Scale = Math.Clamp(Finite(Scale, 1), MinScale, MaxScale), Rotation = Finite(Rotation, 0) % 360,
        Opacity = Math.Clamp(Finite(Opacity, 1), 0, 1),
        InLength = Math.Clamp(Finite(InLength, .3), .05, 5), OutLength = Math.Clamp(Finite(OutLength, .3), .05, 5),
        Size = Math.Clamp(Finite(Size, 72), 8, 400), LetterSpacing = Math.Clamp(Finite(LetterSpacing, 0), -20, 100),
        LineSpacing = Math.Clamp(Finite(LineSpacing, 1), .6, 3), WrapWidth = Math.Clamp(Finite(WrapWidth, 0), 0, 1.5),
        OutlineWidth = Math.Clamp(Finite(OutlineWidth, 0), 0, 40), Padding = Math.Clamp(Finite(Padding, 0), 0, 200),
        Corner = Math.Clamp(Finite(Corner, 0), 0, 200), ShapeBorderWidth = Math.Clamp(Finite(ShapeBorderWidth, 0), 0, 40),
        ImageCorner = Math.Clamp(Finite(ImageCorner, 0), 0, 50), BorderWidth = Math.Clamp(Finite(BorderWidth, 0), 0, 60),
        KeyTolerance = Math.Clamp(Finite(KeyTolerance, .3), 0, 1),
        CropLeft = Math.Clamp(Finite(CropLeft, 0), 0, .9), CropTop = Math.Clamp(Finite(CropTop, 0), 0, .9),
        CropRight = Math.Clamp(Finite(CropRight, 0), 0, .9 - Math.Clamp(Finite(CropLeft, 0), 0, .9)),
        CropBottom = Math.Clamp(Finite(CropBottom, 0), 0, .9 - Math.Clamp(Finite(CropTop, 0), 0, .9)),
        Points = Points is { Count: >= 3 } ? Points : DefaultPoints,
    };
    private static double Finite(double v, double fallback) => double.IsFinite(v) ? v : fallback;

    // How the item looks at a moment, t seconds into it: entrance and exit motion.
    internal OverlayState StateAt(double t)
    {
        double alpha = 1, scale = 1, dx = 0, dy = 0; double reveal = 1;
        void Apply(OverlayMotion motion, double p)
        {
            if (p >= 1) return;
            p = Math.Clamp(p, 0, 1);
            double ease = 1 - Math.Pow(1 - p, 3);
            switch (motion)
            {
                case OverlayMotion.Fade: alpha *= p; break;
                case OverlayMotion.Pop:
                    // Grows past full size and settles back.
                    const double c = 1.70158;
                    scale *= .3 + .7 * (1 + (c + 1) * Math.Pow(p - 1, 3) + c * Math.Pow(p - 1, 2));
                    alpha *= Math.Min(1, p * 4); break;
                case OverlayMotion.SlideUp: dy += (1 - ease) * .2; alpha *= Math.Min(1, p * 2); break;
                case OverlayMotion.SlideDown: dy -= (1 - ease) * .2; alpha *= Math.Min(1, p * 2); break;
                case OverlayMotion.SlideLeft: dx += (1 - ease) * .2; alpha *= Math.Min(1, p * 2); break;
                case OverlayMotion.SlideRight: dx -= (1 - ease) * .2; alpha *= Math.Min(1, p * 2); break;
                case OverlayMotion.Typewriter:
                    if (Kind == OverlayKind.Text) reveal = Math.Min(reveal, p); else alpha *= p;
                    break;
            }
        }
        double length = Math.Max(1e-6, Length);
        // Entrance and exit share the item when it is shorter than both.
        double squeeze = Math.Min(1, length / Math.Max(1e-6, (In != OverlayMotion.None ? InLength : 0) + (Out != OverlayMotion.None ? OutLength : 0)));
        if (In != OverlayMotion.None) Apply(In, t / (InLength * squeeze));
        if (Out != OverlayMotion.None)
        {
            // Leaving mirrors arriving: slides go back the way they came in reverse.
            var motion = Out switch { OverlayMotion.SlideUp => OverlayMotion.SlideDown, OverlayMotion.SlideDown => OverlayMotion.SlideUp,
                OverlayMotion.SlideLeft => OverlayMotion.SlideRight, OverlayMotion.SlideRight => OverlayMotion.SlideLeft, _ => Out };
            Apply(motion, (length - t) / (OutLength * squeeze));
        }
        int chars = DisplayText.Length;
        return new OverlayState(Opacity * alpha, scale, dx, dy, reveal >= 1 ? chars : (int)Math.Floor(reveal * chars + 1e-9));
    }
    // Times within the item where its entrance and exit animate (for sampling frames on export).
    internal IEnumerable<(double From, double To)> Animated()
    {
        double length = Length;
        double squeeze = Math.Min(1, length / Math.Max(1e-6, (In != OverlayMotion.None ? InLength : 0) + (Out != OverlayMotion.None ? OutLength : 0)));
        if (In != OverlayMotion.None) yield return (0, Math.Min(length, InLength * squeeze));
        if (Out != OverlayMotion.None) yield return (Math.Max(0, length - OutLength * squeeze), length);
    }
}

// Offsets are fractions of the frame; Chars is how many characters show (typewriter).
internal readonly record struct OverlayState(double Opacity, double Scale, double Dx, double Dy, int Chars);

internal static class OverlayOrder
{
    // Back to front: lower layers first, then earlier items.
    internal static IEnumerable<OverlayItem> BackToFront(IEnumerable<OverlayItem> items) => items.OrderBy(i => i.Layer).ThenBy(i => i.Start);
    // Renumbers layers 0, 1, 2… with no empty rows between them, keeping the list order.
    internal static OverlayItem[] Compact(IReadOnlyList<OverlayItem> items)
    {
        var used = items.Select(i => i.Layer).Distinct().OrderBy(l => l).Select((layer, index) => (layer, index)).ToDictionary(p => p.layer, p => p.index);
        return items.Select(i => used[i.Layer] == i.Layer ? i : i with { Layer = used[i.Layer] }).ToArray();
    }
    // The lowest layer where a new item fits without overlapping another.
    internal static int FreeLayer(IEnumerable<OverlayItem> items, double start, double end)
    {
        var list = items.ToList();
        for (int layer = 0; ; layer++)
            if (!list.Any(i => i.Layer == layer && i.End > start + 1e-9 && i.Start < end - 1e-9)) return layer;
    }
    // Moves an item one layer forward (up) or back (down). If something is in the way it trades places;
    // sending the bottom item back puts it on a new bottom layer by itself.
    internal static OverlayItem[] Restack(IReadOnlyList<OverlayItem> items, int index, int direction)
    {
        var list = items.ToArray(); var item = list[index]; int target = item.Layer + direction;
        if (target < 0)
        {
            for (int i = 0; i < list.Length; i++) if (i != index) list[i] = list[i] with { Layer = list[i].Layer + 1 };
            return Compact(list);
        }
        bool Overlaps(OverlayItem o) => o.End > item.Start + 1e-9 && o.Start < item.End - 1e-9;
        var blockers = list.Select((o, i) => (o, i)).Where(p => p.i != index && p.o.Layer == target && Overlaps(p.o)).ToList();
        // Trade places only when the blockers fit where this item was.
        bool swap = blockers.All(b => !list.Where((o, i) => i != index && !blockers.Any(x => x.i == i) && o.Layer == item.Layer).Any(o => o.End > b.o.Start + 1e-9 && o.Start < b.o.End - 1e-9));
        if (blockers.Count > 0 && !swap) return list;
        foreach (var b in blockers) list[b.i] = b.o with { Layer = item.Layer };
        list[index] = item with { Layer = target };
        return Compact(list);
    }
}
