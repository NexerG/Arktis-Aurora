using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Filing.Serialization;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Testing;

namespace ArctisAurora.Tests
{
    internal static class FontTests
    {
        [A_XSDActionDependency("Fonts.MathFontBaked", "Test")]
        private static IEnumerator<int> MathFontBaked(TestContext t)
        {
            AtlasMetaData atlas = new AtlasMetaData();
            Serializer.DeserializeAttributed(Paths.Font("cambria", "cambria.agd"), ref atlas);
            atlas.BuildCharIndex();

            GlyphMetrics sum = atlas.GetGlyphAndIndex('∑').Item1.regular;
            GlyphMetrics alpha = atlas.GetGlyphAndIndex('α').Item1.regular;
            GlyphMetrics integral = atlas.GetGlyphAndIndex('∫').Item1.regular;
            t.Check(!Same(sum, alpha) && !Same(alpha, integral) && !Same(sum, integral),
                "sum, alpha and integral bake distinct glyphs, not .notdef");

            Glyph x = atlas.GetGlyphAndIndex('x').Item1;
            t.Check(atlas.HasFace(0, FontStyle.Italic) && !Same(x.regular, atlas.Metrics(x, atlas.Effective(0, FontStyle.Italic))),
                "x has its own italic face");

            MathConstants math = MathConstants.Load(Paths.Font("cambria", "cambria.math.axml"));
            t.Check(math.axisHeight > 0f && math.fractionRuleThickness > 0f && math.fractionNumeratorGapMin > 0f
                && math.scriptPercentScaleDown > 0f, "MATH constants are read and non-zero");
            yield break;
        }

        private static readonly Dictionary<string, byte[]> cffOps = new Dictionary<string, byte[]>
        {
            ["hstemhm"] = new byte[] { 18 }, ["hintmask"] = new byte[] { 19 }, ["rmoveto"] = new byte[] { 21 },
            ["rlineto"] = new byte[] { 5 }, ["hlineto"] = new byte[] { 6 }, ["vlineto"] = new byte[] { 7 },
            ["rrcurveto"] = new byte[] { 8 }, ["hvcurveto"] = new byte[] { 31 }, ["callsubr"] = new byte[] { 10 },
            ["callgsubr"] = new byte[] { 29 }, ["return"] = new byte[] { 11 }, ["endchar"] = new byte[] { 14 },
            ["flex"] = new byte[] { 12, 35 }, ["mask"] = new byte[] { 0xFF }
        };

        // A Type 2 charstring from "10 20 rmoveto ..." — small numbers in one byte, the rest as int16.
        private static byte[] Charstring(string program)
        {
            List<byte> bytes = new List<byte>();
            foreach (string word in program.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (cffOps.TryGetValue(word, out byte[]? op)) bytes.AddRange(op);
                else if (int.Parse(word) is int n and >= -107 and <= 107) bytes.Add((byte)(n + 139));
                else bytes.AddRange(new byte[] { 28, (byte)(int.Parse(word) >> 8), (byte)int.Parse(word) });
            }
            return bytes.ToArray();
        }

        private static List<Bezier> Outline(string program, string[]? local = null, string[]? global = null) =>
            CffOutlines.Interpret(Charstring(program),
                (global ?? Array.Empty<string>()).Select(Charstring).ToArray(),
                (local ?? Array.Empty<string>()).Select(Charstring).ToArray());

        private static string Points(Bezier contour) =>
            string.Join(" ", contour.points.Select(p => $"{(p.isCubicControl ? "c" : "a")}{p.pos.X},{p.pos.Y}"));

        [A_XSDActionDependency("Fonts.CffCharstring", "Test")]
        private static IEnumerator<int> CffCharstring(TestContext t)
        {
            List<Bezier> box = Outline("100 100 rmoveto 50 hlineto 50 vlineto -50 hlineto endchar");
            t.Check(box.Count == 1 && Points(box[0]) == "a100,100 a150,100 a150,150 a100,150",
                $"moveto and alternating lines ({(box.Count == 1 ? Points(box[0]) : box.Count.ToString())})");

            List<Bezier> curve = Outline("0 0 rmoveto 10 0 10 10 0 10 rrcurveto endchar");
            t.Check(Points(curve[0]) == "a0,0 c10,0 c20,10 a20,20", $"rrcurveto keeps both controls ({Points(curve[0])})");

            List<Bezier> hv = Outline("0 0 rmoveto 10 5 5 10 hvcurveto endchar");
            t.Check(Points(hv[0]) == "a0,0 c10,0 c15,5 a15,15", $"hvcurveto starts horizontal ({Points(hv[0])})");

            List<Bezier> hinted = Outline("500 10 20 hstemhm 30 40 hintmask mask 0 0 rmoveto 5 hlineto 5 vlineto endchar");
            t.Check(hinted.Count == 1 && Points(hinted[0]) == "a0,0 a5,0 a5,5",
                $"a width, stems and a one-byte hint mask are skipped ({(hinted.Count == 1 ? Points(hinted[0]) : hinted.Count.ToString())})");

            List<Bezier> subrs = Outline("0 0 rmoveto -107 callsubr -107 callgsubr endchar",
                local: new[] { "10 0 rlineto return" }, global: new[] { "0 10 rlineto return" });
            t.Check(Points(subrs[0]) == "a0,0 a10,0 a10,10", $"local and global subroutines with their bias ({Points(subrs[0])})");

            List<Bezier> flex = Outline("0 0 rmoveto 1 0 1 0 1 0 1 0 1 0 1 0 50 flex endchar");
            t.Check(flex[0].points.Count == 7 && flex[0].points[^1].pos.X == 6f, "flex draws two curves");

            List<Bezier> bulge = Outline("0 0 rmoveto 0 10 10 0 0 -10 rrcurveto endchar");
            (float _, float _, float _, float yMax) = CffOutlines.Bounds(bulge);
            t.Check(MathF.Abs(yMax - 7.5f) < 1e-3f, $"the ink box follows the curve, not its controls ({yMax})");
            yield break;
        }

        private static bool Same(GlyphMetrics a, GlyphMetrics b) =>
            a.xMin == b.xMin && a.yMin == b.yMin && a.xMax == b.xMax && a.yMax == b.yMax && a.advanceWidth == b.advanceWidth;
    }
}
