# LiveWall

Lightweight animated desktop wallpaper for Windows 10/11. It draws images, slideshow folders or
looping videos **behind the desktop icons** (no Explorer replacement, no driver, no background service),
lets every monitor have its own wallpaper, and automatically pauses playback while a monitor is covered.

## Features

- **Replace the Windows wallpaper** with
  - a still image,
  - a slideshow folder (optional subfolders, shuffle, 5–600 s interval, cross-fade),
  - a looping video (muted by default, volume configurable),
  - or a solid colour, picked with the same colour dialog SubtitleForge uses (hue/saturation square,
    alpha ramp, presets, hex field — used as the fallback while an image/video is loading or missing).
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
| `Apply` | Pushes the whole settings page to the desktop and saves it (the editor applies itself as you change it). |
| `Diagnostics…` | Desktop topology, per-surface state, what the screen actually shows, plus the visibility test. |
| `Pause all` / `Resume all` | Manual override, independent of the automatic detection. |
| `Hide to tray` | Keeps the wallpaper running, removes the window. |
| `Same wallpaper on every monitor` | Shared configuration; uncheck to edit each monitor separately. |

## How it works

1. `DesktopWindowService` sends `0x052C` to `Progman` and then detects which desktop layout the shell
   currently exposes, because the two are not interchangeable:
   - **classic** (Windows 10, 11 up to 23H2): the icons move into a top-level `WorkerW` and an empty
     top-level `WorkerW` appears behind them — that one becomes the parent;
   - **raised desktop** (Windows 11 24H2+, build 26100+): the icons stay inside `Progman`, a `WorkerW`
     child of `Progman` paints the wallpaper, and `Progman` is created with `WS_EX_NOREDIRECTIONBITMAP`.
     The surface is then parented to `Progman`, slotted directly below `SHELLDLL_DefView`, pushed onto
     the shell's `WorkerW`, and made a **layered** child — without that the shell never composes it;
     `SHELLDLL_DefView` is then hidden/re-shown once so the shell drops the icon-layer snapshot it
     caches across desktop switches.
2. One `WallpaperSurface` (an `HwndSource` holding a `Grid` with a `Border` backdrop, two `Image` layers
   and a video `Rectangle`) is made a child of that host per monitor — `GWL_STYLE` to
   `WS_CHILD | WS_VISIBLE` FIRST, then `SetParent`, then `SetWindowPos` with the host's z-order anchor —
   and positioned exactly on the monitor rectangle in physical pixels relative to the host's client area.
3. Video is painted by a `MediaPlayer` through a `VideoDrawing` brush instead of a `MediaElement`:
   a `MediaElement` expects to own the layout of a normal window, a brush renders wherever it is used.
   The media session is opened once, paused when the monitor is covered, and **closed** (`Stop()` +
   `Close()`) whenever the source changes, so switching to an image really unloads the video.
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
- Video decoding uses `MediaPlayer` (Media Foundation), so anything Windows Media Player can play
  (H.264 MP4, WMV, …) works; exotic containers may not. Every state is reported honestly in the status
  line: `opening…`, `playing (1920x1080, muted, looping)`, or `playback failed: <reason>`.
- Changing anything in the editor (source kind, file, folder, colour, scaling, mute) is applied
  immediately; `Apply` only exists for the settings that are not part of the editor.
- Windows 11 24H2/25H2 block ordinary child windows from being composed under the desktop. The app
  handles that topology, and `Diagnostics…` → **Test visibility** proves whether pixels really reach the
  screen: it paints magenta, samples the monitor back and reports `VISIBLE` / `NOT VISIBLE` per display.
- The desktop remains fully interactive: the surfaces never take focus, clicks or Alt+Tab slots.
- Diagnostics: `%LocalAppData%\LiveWall\crash.log` (attach failures, Explorer restarts, media errors).
  An attach failure names the host window class and the exact Win32 error `SetParent` returned.
