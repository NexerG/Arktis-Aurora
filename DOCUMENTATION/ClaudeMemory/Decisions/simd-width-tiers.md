# Decision — every SIMD kernel in Arctis.Imaging gets a 512-, 256- and 128-bit tier

**Date:** 2026-11-07
**Scope:** `Arctis.Imaging` (project `ArctisImaging`) — `Crc32`, `Deflater`, `PngEncoder`, `PngDecoder`, `PixelRows`, `JpegDecoder`, `DistanceField`

## What changed
- Rule (user, 2026-11-07): every vector width is a target where the algorithm allows it; each tier gated on its own, tail goes to the next narrower tier, then scalar. Written into [[simd-data-layout]] (".NET specifics"); these codecs are its first users.
- Every new x86-intrinsic tier is gated on both the ISA and the matching `VectorNNN.IsHardwareAccelerated`.
- No data XML read or added.

| Kernel | New tiers |
|---|---|
| `Crc32.Fold(uint, ReadOnlySpan<byte>)` | VPCLMULQDQ under `#if NET9_0_OR_GREATER`: four 512-bit accumulators (256 B/step) when `Vector512.IsHardwareAccelerated && Pclmulqdq.V512.IsSupported && n >= 256`; else four 256-bit (128 B/step, `Pclmulqdq.V256`); else the old 4x128 path. Accumulators folded together, lanes folded to 128, then the existing 16-byte tail + Barrett. Helpers `Fold(Vector512<ulong>, Vector512<ulong>)`, `Fold(Vector256<ulong>, Vector256<ulong>)`, `Broadcast(Vector128<ulong>)` |
| `Deflater.Common` | Vector512 above Vector256, Vector128 below (ARM gets a vector match-length compare) |
| `Deflater.Adler32` | Avx512BW (64 B/step, weights 64..1, `vs3 << 6`) and Avx2 (32 B/step, `<< 5`) above SSSE3; one `fixed` |
| `PngEncoder.Encode` filter-cost loop | Avx512BW and Avx2 above SSSE3 |
| `PngDecoder.AddUp` | Vector512, Vector256 |
| `PngDecoder.Expand` | gray8 (16 grey bytes broadcast to every 128-bit lane, pshufb per lane), RGB8 (`PermuteVar16x32` / `PermuteVar8x32` gives each lane its 12 bytes, then pshufb), RGBA16 (pshufb byte swap, portable Widen/Narrow); Avx512BW and Avx2 above the Vector128 ones |
| `PixelRows.Pack24` | VBMI-only `Vector512.Shuffle` tier replaced by Avx512BW in-lane `Shuffle` + `Avx512F.PermuteVar16x32` (no VBMI needed); new Avx2 tier (`Avx2.Shuffle` + `Avx2.PermuteVar8x32`, 8 px/step); Vector128 x4 kept |
| `JpegDecoder.ConvertRow` | Avx512BW (32 px/step in 16-bit lanes, `Avx512F.PermuteVar8x64x2` restores order, plus a 16-px int path); private `Product(Vector512<short>...)`. Avx2 gate now `Vector256.IsHardwareAccelerated && Avx2.IsSupported`; Vector128 tier is a plain `if` (falls through) |
| `JpegDecoder.ConvertCmykRow` (2026-11-08) | Avx512BW (32 px/step in 16-bit lanes, qword permutes), Avx2 (16 px, `Permute2x128`), Vector128 (16 px, int-lane YCbCr, zip store); scalar tail; `[AggressiveOptimization]`. 512/256 reuse `ConvertRow`'s `Product` and `Pair`; see [[jpeg-gif-decoders]] |
| `JpegDecoder.ColumnSums`, `Interleave` | Avx512BW (32 px); same gate/fall-through change |
| `PcxDecoder.ReadLine` literal scan (2026-11-08) | 512 (Avx512BW, 64 B/step), 256 (Avx2, 32 B), 128 (portable `Vector128`, 16 B); store the input vector unconditionally, advance by tzcnt of the bytes >= 0xC0 mask. The 512 tier measured slower than 256 on Zen 5 (8-12% in isolation) and was kept by user choice; see [[ico-simple-rasters]] |
| `PixelRows.BitIndices`, `PackedIndices`, `LookupIndices` (moved from `PcxDecoder` 2026-11-08; also used by `BmpDecoder.DecodeIndexed`) | 512 (Avx512BW), 256 (Avx2), 128 (portable `Vector128`, no `IsHardwareAccelerated` gate); byte shuffle from 16-byte tables, the 128 tier uses variable-index `Vector128.Shuffle`; see [[ico-simple-rasters]] |
| `PcxDecoder.InterleavePlanes` (2026-11-08) | portable `WidenLower/WidenUpper` + shift ladder at 512 (64 px, Avx512BW-gated), 256 (32 px, Avx2-gated), 128 (16 px); no permute needed |
| `JpegDecoder.HasAc` | Vector512 (two loads), Vector256 (four loads) |
| `JpegDecoder.TransformAll` (progressive only) | `Vector512.IsHardwareAccelerated && Avx512F.IsSupported`: two horizontally adjacent blocks of one component that both have AC go through `IdctAvx512Pair`. Private `Row512`, `Butterfly(Vector512...)`, `Transpose(Vector512...)` (both 8x8 halves at once; qword permutes do Permute2x128's job), `StorePairRows` |
| `JpegEncoder.PredictRow` (lossless encode differences, 2026-11-09) | Vector512, Vector256, Vector128 (portable `Widen`, prediction in shorts, subtract, store); scalar tail; per-tier timing not measured, AVX-512 machine only |
| `DistanceField.Interleave` (ADF decode) | Avx512BW (64 px, qword permutes) and Avx2 (32 px, Permute2x128) above SSE2 |

CRC fold constants per distance D bits = (reflect(x^(D+32) mod P) << 1, reflect(x^(D-32) mod P) << 1): D=2048 (0x011542778a, 0x01322d1430), 1024 (0x01e88ef372, 0x014a7fe880), 512 = existing k1k2, 384 (0x003db1ecdc, 0x0174359406), 256 (0x00f1da05aa, 0x015a546366), 128 = existing k3k4. Derivation checked by reproducing k1k2 and k3k4 exactly.

## Why these choices

**All widths are targets.** Supersedes "Vector512 is not a default target" in [[simd-data-layout]] and the "No Vector256 path in `Pack24`" bullet in [[simple-raster-encoders]] (retracted there). That bullet claimed a cross-lane 256-bit byte shuffle needs VBMI; in-lane pshufb plus a dword permute needs only AVX2 (512: AVX-512BW).

**Shuffle-based 256/512 tiers use x86 intrinsics, not portable `Vector256/512.Shuffle`** (user, after measurement). Scratch microbench, Ryzen 9 9900X (Zen 5), 16 MB buffer, best of 15, ms:
- .NET 8.0.31, portable Shuffle with the index vector in a local: V128 7.4, V256 6.0, V512 5.4 (software fallback, ~25x slower).
- .NET 8, same index written inline at the call: V128 0.32, V256 0.29. `Avx2.Shuffle` 0.29, `Avx512BW.Shuffle` 0.24-0.29.
- .NET 10.0.12: every form 0.24-0.34.
- So on .NET 8 a portable Shuffle is accelerated only with an inline constant index, and portable has no dword permute, so compaction would need VBMI. Rejected: portable with inline indices — works for in-lane shuffles only; Pack24/RGB8 compaction would still need VBMI.

**JPEG 512 IDCT = paired blocks** (user fork F1-b). One 8x8 block is exactly eight Vector256 rows, so 512 bits means two blocks at once. Only in `TransformAll` (progressive): baseline/sequential JPEGs transform a block as soon as it is decoded (`SequentialBlock`), so pairing them would mean holding one block back. Tried 2026-11-08 in a scratch copy and rejected: 2-8% slower on dense files. The progressive pairing itself measured no gain on Zen 5 (one photo pair; code left in place, undecided). See [[jpeg-gif-decoders]] "Sequential decode trials (2026-11-08)".

**Crc32 wide tiers are net9+ only.** .NET 8 has no `Pclmulqdq.V256/V512`; the net8 build keeps the 128 fold.

**Left out on purpose:**
- Paeth/Sub/Average unfilters (`Paeth4`, `Paeth4Rows`, `PaethWide`, `Sub4/8`, `Average4/8`): each pixel depends on the one to its left, so a wider vector cannot hold more pixels.
- Inflater match copy and the DistanceField tile copy: they over-write past the copy end and the window/buffer slack is sized for 16-byte vectors (`fastLimit = outEnd - MaxMatch - 32`); wider copies need that slack re-checked.
- `JpegEncoder` and the other codecs had no SIMD before and got none.

## Measurements
Scratch bench `Program.cs` (not committed), Release, Ryzen 9 9900X, synthetic 2048² RGBA (gradient + noise) unless noted, median of 15 timed calls after 4 warm-ups, ms per call; base = working tree before this change (incl. the other session's encoder work); interleaved base/new.
- .NET 8: BMP 24-bit encode 5.9-7.1 -> 0.44-0.61; PNG gray8 decode 12.2-12.9 -> 4.6-5.6; PNG RGB8 decode 25.2-26.7 -> 17.8-18.0; PNG RGBA16 decode 93-97 -> 69-72. Unchanged within noise: PNG encode ~750, PNG RGBA/BGRA decode, Brick.png decode, JPEG 4:2:0/4:4:4 decode 8.2-10.6, 12 ADF atlases 125-133.
- .NET 10: gray8 5.9-6.2 -> 5.5-5.8; RGB8 23.6-25.2 -> 22.4-23.8; RGBA16 65-72 -> 61-64; everything else within noise (PNG encode 733-751 vs 734-747).
- End to end the wide kernels barely show because entropy coding / inflate / deflate dominate.

## Verification
- Test-verified: `ArctisImaging.Tests` 433/433 on net10.0 under each forced tier — default (512), `DOTNET_PreferredVectorBitWidth=256`, `DOTNET_EnableAVX2=0` (128), `DOTNET_EnableHWIntrinsic=0` (scalar). PreferredVectorBitWidth=256 and EnableAVX2=0 turn `Vector512.IsHardwareAccelerated` off; `DOTNET_EnableAVX512F=0` does not on .NET 10 (it is IGNORED there); `DOTNET_EnableAVX512=0` does drop to the 256 tier. Confirmed again with the XBM expand in [[ico-simple-rasters]].
- 11 bench cases' outputs hashed, byte-identical to the pre-change baseline under all four tiers on net8.0 and net10.0. Builds clean Release net8.0 + net10.0, 0 warnings.
- Tests cover widths 37 and 301 for every PNG colour type/depth, CRC at every length 0-616, progressive JPEG fixtures.
- Not checked: ARM64, an AVX-512 CPU without VBMI or with only AVX2 hardware (simulated by env vars only), test suite on net8 (bench hashes only), `_Build/test.sh` engine suite.

## Known gaps
- Sequential (baseline) JPEG has no 512 IDCT.
- Crc32 512/256 only on .NET 9+.
- Existing portable Vector128 shuffles with a local index vector (PNG `Expand` 128 tier, `Pack24` 128 tier, `ImageOps.CopySwizzled` all three widths) hit the .NET 8 software fallback; e.g. PNG->BGRA decode .NET 8 19.9 ms vs .NET 10 14.4. Fix (inline index vectors or intrinsics) undecided.
- Inflater / tile copies stay 128-bit pending a slack check.

Related: [[simd-data-layout]], [[simple-raster-encoders]], [[jpeg-gif-decoders]], [[managed-inflate]], [[png-decode-fast-paths]], [[distance-field-format]]
