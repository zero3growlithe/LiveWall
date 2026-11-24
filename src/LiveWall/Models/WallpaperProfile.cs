using System;
using System.Collections.Generic;
using System.Globalization;

namespace LiveWall.Models;

/// <summary>
/// A named wallpaper setup covering every monitor at once, saved to
/// %LocalAppData%\LiveWall\profiles\&lt;name&gt;.json.
/// <para>
/// Only the decor is stored: which wallpaper each monitor shows, whether one wallpaper is shared, and the
/// slideshow and video options. Renderer choice and the window placement describe the application rather
/// than the setup, so a profile never overwrites them.
/// </para>
/// </summary>
public sealed class WallpaperProfile
{
    public string Name { get; set; } = string.Empty;

    public bool SameForAllMonitors { get; set; } = true;

    public WallpaperSource Shared { get; set; } = new WallpaperSource();

    public Dictionary<string, WallpaperSource> PerMonitor { get; set; } =
        new Dictionary<string, WallpaperSource>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Copies the current wallpaper setup out of the live settings, so later edits cannot touch it.</summary>
    public static WallpaperProfile From(WallpaperSettings settings)
    {
        WallpaperProfile profile = new WallpaperProfile();
        profile.Name = string.Empty;
        profile.SameForAllMonitors = settings.SameForAllMonitors;
        profile.Shared = settings.Shared.Clone();
        profile.PerMonitor = new Dictionary<string, WallpaperSource>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, WallpaperSource> pair in settings.PerMonitor)
        {
            profile.PerMonitor[pair.Key] = pair.Value.Clone();
        }

        return profile;
    }

    /// <summary>Applies the profile onto the live settings, replacing every wallpaper definition.</summary>
    public void CopyTo(WallpaperSettings settings)
    {
        settings.SameForAllMonitors = SameForAllMonitors;
        settings.Shared = Shared == null ? new WallpaperSource() : Shared.Clone();
        settings.PerMonitor = new Dictionary<string, WallpaperSource>(StringComparer.OrdinalIgnoreCase);
        if (PerMonitor == null)
        {
            return;
        }

        foreach (KeyValuePair<string, WallpaperSource> pair in PerMonitor)
        {
            if (pair.Value != null && !string.IsNullOrEmpty(pair.Key))
            {
                settings.PerMonitor[pair.Key] = pair.Value.Clone();
            }
        }
    }

    /// <summary>Short description for the profile list.</summary>
    public string Describe()
    {
        if (SameForAllMonitors)
        {
            return "shared: " + Shared.Describe();
        }

        return PerMonitor.Count.ToString(CultureInfo.InvariantCulture) + " monitors";
    }
}
