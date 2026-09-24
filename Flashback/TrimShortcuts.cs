using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
namespace Flashback;

internal enum TrimAction
{
    PlayPause, PlayPauseKeepSpeed, Slower, Faster, MuchSlower, MuchFaster, Mute, VolumeUp, VolumeDown,
    PreviousFrame, NextFrame, FrameBack, FrameForward, HalfSecondBack, HalfSecondForward, SecondBack, SecondForward, TenSecondsBack, TenSecondsForward,
    VideoStart, VideoEnd, MarkedStart, MarkedEnd, GoToTime,
    MarkStart, MarkEnd, AddSection, Split, RemoveSection, PreviousSection, NextSection, FirstSection, LastSection, Undo, Redo,
    ZoomIn, ZoomOut, ZoomToggle, Snapshot, Export, OpenVideo, SaveProject, ShortcutGuide, CutTool, SpeedTool, ZoomTool, TextTool, ImageTool
}

// Default is a comma-separated list; a user binding replaces it with a single shortcut.
internal sealed record TrimShortcut(TrimAction Action, string Group, string Label, string Default, bool Repeats = false, bool TimelineOnly = false);

internal static class TrimShortcuts
{
    internal static readonly TrimShortcut[] All =
    {
        new(TrimAction.PlayPause,"Playback","Play / pause","Space"),
        new(TrimAction.PlayPauseKeepSpeed,"Playback","Play / pause (second key)","K"),
        new(TrimAction.Slower,"Playback","Slower preview","J",true),
        new(TrimAction.Faster,"Playback","Faster preview","L",true),
        new(TrimAction.MuchSlower,"Playback","Slower preview, two steps","Shift+J",true),
        new(TrimAction.MuchFaster,"Playback","Faster preview, two steps","Shift+L",true),
        new(TrimAction.Mute,"Playback","Mute preview","M"),
        new(TrimAction.VolumeUp,"Playback","Preview volume up","Alt+Up",true),
        new(TrimAction.VolumeDown,"Playback","Preview volume down","Alt+Down",true),
        new(TrimAction.PreviousFrame,"Navigation","Previous frame","OemComma",true),
        new(TrimAction.NextFrame,"Navigation","Next frame","OemPeriod",true),
        new(TrimAction.FrameBack,"Navigation","Frame back (timeline focused)","Left",true,true),
        new(TrimAction.FrameForward,"Navigation","Frame forward (timeline focused)","Right",true,true),
        new(TrimAction.HalfSecondBack,"Navigation","Half second back (timeline focused)","Shift+Left",true,true),
        new(TrimAction.HalfSecondForward,"Navigation","Half second forward (timeline focused)","Shift+Right",true,true),
        new(TrimAction.SecondBack,"Navigation","One second back (timeline focused)","Ctrl+Left",true,true),
        new(TrimAction.SecondForward,"Navigation","One second forward (timeline focused)","Ctrl+Right",true,true),
        new(TrimAction.TenSecondsBack,"Navigation","Back 10 seconds","Ctrl+Shift+Left",true),
        new(TrimAction.TenSecondsForward,"Navigation","Forward 10 seconds","Ctrl+Shift+Right",true),
        new(TrimAction.VideoStart,"Navigation","Video start","Ctrl+Home"),
        new(TrimAction.VideoEnd,"Navigation","Video end","Ctrl+End"),
        new(TrimAction.MarkedStart,"Navigation","Jump to marked start","Shift+Home"),
        new(TrimAction.MarkedEnd,"Navigation","Jump to marked end","Shift+End"),
        new(TrimAction.GoToTime,"Navigation","Go to time","G"),
        new(TrimAction.MarkStart,"Editing","Set start at playhead","I"),
        new(TrimAction.MarkEnd,"Editing","Set end at playhead","O"),
        new(TrimAction.AddSection,"Editing","Add marked section","+"),
        new(TrimAction.Split,"Editing","Split section","S, B"),
        new(TrimAction.RemoveSection,"Editing","Remove selected section","Delete, Back"),
        new(TrimAction.PreviousSection,"Editing","Previous section","Up",true),
        new(TrimAction.NextSection,"Editing","Next section","Down",true),
        new(TrimAction.FirstSection,"Editing","First section","PageUp"),
        new(TrimAction.LastSection,"Editing","Last section","PageDown"),
        new(TrimAction.Undo,"Editing","Undo","Ctrl+Z"),
        new(TrimAction.Redo,"Editing","Redo","Ctrl+Y, Ctrl+Shift+Z"),
        new(TrimAction.CutTool,"Editing","Cut out tool (Esc leaves)","X"),
        new(TrimAction.SpeedTool,"Editing","Speed tool (Esc leaves)","R"),
        new(TrimAction.ZoomTool,"Editing","Zoom tool (Esc leaves)","Q"),
        new(TrimAction.TextTool,"Editing","Text tool (Esc leaves)","T"),
        new(TrimAction.ImageTool,"Editing","Picture tool (Esc leaves)","P"),
        new(TrimAction.ZoomIn,"View and file","Zoom in","Ctrl+Up"),
        new(TrimAction.ZoomOut,"View and file","Zoom out","Ctrl+Down"),
        new(TrimAction.ZoomToggle,"View and file","Toggle zoom / fit","Z"),
        new(TrimAction.Snapshot,"View and file","Save snapshot","C"),
        new(TrimAction.Export,"View and file","Export sections","E"),
        new(TrimAction.OpenVideo,"View and file","Open video","Ctrl+O"),
        new(TrimAction.SaveProject,"View and file","Save trim project","Ctrl+S"),
        new(TrimAction.ShortcutGuide,"View and file","Shortcut guide","Shift+OemQuestion"),
    };
    internal static TrimShortcut Info(TrimAction action) => All.First(s => s.Action == action);

