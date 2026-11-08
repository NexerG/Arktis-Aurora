Status: Current
tags:
  - Engine
  - Systems

`Arctis.Imaging` is the engine's own image library: PNG decode and encode, zlib inflate and deflate, KTX 2.0 textures, and TGA, baseline TIFF and WebP decode. Nothing at runtime uses `System.IO.Compression`; the test projects keep `ZLibStream` only as an independent reference.

## Deflater

`Arctis.Imaging.Deflater` is a port of the match search of zlib 1.3.1 `deflate_slow`, with the tuning numbers chosen by the caller in a `DeflateSettings`. It writes one zlib stream: header, blocks, Adler-32.

`DeflateSettings` holds zlib's four numbers instead of named levels. `MaxChain` is how many earlier positions are tried per byte, `NiceLength` stops the search at a match that long, `MaxLazy` skips the one-byte-ahead search when the current match is already that long, and `GoodLength` cuts the chain to a quarter when the previous match is that long. A zero or negative field takes its default (8, 16, 128, 128, which is zlib level 6).

The output is close to zlib but not byte-exact: Huffman lengths come from Moffat-Katajainen minimum redundancy with a length limit, not from zlib's `trees.c`. The hash is a multiplicative one over 3 bytes rather than zlib's shift-and-xor, so chains hold fewer collisions. On Thorium's 12 atlases it is 0.02 % smaller than its zlib-hash version and, on Windows, about 1.55x slower than .NET's zlib-ng at level 6, whose output is 1.8 % larger.

```
Deflate(input, output, settings)
	settings.Check()
	write header 0x78 0x9C
	for each block of up to 16383 symbols
		for each position in the block
			find the longest match through the hash chain
			if the current match is shorter than MaxLazy and the next byte starts a longer one
				emit a literal and move one byte
			else
				emit the match
		build the dynamic trees for the block
		write whichever is smallest, dynamic, fixed or stored
	write Adler-32
```

`PngEncoder.Encode` filters every row Sub or Up into one pooled buffer and hands it to `Deflater`. `PdfDocument.Deflate` uses it for PDF streams.

## KTX 2.0

`ImageCodec` reads and writes KTX 2.0 files: 2D, one layer, one face, any mip count, R8, R8G8, R8G8B8A8 or B8G8R8A8, UNORM or SRGB, uncompressed or ZLIB-supercompressed. Anything else, such as BasisLZ, Zstd, block-compressed formats, 3D, arrays and cubemaps, throws `NotSupportedException`; a malformed file throws `ImageFormatException`.

`TryReadKtx2` reports the layout, colour space, size and level count. `DecodeKtx2Level` decodes one level into the layout the caller asks for. `EncodeKtx2` takes the finished mip chain, it does not generate mips. `ImageCodec.Decode` and `ImageFiles` load a `.ktx2` like any other image, taking level 0. A mipmapped texture loaded from a multi-level `.ktx2` instead uploads its stored levels, one Vulkan mip level per stored level, with no CPU mip generation.

R8 and R8G8 decode as grey and grey+alpha, the same meaning they have for PNG, not Vulkan's red and red-green.

See `ClaudeMemory/Decisions/png-deflate.md` and `ClaudeMemory/Decisions/ktx2-container.md` for the reasoning and measurements.

## TGA, TIFF and WebP

These three decoders exist so that dropped or copied `.tga`, `.tif`/`.tiff` and `.webp` files need no third-party library. ImageSharp has been removed from the engine and nothing falls back to it: a file Arctis refuses, such as an unsupported TIFF variant, throws `NotSupportedException` from `ImageFiles.Load`. The new decode code for TIFF extensions, CMYK JPEG and BMP RLE is unprofiled; no perf tests were written. JPEG and GIF are decoded by Arctis as well, see the section below.

`TryIdentify`, `Decode` and `Image.Load` take an optional trailing `ImageFormat hint`. TIFF and WebP are recognised by their magic bytes. TGA has no signature, so it is tried only when the hint is `ImageFormat.Tga`; `ImageFiles.Load(path, layout)` passes that hint when the path ends in `.tga`. A TGA reached as bytes without a path is therefore not recognised by Arctis.

### TGA

Image types 1, 2 and 3 and their RLE forms 9, 10 and 11, at 8, 15, 16, 24 and 32 bits. Colour-mapped images take 8 or 16-bit indices and 15, 16, 24 or 32-bit map entries, honouring the first-entry index. Grey is 8-bit, grey+alpha 16-bit. Both origins work, and an RLE packet may run across rows. Alpha is used only when the descriptor's alpha bits are above zero, and an image whose alpha is zero everywhere decodes opaque, the same rule as the BMP decoder. An out-of-range palette index gives black.

