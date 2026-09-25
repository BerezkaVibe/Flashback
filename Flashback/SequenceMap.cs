using System;
using System.Collections.Generic;
using System.Linq;

namespace Flashback;

// The finished video laid out in time: the kept sections in the order they play, with speed parts
// stretched or squeezed and freeze frames holding. Built from the same pieces the export uses, so the
// finished view of the timeline shows exactly what gets exported. Parts stay stored in recording
// (source) time; this maps between the two.
internal sealed class SequenceMap
{
    private readonly ExportPiece[] pieces;
    private readonly double[] starts;   // where each piece begins in the finished video
    private readonly int[] sections;    // which kept range each piece came from
    private readonly KeepSection[] ranges;
    internal double Total { get; }
    internal int Version { get; }
    internal IReadOnlyList<ExportPiece> Pieces => pieces;
    private static int versions;

    internal SequenceMap(IReadOnlyList<KeepSection> ranges, ShareExportOptions options)
    {
        this.ranges = ranges.ToArray();
        var list = new List<ExportPiece>(); var from = new List<int>();
        for (int r = 0; r < ranges.Count; r++)
            foreach (var piece in options.Pieces(new[] { ranges[r] })) { list.Add(piece); from.Add(r); }
        pieces = list.ToArray(); sections = from.ToArray(); starts = new double[pieces.Length];
        double at = 0;
        for (int i = 0; i < pieces.Length; i++) { starts[i] = at; at += pieces[i].Seconds; }
        Total = at; Version = ++versions;
    }

    // Where a moment of the recording lands in the finished video. A freeze's own moment lands at the
    // start of its hold; a moment that isn't kept lands where the next kept footage (in play order) starts.
    internal double ToOutput(double source)
    {
        double soonest = double.MaxValue, soonestAt = -1;
        for (int i = 0; i < pieces.Length; i++)
        {
            var p = pieces[i];
            if (p.Freeze) { if (Math.Abs(source - p.Start) < 1e-9) return starts[i]; continue; }
            if (source >= p.Start - 1e-9 && source < p.End) return starts[i] + Math.Max(0, source - p.Start) / p.Speed;
            if (p.Start >= source && p.Start < soonest) { soonest = p.Start; soonestAt = starts[i]; }
        }
        // Past the last kept moment: the end of the piece that finishes latest before it.
        if (soonestAt >= 0) return soonestAt;
        for (int i = pieces.Length - 1; i >= 0; i--) if (!pieces[i].Freeze && Math.Abs(source - pieces[i].End) < 1e-6) return starts[i] + pieces[i].Seconds;
        return Total;
    }

    // Where footage up to a moment ends in the finished video (for the end edge of a part): the same as
    // ToOutput inside a piece, but at the very end of a section it stays with that section.
    internal double EndToOutput(double source)
    {
        for (int i = 0; i < pieces.Length; i++)
        {
            var p = pieces[i];
            if (!p.Freeze && source > p.Start + 1e-9 && source <= p.End + 1e-9) return starts[i] + (Math.Min(p.End, source) - p.Start) / p.Speed;
        }
        return ToOutput(source);
    }
    // What the finished video shows at a moment: the recording's moment, and how far into a freeze's
    // hold it is (0 outside holds).
    internal (double Source, double Hold) ToSource(double output)
    {
        if (pieces.Length == 0) return (0, 0);
        int i = PieceAt(output);
        // Right at the end of a hold it's still the held frame, all of it held (not the hold's start again).
        if (i > 0 && pieces[i - 1].Freeze && Math.Abs(output - starts[i]) < 1e-9) return (pieces[i - 1].Start, pieces[i - 1].Seconds);
        var p = pieces[i]; double into = Math.Clamp(output - starts[i], 0, p.Seconds);
        return p.Freeze ? (p.Start, into) : (Math.Min(p.End, p.Start + into * p.Speed), 0);
    }
    private int PieceAt(double output)
    {
        int lo = 0, hi = pieces.Length - 1;
        while (lo < hi) { int mid = (lo + hi + 1) / 2; if (starts[mid] <= output + 1e-9) lo = mid; else hi = mid - 1; }
        return lo;
    }
    // Which kept range (section, in play order) plays at a moment of the finished video.
    internal int SectionAt(double output) => pieces.Length == 0 ? -1 : sections[PieceAt(output)];
    // Where a kept range starts and ends in the finished video.
    internal (double From, double To) SectionSpan(int section)
    {
        double from = double.MaxValue, to = 0;
        for (int i = 0; i < pieces.Length; i++) if (sections[i] == section) { from = Math.Min(from, starts[i]); to = Math.Max(to, starts[i] + pieces[i].Seconds); }
        return from == double.MaxValue ? (0, 0) : (from, to);
    }
    internal int SectionCount => ranges.Length;
    // The kept range (in play order) a moment of the recording is in, or -1.
    internal int SectionOf(double source)
    {
        for (int r = 0; r < ranges.Length; r++) if (source >= ranges[r].Start - 1e-9 && source <= ranges[r].End + 1e-9) return r;
        return -1;
    }
    internal KeepSection Range(int section) => ranges[section];
    // The recording's moment at a point of the finished video, staying inside one section (so the very end
    // of a section is its last moment, not the start of whatever plays next).
    internal double SourceIn(int section, double output)
    {
        double best = ranges[section].Start, gap = double.MaxValue;
        for (int i = 0; i < pieces.Length; i++)
        {
            if (sections[i] != section) continue;
            var p = pieces[i]; double from = starts[i], to = from + p.Seconds;
            double miss = output < from ? from - output : output > to ? output - to : 0;
            if (miss >= gap) continue;
            gap = miss;
            best = p.Freeze ? p.Start : Math.Min(p.End, p.Start + Math.Clamp(output - from, 0, p.Seconds) * p.Speed);
        }
        return best;
    }

