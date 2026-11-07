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

**Row fast paths sit beside the per-pixel path (2026-11-07).**
The copy-like formats were 3-70x ImageSharp because they converted per pixel (TGA through a uint per pixel, PNM through a ushort sample row, BMP through an RGBA row plus a whole-row `Clear`). Common raw cases now convert one row at a time from the source row into the output line; output bytes are unchanged. The old per-pixel path stays: it still serves every rare variant (bit fields, indexed, RLE, right-to-left, maxval scaling, plain text), and only the copy-like formats were slow.
- `PixelRows.ToBgr(row, dst, width, layout)` and `ToRgb(...)` (internal, new): RGBA8/BGRA8 to packed 24-bit B,G,R or R,G,B, sharing private `Pack24`. `Pack24` works on refs and throws `ArgumentException` if row < width*4 or dst < width*3; vector stores never pass dst's end (the loops check the room left, narrower loops and the scalar tail finish). Widest first: Avx512BW in-lane `Shuffle` + `Avx512F.PermuteVar16x32`, 16 pixels per step (no VBMI); then Avx2 `Shuffle` + `PermuteVar8x32`, 8 pixels per step; else Vector128.Shuffle unrolled x4 (tiers: [[simd-width-tiers]]); then single Vector128 steps; then scalar. See "SIMD kernels and block reads" below.
- `TgaEncoder.WriteDirect` (private): raw, non-indexed, left-to-right rows (origin BottomLeft or TopLeft). Argb32 from BGRA8 copies, from RGBA8 `ImageOps.CopySwizzled`; Rgb24 from RGBA8/BGRA8 `ToBgr`; Grey8 from R8 copies, from RGBA8/BGRA8 a luma loop; GreyAlpha16 from RG8 copies. RLE, indexed, 16-bit and right-to-left origins keep the old path.
- `PnmEncoder.WriteDirect` (private): binary output at maxval 255. P6 from RGBA8/BGRA8 `ToRgb`; P5 from R8 copies, from RG8 takes every other byte, from colour a luma loop; PAM RGB_ALPHA from RGBA8 copies. Plain, other maxvals, PBM and other PAM tuple types keep the old path.
- `BmpEncoder.Write`: non-icon, non-indexed 24/32-bit rows from RGBA8/BGRA8 are written straight from the source. 24-bit `ToBgr` then only the row padding is cleared; 32-bit copy (BGRA8) or `CopySwizzled` (RGBA8), Xrgb32 still zeroes the alpha byte. The `ToRgba` pass and the whole-row `Clear` are skipped.
- Left untouched on purpose: QOI (0.66-0.85), TGA RLE (0.71-1.08), BMP 16-bit (0.36-0.68). Also untouched: ICO, BMP RLE and indexed, and the PNM header's StringBuilder (about 370 B once per encode).
- `PixelRows.Pack24` carries AggressiveOptimization (user, 2026-11-07), like `ImageOps.CopySwizzled`: an exception to rule 42a beyond decode loops, because an encode also runs once per image. Without it the bench's first case that calls `Pack24` (BMP 24-bit, photo set) ran entirely in tier-0 at 8.25-9.86x ImageSharp (1.32x with `DOTNET_TieredCompilation=0`); with it, 0.80-0.82x. Cost accepted: no tier-1 PGO recompile for `Pack24`.

