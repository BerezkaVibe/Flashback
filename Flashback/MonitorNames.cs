using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Flashback;

// Maps Windows desktop device names (\\.\DISPLAY1) to the monitor's own name, such as "LG ULTRAGEAR".
internal static class MonitorNames
{
    internal static Dictionary<string, string> Read()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (GetDisplayConfigBufferSizes(OnlyActivePaths, out uint pathCount, out uint modeCount) != 0) return names;
            var paths = new PathInfo[pathCount]; var modes = new ModeInfo[modeCount];
            if (QueryDisplayConfig(OnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return names;
            for (int i = 0; i < pathCount; i++)
            {
                var source = new SourceName { Header = new Header { Type = 1, Size = (uint)Marshal.SizeOf<SourceName>(), Adapter = paths[i].Source.Adapter, Id = paths[i].Source.Id } };
                var target = new TargetName { Header = new Header { Type = 2, Size = (uint)Marshal.SizeOf<TargetName>(), Adapter = paths[i].Target.Adapter, Id = paths[i].Target.Id } };
                if (DisplayConfigGetDeviceInfo(ref source) != 0 || DisplayConfigGetDeviceInfo(ref target) != 0) continue;
                string name = target.FriendlyName?.Trim() ?? "";
                if (name.Length == 0 && paths[i].Target.OutputTechnology is 0x80000000 or 11 or 13) name = "Built-in display";
                if (name.Length > 0) names.TryAdd(source.GdiName, name);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        return names;
    }

    private const uint OnlyActivePaths = 2;
    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct PathSource { public Luid Adapter; public uint Id, ModeIndex, Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct PathTarget { public Luid Adapter; public uint Id, ModeIndex, OutputTechnology, Rotation, Scaling, RefreshNumerator, RefreshDenominator, ScanLineOrdering; public int Available; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct PathInfo { public PathSource Source; public PathTarget Target; public uint Flags; }
    [StructLayout(LayoutKind.Explicit, Size = 64)] private struct ModeInfo { }
    [StructLayout(LayoutKind.Sequential)] private struct Header { public int Type; public uint Size; public Luid Adapter; public uint Id; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct SourceName { public Header Header; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string GdiName; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct TargetName
    {
        public Header Header; public uint Flags, OutputTechnology; public ushort Manufacturer, ProductCode; public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string FriendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DevicePath;
    }
    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PathInfo[] paths, ref uint modeCount, [Out] ModeInfo[] modes, IntPtr topology);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref SourceName info);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref TargetName info);
}
