# LiveWall

Lightweight animated desktop wallpaper for Windows 10/11. It draws images, slideshow folders or
looping videos **behind the desktop icons** (no Explorer replacement, no driver, no background service),
lets every monitor have its own wallpaper, and automatically pauses playback while a monitor is covered.

## Features

- **Replace the Windows wallpaper** with
  - a still image,
  - a slideshow folder (optional subfolders, shuffle, 5–600 s interval, cross-fade),
  - a looping video (muted by default, volume configurable),
  - a **video folder** played as a playlist — sequential (name order, then back to the first),
    random sequence (every clip once, reshuffled on each pass) or random loop (one random clip, looped
    from the start of the session); optional subfolders, and muted volume settings that apply to all of them,
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
- **Scaling**: fill (crop), fit (letterbox), stretch, or original size centred. A black band that arrives
  *inside* a decoded video frame is cropped off before drawing: the band is part of the frame data, so no
  scaling mode can hide it. Whatever a mode leaves uncovered is painted with the wallpaper's background colour,
  so the previous Windows wallpaper never shows through around a centred or fitted picture.
- **Skip by hand**: a `Skip to the next picture / video` button in the settings window steps a slideshow or a
  video folder forward immediately (a single random-loop folder draws a new random clip).
- **Profiles**: shown at the top of the window; save the current setup — every monitor at once — under a name and
  load it later (picking a profile in the list copies its name into the name box, so `Save` overwrites it)
  (`%LocalAppData%\LiveWall\profiles\<name>.json`). The renderer choice and the window placement are not part
  of a profile: they describe the app, not the decor.
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
| `Skip to the next picture / video` | Steps a slideshow or video folder forward at once. |
| `Wallpaper scale` | `pixels as DIPs (no DPI adjustment)` is the default and the setting that draws 1:1 on a 100 % display. |

## CPU

A wallpaper video has to be decoded, and decoding is the expensive part: on a 1080p clip the decoder alone
can take a fifth of a core, and asking Media Foundation for RGB adds a whole extra full-frame pass over every
frame. Two settings keep it in check, both in the `VIDEO` block:

- **Frame rate** — `30 fps` by default. Every decoded frame still costs its decode, but only the ones that
  are actually shown cost an upload and a present, so a 60 fps clip costs half of that half.
- **Convert colours in LiveWall** — off by default. When on, LiveWall asks the decoder for its native NV12
  output and converts it itself, which removes Media Foundation's video-processor stage from the graph. If
  the decoder offers nothing convertible, LiveWall logs the fallback and goes back to RGB by itself.

`Diagnostics…` prints a `video cost=` figure per monitor (`fps`, decode ms, present ms per frame, and the
share of one core that adds up to), so the effect of both settings is measurable instead of assumed.
A covered monitor also releases its decoder after 15 seconds, and the clip restarts when the monitor comes
back — no decoder, no CPU and no tens of megabytes of reference frames while nobody is looking.

## Memory

The renderer keeps **one** decoded frame buffer, one crop buffer and a pair of image buffers per monitor and
re-uses them. That matters at 4K: a frame is 33 MB, so allocating a fresh one per video frame or per slide
would hand the large object heap gigabytes of garbage a minute and the process would climb without settling.
Slideshow advances additionally ask the runtime to compact the large object heap once, which is what returns
the previous picture to Windows. The settings window is only built when it is opened, so an autostart
tray-only run does not pay for a WPF visual tree at all.
`Diagnostics…` reports working set, private bytes, managed heap size and the three GC counters, so the
footprint can be measured instead of felt.

## How it works

1. `DesktopWindowService` sends `0x052C` to `Progman` and then detects which desktop layout the shell
   currently exposes, because the two are not interchangeable:
   - **classic** (Windows 10, 11 up to 23H2): the icons move into a top-level `WorkerW` and an empty
     top-level `WorkerW` appears behind them — that one becomes the parent;
   - **raised desktop** (Windows 11 24H2+, build 26100+): the icons stay inside `Progman`, the wallpaper is
     painted by a `WorkerW` **child** of `Progman`, and `Progman` itself is created with
     `WS_EX_NOREDIRECTIONBITMAP`. The wallpaper window is parented into that `WorkerW` child and inserted
     directly below `SHELLDLL_DefView`, so icons stay on top. When the `WorkerW` appears later, the surfaces
     are re-attached into it automatically.
2. **The wallpaper is drawn with DirectComposition**, not with WPF: one child window per monitor
   (a `Static` window created with `WS_EX_NOREDIRECTIONBITMAP`) carries a `IDCompositionTarget` /
   `IDCompositionVisual` tree whose content is a **D3D11 flip-model composition swapchain**. Frames are
   uploaded as BGRA8 (`UpdateSubresource` → `CopyResource` → `Present`) and the visual's transform does the
   scaling (fill/fit/stretch/centre), so no shader is involved. On the raised desktop this is the *only*
   arrangement the shell composes — WPF content (hardware **and** software) and plain GDI blits to a child of
   the desktop host never reach the screen there, even though the window is attached at the right place with
   the right size.
3. Video is decoded by a **Media Foundation source reader** (`MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING`,
   RGB32 output) on the surface's render thread, paced against sample timestamps and looped at end of
   stream. Images are decoded with WPF imaging and converted to BGRA8. Playback of a video and the D3D
   context share one dedicated thread per monitor, so neither the UI nor the decoder can block the other.
4. `FullscreenWatcher` decides what is covered every 500 ms: a monitor showing a maximized (or
   fullscreen-covering) window pauses only that monitor, a focused fullscreen app pauses everything. Windows
   that belong to LiveWall itself, tool windows and virtual-desktop-cloaked windows are ignored, and
   `SHQueryUserNotificationState` is used as a second opinion for exclusive-fullscreen games. Pausing stops
   the decoder (the last frame stays on screen) and halts the slideshow timer.
5. Explorer restarts and monitor hot-plugs are detected and the surfaces are rebuilt automatically.
6. `COMPATIBILITY` lets you switch the drawing stack: DirectComposition (default), or the two WPF paths as
   fallbacks for older builds. The app also walks that list by itself on first run, paints magenta, samples
   the desktop pixels to prove whether anything arrived, and keeps the first combination that worked.

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
- Windows 11 24H2/25H2 (build 26100+, including 26200) block ordinary child windows from being composed
  under the desktop. The app handles that topology, and **Compatibility → `Check what the desktop shows`**
  (also run automatically once on first launch) proves whether pixels really reach the screen: it paints
  magenta, samples the monitor back and reports `VISIBLE` / `NOT VISIBLE` per display.
- If that check says `NOT VISIBLE`, the app walks through the four rendering combinations by itself
  (hardware/software × window created in place / popup re-parented), restarting once per combination and
  stopping after the last one, logging every attempt:
  `composition check (hardware rendering + child at creation (stage 0)): …`. The winning combination is
  what stays configured; `Compatibility → Use software rendering` and `Restart LiveWall` set it by hand.
- A window created straight into the desktop host is the normal case: WPF sets its presentation up for a
  child window, which is what the shell composes. Creating a popup and re-parenting it afterwards leaves
  WPF presenting to a surface that is no longer part of that composition — the "attached, playing,
  nothing on screen" case, which is why the in-place creation order comes first.
- The desktop remains fully interactive: the surfaces never take focus, clicks or Alt+Tab slots.
- Diagnostics: `%LocalAppData%\LiveWall\crash.log` (attach failures, Explorer restarts, media errors).
  An attach failure names the host window class and the exact Win32 error `SetParent` returned.
