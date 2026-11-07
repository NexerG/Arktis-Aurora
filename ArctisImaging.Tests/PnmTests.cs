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
