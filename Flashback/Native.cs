using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;

namespace Flashback;

public sealed class Hotkeys : IDisposable
{
    private readonly HwndSource source;
    private const int Id = 0x0FB1;
    private const int PauseId = 0x0FB2;
    private (uint Modifiers, uint Key)? registered;
    private (uint Modifiers, uint Key)? pauseRegistered;
    private bool suspended;
    private readonly bool allowShared;
    private SharedKeyboard? shared;
    public bool UsesSharedInput => shared != null;
    internal void FeedSharedForTest(uint key, bool down) => shared?.HandleKey(key, down);
    public event Action? Pressed;
    public event Action? PausePressed;
    public Hotkeys(bool allowShared = true)
    {
        this.allowShared = allowShared;
        source = new HwndSource(new HwndSourceParameters("Flashback hotkey") { ParentWindow = new IntPtr(-3) });
        source.AddHook(Hook);
    }
    public static (uint Modifiers, uint Key) Parse(string text)
    {
        uint modifiers = 0x4000;
        uint key = 0;
        foreach (var token in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (token.ToLowerInvariant())
            {
                case "ctrl": modifiers |= 2; break;
                case "shift": modifiers |= 4; break;
                case "alt": modifiers |= 1; break;
                default:
                    if (key != 0 || !Enum.TryParse<Key>(token, true, out var k) || !Enum.IsDefined(k) || k is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System)
                        throw new ArgumentException("Choose one key, such as F8, optionally combined with Ctrl, Alt or Shift.");
                    key = (uint)KeyInterop.VirtualKeyFromKey(k); break;
            }
        }
        if (key == 0) throw new ArgumentException("Choose a key, such as F8, optionally combined with Ctrl, Alt or Shift.");
        return (modifiers, key);
    }
    public void Register(string text, string? pauseText = null)
    {
        var next = Parse(text);
        var pause = pauseText == null ? ((uint Modifiers, uint Key)?)null : Parse(pauseText);
        if (pause == next) throw new ArgumentException("Save and pause need different hotkeys.");
        if (next == registered && pause == pauseRegistered && !suspended) return;
        bool wasSuspended = suspended;
        Release();
        try { Install(next, pause); }
        catch
        {
            Release();
            if (!wasSuspended && registered is { } previous) Install(previous, pauseRegistered);
            throw;
        }
        registered = next; pauseRegistered = pause; suspended = false;
    }
    private void Install((uint Modifiers, uint Key) save, (uint Modifiers, uint Key)? pause)
    {
        bool sharedSave = !RegisterOne(Id, save);
        bool sharedPause = pause is { } p && !RegisterOne(PauseId, p);
        if (sharedSave || sharedPause)
            shared = new SharedKeyboard(source.Handle, sharedSave ? save : null, sharedPause ? pause : null,
                () => Pressed?.Invoke(), () => PausePressed?.Invoke());
    }
    private bool RegisterOne(int id, (uint Modifiers, uint Key) shortcut)
    {
        if (RegisterHotKey(source.Handle, id, shortcut.Modifiers, shortcut.Key)) return true;
        int error = Marshal.GetLastWin32Error();
        if (allowShared && error == 1409) return false;
        throw new InvalidOperationException(error == 1409
            ? "Windows reports that this shortcut is already registered by another app."
            : $"Windows could not register the shortcut (error {error}).", new System.ComponentModel.Win32Exception(error));
    }
    private void Release()
    {
        shared?.Dispose(); shared = null;
        UnregisterHotKey(source.Handle, Id); UnregisterHotKey(source.Handle, PauseId);
    }
    public void Suspend() { Release(); suspended = true; }
    public void Resume()
    {
        if (!suspended) return;
        try { if (registered is { } save) Install(save, pauseRegistered); suspended = false; }
        catch { Release(); throw; }
    }
    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == 0x00FF) shared?.Read(l);
        if (msg == 0x00FE) shared?.Reset();
        if (!suspended && msg == 0x0312 && w.ToInt32() == Id) { handled = true; Pressed?.Invoke(); }
        if (!suspended && msg == 0x0312 && w.ToInt32() == PauseId) { handled = true; PausePressed?.Invoke(); }
        return IntPtr.Zero;
    }
    public void Dispose() { Release(); source.Dispose(); }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr h, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr h, int id);
}

public static class GameTracker
{
    private static readonly HashSet<string> DesktopProcesses = new(StringComparer.OrdinalIgnoreCase)
        { "explorer", "dwm", "ApplicationFrameHost", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "LockApp" };
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RainbowSix"] = "Rainbow Six Siege", ["RainbowSix_Vulkan"] = "Rainbow Six Siege", ["RainbowSix_DX11"] = "Rainbow Six Siege",
        ["cs2"] = "Counter-Strike 2", ["VALORANT-Win64-Shipping"] = "VALORANT", ["r5apex"] = "Apex Legends",
        ["FortniteClient-Win64-Shipping"] = "Fortnite", ["RocketLeague"] = "Rocket League", ["cod"] = "Call of Duty"
    };
    public static string LastForeground { get; private set; } = "Desktop";
    internal static bool IsKnownGame(string processName) => Known.ContainsKey(processName);
    public static void Update()
    {
        try
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
            if (pid == 0 || pid == Environment.ProcessId) return;
            using var process = Process.GetProcessById((int)pid);
            var name = process.ProcessName;
            if (DesktopProcesses.Contains(name)) return; // Opening the tray must not replace the game with Explorer.
            LastForeground = Known.TryGetValue(name, out var friendly) ? friendly : Storage.SafeName(name);
        }
        catch { LastForeground = "Desktop"; }
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint processId);
}

public static class StartupRegistration
{
    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("Flashback", $"\"{Environment.ProcessPath}\" --tray");
        else key.DeleteValue("Flashback", false);
    }
}

// Prevent an encoder continuing to record if the UI crashes or is killed.
public sealed class ChildProcessJob : IDisposable
{
    private IntPtr handle;
    public ChildProcessJob()
    {
        handle = CreateJobObject(IntPtr.Zero, null);
        var info = new ExtendedLimit { Basic = new BasicLimit { LimitFlags = 0x2000 } };
        if (handle == IntPtr.Zero || !SetInformationJobObject(handle, 9, ref info, (uint)Marshal.SizeOf<ExtendedLimit>()))
            throw new InvalidOperationException("Could not create the recording process guard.");
    }
    public void Add(Process process)
    {
        if (!AssignProcessToJobObject(handle, process.Handle))
        {
            try { process.Kill(true); } catch { }
            throw new InvalidOperationException("Could not guard the recording process; recording was stopped.");
        }
    }
    public void Dispose() { if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; } }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimit { public long ProcessTime, JobTime; public uint LimitFlags; public UIntPtr MinWorkingSet, MaxWorkingSet; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOp, WriteOp, OtherOp, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimit { public BasicLimit Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr h, int infoClass, ref ExtendedLimit info, uint size);
    [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr h, IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
}
