# Decision — Arctis.Imaging encodes BMP/DIB, TGA, ICO/CUR, PNM/PAM and QOI, every variant reachable through a settings struct

**Date:** 2026-11-07
**Scope:** `Arctis.Imaging` — `BmpEncoder`, `TgaEncoder`, `PnmEncoder`, `QoiEncoder`, `IcoEncoder`, `PixelRows`, `ImageCodec`, `Image`, `Types.cs` (settings structs and enums)

## What changed
- New internal static classes: `BmpEncoder` (`Encode`, `EncodeIconEntry`, `BuildPalette` shared with TGA, private `EncodeRle`), `TgaEncoder.Encode`, `PnmEncoder.Encode`, `QoiEncoder.Encode`, `IcoEncoder.Encode(entries, cursor, output, deflate)`.
- `PixelRows.ToRgba(row, rgba, width, layout)`: any layout to an RGBA8 row, inverse of `FromRgba`. `PixelRows` was not in the agreed plan's file list; recorded as a fact only.
- New public types in `Types.cs`:
  - `BmpPixelFormat` (Auto, Indexed1, Indexed4, Indexed8, Rgb555, Rgb565, Argb1555, Argb4444, Rgb24, Xrgb32, Argb32), `BmpHeader` (Auto, Core, Info, V4, V5), `BmpCompression` (Auto, None, Rle, BitFields, AlphaBitFields), `BmpSettings(format, header, compression, topDown)`.
  - `TgaPixelFormat` (Auto, Grey8, GreyAlpha16, Indexed8, IndexedAlpha8, Rgb555, Argb1555, Rgb24, Argb32), `TgaOrigin` (BottomLeft, BottomRight, TopLeft, TopRight), `TgaSettings(format, rle, origin, omitFooter)`.
  - `PnmKind` (Auto, Pbm, Pgm, Ppm, Pam), `PamTupleType` (Auto, BlackAndWhite, BlackAndWhiteAlpha, Grayscale, GrayscaleAlpha, Rgb, RgbAlpha), `PnmSettings(kind, plain, maxVal, tupleType)`.
  - `QoiColorspace` (Srgb, Linear), `QoiSettings(channels, colorspace)`.
  - `IconEncoding` (Png, Bmp), `IconEntry(image, encoding, bmpFormat, hotspotX, hotspotY)`.
- New public `ImageCodec` methods: `EncodeBmp`, `EncodeDib`, `EncodeTga`, `EncodePnm`, `EncodeQoi` (all `(pixels, width, height, rowPitch, layout, output, in XxxSettings settings = default)`), `EncodeIco` and `EncodeCur` (`ReadOnlySpan<IconEntry>, Stream, in DeflateSettings = default`); private `CheckPixels`, `CheckEntries`.
- New `Image` methods `SaveBmp`, `SaveTga`, `SavePnm`, `SaveQoi`, each a Stream and a path overload with the settings parameter. No `Image` method for ICO/CUR/DIB (like KTX2).
- Tests in `ArctisImaging.Tests`: encode tests in `BmpTests`, `TgaTests`, `PnmTests`, `QoiTests`, `IcoTests`. New Pillow 13 fixtures in `ArctisImaging.Tests/Fixtures`: `EncodeSource.qoi`, `EncodeRgba.qoi`, `EncodeSource.ppm`, `EncodeGrey.pgm`, `EncodeSource.tga`, `EncodeRgba.tga`, `EncodeGrey.tga`, `EncodeSourceRle.tga`, `EncodeRgbaRle.tga`, `EncodeSource.bmp`, `EncodeRgba.bmp`, `EncodeGrey.bmp`.
- No data XML read or added. No engine (AuroraEngine) code changed.
- Every variant written is one the existing decoders read.

## Why these choices

**Settings per format, every variant reachable (user, 2026-11-07: "when formats have codecs, i want to allow to mix and mash them as much as people want. they just need to type in the settings").**
Enum value 0 is `Auto`, resolved from the pixel layout (`BmpSettings.Resolve`, `TgaSettings.FormatFor`, `PnmSettings.KindFor/TupleTypeFor`, `QoiSettings.ChannelsFor`); zero settings give the defaults below. Each struct has `Check()` throwing `ArgumentException` on contradictions:
- BMP: RLE only with Indexed4/8 and never top-down; Core header only uncompressed bottom-up indexed or 24-bit, at most 65535 a side; AlphaBitFields only after an Info header; Rgb565/Argb1555/Argb4444 need bit fields; an alpha format with BitFields after an Info header has no alpha mask and throws.
- PNM: no plain PAM; PBM and the black-and-white tuple types need maxval 1; tuple type only for PAM; maxval 1-65535.
- QOI: channels 0, 3 or 4.
- ICO: 1-65535 entries, at most 256 a side; BMP entry formats only Indexed1/4/8, Rgb555, Rgb24, Xrgb32, Argb32; hotspots only on cursors and inside the image.

