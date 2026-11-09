# Decision — Arctis.Imaging decodes ICO/CUR, PNM/PAM, QOI, farbfeld, PCX, XBM and XPM

**Date:** 2026-11-07
**Scope:** `Arctis.Imaging` — `IcoDecoder`, `PnmDecoder`, `QoiDecoder`, `FarbfeldDecoder`, `PcxDecoder`, `XbmDecoder`, `XpmDecoder`, `BmpDecoder`, `ImageCodec`, `ImageFormat`; `ArctisAurora.Core.Imaging.ImageFiles`; `ArctisAurora.Core.Filing.ClipboardImage`

## What changed
- Scope chosen by the user from the roadmap (2026-11-06/07): the "simple rasters" item plus ICO/CUR. Decoders only in this go; the encoders followed the same day in [[simple-raster-encoders]]. (An earlier version of this note claimed ImageSharp 3.1 had ICO/CUR encoders; retracted, 3.1.11 has PBM and QOI encoders but no ICO/CUR at all.)
- `ImageFormat` gains, appended after `Webp`: `Ico` (ICO and CUR), `Pnm`, `Qoi`, `Farbfeld`, `Pcx`, `Xbm`, `Xpm`, `JpegLs` (JPEG-LS is in [[lossless-jpeg]]).
- New internal static classes, each with `TryReadHeader(ReadOnlySpan<byte>, out ImageHeader)` and `Decode(ReadOnlySpan<byte>, Span<byte> destination, int pitch, PixelLayout)`: `IcoDecoder`, `PnmDecoder`, `QoiDecoder`, `FarbfeldDecoder`, `PcxDecoder`, `XbmDecoder`, `XpmDecoder`.
- `BmpDecoder`: new internal `DecodeIcon(dib, destination, pitch, layout)`; `Decode` forwards to a private core `Decode(…, bool icon)`; new private `ApplyMask`.
- `ImageCodec.TryIdentify` probes after WebP, in this order: Qoi, Farbfeld, Xpm, Ico, Pnm by magic; Pcx and Xbm only when they are the `hint` (same rule as Tga). `ImageCodec.Decode` has a case per format.
- `ImageFiles.HintOf`: `.pcx` gives Pcx, `.xbm` gives Xbm, alongside `.tga`.
- `ClipboardImage.imageExtensions` adds `.ico .cur .pbm .pgm .ppm .pnm .pam .qoi .ff .pcx .xbm .xpm .jls`.
- Tests in `ArctisImaging.Tests`: `IcoTests`, `PnmTests`, `QoiTests`, `FarbfeldTests`, `PcxTests`, `XbmTests`, `XpmTests`. Fixtures in `ArctisImaging.Tests/Fixtures` (Ppm/Pgm/Pbm*, Pcx*, Qoi*, XbmBits.xbm, IcoPng.ico, IcoBmp.ico), each with a reference .png beside it.
- Binary PNM (P5, P6, P7) at maxval 255 takes `PnmDecoder.DecodeDirect` (see "PNM direct path" below). `XbmDecoder` is optimised (see "XBM fast path" below) and so is `PcxDecoder` (see "PCX fast path" below); `BmpDecoder` writes 32/24/16-bit DIBs direct (see "BMP direct rows" below), and 1/4/8-bit palettes and RLE4/RLE8 too (see "BMP indexed rows and RLE" below), and so does every bit-field form via a SIMD field kernel (see "BMP bit-field rows" below): BMP is fully optimised. Every other decoder is scalar, a first pass: **unoptimised**; speed measured 2026-11-07 (table below). No data XML touched.

## Measured speed (2026-11-07)

Method: scratch console bench, not a test, not committed (no perf tests). Release, net10.0, Windows, Ryzen 9 9900X. Inputs: `Brick.png` + `Brick2.png` (two 1024² RGBA) encoded into each format by Pillow 13 / imagecodecs 2026.10.10; ICO is the same images resized to 256², one entry. Per case the 2-image set is decoded per pass; 40 warm-up passes, 6 timed passes, median ms for the set; 3 processes; ratio = Arctis / reference, range over the 3 processes. Arctis decodes via `ImageCodec.Decode` with a format hint into a preallocated buffer (RGBA8 colour, R8 grey). Reference: ImageSharp 3.1.11 in the same process where it reads the format; otherwise a native C decoder called from Python (CPython 3.14), a cross-runtime ratio, indicative only. Pillow decodes into the file's native mode while Arctis expands to RGBA8, so Pillow ratios flatter Pillow, most for XBM and palette PCX.

| Case | Arctis ms | Reference ms | Ratio |
|---|---|---|---|
| PPM (before the direct path) | 6.9-7.3 | ImageSharp 0.33-0.39 | ~20x |
| PPM (direct path, RGBA8 / BGRA8) | 0.130-0.177 | ImageSharp 0.33-0.39 | 0.36-0.45x, faster |
| PGM -> R8 (direct path; before 4.69-4.77) | 0.047-0.049 | ImageSharp 0.18-0.19 | 0.25-0.27x, faster (before ~25x) |
| PGM -> RGBA8 (direct path; before 3.22-3.24) | 0.130-0.132 | ImageSharp 0.93-0.96 | 0.14x, faster |
| QOI | 8.8-9.2 | ImageSharp 14.5-15.0 | 0.59-0.63x, faster (imagecodecs qoi 10.9: 0.81-0.84x) |
| farbfeld | 2.3-3.4 | no reference | - |
| PCX 24-bit (before the fast path) | 12.0-14.2 | Pillow 10.9-11.3 | 1.1-1.3x (after 2026-11-08: 3.06-3.27 ms, see "PCX fast path") |
| PCX 8-bit palette (before the fast path) | 5.8-6.0 | Pillow 4.0-5.1 | 1.2-1.5x (after 2026-11-08: 2.73-2.79 ms, see "PCX fast path") |
| XBM (before the fast path) | 4.58-4.61 RGBA8, 5.96-5.98 R8 | Pillow 1.80-1.99 | 2.4-2.6x (first measure 4.7-4.9 ms, 2.3-2.6x) |
| XBM (fast path, three SIMD tiers, 2026-11-07) | default 512 tier, 5 processes per tier: 1.17-1.29 RGBA8, 1.10-1.16 R8 (256 tier 1.17-1.25 / 1.11-1.12; 128 tier 1.21-1.25 / 1.14-1.21) | Pillow 1.79-1.81 | 0.65-0.72x RGBA8, 0.61-0.65x R8, faster |
| XPM (64 colours, 1 char/pixel) | 3.0 | Pillow 250-256 | 0.012x, ~80x faster |
| ICO, PNG entry 256² | 0.71-0.79 | Pillow 2.19-2.22 | 0.32-0.36x, faster |
| ICO, BMP entry 256² (before the direct rows; retracted as the current figure, after 2026-11-08 see "BMP direct rows") | 0.34 | Pillow 0.27-0.28 | 1.2-1.3x |

- Allocation per decode: Arctis 0 B for all except ICO (~2 KB per set); ImageSharp ~4 KB per set.
- PNM was the outlier (~3 ms per megapixel); cause and fix are in "PNM direct path" below. The PPM/PGM rows above were re-measured 2026-11-07 (Brick.ppm/Brick2.ppm and .pgm, 5 processes after, 3 before); the other rows are from the first bench.
- Decoded outputs were not compared against the references in this bench (correctness is covered by the existing tests). Not measured: ARM64, net8.0, CUR, PAM, 16-bit PNM, multi-entry ICO.

## PNM direct path (2026-11-07)

- New private `PnmDecoder.DecodeDirect(data, in Info, destination, pitch, layout)` returns bool; `Decode` calls it when `!plain && !bits && maxval == 255`.
- Depth 1: R8 row copy, RGBA8/BGRA8 via `PixelRows.FromGrey`, RG8 scalar interleave with 255. Depth 3: RGBA8/BGRA8 via `PixelRows.FromRgb`. Depth 4: RGBA8 copy, BGRA8 `ImageOps.CopySwizzled`.
- Returns false (general path) for depth 2 and for depth 3/4 into R8/RG8.
- New `PixelRows.FromGrey(grey, row, width)` (grey to opaque RGBA8/BGRA8) and `PixelRows.FromRgb(rgb, row, width, bgra)`: AVX-512 / AVX2 / SSE tiers + scalar tail, moved from PNG's `Expand` and shared with it. `FromRgb` bounds its vector loads by the source span's length, not width, so back-to-back PNM rows get vector loads to the row end except on the last row (scalar tail).
- Tests: `PnmTests.RawDecodesIntoEveryLayout` (PpmRgb.ppm / PgmGrey.pgm into BGRA8, R8, RG8 vs the Pillow PNG reference), `OddWidthRowsDecodeExactly` (P6/P5/P7 at width 37 x 3 rows, every byte).
- Bench method as the table above, plus RGB-8 and grey-8 PNGs of the same images written by ImageSharp 3.1.11; 40 warm-up, 6 timed passes, median for the 2-image set. Decoded checksums identical before and after for every case; allocation 0 B before and after.

| PNG (unchanged by the move) | Before ms | After ms | ImageSharp ms |
|---|---|---|---|
| RGB-8 -> RGBA8 | 17.98-18.40 | 18.05-18.24 | ~36-37 |
| RGB-8 -> BGRA8 | 17.97-18.22 | 18.03-18.28 | ~36-38 |
| grey-8 -> RGBA8 | 17.35-17.82 | 17.37-17.68 | ~16-17 |

