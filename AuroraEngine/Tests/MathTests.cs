using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Filing.Serialization;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Testing;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Tex;

namespace ArctisAurora.Tests
{
    internal static class MathTests
    {
        private static AtlasMetaData atlas;
        private static MathConstants constants;

        private static void LoadFont()
        {
            if (atlas != null) return;
            atlas = new AtlasMetaData();
            Serializer.DeserializeAttributed(Paths.Font("cambria", "cambria.agd"), ref atlas);
            atlas.BuildCharIndex();
            constants = MathConstants.Load(Paths.Font("cambria", "cambria.math.axml"));
        }

        private static MathBox Lay(string tex, bool display = false)
        {
            LoadFont();
            return MathLayout.Layout(MathParser.Parse(tex), display, atlas, constants);
        }

        private static MathGlyph Find(MathBox box, char ch) => box.glyphs.First(g => g.ch == ch);

        // a placed glyph's ink, bottom and top, in the box's coordinates
        private static (float bottom, float top) InkOf(MathGlyph g)
        {
            GlyphMetrics m = atlas.Metrics(atlas.GetGlyph(g.ch), atlas.Effective(0, g.face));
            int range = m.yMax - m.yMin;
            return (g.y + m.glyphHeight * m.yMin / range * g.scale, g.y + m.glyphHeight * m.yMax / range * g.scale);
        }

        [A_XSDActionDependency("Math.SymbolsInCharset", "Test")]
        private static IEnumerator<int> SymbolsInCharset(TestContext t)
        {
            LoadFont();
            List<char> chars = new List<char>();
            chars.AddRange(MathSymbols.commands.Values.Select(v => v.ch));
            chars.AddRange(MathSymbols.bigOps.Values.Select(v => v.ch));
            chars.AddRange(MathSymbols.accents.Values);
            chars.AddRange(MathSymbols.blackboard.Values);
            chars.AddRange(MathSymbols.delimiters.Values);
            foreach (var set in MathLayout.pieces.Values)
                chars.AddRange(new[] { set.top, set.ext, set.bottom });
            chars.AddRange("√⎷′−∗");

            string missing = new string(chars.Where(c => c != '\0' && atlas.GetGlyph(c) == null).Distinct().ToArray());
            t.Check(missing.Length == 0, $"every symbol is in the Math charset (missing {missing.Length})");
            yield break;
        }

        [A_XSDActionDependency("Math.ParseNeverThrows", "Test")]
        private static IEnumerator<int> ParseNeverThrows(TestContext t)
        {
            string[] invalid = { @"\frac{a", "}", @"\foo", "x^a^b", "^", @"\left(", @"\right)", "a & b", @"\sqrt", @"\\", @"\left( x \right" };
            foreach (string tex in invalid)
            {
                MathNode node = MathParser.Parse(tex);
                t.Check(node is MathError && Lay(tex).error, $"'{tex}' is an error formula");
            }

            string[] valid = { "", "x^2", @"\frac{a}{b}", @"\sqrt[3]{x}", @"\left(\frac{1}{2}\right)", @"\sum_{i=1}^{n} i",
                               @"\text{if } x", @"\mathbb{R}", "f'(x)", @"\hat{a}", @"a \, b", @"\left. x \right|", @"\lim_{x \to 0}" };
            foreach (string tex in valid)
                t.Check(MathParser.Parse(tex) is not MathError && !Lay(tex, true).error, $"'{tex}' parses");
            yield break;
        }

        [A_XSDActionDependency("Math.SuperscriptRaises", "Test")]
        private static IEnumerator<int> SuperscriptRaises(TestContext t)
        {
            MathBox x = Lay("x");
            MathBox squared = Lay("x^2");
            MathGlyph two = Find(squared, '2');
            t.Check(squared.height > x.height, "x^2 is taller than x");
            t.Check(two.y > 0f, "the 2 sits above the baseline");
            t.Check(MathF.Abs(two.scale - constants.scriptPercentScaleDown / 100f) < 1e-4f, "the 2 is at script scale");
            yield break;
        }

        [A_XSDActionDependency("Math.SubscriptBelowBaseline", "Test")]
        private static IEnumerator<int> SubscriptBelowBaseline(TestContext t)
        {
            MathBox box = Lay("x_i");
            t.Check(Find(box, 'i').y < 0f, "the i sits below the baseline");
            t.Check(box.depth > 0f, "the box has depth");
            yield break;
        }