### TIFF

Only the first IFD is read, in either byte order, as strips or tiles. Samples are 8-bit or 16-bit (a 16-bit sample keeps only its high byte), or 1, 2 or 4-bit for grey and palette, chunky or planar, with WhiteIsZero, BlackIsZero, RGB, Palette or CMYK (InkSet 1) photometrics. Compression may be none, LZW (the TIFF variant, which widens one code early), Adobe Deflate, old Deflate, PackBits or JPEG (7), with the horizontal predictor at 8 and 16 bits; fill order 2 is honoured. Associated alpha is un-premultiplied to straight alpha on decode. JPEG-in-TIFF stores each strip or tile as an abbreviated JPEG stream with shared tables; the TIFF's photometric value decides whether it is YCbCr or RGB, and it is supported for 8-bit chunky grey, RGB and YCbCr only. CCITT, floating point and other sample formats, CieLab, predictor 3, planar, old-style (compression 6), palette and CMYK JPEG, 16-bit palettes, mixed sample depths and YCbCr without JPEG throw `NotSupportedException`. BigTIFF is not recognised.

### WebP

Simple and extended (VP8X) RIFF containers, with full lossless VP8L: all predictor modes, the cross-colour, subtract-green and colour-indexing transforms, the colour cache, meta prefix codes and LZ77 with the short-distance table. Lossy VP8 is decoded as well, byte-identical to libwebp 1.6.0: the boolean decoder, intra prediction, DCT and Walsh-Hadamard inverse, the simple and normal loop filters and libwebp's fancy 4:2:0 upsampling. An ALPH alpha plane is decoded too, raw or VP8L-coded, with none, horizontal, vertical or gradient filtering. An animated WebP decodes its first frame only, composited onto a transparent canvas the size of the file; there is no frames API. The lossy decoder is unoptimised: it is scalar, with no SIMD and no profiling-driven work, and takes about 1.2 to 1.3 times ImageSharp's time. A truncated stream throws `ImageFormatException`.

See `ClaudeMemory/Decisions/other-raster-formats.md` for the reasoning and open gaps.

## JPEG and GIF

`JpegDecoder` reads baseline and extended (SOF0/SOF1) and progressive (SOF2) Huffman JPEG, 8-bit, one, three or four components (grey, RGB/YCbCr, CMYK/YCCK), any integral sampling, with restart intervals. It decodes the way libjpeg-turbo 3.2 does, with the islow integer IDCT, libjpeg's fancy upsampling for 4:2:2, 4:2:0 and 4:4:0 and its fixed-point YCbCr conversion, and every file tested comes out byte-identical (no 4:4:0 file was available to test). Adobe APP14 transform 0 means RGB, otherwise YCbCr. EXIF orientation is not applied. A truncated file decodes what it holds. The IDCT and colour conversion have an AVX2 path, a Vector128 path (SSE2 or ARM64 AdvSimd) and a scalar path; upsampling has an AVX2 path as well as the Vector128 and scalar ones. On real files and in-range blocks all of them give the same bytes; on extreme synthetic blocks (coefficients of ±2047 or more times large quantisers) the AVX2 IDCT differs from the scalar one. Decode time is 0.36 to 0.80 of ImageSharp's depending on the file, with small progressive files (bricks, 0.78 to 0.80) at the slow end and q90 at 0.69 to 0.70. Large, dense sequential scans refill the bit buffer without a branch, and other scans only when it runs low.

Four-component files are treated as inverted CMYK only when an Adobe APP14 marker is present, which is what Chromium and Firefox do and nearly every real CMYK JPEG satisfies; Pillow always inverts. YCCK (Adobe transform 2) is converted to CMYK first, as libjpeg does. The result is RGB with opaque alpha. The CMYK conversion is scalar and unprofiled. Arithmetic coding (SOF9/SOF10), hierarchical, 12-bit DCT and two-component JPEG throw `NotSupportedException`; there is no fallback, so such files fail to load. Lossless JPEG is decoded, see the next section.

`GifDecoder` decodes the first frame only, placed at its offset on a transparent canvas of the logical-screen size. Global and local palettes, interlacing and graphic-control transparency work; later frames of an animated GIF are ignored.

