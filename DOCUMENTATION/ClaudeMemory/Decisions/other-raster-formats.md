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
- **BmpDecoder RLE**: constants `Rle8 = 1`, `Rle4 = 2`, private `DecodeRle`. Pixels skipped by a delta or end-of-line come out transparent black (the approved plan; ImageSharp's default `RleSkippedPixelHandling` was black). A stream cut short decodes what it holds; a literal running past the end is an `ImageFormatException`.
- **Perf tests** (user: "no perf tests"): `ImageDecodePerfTests` lost `Perf.ImageDecode.Atlases.ImageSharp`, `Formats.Arctis`, `Formats.ImageSharp` (and `Formats`, `Encode`, `DecodeSharp`, `sampleSize`; `Measure` lost `sharp`), and their entries in `Perf.tests.axml`. The Formats benchmark built its inputs with ImageSharp's encoders, so it went; the atlas benchmarks `Atlases.Arctis` and `Atlases.Adf` do not use ImageSharp and stay. Earlier ratios vs ImageSharp in these notes are history; the comparison benchmark can no longer run.
- Tests: ArctisImaging.Tests 278/278. TiffTests: 16-bit in both byte orders, 16-bit predictor, 1/2/4-bit grey and palette, CMYK, planar (one strip per row per plane), fill order 2, `JpegStripsMatchPillow` (JpegRgb, JpegYcc: three 8-row strips with JPEGTables from libtiff through Pillow 13, within 1), reworked `UnsupportedVariantsThrow`. BmpTests: RLE8 runs/literals/delta/end, RLE4 nibbles, literal past the end rejected. Fixtures `JpegRgb.tif`/`.png`, `JpegYcc.tif`/`.png`. Solution builds clean.

## Known gaps
- No ImageSharp fallback any more: an unsupported variant throws `NotSupportedException` from `ImageFiles.Load`; `ClipboardImage` logs a warning, other callers throw. Still unsupported: TIFF CCITT (2/3/4), float and other sample formats, CieLab, predictor 3, planar/old-style/palette/CMYK JPEG-in-TIFF; arithmetic JPEG ([[jpeg-gif-decoders]]).
- The new decode paths (TIFF `Widen`, CMYK row conversion, BMP RLE) are scalar and unprofiled (no perf tests written or run).
- Not checked: GUI paste of a CMYK .jpg, a 16-bit or JPEG .tif, an RLE .bmp; tiled JPEG TIFF from a real tool; Release build (removing the package should clear the licence-check blocker, not run); the full `--test` suite (only Visual: 5 passed, 1 failed, Boot, from the pre-existing default sampler error; no `[Vulkan]` lines).
- `actionSchema.xsd` and SchemaManifest regenerate on the next run and drop the three removed perf actions; not checked.
- Verified by tests (ArctisImaging.Tests 180/180) and a scratch cross-check: 28 Pillow-written TIFF/TGA files plus two libwebp method-6 lossless files decode byte-identical to Pillow. Not checked: GUI paste of a .tga/.tif/.webp into a Thorium note; tiled TIFF and predictor 2 from a real tool (hand-built only); which VP8L transforms/meta codes each fixture exercises; performance.
- No measurements recorded.

Related: [[png-decode-fast-paths]], [[ktx2-container]], [[app-icon]], [[webp-lossy]], [[ico-simple-rasters]], [[lossless-jpeg]]