        [A_XSDActionDependency("Math.FractionOnAxis", "Test")]
        private static IEnumerator<int> FractionOnAxis(TestContext t)
        {
            MathBox box = Lay(@"\frac{a}{b}");
            t.Check(box.rules.Count == 1, "one rule");
            MathRule rule = box.rules[0];
            t.Check(MathF.Abs(rule.y - rule.height * 0.5f - constants.axisHeight) < 1e-4f, "the rule is centred on the axis");

            MathGlyph a = Find(box, 'a'), b = Find(box, 'b');
            t.Check(a.y > rule.y && b.y < rule.y - rule.height, "numerator above the rule, denominator below");
            t.Check(a.x >= rule.x && b.x >= rule.x && a.x < rule.x + rule.width && b.x < rule.x + rule.width,
                "both sit within the rule's width");
            t.Check(box.depth > 0f, "the fraction has depth");
            yield break;
        }

        [A_XSDActionDependency("Math.BigOpDisplay", "Test")]
        private static IEnumerator<int> BigOpDisplay(TestContext t)
        {
            MathBox display = Lay(@"\sum_{i=1}^{n}", true);
            MathBox inline = Lay(@"\sum_{i=1}^{n}");
            t.Check(Find(display, '∑').scale > Find(inline, '∑').scale, "the sum is larger in display style");

            MathGlyph displaySum = Find(display, '∑');
            float displayOp = Lay(@"\sum", true).width;
            t.Check(Find(display, 'n').x < displayOp && Find(display, 'n').y > displaySum.y,
                "display limits sit above, within the operator's width");
            t.Check(Find(inline, 'n').x >= Lay(@"\sum").width - 1e-4f, "inline limits sit to the right");
            yield break;
        }

        [A_XSDActionDependency("Math.DelimitersGrow", "Test")]
        private static IEnumerator<int> DelimitersGrow(TestContext t)
        {
            foreach (string body in new[] { @"\frac{a}{b}", @"\frac{\frac{a}{b}}{\frac{\frac{c}{d}}{e}}" })
            {
                MathBox inner = Lay(body, true);
                MathBox box = Lay(@"\left(" + body + @"\right)", true);
                float bottom = float.MaxValue, top = float.MinValue;
                foreach (MathGlyph g in box.glyphs.Where(g => "(⎛⎜⎝".Contains(g.ch)))
                {
                    (float gb, float gt) = InkOf(g);
                    bottom = MathF.Min(bottom, gb);
                    top = MathF.Max(top, gt);
                }
                t.Check(top - bottom >= 0.9f * (inner.height + inner.depth), $"the paren covers {body}");
            }
            yield break;
        }

        [A_XSDActionDependency("Math.SqrtCoversBody", "Test")]
        private static IEnumerator<int> SqrtCoversBody(TestContext t)
        {
            MathBox x = Lay("x");
            MathBox box = Lay(@"\sqrt{x}");
            t.Check(box.rules.Count >= 1 && box.rules[^1].y - box.rules[^1].height >= x.height, "the bar is above the x");
            t.Check(box.width > x.width, "the radical is wider than its body");
            yield break;
        }

        [A_XSDActionDependency("Math.Spacing", "Test")]
        private static IEnumerator<int> Spacing(TestContext t)
        {
            float ab = Lay("ab").width, plus = Lay("a+b").width - Lay("+").width, equals = Lay("a=b").width - Lay("=").width;
            t.Check(ab < plus && plus < equals, "ab < a+b < a=b once the operator itself is taken out");
            t.Check(Lay("-a").width < Lay("a-a").width - Lay("a").width, "a unary minus takes no binary space");
            yield break;
        }

        [A_XSDActionDependency("Math.Classes", "Test")]
        private static IEnumerator<int> Classes(TestContext t)
        {
            MathSymbol First(string tex) => (MathSymbol)((MathList)MathParser.Parse(tex)).items[0];

            MathSymbol alpha = First(@"\alpha"), gamma = First(@"\Gamma"), le = First(@"\le");
            t.Check(alpha.ch == 'α' && alpha.face == FontStyle.Italic, "alpha is an italic α");
            t.Check(gamma.ch == 'Γ' && gamma.face == FontStyle.Regular, "Gamma is an upright Γ");
            t.Check(le.ch == '≤' && le.cls == MathClass.Rel, "le is a ≤ relation");
            yield break;
        }

