using ArctisAurora.Core.Filing.Serialization;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Tex;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Xml.Linq;

namespace ArctisAurora.Core.Filing
{
    [A_XSDType("MathConstants", "AssetRegistry")]
    public class MathConstants
    {
        // percentages
        [A_XSDElementProperty("ScriptPercentScaleDown", "AssetRegistry")]
        public float scriptPercentScaleDown { get; set; }
        [A_XSDElementProperty("ScriptScriptPercentScaleDown", "AssetRegistry")]
        public float scriptScriptPercentScaleDown { get; set; }
        [A_XSDElementProperty("RadicalDegreeBottomRaisePercent", "AssetRegistry")]
        public float radicalDegreeBottomRaisePercent { get; set; }

        // lengths, in em
        [A_XSDElementProperty("DelimitedSubFormulaMinHeight", "AssetRegistry")]
        public float delimitedSubFormulaMinHeight { get; set; }
        [A_XSDElementProperty("DisplayOperatorMinHeight", "AssetRegistry")]
        public float displayOperatorMinHeight { get; set; }
        [A_XSDElementProperty("MathLeading", "AssetRegistry")]
        public float mathLeading { get; set; }
        [A_XSDElementProperty("AxisHeight", "AssetRegistry")]
        public float axisHeight { get; set; }
        [A_XSDElementProperty("AccentBaseHeight", "AssetRegistry")]
        public float accentBaseHeight { get; set; }
        [A_XSDElementProperty("FlattenedAccentBaseHeight", "AssetRegistry")]
        public float flattenedAccentBaseHeight { get; set; }
        [A_XSDElementProperty("SubscriptShiftDown", "AssetRegistry")]
        public float subscriptShiftDown { get; set; }
        [A_XSDElementProperty("SubscriptTopMax", "AssetRegistry")]
        public float subscriptTopMax { get; set; }
        [A_XSDElementProperty("SubscriptBaselineDropMin", "AssetRegistry")]
        public float subscriptBaselineDropMin { get; set; }
        [A_XSDElementProperty("SuperscriptShiftUp", "AssetRegistry")]
        public float superscriptShiftUp { get; set; }
        [A_XSDElementProperty("SuperscriptShiftUpCramped", "AssetRegistry")]
        public float superscriptShiftUpCramped { get; set; }
        [A_XSDElementProperty("SuperscriptBottomMin", "AssetRegistry")]
        public float superscriptBottomMin { get; set; }
        [A_XSDElementProperty("SuperscriptBaselineDropMax", "AssetRegistry")]
        public float superscriptBaselineDropMax { get; set; }
        [A_XSDElementProperty("SubSuperscriptGapMin", "AssetRegistry")]
        public float subSuperscriptGapMin { get; set; }
        [A_XSDElementProperty("SuperscriptBottomMaxWithSubscript", "AssetRegistry")]
        public float superscriptBottomMaxWithSubscript { get; set; }
        [A_XSDElementProperty("SpaceAfterScript", "AssetRegistry")]
        public float spaceAfterScript { get; set; }
        [A_XSDElementProperty("UpperLimitGapMin", "AssetRegistry")]
        public float upperLimitGapMin { get; set; }
        [A_XSDElementProperty("UpperLimitBaselineRiseMin", "AssetRegistry")]
        public float upperLimitBaselineRiseMin { get; set; }
        [A_XSDElementProperty("LowerLimitGapMin", "AssetRegistry")]
        public float lowerLimitGapMin { get; set; }
        [A_XSDElementProperty("LowerLimitBaselineDropMin", "AssetRegistry")]
        public float lowerLimitBaselineDropMin { get; set; }
        [A_XSDElementProperty("StackTopShiftUp", "AssetRegistry")]
        public float stackTopShiftUp { get; set; }
        [A_XSDElementProperty("StackTopDisplayStyleShiftUp", "AssetRegistry")]
        public float stackTopDisplayStyleShiftUp { get; set; }
        [A_XSDElementProperty("StackBottomShiftDown", "AssetRegistry")]
        public float stackBottomShiftDown { get; set; }
        [A_XSDElementProperty("StackBottomDisplayStyleShiftDown", "AssetRegistry")]
        public float stackBottomDisplayStyleShiftDown { get; set; }
        [A_XSDElementProperty("StackGapMin", "AssetRegistry")]
        public float stackGapMin { get; set; }
        [A_XSDElementProperty("StackDisplayStyleGapMin", "AssetRegistry")]
        public float stackDisplayStyleGapMin { get; set; }
        [A_XSDElementProperty("StretchStackTopShiftUp", "AssetRegistry")]
        public float stretchStackTopShiftUp { get; set; }
        [A_XSDElementProperty("StretchStackBottomShiftDown", "AssetRegistry")]
        public float stretchStackBottomShiftDown { get; set; }
        [A_XSDElementProperty("StretchStackGapAboveMin", "AssetRegistry")]
        public float stretchStackGapAboveMin { get; set; }
        [A_XSDElementProperty("StretchStackGapBelowMin", "AssetRegistry")]
        public float stretchStackGapBelowMin { get; set; }
        [A_XSDElementProperty("FractionNumeratorShiftUp", "AssetRegistry")]
        public float fractionNumeratorShiftUp { get; set; }
        [A_XSDElementProperty("FractionNumeratorDisplayStyleShiftUp", "AssetRegistry")]
        public float fractionNumeratorDisplayStyleShiftUp { get; set; }
        [A_XSDElementProperty("FractionDenominatorShiftDown", "AssetRegistry")]
        public float fractionDenominatorShiftDown { get; set; }
        [A_XSDElementProperty("FractionDenominatorDisplayStyleShiftDown", "AssetRegistry")]
        public float fractionDenominatorDisplayStyleShiftDown { get; set; }
        [A_XSDElementProperty("FractionNumeratorGapMin", "AssetRegistry")]
        public float fractionNumeratorGapMin { get; set; }
        [A_XSDElementProperty("FractionNumDisplayStyleGapMin", "AssetRegistry")]
        public float fractionNumDisplayStyleGapMin { get; set; }
        [A_XSDElementProperty("FractionRuleThickness", "AssetRegistry")]
        public float fractionRuleThickness { get; set; }
        [A_XSDElementProperty("FractionDenominatorGapMin", "AssetRegistry")]
        public float fractionDenominatorGapMin { get; set; }
        [A_XSDElementProperty("FractionDenomDisplayStyleGapMin", "AssetRegistry")]
        public float fractionDenomDisplayStyleGapMin { get; set; }
        [A_XSDElementProperty("SkewedFractionHorizontalGap", "AssetRegistry")]
        public float skewedFractionHorizontalGap { get; set; }
        [A_XSDElementProperty("SkewedFractionVerticalGap", "AssetRegistry")]
        public float skewedFractionVerticalGap { get; set; }
        [A_XSDElementProperty("OverbarVerticalGap", "AssetRegistry")]
        public float overbarVerticalGap { get; set; }
        [A_XSDElementProperty("OverbarRuleThickness", "AssetRegistry")]
        public float overbarRuleThickness { get; set; }
        [A_XSDElementProperty("OverbarExtraAscender", "AssetRegistry")]
        public float overbarExtraAscender { get; set; }
        [A_XSDElementProperty("UnderbarVerticalGap", "AssetRegistry")]
        public float underbarVerticalGap { get; set; }
        [A_XSDElementProperty("UnderbarRuleThickness", "AssetRegistry")]
        public float underbarRuleThickness { get; set; }
        [A_XSDElementProperty("UnderbarExtraDescender", "AssetRegistry")]
        public float underbarExtraDescender { get; set; }
        [A_XSDElementProperty("RadicalVerticalGap", "AssetRegistry")]
        public float radicalVerticalGap { get; set; }
        [A_XSDElementProperty("RadicalDisplayStyleVerticalGap", "AssetRegistry")]
        public float radicalDisplayStyleVerticalGap { get; set; }
        [A_XSDElementProperty("RadicalRuleThickness", "AssetRegistry")]
        public float radicalRuleThickness { get; set; }
        [A_XSDElementProperty("RadicalExtraAscender", "AssetRegistry")]
        public float radicalExtraAscender { get; set; }
        [A_XSDElementProperty("RadicalKernBeforeDegree", "AssetRegistry")]
        public float radicalKernBeforeDegree { get; set; }
        [A_XSDElementProperty("RadicalKernAfterDegree", "AssetRegistry")]
        public float radicalKernAfterDegree { get; set; }