See `ClaudeMemory/Decisions/jpeg-gif-decoders.md` for the reasoning, measurements and open gaps.

## Lossless JPEG and JPEG-LS

Lossless JPEG (T.81 process 14, SOF3) is decoded by the same `JpegDecoder`, split into a partial file `JpegLossless.cs` so it can share the bit reader and Huffman tables. It handles precision 2 to 16 bits, predictors 1 to 7, point transform, restart intervals, and interleaved and non-interleaved scans of one, three or four components at equal sampling. Three components follow the baseline YCbCr rule, because libjpeg-turbo writes an Adobe marker with transform 0 and R/G/B ids and so produces RGB copies. Above 8 bits the high byte is kept and below 8 bits samples are scaled to 0-255, since the pixel layouts are 8-bit only. Arithmetic (SOF11), hierarchical and subsampled lossless files throw `NotSupportedException`.

`JpegLsDecoder` decodes JPEG-LS (T.87) the way CharLS does: median edge predictor, 365 regular contexts, limited-length Golomb coding, run mode with run-interruption contexts, near-lossless (NEAR above 0), interleave modes 0, 1 and 2, and LSE type 1 presets. `JpegDecoder.TryReadHeader` recognises the SOF55 marker and reports `ImageFormat.JpegLs`. Mapping tables, the HP colour transform, subsampling, a nonzero point transform and restart intervals throw `NotSupportedException`. Both decoders are scalar. The lossless JPEG decoder is unoptimised and measures 1.1 to 2.2 times libjpeg-turbo's time. The JPEG-LS decoder was optimised on 2026-11-07 (a quantisation lookup table, context state as an array of structs, a faster bit refill, branchless prediction) and again on 2026-11-08 (a median predictor that really compiles without branches, a masked wrap for lossless reconstruction, a masked bias update above 8 bits), then a fourth time on the same day (a CharLS-style lookup table for Golomb codes of at most 8 bits, used only on 8-bit RGB through compile-time width traits so the other paths are unchanged), and a fifth time (a table-hit path with no calls, reuse of the previous pixel's gradient quantisation, and 8-bit RGB written straight into the RGBA destination without sample planes). It now takes 0.96 times CharLS's time on 8-bit RGB (slightly faster), down from 1.9 to 5.3 originally, is about level on 8-bit RGB with NEAR=2, and is faster than CharLS on 8-bit grey and on every case above 8 bits, with byte-identical output. The CharLS comparison is native code called from Python, so it is indicative only.

See `ClaudeMemory/Decisions/lossless-jpeg.md` for the reasoning and open gaps.

## ICO and simple rasters

Seven more decoders cover ICO/CUR, Netpbm (PBM, PGM and PPM plain and raw, and PAM), QOI, farbfeld, PCX, XBM and XPM. QOI, farbfeld, XPM, ICO and PNM are recognised by their magic bytes; PCX and XBM have no usable signature and are accepted only when the file extension hints at them, as TGA is. Above 8 bits the high byte is kept and below 8 bits samples are scaled to 0-255. The decoders are scalar and unoptimised, except binary PNM at maxval 255: P5, P6 and PAM rows go straight from the raster into the destination layout with the shared `PixelRows.FromGrey` and `PixelRows.FromRgb` expands (the same ones PNG uses), which measures 0.14 to 0.45 times ImageSharp's time for PPM and PGM into RGBA8, BGRA8 and R8. Plain text, PBM, 16-bit and other maxvals stay on the general per-sample path. XBM is the second exception: a lookup-table parse of the array values, then a 512/256/128-bit SIMD expand straight into the destination layout, now 0.61 to 0.72 times Pillow's time (faster). PCX stays at 1.1 to 1.5 times Pillow's, while QOI, XPM and PNG-entry ICO are already faster than their references; the encoders for BMP, TGA, ICO, PNM and QOI are described below.

An ICO or CUR file decodes to one image, chosen by this rule, with the CUR hotspot not exposed:

```
for each directory entry
	read the entry's own header (PNG IHDR, or the DIB header with its height halved)
	score = area, then bits per pixel
decode the highest-scoring entry
if the entry is a DIB
	if it is 32-bit and any alpha is nonzero
		keep its alpha
	else
		apply the AND mask, a set bit means transparent, the RGB is kept
```

XBM treats a set bit as black, the X11 convention. XPM reads XPM3 only and throws `NotSupportedException` for a named colour such as `red`, because the X11 colour table is not embedded; `#` hex colours and `None` work. Only the first image of a multi-image Netpbm file is decoded.

