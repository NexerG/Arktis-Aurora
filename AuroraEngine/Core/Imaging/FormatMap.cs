using Arctis.Imaging;
using Silk.NET.Vulkan;

namespace ArctisAurora.Core.Imaging
{
    internal static class FormatMap
    {
        // The layout a texture of this format decodes into.
        internal static PixelLayout LayoutOf(Format format) => format switch
        {
            Format.R8G8B8A8Unorm or Format.R8G8B8A8Srgb => PixelLayout.RGBA8,
            Format.B8G8R8A8Unorm or Format.B8G8R8A8Srgb => PixelLayout.BGRA8,
            Format.R8Unorm or Format.R8Srgb => PixelLayout.R8,
            Format.R8G8Unorm or Format.R8G8Srgb => PixelLayout.RG8,
            _ => throw new NotSupportedException($"an image file cannot be decoded as {format}")
        };
    }
}
