using System;
using System.Globalization;

namespace LiveWall.Models;

/// <summary>
/// One monitor's wallpaper definition. Cloned per monitor when the user does not want a shared setup.
/// </summary>
public sealed class WallpaperSource
{
    public WallpaperKind Kind { get; set; } = WallpaperKind.SolidColor;

    /// <summary>Image file or video file, depending on <see cref="Kind"/>.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Slideshow folder, used when <see cref="Kind"/> is <see cref="WallpaperKind.Slideshow"/>.</summary>
    public string FolderPath { get; set; } = string.Empty;

    public bool IncludeSubfolders { get; set; }

    public int SlideshowIntervalSeconds { get; set; } = 30;

    public bool Shuffle { get; set; } = true;

    /// <summary>Play order for a <see cref="WallpaperKind.VideoFolder"/> source.</summary>
    public VideoPlaybackOrder VideoOrder { get; set; } = VideoPlaybackOrder.Sequential;

    /// <summary>
    /// Highest number of frames per second pushed to the screen; 0 pushes every decoded frame. Decoding still
    /// has to run for every frame, this only saves the per-frame upload and present.
    /// </summary>
    public int VideoFrameRateLimit { get; set; } = 30;

    /// <summary>Who converts the decoded frame to RGB (see <see cref="VideoPixelMode"/>).</summary>
    public VideoPixelMode VideoPixelMode { get; set; } = VideoPixelMode.DecoderConvertsToRgb;

    public WallpaperStretch Stretch { get; set; } = WallpaperStretch.Fill;

    public string BackgroundColor { get; set; } = "#000000";

    public bool MuteVideo { get; set; } = true;

    public int VideoVolume { get; set; }

    public WallpaperSource Clone()
    {
        return (WallpaperSource)MemberwiseClone();
    }

    public string Describe()
    {
        switch (Kind)
        {
            case WallpaperKind.Image:
                return string.IsNullOrWhiteSpace(FilePath) ? "image (no file selected)" : "image " + FilePath;
            case WallpaperKind.Slideshow:
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "slideshow {0} every {1}s",
                    string.IsNullOrWhiteSpace(FolderPath) ? "(no folder selected)" : FolderPath,
                    SlideshowIntervalSeconds);
            case WallpaperKind.Video:
                return string.IsNullOrWhiteSpace(FilePath) ? "video (no file selected)" : "video " + FilePath;
            case WallpaperKind.VideoFolder:
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "video folder {0} ({1})",
                    string.IsNullOrWhiteSpace(FolderPath) ? "(no folder selected)" : FolderPath,
                    VideoOrder);
            default:
                return "solid colour " + BackgroundColor;
        }
    }

    public override string ToString()
    {
        return Describe();
    }
}