See `ClaudeMemory/Decisions/ico-simple-rasters.md` for the reasoning and open gaps.

## BMP, TGA, ICO, PNM and QOI encoders

`ImageCodec.EncodeBmp`, `EncodeDib`, `EncodeTga`, `EncodePnm` and `EncodeQoi` take `(pixels, width, height, rowPitch, layout, output, settings)`, and `Image.SaveBmp`, `SaveTga`, `SavePnm` and `SaveQoi` take a stream or a path plus the same settings. `EncodeIco` and `EncodeCur` take a span of `IconEntry` values and the output stream; there is no `Image` method for ICO, CUR or DIB. Each format has a settings struct that exposes every variant the format allows, so any combination can be typed in: `BmpSettings(format, header, compression, topDown)`, `TgaSettings(format, rle, origin, omitFooter)`, `PnmSettings(kind, plain, maxVal, tupleType)` and `QoiSettings(channels, colorspace)`. A zero value in an enum is `Auto`, chosen from the pixel layout, and `default` settings give the defaults below. A contradictory combination, such as RLE with a top-down BMP or a plain PAM, throws `ArgumentException`. Every variant written is one the matching decoder reads.

The defaults are as follows. BMP writes an 8-bit grey palette for R8 and otherwise 32-bit ARGB in a V5 header with bit fields, bottom-up, so alpha survives. TGA writes 8-bit grey for R8 and otherwise 32-bit ARGB, uncompressed, bottom-left, with a TGA 2.0 footer. PNM writes P5 for one or two channels and P6 for four, dropping alpha, because P6 opens in more readers than PAM. QOI writes 3 channels for R8 and otherwise 4, sRGB. An ICO or CUR entry is an RGBA8 PNG unless the entry asks for a BMP, which carries an AND mask, so a classic mix of PNG at 256 and BMP at the small sizes is one call. Entries are written in the order given and no sizes are generated.

Indexed formats take exact palettes only: the encoder counts the image's colours and throws if there are more than the palette holds. A quantizer was rejected for now because GIF and palette PNG need the same one, so it belongs with the GIF encoder. QOI, P6, P5, TGA raw and RLE, and the BMP 24-bit, 32-bit and 8-bit grey outputs are byte-identical to Pillow 13.

The formats that are nearly a memory copy (raw TGA, binary PNM, 24 and 32-bit BMP) convert a whole row at a time straight from the source row into the output line, with SIMD kernels for the BGRA to RGBA swap and the 24-bit packing; the rarer variants keep the per-pixel path. Against ImageSharp 3.1.11 on two 1024² photos and the 12 Thorium atlases the raw and copy-like encoders now run at 0.36 to 0.87 of its time: 24-bit BMP and TGA at 0.36 to 0.53, 32-bit BMP at 0.66 to 0.75, raw 32-bit TGA at 0.69 to 0.87, 8-bit TGA and BMP 16-bit at 0.34 to 0.68, binary PPM and PGM at 0.36 to 0.80. The swap and the packer pick the widest vector the machine reports (Vector512, then Vector256, then Vector128, then a scalar tail), and the packer uses an in-lane byte shuffle plus a dword permute per 16 pixels where AVX-512BW exists (8 pixels per step on AVX2). Bottom-up rows used to be read in descending order, which alone cost about twice as much; the direct BMP and TGA paths now read the source rows in ascending 256 KB blocks, write each row into its mirrored slot and issue one stream write per block. Still over 0.7 are raw 32-bit TGA on photos, the TGA RLE atlas (1.06 to 1.08), the binary PPM and PGM atlases (0.74 to 0.80), plain PPM (1.27 to 1.67) and QOI (0.75 to 0.85); the encoders target 0.7 or below everywhere. The 24-bit packer carries AggressiveOptimization because a short one-shot encode would otherwise run it as tier-0 code: without it the first 24-bit BMP case in the bench measured 8 to 10 times ImageSharp's time, with it 0.8. ImageSharp 3.1.11 has no ICO or CUR support, so that encoder was not timed.

