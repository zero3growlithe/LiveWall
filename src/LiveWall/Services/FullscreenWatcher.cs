using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using LiveWall.Models;

namespace LiveWall.Services;

/// <summary>
/// Decides which monitors must pause their wallpaper.
///
/// Rules (both toggleable):
///  * a monitor showing a maximized (or fullscreen-covering) window pauses that monitor only;
///  * a fullscreen application that currently has the focus pauses every monitor.
/// </summary>
public sealed class FullscreenWatcher
{
    private const int CoordinateTolerance = 2;
    private const int MinimumCandidateSize = 100;
    private const int MaximumCachedProcessNames = 256;

    private readonly Dictionary<uint, string> _processNames = new Dictionary<uint, string>();
    private readonly uint _ownProcessId = (uint)Environment.ProcessId;

    public PauseDecision Evaluate(IReadOnlyList<MonitorDescriptor> monitors, bool watchMaximized, bool watchFullscreen)
    {
        PauseDecision decision = new PauseDecision();
        if (monitors.Count == 0)
        {
            return decision;
        }

        if (!watchMaximized && !watchFullscreen)
        {
            return decision;
        }

        if (watchFullscreen)
        {
            MonitorDescriptor? focusedMonitor;
            string focusedReason;
            if (TryDetectFocusedFullscreen(monitors, out focusedMonitor, out focusedReason))
            {
                decision.PauseAll = true;
                decision.PauseAllReason = focusedReason;
                if (focusedMonitor != null)
                {
                    decision.MonitorReasons[focusedMonitor.DeviceName] = focusedReason;
                }

                return decision;
            }
        }

        NativeMethods.EnumWindowsProc callback = (IntPtr windowHandle, IntPtr parameter) =>
        {
            InspectWindow(windowHandle, monitors, watchMaximized, watchFullscreen, decision);
            return true;
        };

        NativeMethods.EnumWindows(callback, IntPtr.Zero);
        return decision;
    }

    // ── Rules ──

    private bool TryDetectFocusedFullscreen(IReadOnlyList<MonitorDescriptor> monitors, out MonitorDescriptor? monitor, out string reason)
    {
        monitor = null;
        reason = string.Empty;

        IntPtr foregroundWindow = NativeMethods.GetForegroundWindow();
        if (foregroundWindow != IntPtr.Zero && !NativeMethods.IsIconic(foregroundWindow) && !IsCloaked(foregroundWindow))
        {
            uint processId = 0;
            NativeMethods.GetWindowThreadProcessId(foregroundWindow, out processId);
            string className = GetClassName(foregroundWindow);
            RECT bounds = GetWindowBounds(foregroundWindow);
            MonitorDescriptor? candidate = FindMonitor(monitors, NativeMethods.MonitorFromWindow(foregroundWindow, NativeMethods.MONITOR_DEFAULTTONEAREST));

            if (processId != _ownProcessId
                && !IsShellWindowClass(className)
                && candidate != null
                && bounds.Covers(candidate.MonitorRectangle, CoordinateTolerance))
            {
                monitor = candidate;
                reason = "fullscreen app is focused: " + GetProcessName(processId);
                return true;
            }
        }

        int notificationState;
        if (NativeMethods.SHQueryUserNotificationState(out notificationState) == 0)
        {
            if (notificationState == NativeMethods.QUNS_RUNNING_D3D_FULL_SCREEN || notificationState == NativeMethods.QUNS_BUSY)
            {
                monitor = null;
                reason = "a fullscreen application is running (Windows reports full-screen state)";
                return true;
            }
        }

        return false;
    }

