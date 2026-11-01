using ArctisAurora.Core.Filing.Serialization;
using ArctisAurora.EngineWork.Registry;
using ArctisAurora.EngineWork.Rendering;
using Silk.NET.Vulkan;
using System.Xml.Linq;

namespace ArctisAurora.Core.Registry.Assets
{
    [A_XSDType("SamplerFilter", "Rendering")]
    public enum SamplerFilter { Nearest, Linear }

    [A_XSDType("SamplerAddressMode", "Rendering")]
    public enum SamplerAddressMode { Repeat, MirroredRepeat, ClampToEdge, ClampToBorder }

    [A_XSDType("SamplerMipmapMode", "Rendering")]
    public enum SamplerMipmapMode { Nearest, Linear }

    [A_XSDType("SamplerAsset", "Rendering")]
    public unsafe class SamplerAsset : AbstractAsset
    {
        private static readonly Diagnostics.LogChannel Log = Diagnostics.LogChannel.For("Assets");

        [A_XSDElementProperty("Name", "Rendering")]
        public string name { get; set; } = "default";

        [A_XSDElementProperty("MagFilter", "Rendering")]
        public SamplerFilter magFilter { get; set; } = SamplerFilter.Nearest;

        [A_XSDElementProperty("MinFilter", "Rendering")]
        public SamplerFilter minFilter { get; set; } = SamplerFilter.Nearest;

        [A_XSDElementProperty("AddressModeU", "Rendering")]
        public SamplerAddressMode addressModeU { get; set; } = SamplerAddressMode.Repeat;

        [A_XSDElementProperty("AddressModeV", "Rendering")]
        public SamplerAddressMode addressModeV { get; set; } = SamplerAddressMode.Repeat;

        [A_XSDElementProperty("AddressModeW", "Rendering")]
        public SamplerAddressMode addressModeW { get; set; } = SamplerAddressMode.Repeat;

        [A_XSDElementProperty("Anisotropy", "Rendering")]
        public bool anisotropyEnable { get; set; } = true;

        [A_XSDElementProperty("MipmapMode", "Rendering")]
        public SamplerMipmapMode mipmapMode { get; set; } = SamplerMipmapMode.Nearest;

        [A_XSDElementProperty("MaxLod", "Rendering")]
        public float maxLod { get; set; } = 0f;

        public Sampler handle { get; private set; }

        public SamplerAsset()
        { }

        public SamplerAsset(string name)
        {
            Dictionary<string, SamplerAsset> d = AssetRegistries.GetRegistryByValueType<string, SamplerAsset>(typeof(SamplerAsset));
            d.Add(name, this);
        }

        // Samplers are not manifest-driven; they load as a set through LoadAll.
        public override void Load(string name, string source)
        {
            throw new NotImplementedException();
        }

        public void LoadDefault()
        {
            Log.Error($"failed to load the default sampler asset — fault handling NOT IMPLEMENTED");
        }

        public void LoadAll(string path)
        {
            Dictionary<string, SamplerAsset> dSamplers = AssetRegistries.GetRegistryByValueType<string, SamplerAsset>(typeof(SamplerAsset));

            string[] files = VirtualFileSystem.EnumerateAll("XML/Documents/Samplers", "*.sampler.axml");
            for (int i = 0; i < files.Length; i++)
            {
                XElement samplerRoot = DataFiles.LoadXml(files[i]);
                XNamespace sns = samplerRoot.GetDefaultNamespace();
                SamplerAsset sa = new SamplerAsset();
                foreach (XAttribute attr in samplerRoot.Attributes())
                {
                    if (attr.Name == "Name") sa.name = attr.Value;
                    if (attr.Name == "MagFilter") sa.magFilter = Enum.Parse<SamplerFilter>(attr.Value);
                    if (attr.Name == "MinFilter") sa.minFilter = Enum.Parse<SamplerFilter>(attr.Value);
                    if (attr.Name == "AddressModeU") sa.addressModeU = Enum.Parse<SamplerAddressMode>(attr.Value);
                    if (attr.Name == "AddressModeV") sa.addressModeV = Enum.Parse<SamplerAddressMode>(attr.Value);
                    if (attr.Name == "AddressModeW") sa.addressModeW = Enum.Parse<SamplerAddressMode>(attr.Value);
                    if (attr.Name == "Anisotropy") sa.anisotropyEnable = bool.Parse(attr.Value);
                    if (attr.Name == "MipmapMode") sa.mipmapMode = Enum.Parse<SamplerMipmapMode>(attr.Value);
                    if (attr.Name == "MaxLod") sa.maxLod = float.Parse(attr.Value, System.Globalization.CultureInfo.InvariantCulture);
                }
                sa.CreateVulkanSampler();
                dSamplers.Add(sa.name, sa);
            }
        }

        public void CreateVulkanSampler()
        {
            Renderer.vk.GetPhysicalDeviceProperties(Renderer.gpu, out PhysicalDeviceProperties props);
            bool anisotropic = anisotropyEnable && Renderer.renderer.features.SamplerAnisotropy == true;

            SamplerCreateInfo info = new SamplerCreateInfo()
            {
                SType = StructureType.SamplerCreateInfo,
                MagFilter = (Filter)magFilter,
                MinFilter = (Filter)minFilter,
                AddressModeU = (Silk.NET.Vulkan.SamplerAddressMode)addressModeU,
                AddressModeV = (Silk.NET.Vulkan.SamplerAddressMode)addressModeV,
                AddressModeW = (Silk.NET.Vulkan.SamplerAddressMode)addressModeW,
                AnisotropyEnable = anisotropic,
                MaxAnisotropy = anisotropic ? props.Limits.MaxSamplerAnisotropy : 1f,
                BorderColor = BorderColor.IntOpaqueBlack,
                UnnormalizedCoordinates = false,
                CompareEnable = false,
                CompareOp = CompareOp.Always,
                MipmapMode = (Silk.NET.Vulkan.SamplerMipmapMode)mipmapMode,
                MaxLod = maxLod,
            };

            Sampler sampler;
            Result r = Renderer.vk.CreateSampler(Renderer.logicalDevice, ref info, null, &sampler);
            if (r != Result.Success)
                throw new Exception("Failed to create sampler: " + r);
            handle = sampler;
        }
    }
}
