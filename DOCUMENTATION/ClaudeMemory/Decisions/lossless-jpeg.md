# Decision — Arctis.Imaging decodes lossless JPEG (SOF3) and JPEG-LS, and encodes lossless JPEG

**Date:** 2026-11-07 (lossless JPEG encoder 2026-11-09)
**Scope:** `Arctis.Imaging` — `JpegDecoder` (partial, `JpegLossless.cs`), `JpegLsDecoder`, `JpegEncoder` (partial, `JpegLosslessEncoder.cs`), `JpegLosslessSettings`, `ImageCodec`, `Image`, `ImageFormat`

## What changed
- `ImageFormat.JpegLs` added. Lossless JPEG stays `ImageFormat.Jpeg`.
- `JpegDecoder` is now `partial`. New file `JpegLossless.cs` holds `ReadLosslessFrame`, `DecodeLossless`, `OutputLossless`.
- `Frame` gains `lossless`, `precision`, `pointTransform`, `ushort[] samples`. `ReadSegments` handles SOF3 (0xC3). `ReadScan` routes lossless scans to `DecodeLossless(…, predictor = Ss, pt = Al)`.
- New private `IsYcc(in Frame)` holds the existing three-component colour-space rule; `Output` now calls it.
- `JpegDecoder.TryReadHeader` recognises SOF55 (0xF7) and returns `ImageFormat.JpegLs`.
- New internal static `JpegLsDecoder` with only `Decode(ReadOnlySpan<byte>, Span<byte> destination, int pitch, PixelLayout)`; its header comes from `JpegDecoder.TryReadHeader`.
- `ImageCodec.Decode` has a case for `JpegLs`. Tests: `JpegLosslessTests`, `JpegLsTests`. Fixtures: `Ls*.jls`, `Lj*.jpg` in `ArctisImaging.Tests/Fixtures`, each with a reference .png.
- Decoders for both; an encoder for lossless JPEG only (2026-11-09, see "Lossless JPEG encoder (2026-11-09)" below), JPEG-LS has no encoder. Lossless JPEG optimised 2026-11-08 (see "Lossless JPEG optimised (2026-11-08)" below): scalar entropy decode, SIMD output reduce, 0.61-0.68x libjpeg-turbo (was 1.1-2.2x on 2026-11-07). JPEG-LS: scalar, optimised (A, B, E2 landed 2026-11-07, E3, E4 and E5 landed 2026-11-08), 8-bit RGB ~0.96x CharLS, i.e. faster than CharLS in every bench case except RGB NEAR=2 at ~1.03x (tables below).

## Measured speed (2026-11-07)

Method: scratch console bench, not a test, not committed (no perf tests, see [[other-raster-formats]]). Release, net10.0, Windows, Ryzen 9 9900X. Inputs: `Brick.png` + `Brick2.png` (two 1024² RGBA) encoded by Pillow 13 / imagecodecs 2026.10.10 (CharLS for JPEG-LS, libjpeg-turbo 3.2 lossless for SOF3). Per case the 2-image set is decoded per pass; 40 warm-up passes, 6 timed passes, median ms for the set; 3 processes; ratio = Arctis / reference, range over the 3 processes. Arctis decodes via `ImageCodec.Decode` with a format hint into a preallocated buffer (RGBA8 colour, R8 grey). References are native C decoders called from Python (CPython 3.14): a cross-runtime ratio, indicative only.

Lossless JPEG (SOF3) vs libjpeg-turbo 3.2, ms for the set:

| Case | Arctis | libjpeg-turbo | Ratio |
|---|---|---|---|
| RGB predictor 1 | 33.2-33.7 | 26.8-28.7 | 1.2-1.3x |
| RGB predictor 7 | 32.9-45.6 | 29.3-30.8 | 1.1-1.6x |
| grey 8-bit | 13.2-19.3 | 8.8-8.9 | 1.5-2.2x |
| grey 12-bit | 13.4-15.9 | 9.8-9.9 | 1.4-1.6x |

The 45.6 and 19.3 highs are process 1; processes 2-3 were ~33 and ~13.5.

JPEG-LS vs CharLS, before the optimisation below (the same-day baseline; re-measured later and matched within noise):

| Case | Arctis | CharLS | Ratio |
|---|---|---|---|
| RGB (ILV from imagecodecs default) | 252-269 | 50.7-50.9 | 5.0-5.3x |
| RGB NEAR=2 | 219-225 | 49.7-50.2 | 4.4-4.5x |
| grey 8-bit | 56.7-58.5 | 23.2-24.3 | 2.3-2.5x |
| grey 12-bit | 68.7-69.2 | 33.3-35.9 | 1.9-2.1x |

- Decoded outputs were not compared against the references in this bench (correctness is covered by the existing tests). Not measured: ARM64, net8.0, restart intervals.

## JPEG-LS optimised (2026-11-07)

Scalar, single-threaded, byte-identical output. About 2.7x faster on RGB and 1.8-1.9x on grey; after option A 1.4-2.2x CharLS (was 1.9-5.3x); with option B 1.3-2.0x; with the E2 pass 1.1-1.8x.

What changed (private members of `Arctis.Imaging.JpegLsDecoder`):
- New private structs `ContextState { a, b, c, n }` and `RunState { a, n, nn }`.
- `Coder.state` (int[]) removed; replaced by `Coder.contexts` (ContextState[365]), `Coder.runs` (RunState[2]) and `Coder.quantise` (sbyte[2*maxval+1], indexed d + maxval). All three are rented from ArrayPool in `ReadScan` and returned in its finally.
- Constants `AAt, BAt, CAt, NAt, RunAAt, RunNAt, RunNnAt, StateLength` removed.
- `Initialise` builds the quantisation table from `Quantise`; `Context` is three table lookups; `Quantise` is used only to build the table.
- New `GolombK(int n, int a)`: least k with n << k >= a, from LeadingZeroCount plus one correction; replaces the shift loops in `Regular` and `InterruptionError`.
- `Bits.Fill` fast path: if the previous byte was not 0xFF, the stream has not ended and 8 bytes remain, a big-endian ulong load plus a SWAR "any byte == 0xFF" test, and if none is found it takes (63-count)>>3 bytes at once; otherwise the existing byte loop. `Bits.GetBits` lost its `n == 0` branch (every caller passes n >= 1).
- `DecodeSampleInterleaved` (ILV 2) stores lines pixel-interleaved (component i of position p at p*count+i, within one row of stride*count ints); new `StoreInterleaved(line, component, count, plane)` de-interleaves into the planes.
- `Output` uses a `byte[]` reduce table of length max(precision max, LSE maxval)+1, built from `Reduce`, instead of calling `Reduce` per sample.
- `[MethodImpl(MethodImplOptions.AggressiveOptimization)]` on `DecodeLine` and `DecodeSampleInterleaved`, under the existing Arctis.Imaging exception to aurora-code rule 42a (see [[managed-inflate]]).

