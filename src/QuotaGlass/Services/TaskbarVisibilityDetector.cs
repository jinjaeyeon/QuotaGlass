using System.Runtime.InteropServices;

namespace QuotaGlass.Services;

internal static class TaskbarVisibilityDetector
{
    private const uint AbmGetState = 0x00000004;
    private const uint AbsAutoHide = 0x00000001;
    private const uint MonitorDefaultToNearest = 2;

    public static bool IsShown(nint taskbar)
    {
        var appBarData = new AppBarData
        {
            Size = (uint)Marshal.SizeOf<AppBarData>()
        };
        var state = SHAppBarMessage(AbmGetState, ref appBarData);
        if ((state & AbsAutoHide) == 0)
        {
            return true;
        }

        var monitor = MonitorFromWindow(
            taskbar,
            MonitorDefaultToNearest);
        if (monitor == nint.Zero ||
            !GetWindowRect(taskbar, out var taskbarBounds))
        {
            return true;
        }

        var monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };
        return !GetMonitorInfo(monitor, ref monitorInfo) ||
               HasVisibleThickness(
                   taskbarBounds.Left,
                   taskbarBounds.Top,
                   taskbarBounds.Right,
                   taskbarBounds.Bottom,
                   monitorInfo.Monitor.Left,
                   monitorInfo.Monitor.Top,
                   monitorInfo.Monitor.Right,
                   monitorInfo.Monitor.Bottom);
    }

    internal static bool HasVisibleThickness(
        int taskbarLeft,
        int taskbarTop,
        int taskbarRight,
        int taskbarBottom,
        int monitorLeft,
        int monitorTop,
        int monitorRight,
        int monitorBottom)
    {
        var intersectionWidth = Math.Max(
            0,
            Math.Min(taskbarRight, monitorRight) -
            Math.Max(taskbarLeft, monitorLeft));
        var intersectionHeight = Math.Max(
            0,
            Math.Min(taskbarBottom, monitorBottom) -
            Math.Max(taskbarTop, monitorTop));
        var isHorizontal =
            taskbarRight - taskbarLeft >= taskbarBottom - taskbarTop;

        const int hiddenEdgeThickness = 2;
        return (isHorizontal ? intersectionHeight : intersectionWidth) >
               hiddenEdgeThickness;
    }

    [DllImport("shell32.dll")]
    private static extern nuint SHAppBarMessage(
        uint message,
        ref AppBarData data);

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
    private struct AppBarData
    {
        public uint Size;
        public nint Window;
        public uint CallbackMessage;
        public uint Edge;
        public NativeRect Rect;
        public nint Parameter;
    }

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
