using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
namespace Flashback;
internal sealed class FrameCadence : IDisposable
{
    private readonly int fps;
    private readonly IntPtr timer;
    private long origin, count;
    internal FrameCadence(int fps)
    {
        this.fps = fps; timer = CreateWaitableTimerEx(IntPtr.Zero, null, 2, 0x1F0003);
        if (timer == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        Reset();
    }
    internal void Reset() { origin = Stopwatch.GetTimestamp(); count = 0; }
    internal void Wait()
    {
        double elapsed = Stopwatch.GetElapsedTime(origin).TotalSeconds;
        double remaining = count / (double)fps - elapsed;
        if (remaining > 0)
        {
            long due = -Math.Max(1, (long)(remaining * 10000000));
            if (!SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false)) throw new System.ComponentModel.Win32Exception();
            WaitForSingleObject(timer, 1000);
        }
        // Capture does not issue a burst of redundant GPU copies after a scheduler stall.
        count = Math.Max(count + 1, (long)(elapsed * fps));
    }
    public void Dispose() => CloseHandle(timer);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWaitableTimerEx(IntPtr attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetWaitableTimer(IntPtr timer, ref long due, int period, IntPtr callback, IntPtr argument, bool resume);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
