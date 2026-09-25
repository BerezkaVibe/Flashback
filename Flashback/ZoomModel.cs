using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Flashback;

// A zoom ramp: points of (seconds from the ramp's start, fraction of the way to max zoom 0..1).
// The first point is always (0, 0) and the last is (ramp length, 1). Between points the curve is a
// monotone cubic, so it eases smoothly without overshooting.
internal sealed record ZoomCurve(IReadOnlyList<(double T, double F)> Points)
{
    internal double Length => Points[^1].T;
    internal static readonly ZoomCurve Smooth = new(new[] { (0.0, 0.0), (.2, .12), (.4, .5), (.6, .88), (.8, 1.0) });

    // Fraction of the way to max zoom, t seconds into the ramp.
    internal double At(double t)
    {
        var p = Points;
        if (t <= 0) return 0;
        if (t >= p[^1].T) return 1;
        int i = 0; while (i < p.Count - 2 && t > p[i + 1].T) i++;
        // Fritsch-Carlson tangents keep each segment within its end values.
        double Slope(int k) => (p[k + 1].F - p[k].F) / Math.Max(1e-9, p[k + 1].T - p[k].T);
        double Tangent(int k)
        {
            // Flat at both ends, so the ramp eases out of the full frame and into the plateau.
            if (k == 0 || k == p.Count - 1) return 0;
            double a = Slope(k - 1), b = Slope(k);
            return a * b <= 0 ? 0 : 3 * (p[k + 1].T - p[k - 1].T) / ((2 * p[k + 1].T - p[k].T - p[k - 1].T) / a + (p[k + 1].T + p[k].T - 2 * p[k - 1].T) / b);
        }
        double h = p[i + 1].T - p[i].T, s = (t - p[i].T) / h, m0 = Tangent(i) * h, m1 = Tangent(i + 1) * h;
        double v = (2 * s * s * s - 3 * s * s + 1) * p[i].F + (s * s * s - 2 * s * s + s) * m0 + (-2 * s * s * s + 3 * s * s) * p[i + 1].F + (s * s * s - s * s) * m1;
        return Math.Clamp(v, 0, 1);
    }
    // Stretches or squeezes the ramp in time.
    internal ZoomCurve Scaled(double factor) => new(Points.Select(p => (p.T * factor, p.F)).ToArray());
    internal ZoomCurve Validated()
    {
        var pts = Points.OrderBy(p => p.T).ToList();
        if (pts.Count < 2) return Smooth;
        pts[0] = (0, 0); pts[^1] = (Math.Clamp(pts[^1].T, .05, ZoomRegion.MaxRamp), 1);
        for (int i = 1; i < pts.Count - 1; i++) pts[i] = (Math.Clamp(pts[i].T, pts[i - 1].T + .01, pts[^1].T - .01), Math.Clamp(pts[i].F, 0, 1));
        return new ZoomCurve(pts);
    }
}

// A zoomed stretch of the clip, in source time. X and Y are the zoom target (0..1 of the frame).
// Without zoom-out the view stays zoomed to the end and then returns to the full frame at once.
internal sealed record ZoomRegion(double Start, double End, double X, double Y, double MaxZoom, ZoomCurve In, bool ZoomOut = false, ZoomCurve? Out = null)
{
    // Seconds into the hold of a freeze frame at Start or End, for zooms that start or stop partway
    // through one (see HoldTiming).
    public double StartHold { get; init; }
    public double EndHold { get; init; }
    // Keeps zooming while a freeze holds the picture; off, it freezes with the picture.
    public bool ThroughFreezes { get; init; } = true;
    internal const double Limit = 8, MaxRamp = 3;
    // The zoom-out ramp: its own curve, or the zoom-in curve mirrored.
    internal ZoomCurve OutCurve => Out ?? In;
    // Zoom level at a source time (1 outside the region).
    internal double ZoomAt(double time)
    {
        if (time < Start || time >= End) return 1;
        double length = End - Start, inRamp = In.Length, outRamp = ZoomOut ? OutCurve.Length : 0;
        // Ramps longer than the region are squeezed to fit.
        double squeeze = Math.Min(1, length / Math.Max(1e-9, inRamp + outRamp));
        double f = In.At((time - Start) / squeeze);
        if (ZoomOut) f = Math.Min(f, OutCurve.At((End - time) / squeeze));
        return 1 + (MaxZoom - 1) * f;
    }
    // Normalized top-left corner of the visible area at a zoom level, kept inside the frame.
    internal (double Left, double Top) ViewAt(double zoom) =>
        (Math.Clamp(X - .5 / zoom, 0, 1 - 1 / zoom), Math.Clamp(Y - .5 / zoom, 0, 1 - 1 / zoom));

