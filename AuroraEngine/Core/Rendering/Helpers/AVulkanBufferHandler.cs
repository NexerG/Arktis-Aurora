using System.Numerics;
using Silk.NET.Vulkan;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Buffer = Silk.NET.Vulkan.Buffer;
using Format = Silk.NET.Vulkan.Format;
using Image = SixLabors.ImageSharp.Image;
using ImageLayout = Silk.NET.Vulkan.ImageLayout;
using Queue = Silk.NET.Vulkan.Queue;

namespace ArctisAurora.EngineWork.Rendering.Helpers
{
    public struct UBO
    {
        public Matrix4x4 _view;
        public Matrix4x4 _projection;
        //public Matrix4x4 _lightProjection;
        //public Matrix4x4 _lightView;
        //public Vector3 _camPos;
    }

    internal static unsafe class AVulkanBufferHandler
    {
        private static readonly Core.Diagnostics.LogChannel Log = Core.Diagnostics.LogChannel.For("Renderer");

        internal static void CreateTextureBuffer(ref Silk.NET.Vulkan.Image _textureImage, ref DeviceMemory _textureBufferMemory, string pathToImage, Format imageFormat, ref Queue queue, ref CommandPool cPool)
        {
            using var _image = Image.Load<Rgba32>(pathToImage);
            ulong _imageSize = (ulong)(_image.Width * _image.Height * _image.PixelType.BitsPerPixel / 8);

            Buffer _stagingBuffer = default;
            DeviceMemory _stagingBufferMemory = default;
            CreateBuffer(_imageSize, ref _stagingBuffer, ref _stagingBufferMemory, BufferUsageFlags.TransferSrcBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

            void* _data;
            Renderer.vk.MapMemory(Renderer.logicalDevice, _stagingBufferMemory, 0, _imageSize, 0, &_data);
            _image.CopyPixelDataTo(new Span<byte>(_data, (int)_imageSize));
            Renderer.vk.UnmapMemory(Renderer.logicalDevice, _stagingBufferMemory);

            CreateImage((uint)_image.Width, (uint)_image.Height, imageFormat, ImageTiling.Optimal, ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit, MemoryPropertyFlags.DeviceLocalBit, ref _textureImage, ref _textureBufferMemory);

            BufferImageCopy[] regions =
            {
                new BufferImageCopy
                {
                    ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                    ImageExtent = new Extent3D((uint)_image.Width, (uint)_image.Height, 1)
                }
            };
            UploadTexture(_textureImage, _stagingBuffer, regions, ref queue, ref cPool);

            Renderer.vk.DestroyBuffer(Renderer.logicalDevice, _stagingBuffer, null);
            Renderer.vk.FreeMemory(Renderer.logicalDevice, _stagingBufferMemory, null);
        }

        internal static void CreateTextureBuffer(ref Silk.NET.Vulkan.Image _textureImage, ref DeviceMemory _textureBufferMemory, ref Image<Rgba32> image, Format imageFormat, ref Queue queue, ref CommandPool cPool, bool mipmaps = false)
        {
            using var _image = image;
            ClampToDevice(_image);

            // mip chain, each level box-filtered from the one above in linear light
            Image<Rgba32>[] levels = new Image<Rgba32>[mipmaps ? BitOperations.Log2((uint)Math.Max(_image.Width, _image.Height)) + 1 : 1];
            levels[0] = _image;
            for (int level = 1; level < levels.Length; level++)
            {
                Size half = new Size(Math.Max(1, levels[level - 1].Width / 2), Math.Max(1, levels[level - 1].Height / 2));
                levels[level] = levels[level - 1].Clone(c => c.Resize(new ResizeOptions
                {
                    Size = half,
                    Mode = ResizeMode.Stretch,
                    Sampler = KnownResamplers.Box,
                    Compand = true
                }));
            }

            int bytesPerPixel = _image.PixelType.BitsPerPixel / 8;
            BufferImageCopy[] regions = new BufferImageCopy[levels.Length];
            ulong _imageSize = 0;
            for (int i = 0; i < levels.Length; i++)
            {
                regions[i] = new BufferImageCopy
                {
                    BufferOffset = _imageSize,
                    ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, (uint)i, 0, 1),
                    ImageExtent = new Extent3D((uint)levels[i].Width, (uint)levels[i].Height, 1)
                };
                _imageSize += (ulong)(levels[i].Width * levels[i].Height * bytesPerPixel);
            }

            Buffer _stagingBuffer = default;
            DeviceMemory _stagingBufferMemory = default;
            CreateBuffer(_imageSize, ref _stagingBuffer, ref _stagingBufferMemory, BufferUsageFlags.TransferSrcBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

            void* _data;
            Renderer.vk.MapMemory(Renderer.logicalDevice, _stagingBufferMemory, 0, _imageSize, 0, &_data);
            for (int i = 0; i < levels.Length; i++)
            {
                int levelSize = levels[i].Width * levels[i].Height * bytesPerPixel;
                levels[i].CopyPixelDataTo(new Span<byte>((byte*)_data + regions[i].BufferOffset, levelSize));
            }
            Renderer.vk.UnmapMemory(Renderer.logicalDevice, _stagingBufferMemory);

            CreateImage((uint)_image.Width, (uint)_image.Height, imageFormat, ImageTiling.Optimal, ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit, MemoryPropertyFlags.DeviceLocalBit, ref _textureImage, ref _textureBufferMemory, (uint)levels.Length);

            UploadTexture(_textureImage, _stagingBuffer, regions, ref queue, ref cPool);

            for (int i = 1; i < levels.Length; i++)
                levels[i].Dispose();

            Renderer.vk.DestroyBuffer(Renderer.logicalDevice, _stagingBuffer, null);
            Renderer.vk.FreeMemory(Renderer.logicalDevice, _stagingBufferMemory, null);
        }

        // Shrinks an image past the device's 2D limit, keeping its aspect.
        private static void ClampToDevice(Image<Rgba32> image)
        {
            Renderer.vk.GetPhysicalDeviceProperties(Renderer.gpu, out PhysicalDeviceProperties props);
            uint limit = props.Limits.MaxImageDimension2D;
            if (image.Width <= limit && image.Height <= limit) return;

            float scale = (float)limit / Math.Max(image.Width, image.Height);
            int width = Math.Max(1, (int)(image.Width * scale));
            int height = Math.Max(1, (int)(image.Height * scale));
            Log.Warn($"image {image.Width}x{image.Height} exceeds the device limit {limit}; uploading at {width}x{height}.");
            image.Mutate(c => c.Resize(width, height));
        }

        // Copies staging into a fresh image and releases it to the graphics family, in one submit.
        private static void UploadTexture(Silk.NET.Vulkan.Image _image, Buffer _staging, BufferImageCopy[] regions, ref Queue queue, ref CommandPool cPool)
        {
            uint transferFamily = (uint)Renderer.queueAllocator.GetFamilyIndex(QueueFlags.TransferBit);
            uint graphicsFamily = (uint)Renderer.queueAllocator.GetFamilyIndex(QueueFlags.GraphicsBit);
            bool handOff = transferFamily != graphicsFamily;

            lock (Renderer.transferCommandLock)
            {
                CommandBuffer _commandBuffer = BeginSingleTimeCommands(ref cPool);

                ImageMemoryBarrier _toTransfer = new()
                {
                    SType = StructureType.ImageMemoryBarrier,
                    OldLayout = ImageLayout.Undefined,
                    NewLayout = ImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    SrcAccessMask = 0,
                    DstAccessMask = AccessFlags.TransferWriteBit,
                    Image = _image,
                    SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, Vk.RemainingMipLevels, 0, 1)
                };
                Renderer.vk!.CmdPipelineBarrier(_commandBuffer, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, ref _toTransfer);

                fixed (BufferImageCopy* regionsPtr = regions)
                    Renderer.vk!.CmdCopyBufferToImage(_commandBuffer, _staging, _image, ImageLayout.TransferDstOptimal, (uint)regions.Length, regionsPtr);

                ImageMemoryBarrier _toShader = new()
                {
                    SType = StructureType.ImageMemoryBarrier,
                    OldLayout = ImageLayout.TransferDstOptimal,
                    NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                    SrcQueueFamilyIndex = handOff ? transferFamily : Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = handOff ? graphicsFamily : Vk.QueueFamilyIgnored,
                    SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = handOff ? 0 : AccessFlags.ShaderReadBit,
                    Image = _image,
                    SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, Vk.RemainingMipLevels, 0, 1)
                };
                PipelineStageFlags dstStage = handOff ? PipelineStageFlags.BottomOfPipeBit : PipelineStageFlags.FragmentShaderBit;
                Renderer.vk!.CmdPipelineBarrier(_commandBuffer, PipelineStageFlags.TransferBit, dstStage, 0, 0, null, 0, null, 1, ref _toShader);

                EndSingleTimeCommands(ref _commandBuffer, ref queue, ref cPool);
            }

