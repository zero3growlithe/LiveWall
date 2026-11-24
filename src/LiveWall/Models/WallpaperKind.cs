namespace LiveWall.Models;

public enum WallpaperKind
{
    SolidColor = 0,
    Image = 1,
    Slideshow = 2,
    Video = 3,

    /// <summary>A folder of videos, played in the order chosen with <see cref="VideoPlaybackOrder"/>.</summary>
    VideoFolder = 4
}
