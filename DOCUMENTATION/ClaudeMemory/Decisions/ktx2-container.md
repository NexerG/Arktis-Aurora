# Decision — ktx2 container: Arctis.Imaging reads and writes KTX 2.0 textures

**Date:** 2026-11-05
**Scope:** `Arctis.Imaging` — `Ktx2`, `Ktx2Info`, `ImageCodec`, `ImageFormat`, `Inflater`; `ArctisAurora.Core.Imaging` — `ImageFiles`; tests `Ktx2Tests`

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
`FontAsset` / `IconSetAsset` still load `_atlas.png`; `TextureLoader` decodes KTX2 level 0 through the same path as PNG rather than uploading stored mips directly.

## Verification
- **test-verified (unit)**: `dotnet test ArctisImaging.Tests -f net10.0` -> 124 passed, 0 failed (see [[png-deflate]]).
- External: Khronos KTX-Software 4.4.2 (official Linux release) `ktx validate` passes all 32 files the writer produced (4 layouts x UNORM/SRGB x none/ZLIB x single level / full chain), silently; the validator was confirmed to flag a deliberately corrupted file.
- `ktx extract --level 0 --raw` (libktx inflating our ZLIB levels) returned byte-identical pixels for all 16 multi-level files.
- NOT checked: Windows; any real `.ktx2` from another tool loaded in the engine. NOT GUI-verified.

## Known gaps
- No Zstd, BasisLZ or block-compressed (BC / ASTC / ETC2) support.
- Reader ignores the DFD and key/value data.
- Atlases not switched to KTX2.
- `TextureLoader` does not upload stored KTX2 mips directly.

Related: [[png-deflate]], [[managed-inflate]], [[png-decode-fast-paths]]
