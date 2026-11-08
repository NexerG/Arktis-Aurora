using System;
using System.IO;
using System.Text;
using Xunit;

namespace Arctis.Imaging.Tests
{
    // Fixtures are raw PBM/PGM/PPM written by Pillow 13 with Pillow's decode beside them; plain and PAM files are built here.
    public class PnmTests
    {
        private static byte[] Fixture(string file) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));

        private static byte[] Text(string header, params byte[] raster)
        {
            byte[] head = Encoding.ASCII.GetBytes(header);
            byte[] file = new byte[head.Length + raster.Length];
            head.CopyTo(file, 0);
            raster.CopyTo(file, head.Length);
            return file;
        }

        [Theory]
        [InlineData("PpmRgb.ppm")]
        [InlineData("PgmGrey.pgm")]
        [InlineData("PbmBits.pbm")]
        [InlineData("Pgm16.pgm")]
        public void RawMatchesPillow(string file)
        {
            byte[] data = Fixture(file);
            Assert.True(ImageCodec.TryIdentify(data, out ImageHeader header));
            Assert.Equal(ImageFormat.Pnm, header.Format);
            Assert.Equal(Image.Load(Fixture(Path.ChangeExtension(file, ".png"))).Pixels, Image.Load(data).Pixels);
        }

        [Fact]
        public void PlainBitmapNeedsNoSpaceBetweenBits()
        {
            Image image = Image.Load(Text("P1\n# comment\n3 1\n101"));
            Assert.Equal(new byte[] { 0, 0, 0, 255, 255, 255, 255, 255, 0, 0, 0, 255 }, image.Pixels);
        }

        [Fact]
        public void PlainGreyAndColourScaleFromMaxval()
        {
            Assert.Equal(new byte[] { 0, 0, 0, 255, 128, 128, 128, 255, 255, 255, 255, 255 }, Image.Load(Text("P2 3 1 2\n0 1 2\n")).Pixels);
            Assert.Equal(new byte[] { 255, 0, 85, 255 }, Image.Load(Text("P3\n1 1\n3\n3 0 1\n")).Pixels);
        }

        [Fact]
        public void SixteenBitRawScalesFromMaxval()
        {
            Image image = Image.Load(Text("P5 2 1 1000\n", 0x03, 0xE8, 0x01, 0xF4));
            Assert.Equal(new byte[] { 255, 255, 255, 255, 128, 128, 128, 255 }, image.Pixels);
        }

        [Fact]
        public void PamCarriesAlpha()
        {
            byte[] file = Text("P7\nWIDTH 2\nHEIGHT 1\nDEPTH 4\nMAXVAL 255\nTUPLTYPE RGB_ALPHA\nENDHDR\n", 1, 2, 3, 4, 5, 6, 7, 8);
            Assert.True(ImageCodec.TryIdentify(file, out ImageHeader header));
            Assert.True(header.HasAlpha);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, Image.Load(file).Pixels);
        }

        [Fact]
        public void PamBlackAndWhiteIsWhiteForOne()
        {
            byte[] file = Text("P7\nWIDTH 2\nHEIGHT 1\nMAXVAL 1\nTUPLTYPE BLACKANDWHITE_ALPHA\nENDHDR\n", 1, 1, 0, 0);
            Assert.Equal(new byte[] { 255, 255, 255, 255, 0, 0, 0, 0 }, Image.Load(file).Pixels);
        }

        [Fact]
        public void TruncatedRasterThrows()
        {
            Assert.Throws<ImageFormatException>(() => Image.Load(Text("P6 2 2 255\n", 1, 2, 3)));
            Assert.Throws<ImageFormatException>(() => Image.Load(Text("P2 2 1 255\n7")));
        }

        [Fact]
        public void TextThatOnlyStartsLikeNetpbmIsNotRecognised()
        {
            Assert.False(ImageCodec.TryIdentify(Text("P5 is not an image"), out _));
            Assert.False(ImageCodec.TryIdentify(Text("P8 1 1 255\n", 0), out _));
        }

        [Theory]
        [InlineData("PpmRgb.ppm", PixelLayout.BGRA8)]
        [InlineData("PgmGrey.pgm", PixelLayout.BGRA8)]
        [InlineData("PgmGrey.pgm", PixelLayout.R8)]
        [InlineData("PgmGrey.pgm", PixelLayout.RG8)]
        public void RawDecodesIntoEveryLayout(string file, PixelLayout layout)
        {
            byte[] rgba = Image.Load(Fixture(Path.ChangeExtension(file, ".png"))).Pixels;
            Assert.Equal(FromRgba(rgba, layout), Image.Load(Fixture(file), layout).Pixels);
        }

        [Theory]
        [InlineData("P6 37 3 255\n", 3, PixelLayout.RGBA8)]
        [InlineData("P6 37 3 255\n", 3, PixelLayout.BGRA8)]
        [InlineData("P5 37 3 255\n", 1, PixelLayout.RGBA8)]
        [InlineData("P5 37 3 255\n", 1, PixelLayout.R8)]
        [InlineData("P7\nWIDTH 37\nHEIGHT 3\nDEPTH 4\nMAXVAL 255\nTUPLTYPE RGB_ALPHA\nENDHDR\n", 4, PixelLayout.BGRA8)]
        public void OddWidthRowsDecodeExactly(string header, int depth, PixelLayout layout)
        {
            byte[] raster = new byte[37 * 3 * depth];
            for (int i = 0; i < raster.Length; i++) raster[i] = (byte)(i * 7 + 3);
            byte[] rgba = new byte[37 * 3 * 4];
            for (int p = 0; p < 37 * 3; p++)
                for (int k = 0; k < 4; k++)
                    rgba[p * 4 + k] = k == 3 && depth < 4 ? (byte)255 : raster[p * depth + (depth == 1 ? 0 : k)];
            Assert.Equal(FromRgba(rgba, layout), Image.Load(Text(header, raster), layout).Pixels);
        }

        // RGBA8 pixels in another layout, for images whose colour channels are equal when the layout is grey.
        private static byte[] FromRgba(byte[] rgba, PixelLayout layout)
        {
            int n = rgba.Length / 4, bpp = layout == PixelLayout.R8 ? 1 : layout == PixelLayout.RG8 ? 2 : 4;
            byte[] pixels = new byte[n * bpp];
            for (int p = 0; p < n; p++)
            {
                if (bpp == 4)
                {
                    bool swap = layout == PixelLayout.BGRA8;
                    pixels[p * 4] = rgba[p * 4 + (swap ? 2 : 0)];
                    pixels[p * 4 + 1] = rgba[p * 4 + 1];
                    pixels[p * 4 + 2] = rgba[p * 4 + (swap ? 0 : 2)];
                    pixels[p * 4 + 3] = rgba[p * 4 + 3];
                }
                else
                {
                    pixels[p * bpp] = rgba[p * 4];
                    if (bpp == 2) pixels[p * 2 + 1] = rgba[p * 4 + 3];
                }
            }
            return pixels;
        }

        private static byte[] Encode(Image image, in PnmSettings settings)
        {
            MemoryStream output = new MemoryStream();
            image.SavePnm(output, settings);
            return output.ToArray();
        }

        // A sample through maxval and back, as the decoder scales it.
        private static byte Through(int v, int maxval) => (byte)(((v * maxval + 127) / 255 * 255 + maxval / 2) / maxval);

        [Fact]
        public void EncodeMatchesPillow()
        {
            Assert.Equal(Fixture("EncodeSource.ppm"), Encode(Image.Load(Fixture("EncodeSource.png")), default));
            Assert.Equal(Fixture("EncodeGrey.pgm"), Encode(Image.Load(Fixture("EncodeGrey.png"), PixelLayout.R8), default));
            Assert.Equal(Fixture("EncodeGrey.pgm"), Encode(Image.Load(Fixture("EncodeGrey.png"), PixelLayout.RG8), default));
        }

        [Theory]
        [InlineData(PnmKind.Pbm, false, 0, PamTupleType.Auto)]
        [InlineData(PnmKind.Pbm, true, 0, PamTupleType.Auto)]
        [InlineData(PnmKind.Pgm, false, 255, PamTupleType.Auto)]
        [InlineData(PnmKind.Pgm, true, 15, PamTupleType.Auto)]
        [InlineData(PnmKind.Pgm, false, 65535, PamTupleType.Auto)]
        [InlineData(PnmKind.Ppm, false, 1000, PamTupleType.Auto)]
        [InlineData(PnmKind.Ppm, true, 255, PamTupleType.Auto)]
        [InlineData(PnmKind.Ppm, true, 65535, PamTupleType.Auto)]
        [InlineData(PnmKind.Pam, false, 0, PamTupleType.Auto)]
        [InlineData(PnmKind.Pam, false, 0, PamTupleType.BlackAndWhite)]
        [InlineData(PnmKind.Pam, false, 0, PamTupleType.BlackAndWhiteAlpha)]
        [InlineData(PnmKind.Pam, false, 7, PamTupleType.Grayscale)]
        [InlineData(PnmKind.Pam, false, 300, PamTupleType.GrayscaleAlpha)]
        [InlineData(PnmKind.Pam, false, 255, PamTupleType.Rgb)]
        [InlineData(PnmKind.Pam, false, 4095, PamTupleType.RgbAlpha)]
        public void EncodeRoundTripsEveryKind(PnmKind kind, bool plain, int maxval, PamTupleType tuple)
        {
            Image image = Image.Load(Fixture("QoiRgba.png"));
            PnmSettings settings = new PnmSettings(kind, plain, maxval, tuple);
            byte[] file = Encode(image, settings);
            Image decoded = Image.Load(file);
            int m = settings.MaxVal;
            PamTupleType type = kind == PnmKind.Pam && tuple == PamTupleType.Auto ? PamTupleType.RgbAlpha : tuple;
            bool bits = kind == PnmKind.Pbm || type == PamTupleType.BlackAndWhite || type == PamTupleType.BlackAndWhiteAlpha;
            bool greyOut = bits || kind == PnmKind.Pgm || type == PamTupleType.Grayscale || type == PamTupleType.GrayscaleAlpha;
            bool alphaOut = type == PamTupleType.BlackAndWhiteAlpha || type == PamTupleType.GrayscaleAlpha || type == PamTupleType.RgbAlpha;
            byte[] p = image.Pixels;
            for (int i = 0; i < p.Length; i += 4)
            {
                int luma = (p[i] * 13933 + p[i + 1] * 46871 + p[i + 2] * 4732 + 32768) >> 16;
                byte[] expected = greyOut
                    ? new byte[] { Through(luma, m), Through(luma, m), Through(luma, m), 0 }
                    : new byte[] { Through(p[i], m), Through(p[i + 1], m), Through(p[i + 2], m), 0 };
                if (kind == PnmKind.Pbm) expected[0] = expected[1] = expected[2] = luma < 128 ? (byte)0 : (byte)255;
                expected[3] = alphaOut ? Through(p[i + 3], m) : (byte)255;
                Assert.Equal(expected, decoded.Pixels[i..(i + 4)]);
            }
            if (plain)
                foreach (string line in Encoding.ASCII.GetString(file).Split('\n')) Assert.True(line.Length <= 70, $"plain line of {line.Length} characters");
        }

        [Fact]
        public void PamHeaderNamesTheTuple()
        {
            byte[] file = Encode(Image.Load(Fixture("EncodeGrey.png"), PixelLayout.RG8), new PnmSettings(PnmKind.Pam));
            Assert.StartsWith("P7\nWIDTH 45\nHEIGHT 29\nDEPTH 2\nMAXVAL 255\nTUPLTYPE GRAYSCALE_ALPHA\nENDHDR\n", Encoding.ASCII.GetString(file));
        }

        [Fact]
        public void ContradictorySettingsThrow()
        {
            Image image = new Image(1, 1);
            Assert.Throws<ArgumentException>(() => Encode(image, new PnmSettings(PnmKind.Pam, plain: true)));
            Assert.Throws<ArgumentException>(() => Encode(image, new PnmSettings(PnmKind.Pbm, maxVal: 3)));
            Assert.Throws<ArgumentException>(() => Encode(image, new PnmSettings(PnmKind.Ppm, maxVal: 65536)));
            Assert.Throws<ArgumentException>(() => Encode(image, new PnmSettings(PnmKind.Ppm, tupleType: PamTupleType.Rgb)));
            Assert.Throws<ArgumentException>(() => Encode(image, new PnmSettings(PnmKind.Pam, maxVal: 255, tupleType: PamTupleType.BlackAndWhite)));
        }
    }
}
