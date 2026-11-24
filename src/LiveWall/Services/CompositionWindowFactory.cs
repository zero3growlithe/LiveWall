using System;

namespace LiveWall.Services;

/// <summary>
/// Creates the plain child window that a DirectComposition visual tree is attached to.
/// <para>
/// The window deliberately has no content of its own: it is created with <c>WS_EX_NOREDIRECTIONBITMAP</c>, so
/// Windows never gives it a redirection surface and everything you see comes from DirectComposition. That is
/// the same arrangement the shell itself uses for the desktop host on Windows 11 24H2+, which is exactly why
/// it is the arrangement that composes.
/// </para>
/// </summary>
internal static class CompositionWindowFactory
{
    private const string WindowClassName = "Static";
    private const int WindowStyleChild = 0x40000000;
    private const int WindowStyleVisible = 0x10000000;
    private const int WindowStyleClipSiblings = 0x04000000;
    private const int WindowStyleClipChildren = 0x02000000;
    private const int WindowExNoActivate = 0x08000000;
    private const int WindowExToolWindow = 0x00000080;
    private const int WindowExNoRedirectionBitmap = 0x00200000;

    public static IntPtr Create(IntPtr parentWindow, int x, int y, int width, int height)
    {
        if (parentWindow == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        int style = WindowStyleChild | WindowStyleVisible | WindowStyleClipSiblings | WindowStyleClipChildren;
        int extendedStyle = WindowExNoActivate | WindowExToolWindow | WindowExNoRedirectionBitmap;

        return NativeMethods.CreateWindowEx(
            (uint)extendedStyle,
            WindowClassName,
            string.Empty,
            (uint)style,
            x,
            y,
            width,
            height,
            parentWindow,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
    }
}
