# Decision — Arctis.Imaging decodes TGA, baseline TIFF and lossless WebP

**Date:** 2026-11-06
**Scope:** `Arctis.Imaging` — `TgaDecoder`, `TiffDecoder`, `WebpDecoder`, `ImageCodec` (`TryIdentify`, `Decode`), `Image` (`Load`), `ImageFormat`; `ArctisAurora.Core.Imaging` — `ImageFiles`

## What changed
- The three formats `ClipboardImage` accepts for dropped/copied files (`.tga`, `.tif/.tiff`, `.webp`) no longer depend on ImageSharp. JPEG and GIF are decoded by Arctis too, see [[jpeg-gif-decoders]].
- `ImageFormat` gains `Tga`, `Tiff`, `Webp`, appended after `Adf`.
- `ImageCodec.TryIdentify(data, out header, ImageFormat hint = Unknown)`, `ImageCodec.Decode(source, destination, in options, ImageFormat hint = Unknown)`, `Image.Load(ReadOnlySpan<byte>, layout, limits, ImageFormat hint = Unknown)`: optional trailing parameter, source compatible.
- TIFF and WebP are recognised by magic. TGA has no signature and is tried only when `hint == ImageFormat.Tga`.
- (`ImageFiles.CanDecode` existed here and is removed, see "ImageSharp removed" below.) `ImageFiles.Load(ReadOnlySpan<byte>, layout, ImageFormat hint = Unknown)` gained the hint. `ImageFiles.Load(string path, layout)` passes `Tga` when the path ends in `.tga` (case-insensitive).
- `TgaDecoder` (internal): image types 1/2/3 and RLE 9/10/11; 8/15/16/24/32-bit; colour-mapped with 8 or 16-bit indices and 15/16/24/32-bit map entries honouring the first-entry index; grey 8 and grey+alpha 16; both origins; RLE packets may run across rows. Alpha used only when descriptor alpha bits > 0; a 32-bit/alpha image whose alpha is zero everywhere decodes opaque (same rule as `BmpDecoder`). Out-of-range palette index gives black (transparent black if the image has alpha).
- `TiffDecoder` (internal): first IFD only; II and MM; strips and tiles (edge tiles clipped); 8-bit only; WhiteIsZero, BlackIsZero, RGB, Palette (256-entry ColorMap); chunky only; compression none (1), LZW (5, TIFF variant: MSB-first, 9-12 bits, widens one code early), Adobe Deflate (8) and old Deflate (32946) via `Inflater.InflateZlib`, PackBits (32773); horizontal predictor (317 = 2); ExtraSamples 1 (associated, un-premultiplied to straight on decode) or 2 (unassociated). A tile larger than max(dimension, 4096) is rejected as malformed.
- `TiffDecoder` throws `NotSupportedException` for: JPEG-in-TIFF (6/7), CCITT, other compressions, CMYK/YCbCr/other photometrics, planar config 2, sample format other than 1, fill order 2, predictor 3, bit depths other than 8 (1-bit and 4-bit included). BigTIFF (magic 43) is not recognised.
- `WebpDecoder` (internal): RIFF simple (VP8L / "VP8 ") and extended (VP8X); full VP8L: all 14 predictor modes, cross-colour, subtract-green and colour-indexing (1/2/4/8-bit packed) transforms, colour cache, meta prefix codes, LZ77 with the 120-entry short-distance table, two-level Huffman lookup (8-bit root). Truncated stream throws `ImageFormatException`. Lossy VP8, ALPH alpha and the first frame of animated WebP are decoded since the same day, see [[webp-lossy]] (unoptimised).
- Tests: `TgaTests`, `TiffTests` (test-side TIFF writer and TIFF-LZW encoder; `ZLibStream` as Deflate reference), `WebpTests`; fixtures `Fixtures/*.webp` (8 libwebp lossless encodes via Pillow 13, patterns recomputed in the test), copied to output by `<None Include="Fixtures\**">` in the test csproj.

## Why these choices

