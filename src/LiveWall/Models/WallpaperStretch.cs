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