    // Settings keep only the bindings a user changed; add-section keeps its original setting.
    internal static Dictionary<TrimAction, string> Resolve(Settings settings)
    {
        var map = All.ToDictionary(s => s.Action, s => s.Default);
        foreach (var (name, binding) in settings.TrimKeys)
            if (Enum.TryParse<TrimAction>(name, out var action) && action != TrimAction.AddSection && !string.IsNullOrWhiteSpace(binding)) map[action] = binding.Trim();
        map[TrimAction.AddSection] = settings.TrimAddHotkey;
        return map;
    }
    internal static TrimAction? Find(IReadOnlyDictionary<TrimAction, string> map, Key key, ModifierKeys modifiers, bool timelineFocused)
    {
        foreach (var shortcut in All)
            if ((timelineFocused || !shortcut.TimelineOnly) && map.TryGetValue(shortcut.Action, out var binding) && Matches(binding, key, modifiers)) return shortcut.Action;
        return null;
    }
    private static IEnumerable<string> Alternatives(string binding) => binding.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    internal static string Format(Key key, ModifierKeys modifiers)
    {
        if (key == Key.Add && modifiers == ModifierKeys.None || key == Key.OemPlus && modifiers is ModifierKeys.None or ModifierKeys.Shift) return "+";
        return (modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "") + (modifiers.HasFlag(ModifierKeys.Alt) ? "Alt+" : "") + (modifiers.HasFlag(ModifierKeys.Shift) ? "Shift+" : "") + key;
    }
    internal static bool Matches(string binding, Key key, ModifierKeys modifiers)
    {
        if (modifiers.HasFlag(ModifierKeys.Windows)) return false;
        uint mods = 0x4000u | (modifiers.HasFlag(ModifierKeys.Control) ? 2u : 0u) | (modifiers.HasFlag(ModifierKeys.Shift) ? 4u : 0u) | (modifiers.HasFlag(ModifierKeys.Alt) ? 1u : 0u);
        var pressed = (mods, (uint)KeyInterop.VirtualKeyFromKey(key));
        return Alternatives(binding).Any(b => Keys(b).Contains(pressed));
    }
    // "+" covers both plus keys, with or without Shift on the main keyboard.
    private static IEnumerable<(uint Modifiers, uint Key)> Keys(string single)
    {
        if (single == "+")
        {
            uint add = (uint)KeyInterop.VirtualKeyFromKey(Key.Add), plus = (uint)KeyInterop.VirtualKeyFromKey(Key.OemPlus);
            return new[] { (0x4000u, add), (0x4000u, plus), (0x4004u, plus) };
        }
        return new[] { Hotkeys.Parse(single) };
    }
    internal static void Validate(Settings settings)
    {
        var map = Resolve(settings);
        var global = new[] { (Name: "Save replay", Key: Hotkeys.Parse(settings.Hotkey)), (Name: "Start / pause recording", Key: Hotkeys.Parse(settings.PauseHotkey)) };
        var used = new Dictionary<(uint, uint), TrimAction>();
        foreach (var shortcut in All)
        foreach (var single in Alternatives(map[shortcut.Action]))
        foreach (var key in Keys(single))
        {
            if (used.TryGetValue(key, out var other) && other != shortcut.Action)
                throw new ArgumentException($"{Display(single)} is set for both “{Info(other).Label}” and “{shortcut.Label}”. Choose a different key for one of them.");
            used[key] = shortcut.Action;
            foreach (var g in global)
                if (g.Key == key) throw new ArgumentException($"{Display(single)} is already the {g.Name} hotkey. Choose a different key for “{shortcut.Label}”.");
        }
    }
    internal static string Display(string binding) => string.Join(" / ", Alternatives(binding).Select(single => single == "+" ? "+" :
        string.Join("+", single.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(KeyName))));
    private static string KeyName(string token) => token.ToLowerInvariant() switch
    {
        "oemcomma" => ",", "oemperiod" => ".", "oemquestion" => "?", "oemplus" => "=", "oemminus" => "-", "add" => "Num +", "subtract" => "Num -",
        "back" => "Backspace", "prior" or "pageup" => "Page Up", "next" or "pagedown" => "Page Down", "return" or "enter" => "Enter",
        "up" => "↑", "down" => "↓", "left" => "←", "right" => "→", "escape" => "Esc",
        _ when token.Length == 2 && token[0] is 'D' or 'd' && char.IsDigit(token[1]) => token[1..],
        _ => token
    };
}