Every SIMD kernel that can fill a wider vector has a 512-bit, a 256-bit and a 128-bit tier, each gated on its own hardware check and handing its tail to the next narrower tier and finally to scalar code. This covers the CRC-32 (carry-less multiply folding, .NET 9 and later), the Adler-32 and match-length compare in the deflater, the PNG filter-cost loop and the PNG unfilter and expand steps, the 24-bit packer, the JPEG colour conversion, upsampling and block tests, the progressive JPEG inverse transform (two blocks at once in a 512-bit register) and the distance-field atlas interleave. Shuffles at 256 and 512 bits use the x86 instructions directly rather than the portable shuffle, because on .NET 8 the portable one falls back to software unless its index is written inline (about 25 times slower in a microbench). Left scalar-width on purpose are the Paeth, Sub and Average unfilters, where each pixel depends on its left neighbour, and the inflate match copy and the atlas tile copy, which over-write past the end and size their slack for 16-byte vectors. Measured on a Ryzen 9 9900X the wide kernels cut 24-bit BMP encode from about 6.5 ms to 0.5 ms and 8-bit grey PNG decode from about 12.5 ms to 5 ms on .NET 8, but barely show end to end because entropy coding and inflate dominate. See `ClaudeMemory/Decisions/simd-width-tiers.md`.

Not added: a quantizer, 16-bit-index TGA colour maps, TGA metadata, arbitrary BMP masks, automatic icon size generation and clipboard image writing.

See `ClaudeMemory/Decisions/simple-raster-encoders.md` for the reasoning and open gaps.

## JPEG encoder

`ImageCodec.EncodeJpeg(pixels, width, height, rowPitch, layout, output, settings)` and `Image.SaveJpeg(stream or path, settings)` write baseline Huffman JPEG. Grey comes from R8 or RG8 pixels, YCbCr from RGBA8 or BGRA8 with alpha dropped, at 4:2:0, 4:2:2 or 4:4:4 (`ChromaSubsampling`). `JpegSettings` carries the quality (1 to 100, 0 meaning the default of 75), the subsampling and `Optimize`, which builds two-pass optimal Huffman tables instead of the standard ones. `default(JpegSettings)` is quality 75, 4:2:0, standard tables, the same as libjpeg and Pillow. Each side must be 1 to 65535 pixels.

The output is byte-identical to libjpeg-turbo 3.2 (Pillow 13): the same markers, tables and entropy data. That matches the decoder, and it lets the tests compare whole files against Pillow's output. To get there the encoder copies libjpeg's fixed-point colour conversion, its downsampling and edge padding, its islow forward DCT, its quantiser rounding and quality scaling, and, for `Optimize`, its optimal-table generation.

The encoder is scalar, with no SIMD, and its speed has not been measured. Progressive output, restart intervals, arithmetic coding, CMYK, 12-bit and EXIF, ICC or DPI metadata are not written. ImageSharp no longer encodes JPEG anywhere in the engine or the Thorium tests.

See `ClaudeMemory/Decisions/jpeg-encoder.md` for the reasoning and open gaps.

## ADF distance-field atlases

Distance-field atlases (every MTSDF font atlas, the default icon atlas and the font-preview atlas) are stored as ADF, Arctis.Imaging's own near-lossless format, in `*_atlas.adf` files. `ImageCodec.EncodeAdf` and `Image.SaveAdf` write it, `ImageCodec.Decode` and `ImageFiles` read it, and `ImageCodec.TryIdentify` recognises it. Only RGBA8 is accepted, because the format exists for MTSDF atlases and nothing else.

Only the R, G and B planes are stored. Alpha is rebuilt as the median of R, G and B, so the encoder throws if a pixel's alpha is anything else. The image is cut into 256 by 256 tiles, each with its own bitstream, which keeps decode scratch at 192 KB.

```
Encode(rgba, width, height):
	for each plane R, G, B:
		for each 256x256 tile:
			for each texel:
				predicted = MED(left, up, up-left)
				residual = quantise(texel - predicted) to step 3, error at most 1
				if residual is zero:
					extend the current zero run
				else:
					flush the run, write the zigzag literal
	build one canonical Huffman code per plane, capped at 12 bits
	write header, code lengths, tile offsets, tile bitstreams, 16 zero bytes

Decode(file):
	read and validate header, code lengths and tile offsets
	for each 256x256 tile:
		for each plane:
			while texels remain:
				read one symbol through the 4096-entry root table
				if literal:
					texel = MED(left, up, up-left) + dequantised residual
				else:
					copy the run of predicted texels
		interleave the three planes into RGBA, alpha = median(R, G, B)
```

Decode is single-threaded and about as fast as the Arctis PNG decoder; the files are roughly 15 to 18 percent smaller than the PNGs they replaced. A decoded texel is within 1 of the source value. See `ClaudeMemory/Decisions/distance-field-format.md` for the format, the rejected alternatives and the measurements.
