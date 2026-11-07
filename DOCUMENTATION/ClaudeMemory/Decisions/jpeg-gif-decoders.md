# Decision — Arctis decodes JPEG and GIF itself

**Date:** 2026-11-06
**Scope:** `Arctis.Imaging` — `JpegDecoder`, `GifDecoder`, `ImageCodec`; `ArctisAurora.Core.Imaging.ImageFiles`; `ArctisAurora.Tests.ImageDecodePerfTests`; `ArctisImaging.Tests` (`JpegTests`, `GifTests`)

## What changed
- ImageSharp is removed from the engine (2026-11-06); there is no fallback. Arctis now also decodes 4-component JPEG (CMYK/YCCK, see "CMYK and YCCK" below). Arithmetic coding (SOF9/SOF10), hierarchical, 12-bit DCT and 2-component files throw `NotSupportedException`. Lossless SOF3 (2-16 bit) and JPEG-LS now decode, see [[lossless-jpeg]].
- JPEG output is byte-identical to libjpeg-turbo 3.2 (Pillow 13); decodes in 0.39-0.83x ImageSharp 3.1.11's time depending on the file. GIF decodes the first frame at ~0.65x ImageSharp's time.
- New `JpegDecoder` (internal static): `TryReadHeader`, `Decode(data, destination, pitch, layout)`. Moved from `ImageCodec.TryReadJpegHeader` (removed, logic unchanged).
- New `GifDecoder` (internal static): `TryReadHeader`, `Decode`. Moved from `ImageCodec.TryReadGifHeader` (removed; now requires 13 bytes instead of 10, the whole logical screen descriptor).
- `ImageCodec.Decode` gains `ImageFormat.Jpeg` and `ImageFormat.Gif` cases; `TryIdentify` probe order unchanged. `ImageFiles.CanDecode` includes Jpeg and Gif.
- `ImageDecodePerfTests`: `Formats(string library, bool foreign)` becomes `Formats(string library)`; the Jpeg.Q90 and Gif rows now run for Arctis too (zones `Image.Decode.Arctis.Jpeg.Q90`, `Image.Decode.Arctis.Gif`).
- Tests: new `GifTests` (test-side GIF LZW encoder), `JpegTests`; fixtures `ArctisImaging.Tests/Fixtures/*.jpg` + matching `*.png` (Pillow's decode): Baseline444/422/420, Quality100, Quality5, Optimized, Restart420, Progressive444/420, ProgressiveRestart, Tiny420 (3x3), Single (1x1), Large420, LargeProgressive (333x211), Grey, GreyProgressive, Cmyk. `OpsAndHeaderTests.JpegHeaderIsIdentified` now expects `ImageFormatException` (its header-only file is malformed now that a decoder exists).

### JpegDecoder internals
| Part | Members |
|---|---|
| Sequential scans | `DecodeSequential` + `SequentialBlock` (decode, transform and write each block at once) |
| Progressive scans | `DecodeProgressive` -> `ProgressiveScan` (inlined once per mode DcFirst/DcRefine/AcFirst/AcRefine) + `ProgressiveBlock` + `RefineAc`; `TransformAll` after all scans |
| IDCT | `Transform` dispatch -> `IdctAvx2` / `IdctVector` (Vector128 with Sse2 or AdvSimd.Arm64 transposes) / scalar `Idct`; `HasAc`, `DcOnly`, `FillBlock` |
| Output | `Output` -> `Row` (upsampling) -> `ColumnSums` + `Interleave` (fancy h2v1/h2v2), `ConvertRow` (AVX2 8 px / Vector128 16 px / scalar) |
| Bit reader | `Bits` ref struct: `Ensure`, `Peek`, `Skip`, `Symbol`, `LongSymbol`, `GetBits`, `Receive`, `Restart`; slow paths `Filled` / `Restarted` |

## Why these choices

**JPEG scope: baseline/extended and progressive Huffman only.**
SOF0/SOF1 and SOF2, 8-bit, 1 or 3 components, any integral sampling, restart intervals. `NotSupportedException` for arithmetic SOF9-15, hierarchical SOF5-7 (lossless SOF3 landed 2026-11-07, see [[lossless-jpeg]]), 12-bit, 2- or 4-component (CMYK/YCCK) and non-integral sampling ratios. User chose (2026-11-06) to leave arithmetic coding (~300 lines, patents expired, rare) and CMYK/YCCK out.

**Colour follows Adobe/JFIF markers; no EXIF orientation.**
Adobe APP14 transform 0 -> RGB, otherwise YCbCr; without APP14, ids 'R','G','B' and no JFIF -> RGB. ImageSharp's Load doesn't apply EXIF orientation either. A truncated file decodes what it holds: past the end the reader feeds zeros.

**Decode like libjpeg-turbo, exactly.**
- islow integer IDCT (6b algorithm); fancy upsampling (h2v1/h2v2 triangle filters when the component is wider than 2, h1v2 for 4:4:0), plain replication otherwise.
- YCbCr via libjpeg's 16.16 fixed-point formulas: R = Y + ((91881·Cr' + 32768) >> 16), G = Y + ((-22554·Cb' - 46802·Cr' + 32768) >> 16), B = Y + ((116130·Cb' + 32768) >> 16). The tables were replaced by the formula they were built from, which reproduces them exactly and vectorises.
- Fancy upsampling is column sums with the edge sums repeated (index 0 and width+1), then one interleave kernel: libjpeg's special first/last-column formulas equal the interior formula with edge replication, so h2v1 (bias 1/2, shift 2) and h2v2 (bias 8/7, shift 4) share one kernel.
- The vector IDCTs do the same integer operations as scalar islow, so they are exact for in-range data and real files (the AVX2 IDCT differs from scalar on extreme synthetic blocks, see "Output SIMD and IDCT trials"); libjpeg's zero-AC shortcuts give the same result as the full arithmetic, so the vector kernels skip them.
- A block whose only coefficient is the DC is a fill with clamp(((dc·q + 4) >> 3) + 128), exactly what islow gives it. Sequential: detected when the first AC symbol is an end-of-block; progressive: `HasAc`, a vector OR of the block with the DC masked.

**GIF scope: first frame only, on a transparent canvas.**
Placed at its offset on a transparent canvas of the logical-screen size (user chose transparent over the background colour, 2026-11-06). Global/local palettes, interlace, GCE transparency, LZW codes 1..11-bit minimum, deferred clear tolerated. A palette index past the table reads opaque black. Data that ends early leaves the rest transparent.

**Register allocation is the main lever in the scan loop.**
With the bit buffer as a ref struct whose slow paths took `this` by ref, buffer and count lived on the stack. Fix: slow paths (`Filled`, `Restarted`, `LongSymbol`) take state by value/return it, and the common bulk refill is inlined into `Ensure`. Rejected: by-value refill with no inlined bulk path (q90 3.29 -> 3.71 ms); the block decoder as a non-inlined method copying state in and out (img20 16.9 -> 19.3 ms); a vector HasAc test instead of a flag in sequential blocks (img105 1.33 -> 1.54 ms).

**Other entropy work kept.**
Whole-coefficient AC lookup (one lookup gives run, length and signed value when code + value bits fit); 8-byte bulk refill up to the first 0xFF (SWAR test (~w - 0x0101...) & w & 0x8080...; a false positive only stops the bulk read early, which is safe); refill check once per coefficient (>=32 bits buffered); sequential scans in their own loop; progressive scans specialised per mode. AC lookup width measured at 10, 12, 13 bits against 11: q90 2.91/2.91/2.98 vs 2.87 ms, img20 15.8/17.2/17.5 vs 16.8; kept 11.

**Progressive refinement uses a nonzero-position bitmask.**
End-of-band runs walked all 63 positions per block; a per-block ulong bitmask of nonzero zig-zag positions (set when a coefficient is first placed; refinement never makes a coefficient zero) visits only nonzero ones: bricks 1.05 -> 0.95 ms. Sequential files no longer clear the planes: a sequential scan writes every block of its components (past the end it decodes zeros), so only components no scan named are cleared, at output. Progressive coefficients and masks are still cleared.

**One pooled table allocation.**
All decode tables (9-bit symbol lookups for DC 0-3/AC 4-7, 11-bit whole-coefficient AC lookups, maxcode, valoffset, symbols, quantisers) are one ArrayPool `int[]` (~58 KB rented); components are an `[InlineArray(4)]` struct in Frame. Steady-state managed allocation per decode: 2-61 bytes (was ~25 KB of `new` tables). Not built: a static prebuilt copy of the Annex K standard tables; sizing the run to the table ids a file defines.

**`[MethodImpl(AggressiveOptimization)]` on the decode loops** follows the existing Arctis.Imaging exception (rule 42a / managed-inflate).

## CMYK and YCCK (2026-11-06)
- `JpegDecoder` accepts 4-component frames: private `ConvertCmykRow`, `Times(a, b)` (a*b/255, rounded). `ReadSegments(data, ref Frame)` holds the segment loop; `Frame` gained `ycc`; `internal static DecodeAbbreviated(data, tables, destination, pitch, layout, bool ycc)` serves JPEG-in-TIFF ([[other-raster-formats]]). `Decode` and `DecodeAbbreviated` share a private `Decode(..., int ycc)`: -1 guesses from the markers, 0 RGB, 1 YCbCr.
- **Inversion follows the browsers, not Pillow (user, 2026-11-06).** Inks are taken as inverted only when an Adobe APP14 marker is present, as Chromium and Firefox do; Pillow always inverts. Nearly every real CMYK JPEG comes from Photoshop and carries the marker, so they agree in practice. RGB = inverted C * inverted K / 255 per channel, opaque alpha.
- **YCCK (Adobe transform 2)** goes through libjpeg's ycck->cmyk step (CMY = 255 - YCbCr->RGB, K passed through), then the same inverted CMYK conversion; matches Pillow/libjpeg.
- **Arithmetic coding (SOF9/SOF10) stays unsupported (user, 2026-11-06).** Default encoders do not write it; it comes from `jpegtran -arithmetic` archiving and some DICOM pipelines. Nothing installed here can produce a test file (Pillow cannot, jpegtran is not installed), so supporting it needs a tool download. Cost: such files now fail to load, where ImageSharp decoded them. 12-bit, lossless and hierarchical were never supported.
- Tests: `JpegTests.FourComponentsMatchPillow` (Cmyk, Ycck, within 1 of Pillow) and `CmykWithoutAdobeMarkerIsNotInverted`; fixtures `Ycck.jpg`/`.png`, `CmykPlain.jpg`, `Cmyk.png`. The old `CmykIsNotSupported` behaviour is gone.
- Unprofiled: the CMYK row conversion is scalar and was never measured. Not checked: YCCK from a real Photoshop file (the fixture is a hand-built flat 8x8).

## Speed (measured 2026-11-06)
Scratch bench = best of 7 in-process, ms per decode; ImageSharp 3.1.11 in the same process.
- Stage split before optimising (Q90 1024² 4:2:0 perf sample): total 12.2, IDCT ~6.2, upsample ~2.7, colour ~2.8, entropy ~1.5; ImageSharp 3.6.
- After SIMD IDCT/colour/upsampling: 4.69 (AVX2) / 4.96 (AVX2 off); ImageSharp 3.64 / 5.50. ImageSharp loses ~half its speed without AVX2 because it uses 256-bit vectors in every stage; ours used AVX2 only in the IDCT at that point.
- Final bench (two passes each, quiet machine):

| File | Arctis ms | ImageSharp ms | Ratio |
|---|---|---|---|
| q90 | 2.81-2.85 | 3.61-3.75 | 0.76-0.78 |
| img105 1920x1200 4:2:0 | 1.30-1.43 | 2.65-2.74 | 0.47-0.54 |
| img20 3840x2400 4:4:4 | 16.8-17.1 | 25.1-25.8 | 0.66-0.67 |
| TouchKeyboard Dark 2736x1539 progressive | 9.6-10.3 | 13.7-15.0 | 0.67-0.74 |
| TouchKeyboard Light progressive | 7.0-7.2 | 10.8-11.0 | 0.65 |
| beard 864x1152 | 0.80-0.83 | 1.35-1.50 | 0.55-0.59 |
| bricks 700x350 progressive | 0.95 | 1.17-1.19 | 0.80-0.82 |

- User target was ~0.5, no more than 0.7, of ImageSharp's time; q90 and bricks miss it. q90 is ~2.0 of 2.8 ms Huffman decoding (~450k symbols, ~4 ns each, a serial shift-load-shift dependency chain; loop counter, buffer and count are in registers), so even zero for every other stage would leave it near 0.58.
- Release+PROFILE `--test=Perf`, 3 runs, `Perf.ImageDecode.Formats`, ms per decode p50 / p95: Arctis Jpeg.Q90 3.64-3.77 / 4.35-4.47 (before this work 13.8-14.1 / 27.9-32.4; after SIMD only 5.45-5.59 / 12.7-13.9); ImageSharp Jpeg.Q90 4.99-5.07 / 11.2-12.6; Arctis Gif 3.20-3.42 / 3.79-4.32; ImageSharp Gif 4.96-5.20 / 6.85-7.24. Arctis JPEG worst-frame alloc 4.0 KB (was 24.7). In-engine the JPEG ratio is 0.73-0.74.

## Entropy attempts (2026-11-07, two reverted, one later landed)
Three attempts to speed up the entropy stage were measured; the first two were reverted and the third was reverted at the time, then landed in conditional form, see "Per-scan refill (2026-11-07)".
- Bench: scratch tool outside the repo (not committed). A frozen copy of the decoder (renamed namespace) vs the repo build vs ImageSharp 3.1.11 in one process, optimized build, best of 7 x 300 ms, ms per decode. Exactness = byte-identical output on 94 files (all ArctisImaging.Tests fixtures incl. JPEG-in-TIFF, every .jpg under C:\Windows\Web, Downloads JPEGs, q90). Noise floor ~±2% between identical builds, single runs up to ±8%.
- Gate per attempt: keep only if it beats the previous version on q90 AND bricks in two passes, without slowing other files beyond noise.
- Hitting ≤0.7 needs q90 ≤ ~2.48 ms and bricks ≤ ~0.84 ms. IDCT+output on q90 is only 0.59 ms, so entropy is the lever.

Stage split (stage switches in a scratch copy, one pass); "entropy" includes per-block work (DC decode, block.Clear, FillBlock for DC-only blocks):

| File | total | entropy | IDCT | output | ImageSharp | ratio |
|---|---|---|---|---|---|---|
| q90 1024² 4:2:0 | 2.79 | 2.19 | 0.30 | 0.29 | 3.54 | 0.79 |
| bricks 700x350 progressive | 0.97 | 0.78 | 0.13 | 0.07 | 1.20 | 0.81 |
| TouchKeyboard Dark progressive | 10.47 | 7.62 | 1.22 | 1.62 | 14.14 | 0.74 |
| img20 3840x2400 4:4:4 | 15.78 | 10.57 | 2.14 | 3.07 | 25.11 | 0.63 |
| img105 1920x1200 (every block DC-only) | 1.21 | 0.40 | 0.01 | 0.80 | 2.52 | 0.48 |

- q90 counts: 24576 blocks, 18932 full IDCTs, 387k whole-table coefficients, 63.5k slow-path symbols (17.2k of them end-of-block), ~4.9 ns per symbol.

Rejected:
- **EOB (0x00) and ZRL (0xF0) in the whole-coefficient table.** Each got its own flag bit in the entry; sequential and progressive AcFirst ended the block on the EOB flag instead of the second `Symbol` lookup. Noise: q90 -1%, -1%, +1%, +8%; bricks +1-2%.
- **Leaner per-block call.** `Transform`, the vector IDCTs and `FillBlock` took refs instead of spans, plane offset a ref. q90 -1% and -4%, bricks unchanged (0.99 = 0.99); failed the gate.
- **Unstuffed scan with a branchless refill.** Each segment copied once up to the next marker into a pooled buffer (0xFF 00 -> 0xFF, 8 zero bytes of padding, `IndexOf` for the 0xFF scan); `Ensure` unconditional: `buffer |= ReadBE64(at) >> count; at = min(at + ((63 - count) >> 3), end); count |= 56`. Consistent over 2 passes plus repeats: q90 -6% (2.77 -> 2.59, ratio 0.80 -> 0.75); large progressive -2%; bricks +2-3% (0.93 -> 0.95-0.96); img105 +3-5% (1.22-1.29 -> 1.28-1.35). Exact on all 94 files. A scratch variant keeping the unstuffed buffer but restoring the `count >= 32` early-out matched the original on every file (q90 2.77-2.78, img105 1.19-1.20, bricks 0.92-0.93). So the unstuff copy costs nothing measurable; the branchless refill is both the q90 win and the loss on DC-heavy and small progressive files.
- Superseded: the per-scan choice (branchless for dense scans, early-out for the rest) was tried later the same day and landed, see "Per-scan refill (2026-11-07)". The unstuffed buffer is kept; the branchless refill is used only for large, dense sequential scans.
- Not attempted then (done later the same day, see "Output SIMD and IDCT trials"): AVX2 upsampling, 16-bit colour conversion, 16-bit (libjpeg-turbo-style) IDCT.
- Verified only under the default ISA; ArctisImaging.Tests not run (final code identical to before).

## Output SIMD and IDCT trials (2026-11-07)
Two AVX2 paths landed in the output stage; output is byte-identical to before. `JpegLossless.cs` unchanged, no data XML touched.
- `JpegDecoder.ConvertRow`: new 16-pixel YCbCr loop in the AVX2 branch, ahead of the 8-pixel loop, which now serves the tail and the RGB copy. New private helpers `Product(Vector256<short> a, Vector256<short> b, Vector256<short> k, Vector256<int> add)` and `Pair(short k0, short k1)`.
- `JpegDecoder.ColumnSums`: new `Avx2` branch before the Vector128 one (now `else if`).
- `JpegDecoder.Interleave`: new `Avx2` branch, 16 sums in and 32 bytes out; the Vector128 loop still handles an 8-wide tail.
- **Colour conversion uses split constants so each multiply pair fits 16-bit `pmaddwd`, exact by construction:** R = y + cr + ((cr·26345 + 2·16384) >> 16) (91881 = 65536 + 26345); G = y − cr + ((cb·−22554 + cr·18734 + 32768) >> 16) (−46802 = −65536 + 18734); B = y + 2cb + ((cb·−14942 + 2·16384) >> 16) (116130 = 131072 − 14942). Clamping is `packssdw`/`packuswb`. Checked exhaustively: all 2²⁴ (y, cb, cr) match libjpeg's formula.
- **AVX2 upsampling needs no permute:** the per-lane unpack and pack (`UnpackLow`/`UnpackHigh` then `PackUnsignedSaturate`) already lands the bytes in pixel order.
- Bench: scratch outside the repo (not committed); frozen copy of the old decoder vs the repo build vs ImageSharp 3.1.11 in one process, optimized build, best of 7 x 300 ms, ms per decode. Final pass:

| File | before | after | ImageSharp | ratio before -> after |
|---|---|---|---|---|
| q90 1024² 4:2:0 | 2.77 | 2.64 | 3.70 | 0.75 -> 0.71 |
| img105 1920x1200 (DC-only) | 1.27 | 1.01 | 2.60 | 0.49 -> 0.39 |
| img20 3840x2400 4:4:4 | 16.50 | 16.38 | 25.04 | 0.66 -> 0.65 |
| TouchKeyboard Dark progressive | 9.97 | 10.02 | 14.18 | 0.70 -> 0.71 |
| TouchKeyboard Light progressive | 10.73 | 10.66 | 14.67 | 0.73 -> 0.73 |
| beard 864x1152 | 0.79 | 0.65 | 1.28 | 0.62 -> 0.51 |
| bricks 700x350 progressive | 0.98 | 0.96 | 1.16 | 0.84 -> 0.83 |

- Per step, two passes each: colour step alone img105 -12/-16%, beard -7%, q90 0/-3%. Adding upsampling on top: img105 1.10 -> 0.91-0.94, beard 0.75 -> 0.65, q90 2.75-2.83 -> 2.68-2.73. Stage split before this work (q90): IDCT 0.28, colour 0.15, upsample 0.13, entropy 2.20 ms.
- **IDCT variants, all rejected.** Measured in a scratch copy in one process, every mode (today's included) through the same per-block test hook:
  - A: 32-bit sparse kernel for blocks whose coefficients all sit in the top-left 4x4, zero products dropped from both passes. Exact by ring identity.
  - B: pass 1 as 16-bit `pmaddwd`, product pairs expanded across row pairs (1,5) and (3,7), so no 16-bit intermediate sums; pass 2 is today's 32-bit code. Guard: every dequantised value fits int16, checked with `pmullw`/`pmulhw`. Used a precomputed 16-bit quant table that exists only in scratch.
  - A+B: A for sparse blocks, B for the rest.
  - C: full 16-bit with no guard, as libjpeg-turbo does it.
  - Over two passes, q90: today's kernel 2.73-2.74, A +3-5%, B +2-3%, A+B +3-8%, C +0-2%. img20 and TouchKeyboard: A and B -1-3%, C -3-5%. beard and bricks: noise.
  - None beats today's kernel on q90, so none was kept. Op counting agrees: the guard and the pair shuffles eat the `vpmulld` savings.
  - Fuzz, 400k random blocks: A, B and A+B match today's AVX2 kernel on every block. C differs on about half, including blocks with coefficients ±200 and quantisers up to 64, because pass 1 overflows 16 bits.
- **Pre-existing finding, not changed:** today's AVX2 IDCT already differs from the scalar `Idct` on extreme blocks, ~3% of the fuzz set, only with coefficients ±2047 or ±32767 times large quantisers; small and in-range blocks match. The claim that all IDCT paths give the same bytes therefore holds for in-range data only; every real file tested is identical.
- Test-verified: ArctisImaging.Tests 348/348. The 94-file check (fixtures incl. JPEG-in-TIFF, every .jpg under C:\Windows\Web, Downloads JPEGs, q90) is byte-identical to the old decoder under default settings, `DOTNET_EnableAVX2=0` and `DOTNET_EnableHWIntrinsic=0`.
- Not checked: the engine suite and the Perf suite; GUI paste of a .jpg into Thorium; ARM (the new paths are AVX2-only, ARM and SSE2-only machines run the unchanged Vector128 code).

## Per-scan refill (2026-11-07)
`JpegDecoder.Bits` reads each segment from a pooled unstuffed buffer and has two refill modes, chosen per sequential scan. Output is byte-identical. Supersedes the third rejected item in "Entropy attempts".
- `Bits`: removed `Filled` and `atMarker`. New constructor `Bits(ReadOnlySpan<byte> data, int pos, byte[] clean)`, fields `clean`, `at`, `end`, `public bool always`, property `Length` (the unstuffed segment length), private static `Unstuff(data, from, clean, out int marker)` (copies up to the next marker with 0xFF 00 -> 0xFF, 8 zero bytes of padding, `IndexOf` for the 0xFF search).
- `Ensure`: `if (!always && count >= 32) return;` then `buffer |= ReadBE64(clean, at) >> count; at = min(at + ((63 - count) >> 3), end); count |= 56`. `GetBits` calls `Ensure` when short. `Restarted` steps over the RSTn, then unstuffs the next interval. `pos` holds the source position of the marker that ended the current segment.
- `Frame.unstuffed`: rented in `Decode` (size max(data, tables) + 8), returned in its `finally`. `JpegLossless.DecodeLossless` passes `f.unstuffed` to `Bits`; progressive and lossless scans never set `always`.
- New private constants `DenseScanBlocks = 4096`, `DenseScanBits = 15`. New private `ScanEnd(data, pos)`: vectorised (`IndexOf`) end of a scan, the first marker that isn't stuffing, fill or RSTn.
- `DecodeSequential` computes the scan's blocks (MCUs x sum of h·v, or the component's blocks for a single-component scan). If blocks >= 4096 it computes the whole scan's bytes (`bits.Length` without restarts, `ScanEnd(data, start) - start` with them) and sets `bits.always` when bytes·8 >= 15·blocks.
- No data XML.

**Refill mode is chosen per scan because neither mode wins everywhere.** Per-scan measurement, scratch copy, same process, best per scan, always-refill vs early-out:

| Scan | blocks | bits/block | always-refill vs early-out |
|---|---|---|---|
| q90 seq | 24576 | 126.6 | -7.5% |
| img20 seq (restart interval 480 MCUs) | 432000 | 15.3 | -3.9% |
| beard seq | 23328 | 8.0 | -0.7% |
| beard2 seq | 23328 | 5.4 | +5.0% |
| img105 seq, DC-only | 54000 | 5.3 | +13.1% |
| Large420 fixture seq | 1764 | 49.8 | +7.6% |
| EncodeLarge420 fixture seq | 1764 | 116.5 | +16.9% |
| TouchKeyboard progressive scans | 66k-198k | 0.6-20.0 | -3.4% to +9.0% |
| bricks progressive scans | 3.9k-11.6k | 0.6-32.2 | -3.8% to +2.3% |

- Density alone doesn't decide: the small synthetic fixtures are dense and still lose, probably because their regular content lets the early-out branch predict well.
- **The thresholds 4096 blocks and 15 bits per block are fitted to ~12 files.** 15 sits between beard (8.0, neutral) and img20 (15.3, a win); 4096 excludes the 1764-block fixtures. A different photo set could move them.
- **Sequential only (user's choice over including progressive `AcFirst`).** Progressive gains were -2 to -3% on a few scans, with +4 to +9% on others.
- **Density is taken over the whole scan, not the first segment (user's choice).** The first version sampled the first restart interval; img20's first interval is 1951 bytes for 1440 blocks (~10.7 bits per block), so the rule never fired on it and img20 moved only -0.2 / -1%. `ScanEnd` costs one vector pass over the scan's bytes, and only on scans with restarts and >= 4096 blocks.
- **The unstuffed buffer costs nothing measurable:** with the early-out kept, the unstuffed reader matched the old SWAR/`Filled` reader on every file.

Speed. Scratch bench outside the repo (not committed): the same code with `DenseScanBlocks = int.MaxValue` (rule off) vs the repo build, ImageSharp 3.1.11 in the same process, optimized build, best of 7 x 300 ms, ms per decode, two passes:

| File | rule off | rule on | ImageSharp ratio (off -> on) |
|---|---|---|---|
| q90 | 2.62 / 2.78 | 2.48 / 2.50 | 0.74->0.70 / 0.77->0.69 |
| img20 | 16.45 / 15.73 | 16.23 / 15.44 | 0.65->0.65 / 0.65->0.64 |
| img105 | 1.10 / 0.92 | 1.08 / 0.92 | ~0.36-0.40 |
| beard | 0.68 / 0.67 | 0.68 / 0.67 | ~0.46-0.52 |
| bricks | 0.94 / 0.93 | 0.97 / 0.95 | 0.80->0.83 / 0.79->0.82 |
| TouchKeyboard Dark / Light | ~9.2-10.5 | ~9.2-10.8 | ~0.66-0.69 |

- q90 decode is -5 to -10%, ratio 0.74-0.77 -> 0.69-0.70, so it meets the <=0.7 target. img20 is -1.3 to -1.8%. bricks (0.82-0.83) still misses.
- An A/A run, identical code on both sides, swings +-6% on TouchKeyboard and 0% on bricks. The progressive differences above are noise; those scans don't take the new path.
- Test-verified: ArctisImaging.Tests 348/348. The 94-file check (fixtures incl. JPEG-in-TIFF and restart-interval files, every .jpg under C:\Windows\Web incl. img20 with restarts, Downloads JPEGs, q90) is byte-identical to the old decoder under default settings, `DOTNET_EnableAVX2=0` and `DOTNET_EnableHWIntrinsic=0`.
- Not checked: the engine suite (`_Build/test.sh`) and the Perf suite; GUI paste of a .jpg into Thorium; ARM.

## RefineAc bitmask stepping (2026-11-07)
`JpegDecoder.RefineAc` (same signature) now steps through zig-zag positions with bitmasks and reads correction bits up to 16 at a time, instead of walking every position. Output is byte-identical.
- Main path keeps a local `nonzero` mask (copied from and written back to the per-block `mask`). Per symbol: zero-history positions are `~nonzero & Band(k, se)`, drop r of them, the next one is the target (se + 1 if they run out). Corrections apply to nonzero positions in [k, target), then ±(1 << al) is placed at the target when s ≠ 0, its bit set with `Math.Min(k, 63)`, then k++. The end-of-band path now calls `Correct` too.
- New private `Correct(ref Bits bits, Span<short> block, ulong pending, int p1, int m1)`: correction bits in zig-zag order, `GetBits(min(popcount, 16))` per read, most significant bit first.
- New private `Band(int from, int to)`: zig-zag positions from through to as a `ulong` mask.
- **Zero history comes from the per-block `mask`, not `coef != 0`**, as the end-of-band path already did. They differ only if a first-scan coefficient wrapped to 0 when shifted into a `short` (8 << 13 = 65536 -> 0), which only out-of-range data produces. The mutation fuzz found no difference.
- **Edge cases kept exactly as libjpeg's `decode_mcu_AC_refine`:** ZRL skips 16 zero-history positions; a run that overshoots se places the coefficient at `Natural[se + 1]` (the padded table) and sets bit `min(se + 1, 63)`.
- The estimate made beforehand (bricks ~0.65 ms) was wrong: walking positions was not where most of the refinement scan's time went.

Speed. Scratch bench outside the repo: old `RefineAc` (copy of the previous tree) vs the repo build, ImageSharp 3.1.11 in the same process, optimized build, best of 7 x 300 ms, ms per decode, two passes:

| File | before | after | ImageSharp ratio (before -> after) |
|---|---|---|---|
| bricks 700x350 progressive | 0.95 / 0.95 | 0.92 / 0.93 | 0.81->0.78 / 0.82->0.80 |
| LargeProgressive fixture | 0.14 / 0.14 | 0.14 / 0.14 | 0.60->0.58 / 0.64->0.61 |
| Pillow p05 q75 4:2:0 progressive | 6.30 / 6.33 | 5.92 / 6.13 | 0.63->0.59 / 0.66->0.64 |
| Pillow p05 q90 4:4:4 progressive | 12.92 / 12.79 | 12.23 / 12.12 | 0.68->0.65 / 0.67->0.64 |
| Pillow p20 q95 4:2:2 progressive | 13.00 / 12.90 | 12.35 / 12.12 | 0.73->0.69 / 0.71->0.66 |

- bricks -2 to -3%, Pillow-made progressive files -3 to -6%. Sequential files and TouchKeyboard (progressive without refinement scans) are within noise; an A/A run swings ±6%.
- Test-verified: ArctisImaging.Tests 348/348. The 94-file check is byte-identical to the old decoder under default settings, `DOTNET_EnableAVX2=0` and `DOTNET_EnableHWIntrinsic=0`. 195 progressive JPEGs made with Pillow 13.0.0 (dev-only, scratch: 39 photos x q50/q75/q90/q95 at 4:2:0/4:2:0/4:4:4/4:2:2 plus greyscale; libjpeg's default progression with refinement scans, ah > 0) are byte-identical under all three configurations. Mutation fuzz, 10k mutants of progressive files with 1-4 random bytes changed in the entropy data: 4633 decoded byte-identically, 5384 threw the same exception type on both sides, 0 differed.
- Not checked: the engine suite and the Perf suite; GUI paste; ARM.
- The scratch benches, the Pillow set and the fuzz are not in the repo.

## Verification
- Test-verified: ArctisImaging.Tests 219/219 (GifTests 18, JpegTests 21, each channel within ±1 of Pillow; actual output is exact).
- Exactness cross-check (scratch tool, not committed): 57 real-world JPEGs on this machine (Windows wallpapers up to 6400x4000 incl. progressive 4:4:4, photos 4:2:0, phone photos 3472x4624) + 4 real GIFs + all fixtures decode byte-identical to Pillow, in three configurations: AVX2, `DOTNET_EnableAVX2=0` (Vector128 kernels), `DOTNET_EnableHWIntrinsic=0` (scalar fallbacks).
- Engine suite `_Build/test.sh`: 509 passed, 8 failed, 2 skipped. Boot (pre-existing baseline error) and 7 LatinModern Tex goldens, identical pixel counts to runs from before this change.
- Perf: all ImageDecode tests PASS in 3 runs, zero `[Vulkan]` lines.
- Not checked: GUI paste of a .jpg/.gif into a Thorium note; 4:4:0 (h1v2) and 4:1:1 (replicate) upsampling (no encoder here writes them); AdvSimd (ARM64) paths never run, only compiled; restart markers inside progressive files only via one fixture.

## Known gaps
- q90 now meets the target (0.69-0.70 after the per-scan refill); small progressive bricks is 0.78-0.80 of ImageSharp (after "RefineAc bitmask stepping"), still above the 0.7 ceiling. Its refinement scan (`RefineAc`) was measured at 0.45 of its 0.93 ms; the stepping cut only ~0.02-0.03 ms of it, so where the rest goes is not located and needs a per-stage split inside `RefineAc`. The per-scan thresholds (4096 blocks, 15 bits per block) are fitted to ~12 files, so a dense photo with regular content could land on the losing side, costing a few percent; progressive scans never use the unconditional refill. EOB-in-table and ref-based per-block calls were rejected, see "Entropy attempts". The IDCT is exhausted at exact variants; only unguarded 16-bit (C) gains, -3-5% on large files, and it is not exact. The AVX2 colour and upsampling paths have no new Vector128 equivalents; the scratch IDCT experiments and fuzz are not in the repo.
- The Perf `Jpeg.Q90` sample is now encoded by Arctis (see [[jpeg-encoder]]), so the Perf numbers above predate that change and are not directly comparable to new runs.
- Arithmetic-coded, 12-bit DCT and hierarchical JPEG fail to load (no ImageSharp fallback any more); lossless SOF3 at 2-16 bits now decodes, see [[lossless-jpeg]]; animated GIF frames beyond the first are ignored.
- The "Speed" tables and ratios below compare against ImageSharp 3.1.11; the `Perf.ImageDecode.Formats.*` benchmarks that produced them are removed, so they are history and cannot be re-run.
- Review finding open: `GifTests.Frame` is a class (aurora-code rule 30).
- The scratch cross-check and benches are not in the repo.

- 512/256-bit tiers for `ConvertRow`, `ColumnSums`, `Interleave`, `HasAc` and the paired progressive IDCT (`IdctAvx512Pair`): see [[simd-width-tiers]].

Related: [[other-raster-formats]], [[png-decode-fast-paths]], [[png-deflate]], [[lossless-jpeg]], [[simd-width-tiers]]