        // classic TeX's cmsy10 and cmex10 font dimensions, em; set in the data file, not read from the font
        [A_XSDElementProperty("Sub1", "AssetRegistry")]
        public float sub1 { get; set; }
        [A_XSDElementProperty("Sub2", "AssetRegistry")]
        public float sub2 { get; set; }
        [A_XSDElementProperty("Sup1", "AssetRegistry")]
        public float sup1 { get; set; }
        [A_XSDElementProperty("Sup2", "AssetRegistry")]
        public float sup2 { get; set; }
        [A_XSDElementProperty("Sup3", "AssetRegistry")]
        public float sup3 { get; set; }
        [A_XSDElementProperty("SubDrop", "AssetRegistry")]
        public float subDrop { get; set; }
        [A_XSDElementProperty("SupDrop", "AssetRegistry")]
        public float supDrop { get; set; }
        [A_XSDElementProperty("BigOpSpacing1", "AssetRegistry")]
        public float bigOpSpacing1 { get; set; }
        [A_XSDElementProperty("BigOpSpacing3", "AssetRegistry")]
        public float bigOpSpacing3 { get; set; }
        [A_XSDElementProperty("BigOpSpacing5", "AssetRegistry")]
        public float bigOpSpacing5 { get; set; }

        // each big operator's display-size variant, "∑ 1.4,∫ 2.22", height in em
        [A_XSDElementProperty("DisplayOperators", "AssetRegistry")]
        public string displayOperators { get; set; } = string.Empty;
        private readonly Dictionary<char, float> displayHeights = new Dictionary<char, float>();

