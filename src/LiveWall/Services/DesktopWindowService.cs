using System;
using LiveWall.Models;

namespace LiveWall.Services;

/// <summary>
/// Finds the desktop host window (the WorkerW that sits behind the desktop icons) so wallpaper
/// surfaces can be parented into it. Windows 10 and 11 differ, so three strategies are tried in order.
/// </summary>
public sealed class DesktopWindowService
{
    private IntPtr _workerWindow = IntPtr.Zero;

    public IntPtr WorkerWindow
    {
        get { return _workerWindow; }
    }

    public bool IsAlive
    {
        get { return _workerWindow != IntPtr.Zero && NativeMethods.IsWindow(_workerWindow); }
    }

    public bool EnsureWorkerWindow()
    {
        if (IsAlive)
        {
            return true;
        }

        _workerWindow = IntPtr.Zero;
        IntPtr progmanWindow = NativeMethods.FindWindow("Progman", null);
        if (progmanWindow != IntPtr.Zero)
        {
            // The 0x052C message makes Explorer spawn the WorkerW that lives behind the icons.
            IntPtr messageResult;
            NativeMethods.SendMessageTimeout(progmanWindow, NativeMethods.WM_SPAWN_WORKER, new IntPtr(0x0D), new IntPtr(0x01), NativeMethods.SMTO_NORMAL, 1000, out messageResult);
            NativeMethods.SendMessageTimeout(progmanWindow, NativeMethods.WM_SPAWN_WORKER, IntPtr.Zero, IntPtr.Zero, NativeMethods.SMTO_NORMAL, 1000, out messageResult);
        }

        IntPtr workerBehindIcons = IntPtr.Zero;
        NativeMethods.EnumWindowsProc callback = (IntPtr windowHandle, IntPtr parameter) =>
        {
            IntPtr shellView = NativeMethods.FindWindowEx(windowHandle, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (shellView != IntPtr.Zero)
            {
                workerBehindIcons = NativeMethods.FindWindowEx(IntPtr.Zero, windowHandle, "WorkerW", null);
                if (workerBehindIcons != IntPtr.Zero)
                {
                    return false;
                }
            }

            return true;
        };

        NativeMethods.EnumWindows(callback, IntPtr.Zero);

        if (workerBehindIcons != IntPtr.Zero)
        {
            _workerWindow = workerBehindIcons;
        }

        if (_workerWindow == IntPtr.Zero && progmanWindow != IntPtr.Zero)
        {
            // Windows 11 keeps the WorkerW inside Progman.
            _workerWindow = NativeMethods.FindWindowEx(progmanWindow, IntPtr.Zero, "WorkerW", null);
        }

        if (_workerWindow == IntPtr.Zero)
        {
            // Last resort: draw directly inside Progman (still behind the icons on current builds).
            _workerWindow = progmanWindow;
        }

        if (_workerWindow != IntPtr.Zero)
        {
            AppLog.Write("desktop host window resolved: 0x" + _workerWindow.ToInt64().ToString("X"));
        }
        else
        {
            AppLog.Write("desktop host window NOT found (Progman missing)");
        }

        return IsAlive;
    }

    public RECT GetWorkerRectangle()
    {
        RECT rectangle = new RECT();
        if (IsAlive && NativeMethods.GetWindowRect(_workerWindow, out rectangle))
        {
            return rectangle;
        }

        return new RECT();
    }
}