**TGA is recognised by an extension hint, not a header heuristic.**
Rejected: a plausibility check on the 18-byte header as the last `TryIdentify` probe (what ImageSharp does), because it can mistake arbitrary bytes for a TGA. Rejected: the TGA 2.0 footer ("TRUEVISION-XFILE"), because v1 files have none. Cost: a TGA reached as bytes without a path (e.g. `ImageFiles.TryIdentify(bytes, ...)`) is not recognised by Arctis.

**WebP was lossless first; lossy followed the same day.**
This was ordering, not licensing: VP8 is under Google's royalty-free patent grant. Lossy, ALPH and first-frame animation are in [[webp-lossy]].

**TIFF scope is baseline including LZW, not "uncompressed + Deflate only".**
Most tools write LZW. 16-bit, 1/4-bit, CMYK, planar and JPEG-in-TIFF are deliberately out.

**Long-term goal (user, 2026-11-06): support most image formats and codecs that are not patent- or copyright-encumbered.**
- Excluded: HEIC/HEIF (HEVC patent pools).
- Doubtful, decide later: AVIF (AV1 royalty-free but a competing Sisvel pool), JPEG 2000, JBIG2.
- Licence calls are from public knowledge, not legal advice.
- The roadmap itself lives in the WIP list as grouped open items.

**ICO is not needed for the tray icon.**
`Background.TrayIcon` builds it from `Icons/app/*.png` via `AGlfwWindow.AppIcons`, and Thorium already ships `Thorium.ico` (see `app-icon`). The WIP item saying the tray icon is IDI_APPLICATION because Thorium has no .ico looks stale (IDI_APPLICATION is only the fallback when no app PNGs ship); flagged, not deleted.

## WebP decoder internals
- `BuildTable`: 8-bit root table; a root entry is `length << 16 | symbol`, or for a code longer than 8 bits a link `(subBits + 8) << 16 | offset` where offset runs from that root entry to its second-level table. Second-level entries are `(length - 8) << 16 | symbol`. One-symbol codes fill the root with length 0 (reads no bits). Called twice per code: `fill: false` sizes it, then it is built into the shared flat table array.
- `UndoPredictor`: the row above is taken as a span one pixel longer than the row, so the last pixel's top-right is the current row's first pixel, which is what VP8L specifies.
- `Bits` reads zeros past the end and counts them in `overread`; the decode throws only once, after the main image, if those bits were consumed.

