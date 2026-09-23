using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Flashback;

// Only used when another app owns one of our shortcuts. Raw input delivers key
// transitions in the background without consuming them or polling the keyboard.
// Keep current key-down state only; no text, history, or key logging.
internal sealed class SharedKeyboard : IDisposable
{
    private readonly bool[] held = new bool[256];
    private readonly (uint Modifiers, uint Key)? save, pause;
    private readonly Action saveAction, pauseAction;
    private bool disposed;
    internal SharedKeyboard(IntPtr window, (uint Modifiers, uint Key)? save, (uint Modifiers, uint Key)? pause, Action saveAction, Action pauseAction)
    {
        this.save = save; this.pause = pause; this.saveAction = saveAction; this.pauseAction = pauseAction;
        var device = new Device { UsagePage = 1, Usage = 6, Flags = 0x100 | 0x2000, Target = window };
        if (!RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf<Device>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enable shared background shortcuts.");
        for (int key = 0; key < held.Length; key++) held[key] = (GetAsyncKeyState(key) & 0x8000) != 0;
    }
    internal void Read(IntPtr handle)
    {
        uint size = (uint)Marshal.SizeOf<Packet>();
        uint read = GetRawInputData(handle, 0x10000003, out var packet, ref size, (uint)Marshal.SizeOf<Header>());
        if (read == uint.MaxValue || read < Marshal.SizeOf<Packet>() || packet.Header.Type != 1 || packet.Key >= 255 || packet.Scan == 255) return;
        uint key = packet.Key;
        if (key == 0x10) key = packet.Scan == 0x36 ? 0xA1u : 0xA0u;
        else if (key == 0x11) key = (packet.Flags & 2) != 0 ? 0xA3u : 0xA2u;
        else if (key == 0x12) key = (packet.Flags & 2) != 0 ? 0xA5u : 0xA4u;
        HandleKey(key, (packet.Flags & 1) == 0);
    }
    internal void HandleKey(uint key, bool down)
    {
        if (disposed || key == 0 || key >= held.Length) return;
        bool repeat = held[key]; held[key] = down;
        if (!down || repeat) return;
        uint mods = (held[0xA0] || held[0xA1] ? 4u : 0) | (held[0xA2] || held[0xA3] ? 2u : 0)
            | (held[0xA4] || held[0xA5] ? 1u : 0) | (held[0x5B] || held[0x5C] ? 8u : 0);
        if (save is { } s && s.Key == key && (s.Modifiers & 15) == mods) saveAction();
        else if (pause is { } p && p.Key == key && (p.Modifiers & 15) == mods) pauseAction();
    }
    internal void Reset() => Array.Clear(held);
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        RegisterRawInputDevices(new[] { new Device { UsagePage = 1, Usage = 6, Flags = 1 } }, 1, (uint)Marshal.SizeOf<Device>());
        Reset();
    }
    [StructLayout(LayoutKind.Sequential)] private struct Device { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)] private struct Header { public uint Type, Size; public IntPtr Device, WParam; }
    [StructLayout(LayoutKind.Sequential)] private struct Packet { public Header Header; public ushort Scan, Flags, Reserved, Key; public uint Message, Extra; }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterRawInputDevices(Device[] devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetRawInputData(IntPtr input, uint command, out Packet data, ref uint size, uint headerSize);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
