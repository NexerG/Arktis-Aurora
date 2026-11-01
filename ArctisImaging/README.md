# Arctis.Imaging

Image decoding and encoding for .NET that writes pixels straight into memory you own — a pooled array, a
native buffer, or a mapped GPU staging buffer — with no intermediate image object.

- Managed code only, no native dependencies: runs wherever .NET runs (Windows, Linux, macOS, iOS, Android),
  trimming- and AOT-compatible.
- Every header is checked against `ImageLimits` before anything is allocated.
- Decoders: PNG (all colour types and bit depths, interlaced), BMP / DIB (1–32 bit, bit fields).
  Identification only: JPEG, GIF.
- Encoder: PNG.

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
