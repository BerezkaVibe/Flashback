using System;
using System.Collections.Generic;

namespace Flashback;

// A moment of the finished video, as the recording's moment plus how far into the hold of a freeze frame
// at that moment it is (0 when it isn't in a hold).
internal readonly record struct Moment(double At, double Hold = 0);

// Parts (text, pictures, shapes, videos, zooms) are stored in recording time, and their start and end can
// also sit partway into a freeze's hold. A part covers [start, end): a start at a freeze's moment with no
// hold time begins with the hold; an end there with no hold time stops just before it; an end past the
// moment covers the whole hold.
//
// Each part has its own clock (for fades, keyframes, GIF frames, zoom ramps and where a video is): the
// recording's seconds since it started, plus the time of the holds it's shown in, except a hold it covers
// completely stands still with the frozen picture, unless it keeps going through freezes.
internal static class HoldTiming
{
    private const double Tolerance = 1e-9;
    private static bool Same(double a, double b) => Math.Abs(a - b) < Tolerance;
    internal static int Compare(Moment a, Moment b)
    {
        if (!Same(a.At, b.At)) return a.At < b.At ? -1 : 1;
        return Same(a.Hold, b.Hold) ? 0 : a.Hold < b.Hold ? -1 : 1;
    }
    internal static bool Covers(Moment start, Moment end, Moment now) => Compare(start, now) <= 0 && Compare(now, end) < 0;
    internal static bool Overlaps(Moment startA, Moment endA, Moment startB, Moment endB) => Compare(startA, endB) < 0 && Compare(startB, endA) < 0;
    // How long a freeze at a moment holds (0 if there's none).
    internal static double HoldAt(double at, IReadOnlyList<FreezeFrame> freezes)
    {
        foreach (var f in freezes) if (Same(f.At, at)) return f.Seconds;
        return 0;
    }
    // A part's own time at a moment (clamped to the part).
    internal static double Clock(Moment start, Moment end, Moment now, IReadOnlyList<FreezeFrame> freezes, bool through)
    {
        if (Compare(now, start) < 0) now = start;
        if (Compare(now, end) > 0) now = end;
        double clock = now.At - start.At;
        foreach (var f in freezes)
        {
            if (f.At < start.At - Tolerance || f.At > now.At + Tolerance) continue;
            double from = Same(f.At, start.At) ? start.Hold : 0, to = Math.Min(f.Seconds, Same(f.At, now.At) ? now.Hold : f.Seconds);
            if (to - from <= Tolerance) continue;
            // Covered completely: shown from before the hold began until after it ended.
            bool whole = Compare(start, new Moment(f.At)) <= 0 && end.At > f.At + Tolerance;
            if (through || !whole) clock += to - from;
        }
        return clock;
    }
    internal static double Duration(Moment start, Moment end, IReadOnlyList<FreezeFrame> freezes, bool through) => Clock(start, end, end, freezes, through);

    // Text, pictures, shapes and videos. Only videos keep going through freezes.
    internal static Moment From(this OverlayItem o) => new(o.Start, o.StartHold);
    internal static Moment To(this OverlayItem o) => new(o.End, o.EndHold);
    internal static bool Through(this OverlayItem o) => o.Kind == OverlayKind.Video && o.ThroughFreezes;
    internal static bool InHolds(this OverlayItem o) => o.StartHold > 0 || o.EndHold > 0;
    internal static bool Covers(this OverlayItem o, Moment now) => Covers(o.From(), o.To(), now);
    internal static bool Overlaps(this OverlayItem a, OverlayItem b) => Overlaps(a.From(), a.To(), b.From(), b.To());
    internal static double Clock(this OverlayItem o, Moment now, IReadOnlyList<FreezeFrame> freezes) => Clock(o.From(), o.To(), now, freezes, o.Through());
    internal static double Duration(this OverlayItem o, IReadOnlyList<FreezeFrame> freezes) => Duration(o.From(), o.To(), freezes, o.Through());
    // The part laid out on its own clock from Start (no hold parts), so everything that draws a part from
    // its Start and Length (fades, keyframes, GIF frames) draws it the same way inside holds too.
    internal static OverlayItem Timed(this OverlayItem o, IReadOnlyList<FreezeFrame> freezes)
    {
        if (!o.InHolds() && (freezes.Count == 0 || !o.Through())) return o;
        double length = o.Duration(freezes);
        return Math.Abs(length - o.Length) < Tolerance && !o.InHolds() ? o : o with { End = o.Start + length, StartHold = 0, EndHold = 0 };
    }

    // Zooms keep going through freezes unless that's turned off.
    internal static Moment From(this ZoomRegion z) => new(z.Start, z.StartHold);
    internal static Moment To(this ZoomRegion z) => new(z.End, z.EndHold);
    internal static bool InHolds(this ZoomRegion z) => z.StartHold > 0 || z.EndHold > 0;
    internal static bool Covers(this ZoomRegion z, Moment now) => Covers(z.From(), z.To(), now);
    internal static bool Overlaps(this ZoomRegion a, ZoomRegion b) => Overlaps(a.From(), a.To(), b.From(), b.To());
    internal static double Clock(this ZoomRegion z, Moment now, IReadOnlyList<FreezeFrame> freezes) => Clock(z.From(), z.To(), now, freezes, z.ThroughFreezes);
    internal static double Duration(this ZoomRegion z, IReadOnlyList<FreezeFrame> freezes) => Duration(z.From(), z.To(), freezes, z.ThroughFreezes);
    // How far a zoom is zoomed in at a moment of the finished video.
    internal static double ZoomAt(this ZoomRegion z, Moment now, IReadOnlyList<FreezeFrame> freezes)
    {
        if (!z.Covers(now)) return 1;
        if (!z.InHolds() && freezes.Count == 0) return z.ZoomAt(now.At);
        double length = z.Duration(freezes);
        return (z with { Start = 0, End = length, StartHold = 0, EndHold = 0 }).ZoomAt(Math.Min(z.Clock(now, freezes), length - 1e-6));
    }
}