    private void InspectWindow(IntPtr windowHandle, IReadOnlyList<MonitorDescriptor> monitors, bool watchMaximized, bool watchFullscreen, PauseDecision decision)
    {
        if (!NativeMethods.IsWindowVisible(windowHandle))
        {
            return;
        }

        if (NativeMethods.IsIconic(windowHandle) || IsCloaked(windowHandle))
        {
            return;
        }

        string className = GetClassName(windowHandle);
        if (IsShellWindowClass(className))
        {
            return;
        }

        uint processId = 0;
        NativeMethods.GetWindowThreadProcessId(windowHandle, out processId);
        if (processId == _ownProcessId)
        {
            return;
        }

        RECT bounds = GetWindowBounds(windowHandle);
        if (bounds.Width < MinimumCandidateSize || bounds.Height < MinimumCandidateSize)
        {
            return;
        }

        MonitorDescriptor? monitor = FindMonitor(monitors, NativeMethods.MonitorFromWindow(windowHandle, NativeMethods.MONITOR_DEFAULTTONEAREST));
        if (monitor == null || !bounds.Intersects(monitor.MonitorRectangle))
        {
            return;
        }

        if (decision.MonitorReasons.ContainsKey(monitor.DeviceName))
        {
            return;
        }

        bool isMaximized = NativeMethods.IsZoomed(windowHandle);
        bool coversMonitor = bounds.Covers(monitor.MonitorRectangle, CoordinateTolerance);

        if (isMaximized && watchMaximized)
        {
            decision.MonitorReasons[monitor.DeviceName] = "maximized window: " + GetProcessName(processId);
            return;
        }

        if (coversMonitor && watchFullscreen)
        {
            decision.MonitorReasons[monitor.DeviceName] = "fullscreen window: " + GetProcessName(processId);
        }
    }

    // ── Win32 helpers ──

    private static bool IsShellWindowClass(string className)
    {
        return string.Equals(className, "Progman", StringComparison.OrdinalIgnoreCase)
            || string.Equals(className, "WorkerW", StringComparison.OrdinalIgnoreCase)
            || string.Equals(className, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase)
            || string.Equals(className, "Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase)
            || string.Equals(className, "XamlExplorerHostIslandWindow", StringComparison.OrdinalIgnoreCase)
            || string.Equals(className, "MultitaskingViewFrame", StringComparison.OrdinalIgnoreCase)
            || string.Equals(className, "ForegroundStaging", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCloaked(IntPtr windowHandle)
    {
        int cloaked;
        if (NativeMethods.DwmGetWindowAttributeInt32(windowHandle, NativeMethods.DWMWA_CLOAKED, out cloaked, sizeof(int)) != 0)
        {
            return false;
        }

        return cloaked != 0;
    }

    private static RECT GetWindowBounds(IntPtr windowHandle)
    {
        RECT bounds;
        if (NativeMethods.DwmGetWindowAttributeRectangle(windowHandle, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out bounds, Marshal.SizeOf<RECT>()) == 0)
        {
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                return bounds;
            }
        }

        RECT fallback;
        NativeMethods.GetWindowRect(windowHandle, out fallback);
        return fallback;
    }

    private static string GetClassName(IntPtr windowHandle)
    {
        StringBuilder builder = new StringBuilder(256);
        NativeMethods.GetClassName(windowHandle, builder, builder.Capacity);
        return builder.ToString();
    }

    private static MonitorDescriptor? FindMonitor(IReadOnlyList<MonitorDescriptor> monitors, IntPtr monitorHandle)
    {
        for (int index = 0; index < monitors.Count; index++)
        {
            if (monitors[index].MonitorHandle == monitorHandle)
            {
                return monitors[index];
            }
        }

        return null;
    }

    private string GetProcessName(uint processId)
    {
        if (processId == 0)
        {
            return "unknown process";
        }

        if (_processNames.TryGetValue(processId, out string? cached) && cached != null)
        {
            return cached;
        }

        string name = "unknown process";
        try
        {
            using (Process process = Process.GetProcessById((int)processId))
            {
                name = process.ProcessName + ".exe";
            }
        }
        catch (Exception)
        {
            // Access denied (elevated / protected process) is a normal outcome, keep the placeholder.
        }

        if (_processNames.Count > MaximumCachedProcessNames)
        {
            _processNames.Clear();
        }

        _processNames[processId] = name;
        return name;
    }
}
