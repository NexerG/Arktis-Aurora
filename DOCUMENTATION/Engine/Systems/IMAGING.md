Status: Current
tags:
  - Engine
  - Systems

`Arctis.Imaging` is the engine's own image library: PNG decode and encode, zlib inflate and deflate, and KTX 2.0 textures. Nothing at runtime uses `System.IO.Compression`; the test projects keep `ZLibStream` only as an independent reference.

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

`TryReadKtx2` reports the layout, colour space, size and level count. `DecodeKtx2Level` decodes one level into the layout the caller asks for. `EncodeKtx2` takes the finished mip chain, it does not generate mips. `ImageCodec.Decode` and `ImageFiles` load a `.ktx2` like any other image, taking level 0.

R8 and R8G8 decode as grey and grey+alpha, the same meaning they have for PNG, not Vulkan's red and red-green.

See `ClaudeMemory/Decisions/png-deflate.md` and `ClaudeMemory/Decisions/ktx2-container.md` for the reasoning and measurements.

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
