using System;
using System.Runtime.InteropServices;
using System.Text;
using LiveWall.Models;

namespace LiveWall.Services;

internal static class NativeMethods
{
    internal const int GWL_EXSTYLE = -20;

    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_FRAMECHANGED = 0x0020;
    internal const uint SWP_SHOWWINDOW = 0x0040;

    internal const uint SMTO_NORMAL = 0x0000;
    internal const uint WM_SPAWN_WORKER = 0x052C;

    internal const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    internal const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    internal const int DWMWA_CLOAKED = 14;

    internal const int QUNS_BUSY = 2;
    internal const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;

    internal const int MONITORINFOF_PRIMARY = 1;

    internal delegate bool EnumWindowsProc(IntPtr windowHandle, IntPtr parameter);

    internal delegate bool MonitorEnumProc(IntPtr monitorHandle, IntPtr deviceContext, ref RECT monitorRectangle, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr FindWindowEx(IntPtr parentWindow, IntPtr childAfter, string? className, string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SendMessageTimeout(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll")]
    internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    internal static extern bool EnumDisplayMonitors(IntPtr deviceContext, IntPtr clipRectangle, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetParent(IntPtr childWindow, IntPtr newParentWindow);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetParent(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(IntPtr windowHandle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(IntPtr windowHandle, out RECT rectangle);

    [DllImport("user32.dll")]
    internal static extern bool IsWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("user32.dll")]
    internal static extern bool IsZoomed(IntPtr windowHandle);

    [DllImport("user32.dll")]
    internal static extern bool IsIconic(IntPtr windowHandle);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr windowHandle, StringBuilder className, int maximumCount);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    internal static extern bool GetMonitorInfoEx(IntPtr monitorHandle, ref MONITORINFOEX monitorInfo);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    internal static extern int DwmGetWindowAttributeRectangle(IntPtr windowHandle, int attribute, out RECT value, int size);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    internal static extern int DwmGetWindowAttributeInt32(IntPtr windowHandle, int attribute, out int value, int size);

    [DllImport("shell32.dll")]
    internal static extern int SHQueryUserNotificationState(out int state);
}
