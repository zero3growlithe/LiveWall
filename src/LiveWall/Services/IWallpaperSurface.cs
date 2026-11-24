using System;
using LiveWall.Models;

namespace LiveWall.Services;

/// <summary>
/// One wallpaper surface for one monitor. Two implementations exist: <see cref="CompositionSurface"/> (the
/// DirectComposition presenter) and <see cref="WallpaperSurface"/> (the older WPF one). The engine only ever
/// talks to this interface, so the rendering stack can be swapped without touching the pause logic.
/// </summary>
public interface IWallpaperSurface : IDisposable
{
    event EventHandler? DescriptionChanged;

    string DeviceName { get; }

    string ActiveDescription { get; }

    string AttachFailureReason { get; }

    string HostDescription { get; }

    string CreationMode { get; }

    bool IsAttached { get; }

    bool IsPaused { get; }

    bool IsVideoOpen { get; }

    bool Attach(DesktopHost host, RECT monitorRectangle, RECT hostRectangle, SurfaceCreationMode creationMode);

    void ApplySource(WallpaperSource config);

    void SetPaused(bool paused);

    string DiagnosticLine();
}