        // A big operator's display-size height in em, or DisplayOperatorMinHeight when the font names no variant.
        public float DisplayHeight(char op) => displayHeights.TryGetValue(op, out float height) ? height : displayOperatorMinHeight;

        // Reads a face's MathConstants subtable; null when the face has no MATH table.
        public static MathConstants Read(string fontPath, int face)
        {
            using BinaryReader reader = new BinaryReader(new FileStream(fontPath, FileMode.Open, FileAccess.Read));
            reader.BaseStream.Position = AssetImporter.FaceOffset(reader, face) + 4;
            ushort tableCount = AssetImporter.ReadUInt16BE(reader);
            reader.BaseStream.Position += 6;

            uint math = 0, head = 0, cmap = 0;
            for (int i = 0; i < tableCount; i++)
            {
                string tag = new string(reader.ReadChars(4));
                reader.BaseStream.Position += 4;
                uint offset = AssetImporter.ReadUInt32BE(reader);
                reader.BaseStream.Position += 4;
                if (tag == "MATH") math = offset;
                else if (tag == "head") head = offset;
                else if (tag == "cmap") cmap = offset;
            }
            if (math == 0) return null;

            reader.BaseStream.Position = head + 18;
            float em = AssetImporter.ReadUInt16BE(reader);

            reader.BaseStream.Position = math + 4;
            reader.BaseStream.Position = math + AssetImporter.ReadUInt16BE(reader);

            float Length() => AssetImporter.ReadUInt16BE(reader) / em;
            float Value()
            {
                float value = AssetImporter.ReadInt16BE(reader) / em;
                reader.BaseStream.Position += 2;
                return value;
            }

            MathConstants c = new MathConstants();
            c.scriptPercentScaleDown = AssetImporter.ReadInt16BE(reader);
            c.scriptScriptPercentScaleDown = AssetImporter.ReadInt16BE(reader);
            c.delimitedSubFormulaMinHeight = Length();
            c.displayOperatorMinHeight = Length();
            c.mathLeading = Value();
            c.axisHeight = Value();
            c.accentBaseHeight = Value();
            c.flattenedAccentBaseHeight = Value();
            c.subscriptShiftDown = Value();
            c.subscriptTopMax = Value();
            c.subscriptBaselineDropMin = Value();
            c.superscriptShiftUp = Value();
            c.superscriptShiftUpCramped = Value();
            c.superscriptBottomMin = Value();
            c.superscriptBaselineDropMax = Value();
            c.subSuperscriptGapMin = Value();
            c.superscriptBottomMaxWithSubscript = Value();
            c.spaceAfterScript = Value();
            c.upperLimitGapMin = Value();
            c.upperLimitBaselineRiseMin = Value();
            c.lowerLimitGapMin = Value();
            c.lowerLimitBaselineDropMin = Value();
            c.stackTopShiftUp = Value();
            c.stackTopDisplayStyleShiftUp = Value();
            c.stackBottomShiftDown = Value();
            c.stackBottomDisplayStyleShiftDown = Value();
            c.stackGapMin = Value();
            c.stackDisplayStyleGapMin = Value();
            c.stretchStackTopShiftUp = Value();
            c.stretchStackBottomShiftDown = Value();
            c.stretchStackGapAboveMin = Value();
            c.stretchStackGapBelowMin = Value();
            c.fractionNumeratorShiftUp = Value();
            c.fractionNumeratorDisplayStyleShiftUp = Value();
            c.fractionDenominatorShiftDown = Value();
            c.fractionDenominatorDisplayStyleShiftDown = Value();
            c.fractionNumeratorGapMin = Value();
            c.fractionNumDisplayStyleGapMin = Value();
            c.fractionRuleThickness = Value();
            c.fractionDenominatorGapMin = Value();
            c.fractionDenomDisplayStyleGapMin = Value();
            c.skewedFractionHorizontalGap = Value();
            c.skewedFractionVerticalGap = Value();
            c.overbarVerticalGap = Value();
            c.overbarRuleThickness = Value();
            c.overbarExtraAscender = Value();
            c.underbarVerticalGap = Value();
            c.underbarRuleThickness = Value();
            c.underbarExtraDescender = Value();
            c.radicalVerticalGap = Value();
            c.radicalDisplayStyleVerticalGap = Value();
            c.radicalRuleThickness = Value();
            c.radicalExtraAscender = Value();
            c.radicalKernBeforeDegree = Value();
            c.radicalKernAfterDegree = Value();
            c.radicalDegreeBottomRaisePercent = AssetImporter.ReadInt16BE(reader);
            if (cmap != 0) c.displayOperators = DisplayOperators(reader, math, cmap, em, c.displayOperatorMinHeight);
            return c;
        }

