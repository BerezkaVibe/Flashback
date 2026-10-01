using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Flashback;

// The replay buffer held in memory. The recorder's encoder writes one continuous MPEG-TS stream into a named
// pipe, and this reads it, cutting it into chunks at each keyframe (about every two seconds), each chunk a
// piece of stream that begins with the tables a player needs and a keyframe. That is what the segment files of
// the disk buffer are, so the rest of the recorder treats a chunk like a segment: it is listed with its start
// and end time, the old ones are let go, and saving writes the wanted ones out and joins them into a clip.
//
// Chunks live in blocks taken from a pool and handed back when a chunk is let go, so a buffer that runs for
// hours holds a steady amount of memory and never makes the garbage collector chase large arrays.
internal sealed class MemoryBuffer : IDisposable
{
    internal const int BlockSize = 256 * 1024;
    private const int Packet = 188;

    private sealed class Chunk
    {
        public string Name = ""; public int Epoch; public int Index; public double Start, End; public bool Done;
        public readonly List<byte[]> Blocks = new(); public long Length; public long VideoPts = -1;
        public void Append(ReadOnlySpan<byte> data, ConcurrentBag<byte[]> pool)
        {
            while (data.Length > 0)
            {
                int used = (int)(Length % BlockSize);
                if (used == 0 && Blocks.Count * (long)BlockSize == Length) Blocks.Add(pool.TryTake(out var block) ? block : new byte[BlockSize]);
                int take = Math.Min(BlockSize - used, data.Length);
                data[..take].CopyTo(Blocks[^1].AsSpan(used));
                data = data[take..]; Length += take;
            }
        }
    }

    private readonly object gate = new();
    private readonly ConcurrentBag<byte[]> pool = new();
    private readonly List<Chunk> chunks = new();   // finished chunks, oldest first, every epoch
    private long totalBytes;
    private NamedPipeServerStream? server;
    private Task? reader;
    private int epoch = -1;
    private double frameSeconds = 1.0 / 60;
    // Memory the buffer holds, in bytes (finished chunks and the one being filled).
    internal long Bytes { get { lock (gate) return totalBytes + (open?.Length ?? 0); } }
    private Chunk? open;
    internal Exception? Failure { get; private set; }

    // How much memory a buffer of the given estimated size may use: a fifth of the PC's memory, at most 4 GB.
    internal static double LimitMb
    {
        get
        {
            double total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1048576.0;
            return total <= 0 ? 1024 : Math.Min(4096, total * 0.2);
        }
    }

