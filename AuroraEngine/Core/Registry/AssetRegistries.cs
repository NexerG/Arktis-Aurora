using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Filing.Serialization;
using ArctisAurora.EngineWork.Rendering;
using System.Collections;
using System.Reflection;
using System.Xml.Linq;
using ArctisAurora.Core.Registry.Assets;

namespace ArctisAurora.EngineWork.Registry
{
    [A_XSDType("AssetEntry", "AssetRegistry")]
    public class AssetRegistryEntry
    {
        [A_XSDElementProperty("Name", "AssetRegistry")]
        public string name { get; set; } = string.Empty;
        [A_XSDElementProperty("KeyType", "AssetRegistry")]
        public AnyXMLType keyType { get; set; } = null!;
        [A_XSDElementProperty("ValueType", "AssetRegistry")]
        public AnyXMLType valueType { get; set; } = null!;
    }

    [A_XSDType("Asset", "AssetRegistry")]
    public class AssetManifestEntry
    {
        [A_XSDElementProperty("Type", "AssetRegistry")]
        public string type { get; set; } = string.Empty;
        [A_XSDElementProperty("Name", "AssetRegistry")]
        public string name { get; set; } = string.Empty;
        [A_XSDElementProperty("Source", "AssetRegistry")]
        public string source { get; set; } = string.Empty;
    }

    [A_XSDType("AssetRegistries", "AssetRegistry")]
    public class AssetRegistries : IXMLParser<AssetRegistries>
    {
        private static readonly Core.Diagnostics.LogChannel Log = Core.Diagnostics.LogChannel.For("Assets");

        [A_XSDElementProperty("Dictionary", "AssetRegistry")]
        public static List<AssetRegistryEntry> registries { get; set; } = null!;

        public static Dictionary<Type, object> library = new Dictionary<Type, object>();
        public static Dictionary<string, object> libraryByName = new Dictionary<string, object>();

        private static readonly HashSet<string> reportedMisses = new HashSet<string>();

        public static AssetRegistries assetRegistries = null!;

        public AssetRegistries()
        {
        }

        public static void AddLibraryEntry(string name, object dict, Type t)
        {
            if (library.TryGetValue(t, out var _))
            {
                return;
            }
            library.Add(t, dict);
            libraryByName.Add(name, dict);
        }

        public static Dictionary<TKey, TValue> GetRegistryByValueType<TKey, TValue>(Type t)
        {
            if (library.TryGetValue(t, out var dict))
            {
                return (Dictionary<TKey, TValue>)dict;
            }
            return null;
        }

        public static Dictionary<TKey, TValue> GetRegistryByName<TKey, TValue>(string name)
        {
            libraryByName.TryGetValue(name, out var dict);
            return (Dictionary<TKey, TValue>)dict;
        }

        public static T GetAsset<T>(string name)
        {
            Type t = typeof(T);
            if(library.TryGetValue(t, out var dict))
            {
                var d = (Dictionary<string, T>)dict;
                if(d.TryGetValue(name, out var asset))
                {
                    return asset;
                }
                RequestLoad(t, name);
                if (d.TryGetValue("default", out var fallback))
                {
                    return fallback;
                }
            }
            throw new Exception("Asset not found");
        }

        // Reports a miss once.
        private static void RequestLoad(Type type, string name)
        {
            if (reportedMisses.Add(type.Name + ":" + name))
                Log.Warn($"asset '{name}' ({type.Name}) is not loaded — falling back to default.");
        }