    // ffmpeg expressions for zoompan over a piece that starts at pieceStart in source time.
    // The curve is sampled into short straight segments so the export matches the preview.
    internal static (string Zoom, string X, string Y) Expressions(IReadOnlyList<ZoomRegion> regions, double pieceStart, double frameRate)
    {
        string N(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
        string time = $"(it+{N(pieceStart)})";
        var zoom = new StringBuilder("1"); var x = new StringBuilder("0.5"); var y = new StringBuilder("0.5");
        foreach (var r in regions)
        {
            // Sample every couple of frames; flat stretches (the plateau) merge into one term.
            double step = Math.Max(2 / Math.Max(1, frameRate), (r.End - r.Start) / 400);
            var cuts = new List<double>();
            for (double a = r.Start; a < r.End - 1e-9; a += step) cuts.Add(a);
            cuts.Add(r.End);
            double runStart = cuts[0], runValue = r.ZoomAt(cuts[0]);
            void Flush(double end) { if (Math.Abs(runValue - 1) > 1e-6 && end > runStart) zoom.Append($"+gte({time},{N(runStart)})*lt({time},{N(end)})*{N(runValue - 1)}"); }
            for (int i = 0; i + 1 < cuts.Count; i++)
            {
                double a = cuts[i], b = cuts[i + 1], za = r.ZoomAt(a), zb = r.ZoomAt(Math.Min(b, r.End - 1e-6));
                if (Math.Abs(zb - za) < 1e-6 && Math.Abs(za - runValue) < 1e-6) continue; // still flat
                Flush(a);
                if (Math.Abs(zb - za) < 1e-6) { runStart = a; runValue = za; continue; }
                zoom.Append($"+gte({time},{N(a)})*lt({time},{N(b)})*({N(za - 1)}+({time}-{N(a)})*{N((zb - za) / (b - a))})");
                runStart = b; runValue = double.NaN;
            }
            if (!double.IsNaN(runValue)) Flush(r.End);
            x.Append($"+gte({time},{N(r.Start)})*lt({time},{N(r.End)})*{N(r.X - .5)}");
            y.Append($"+gte({time},{N(r.Start)})*lt({time},{N(r.End)})*{N(r.Y - .5)}");
        }
        return (zoom.ToString(), $"clip(iw*({x})-iw/zoom/2,0,iw-iw/zoom)", $"clip(ih*({y})-ih/zoom/2,0,ih-ih/zoom)");
    }
}

// Named zoom settings. Built-ins ship with the app; saved ones live in zoom-presets.json so
// they survive settings changes made in the main window.
internal sealed record ZoomPreset(string Name, double MaxZoom, (double T, double F)[] Points, bool BuiltIn = false)
{
    internal ZoomCurve Curve => new ZoomCurve(Points).Validated();
    internal static readonly ZoomPreset[] BuiltIns =
    {
        new("Smooth", 2, new[] { (0.0, 0.0), (.2, .12), (.4, .5), (.6, .88), (.8, 1.0) }, true),
        new("Snappy", 2, new[] { (0.0, 0.0), (.08, .35), (.16, .8), (.24, .96), (.3, 1.0) }, true),
        new("Punch-in", 2.5, new[] { (0.0, 0.0), (.03, .6), (.06, .9), (.09, .98), (.12, 1.0) }, true),
        new("Slow push", 1.6, new[] { (0.0, 0.0), (.6, .2), (1.2, .55), (1.8, .85), (2.4, 1.0) }, true),
    };
    private static string FilePath => Path.Combine(Storage.Root, "zoom-presets.json");
    private sealed record Stored(string Name, double MaxZoom, double[][] Points);
    internal static List<ZoomPreset> LoadSaved()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var stored = JsonSerializer.Deserialize<List<Stored>>(File.ReadAllText(FilePath)) ?? new();
            return stored.Where(s => !string.IsNullOrWhiteSpace(s.Name) && s.Points?.Length >= 2)
                .Select(s => new ZoomPreset(s.Name, Math.Clamp(s.MaxZoom, 1.1, ZoomRegion.Limit), s.Points.Select(p => (p[0], p[1])).ToArray())).ToList();
        }
        catch { return new(); }
    }
    internal static void SaveAll(IEnumerable<ZoomPreset> saved)
    {
        Directory.CreateDirectory(Storage.Root);
        var data = saved.Where(p => !p.BuiltIn).Select(p => new Stored(p.Name, p.MaxZoom, p.Points.Select(q => new[] { q.T, q.F }).ToArray())).ToList();
        var temp = FilePath + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(data)); File.Move(temp, FilePath, true);
    }
}