**SIMD kernels and block reads (second round, 2026-11-07).**
- `ImageOps.CopySwizzled` (BGRA<->RGBA): Vector512 (2 x 64 B per step), then Vector256 (2 x 32 B), then Vector128, then scalar tail; works through `MemoryMarshal.GetReference`/`LoadUnsafe`/`StoreUnsafe`. Throws `ArgumentException` when dst is shorter than src (replaces the span bounds checks). Still `AggressiveOptimization` and safe in place (load before store per step; `SwizzleBgraToRgba` relies on it).
- `BmpEncoder.WriteBottomUp` / `WriteDirect` (private; the direct 24/32-bit row conversion moved out of `Write` into `WriteDirect`), const `BmpEncoder.BlockBytes = 1 << 18`. `TgaEncoder.WriteBottomUp` and `TgaEncoder.BlockBytes = 1 << 18`, used when the direct path is on and the origin is BottomLeft. Direct bottom-up rows are read in ascending 256 KB blocks of source rows, each row written into its mirrored slot, one `Stream.Write` per block. Top-down direct rows keep one Write per row (blocks measured 4.05 vs 3.5 ms per row). No public API change, no data XML.
- **Descending row order costs about 2x on this machine.** Fixed for direct rows by ascending block reads, not by changing defaults.
- **How the width is chosen.** Each `IsHardwareAccelerated` is a JIT-time constant; the blocks run widest first and the narrower loops finish the tail. .NET reports Vector512 off by default on Skylake-X/Cascade Lake, and only Vector128 on ARM64. A single loop generic over the vector width is not possible: `ISimdVector<TSelf,T>` is internal in .NET 10 and missing in .NET 8 (checked). A hand-rolled interface plus structs was rejected as more code than the three loops.
- **`Pack24` has a Vector256 (Avx2) tier** (2026-11-07). The earlier "no Vector256 path, a cross-lane byte shuffle needs VBMI" claim is retracted: in-lane pshufb plus a dword permute needs only AVX2. See [[simd-width-tiers]].
- **Vector512 was not faster than Vector256 for `CopySwizzled` here** (AVX-512, memory-bandwidth bound) and slightly slower inside the encoders; the user chose to keep all three widths (2026-11-07).
- Scratch measurements, 12 Thorium atlases 1408², ms, median of 3 processes. `Pack24` kernel only: scalar 24.0, old (V128 with span slices) 4.8-5.1, V128 on refs 3.7-3.9, V128x4 2.8-3.1, V512 vpermb 2.75-3.0. Top-down rows with Write: old 6.1-6.2, V128x4 3.5-3.7, V512 3.5-3.6. Bottom-up rows: old 10.9-11.3, V512 7.0-7.3; in 256 KB blocks: old kernel 6.6-7.2, V512 4.2-4.3.
- `CopySwizzled`, TGA 32-bit, same atlases, ms: ImageSharp 8.15-8.59. Bottom-up blocks: V128 7.8-8.1, V256 6.0-6.6, V512 6.3-6.5. Top-down: V128 6.0-7.0, V256 5.0-5.7, V512 5.4-6.1. Kernel alone: V128 5.2-6.0, V256 4.5-5.5, V512 4.6-5.3.

**Speed vs ImageSharp 3.1.11, after the SIMD kernels and block reads (replaces the table after the first row fast paths).**
Scratch console bench, not a test: Release, net10.0, Windows, same process; source pixels prepared outside timing for both; one reused MemoryStream; per case the whole set is encoded once per pass, 6 passes with alternating order, median ms; 3 processes. Sets: "photo" = `Brick.png` + `Brick2.png` (two 1024² RGBA, AuroraEngine/Shaders); "atlas" = Thorium's 12 committed `*_atlas.adf` (11 fonts + icons) decoded to RGBA8. Ratio = Arctis time / ImageSharp time, range over the 3 processes.

| Case | photo | atlas |
|---|---|---|
| BMP 32-bit (Arctis default V5 vs `Pixel32, SupportTransparency`) | 0.70-0.75 | 0.66-0.67 |
| BMP 24-bit | 0.36-0.47 | 0.46 |
| BMP 16-bit 565 (vs `Pixel16`) | 0.36-0.55 | 0.67-0.68 |
| TGA 32-bit raw | 0.69-0.87 | 0.70-0.73 |
| TGA 32-bit RLE | 0.71-0.75 | 1.06-1.08 |
| TGA 24-bit | 0.39-0.46 | 0.49-0.53 |
| TGA 8-bit grey | 0.34-0.47 | 0.47-0.48 |
| PPM binary | 0.49-0.54 | 0.74-0.78 |
| PGM binary | 0.36-0.38 | 0.79-0.80 |
| PPM plain | 1.63-1.67 | 1.27-1.28 |
| QOI 4-channel (identical bytes) | 0.82-0.84 | 0.66-0.67 |
| QOI 3-channel | 0.80-0.85 | 0.75-0.80 |

