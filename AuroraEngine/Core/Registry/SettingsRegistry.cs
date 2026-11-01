using ArctisAurora.Core.Diagnostics;
using ArctisAurora.Core.Filing.Serialization;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Xml.Linq;

namespace ArctisAurora.Core.Registry
{
    // Marker for a settings group; the allowedChildren target the schema generator scans.
    public interface ISettingsGroup { }

    // A group whose stored shape can go stale. Migrate rewrites the stored element in place, before
    // anything on it is read.
    public interface IMigratableSettings : ISettingsGroup
    {
        int version { get; }
        void Migrate(int from, XElement stored);
    }

    [A_XSDType("UserSettings", "Settings", allowedChildren: typeof(ISettingsGroup))]
    public class UserSettingsFile { }

    // One instance per ISettingsGroup, found by reflection, filled from every Data/XML/Settings
    // manifest across the mounts and then from the application's write root.
    public static class SettingsRegistry
    {
        private static readonly LogChannel Log = LogChannel.For("Settings");

        private const string settingsDir = "XML/Settings";
        private const string versionAttribute = "SettingsVersion";

        // groups, the values they held before the write root was applied, and what the write root said
        private static readonly Dictionary<Type, ISettingsGroup> groups = new Dictionary<Type, ISettingsGroup>();
        private static readonly Dictionary<Type, MemberSnapshot> baselines = new Dictionary<Type, MemberSnapshot>();
        private static readonly Dictionary<Type, MemberSnapshot> defaultSnapshots = new Dictionary<Type, MemberSnapshot>();
        private static readonly Dictionary<Type, XElement> stored = new Dictionary<Type, XElement>();

        private static string? writeRoot;

        public static IReadOnlyDictionary<Type, ISettingsGroup> Groups => groups;

        // Folder the application's own settings are read from last and written to.
        public static void SetWriteRoot(string path) => writeRoot = path;

        public static string? WriteRoot => writeRoot;

        public static T Get<T>() where T : ISettingsGroup
        {
            if (groups.TryGetValue(typeof(T), out ISettingsGroup group))
                return (T)group;
            throw new Exception($"{typeof(T).Name} is not a registered settings group.");
        }

        [A_XSDActionDependency("Settings.LoadAll", "Bootstrap")]
        public static bool LoadAll()
        {
            groups.Clear();
            baselines.Clear();
            stored.Clear();

            foreach (Type type in EngineTypes.All)
                if (!type.IsAbstract && !type.IsInterface
                    && typeof(ISettingsGroup).IsAssignableFrom(type)
                    && type.GetCustomAttribute<A_XSDTypeAttribute>(false) != null)
                    groups[type] = (ISettingsGroup)Activator.CreateInstance(type);

            IReadOnlyList<IDataMount> mounts = VirtualFileSystem.Mounts;
            for (int i = mounts.Count - 1; i >= 0; i--)
                foreach (string file in mounts[i].Enumerate(settingsDir, "*.settings.axml").OrderBy(f => f))
                    ApplyFile(file, false);

            foreach (KeyValuePair<Type, ISettingsGroup> group in groups)
                baselines[group.Key] = Snapshot(group.Value);

            if (writeRoot != null && Directory.Exists(writeRoot))
                foreach (string file in Directory.GetFiles(writeRoot, "*.settings.axml").OrderBy(f => f))
                    ApplyFile(file, true);

            // Loading is not a change — the first Apply must not fire everything that has a file.
            foreach (ISettingsGroup group in groups.Values)
                if (group is SettingCategory category)
                    foreach (Setting setting in category.settings)
                        MarkApplied(setting);

            return true;
        }

