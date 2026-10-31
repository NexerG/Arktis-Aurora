using ArctisAurora.Core.Diagnostics;
using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Filing.Serialization;
using System.Xml.Linq;

namespace ArctisAurora.Core.UI.Tex
{
    // Computer Modern's math widths and italic corrections at the design size LaTeX picks for a point size.
    public static class TexMathMetrics
    {
        private static readonly LogChannel Log = LogChannel.For("Tex");

        private const byte MathItalic = 0, Roman = 1, Symbol = 2;
        // the characters plain TeX's mathcodes take from cmmi though they are set upright
        private const string ItalicFamilyUpright = ".,</>∂ℓ";
        // LaTeX's \DeclareMathSizes: text, script, scriptscript, pt
        private static readonly float[] mathSizes =
        {
            5f, 5f, 5f, 6f, 5f, 5f, 7f, 5f, 5f, 8f, 6f, 5f, 9f, 6f, 5f, 10f, 7f, 5f, 10.95f, 8f, 6f,
            12f, 8f, 6f, 14.4f, 10f, 7f, 17.28f, 12f, 10f, 20.74f, 14.4f, 12f, 24.88f, 20.74f, 17.28f
        };

        private static Dictionary<(byte family, byte size, char ch), (float width, float italic, float height, float depth)>? glyphs;
        private static readonly Dictionary<(char ch, bool display), (float height, float depth)> bigOps = new Dictionary<(char, bool), (float, float)>(32);
        private static readonly object gate = new object();

        // A character's box in em at pt, from the family TeX's math takes it from; false when Computer Modern lacks it.
        public static bool Glyph(FontStyle face, float pt, char ch, out (float width, float italic, float height, float depth) box)
        {
            box = default;
            if (face == FontStyle.Bold || Glyphs() is not { } table) return false;
            if (face == FontStyle.Italic || ItalicFamilyUpright.Contains(ch)) return Find(table, MathItalic, pt, ch, out box);
            return Find(table, Roman, pt, ch, out box) || Find(table, Symbol, pt, ch, out box);
        }

        // A big operator's cmex10 box height and depth in em, text or display size; false when cmex10 lacks it.
        public static bool BigOp(char op, bool display, out float height, out float depth)
        {
            height = depth = 0f;
            if (Glyphs() == null || !bigOps.TryGetValue((op, display), out (float height, float depth) box)) return false;
            (height, depth) = box;
            return true;
        }

        // The script or scriptscript size for a text size, pt; LaTeX's default ratios off its table.
        public static float ScriptPt(float textPt, bool scriptScript)
        {
            float[] sizes = mathSizes;
            for (int i = 0; i < sizes.Length; i += 3)
                if (MathF.Abs(sizes[i] - textPt) < 0.05f) return sizes[i + (scriptScript ? 2 : 1)];
            return textPt * (scriptScript ? 0.5f : 0.7f);
        }

        private static bool Find(Dictionary<(byte, byte, char), (float, float, float, float)> table, byte family, float pt, char ch,
                                 out (float width, float italic, float height, float depth) box) =>
            table.TryGetValue((family, DesignSize(family, pt), ch), out box);

        // The .fd files' choice: 5–9 pt their own design, 10.95 pt the 10 pt one, cmmi12 and cmr12 from 12 pt, cmr17 from 17.28, cmsy10 from 10.
        private static byte DesignSize(byte family, float pt)
        {
            if (pt < 9.5f) return (byte)Math.Clamp((int)MathF.Round(pt), 5, 9);
            if (pt < 11.5f || family == Symbol) return 10;
            return family == Roman && pt >= 15.8f ? (byte)17 : (byte)12;
        }

        private static Dictionary<(byte, byte, char), (float, float, float, float)>? Glyphs()
        {
            lock (gate)
            {
                if (glyphs != null) return glyphs.Count == 0 ? null : glyphs;
                glyphs = new Dictionary<(byte, byte, char), (float, float, float, float)>(2048);
                string path = Paths.Doc("Math/ComputerModern.tfm.axml");
                if (!DataFiles.Exists(path))
                {
                    Log.Warn($"no Computer Modern metrics at {path}; classic math takes Latin Modern's widths");
                    return null;
                }
                foreach (XElement font in DataFiles.LoadXml(path).Elements("Font"))
                {
                    if ((string?)font.Attribute("Family") == "Extension")
                    {
                        foreach (XElement c in font.Elements("Char"))
                            if ((string?)c.Attribute("Code") is { Length: 1 } op)
                                bigOps[(op[0], (string?)c.Attribute("Style") == "Display")] = ((float?)c.Attribute("Height") ?? 0f, (float?)c.Attribute("Depth") ?? 0f);
                        continue;
                    }
                    byte family = (string?)font.Attribute("Family") switch { "MathItalic" => MathItalic, "Roman" => Roman, _ => Symbol };
                    byte size = (byte)MathF.Floor((float?)font.Attribute("Size") ?? 10f);
                    foreach (XElement c in font.Elements("Char"))
                        if ((string?)c.Attribute("Code") is { Length: 1 } code)
                            glyphs[(family, size, code[0])] = ((float?)c.Attribute("Width") ?? 0f, (float?)c.Attribute("Italic") ?? 0f,
                                                               (float?)c.Attribute("Height") ?? 0f, (float?)c.Attribute("Depth") ?? 0f);
                }
                return glyphs;
            }
        }
    }
}
