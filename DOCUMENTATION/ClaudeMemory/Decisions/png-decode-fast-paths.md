# Decision — png decode fast paths: simd paeth, sub4, block inflate

**Date:** 2026-11-01
**Scope:** `Arctis.Imaging` — `PngDecoder`; `ArctisAurora.Core.Imaging` — `TextureLoader.UploadFile`

## What changed
- `PngDecoder.Sub4(Span<byte>)` and `PngDecoder.Paeth4(Span<byte>, ReadOnlySpan<byte>)`, called from `Unfilter` when bpp == 4. `Paeth4` only when `Vector128.IsHardwareAccelerated`.
- `DecodeRows` gains an `int block` parameter (rows per inflate read).
- `Decode` rents `lines` as `(block + 1) * (rowBytes + 1)`, with `block = clamp(64 KB / (rowBytes + 1), 1, height)`. Slot 0 holds the previous row; the last row is carried forward between blocks.
- `[MethodImpl(MethodImplOptions.AggressiveOptimization)]` on `DecodeRows`, `Unfilter`, `Sub4`, `Paeth4`, `AddUp`.
- Interlaced path and bpp != 4 stay scalar, one row per read.
- `TextureLoader.UploadFile`: permanent `Profiling.Zone` "Image.Decode" around `ImageCodec.Decode` into mapped staging. Only the fast path is zoned, not the CPU-copy path through `ImageFiles.Load`.
- Decoded output is byte-identical to before. No data XML changed.

## Why these choices

**Paeth undo is the cost, so it got SIMD.**
Thorium's 11 font atlases plus the default icon atlas are 15.5 MB of PNG decoding to 127 MB of RGBA8. ImageSharp baked them with Paeth on all 18,304 rows. Paeth has a serial dependency per pixel (the left neighbour must be finished first): ~130-200 ms scalar. Inflate alone is ~62 ms. CRC is noise.

**The SIMD is across one pixel's 4 channels (16-bit lanes), not across pixels.**
The left-neighbour dependency forbids going across pixels. SIMD Paeth ~130 ms vs ~200 ms scalar on these atlases.

**Inflate in ~64 KB blocks of rows.**
Per-row `ReadExactly` on `ZLibStream` had measurable call overhead.

**AggressiveOptimization on the hot loops.**
They run once at boot, so tier-0 plus OSR cost ~90 ms of cold time. Cold-process bench: 311 -> 221 ms with that plus block inflate.

**Decoding straight into mapped staging is not the problem.**
Decoding into a PAGE_WRITECOMBINE buffer measured 135 vs 137 ms into a normal array. Rows are still unfiltered in pooled scratch and written forward once.

**Rejected for now: re-baking atlases with Arctis's own `PngEncoder`.**
It writes Sub/Up rows only (bump `AssetImporter.importerVersion` 9 -> 10). Decode would drop to ~100 ms warm / ~130 ms cold process, but PNG size grows 15.5 -> 22.5 MB (Fastest deflate), ~22 MB of binary churn lands in git, and the first boot re-bakes. The target was met without it. It stays the next lever, along with Optimal compression to win back size.

**Rejected: decoding atlases in parallel on workers.**
Single-core speed first (optimize-before-threading rule). Remains a later option: decode in parallel into staging, upload serially.

## Measurements
Release+PROFILE, Thorium `--profile=30`, Bootstrap thread, 3 runs each side, same machine, 2026-11-01. Before = the ImageSharp -> Arctis switch with the scalar decoder; after = this change.

| Metric | Before (ms) | After (ms) |
|---|---|---|
| `Preload.FontAsset` (temporary zone, 13 calls) | 885 / 681 / 692 | 435 / 433 / 432 |
| `AssetRegistries.PreloadAssets` | 1,035 / 722 / 733 | 478 / 467 / 466 |
| Atlas decode total (`Tex.Decode` temporary zone = `Image.Decode` now) | 648 / 544 / 546 | 306 / 304 / 303 |
| Bench warm decode of the 12 atlases | 270 | 210 |

- User target for `Preload.FontAsset` was < 600 ms, roughly what ImageSharp did.
- In-engine decode (~300 ms) is above the bench (~210 ms warm) because of cold-process effects: JIT of the rest and first touch of fresh staging pages.
- Earlier ImageSharp numbers: warm decode of the atlases ~205 ms, cold ~385 ms; PreloadAssets 612 ms of a ~1.1 s boot.
- Extends the existing memory that ImageSharp is being replaced by an in-house loader for license and security reasons.

## Verification
- **Test-verified** for the decoder: ArctisImaging.Tests 50/50, including RGBA8 under every filter.
- SHA-256 of all 12 real atlases decoded by the old build and the new one match.
- Engine suite `_Build/test.sh`: 508 passed, 8 failed, 2 skipped, 0 new. Failures are Boot (pre-existing baseline error) and 7 LatinModern Tex goldens, identical px counts and deltas to a run from before this change.
- No `[Vulkan]` lines in the profile runs.
- Not checked: Carbon/AuroraEditor hosts, interlaced or non-RGBA PNG performance.

## Known gaps
- The 7 LatinModern golden failures and the Boot baseline error predate this change and are unexplained here.
- No perf test with a Budget locks in the `Image.Decode` cost.
- Atlases are still Paeth-baked by ImageSharp; the re-bake is an open option.
- bpp != 4 (RGB8, grey, 16-bit) and interlaced images take the scalar path.

Related: [[atlas-bake-one-edge-pass]], [[asset-manifest-and-import]], [[engine-profiling]]
