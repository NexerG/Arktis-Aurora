# Mistake — tier-0 helpers skewed a decode benchmark

**Date:** 2026-11-08
**Scope:** `Arctis.Imaging` — `BmpDecoder.DecodeRle` and its helpers, `StoreRepeated`

## What went wrong
- A decode loop's time depended on run order (2.4 vs 5.1 ms) and looked address-dependent (a 4 KB pattern in a destination-offset sweep).
- Cause: helpers without `AggressiveOptimization` called from an `AggressiveOptimization` loop start at tier 0 and tier up in the background. Only the loop itself was FullOpts.

## Rules
- When a decode loop's time depends on run order or looks address-dependent, re-run with `DOTNET_TieredCompilation=0` before blaming the code, the vector width or the stores.
- Every helper on the hot path of an `AggressiveOptimization` decode loop gets `AggressiveOptimization` (or `AggressiveInlining`), under aurora-code rule 42a's Arctis.Imaging decode-loop exception.
- A `Vector512` built with `Create(Vector256, Vector256)` in a large method can go through a 64-byte stack slot (partial write, reload per use). Broadcast from a scalar (`Vector512.Create(ulong)`) so it stays in a register.

- 2026-11-09: TGA decode hit the same pattern (first RLE case 4.4-4.9 ms instead of ~1.7 ms) and took `AggressiveOptimization` on `Decode`, `DecodeRow`, `HasAlpha`, `ReadRunLengthRow`, `Repeat`; see [[other-raster-formats]].

## Known gaps
- Blocky RLE4 RGBA8 is still 2x slower with tiering on than off, with every method FullOpts; cause unknown, see [[ico-simple-rasters]] "BMP indexed rows and RLE".

Related: [[ico-simple-rasters]], [[simd-width-tiers]], [[profiling-unoptimized-jit]]