    // Starts a stream: a pipe the encoder is to write to, listening before it is told where to find it.
    internal string StartEpoch(double frameRate)
    {
        lock (gate) { epoch++; open = null; }
        frameSeconds = 1.0 / Math.Max(1, frameRate);
        string name = "flashback-buffer-" + Guid.NewGuid().ToString("N");
        var created = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 1 << 20, 1 << 20);
        server = created;
        int mine = epoch;
        reader = Task.Run(() => ReadAsync(created, mine));
        return @"\\.\pipe\" + name;
    }

    // Waits for the stream to end (the encoder stopped, or the pipe broke) and its last chunk to be finished.
    internal async Task EndAsync(TimeSpan wait)
    {
        var task = reader;
        if (task != null) { try { await task.WaitAsync(wait).ConfigureAwait(false); } catch { } }
        try { server?.Dispose(); } catch { }
        server = null;
    }

    internal List<Segment> Segments(int ofEpoch = -1)
    {
        lock (gate) return chunks.Where(c => c.Done && (ofEpoch < 0 || c.Epoch == ofEpoch)).Select(c => new Segment(c.Name, c.Start, c.End)).ToList();
    }
    internal int CurrentEpoch => epoch;
    internal bool Has(string name) { lock (gate) return chunks.Any(c => c.Name == name); }
    internal long SizeOf(string name) { lock (gate) return chunks.FirstOrDefault(c => c.Name == name)?.Length ?? 0; }

    // Lets go of the finished chunks that aren't wanted (never the newest finished one), handing their blocks back.
    internal void Keep(HashSet<string> wanted, string newest)
    {
        lock (gate)
        {
            for (int i = chunks.Count - 1; i >= 0; i--)
            {
                var c = chunks[i];
                if (!c.Done || wanted.Contains(c.Name) || c.Name == newest || string.CompareOrdinal(c.Name, newest) >= 0) continue;
                Release(c); chunks.RemoveAt(i);
            }
        }
    }
    internal void Clear()
    {
        lock (gate)
        {
            foreach (var c in chunks) Release(c);
            chunks.Clear(); if (open != null) { Release(open); open = null; }
            totalBytes = 0; epoch = -1;
        }
    }
    private void Release(Chunk c) { totalBytes -= c.Length; foreach (var block in c.Blocks) pool.Add(block); c.Blocks.Clear(); c.Length = 0; }

    // Writes the named chunks, which must be consecutive, one after another: a continuous stream.
    internal async Task WriteAsync(IEnumerable<string> names, Stream destination)
    {
        // The caller holds the recorder's file lock, so no chunk is let go (and its blocks refilled) while this writes.
        var snapshot = new List<(byte[] Block, int Length)>();
        lock (gate)
        {
            foreach (var name in names)
            {
                var c = chunks.FirstOrDefault(x => x.Name == name) ?? throw new IOException("A part of the buffer was already let go.");
                long left = c.Length;
                foreach (var block in c.Blocks) { int n = (int)Math.Min(BlockSize, left); snapshot.Add((block, n)); left -= n; }
            }
        }
        foreach (var (block, length) in snapshot) await destination.WriteAsync(block.AsMemory(0, length)).ConfigureAwait(false);
    }

    // ---- Reading the stream ----
    // What reading one stream remembers between reads.
    private sealed class StreamState
    {
        public int Epoch, VideoPid = -1, PmtPid = -1, Index;
        public long BasePts = -1;
        public readonly List<byte[]> Tables = new();   // PAT and PMT packets waiting to see which chunk they belong to
        public byte[]? LastPat, LastPmt;
    }

    private async Task ReadAsync(NamedPipeServerStream pipe, int ofEpoch)
    {
        var buffer = new byte[1 << 20]; int have = 0;
        var state = new StreamState { Epoch = ofEpoch };
        try
        {
            await pipe.WaitForConnectionAsync().ConfigureAwait(false);
            int read;
            while ((read = await pipe.ReadAsync(buffer.AsMemory(have, buffer.Length - have)).ConfigureAwait(false)) > 0)
            {
                have += read;
                int used = Feed(buffer, have, state);
                // Keep the partial packet at the end for the next read.
                have -= used; if (have > 0) Buffer.BlockCopy(buffer, used, buffer, 0, have);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { if (ex is IOException) Failure = ex; }
        finally
        {
            lock (gate)
            {
                // The stream ended: the chunk being filled is as long as the video it holds.
                if (open != null && state.BasePts >= 0) FinishOpen(((open.VideoPts - state.BasePts) / 90000.0) + frameSeconds);
                else if (open != null) { Release(open); open = null; }
            }
            try { pipe.Dispose(); } catch { }
        }
    }

    // Goes through the whole packets in the buffer, filing each into the chunk being filled and starting a new
    // chunk at every keyframe. Returns how many bytes it used.
    private int Feed(byte[] buffer, int have, StreamState st)
    {
        int pos = 0;
        while (have - pos >= Packet)
        {
            if (buffer[pos] != 0x47) { pos++; continue; }            // out of step: find the next packet
            var packet = buffer.AsSpan(pos, Packet);
            int pid = ((packet[1] & 0x1F) << 8) | packet[2];
            bool start = (packet[1] & 0x40) != 0; int afc = (packet[3] >> 4) & 3;
            if (pid == 0 || pid == st.PmtPid)
            {
                // Tables go with the keyframe that follows them.
                var copy = packet.ToArray(); st.Tables.Add(copy);
                if (pid == 0) { st.LastPat = copy; st.PmtPid = ParsePat(packet, st.PmtPid); }
                else { st.LastPmt = copy; int found = ParsePmt(packet); if (found >= 0) st.VideoPid = found; }
                pos += Packet; continue;
            }
            bool key = pid == st.VideoPid && start && (afc & 2) != 0 && packet[4] > 0 && (packet[5] & 0x40) != 0;
            long pts = pid == st.VideoPid && start ? ParsePts(packet, afc) : -1;
            lock (gate)
            {
                if (key && pts >= 0)
                {
                    if (st.BasePts < 0) st.BasePts = pts;
                    double at = (pts - st.BasePts) / 90000.0;
                    FinishOpen(at);
                    open = new Chunk { Name = $"mem-{st.Epoch:D3}-{st.Index++:D9}", Epoch = st.Epoch, Start = at, VideoPts = pts };
                    if (st.Tables.Count == 0) { if (st.LastPat != null) open.Append(st.LastPat, pool); if (st.LastPmt != null) open.Append(st.LastPmt, pool); }
                }
                if (open != null)
                {
                    foreach (var t in st.Tables) open.Append(t, pool);
                    open.Append(packet, pool);
                    if (pts >= 0 && st.BasePts >= 0) open.VideoPts = Math.Max(open.VideoPts, pts);
                }
            }
            st.Tables.Clear();
            pos += Packet;
        }
        return pos;
    }
    // Called with the lock held: the chunk being filled ends at the given time and becomes a finished one.
    private void FinishOpen(double end)
    {
        if (open == null) return;
        open.End = Math.Max(open.Start, end); open.Done = true;
        totalBytes += open.Length; chunks.Add(open); open = null;
    }

    // PAT: the program's PMT PID (the first program that isn't the network table).
    private static int ParsePat(ReadOnlySpan<byte> p, int current)
    {
        int at = 4 + (((p[3] >> 4) & 2) != 0 ? 1 + p[4] : 0);
        if (at + 13 >= Packet) return current;
        at += 1 + p[at];                                  // pointer field
        if (p[at] != 0) return current;
        int length = ((p[at + 1] & 0x0F) << 8) | p[at + 2];
        for (int i = at + 8; i + 4 <= at + 3 + length - 4 && i + 4 <= Packet; i += 4)
        {
            int program = (p[i] << 8) | p[i + 1];
            if (program != 0) return ((p[i + 2] & 0x1F) << 8) | p[i + 3];
        }
        return current;
    }
    // PMT: the PID of the first video stream.
    private static int ParsePmt(ReadOnlySpan<byte> p)
    {
        int at = 4 + (((p[3] >> 4) & 2) != 0 ? 1 + p[4] : 0);
        if (at + 14 >= Packet) return -1;
        at += 1 + p[at];
        if (p[at] != 2) return -1;
        int length = ((p[at + 1] & 0x0F) << 8) | p[at + 2];
        int info = ((p[at + 10] & 0x0F) << 8) | p[at + 11];
        int i = at + 12 + info, end = Math.Min(Packet, at + 3 + length - 4);
        while (i + 5 <= end)
        {
            int type = p[i], pid = ((p[i + 1] & 0x1F) << 8) | p[i + 2], es = ((p[i + 3] & 0x0F) << 8) | p[i + 4];
            if (type is 0x1B or 0x24 or 0x01 or 0x02 or 0x10 or 0xD1) return pid;
            i += 5 + es;
        }
        return -1;
    }
    // The presentation time (90 kHz) of the picture starting in this packet, or -1.
    private static long ParsePts(ReadOnlySpan<byte> p, int afc)
    {
        int at = 4 + ((afc & 2) != 0 ? 1 + p[4] : 0);
        if (at + 14 > Packet || p[at] != 0 || p[at + 1] != 0 || p[at + 2] != 1 || (p[at + 3] & 0xF0) != 0xE0 || (p[at + 7] & 0x80) == 0) return -1;
        return ((long)((p[at + 9] >> 1) & 7) << 30) | ((long)p[at + 10] << 22) | ((long)(p[at + 11] >> 1) << 15) | ((long)p[at + 12] << 7) | (long)(p[at + 13] >> 1);
    }

    public void Dispose()
    {
        try { server?.Dispose(); } catch { }
        Clear();
    }
}
