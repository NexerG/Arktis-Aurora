using ArctisAurora.Core.Diagnostics;
using ArctisAurora.EngineWork.Rendering;
using ArctisAurora.EngineWork.Rendering.Helpers;
using Arctis.Imaging;
using Silk.NET.Vulkan;
using System.Buffers;
using System.Numerics;
using Buffer = Silk.NET.Vulkan.Buffer;
using Image = Arctis.Imaging.Image;
using VkImage = Silk.NET.Vulkan.Image;

namespace ArctisAurora.Core.Imaging
{
    // Uploads image files as textures: decoded straight into staging, or through a CPU copy when mips, a shrink
    // to the device limit or the opaque-row table need the pixels.
    internal static unsafe class TextureLoader
    {
        private static readonly LogChannel Log = LogChannel.For("Renderer");

        // Uploads an encoded file; rows is the opaque-row table when asked for, else null.
        internal static void UploadFile(ReadOnlySpan<byte> file, Format format, bool mipmaps, bool opaqueRows, ref VkImage image, ref DeviceMemory memory,
            out int width, out int height, out (float left, float right)[]? rows)
        {
            if (mipmaps && ImageCodec.TryReadKtx2(file, out Ktx2Info info) && info.Levels > 1)
            {
                UploadKtx2(file, info, format, opaqueRows, ref image, ref memory, out width, out height, out rows);
                return;
            }
            PixelLayout layout = FormatMap.LayoutOf(format);
            if (!mipmaps && !opaqueRows && ImageCodec.TryIdentify(file, out ImageHeader header))
            {
                uint limit = AVulkanBufferHandler.MaxImageDimension();
                if (header.Width <= limit && header.Height <= limit)
                {
                    DecodeOptions options = new DecodeOptions(layout, 0, ImageFiles.Limits);
                    int size = ImageCodec.RequiredBytes(header, options);
                    byte* staging = AVulkanBufferHandler.MapStaging((ulong)size, out Buffer stagingBuffer, out DeviceMemory stagingMemory);
                    try
                    {
                        Profiling.Zone.Start("Image.Decode");
                        ImageCodec.Decode(file, new Span<byte>(staging, size), options);
                        Profiling.Zone.End("Image.Decode");
                    }
                    catch
                    {
                        AVulkanBufferHandler.FreeStaging(stagingBuffer, stagingMemory);
                        throw;
                    }
                    BufferImageCopy[] regions = { Region(0, 0, header.Width, header.Height) };
                    AVulkanBufferHandler.UploadStaged(ref image, ref memory, (uint)header.Width, (uint)header.Height, format, regions, stagingBuffer, stagingMemory,
                        ref Renderer.transferQueue, ref Renderer.transferCommandPool);
                    width = header.Width;
                    height = header.Height;
                    rows = null;
                    return;
                }
            }
            UploadImage(ImageFiles.Load(file, layout), format, mipmaps, opaqueRows, ref image, ref memory, out width, out height, out rows);
        }

        // Uploads a KTX2 file's stored mips, decoded straight into staging, from the first level the device fits.
        private static void UploadKtx2(ReadOnlySpan<byte> file, in Ktx2Info info, Format format, bool opaqueRows, ref VkImage image, ref DeviceMemory memory,
            out int width, out int height, out (float left, float right)[]? rows)
        {
            PixelLayout layout = FormatMap.LayoutOf(format);
            int bpp = ImageCodec.BytesPerPixel(layout);
            if (opaqueRows && bpp != 4) throw new ArgumentException("opaque rows need four bytes per pixel");
            bool srgb = format is Format.R8G8B8A8Srgb or Format.B8G8R8A8Srgb or Format.R8Srgb or Format.R8G8Srgb;
            if (srgb != info.Srgb) Log.Once().Warn($"a KTX2 file stored as {(info.Srgb ? "SRGB" : "UNORM")} uploads as {format}");

            uint limit = AVulkanBufferHandler.MaxImageDimension();
            int first = 0;
            while (first < info.Levels && (LevelSize(info.Width, first) > limit || LevelSize(info.Height, first) > limit)) first++;
            if (first == info.Levels) throw new NotSupportedException($"no level of a {info.Width}x{info.Height} KTX2 fits the device limit {limit}");
            if (first > 0) Log.Warn($"KTX2 {info.Width}x{info.Height} exceeds the device limit {limit}; uploading from level {first}.");

            BufferImageCopy[] regions = new BufferImageCopy[info.Levels - first];
            ulong size = 0;
            for (int i = 0; i < regions.Length; i++)
            {
                int w = LevelSize(info.Width, first + i), h = LevelSize(info.Height, first + i);
                regions[i] = Region(i, size, w, h);
                size += (ulong)(w * h * bpp);
            }
            width = LevelSize(info.Width, first);
            height = LevelSize(info.Height, first);
            rows = null;

            DecodeOptions options = new DecodeOptions(layout, 0, ImageFiles.Limits);
            byte* staging = AVulkanBufferHandler.MapStaging(size, out Buffer stagingBuffer, out DeviceMemory stagingMemory);
            byte[]? top = null;
            try
            {
                Profiling.Zone.Start("Image.Decode");
                for (int i = 0; i < regions.Length; i++)
                {
                    int length = (int)(regions[i].ImageExtent.Width * regions[i].ImageExtent.Height) * bpp;
                    Span<byte> target = new Span<byte>(staging + regions[i].BufferOffset, length);
                    if (i > 0 || !opaqueRows)
                    {
                        ImageCodec.DecodeKtx2Level(file, first + i, target, options);
                        continue;
                    }
                    top = ArrayPool<byte>.Shared.Rent(length);
                    Span<byte> pixels = top.AsSpan(0, length);
                    ImageCodec.DecodeKtx2Level(file, first, pixels, options);
                    rows = OpaqueRowsOf(pixels, width, height);
                    pixels.CopyTo(target);
                }
                Profiling.Zone.End("Image.Decode");
            }
            catch
            {
                AVulkanBufferHandler.FreeStaging(stagingBuffer, stagingMemory);
                throw;
            }
            finally
            {
                if (top != null) ArrayPool<byte>.Shared.Return(top);
            }
            AVulkanBufferHandler.UploadStaged(ref image, ref memory, (uint)width, (uint)height, format, regions, stagingBuffer, stagingMemory,
                ref Renderer.transferQueue, ref Renderer.transferCommandPool);
        }

