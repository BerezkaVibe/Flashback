using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;

namespace Flashback;

// Retain capture-time pictures across brief encoder read stalls. The encoder's
// timeline chooses a past picture, never a newer picture for an older timestamp.
internal sealed class FrameHistory : IDisposable
{
    private readonly object gate = new();
    private readonly Queue<Sample> pending = new();
    private readonly int capacity;
    private byte[]? current;
    private long lastLive;
    private bool live;
    internal long Dropped { get; private set; }
    internal int Capacity => capacity;
    private readonly record struct Sample(long Tick, byte[]? Pixels, bool Live);

    internal FrameHistory(int frameBytes, int fps) =>
        capacity = Math.Clamp(Math.Min((fps + 1) / 2, 48 * 1024 * 1024 / frameBytes), 2, 60);

    internal void Publish(byte[]? pixels, long tick, bool available)
    {
        lock (gate)
        {
            if (pending.Count == capacity)
            {
                Return(pending.Dequeue().Pixels);
                Dropped++;
            }
            pending.Enqueue(new(tick, pixels, available));
        }
    }

    // Only the writer calls Select/Dispose. Its returned buffer remains owned
    // here until the next Select, so capture cannot recycle a pipe write's bytes.
    internal byte[]? Select(long tick, out bool changed)
    {
        changed = false;
        lock (gate)
        {
            while (pending.TryPeek(out var sample) && sample.Tick <= tick)
            {
                pending.Dequeue();
                live = sample.Live;
                lastLive = sample.Tick;
                if (!live || sample.Pixels != null)
                {
                    Return(current);
                    current = sample.Pixels;
                    changed = true;
                }
            }
            return live && tick - lastLive < Stopwatch.Frequency * .3 ? current : null;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            Return(current); current = null;
            while (pending.TryDequeue(out var sample)) Return(sample.Pixels);
        }
    }
    private static void Return(byte[]? pixels) { if (pixels != null) ArrayPool<byte>.Shared.Return(pixels); }
}
