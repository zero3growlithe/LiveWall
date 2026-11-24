# LiveWall

Lightweight animated desktop wallpaper for Windows 10/11. It draws images, slideshow folders or
looping videos **behind the desktop icons** (no Explorer replacement, no driver, no background service),
lets every monitor have its own wallpaper, and automatically pauses playback while a monitor is covered.

## Features

- **Replace the Windows wallpaper** with
  - a still image,
  - a slideshow folder (optional subfolders, shuffle, 5–600 s interval, cross-fade),
  - a looping video (muted by default, volume configurable),
  - or a solid colour (used as the fallback while an image/video is loading or missing).
- **Multi-monitor**: either one wallpaper for every monitor, or a separate wallpaper per monitor
  (`Display 1`, `Display 2`, … each with its own source, scaling and interval).
- **Pause when covered** (the point of the app):
  - a monitor that shows a **maximized** (or fullscreen-covering) window pauses **that monitor only** —
    the other monitors keep playing;
  - a **fullscreen application that has the focus** pauses **every** monitor.
  - Both rules are toggleable, and every paused monitor explains *why* in its status line
    (e.g. `Paused — maximized window: chrome.exe (video loop.mp4)`).
- **Scaling**: fill (crop), fit (letterbox), stretch, or original size centred.
- **Tray icon** with `Settings…`, `Pause all` / `Resume all` and `Exit`; closing the window hides to tray.
- **Start with Windows** (HKCU `Run`, registered with the `--tray` switch so a manual launch always
  opens the settings window while an autostart launch goes straight to the notification area).
- Single instance, per-monitor DPI aware, settings and log in `%LocalAppData%\LiveWall\`.
- The settings window remembers its position and size across launches, and the 500 ms coverage scan runs
  on a worker thread so it can never stall the UI.

## Controls

| Control | Meaning |
| --- | --- |
| `Apply` | Pushes the current editor state to the desktop and saves it. |
| `Pause all` / `Resume all` | Manual override, independent of the automatic detection. |
| `Hide to tray` | Keeps the wallpaper running, removes the window. |
| `Same wallpaper on every monitor` | Shared configuration; uncheck to edit each monitor separately. |

## How it works

1. `DesktopWindowService` sends `0x052C` to `Progman` so Explorer spawns the `WorkerW` that lives behind
   the icons, then resolves that window (Windows 10: the `WorkerW` after `SHELLDLL_DefView`;
   Windows 11: the `WorkerW` inside `Progman`; last resort: `Progman` itself).
2. One `WallpaperSurface` (an `HwndSource` with a `WS_CHILD | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`
   window holding a `Grid` with two `Image` layers and a `MediaElement`) is parented into that host per
   monitor and positioned exactly on the monitor rectangle in physical pixels.
3. `FullscreenWatcher` runs every 500 ms:
   - foreground window covers its whole monitor rectangle → pause everything;
   - otherwise every visible, non-cloaked top-level window that is maximized (or covers its monitor)
     pauses the monitor it lives on. Shell windows, our own windows and virtual-desktop-cloaked windows
     are ignored, and Windows' own `SHQueryUserNotificationState` full-screen signal is used as a
     second opinion for exclusive-fullscreen games.
4. Explorer restarts and monitor hot-plugs are detected and the surfaces are rebuilt automatically.

## Requirements

- Windows 10 or 11 (x64).
- .NET 8 Desktop Runtime (the published build is framework-dependent, like the other apps here).

## Build

GitHub Actions is the build authority (`.github/workflows/build.yml`): `windows-latest`, .NET 8,
`dotnet build` + `dotnet publish -r win-x64 --self-contained false`, zipped as `LiveWall-win-x64.zip`
artifact.

## Notes

- Playback stops only for slideshows and videos — a still image has nothing to pause.
- Video decoding uses WPF's `MediaElement` (Media Foundation), so anything Windows Media Player can play
  (H.264 MP4, WMV, …) works; exotic containers may not.
- The desktop remains fully interactive: the surfaces never take focus, clicks or Alt+Tab slots.
- Diagnostics: `%LocalAppData%\LiveWall\crash.log` (attach failures, Explorer restarts, media errors).
