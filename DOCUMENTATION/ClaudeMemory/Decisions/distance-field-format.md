# Decision — distance-field atlases are stored as ADF, Arctis's own near-lossless format

**Date:** 2026-11-06
**Scope:** `Arctis.Imaging` — `DistanceField`, `ImageCodec`, `Image`, `ImageFormat`; `ArctisAurora.Core.Imaging.ImageFiles`; `ArctisAurora.Core.Filing` — `AuroraFont`, `IconSet`, `FontPreviews`, `AssetImporter`; `ArctisAurora.Core.Registry.Assets` — `FontAsset`, `IconSetAsset`; `ArctisAurora.Tests.ImageDecodePerfTests`; `Arctis.Imaging.Tests.AdfTests`

## What changed
- All MTSDF font atlases, the default icon atlas and the font-preview atlas are `*_atlas.adf`, not `*_atlas.png`.
- Every app's Data folder was rebaked (Thorium 12 atlases, Carbon 6, AuroraEditor 6); the 24 PNG atlases were deleted.
- NEW `Arctis.Imaging.DistanceField` (internal static): `TryReadHeader`, `Encode`, `Decode`; private `Split`, `PlaneSymbols`, `Run`, `Put`, `BuildLengths`, `Canonical`, `BuildEntries`, `DecodeTile`, `Interleave`, `Corrupt`, `TileRect`, `Med`, `Median`, `U32`.
- NEW `ImageFormat.Adf`, appended last so existing enum values keep their numbers.
- NEW `ImageCodec.EncodeAdf(ReadOnlySpan<byte> rgba, int width, int height, int rowPitch, Stream output)`; `ImageCodec.TryIdentify` recognises ADF; `ImageCodec.Decode` decodes it (RGBA8 layout only, any row pitch; other layouts throw `NotSupportedException`).
- NEW `Image.SaveAdf(string path)` (RGBA8 only).
- `ImageFiles.CanDecode` accepts `ImageFormat.Adf`, so `TextureLoader.UploadFile`'s staging fast path decodes ADF straight into the staging buffer.
- Bakers write `_atlas.adf`: `AuroraFont` atlas bake, `IconSet.GenerateIconAtlas`, `FontPreviews.Bake`/`WriteAtlas` (local `pngPath` renamed `atlasPath`). Profiling zone `Atlas.SavePng` renamed `Atlas.SaveAdf`.
- Loaders read `_atlas.adf`: `FontAsset.Load`, `IconSetAsset.Load`.
- `AssetImporter.importerVersion` 9 -> 10 (forces rebake); its font and icon `ReadStamp` output names are `_atlas.adf`. `FontPreviews.version` 1 -> 2.
- `ImageDecodePerfTests`: `Atlases(zone, bool png)` reads the baked `*_atlas.adf`; the PNG rows (`Perf.ImageDecode.Atlases.Arctis` / `.ImageSharp`) decode them and re-encode PNG in memory with Arctis's encoder. NEW action `Perf.ImageDecode.Atlases.Adf` (zone `Image.Decode.Arctis.AtlasAdf`) in `Perf.tests.axml` (Timeout 60000, no Budget). `Formats()` reads `Fonts/arial/arial_atlas.adf` as its sample source.
- NEW tests `AdfTests`: `RoundTripsWithinOne` (12 shapes incl. 1x1, 7x5, 15x33 narrow tiles, 256^2, 300x270, 513x17; smooth field and noise), `DecodeHonoursRowPitch`, `IdentifiesTheHeader`, `AlphaThatIsNotTheMedianIsRejected`, `OnlyRgba8IsDecoded`, `EveryTruncationIsRejected`, `CorruptStreamsThrowOrDecode` (500-round bit-flip fuzz).

## Format (ADF v1, magic `ADF1`)
- Header: magic, u32 width, u32 height; then 3 x 537 code-length bytes (one canonical Huffman code per plane); then (tiles+1) u32 tile offsets; then one bitstream per 256^2 tile; then 16 zero bytes.
- Only R, G, B are stored as planes. Alpha is rebuilt as median(R,G,B) on decode.
- Each texel is MED/LOCO-I predicted (left, up, up-left; row 0 predicts left, column 0 predicts up, first texel 0). The residual is quantised to +-1 (NEAR=1, step 3, as JPEG-LS near-lossless).
- Coded as zigzag literals (symbols 1..511) and zero runs (symbols 512..536 = run bucket k, then k raw bits).
- Codes are capped at 12 bits so the 4096-entry root table resolves every code with no long-code path.
- Bits are MSB-first; the decoder refills 64 bits branch-free (`buf |= bswap(load64) >> cnt; inp += (63-cnt)>>3; cnt |= 56`).

## Why these choices

**Only three planes are stored because alpha is the median.**
The bench found alpha == median(R,G,B) exactly on all 12 Thorium atlases, so a 4th plane is wasted (4 planes measured 145% of PNG). This is the open item in [[atlas-is-unorm-not-srgb]]; ADF now relies on it.

**ADF over PNG, KTX2, BC7 and Zstd.**
PNG decode is fine but ~16% bigger and has no near-lossless mode. BC4/BC5 cannot hold a 3-4 channel MTSDF; BC7 on distance fields is untested and lossy in an unbounded way. Zstd would need a decoder port for maybe 50-80 ms of boot. KTX2 wrapping ADF as a vendor supercompression scheme adds nothing since no other tool could read it; KTX2 stays the container for future ordinary textures and was not touched ([[ktx2-container]]).

**Tiles are 256^2 to bound decode scratch, not for parallelism.**
Scratch is 192 KB (3 planes x 64 K, rented) instead of 18 MB of planes for a 2432^2 atlas. Decode is single-threaded (aurora-code: no threading unless asked).