        [A_XSDActionDependency("Math.Matrix", "Test")]
        private static IEnumerator<int> Matrix(TestContext t)
        {
            const string body = @"a & b \\ c & d";
            MathBox plain = Lay(@"\begin{matrix}" + body + @"\end{matrix}");
            MathBox box = Lay(@"\begin{pmatrix}" + body + @"\end{pmatrix}");
            MathGlyph a = Find(plain, 'a'), b = Find(plain, 'b'), c = Find(plain, 'c'), d = Find(plain, 'd');
            t.Check(a.y == b.y && c.y == d.y && a.y > c.y, "a b on one row, c d on the row below");
            t.Check(b.x - a.x > 1f && d.x - c.x > 1f, "columns are an \\arraycolsep pair (1 em) apart at least");
            t.Check(MathF.Abs((plain.height - plain.depth) * 0.5f - constants.axisHeight) < 1e-3f, "the grid is centred on the axis");

            float bottom = float.MaxValue, top = float.MinValue;
            foreach (MathGlyph g in box.glyphs.Where(g => "(⎛⎜⎝".Contains(g.ch)))
            {
                (float gb, float gt) = InkOf(g);
                bottom = MathF.Min(bottom, gb);
                top = MathF.Max(top, gt);
            }
            t.Check(top - bottom >= 0.9f * (plain.height + plain.depth), "the parens cover both rows");

            MathBox small = Lay(@"\begin{smallmatrix}" + body + @"\end{smallmatrix}");
            t.Check(Find(small, 'a').y - Find(small, 'c').y < a.y - c.y, "smallmatrix rows sit closer");
            t.Check(Find(small, 'a').scale < 1f, "smallmatrix cells are script size");
            yield break;
        }

        [A_XSDActionDependency("Math.Cases", "Test")]
        private static IEnumerator<int> Cases(TestContext t)
        {
            MathBox box = Lay(@"|x| = \begin{cases} x & \text{if } x \ge 0 \\ -x & \text{otherwise} \end{cases}", true);
            MathGlyph minus = Find(box, '−');
            MathGlyph firstX = box.glyphs.Where(g => g.ch == 'x' && g.y > minus.y).OrderBy(g => g.x).Skip(1).First();
            t.Check(MathF.Abs(firstX.x - minus.x) < 1e-4f, "the first column is left-aligned");
            t.Check(MathF.Abs(Find(box, 'i').x - Find(box, 'o').x) < 1e-4f, "the second column is left-aligned");
            t.Check(box.glyphs.Any(g => "{⎧⎨⎩".Contains(g.ch)), "a left brace");
            t.Check(!box.glyphs.Any(g => "}⎫⎬⎭".Contains(g.ch)), "no right brace");
            yield break;
        }

        [A_XSDActionDependency("Math.Aligned", "Test")]
        private static IEnumerator<int> Aligned(TestContext t)
        {
            MathBox box = Lay(@"\begin{aligned} a &= b + c \\ xyz &= d \end{aligned}", true);
            List<MathGlyph> equals = box.glyphs.Where(g => g.ch == '=').ToList();
            t.Check(equals.Count == 2 && MathF.Abs(equals[0].x - equals[1].x) < 1e-4f, "the = signs line up");
            t.Check(MathF.Abs(equals[0].y - equals[1].y - 1.5f) < 1e-3f, "rows are \\baselineskip + \\jot (1.5 em) apart");
            MathGlyph a = Find(box, 'a');
            t.Check(a.x > Find(box, 'x').x, "the left column is right-aligned");
            t.Check(equals[0].x - a.x > Lay("a").width + 0.2f, "= keeps its relation space after &");

            MathBox pairs = Lay(@"\begin{aligned} a &= b & c &= d \end{aligned}", true);
            MathBox tight = Lay(@"\begin{alignedat}{2} a &= b & c &= d \end{alignedat}", true);
            t.Check(MathF.Abs(pairs.width - tight.width - 1f) < 1e-3f, "aligned puts \\minalignsep between pairs, alignedat none");

            MathBox multline = Lay(@"\begin{multline} aaaa \\ b \end{multline}", true);
            t.Check(Find(multline, 'a').x == 0f && MathF.Abs(Find(multline, 'b').x + Lay("b", true).width - multline.width) < 1e-3f,
                "multline: first row left, last row right");
            yield break;
        }

