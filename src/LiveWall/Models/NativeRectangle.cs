using System.Runtime.InteropServices;

namespace LiveWall.Models;

/// <summary>
/// Native Win32 rectangle in physical screen pixels.
/// Public because it is part of the public monitor description used by the engine and the UI.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public int Width
    {
        get { return Right - Left; }
    }

    public int Height
    {
        get { return Bottom - Top; }
    }

    /// <summary>
    /// True when this rectangle covers <paramref name="other"/> within the given pixel tolerance.
    /// </summary>
    public bool Covers(RECT other, int tolerance)
    {
        return Left <= other.Left + tolerance
            && Top <= other.Top + tolerance
            && Right >= other.Right - tolerance
            && Bottom >= other.Bottom - tolerance;
    }

    /// <summary>
    /// True when the two rectangles share at least one pixel.
    /// </summary>
    public bool Intersects(RECT other)
    {
        return Left < other.Right && Right > other.Left && Top < other.Bottom && Bottom > other.Top;
    }

    public override string ToString()
    {
        return string.Concat(Left.ToString(), ",", Top.ToString(), ",", Width.ToString(), "x", Height.ToString());
    }
}

/// <summary>
/// MONITORINFOEXW: monitor rectangle, work area, flags and the device name (e.g. \\.\DISPLAY1).
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct MONITORINFOEX
{
    public int cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string szDevice;
}
