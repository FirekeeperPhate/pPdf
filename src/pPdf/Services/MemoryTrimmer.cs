using System.Diagnostics;
using System.Runtime.InteropServices;

namespace pPdf.Services;

/// <summary>Gives memory back to the system: used when the window has been minimized for a while.</summary>
public static class MemoryTrimmer
{
    [DllImport("psapi.dll")]
    static extern bool EmptyWorkingSet(IntPtr process);

    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int index);

    /// <summary>Pixels of the primary screen (physical), the unit the render cache is sized in.</summary>
    public static long ScreenPixels()
    {
        long w = GetSystemMetrics(0), h = GetSystemMetrics(1);
        return w > 0 && h > 0 ? w * h : 1920L * 1080;
    }

    public static void Trim()
    {
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        using var me = Process.GetCurrentProcess();
        EmptyWorkingSet(me.Handle);
    }
}
