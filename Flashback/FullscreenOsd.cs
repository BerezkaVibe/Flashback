using System;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Windows.Threading;

namespace Flashback;

// Uses RTSS's published OSD shared-memory protocol. RTSS owns the game renderer/hook.
// Flashback never changes game files, RTSS profiles, other owners' slots or frame limits.
internal sealed class FullscreenOsd : IDisposable
{
    private readonly string mappingName;
    private readonly string owner = "Flashback." + Environment.ProcessId;
    private readonly DispatcherTimer dismiss = new();
    private MemoryMappedFile? mapping;
    private MemoryMappedViewAccessor? view;
    private long slot = -1;
    private uint version, entrySize;
    internal FullscreenOsd(string mappingName = "RTSSSharedMemoryV2") { this.mappingName = mappingName; dismiss.Tick += (_, _) => Dispose(); }
    internal bool Show(SaveFeedback state, int seconds)
    {
        Dispose();
        try
        {
            mapping = MemoryMappedFile.OpenExisting(mappingName, MemoryMappedFileRights.ReadWrite);
            view = mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
            if (view.Capacity < 36 || view.ReadUInt32(0) != 0x52545353) { Dispose(); return false; }
            version = view.ReadUInt32(4);
            if (version < 0x00020000 || version >= 0x00030000) { Dispose(); return false; }
            entrySize = view.ReadUInt32(20); uint start = view.ReadUInt32(24), count = view.ReadUInt32(28);
            if (entrySize < 512 || count > 256 || start < 36 || start + (long)entrySize * count > view.Capacity) { Dispose(); return false; }
            // Slot zero belongs to the server.
            for (uint i = 1; i < count; i++)
            {
                long candidate = start + (long)i * entrySize;
                var name = ReadText(candidate + 256);
                if (name.Length != 0 && name != owner) continue;
                slot = candidate; WriteText(slot + 256, owner, 256); break;
            }
            if (slot < 0) { Dispose(); return false; }
            string text = state switch { SaveFeedback.Failed => "Flashback: clip not saved", SaveFeedback.Saving => "Flashback: saving clip...", _ => "Flashback: clip saved" };
            WriteText(slot, text, 256);
            if (version >= 0x00020007 && entrySize >= 4608) WriteText(slot + 512, text, 4096);
            Refresh();
            if (state != SaveFeedback.Saving) { dismiss.Interval = TimeSpan.FromSeconds(seconds); dismiss.Start(); }
            return true;
        }
        catch { Dispose(); return false; }
    }
    private string ReadText(long offset) { byte[] bytes = new byte[256]; view!.ReadArray(offset, bytes, 0, bytes.Length); return Encoding.ASCII.GetString(bytes).Split('\0')[0]; }
    private void WriteText(long offset, string text, int size) { byte[] bytes = new byte[size]; Encoding.ASCII.GetBytes(text.AsSpan(0, Math.Min(text.Length, size - 1)), bytes); view!.WriteArray(offset, bytes, 0, bytes.Length); }
    private void Refresh() => view!.Write(32, unchecked(view.ReadUInt32(32) + 1));
    public void Dispose()
    {
        dismiss.Stop();
        try
        {
            if (view != null && slot >= 0 && view.ReadUInt32(0) == 0x52545353 && ReadText(slot + 256) == owner)
            {
                WriteText(slot, "", 256);
                if (version >= 0x00020007 && entrySize >= 4608) WriteText(slot + 512, "", 4096);
                WriteText(slot + 256, "", 256); Refresh();
            }
        }
        catch { }
        view?.Dispose(); mapping?.Dispose(); view = null; mapping = null; slot = -1;
    }
}

