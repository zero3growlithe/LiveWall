using System;
using System.Collections.Generic;

namespace LiveWall.Models;

/// <summary>
/// Persisted application configuration (%LocalAppData%\LiveWall\settings.json).
/// </summary>
public sealed class WallpaperSettings
{
    /// <summary>When true every monitor uses <see cref="Shared"/> and the per-monitor map is ignored.</summary>
    public bool SameForAllMonitors { get; set; } = true;

    public WallpaperSource Shared { get; set; } = new WallpaperSource();

    /// <summary>Per-monitor definitions keyed by the monitor device name (\\.\DISPLAY1).</summary>
    public Dictionary<string, WallpaperSource> PerMonitor { get; set; } = new Dictionary<string, WallpaperSource>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Pause the wallpaper of a monitor that shows a maximized window.</summary>
    public bool PauseWhenMaximized { get; set; } = true;

    /// <summary>Pause every monitor while a fullscreen application has the focus.</summary>
    public bool PauseWhenFullscreen { get; set; } = true;

    public bool StartAutomatically { get; set; } = true;

    public bool StartMinimized { get; set; } = true;

    public bool StartWithWindows { get; set; }

    // Saved settings-window placement, so the window reopens where it was left instead of centred.
    public double? WindowLeft { get; set; }

    public double? WindowTop { get; set; }

    public double? WindowWidth { get; set; }

    public double? WindowHeight { get; set; }

    /// <summary>Wallpaper to use for a monitor (falls back to the shared definition).</summary>
    public WallpaperSource SourceForMonitor(string deviceName)
    {
        if (!string.IsNullOrEmpty(deviceName) && PerMonitor.ContainsKey(deviceName))
        {
            return PerMonitor[deviceName];
        }

        return Shared;
    }

    /// <summary>Per-monitor definition, created as a copy of the shared one on first use.</summary>
    public WallpaperSource GetOrCreateMonitorSource(string deviceName)
    {
        if (string.IsNullOrEmpty(deviceName))
        {
            return Shared;
        }

        if (PerMonitor.ContainsKey(deviceName))
        {
            return PerMonitor[deviceName];
        }

        WallpaperSource created = Shared.Clone();
        PerMonitor[deviceName] = created;
        return created;
    }
}
