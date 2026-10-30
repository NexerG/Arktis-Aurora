using ArctisAurora.Core.Animation;
using ArctisAurora.Core.Data;
using ArctisAurora.Core.Filing.Serialization;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.EngineWork;
using ArctisAurora.EngineWork.Rendering;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace ArctisAurora.Core.UI
{
    // One paint table slot on the GPU.
    [StructLayout(LayoutKind.Sequential), A_XSDType("GpuPaint", "DataPools")]
    public struct GpuPaint
    {
        public Vector4 color;
    }

    [A_XSDType("PaletteRole", "UI")]
    public enum PaletteRole
    {
        None, Clear,
        Ground, Surface, Chrome, Field, SubField, Line, Accent, Danger,
        Ink, MutedInk,
        Doc, Sheet, Planner
    }

    // Which palette radius a code-built control takes. TabEnd rounds only the top right.
    public enum CornerRole
    {
        None, Row, Tab, TabEnd, Control, Popup
    }

    // Which palette accent bar a code-built control draws: Row on the left, Tab and TabIdle on the top.
    public enum AccentRole
    {
        None, Row, Tab, TabIdle
    }

    [A_XSDType("WindowCorners", "UI")]
    public enum WindowCorners
    {
        Round, Small, Square
    }

    [A_XSDType("Palette", "UI")]
    public class PaletteDefinition
    {
        [A_XSDElementProperty("Name", "UI", "What a control's Palette attribute calls this palette.")]
        public string name = "";

        // surfaces
        [A_XSDElementProperty("Ground", "UI", "Window background, as a hex code.")]
        public string ground = "";
        [A_XSDElementProperty("Surface", "UI", "Panels and sidebars, as a hex code.")]
        public string surface = "";
        [A_XSDElementProperty("Chrome", "UI", "Title bars, toolbars and tab strips, as a hex code.")]
        public string chrome = "";
        [A_XSDElementProperty("Field", "UI", "Input boxes, as a hex code.")]
        public string field = "";
        [A_XSDElementProperty("SubField", "UI", "Input boxes that sit on a panel or a field, as a hex code.")]
        public string subField = "";
        [A_XSDElementProperty("Line", "UI", "Separators, splitters and borders, as a hex code.")]
        public string line = "";
        [A_XSDElementProperty("Accent", "UI", "Highlights and active state, as a hex code.")]
        public string accent = "";
        [A_XSDElementProperty("Danger", "UI", "Destructive actions, as a hex code.")]
        public string danger = "";
        [A_XSDElementProperty("EdgeAccent", "UI", "Control edges, as a hex code. Accent when left out.")]
        public string edgeAccent = "";
        [A_XSDElementProperty("IdleAccent", "UI", "Accent bar of the active tab in a pane that is not focused, as a hex code. Line stepped toward its text when left out.")]
        public string idleAccent = "";

        // text
        [A_XSDElementProperty("DarkInk", "UI", "Text on light backgrounds, as a hex code.")]
        public string darkInk = "";
        [A_XSDElementProperty("LightInk", "UI", "Text on dark backgrounds, as a hex code.")]
        public string lightInk = "";

        // code block colours, optional
        [A_XSDElementProperty("Keyword", "UI", "Keywords and tag names in code blocks, as a hex code. Accent when left out.")]
        public string keyword = "";
        [A_XSDElementProperty("String", "UI", "String literals in code blocks, as a hex code. Accent when left out.")]
        public string stringLiteral = "";
        [A_XSDElementProperty("Number", "UI", "Numbers in code blocks, as a hex code. Accent when left out.")]
        public string number = "";
        [A_XSDElementProperty("Comment", "UI", "Comments in code blocks, as a hex code. Muted text when left out.")]
        public string comment = "";

        // file kind colours, optional
        [A_XSDElementProperty("Doc", "UI", "Icons of notes and documents in file browsers, as a hex code. Muted text when left out.")]
        public string doc = "";
        [A_XSDElementProperty("Sheet", "UI", "Icons of sheets in file browsers, as a hex code. Muted text when left out.")]
        public string sheet = "";
        [A_XSDElementProperty("Planner", "UI", "Icons of planners in file browsers, as a hex code. Muted text when left out.")]
        public string planner = "";

        // derivation
        [A_XSDElementProperty("Step", "UI", "How far each hover or press step moves a colour toward its text colour, 0 to 1.")]
        public float step = 0.04f;
        [A_XSDElementProperty("Muted", "UI", "How far muted text blends toward the colour behind it, 0 to 1.")]
        public float muted = 0.24f;

        // shape
        [A_XSDElementProperty("RowRadius", "UI", "Corner radius of list and browser rows, in design-space pixels.")]
        public float rowRadius = 0f;
        [A_XSDElementProperty("TabRadius", "UI", "Top corner radius of tabs, in design-space pixels.")]
        public float tabRadius = 0f;
        [A_XSDElementProperty("ControlRadius", "UI", "Corner radius of buttons, fields and dropdowns built in code, in design-space pixels.")]
        public float controlRadius = 4f;
        [A_XSDElementProperty("PopupRadius", "UI", "Corner radius of context menus, in design-space pixels.")]
        public float popupRadius = 6f;
        [A_XSDElementProperty("RowAccentWidth", "UI", "Width of the accent bar on the current row, in design-space pixels.")]
        public float rowAccentWidth = 3f;
        [A_XSDElementProperty("TabAccentWidth", "UI", "Width of the accent bar on the active tab, in design-space pixels.")]
        public float tabAccentWidth = 2f;
        [A_XSDElementProperty("WindowCorners", "UI", "How the OS rounds the window's corners.")]
        public WindowCorners windowCorners = WindowCorners.Round;
        [A_XSDElementProperty("StateFrequency", "UI", "How fast hover and press states ease, in Hz.")]
        public float stateFrequency = 6f;
        [A_XSDElementProperty("StateDamping", "UI", "How hover and press states settle: 1 settles without overshoot, below 1 bounces.")]
        public float stateDamping = 1f;
        [A_XSDElementProperty("ThemeFade", "UI", "Seconds the app takes to fade into this palette when it is picked.")]
        public float themeFade = 0.3f;

        // first slot of this palette's block in the paint table
        internal uint firstSlot;
    }

    // Loaded palettes and the paint table their colours live in. A paint word is a slot in that table,
    // an 0xRRGGBB colour with inlineBit set, or a gradient with gradientBit set.
    public static class Palettes
    {
        private static readonly Diagnostics.LogChannel Log = Diagnostics.LogChannel.For("UI");

        public const uint inlineBit = 0x80000000;
        public const uint gradientBit = 0x40000000;

        // no fill at all; the edge still draws
        public const uint clear = inlineBit | gradientBit;

        // block layout: surfaces × states, ink and muted ink per surface, the two raw inks, the edge accent,
        // the ground's ink stepped once, the four code colours, the idle accent, then the three file kinds
        private const uint surfaceCount = 8;
        private const uint stateCount = 3;
        private const uint inkBase = surfaceCount * stateCount;
        private const uint rawInkBase = inkBase + surfaceCount * 2;
        private const uint edgeAccentSlot = rawInkBase + 2;
        private const uint inkStepSlot = edgeAccentSlot + 1;
        private const uint codeBase = inkStepSlot + 1;
        private const uint idleAccentSlot = codeBase + 4;
        private const uint fileKindBase = idleAccentSlot + 1;
        internal const uint blockSize = fileKindBase + 3;

        private static readonly Dictionary<string, PaletteDefinition> byName =
            new Dictionary<string, PaletteDefinition>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<PaletteDefinition> blocks = new List<PaletteDefinition>();

        // the paint table; slot 0 is transparent black, so a zeroed row paints nothing
        public static DataPool Paints { get; private set; } = null!;

        // the table's load-time colours, which main-thread decisions read instead of the animated pool
        private static Vector4[] baked = Array.Empty<Vector4>();
        private static readonly List<Vector4> baking = new List<Vector4>();

        public static PaletteDefinition Default { get; internal set; } = null!;

        // Loaded palette names, in load order.
        public static IReadOnlyList<string> Names => blocks.Select(palette => palette.name).ToArray();

        // An empty name is no palette. An unknown name is an authoring error, not a fallback.
        public static PaletteDefinition? Get(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (byName.TryGetValue(name, out PaletteDefinition palette)) return palette;
            throw new Exception($"Palette '{name}' is not defined in any Palettes/*.palette.axml.");
        }

        // Crossfades the app to a palette, then makes it the default and re-rounds every window.
        public static void Switch(PaletteDefinition to)
        {
            PaletteDefinition from = Default;
            if (to == from) return;

            Animations.FadeSlots(from.firstSlot, to.firstSlot, (int)blockSize, to.themeFade, Curve.Ease(EaseKind.CubicInOut), () =>
            {
                Default = to;
                foreach (RenderWindow window in Engine.windows.Values)
                    window.os.RoundCorners();
            });
        }

        [A_XSDActionDependency("Palettes.Set", "Console")]
        private static string Set(string name)
        {
            if (!byName.TryGetValue(name, out PaletteDefinition palette)) return $"error: no palette named {name}";
            Switch(palette);
            return "ok";
        }

        [A_XSDActionDependency("Palettes.LoadPalettes", "Bootstrap")]
        public static bool LoadPalettes()
        {
            byName.Clear();
            blocks.Clear();
            Paints = DataManager.Get("Paints");
            Paints.Rewind();
            baking.Clear();
            Put(Vector4.Zero);

            foreach (string file in VirtualFileSystem.EnumerateAll("XML/Documents/Palettes", "*.palette.axml"))
            {
                PaletteDefinition palette = Parse(DataFiles.LoadXml(file));
                palette.firstSlot = (uint)Paints.Count;
                Bake(palette);
                byName[palette.name] = palette;
                blocks.Add(palette);
            }

            string chosen = SettingsRegistry.Get<UISettings>().palette.name;
            if (!byName.TryGetValue(chosen, out PaletteDefinition fallback))
            {
                Log.Error($"no palette named '{chosen}' — a tree that names no palette has nothing to paint with.");
                return false;
            }

            Default = fallback;
            baked = baking.ToArray();
            Log.Debug($"loaded {blocks.Count} palette(s) into {Paints.Count} paint slots.");
            return true;
        }

        #region ---- paint words ----
        public static uint Inline(Vector3 rgb)
        {
            uint r = (uint)MathF.Round(Math.Clamp(rgb.X, 0f, 1f) * 255f);
            uint g = (uint)MathF.Round(Math.Clamp(rgb.Y, 0f, 1f) * 255f);
            uint b = (uint)MathF.Round(Math.Clamp(rgb.Z, 0f, 1f) * 255f);
            return inlineBit | r << 16 | g << 8 | b;
        }

        public static uint Inline(string hex) => Inline(Control.HexToRGB(hex));

        public static bool IsInline(uint paint) => (paint & inlineBit) != 0;

        // The colour a word paints, as the shader reads it.
        public static Vector3 ColorOf(uint paint)
        {
            if (IsInline(paint))
                return new Vector3((paint >> 16) & 0xFF, (paint >> 8) & 0xFF, paint & 0xFF) / 255f;

            if (paint >= baked.Length) return Vector3.Zero;
            Vector4 color = baked[paint];
            return new Vector3(color.X, color.Y, color.Z);
        }
        #endregion

        #region ---- resolving ----
        // A surface role's slot: state 0 rest, 1 hover, 2 press.
        public static uint Surface(PaletteDefinition palette, PaletteRole role, uint state = 0)
            => palette.firstSlot + (uint)(role - PaletteRole.Ground) * stateCount + state;

        // The slot every unauthored edge paints with.
        public static uint EdgeAccent(PaletteDefinition palette) => palette.firstSlot + edgeAccentSlot;

        // The slot an unfocused pane's active tab bar paints with.
        public static uint IdleAccent(PaletteDefinition palette) => palette.firstSlot + idleAccentSlot;

        // A code token's slot; Plain has none.
        public static uint Code(PaletteDefinition palette, SyntaxToken token) => palette.firstSlot + codeBase + (uint)token - 1;

        // A file kind role's slot: Doc, Sheet or Planner.
        public static uint FileKind(PaletteDefinition palette, PaletteRole role) => palette.firstSlot + fileKindBase + (uint)(role - PaletteRole.Doc);

        // A gradient stop's role as offsets inside any palette block: its colour, and that colour stepped once.
        public static (uint rest, uint stepped) RoleOffsets(PaletteRole role)
        {
            if (role == PaletteRole.Ink) return (inkBase, inkStepSlot);
            if (role == PaletteRole.MutedInk) return (inkBase + 1, inkBase + 1);
            if (role < PaletteRole.Ground || role > PaletteRole.Danger)
                throw new Exception($"A gradient stop cannot take role {role}; only surfaces, Ink and MutedInk.");

            uint rest = (uint)(role - PaletteRole.Ground) * stateCount;
            return (rest, rest + 1);
        }

        // Text on a ground. A surface of this palette has its ink baked; any other ground picks between
        // the palette's two inks here and mixes a muted one inline.
        public static uint Ink(PaletteDefinition palette, uint ground, bool muted)
        {
            if (TryBlock(ground, out PaletteDefinition owner, out uint offset) && owner == palette && offset < inkBase)
                return palette.firstSlot + inkBase + offset / stateCount * 2 + (muted ? 1u : 0u);

            Vector3 back = ColorOf(ground);
            uint raw = palette.firstSlot + rawInkBase + (PicksDark(palette, back) ? 0u : 1u);
            if (!muted) return raw;

            return Inline(Vector3.Lerp(ColorOf(raw), back, palette.muted));
        }

        // A resting colour moved toward its text by state steps. A surface slot at rest steps inside its
        // own block; anything else is mixed inline with this palette's inks.
        public static uint Step(PaletteDefinition palette, uint rest, uint state)
        {
            if (state == 0) return rest;
            if (IsSurfaceRest(rest))
                return rest + state;

            Vector3 color = ColorOf(rest);
            Vector3 ink = ColorOf(palette.firstSlot + rawInkBase + (PicksDark(palette, color) ? 0u : 1u));
            return Inline(Vector3.Lerp(color, ink, palette.step * state));
        }

        // A surface's rest slot, whose hover and press slots follow it.
        public static bool IsSurfaceRest(uint paint)
            => TryBlock(paint, out _, out uint offset) && offset < inkBase && offset % stateCount == 0;

        // WCAG contrast ratio, 1 to 21.
        public static float Contrast(Vector3 a, Vector3 b)
        {
            float la = Luminance(a);
            float lb = Luminance(b);
            return (MathF.Max(la, lb) + 0.05f) / (MathF.Min(la, lb) + 0.05f);
        }

        private static bool PicksDark(PaletteDefinition palette, Vector3 back)
        {
            Vector4 dark = baked[palette.firstSlot + rawInkBase];
            Vector4 light = baked[palette.firstSlot + rawInkBase + 1];
            return Contrast(new Vector3(dark.X, dark.Y, dark.Z), back) >= Contrast(new Vector3(light.X, light.Y, light.Z), back);
        }

        // The block a slot sits in, and its offset inside it.
        private static bool TryBlock(uint paint, out PaletteDefinition owner, out uint offset)
        {
            owner = null!;
            offset = 0;
            if (IsInline(paint) || paint == 0) return false;

            int index = (int)((paint - 1) / blockSize);
            if (index >= blocks.Count) return false;

            owner = blocks[index];
            offset = paint - owner.firstSlot;
            return true;
        }

        private static float Luminance(Vector3 c) => 0.2126f * Linear(c.X) + 0.7152f * Linear(c.Y) + 0.0722f * Linear(c.Z);

        private static float Linear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        #endregion

        #region ---- loading ----
        private static PaletteDefinition Parse(XElement element)
        {
            PaletteDefinition palette = new PaletteDefinition
            {
                name = element.Attribute("Name")?.Value ?? "",
                ground = Required(element, "Ground"),
                surface = Required(element, "Surface"),
                chrome = Required(element, "Chrome"),
                field = Required(element, "Field"),
                subField = Required(element, "SubField"),
                line = Required(element, "Line"),
                accent = Required(element, "Accent"),
                danger = Required(element, "Danger"),
                edgeAccent = element.Attribute("EdgeAccent")?.Value ?? "",
                idleAccent = element.Attribute("IdleAccent")?.Value ?? "",
                keyword = element.Attribute("Keyword")?.Value ?? "",
                stringLiteral = element.Attribute("String")?.Value ?? "",
                number = element.Attribute("Number")?.Value ?? "",
                comment = element.Attribute("Comment")?.Value ?? "",
                doc = element.Attribute("Doc")?.Value ?? "",
                sheet = element.Attribute("Sheet")?.Value ?? "",
                planner = element.Attribute("Planner")?.Value ?? "",
                darkInk = Required(element, "DarkInk"),
                lightInk = Required(element, "LightInk")
            };

            string step = element.Attribute("Step")?.Value;
            if (!string.IsNullOrEmpty(step)) palette.step = float.Parse(step, CultureInfo.InvariantCulture);
            string muted = element.Attribute("Muted")?.Value;
            if (!string.IsNullOrEmpty(muted)) palette.muted = float.Parse(muted, CultureInfo.InvariantCulture);

            palette.rowRadius = Optional(element, "RowRadius", palette.rowRadius);
            palette.tabRadius = Optional(element, "TabRadius", palette.tabRadius);
            palette.controlRadius = Optional(element, "ControlRadius", palette.controlRadius);
            palette.popupRadius = Optional(element, "PopupRadius", palette.popupRadius);
            palette.rowAccentWidth = Optional(element, "RowAccentWidth", palette.rowAccentWidth);
            palette.tabAccentWidth = Optional(element, "TabAccentWidth", palette.tabAccentWidth);
            palette.stateFrequency = Optional(element, "StateFrequency", palette.stateFrequency);
            palette.stateDamping = Optional(element, "StateDamping", palette.stateDamping);
            palette.themeFade = Optional(element, "ThemeFade", palette.themeFade);
            string corners = element.Attribute("WindowCorners")?.Value;
            if (!string.IsNullOrEmpty(corners)) palette.windowCorners = Enum.Parse<WindowCorners>(corners);

            return palette;
        }

        private static float Optional(XElement element, string attribute, float fallback)
        {
            string value = element.Attribute(attribute)?.Value;
            return string.IsNullOrEmpty(value) ? fallback : float.Parse(value, CultureInfo.InvariantCulture);
        }

        private static string Required(XElement element, string attribute)
        {
            string value = element.Attribute(attribute)?.Value;
            if (string.IsNullOrEmpty(value))
                throw new Exception($"Palette '{element.Attribute("Name")?.Value}' has no {attribute}.");
            return value;
        }

        // Appends one slot to the paint table.
        private static void Put(Vector4 color)
        {
            int row = Paints.Append();
            Paints.GetSpan<GpuPaint>()[row].color = color;
            baking.Add(color);
        }

        private static void Bake(PaletteDefinition palette)
        {
            Vector3 dark = Control.HexToRGB(palette.darkInk);
            Vector3 light = Control.HexToRGB(palette.lightInk);
            Vector3[] surfaces =
            {
                Control.HexToRGB(palette.ground), Control.HexToRGB(palette.surface), Control.HexToRGB(palette.chrome),
                Control.HexToRGB(palette.field), Control.HexToRGB(palette.subField), Control.HexToRGB(palette.line),
                Control.HexToRGB(palette.accent), Control.HexToRGB(palette.danger)
            };

            foreach (Vector3 color in surfaces)
            {
                Vector3 ink = Contrast(dark, color) >= Contrast(light, color) ? dark : light;
                for (uint state = 0; state < stateCount; state++)
                    Put(new Vector4(Vector3.Lerp(color, ink, palette.step * state), 1f));
            }

            foreach (Vector3 color in surfaces)
            {
                Vector3 ink = Contrast(dark, color) >= Contrast(light, color) ? dark : light;
                Put(new Vector4(ink, 1f));
                Put(new Vector4(Vector3.Lerp(ink, color, palette.muted), 1f));
            }

            Put(new Vector4(dark, 1f));
            Put(new Vector4(light, 1f));
            Put(new Vector4(Control.HexToRGB(string.IsNullOrEmpty(palette.edgeAccent) ? palette.accent : palette.edgeAccent), 1f));

            Vector3 groundInk = Contrast(dark, surfaces[0]) >= Contrast(light, surfaces[0]) ? dark : light;
            Vector3 otherInk = Contrast(dark, groundInk) >= Contrast(light, groundInk) ? dark : light;
            Put(new Vector4(Vector3.Lerp(groundInk, otherInk, palette.step), 1f));

            Vector3 codeGround = surfaces[4];
            Vector3 codeInk = Contrast(dark, codeGround) >= Contrast(light, codeGround) ? dark : light;
            Put(new Vector4(HexOr(palette.keyword, surfaces[6]), 1f));
            Put(new Vector4(HexOr(palette.stringLiteral, surfaces[6]), 1f));
            Put(new Vector4(HexOr(palette.number, surfaces[6]), 1f));
            Put(new Vector4(HexOr(palette.comment, Vector3.Lerp(codeInk, codeGround, palette.muted)), 1f));

            Vector3 lineInk = Contrast(dark, surfaces[5]) >= Contrast(light, surfaces[5]) ? dark : light;
            Put(new Vector4(HexOr(palette.idleAccent, Vector3.Lerp(surfaces[5], lineInk, palette.step * 2)), 1f));

            Vector3 browserGround = surfaces[1];
            Vector3 browserInk = Contrast(dark, browserGround) >= Contrast(light, browserGround) ? dark : light;
            Vector3 browserMuted = Vector3.Lerp(browserInk, browserGround, palette.muted);
            Put(new Vector4(HexOr(palette.doc, browserMuted), 1f));
            Put(new Vector4(HexOr(palette.sheet, browserMuted), 1f));
            Put(new Vector4(HexOr(palette.planner, browserMuted), 1f));
        }

        private static Vector3 HexOr(string hex, Vector3 fallback) => string.IsNullOrEmpty(hex) ? fallback : Control.HexToRGB(hex);
        #endregion
    }
}
