using ArctisAurora.Core.Diagnostics;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Filing.Serialization;
using System.Reflection;
using System.Xml.Linq;

namespace ArctisAurora.Core.Data
{
    public readonly record struct DataHandle(ushort PoolId, int StableId, int Version);

    // Static owner of all data pools. Pools are declared in Pools.pools.axml and composed from
    // C#-defined component structs at bootstrap. Lookup by name or by id.
    public static class DataManager
    {
        private static readonly LogChannel Log = LogChannel.For("Data");

        private static readonly List<DataPool> _pools = new();
        private static readonly Dictionary<string, DataPool> _byName = new();

        public static IReadOnlyList<DataPool> Pools => _pools;

        public static DataPool Get(string name) => _byName[name];
        public static DataPool Get(ushort id) => _pools[id];
        public static bool TryGet(string name, out DataPool pool) => _byName.TryGetValue(name, out pool);

        [A_XSDActionDependency("DataManager.ParseXML", "Bootstrap")]
        public static bool ParseXML()
        {
            LoadManifest("Pools.pools.axml");
            return true;
        }

        private static void LoadManifest(string xmlName)
        {
            string path = Paths.Doc(xmlName);
            XElement root = DataFiles.LoadXml(path);
            XNamespace ns = root.GetDefaultNamespace();

            foreach (XElement poolElem in root.Elements(ns + "Pool"))
            {
                string name = poolElem.Attribute("Name").Value;
                int capacity = int.Parse(poolElem.Attribute("Capacity").Value);
                bool ordered = bool.Parse(poolElem.Attribute("Ordered")?.Value ?? "false");
                string sortAction = poolElem.Attribute("SortAction")?.Value;
                PoolGrowthType growth = Enum.Parse<PoolGrowthType>(poolElem.Attribute("Growth")?.Value ?? "Multiplicative");
                int growthValue = int.Parse(poolElem.Attribute("GrowthValue")?.Value ?? "2");
                bool handles = bool.Parse(poolElem.Attribute("Handles")?.Value ?? "true");

                List<Type> componentTypes = new();
                foreach (XElement compElem in poolElem.Elements(ns + "Component"))
                {
                    string typeName = compElem.Attribute("Type").Value;
                    Type? t = ResolveComponent(typeName);
                    if (t == null)
                        throw new Exception($"[DataManager] Pool '{name}' component type '{typeName}' not found.");
                    componentTypes.Add(t);
                }

                DataPool pool = new((ushort)_pools.Count, name, capacity, ordered, growth, growthValue, handles, componentTypes);
                if (!string.IsNullOrEmpty(sortAction))
                    pool.SortProvider = ResolveSortProvider(sortAction);

                Register(pool);
            }
        }

        public static void Register(DataPool pool)
        {
            _pools.Add(pool);
            _byName[pool.Name] = pool;
        }

        // A component type as Pools.pools.axml and Frame.frame.axml name it.
        internal static Type? ResolveComponent(string typeName)
            => AnyXMLType.typeMap.TryGetValue(typeName, out Type? mapped) ? mapped : AnyXMLType.FindType(typeName);

        // Binds a "PoolSort" action name to a static IReadOnlyList<int> Method(DataPool).
        // Returns null if none is registered yet (e.g. before the UI sort provider exists).
        private static Func<DataPool, IReadOnlyList<int>> ResolveSortProvider(string actionName)
        {
            foreach ((MethodInfo method, A_XSDActionDependencyAttribute attr) in EngineTypes.Actions)
            {
                if (attr.Category != "PoolSort" || attr.Name != actionName)
                    continue;
                try
                {
                    return (Func<DataPool, IReadOnlyList<int>>)Delegate.CreateDelegate(
                        typeof(Func<DataPool, IReadOnlyList<int>>), method);
                }
                catch
                {
                    Log.Warn($"sort action '{actionName}' has an incompatible signature.");
                    return null;
                }
            }
            Log.Warn($"sort action '{actionName}' not found — pool will keep insertion order.");
            return null;
        }
    }
}