**The old slowness was the per-sample structure, not the data volume.**
A branch tree (plain / wide / bits) ran inside the pixel loop, each pixel was written to a temporary RGBA row, then a second pass ran `PixelRows.FromRgba`; for R8 that pass ran `Luma(v,v,v)` per pixel, which made PGM -> R8 slower than PGM -> RGBA8.

**The SIMD lives once in `PixelRows`, not copied into `PnmDecoder`.**
One copy of ~50 lines, no drift between PNG and PNM. It cost touching PNG's measured hot path, so PNG was re-benched (condition: PNG speed must not drop); it did not.

**Only binary maxval 255 gets the direct path.**
Plain text, PBM bits, 16-bit, other maxvals, grey+alpha PAM and RGB -> R8/RG8 keep the per-sample path: rare variants, left unoptimised on purpose.

## Why these choices

**Above 8 bits the high byte is kept; below 8 bits samples scale to 0-255 as `(v*255 + max/2)/max`.**
`PixelLayout` has only 8-bit layouts, so this follows the TIFF rule in [[other-raster-formats]]. Netpbm scales from any maxval with the same formula; farbfeld keeps the high byte.

**ICO and CUR share one `ImageFormat.Ico`.**
Nothing downstream would treat them differently. The CUR hotspot is not exposed.

**ICO is recognised by magic.**
`00 00 01 00` or `00 00 02 00`, a count of at least 1, and every entry's offset and length inside the file. A hint-only rule would have been weaker for a format that does have a signature.

**ICO entry selection: largest area, then most bits per pixel, read from each entry's own header.**
That is the PNG IHDR or the DIB header with its height halved, not the directory bytes, which are unreliable (0 means 256, writers leave them wrong). This departs slightly from the plan's wording. A PNG entry scores channels times bit depth.

**ICO DIB entries: AND mask applied only when the 32-bit alpha is zero everywhere.**
The header holds twice the height; a 1-bit AND mask follows the colour rows bottom-up, a set bit means transparent and the RGB is kept. A 32-bit entry keeps its own alpha (Windows behaviour, same rule as `BmpDecoder`). A mask that runs past the end is ignored.

**PNM is recognised by magic.**
`P1`-`P7` followed by whitespace, accepted only if the whole header parses. Requiring the extension hint was rejected. PBM 1 means black. PAM `BLACKANDWHITE` is grey, so 1 means white, as the spec says. Only the first image in a file is decoded. Comments are skipped in the header and in plain rasters.

**PCX and XBM are recognised only with the extension hint, like TGA.**
A PCX file's first bytes (0x0A, version, encoding) are too weak a signature, and XBM is C text with no magic.

**PCX details.**
RLE runs may carry over between scanlines. 8-bit single-plane files use the 256-colour palette after the 0x0C trailer, or grey if there is none. 1-bit with 2-4 planes and 2/4-bit single-plane files use the 16-entry header palette. 1-bit single-plane is black and white.

**XBM: a set bit is black, the X11 convention.**
Pillow reads a set bit as white, so the XbmBits fixture's reference PNG is Pillow's decode inverted. Both X11 `char` and X10 `short` arrays are read, bits least significant first.

**XPM: XPM3 only; a named colour throws `NotSupportedException` (user choice, 2026-11-07).**
Colour key preference is `c`, then `g`, then `g4`, then `m`. Accepted values: `#RGB`, `#RRGGBB`, `#RRRGGGBBB`, `#RRRRGGGGBBBB`, and `None` (transparent). The rejected alternative was embedding the X11 rgb.txt table (about 750 entries). Keys of 1-2 characters per pixel use a flat direct table; 3-8 use a `Dictionary<ulong, uint>`; more than 8 is rejected.

**Test oracle: Pillow 13 wrote the PNM, PCX, QOI, XBM and ICO fixtures** (oracle choice for the JPEG family is in [[lossless-jpeg]]).
Pillow clips 16-bit grey when converting to RGBA, so the `Pgm16` reference is the 8-bit grey the file was made from.

## XBM fast path (2026-11-07)
- `XbmDecoder.Decode` is `[AggressiveOptimization]`. It parses one row of values into a rented `bits` byte row (X10 shorts split low byte then high), then calls private `ExpandRow(byte[] bits, int width, ref byte d, PixelLayout)` (`NoInlining | AggressiveOptimization`); `WriteTail(int value, int count, ref byte d, PixelLayout)` does the row's last `width & 7` pixels and any whole byte a tier leaves over (count 8), scalar. Bits expand straight into the destination layout: no RGBA scratch row, no `PixelRows.FromRgba` pass.
- `TryReadInfo` finds `{` first and looks for `#define`s only in `data[..brace]`. It runs twice per `ImageCodec.Decode` (identify, then decode); the old loop called `IndexOf("#define")` after the last define and scanned the rest of the file (~660 KB) each time.
- `ReadValue` uses a 256-entry `charClass` table (digit value 0-15, `Separator` 0x40 for space/tab/CR/LF/comma, `NotDigit` 0xFF). Same acceptance and same throws as before.
- No expand lookup tables remain (`greyOfByte`, `greyAlphaOfNibble`, `rgbaOfNibble` and their builders are gone); `charClass` (the hex-parse table) stays. New `using System.Runtime.Intrinsics.X86`.

**Every layout expands with three SIMD widths, no tables.**
User rule (2026-11-07): "If possible always have all 3 simd size variants"; tables stay only where nothing vector fits, and here nothing table-based is left in the expand.
- Tiers are tried in order, each hands its remainder down: 512 when `Vector512.IsHardwareAccelerated && Avx512BW.IsSupported`, 256 when `Vector256.IsHardwareAccelerated && Avx2.IsSupported`, then 128 (cross-platform `Vector128`, also NEON). Leftover whole bytes and the row's last `width & 7` pixels go to `WriteTail`.
- Every tier: broadcast the source bytes into a vector; for R8/RG8 an in-lane byte shuffle (`Avx512BW.Shuffle` / `Avx2.Shuffle` / `Vector128.Shuffle`) routes each byte to its lanes; AND with a per-lane bit mask; compare to zero. Clear bit gives 0xFF (white), set bit 0 (black); a mask lane of 0 (the alpha lanes) is always 0xFF.
- Source bytes per step:

| Layout | 512 | 256 | 128 |
|---|---|---|---|
| R8 | 8 (ulong broadcast + shuffle) | 4 (uint + shuffle) | 2 (ushort + shuffle) |
| RG8 | 4 (uint + shuffle) | 2 (ushort + shuffle) | 1 (byte broadcast) |
| RGBA8/BGRA8 | 2 (ushort broadcast + shuffle) | 1 (byte broadcast) | 1 (two 128-bit compares) |

- Isolated expand bench, 2048 rows x 128 source bytes = 2 M pixels, one process, 3 processes, medians, all outputs byte-equal to the table version (ms):

| Layout | Old table | 128-bit | 256-bit | 512-bit |
|---|---|---|---|---|
| RGBA8 (8 MB out) | 0.202-0.211 | 0.108-0.110 | 0.097-0.098 | 0.080-0.083 |
| R8 (2 MB out) | 0.096-0.097 | 0.050-0.051 | 0.024 | 0.019 |
| RG8 (4 MB out) | 0.137 | 0.065-0.067 | 0.047-0.050 | 0.041-0.044 |

- The 128-bit RGBA compare is 0.108-0.110 ms with VEX/AVX2 encoding but 0.191 ms SSE-only (`DOTNET_EnableAVX2=0`), against 0.202-0.203 ms for the old table under SSE: it beats the table in both encodings, which is why the table went.
- **Rejected:** a 512-bit RGBA variant joining two 256-bit broadcasts (`Vector512.Create(Vector256, Vector256)`): 0.106-0.107 ms, slower than plain 256. The shipped 512 tier broadcasts a ushort and splits it with `Avx512BW.Shuffle`.
- **Full decode: the tiers cannot be told apart** (final table in "Measured speed"). Parsing is ~0.9-1.0 ms of a ~1.15 ms decode and the bench swings about +-0.15 ms between processes of an unchanged build; the expand win shows only in the isolated bench.
- **.NET 10 tier switches for testing a tier:** `DOTNET_EnableAVX512F=0` is IGNORED (`Vector512.IsHardwareAccelerated` stays true). `DOTNET_EnableAVX512=0` or `DOTNET_PreferredVectorBitWidth=256` drops to the 256 tier, `DOTNET_EnableAVX2=0` to 128, `DOTNET_EnableHWIntrinsic=0` to no SIMD (software Vector128). See also [[simd-width-tiers]].

**`ExpandRow` is its own NoInlining method.**
With the Vector256 branch inline in `Decode` the whole decode got about 0.15 ms slower (1.27-1.29 ms against 1.10-1.12 ms for R8, whose own code was identical; builds alternated over 5 rounds). The vector code degraded the parse loop's codegen in the shared method. Split out, the combined build matches the table-only build.

**Parsing now dominates.**
About 0.9-1.0 ms of the ~1.15 ms is parsing, 0.1-0.2 ms expand. SIMD hex parsing (vector-scanning for `x`, reading the two digits after it) was deliberately left out: it drops validation between values. Consider it only if more speed is wanted.

