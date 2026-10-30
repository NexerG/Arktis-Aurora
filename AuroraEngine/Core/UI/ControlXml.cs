using ArctisAurora.Core.Filing.Serialization;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Registry.Assets;
using ArctisAurora.Core.UI.Menus;
using ArctisAurora.EngineWork.Registry;
using System.Collections;
using System.ComponentModel;
using System.Reflection;
using System.Xml.Linq;

namespace ArctisAurora.Core.UI
{
    public partial class Control
    {
        private static (MethodInfo method, A_XSDActionDependencyAttribute attr)[] _taggedActions = null!;

        // Every [A_XSDActionDependency] in the process, scanned once and shared by every load.
        private static (MethodInfo method, A_XSDActionDependencyAttribute attr)[] TaggedActions =>
            _taggedActions ??= AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => a.GetTypes())
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
                .Select(m => (method: m, attr: m.GetCustomAttribute<A_XSDActionDependencyAttribute>()))
                .Where(x => x.attr != null)
                .ToArray();

        // Builds a tree from a document named in the uiDocuments registry.
        public static Control ParseXML(string document)
        {
            UIDocumentAsset asset = AssetRegistries.GetAsset<UIDocumentAsset>(document);
            return Parse(DataFiles.LoadXml(asset.path));
        }

        // Builds a tree from a document's root element, which names its own type.
        private static Control Parse(XElement root)
        {
            Control control = (Control)Activator.CreateInstance(AnyXMLType.FindType(root.Name.LocalName));
            ResolveAttributes(root, control, TaggedActions);

            // A root has no parent to be arranged by, so it starts at its authored size and the
            // window's FitTo replaces that on the first resize.
            if (control is WindowRoot window)
            {
                window.WriteArranged(new LayoutRect(0, 0, window.preferredWidth, window.preferredHeight));
                UIEngine.RegisterDirtyRoot(window);
            }

            RecursiveParse(root, control, TaggedActions);
            return control;
        }

        // Builds a context menu from a menu document.
        internal static ContextMenu ParseMenu(string path)
        {
            ContextMenu menu = new ContextMenu();
            RecursiveParse(DataFiles.LoadXml(path), menu, TaggedActions);
            return menu;
        }

        private static void RecursiveParse(XElement root, object owner, (MethodInfo method, A_XSDActionDependencyAttribute attr)[] tagged)
        {
            foreach (XElement element in root.Elements())
            {
                Type type = AnyXMLType.FindType(element.Name.LocalName);
                object control = Activator.CreateInstance(type);
                ResolveAttributes(element, control, tagged);

                // Not a control — the owner holds it in the one List<> whose element type accepts it.
                if (!typeof(Control).IsAssignableFrom(type))
                {
                    FieldInfo field = owner.GetType()
                        .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        .FirstOrDefault(f => f.FieldType.IsGenericType &&
                                             f.FieldType.GetGenericTypeDefinition() == typeof(List<>)
                                             && f.FieldType.GetGenericArguments()[0].IsAssignableFrom(control.GetType()));
                    IList list = (IList)field.GetValue(owner);

                    list.Add(control);
                    RecursiveParse(element, control, tagged);
                    continue;
                }
                ((Control)owner).AddChild((Control)control);
                RecursiveParse(element, control, tagged);
            }
        }

        private static void ResolveAttributes(XElement root, object topControl, (MethodInfo method, A_XSDActionDependencyAttribute attr)[] tagged)
        {
            foreach (XAttribute attr in root.Attributes())
            {
                MemberInfo prop = topControl.GetType().GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase).FirstOrDefault(m =>
                {
                    // Attribute.GetCustomAttribute, not MemberInfo's — the latter ignores `inherit`
                    // on a property, so an override reads as unannotated and its attribute is
                    // dropped without a word.
                    A_XSDElementPropertyAttribute? a = Attribute.GetCustomAttribute(m, typeof(A_XSDElementPropertyAttribute), true) as A_XSDElementPropertyAttribute;
                    return a != null && string.Equals(a.Name, attr.Name.LocalName, StringComparison.OrdinalIgnoreCase);
                });

                if (prop == null) continue;

                Type memberType = prop.MemberType == MemberTypes.Field ? ((FieldInfo)prop).FieldType : ((PropertyInfo)prop).PropertyType;

                if (memberType == typeof(Action))
                {
                    MethodInfo? methodInfo = tagged
                        .FirstOrDefault(x => string.Equals(x.attr.Name, attr.Value, StringComparison.OrdinalIgnoreCase))
                        .method;

                    if (methodInfo == null)
                        throw new Exception($"Action method '{attr.Value}' not found in A_XSDActionDependency.");

                    Action actionDelegate = (Action)Delegate.CreateDelegate(typeof(Action), methodInfo);
                    if (prop is PropertyInfo actionProperty)
                    {
                        Action current = (Action?)actionProperty.GetValue(topControl);
                        actionProperty.SetValue(topControl, current + actionDelegate);
                        continue;
                    }
                    if (prop is FieldInfo actionField)
                    {
                        Action current = (Action?)actionField.GetValue(topControl);
                        actionField.SetValue(topControl, current + actionDelegate);
                        continue;
                    }
                    continue;
                }

                if (memberType == typeof(Func<PointerEvent, bool>))
                {
                    MethodInfo? pointerMethod = tagged
                        .FirstOrDefault(x => string.Equals(x.attr.Name, attr.Value, StringComparison.OrdinalIgnoreCase))
                        .method;

                    if (pointerMethod == null)
                        throw new Exception($"Action method '{attr.Value}' not found in A_XSDActionDependency.");

                    Action bound = (Action)Delegate.CreateDelegate(typeof(Action), pointerMethod);
                    Func<PointerEvent, bool> handler = _ => { bound(); return true; };

                    if (prop is PropertyInfo handlerProperty)
                    {
                        Func<PointerEvent, bool>? currentHandler = (Func<PointerEvent, bool>?)handlerProperty.GetValue(topControl);
                        handlerProperty.SetValue(topControl, currentHandler + handler);
                        continue;
                    }
                    if (prop is FieldInfo handlerField)
                    {
                        Func<PointerEvent, bool>? currentHandler = (Func<PointerEvent, bool>?)handlerField.GetValue(topControl);
                        handlerField.SetValue(topControl, currentHandler + handler);
                        continue;
                    }
                    continue;
                }

                if (memberType.IsEnum)
                {
                    object enumValue = Enum.Parse(memberType, attr.Value, true);
                    if (prop is PropertyInfo enumProperty) enumProperty.SetValue(topControl, enumValue);
                    else ((FieldInfo)prop).SetValue(topControl, enumValue);
                    continue;
                }

                object value = TypeDescriptor.GetConverter(memberType).ConvertFromInvariantString(attr.Value);
                if (prop is PropertyInfo propertyInfo) propertyInfo.SetValue(topControl, value);
                else ((FieldInfo)prop).SetValue(topControl, value);
            }
        }
    }
}
