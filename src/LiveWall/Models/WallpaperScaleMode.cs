namespace LiveWall.Models;

/// <summary>
/// How the DirectComposition visual is scaled against the target window's DPI.
/// <para>
/// DirectComposition positions visuals in DIPs and the desktop compositor may or may not turn those into
/// device pixels using the monitor's DPI — the answer differs per build and per window, so it is a setting
/// rather than a guess. On a 150 % display the two modes differ by exactly 1.5x, which is easy to recognise:
/// the wallpaper is either 1.5x too large (cropped) or 1.5x too small (not filling).
/// </para>
/// </summary>
public enum WallpaperScaleMode
{
    /// <summary>Divide the visual scale by the display scaling (1 DIP = 1/DPI inch). Correct on most builds.</summary>
    MatchDisplayScaling = 0,

    /// <summary>Treat one DIP as one device pixel. Use this when the wallpaper comes out 1.5x too big on a 150 % display.</summary>
    Pixels = 1,

    /// <summary>
    /// Compensate as if the display were at 150 %, even when Windows reports 96 DPI for the window. The escape
    /// hatch for a desktop host that hides the real DPI from a child window.
    /// </summary>
    AssumeOneHundredFiftyPercent = 2
}
