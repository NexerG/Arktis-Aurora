# Decision — ktx2 container: Arctis.Imaging reads and writes KTX 2.0 textures

**Date:** 2026-11-05
**Scope:** `Arctis.Imaging` — `Ktx2`, `Ktx2Info`, `ImageCodec`, `ImageFormat`, `Inflater`; `ArctisAurora.Core.Imaging` — `ImageFiles`, `TextureLoader`; tests `Ktx2Tests`, `VisualTests.Ktx2StoredMips`

## What changed
- NEW `Arctis.Imaging.Ktx2` (internal static): `TryReadHeader`, `ReadInfo`, `DecodeLevel`, `Encode`, `WriteDfd`, `IsKtx2`, `LevelSize`.
- NEW `Arctis.Imaging.Ktx2Info` (public readonly struct, `Types.cs`): `Layout`, `Srgb`, `Width`, `Height`, `Levels`.
- NEW `ImageFormat.Ktx2` enum member.
- NEW on `ImageCodec`:
	- `TryReadKtx2(data, out Ktx2Info)`: false only when the data is not a KTX2 file; throws `ImageFormatException` / `NotSupportedException` for a KTX2 it cannot use.
	- `DecodeKtx2Level(source, level, destination, in DecodeOptions)`.
	- `EncodeKtx2(ReadOnlySpan<Image> levels, bool srgb, bool compress, Stream output, in DeflateSettings deflate = default)`.
	- `TryIdentify` recognises KTX2; `Decode` decodes its level 0.
- CHANGED `ArctisAurora.Core.Imaging.ImageFiles.CanDecode` accepts `ImageFormat.Ktx2`, so the engine loads `.ktx2` like any other image (level 0).
- NEW tests `Ktx2Tests`: `RoundTripsEveryLayout`, `DecodeConvertsLayouts`, `DecodeHonoursRowPitch`, `WriterMatchesAFileBuiltFromTheSpecification`, `OtherDataIsNotKtx2`, `EveryTruncationIsRejected`, `UnsupportedFilesThrowNotSupported`, `MalformedHeadersAreRejected`, `LevelsOutsideTheFileAreRejected`, `LevelsMustHalve`.
- No data XML read or added.

## Why these choices

