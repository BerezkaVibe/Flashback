using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;

namespace Flashback;
internal static class FrameHistoryDiagnostics
{
    internal static void Run()
    {
        Directory.CreateDirectory(Storage.Root);
        void Check(bool ok, string message)
        {
            if (!ok) throw new Exception(message);
            File.AppendAllText(Path.Combine(Storage.Root, "frame-history-results.txt"), "PASS " + message + "\n");
        }
        long Tick(int frame) => (long)(frame * (double)Stopwatch.Frequency / 60);
        byte[] Picture(int id) { var data = ArrayPool<byte>.Shared.Rent(16); data[0] = (byte)id; return data; }
        using (var history = new FrameHistory(new FramePool(1920 * 1080 * 3 / 2, 64), 60))
        {
            Check(history.Capacity == 16, "1080p history is bounded to sixteen pictures");
            for (int i = 0; i < 12; i++) history.Publish(Picture(i), Tick(i), true);
            for (int i = 0; i < 12; i++)
                Check(history.Select(Tick(i), out var changed)?[0] == i && changed, "Encoder catches up after 180 ms stall without replacing frame " + i);
            history.Publish(Picture(20), Tick(20), true);
            Check(history.Select(Tick(15), out _)![0] == 11, "Future captured picture cannot move backward into an earlier video timestamp");
            history.Publish(null, Tick(21), false);
            history.Publish(Picture(22), Tick(22), true);
            Check(history.Select(Tick(21), out _) == null, "Queued display outage produces black at its original timestamp");
            Check(history.Select(Tick(22), out _)![0] == 22, "Queued recovery resumes the picture at its original timestamp");
            history.Publish(null, Tick(35), true);
            Check(history.Select(Tick(35), out _)![0] == 22, "Static desktop heartbeat preserves its last image");
            Check(history.Select(Tick(55), out _) == null, "Missing capture heartbeat produces black after timeout");
        }
        using (var history = new FrameHistory(new FramePool(1920 * 1080 * 3 / 2, 64), 60))
        {
            for (int i = 0; i < 40; i++) history.Publish(Picture(i), Tick(i), true);
            Check(history.Dropped == 24, "Long stalls drop bounded history without blocking capture or growing memory");
            Check(history.Select(Tick(0), out _) == null, "Overflow cannot replace lost past frames with future motion");
            Check(history.Select(Tick(39), out _)![0] == 39, "History catches up after overload");
        }
    }
}
