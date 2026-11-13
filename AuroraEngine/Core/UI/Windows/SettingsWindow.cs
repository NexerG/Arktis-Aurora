using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Filing.Serialization;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.EngineWork;
using ArctisAurora.EngineWork.Registry;
using ArctisAurora.EngineWork.Rendering;
using Silk.NET.Vulkan;
using System.ComponentModel;
using System.Reflection;

namespace ArctisAurora.Core.UI.Windows
{
    // Every settings group the registry knows, plus the active keybinds, as one screen. The shell is
    // Settings.ui.axml — frame, title bar and star sizing come from there; only the rows are reflected,
    // because a new setting has to appear without anyone authoring it. Built per open and closed by
    // its own title bar, the same as a torn-off tab window.
    public static class SettingsWindow
    {
        private static readonly Diagnostics.LogChannel Log = Diagnostics.LogChannel.For("Settings");

        private const string windowName = "settings";
        private const string document = "settings";
        private const uint windowWidth = 720;
        private const uint windowHeight = 480;
        internal const string keybindsCategory = "Keybinds";
        internal const string fileTypesCategory = "File types";

        // layout
        private const int rowHeight = 26;
        private const int labelWidth = 170;
        private const int editorWidth = 220;
        private const int categoryWidth = 144;

        private static StackPanelControl _rows = null!;
        private static ButtonControl? _apply;
        private static bool _keybindsMoved;

        public static unsafe void Open(RenderWindow source)
        {
            if (source == null) return;

            // One screen at a time; a second ask raises the one already up.
            if (Engine.windows.TryGetValue(windowName, out RenderWindow existing))
            {
                existing.os.Show();
                existing.os.Focus();
                return;
            }

            Diagnostics.Profiling.Zone.Start("Settings.CreateWindow");
            uint width = UIScaling.ToPixels(source, windowWidth);
            uint height = UIScaling.ToPixels(source, windowHeight);
            RenderWindow window = Engine.OpenMenuWindow(windowName, width, height, true);
            Diagnostics.Profiling.Zone.End("Settings.CreateWindow");

            Diagnostics.Profiling.Zone.Start("Settings.Parse");
            WindowRoot root = (WindowRoot)Control.ParseXML(document);
            window.ui.uiRoot = root;
            Diagnostics.Profiling.Zone.End("Settings.Parse");

            StackPanelControl categories = (StackPanelControl)root.FindByName("Categories");
            _rows = (StackPanelControl)root.FindByName("Rows");
            _apply = root.FindByName("Apply") as ButtonControl;
            RefreshApply();

            Diagnostics.Profiling.Zone.Start("Settings.Categories");
            foreach (string category in Categories())
            {
                string named = category;
                ButtonControl button = Button(named, () => ShowCategory(named), categoryWidth);
                button.horizontalPosition = 0f;
                categories.AddChild(button);
            }
            Diagnostics.Profiling.Zone.End("Settings.Categories");

            root.FitTo(window.os.windowSize);

            Diagnostics.Profiling.Zone.Start("Settings.ShowCategory");
            ShowCategory(FirstCategory());
            Diagnostics.Profiling.Zone.End("Settings.ShowCategory");

            AGlfwWindow._glfw.GetWindowPos(source.os.handle, out int sx, out int sy);
            AGlfwWindow._glfw.GetWindowSize(source.os.handle, out int sw, out int sh);
            window.os.SetPosition(sx + (sw - (int)width) / 2, sy + (sh - (int)height) / 2);

            Diagnostics.Profiling.Zone.Start("Settings.Show");
            window.os.Show();
            window.os.Focus();
            window.os.SeedIsInWindow();
            Diagnostics.Profiling.Zone.End("Settings.Show");
        }

        [A_XSDActionDependency("Settings.Open", "UI", "Opens the settings screen over the window that asked")]
        public static void Open() => Open(UIActions.Invoking());

        // Saving is what applies — the rows already hold the new values, so this is where OnChanged
        // fires and the user's file is written.
        [A_XSDActionDependency("Settings.Save", "UI", "Applies and writes the settings screen, then closes it")]
        public static void Save()
        {
            RenderWindow window = UIActions.Invoking();
            Commit();
            if (window != null) WindowActions.Close(window);
        }

        [A_XSDActionDependency("Settings.Apply", "UI", "Applies and writes the settings screen, keeping it open")]
        public static void Apply() => Commit();

        private static void Commit()
        {
            SettingsRegistry.Commit();
            _keybindsMoved = false;
            RefreshApply();
        }

