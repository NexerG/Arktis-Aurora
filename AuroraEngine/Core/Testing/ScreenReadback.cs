using ArctisAurora.EngineWork;
using ArctisAurora.EngineWork.Rendering;
using ArctisAurora.EngineWork.Rendering.Helpers;
using ArctisAurora.EngineWork.Rendering.Modules;
using Arctis.Imaging;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;
using Image = Arctis.Imaging.Image;

namespace ArctisAurora.Core.Testing
{
    // A copy of a window's swapchain image after compositing, before present.
    internal static unsafe class ScreenReadback
    {
        // the copy in flight, render thread only
        private static Buffer _buffer;
        private static DeviceMemory _memory;
        private static CommandBuffer _commands;

        // Asks for the first frame drawn from a tick after this one. Main thread.
        internal static void Request(RenderWindow window)
        {
            window.readbackPixels = null;
            window.readbackEpoch = Engine.mainSystem.Epoch + 1;
            window.readbackRequested = true;
        }

        // The copied frame as RGBA once it has landed, or an error for a format it cannot read. Main thread.
        internal static bool TryTake(RenderWindow window, out Image? image, out string? error)
        {
            image = null;
            error = null;
            byte[]? pixels = window.readbackPixels;
            if (pixels == null) return false;
            window.readbackPixels = null;

            int width = (int)window.readbackWidth, height = (int)window.readbackHeight;
            switch (window.readbackFormat)
            {
                case Format.R8G8B8A8Unorm:
                case Format.R8G8B8A8Srgb:
                    image = Image.FromPixels(pixels, width, height, 0, PixelLayout.RGBA8);
                    break;
                case Format.B8G8R8A8Unorm:
                case Format.B8G8R8A8Srgb:
                    image = Image.FromPixels(pixels, width, height, 0, PixelLayout.RGBA8);
                    ImageOps.SwizzleBgraToRgba(image.Pixels);
                    break;
                default:
                    error = $"cannot read a {window.readbackFormat} swapchain";
                    return true;
            }

            byte[] rgba = image.Pixels;
            for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 255;
            return true;
        }

        // Records the copy of this frame's image when one is due; it goes after the compositor in its submit.
        internal static bool Record(RenderWindow window, uint imageIndex, out CommandBuffer commands)
        {
            commands = default;
            if (!window.readbackRequested || Engine.mainSystem.Epoch < window.readbackEpoch) return false;

            Vk vk = Renderer.vk;
            Extent2D extent = window.swapchainExtent;
            ulong size = (ulong)extent.Width * extent.Height * 4;
            AVulkanBufferHandler.CreateBuffer(size, ref _buffer, ref _memory, BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit | MemoryPropertyFlags.HostCachedBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

            CommandBufferAllocateInfo alloc = new CommandBufferAllocateInfo()
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = Renderer.compositeCommandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1
            };
            fixed (CommandBuffer* ptr = &_commands)
                vk.AllocateCommandBuffers(Renderer.logicalDevice, ref alloc, ptr);

            CommandBufferBeginInfo begin = new CommandBufferBeginInfo()
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit
            };
            vk.BeginCommandBuffer(_commands, ref begin);

            Silk.NET.Vulkan.Image image = window.swapchainImages[imageIndex];
            RenderingModule.ImageBarrier(_commands, image, ImageLayout.PresentSrcKhr, ImageLayout.TransferSrcOptimal,
                PipelineStageFlags.AllCommandsBit, PipelineStageFlags.TransferBit,
                AccessFlags.MemoryWriteBit, AccessFlags.TransferReadBit);

            BufferImageCopy region = new BufferImageCopy()
            {
                ImageSubresource = new ImageSubresourceLayers()
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    MipLevel = 0,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                },
                ImageExtent = new Extent3D(extent.Width, extent.Height, 1)
            };
            vk.CmdCopyImageToBuffer(_commands, image, ImageLayout.TransferSrcOptimal, _buffer, 1, &region);

            RenderingModule.ImageBarrier(_commands, image, ImageLayout.TransferSrcOptimal, ImageLayout.PresentSrcKhr,
                PipelineStageFlags.TransferBit, PipelineStageFlags.BottomOfPipeBit,
                AccessFlags.TransferReadBit, AccessFlags.None);

            BufferMemoryBarrier host = new BufferMemoryBarrier()
            {
                SType = StructureType.BufferMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.HostReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = _buffer,
                Offset = 0,
                Size = Vk.WholeSize
            };
            vk.CmdPipelineBarrier(_commands, PipelineStageFlags.TransferBit, PipelineStageFlags.HostBit, 0, 0, null, 1, &host, 0, null);
            vk.EndCommandBuffer(_commands);

            window.readbackWidth = extent.Width;
            window.readbackHeight = extent.Height;
            window.readbackFormat = window.surfaceFormat.Format;
            commands = _commands;
            return true;
        }

        // Waits for the frame, copies its pixels out and hands them to main.
        internal static void Complete(RenderWindow window, ulong frameDone)
        {
            Vk vk = Renderer.vk;
            Silk.NET.Vulkan.Semaphore timeline = window.timelineSemaphore;
            SemaphoreWaitInfo wait = new SemaphoreWaitInfo()
            {
                SType = StructureType.SemaphoreWaitInfo,
                SemaphoreCount = 1,
                PSemaphores = &timeline,
                PValues = &frameDone
            };
            vk.WaitSemaphores(Renderer.logicalDevice, ref wait, ulong.MaxValue);

            byte[] pixels = new byte[(int)(window.readbackWidth * window.readbackHeight * 4)];
            void* mapped;
            vk.MapMemory(Renderer.logicalDevice, _memory, 0, Vk.WholeSize, 0, &mapped);
            new ReadOnlySpan<byte>(mapped, pixels.Length).CopyTo(pixels);
            vk.UnmapMemory(Renderer.logicalDevice, _memory);

            fixed (CommandBuffer* ptr = &_commands)
                vk.FreeCommandBuffers(Renderer.logicalDevice, Renderer.compositeCommandPool, 1, ptr);
            vk.DestroyBuffer(Renderer.logicalDevice, _buffer, null);
            vk.FreeMemory(Renderer.logicalDevice, _memory, null);

            window.readbackRequested = false;
            window.readbackPixels = pixels;
        }
    }
}
