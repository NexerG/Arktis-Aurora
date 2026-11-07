# Decision — Arctis.Imaging decodes ICO/CUR, PNM/PAM, QOI, farbfeld, PCX, XBM and XPM

**Date:** 2026-11-07
**Scope:** `Arctis.Imaging` — `IcoDecoder`, `PnmDecoder`, `QoiDecoder`, `FarbfeldDecoder`, `PcxDecoder`, `XbmDecoder`, `XpmDecoder`, `BmpDecoder`, `ImageCodec`, `ImageFormat`; `ArctisAurora.Core.Imaging.ImageFiles`; `ArctisAurora.Core.Filing.ClipboardImage`

## What changed
- Scope chosen by the user from the roadmap (2026-11-06/07): the "simple rasters" item plus ICO/CUR. Decoders only in this go; the encoders followed the same day in [[simple-raster-encoders]]. (An earlier version of this note claimed ImageSharp 3.1 had ICO/CUR encoders; retracted, 3.1.11 has PBM and QOI encoders but no ICO/CUR at all.)
- `ImageFormat` gains, appended after `Webp`: `Ico` (ICO and CUR), `Pnm`, `Qoi`, `Farbfeld`, `Pcx`, `Xbm`, `Xpm`, `JpegLs` (JPEG-LS is in [[lossless-jpeg]]).
- New internal static classes, each with `TryReadHeader(ReadOnlySpan<byte>, out ImageHeader)` and `Decode(ReadOnlySpan<byte>, Span<byte> destination, int pitch, PixelLayout)`: `IcoDecoder`, `PnmDecoder`, `QoiDecoder`, `FarbfeldDecoder`, `PcxDecoder`, `XbmDecoder`, `XpmDecoder`.
- `BmpDecoder`: new internal `DecodeIcon(dib, destination, pitch, layout)`; `Decode` forwards to a private core `Decode(…, bool icon)`; new private `ApplyMask`.
- `ImageCodec.TryIdentify` probes after WebP, in this order: Qoi, Farbfeld, Xpm, Ico, Pnm by magic; Pcx and Xbm only when they are the `hint` (same rule as Tga). `ImageCodec.Decode` has a case per format.
- `ImageFiles.HintOf`: `.pcx` gives Pcx, `.xbm` gives Xbm, alongside `.tga`.
- `ClipboardImage.imageExtensions` adds `.ico .cur .pbm .pgm .ppm .pnm .pam .qoi .ff .pcx .xbm .xpm .jls`.
- Tests in `ArctisImaging.Tests`: `IcoTests`, `PnmTests`, `QoiTests`, `FarbfeldTests`, `PcxTests`, `XbmTests`, `XpmTests`. Fixtures in `ArctisImaging.Tests/Fixtures` (Ppm/Pgm/Pbm*, Pcx*, Qoi*, XbmBits.xbm, IcoPng.ico, IcoBmp.ico), each with a reference .png beside it.
- All decoders are scalar, a first pass: **unoptimised**, never profiled (no ratio measured). No data XML touched.

## Why these choices

**Above 8 bits the high byte is kept; below 8 bits samples scale to 0-255 as `(v*255 + max/2)/max`.**
`PixelLayout` has only 8-bit layouts, so this follows the TIFF rule in [[other-raster-formats]]. Netpbm scales from any maxval with the same formula; farbfeld keeps the high byte.

**ICO and CUR share one `ImageFormat.Ico`.**
Nothing downstream would treat them differently. The CUR hotspot is not exposed.

**ICO is recognised by magic.**
`00 00 01 00` or `00 00 02 00`, a count of at least 1, and every entry's offset and length inside the file. A hint-only rule would have been weaker for a format that does have a signature.

**ICO entry selection: largest area, then most bits per pixel, read from each entry's own header.**
That is the PNG IHDR or the DIB header with its height halved, not the directory bytes, which are unreliable (0 means 256, writers leave them wrong). This departs slightly from the plan's wording. A PNG entry scores channels times bit depth.

**ICO DIB entries: AND mask applied only when the 32-bit alpha is zero everywhere.**
The header holds twice the height; a 1-bit AND mask follows the colour rows bottom-up, a set bit means transparent and the RGB is kept. A 32-bit entry keeps its own alpha (Windows behaviour, same rule as `BmpDecoder`). A mask that runs past the end is ignored.

**PNM is recognised by magic.**
`P1`-`P7` followed by whitespace, accepted only if the whole header parses. Requiring the extension hint was rejected. PBM 1 means black. PAM `BLACKANDWHITE` is grey, so 1 means white, as the spec says. Only the first image in a file is decoded. Comments are skipped in the header and in plain rasters.

**PCX and XBM are recognised only with the extension hint, like TGA.**
A PCX file's first bytes (0x0A, version, encoding) are too weak a signature, and XBM is C text with no magic.

**PCX details.**
RLE runs may carry over between scanlines. 8-bit single-plane files use the 256-colour palette after the 0x0C trailer, or grey if there is none. 1-bit with 2-4 planes and 2/4-bit single-plane files use the 16-entry header palette. 1-bit single-plane is black and white.

**XBM: a set bit is black, the X11 convention.**
Pillow reads a set bit as white, so the XbmBits fixture's reference PNG is Pillow's decode inverted. Both X11 `char` and X10 `short` arrays are read, bits least significant first.

**XPM: XPM3 only; a named colour throws `NotSupportedException` (user choice, 2026-11-07).**
Colour key preference is `c`, then `g`, then `g4`, then `m`. Accepted values: `#RGB`, `#RRGGBB`, `#RRRGGGBBB`, `#RRRRGGGGBBBB`, and `None` (transparent). The rejected alternative was embedding the X11 rgb.txt table (about 750 entries). Keys of 1-2 characters per pixel use a flat direct table; 3-8 use a `Dictionary<ulong, uint>`; more than 8 is rejected.

**Test oracle: Pillow 13 wrote the PNM, PCX, QOI, XBM and ICO fixtures** (oracle choice for the JPEG family is in [[lossless-jpeg]]).
Pillow clips 16-bit grey when converting to RGBA, so the `Pgm16` reference is the 8-bit grey the file was made from.

## Verification
- ArctisImaging.Tests 348/348 (shared with [[lossless-jpeg]]); every fixture decodes byte-identical to its reference.
- Scratch cross-check (not committed): 60 Pillow-written QOI/PCX/PPM/PGM files byte-identical to Pillow's decodes.
- Solution builds clean, no new warnings. `_Build/test.sh`: 509 passed, 8 failed, 0 new (Boot sampler error and 7 LatinModern Tex.* goldens, both in the baseline). No `[Vulkan]` lines.

## Known gaps
- Not GUI-verified: paste or drop of any new format into a Thorium note.
- Plain PBM/PGM/PPM, PAM, CUR, 4-bit/1-bit icon DIBs, PCX EGA planar, XPM and farbfeld were checked only against hand-built files in the tests, not an external tool.
- Encoders: see [[simple-raster-encoders]].
- XPM named colours unsupported.
- No EXIF orientation. PFM (float PNM) is left to the HDR roadmap item. Only the first image in a multi-image PNM is decoded.
- Performance not measured at all.

Related: [[other-raster-formats]], [[jpeg-gif-decoders]], [[lossless-jpeg]], [[simple-raster-encoders]]
