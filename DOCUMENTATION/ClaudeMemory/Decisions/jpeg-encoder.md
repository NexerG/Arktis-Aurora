# Decision — Arctis writes baseline JPEG itself, byte-identical to libjpeg-turbo

**Date:** 2026-11-06
**Scope:** `Arctis.Imaging` — `JpegEncoder`, `JpegSettings`, `ChromaSubsampling`, `ImageCodec`, `Image`; `ArctisAurora.Tests.ImageDecodePerfTests`; `Thorium/Tests/LatinModernTests.cs`; `ArctisImaging.Tests/JpegEncodeTests.cs`

## What changed
- New `JpegEncoder` (internal static), `Encode(pixels, width, height, pitch, layout, output, in JpegSettings)`. Regions: sampling (`Sample`, `Downsample`), transform (`Quantisers`, `TransformAll`, `Fdct`, `Odd`, `Descale`, `Quantise`), entropy coding (`Scan`, `Symbol`, `Length`, `Standard`, `Optimal`, `Codes`, `Headers`, the `Writer` ref struct), standard tables (Annex K quantisers and Huffman specs).
- New `JpegSettings` readonly struct (Types.cs): ctor `(int quality, ChromaSubsampling subsampling = S420, bool optimize = false)`; `Quality` (0 means `DefaultQuality` = 75), `Subsampling`, `Optimize`, internal `Check()` (ArgumentException outside 1-100). `default(JpegSettings)` = q75, 4:2:0, standard tables = libjpeg's/Pillow's default.
- New `ChromaSubsampling` enum: `S420`, `S422`, `S444`.
- New `ImageCodec.EncodeJpeg(ReadOnlySpan<byte> pixels, int width, int height, int rowPitch, PixelLayout layout, Stream output, in JpegSettings settings = default)`; size checks 1-65535 per side, pitch/span checks like `EncodePng`.
- New `Image.SaveJpeg(Stream, in JpegSettings = default)` and `SaveJpeg(string, in JpegSettings = default)`.
- Writes grey (from R8/RG8) and YCbCr (from RGBA8/BGRA8, alpha dropped), quality 1-100, standard or two-pass optimal Huffman tables.
- ImageSharp no longer encodes JPEG in the engine or Thorium tests: `ImageDecodePerfTests` `Jpeg.Q90` sample is `ImageCodec.EncodeJpeg(crop, …, new JpegSettings(90))` (4:2:0); `LatinModernTests` writes `photo.jpg` with `picture.SaveJpeg(path)` (q75 4:2:0).
- Tests: `JpegEncodeTests`; fixtures `ArctisImaging.Tests/Fixtures/Encode*.png` sources (EncodeSource 61x37, EncodeEven 50x34, EncodeLarge 333x211, EncodeTiny 3x3, EncodeSingle 1x1, EncodeGrey 45x29 L) plus Pillow-written `Encode*.jpg`: 420/422/444 Q75, 420Q90, 420Q100, 420Q5, 420Optimized, 444Optimized (q90), EncodeEven420/422, EncodeLarge420, EncodeLargeOptimized (q85), EncodeTiny420, EncodeSingle420, EncodeGrey, EncodeGreyOptimized.

## Why these choices

**Byte-identical to libjpeg-turbo 3.2 (Pillow 13), not "within tolerance".**
User's choice, 2026-11-06, to fit the decoder, which is byte-identical to libjpeg-turbo. It makes the tests exact file comparisons. Rejected: float or approximate FDCT/quantiser shortcuts with a pixel tolerance, which would have meant no exact oracle.

To be exact the encoder reproduces:
- libjpeg's 16.16 RGB→YCbCr: Y=(19595R+38470G+7471B+32768)>>16; Cb=(-11059R-21709G+32768B+8421375)>>16; Cr=(32768R-27439G-5329B+8421375)>>16, 8421375 = (128<<16)+32767 (libjpeg's 0.5-epsilon rounding).
- h2v1 downsampling (bias 0,1 alternating per row) and h2v2 (bias 1,2).
- Edge padding as libjpeg: columns past the image replicate the last pixel at full resolution; rows replicate the last image row only up to a multiple of vmax at full resolution; after downsampling the last downsampled row is repeated down to the plane height. Repeating at full resolution all the way down gives different chroma when the height is even but not a multiple of 16 (`EncodeEven` covers this).
- Dummy blocks past a component's blocks-in-width/height inside an MCU: AC zero, DC copied from the previous block.
- jfdctint islow (6b algorithm, output scaled by 8).
- Quantisation as round-half-away of |x|/(8q), equal to libjpeg-turbo's reciprocal multiply.
- `jpeg_quality_scaling` (q<50: 5000/q, else 200-2q; (base·scale+50)/100 clamped 1..255, force-baseline).
- JFIF 1.01 APP0 (density 1:1, units 0), one DQT marker per table, SOF0 with ids 1-3, one DHT per table in order DC0, AC0, DC1, AC1, SOS, pad bits of 1s.

**Optimal Huffman is offered (`Optimize`).**
User's choice, 2026-11-06 (fork 2b). Straight port of libjpeg's `jpeg_gen_optimal_table`, including tie-breaking (larger symbol wins), the 1e9 frequency ceiling and pseudo-symbol 256. Deviation: the code-size count array has 257 entries rather than libjpeg's 33, so a tree deeper than 32 is folded rather than erroring; output is the same whenever libjpeg succeeds. The pass stores all quantised coefficients (zig-zag order) once and walks them twice (count, then write); the DCT is not recomputed.

**Scalar only, no perf work in this change.**
User said "keep performance out right now" on 2026-11-06. No SIMD tiers and no `AggressiveOptimization`: rule 42a's Arctis.Imaging exception covers decode loops only. Encoding speed is unmeasured and has no target.

**Whole-image planes and coefficient buffer, pooled.**
Component planes, one row group of conversion scratch, the coefficients (blocks·64 shorts), one int[] of tables (quantisers, codes/sizes, optimal-table scratch), the specs and a 64 KB output chunk all come from ArrayPool and are returned in a finally. Simplest code; cost is about 3 bytes/pixel of planes plus 2 bytes per coefficient.

**Not built:** progressive output, restart intervals, arithmetic coding, CMYK, 12-bit, EXIF/ICC/DPI metadata.

## Verification
Test-verified: ArctisImaging.Tests 242/242 on net10.0, with `JpegEncodeTests` 23/23 byte-identical to the Pillow fixtures. They cover q5/75/90/100, 4:2:0/4:2:2/4:4:4, optimized colour and grey, R8 and RG8 grey, BGRA equal to RGBA, a pitched source, odd sizes 1x1/3x3/61x37/333x211, the even-height 50x34, a decoder round trip, and bad quality/span throwing. Thorium (and so AuroraEngine) builds with 0 errors after both call-site swaps.

Not checked: the engine suite `_Build/test.sh` (including the LatinModern PDF-export test that now embeds an Arctis-written photo.jpg); the Perf ImageDecode run; the net8.0 test target; encode speed; ARM.

## Known gaps
- Encoding speed unmeasured; scalar only (no SIMD FDCT/colour/downsample).
- `_Build/test.sh` and the Perf suite have not been run since the swap.
- The Perf `Image.Decode.*.Jpeg.Q90` sample is now Arctis-encoded, so baseline numbers in [[jpeg-gif-decoders]] predate the change and are not directly comparable.
- The Pillow fixture generator script is not in the repo.

Related: [[jpeg-gif-decoders]], [[png-deflate]], [[other-raster-formats]]