        // Fires the OnChanged action of every setting whose value moved since the last Apply. The
        // batch is the unit: a settings screen edits freely and pays for one rebuild, not five.
        public static void Apply()
        {
            List<string> fired = new List<string>();

            foreach (ISettingsGroup group in groups.Values)
            {
                if (group is not SettingCategory category) continue;

                foreach (Setting setting in category.settings)
                {
                    if (!MarkApplied(setting)) continue;
                    if (setting.onChanged.Length == 0 || fired.Contains(setting.onChanged)) continue;
                    fired.Add(setting.onChanged);
                }
            }

            foreach (string action in fired)
            {
                if (!Actions().TryGetValue(action, out MethodInfo method))
                {
                    Log.Warn($"OnChanged action '{action}' not found — skipping.");
                    continue;
                }
                Log.Info($"running: {action}");
                method.Invoke(null, null);
            }
        }

        // Fires what moved, then writes the user's file — the one call a settings screen makes.
        public static void Commit()
        {
            Apply();
            SaveAll();
        }

        // Whether any setting moved since the last Apply, without recording it.
        public static bool Pending()
        {
            foreach (ISettingsGroup group in groups.Values)
            {
                if (group is not SettingCategory category) continue;

                foreach (Setting setting in category.settings)
                    foreach (MemberInfo member in Setting.ValueMembers(setting.GetType()))
                        if (!setting.applied.TryGetValue(member, out object previous)
                            || !Equals(XmlReflection.GetMember(member, setting), previous))
                            return true;
            }
            return false;
        }

        // Records what a setting holds now, and reports whether any of it moved since the last Apply.
        private static bool MarkApplied(Setting setting)
        {
            bool moved = false;
            Dictionary<MemberInfo, object> current = new Dictionary<MemberInfo, object>();

            foreach (MemberInfo member in Setting.ValueMembers(setting.GetType()))
            {
                object value = XmlReflection.GetMember(member, setting);
                current[member] = value;
                if (!setting.applied.TryGetValue(member, out object previous) || !Equals(value, previous))
                    moved = true;
            }

            setting.applied = current;
            return moved;
        }

        private static Dictionary<string, MethodInfo> actions = null!;

        private static Dictionary<string, MethodInfo> Actions()
        {
            if (actions != null) return actions;

            actions = new Dictionary<string, MethodInfo>();
            foreach ((MethodInfo method, A_XSDActionDependencyAttribute attribute) in EngineTypes.Actions)
                if (attribute.Category == "Settings")
                    actions[attribute.Name] = method;
            return actions;
        }

        #region ---- read ----
        private static void ApplyFile(string path, bool fromWriteRoot)
        {
            XElement root = DataFiles.LoadXml(path);
            HashSet<(object, FieldInfo)> clearedLists = new HashSet<(object, FieldInfo)>();

            foreach (XElement element in root.Elements())
            {
                Type type = AnyXMLType.FindType(element.Name.LocalName);
                if (type == null || !groups.TryGetValue(type, out ISettingsGroup group))
                {
                    Log.Warn($"'{element.Name.LocalName}' in {Path.GetFileName(path)} is not a settings group — skipping.");
                    continue;
                }

                Migrate(element, group);
                ApplyInto(element, group, clearedLists, fromWriteRoot);
                if (fromWriteRoot) stored[type] = element;
            }
        }

        // Only a stamped element migrates, so a shipped manifest is always read as current.
        private static void Migrate(XElement element, ISettingsGroup group)
        {
            if (group is not IMigratableSettings migratable) return;

            XAttribute stamp = element.Attribute(versionAttribute);
            if (stamp == null || !int.TryParse(stamp.Value, out int from) || from >= migratable.version) return;

            Log.Info($"migrating {group.GetType().Name} from version {from} to {migratable.version}.");
            migratable.Migrate(from, element);
            element.SetAttributeValue(versionAttribute, migratable.version);
        }