        // Each big operator's first vertical variant at least DisplayOperatorMinHeight tall, from the MathVariants subtable.
        private static string DisplayOperators(BinaryReader reader, uint math, uint cmap, float em, float minHeight)
        {
            reader.BaseStream.Position = math + 8;
            long variants = math + AssetImporter.ReadUInt16BE(reader);
            reader.BaseStream.Position = variants + 2;
            long coverage = variants + AssetImporter.ReadUInt16BE(reader);
            reader.BaseStream.Position += 2;
            ushort vertCount = AssetImporter.ReadUInt16BE(reader);

            AuroraFont.TableEntry table = new AuroraFont.TableEntry { offset = cmap };
            StringBuilder written = new StringBuilder();
            foreach ((char op, bool _) in MathSymbols.bigOps.Values)
            {
                int index = CoverageIndex(reader, coverage, AuroraFont.GetGlyphIndex(op, reader, table));
                if (index < 0 || index >= vertCount) continue;

                reader.BaseStream.Position = variants + 10 + 2 * index;
                long construction = variants + AssetImporter.ReadUInt16BE(reader);
                reader.BaseStream.Position = construction + 2;
                ushort count = AssetImporter.ReadUInt16BE(reader);
                float height = 0f;
                for (int v = 0; v < count && height < minHeight; v++)
                {
                    reader.BaseStream.Position += 2;
                    height = AssetImporter.ReadUInt16BE(reader) / em;
                }
                if (height <= 0f) continue;
                if (written.Length > 0) written.Append(',');
                written.Append(op).Append(' ').Append(height.ToString(CultureInfo.InvariantCulture));
            }
            return written.ToString();
        }

        // A glyph's index in a Coverage table, or -1.
        private static int CoverageIndex(BinaryReader reader, long at, ushort glyph)
        {
            reader.BaseStream.Position = at;
            ushort format = AssetImporter.ReadUInt16BE(reader);
            ushort count = AssetImporter.ReadUInt16BE(reader);
            for (int i = 0; i < count; i++)
            {
                if (format == 1)
                {
                    if (AssetImporter.ReadUInt16BE(reader) == glyph) return i;
                    continue;
                }
                ushort start = AssetImporter.ReadUInt16BE(reader), end = AssetImporter.ReadUInt16BE(reader), first = AssetImporter.ReadUInt16BE(reader);
                if (glyph >= start && glyph <= end) return first + glyph - start;
            }
            return -1;
        }

        public void Save(string path)
        {
            XElement element = new XElement("MathConstants");
            foreach (MemberInfo member in XmlReflection.ScalarMembers(GetType()))
            {
                object value = XmlReflection.GetMember(member, this);
                element.Add(new XAttribute(member.GetCustomAttribute<A_XSDElementPropertyAttribute>().Name,
                    value is float f ? f.ToString(CultureInfo.InvariantCulture) : value));
            }
            element.Save(path);
        }

        public static MathConstants Load(string path)
        {
            MathConstants constants = new MathConstants();
            XmlReflection.ApplyAttributes(DataFiles.LoadXml(path), constants);
            foreach (string entry in constants.displayOperators.Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (entry.Length > 2 && float.TryParse(entry.AsSpan(2), NumberStyles.Float, CultureInfo.InvariantCulture, out float height))
                    constants.displayHeights[entry[0]] = height;
            return constants;
        }
    }
}