            if (handOff)
                Renderer.QueueAcquire(_image);
        }

        internal static void CreateImage(uint _width, uint _height, Format _format, ImageTiling _tiling, ImageUsageFlags _usage, MemoryPropertyFlags _properties, ref Silk.NET.Vulkan.Image _im, ref DeviceMemory _devMemory, uint _mipLevels = 1)
        {
            ImageCreateInfo _imageInfo = new()
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Extent =
                {
                    Width = _width,
                    Height = _height,
                    Depth = 1,
                },
                MipLevels = _mipLevels,
                ArrayLayers = 1,
                Format = _format,
                Tiling = _tiling,
                InitialLayout = ImageLayout.Undefined,
                Usage = _usage,
                Samples = SampleCountFlags.Count1Bit,
                SharingMode = SharingMode.Exclusive,
            };

            fixed (Silk.NET.Vulkan.Image* imagePtr = &_im)
            {
                if (Renderer.vk!.CreateImage(Renderer.logicalDevice, ref _imageInfo, null, imagePtr) != Result.Success)
                {
                    throw new Exception("failed to create image!");
                }
            }

            Renderer.vk.GetImageMemoryRequirements(Renderer.logicalDevice, _im, out MemoryRequirements _memReqs);

            MemoryAllocateInfo allocInfo = new()
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = _memReqs.Size,
                MemoryTypeIndex = FindMemoryType(_memReqs.MemoryTypeBits, _properties),
            };

            fixed (DeviceMemory* imageMemoryPtr = &_devMemory)
            {
                if (Renderer.vk!.AllocateMemory(Renderer.logicalDevice, ref allocInfo, null, imageMemoryPtr) != Result.Success)
                {
                    throw new Exception("failed to allocate image memory!");
                }
            }

            Renderer.vk!.BindImageMemory(Renderer.logicalDevice, _im, _devMemory, 0);
        }
        internal static void CreateImage(Vk vk, ref Device logicalDevice, PhysicalDevice gpu, uint _width, uint _height, Format _format, ImageTiling _tiling, ImageUsageFlags _usage, MemoryPropertyFlags _properties, ref Silk.NET.Vulkan.Image _im, ref DeviceMemory _devMemory)
        {
            ImageCreateInfo _imageInfo = new()
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Extent =
                {
                    Width = _width,
                    Height = _height,
                    Depth = 1,
                },
                MipLevels = 1,
                ArrayLayers = 1,
                Format = _format,
                Tiling = _tiling,
                InitialLayout = ImageLayout.Undefined,
                Usage = _usage,
                Samples = SampleCountFlags.Count1Bit,
                SharingMode = SharingMode.Exclusive,
            };

            fixed (Silk.NET.Vulkan.Image* imagePtr = &_im)
            {
                if (vk!.CreateImage(logicalDevice, ref _imageInfo, null, imagePtr) != Result.Success)
                {
                    throw new Exception("failed to create image!");
                }
            }

            vk.GetImageMemoryRequirements(logicalDevice, _im, out MemoryRequirements _memReqs);

            MemoryAllocateInfo allocInfo = new()
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = _memReqs.Size,
                MemoryTypeIndex = FindMemoryType(vk, gpu, _memReqs.MemoryTypeBits, _properties),
            };

            fixed (DeviceMemory* imageMemoryPtr = &_devMemory)
            {
                if (vk!.AllocateMemory(logicalDevice, ref allocInfo, null, imageMemoryPtr) != Result.Success)
                {
                    throw new Exception("failed to allocate image memory!");
                }
            }

            vk.BindImageMemory(logicalDevice, _im, _devMemory, 0);
        }

        internal static void CreateImageView(Vk vk, ref Device logicalDevice, ref Silk.NET.Vulkan.Image _textureImage, ref ImageView _imageView, Format imageFormat, ImageAspectFlags aspectFlags)
        {
            ImageViewCreateInfo _createInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _textureImage,
                Format = imageFormat,
                ViewType = ImageViewType.Type2D
            };

            _createInfo.SubresourceRange.AspectMask = aspectFlags;
            _createInfo.SubresourceRange.BaseMipLevel = 0;
            _createInfo.SubresourceRange.LevelCount = Vk.RemainingMipLevels;
            _createInfo.SubresourceRange.BaseArrayLayer = 0;
            _createInfo.SubresourceRange.LayerCount = 1;

            if (vk!.CreateImageView(logicalDevice, ref _createInfo, null, out _imageView) != Result.Success)
            {
                throw new Exception("failed to create texture image view!");
            }
        }





        internal static BufferUsageFlags vertexBufferFlags = BufferUsageFlags.VertexBufferBit;
        internal static BufferUsageFlags indexBufferFlags =  BufferUsageFlags.IndexBufferBit;
        internal static BufferUsageFlags raytracingBufferFlags = BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr;
        internal static BufferUsageFlags storageBufferFlags = BufferUsageFlags.StorageBufferBit;
        private static BufferUsageFlags defaultBufferFlags = BufferUsageFlags.TransferDstBit;

        private static BufferUsageFlags defaultStagingBufferFlags = BufferUsageFlags.TransferSrcBit;
        private static MemoryPropertyFlags defaultStagingMemoryFlags = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;

        // memory prefabs by write frequency: uploaded once, or rewritten every frame
        private static MemoryPropertyFlags staticDeviceMemoryFlags = MemoryPropertyFlags.DeviceLocalBit;
        private static MemoryPropertyFlags streamingMemoryFlags = MemoryPropertyFlags.DeviceLocalBit | MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
        private static MemoryPropertyFlags streamingMemoryFlagsFallback = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;


        internal static void CreateBuffer<T>(ref T[] data, ref Queue queue, ref CommandPool cPool, ref Buffer buffer, ref DeviceMemory memory, BufferUsageFlags usageFlags) where T : unmanaged
        {
            ulong bufferSize = (ulong)(sizeof(T) * data.Length);

            Buffer _stagingBuffer = default;
            DeviceMemory _stagingBufferMemory = default;
            CreateBuffer(bufferSize, ref _stagingBuffer, ref _stagingBufferMemory, defaultStagingBufferFlags, defaultStagingMemoryFlags);

            void* _dataPtr;
            Renderer.vk.MapMemory(Renderer.logicalDevice, _stagingBufferMemory, 0, bufferSize, 0, &_dataPtr);
            data.AsSpan().CopyTo(new Span<T>(_dataPtr, data.Length));
            Renderer.vk.UnmapMemory(Renderer.logicalDevice, _stagingBufferMemory);

            CreateBuffer(bufferSize, ref buffer, ref memory, defaultBufferFlags | usageFlags, staticDeviceMemoryFlags);

            CopyBuffer(ref _stagingBuffer, ref buffer, bufferSize, ref queue, ref cPool);
            Renderer.vk.DestroyBuffer(Renderer.logicalDevice, _stagingBuffer, null);
            Renderer.vk.FreeMemory(Renderer.logicalDevice, _stagingBufferMemory, null);
        }

        internal static void CreateBuffer<T>(ref T data, ref Queue queue, ref CommandPool cPool, ref Buffer buffer, ref DeviceMemory memory, BufferUsageFlags usageFlags) where T : unmanaged
        {
            ulong bufferSize = (ulong)(sizeof(T));

            Buffer _stagingBuffer = default;
            DeviceMemory _stagingBufferMemory = default;
            CreateBuffer(bufferSize, ref _stagingBuffer, ref _stagingBufferMemory, defaultStagingBufferFlags, defaultStagingMemoryFlags);

            void* _dataPtr;
            Renderer.vk.MapMemory(Renderer.logicalDevice, _stagingBufferMemory, 0, bufferSize, 0, &_dataPtr);
            new Span<T>(_dataPtr, 1)[0] = data;
            Renderer.vk.UnmapMemory(Renderer.logicalDevice, _stagingBufferMemory);

            CreateBuffer(bufferSize, ref buffer, ref memory, defaultBufferFlags | usageFlags, staticDeviceMemoryFlags);

            CopyBuffer(ref _stagingBuffer, ref buffer, bufferSize, ref queue, ref cPool);
            Renderer.vk.DestroyBuffer(Renderer.logicalDevice, _stagingBuffer, null);
            Renderer.vk.FreeMemory(Renderer.logicalDevice, _stagingBufferMemory, null);
        }

        internal static void UpdateBuffer<T>(ref T[] data, ref Queue queue, ref CommandPool cPool, ref Buffer buffer, ref DeviceMemory memory, BufferUsageFlags usageFlags) where T : unmanaged
        {
            ulong bufferSize = (ulong)(sizeof(T) * data.Length);

            Buffer _stagingBuffer = default;
            DeviceMemory _stagingBufferMemory = default;
            CreateBuffer(bufferSize, ref _stagingBuffer, ref _stagingBufferMemory, defaultStagingBufferFlags, defaultStagingMemoryFlags);

            void* _dataPtr;
            Renderer.vk.MapMemory(Renderer.logicalDevice, _stagingBufferMemory, 0, bufferSize, 0, &_dataPtr);
            data.AsSpan().CopyTo(new Span<T>(_dataPtr, data.Length));
            Renderer.vk.UnmapMemory(Renderer.logicalDevice, _stagingBufferMemory);

            CopyBuffer(ref _stagingBuffer, ref buffer, bufferSize, ref queue, ref cPool);
            Renderer.vk.DestroyBuffer(Renderer.logicalDevice, _stagingBuffer, null);
            Renderer.vk.FreeMemory(Renderer.logicalDevice, _stagingBufferMemory, null);
        }

        internal static void UpdateBuffer<T>(ref T data, ref Queue queue, ref CommandPool cPool, ref Buffer buffer, ref DeviceMemory memory, BufferUsageFlags usageFlags) where T : unmanaged
        {
            ulong bufferSize = (ulong)(sizeof(T));

            Buffer _stagingBuffer = default;
            DeviceMemory _stagingBufferMemory = default;
            CreateBuffer(bufferSize, ref _stagingBuffer, ref _stagingBufferMemory, defaultStagingBufferFlags, defaultStagingMemoryFlags);

            void* _dataPtr;
            Renderer.vk.MapMemory(Renderer.logicalDevice, _stagingBufferMemory, 0, bufferSize, 0, &_dataPtr);
            new Span<T>(_dataPtr, 1)[0] = data;
            Renderer.vk.UnmapMemory(Renderer.logicalDevice, _stagingBufferMemory);

            CopyBuffer(ref _stagingBuffer, ref buffer, bufferSize, ref queue, ref cPool);
            Renderer.vk.DestroyBuffer(Renderer.logicalDevice, _stagingBuffer, null);
            Renderer.vk.FreeMemory(Renderer.logicalDevice, _stagingBufferMemory, null);
        }

        // Persistently mapped buffer for data rewritten every frame.
        internal static void CreateMappedBuffer(ulong size, ref Buffer buffer, ref DeviceMemory memory, out nint mapped, BufferUsageFlags usageFlags)
        {
            CreateBuffer(size, ref buffer, ref memory, usageFlags, streamingMemoryFlags, streamingMemoryFlagsFallback);

            void* _ptr;
            Renderer.vk.MapMemory(Renderer.logicalDevice, memory, 0, size, 0, &_ptr);
            mapped = (nint)_ptr;
        }

        // Copies elements [start, start + count) into the same slots of a mapped buffer. Forward writes only:
        // the target may be write-combined, where reading back costs orders of magnitude.
        internal static void WriteMappedRange<T>(nint mapped, T[] data, int start, int count) where T : unmanaged
            => WriteMappedRange(mapped, start, data, start, count);

        // Copies elements [start, start + count) into slots [destination, destination + count).
        internal static void WriteMappedRange<T>(nint mapped, int destination, T[] data, int start, int count) where T : unmanaged
        {
            if (count <= 0) return;
            Span<T> _dst = new Span<T>((void*)(mapped + (nint)(destination * sizeof(T))), count);
            data.AsSpan(start, count).CopyTo(_dst);
        }

        internal static void CreateBuffer(ulong _size, ref Buffer _buffer, ref DeviceMemory _bufferMemory, BufferUsageFlags _usage, MemoryPropertyFlags _properties)
            => CreateBuffer(_size, ref _buffer, ref _bufferMemory, _usage, _properties, _properties);

        internal static void CreateBuffer(ulong _size, ref Buffer _buffer, ref DeviceMemory _bufferMemory, BufferUsageFlags _usage, MemoryPropertyFlags _preferred, MemoryPropertyFlags _required)
        {
            BufferCreateInfo _bufferCreateInfo = new BufferCreateInfo()
            {
                SType = StructureType.BufferCreateInfo,
                Size = _size,
                Usage = _usage,
                SharingMode = SharingMode.Exclusive
            };

            fixed (Buffer* _bufferPtr = &_buffer)
            {
                if (Renderer.vk.CreateBuffer(Renderer.logicalDevice, ref _bufferCreateInfo, null, _bufferPtr) != Result.Success)
                {
                    throw new Exception("Failed to create a buffer");
                }
            }
            MemoryRequirements _memReqs = new MemoryRequirements();
            Renderer.vk.GetBufferMemoryRequirements(Renderer.logicalDevice, _buffer, out _memReqs);

            MemoryAllocateInfo _allocateInfo = new MemoryAllocateInfo()
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = _memReqs.Size,
                MemoryTypeIndex = FindMemoryType(_memReqs.MemoryTypeBits, _preferred, _required)
            };

            fixed (DeviceMemory* _bufferMemoryPtr = &_bufferMemory)
            {
                if (Renderer.vk.AllocateMemory(Renderer.logicalDevice, ref _allocateInfo, null, _bufferMemoryPtr) != Result.Success)
                {
                    throw new Exception("Failed to allocate buffer memory");
                }
            }

            Renderer.vk.BindBufferMemory(Renderer.logicalDevice, _buffer, _bufferMemory, 0);
        }

        // Copies staging into a device buffer and releases it to the graphics family, in one submit.
        private static void CopyBuffer(ref Buffer _sourceBuffer, ref Buffer _dstBuffer, ulong bufferSize, ref Queue queue, ref CommandPool commandPool)
        {
            uint transferFamily = (uint)Renderer.queueAllocator.GetFamilyIndex(QueueFlags.TransferBit);
            uint graphicsFamily = (uint)Renderer.queueAllocator.GetFamilyIndex(QueueFlags.GraphicsBit);
            bool handOff = transferFamily != graphicsFamily;

            lock (Renderer.transferCommandLock)
            {
                CommandBufferAllocateInfo _allocInfo = new CommandBufferAllocateInfo()
                {
                    SType = StructureType.CommandBufferAllocateInfo,
                    Level = CommandBufferLevel.Primary,
                    CommandPool = commandPool,
                    CommandBufferCount = 1
                };
                CommandBuffer _localCommandBuffer;
                Renderer.vk.AllocateCommandBuffers(Renderer.logicalDevice, ref _allocInfo, out _localCommandBuffer);

                CommandBufferBeginInfo _cBBeginInfo = new CommandBufferBeginInfo()
                {
                    SType = StructureType.CommandBufferBeginInfo,
                    Flags = CommandBufferUsageFlags.OneTimeSubmitBit
                };
                Renderer.vk.BeginCommandBuffer(_localCommandBuffer, ref _cBBeginInfo);

                BufferCopy _copyRegion = new BufferCopy()
                {
                    Size = bufferSize
                };
                Renderer.vk.CmdCopyBuffer(_localCommandBuffer, _sourceBuffer, _dstBuffer, 1, ref _copyRegion);

                BufferMemoryBarrier _toRead = new()
                {
                    SType = StructureType.BufferMemoryBarrier,
                    SrcQueueFamilyIndex = handOff ? transferFamily : Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = handOff ? graphicsFamily : Vk.QueueFamilyIgnored,
                    SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = handOff ? 0 : Renderer.bufferReadAccess,
                    Buffer = _dstBuffer,
                    Offset = 0,
                    Size = Vk.WholeSize
                };
                PipelineStageFlags dstStage = handOff ? PipelineStageFlags.BottomOfPipeBit : Renderer.bufferReadStages;
                Renderer.vk.CmdPipelineBarrier(_localCommandBuffer, PipelineStageFlags.TransferBit, dstStage, 0, 0, null, 1, ref _toRead, 0, null);

                Renderer.vk.EndCommandBuffer(_localCommandBuffer);

                SubmitInfo _subInfo = new SubmitInfo()
                {
                    SType = StructureType.SubmitInfo,
                    CommandBufferCount = 1,
                    PCommandBuffers = &_localCommandBuffer
                };
                
                Result submitted = Renderer.vk.QueueSubmit(queue, 1, ref _subInfo, default);
                Result waited = submitted == Result.Success ? Renderer.vk.QueueWaitIdle(queue) : submitted;
                if (submitted != Result.Success || waited != Result.Success)
                {
                    Log.Error($"'copy buffer' submit failed — submit {submitted}, wait {waited}");
                    throw new Exception("failed to submit 'copy buffer' commands");
                }
                Renderer.vk.FreeCommandBuffers(Renderer.logicalDevice, commandPool, 1, ref _localCommandBuffer);
            }

            if (handOff)
                Renderer.QueueAcquire(_dstBuffer);
        }

        internal static uint FindMemoryType(uint _typeFilter, MemoryPropertyFlags _preferred, MemoryPropertyFlags _required)
        {
            PhysicalDeviceMemoryProperties _memProperties;
            Renderer.vk.GetPhysicalDeviceMemoryProperties(Renderer.gpu, out _memProperties);

            for (int i = 0; i < _memProperties.MemoryTypeCount; i++)
            {
                if ((_typeFilter & 1 << i) != 0 && (_memProperties.MemoryTypes[i].PropertyFlags & _preferred) == _preferred)
                {
                    return (uint)i;
                }
            }
            return FindMemoryType(_typeFilter, _required);
        }

        internal static uint FindMemoryType(uint _typeFilter, MemoryPropertyFlags _properties)
        {
            PhysicalDeviceMemoryProperties _memProperties;
            Renderer.vk.GetPhysicalDeviceMemoryProperties(Renderer.gpu, out _memProperties);

            for (int i = 0; i < _memProperties.MemoryTypeCount; i++)
            {
                if ((_typeFilter & 1 << i) != 0 && (_memProperties.MemoryTypes[i].PropertyFlags & _properties) == _properties)
                {
                    return (uint)i;
                }
            }
            throw new Exception("Failed to find suitable memory type");
        }

        internal static uint FindMemoryType(Vk vk, PhysicalDevice gpu, uint _typeFilter, MemoryPropertyFlags _properties)
        {
            PhysicalDeviceMemoryProperties _memProperties;
            vk.GetPhysicalDeviceMemoryProperties(gpu, out _memProperties);

            for (int i = 0; i < _memProperties.MemoryTypeCount; i++)
            {
                if ((_typeFilter & 1 << i) != 0 && (_memProperties.MemoryTypes[i].PropertyFlags & _properties) == _properties)
                {
                    return (uint)i;
                }
            }
            throw new Exception("Failed to find suitable memory type");
        }

        internal static CommandBuffer BeginSingleTimeCommands(ref CommandPool cPool)
        {
            CommandBufferAllocateInfo _allocInfo = new CommandBufferAllocateInfo()
            {
                SType = StructureType.CommandBufferAllocateInfo,
                Level = CommandBufferLevel.Primary,
                CommandPool = cPool,
                CommandBufferCount = 1,
            };

            Renderer.vk!.AllocateCommandBuffers(Renderer.logicalDevice, ref _allocInfo, out CommandBuffer _commandBuffer);

            CommandBufferBeginInfo beginInfo = new CommandBufferBeginInfo()
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };

            Renderer.vk!.BeginCommandBuffer(_commandBuffer, ref beginInfo);

            return _commandBuffer;
        }

        internal static void EndSingleTimeCommands(ref CommandBuffer commandBuffer, ref Queue queue, ref CommandPool cPool)
        {
            Renderer.vk!.EndCommandBuffer(commandBuffer);

            fixed (CommandBuffer* _cptr = &commandBuffer)
            {
                SubmitInfo submitInfo = new()
                {
                    SType = StructureType.SubmitInfo,
                    CommandBufferCount = 1,
                    PCommandBuffers = _cptr,
                };
                Result submitted = Renderer.vk!.QueueSubmit(queue, 1, ref submitInfo, default);
                Result waited = submitted == Result.Success ? Renderer.vk!.QueueWaitIdle(queue) : submitted;
                if (submitted != Result.Success || waited != Result.Success)
                {
                    Log.Error($"single time commands submit failed — submit {submitted}, wait {waited}");
                    throw new Exception("failed to submit single time commands");
                }

                Renderer.vk!.FreeCommandBuffers(Renderer.logicalDevice, cPool, 1, _cptr);
            }
        }
    }
}