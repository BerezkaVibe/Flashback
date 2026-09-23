using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Flashback;

internal static class GraphicsDiagnostics
{
    internal static string DescribeDisplays()
    {
        try
        {
            var displays = new List<string>();
            for (uint index = 0; index < 32; index++)
            {
                var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
                if (!EnumDisplayDevices(null, index, ref device, 0)) break;
                displays.Add($"{device.Name}: {device.Description} (attached: {(device.Flags & 1) != 0})");
            }
            return "Windows display adapters:\n" + string.Join("\n", displays);
        }
        catch { return "Windows display adapter details unavailable."; }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice display, uint flags);
}
