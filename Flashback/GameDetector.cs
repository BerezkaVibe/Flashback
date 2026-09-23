using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Flashback;

// Decides whether the foreground app is a game, for automatic recording.
// Checked once a second from the existing UI timer; it opens no handles between checks.
internal static class GameDetector
{
    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "chrome", "msedge", "firefox", "opera", "brave", "vivaldi", "iexplore", "vlc", "mpc-hc64", "mpc-hc", "mpv", "PotPlayerMini64",
        "Spotify", "Discord", "Teams", "ms-teams", "Zoom", "obs64", "Flashback", "devenv", "Code", "WINWORD", "EXCEL", "POWERPNT", "Photoshop",
        "steamwebhelper", "steam", "EpicGamesLauncher", "Battle.net", "EADesktop", "UbisoftConnect", "upc", "ApplicationFrameHost", "TextInputHost"
    };
    private static HashSet<string>? gameBarPaths;
    private static DateTime gameBarRead;

    // Returns the running game's process id, or 0.
    internal static int ForegroundGame()
    {
        try
        {
            var window = GetForegroundWindow();
            GetWindowThreadProcessId(window, out var pid);
            if (pid == 0 || pid == Environment.ProcessId) return 0;
            using var process = Process.GetProcessById((int)pid);
            if (NotGames.Contains(process.ProcessName)) return 0;
            if (GameTracker.IsKnownGame(process.ProcessName)) return (int)pid;
            string? path = null;
            try { path = process.MainModule?.FileName; } catch { }
            if (path != null && GameBarGames().Contains(path)) return (int)pid;
            return IsFullscreen(window) ? (int)pid : 0;
        }
        catch { return 0; }
    }
    internal static bool IsRunning(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch { return false; }
    }
    // Game Bar's own list of executables it has recognized as games.
    private static HashSet<string> GameBarGames()
    {
        if (gameBarPaths != null && DateTime.UtcNow - gameBarRead < TimeSpan.FromMinutes(5)) return gameBarPaths;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var children = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children");
            if (children != null)
                foreach (var name in children.GetSubKeyNames())
                {
                    using var child = children.OpenSubKey(name);
                    if (child?.GetValue("MatchedExeFullPath") is string exe && exe.Length > 0) paths.Add(Path.GetFullPath(exe));
                }
        }
        catch { }
        gameBarRead = DateTime.UtcNow;
        return gameBarPaths = paths;
    }
    private static bool IsFullscreen(IntPtr window)
    {
        if (window == IntPtr.Zero || !GetWindowRect(window, out var rect)) return false;
        var monitor = MonitorFromWindow(window, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;
        return rect.Left <= info.Monitor.Left && rect.Top <= info.Monitor.Top && rect.Right >= info.Monitor.Right && rect.Bottom >= info.Monitor.Bottom;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
