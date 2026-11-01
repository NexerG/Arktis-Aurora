using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Filing.Serialization;
using ArctisAurora.EngineWork;
using ArctisAurora.EngineWork.Registry;
using System.Reflection;
using System.Xml.Linq;

namespace ArctisAurora.Core.Registry
{
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = true)]
    public sealed class A_ActiveContextAttribute : Attribute
    {
        public string name;
        public A_ActiveContextAttribute(string name)
        {
            this.name = name;
        }
    }

    public interface IContext
    {
        public abstract void OnContextAdded(string context);
        public abstract void OnContextRemoved(string context);
    }

    public record ContextEntry(Type valueType, Func<object?> Get, Action<object?> set);

    public record Derivation(string name, Type type);

    [A_XSDType("ActiveContext", "Context")]
    public sealed class Context
    {
        private static readonly Diagnostics.LogChannel Log = Diagnostics.LogChannel.For("Context");

        public static readonly Dictionary<string, object> activeContexts =
            AssetRegistries.GetRegistryByName<string, object>("ActiveContexts");

        // declared contexts computed from another, keyed by the one they follow
        private static readonly Dictionary<string, List<Derivation>> derived =
            new Dictionary<string, List<Derivation>>();

        public static void Register(string name, Type valueType, Func<object?> get, Action<object?> set) =>
            activeContexts[name] = new ContextEntry(valueType, get, set);

        public static T? Get<T>(string name) where T : class =>
            activeContexts.TryGetValue(name, out var entry) ? (entry as ContextEntry)?.Get() as T : null;

        // Raised after a context is set, with its name and new value.
        public static event Action<string, object?>? changed;

        public static void Set(string name, object? value)
        {
            if (activeContexts.TryGetValue(name, out var entry))
            {
                (entry as ContextEntry).set(value);
                changed?.Invoke(name, value);
                Derive(name, value);
            }
        }

        public static void Clear(string name) => Set(name, null);

        // Drops a value out of every context holding it, without deriving from the loss.
        public static void Forget(object value)
        {
            foreach (object entry in activeContexts.Values)
                if (entry is ContextEntry context && ReferenceEquals(context.Get(), value))
                    context.set(null);
        }

        // Recomputes the contexts derived from this one.
        private static void Derive(string source, object? value)
        {
            if (!derived.TryGetValue(source, out List<Derivation> list)) return;

            foreach (Derivation derivation in list)
            {
                object? found = Ancestor(value, derivation.type);
                if (found != null && !ReferenceEquals(Value(derivation.name), found))
                    Set(derivation.name, found);
            }
        }

        // Nearest ancestor assignable to the type, the value itself included.
        private static object? Ancestor(object? value, Type type)
        {
            Entity? entity = value as Entity;
            while (entity != null && !type.IsInstanceOfType(entity))
                entity = entity.parent;

            return entity;
        }

        private static object? Value(string name) =>
            activeContexts.TryGetValue(name, out var entry) ? (entry as ContextEntry)?.Get() : null;

        
        //[A_BootstrapStage(BootstrapStage.PostGPUAPI)]
        [A_XSDActionDependency("Context.LoadContexts", "Bootstrap")]
        internal static bool LoadContexts()
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (Type type in EngineTypes.All)
            {
                foreach (PropertyInfo prop in type.GetProperties(flags))
                {
                    A_ActiveContextAttribute? attr = prop.GetCustomAttribute<A_ActiveContextAttribute>(false);
                    if (attr != null) Register(attr.name, prop.PropertyType, () => prop.GetValue(null), v => prop.SetValue(null, v));
                }
                foreach (FieldInfo field in type.GetFields(flags))
                {
                    A_ActiveContextAttribute? attr = field.GetCustomAttribute<A_ActiveContextAttribute>(false);
                    if (attr != null) Register(attr.name, field.FieldType, () => field.GetValue(null), v => field.SetValue(null, v));
                }
            }

            return true;
        }

        // Contexts authored in Contexts/*.contexts.axml, one file per contributor across every mount,
        // holding their own value rather than binding to a static.
        [A_XSDActionDependency("Context.LoadDeclared", "Bootstrap")]
        internal static bool LoadDeclared()
        {
            foreach (string file in VirtualFileSystem.EnumerateAll("XML/Documents/Contexts", "*.contexts.axml"))
            {
                XElement root = DataFiles.LoadXml(file);
                foreach (XElement element in root.Elements())
                {
                    ContextDefinition definition = new ContextDefinition
                    {
                        name = element.Attribute("Name")?.Value ?? "",
                        type = element.Attribute("Type")?.Value ?? "",
                        from = element.Attribute("From")?.Value ?? ""
                    };

                    Type? valueType = AnyXMLType.FindType(definition.type);
                    if (valueType == null)
                    {
                        Log.Warn($"'{definition.name}' names unknown type '{definition.type}' — skipping.");
                        continue;
                    }

                    object? slot = null;
                    Register(definition.name, valueType, () => slot, v => slot = v);

                    if (string.IsNullOrEmpty(definition.from)) continue;

                    if (!derived.TryGetValue(definition.from, out List<Derivation> list))
                        derived[definition.from] = list = new List<Derivation>();

                    list.Add(new Derivation(definition.name, valueType));
                }
            }

            return true;
        }

        public object GetContextByName(string name)
        {
            activeContexts.TryGetValue(name, out var context);
            return context;
        }
    }
}