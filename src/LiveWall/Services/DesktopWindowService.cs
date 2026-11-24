using System;
using System.Globalization;
using System.Text;
using LiveWall.Models;

namespace LiveWall.Services;

/// <summary>
/// Finds the window a wallpaper surface has to be parented into, and how it has to be stacked there.
///
/// Two desktop layouts exist and they are NOT interchangeable:
///   • classic (Windows 10, Windows 11 up to 23H2): <c>0x052C</c> splits the desktop, the icons move into
///     a top-level <c>WorkerW</c> and a second, empty top-level <c>WorkerW</c> appears behind them.
///   • raised desktop (Windows 11 24H2, build 26100+): the icons stay inside <c>Progman</c>, a
///     <c>WorkerW</c> child of <c>Progman</c> paints the wallpaper, and <c>Progman</c> itself is created
///     with <c>WS_EX_NOREDIRECTIONBITMAP</c>, so an ordinary child window is never composed. The surface
///     has to be a layered child of <c>Progman</c> slotted directly below <c>SHELLDLL_DefView</c>, and the
///     icon host has to be forced to repaint once, otherwise the shell keeps showing the snapshot it took
///     while switching desktops.
///
/// The <c>0x052C</c> spawn message is rate limited: sending it on a loop makes Explorer rebuild its
/// desktop windows a few times per second, which steals activation from the foreground application.
/// </summary>
public sealed class DesktopWindowService
{
    private const uint SpawnMessageTimeoutMilliseconds = 500;

    private static readonly TimeSpan SpawnMessageInterval = TimeSpan.FromSeconds(10);

    private DesktopHost _host = DesktopHost.Empty;
    private DateTime _lastSpawnMessageUtc = DateTime.MinValue;

    /// <summary>The resolved desktop host (topology, parent window, z-order anchor).</summary>
    public DesktopHost Host
    {
        get { return _host; }
    }

    /// <summary>The window surfaces are parented into; <see cref="IntPtr.Zero"/> when nothing was found.</summary>
    public IntPtr WorkerWindow
    {
        get { return _host.ParentWindow; }
    }

    public bool IsAlive
    {
        get { return !_host.IsEmpty && NativeMethods.IsWindow(_host.ParentWindow); }
    }

    public bool EnsureWorkerWindow()
    {
        if (IsAlive)
        {
            return true;
        }

        _host = Resolve();
        if (_host.IsEmpty)
        {
            AppLog.Write("desktop host window NOT found (Progman missing)");
            return false;
        }

        if (_host.IsRaisedDesktop)
        {
            // The shell paints the wallpaper in its own WorkerW child of Progman: keep that one at the very
            // bottom so our surface sits above it, and nudge the icon host so its cached snapshot is dropped.
            PushShellWorkerToBottom();
            RefreshIconHost();
        }

        AppLog.Write("desktop host resolved: " + _host.Describe() + " client " + GetWorkerRectangle());
        return IsAlive;
    }

    /// <summary>Client area of the host in screen coordinates — the origin child positions are relative to.</summary>
    public RECT GetWorkerRectangle()
    {
        RECT result = new RECT();
        if (!IsAlive)
        {
            return result;
        }

        NativeMethods.NativePoint origin = new NativeMethods.NativePoint();
        if (!NativeMethods.ClientToScreen(_host.ParentWindow, ref origin))
        {
            return result;
        }

        RECT clientRectangle = new RECT();
        if (!NativeMethods.GetClientRect(_host.ParentWindow, out clientRectangle))
        {
            return result;
        }

        result.Left = origin.X;
        result.Top = origin.Y;
        result.Right = origin.X + clientRectangle.Width;
        result.Bottom = origin.Y + clientRectangle.Height;
        return result;
    }

    // ── Resolution ──