        // Greys Apply when nothing moved since the last commit.
        private static void RefreshApply()
        {
            if (_apply != null) _apply.enabled = _keybindsMoved || SettingsRegistry.Pending();
        }

        #region ---- rows ----
        private static IEnumerable<string> Categories()
        {
            foreach (ISettingsGroup group in SettingsRegistry.Groups.Values)
                if (group is SettingCategory)
                    yield return NameOf(group.GetType());

            yield return keybindsCategory;
            if (FileAssociations.enabled) yield return fileTypesCategory;
        }

        private static string FirstCategory() => Categories().First();

        private static string NameOf(Type type) =>
            type.GetCustomAttribute<A_XSDTypeAttribute>(false)?.Name ?? type.Name;

        internal static void ShowCategory(string category)
        {
            foreach (Entity child in _rows.children.ToArray())
                child.Destroy();

            if (category == keybindsCategory) FillKeybinds();
            else if (category == fileTypesCategory) FillFileTypes();
            else FillSettings(category);

            _rows.InvalidateLayout();
        }

        // An App-scoped setting is the build's decision, so it is not shown at all rather than shown
        // and refused.
        private static void FillSettings(string category)
        {
            foreach (ISettingsGroup group in SettingsRegistry.Groups.Values)
            {
                if (group is not SettingCategory settings || NameOf(group.GetType()) != category) continue;

                foreach (Setting setting in settings.settings)
                {
                    if (setting.scope == SettingScope.App) continue;

                    foreach (MemberInfo member in Setting.ValueMembers(setting.GetType()))
                        _rows.AddChild(Row(
                            $"{SettingCategory.NameOf(setting)} {member.GetCustomAttribute<A_XSDElementPropertyAttribute>().Name}",
                            Editor(setting, member)));
                }
            }
        }

        private static void FillKeybinds()
        {
            InputHandler handler = Engine.inputHandler;
            if (handler == null) return;

            foreach (KeybindDefinition bind in handler.gestureMatcher.ActiveBinds)
            {
                if (bind.actionName.Length == 0) continue;
                _rows.AddChild(Row(bind.actionName, KeybindEditor(bind)));
            }
        }

        private static void FillFileTypes()
        {
            bool allDefault = true;
            foreach (FileType type in FileAssociations.Registered)
            {
                bool isDefault = FileAssociations.IsDefault(type);
                allDefault &= isDefault;
                _rows.AddChild(Row($"{type.description} ({type.suffix})", DefaultEditor("Make default", () => FileAssociations.Claim(type), isDefault)));
            }

            _rows.AddChild(Row("All types", DefaultEditor("Make default for all", FileAssociations.ClaimAll, allDefault)));
        }

        // A claim button, greyed with a "Default" label beside it once the claim already holds.
        private static Control DefaultEditor(string caption, Action claim, bool isDefault)
        {
            StackPanelControl editor = new StackPanelControl
            {
                orientation = StackPanelControl.Orientation.Horizontal,
                alpha = 0f,
                Spacing = 10
            };

            ButtonControl button = Button(caption, () => { claim(); ShowCategory(fileTypesCategory); }, 150);
            button.enabled = !isDefault;
            editor.AddChild(button);
            if (isDefault) editor.AddChild(new LabelControl { text = "Default", fontSize = 14, role = PaletteRole.MutedInk });
            return editor;
        }

        private static Control Row(string caption, Control editor)
        {
            StackPanelControl row = new StackPanelControl
            {
                orientation = StackPanelControl.Orientation.Horizontal,
                alpha = 0f,
                preferredHeight = rowHeight,
                Spacing = 10
            };

            row.AddChild(new LabelControl
            {
                text = caption,
                fontSize = 14,
                preferredWidth = labelWidth,
                horizontalPosition = 0f
            });
            row.AddChild(editor);

            return row;
        }
        #endregion

