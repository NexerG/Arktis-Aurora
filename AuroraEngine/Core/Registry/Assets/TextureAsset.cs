using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Filing.Serialization;
using ArctisAurora.Core.Registry;
using ArctisAurora.EngineWork.Registry;
using ArctisAurora.EngineWork.Rendering;
using ArctisAurora.EngineWork.Rendering.Helpers;
using ArctisAurora.Core.Imaging;
using Silk.NET.Vulkan;
using System.Buffers;
using Image = Arctis.Imaging.Image;

namespace ArctisAurora.Core.Registry.Assets
{
    [A_XSDType("TextureAsset", "AssetRegistry")]
    public class TextureAsset : AbstractAsset
    {
        private static readonly Diagnostics.LogChannel Log = Diagnostics.LogChannel.For("Assets");

        internal Silk.NET.Vulkan.Image _textureImage;
        internal ImageView textureImageView;
        internal DeviceMemory _textureBufferMemory;
        internal Sampler textureSampler;
        internal int width;
        internal int height;

        #region ---- bindless texture table ----
        public static uint MaxTextures { get; internal set; } = 256;

        private static TextureAsset[] _table = Array.Empty<TextureAsset>();
        public static IReadOnlyList<TextureAsset> Table => Volatile.Read(ref _table);

        public static int TableVersion { get; private set; }

        public uint textureIndex { get; private set; }

        private void RegisterInTable()
        {
            if (_table.Length >= MaxTextures)
                throw new Exception($"Texture table is full ({MaxTextures}). Raise TextureAsset.MaxTextures.");

            textureIndex = (uint)_table.Length;
            TextureAsset[] grown = new TextureAsset[_table.Length + 1];
            Array.Copy(_table, grown, _table.Length);
            grown[^1] = this;
            Volatile.Write(ref _table, grown);
            TableVersion++;
        }
        #endregion

        #region ---- pictures by file ----
        private static readonly Dictionary<string, TextureAsset> _byFile = new Dictionary<string, TextureAsset>(StringComparer.OrdinalIgnoreCase);

        // One mipmapped colour texture per file; null when it cannot load or the table is full.
        public static TextureAsset? ForFile(string path)
        {
            string full = Path.GetFullPath(path);
            if (_byFile.TryGetValue(full, out TextureAsset? cached)) return cached;

            if (_table.Length >= MaxTextures)
            {
                Log.Warn($"texture table is full ({MaxTextures}); '{full}' is not drawn.");
                return null;
            }

            try
            {
                TextureAsset texture = new TextureAsset();
                texture.LoadBytes(VaultFiles.ReadBytes(full), Format.R8G8B8A8Unorm, true, true);
                _byFile.Add(full, texture);
                return texture;
            }
            catch (Exception e)
            {
                Log.Warn($"picture '{full}' failed to load: {e.Message}");
                return null;
            }
        }
        // Each pixel row's leftmost and rightmost opaque pixel as fractions of the width; an empty row has
        // left > right. Taken at load by ForFile, since the pixels are freed after upload; null otherwise.
        private (float left, float right)[]? _opaqueRows;

        public (float left, float right)[]? OpaqueRows() => _opaqueRows;
        #endregion

        public TextureAsset() { }

        public override void Load(string name, string source)
        {
            LoadFile(VirtualFileSystem.ResolveFile(source));
        }

        // Uploads an image and claims a bindless table slot, without taking a registry name.
        // Non-colour images (distance fields) must pass a UNORM format.
        public void LoadFile(string path, Format format = Format.R8G8B8A8Srgb, bool mipmaps = false)
        {
            if (!DataFiles.Exists(path))
                throw new Exception("Texture not found: " + path);

            using Stream stream = DataFiles.Open(path);
            int length = (int)stream.Length;
            byte[] bytes = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                stream.ReadExactly(bytes, 0, length);
                LoadBytes(bytes.AsSpan(0, length), format, mipmaps, false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(bytes);
            }
        }

        // Uploads a decoded image and claims a table slot.
        public void LoadImage(Image source, Format format = Format.R8G8B8A8Srgb, bool mipmaps = false)
        {
            TextureLoader.UploadImage(source, format, mipmaps, false, ref _textureImage, ref _textureBufferMemory, out width, out height, out _opaqueRows);
            AVulkanBufferHandler.CreateImageView(Renderer.vk, ref Renderer.logicalDevice, ref _textureImage, ref textureImageView, format, ImageAspectFlags.ColorBit);
            RegisterInTable();
        }

        // Uploads an image file's bytes and claims a table slot.
        private void LoadBytes(ReadOnlySpan<byte> file, Format format, bool mipmaps, bool opaqueRows)
        {
            TextureLoader.UploadFile(file, format, mipmaps, opaqueRows, ref _textureImage, ref _textureBufferMemory, out width, out height, out _opaqueRows);
            AVulkanBufferHandler.CreateImageView(Renderer.vk, ref Renderer.logicalDevice, ref _textureImage, ref textureImageView, format, ImageAspectFlags.ColorBit);
            RegisterInTable();
        }
    }
}
