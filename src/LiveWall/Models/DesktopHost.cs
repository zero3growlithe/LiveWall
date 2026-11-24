using System;
using System.Globalization;

namespace LiveWall.Models;

/// <summary>
/// Which desktop layout the shell currently exposes. Windows 11 24H2 (build 26100+) moved the desktop
/// icons back inside <c>Progman</c> and made the Progman subtree a "raised desktop" that is composed
/// through the visual layer instead of a redirection surface, so the window has to be parented and
/// stacked differently than on Windows 10 / 11 23H2.
/// </summary>
public enum DesktopHostTopology
{
    None,
    ClassicWorker,
    RaisedProgman
}

/// <summary>
/// Where a wallpaper surface has to be parented and how it has to be stacked to end up between the
/// static wallpaper and the desktop icons.
/// </summary>
public sealed class DesktopHost
{
    public static readonly DesktopHost Empty = new DesktopHost(
        DesktopHostTopology.None,
        IntPtr.Zero,
        IntPtr.Zero,
        IntPtr.Zero,
        IntPtr.Zero,
        false,
        false);

    public DesktopHost(
        DesktopHostTopology topology,
        IntPtr parentWindow,
        IntPtr insertAfterWindow,
        IntPtr shellDefView,
        IntPtr shellWorker,
        bool useLayeredWindow,
        bool progmanHasNoRedirectionBitmap)
    {
        Topology = topology;
        ParentWindow = parentWindow;
        InsertAfterWindow = insertAfterWindow;
        ShellDefView = shellDefView;
        ShellWorker = shellWorker;
        UseLayeredWindow = useLayeredWindow;
        ProgmanHasNoRedirectionBitmap = progmanHasNoRedirectionBitmap;
    }

    public DesktopHostTopology Topology { get; private set; }

    /// <summary>The window the surface becomes a child of (the WorkerW behind the icons, or Progman).</summary>
    public IntPtr ParentWindow { get; private set; }

    /// <summary>z-order anchor for SetWindowPos: the sibling the surface is placed directly below.</summary>
    public IntPtr InsertAfterWindow { get; private set; }

    /// <summary><c>SHELLDLL_DefView</c> (the icon host), when the shell exposes it.</summary>
    public IntPtr ShellDefView { get; private set; }

    /// <summary>The shell's own wallpaper <c>WorkerW</c> inside Progman (raised desktop only).</summary>
    public IntPtr ShellWorker { get; private set; }

    /// <summary>
    /// True when the surface must be a layered child: children of the no-redirection-bitmap Progman are
    /// not composed otherwise, which is exactly the "attached but nothing on screen" symptom.
    /// </summary>
    public bool UseLayeredWindow { get; private set; }

    public bool ProgmanHasNoRedirectionBitmap { get; private set; }

    public bool IsEmpty
    {
        get { return Topology == DesktopHostTopology.None || ParentWindow == IntPtr.Zero; }
    }

    /// <summary>True when this is the post-24H2 layout that needs the icon-refresh nudge.</summary>
    public bool IsRaisedDesktop
    {
        get { return Topology == DesktopHostTopology.RaisedProgman; }
    }

    public string Describe()
    {
        return Topology.ToString()
            + " parent 0x" + ParentWindow.ToInt64().ToString("X", CultureInfo.InvariantCulture)
            + " insertAfter 0x" + InsertAfterWindow.ToInt64().ToString("X", CultureInfo.InvariantCulture)
            + " defView 0x" + ShellDefView.ToInt64().ToString("X", CultureInfo.InvariantCulture)
            + " shellWorker 0x" + ShellWorker.ToInt64().ToString("X", CultureInfo.InvariantCulture)
            + (UseLayeredWindow ? " layered" : string.Empty)
            + (ProgmanHasNoRedirectionBitmap ? " no-redirection-bitmap" : string.Empty);
    }

    public override string ToString()
    {
        return Describe();
    }
}
