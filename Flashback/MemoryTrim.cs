using System;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;

namespace Flashback;

// While Flashback sits in the tray or taskbar, its window memory is not needed.
// Compact the managed heap once and let Windows reclaim idle pages; pages the
// recorder touches come straight back, so capture is unaffected.
internal static class MemoryTrim
{
    private static long pending;
    internal static void Attach(Window window)
    {
        window.IsVisibleChanged += (_, _) => { if (!window.IsVisible) Schedule(window); };
        window.StateChanged += (_, _) => { if (window.WindowState == WindowState.Minimized) Schedule(window); };
    }
    private static void Schedule(Window window)
    {
        long ticket = Stopwatch.GetTimestamp(); pending = ticket;
        _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => window.Dispatcher.BeginInvoke(() =>
        {
            if (pending != ticket || (window.IsVisible && window.WindowState != WindowState.Minimized)) return;
            Trim();
        }));
    }
    internal static void Trim()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: true);
        try { SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1); } catch { }
    }
    [DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(IntPtr process, nint minimum, nint maximum);
}
