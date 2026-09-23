using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Flashback;
internal static class WindowTheme
{
    internal static void Attach(Window window) => window.SourceInitialized += (_, _) =>
    {
        int dark = 1;
        try { DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 20, ref dark, sizeof(int)); } catch { }
    };
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
