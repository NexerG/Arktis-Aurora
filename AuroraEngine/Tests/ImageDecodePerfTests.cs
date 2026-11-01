using Arctis.Imaging;
using ArctisAurora.Core.Diagnostics;
using ArctisAurora.Core.Filing.Serialization;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Testing;
using ArctisAurora.EngineWork;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SharpImage = SixLabors.ImageSharp.Image;

namespace ArctisAurora.Tests
{
    internal static class ImageDecodePerfTests
    {
        private const int decodes = 64;
        private const int sampleSize = 1024;

        // one decodable file per frame, the zone it is timed under, and its decoded RGBA8 size
        private struct Work
        {
            public byte[][] files;
            public string[] zones;
            public int largest;
        }

        [A_XSDActionDependency("Perf.ImageDecode.Atlases.Arctis", "Test")]
        private static IEnumerator<int> AtlasesArctis(TestContext t) => Measure(t, Atlases("Image.Decode.Arctis.Atlas"), false);

        [A_XSDActionDependency("Perf.ImageDecode.Atlases.ImageSharp", "Test")]
        private static IEnumerator<int> AtlasesImageSharp(TestContext t) => Measure(t, Atlases("Image.Decode.ImageSharp.Atlas"), true);

        [A_XSDActionDependency("Perf.ImageDecode.Formats.Arctis", "Test")]
        private static IEnumerator<int> FormatsArctis(TestContext t) => Measure(t, Formats("Arctis", false), false);

        [A_XSDActionDependency("Perf.ImageDecode.Formats.ImageSharp", "Test")]
        private static IEnumerator<int> FormatsImageSharp(TestContext t) => Measure(t, Formats("ImageSharp", true), true);

        // Decodes one file per frame into a reused RGBA8 buffer, each under its zone.
        private static IEnumerator<int> Measure(TestContext t, Func<Work> prepare, bool sharp)
        {
            if (Engine.isDebug || !Profiling.compiledIn)
            {
                t.StartMeasure();
                yield return t.EndMeasure();
                yield break;
            }

            Work work = prepare();
            byte[] pixels = new byte[work.largest];
            DecodeOptions options = new DecodeOptions(PixelLayout.RGBA8);
            byte[][] files = work.files;
            string[] zones = work.zones;

            t.StartMeasure();
            yield return 1;
            for (int i = 0; i < files.Length; i++)
            {
                Profiling.Zone.Start(zones[i]);
                if (sharp) DecodeSharp(files[i], pixels);
                else ImageCodec.Decode(files[i], pixels, options);
                Profiling.Zone.End(zones[i]);
                yield return 1;
            }
            yield return t.EndMeasure();
        }

        private static void DecodeSharp(byte[] file, byte[] pixels)
        {
            using SixLabors.ImageSharp.Image<Rgba32> image = SharpImage.Load<Rgba32>(file);
            image.CopyPixelDataTo(new Span<byte>(pixels, 0, image.Width * image.Height * 4));
        }

        // The font and icon atlases, cycled into 64 separate copies.
        private static Func<Work> Atlases(string zone) => () =>
        {
            string[] fonts = Directory.GetFiles(VirtualFileSystem.ResolveDir("Fonts"), "*_atlas.png", SearchOption.AllDirectories);
            byte[][] sources = new byte[fonts.Length + 1][];
            for (int i = 0; i < fonts.Length; i++)
                sources[i] = File.ReadAllBytes(fonts[i]);
            sources[fonts.Length] = File.ReadAllBytes(VirtualFileSystem.ResolveFile("Icons/default/default_atlas.png"));

            Work work = new Work { files = new byte[decodes][], zones = new string[decodes] };
            for (int i = 0; i < decodes; i++)
            {
                work.files[i] = (byte[])sources[i % sources.Length].Clone();
                work.zones[i] = zone;
            }
            work.largest = Largest(sources);
            return work;
        };

        // A 1024² crop of the arial atlas in every PNG colour type, filter and interlace, plus JPEG and GIF.
        private static Func<Work> Formats(string library, bool foreign) => () =>
        {
            byte[] atlas = File.ReadAllBytes(VirtualFileSystem.ResolveFile("Fonts/arial/arial_atlas.png"));
            ImageCodec.TryIdentify(atlas, out ImageHeader header);
            byte[] full = new byte[header.Width * header.Height * 4];
            ImageCodec.Decode(atlas, full, new DecodeOptions(PixelLayout.RGBA8));
            byte[] crop = new byte[sampleSize * sampleSize * 4];
            ImageOps.Crop(full, header.Width * 4, PixelLayout.RGBA8, 0, 0, sampleSize, sampleSize, crop, sampleSize * 4);

            PngColorType[] colours = { PngColorType.RgbWithAlpha, PngColorType.Rgb, PngColorType.Grayscale, PngColorType.RgbWithAlpha };
            PngBitDepth[] depths = { PngBitDepth.Bit8, PngBitDepth.Bit8, PngBitDepth.Bit8, PngBitDepth.Bit16 };
            string[] colourNames = { "RGBA8", "RGB8", "Grey8", "RGBA16" };
            PngFilterMethod[] filters = { PngFilterMethod.None, PngFilterMethod.Sub, PngFilterMethod.Up, PngFilterMethod.Average, PngFilterMethod.Paeth };

            int rows = colours.Length * filters.Length + 1 + (foreign ? 2 : 0);
            byte[][] samples = new byte[rows][];
            string[] names = new string[rows];
            using (SixLabors.ImageSharp.Image<Rgba32> image = SharpImage.LoadPixelData<Rgba32>(crop, sampleSize, sampleSize))
            {
                int row = 0;
                for (int c = 0; c < colours.Length; c++)
                {
                    for (int f = 0; f < filters.Length; f++)
                    {
                        samples[row] = Encode(image, new PngEncoder { ColorType = colours[c], BitDepth = depths[c], FilterMethod = filters[f] });
                        names[row++] = $"Image.Decode.{library}.Png.{colourNames[c]}.{filters[f]}";
                    }
                }
                samples[row] = Encode(image, new PngEncoder { ColorType = PngColorType.RgbWithAlpha, InterlaceMethod = PngInterlaceMode.Adam7 });
                names[row++] = $"Image.Decode.{library}.Png.RGBA8.Adam7";
                if (foreign)
                {
                    samples[row] = Encode(image, new JpegEncoder { Quality = 90 });
                    names[row++] = $"Image.Decode.{library}.Jpeg.Q90";
                    samples[row] = Encode(image, new GifEncoder());
                    names[row++] = $"Image.Decode.{library}.Gif";
                }
            }

            Work work = new Work { files = new byte[rows * decodes][], zones = new string[rows * decodes] };
            for (int r = 0; r < rows; r++)
            {
                for (int i = 0; i < decodes; i++)
                {
                    work.files[r * decodes + i] = (byte[])samples[r].Clone();
                    work.zones[r * decodes + i] = names[r];
                }
            }
            work.largest = crop.Length;
            return work;
        };

        private static byte[] Encode(SixLabors.ImageSharp.Image<Rgba32> image, IImageEncoder encoder)
        {
            using MemoryStream stream = new MemoryStream();
            image.Save(stream, encoder);
            return stream.ToArray();
        }

        private static int Largest(byte[][] files)
        {
            int largest = 0;
            for (int i = 0; i < files.Length; i++)
            {
                ImageCodec.TryIdentify(files[i], out ImageHeader header);
                largest = Math.Max(largest, header.Width * header.Height * 4);
            }
            return largest;
        }
    }
}