    // The stretches of the finished video a span of the recording covers: split where sections jump, and
    // including the whole hold of any freeze inside it (the held frame belongs to it).
    internal List<(double From, double To)> Spans(double start, double end)
    {
        var spans = new List<(double From, double To)>();
        for (int i = 0; i < pieces.Length; i++)
        {
            var p = pieces[i]; double from, to;
            if (p.Freeze)
            {
                if (p.Start < start - 1e-9 || p.Start >= end - 1e-9) continue;
                from = starts[i]; to = starts[i] + p.Seconds;
            }
            else
            {
                double a = Math.Max(start, p.Start), b = Math.Min(end, p.End);
                if (b - a <= 1e-9) continue;
                from = starts[i] + (a - p.Start) / p.Speed; to = starts[i] + (b - p.Start) / p.Speed;
            }
            if (spans.Count > 0 && Math.Abs(spans[^1].To - from) < 1e-6) spans[^1] = (spans[^1].From, to);
            else spans.Add((from, to));
        }
        return spans;
    }
    // Where a moment that may sit partway into a hold lands (end: an end with no hold time stays with the
    // footage before it).
    internal double ToOutput(Moment moment, bool end)
    {
        if (moment.Hold > 0 && Hold(moment.At) is { } hold) return hold.From + Math.Min(moment.Hold, hold.To - hold.From);
        return end ? EndToOutput(moment.At) : ToOutput(moment.At);
    }
    // The stretches of the finished video a part covers when its start or end can sit partway into a hold:
    // the usual stretches, with the hold before its start (and after its end) trimmed off. A part that lives
    // inside one hold is that part of the hold; a hold outside what's kept gives nothing.
    internal List<(double From, double To)> Spans(Moment from, Moment to)
    {
        if (from.Hold <= 0 && to.Hold <= 0) return Spans(from.At, to.At);
        bool oneHold = Math.Abs(from.At - to.At) < 1e-9;
        if (oneHold && Hold(from.At) == null) return new();
        var spans = Spans(from.At, to.Hold > 0 ? to.At + 1e-7 : to.At);
        if (from.Hold > 0 && Hold(from.At) is { } first)
            for (int i = 0; i < spans.Count; i++)
                if (spans[i].From <= first.From + 1e-9 && spans[i].To >= first.From - 1e-9) { spans[i] = (first.From + Math.Min(from.Hold, first.To - first.From), spans[i].To); break; }
        if (to.Hold > 0 && Hold(to.At) is { } last)
            for (int i = 0; i < spans.Count; i++)
                if (spans[i].From <= last.To + 1e-9 && spans[i].To >= last.From - 1e-9) { spans[i] = (spans[i].From, Math.Min(spans[i].To, last.From + Math.Min(to.Hold, last.To - last.From))); break; }
        spans.RemoveAll(s => s.To - s.From <= 1e-6);
        return spans;
    }
    // A freeze's hold in the finished video, if it's inside what's kept.
    internal (double From, double To)? Hold(double at)
    {
        for (int i = 0; i < pieces.Length; i++) if (pieces[i].Freeze && Math.Abs(pieces[i].Start - at) < 1e-9) return (starts[i], starts[i] + pieces[i].Seconds);
        return null;
    }
    // The speed footage plays at a moment of the finished video (0 during a hold).
    internal double SpeedAt(double output) => pieces.Length == 0 ? 1 : pieces[PieceAt(output)] is { Freeze: true } ? 0 : pieces[PieceAt(output)].Speed;
}
