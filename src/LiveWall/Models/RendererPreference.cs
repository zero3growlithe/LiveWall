namespace LiveWall.Models;

/// <summary>
/// Which drawing stack paints the wallpaper. DirectComposition is the normal case: it is the only thing the
/// shell composes on Windows 11 24H2 and newer (build 26100+), where a window parented into the desktop host
/// never shows WPF (or GDI) content. The two WPF modes are kept as fallbacks for older builds.
/// Changing this needs an application restart.
/// </summary>
public enum RendererPreference
{
    /// <summary>DirectComposition + D3D11 flip-model swapchains. Works on every desktop topology we know of.</summary>
    Composition = 0,

    /// <summary>WPF visuals, GPU accelerated. Classic WorkerW desktops only.</summary>
    WpfHardware = 1,

    /// <summary>WPF visuals, software rasteriser. Classic WorkerW desktops only.</summary>
    WpfSoftware = 2
}
