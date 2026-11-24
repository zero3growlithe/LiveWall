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

    /// <summary>
    /// Which drawing stack paints the wallpaper (see <see cref="RendererPreference"/>). DirectComposition is
    /// the default because it is the only path the shell composes on Windows 11 24H2+ (build 26100+).
    /// </summary>
    public RendererPreference Renderer { get; set; } = RendererPreference.Composition;

    /// <summary>
    /// How the DirectComposition visual reacts to the display scaling (see <see cref="WallpaperScaleMode"/>).
    /// Only used by the composition renderer.
    /// </summary>
    public WallpaperScaleMode WallpaperScale { get; set; } = WallpaperScaleMode.MatchDisplayScaling;

    /// <summary>
    /// Which WPF render mode the WPF fallback surfaces use. Only consulted when
    /// <see cref="Renderer"/> is one of the WPF modes.
    /// </summary>
    public RenderModePreference RenderMode { get; set; } = RenderModePreference.Hardware;

    /// <summary>How the wallpaper window is created (see <see cref="SurfaceCreationMode"/>).</summary>
    public SurfaceCreationMode SurfaceCreation { get; set; } = SurfaceCreationMode.ChildAtCreation;

    /// <summary>Result of the last automatic composition check, or empty when it never ran.</summary>
    public string CompositionCheckResult { get; set; } = string.Empty;

    /// <summary>True once a check proved that the wallpaper reaches the screen.</summary>
    public bool CompositionVerified { get; set; }

    /// <summary>How many automatic combinations were already tried (0 = hardware + child at creation).</summary>
    public int CompositionRetryStage { get; set; }

    // Saved settings-window placement, so the window reopens where it was left instead of centred.
    public double? WindowLeft { get; set; }

    public double? WindowTop { get; set; }

    public double? WindowWidth { get; set; }

    public double? WindowHeight { get; set; }

    /// <summary>
    /// Wallpaper to use for a monitor. When <see cref="SameForAllMonitors"/> is on the shared definition wins
    /// for every monitor, even if per-monitor entries exist from an earlier session — otherwise editing the
    /// shared setup changed nothing on screen and every monitor had to be edited on its own.
    /// </summary>
    public WallpaperSource SourceForMonitor(string deviceName)
    {
        if (SameForAllMonitors)
        {
            return Shared;
        }

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
