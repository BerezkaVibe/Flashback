using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace Flashback;

// Installed only while a shortcut field in our foreground window is being edited.
// Captures function keys before a global hotkey or window command can consume them.
// Never records key history or stays installed during background recording.
internal sealed class ShortcutKeyCapture : IDisposable
{
    private readonly HookProc callback;
    private readonly Func<bool> active;
    private readonly Action<Key, ModifierKeys, bool> captured;
    private readonly HashSet<int> modifiers = new();
    private IntPtr hook;
    private uint pendingKey;
    private ModifierKeys pendingModifiers;
    internal int FunctionEvents { get; private set; }
    internal ShortcutKeyCapture(Func<bool> active, Action<Key, ModifierKeys, bool> captured)
    {
        this.active = active; this.captured = captured; callback = OnKey;
        foreach (int key in new[] { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C })
            if ((GetAsyncKeyState(key) & 0x8000) != 0) modifiers.Add(key);
        hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not start function-key entry.");
    }
    private IntPtr OnKey(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && (uint)Marshal.ReadInt32(data) is >= 0x70 and <= 0x87) FunctionEvents++;
        if (code < 0 || !active()) return CallNextHookEx(hook, code, message, data);
        int msg = message.ToInt32();
        bool down = msg is 0x100 or 0x104, up = msg is 0x101 or 0x105;
        if (!down && !up) return CallNextHookEx(hook, code, message, data);
        return HandleKey((uint)Marshal.ReadInt32(data), down) ? new IntPtr(1) : CallNextHookEx(hook, code, message, data);
    }
    internal bool HandleKey(uint key, bool down)
    {
        if (hook == IntPtr.Zero || !active()) return false;
        if (ModifierFor((int)key) != ModifierKeys.None)
        {
            if (down) modifiers.Add((int)key); else modifiers.Remove((int)key);
            return false;
        }
        var mods = modifiers.Aggregate(ModifierKeys.None, (value, vk) => value | ModifierFor(vk));
        if (key is < 0x70 or > 0x87 || mods.HasFlag(ModifierKeys.Windows)) return false;
        if (down && pendingKey == 0)
        {
            pendingKey = key; pendingModifiers = mods;
            captured(KeyInterop.KeyFromVirtualKey((int)key), mods, false);
        }
        else if (!down && pendingKey == key)
        {
            pendingKey = 0;
            captured(KeyInterop.KeyFromVirtualKey((int)key), pendingModifiers, true);
        }
        return true;
    }
    private static ModifierKeys ModifierFor(int key) => key switch
    {
        0x10 or 0xA0 or 0xA1 => ModifierKeys.Shift,
        0x11 or 0xA2 or 0xA3 => ModifierKeys.Control,
        0x12 or 0xA4 or 0xA5 => ModifierKeys.Alt,
        0x5B or 0x5C => ModifierKeys.Windows,
        _ => ModifierKeys.None
    };
    public void Dispose() { if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; } }
    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int type, HookProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
