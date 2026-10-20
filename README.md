# HMLab Hub Control

Unofficial Windows wallpaper controller for HMLab Z-NEO hubs. It runs offline and includes a video/GIF cropper. Tested with firmware 1.3.7.

- **Specs:** Local media library, selected-file uploads, four screen orientations, folder playlists and a 70-frame cropper with FFmpeg included in the Windows release.
- **How:** Run `HMLab-Hub-Control.cmd`; the app detects the connected hub and reads its orientation automatically, with manual port selection available.
- **Why:** Manage hub wallpapers without opening the web driver, and keep a library larger than the hub's storage.

## Run

Requires Windows, Windows PowerShell 5.1 and .NET Framework 4.5 or later. The build uses the .NET Framework compiler included with Windows; Visual Studio is not required.

For a ready-to-run copy, download the Windows ZIP from [Releases](https://github.com/judahx67/HMLab-Hub-Control/releases), extract the whole folder and run **HMLab-Hub-Control.exe**. The ZIP includes the cropper, FFmpeg, ffprobe, their required DLLs and the controller source; no installer or separate FFmpeg setup is needed. The bundled tools require 64-bit Windows 10 or later.

Double-click **HMLab-Hub-Control.cmd**. It builds the executable when needed and starts the app. To build manually:

```powershell
.\Build.ps1
.\HMLab-Hub-Control.exe
```

When running from a source checkout, the cropper needs `ffmpeg.exe` and `ffprobe.exe` on PATH or beside `tools/Gif70.ps1`. Release packages already supply these files and their DLLs in `tools/`. They are not required for ordinary wallpaper uploads. **Crop-Gif.cmd** opens the cropper on its own.

Keep `tools/` with the executable when moving the app. The cropper starts only when opened, and the playlist panel loads only when its tab is selected.

Media conversion and preview use [FFmpeg](https://ffmpeg.org/), copyright the FFmpeg developers. The bundled FFmpeg 9.0.2 build is supplied by [TAS-FFMPEG](https://github.com/NevermindNilas/TAS-FFMPEG/releases/tag/v9.0.2) under **GPL-2.0-or-later**; its dependencies retain their own licenses. Credits, license texts and build details are included in `licenses/ffmpeg/`. The matching complete source archive, including dependencies and build scripts, is a separate download beside the Windows ZIP in the same release. See [THIRD-PARTY.md](THIRD-PARTY.md).

## Upload a wallpaper

1. Disconnect the hub in its web driver and close any other controller using its COM port.
2. The app selects a connected HMLab hub by its USB ID and reads its identity and screen orientation after opening; select another port and click **Detect hub** to retry or override it.
3. If needed, choose **Normal (0°)**, **Upside down (180°)**, **Sideways (90°)** or **Sideways (270°)** and click **Apply**.
4. Use **Add files** to copy GIFs or images into the library. Select one and choose **Fit** or **Fill**.
5. Click **Upload selected media**.

The hint above Upload explains any missing step. Detecting the hub confirms its current orientation; changing the dropdown requires Apply before uploading.

Automatic detection runs once at startup in the background and only opens a port identified as an HMLab hub, regardless of its COM number. If the hub is missing or busy, the status explains how to retry without an error popup. It does not change settings or wallpapers.

Uploads replace the hub's entire stored album with the selected file, then select wallpaper display. The app backs up the previous album in `backups/` and verifies the upload by reading it back. **Skip backup** and **Skip verification** are separate toggles, both off at startup. Cancelling after erasure starts can leave an incomplete album; upload again to replace it.

If wallpaper display is already selected, completing an upload requires no further settings requests. A failure to select that page after a completed upload is reported separately from transfer or verification failures.

The library lives in `media/` beside the executable. Imports keep the original files, rename conflicting copies and reuse identical copies. Remove deletes the library copy after confirmation. Supported formats: GIF, PNG, JPG/JPEG and BMP.

The hub's capacity is read from the device. An 8 MiB hub holds 77 native frames. GIFs keep their frame count but use one average frame delay, as required by the device format. Sideways artwork is prepared on a portrait canvas and rotated into the same 320 x 170 upload buffer. The app does not update firmware.

## Crop and preview

**Crop tool** opens the selected library file. **Open video / GIF** can choose another source. Each completed export is imported and selected automatically; a cancelled or failed export is not imported. The save dialog starts in the library folder. Files saved elsewhere are copied into the library.

- Drag either trim handle to select an interval. Numeric times and **Start/End here** give precise control.
- Move the crop box or resize its bottom-right grip. Width and height controls also resize it.
- Choose landscape (17:9) or portrait (9:17), and rotate the source left or right. Rotation and layout changes reset the crop while keeping the trim.
- **Play/Pause** loops the same 70 samples used for export. **Cropped preview** shows only the result; turn it off to drag the crop box. Numeric crop controls work in either view.
- **Stop** returns to the trim start and releases the preview frames. Changing the trim or source clears the preview cache. Playback is silent.
- Output dimensions scale up or down together. Default sizes are 425 x 225 landscape and 225 x 425 portrait.

Export samples the whole selected interval into exactly 70 frames. Selections shorter than 1.4 seconds play over 1.4 seconds for reliable GIF timing. Playback previews use reduced-resolution frames; exports use the full source.

## Folder playlists

Choose a folder, playback mode, order and display time, then **Start playlist**. Files can play by filename, modified date or shuffle without repeats within a cycle. Only files directly in the folder are scanned; changes are picked up at the next transition. Playback continues while the window is minimized and never starts automatically.

**Full resolution** uploads and verifies each file before starting its display timer. Transitions take about a minute for a 70-frame animation on the tested hub. Each file must fit the hub, but the folder can be larger. The original album is backed up once per session. Stop, Esc and closing the window finish an active transfer before stopping, leaving the last complete wallpaper on the hub.

**Live USB** streams a 32 x 17 grid for faster transitions without wallpaper flash writes. It is experimental and visibly lower resolution. Full-resolution streaming is not available in the known protocol. Next advances immediately; Stop restores the previous display page. If a session is interrupted, use **Restore display** after reconnecting. Use one hub at a time for live mode and disable its SignalRGB controller while this app is streaming.

Playlist options are stored in `state/`. Media, backups, settings, test output and compiled executables are excluded from Git.

## Checks

These checks use generated media and a simulated hub; they do not open USB devices:

```powershell
.\Test.ps1           # controller checks; no FFmpeg required
.\Test.ps1 -Cropper  # also check the cropper; requires FFmpeg
```

The controller checks cover transfer framing, readback failures, cancellation, configuration preservation, sideways packing, library imports and orientation guidance. The cropper checks cover trimming, rotation, frame count, playback and export handoff. Reports and UI captures are written under `.checks/`.

Protocol details are in [PROTOCOL.md](PROTOCOL.md). This project uses **AGPL-3.0-or-later**; see [LICENSE](LICENSE) and [THIRD-PARTY.md](THIRD-PARTY.md).

## Package a release

From a clean, committed checkout, run `./Package.ps1 -Version 0.1.3`. The script downloads the checksum-pinned FFmpeg build and corresponding source, preserves its notices, builds the controller and checks the packaged controller and cropper with FFmpeg removed from PATH. It writes the Windows ZIP, corresponding-source archive and `SHA256SUMS.txt` under `.checks/packages/`. Publish all three files together; the source archive is for redistribution and is not needed to run the app. Downloads are cached under `.checks/ffmpeg-downloads/` and never committed.
