using System;
using System.Runtime.InteropServices;

namespace Flashback;

// Gives the current thread a name Windows knows (Task Manager, debuggers, --open-cpu-test), which .NET's
// Thread.Name doesn't pass on here.
internal static class ThreadNames
{
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int SetThreadDescription(IntPtr thread, string description);
    [ThreadStatic] private static string? named;
    internal static void Name(string name)
    {
        if (named == name) return;
        named = name;
        try { SetThreadDescription(GetCurrentThread(), name); } catch { }
    }
}