        public static AssetRegistries ParseXML(string xmlName)
        {
            //parse the XML and create the registries
            string path = Paths.Doc(xmlName);
            AssetRegistries registries = new AssetRegistries();
            XElement root = DataFiles.LoadXml(path);
            XNamespace ns = root.GetDefaultNamespace();
            foreach (var dictElem in root.Elements(ns + "Dictionary"))
            {
                string name = dictElem.Attribute("Name").Value;
                string keyTypeStr = dictElem.Attribute("KeyType").Value;
                string valueTypeStr = dictElem.Attribute("ValueType").Value;

                Type keyType, valueType;
                if (AnyXMLType.typeMap.ContainsKey(keyTypeStr))
                {
                    keyType = AnyXMLType.typeMap[keyTypeStr];
                }
                else
                {
                    keyType = AnyXMLType.FindType(keyTypeStr);
                }

                if (AnyXMLType.typeMap.ContainsKey(valueTypeStr))
                {
                    valueType = AnyXMLType.typeMap[valueTypeStr];
                }
                else
                {
                    valueType = AnyXMLType.FindType(valueTypeStr);
                }

                Type dictType = typeof(Dictionary<,>).MakeGenericType(keyType, valueType);
                object dictInstance = Activator.CreateInstance(dictType);

                AddLibraryEntry(name, dictInstance, valueType);
            }
            return registries;
        }
        
        [A_XSDActionDependency("AssetRegistries.RegisterSerializableTypes", "Bootstrap")]
        internal static bool RegisterSerializableTypes()
        {
            Dictionary<uint, Type> serializableTypes = GetRegistryByValueType<uint, Type>(typeof(Type));
            foreach (Type t in EngineTypes.All)
            {
                var attr = t.GetCustomAttribute<@Serializable>();
                if (attr != null)
                {
                    attr.ID = Serializable.GenerateID(t.Name);
                    serializableTypes.Add((uint)attr.ID, t);
                }
            }

            return true;
        }

        [A_XSDActionDependency("AssetRegistries.InstantiateRegistries", "Bootstrap")]
        internal static bool InstantiateRegistries()
        {
            assetRegistries = ParseXML("Registry.registry.axml");
            return true;
        }

        // Assets with no file behind them; everything file-backed comes from the manifest.
        [A_XSDActionDependency("AssetRegistries.PrepareDefaultAssets", "Bootstrap")]
        internal static bool PrepareDefaultAssets()
        {
            Dictionary<string, AVulkanMesh> dMeshes = GetRegistryByValueType<string, AVulkanMesh>(typeof(AVulkanMesh));
            dMeshes.Add("default", AVulkanMesh.LoadDefault());

            SamplerAsset sampler = new SamplerAsset("default");
            sampler.LoadDefault();

            return true;
        }

        [A_XSDActionDependency("AssetRegistries.PreloadAssets", "Bootstrap")]
        internal static bool PreloadAssets()
        {
            Dictionary<(string, string), AssetManifestEntry> entries = new Dictionary<(string, string), AssetManifestEntry>();
            foreach (string file in VirtualFileSystem.EnumerateAll("XML/Assets", "*.assets.axml"))
            {
                XElement root = DataFiles.LoadXml(file);
                XNamespace ns = root.GetDefaultNamespace();
                foreach (XElement element in root.Elements(ns + "Asset"))
                {
                    AssetManifestEntry entry = new AssetManifestEntry();
                    XmlReflection.ApplyAttributes(element, entry);
                    entries.TryAdd((entry.type, entry.name), entry);
                }
            }

            foreach (AssetManifestEntry entry in entries.Values)
            {
                Type valueType = AnyXMLType.FindType(entry.type)
                    ?? throw new Exception($"Asset manifest declares unknown type '{entry.type}'.");
                if (!library.TryGetValue(valueType, out object dict))
                    throw new Exception($"No registry is declared for asset type '{entry.type}'.");

                AbstractAsset asset = (AbstractAsset)Activator.CreateInstance(valueType);
                asset.Load(entry.name, entry.source);
                ((IDictionary)dict).Add(entry.name, asset);
            }

            return true;
        }

        [A_XSDActionDependency("AssetRegistries.PrepareAllAssets", "Bootstrap")]
        internal static bool PrepareAllAssets()
        {
            // samplers
            SamplerAsset sampler = new SamplerAsset();
            sampler.LoadAll(Paths.XMLDOCUMENTS_SAMPLERS);

            return true;
        }
    }
}