## ImageSharp removed, TIFF and BMP extended (2026-11-06)
- **ImageSharp is gone**: the `SixLabors.ImageSharp` 3.1.11 `PackageReference` is removed from the AuroraEngine and Thorium projects; no SixLabors reference remains in any .cs or .csproj. `ImageFiles.Load(span, layout, hint)` is just `Image.Load(data, layout, Limits, hint)`. Removed: `CanDecode`, `LoadLegacy`, the fallback, `TryIdentify(ReadOnlySpan<byte>, out int, out int)`. Added `ImageFiles.TryIdentify(string path, out int width, out int height)`, which takes the TGA hint from the extension via private `HintOf(path)`; `TexLowering` and Carbon `TestResultsControl` call it (so TGA now identifies there). `TextureLoader`'s direct-to-staging path no longer checks `CanDecode`: every format `ImageCodec.TryIdentify` returns has a decoder.
- **TiffDecoder**: constants `Jpeg = 7`, `Cmyk = 5`, `YCbCr = 6`; `Info` gained `inkSet` (tag 332, default 1) and `jpegTables` (tag 347); `TryReadField`/`Value` accept type 7 (UNDEFINED) as bytes; `CheckSupported` returns bits per sample. New private `BaseSamples`, `Stored` (one strip or tile's bytes, bit-reversed under fill order 2), `Widen` (a row to 8-bit chunky samples: planes interleaved, 16-bit to the high byte, sub-byte samples unpacked MSB-first and scaled to 0-255 unless palette indices), `Times`. `UndoPredictor` gained `bits` and `bigEndian`; `ReadPalette` gained `bits` and reads 2^bits entries.
- **TIFF limits**: 16-bit samples keep only their high byte; predictor works at 8 and 16 bits only; sub-byte samples for grey and palette only; 16-bit palettes, mixed sample depths, InkSet other than 1 and YCbCr without JPEG are rejected.
- **JPEG-in-TIFF**: each strip or tile is an abbreviated JPEG stream; `JpegDecoder.DecodeAbbreviated` reads the JPEGTables (DQT/DHT) first. The TIFF's photometric value fixes the colour space, not the JFIF/Adobe/component-id guess: 6 (YCbCr) is converted, 2 (RGB) is copied as stored (libtiff writes photometric-RGB JPEG with no colour conversion). The strip or tile decodes into an RGBA scratch chunk and the visible part is copied out, so edge tiles clip. The frame size must cover the visible area and fit inside the chunk, else `ImageFormatException`. Supported only for 8-bit chunky grey, RGB or YCbCr; not planar, old-style (compression 6, obsolete since 1995), palette or CMYK JPEG.
- **BmpDecoder RLE**: constants `Rle8 = 1`, `Rle4 = 2`, private `DecodeRle`. Pixels skipped by a delta or end-of-line come out transparent black (the approved plan; ImageSharp's default `RleSkippedPixelHandling` was black). A stream cut short decodes what it holds; a literal running past the end is an `ImageFormatException`. **Rewritten 2026-11-08**: `DecodeRle` writes straight into the destination with lazy gap clearing (no full-image RGBA frame, no `FromRgba` pass) and a fixed-width fast path for short ops; skipped pixels stay transparent black; see [[ico-simple-rasters]] "BMP indexed rows and RLE".
- **Perf tests** (user: "no perf tests"): `ImageDecodePerfTests` lost `Perf.ImageDecode.Atlases.ImageSharp`, `Formats.Arctis`, `Formats.ImageSharp` (and `Formats`, `Encode`, `DecodeSharp`, `sampleSize`; `Measure` lost `sharp`), and their entries in `Perf.tests.axml`. The Formats benchmark built its inputs with ImageSharp's encoders, so it went; the atlas benchmarks `Atlases.Arctis` and `Atlases.Adf` do not use ImageSharp and stay. Earlier ratios vs ImageSharp in these notes are history; the comparison benchmark can no longer run.
- Tests: ArctisImaging.Tests 278/278. TiffTests: 16-bit in both byte orders, 16-bit predictor, 1/2/4-bit grey and palette, CMYK, planar (one strip per row per plane), fill order 2, `JpegStripsMatchPillow` (JpegRgb, JpegYcc: three 8-row strips with JPEGTables from libtiff through Pillow 13, within 1), reworked `UnsupportedVariantsThrow`. BmpTests: RLE8 runs/literals/delta/end, RLE4 nibbles, literal past the end rejected. Fixtures `JpegRgb.tif`/`.png`, `JpegYcc.tif`/`.png`. Solution builds clean.

## Known gaps
- No ImageSharp fallback any more: an unsupported variant throws `NotSupportedException` from `ImageFiles.Load`; `ClipboardImage` logs a warning, other callers throw. Still unsupported: TIFF CCITT (2/3/4), float and other sample formats, CieLab, predictor 3, planar/old-style/palette/CMYK JPEG-in-TIFF; arithmetic JPEG ([[jpeg-gif-decoders]]).
- The new decode paths (TIFF `Widen`, BMP RLE; the CMYK row conversion is SIMD since 2026-11-08, see [[jpeg-gif-decoders]]) are scalar and unprofiled (no perf tests written or run). The BMP non-RLE paths are no longer part of this: 32/24/555/565 DIBs were optimised and measured 2026-11-08 ([[ico-simple-rasters]]); only BMP RLE stays scalar and unprofiled.
- Not checked: GUI paste of a CMYK .jpg, a 16-bit or JPEG .tif, an RLE .bmp; tiled JPEG TIFF from a real tool; Release build (removing the package should clear the licence-check blocker, not run); the full `--test` suite (only Visual: 5 passed, 1 failed, Boot, from the pre-existing default sampler error; no `[Vulkan]` lines).
- `actionSchema.xsd` and SchemaManifest regenerate on the next run and drop the three removed perf actions; not checked.
- Verified by tests (ArctisImaging.Tests 180/180) and a scratch cross-check: 28 Pillow-written TIFF/TGA files plus two libwebp method-6 lossless files decode byte-identical to Pillow. Not checked: GUI paste of a .tga/.tif/.webp into a Thorium note; tiled TIFF and predictor 2 from a real tool (hand-built only); which VP8L transforms/meta codes each fixture exercises. Performance: see "Measured against native references (2026-11-08)".
- Measurements: see "Measured against native references (2026-11-08)" below.

## TGA decode fast paths (2026-11-09)
- `TgaDecoder` decodes common variants a row at a time into the destination (raw and RLE). New private: `enum Direct { None, Bgra32, Bgr24, Rgb555, Grey8, GreyAlpha16, Mapped8 }`, `DirectFor(in Info, PixelLayout, bool alpha)`, `BuildTable(uint[] palette, in Info, bool alpha, bool bgra, Span<uint> table)`, `DecodeRow(Direct, src, dst, width, layout, alpha, table)`, `HasAlpha(Direct, src, dst, width)`, `Repeat(pixel, span)`. `ReadRunLengthRow` rewritten (same signature) to work per packet. `Decode` picks a `Direct` kernel per file+layout; `ConvertRow` + `FromRgba` remain for `Direct.None`.
- Shared kernels moved from private `BmpDecoder` to internal `PixelRows`: `Row32(src, dst, width, layout, opaque)`, `Row24`, `Row16(..., wideGreen)`, `ForceOpaque`, `HasAnyAlpha(pixels, aMask)`; private `Grey16` moved with them. Bodies unchanged; `BmpDecoder.DecodeDirect` and `Decode` call `PixelRows`. TGA stores B,G,R(,A) like BMP, so the kernels apply unchanged.
- Which files get a direct row: left-to-right only. TrueColour 32 -> `Row32` (opaque forced when alpha bits are 0), 24 -> `Row24`, 15-bit or 16-bit with no alpha bits -> `Row16(wideGreen: false)`. Grey 8 -> copy for R8, `FromGrey` for RGBA8/BGRA8. Grey 16 -> copy only into RG8 with alpha bits > 0. Colour-mapped 8-bit index into RGBA8/BGRA8 -> `LookupRow` over a 256-entry stackalloc table (1 KB) built once with `mapFirst` and the out-of-range colour folded in.
- Still per-pixel: right-to-left, 1555 with alpha, 16-bit indices, mapped into R8/RG8, grey 16 other than into RG8 with alpha, grey 8 into RG8.
- RLE decode expands per packet, then runs the same row kernel. Raw packet = one span copy of count*bpp bytes; run packet = `Repeat` (uint Fill for 4 bytes, byte Fill for 1, doubling copies for 2/3). Packets still run on across rows.
- `Decode`, `DecodeRow`, `HasAlpha`, `ReadRunLengthRow`, `Repeat` carry `[MethodImpl(AggressiveOptimization)]` (user approved 2026-11-09, aurora-code rule 42a's Arctis.Imaging decode-loop exception). See [[tier0-helpers-skew-decode-bench]].

**The shared row kernels live in `PixelRows`, not `BmpDecoder`.**
The user picked this over making them internal in `BmpDecoder`, which was rejected because one decoder would depend on another. Same precedent as `FromGrey`/`FromRgb` leaving `PngDecoder`.

**"Zero alpha everywhere means opaque" is kept, checked per row.**
`HasAlpha` scans each decoded row (stored bytes for 32-bit and grey+alpha, mask 0xFF000000 / 0xFF00FF00 plus the odd last pixel; destination row for the palette) only until the first alpha is found. Only an image whose alpha is zero everywhere takes the existing `MakeOpaque` pass afterwards. No whole-image prescan, so raw and RLE share one path.

**RLE runs are expanded into the row buffer, not decoded straight into the destination.**
Rejected: converting the colour once per run straight into the destination (BMP RLE style), because it is more code; the extra row copy is cache-hot. Cost: RLE decode is ~5-6x slower than raw on atlases (21-23 ms vs 3.5 ms).

**`AggressiveOptimization` on the decode loop and helpers.**
Without it the first RLE case in a process sometimes ran 4.4-4.9 ms instead of ~1.7 ms (photo RGB RLE); with `DOTNET_TieredCompilation=0` it was a stable 1.75-1.87 ms. With the attribute: 1.73-1.90 in every run. Cost accepted: RGBA RLE lost its tier-1 PGO best case (photo 1.25-2.13 -> 2.19-2.27 ms, atlas 16.6-19.0 -> 21.3-22.7 ms).

Measurements. Method: scratch console benches, not committed. Release, net10.0, Windows, AMD Ryzen 9 9900X, default tiering. `ImageCodec.Decode` with `ImageFormat.Tga` hint into a preallocated RGBA8 buffer; set decoded per pass, 40 warm-up, 6 timed, median; 3 processes before, 3 after, alternated with the reference. Photo = `Brick.png`+`Brick2.png` (1024², opaque) written by Pillow 13 (RGB, RGBA, both with `tga_rle`, and `quantize(256)` palette). Atlas = Thorium's 12 `*_atlas.adf` decoded to RGBA8 and written by Arctis as Argb32 raw / RLE. Reference = imagecodecs 2026.10.10 `tga_decode` and Pillow 13 from CPython 3.14 (cross-runtime, indicative; Pillow's palette decode stays palette-mode, so the palette row uses imagecodecs).

| Decode case | Before ms | After ms | Change | Reference ms |
|---|---|---|---|---|
| Photo RGB raw | 2.19-2.33 | 0.13 | ~17x faster | imagecodecs 1.25-1.30 (Pillow 2.49-2.68) |
| Photo RGBA raw | 3.54-3.70 | 0.14 | ~26x faster | imagecodecs 1.43-1.50 (Pillow 2.85-2.97) |
| Photo RGB RLE | 5.67-6.45 | 1.73-1.90 | ~3.3x faster | Pillow 3.69-3.74 (imagecodecs 4.49-4.61) |
| Photo RGBA RLE | 7.75-11.42 | 2.19-2.27 | ~4x faster | Pillow 4.17-4.41 (imagecodecs 5.36-5.41) |
| Photo 8-bit palette | 2.49-2.53 | 0.43 | ~5.8x faster | imagecodecs 1.44-1.49 |
| Atlas RGBA raw (12 x 1408²) | 62.5-103.7 | 3.51-3.58 | ~20x faster | - |
| Atlas RGBA RLE | 137.9-178.6 | 21.3-22.7 | ~7x faster | - |

Ratios vs the best reference after: RGB raw ~0.10x, RGBA raw ~0.10x, RGB RLE 0.47-0.51x, RGBA RLE 0.50-0.54x, palette ~0.30x. Allocation unchanged (cold first call: 0 B raw/RLE, 3 320 B palette, 9 800 B atlas raw; ArrayPool first rents).

Verified: ArctisImaging.Tests 519/519 (net10.0, Release) including `TgaTests.DirectRowsMatchThePerPixelPath` (Theory, 12 cases: raw and RLE left-to-right files vs the per-pixel path on the same pixels stored right to left, 4 layouts, both row orders, widths 1-149, runs crossing rows and over 128 px, zero-alpha variants; helpers `Layouts`, `RunLength(pixels, bpp)`). Scratch equality check: 69 196 cases (decode of every image type x depth x alpha bits x map entry size x origin x raw/RLE x layout, widths 1-129 plus 1408 and 3001, zero-alpha variants, truncated files; encode of every TgaPixelFormat x layout x origin x RLE x padded pitch x few/many colours) byte-identical (SHA-256, or same exception type) between the pre-change ArctisImaging and the new one. The `AggressiveOptimization` change was re-tested (519/519) and re-benchmarked but not re-run through the equality harness. Not checked: ARM64, net8.0, lower SIMD tiers, real TGA files from tools other than Pillow, GUI paste of a .tga into Thorium. The encode side is in [[simple-raster-encoders]].

## Measured against native references (2026-11-08)

- Method: scratch console bench, not committed (no perf tests, see above). Release, net10.0, Windows, AMD Ryzen 9 9900X, default tiering. Entry: `ImageCodec.Decode` with a format hint (`ImageFormat.Tga` for TGA, `Unknown` otherwise) into a preallocated RGBA8 buffer.
- Inputs: `Brick.png` + `Brick2.png` (two 1024² opaque photos) encoded per case: Pillow 13 (TGA, JPEG-in-TIFF, WebP lossless), imagecodecs 2026.10.10 / libtiff 4.7.2 (TIFF none/LZW/Deflate/PackBits, rowsperstrip 16, predictor 2 where named).
- The 2-image set is decoded per pass; 40 warm-up passes, 6 timed, median ms for the set; 3 Arctis processes alternated with 3 reference processes; ratio = Arctis / reference paired per round (below 1 = Arctis faster). Arctis allocated 24 B per set in every case.
- References are native C decoders called from CPython 3.14: a cross-runtime ratio, indicative only. They mostly write 3-channel RGB, Arctis writes 4-byte RGBA8, which favours the reference. The photos are opaque, so RGBA cases exercise the 4-channel path but not real alpha.
- Correctness: all Arctis outputs byte-identical to Pillow's decode converted to RGBA (56/56 across the whole bench, all four notes).

| Case | Arctis ms | Reference ms | Ratio |
|---|---|---|---|
| TGA (superseded 2026-11-09, see "TGA decode fast paths": now 0.10-0.54x) | - | - | was 1.44-2.57x |
| TIFF uncompressed RGB | 2.35-2.45 | libtiff 0.73-0.80 (Pillow 1.68-1.72) | 2.95-3.34x |
| TIFF uncompressed RGBA | 2.51-2.83 | libtiff 0.95-1.02 (Pillow 1.51-1.55) | 2.53-2.76x |
| TIFF LZW | 21.06-22.20 | libtiff 15.55-16.68 | 1.26-1.43x |
| TIFF LZW + predictor 2 | 21.71-22.25 | libtiff 15.21-15.67 | 1.39-1.46x |
| TIFF Deflate + predictor 2 | 14.38-14.42 | libtiff 10.07-10.20 | 1.41-1.43x |
| TIFF PackBits | 2.68-3.56 | libtiff 0.72-0.87 | 3.36-4.67x |
| TIFF JPEG (YCbCr, Pillow/libtiff) | 9.33-9.55 | libtiff 10.59-10.86 | 0.88-0.89x, faster |
| WebP lossless, method 4 | 39.54-40.83 | libwebp 1.6.0 26.53-32.29 (Pillow 34.66-35.03) | 1.22-1.52x |
| WebP lossless, method 6 | 30.49-35.05 | libwebp 26.10-28.55 | 1.07-1.34x |

- Cold first decode of one image (JIT included, one process): TIFF JPEG 33 ms, WebP m4 41 ms, TGA RGB 23 ms.
- Slow spots, recorded only (no WIP entry by decision): TIFF raw/PackBits (likely no direct-copy path), TIFF LZW/Deflate, WebP lossless.
- Not checked: ARM64, net8.0, lower SIMD tiers (EnableAVX512=0 etc.), real-alpha images.

Related: [[png-decode-fast-paths]], [[ktx2-container]], [[app-icon]], [[webp-lossy]], [[ico-simple-rasters]], [[lossless-jpeg]]