        [A_XSDActionDependency("Math.FillWidth", "Test")]
        private static IEnumerator<int> FillWidth(TestContext t)
        {
            LoadFont();
            MathBox Fit(string tex, float width) => MathLayout.Layout(MathParser.Parse(tex), true, atlas, constants, width);
            bool Near(float a, float b) => MathF.Abs(a - b) < 1e-3f;
            const float line = 40f;

            const string two = @"\begin{align} a &= b & c &= d \end{align}";
            MathBox natural = Lay(two, true);
            t.Check(natural.widthAware && !Lay(@"\begin{aligned} a &= b \end{aligned}", true).widthAware && !Lay("a", true).widthAware,
                "align is width-aware, aligned and plain displays are not");
            t.Check(Fit(two, 0f).glyphs.Select(g => g.x).SequenceEqual(natural.glyphs.Select(g => g.x)) && Near(Fit(two, 0f).width, natural.width),
                "no width lays out as before");

            float total = natural.width - 1f;
            MathBox align = Fit(two, line);
            float margin = Find(align, 'a').x - Find(natural, 'a').x;
            float pair = Find(align, 'c').x - Find(natural, 'c').x - margin + 1f;
            t.Check(Near(align.width, line) && Near(margin, (line - total) / 3f) && Near(pair, margin),
                $"align: both margins and the gap between pairs equal: {margin}, {pair}, width {align.width}");

            MathBox flalign = Fit(two.Replace("align", "flalign"), line);
            t.Check(Near(flalign.width, line) && Near(Find(flalign, 'a').x, Find(natural, 'a').x)
                && Near(Find(flalign, 'c').x - Find(natural, 'c').x + 1f, line - total), "flalign: flush margins, the pair gap takes the rest");

            MathBox narrow = Fit(two, total * 0.5f);
            t.Check(Near(narrow.width, natural.width) && Near(Find(narrow, 'c').x, Find(natural, 'c').x), "too narrow: \\minalignsep and no margin");

            const string one = @"\begin{align} a &= b \tag{1} \end{align}";
            MathBox tagged = Fit(one, line);
            t.Check(Near(tagged.width, line), "a tag clear of its row leaves the full width");
            float crowd = Lay(one, true).width + tagged.tag!.width + 1.5f;
            t.Check(Near(Fit(one, crowd).width, crowd - tagged.tag.width - 1f), "a tag within a quad of its row takes its room and a quad off the width");

            MathBox multline = Fit(@"\begin{multline} aaaa \\ m \\ b \end{multline}", line);
            float b = Lay("b", true).width;
            float m = Lay("m", true).width;
            t.Check(Near(multline.width, line) && Near(multline.glyphs.Min(g => g.x), 1f) && Near(Find(multline, 'b').x + b, line - 1f)
                && Near(Find(multline, 'm').x + m * 0.5f, line * 0.5f), "multline: first row a gap from the left, last a gap from the right, middle centred");
            MathBox lastTagged = Fit(@"\begin{multline} aaaa \\ b \tag{3} \end{multline}", line);
            float fit = line - lastTagged.tag!.width - 1f;
            t.Check(Near(lastTagged.width, fit) && Near(Find(lastTagged, 'b').x + b, fit), "a tagged last row runs to the tag's quad");
            yield break;
        }

        [A_XSDActionDependency("Math.Array", "Test")]
        private static IEnumerator<int> Array(TestContext t)
        {
            MathBox box = Lay(@"\begin{array}{l|r} 1 & 22 \\ \hline 333 & 4 \end{array}");
            List<MathRule> vertical = box.rules.Where(r => r.height > r.width).ToList();
            List<MathRule> horizontal = box.rules.Where(r => r.width > r.height).ToList();
            t.Check(vertical.Count == 1 && horizontal.Count == 1, "one | rule and one \\hline");

            MathGlyph one = Find(box, '1'), three = Find(box, '3'), four = Find(box, '4');
            MathGlyph lastTwo = box.glyphs.Where(g => g.ch == '2').OrderBy(g => g.x).Last();
            t.Check(MathF.Abs(one.x - three.x) < 1e-4f, "l column left-aligned");
            t.Check(MathF.Abs(four.x - lastTwo.x) < 1e-4f, "r column right-aligned");
            t.Check(vertical[0].x > one.x && vertical[0].x < box.glyphs.Where(g => g.ch == '2').Min(g => g.x), "the | sits between the columns");
            t.Check(horizontal[0].y < one.y && horizontal[0].y > three.y, "the \\hline sits between the rows");
            yield break;
        }

