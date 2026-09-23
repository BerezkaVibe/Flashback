using System;
using System.Collections.Concurrent;

namespace Flashback;

// Exact-size frame buffers with a hard cap on what is kept for reuse. The shared
// ArrayPool rounds a 3 MB 1080p frame up to 4 MB and keeps spare buffers per core
// and per thread, which grew to hundreds of megabytes during long sessions.
internal sealed class FramePool(int frameBytes, int maxRetained)
{
    private readonly ConcurrentBag<byte[]> free = new();
    internal int FrameBytes => frameBytes;
    internal byte[] Rent() => free.TryTake(out var buffer) ? buffer : GC.AllocateUninitializedArray<byte>(frameBytes);
    internal void Return(byte[]? buffer)
    {
        if (buffer == null || buffer.Length != frameBytes || free.Count >= maxRetained) return;
        free.Add(buffer);
    }
    internal void Clear() => free.Clear();
}
