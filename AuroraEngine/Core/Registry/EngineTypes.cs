using System.Reflection;

namespace ArctisAurora.Core.Registry
{
    // Reflection over the assemblies that can carry engine attributes, scanned once.
    public static class EngineTypes
    {
        private static Type[]? all;
        private static (MethodInfo method, A_XSDActionDependencyAttribute attr)[]? actions;

        // Every type in the engine and in each loaded assembly that references it, in load order.
        public static Type[] All => all ??= CollectTypes();

        // Every static method tagged [A_XSDActionDependency].
        public static (MethodInfo method, A_XSDActionDependencyAttribute attr)[] Actions => actions ??= CollectActions();

        private static Type[] CollectTypes()
        {
            Assembly engine = typeof(EngineTypes).Assembly;
            string engineName = engine.GetName().Name!;
            Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();
            Span<bool> carries = stackalloc bool[loaded.Length];

            int count = 0;
            for (int i = 0; i < loaded.Length; i++)
            {
                carries[i] = loaded[i] == engine || DoesReference(loaded[i], engineName);
                if (carries[i]) count += loaded[i].GetTypes().Length;
            }

            Type[] types = new Type[count];
            int at = 0;
            for (int i = 0; i < loaded.Length; i++)
            {
                if (!carries[i]) continue;
                Type[] own = loaded[i].GetTypes();
                own.CopyTo(types, at);
                at += own.Length;
            }
            return types;
        }

        private static (MethodInfo method, A_XSDActionDependencyAttribute attr)[] CollectActions()
        {
            Type[] types = All;
            List<(MethodInfo method, A_XSDActionDependencyAttribute attr)> tagged = new List<(MethodInfo method, A_XSDActionDependencyAttribute attr)>(1024);

            for (int i = 0; i < types.Length; i++)
                foreach (MethodInfo method in types[i].GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    A_XSDActionDependencyAttribute? attr = method.GetCustomAttribute<A_XSDActionDependencyAttribute>();
                    if (attr != null) tagged.Add((method, attr));
                }

            return tagged.ToArray();
        }

        private static bool DoesReference(Assembly assembly, string name)
        {
            AssemblyName[] references = assembly.GetReferencedAssemblies();
            for (int i = 0; i < references.Length; i++)
                if (references[i].Name == name) return true;
            return false;
        }
    }
}