**Indexed formats use exact palettes only (user choice, 2026-11-07).**
`BmpEncoder.BuildPalette` counts the image's colours (first-seen order, 512-slot open-addressing table) and throws `ArgumentException` above 2^n. BMP palettes drop alpha; TGA `IndexedAlpha8` keeps it in a 32-bit colour map. R8 written as BMP Indexed8 uses the identity 256-entry grey ramp (byte-identical to Pillow's "L" BMP). Rejected: a Wu/median-cut quantizer with optional dithering. It is its own system, and GIF and palette PNG need the same one, so it belongs with the GIF encoder work.

**Defaults (user accepted, 2026-11-07).**
- BMP: Indexed8 grey for R8, otherwise Argb32 in a V5 header with BI_BITFIELDS (alpha kept, as CF_DIBV5 and ImageSharp's `SupportTransparency` do), rows bottom-up.
- TGA: Grey8 for R8, Argb32 otherwise, uncompressed, bottom-left origin, TGA 2.0 footer.
- PNM: P5 for R8/RG8, P6 for RGBA8/BGRA8, alpha dropped. PAM was rejected as the default because RGBA8 is the default layout everywhere, so nearly every image would become PAM, which fewer readers open than P6.
- QOI: 3 channels for R8, 4 otherwise, sRGB.
- ICO/CUR entries: RGBA8 PNG (Pillow's default, smallest, Windows Vista+). BMP entries (with AND mask, alpha 0 to mask bit) are selectable per entry, so the classic mix (PNG at 256, 32-bit BMP at 48, 8-bit at 16) is one call. Entries are written in the caller's order; no size generation or resizing.

**BMP header fields match Pillow.**
3780 px/m, clrUsed = clrImportant = palette count, biSizeImage = data length. A Core header has no colour count, so its palette is padded to 2^bits entries, and `BmpDecoder` reads 2^bits entries after a Core header. Auto compression: Argb32 in an Info header is BI_RGB 32-bit (Pillow-identical); in V4/V5 it is BI_BITFIELDS. 16-bit alpha formats after an Info header use BI_ALPHABITFIELDS. V4/V5 write LCS_sRGB, V5 intent LCS_GM_IMAGES. RLE: runs of 2 or more, literals of 3 or more (shorter as runs of 1), end of line after every row plus end of bitmap.

**Bit-field channels round with `(c*max+127)/255`.**
Our decoders widen by bit replication, Pillow by shifting, so decoded 5/6-bit values differ from Pillow's while the stored bits agree (checked).

**TGA RLE packets never cross a row (TGA 2.0); raw packets run up to the next equal pair.**
Byte-identical to Pillow 13 for its raw and RLE writes.

**QOI follows qoi.h's op order.**
Byte-identical to Pillow 13 with `colorspace="sRGB"` (Pillow's own default header byte is linear). Output is buffered in 64 KB chunks.

**PNM plain lines wrap before 70 columns (PBM bits without separators). Samples scale with `(v*maxval+127)/255`.**

**Measure only, scalar, no optimisation in this go (user accepted; same as the JPEG encoder).**
No AggressiveOptimization (rule 42a's Arctis exception covers decode loops only).

**Speed vs ImageSharp 3.1.11, UNOPTIMISED.**
Scratch console bench, not a test: Release, net10.0, Windows, same process; source pixels prepared outside timing for both; one reused MemoryStream; per case the whole set is encoded once per pass, 6 passes with alternating order, median ms; 3 processes. Sets: "photo" = `Brick.png` + `Brick2.png` (two 1024² RGBA, AuroraEngine/Shaders); "atlas" = Thorium's 12 committed `*_atlas.adf` (11 fonts + icons) decoded to RGBA8. Ratio = Arctis time / ImageSharp time, range over the 3 processes.

| Case | photo | atlas |
|---|---|---|
| BMP 32-bit (Arctis default V5 vs `Pixel32, SupportTransparency`) | 1.27-1.34 | 1.37-1.45 |
| BMP 24-bit | 2.51-2.87 | 2.85-2.99 |
| BMP 16-bit 565 (vs `Pixel16`) | 0.26-0.31 | 0.66-0.71 |
| TGA 32-bit raw | 12.1-13.0 | 12.4-14.7 |
| TGA 32-bit RLE | 0.57-0.92 | 1.12-1.61 |
| TGA 24-bit | 8.9-13.0 | 10.2-11.2 |
| TGA 8-bit grey | 19-22 | 34-39 |
| PPM binary | 11.0-11.7 | 11.3-12.1 |
| PGM binary | 48-61 | 56-70 |
| PPM plain | 1.26-1.51 | 1.24-1.34 |
| QOI 4-channel (identical bytes) | 0.79-0.81 | 0.65-0.82 |
| QOI 3-channel | 0.83-0.87 | 0.76-0.79 |

- TGA RLE output: 8 198 563 vs 8 443 449 B (photo), 41 952 830 vs 46 208 473 B (atlas, 9.2 % smaller).
- Absolute examples (run 1, ms for the set): TGA 32 raw atlas 133.7 vs 9.2; PGM photo 5.03 vs 0.10; QOI 4 atlas 85.0 vs 130.0.
- Allocation per encode: Arctis 3-20 B (BMP/TGA/QOI), 369-388 B (PNM, header StringBuilder); ImageSharp 99-324 B.
- The slow ones are the formats that are nearly a memory copy (raw TGA, binary PNM, 24-bit BMP): our loops convert per pixel (TGA through a uint per pixel then byte-by-byte Put; PNM through a ushort sample row) where ImageSharp converts rows in bulk.

**ImageSharp 3.1.11 has no ICO/CUR support at all.**
No Formats.Icon namespace; ICO arrives in ImageSharp 4.x (cached locally as 4.1.3, with the licence change). ICO was not benchmarked. This retracts the earlier claim that ImageSharp 3.1 had ICO/CUR encoders ([[ico-simple-rasters]]).

## Verification
test-verified (unit). `dotnet test ArctisImaging.Tests` (net10.0): 433 passed.
- Pillow 13 fixtures byte-identical: QOI 3 and 4 channels, P6, P5 (from R8 and RG8), TGA 24/32-bit raw and RLE, TGA 8-bit grey, BMP 24-bit, BMP 32-bit BI_RGB Info, BMP 8-bit grey.
- Scratch cross-check 1: the bench decodes every lossless Arctis output with ImageSharp 3.1.11, all identical to the source.
- Scratch cross-check 2: 374 settings combinations written and decoded by Pillow 13 and compared to Arctis's own decode. Everything Pillow can read agrees, except where Pillow differs by design: 16-bit channels widened by shift (stored bits agree in all), PGM maxval above 255 read as 16-bit (agrees after scaling), BI_RGB 32-bit read without alpha, 32-bit icon alpha read directly (see gaps).
- Pillow cannot read PAM, BI_ALPHABITFIELDS, ARGB4444 bit fields, or PNG entries in a CUR, so those are checked only by our own decoders. A CUR with a BMP entry decodes identically in Pillow.
- AuroraEngine builds clean; ArctisImaging builds clean for net8.0 and net10.0.
- Not checked: engine test suite `_Build/test.sh` (no engine code changed), Windows Explorer or real Windows icon loading, ARM, netpbm tools (none installed), ImageSharp 4.x ICO.

## Known gaps
- Xrgb32 icon entries (32-bit, alpha byte 0) rely on the AND mask, as Windows and our decoder do; Pillow reads the zero alpha byte and shows them fully transparent.
- Every encoder UNOPTIMISED; raw TGA, binary PNM and 24-bit BMP are 3-70x ImageSharp's time.
- Not added: quantizer (indexed formats beyond exact palettes), 16-bit-index TGA colour maps, TGA image-ID/metadata, arbitrary BMP masks, PBM threshold setting (fixed at luma < 128), automatic ICO size generation, clipboard image writing.
- PNG and JPEG encoders were deliberately not widened to the settings-everything approach; a separate go if wanted.

Related: [[ico-simple-rasters]], [[jpeg-encoder]], [[other-raster-formats]]