        // Settings merge by element name, resolved against the category's own fields rather than the
        // global type map, so a setting name only has to be unique inside its category.
        private static void ApplyCategory(XElement element, SettingCategory category, bool fromWriteRoot)
        {
            foreach (XElement child in element.Elements())
            {
                string name = child.Name.LocalName;

                Setting setting = category.Find(name);
                if (setting == null)
                {
                    Log.Warn($"{category.GetType().Name} declares no setting called '{name}' — skipping.");
                    continue;
                }

                if (fromWriteRoot && setting.scope == SettingScope.App)
                {
                    Log.Warn($"'{name}' on {category.GetType().Name} is App-scoped — ignoring the user's value.");
                    continue;
                }

                SettingScope scope = setting.scope;
                string action = setting.onChanged;

                XmlReflection.ApplyAttributes(child, setting, true);

                // Only the code's own declaration and the mounts decide scope and action — a user
                // file naming a scope would otherwise promote itself out of App.
                if (!fromWriteRoot) continue;
                setting.scope = scope;
                setting.onChanged = action;
            }
        }

        private static void ApplyInto(XElement element, object target, HashSet<(object, FieldInfo)> clearedLists, bool fromWriteRoot)
        {
            if (target is SettingCategory category)
            {
                ApplyCategory(element, category, fromWriteRoot);
                return;
            }

            XmlReflection.ApplyAttributes(element, target, true);

            foreach (XElement child in element.Elements())
            {
                Type type = AnyXMLType.FindType(child.Name.LocalName);
                if (type == null) continue;

                MemberInfo member = XmlReflection.ComplexMembers(target.GetType())
                    .FirstOrDefault(m => XmlReflection.MemberType(m).IsAssignableFrom(type));
                if (member != null)
                {
                    object nested = XmlReflection.GetMember(member, target)
                        ?? Activator.CreateInstance(XmlReflection.MemberType(member));
                    XmlReflection.SetMember(member, target, nested);
                    ApplyInto(child, nested, clearedLists, fromWriteRoot);
                    continue;
                }

                FieldInfo list = XmlReflection.ChildListFields(target.GetType())
                    .FirstOrDefault(f => f.FieldType.GetGenericArguments()[0].IsAssignableFrom(type));
                if (list == null) continue;

                IList entries = (IList)list.GetValue(target);
                if (clearedLists.Add((target, list))) entries.Clear();

                object entry = Activator.CreateInstance(type);
                ApplyInto(child, entry, clearedLists, fromWriteRoot);
                entries.Add(entry);
            }
        }
        #endregion

        #region ---- write ----
        // One file for every group, so a settings screen dismisses to a single write and the user
        // has one file to look at. A group with nothing to say is left out of it entirely.
        [A_XSDActionDependency("Settings.SaveAll", "Shutdown", "Writes every settings group that differs from its baseline")]
        public static bool SaveAll()
        {
            if (writeRoot == null)
            {
                Log.Warn($"no write root — settings not saved.");
                return false;
            }

            XNamespace ns = XSDGenerator.NamespaceFor("Settings");
            XElement root = new XElement(ns + "UserSettings");

            foreach (KeyValuePair<Type, ISettingsGroup> entry in groups)
            {
                XElement group = WriteDiff(entry.Value, baselines[entry.Key]);

                if (entry.Value is IMigratableSettings migratable)
                    group.SetAttributeValue(versionAttribute, migratable.version);

                if (stored.TryGetValue(entry.Key, out XElement previous))
                {
                    if (entry.Value is SettingCategory category) CarryCategoryUnknowns(group, previous, category);
                    else CarryUnknowns(group, previous, entry.Key);
                }

                if (group.HasAttributes || group.HasElements) root.Add(group);
            }

            string schemaFile = Path.Combine(Paths.XMLSCHEMAS, "SettingsTypeSchema.xsd");
            string relative = Path.GetRelativePath(writeRoot, schemaFile).Replace('\\', '/');

            XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
            root.SetAttributeValue(XNamespace.Xmlns + "xsi", xsi.NamespaceName);
            root.SetAttributeValue(xsi + "schemaLocation", $"{ns.NamespaceName} {relative}");

            Directory.CreateDirectory(writeRoot);
            new XDocument(new XDeclaration("1.0", "utf-8", null), root)
                .Save(Path.Combine(writeRoot, "UserSettings.settings.axml"));

            return true;
        }