    private DesktopHost Resolve()
    {
        IntPtr progmanWindow = NativeMethods.FindWindow("Progman", null);
        if (progmanWindow == IntPtr.Zero)
        {
            return DesktopHost.Empty;
        }

        SendSpawnMessages(progmanWindow);

        IntPtr defViewInProgman = NativeMethods.FindWindowEx(progmanWindow, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defViewInProgman != IntPtr.Zero)
        {
            // Raised desktop: SHELLDLL_DefView never left Progman.
            IntPtr shellWorker = NativeMethods.FindWindowEx(progmanWindow, IntPtr.Zero, "WorkerW", null);
            bool noRedirectionBitmap = HasNoRedirectionBitmap(progmanWindow);
            return new DesktopHost(
                DesktopHostTopology.RaisedProgman,
                progmanWindow,
                defViewInProgman,
                defViewInProgman,
                shellWorker,
                true,
                noRedirectionBitmap);
        }

        IntPtr defViewHost = IntPtr.Zero;
        IntPtr workerBehindIcons = IntPtr.Zero;
        NativeMethods.EnumWindowsProc callback = (IntPtr windowHandle, IntPtr parameter) =>
        {
            IntPtr shellView = NativeMethods.FindWindowEx(windowHandle, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (shellView == IntPtr.Zero)
            {
                return true;
            }

            defViewHost = windowHandle;
            workerBehindIcons = NativeMethods.FindWindowEx(IntPtr.Zero, windowHandle, "WorkerW", null);
            return workerBehindIcons == IntPtr.Zero;
        };

        NativeMethods.EnumWindows(callback, IntPtr.Zero);

        if (workerBehindIcons != IntPtr.Zero)
        {
            return new DesktopHost(
                DesktopHostTopology.ClassicWorker,
                workerBehindIcons,
                new IntPtr(NativeMethods.HWND_BOTTOM),
                defViewHost,
                IntPtr.Zero,
                false,
                false);
        }

        if (defViewHost != IntPtr.Zero)
        {
            // DefView lives in a top-level window that has no WorkerW sibling (some 23H2 builds): draw in
            // Progman itself, below the icons if we can find them.
            IntPtr defViewAnywhere = NativeMethods.FindWindowEx(defViewHost, IntPtr.Zero, "SHELLDLL_DefView", null);
            IntPtr insertAfter = defViewAnywhere != IntPtr.Zero ? defViewAnywhere : new IntPtr(NativeMethods.HWND_BOTTOM);
            return new DesktopHost(
                DesktopHostTopology.RaisedProgman,
                progmanWindow,
                insertAfter,
                defViewAnywhere,
                IntPtr.Zero,
                true,
                HasNoRedirectionBitmap(progmanWindow));
        }

        // Nothing found: keep Progman as the parent so the user gets a precise failure instead of silence.
        return new DesktopHost(
            DesktopHostTopology.RaisedProgman,
            progmanWindow,
            new IntPtr(NativeMethods.HWND_BOTTOM),
            IntPtr.Zero,
            IntPtr.Zero,
            true,
            HasNoRedirectionBitmap(progmanWindow));
    }

    private void SendSpawnMessages(IntPtr progmanWindow)
    {
        if (DateTime.UtcNow - _lastSpawnMessageUtc < SpawnMessageInterval)
        {
            return;
        }

        _lastSpawnMessageUtc = DateTime.UtcNow;
        IntPtr messageResult;
        NativeMethods.SendMessageTimeout(
            progmanWindow,
            NativeMethods.WM_SPAWN_WORKER,
            new IntPtr(0x0D),
            new IntPtr(0x01),
            NativeMethods.SMTO_NORMAL,
            SpawnMessageTimeoutMilliseconds,
            out messageResult);

        NativeMethods.SendMessageTimeout(
            progmanWindow,
            NativeMethods.WM_SPAWN_WORKER,
            IntPtr.Zero,
            IntPtr.Zero,
            NativeMethods.SMTO_NORMAL,
            SpawnMessageTimeoutMilliseconds,
            out messageResult);
    }

    private static bool HasNoRedirectionBitmap(IntPtr windowHandle)
    {
        long extendedStyle = NativeMethods.GetWindowLongPtr(windowHandle, NativeMethods.GWL_EXSTYLE).ToInt64();
        return (extendedStyle & NativeMethods.WS_EX_NOREDIRECTIONBITMAP) != 0;
    }

    private void PushShellWorkerToBottom()
    {
        if (_host.ShellWorker == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            _host.ShellWorker,
            new IntPtr(NativeMethods.HWND_BOTTOM),
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>
    /// On the raised desktop the shell keeps a snapshot of the icon layer from the last desktop switch and
    /// paints it over everything below the icons, which hides our surface. Hiding and re-showing
    /// <c>SHELLDLL_DefView</c> makes it repaint itself, which drops the snapshot.
    /// </summary>
    private void RefreshIconHost()
    {
        if (_host.ShellDefView == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.ShowWindow(_host.ShellDefView, NativeMethods.SW_HIDE);
        NativeMethods.ShowWindow(_host.ShellDefView, NativeMethods.SW_SHOWNOACTIVATE);
        AppLog.Write("icon host " + DescribeWindow(_host.ShellDefView) + " refreshed");
    }

    private static string DescribeWindow(IntPtr windowHandle)
    {
        StringBuilder builder = new StringBuilder(256);
        NativeMethods.GetClassName(windowHandle, builder, builder.Capacity);
        return "0x" + windowHandle.ToInt64().ToString("X", CultureInfo.InvariantCulture) + " [" + builder.ToString() + "]";
    }
}
