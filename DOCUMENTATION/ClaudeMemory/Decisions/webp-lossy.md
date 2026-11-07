# Decision — Arctis.Imaging decodes lossy WebP (VP8), ALPH alpha and the first frame of animated WebP (UNOPTIMISED)

**Date:** 2026-11-06
**Scope:** `Arctis.Imaging` — `Vp8Decoder`, `WebpDecoder`; `ArctisImaging.Tests` — `WebpTests`; `ArctisAurora.Tests` — `ImageDecodePerfTests`

## What changed
- New `Vp8Decoder` (internal static): `Decode(ReadOnlySpan<byte> data, int width, int height, Span<byte> rgba, int pitch)` writes RGBA8 rows with alpha 255.
- `Vp8Decoder` private parts: `BoolDecoder` struct (libwebp-style: range kept minus one, 7-byte refill, eof flag), `ReadHeader`, `ReadResiduals`, `ReadCoeffs`, `ReadLargeValue`, `InverseWht`, `Reconstruct`, `Predict16`, `Predict4`, `AddResidual`, `Filter` (simple and normal loop filter), `Upsample` / `UpsampleRows` (libwebp "fancy" 4:2:0), `ToRgba` (libwebp 14-bit fixed-point BT.601).
- `WebpDecoder.Container` gains `alphaAt`, `alphaLength`, `frameX`, `frameY`, `frameWidth`, `frameHeight`. The `Animated` kind constant is removed. `TryReadContainer` walks into the first ANMF frame and records ALPH chunks.
- `WebpDecoder` new private: `DecodeLossless`, `LosslessToRgba`, `LosslessReader`, `ArgbToRgba`, `Place` (frame onto the canvas), `DecodeAlpha`, `Unfilter` (ALPH raw or VP8L-coded, then none/horizontal/vertical/gradient).
- `WebpDecoder.DecodeArgb(ref Bits, width, height)` returns rented ARGB. It is the former body of `Decode`, now shared by the main lossless image and VP8L-coded alpha.
- A lossy whole-canvas decode into RGBA8 writes straight into the destination. Every other layout goes through a rented RGBA frame and `PixelRows.FromRgba`.
- `ImageCodec` and `ImageFiles` unchanged (`CanDecode` already listed Webp). `ImageFiles.Load` no longer falls back to ImageSharp for any WebP.
- Tests: `LossyIsRecognisedButNotDecoded` and `AnimatedIsRecognisedButNotDecoded` removed; new `LossyMatchesLibwebp` (20 cases), `LossyDecodesToBgra`, `TruncatedLossyThrows`, `LossyWithoutStartCodeThrows`.
- Fixtures (`.webp` plus reference `.png`): LossyQ75, LossyQ5, LossyQ100, LossyM0, LossyM6, LossyOneSegment, LossySimpleFilter, LossyNoFilter, LossySharp, LossyLarge, LossyFlat, LossyTiny, LossySingle, LossyAlpha, LossyAlphaRaw, LossyAlphaHorizontal, LossyAlphaVertical, LossyAlphaGradient, LossyAlphaQ50, Animated.
- `ImageDecodePerfTests.Formats`: new row `Image.Decode.{library}.Webp.Lossy` (ImageSharp `WebpEncoder`, Lossy, Quality 90, same 1024x1024 atlas crop as the other rows).
- No data XML changed.

## Why these choices

**Byte-identical to libwebp 1.6.0, not merely spec-conformant.**
The port follows libwebp's decode path: boolean decoder, prediction with 127/129 edges, top-right replication for 4x4 blocks, DC-only and full IDCT, loop-filter strengths and order, fancy upsampling with libwebp's YUV constants. Rejected: point upsampling. It is simpler and faster, but no reference decoder outputs it, so there would be no exact test oracle.

**Whole-frame reconstruction, then one filter pass over the frame in raster order.**
libwebp filters with a row delay instead. Both give the same result because intra prediction reads unfiltered samples. Planes carry a top border row of 127, a left border column of 129 and 4 spare right columns, so prediction needs no work buffer. The 4x4 top-right samples are written temporarily into the next macroblock's area, which that macroblock overwrites when it decodes.

**Animation: first frame only, onto a transparent canvas the size of the file.**
Matches `GifDecoder` and what the ImageSharp fallback returned before. A frames API (timings, disposal, blending) is a separate design that GIF should share. The first frame is copied verbatim whatever its blend flag, matching libwebp's anim decoder where the first frame is a key frame.

**Alpha dithering and pre-processing level smoothing are not applied.**
Neither dwebp by default nor Pillow applies them.

**Constant tables are the RFC 6386 values, no new dependency.**
DC/AC quantisers, default and update coefficient probabilities, 4x4 mode probabilities, zigzag, bands and DCT_CAT probabilities were transcribed from ImageSharp 3.1.11's internal tables via reflection, then checked number for number against that source by a script: all 8 tables match.

**Unoptimised, labelled so by the user's instruction.**
Scalar first pass: no SIMD, no profiling-driven work. Release+PROFILE, 3 runs, `--test=Perf`, zone `Image.Decode.*.Webp.Lossy`, 1024x1024 q90, ms per decode:

| Library | p50 | p95 | Worst-frame alloc |
|---|---|---|---|
| Arctis | 31.1-32.8 | 71.2-74.8 | 4,714 KB |
| ImageSharp | 24.8-26.4 | 43.8-61.8 | 431 KB |

Arctis takes about 1.2-1.3x ImageSharp's time, so it is slower. No target is set yet. Same runs: Jpeg.Q90 Arctis p50 3.3-3.6 ms, Gif 3.1-3.2 ms.

## Known gaps
- **Unoptimised.** Candidates: SIMD IDCT, loop filter and upsampler; a cheaper allocation pattern (worst frame allocates 4.7 MB).
- No fixture has more than one token partition: libwebp 1.6.0's encoder ignores `config.partitions` (writes 1 partition even with partitions=3) and cwebp has no option for it. Multi-partition streams are untested; an older libwebp is needed to make one.
- Animated WebP decodes only the first frame; no frames API.
- Pasting a lossy `.webp` into a Thorium note is NOT GUI-verified (stays on the WIP item for pasting .tga/.tif/.webp).
- The ImageSharp fallback in `ImageFiles.Load` remains for CMYK/YCCK and arithmetic-coded JPEG and for unsupported TIFF variants.
- Test-verified: ArctisImaging.Tests 265/265. All 20 lossy and animated fixtures are byte-identical to libwebp 1.6.0 dwebp, the animated one to Pillow 13's first frame. A scratch cross-check of 80 random cwebp encodes (random crops of four photos, all qualities, methods 0-6, 1-4 segments, sns, filter strength and sharpness, simple filter, sharp_yuv, presets, autofilter, alpha with every filter, both alpha methods, alpha_q) was also byte-identical to dwebp.
- `_Build/test.sh`: 509 passed, 8 failed: Boot (known default-sampler error, in the baseline) and 7 LatinModern Tex.* golden mismatches. Not caused by this change (no WebP path reachable from them), not otherwise investigated.
- The Perf suite as a whole reported many budget failures on this machine (42-51 of 61 per run); not investigated, may predate this change.
- No `[Vulkan]` lines.

Related: [[other-raster-formats]], [[jpeg-gif-decoders]]
