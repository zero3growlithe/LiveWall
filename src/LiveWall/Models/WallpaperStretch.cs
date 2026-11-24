namespace LiveWall.Models;

public enum WallpaperStretch
{
    Fill = 0,
    Fit = 1,
    Stretch = 2,
    Center = 3
}

/// <summary>How the wallpaper windows render. Changing this needs an application restart.</summary>
public enum RenderModePreference
{
    Hardware = 0,
    Software = 1
}

/// <summary>
/// How the wallpaper window is handed to the desktop host. A window created straight into the host is the
/// normal case; "reparented popup" is the older order kept as a fallback because the two can behave
/// differently in the shell's composition. Changing this needs an application restart.
/// </summary>
public enum SurfaceCreationMode
{
    ChildAtCreation = 0,
    ReparentedPopup = 1
}
