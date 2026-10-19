# Attribution

The packet framing, album/header layout, and serial command implementation are a Windows C# port informed by [creations/hm-hub](https://heliopolis.live/creations/hm-hub), revision `f520612f26`, licensed **AGPL-3.0-or-later**.

This HMLab-Hub-Control implementation is licensed under the same terms; see `LICENSE`. Its source and build script are included beside the executable. Other utilities in the parent folder are outside this license declaration.

Readback was checked against the [official 1.3.7 driver](https://hmlab.tech/hub/1.3.7/) and corrected to use subcommand 4; upstream's subcommand 3 clears media in that driver. See `PROTOCOL.md` for primary-source evidence and differences. Vendor web code is reference material and is not included in the executable.

Live streaming follows the protocol described by HMLab's `Z-Neo HUB-8K.js` (Jacky, V0.3, 2025-06-24). The JavaScript is not executed or embedded; the native implementation constructs the RGB888 chunk layout independently. The report format is documented in `PROTOCOL.md`.
