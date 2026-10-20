# Attribution

The packet framing, album/header layout, and serial command implementation are a Windows C# port informed by [creations/hm-hub](https://heliopolis.live/creations/hm-hub), revision `f520612f26`, licensed **AGPL-3.0-or-later**.

This HMLab-Hub-Control implementation is licensed under the same terms; see `LICENSE`. Its source and build script are included beside the executable. Other utilities in the parent folder are outside this license declaration.

Readback was checked against the [official 1.3.7 driver](https://hmlab.tech/hub/1.3.7/) and corrected to use subcommand 4; upstream's subcommand 3 clears media in that driver. See `PROTOCOL.md` for primary-source evidence and differences. Vendor web code is reference material and is not included in the executable.

Live streaming follows the protocol described by HMLab's `Z-Neo HUB-8K.js` (Jacky, V0.3, 2025-06-24). The JavaScript is not executed or embedded; the native implementation constructs the RGB888 chunk layout independently. The report format is documented in `PROTOCOL.md`.

## FFmpeg

Video decoding, cropping, playback previews and GIF export use [FFmpeg](https://ffmpeg.org/), copyright (c) 2000-2026 the FFmpeg developers. FFmpeg is a separate command-line program; the controller does not link its libraries. FFmpeg and its dependencies retain their own licenses.

Windows releases bundle the unmodified **FFmpeg 9.0.2-tas** executables and required shared libraries from [NevermindNilas/TAS-FFMPEG v9.0.2](https://github.com/NevermindNilas/TAS-FFMPEG/releases/tag/v9.0.2). Thanks to the FFmpeg contributors and the TAS-FFMPEG maintainer for the software, Windows builds and corresponding-source materials.

This build identifies itself as **GPL-2.0-or-later**, includes GPL components such as x264 and x265, and does not enable nonfree components. This is the license of this specific build, rather than a claim that all FFmpeg builds have the same license. The version, download URLs and SHA-256 checksums are pinned in `packaging/ffmpeg.json`.

The release ZIP preserves the complete upstream `licenses/` directory under `licenses/ffmpeg/`, including FFmpeg's GPL and LGPL texts and all supplied dependency notices. The upstream build manifest, configuration and our source-download notice are also included there. Executables and DLLs are copied without modification.

The matching `tas-ffmpeg-9.0.2-corresponding-source.tar.xz` is mirrored as an asset beside the Windows ZIP in every release produced by `Package.ps1`. It contains FFmpeg, dependency sources, version pins and build scripts. Download it from the same [HMLab Hub Control release](https://github.com/judahx67/HMLab-Hub-Control/releases) as your binary package. Checksums cover both downloads. The upstream copy remains available in the [TAS-FFMPEG release](https://github.com/NevermindNilas/TAS-FFMPEG/releases/tag/v9.0.2).