        // Uploads a decoded image; mips are box-filtered in linear light, each level from a CPU copy of the one above.
        internal static void UploadImage(Image source, Format format, bool mipmaps, bool opaqueRows, ref VkImage image, ref DeviceMemory memory,
            out int width, out int height, out (float left, float right)[]? rows)
        {
            if (FormatMap.LayoutOf(format) != source.Layout) throw new ArgumentException($"a {source.Layout} image cannot upload as {format}");
            int bpp = ImageCodec.BytesPerPixel(source.Layout);
            if ((mipmaps || opaqueRows) && bpp != 4) throw new ArgumentException("mips and opaque rows need four bytes per pixel");
            Image top = FitDevice(source, bpp);
            rows = opaqueRows ? OpaqueRowsOf(top.Pixels, top.Width, top.Height) : null;

            int levels = mipmaps ? BitOperations.Log2((uint)Math.Max(top.Width, top.Height)) + 1 : 1;
            BufferImageCopy[] regions = new BufferImageCopy[levels];
            ulong size = 0;
            for (int i = 0, w = top.Width, h = top.Height; i < levels; i++, w = ImageOps.HalfSize(w), h = ImageOps.HalfSize(h))
            {
                regions[i] = Region(i, size, w, h);
                size += (ulong)(w * h * bpp);
            }

            byte* staging = AVulkanBufferHandler.MapStaging(size, out Buffer stagingBuffer, out DeviceMemory stagingMemory);
            byte[]? scratch = null;
            try
            {
                top.Pixels.CopyTo(new Span<byte>(staging, top.Pixels.Length));
                if (levels > 1)
                {
                    int half = ImageOps.HalfSize(top.Width) * ImageOps.HalfSize(top.Height) * 4;
                    scratch = ArrayPool<byte>.Shared.Rent(half * 2);
                    ReadOnlySpan<byte> above = top.Pixels;
                    int aw = top.Width, ah = top.Height;
                    for (int i = 1; i < levels; i++)
                    {
                        int lw = ImageOps.HalfSize(aw), lh = ImageOps.HalfSize(ah);
                        Span<byte> level = scratch.AsSpan((i & 1) == 1 ? 0 : half, lw * lh * 4);
                        ImageOps.HalveLinear(above, aw, ah, aw * 4, level, lw * 4);
                        level.CopyTo(new Span<byte>(staging + regions[i].BufferOffset, level.Length));
                        above = level;
                        aw = lw;
                        ah = lh;
                    }
                }
            }
            catch
            {
                AVulkanBufferHandler.FreeStaging(stagingBuffer, stagingMemory);
                throw;
            }
            finally
            {
                if (scratch != null) ArrayPool<byte>.Shared.Return(scratch);
            }
            AVulkanBufferHandler.UploadStaged(ref image, ref memory, (uint)top.Width, (uint)top.Height, format, regions, stagingBuffer, stagingMemory,
                ref Renderer.transferQueue, ref Renderer.transferCommandPool);
            width = top.Width;
            height = top.Height;
        }

        // The image itself, or a copy shrunk to the device's 2D limit with its aspect kept.
        private static Image FitDevice(Image source, int bpp)
        {
            uint limit = AVulkanBufferHandler.MaxImageDimension();
            if (source.Width <= limit && source.Height <= limit) return source;
            if (bpp != 4) throw new NotSupportedException($"a {source.Layout} image larger than the device limit {limit} cannot be shrunk");

            float scale = (float)limit / Math.Max(source.Width, source.Height);
            int width = Math.Max(1, (int)(source.Width * scale));
            int height = Math.Max(1, (int)(source.Height * scale));
            Log.Warn($"image {source.Width}x{source.Height} exceeds the device limit {limit}; uploading at {width}x{height}.");
            Image fitted = new Image(width, height, source.Layout);
            ImageOps.ResizeArea(source.Pixels, source.Width, source.Height, source.RowPitch, fitted.Pixels, width, height, fitted.RowPitch);
            return fitted;
        }

        // Each pixel row's leftmost and rightmost opaque pixel as fractions of the width; an empty row has left > right.
        private static (float left, float right)[] OpaqueRowsOf(ReadOnlySpan<byte> pixels, int width, int height)
        {
            (float left, float right)[] rows = new (float, float)[height];
            for (int y = 0; y < rows.Length; y++)
            {
                int start = y * width * 4;
                int first = -1, last = -1;
                for (int x = 0; x < width; x++)
                    if (pixels[start + x * 4 + 3] >= 128)
                    {
                        if (first < 0) first = x;
                        last = x;
                    }
                rows[y] = first < 0 ? (1f, 0f) : ((float)first / width, (float)(last + 1) / width);
            }
            return rows;
        }

        private static BufferImageCopy Region(int level, ulong offset, int width, int height) => new BufferImageCopy
        {
            BufferOffset = offset,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, (uint)level, 0, 1),
            ImageExtent = new Extent3D((uint)width, (uint)height, 1)
        };

        private static int LevelSize(int size, int level) => Math.Max(1, size >> level);
    }
}
