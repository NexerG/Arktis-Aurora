# Decision — Arctis.Imaging decodes lossless JPEG (SOF3) and JPEG-LS

**Date:** 2026-11-07
**Scope:** `Arctis.Imaging` — `JpegDecoder` (partial, `JpegLossless.cs`), `JpegLsDecoder`, `ImageCodec`, `ImageFormat`

## What changed
- `ImageFormat.JpegLs` added. Lossless JPEG stays `ImageFormat.Jpeg`.
- `JpegDecoder` is now `partial`. New file `JpegLossless.cs` holds `ReadLosslessFrame`, `DecodeLossless`, `OutputLossless`.
- `Frame` gains `lossless`, `precision`, `pointTransform`, `ushort[] samples`. `ReadSegments` handles SOF3 (0xC3). `ReadScan` routes lossless scans to `DecodeLossless(…, predictor = Ss, pt = Al)`.
- New private `IsYcc(in Frame)` holds the existing three-component colour-space rule; `Output` now calls it.
- `JpegDecoder.TryReadHeader` recognises SOF55 (0xF7) and returns `ImageFormat.JpegLs`.
- New internal static `JpegLsDecoder` with only `Decode(ReadOnlySpan<byte>, Span<byte> destination, int pitch, PixelLayout)`; its header comes from `JpegDecoder.TryReadHeader`.
- `ImageCodec.Decode` has a case for `JpegLs`. Tests: `JpegLosslessTests`, `JpegLsTests`. Fixtures: `Ls*.jls`, `Lj*.jpg` in `ArctisImaging.Tests/Fixtures`, each with a reference .png.
- Decoders only, no encoders. **Unoptimised**: scalar, never profiled, no ratio measured.

## Why these choices

**Lossless JPEG lives in a partial `JpegDecoder` file (user choice).**
It reuses the private `Bits` reader and Huffman tables. The rejected alternative was a `#region` inside the 1,413-line `JpegDecoder.cs`.

**Lossless scope: precision 2-16, predictors 1-7, point transform, restart intervals, interleaved and non-interleaved scans, 1/3/4 components at equal sampling.**
A difference of category 16 is 32768. Samples are taken modulo 2^16. Above 8 bits the high byte is kept; below 8 bits samples scale to 0-255 as `(v*255 + max/2)/max`, because `PixelLayout` has only 8-bit layouts (the TIFF rule in [[other-raster-formats]]).

**Three components go through the baseline YCbCr rule (`IsYcc`).**
libjpeg-turbo writes an Adobe marker with transform 0 and R/G/B ids, so its files come out as RGB copies.

**Still rejected for lossless:** arithmetic SOF11 (the arithmetic choice in [[jpeg-gif-decoders]] stands), hierarchical, subsampled.

**JPEG-LS is a separate `JpegLsDecoder.cs`, decoded the way CharLS decodes it.**
- its own bit reader: a byte after 0xFF holds 7 bits under a stuffed zero, and 0xFF before a byte >= 0x80 is a marker
- median edge predictor; 365 regular contexts indexed by |Qs|, sign taken from Qs
- limited-length Golomb coding with LIMIT = 2*(bpp + max(8, bpp))
- run mode with the 32-entry J table and two run-interruption contexts
- NEAR > 0, interleave modes 0/1/2, LSE type 1 presets (a zero field keeps the T.87 default thresholds)
- ILV 1: each component keeps its own run index. ILV 2: one run index is shared, a run needs every component flat, and the interruption always uses context 0 predicting from Rb
- components map to grey, grey + alpha, RGB or RGBA; above 8 bits the high byte is kept, below 8 samples scale

**JPEG-LS throws `NotSupportedException` for** mapping tables (LSE 2/3, or a nonzero Tm in SOS), LSE 4 / a size given later, the HP colour transform (APP8 "mrfx"), subsampling, a nonzero point transform, and restart intervals (DRI != 0).
The last two were not in the plan's scope list; they are rejected because no reference encoder here can produce them.

**Test oracle (user choice 2026-11-07): `pip install imagecodecs` 2026.10.10**, a dev-only Python package, not a solution dependency.
It brings CharLS (JPEG-LS) and libjpeg-turbo 3.2 (lossless encode). The rejected alternative was test-side encoders, which would share any misreading of the spec with the decoder. imagecodecs writes ILV 2 for 3-component JPEG-LS and ILV 1 for 4-component, and cannot write ILV 0, so the `LsRgbIlv0` fixture is three single-component CharLS scans spliced into one 3-component frame, checked against CharLS's own decode.

## Verification
- ArctisImaging.Tests 348/348 (shared with [[ico-simple-rasters]]). All 38 encoded fixtures decode byte-identical to their references.
- Scratch random cross-check (not committed), 260/260 byte-identical: 200 random encodes (crops of the fixture photos, grey/RGB/RGBA, flattened colours for runs, bit depths 2/3/5/8/10/12/14/16, JPEG-LS NEAR 0/1/2/5/10, lossless predictors 1-7) against CharLS / libjpeg-turbo decodes, plus 60 Pillow-written files for the simple rasters.
- Solution builds clean. `_Build/test.sh`: 509 passed, 8 failed, 0 new (Boot sampler error, 7 LatinModern Tex.* goldens; both in the baseline). No `[Vulkan]` lines.

## Known gaps
- Not GUI-verified: paste or drop of a lossless JPEG or `.jls` into a Thorium note.
- Not checked: lossless JPEG with restart markers (no encoder here writes them); JPEG-LS with LSE preset thresholds from a real file (only defaults exercised); 2-component JPEG-LS.
- Unsupported: JPEG-LS restart intervals, point transform, mapping tables, HP colour transform; arithmetic, hierarchical and subsampled lossless JPEG. 12-bit baseline/progressive DCT JPEG is still unsupported.
- No encoders. Performance not measured.

Related: [[jpeg-gif-decoders]], [[other-raster-formats]], [[ico-simple-rasters]]