        #region ---- editors ----
        // The editor a member's type asks for. An enum restricted by [A_XSDDomain] offers the
        // domain's variants, which is the same set the loader would accept.
        private static Control Editor(Setting setting, MemberInfo member)
        {
            Type memberType = XmlReflection.MemberType(member);
            object current = XmlReflection.GetMember(member, setting);

            if (memberType == typeof(bool))
            {
                CheckBoxControl box = new CheckBoxControl
                {
                    role = PaletteRole.SubField,
                    isChecked = (bool)current
                };
                box.onChanged = value =>
                {
                    XmlReflection.SetMember(member, setting, value);
                    RefreshApply();
                };
                return box;
            }

            if (memberType.IsEnum)
            {
                Type domain = A_XSDDomainAttribute.DomainOf(member) ?? memberType;

                string[] names = Enum.GetNames(domain);
                if (names.Length >= 2 && names.Length <= 4)
                {
                    SwitchControl choice = new SwitchControl
                    {
                        options = names.Select(Words).ToArray(),
                        selected = Array.IndexOf(names, current?.ToString())
                    };
                    choice.onChanged = index =>
                    {
                        XmlReflection.SetMember(member, setting, Enum.Parse(memberType, names[index]));
                        RefreshApply();
                    };
                    return choice;
                }

                DropdownControl dropdown = new DropdownControl
                {
                    preferredWidth = editorWidth,
                    preferredHeight = rowHeight,
                    role = PaletteRole.SubField,
                    cornerRole = CornerRole.Control,
                    options = Enum.GetNames(domain),
                    selected = current?.ToString() ?? ""
                };
                dropdown.onPicked = value =>
                {
                    XmlReflection.SetMember(member, setting, Enum.Parse(memberType, value));
                    RefreshApply();
                };
                return dropdown;
            }

            if (setting is PaletteSetting paletteSetting)
            {
                DropdownControl dropdown = new DropdownControl
                {
                    preferredWidth = editorWidth,
                    preferredHeight = rowHeight,
                    role = PaletteRole.SubField,
                    cornerRole = CornerRole.Control,
                    options = Palettes.Names,
                    selected = paletteSetting.name
                };
                dropdown.onPicked = value =>
                {
                    paletteSetting.name = value;
                    RefreshApply();
                    Palettes.Switch(Palettes.Get(value)!);
                };
                return dropdown;
            }

            TextBoxControl field = new TextBoxControl
            {
                preferredWidth = editorWidth,
                preferredHeight = rowHeight,
                role = PaletteRole.SubField,
                fontSize = 14,
                padding = new Thickness(0, 6, 0, 8),
                text = current?.ToString() ?? ""
            };
            field.onCommit = text =>
            {
                try
                {
                    XmlReflection.SetMember(member, setting,
                        TypeDescriptor.GetConverter(memberType).ConvertFromInvariantString(text));
                }
                catch (Exception)
                {
                    Log.Warn($"'{text}' is not a valid {memberType.Name} — keeping {XmlReflection.GetMember(member, setting)}.");
                    field.text = XmlReflection.GetMember(member, setting)?.ToString() ?? "";
                }
                RefreshApply();
            };
            return field;
        }

        // "LastVault" -> "Last vault".
        private static string Words(string name)
        {
            System.Text.StringBuilder words = new System.Text.StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                if (i > 0 && char.IsUpper(name[i])) words.Append(' ').Append(char.ToLowerInvariant(name[i]));
                else words.Append(name[i]);
            }
            return words.ToString();
        }

        // A locked bind is the build's, so it reads as text with nothing to press.
        private static Control KeybindEditor(KeybindDefinition bind)
        {
            List<Keys> modifiers = bind.modifiers.Select(m => m.key).ToList();

            if (bind.access == KeybindAccess.Locked)
                return new LabelControl
                {
                    text = KeyCaptureControl.Describe(bind.trigger, modifiers),
                    fontSize = 14,
                    role = PaletteRole.MutedInk,
                    preferredWidth = editorWidth,
                    horizontalPosition = 0f
                };

            KeyCaptureControl capture = new KeyCaptureControl
            {
                preferredWidth = editorWidth,
                preferredHeight = rowHeight,
                role = PaletteRole.SubField,
                cornerRole = CornerRole.Control
            };
            capture.SetCombo(bind.trigger, modifiers);
            capture.onCaptured = (trigger, held) =>
            {
                List<KeybindModifier> bound = held.Select(k => new KeybindModifier { key = k }).ToList();
                if (!Engine.inputHandler.gestureMatcher.Rebind(bind.actionName, trigger, bound))
                {
                    capture.SetCombo(bind.trigger, bind.modifiers.Select(m => m.key));
                    return;
                }
                SettingsRegistry.Get<InputBindings>().Remember(bind.actionName, trigger, bound);
                _keybindsMoved = true;
                RefreshApply();
            };
            return capture;
        }

        private static ButtonControl Button(string caption, Action action, int width = 90)
        {
            ButtonControl button = new ButtonControl
            {
                preferredWidth = width,
                preferredHeight = 26,
                role = PaletteRole.Chrome,
                cornerRole = CornerRole.Control
            };
            button.AddChild(new LabelControl { text = caption, fontSize = 14, role = PaletteRole.MutedInk });
            button.RegisterOnRelease(_ => { action(); return true; });
            return button;
        }
        #endregion
    }
}