        [A_XSDActionDependency("Math.Tag", "Test")]
        private static IEnumerator<int> Tag(TestContext t)
        {
            MathBox display = Lay(@"x \tag{3}", true);
            t.Check(display.tag != null && new string(display.tag.glyphs.Select(g => g.ch).ToArray()) == "(3)", "\\tag{3} reads (3)");
            t.Check(display.tag!.glyphs.All(g => g.x <= 0f) && !display.glyphs.Any(g => g.ch == '('), "a display tag is right-aligned, apart from the formula");
            t.Check(Lay(@"x \tag*{a}", true).tag?.glyphs.Count == 1, "\\tag* has no parentheses");

            MathBox inline = Lay(@"x \tag{3}");
            t.Check(inline.tag == null && Find(inline, '(').x > Lay("x").width, "an inline tag follows the formula");

            MathBox rows = Lay(@"\begin{align} a &= b \tag{1} \\ c &= d \tag{2} \end{align}", true);
            float rowGap = Find(rows, 'a').y - Find(rows, 'c').y;
            t.Check(rows.tag != null && MathF.Abs(Find(rows.tag, '1').y - Find(rows.tag, '2').y - rowGap) < 1e-4f, "each row's tag sits on its row");
            t.Check(!Lay(@"a \notag \nonumber \label{x}", true).error, "\\notag, \\nonumber and \\label are accepted");
            yield break;
        }

        [A_XSDActionDependency("Math.EnvErrors", "Test")]
        private static IEnumerator<int> EnvErrors(TestContext t)
        {
            string[] invalid = { @"\begin{pmatrix} a & b", @"\begin{pmatrix} a \end{bmatrix}", @"\begin{foo} a \end{foo}",
                                 @"\begin{gather} a & b \end{gather}", @"\begin{array}{l} a & b \end{array}", @"\begin{array}{lx} a \end{array}",
                                 @"\end{pmatrix}", @"a \hline b", @"\begin{matrix} a \\[2zz] b \end{matrix}", @"\substack{a & b}",
                                 @"\begin{equation} a \\ b \end{equation}" };
            foreach (string tex in invalid)
                t.Check(MathParser.Parse(tex) is MathError, $"'{tex}' is an error formula");

            string[] valid = { @"\begin{matrix} a \\[2pt] b \end{matrix}", @"\begin{vmatrix} a \end{vmatrix}", @"\begin{Vmatrix} a \end{Vmatrix}",
                               @"\begin{Bmatrix} a \end{Bmatrix}", @"\begin{split} a &= b \\ &= c \end{split}", @"\begin{gathered} a \\ b \end{gathered}",
                               @"\begin{gather*} a \\ b \end{gather*}", @"\begin{flalign} a &= b \end{flalign}", @"\begin{equation} x \end{equation}",
                               @"\begin{array}{c|c} a & b \end{array}" };
            foreach (string tex in valid)
                t.Check(MathParser.Parse(tex) is not MathError && !Lay(tex, true).error, $"'{tex}' parses");

            MathArray trailing = (MathArray)((MathDelimited)((MathList)MathParser.Parse(@"\begin{bmatrix} 1 \\ 2 \\ \end{bmatrix}")).items[0]).body;
            t.Check(trailing.rows.Count == 2, "a trailing \\\\ adds no empty row");
            yield break;
        }

        [A_XSDActionDependency("Math.AmsCommands", "Test")]
        private static IEnumerator<int> AmsCommands(TestContext t)
        {
            MathBox sum = Lay(@"\sum_{\substack{0<i<m \\ 0<j<n}} P(i,j)", true);
            t.Check(Find(sum, 'm').y > Find(sum, 'n').y && Find(sum, 'n').y < Find(sum, '∑').y, "\\substack stacks under the sum");

            MathBox binom = Lay(@"\binom{n}{k}");
            t.Check(binom.rules.Count == 0 && Find(binom, 'n').y > Find(binom, 'k').y, "\\binom stacks without a rule");
            t.Check(binom.glyphs.Any(g => "(⎛".Contains(g.ch)) && binom.glyphs.Any(g => ")⎞".Contains(g.ch)), "\\binom has parentheses");

            MathBox over = Lay(@"\overset{!}{=}");
            t.Check(Find(over, '!').y > 0f && Find(over, '!').scale < 1f, "\\overset puts a script above");
            t.Check(Lay(@"a \overset{!}{=} b").width > Lay("ab").width + over.width + 0.4f, "\\overset{!}{=} keeps the relation spacing");
            t.Check(Find(Lay(@"\underset{y}{\max} f"), 'y').y < 0f, "\\underset puts a script below");

            t.Check(Lay(@"\boxed{x}").rules.Count == 4, "\\boxed draws a frame");
            MathBox op = Lay(@"\operatorname*{argmax}_y f", true);
            t.Check(Find(op, 'y').y < 0f && Find(op, 'y').x > Find(op, 'a').x && Find(op, 'y').x < Find(op, 'f').x - 0.1f,
                "\\operatorname* is upright and takes limits in display");
            t.Check(Find(op, 'a').face == FontStyle.Regular, "operator names are upright");
            yield break;
        }
    }
}