        private static XElement WriteDiff(object node, MemberSnapshot baseline)
        {
            A_XSDTypeAttribute meta = node.GetType().GetCustomAttribute<A_XSDTypeAttribute>(false)
                ?? throw new Exception($"Type {node.GetType().Name} is missing [A_XSDType].");

            XNamespace ns = XSDGenerator.NamespaceFor(meta.Category);
            XElement element = new XElement(ns + meta.Name);

            // An App-scoped setting is the build's to decide, so it never reaches the user's file.
            if (node is SettingCategory category)
            {
                foreach (Setting setting in category.settings)
                {
                    if (setting.scope == SettingScope.App) continue;

                    baseline.settings.TryGetValue(SettingCategory.NameOf(setting), out MemberSnapshot previous);
                    XElement child = WriteDiff(setting, previous ?? new MemberSnapshot());
                    if (child.HasAttributes) element.Add(child);
                }
                return element;
            }

            foreach (MemberInfo member in XmlReflection.ScalarMembers(node.GetType()))
            {
                // Scope and OnChanged are the code's and the mounts', so they are never written back.
                if (node is Setting && member.DeclaringType == typeof(Setting)) continue;

                object value = XmlReflection.GetMember(member, node);
                if (value == null) continue;
                if (baseline.scalars.TryGetValue(member, out object previous) && Equals(value, previous)) continue;
                element.SetAttributeValue(member.GetCustomAttribute<A_XSDElementPropertyAttribute>().Name,
                    Convert.ToString(value, CultureInfo.InvariantCulture));
            }

            foreach (MemberInfo member in XmlReflection.ComplexMembers(node.GetType()))
            {
                object value = XmlReflection.GetMember(member, node);
                if (value == null) continue;

                baseline.nested.TryGetValue(member, out MemberSnapshot nested);
                XElement child = WriteDiff(value, nested ?? new MemberSnapshot());
                if (child.HasAttributes || child.HasElements) element.Add(child);
            }

            foreach (FieldInfo list in XmlReflection.ChildListFields(node.GetType()))
            {
                List<object> entries = ((IEnumerable)list.GetValue(node)).Cast<object>().ToList();
                baseline.lists.TryGetValue(list, out List<MemberSnapshot> previous);
                if (!ListChanged(entries, previous)) continue;

                foreach (object entry in entries)
                    element.Add(WriteDiff(entry, DefaultsOf(entry.GetType())));
            }
            return element;
        }

        // A stored setting the code no longer declares is copied back whole; one it still declares is
        // walked for attributes no member claims.
        private static void CarryCategoryUnknowns(XElement written, XElement previous, SettingCategory category)
        {
            foreach (XElement stored in previous.Elements())
            {
                XElement writtenChild = written.Elements()
                    .FirstOrDefault(e => e.Name.LocalName == stored.Name.LocalName);
                Setting setting = category.Find(stored.Name.LocalName);

                if (setting == null)
                {
                    if (writtenChild == null) written.Add(new XElement(stored));
                    continue;
                }
                if (writtenChild != null) CarryUnknowns(writtenChild, stored, setting.GetType());
            }
        }