**The encoder throws if alpha is not the median.**
`EncodeAdf` throws `ArgumentException` if any pixel's alpha is not median(R,G,B); every baker today produces median alpha. Rejected: a 4th-plane fallback (a second decoder path nothing uses). If the MTSDF generator ever emits a true-SDF alpha, ADF needs a version 2.

**The decoder rejects malformed streams as `ImageFormatException`.**
Code lengths > 12, over-subscribed codes (Kraft), unused root entries (length 0), non-monotonic or out-of-file tile offsets, bit-reader over-read past file end - 8, and runs that overrun a plane. The 16 trailing zero bytes exist because the reader legitimately runs up to 8 bytes past the last stream byte; 8 bytes of padding (the scratch bench's choice) made that position fail the bound and would have over-read.

## Measurements
Scratchpad bench, this Windows machine, 24 cores, Release, 12 Thorium atlases = 130 MB RGBA8, single core unless stated.
- Size: PNG 15.9 MB = 100%; ADF 84.5% (83.8% before capping codes at 12 bits). In the repo the baked files total ~18% smaller than the PNGs they replaced (arial 779 KB vs 930 KB; Electrolize 162 vs 154 KB is the one atlas that grew).
- Decode: Arctis PNG 111-114 ms; ADF 115-118 ms (0.96-0.98x); all 24 cores with tiles ~10 ms (not used).
- Rejected variants:
	- lossless planar: 99-102.5% of PNG, 4x slower to decode
	- 4 planes: 145%
	- QOI: 262% size, 2.7x faster decode
	- cross-channel residuals: -3.5% size, blocks fast run paths
	- joint run+literal symbols: 94.9% size, 153 ms, worse on both
	- two-pass decode (residuals then reconstruction): 188 ms
- Profile of the final decoder (knockouts): entropy decode + loop ~57%, reconstruction ~37%, RGBA interleave ~6%. 19.8M symbols over 99.9M texel-planes; 15.2M literals, 4.56M runs; long codes were 0.9% of symbols before the 12-bit cap.

## Codegen lessons (decoder hot loop)
- `Math.Min/Max` in the MED compiled to jumps, not cmov; replaced with sign-mask arithmetic.
- Any call inside the hot loop (a non-inlined long-code helper, `Span.Fill`) made the JIT keep the bit buffer and count on the stack for the whole loop (stack refs 111 -> 68 once all calls were removed).
- A non-inlined per-run helper cost +15 ms (5M calls).
- Key identity: MED(a,b,c) with a == c is b, so a zero run after a texel equal to its up neighbour is a straight copy of the row above.

## Verification
- **Test-verified (Arctis.Imaging):** ArctisImaging.Tests 142/142 pass, including the 7 new `AdfTests`.
- **Byte-level check of the rebake:** all 24 rebaked atlases decoded and compared with the PNG each replaced: max |delta| = 1 in R, G, B and A, 0 texels off by more than 1, 0 texels whose alpha crossed 128 (the glyph edge).
- **Builds clean:** whole solution Debug, no new warnings.
- **Golden-verified after re-recording 60 shots.** First run: 461 passed, 55 failed. 60 shots differed only by the +-1 atlas values (max channel delta 1-3, plus `LatinModern.Draws` with 2 single texels on 1 px strokes at 37-38) and had passed on 2026-11-01; their actuals were copied over the goldens (what `--test-approve` writes, but per shot, since approve takes a whole suite). Re-run: 508 passed, 8 failed, 2 skipped. The 8 predate this change and were not approved: `Boot` (`SamplerAsset.LoadDefault` logs an error unconditionally) and 7 LatinModern/Tex shots at delta 143 (fold chevrons beside headings that the goldens lack, x~392-400), the same mismatches as in runs from 2026-11-01.
- No `[Vulkan]` lines in any boot. Not GUI-verified. `Perf.ImageDecode.Atlases.Adf` has not been run in-engine (needs Release + PROFILE); all ADF timings are from the scratch bench.

## Known gaps
- AuroraEngine/Data's 4 atlases (Fonts arial, arialbd, Electrolize-Regular; Icons default) were converted from their PNGs, not rebaked: the AuroraEngine exe does not run the importer. All 4 within ±1, 0 edge flips; their stamps still say `ImporterVersion` 9.
- `MathConstants.Read` writes 0 for `Sub1`, `Sub2`, `Sup1-3`, `SubDrop`, `SupDrop`, `BigOpSpacing1/3/5` for latinmodern-math, where the committed `latinmodern-math.math.axml` has real values. The committed file was restored by hand; a future rebake of that font zeroes them again. (Carbon's and AuroraEditor's `cambria.math.axml` gained those attributes as 0, matching Thorium's cambria, which is consistent.)
- AuroraEditor crashes at boot: its import of `times.ttf` is skipped (`unknown charset 'Latex,Latin'`) and preload then wants a `times` atlas. Pre-existing and unrelated.
- Old font-preview PNGs already in users' folders are left in place (cache).
- Commented-out dead code in `FontAsset` still names `_atlas.png`.
- ARM gets the scalar interleave (SSE2 path only).

- `DistanceField.Interleave` has Avx512BW and Avx2 tiers above SSE2 (2026-11-07): see [[simd-width-tiers]]. The tile copy stays 128-bit pending a slack check.

Related: [[simd-width-tiers]], [[ktx2-container]], [[managed-inflate]], [[png-decode-fast-paths]], [[atlas-is-unorm-not-srgb]], [[atlas-bake-one-edge-pass]]
