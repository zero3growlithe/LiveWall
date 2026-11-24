using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LiveWall.Models;

namespace LiveWall.Services;

/// <summary>
/// Enumerates the physical monitors and resolves their device names / rectangles.
/// </summary>
public static class MonitorService
{
    public static List<MonitorDescriptor> GetMonitors()
    {
        List<MonitorDescriptor> monitors = new List<MonitorDescriptor>();

        NativeMethods.MonitorEnumProc callback = (IntPtr monitorHandle, IntPtr deviceContext, ref RECT monitorRectangle, IntPtr data) =>
        {
            MONITORINFOEX monitorInfo = new MONITORINFOEX();
            monitorInfo.cbSize = Marshal.SizeOf<MONITORINFOEX>();
            if (!NativeMethods.GetMonitorInfoEx(monitorHandle, ref monitorInfo))
            {
                return true;
            }

            MonitorDescriptor descriptor = new MonitorDescriptor();
            descriptor.MonitorHandle = monitorHandle;
            descriptor.DeviceName = monitorInfo.szDevice ?? string.Empty;
            descriptor.MonitorRectangle = monitorInfo.rcMonitor;
            descriptor.WorkRectangle = monitorInfo.rcWork;
            descriptor.IsPrimary = (monitorInfo.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0;
            monitors.Add(descriptor);
            return true;
        };

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);

        monitors.Sort((MonitorDescriptor left, MonitorDescriptor right) =>
        {
            int byX = left.MonitorRectangle.Left.CompareTo(right.MonitorRectangle.Left);
            if (byX != 0)
            {
                return byX;
            }

            return left.MonitorRectangle.Top.CompareTo(right.MonitorRectangle.Top);
        });

        for (int index = 0; index < monitors.Count; index++)
        {
            monitors[index].Index = index;
            if (string.IsNullOrEmpty(monitors[index].DeviceName))
            {
                monitors[index].DeviceName = "MONITOR" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return monitors;
    }
}