**Scope (user approved): 2D only, one layer, one face, any number of mip levels.**
- vkFormats: R8 (9/15), R8G8 (16/22), R8G8B8A8 (37/43), B8G8R8A8 (44/50), UNORM / SRGB.
- Supercompression none (levels aligned to 4 bytes, the spec's lcm(texel block size, 4)) or ZLIB (scheme 3, each level its own zlib stream, alignment 1).
- Everything else (BasisLZ 1, Zstd 2, block-compressed formats, 1D, 3D, arrays, cubemaps) throws `NotSupportedException`.
- Malformed files (short header, bad typeSize, width 0, too many levels, level sizes not matching dimensions, offsets outside the file) throw `ImageFormatException`.

**ZLIB is the supercompression scheme because it reuses `Deflater` / `Inflater` with no new dependency.**
Zstd would need a decoder or a package; Basis needs a native transcoder (C++).

**R8 / R8G8 decode as grey / grey+alpha.**
That is the library's existing meaning for `PixelLayout.R8` / `RG8` (same as PNG), not Vulkan's red / red-green.
Conversion between layouts goes through an RGBA row and `PixelRows.FromRgba` (Rec. 709 luma).

**Writer and reader.**
- The writer emits one basic DFD block (RGBSDA, BT.709 primaries, LINEAR or SRGB transfer, straight alpha, 8-bit samples, alpha sample LINEAR-qualified for SRGB) and one key/value pair `KTXwriter` = `Arctis.Imaging`.
- Levels are written smallest first, as the spec requires.
- The reader trusts `vkFormat` and does not parse the DFD.
- The writer does not generate mips: the caller passes the chain (e.g. via `ImageOps.HalveLinear`); each level must be `HalfSize` of the one before.

**Not wired into atlases.**
`FontAsset` / `IconSetAsset` load `_atlas.adf` ([[distance-field-format]]), not KTX2; `TextureLoader` uploads a KTX2 file's stored mips directly (below).

**`TextureLoader.UploadFile` uploads stored KTX2 mips when asked for mipmaps and the file has more than one level.**
`mipmaps && ImageCodec.TryReadKtx2(file, out info) && info.Levels > 1` -> private `UploadKtx2`: each level decodes straight into mapped staging, one Vulkan mip level per stored level, no CPU mip generation. Every other case (single-level KTX2, mipmaps not asked for, PNG/BMP/ADF) takes the old path unchanged. New private `LevelSize(size, level)`; `OpaqueRowsOf` now takes `(ReadOnlySpan<byte> pixels, int width, int height)` instead of an `Image` (its `UploadImage` caller updated). New test `ArctisAurora.Tests.VisualTests.Ktx2StoredMips` (`Visual.Ktx2StoredMips`, in `Visual.tests.axml`). User approved each fork:
- **The caller's flag decides.** A file with mips but `mipmaps: false` uploads level 0 only, as before; the caller's sampler may not expect mips.
- **A partial chain (1 < Levels < full) uploads as stored**, since Vulkan accepts any level count. Rejected: generating the missing tail from the smallest stored level - extra code for files nobody writes yet.
- **The caller's `Format` wins over the file's SRGB/UNORM flag**, matching PNG; a mismatch logs `Log.Once().Warn`. Rejected: the file overriding the format - `TextureAsset` would have to learn the format back to build its image view (signature change).
- **Device limit: drop whole levels**, starting at the first stored level that fits `MaxImageDimension()` (logs a Warn); no level fits -> `NotSupportedException`. Rejected: `FitDevice`'s area resize - stored mips give the smaller level for free.
- **Opaque rows:** staging is mapped and must never be read back ([[mapped-streaming-buffers]]), so when `opaqueRows` is asked the start level decodes into an `ArrayPool` buffer first, `OpaqueRowsOf` runs on it, then it is copied into staging. Needs 4 bytes per pixel (`ArgumentException` otherwise, as `UploadImage`). `OpaqueRowsOf` took a span so the KTX2 path need not wrap the rented array in an `Image` allocation.
- Any pixel layout works on the KTX2 path (decode converts), unlike `UploadImage`'s mip path, which needs 4 bpp.

## Verification
- **test-verified (unit)**: `dotnet test ArctisImaging.Tests -f net10.0` -> 124 passed, 0 failed (see [[png-deflate]]).
- External: Khronos KTX-Software 4.4.2 (official Linux release) `ktx validate` passes all 32 files the writer produced (4 layouts x UNORM/SRGB x none/ZLIB x single level / full chain), silently; the validator was confirmed to flag a deliberately corrupted file.
- `ktx extract --level 0 --raw` (libktx inflating our ZLIB levels) returned byte-identical pixels for all 16 multi-level files.
- NOT checked: Windows; any real `.ktx2` from another tool loaded in the engine. NOT GUI-verified.
- `TextureLoader` mip upload, **test-verified on Windows (in-engine, real GPU)**: whole solution builds clean in Debug; `ArctisImaging.Tests` 142/142; `_Build/test.sh` 509 passed, 8 failed, 2 skipped - the 8 are Boot (baseline `SamplerAsset` error, no new error kinds) and 7 LatinModern Tex goldens that failed identically in the run before this change. `Visual.Ktx2StoredMips` passes: writes a 64x64 7-level ZLIB KTX2 via `EncodeKtx2`, loads it through `ImageControl`/`TextureAsset.ForFile`, checks the texture is 64x64 and opaque rows come from level 0. Zero `[Vulkan]` lines.
- NOT checked (mip upload): the device-limit level-drop path (cannot force the limit); that the GPU samples the stored levels rather than only that upload succeeds (no golden); a `.ktx2` written by another tool (toktx / KTX-Software) loaded in the engine; boot time. NOT GUI-verified.

## Known gaps
- No Zstd, BasisLZ or block-compressed (BC / ASTC / ETC2) support.
- Reader ignores the DFD and key/value data.
- Atlases not switched to KTX2.
- Level-drop path for files over the device limit untested; real third-party `.ktx2` in the engine still unchecked.

Related: [[png-deflate]], [[managed-inflate]], [[png-decode-fast-paths]]
