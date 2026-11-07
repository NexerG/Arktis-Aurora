# Arctis.Imaging

Image decoding and encoding for .NET that writes pixels straight into memory you own — a pooled array, a
native buffer, or a mapped GPU staging buffer — with no intermediate image object.

- Managed code only, no native dependencies: runs wherever .NET runs (Windows, Linux, macOS, iOS, Android),
  trimming- and AOT-compatible.
- Every header is checked against `ImageLimits` before anything is allocated.
- Decoders: PNG (all colour types and bit depths, interlaced), BMP / DIB (1–32 bit, bit fields, RLE4/RLE8),
  JPEG (baseline, progressive, grey, RGB/YCbCr, CMYK/YCCK; lossless 2-16 bit; no arithmetic coding),
  JPEG-LS, GIF (first frame),
  TGA, TIFF (8/16-bit, 1/2/4-bit grey and palette, CMYK, planar, none/LZW/Deflate/PackBits/JPEG),
  WebP (lossless, lossy, first frame of animated), ICO / CUR, PNM / PAM (PBM, PGM, PPM), QOI,
  farbfeld, PCX, XBM, XPM.
- Encoders: PNG, JPEG, BMP / DIB, TGA, ICO / CUR, PNM / PAM, QOI. BMP, TGA, PNM and QOI take a settings
  struct (`BmpSettings`, `TgaSettings`, `PnmSettings`, `QoiSettings`) exposing every variant of the format;
  ICO / CUR take `IconEntry` values. Indexed output uses exact palettes only (more colours than the palette
  holds throws).
- A variant a decoder refuses throws `NotSupportedException`.
- PNG image data is inflated by Arctis's own decoder; zlib is not on the decode path.

## Decode into your own memory

```csharp
byte[] file = File.ReadAllBytes("picture.png");
if (!ImageCodec.TryIdentify(file, out ImageHeader header)) return;

DecodeOptions options = new DecodeOptions(PixelLayout.RGBA8, rowPitch: 0);
Span<byte> staging = MapStagingBuffer(ImageCodec.RequiredBytes(header, options));
ImageCodec.Decode(file, staging, options);
```

`RowPitch` lets rows land at a stride wider than the image, as a GPU copy may require.

## Or just load an image

```csharp
Image image = Image.Load("picture.png");
Span<byte> firstRow = image.Row(0);
image.SavePng("copy.png");
```

## Pixel operations

`ImageOps` works span to span: `Crop`, `HalveLinear` (one mip level, box filter in linear light with
premultiplied alpha), `ResizeArea`, `SwizzleBgraToRgba`.

## License

Apache-2.0.
