using ArctisAurora.Core.Filing.Serialization;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace ArctisAurora.Core.Registry
{
    #region Attributes
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class A_XSDElementPropertyAttribute : Attribute
    {
        public string Name { get; set; }
        public string Description { get; } = string.Empty;
        public string Category { get; set; } = "Uncategorized";

        public A_XSDElementPropertyAttribute(string name, string? category = "Uncategorized", string? description = "")
        {
            Name = name;
            Description = description;
            Category = category;
        }
    }

    // Restricts a member to a sibling enum listing the variants this build permits. The member keeps
    // its own type; only the schema and the parse target change, so the two cannot disagree.
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class A_XSDDomainAttribute : Attribute
    {
        public Type Domain { get; }

        public A_XSDDomainAttribute(Type domain)
        {
            Domain = domain;
        }

        public static Type? DomainOf(MemberInfo member) =>
            member.GetCustomAttribute<A_XSDDomainAttribute>()?.Domain;
    }

    [AttributeUsage(AttributeTargets.Enum | AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface)]
    public class A_XSDTypeAttribute : Attribute
    {
        public string Name { get; set; }
        public string Description { get; set; } = string.Empty;
        public string Category { get; set; } = "Uncategorized";
        public string PatternValue { get; set; } = string.Empty;
        public Type? AllowedChildren { get; set; } = null;
        public int MinChildren { get; set; } = 0;
        public int MaxChildren { get; set; } = -1;
        public bool IsAbstract { get; set; } = false;

        public A_XSDTypeAttribute(string name, string category = "Uncategorized", Type allowedChildren = null, int minChildren = 0, int maxChildren = -1, string patternValue="", string description = "", bool isAbstract = false)
        {
            Name = name;
            Description = description;
            Category = category;
            PatternValue = patternValue;
            AllowedChildren = allowedChildren;
            MaxChildren = maxChildren;
            MinChildren = minChildren;
            IsAbstract = isAbstract;
        }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class A_XSDActionDependencyAttribute : Attribute
    {
        public string Name { get; set; }
        public string Description { get; } = string.Empty;
        public string Category { get; set; } = "Any";
        public A_XSDActionDependencyAttribute(string name, string? category = "Any", string? description = "")
        {
            Name = name;
            Description = description;
            Category = category;
        }
    }
    #endregion

    [A_XSDType("IParseableTypes", category:"AssetRegistry")]
    public interface IXMLParser<T> where T: class
    {
        public static abstract T ParseXML(string xmlName);
    }

    public sealed class AnyXMLType
    {
        public static readonly Dictionary<string, Type> typeMap = BuildTypeMap();
        private static Dictionary<string, Type> BuildTypeMap()
        {
            Dictionary<string, Type> map = new Dictionary<string, Type>();
            map.Add("xs:string", typeof(string));
            map.Add("xs:int", typeof(int));
            map.Add("xs:float", typeof(float));
            map.Add("xs:double", typeof(double));
            map.Add("xs:boolean", typeof(bool));
            map.Add("xs:byte", typeof(byte));
            map.Add("xs:short", typeof(short));
            map.Add("xs:long", typeof(long));
            map.Add("xs:unsignedInt", typeof(uint));
            map.Add("xs:unsignedShort", typeof(ushort));
            map.Add("xs:unsignedLong", typeof(ulong));
            map.Add("xs:char", typeof(string));
            map.Add("xs:decimal", typeof(decimal));
            map.Add("Action", typeof(Action));
            map.Add("Type", typeof(Type));
            map.Add("Object", typeof(object));

            return map;
        }

        // [A_XSDType] name -> type, in assembly load order so the first declaration of a name wins
        private static Dictionary<string, Type>? xsdTypes;

        public static Type? FindType(string typeName)
        {
            var type = Type.GetType(typeName);
            if (type != null)
            {
                return type;
            }

            if (xsdTypes == null)
            {
                xsdTypes = new Dictionary<string, Type>();
                foreach (Type t in EngineTypes.All)
                {
                    object[] attributes = t.GetCustomAttributes(typeof(A_XSDTypeAttribute), false);
                    if (attributes.Length == 0) continue;

                    string name = ((A_XSDTypeAttribute)attributes[0]).Name;
                    if (!xsdTypes.ContainsKey(name))
                        xsdTypes.Add(name, t);
                }
            }

            return xsdTypes.TryGetValue(typeName, out Type? found) ? found : null;
        }
    }

    public static class XSDGenerator
    {
        private static readonly Diagnostics.LogChannel Log = Diagnostics.LogChannel.For("XSD");

        // The action category every other one folds in, so an action declared here is callable from
        // any document that takes an action.
        private const string AnyActions = "Any";

        #region ---- QUICK ACCESS ----
        // dictionaries
        public static readonly Dictionary<Type, string> typeMap = BuildTypeMap();
        // Which category owns each [A_XSDType]. Category decides both the file a type is written to
        // and the namespace that owns it, so any reference to a type from another category has to be
        // qualified with that category's prefix instead of the local "types:".
        private static readonly Dictionary<Type, string> categoryMap = BuildCategoryMap();

        // static variables that all schemas use
        private static XmlSchemaImport actionDependency = new XmlSchemaImport
        {
            Namespace = "http://arctisaurora/ActionDependencies",
            SchemaLocation = "actionSchema.xsd"
        };
        private static XmlSchemaImport allTypeDependency = new XmlSchemaImport
        {
            Namespace = "http://arctisaurora/AuroraTypes",
            SchemaLocation = "AllTypesSchema.xsd"
        };

        private static XmlWriterSettings settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = System.Text.Encoding.UTF8,
        };

        #endregion

        #region ---- HASHING ----
        private static Dictionary<string, string> _previousHashes = new Dictionary<string, string>();
        private static Dictionary<string, string> _currentHashes = new Dictionary<string, string>();
        private static string ManifestPath => Path.Combine(Paths.XMLSCHEMAS, "SchemaManifest.axml");

        private static void LoadHashes()
        {
            _previousHashes.Clear();
            if (!File.Exists(ManifestPath)) return;

            XElement root = XElement.Load(ManifestPath);
            foreach (XElement entry in root.Elements("Schema"))
            {
                string file = entry.Attribute("FileName")?.Value;
                string hash = entry.Attribute("Hash")?.Value;
                if (file != null && hash != null)
                    _previousHashes[file] = hash;
            }
        }

        private static void SaveHashes()
        {
            foreach (var kvp in _previousHashes)
            {
                if (!_currentHashes.ContainsKey(kvp.Key))
                    _currentHashes[kvp.Key] = kvp.Value;
            }

            XElement root = new XElement("SchemaManifest");
            foreach (var kvp in _currentHashes.OrderBy(k => k.Key))
            {
                root.Add(new XElement("Schema",
                    new XAttribute("FileName", kvp.Key),
                    new XAttribute("Hash", kvp.Value)));
            }
            root.Save(ManifestPath);
        }

        private static string ComputeHash(string content)
        {
            byte[] bytes = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(content));
            return Convert.ToHexString(bytes);
        }

        private static bool NeedsRegeneration(string fileName, string fingerprint)
        {
            string hash = ComputeHash(fingerprint);
            _currentHashes[fileName] = hash;
            return !_previousHashes.TryGetValue(fileName, out string old) || old != hash;
        }

        #region ---- XSD FINGERPRINT ----
        private static string BuildCategoryFingerprint(string category,
            List<(Type Type, A_XSDTypeAttribute Attribute)> categoryTypes, Type[] allTypes)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"category:{category}|");
            // Abstract types are no longer skipped — they emit no element, but they do own the
            // attribute group their descendants point at.
            foreach (var t in categoryTypes.OrderBy(x => x.Attribute.Name))
            {
                sb.Append($"type:{t.Attribute.Name}:{t.Type.FullName}:{t.Attribute.IsAbstract}|");
                if (t.Type.IsEnum)
                {
                    foreach (string name in Enum.GetNames(t.Type).OrderBy(n => n))
                        sb.Append($"ev:{name}|");
                }
                else
                {
                    // Only what this file emits: the type's own declarations and the ref it points at.
                    // A member added to a base rewrites the base's file, and this one is left alone.
                    HashSet<string> probe = new HashSet<string>();
                    sb.Append($"agref:{AttributeGroupRefFor(t.Type, category, probe) ?? "-"}|");

                    List<A_XSDTypeAttribute> childTypes = t.Attribute.AllowedChildren == null
                        ? new List<A_XSDTypeAttribute>()
                        : ChildTypesOf(t.Attribute.AllowedChildren, allTypes);
                    HashSet<string> childNames = childTypes.Select(a => a.Name).ToHashSet();

                    var emitted = SplitChain(t.Type).Own
                        .Where(m => IsAttributeMember(MemberTypeOf(m.Member)))
                        .Concat(GetAnnotatedMembers(t.Type).Where(m => !IsAttributeMember(MemberTypeOf(m.Member))
                            && !childNames.Contains(m.XmlAttribute?.Name ?? m.Member.Name)));
                    foreach (var member in emitted.OrderBy(m => m.XmlAttribute?.Name))
                    {
                        Type memberType = MemberTypeOf(member.Member);
                        sb.Append($"m:{member.XmlAttribute?.Name}:{memberType.FullName}:{member.XmlAttribute?.Category}:{OwningCategoryOf(ReferencedTypeOf(memberType))}|");
                    }
                    if (t.Attribute.AllowedChildren != null)
                    {
                        sb.Append($"group:{t.Attribute.AllowedChildren.Name}:{t.Attribute.MinChildren}:{t.Attribute.MaxChildren}|");
                        foreach (string cn in childTypes.Select(a => $"{a.Category}:{a.Name}").OrderBy(n => n))
                            sb.Append($"child:{cn}|");
                    }
                }
            }
            return sb.ToString();
        }

        // The type a member actually points at, unwrapping the collection and nullable the same way
        // GenerateComplexType/ResolveTypeName do — the fingerprint has to track the category that
        // owns it, or moving a type between categories rewrites the schema without changing its
        // hash and the stale file is never regenerated.
        private static Type ReferencedTypeOf(Type memberType)
        {
            Type referenced = memberType.IsGenericType
                && memberType.GetGenericArguments().Length == 1
                && typeof(IEnumerable<>).MakeGenericType(memberType.GetGenericArguments()).IsAssignableFrom(memberType)
                ? memberType.GetGenericArguments()[0]
                : memberType;

            return Nullable.GetUnderlyingType(referenced) ?? referenced;
        }

        private static string BuildAllTypesFingerprint(Type[] allTypes)
        {
            var sb = new System.Text.StringBuilder();
            var types = allTypes
                .Where(t => t.GetCustomAttributes(typeof(A_XSDTypeAttribute), false).Any())
                .Select(t => t.GetCustomAttribute<A_XSDTypeAttribute>()?.Name ?? t.Name)
                .OrderBy(n => n);
            foreach (string n in types) sb.Append($"t:{n}|");
            foreach (string k in AnyXMLType.typeMap.Keys.OrderBy(k => k)) sb.Append($"p:{k}|");

            // The global list unions the category types rather than restating their names, so the
            // set of categories is part of what the file says.
            var categories = allTypes
                .Select(t => t.GetCustomAttribute<A_XSDTypeAttribute>(false)?.Category)
                .Where(c => !string.IsNullOrEmpty(c)).Distinct().OrderBy(c => c);
            foreach (string c in categories) sb.Append($"c:{c}|");
            return sb.ToString();
        }

        private static string BuildActionFingerprint(Type[] allTypes)
        {
            var sb = new System.Text.StringBuilder();
            var allMethods = allTypes
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                .Select(m => m.GetCustomAttributes(typeof(A_XSDActionDependencyAttribute), true)
                    .FirstOrDefault() as A_XSDActionDependencyAttribute)
                .Where(attr => attr != null)
                .OrderBy(a => a.Category).ThenBy(a => a.Name);
            foreach (var a in allMethods) sb.Append($"a:{a.Category}:{a.Name}|");
            return sb.ToString();
        }
        #endregion
        #endregion

        public static void GenerateXSD()
        {
            if (Paths.packed) return;
            Type[] allTypes = EngineTypes.All;
            LoadHashes();
            GenerateDependencyXSD(allTypes);
            SaveHashes();
        }

        private static void GenerateDependencyXSD(Type[] allTypes)
        {
            GenerateTypeXSD(allTypes);
            GenerateActionXSD(allTypes);
        }

        private static void GenerateTypeXSD(Type[] allTypes)
        {
            GenerateTypesPerCategory(allTypes);
            GenerateAllTypesXSD(allTypes);
        }

        private static void GenerateTypesPerCategory(Type[] allTypes)
        {
            var types = allTypes
                .Where(t => t.GetCustomAttributes(typeof(A_XSDTypeAttribute), false).Any())
                .Select(t => (
                    Type: t,
                    Attribute: (A_XSDTypeAttribute)t.GetCustomAttributes(typeof(A_XSDTypeAttribute), true).First()
                ))
                .Where(x => x.Attribute != null).ToList();

            var categorizedTypes = types.Where(x => x.Attribute.Category != "Uncategorized")
                .GroupBy(x => x.Attribute.Category)
                .ToDictionary(g => g.Key ?? "Uncategorized", g => g.ToList());

            foreach (var category in categorizedTypes)
            {
                if (category.Key == "Uncategorized") continue;

                string fileName = $"{category.Key}TypeSchema.xsd";
                string fp = BuildCategoryFingerprint(category.Key, category.Value, allTypes);
                if (!NeedsRegeneration(fileName, fp))
                {
                    Log.Debug($"skipping {fileName} — unchanged");
                    continue;
                }

                XmlSchema typeSchema = BuildSchemaBase(category.Key);
                // Which other categories this schema ended up pointing at. Only known once every
                // type is emitted, so the imports go on just before the write.
                HashSet<string> foreignCategories = new HashSet<string>();

                XmlSchemaSimpleTypeUnion categoryUnion = new XmlSchemaSimpleTypeUnion
                {
                    MemberTypes = category.Value
                        .Where(t => t.Type.IsEnum)
                        .Select(t => new XmlQualifiedName("types:" + t.Attribute?.Name))
                        .ToArray()
                };
                if (categoryUnion.MemberTypes.Length != 0)
                {
                    XmlSchemaSimpleType typeSimpleCategory = new XmlSchemaSimpleType
                    {
                        Name = category.Key
                    };
                    typeSimpleCategory.Content = categoryUnion;
                    typeSchema.Items.Add(typeSimpleCategory);
                }
                // AllowedChildren base -> the group holding its implementors, one per file.
                Dictionary<Type, string?> childGroups = new Dictionary<Type, string?>();

                foreach (var t in category.Value)
                {
                    // An abstract base is never authored, but it still owns the attribute group its
                    // descendants point at, so it is emitted before the skip.
                    if (!t.Type.IsEnum)
                        GenerateAttributeGroup(t.Type, typeSchema, category.Key, foreignCategories);

                    // Abstract (base) types are registered but never authored — no element, no complexType.
                    if (t.Attribute.IsAbstract) continue;

                    if (t.Type.IsEnum)
                    {
                        GenerateEnumType(t.Type, t.Attribute.Name, typeSchema);
                    }
                    else
                    {
                        XmlSchemaElement schemaElement = new XmlSchemaElement
                        {
                            Name = t.Attribute.Name,
                            SchemaTypeName = new XmlQualifiedName($"types:{t.Attribute.Name}")
                        };
                        typeSchema.Items.Add(schemaElement);

                        if(category.Key != "Uncategorized")
                            GenerateComplexType(t.Type, t.Attribute, typeSchema, allTypes, category.Key, foreignCategories, childGroups);

                    }
                }
                if (category.Key != "Uncategorized")
                {
                    AddForeignImports(typeSchema, foreignCategories);
                    WriteSchema(typeSchema, $"{category.Key}TypeSchema.xsd");
                }
            }
        }

        private static void GenerateAllTypesXSD(Type[] allTypes)
        {
            string fileName = "AllTypesSchema.xsd";
            string fp = BuildAllTypesFingerprint(allTypes);
            if (!NeedsRegeneration(fileName, fp))
            {
                Log.Debug($"skipping {fileName} — unchanged");
                return;
            }


            XmlSchema allTypeSchema = BuildSchemaBase("");

            // Names belonging to no category, plus the primitives. Every other name is already
            // spelled out by its own category type, which the global list unions rather than repeats.
            XmlSchemaSimpleTypeRestriction looseNames = new XmlSchemaSimpleTypeRestriction
            {
                BaseTypeName = new XmlQualifiedName("xs:string")
            };

            var types = allTypes
                .Where(t => t.GetCustomAttributes(typeof(A_XSDTypeAttribute), false).Any()).ToList();

            var categorizedTypes = types.Where(x => !string.IsNullOrEmpty(x.GetCustomAttribute<A_XSDTypeAttribute>()?.Category))
                .GroupBy(x => x.GetCustomAttribute<A_XSDTypeAttribute>()?.Category)
                .ToDictionary(g => g.Key ?? "Uncategorized", g => g.ToList());

            foreach (var category in categorizedTypes)
            {
                XmlSchemaSimpleTypeRestriction categoryRestriction = new XmlSchemaSimpleTypeRestriction
                {
                    BaseTypeName = new XmlQualifiedName("xs:string")
                };

                foreach (var t in category.Value)
                {
                    XmlSchemaEnumerationFacet typeElement = new XmlSchemaEnumerationFacet
                    {
                        Value = t.GetCustomAttribute<A_XSDTypeAttribute>()?.Name ?? t.Name
                    };
                    categoryRestriction.Facets.Add(typeElement);
                    if (category.Key == "Uncategorized")
                        looseNames.Facets.Add(typeElement);
                }
                if (category.Key == "Uncategorized")
                    continue;
                allTypeSchema.Items.Add(new XmlSchemaSimpleType
                {
                    Name = category.Key,
                    Content = categoryRestriction
                });
            }

            foreach (var key in AnyXMLType.typeMap.Keys)
            {
                XmlSchemaEnumerationFacet typeElement = new XmlSchemaEnumerationFacet { Value = key };
                looseNames.Facets.Add(typeElement);
            }

            XmlSchemaSimpleTypeUnion allTypesUnion = new XmlSchemaSimpleTypeUnion
            {
                MemberTypes = categorizedTypes.Keys.Where(k => k != "Uncategorized")
                    .Select(k => new XmlQualifiedName("types:" + k)).ToArray()
            };
            allTypesUnion.BaseTypes.Add(new XmlSchemaSimpleType { Content = looseNames });
            allTypeSchema.Items.Add(new XmlSchemaSimpleType
            {
                Name = "Uncategorized",
                Content = allTypesUnion
            });

            WriteSchema(allTypeSchema, "AllTypesSchema.xsd");
        }

        private static void GenerateActionXSD(Type[] allTypes)
        {
            string fileName = "actionSchema.xsd";
            string fp = BuildActionFingerprint(allTypes);
            if (!NeedsRegeneration(fileName, fp))
            {
                Log.Debug($"skipping {fileName} — unchanged");
                return;
            }



            var allMethods = allTypes
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                .Select(m => new
                {
                    Method = m,
                    DeclaringType = t,
                    Attribute = m.GetCustomAttributes(typeof(A_XSDActionDependencyAttribute), true)
                                    .FirstOrDefault() as A_XSDActionDependencyAttribute
                })
                .Where(x => x.Attribute != null)).ToList();

            var anyMethods = allMethods.Where(x => x.Attribute.Category == AnyActions).ToList();

            var categorizedMethods = allMethods.Where(x => x.Attribute.Category != AnyActions)
                .GroupBy(x => x.Attribute.Category)
                .ToDictionary(g => g.Key, g => g.ToList());


            XmlSchema actionSchema = new XmlSchema
            {
                TargetNamespace = "http://arctisaurora/ActionDependencies",
                ElementFormDefault = XmlSchemaForm.Qualified
            };
            actionSchema.Namespaces.Add("actions", "http://arctisaurora/ActionDependencies");
            actionSchema.Namespaces.Add("xsd", "http://www.w3.org/2001/XMLSchema");

            // Emitted only when something declares it — an empty enumeration restricts nothing, and
            // every category unions this in.
            if (anyMethods.Count != 0)
            {
                actionSchema.Items.Add(new XmlSchemaSimpleType
                {
                    Name = AnyActions,
                    Content = ActionEnumeration(anyMethods.Select(m => m.Attribute.Name))
                });
            }

            foreach(var category in categorizedMethods)
            {
                XmlSchemaSimpleTypeRestriction categoryActions =
                    ActionEnumeration(category.Value.Select(m => m.Attribute.Name));

                XmlSchemaSimpleType actionType = new XmlSchemaSimpleType
                {
                    Name = category.Key
                };

                if (anyMethods.Count == 0)
                {
                    actionType.Content = categoryActions;
                }
                else
                {
                    XmlSchemaSimpleTypeUnion withAny = new XmlSchemaSimpleTypeUnion
                    {
                        MemberTypes = new[]
                        {
                            new XmlQualifiedName(AnyActions, "http://arctisaurora/ActionDependencies")
                        }
                    };
                    withAny.BaseTypes.Add(new XmlSchemaSimpleType { Content = categoryActions });
                    actionType.Content = withAny;
                }
                actionSchema.Items.Add(actionType);
            }

            // Write schema to file
            WriteSchema(actionSchema, "actionSchema.xsd");
        }

        private static XmlSchemaSimpleTypeRestriction ActionEnumeration(IEnumerable<string> names)
        {
            XmlSchemaSimpleTypeRestriction restriction = new XmlSchemaSimpleTypeRestriction
            {
                BaseTypeName = new XmlQualifiedName("xs:string")
            };
            foreach (string name in names)
                restriction.Facets.Add(new XmlSchemaEnumerationFacet { Value = name });

            return restriction;
        }

        // The XML namespace a category's types live in. Shared, because a schema's targetNamespace,
        // the imports pointing at it and the documents written against it all have to agree on this
        // one string — a literal copied into a third place is a note that silently stops validating.
        public static string NamespaceFor(string category) => $"http://arctisaurora/Aurora{category}Types";

        private static XmlSchema BuildSchemaBase(string category)
        {
            XmlSchema schema = new XmlSchema
            {
                TargetNamespace = NamespaceFor(category),
                ElementFormDefault = XmlSchemaForm.Qualified
            };
            schema.Namespaces.Add("types", NamespaceFor(category));
            schema.Namespaces.Add("xs", "http://www.w3.org/2001/XMLSchema");
            schema.Namespaces.Add("actions", "http://arctisaurora/ActionDependencies");
            schema.Includes.Add(actionDependency);

            if (category != "")
            {
                schema.Namespaces.Add("allTypes", "http://arctisaurora/AuroraTypes");
                schema.Includes.Add(allTypeDependency);
            }
            return schema;
        }

        // "types:" is only ever the schema being written. A type owned by another category lives in
        // that category's namespace, so referencing it locally names something that was never
        // declared — and XSD rejects the whole file, not just the reference. Uncategorized types get
        // no schema file at all, so they stay local: there is nothing to import for them, and a
        // dangling schemaLocation would break the file the same way.
        private static string Qualify(string owningCategory, string typeName, string currentCategory, HashSet<string> foreignCategories)
        {
            if (owningCategory == currentCategory || owningCategory == "Uncategorized")
                return $"types:{typeName}";

            foreignCategories.Add(owningCategory);
            return $"{owningCategory}:{typeName}";
        }

        // A qualified reference only resolves once the schema declares the owning category's prefix
        // and imports its file. Categories reference each other both ways — UI containers hold
        // EntityRegistry types and vice versa — and XSD resolves mutual imports fine.
        private static void AddForeignImports(XmlSchema schema, HashSet<string> foreignCategories)
        {
            foreach (string category in foreignCategories.OrderBy(c => c))
            {
                string ns = NamespaceFor(category);
                schema.Namespaces.Add(category, ns);
                schema.Includes.Add(new XmlSchemaImport
                {
                    Namespace = ns,
                    SchemaLocation = $"{category}TypeSchema.xsd"
                });
            }
        }

        private static string OwningCategoryOf(Type type)
            => categoryMap.TryGetValue(type, out string? category) ? category : "Uncategorized";

        // A member typed as a complex [A_XSDType] — a class or struct that becomes an xs:complexType.
        // Enums and everything in the primitive map resolve to simple types, which an attribute
        // accepts; complex ones do not, and have to be written as nested elements instead.
        private static bool IsComplexMember(Type memberType)
        {
            Type resolved = Nullable.GetUnderlyingType(memberType) ?? memberType;

            if (resolved.IsEnum) return false;
            if (AnyXMLType.typeMap.Any(kvp => kvp.Value == resolved)) return false;

            return typeMap.ContainsKey(resolved);
        }

        // The type a restricted member's attribute is emitted and parsed as. A domain naming a variant
        // the member's own enum lacks is a rename that desynced the two, so it is refused whole.
        private static Type DomainFor(MemberInfo member, Type memberType)
        {
            Type? domain = A_XSDDomainAttribute.DomainOf(member);
            if (domain == null) return memberType;

            if (!domain.IsEnum || !memberType.IsEnum)
            {
                Log.Error($"{member.DeclaringType?.Name}.{member.Name} restricts to {domain.Name}, "
                    + $"but a domain and its member must both be enums — ignoring it.");
                return memberType;
            }

            foreach (string variant in Enum.GetNames(domain))
            {
                if (Enum.IsDefined(memberType, variant)) continue;

                Log.Error($"{domain.Name}.{variant} does not exist on {memberType.Name}, so "
                    + $"{member.DeclaringType?.Name}.{member.Name} cannot be restricted by it — ignoring it.");
                return memberType;
            }
            return domain;
        }

        private static void GenerateEnumType(Type t, string name, XmlSchema schema)
        {
            XmlSchemaSimpleTypeRestriction restriction = new()
            {
                BaseTypeName = new XmlQualifiedName("xs:string")
            };
            foreach (var value in Enum.GetNames(t))
            {
                restriction.Facets.Add(new XmlSchemaEnumerationFacet { Value = value });
            }

            schema.Items.Add(new XmlSchemaSimpleType
            {
                Name = name,
                Content = restriction
            });
        }

        private static void GenerateComplexType(Type t, A_XSDTypeAttribute attribute, XmlSchema schema, Type[] allTypes, string currentCategory, HashSet<string> foreignCategories, Dictionary<Type, string?> childGroups)
        {
            XmlSchemaComplexType complexType = new XmlSchemaComplexType()
            {
                Name = attribute.Name
            };
            XmlSchemaSequence sequence = new XmlSchemaSequence();

            // element names the child group already offers
            HashSet<string> childNames = attribute.AllowedChildren == null
                ? new HashSet<string>()
                : ChildTypesOf(attribute.AllowedChildren, allTypes).Select(a => a.Name).ToHashSet();

            foreach (var member in GetAnnotatedMembers(t))
            {
                Type memberType = MemberTypeOf(member.Member);

                // Scalars are declared once in the type's attribute group and referenced below.
                if (IsAttributeMember(memberType)) continue;

                // Declaring the same element twice in one content model is what XSD rejects.
                if (childNames.Contains(member.XmlAttribute?.Name ?? member.Member.Name)) continue;

                var annotation = new XmlSchemaAnnotation();
                var documentation = new XmlSchemaDocumentation();
                documentation.Markup = new XmlNode[]
                {
                                new XmlDocument().CreateTextNode(member.XmlAttribute?.Description ?? "")
                };
                annotation.Items.Add(documentation);

                if (memberType.IsGenericType && typeof(IEnumerable<>).MakeGenericType(memberType.GetGenericArguments()).IsAssignableFrom(memberType))
                {
                    string typeName = ResolveTypeName(memberType.GetGenericArguments()[0], member.XmlAttribute, currentCategory, foreignCategories);

                    XmlQualifiedName qualifiedName = new(typeName);
                    XmlSchemaElement listElement = new()
                    {
                        Name = member.XmlAttribute?.Name ?? member.Member.Name,
                        SchemaTypeName = qualifiedName,
                        MinOccurs = 0,
                        MaxOccursString = "unbounded"
                    };
                    listElement.Annotation = annotation;
                    sequence.Items.Add(listElement);
                }
                else if (IsComplexMember(memberType))
                {
                    // An xs:attribute can only carry a simple type, so a member typed as a complex
                    // [A_XSDType] has to be a nested element instead — as an attribute it emits a
                    // schema that will not compile. Optional and single, so a document that leaves
                    // it out still validates and picks up the type's own defaults.
                    string typeName = ResolveTypeName(memberType, member.XmlAttribute, currentCategory, foreignCategories);

                    XmlSchemaElement memberElement = new XmlSchemaElement
                    {
                        Name = member.XmlAttribute?.Name ?? member.Member.Name,
                        SchemaTypeName = new XmlQualifiedName(typeName),
                        MinOccurs = 0,
                        MaxOccurs = 1
                    };
                    memberElement.Annotation = annotation;
                    sequence.Items.Add(memberElement);
                }
            }

            if (attribute.AllowedChildren != null)
            {
                string? groupName = EnsureChildGroup(attribute.AllowedChildren, schema, allTypes,
                    currentCategory, foreignCategories, childGroups);

                // A null name means nothing implements the base, so the type takes no children and
                // the particle is left off rather than emitted empty.
                if (groupName != null)
                {
                    sequence.Items.Add(new XmlSchemaGroupRef
                    {
                        RefName = new XmlQualifiedName($"types:{groupName}"),
                        MinOccurs = attribute.MinChildren,
                        MaxOccursString = attribute.MaxChildren == -1 ? "unbounded" : attribute.MaxChildren.ToString()
                    });
                }
            }

            string? attributeGroup = AttributeGroupRefFor(t, currentCategory, foreignCategories);
            if (attributeGroup != null)
                complexType.Attributes.Add(new XmlSchemaAttributeGroupRef { RefName = new XmlQualifiedName(attributeGroup) });

            complexType.Particle = sequence;
            schema.Items.Add(complexType);
        }

        // Everything an AllowedChildren base admits, the base itself included when it is authorable.
        private static List<A_XSDTypeAttribute> ChildTypesOf(Type childBase, Type[] allTypes)
        {
            List<A_XSDTypeAttribute> children = new List<A_XSDTypeAttribute>();
            foreach (Type ty in allTypes.Where(ty => childBase.IsAssignableFrom(ty)))
            {
                A_XSDTypeAttribute? childAttr = ty.GetCustomAttribute<A_XSDTypeAttribute>(false);
                // No [A_XSDType] name, or an abstract base (e.g. VulkanControl) → never a valid child.
                if (childAttr == null || childAttr.Name == string.Empty || childAttr.IsAbstract)
                {
                    continue;
                }
                children.Add(childAttr);
            }
            return children;
        }

        // One group per AllowedChildren base, so a new control costs a line here instead of a line in
        // every container that accepts one. It lives in the schema that references it, which keeps it
        // resolvable without an import.
        private static string? EnsureChildGroup(Type childBase, XmlSchema schema, Type[] allTypes,
            string currentCategory, HashSet<string> foreignCategories, Dictionary<Type, string?> childGroups)
        {
            if (childGroups.TryGetValue(childBase, out string? existing)) return existing;

            XmlSchemaChoice childChoice = new XmlSchemaChoice();
            foreach (A_XSDTypeAttribute childAttr in ChildTypesOf(childBase, allTypes))
            {
                XmlSchemaElement childElement = new XmlSchemaElement
                {
                    Name = childAttr.Name,
                    SchemaTypeName = new XmlQualifiedName(Qualify(childAttr.Category, childAttr.Name, currentCategory, foreignCategories))
                };
                childChoice.Items.Add(childElement);
            }

            string? groupName = childChoice.Items.Count == 0 ? null : $"{childBase.Name}.Children";
            childGroups[childBase] = groupName;
            if (groupName != null)
                schema.Items.Add(new XmlSchemaGroup { Name = groupName, Particle = childChoice });

            return groupName;
        }

        // A type's own attributes, plus a ref to the group its base owns. Restating an inherited
        // member in every descendant is what made one addition to a base a change in 25 places.
        private static void GenerateAttributeGroup(Type type, XmlSchema schema, string currentCategory, HashSet<string> foreignCategories)
        {
            (var own, Type? baseOwner) = SplitChain(type);
            var scalars = own.Where(m => IsAttributeMember(MemberTypeOf(m.Member))).ToList();
            if (scalars.Count == 0) return;

            XmlSchemaAttributeGroup group = new XmlSchemaAttributeGroup
            {
                Name = $"{type.GetCustomAttribute<A_XSDTypeAttribute>(false)!.Name}.Attributes"
            };

            string? baseRef = baseOwner == null ? null : AttributeGroupRefFor(baseOwner, currentCategory, foreignCategories);
            if (baseRef != null)
                group.Attributes.Add(new XmlSchemaAttributeGroupRef { RefName = new XmlQualifiedName(baseRef) });

            foreach (var member in scalars)
            {
                string typeName = ResolveTypeName(DomainFor(member.Member, MemberTypeOf(member.Member)),
                    member.XmlAttribute, currentCategory, foreignCategories);

                group.Attributes.Add(new XmlSchemaAttribute
                {
                    Name = member.XmlAttribute?.Name ?? member.Member.Name,
                    SchemaTypeName = new XmlQualifiedName(typeName)
                });
            }
            schema.Items.Add(group);
        }

        // The group a type points at: its own when it declares a scalar, otherwise its base's, walking
        // up until one exists. Nothing to point at means the type carries no attributes at all.
        private static string? AttributeGroupRefFor(Type type, string currentCategory, HashSet<string> foreignCategories)
        {
            Type? owner = type;
            while (owner != null)
            {
                (var own, Type? next) = SplitChain(owner);
                if (own.Any(m => IsAttributeMember(MemberTypeOf(m.Member))))
                {
                    A_XSDTypeAttribute attr = owner.GetCustomAttribute<A_XSDTypeAttribute>(false)!;
                    return Qualify(attr.Category, $"{attr.Name}.Attributes", currentCategory, foreignCategories);
                }
                owner = next;
            }
            return null;
        }

        // What a type contributes to its own group: its declarations, plus those of any ancestor with
        // no schema to live in, and the nearest ancestor that does own one.
        private static (List<(MemberInfo Member, A_XSDElementPropertyAttribute? XmlAttribute)> Own, Type? Base) SplitChain(Type type)
        {
            var own = GetDeclaredMembers(type);
            Type? examined = type.BaseType;
            while (examined != null && examined != typeof(object))
            {
                A_XSDTypeAttribute? attr = examined.GetCustomAttribute<A_XSDTypeAttribute>(false);
                if (attr != null && attr.Category != "Uncategorized") return (own, examined);

                own.AddRange(GetDeclaredMembers(examined));
                examined = examined.BaseType;
            }
            return (own, null);
        }

        private static List<(MemberInfo Member, A_XSDElementPropertyAttribute? XmlAttribute)> GetDeclaredMembers(Type type)
        {
            return GetAnnotatedMembers(type).Where(m => IsDeclaredOn(m.Member, type)).ToList();
        }

        // An override restates what the base already contributes, so it belongs to the base's group —
        // counting it on both makes the chain declare one attribute twice, which XSD rejects.
        private static bool IsDeclaredOn(MemberInfo member, Type type)
        {
            if (member.DeclaringType != type) return false;
            if (member is not PropertyInfo property) return true;

            MethodInfo? accessor = property.GetMethod ?? property.SetMethod;
            return accessor == null || accessor.GetBaseDefinition().DeclaringType == type;
        }

        private static Type MemberTypeOf(MemberInfo member) => member.MemberType == MemberTypes.Field
            ? ((FieldInfo)member).FieldType
            : ((PropertyInfo)member).PropertyType;

        // A scalar, which becomes an xs:attribute. Collections and complex [A_XSDType] members are
        // nested elements instead, so they stay on the type's own sequence.
        private static bool IsAttributeMember(Type memberType)
        {
            if (typeof(Delegate).IsAssignableFrom(memberType)) return true;

            if (memberType.IsGenericType
                && typeof(IEnumerable<>).MakeGenericType(memberType.GetGenericArguments()).IsAssignableFrom(memberType))
                return false;

            return !IsComplexMember(memberType);
        }

        private static List<(MemberInfo Member, A_XSDElementPropertyAttribute? XmlAttribute)> GetAnnotatedMembers(Type type)
        {
            // GetCustomAttribute<T>(), not GetCustomAttributes(type, inherit) — the latter ignores
            // `inherit` for properties, so an override loses the base declaration's annotation.
            return type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .Where(m => (m.MemberType == MemberTypes.Field || m.MemberType == MemberTypes.Property) &&
                            m.GetCustomAttribute<A_XSDElementPropertyAttribute>() != null)
                .Select(m => (
                    Member: m,
                    XmlAttribute: m.GetCustomAttribute<A_XSDElementPropertyAttribute>()
                )).ToList();
        }
        
        private static string ResolveTypeName(Type memberType, A_XSDElementPropertyAttribute? xmlAttr, string currentCategory, HashSet<string> foreignCategories)
        {
            Type resolved = Nullable.GetUnderlyingType(memberType) ?? memberType;

            if (typeof(Delegate).IsAssignableFrom(resolved)) return $"actions:{xmlAttr?.Category}";

            string? mapped = AnyXMLType.typeMap.FirstOrDefault(kvp => kvp.Value == resolved).Key;
            if (mapped != null)
            {
                if (mapped == "types:Uncategorized") return $"allTypes:{xmlAttr?.Category}";
                return mapped;
            }

            if (typeMap.TryGetValue(resolved, out string? typeMapped))
                return Qualify(OwningCategoryOf(resolved), typeMapped, currentCategory, foreignCategories);

            if (resolved == typeof(AnyXMLType))
                return $"allTypes:Uncategorized";

            return "xs:string";
        }

        private static void WriteSchema(XmlSchema schema, string fileName)
        {
            Directory.CreateDirectory(Paths.XMLSCHEMAS);
            string path = Path.Combine(Paths.XMLSCHEMAS, fileName);
            using var writer = XmlWriter.Create(path, settings);
            schema.Write(writer);
            Log.Info($"{fileName} updated");
        }

        #region MapBuilders
        private static Dictionary<Type, string> BuildTypeMap()
        {
            Dictionary<Type, string> map = new Dictionary<Type, string>();
            /*{
                { typeof(string),     "xs:string" },
                { typeof(int),        "xs:int" },
                { typeof(float),      "xs:float" },
                { typeof(double),     "xs:double" },
                { typeof(bool),       "xs:boolean" },
                { typeof(byte),       "xs:byte" },
                { typeof(short),      "xs:short" },
                { typeof(long),       "xs:long" },
                { typeof(uint),       "xs:unsignedInt" },
                { typeof(ushort),     "xs:unsignedShort" },
                { typeof(ulong),      "xs:unsignedLong" },
                { typeof(char),       "xs:string" },
                { typeof(decimal),    "xs:decimal" },
                { typeof(object),     "Object" },
                { typeof(Action),     "Action" },
                { typeof(Type),       "Type" },
                { typeof(AnyXMLType), "types:Uncategorized" }
            };*/
            foreach (Type type in EngineTypes.All)
            {
                object[] attributes = type.GetCustomAttributes(typeof(A_XSDTypeAttribute), true);
                if (attributes.Length > 0) map[type] = ((A_XSDTypeAttribute)attributes[0]).Name;
            }
            return map;
        }

        private static Dictionary<Type, string> BuildCategoryMap()
        {
            Dictionary<Type, string> map = new Dictionary<Type, string>();
            foreach (Type type in EngineTypes.All)
            {
                object[] attributes = type.GetCustomAttributes(typeof(A_XSDTypeAttribute), true);
                if (attributes.Length > 0) map[type] = ((A_XSDTypeAttribute)attributes[0]).Category;
            }
            return map;
        }
        #endregion
    }
}