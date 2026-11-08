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
- Binary PNM (P5, P6, P7) at maxval 255 takes `PnmDecoder.DecodeDirect` (see "PNM direct path" below). `XbmDecoder` is optimised (see "XBM fast path" below). Every other decoder is scalar, a first pass: **unoptimised**; speed measured 2026-11-07 (table below). No data XML touched.

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
| PCX 24-bit | 12.0-14.2 | Pillow 10.9-11.3 | 1.1-1.3x |
| PCX 8-bit palette | 5.8-6.0 | Pillow 4.0-5.1 | 1.2-1.5x |
| XBM (before the fast path) | 4.58-4.61 RGBA8, 5.96-5.98 R8 | Pillow 1.80-1.99 | 2.4-2.6x (first measure 4.7-4.9 ms, 2.3-2.6x) |
| XBM (fast path, three SIMD tiers, 2026-11-07) | default 512 tier, 5 processes per tier: 1.17-1.29 RGBA8, 1.10-1.16 R8 (256 tier 1.17-1.25 / 1.11-1.12; 128 tier 1.21-1.25 / 1.14-1.21) | Pillow 1.79-1.81 | 0.65-0.72x RGBA8, 0.61-0.65x R8, faster |
| XPM (64 colours, 1 char/pixel) | 3.0 | Pillow 250-256 | 0.012x, ~80x faster |
| ICO, PNG entry 256² | 0.71-0.79 | Pillow 2.19-2.22 | 0.32-0.36x, faster |
| ICO, BMP entry 256² | 0.34 | Pillow 0.27-0.28 | 1.2-1.3x |

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

Related: [[other-raster-formats]], [[jpeg-gif-decoders]], [[lossless-jpeg]], [[simple-raster-encoders]]