- Absolute examples (ms for the set, run 1): TGA 32 raw atlas 133.7 before, 14.81 now (ImageSharp 9.78); PGM photo 5.03 before, 0.07 now (ImageSharp 0.17).
- Absolute, second round, run 1, atlas, ms (Arctis vs ImageSharp): BMP 24 4.45 vs 9.78; TGA 24 4.44 vs 9.03; TGA 32 5.45 vs 7.76; PPM 3.94 vs 5.34.
- Retracted: the earlier "1.3-1.5x on the copy-like cases, unprofiled" (written 2026-11-07 after the first round); the ratios above supersede it.
- TGA RLE output size (before the fast paths, bytes unchanged since): 8 198 563 vs 8 443 449 B (photo), 41 952 830 vs 46 208 473 B (atlas, 9.2 % smaller).
- Allocation per encode (first bench): Arctis 3-20 B (BMP/TGA/QOI), 369-388 B (PNM, header StringBuilder); ImageSharp 99-324 B.

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
- Still over 0.7 (user's target is <= 0.7 for every case): TGA 32 raw (photo up to 0.87, atlas 0.70-0.73), TGA RLE atlas 1.06-1.08, PPM binary atlas 0.74-0.78, PGM atlas 0.79-0.80, PPM plain 1.27-1.67, QOI 0.75-0.85.
- PPM's remaining cost is the copy into the stream itself, about 0.7 of 3.5 ms in scratch, unavoidable through the Stream API.
- **Open, NOT decided: TGA default origin.** The user earlier picked changing the default to TopLeft; enum reorder vs a new `TgaOrigin.Auto` is unanswered. With block reads BottomLeft TGA 32 is already 0.70-0.73 on the atlases, so the change may no longer be needed. The default is still BottomLeft.
- Vector512 slightly slower than Vector256 here, kept by user choice.
- Row fast paths cover only the pairs listed above. Other layout/format pairs (TGA Argb32 from R8, PAM RGB, PAM grey, BGRA8 PAM RGB_ALPHA) still use the per-pixel path.
- Row fast paths verified by a scratch byte-equality check (9688 cases against HEAD's encoders, all identical) and 433 unit tests; not checked: net8.0 test run, ARM / non-SSSE3 hardware (the scalar tail is the only path there).
- Second round (SIMD kernels, block reads): scratch equality vs HEAD 6d11197, 11072 cases (9488 encodes + rejections) identical. Coverage: RGBA8/BGRA8/R8/RG8, widths 1-101 plus 1408 and 3001 with heights 150-189 (several 256 KB blocks), padded pitches, every non-indexed TGA format x 4 origins, BMP Auto/Rgb24/Xrgb32/Argb32 x headers x top-down/bottom-up, PNM kinds x maxvals x tuple types. Kernel variants checked against scalar at widths 1-1408, both byte orders; the bench's ImageSharp round-trip decode reported no mismatches. 433 tests passed, ArctisImaging builds clean Release net8.0+net10.0, 0 warnings. Not checked: `_Build/test.sh`, net8.0 test run, ARM, a machine without VBMI, a machine without AVX-512.
- Not added: quantizer (indexed formats beyond exact palettes), 16-bit-index TGA colour maps, TGA image-ID/metadata, arbitrary BMP masks, PBM threshold setting (fixed at luma < 128), automatic ICO size generation, clipboard image writing.
- PNG and JPEG encoders were deliberately not widened to the settings-everything approach; a separate go if wanted.

Related: [[ico-simple-rasters]], [[jpeg-encoder]], [[other-raster-formats]], [[simd-width-tiers]]
