using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Flashback;

internal sealed record CaptureDisplay(int AdapterIndex, int OutputIndex, string DeviceName, string AdapterName, uint VendorId, bool Attached)
{
    internal bool NeedsNvidiaTransfer => VendorId != 0x10de;

    internal static CaptureDisplay Resolve(int screenIndex)
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (screenIndex < 0 || screenIndex >= screens.Length)
            throw new InvalidOperationException("The selected display is no longer connected. Choose a display in Settings.");
        return Resolve(screens[screenIndex].DeviceName, Enumerate());
    }

    internal static CaptureDisplay Resolve(string name, IReadOnlyList<CaptureDisplay> outputs) =>
        outputs.FirstOrDefault(o => o.Attached && string.Equals(o.DeviceName, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"Could not find a desktop capture adapter for {name}. Wake the display, unlock Windows, and select a connected display in Settings.");

    // Match FFmpeg's EnumAdapters ordering. Output indices are local to an adapter,
    // whereas Screen.AllScreens describes the complete Windows desktop.
    internal static IReadOnlyList<CaptureDisplay> Enumerate() => EnumerateTopology(out _);
    internal static IReadOnlyList<VideoAdapter> EnumerateAdapters() { EnumerateTopology(out var adapters); return adapters; }
    private static IReadOnlyList<CaptureDisplay> EnumerateTopology(out List<VideoAdapter> adapters)
    {
        adapters = new();
        var result = new List<CaptureDisplay>();
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref iid, out var factory));
        try
        {
            for (uint a = 0; ; a++)
            {
                int hr = Method<EnumChild>(factory, 7)(factory, a, out var adapter);
                if (hr == NotFound) break;
                Marshal.ThrowExceptionForHR(hr);
                try
                {
                    Marshal.ThrowExceptionForHR(Method<GetAdapterDesc>(adapter, 8)(adapter, out var desc));
                    adapters.Add(new((int)a, desc.Description, desc.VendorId));
                    for (uint o = 0; ; o++)
                    {
                        hr = Method<EnumChild>(adapter, 7)(adapter, o, out var output);
                        if (hr == NotFound) break;
                        Marshal.ThrowExceptionForHR(hr);
                        try
                        {
                            Marshal.ThrowExceptionForHR(Method<GetOutputDesc>(output, 7)(output, out var screen));
                            result.Add(new((int)a, (int)o, screen.DeviceName, desc.Description, desc.VendorId, screen.Attached != 0));
                        }
                        finally { Marshal.Release(output); }
                    }
                }
                finally { Marshal.Release(adapter); }
            }
        }
        finally { Marshal.Release(factory); }
        return result;
    }

    private const int NotFound = unchecked((int)0x887A0002);
    private static T Method<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumChild(IntPtr self, uint index, out IntPtr child);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetAdapterDesc(IntPtr self, out AdapterDesc desc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetOutputDesc(IntPtr self, out OutputDesc desc);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDesc
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OutputDesc
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public int Left, Top, Right, Bottom, Attached, Rotation;
        public IntPtr Monitor;
    }
}