        // Attributes the stored file carried that no member claims, copied onto the fresh diff so a
        // save cannot erase a value the code stopped knowing about. List entries are not walked.
        private static void CarryUnknowns(XElement written, XElement previous, Type type)
        {
            HashSet<string> known = new HashSet<string>(
                XmlReflection.ScalarMembers(type).Select(m => m.GetCustomAttribute<A_XSDElementPropertyAttribute>().Name),
                StringComparer.OrdinalIgnoreCase) { versionAttribute };

            foreach (XAttribute attribute in previous.Attributes())
                if (!attribute.IsNamespaceDeclaration
                    && !known.Contains(attribute.Name.LocalName)
                    && written.Attribute(attribute.Name) == null)
                    written.SetAttributeValue(attribute.Name, attribute.Value);

            foreach (MemberInfo member in XmlReflection.ComplexMembers(type))
            {
                Type memberType = XmlReflection.MemberType(member);
                A_XSDTypeAttribute meta = memberType.GetCustomAttribute<A_XSDTypeAttribute>(false);
                if (meta == null) continue;

                XElement previousChild = previous.Elements().FirstOrDefault(e => e.Name.LocalName == meta.Name);
                if (previousChild == null) continue;

                XElement writtenChild = written.Elements().FirstOrDefault(e => e.Name.LocalName == meta.Name);
                bool created = writtenChild == null;
                if (created)
                {
                    writtenChild = new XElement(XSDGenerator.NamespaceFor(meta.Category) + meta.Name);
                    written.Add(writtenChild);
                }

                CarryUnknowns(writtenChild, previousChild, memberType);
                if (created && !writtenChild.HasAttributes && !writtenChild.HasElements) writtenChild.Remove();
            }
        }

        private static bool ListChanged(List<object> entries, List<MemberSnapshot> baseline)
        {
            if (baseline == null || entries.Count != baseline.Count) return true;

            for (int i = 0; i < entries.Count; i++)
                foreach (MemberInfo member in XmlReflection.ScalarMembers(entries[i].GetType()))
                    if (!baseline[i].scalars.TryGetValue(member, out object previous)
                        || !Equals(XmlReflection.GetMember(member, entries[i]), previous))
                        return true;

            return false;
        }
        #endregion

        #region ---- snapshots ----
        // Every value a node carries, deep enough to diff a save against.
        private sealed class MemberSnapshot
        {
            public Dictionary<MemberInfo, object> scalars = new Dictionary<MemberInfo, object>();
            public Dictionary<MemberInfo, MemberSnapshot> nested = new Dictionary<MemberInfo, MemberSnapshot>();
            public Dictionary<FieldInfo, List<MemberSnapshot>> lists = new Dictionary<FieldInfo, List<MemberSnapshot>>();
            public Dictionary<string, MemberSnapshot> settings = new Dictionary<string, MemberSnapshot>(StringComparer.OrdinalIgnoreCase);
        }

        private static MemberSnapshot Snapshot(object node)
        {
            MemberSnapshot snapshot = new MemberSnapshot();

            if (node is SettingCategory category)
            {
                foreach (Setting setting in category.settings)
                    snapshot.settings[SettingCategory.NameOf(setting)] = Snapshot(setting);
                return snapshot;
            }

            foreach (MemberInfo member in XmlReflection.ScalarMembers(node.GetType()))
                snapshot.scalars[member] = XmlReflection.GetMember(member, node);

            foreach (MemberInfo member in XmlReflection.ComplexMembers(node.GetType()))
            {
                object value = XmlReflection.GetMember(member, node);
                if (value != null) snapshot.nested[member] = Snapshot(value);
            }

            foreach (FieldInfo list in XmlReflection.ChildListFields(node.GetType()))
            {
                List<MemberSnapshot> entries = new List<MemberSnapshot>();
                foreach (object entry in (IEnumerable)list.GetValue(node))
                    entries.Add(Snapshot(entry));
                snapshot.lists[list] = entries;
            }
            return snapshot;
        }

        private static MemberSnapshot DefaultsOf(Type type)
        {
            if (!defaultSnapshots.TryGetValue(type, out MemberSnapshot snapshot))
                defaultSnapshots[type] = snapshot = Snapshot(Activator.CreateInstance(type));
            return snapshot;
        }
        #endregion
    }
}