Decisions:
- **Quantisation by lookup table, not the 9-branch `Quantise` chain three times per sample.** Largest single win: RGB 236 -> 114 ms and grey 8 58 -> 40 ms for the set (together with the context structs and `GolombK`, measured in the same step). At 16 bits the table is 128 KB (CharLS does the same). Rejected: keeping the branch chain, unpredictable on photographic data.
- **Context state as an array of structs**, not four offset slices of one int[]: one ref, one bounds check, A/B/C/N on one cache line.
- The Fill fast path was a small win: grey 12 52 -> 48 ms, other cases within noise.
- ILV 2 pixel-interleaved lines (the user's "option A"): RGB 111 -> 105 ms.
- **Option B was landed (user's choice, 2026-11-07).** A plus a hand-unrolled 3-component `DecodeTriplets` (`[MethodImpl(AggressiveOptimization)]`, per-component ra/rb/rc/rd locals, three `Regular` calls written out, same pixel-interleaved line layout as `DecodeSampleInterleaved`). `ReadScan` routes ILV 2 with exactly 3 components to it; other ILV 2 component counts still use `DecodeSampleInterleaved`; ILV 0/1 use `DecodeLineInterleaved`. Consistently ~7% faster on RGB (97-98 ms vs 103-108 for A; NEAR=2 95-98 vs 101-114; 3 processes each), byte-identical. Accepted cost: most of the ILV 2 loop is written twice.
- `AggressiveOptimization` A/B (3 processes each): warm decode within noise; the first decode of a grey image drops from 30-33 ms to 22-24 ms, so it was kept. The first RGB decode is ~118-125 ms either way.
- The reduce table in `Output` was neutral on the bench (removes a per-sample branch and the below-8-bit division).
- Not done, deliberately: a separate lossless (NEAR=0) Reconstruct (its branches are well predicted); removing the planar `samples` stage (ILV 0 needs it); threads.

Measured 2026-11-07, same method as above (scratch bench, 40 warm-up + 6 timed passes, median ms for the 2-image set, 3 processes; ratios per process pair). The before column re-measured the old decoder and matches the table above within noise.

| Case | Before | After | CharLS | Ratio after |
|---|---|---|---|---|
| RGB (ILV 2) | 235-239 | 108-114 | 51.1-51.6 | 2.1-2.2x |
| RGB NEAR=2 | 200-202 | 104-109 | 51.2-53.3 | 1.9-2.1x |
| grey 8-bit | 57-59 | 41-43 | 23.3-23.6 | 1.7-1.8x |
| grey 12-bit | 69-73 | 48-52 | 35.0-36.4 | 1.4-1.5x |

The table above is after option A. After option B (same method; grey rows are re-measurements, B does not touch ILV 0/1):

| Case | After B | CharLS | Ratio |
|---|---|---|---|
| RGB (ILV 2) | 100.8-102.4 | 51.1-51.6 | 1.95-2.0x |
| RGB NEAR=2 | 97.5-98.2 | 51.2-53.3 | 1.8-1.9x |
| grey 8-bit | 41.3-45.0 | 23.3-23.6 | 1.75-1.9x |
| grey 12-bit | 47.8-48.7 | 35.0-36.4 | 1.3-1.4x |

Verified: `dotnet test ArctisImaging.Tests` 433/433; SHA-256 of the 4 bench cases' output unchanged before and after every step; a new scratch random cross-check (not committed) 120/120 byte-identical against CharLS (random crops of the Brick photos, grey ILV 0 / RGB ILV 2 / RGBA ILV 1, bit depths 2/3/5/8/10/12/14/16, NEAR 0/1/2/5/10, 30% flattened for run mode); solution builds clean, no new warnings in `JpegLsDecoder`. Not checked: ARM64, net8.0, the engine `_Build/test.sh` suite, GUI paste of a .jls in Thorium. After option B: `ArctisImaging.Tests` 433/433, 120-file CharLS cross-check 120/120 byte-identical, the 4 bench-case output hashes unchanged; same not-checked list (ARM64, net8.0, engine suite, GUI).

### E2 pass (2026-11-07)

A further pass on top of A and B. Output byte-identical. Changes (private members of `Arctis.Imaging.JpegLsDecoder`):
- `[MethodImpl(MethodImplOptions.AggressiveInlining)]` on `Context`, `Predict`, `Reconstruct`, `DecodeValue`, `GolombK`, `Bits.GetBits`. First use of AggressiveInlining in this file; aurora-code has no rule on it.
- `Predict` was written in a form meant to be branchless: min/max of Ra and Rb, p = Ra + Rb - Rc, then `p = rc >= max ? min : p; return rc <= min ? max : p;`. Same result as the MED. (Retracted: E3 found it compiled to branches in `DecodeLine` and `DecodeTriplets`.)
- In `Regular`, `Math.Abs(err)` became `(err ^ (err >> 31)) - (err >> 31)`.
- `Output`: one-component output to `PixelLayout.R8` writes `reduce[sample]` straight into the destination row (no RGBA row, no `PixelRows.FromRgba`). Exact because `PixelRows.Luma` weights sum to 65536, so Luma(g,g,g) == g. RGBA8 output also writes straight into the destination row; other layouts still go through the RGBA row and `FromRgba`.

Decisions (same method as above; the JIT assembly is from `DOTNET_JitDisasm`):
- **Branchless Abs and MED, plus inlining the small helpers.** The assembly showed `Math.Abs` compiled to a branch on the error's sign, which mispredicts about half the time; the MED branches are data-dependent too. Together: RGB 101 -> 87-90 ms, grey 8 41-45 -> 34-36.
- **Direct Output paths:** grey 12 46 -> 40-43, grey 8 -> 32-34, RGB unchanged.
- Rejected, measured:
  - **(a) Bit reader in registers.** A local copy of `Bits` in `DecodeLine`, the slow refill split into a NoInlining by-value `Refilled(Bits)`, run mode through a by-value copy, everything inlined. The JIT did keep buffer/count in registers (checked in the disassembly), but warm grey 8 stayed at 41 ms, so memory round-trips on the bit state were not the bottleneck. RGB 105 vs 101.
  - **(b) Branch-free clamp batch.** Math.Clamp -> Min/Max in `Regular` and `Reconstruct`, the `k == 0 && near == 0` error flip as a mask, dropping `ReadZeros`' `buffer == 0 ? 64` test. Neutral to slightly worse: RGB 91-95 vs 87-90.
  - **(c) Also forcing inlining of `Regular` and `ReadZeros`** (user chose "warm"). The first, cold RGB decode drops from ~104-113 to 67-71 ms and NEAR=2 goes 87-93 -> 80-82, but warm RGB gets worse (98-103 vs 87-91) and grey 12 slightly worse (46-50 vs 40-43). Kept out in favour of warm speed. This is the lever if cold first-decode time ever matters.

After E2:

| Case | After E2 | CharLS | Ratio |
|---|---|---|---|
| RGB (ILV 2) | 85.0-86.8 | 50.5-50.8 | 1.7x |
| RGB NEAR=2 | 84.6-88.0 | 49.4-50.0 | 1.7-1.8x |
| grey 8-bit | 30.6-31.3 | 22.5-22.6 | 1.4x |
| grey 12-bit | 38.7-40.1 | 33.3-34.0 | 1.1-1.2x |

First RGB decode (cold, one 1024² image) is 105-112 ms. Verified: `ArctisImaging.Tests` 433/433; the 120-file random cross-check against CharLS 120/120 byte-identical; the 4 bench output hashes unchanged. Not checked: ARM64, net8.0, the engine suite, GUI.

### E3 pass (2026-11-08)

A third pass on top of A, B and E2. Scalar, single-threaded, output byte-identical. Changes (private members of `Arctis.Imaging.JpegLsDecoder`):
- `Predict` (median edge detector): body rewritten from bit masks; same signature, same result. Min/max of Ra, Rb from the sign of `ra - rb`; the two selects use `(rc - max) >> 31` and `(min - rc) >> 31`.
- `Coder` struct: new fields `bool wraps, wide`. `CreateCoder` sets `wraps = near == 0 && (maxval & (maxval + 1)) == 0` and `wide = maxval > 255`.
- `Regular`: returns `(px + err) & maxval` when `s.wraps`, else `Reconstruct(...)` as before. When `s.wide`, the bias/C update is done with masks (`lo = (b + n - 1) >> 31`, `hi = -b >> 31`, `b += (n & lo) - (n & hi)`, `c += lo - hi`, then the rare clamps `b >= 1 - n`, `b <= 0`, `c` in -128..127 as plain ifs); otherwise the existing two-branch update runs.

Method: the same scratch bench as before (Release, net10.0, Windows, Ryzen 9 9900X, two 1024² Brick photos, 40 warm-up + 6 timed passes, median ms for the 2-image set, 3+ processes), extended from 4 to 9 cases. New cases, all imagecodecs 2026.10.10 / CharLS 2.4.4 encodes of the same photos: grey 8 NEAR=2; grey 12 with random low 4 bits ("grey12n"); grey 16 (g<<8|g); RGB 12 (i<<4|i>>4); RGB 12 with random low 4 bits. The 12/16-bit inputs are the 8-bit photos widened, not real high-bit-depth scans.

Decisions:
- **E2's MED was not branchless; explicit sign masks (P).** The JIT assembly (`DOTNET_JitDisasm`) of `DecodeLine` and `DecodeTriplets` showed the `Math.Min`/`Math.Max` + `?:` form compiled to 4 data-dependent conditional jumps per sample. Largest win of the pass: RGB 86-89 -> 62 ms, RGB NEAR=2 87-89 -> 56-57, grey 8 30.5-31.5 -> 20.3, grey 12 39-40 -> 29-30.
- **Lossless wrap (V).** When NEAR = 0 and maxval + 1 is a power of two, reconstruction is exactly `(px + err) & maxval`: no multiply by 2*NEAR+1, no modulo branches, no clamp. CharLS's lossless traits do the same. A plain bool field branch (perfectly predicted) was as fast as a hard-coded version, so no generics. RGB 62 -> 59, grey 8 20.3 -> 19.3, grey 12 29.5 -> 28.8.
- **Mask bias update only above 8 bits (W).** Branch-outcome counts (scratch instrumentation): the bias update fires on ~3% of samples per direction at 8 bits (well predicted) but ~30% at 12 bits (flip rate 29-41%). Masks everywhere: every >8-bit case wins (RGB 12 82 -> 65, grey 12 29 -> 21, grey 16 27 -> 21) but every 8-bit case loses (RGB 59 -> 64.5, grey 8 19 -> 20.6, RGB NEAR=2 57-60 -> 60.5). Selecting by `maxval > 255` keeps both. Bit depth is a proxy for "how often the update fires"; noisy 8-bit data could behave like the wide case.
- Rejected, measured on top of P+V(+W):
  - Carrying Ra in a register instead of re-reading the line: neutral.
  - Branchless Golomb k (no `n >= a` early out, k==0-free value read): neutral to worse (RGB NEAR=2 56 -> 63).
  - AggressiveInlining of `Regular` into the loops: 40-50% worse on every case (RGB 60 -> 85, grey 8 19 -> 27).
  - Unchecked (`Unsafe.Add`) quantise/context access: neutral.
  - Precomputed NEAR step and wrap in `Coder`: neutral.
  - Topping the bit buffer up on every code: worse (RGB 59 -> 63).
  - One-shot unary + k-bit read with an out-of-line slow path: neutral.
  - **CharLS-style Golomb lookup table** (CharLS 2.4 `golomb_code_table`: peek 8 bits, table of (error, length) per k for codes of at most 8 bits): at 8 bits RGB 61-66 -> 56-58 (about -8%), RGB NEAR=2 -2-3%, grey 8 +3%; but even with the table bypassed above 8 bits, RGB 12 66.5 -> 73-80 and grey 12 21.5 -> 23.6. A trade, not landed in E3 (superseded: landed for ILV 2 only in the E4 pass below).

What CharLS 2.4 does differently (read from its source, team-charls/charls tag 2.4.2): the same algorithm and the same branchy bias update and Golomb-k loop. Differences: (a) the Golomb lookup table above; (b) the bit reader precomputes the next 0xFF position with memchr so the fast refill check is one compare (ours is a SWAR test on each refill; measured and rejected in the E4 pass below); (c) templates inline everything into the line loop so the bit-reader state lives in registers; the .NET JIT equivalent (inlining `Regular`) is the 40-50% regression above. Also measured: in ILV 2 RGB 15-18% of regular samples hit the same context as the previous sample (grey 1-2%), so the shared context table serialises R, G and B more than grey; no lever found. Profiling limit: no hardware counters (non-elevated shell), so RGB attribution beyond this is unproven.

Phase split (scratch timers): the scan is ~96-97% of decode time, output ~3%, the sample-plane clear <0.2%, so removing the planar stage stays not worth it.

After E3 (P+V+W, landed code, 3 processes, ms for the set):

| Case | Before E3 | After E3 | CharLS | Ratio |
|---|---|---|---|---|
| RGB (ILV 2) | 86-89 | 59.6-60.6 | 51.5 | 1.16-1.18x |
| RGB NEAR=2 | 87-89 | 60.4-62.9 | 49.6 | 1.2-1.27x |
| RGB 12 | - | 67.9-68.8 | 92.0 | 0.74x |
| RGB 12 noisy | - | 66.7-68.5 | 93.6 | 0.72x |
| grey 8 | 30.5-31.5 | 19.4-20.0 | 22.6 | 0.86-0.88x |
| grey 8 NEAR=2 | - | 19.0-20.4 | 20.4 | 0.93-1.0x |
| grey 12 | 39-40 | 21.3-22.6 | 33.3 | 0.64-0.68x |
| grey 12 noisy | - | 21.6-22.8 | 33.7 | 0.64-0.68x |
| grey 16 | - | 21.4-22.2 | 36.7 | 0.58-0.6x |

CharLS numbers are native code called from Python: a cross-runtime ratio, indicative only. Test-verified: `dotnet test ArctisImaging.Tests` 442/442 passed (net10.0); the scratch random cross-check against CharLS 120/120 byte-identical (same 120 files as the earlier passes); SHA-256 of all 9 bench cases' output unchanged from the pre-E3 decoder; solution build of ArctisImaging clean, 0 warnings. Not checked: ARM64, net8.0, the engine `_Build/test.sh` suite, GUI paste of a .jls in Thorium; first (cold) decode time not re-measured.

### E4 pass (2026-11-08)

The CharLS-style Golomb lookup table now runs on the 8-bit ILV 2 (RGB) path only, selected at compile time through generic struct width traits. Scalar, single-threaded, output byte-identical.

Method: the E3 scratch bench (Release, net10.0, Windows, Ryzen 9 9900X, the 9 cases, 40 warm-up + 6 timed passes, median ms for the 2-image set), run as an A/B: the E3 decoder rebuilt in a scratch copy of ArctisImaging against the candidate, alternated one process each per round, 6 rounds (grey re-check 8 rounds); figures are the median across rounds. Background Chrome load (~1.8 cores): absolutes sit up to ~5% above E3's table, so only same-session A/B ratios are used.

- New private `IWidth` (`static abstract bool IsWide`, `UsesTable`) with structs `Narrow` (false, false), `NarrowTable` (false, true), `Wide` (true, false). `DecodeLineInterleaved`, `DecodeLine`, `DecodeSampleInterleaved`, `DecodeTriplets` and `Regular` are generic `<T> where T : struct, IWidth`. `Coder.wide` is gone; E3's `if (s.wide)` bias selector is `if (T.IsWide)`.
- `ReadScan` dispatch: maxval > 255 -> `Wide` everywhere; else ILV 2 -> `NarrowTable`, ILV 0/1 -> `Narrow`.
- `Golomb`: `ushort[8 * 256]` (4 KB), index `k * 256 + next 8 stream bits`, entry `(mapped error << 4) | code length` for every Golomb code of at most 8 bits, 0 when longer; rows k 0..7. Independent of LIMIT: at <= 8 bits LIMIT >= 2*(bpp+8), so an 8-bit code is never an escape code. Built by `BuildGolomb()`. `Bits.Peek8()` fills if fewer than 8 bits are buffered and returns the top 8 (E5 replaced Peek8 with Top8 + Ensure); `Bits.Skip(n)`.
- `Regular<T>`: when `T.UsesTable` and k < 8, a nonzero `Golomb[(k << 8) | bits.Peek8()]` skips `entry & 15` bits and takes `entry >> 4` as the mapped value; otherwise (and always when !UsesTable, folded away by the JIT) `DecodeValue` as before.
- **Compile-time width split via generic struct traits (aurora-code rule 34), not runtime flags or duplicated loops.** E3 found that the table, even bypassed above 8 bits by a runtime check, made RGB 12 10-20% slower: the table code in the shared `Regular` worsened the wide path. With `T` a struct the JIT compiles one copy per trait and drops the dead branches, so `Regular<Wide>` is the E3 wide code. Rejected: hand-written duplicate 8-bit loops (a second copy of all four loops).
- **Table only in the ILV 2 loops (apply only where it wins).** The first cut put it in every 8-bit loop (4 rounds): RGB -6.5%, RGB NEAR=2 -2%, but grey 8 +6% (19.4 -> 20.65 ms), the grey loss E3 also saw (+3%). Hence `NarrowTable`; grey and ILV 0/1 use `Narrow`.
- Result, E3 -> E4 (6 rounds, median ms): RGB 60.4 -> 55.65 (-8%); RGB NEAR=2 60.6 -> 57.3 (-5%); RGB 12 67.6 -> 67.3; RGB 12 noisy 67.65 -> 66.75; grey 8 19.65 -> 19.45; grey 8 NEAR=2 19.1 -> 19.75 in that run but 18.6 -> 18.7 in an 8-round grey-only re-run (code unchanged apart from constant folding); grey 12 21.8 -> 21.8; grey 12 noisy 21.4 -> 21.6; grey 16 21.8 -> 21.35. Wide and grey cases neutral within noise.
- Against CharLS (E3's figures, not re-measured: RGB 51.5, RGB NEAR=2 49.6 ms): RGB 1.08x (was 1.16-1.18x), RGB NEAR=2 1.16x (was 1.2-1.27x). Grey and >8-bit unchanged (already faster). Cross-runtime ratio, indicative only.
- **Rejected, measured: CharLS's precomputed next-0xFF position (memchr) in the bit reader.** A `Bits.ffAt` field set by `IndexOf((byte)0xFF)` and refreshed once pos passed it, making the Fill fast path one compare (`!afterFF && pos + 8 <= ffAt`) instead of the SWAR test. A/B vs table-only, 6 rounds: RGB 56.1 -> 55.8, NEAR=2 58.65 -> 58.0, RGB 12 67.4 -> 67.75, RGB 12 noisy 67.55 -> 68.55, grey 8 19.5 -> 19.6, grey 12 21.5 -> 21.75, grey 16 21.6 -> 22.05. Neutral to slightly worse; reverted. The SWAR check is not the bottleneck.
- Still not done, deliberately: forced inlining of `Regular` (E3: 40-50% worse), threads, cold first-decode time.

Test-verified: `dotnet test ArctisImaging.Tests` 442/442 passed (net10.0); the scratch random cross-check against CharLS 120/120 byte-identical (same 120 files); SHA-256 of all 9 bench cases' output unchanged from E3; ArctisImaging builds clean for net8.0 and net10.0, 0 warnings. Not checked: ARM64; net8.0 at runtime; the engine `_Build/test.sh` suite; GUI paste of a .jls in Thorium; cold decode time.

### E5 pass (2026-11-08)

Three levers on 8-bit ILV 2 RGB only, in `JpegLsDecoder`: a call-free table-hit path for regular samples (X), reuse of each component's previous-pixel gradient quantisation (Z, 8-bit only), and 8-bit RGB written straight into an RGBA8 destination (O). Scalar, single-threaded, output byte-identical.

Method: same scratch bench and A/B as E4 (Release, net10.0, Windows, Ryzen 9 9900X, 9 cases, 40 warm-up + 6 timed passes, median ms for the 2-image set; candidate and baseline builds alternated one process each per round, median across rounds). Prototyped in a scratch copy first, each lever A/B'd against the repo.

Profile of 8-bit RGB before E5 (scratch instrumentation): 6.29M regular samples per 2-image set, 332 run pixels, table hit rate 99.1%, k distribution 0: 0.4%, 1: 37%, 2: 49%, 3: 13%; ~9 ns (~45 cycles) per sample. Scan 56.8 ms, of which planar store 1.6; Output 3.0. RGB NEAR=2: 99.9% hits, k=0 92%. Tier1 assembly of `Regular<NarrowTable>` pushed 8 callee-saved registers and spilled k and px, because the rare Fill/ReadZeros calls sit in the same function; the bit buffer is shifted in memory (`shl qword ptr [rbx+0x18]`).

- New private `RegularTable(ref Bits bits, in Coder s, int qs, int prediction)`: the regular-sample path for a code found in the `Golomb` table, no calls on the hit path (context load, GolombK, table lookup via `bits.Top8()`, `bits.Skip`, error, context update, reconstruct with the E3 branchy 8-bit bias update). On a miss (k >= 8 or entry 0) it returns `RegularMiss(...)`. Requires >= 8 buffered bits on entry.
- New `[MethodImpl(NoInlining)] RegularMiss(ref Bits, in Coder, int qs, int prediction)`: calls `Regular<Narrow>` (the full path), then `bits.Ensure(16)`.
- `Regular<T>`: first line `if (T.UsesTable) return RegularTable(...)`; E4's in-line table lookup removed, so `Regular<T>` is again the E3 body (DecodeValue) for `Narrow`/`Wide`.
- `Bits`: `Peek8()` removed (it filled when fewer than 8 bits); new `Top8()` (top 8 buffer bits, no fill, AggressiveInlining) and `Ensure(int n)` (Fill when count < n, AggressiveInlining).
- `DecodeTriplets<T>`: when `T.UsesTable`, `bits.Ensure(24)` before the three Regular calls of a non-flat pixel; `DecodeSampleInterleaved<T>`: `bits.Ensure(32)` before its per-pixel Regular calls (ILV 2 with 2 or 4 components). Invariant: every RegularTable hit consumes <= 8 bits and a miss refills to >= 16, so each RegularTable entry has >= 8 bits.
- `DecodeTriplets<T>` (Z): when `!T.IsWide`, locals `g0..g2` carry each component's Q(Rb - Rc) from the previous pixel's Q(Rd - Rb), set at line start and refreshed after a run; context = Q(Rd-Rb)*81 + g*9 + Q(Rc-Ra) read from `s.quantise` directly. `T.IsWide` keeps the three `Context(...)` calls.
- `DecodeTriplets<T>` (O): signature gained `Span<byte> destination, int pitch, bool direct`; when `direct`, each row is written as RGBA bytes (alpha 255) into `destination.Slice(y * pitch, width * 4)` instead of `StoreInterleaved` into the planes.
- `ReadSegments` and `ReadScan` gained `Span<byte> destination, int pitch, PixelLayout layout`. `ReadScan` sets direct = layout == RGBA8 && f.count == 3 && maxval == 255 && precision == 8 && scan is components 0,1,2 in order; only the `<NarrowTable>` call gets it (`<Wide>` gets false); then `f.written = direct`.
- New field `Frame.written`; `Decode` skips `Output` when it is set. The sample planes are still rented and cleared at the frame header (the scan type is unknown there).
- **X, call-free hot path.** With no calls on the hit path, the prologue drops from 8 to 4 pushes. Scratch A/B vs repo: RGB -4.4%, NEAR=2 -2%. Accepted cost: the context update (~30 lines) now exists twice (`Regular` and `RegularTable`).
- **XI rejected:** AggressiveInlining of RegularTable into the loops: RGB 54.35 vs X's 53.6 (worse than X).
- **Z, gradient reuse, 8-bit only.** Q(Rb-Rc) at pixel x+1 equals Q(Rd-Rb) at x, so 3 of 9 quantise lookups per pixel are carried. X+Z: RGB -5%, NEAR=2 -4% vs repo. Applied also to the wide loop it cost RGB 12 +5% (68.35 -> 72.05) and RGB 12 noisy +5% (69.6 -> 72.9), hence `!T.IsWide` only.
- **O, direct RGBA8 write.** Store + Output were 4.6 of 56.8 ms (8%) on RGB. X+Z+O: RGB 56.15 -> 50.05 (-11%), NEAR=2 ~59.8 -> 53.2 vs repo. Limited to RGBA8 (the only measured layout; the alternative of routing other layouts through the RGBA row + `PixelRows.FromRgba` was left out: more code, unmeasured). The scratch prototype used a static pointer; the landed version passes the destination span down.
- **R rejected:** testing `s.near == 0` before `k == 0` in the hot path (k is 0 on 92% of NEAR=2 samples): RGB 49.9 -> 51.05 (+2%), NEAR=2 within noise.
- Final, E4 -> E5 (6 rounds, median ms, range): RGB 56.80 (56.4-57.8) -> 49.45 (48.8-52.6), -12.9%; RGB NEAR=2 57.75 (56.7-60.5) -> 51.00 (49.2-55.4), -11.7%; RGB 12 67.10 -> 67.55 (+0.7%); RGB 12 noisy 67.15 -> 67.25 (+0.1%). Grey cases do not touch the changed code; 8-round grey-only A/B: grey 8 18.95 -> 19.10 (+0.8%), grey 8 NEAR=2 18.60 -> 18.40 (-1.1%), grey 12 noisy 20.70 -> 20.90 (+1.0%), grey 16 21.90 -> 22.45 (+2.5%): noise (grey 16 runs the unchanged wide line loop).
- Against CharLS (E3's figures, not re-measured: RGB 51.5, NEAR=2 49.6 ms): RGB 0.96x (was 1.08x), NEAR=2 1.03x (was 1.16x). Cross-runtime, indicative only.
- Not done: grey 8 -> R8 direct write (same idea likely helps grey; not measured); skipping the planes' rent/clear when the scan writes direct (<0.2% per E3).

Test-verified: `dotnet test ArctisImaging.Tests` 442/442 passed (net10.0); scratch random cross-check against CharLS 120/120 byte-identical (it decodes RGB ILV 2 to RGBA8, so it exercises the direct path at 8 bits); SHA-256 of all 9 bench outputs unchanged; ArctisImaging builds clean for net8.0 and net10.0, 0 warnings. Not checked: ARM64; net8.0 at runtime; engine `_Build/test.sh`; GUI paste of a .jls in Thorium; cold first decode.

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
- No JPEG-LS encoder (lossless JPEG encoder: see the section below).
- JPEG-LS 8-bit RGB is ~0.96x CharLS after E5 (faster), NEAR=2 still ~1.03x. The direct write covers RGBA8 only: other layouts and grey -> R8 still go through the planes and Output (grey -> R8 not measured); 8-bit RGBA (ILV 1, 4 components) still has no table (line loop) and no bench case (the Golomb table landed for ILV 2, the precomputed-0xFF refill was tried and rejected, see E4); the table is not tried for 8-bit ILV 1 with 4 components (RGBA uses `Narrow`; no RGBA bench case); the `maxval > 255` bias-update selector is a proxy tuned on widened 8-bit photos, real 12-16-bit data not benchmarked; E4 numbers ran under background load, A/B ratios only; the first (cold) decode is slower than warm (lever (c) in the E2 pass). The simple raster decoders are untouched and still unoptimised (lossless JPEG: see the next section).

## Lossless JPEG optimised (2026-11-08)

Scope: `Arctis.Imaging.JpegDecoder` (partial; `JpegDecoder.cs`, `JpegLossless.cs`), all private. Output is byte-identical to before.

What changed:
- New const `DcWholeAt` (after `QuantAt + 4 * 64`). `TablesLength` = `DcWholeAt + (4 << WholeBits)` (was `QuantAt + 4 * 64`), so the pooled tables array grows by 8192 ints (32 KB).
- `BuildTable` also fills a whole-difference lookup for DC tables 0-3: 11 bits (`WholeBits`), entry = `value << 8 | (codeLength + bitCount)`. Category 16 is 32768 with no extra bits. 0 is a miss; symbols > 16 are not filled.
- New `Difference(ref Bits, ref int tables, int t)` (AggressiveInlining): lookup first, else `Symbol` + `Receive` (still throws on category > 16).
- New private `IPredictor` and structs `Predictor1`..`Predictor7` (T.81 table H.1), used as `where P : struct, IPredictor`.
- New `DecodeScan<P>` dispatches to `DecodePlane<P>` (one component in the scan) or `DecodeInterleaved<P>`. Both are `[MethodImpl(AggressiveOptimization)]` under the Arctis.Imaging decode-loop exception to aurora-code rule 42a. First row, first column and inner pixels are separate loops: no per-sample branch on x/y/predictor/restart. Planes via `Unsafe` refs.
- `DecodeLossless`: with `restartInterval == 0` it switches on the predictor once and calls `DecodeScan<PredictorN>`. With restarts the old per-pixel loop remains, now calling `Difference`.
- New `Reduce(ReadOnlySpan<ushort>, Span<byte>, int pt, int max, int down)`: one row of `((v << pt) & max) >> down` narrowed to bytes; Vector512/256/128 tiers (portable `Narrow`) plus scalar tail; AggressiveOptimization.
- `OutputLossless`: precision >= 8 uses `Reduce`; below 8 bits stays scalar (the `down >= 0` branch is hoisted). One component into `PixelLayout.R8` writes straight into the destination and skips `ConvertRow` + `PixelRows.FromRgba`; the `rows` and `rgba` buffers are not rented then.

**The whole-difference lookup is the main lever.** With the old loop, RGB went 33 -> 24.5 ms for the set. Before, the entropy/predict loop was ~30 of RGB's ~33 ms (~4.8 ns per sample) and the output pass ~3 ms.

**The lookup is always built in the shared tables layout (user choice, fork A option a).** Every JPEG decode clears 32 KB more, and baseline/progressive files build a DC lookup they never use (estimated a few us per file, not measured). Rejected: building it only for lossless frames. `BuildTable` cannot know the frame type reliably because DHT may come before SOF, so that needs a lazy build at scan start from maxcode/offset/values, which is more code.

**The specialised loop (generic predictor struct, edge rows/columns split out) took 24.5 -> 20.7 ms on top of the table.** On its own, without the table, it was ~1-2%.

**The fast path runs only without restart intervals (user choice, fork B option a).** Files with DRI != 0 keep the per-pixel loop, so their behaviour is exactly as before. No fixture or encoder here writes restarts. Rejected: also fast-pathing row-aligned restarts (`interval % width == 0`). Unverified belief: libjpeg-turbo handles only row-aligned restarts in lossless.

**Output pass: SIMD reduce plus direct grey -> R8.** RGB ~21 -> 18 ms, grey 8-bit 8.3 -> 5.8 ms (prototype A/B). Grey -> R8 used to expand to RGBA and back.

Deliberately not done: the DC lookup for baseline DCT decoding; pixel-interleaved sample storage or a 3-component unroll (like JPEG-LS option B); dropping the `samples` Clear in `ReadLosslessFrame`; the first (cold) decode cost; threads.

Measured 2026-11-08, scratch console bench (not committed): Release, net10.0, Windows, Ryzen 9 9900X, 40 warm-up and 6 timed passes, median ms for the 2-image set (Brick + Brick2, 1024² each), 3 processes each side, before and after alternated. libjpeg-turbo 3.2 via imagecodecs from CPython, 3 processes (cross-runtime, indicative only). Ratio = after / libjpeg-turbo.

| Case | Before (ms) | After (ms) | libjpeg-turbo (ms) | Ratio after |
|---|---|---|---|---|
| RGB predictor 1 | 32.8-34.4 | 17.7-17.8 | 26.7-26.9 | 0.66x |
| RGB predictor 7 | 32.8-33.2 | 17.8-19.5 | 29.1-29.3 | 0.61-0.67x |
| grey 8-bit | 13.0-14.7 | 5.5-5.9 | 8.8-8.9 | 0.62-0.67x |
| grey 12-bit | 13.3-13.6 | 6.6-6.7 | 9.8 | 0.67-0.68x |

First (cold) decode of the RGB predictor-1 set: 60-61 ms before, 48-52 ms after.

Verification: `dotnet test ArctisImaging.Tests` 459/459; `ArctisImaging` builds clean on both targets, no warnings in touched files; scratch random cross-check (not committed) 435/435 byte-identical against libjpeg-turbo (`imagecodecs.ljpeg_decode`): 300 random lossless JPEGs (crops up to 400x160 of the fixture photos and Brick/Brick2, grey/RGB, 20% flattened, bit depths 2/3/5/7/8/9/10/12/14/16, predictors 1-7), grey decoded to both RGBA8 and R8; bench output SHA-256 unchanged in all runs. Not checked: `_Build/test.sh`, ARM64, net8.0 at runtime, restart-interval files, GUI paste of a lossless JPEG into Thorium, baseline/progressive JPEG decode-time cost of the larger table.

Known gaps:
- Restart-interval files take the old per-pixel loop, untested by fixtures.
- Below-8-bit output stays scalar, not benchmarked.
- Grey -> RGBA8 still goes through `ConvertRow`.
- The extra 32 KB table clear plus DC lookup build for non-lossless JPEGs was not measured.
- The cold first decode is still ~2.7x the warm time.

## Lossless JPEG encoder (2026-11-09)

Writes lossless JPEG (T.81 process 14, SOF3): 8-bit grey or RGB, predictors 1-7, byte-identical to libjpeg-turbo 3.2 (as written through imagecodecs 2026.10.10 `jpeg8_encode(lossless=True, predictor=N)`).

What changed:
- New file `JpegLosslessEncoder.cs`: a partial of `Arctis.Imaging.JpegEncoder` (internal static). Members: `EncodeLossless(ReadOnlySpan<byte> pixels, int width, int height, int pitch, PixelLayout layout, Stream output, in JpegLosslessSettings settings)`, private `Differences<P>` (generic over the predictor struct), private `LosslessHeaders`, private static `JfifMarker` / `AdobeMarker` byte spans.
- `JpegEncoder` is now `partial` (nothing else changed in `JpegEncoder.cs`). The lossless partial reuses its `Optimal`, `Codes`, `Symbol`, `Length`, the `Writer` ref struct and the `TablesLength`/`CodeAt`/`SizeAt`/`SpecLength` constants.
- `JpegDecoder` (`JpegLossless.cs`): `IPredictor` and `Predictor1`..`Predictor7` changed from private to internal so the encoder reuses them.
- New `Arctis.Imaging.JpegLosslessSettings` readonly struct (Types.cs): ctor `(int predictor)`; `Predictor` (0 means 1, TurboJPEG's `TJPARAM_LOSSLESSPSV` default); internal `Check()` throws ArgumentException outside 1-7.
- New `ImageCodec.EncodeLosslessJpeg(ReadOnlySpan<byte> pixels, int width, int height, int rowPitch, PixelLayout layout, Stream output, in JpegLosslessSettings settings = default)`: same size (1-65535), pitch and span checks as `EncodeJpeg`.
- New `Image.SaveLosslessJpeg(Stream, in JpegLosslessSettings = default)` and `SaveLosslessJpeg(string, in JpegLosslessSettings = default)`.
- New tests `JpegLosslessEncodeTests` (22). No new fixtures: it re-encodes the existing `LjGreyP1..P7.png`, `LjRgb.png`, `LjRgbP6.png` and compares with the `.jpg` beside them.

**What libjpeg-turbo writes, reproduced exactly.**
Grey = JFIF APP0 (same bytes as the baseline encoder), component id 1. RGB = Adobe APP14 (version 100, flags 0, transform 0), no JFIF, component ids 'R','G','B' (82,71,66). SOF3 precision 8, sampling 0x11, Tq 0; one DHT, class 0 id 0, shared by every component; SOS Ss = predictor, Se = 0, Ah/Al = 0; pad bits 1s; no restart markers. First sample predicted from 128 (1 << (P-1)), rest of the first row from the left, column 0 from above.

**Always optimal Huffman tables, no settings flag.**
libjpeg-turbo 3.2 in lossless mode always builds optimal tables; imagecodecs `optimize=False/True/None` all give the same bytes (checked). So the encoder runs the port of libjpeg's `jpeg_gen_optimal_table` (`Optimal`) on difference-category counts; there is no standard-table option.

**API: separate `JpegLosslessSettings` + `EncodeLosslessJpeg` / `SaveLosslessJpeg` (user choice 2026-11-09, fork 1a).**
Rejected: a `Lossless`/`Predictor` field on `JpegSettings`, which would mix in quality and subsampling that mean nothing for lossless.

**No point transform (user choice, fork 2a).**
Pt discards low bits, and imagecodecs cannot write it, so there would be no oracle. Rejected: offering it with round-trip tests only.

**Layouts as the baseline encoder.**
R8 and RG8 give grey, RGBA8 and BGRA8 give RGB; alpha is dropped. 4-component output is not written because the decoder reads 4 components as CMYK.

**One pass computes differences into a pooled `short[]` (2 bytes per sample) and counts categories at the same time; the write pass walks it linearly.**
The predictor is a generic struct parameter (aurora-code rule 34), reusing the decoder's predictor structs rather than copies. Scalar, no SIMD in this first version (superseded: see "Lossless JPEG encoder optimised (2026-11-09)").

Speed (user choice fork 3b), pre-pass numbers (unoptimised first version; after the pass 0.17-0.23x, see the later section). Measured 2026-11-09, scratch console bench (not committed): Release, net10.0, Windows, Ryzen 9 9900X; input Brick.png + Brick2.png (1024² each) as RGBA8 (RGB output) and R8 (grey); 40 warm-up + 6 timed passes, median ms for the 2-image set, encoding into a reused MemoryStream; 3 processes. Reference: libjpeg-turbo 3.2 via imagecodecs `jpeg8_encode` from CPython 3.14 with RGB / L numpy arrays (cross-runtime, includes the Python call and output allocation: indicative only). Ratio = Arctis / libjpeg-turbo.

| Case | Arctis (ms) | libjpeg-turbo (ms) | Ratio |
|---|---|---|---|
| RGB predictor 1 | 54.9-55.4 | 102.0-104.4 | 0.53-0.54x |
| RGB predictor 6 | 55.7-56.6 | 104.8-113.4 | 0.49-0.54x |
| Grey predictor 1 | 21.8-22.1 | 41.5-42.1 | 0.52-0.53x |
| Grey predictor 6 | 22.5-24.6 | 40.9-41.8 | 0.54-0.60x |

Faster than libjpeg-turbo in every case; these are the pre-pass numbers, the optimisation pass followed the same day.

Test-verified: `ArctisImaging` builds clean on net8.0 and net10.0, no warnings in touched files. `dotnet test ArctisImaging.Tests` 541/541 (net10.0), including `JpegLosslessEncodeTests` 22/22: the 9 Lj fixtures byte-identical (grey also from RG8), default = predictor 1, BGRA = RGBA, pitched rows, decoder round trip of EncodeLarge.png (333x211) pixel-exact for predictors 1-7 in RGB and grey, predictor -1/8 throws, short span throws. Scratch random cross-check (not committed), 200/200 byte-identical against `imagecodecs.jpeg8_encode(lossless=True)`: random crops up to 400x160 of Brick, Brick2, EncodeLarge and LjRgb, 40% grey (fed as RG8 with random alpha), RGB fed as RGBA8 with random alpha, 20% half-flattened, 10% flat, predictors 1-7. Not checked: `_Build/test.sh`, net8.0 at runtime, ARM64, any GUI/app use (nothing in the engine calls it yet).

Known gaps:
- Point transform, restart intervals, >8-bit precision (no 16-bit PixelLayout), 4-component output, JPEG-LS encoding: not written.
- Optimisation pass done the same day (next section); this section's "scalar" and speed numbers are pre-pass.
- The fixture-generation script for Lj*.jpg is still not in the repo.

## Lossless JPEG encoder optimised (2026-11-09)
One pass, same day the encoder landed. Output unchanged (byte-identical to libjpeg-turbo 3.2). 0.49-0.60x -> 0.17-0.23x libjpeg-turbo's time. Scalar except the differences pass, which has Vector512/256/128 tiers.

What changed:
- `JpegEncoder.Differences<P>(pixels, width, height, pitch, step, short[] differences)`: signature changed, no channels and no freq parameters. Writes every byte's difference in the source's own layout (width*step per row, pitch dropped); alpha bytes are computed and never read. First row scalar, first pixel of each later row scalar.
- New private `PredictRow<P>(ref byte row, ref byte up, ref short d, nuint step, nuint end)`: Vector512/256/128 tiers (bytes widened to shorts with `VectorN.Widen`, prediction in shorts, subtract, store) plus scalar tail.
- `EncodeLossless`: difference buffer is `step * width * height` shorts (was count * width * height). Counting moved out of the differences pass into a value histogram, then folded into categories. The write and count loops step through the R/G/B (or grey) channel offsets.
- New constants `CombinedAt = TablesLength`, `Widest = 510`: a region of 1,021 ints after the shared tables in the same pooled int[]. First the per-difference-value histogram, then overwritten with the combined code per difference `(huffmanCode << s | extraBits) << 5 | (huffmanLength + s)` at index d + 510, so the write loop is one lookup and one `Writer.Put` per sample.
- `JpegDecoder.IPredictor` gained `Vector128<short>/Vector256<short>/Vector512<short> Predict(ra, rb, rc)` overloads; `Predictor1`..`Predictor7` implement them (same formulas in shorts). The decoder does not use them.
- `JpegEncoder.Writer` unchanged (lever B reverted).

Method: scratch console bench (not committed), Release, net10.0, Windows, Ryzen 9 9900X; Brick.png + Brick2.png (1024² each) as RGBA8 (RGB output) and R8 (grey), predictors 1 and 6; 40 warm-up + 6 timed passes, median ms for the 2-image set, encoding into a reused MemoryStream; 3 processes per row, ranges over processes. Each lever A/B-tested, kept only if it won. Profile before: about one third in the differences pass (RGB ~19 ms, ~3 ns/sample), two thirds in tables + write (RGB ~36 ms, ~5.8 ns/sample).

| Step (ms) | RGB p1 | RGB p6 | Grey p1 | Grey p6 | Verdict |
|---|---|---|---|---|---|
| Before | 56.6-58.9 | 59.4-61.2 | 23.5-24.0 | 23.9-25.8 | — |
| A: combined code per difference | 35.9-36.8 | 35.3-35.7 | 13.3-13.9 | 13.9-14.3 | kept |
| B: word-at-a-time Writer.Put | 36.0-39.9 | 37.4-38.1 | 13.6-15.2 | 14.5-15.4 | rejected |
| C: SIMD differences, source layout | 34.7-35.1 | 32.9-33.1 | 13.4-14.0 | 13.3-14.0 | kept |
| C + 4 interleaved category histograms | 36.2-36.4 | 34.1-35.0 | 14.7-16.3 | 14.2-14.6 | rejected |
| Value histogram (final) | 22.1-23.7 | 20.3-20.5 | 7.5-7.6 | 7.4-7.4 | kept |

(A row is the back-to-back re-run used as the reference for B; the first A run was 36.1-37.6 / 35.6-38.0 / 13.5-14.0 / 14.0-14.7.)

Why:
- **A kept: one combined code per difference.** Replaced `Symbol` (freq null check, two `Put`s) with one table lookup and one `Put`. Biggest single win, ~37% off.
- **B rejected, measured: word-at-a-time `Writer.Put`** (gather to 32 bits, SWAR test for 0xFF, write 4 bytes big-endian, else byte-by-byte stuffing; `Pad` drains). Slower on lossless (RGB p6 35.4 -> 37.7, grey p6 14.0 -> 15.0); on the baseline JPEG encoder within noise (q75 4:2:0 26.5-27.2 -> 25.4-27.5 ms, q95 4:4:4 56.0-58.4 -> 51.5-54.2 noisy). The shared Writer stays byte-at-a-time. User chose to try it on the shared Writer (fork 1a) because JpegEncodeTests guard the baseline.
- **C kept: SIMD differences in the source layout (user fork 2a).** The encoder, unlike the decoder, has no sample-to-sample dependency: left, above and above-left are all in the source. Storing in source layout costs 4/3 the buffer for RGBA input and 2x for RG8 but needs no shuffle. Rejected: packing to 3 channels with a shuffle tier per width. The differences pass went ~19 ms -> 0.5 ms for the RGB set (grey ~0.1 ms). Total gain small because counting became the bottleneck: a replica of the category-count loop timed alone was ~16 ms RGB, ~7 ms grey (about half the encode).
- **Interleaved category histograms rejected, measured** (4 int lanes rotated per sample, summed at the end): slower, see table.
- **Value histogram kept (user approved as an off-plan lever, 2026-11-09):** count each difference value (1,021 int counters in the combined region, before it is filled), then fold into categories with `Length(d)`. Removes the per-sample category computation and spreads increments over many addresses. RGB ~35 -> ~22 ms, grey ~13.5 -> ~7.5 ms.
- **Rejected up front (in the plan, not measured):** recomputing differences in the write pass instead of storing them; would double the differences work to save ~2 B/sample of traffic.

Final vs libjpeg-turbo 3.2 (imagecodecs `jpeg8_encode(lossless=True)` from CPython 3.14, 3 processes; cross-runtime, includes the Python call and output allocation, indicative only):

| Case | Before pass (ms) | After pass (ms) | libjpeg-turbo (ms) | Ratio after |
|---|---|---|---|---|
| RGB predictor 1 | 56.6-58.9 | 22.1-23.7 | 101.2-102.6 | 0.22-0.23x |
| RGB predictor 6 | 59.4-61.2 | 20.3-20.5 | 102.2-108.7 | 0.19-0.20x |
| Grey predictor 1 | 23.5-24.0 | 7.5-7.6 | 41.1-42.8 | 0.18x |
| Grey predictor 6 | 23.9-25.8 | 7.4-7.4 | 40.9-43.3 | 0.17-0.18x |

Verification: test-verified. `ArctisImaging` builds clean on net8.0 and net10.0, no warnings in touched files. `dotnet test ArctisImaging.Tests` 541/541 (net10.0), including JpegLosslessEncodeTests 22/22, JpegEncodeTests and JpegLosslessTests. Scratch random cross-check rerun after every lever, kept or rejected: 200/200 byte-identical against imagecodecs (random crops up to 400x160, grey fed as RG8 and RGB fed as RGBA8 with random alpha, flattened regions, predictors 1-7). Not checked: `_Build/test.sh`, net8.0 at runtime, ARM64 (Vector128 tier only there), machines without AVX-512 (Vector256 tier not separately timed), any app use (nothing calls it).

Known gaps:
- Remaining time is the scalar value-histogram loop and the byte-at-a-time write; not split further.
- Per-tier (512/256/128) timings not measured separately; only the AVX-512 machine.
- Point transform, restarts, >8-bit, 4-component output, JPEG-LS encoder still not written.

Related: [[jpeg-gif-decoders]], [[jpeg-encoder]], [[other-raster-formats]], [[ico-simple-rasters]]
