using System.Runtime.InteropServices;

namespace QuotaGlass.Services;

internal static class FullscreenWindowDetector
{
    private const uint MonitorDefaultToNearest = 2;

    public static bool IsForegroundFullscreenOn(nint referenceWindow)
    {
        var foreground = GetForegroundWindow();
        if (foreground == nint.Zero ||
            foreground == referenceWindow ||
            foreground == GetShellWindow() ||
            !IsWindowVisible(foreground) ||
            IsIconic(foreground))
        {
            return false;
        }

        var referenceMonitor = MonitorFromWindow(
            referenceWindow,
            MonitorDefaultToNearest);
        if (referenceMonitor == nint.Zero ||
            MonitorFromWindow(foreground, MonitorDefaultToNearest) !=
            referenceMonitor ||
            !GetWindowRect(foreground, out var windowBounds))
        {
            return false;
        }

        var monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };
        return GetMonitorInfo(referenceMonitor, ref monitorInfo) &&
               CoversMonitor(
                   windowBounds.Left,
                   windowBounds.Top,
                   windowBounds.Right,
                   windowBounds.Bottom,
                   monitorInfo.Monitor.Left,
                   monitorInfo.Monitor.Top,
                   monitorInfo.Monitor.Right,
                   monitorInfo.Monitor.Bottom);
    }

    internal static bool CoversMonitor(
        int windowLeft,
        int windowTop,
        int windowRight,
        int windowBottom,
        int monitorLeft,
        int monitorTop,
        int monitorRight,
        int monitorBottom)
    {
        const int tolerance = 1;
        return windowLeft <= monitorLeft + tolerance &&
               windowTop <= monitorTop + tolerance &&
               windowRight >= monitorRight - tolerance &&
               windowBottom >= monitorBottom - tolerance;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint GetShellWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(
        nint window,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(
        nint window,
        out NativeRect rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(
        nint monitor,
        ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }
}