Measurement: as the table above, with Brick.png + Brick2.png converted by Pillow 13 `convert("1")` and saved as XBM (X11 char arrays, Pillow's `0x04,` format, ~664 KB each). 40 warm-up passes, 6 timed, median. Before: 3 processes; after: 5 processes per tier; Pillow decodes into its native 1-bit mode, 3 processes beside the final runs. 0 B allocated per decode, before and after. The full-decode bench swings about +-0.15 ms between processes of an unchanged build, so the expand variants were compared in isolation.
- Test-verified: ArctisImaging.Tests 442/442 Release (incl. 5 `XbmTests`); builds clean net8.0 and net10.0; SHA-256 of every output identical to the original (pre-optimisation) decoder over 5 inputs (1024^2 x2, 1021x1019 odd width x2, an X10 `short` version of the odd-width image) x 4 layouts (RGBA8, BGRA8, R8, RG8) = 20 cases, all matched under each of: default (512), `DOTNET_EnableAVX512=0` and `DOTNET_PreferredVectorBitWidth=256` (256), `DOTNET_EnableAVX2=0` (128), `DOTNET_EnableHWIntrinsic=0` (no SIMD). Width 1021 = 127 whole bytes per row, so each tier's remainder hand-down and `WriteTail` both run.
- Not checked: ARM64 / NEON (speed or correctness on hardware); net8.0 runtime speed; X10 short speed. Not GUI-verified (no .xbm pasted into Thorium).
- Gap: parsing dominates; SIMD parsing still not done (it drops validation between values).

## PCX fast path (2026-11-08)
- `PcxDecoder.Decode` (same signature): `direct = bits == 8 && layout is RGBA8 or BGRA8`; `grey` = 8-bit single plane whose palette is the identity grey ramp (always true with no 0x0C trailer); for direct BGRA8 non-RGB the palette is swizzled once. Line buffer rented at `lineBytes + 64` (slack for whole-vector stores); the scratch RGBA row is rented only when not direct. Direct rows: RGB(A) to `InterleavePlanes`, grey to `PixelRows.FromGrey`, other palette to `LookupRow`. Sub-byte formats are in "PCX sub-byte path" below; 8-bit palette/RGB into R8/RG8 keeps the old per-pixel loop + `PixelRows.FromRgba`: unoptimised.
- `ReadLine(data, ref pos, end, line, n, rle, ref runLeft, ref runValue)` is `[AggressiveOptimization]` and throws `ArgumentException` if `line.Length < n + 64`. Plain lines are one `CopyTo`. RLE: the run carried from the previous scanline first (`Span.Fill`), then a tiered literal scan at 512 (`Vector512.IsHardwareAccelerated && Avx512BW.IsSupported`, `p <= end - 65`), 256 (`Vector256.IsHardwareAccelerated && Avx2.IsSupported`, `end - 33`), 128 (`Vector128.IsHardwareAccelerated`, portable incl. NEON, `end - 17`); each tier hands its remainder down, then the original byte-checked scalar loop finishes. Each step: load a vector of input, store it unconditionally, k = min(tzcnt(mask of bytes >= 0xC0), n - i), advance i and p by k; a stopping code byte is decoded by `StoreRun`. Same exceptions and carry-over as before.
- `StoreRun` (`AggressiveInlining`): count = code & 0x3F, value = next byte, one 16-byte broadcast store, four when count > 16, advance by min(count, n - i), remainder carried in `runLeft`/`runValue`.
- `InterleavePlanes(line, bpl, width, alpha, row, bgra)` (`NoInlining | AggressiveOptimization`, same split as XBM's `ExpandRow`): portable `WidenLower/WidenUpper` ladder at 512 (64 px/step, Avx512BW-gated), 256 (32 px, Avx2-gated), 128 (16 px): (r | g << 8) and (b | a << 8, or 0xFF00) as ushorts, then (rg | ba << 16) as uints; BGRA8 swaps the R and B plane refs; scalar tail. `LookupRow` is scalar `palette[index]` via `Unsafe.WriteUnaligned`.
- New usings: `System.Numerics`, `System.Runtime.CompilerServices`, `System.Runtime.InteropServices`, `System.Runtime.Intrinsics`, `System.Runtime.Intrinsics.X86`. No data XML, no public API change.
- Tests in `PcxTests`: `MatchesPillowInBgra` (4 fixtures into BGRA8 against the reference PNG decoded into BGRA8) and `OddWidthRowsDecodeExactly(planes, greyRamp, layout)` (hand-built plain 117x3 files, bpl 118; 117 = 64+32+16+5 so every vector width and the scalar tail run; 7 cases).

## PCX sub-byte path (2026-11-08)
- **Moved 2026-11-08:** `BitIndices`, `PackedIndices`, `LookupIndices`, `LookupRow` (and the three private `Split` overloads) are `internal`/private members of `PixelRows`, not `PcxDecoder`; bodies unchanged, `PcxDecoder.Decode` calls `PixelRows.*`. BMP uses them too, see "BMP indexed rows and RLE". Names below refer to the kernels, wherever they live.
- `PcxDecoder.Decode` (same signature): `packed = bits < 8`; `direct = packed || (bits == 8 && layout is RGBA8/BGRA8)`. A 48-byte stackalloc `tables` holds three 16-byte channel tables from palette entries 0-15 in destination byte order (bytes 0/1/2 of the palette uint); the BGRA swizzle of the palette also runs for sub-byte BGRA8, so the tables need no swap. For R8/RG8 the first table holds `PixelRows.Luma(r,g,b)` per entry. Index row `idx` rented at `width + 256` when packed, returned in the `finally`. The per-pixel fallback loop now serves only 8-bit R8/RG8.
- `BitIndices(line, bpl, planes, idx, width)` (`NoInlining | AggressiveOptimization`): one palette index per pixel from 1-bit plane lines, MSB first. Each tier broadcasts source bytes (ulong/uint/ushort for 64/32/16 px), does an in-lane byte shuffle (`Avx512BW.Shuffle` / `Avx2.Shuffle` / `Vector128.Shuffle`, XBM's pick pattern), ANDs with the bit mask 128..1 and compares equal to it. Planes run highest down with `v = v + v | (set & 1)`. Throws `ArgumentException` if `line.Length < planes * bpl + 64` or `idx.Length < width + 64`.
- `PackedIndices(line, bits, idx, width)` (same attributes): 2/4-bit packed bytes to indices, high bits first. A byte widened to a ushort becomes `(x >> 4) | (x & 15) << 8`, nibbles in pixel order with no permute; 2-bit applies the same split again with `(2, 3)`. Portable `WidenLower/WidenUpper`; 512 and 256 gated on `Avx512BW` / `Avx2`. Throws if `line.Length < (width*bits+7)/8 + 64` or `idx.Length < width + 256`. `Split` (three overloads, `AggressiveInlining`) = `(u >> shift | (u & mask) << 8).AsByte()`.
- Both index kernels run only the widest available tier, no scalar tail: they write whole vectors past `width`, reading inside the line's 64-byte slack and writing inside `idx`'s slack. The 128 tier has no `IsHardwareAccelerated` gate, so with no hardware intrinsics it runs software `Vector128`, which is correct.
- `LookupIndices(idx, tables, width, row, layout)` (same attributes): each index (0-15) is shuffled out of each channel table, the 16-byte table broadcast to every 128-bit lane. RGBA8/BGRA8 widen `(c0 | c1 << 8)` and `(c2 | 0xFF00)` into pixels with the `InterleavePlanes` ladder; RG8 stores `luma | 0xFF00` ushorts; R8 stores the shuffled bytes. Tiers of 64/32/16 px hand remainders down, then a scalar tail; it never writes past `width * size`. The 128 tier uses `Vector128.Shuffle` with variable indices, the first variable-index shuffle in Arctis.Imaging.
- No data XML, no public API change, no new usings.
- Test `PcxTests.SubByteRowsDecodeExactly(bits, planes, layout, rle)`, 28 cases: 1-bit 1/2/3/4 planes, 2-bit and 4-bit crossed with the 4 layouts as plain files, plus RLE in BGRA8 for 1-bit 1 plane, 1-bit 4 planes, 2-bit and 4-bit. Hand-built 245x3 files (245 = 3x64 + 32 + 16 + 5, so every lookup tier and the scalar tail run), header palette bytes `i * 13 + 7`, RLE runs of 50 bytes crossing scanlines; expected values from a scalar reference using `PixelRows.Luma` for R8/RG8.

**Two passes per row (indices into a small rented row, then a 16-entry table lookup), not a fused kernel.**
One lookup kernel serves all six sub-byte formats and all four layouts; both passes run on cache-resident rows and the output write dominates.

**Palette lookup is a byte shuffle per channel from 16-byte tables, not a gather or `PermuteVar16x32`.**
Only 16 entries, so pshufb fits at every width including 128/NEON; `PermuteVar16x32` would exist only at 512.

**R8/RG8 are included (user choice 2026-11-08: "Go, include R8/RG8").**
A luma table replaces the first channel table; byte-identical to the old `FromRgba` path because the palette entries are all opaque. 1-bit single-plane runs the same path as EGA with palette {0 black, 1 white}.

**`Vector128.Shuffle` with variable indices is kept, no `Ssse3.Shuffle` fallback.**
The plan flagged it as a risk on net8; on net10 the 128 tier measures 0.49 ms against 0.27 ms for the 256 tier on 1-bit, not pathological.

Measured (scratch console bench, not committed; Release, net10.0, Windows, Ryzen 9 9900X; `Brick.png` + `Brick2.png`, each format hand-built in Python from a non-dithered quantise to 2/4/8/16 colours, 1-bit 1 plane thresholded at 128, one RLE stream per scanline; "Pillow 1-bit" is Pillow 13's dithered `convert('1')` writer; `ImageCodec.Decode` with the `Pcx` hint into a preallocated buffer, 40 warm-up, 6 timed, median ms for the 2-image set; before vs after 5 alternating processes of a snapshot of the previous decoder, other tiers 3 processes; reference imagecodecs 2026.10.10 `pcx_decode`, cross-runtime, indicative only, outputs 1-byte bool for 1-bit and 3-byte RGB otherwise, which favours it):

| Case (RGBA8 unless noted) | Before ms | After ms | Change | 256 tier ms | 128 tier ms | imagecodecs ms |
|---|---|---|---|---|---|---|
| Pillow 1-bit (dithered) | 2.90-2.98 | 0.211-0.223 | 13-14x | 0.292-0.301 | 0.536-0.561 | 0.97-0.98 |
| 1-bit, 1 plane | 2.91-2.93 | 0.199-0.232 | 13-15x | 0.273-0.280 | 0.486-0.496 | 0.83-0.85 |
| 1-bit, 2 planes | 4.15-4.42 | 0.642-0.663 | 6.4-6.7x | 0.688-0.689 | 0.925-0.930 | 4.88-5.16 |
| 1-bit, 3 planes | 5.27-5.69 | 0.958-0.983 | 5.5-5.8x | 0.972-0.981 | 1.243-1.255 | 6.41-6.43 |
| 1-bit, 4 planes (EGA) | 6.51-6.71 | 1.214-1.347 | 5.0-5.4x | 1.224-1.239 | 1.536-1.551 | 7.90-7.96 |
| 2-bit, 1 plane | 2.74-2.84 | 0.398-0.428 | 6.6-6.9x | 0.459-0.465 | 0.650-0.657 | 3.51-3.52 |
| 4-bit, 1 plane | 3.25-3.50 | 0.946-1.012 | 3.4-3.5x | 0.921-0.933 | 1.133-1.146 | 4.66-5.01 |

Other layouts, after (before), ms:

| Case | BGRA8 | R8 | RG8 |
|---|---|---|---|
| 1-bit 1 plane | 0.202-0.216 (2.95-3.00) | 0.131-0.134 (4.40-4.43) | 0.141-0.145 (4.89-4.97) |
| EGA | 1.22-1.39 (6.58-6.61) | 1.16-1.17 (8.09-8.87) | 1.22-1.29 (8.63-9.33) |
| 2-bit | 0.401-0.406 (2.76-2.81) | 0.329-0.331 (4.23-4.26) | 0.335-0.345 (4.90-5.22) |
| 4-bit | 0.98-1.04 (3.31-3.44) | 0.850-0.858 (4.80-4.88) | 0.89-0.99 (5.34-5.71) |

- Ratios to imagecodecs after: 1-bit 0.24-0.28x, EGA 0.15-0.17x, 2-bit 0.11-0.12x, 4-bit 0.19-0.22x (all faster).
- Unprofiled hypothesis: the remaining cost on multi-plane and 4-bit files is RLE reading (`ReadLine`); those quantised streams are mostly literals and short runs and the files are larger than 1-bit 1-plane. Not investigated.
- Verified: test-verified, `ArctisImaging.Tests` 487/487 Release net10.0 under default, `DOTNET_EnableAVX512=0`, `EnableAVX2=0` and `EnableHWIntrinsic=0`; builds clean net8.0 + net10.0; scratch SHA-256 of every output identical to a snapshot of the previous decoder, 38 files x 4 layouts = 152 cases, under default, `EnableAVX512=0`, `PreferredVectorBitWidth=256`, `EnableAVX2=0`, `EnableHWIntrinsic=0` (both Brick images at 1024² in all six formats, odd 1021x1019 crops as one RLE stream with runs crossing scanlines, plain 245x7 crops, Pillow's dithered 1-bit writer). Not GUI-verified.
- Gaps: net8.0 runtime speed/correctness not run (test project is net10.0 only; the variable-index `Vector128.Shuffle` on net8 is the open question); ARM64/NEON not checked; 8-bit palette/RGB into R8/RG8 stays on the old loop (unoptimised).

**RLE reading was the cost, so the literal scan was added (user approved after the first pass, 2026-11-08).**
Pillow-written Brick PCX: the 24-bit stream is 90% literal codes, 1% run-1, 9% run>1; palette 66/19/15; grey 90/0/10. After the first pass (direct rows, tight scalar `ReadLine`) the 24-bit decode was still 9.4-10.3 ms, about 1.6 ns per code: the per-byte literal/run branch and a `Span.Fill` per short run.

**Rejected: a branchless scalar reader** (cmov for code/literal, count, value, advance).
11.8-12.8 ms against 9.4 ms for the branchy one: each code's position depends on the previous code, so the serial load, compare, advance chain replaces the predicted branch.

**The 512-bit scan tier is slower than 256 on this hardware and was kept anyway (user choice 2026-11-08: "Keep all three").**
Isolated with the 512 interleave left on, 4 processes each, ms per 2-image set: 24-bit RGB 3.15-3.19 (512 scan) vs 2.91-3.06 (256 scan); palette 2.86-2.92 vs 2.58-2.71; grey 1.15-1.18 vs 1.07; dark short-run 4.44-4.59 vs 3.96-3.99. Literal stretches are typically 2-10 bytes, so each 64-byte store mostly overlaps the next and usually splits a cache line (unprofiled hypothesis). The 512 interleave tier is not the slow part. `DOTNET_EnableAVX512=0` makes the whole decoder faster here; contrast XBM, where the slower 512 variant was rejected. See [[simd-width-tiers]].

**Direct rows skip the RGBA scratch + `FromRgba` pass** for 8-bit RGBA8/BGRA8 output, as XBM and PNM did. R8/RG8 and sub-byte formats were deliberately left on the old path (rare).

**Palette lookup is scalar; an AVX2 gather was not tried (user chose scalar in the plan, 2026-11-08).**
A gather has no 128 or 512 counterpart, and gathers rarely beat scalar lookups on Zen.

**Grey-ramp palettes go through `PixelRows.FromGrey`.**
Pillow writes greyscale PCX as 8-bit with an identity grey trailer (the PcxGrey fixture has one), so detection is one 256-entry compare.

**Interleave uses portable `VectorNNN.WidenLower/WidenUpper` + shifts at all widths, not x86 unpack/shuffle.**
Widen is order-preserving across lanes, so no permute is needed, and the 128 tier is NEON-portable.

Measurement: scratch console bench, not committed. Release, net10.0, Windows, Ryzen 9 9900X. Inputs Brick.png + Brick2.png (two 1024² RGBA) saved by Pillow 13 as RGB, P (`quantize(256)`) and L PCX; 4-plane RGBA and the dark image (RGB halved, `v // 2`, 1021x1019, many short runs) hand-encoded per scanline. 40 warm-up passes, 6 timed, median ms for the 2-image set. Before vs after: 5 alternating processes of a snapshot build of the old decoder and the new one; lower tiers 3 processes each. Pillow 13: same set, one process, decoding into its native mode (flatters Pillow for palette/grey).

| Case | Before ms | After (512, default) ms | 256 tier ms | 128 tier ms | Change | Pillow 13 ms |
|---|---|---|---|---|---|---|
| 24-bit to RGBA8 | 12.1-13.2 | 3.06-3.27 | 2.83-2.97 | 3.42-3.66 | 3.8-4.2x | 10.7 |
| 24-bit to BGRA8 | 12.1-12.5 | 3.04-3.12 | 2.83-2.94 | 3.42-3.61 | 3.9-4.1x | - |
| 4-plane RGBA to RGBA8 | 13.4-13.9 | 3.14-3.27 | 2.96-3.21 | 3.57-3.76 | 4.2-4.3x | - |
| 8-bit palette to RGBA8 | 5.8-6.0 | 2.73-2.79 | 2.51-2.55 | 2.49-2.66 | 2.1-2.2x | 3.9 |
| Grey palette to RGBA8 | 5.0-5.4 | 1.04-1.15 | 0.93-0.99 | 1.15-1.16 | 4.5-4.8x | 3.0 |
| Dark 24-bit, short runs to RGBA8 | 14.7-15.7 | 4.27-4.73 | 3.86-3.90 | 3.98-4.02 | 3.4-3.5x | - |

Intermediate step (first pass only, before the literal scan): 24-bit 9.4-10.3, 4-plane 9.4-9.9, palette 4.5-4.9, grey 3.1-3.4 ms. Ratios to Pillow after: 24-bit 0.29-0.31x, palette 0.70-0.72x, grey 0.35-0.38x (all faster). Allocation: 24 B per 2-image set, identical before and after, source not investigated (the earlier note recorded 0 B).

Verified: test-verified, `ArctisImaging.Tests` 453/453 Release net10.0 (18 `PcxTests`, which also pass under `DOTNET_EnableAVX512=0`, `EnableAVX2=0`, `EnableHWIntrinsic=0`); builds clean net8.0 + net10.0, no warnings; scratch SHA-256 of every output identical to a snapshot of the original decoder over 22 files x 4 layouts (RGBA8, BGRA8, R8, RG8) = 88 cases (Pillow RGB/P/L at 1024² and 1021x1019, hand-built 4-plane RGBA, RLE streams carrying runs across scanlines, the dark short-run image) under default, `EnableAVX512=0`, `PreferredVectorBitWidth=256`, `EnableAVX2=0` and `EnableHWIntrinsic=0`.

Gaps: ARM64/NEON and net8.0 runtime speed not checked; 8-bit palette/RGB into R8/RG8 remains unoptimised first-pass code; not GUI-verified (the open WIP item "GUI-verify pasting the 2026-11-07 formats" still covers PCX).

## BMP direct rows (2026-11-08)

- `BmpDecoder` writes the common DIB forms straight into the destination: 32-bit BGRA with alpha, 24-bit BGR, 16-bit 555/565 without alpha. ICO/CUR DIB entries and plain .bmp files take the same paths. The per-pixel bit-field loop through an RGBA8 scratch row stays for everything else. The palette is `stackalloc uint[256]` (1 KB), no longer `new uint[256]` per decode.
- New private `DecodeDirect(ReadOnlySpan<byte> pixels, in Info, int stride, Span<byte> destination, int pitch, PixelLayout)` returns bool; private `Row32`, `Row24`, `Row16(src, dst, width, layout, bool wideGreen)`, `HasAnyAlpha(ReadOnlySpan<byte> bgra)`. (Since 2026-11-09 `Row32`, `Row24`, `Row16`, `ForceOpaque` and `HasAnyAlpha` live in internal `PixelRows`, shared with `TgaDecoder`; bodies unchanged, see [[other-raster-formats]] "TGA decode fast paths".)
- New private `DecodeRows(pixels, in Info, int stride, ReadOnlySpan<uint> palette, destination, pitch, layout)` returns anyAlpha: the old general row loop moved out of `Decode` unchanged. `Decode` was then `anyAlpha = DecodeDirect(...) || DecodeRows(...)` followed by the mask/opaque tail (superseded: see "BMP bit-field rows", where `Decode` owns `anyAlpha`).
- `DecodeRle`'s palette parameter changed from `uint[]` to `ReadOnlySpan<uint>`. `ApplyMask` gains `bool opaque`; the call passes `info.aMask == 0`.
- Tests: `BmpTests.DirectRowsWriteEveryLayout` (Theory "32"/"24"/"555"/"565"; 61 wide, both row orders, all four layouts, padded pitch checked untouched), `IcoTests.TwentyFourBitEntryTakesAlphaFromTheMask` (Theory RGBA8/RG8). The `IcoTests.IconDib` helper now writes 24-bit pixels. No data XML.

**Which forms go direct.**
32-bit qualifies only with masks exactly 0x00FF0000/0x0000FF00/0x000000FF/0xFF000000 (BI_RGB 32 and V4/V5 BGRA). 16-bit qualifies with r/g/b exactly 555 (0x7C00/0x03E0/0x1F) or 565 (0xF800/0x07E0/0x1F) and aMask 0. Any other mask set (ARGB1555, 4444, odd 32-bit fields) was left to `DecodeRows` here; since 2026-11-08 it takes `RowFields`, and 32-bit BGRX (BGRA masks with `aMask == 0`) is accepted by `DecodeDirect` too.

**A 32-bit block whose alpha is zero everywhere: the icon AND mask applies and a non-icon becomes opaque.** The exception is R8, where alpha is never written. The check is `HasAnyAlpha`, a scan of the source alpha that stops at the first non-zero value. It reads the source, never the destination, because the destination may be a mapped GPU staging buffer. (Originally a scalar scan that sent all-zero files to `DecodeRows` plus a `MakeOpaque` post-pass; superseded 2026-11-08, see "BMP bit-field rows": SIMD scan up front, one-pass opaque decode.)

**Row writers.**
- `Row32`: RGBA8 = `ImageOps.CopySwizzled`; BGRA8 = straight copy; R8/RG8 = scalar `PixelRows.Luma`.
- `Row24`: RGBA8/BGRA8 = `PixelRows.FromRgb` with the swap flag set for RGBA8; R8/RG8 scalar luma.
- `Row16` RGBA8/BGRA8 has Vector512/256/128 tiers plus a scalar tail. Channels are widened by bit replication in 16-bit lanes; byte 0 and green pair with byte 2 and 0xFF00, then `VectorN.Widen` interleaves them into 32-bit pixels. Byte 0 is red for RGBA8 and blue for BGRA8, chosen by the shift amount, so the loop has no branch. Bit replication matches `Field.Extract`: 5-bit is c<<3|c>>2, 6-bit is c<<2|c>>4. R8/RG8 is scalar luma.

**The opaque AND mask skips zero bytes and writes alpha branchless.**
`ApplyMask` runs in opaque mode when aMask == 0 (rows from a palette, 24-bit, or 16-bit without alpha): it skips zero mask bytes and writes `(byte)(((m >> (7 - k)) & 1) - 1)` for the others. The old per-pixel loop stays for rows that may not be opaque (32-bit zero-alpha, 16-bit with an alpha field). The first prototype wrote only the set bits behind a branch; that cost 0.37 ms on a random mask (mispredicts), and the branchless write brought it to 0.089.

**`[MethodImpl(AggressiveOptimization)]` on `DecodeDirect`, `Row32`, `Row24`, `Row16`, `HasAnyAlpha` and `ApplyMask`,** under aurora-code rule 42a's Arctis.Imaging decode-loop exception. Without it the bench's warm-up still ran them in tier 0: 16-bit 555 measured 1.6-1.7 ms, three times slower than before the change.

Not done (user chose scope 2026-11-08): SIMD luma for R8/RG8 (scalar, ~0.76 ns/px) and a fast path for the non-opaque mask loop.

Measurements. Scratch console bench, not committed. Release, net10.0, Windows, Ryzen 9 9900X, default tiering. Inputs: Brick.png and Brick2.png resized to 256², one entry per ICO. The 32-bit files were written by Pillow 13 (`bitmap_format="bmp"`, BI_RGB 32, opaque alpha, no AND mask); the 24/555/565 files were hand-built with an all-clear AND mask; `24m` is the same with a random mask. Per pass both images decode via `ImageCodec.Decode` with the Ico hint into preallocated buffers; 40 warm-up passes, 6 timed, median ms for the set; 3 processes per side, range shown. Pillow 13 (CPython 3.14) decodes to RGBA only and was noisy: 5 rounds x 3 processes.

| Case | Before ms | After ms | Change | Pillow ms |
|---|---|---|---|---|
| 32-bit → RGBA8 | 0.332-0.340 | 0.009 | ~37x | 0.24-0.46 |
| 32-bit → BGRA8 | 0.329-0.336 | 0.009 | ~37x | - |
| 32-bit → R8 | 0.418-0.475 | 0.100-0.101 | ~4.5x | - |
| 32-bit → RG8 | 0.462-0.489 | 0.130-0.131 | ~3.6x | - |
| 24-bit → RGBA8 | 0.310-0.343 | 0.014 | ~23x | 0.37-0.58 |
| 24-bit → BGRA8 | 0.308-0.309 | 0.014 | ~22x | - |
| 24-bit → R8 | 0.321-0.323 | 0.101-0.102 | ~3.2x | - |
| 24-bit → RG8 | 0.430-0.431 | 0.128-0.129 | ~3.4x | - |
| 24-bit random mask → RGBA8 | 0.602-0.623 | 0.089-0.119 | ~6x | - |
| 24-bit random mask → BGRA8 | 0.605-0.647 | 0.089-0.093 | ~6.8x | - |
| 24-bit random mask → R8 | 0.322-0.355 | 0.102-0.103 | ~3.2x | - |
| 24-bit random mask → RG8 | 0.728-0.752 | 0.203-0.206 | ~3.6x | - |
| 16-bit 555 → RGBA8 | 0.517-0.529 | 0.014 | ~37x | 0.45-0.59 |
| 16-bit 555 → BGRA8 | 0.441-0.462 | 0.013-0.014 | ~33x | - |
| 16-bit 555 → R8 | 0.431-0.443 | 0.204-0.209 | ~2.1x | - |
| 16-bit 555 → RG8 | 0.543-0.583 | 0.196-0.197 | ~2.9x | - |
| 16-bit 565 → RGBA8 | 0.417-0.445 | 0.014 | ~31x | 0.44-0.49 |
| 16-bit 565 → BGRA8 | 0.423-0.469 | 0.014-0.025 | ~25x | - |
| 16-bit 565 → R8 | 0.430-0.518 | 0.203-0.204 | ~2.3x | - |
| 16-bit 565 → RG8 | 0.548-0.586 | 0.196-0.215 | ~2.8x | - |

- 16-bit RGBA8/BGRA8 per SIMD tier (3 processes): 512 tier (default) 0.013-0.025; 256 (`DOTNET_EnableAVX512=0`) 0.019-0.021; 128 (`+DOTNET_EnableAVX2=0`) 0.031-0.033; scalar (`DOTNET_EnableHWIntrinsic=0`) 0.185-0.195.
- Allocation per set: 2120 B before, 24 B after (the 24 B was there before too and does not come from `BmpDecoder`). With tiering off the steady-state before numbers were lower: 32-bit RGBA8 0.30, 24-bit RGBA8 0.23, 16-bit 0.46.
- Test-verified: Arctis.Imaging builds clean for Release net8.0 and net10.0, 0 warnings; ArctisImaging.Tests 459/459 (net10.0). A scratch equality check found 396 file x layout SHA-256 hashes byte-identical to HEAD (ee5c6cf), decoded into padded-pitch buffers pre-filled with 0xA5, covering 32/24/555/565 and 32-bit zero-alpha icons, the same with random masks, plain BMP bottom-up and top-down, 32-bit zero-alpha, 32-bit V5 BGRA top-down, widths 1/3/17/63/257, the Pillow fixtures and all four layouts. It passed identically at every SIMD tier (default 512, `EnableAVX512=0`, `+EnableAVX2=0`, `EnableHWIntrinsic=0`).
- Not checked: net8.0 test run, ARM64, `_Build/test.sh`, real-world third-party icons. Not GUI-verified.
- Gaps: R8/RG8 output from 32/24/16-bit is scalar luma; the non-opaque AND mask loop (32-bit zero-alpha icons) is unchanged; 16-bit with an alpha field and any other bit-field layout were on the then-unoptimised `DecodeRows` path (palettes/RLE left it, see "BMP indexed rows and RLE"; bit-fields left it too, see "BMP bit-field rows").

## BMP indexed rows and RLE (2026-11-08)

**What changed.** All in `Arctis.Imaging`; no public API change, no data XML, no new usings, no new NuGet.
- `PixelRows.LookupRow`, `BitIndices`, `PackedIndices`, `LookupIndices` moved from `PcxDecoder` (see "PCX sub-byte path").
- `BmpDecoder.Decode` (private core): the `stackalloc uint[256]` palette is built in destination pixel form (RGBA8 r|g<<8|b<<16|FF<<24; BGRA8 swapped; R8 `PixelRows.Luma`; RG8 luma | 0xFF00), entries past the count opaque black, only when bitCount <= 8. Chain is `DecodeDirect || DecodeIndexed || DecodeRows`.
- New `DecodeIndexed(pixels, in Info, stride, palette, destination, pitch, layout)` (`AggressiveOptimization`), false for bitCount > 8. 1/4-bit: copy the source row into a rented `line` (rowBytes + 64), `BitIndices(planes: 1)` or `PackedIndices(4)` into a rented `idx` (width + 256), then `LookupIndices` with a 48-byte stackalloc table (palette entries 0-15) into the destination row. 8-bit: `Lookup` per row.
- `DecodeRows` lost its palette parameter and 1/4/8-bit case; it serves only bit-field forms `DecodeDirect` does not take (optimised 2026-11-08, see "BMP bit-field rows"; the dead `case 24` went with that rewrite).
- `DecodeRle` rewritten (`AggressiveOptimization`, same signature): writes into the destination, no RGBA frame, no `FromRgba` pass. Throws `ArgumentException` if the destination is shorter than `(height-1)*pitch + width*size`. A raster cursor `filled` marks the position up to which every pixel is written or cleared; the RLE cursor only moves forward, so a write past `filled` first zeroes the skipped range and the tail is zeroed at the end. Skipped pixels (delta, short end of line, early end) are zero bytes in every layout, as before.
- Fast path: with `x + 32 <= width` and a run of n <= 32 or a literal of 3..16 with 16 stream bytes left, a fixed block is written (`StoreRepeated` 32 px; `ShortLiteral` 16 px). Pixels written past the count lie past `filled` and are overwritten or zeroed later. Slow path: longer runs and row ends via `Reach` + `FillRun`; longer literals via `Reach` + `Lookup` (RLE4 nibbles unpacked into a 256-byte stackalloc).
- New private helpers: `Reach` (clears the gap, advances `filled`, returns the span), `ClearRange`, `FillRun` (`MemoryMarshal.Cast<byte, ulong|uint|ushort>(...).Fill`), `Lookup` (size 4 -> `PixelRows.LookupRow`, 2/1 scalar), `Pattern` (two pixels in a `ulong`), `StoreRepeated(ref byte, ulong, nuint)` (`Vector512/256/128.Create(pattern)` stores), `ShortLiteral` (16 fixed lookups).
- Tests: `BmpTests.IndexedRowsWriteEveryLayout(bitCount, entries)` (1/2, 4/16, 4/5, 8/256, 8/7; 245 wide so every tier and the scalar tail run; both row orders; all 4 layouts; padded pitch untouched; short palettes give opaque black) and `RleWritesEveryLayout(bitCount)` (4, 8; 245x4; runs past the width, odd literals, short end of line, a fast literal then an in-row delta, a delta across rows, early end; gaps zero; pitch untouched). Helpers `IndexedPalette`, `PutIndexed`, `AssertEveryLayout`.

**1/4-bit BMP rows reuse PCX's sub-byte kernels, not a BMP copy.**
One copy, no drift; BMP 1-bit is `BitIndices` with one plane. The source row is copied into a padded scratch row because the kernels read whole vectors past width and the last BMP row has no slack; the copy is width/8 or width/2 bytes.

**8-bit is included (user choice 2026-11-08, "Go, include 8-bit").**
RGBA8/BGRA8 go through `PixelRows.LookupRow` with the palette already in destination form; R8/RG8 through a scalar luma lookup from the same palette. Byte-identical to the old `FromRgba` path because BMP palette entries are opaque.

**RLE writes into the destination with lazy gap clearing (user choice 2026-11-08, option (a)).**
Rejected: (b) zeroing every destination row up front, simpler but writes the destination twice; and the old full-image RGBA frame, which rented `width*height*4` bytes plus a whole `FromRgba` pass.

**A fixed-width fast path handles short RLE ops (user choice 2026-11-08).**
Without it the RLE4 photo stream (~158K ops per 1024² image: runs average 5.2 px, literals 8.6; RLE8 photo runs 3.5, literals 39.6; blocky RLE4 runs 18.9; blocky RLE8 runs 9.8) cost ~14 ns per op, mostly mispredicted loop exits; a fixed 32-px run store or 16-px literal has no length-dependent exit. Runs go to 32 px because blocky RLE4 runs average 19 px and a 16-px limit sent them to the slow path (0.88-1.06 ms vs 0.68-0.70). Rejected and untested: decoding into an index row first and looking up whole segments (bigger rewrite).

**`StoreRepeated` broadcasts a scalar `ulong` (`Vector512.Create(ulong)`), not `Create(Vector256, Vector256)` (user choice 2026-11-08).**
In `DecodeRle` the JIT compiled `Create(half, half)` as a partial write to a 64-byte stack slot, reloaded per op and spilled/reloaded every store-loop iteration (`vinsertf32x8 zmm1, zmm1, ymm0` into `[rbp+0xF0]`); the scalar broadcast is `vpbroadcastq zmm0, r8` in a register. With `DOTNET_TieredCompilation=0`, RGBA8 ms for the 2-image set, 3 processes: RLE4 photo 2.03-2.13 old vs 2.02-2.18 new; RLE8 photo 1.05-1.25 vs 1.03-1.06; blocky RLE4 0.41-0.45 vs 0.39-0.43; blocky RLE8 0.56-0.59 vs 0.52-0.54, so the chain cost little. All three tiers (512/256/128) stay. A store-width microbench (128 B per op, 4M ops) showed 2x512 fastest even misaligned and overlapping: RLE-like step 0.64-0.65 ns/op for 2x512 vs 0.83-0.90 for 4x256 vs 1.50-1.53 for 8x128.

**The RLE helpers carry `AggressiveOptimization` / `AggressiveInlining` (user choice 2026-11-08), under aurora-code rule 42a's Arctis.Imaging decode-loop exception.**
Only `DecodeRle` was FullOpts; `ShortLiteral`, `Reach`, `FillRun`, `Lookup` and `ClearRange` started at tier 0 and tiered up in the background, so RLE4 photo RGBA8 measured 2.4 or 5.1 ms depending on run order (alone 2.36-2.62, inside the full suite 3.6-5.2) and produced a false 4 KB address pattern (destination-offset sweep 1.15 vs 2.55 ms per image, flat at 1.1 with `TieredCompilation=0`). After the attributes: 2.51-2.58 ms across processes with default tiering. Same trap as "BMP direct rows". See [[tier0-helpers-skew-decode-bench]].

**Measurement.** Scratch console bench, not committed. Release, net10.0, Windows, Ryzen 9 9900X, default tiering. Inputs: `Brick.png` + `Brick2.png` (1024² RGBA) quantised by Pillow 13 (`quantize(n, dither=NONE)`) to 2/16/256 colours, BMP files hand-built in Python (BITMAPINFOHEADER, bottom-up); RLE streams from a greedy Python encoder (runs of 3+, literals otherwise); "blocky" = 128² box downscale then nearest upscale to 1024². Per case the 2-image set decodes via `ImageCodec.Decode` with the Bmp hint into preallocated buffers; 40 warm-up passes, 6 timed, median ms for the set; 5 processes per side alternating with a snapshot build of the previous decoder. Allocation 0 B per decode before and after. Pillow 13 reference (decodes into its native mode, which flatters it): raw 1-bit 0.91 ms, 4-bit 0.83, 8-bit 0.67; its RLE decoder is pure Python (RLE4 photo 217 ms, RLE8 photo 23, blocky RLE4 90, blocky RLE8 27), so not a useful RLE reference.

| Case | Layout | Before ms | After ms | Change |
|---|---|---|---|---|
| 1-bit | RGBA8 | 2.22-2.40 | 0.124-0.129 | 17-19x |
| 1-bit | R8 | 3.68-4.10 | 0.050-0.051 | 72-82x |
| 4-bit | RGBA8 | 2.02-2.16 | 0.125-0.126 | 16-17x |
| 4-bit | R8 | 3.48-3.66 | 0.052 | 67-70x |
| 8-bit | RGBA8 | 2.02-2.10 | 0.425-0.445 | 4.5-4.9x |
| 8-bit | R8 | 3.48-3.73 | 0.411-0.450 | 7.7-9.1x |
| RLE4 photo | RGBA8 | 4.04-4.21 | 2.53-2.62 | 1.5-1.7x |
| RLE4 photo | R8 | 5.37-5.93 | 2.56-2.60 | 2.1-2.3x |
| RLE8 photo | RGBA8 | 2.29-2.35 | 1.09-1.15 | 2.0-2.2x |
| RLE8 photo | R8 | 3.69-3.73 | 1.11-1.14 | 3.2-3.4x |
| RLE4 blocky | RGBA8 | 1.97-2.08 | 0.45-0.77 | 2.6-4.6x |
| RLE4 blocky | R8 | 3.38-3.41 | 0.42-0.45 | 7.6-8.0x |
| RLE8 blocky | RGBA8 | 1.99-2.04 | 0.62-0.69 | 2.9-3.3x |
| RLE8 blocky | R8 | 3.27-3.41 | 0.58-0.59 | 5.6-5.9x |

Gap variants (index-0 runs as deltas / early end of line), measured like blocky: RLE4 RGBA8 1.85-1.93 -> 0.45-0.47 ms, RLE8 RGBA8 1.78-2.18 -> 0.62-0.69 ms.

**Verified**: test-verified. `ArctisImaging.Tests` 494/494, Release, net10.0, under default, `DOTNET_EnableAVX512=0`, `PreferredVectorBitWidth=256`, `EnableAVX2=0` and `EnableHWIntrinsic=0`. Library builds clean on net8.0 and net10.0, no warnings. Scratch SHA-256 of every output identical to a snapshot of the previous decoder under the same five settings: 50 BMP files x 4 layouts = 200 cases with a 3-byte padded pitch prefilled 0xAB (both Brick images at 1024² as 1/4/8-bit and RLE4/RLE8, blocky RLE, gap RLE, 1021x1019 odd crops incl. top-down 4/8-bit, 245x7 crops). **NOT GUI-verified.** Not checked: ARM64, net8.0 runtime (the test project targets net10.0 only), ICO-embedded 1/4/8-bit entries (same code path), RG8/BGRA8 timing in the table.

**Known gaps.**
- Blocky RLE4 RGBA8 is ~2x slower with tiering on than with `TieredCompilation=0`: 0.75-0.80 ms in most processes vs 0.39-0.40. Not addresses (offset sweep flat) and not tier 0 (JIT summary shows every RLE-path method FullOpts). With tiering on, `DecodeRle`'s FullOpts body is larger (3609 vs 3010 bytes) and spills the `four` flag, the palette span and computed indices to the stack on every op in the run path; with tiering off they stay in registers. Cause unknown; open follow-up.
- (Resolved 2026-11-08: `DecodeRows` bit-field masks are optimised and the dead `case 24` is removed, see below.)

## BMP bit-field rows (2026-11-08)

**What changed.** All in `Arctis.Imaging` (`BmpDecoder`, `PixelRows`); no public API change, no data XML, no new usings, no NuGet. Last BMP path that was documented as unoptimised; outputs byte-identical to the previous decoder.
- `BmpDecoder.Decode` (private core) computes `anyAlpha` itself, once, up front: `bitCount == 32 && aMask != 0 && layout != R8 && HasAnyAlpha(pixels, aMask)`. A 32-bit bitmap with no alpha sets `info.aMask = 0`, so every path writes opaque pixels. Then `if (!DecodeDirect(...) && !DecodeIndexed(...)) DecodeRows(...)`. The icon AND mask applies when `icon && !anyAlpha` (a zero-alpha 32-bit icon now calls `ApplyMask(opaque: true)`, same bytes). The trailing `PixelRows.MakeOpaque` loop is gone from BMP (`MakeOpaque` stays; TGA uses it).
- `DecodeDirect` returns only "handled"; its 32-bit case also accepts BGRA masks with `aMask == 0` (BGRX: BI_RGB zero alpha, or BI_BITFIELDS XRGB); no longer calls `HasAnyAlpha`.
- `Row32(src, dst, width, layout, bool opaque)`: RGBA8/BGRA8 run new `ForceOpaque(Span<byte> row, int width)` after the swizzle/copy (OR 0xFF000000, 512/256/128 tiers + scalar tail); RG8 writes 255.
- `HasAnyAlpha(ReadOnlySpan<byte> pixels, uint aMask)`: any 32-bit alpha mask, SIMD OR-reduce of 4 vectors per step (512/256/128) with early exit, scalar tail.
- `DecodeRows` rewritten: `void`, `AggressiveOptimization`, per row calls new `RowFields(src, dst, width, bool wide, in Field first, in Field g, in Field third, in Field a, int size)`. No RGBA scratch row, no `FromRgba`, no `ArrayPool`. `RowFields` handles 16 or 32-bit pixels as 32-bit lanes (16-bit via `Vector*.Widen`), 32/16/8 px per iteration at 512/256/128, scalar tail through `Field.Extract`; `first`/`third` are the channels landing in bytes 0 and 2 (b/r swapped for BGRA8; red/blue for luma).
- New `Emit` (Vector512/256/128 overloads, `AggressiveInlining`): RGBA8/BGRA8 `c0 | c1<<8 | c2<<16 | a<<24`; R8 luma narrowed twice; RG8 `luma | a<<8` narrowed. New `Luma` (three vector overloads): `(r*LumaR + g*LumaG + b*LumaB + 32768) >> 16`.
- `Field` (private readonly struct): new private fields `low, up, down, s1, s2, s3` computed in the ctor; three vector `Extract` overloads do the scalar `Extract` as lane shifts: `((v >> shift) & low) << up >> down`, then `w |= w >> s1; w |= w >> s2; w |= w >> s3` (unused steps shift 0), `& 0xFF`.
- `PixelRows.LumaR`, `LumaG`, `LumaB`: `private const` to `internal const`.
- Test `BmpTests.BitFieldRowsWriteEveryLayout(kind)` Theory, 10 cases: 1555, 4444, 444x (16-bit); RGBA order, ABGR, 10-10-10-2, scattered non-contiguous masks, XRGB, BI_RGB zero alpha, RGBA order with zero alpha (32-bit); 245 wide, both row orders, all 4 layouts, padded pitch untouched; expected from a scalar `Widen(uint v, uint mask)` reference in the test.

**One SIMD kernel for every mask, byte-identical by construction.**
The vector code runs the same uint ops as `Field.Extract` (shift-left, up to three shift-OR replication steps, mask to a byte), so non-contiguous masks need no fallback. Rejected: a multiply-shift replication formula (`(c * R) >> P`), exact only for contiguous masks.

**Zero-alpha 32-bit: up-front SIMD scan, then decode opaque (user choice 2026-11-08, option (a)).**
Reads the pixels twice but writes the destination once; `Decode` owns `anyAlpha`, so `DecodeDirect` / `DecodeIndexed` / `DecodeRows` return only "handled". Rejected (b): keep the `MakeOpaque` post-pass and vectorise it, simpler but writes the destination twice. The scan is skipped for R8 (alpha is not output and the icon mask returns for R8). BGRX now takes `DecodeDirect` with `ForceOpaque`.

**SIMD luma reads `PixelRows`' constants, made `internal` (user choice 2026-11-08).**
Rejected: vector `Luma` overloads inside `PixelRows` (~3x the code).

**All new helpers carry `AggressiveOptimization` / `AggressiveInlining`,** under aurora-code 42a's Arctis.Imaging decode-loop exception, per [[tier0-helpers-skew-decode-bench]].

**Measurement.** Scratch console bench, not committed. Release, net10.0, Windows, Ryzen 9 9900X, default tiering. Inputs: `Brick.png` + `Brick2.png` (1024² RGBA) with a linear-gradient alpha, hand-built in Python as BMPs: 32bgra = BI_RGB 32 with alpha; 32zero = BI_RGB 32, X byte 0; 32xrgb = BI_BITFIELDS no alpha mask; 32rgba = V4 masks R low byte + alpha; 32abgr = ALPHABITFIELDS (compression 6); 1010102 = V4 10-10-10-2; 1555, 4444 = V4 16-bit with alpha; 444x = BI_BITFIELDS 16-bit no alpha. The 2-image set decodes via `ImageCodec.Decode` with the Bmp hint into preallocated buffers; 40 warm-up, 6 timed, median ms for the set; 5 processes per side alternating with a snapshot build of the previous decoder. Allocation 0 B per decode after. Pillow 13 (CPython 3.14, native mode, same set): 32bgra 2.91, 32zero 2.84, 32xrgb 2.68, 32rgba 2.17, 1555 5.47 ms; it cannot read 4444, 444x, ABGR or 10-10-10-2.

| Case | Layout | Before ms | After ms | Change |
|---|---|---|---|---|
| 32bgra (direct, unchanged path; scan moved) | RGBA8 | 0.139-0.159 | 0.139-0.169 | same |
| 32bgra | R8 | 1.55-1.58 | 1.55-1.58 | same |
| 32zero | RGBA8 | 5.59-5.70 | 0.238-0.276 | 20-24x |
| 32zero | R8 | 1.56-1.57 | 1.55-1.65 | same |
| 32xrgb | RGBA8 | 4.37-4.49 | 0.164-0.230 | 19-27x |
| 32xrgb | R8 | 5.56-5.60 | 1.55-1.71 | 3.2-3.6x |
| 32rgba | RGBA8 | 4.82-5.24 | 0.618-0.652 | 7.4-8.5x |
| 32rgba | R8 | 6.13-6.19 | 0.494-0.528 | 11.6-12.5x |
| 32abgr | RGBA8 | 4.82-5.03 | 0.626-0.660 | 7.3-8.0x |
| 32abgr | R8 | 6.15-6.41 | 0.497-0.537 | 11.5-12.9x |
| 1010102 | RGBA8 | 5.77-5.82 | 0.615-0.678 | 8.5-9.5x |
| 1010102 | R8 | 7.07-7.62 | 0.492-0.521 | 13.6-15.5x |
| 1555 | RGBA8 | 7.61-9.28 | 0.606-0.712 | 10.7-15.3x |
| 1555 | R8 | 8.90-10.52 | 0.516-0.552 | 16.1-20.4x |
| 4444 | RGBA8 | 6.95-8.44 | 0.607-0.664 | 10.5-13.9x |
| 4444 | R8 | 8.22-9.74 | 0.512-0.815 | 10.1-19.0x |
| 444x | RGBA8 | 5.90-7.91 | 0.457-0.493 | 12.0-17.3x |
| 444x | R8 | 7.20-8.96 | 0.512-0.528 | 13.6-17.5x |

**Verified**: test-verified. `ArctisImaging.Tests` 507/507, Release, net10.0, under default, `DOTNET_EnableAVX512=0`, `DOTNET_PreferredVectorBitWidth=256`, `DOTNET_EnableAVX2=0`, `DOTNET_EnableHWIntrinsic=0` (507 includes 3 JPEG tests another session added in parallel). Library builds clean net8.0 + net10.0. Scratch SHA-256 of every output identical to a snapshot of the previous decoder under the same five settings: 40 bit-field files (odd 1021x1019 and small 245x7 crops of both Brick images in all 8 bit-field forms, plus top-down XRGB and 1555) x 4 layouts = 160 cases, and the earlier 200 indexed/RLE cases. **NOT GUI-verified.** Not checked: ARM64, net8.0 runtime, a 32-bit zero-alpha ICO entry beyond `IcoTests.ThirtyTwoBitEntryWithNoAlphaUsesTheMask`, RG8/BGRA8 timing.

**Known gaps.**
- 32-bit BGRA/BGRX into R8 and RG8 stays on `Row32`'s scalar luma loop (1.55 ms for the set), slower than the field kernel's R8 (~0.5 ms); routing BGRX to `DecodeDirect` made 32xrgb R8 use that loop (still 3.2-3.6x faster than before). Same for `Row24`/`Row16`. Open follow-up: vectorise them or route through the kernel.
- The `DecodeRle` tiering-on spill gap above is unchanged.

## Verification
- ArctisImaging.Tests 348/348 (shared with [[lossless-jpeg]]); every fixture decodes byte-identical to its reference.
- Scratch cross-check (not committed): 60 Pillow-written QOI/PCX/PPM/PGM files byte-identical to Pillow's decodes.
- Solution builds clean, no new warnings. `_Build/test.sh`: 509 passed, 8 failed, 0 new (Boot sampler error and 7 LatinModern Tex.* goldens, both in the baseline). No `[Vulkan]` lines.

## Known gaps
- Not GUI-verified: paste or drop of any new format into a Thorium note.
- Plain PBM/PGM/PPM, PAM, CUR, 4-bit/1-bit icon DIBs, PCX EGA planar, XPM and farbfeld were checked only against hand-built files in the tests, not an external tool.
- Encoders: see [[simple-raster-encoders]].
- XPM named colours unsupported.
- General PNM path (plain, 16-bit, maxval != 255, PBM) still unoptimised, ratio not re-measured. RGB PNM -> R8/RG8 stays on it.
- PNG grey-8 -> R8 still goes Expand to RGBA then `FromRgba` Luma per pixel (the pattern that was slow in PNM); not touched, not measured.
- Direct path: net8.0, ARM64 and non-AVX-512 tiers at runtime not checked; PAM depth 1/3 has no dedicated test.
- No EXIF orientation. PFM (float PNM) is left to the HDR roadmap item. Only the first image in a multi-image PNM is decoded.

## Measured against native references (2026-11-08)

- Method: scratch console bench, not committed (no perf tests, see [[other-raster-formats]]). Release, net10.0, Windows, AMD Ryzen 9 9900X, default tiering. Entry: `ImageCodec.Decode` with a format hint (`ImageFormat.Pcx` for PCX, `Unknown` otherwise) into a preallocated RGBA8 buffer.
- Inputs: `Brick.png` + `Brick2.png` (two 1024² opaque photos). Pillow 13 (PCX 1-bit); hand-built in Python: BMP palette 8/4/1-bit and RLE8/RLE4 from a non-dithered quantise to 256/16/2 colours, PCX 2-bit and 4-bit single-plane and 4-plane 1-bit EGA. 2-image set per pass; 40 warm-up passes, 6 timed, median ms for the set; 3 Arctis processes alternated with 3 reference processes; ratio paired per round (below 1 = Arctis faster). Arctis allocated 24 B per set.
- References are native C called from CPython 3.14: cross-runtime, indicative only. Caveats favouring the reference: imagecodecs mostly writes 3-channel RGB (PCX 1-bit writes 1-byte grey) against Arctis's 4-byte RGBA8.
- Pillow decodes palette and 1-bit files to indices only (mode P / 1), so it is not like-for-like and is excluded from the ratios; the reference is imagecodecs, which expands to RGB. Pillow figures: 8-bit pal 0.58-0.65, 4-bit 0.88-0.94, 1-bit 0.91-0.94, PCX 1-bit 0.99-1.03, EGA 5.54-5.66; its RLE BMP decode is very slow (RLE8 87-112, RLE4 220-226).
- The RLE BMPs are quantised photos: almost all literal runs and larger than the raw files, the RLE worst case. Real run-heavy content was not measured.
- Correctness: byte-identical to Pillow's decode converted to RGBA, except PCX 2-bit and 4-bit single-plane, which Pillow cannot read ("unknown PCX mode") and which are byte-identical to imagecodecs pcx.

| Case | Arctis ms | Reference ms | Ratio |
|---|---|---|---|
| BMP 8-bit palette (before the indexed path) | 2.29-2.54 | imagecodecs bmp 2.55-3.18 | 0.80-0.90x (after 2026-11-08: 0.43-0.45 ms, see "BMP indexed rows and RLE") |
| BMP 4-bit palette (before) | 2.33-2.48 | imagecodecs bmp 2.52-3.11 | 0.80-0.95x (after 2026-11-08: 0.125-0.126 ms) |
| BMP 1-bit palette (before) | 2.09-2.16 | imagecodecs bmp 3.14-3.24 | 0.66-0.67x (after 2026-11-08: 0.124-0.129 ms) |
| BMP RLE8 (before the direct RLE path) | 4.16-4.45 | imagecodecs bmp 5.08-5.35 | 0.81-0.84x (after 2026-11-08: 1.09-1.15 ms) |
| BMP RLE4 (before) | 4.71-5.68 | imagecodecs bmp 5.51-5.60 | 0.86-1.01x (after 2026-11-08: 2.53-2.62 ms) |
| PCX 1-bit (before the sub-byte path) | 2.75-3.02 | imagecodecs pcx 1.00-1.01 (1-byte grey output) | 2.74-3.00x (after 2026-11-08: 0.21 ms, see "PCX sub-byte path") |
| PCX 2-bit, 1 plane (before) | 2.90-3.50 | imagecodecs pcx 3.59-3.73 | 0.81-0.94x (after 2026-11-08: 0.40-0.43 ms) |
| PCX 4-bit, 1 plane (before) | 3.37-3.47 | imagecodecs pcx 4.85-4.89 | 0.69-0.71x (after 2026-11-08: 0.95-1.01 ms) |
| PCX 4-plane 1-bit (EGA) (before) | 6.61-6.90 | imagecodecs pcx 8.16-8.36 | 0.80-0.84x (after 2026-11-08: 1.21-1.35 ms) |

- The BMP and PCX 1/2/4-bit and EGA rows above predate their fast paths and are kept as the "before" figures; the "after" figures come from each section's own bench (BMP inputs built the same way), not a re-run against imagecodecs.
- The "Slow spot: PCX 1-bit" recorded here is resolved by the sub-byte path (0.21 ms against 2.75-3.02).
- Not checked: ARM64, net8.0, lower SIMD tiers, real-alpha images, non-photo (run-heavy) RLE content.

Related: [[other-raster-formats]], [[jpeg-gif-decoders]], [[lossless-jpeg]], [[simple-raster-encoders]]
