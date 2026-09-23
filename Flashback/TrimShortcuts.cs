using System;
using System.Windows.Input;
namespace Flashback;

internal static class TrimShortcuts
{
    internal static string Format(Key key, ModifierKeys modifiers)
    {
        if (key == Key.Add && modifiers == ModifierKeys.None || key == Key.OemPlus && modifiers is ModifierKeys.None or ModifierKeys.Shift) return "+";
        return (modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "") + (modifiers.HasFlag(ModifierKeys.Alt) ? "Alt+" : "") + (modifiers.HasFlag(ModifierKeys.Shift) ? "Shift+" : "") + key;
    }
    internal static bool Matches(string binding, Key key, ModifierKeys modifiers)
    {
        if (binding == "+") return key == Key.Add && modifiers == ModifierKeys.None || key == Key.OemPlus && modifiers is ModifierKeys.None or ModifierKeys.Shift;
        var parsed = Hotkeys.Parse(binding);
        uint mods = 0x4000u | (modifiers.HasFlag(ModifierKeys.Control) ? 2u : 0u) | (modifiers.HasFlag(ModifierKeys.Shift) ? 4u : 0u) | (modifiers.HasFlag(ModifierKeys.Alt) ? 1u : 0u);
        return !modifiers.HasFlag(ModifierKeys.Windows) && parsed.Modifiers == mods && parsed.Key == (uint)KeyInterop.VirtualKeyFromKey(key);
    }
    internal static void Validate(string binding)
    {
        if (binding == "+") return;
        Hotkeys.Parse(binding);
        if (IsReserved(binding)) throw new ArgumentException("That shortcut already controls the trimmer. Choose another add-section shortcut; see the shortcut guide.");
    }
    internal static bool IsReserved(string binding)
    {
        if(binding=="+") return false;
        foreach (var key in new[] { Key.I, Key.O, Key.S, Key.B, Key.Space, Key.K, Key.J, Key.L, Key.M, Key.C, Key.E, Key.G, Key.Z,
            Key.Delete, Key.Back, Key.Home, Key.End, Key.Up, Key.Down, Key.PageUp, Key.PageDown, Key.OemComma, Key.OemPeriod, Key.Tab, Key.Escape })
            if (Matches(binding,key,ModifierKeys.None)) return true;
        foreach (var modifiers in new[] { ModifierKeys.None, ModifierKeys.Shift, ModifierKeys.Control })
            foreach (var key in new[] { Key.Left, Key.Right })
                if (Matches(binding,key,modifiers)) return true;
        foreach(var key in new[]{Key.Z,Key.Y,Key.O,Key.S,Key.Up,Key.Down,Key.Home,Key.End}) if(Matches(binding,key,ModifierKeys.Control)) return true;
        foreach(var key in new[]{Key.Z,Key.Left,Key.Right}) if(Matches(binding,key,ModifierKeys.Control|ModifierKeys.Shift)) return true;
        foreach(var key in new[]{Key.Home,Key.End,Key.J,Key.L,Key.OemQuestion}) if(Matches(binding,key,ModifierKeys.Shift)) return true;
        foreach(var key in new[]{Key.Up,Key.Down}) if(Matches(binding,key,ModifierKeys.Alt)) return true;
        return false;
    }
}

