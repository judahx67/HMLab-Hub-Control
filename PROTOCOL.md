# Device protocol

The implementation follows the [HMLab web driver 1.3.7](https://hmlab.tech/hub/1.3.7/js.js) and [creations/hm-hub](https://heliopolis.live/creations/hm-hub), revision `f520612f26`. The upstream CLI and vendor driver disagree on the host readback opcode; the distinction below matters.

## Serial transport

USB VID/PID: **C019:0401**. Serial: **115200 baud, 8N1**, DTR and RTS asserted. After opening, the host requests identity with command 1. The reply contains little-endian hardware ID, firmware version and flash size as three 32-bit values.

Packets are 256 bytes: command at byte 0, payload at 1 through 251, and little-endian CRC32 at 252 through 255 over the first 252 bytes. CRC uses the reflected IEEE polynomial `0xEDB88320`, initial value `0xFFFFFFFF` and final XOR `0xFFFFFFFF`.

Read continuously, including while idle. The hub sends unsolicited power/status packets (9) and log packets (10); leaving them unread can stall its animation. `SerialLink` drains these and queues command replies separately.

## Configuration

Command 3/subcommand 1 reads configuration. Subcommand 2 writes a chunk containing 24 configuration bytes and their trailing CRC32. Writes are checked by reading the configuration back.

| Byte | Meaning |
| --- | --- |
| 2 | Display page: 2 = wallpapers, 3 = live USB |
| 3 | Orientation: 0 = 0°, 1 = 180°, 2 = 90°, 3 = 270° |
| 4 | Brightness, 0 through 30 |

Read fresh configuration before changing a field. Preserve the other bytes, including reserved values. Applying orientation changes byte 3 only. A successful stored upload selects page 2; live sessions restore only their previous page, preserving settings changed during playback.

Brightness applies the same read/modify/write/readback sequence and changes **byte 4 only**. The saved vendor 1.3.7 driver serializes `screen_brightness` at offset 4 and exposes a slider with `min=1`, `max=30`, `step=1`; this app uses that selectable range. A stored zero is readable but never written by brightness adjustment. Invalid requested levels are rejected before sending any command, and applying the current level skips the settings write. Brightness never uses command 8 or any firmware-update command.

## Stored media

An upload replaces the album collection. The image starts with an 8192-byte header area containing up to 292 consecutive 28-byte album headers, followed by raw frame data.

| Header offset | Type | Meaning |
| --- | --- | --- |
| 0 | u32 | Magic `0xC0190001` |
| 4, 6 | u16, u16 | Width and height |
| 8, 10 | u16, u16 | Frame count and uniform delay in milliseconds |
| 12, 16 | u32, u32 | Absolute pixel-data offset and total length |
| 20 | u32 | Pixel-data CRC32 |
| 24 | u32 | CRC32 over header bytes 0 through 23 |

Header numbers are little-endian. Pixels are row-major RGB565, **high byte first**. Each native 320 x 170 frame uses 108800 bytes. Capacity is `floor((flashBytes - 8192) / 108800)`; 70 frames is the cropper's output rule, not the device's universal limit. The device stores one frame delay per animation, so imports use the average GIF delay.

### Sideways images

Keep upload headers and buffers at **320 x 170**, even for sideways mounting. Firmware 1.3.7 centers media in fixed native LCD bounds. Sending 170 x 320 yields a negative vertical origin that wraps to an invalid unsigned LCD coordinate, producing a black screen even when readback matches.

Render sideways artwork upright at 170 x 320, then rotate it counterclockwise into the native buffer. Firmware's wallpaper scan direction handles normal/180-degree scan; orientation 2 uses normal scan and 3 adds the 180-degree flip. Both sideways settings use the same counterclockwise packing. Local previews undo that packing to show upright artwork. This corrected path has been visually checked on the hub.

### Upload and readback

All operations below use command 8. Offsets are relative to its payload.

1. Host sends subcommand **1**, followed by the total image length as u32. The device erases storage.
2. Device sends subcommand **2**, requesting an offset (u32 at 1) and length (u16 at 5).
3. Host replies with subcommand **2**, the same offset/length, and bytes at payload offset 7. At most 244 data bytes fit. Service the requested ranges and reject out-of-bounds requests.
4. After transfer, host sends subcommand **4** to read back. Incoming subcommand **3** carries offset, length and bytes; incoming **4** ends readback.
5. Require complete expected data and compare every byte before reporting verified success.

**Host subcommand 3 clears stored media.** Do not copy the upstream CLI's readback request, which uses 3. The vendor readback request is 4.

Backups preserve the validated used album prefix, including all referenced frame data. The device need not send unused flash space. Check header CRCs, frame-data CRCs, offsets, dimensions and complete coverage; do not synthesize missing bytes into a backup. Cancelling after erasure starts can leave a partial album.

## Live USB

The vendor SignalRGB script describes a **32 x 17 RGB888** stream, 1632 bytes per frame. The HID endpoint uses usage page `FFC0`, usage `0C00`, interface `MI_00`, report ID `05`. Each frame is split into seven reports with a four-byte header: `05 01 07 <chunk-index>`, then up to 252 pixel bytes. Windows' output report length is 257 bytes, including a trailing pad byte.

The firmware enlarges this grid on the LCD. Increasing host-side frame dimensions does not provide full-resolution streaming. Native-resolution folder playback therefore uses album replacement. Live mode rejects multiple matching hub endpoints rather than choosing one silently.

## Scope

The app does not use firmware-update commands. Protocol and media tests run against a simulator; physical display behavior is checked separately. Downloaded vendor scripts, firmware images, personal media and device backups are not distributed with the source.
