using System;
using System.Buffers.Binary;
using Xunit;

namespace Arctis.Imaging.Tests
{
    public class FarbfeldTests
    {
        private static byte[] Farbfeld(int width, int height, params ushort[] samples)
        {
            byte[] file = new byte[16 + samples.Length * 2];
            "farbfeld"u8.CopyTo(file);
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(8), (uint)width);
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(12), (uint)height);
            for (int i = 0; i < samples.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(16 + i * 2), samples[i]);
            return file;
        }

        [Fact]
        public void SamplesKeepTheirHighByte()
        {
            byte[] file = Farbfeld(2, 1, 0xFFFF, 0x1234, 0x00FF, 0x8000, 0, 0x0100, 0xABCD, 0xFF00);
            Assert.True(ImageCodec.TryIdentify(file, out ImageHeader header));
            Assert.Equal(ImageFormat.Farbfeld, header.Format);
            Assert.Equal(new byte[] { 0xFF, 0x12, 0x00, 0x80, 0, 0x01, 0xAB, 0xFF }, Image.Load(file).Pixels);
        }

        [Fact]
        public void TruncatedPixelsThrow() => Assert.Throws<ImageFormatException>(() => Image.Load(Farbfeld(2, 2, 1, 2, 3, 4)));
    }
}
