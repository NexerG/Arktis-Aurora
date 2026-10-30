using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Filing.Serialization;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Registry.Assets;
using ArctisAurora.Core.Testing;
using ArctisAurora.Core.Tex;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Document;
using ArctisAurora.Core.UI.Notes;
using ArctisAurora.Core.UI.Tex;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.EngineWork.Registry;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ArctisAurora.Tests
{
    internal static class TexTests
    {
        // What the expander hands on: characters as themselves, a control sequence as "\name ".
        private static string Run(string source, out List<TexError> errors)
        {
            TexExpander expander = new TexExpander(source);
            StringBuilder text = new StringBuilder();
            while (expander.Next(out TexToken t))
                text.Append(t.IsCs ? "\\" + t.name + " " : t.ch.ToString());
            errors = expander.errors;
            return text.ToString();
        }

        private static string Run(string source) => Run(source, out _);

        private static bool Has(List<TexError> errors, string text, int line = 0) =>
            errors.Any(e => e.message.Contains(text) && (line == 0 || e.line == line));

        [A_XSDActionDependency("Tex.SourceRoundTrip", "Test")]
        private static IEnumerator<int> SourceRoundTrip(TestContext t)
        {
            foreach (string source in new[] { "\uFEFF\\section{A}\r\n% c\r\n", "\\section{A}\nx\n", "a\nb", "" })
            {
                byte[] bytes = Encoding.UTF8.GetBytes(source);
                string from = Path.Combine(Path.GetTempPath(), $"aurora-tex-{Guid.NewGuid():N}.tex");
                string to = Path.Combine(Path.GetTempPath(), $"aurora-tex-{Guid.NewGuid():N}.tex");
                File.WriteAllBytes(from, bytes);

                RichTextDocument document = RichTextDocument.Load(from);
                bool latex = document.blocks.OfType<NoteBlock>().All(b => b.stylingType == TextStyleType.Code && b.language == "latex");
                document.Save(to);
                byte[] back = File.ReadAllBytes(to);
                File.Delete(from);
                File.Delete(to);

                string shown = source.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\uFEFF", "BOM");
                t.Check(back.SequenceEqual(bytes), $"'{shown}' saves back byte for byte");
                t.Check(latex, $"'{shown}' loads as LaTeX code lines");
            }
            yield break;
        }

        [A_XSDActionDependency("Tex.Catcodes", "Test")]
        private static IEnumerator<int> Catcodes(TestContext t)
        {
            t.Check(Run(@"\def\x{X}\x  a%") == "Xa", "a control word swallows the spaces after it");
            t.Check(Run("a   b%") == "a b", "a run of spaces reads as one");
            t.Check(Run("a%comment\nb%") == "ab", "a comment takes the rest of its line and the line end");
            t.Check(Run("a\n\nb%") == @"a \par b", "a line end is a space and a blank line is \\par");
            t.Check(Run(@"\makeatletter\def\a@b{Y}\a@b\makeatother%") == "Y", "\\makeatletter makes @ a letter");

            string after = Run(@"\makeatletter\def\a@b{Y}\makeatother\a@b%", out List<TexError> errors);
            t.Check(after == "@b" && errors.Count == 1 && Has(errors, @"Undefined control sequence \a"),
                $"after \\makeatother, @ ends the name again ({after})");

            t.Check(Run(@"\catcode`\!=0 !def!x{Z}!x%") == "Z", "a character given category 0 starts control sequences");
            t.Check(Run(@"{\catcode`\!=0 }!x%") == "{}!x", "a category change ends with its group");
            yield break;
        }

        [A_XSDActionDependency("Tex.DelimitedDef", "Test")]
        private static IEnumerator<int> DelimitedDef(TestContext t)
        {
            const string def = @"\def\a#1.#2\stop{(#2,#1)}";
            t.Check(Run(def + @"\a x.{y.z}\stop%") == "(y.z,x)", "delimited arguments, and a whole-group argument loses its braces");
            t.Check(Run(def + @"\a {a.b}.c\stop%") == "(c,a.b)", "a delimiter inside braces does not end the argument");
            t.Check(Run(@"\def\b#1#2{[#2#1]}\b x {yz}%") == "[yzx]", "undelimited arguments skip spaces and take a group");
            t.Check(Run(@"\def\c.#1{<#1>}\c.q%") == "<q>", "a delimiter before the first parameter is matched");

            Run(@"\def\c.#1{<#1>}\c q%", out List<TexError> errors);
            t.Check(Has(errors, @"Use of \c doesn't match its definition"), "a call that misses the leading delimiter is an error");
            yield break;
        }

        [A_XSDActionDependency("Tex.ExpandAfter", "Test")]
        private static IEnumerator<int> ExpandAfter(TestContext t)
        {
            t.Check(Run(@"\def\a{A}\def\b#1{[#1]}\expandafter\b\a%") == "[A]", "\\expandafter expands the token after next first");
            t.Check(Run(@"\def\a{\b}\def\b{B}\def\w#1{(#1)}\expandafter\expandafter\expandafter\w\a%") == "(B)",
                "three \\expandafter reach two levels down");
            t.Check(Run(@"\expandafter\def\csname x y\endcsname{Z}\csname x y\endcsname%") == "Z", "\\csname builds a name with a space in it");
            t.Check(Run(@"\string\foo%") == @"\foo", "\\string gives the name as characters");
            yield break;
        }

        [A_XSDActionDependency("Tex.Edef", "Test")]
        private static IEnumerator<int> Edef(TestContext t)
        {
            t.Check(Run(@"\def\a{1}\edef\b{\a\noexpand\a}\def\a{2}\b%") == "12", "\\edef expands now, except after \\noexpand");
            t.Check(Run(@"\count1=7 \edef\c{\the\count1}\count1=9 \c%") == "7", "\\the inside \\edef takes the value at definition");
            yield break;
        }

        [A_XSDActionDependency("Tex.Groups", "Test")]
        private static IEnumerator<int> Groups(TestContext t)
        {
            t.Check(Run(@"\def\a{O}{\def\a{I}\a}\a%") == "{I}O", "a definition inside a group ends with it");
            t.Check(Run(@"{\gdef\a{G}}\a%") == "{}G", "\\gdef survives the group");
            t.Check(Run(@"\def\a{1}{\def\a{2}\gdef\a{3}}\a%") == "{}3", "a global change wins over the group's saved value");
            t.Check(Run(@"{\count1=5 }\the\count1%") == "{}0", "a register assignment is local");
            t.Check(Run(@"{\global\count1=5 }\the\count1%") == "{}5", "\\global makes it survive");

            string semi = Run(@"\begingroup\def\a{S}\endgroup\a%", out List<TexError> errors);
            t.Check(semi == "" && errors.Count == 1 && Has(errors, @"Undefined control sequence \a"), "\\begingroup scopes like a brace");
            yield break;
        }

        [A_XSDActionDependency("Tex.Conditionals", "Test")]
        private static IEnumerator<int> Conditionals(TestContext t)
        {
            t.Check(Run(@"\ifnum 3>2 Y\else N\fi%") == "Y", "\\ifnum compares and \\else skips its branch");
            t.Check(Run(@"\def\a{x}\def\b{x}\ifx\a\b S\else D\fi%") == "S", "\\ifx finds two macros with the same text equal");
            t.Check(Run(@"\ifdim 1in>72pt T\else F\fi%") == "T", "\\ifdim compares in sp: 1in is 72.27pt");
            t.Check(Run(@"\ifcase 2 a\or b\or c\else d\fi%") == "c", "\\ifcase takes the branch it counts to");
            t.Check(Run(@"\ifcase 5 a\or b\else d\fi%") == "d", "\\ifcase past its branches takes \\else");
            t.Check(Run(@"\iffalse \ifnum1=1 x\else y\fi\else z\fi%") == "z", "a skipped branch skips nested conditionals whole");
            t.Check(Run(@"\ifodd 3 O\fi%") == "O", "\\ifodd");
            t.Check(Run(@"\if aaE\fi\ifcat a1X\else Y\fi%") == "EY", "\\if compares characters, \\ifcat categories");
            t.Check(Run(@"\ifdefined\nothing Y\else N\fi\ifcsname relax\endcsname Y\fi%") == "NY", "\\ifdefined and \\ifcsname");
            t.Check(Run(@"\newif\ifdraft \drafttrue \ifdraft D\else F\fi%") == "D", "\\newif makes a switch and its setters");
            yield break;
        }

        [A_XSDActionDependency("Tex.Registers", "Test")]
        private static IEnumerator<int> Registers(TestContext t)
        {
            t.Check(Run(@"\count1=5 \advance\count1 by 7 \multiply\count1 2 \the\count1%") == "24", "count arithmetic");
            string inch = Run(@"\dimen0=1in \the\dimen0%");
            t.Check(inch == "72.26999pt", $"1in reads back as TeX prints it ({inch})");
            string cm = Run(@"\dimen0=2.5cm \the\dimen0%");
            t.Check(cm == "71.13188pt", $"2.5cm rounds as tex.web does ({cm})");
            t.Check(Run(@"\dimen0=-.5pt \advance\dimen0 by 2\dimen0 \the\dimen0%") == "-1.5pt", "a register as the unit of a dimension");
            t.Check(Run(@"\dimen0=1em \the\dimen0%") == "10.0pt", "em is the fixed 10pt font's");
            string skip = Run(@"\skip0=3pt plus 1fil minus 2pt \the\skip0%");
            t.Check(skip == "3.0pt plus 1.0fil minus 2.0pt", $"glue with fil stretch ({skip})");
            t.Check(Run(@"\countdef\c=3 \c=9 \the\count3%") == "9", "\\countdef names a register");
            t.Check(Run(@"\chardef\x=65 \x%") == "A", "\\chardef names a character");
            t.Check(Run(@"\toks0={ab}\the\toks0%") == "ab", "a token register");
            t.Check(Run(@"\newcounter{c}\setcounter{c}{4}\addtocounter{c}{3}\thec\ifnum\value{c}=7 Y\fi%") == "7Y", "LaTeX counters");
            yield break;
        }

        [A_XSDActionDependency("Tex.NewCommand", "Test")]
        private static IEnumerator<int> NewCommand(TestContext t)
        {
            t.Check(Run(@"\newcommand{\hi}[2][W]{#1-#2}\hi{x} \hi[y]{z}%") == "W-x y-z", "an optional first argument and its default");

            string twice = Run(@"\newcommand\hi{a}\newcommand\hi{b}\hi%", out List<TexError> errors);
            t.Check(twice == "a" && Has(errors, @"Command \hi already defined"), "\\newcommand refuses a defined name");
            t.Check(Run(@"\newcommand\hi{a}\renewcommand\hi{b}\hi%") == "b", "\\renewcommand replaces it");
            t.Check(Run(@"\providecommand\hi{a}\providecommand\hi{b}\hi%") == "a", "\\providecommand keeps the first");

            Run(@"\newcommand*\s[1]{#1}\s{a\par b}%", out errors);
            t.Check(Has(errors, @"Paragraph ended before \s was complete"), "a starred command's argument cannot hold \\par");

            t.Check(Run(@"\makeatletter\def\t{\@ifnextchar[{O}{N}}\t[ \t x%") == "O[ Nx", "\\@ifnextchar looks without taking");
            t.Check(Run(@"\makeatletter\def\s{\@ifstar{S}{P}}\s*\s.%") == "SP.", "\\@ifstar takes the star");
            yield break;
        }

        [A_XSDActionDependency("Tex.NewEnvironment", "Test")]
        private static IEnumerator<int> NewEnvironment(TestContext t)
        {
            t.Check(Run(@"\newenvironment{box}[1]{<#1|}{|>}\begin{box}{t}x\end{box}%") == "<t|x|>", "an environment's begin code takes its argument");

            string local = Run(@"\newenvironment{e}{\def\q{in}}{}\begin{e}\q\end{e}\q%", out List<TexError> errors);
            t.Check(local == "in" && errors.Count == 1 && Has(errors, @"Undefined control sequence \q"), "an environment is a group");

            Run(@"\newenvironment{a}{}{}\begin{a}\end{b}%", out errors);
            t.Check(Has(errors, @"\begin{a} on input line 1 ended by \end{b}"), "a mismatched \\end is an error");

            Run(@"\begin{zz}\end{zz}%", out errors);
            t.Check(errors.Count == 1 && Has(errors, "Environment zz undefined"), "an undefined environment is one error");
            yield break;
        }

        [A_XSDActionDependency("Tex.Errors", "Test")]
        private static IEnumerator<int> Errors(TestContext t)
        {
            Run("a\n\\nope b%", out List<TexError> errors);
            t.Check(Has(errors, @"Undefined control sequence \nope", 2), "an undefined command is reported on its line");

            Run("\\def\\a#1{#1}\n\\a{x\n\nb}%", out errors);
            t.Check(Has(errors, @"Paragraph ended before \a was complete", 3), "a runaway argument is reported where the paragraph ends");

            Run(@"\def\a{x%", out errors);
            t.Check(Has(errors, @"File ended while scanning definition of \a"), "a definition missing its brace");

            Run("a}%", out errors);
            t.Check(Has(errors, "Too many }'s"), "an extra closing brace");

            Run("{a%", out errors);
            t.Check(Has(errors, "group opened on line 1"), "a group left open at the end");

            Run(@"\def\a{\a}\a", out errors);
            t.Check(Has(errors, "Expansion limit"), "a macro calling itself stops at the expansion limit");
            yield break;
        }

        [A_XSDActionDependency("Tex.StepCounter", "Test")]
        private static IEnumerator<int> StepCounter(TestContext t)
        {
            const string counters = @"\newcounter{a}\newcounter{b}[a]\newcounter{c}[b]\setcounter{b}{5}\setcounter{c}{3}";
            t.Check(Run(counters + @"\stepcounter{a}\the\value{a},\the\value{b},\the\value{c}%") == "1,0,0",
                "\\stepcounter zeroes the counters within it, and theirs");
            t.Check(Run(counters + @"{\stepcounter{c}}\the\value{c}%") == "{}4", "\\stepcounter is global");

            Run(@"\newcounter{d}[zz]%", out List<TexError> errors);
            t.Check(Has(errors, "No counter 'zz' defined"), "[within] must name a counter");
            yield break;
        }

        [A_XSDActionDependency("Tex.EmFromFont", "Test")]
        private static IEnumerator<int> EmFromFont(TestContext t)
        {
            TexExpander expander = new TexExpander(@"\dimen0=2em \the\dimen0,\dimen1=1ex \the\dimen1%")
            {
                quad = () => 20 * 65536,
                xHeight = () => 8 * 65536
            };
            StringBuilder text = new StringBuilder();
            while (expander.Next(out TexToken token))
                text.Append(token.ch);
            t.Check(text.ToString() == "40.0pt,8.0pt", $"em and ex come from the font the caller names: {text}");

            TexTypesetter typesetter = Typeset(@"\Large\hskip1em\hskip1ex x", out _);
            List<TexGlueNode> glue = Pars(typesetter)[0].list.OfType<TexGlueNode>().ToList();
            int large = (int)Math.Round(14.4 * 65536);
            t.Check(glue[0].glue.width == large && glue[1].glue.width == (int)(large * 0.430554),
                "the typesetter's current size is the em and ex");
            yield break;
        }

        #region ---- L10g: the thesis against its reference ----
        [A_XSDActionDependency("Tex.FootSkip", "Test")]
        private static IEnumerator<int> FootSkip(TestContext t)
        {
            XElement document = TexLowering.Compile("\\documentclass{article}\\usepackage[footskip=1.5cm,headsep=5mm]{geometry}\\begin{document}x\\end{document}",
                out List<TexError> errors);
            XElement? style = document.Descendants("PageStyle").FirstOrDefault();
            t.Check(errors.Count == 0 && style != null && Near((float)style.Attribute("FootSkip")!, 15f) && Near((float)style.Attribute("HeadSep")!, 5f),
                $"geometry's footskip and headsep reach the page styles: {style}");
            yield break;
        }

        [A_XSDActionDependency("Tex.BibItemSep", "Test")]
        private static IEnumerator<int> BibItemSep(TestContext t)
        {
            string folder = Folder(("refs.bib", "@misc{a, title={First}, year={2020}}\n@misc{b, title={Second}, year={2021}}\n"));
            TexTypesetter typesetter = new TexTypesetter("\\documentclass{article}\\usepackage[style=numeric]{biblatex}\\addbibresource{refs.bib}"
                + "\\setlength{\\parskip}{3pt}\\begin{document}\\cite{a,b}\\printbibliography\\end{document}", null, folder);
            typesetter.Run();
            Directory.Delete(folder, true);
            List<TexNode> v = typesetter.vlist;
            List<TexParagraph> pars = Pars(typesetter);
            int first = v.IndexOf(pars[^2]), second = v.IndexOf(pars[^1]);
            float gap = 0f;
            for (int i = first + 1; i < second; i++)
                if (v[i] is TexVGlue g) gap += Pt(g.glue.width);
            t.Check(typesetter.errors.Count == 0 && pars.Count >= 3 && gap == 4f, $"biblatex entries stand \\bibitemsep apart with no \\parskip: {gap}pt");
            yield break;
        }

        [A_XSDActionDependency("Tex.CaptionSingleLine", "Test")]
        private static IEnumerator<int> CaptionSingleLine(TestContext t)
        {
            string longText = string.Concat(Enumerable.Repeat("a caption that runs on ", 12));
            TexTypesetter typesetter = Typeset("\\documentclass{article}\\begin{document}\\begin{figure}\\caption{Short}\\end{figure}"
                + "\\begin{figure}\\caption{" + longText + "}\\end{figure}\\end{document}", out List<TexError> errors);
            List<TexParagraph> captions = Pars(typesetter).Where(p => Line(p).Contains("Short") || Line(p).Contains("runs on")).ToList();
            t.Check(errors.Count == 0 && captions.Count == 2 && captions[0].style.align == TexAlign.Center && captions[1].style.align == TexAlign.Justify,
                $"a caption that fits is centred, a longer one justified: {string.Join(", ", captions.Select(c => c.style.align))}");
            yield break;
        }

        [A_XSDActionDependency("Tex.DisplayAfterPar", "Test")]
        private static IEnumerator<int> DisplayAfterPar(TestContext t)
        {
            float px = 96f / 72.27f;
            List<XElement> after = TexLowering.Compile("\\documentclass{article}\\begin{document}Text\n\n\\begin{equation}x\\end{equation}\n\nMore\\end{document}",
                out List<TexError> errors).Elements("Block").ToList();
            t.Check(errors.Count == 0 && after.Count == 4 && (string?)after[1].Attribute("KeepWithNext") == "true" && after[1].Element("Run")?.Attribute("Text") == null
                && after[2].Attribute("SpaceBefore") == null && Near((float?)after[3].Attribute("SpaceBefore") ?? 0f, 6f * px, 0.05f),
                $"a display after a blank line: an empty line kept with it above, \\belowdisplayshortskip below: {string.Join(" / ", after.Select(b => b.Attribute("SpaceBefore")?.Value))}");

            List<XElement> inside = TexLowering.Compile("\\documentclass{article}\\begin{document}Text \\[x\\] more\n\nNext\\end{document}", out errors)
                .Elements("Block").ToList();
            t.Check(errors.Count == 0 && inside.Count == 4 && Near((float?)inside[1].Attribute("SpaceBefore") ?? 0f, 10f * px, 0.05f)
                && Near((float?)inside[2].Attribute("SpaceBefore") ?? 0f, 10f * px, 0.05f),
                $"a display inside a paragraph: \\abovedisplayskip and \\belowdisplayskip: {string.Join(" / ", inside.Select(b => b.Attribute("SpaceBefore")?.Value))}");
            yield break;
        }

        [A_XSDActionDependency("Tex.DisplayIntegral", "Test")]
        private static IEnumerator<int> DisplayIntegral(TestContext t)
        {
            MathConstants? math = AssetRegistries.GetRegistryByValueType<string, FontAsset>(typeof(FontAsset))[TexLowering.MathFont].mathConstants;
            t.Check(math != null && Near(math.DisplayHeight('∫'), 2.223f, 0.005f) && Near(math.DisplayHeight('∑'), 1.401f, 0.005f),
                $"display operators take the MATH table's variant heights: ∫ {math?.DisplayHeight('∫')} ∑ {math?.DisplayHeight('∑')}");
            t.Check(math != null && math.DisplayHeight('x') == math.displayOperatorMinHeight, "anything else falls back to DisplayOperatorMinHeight");
            yield break;
        }

        [A_XSDActionDependency("Tex.BlockLineHeight", "Test")]
        private static IEnumerator<int> BlockLineHeight(TestContext t)
        {
            XElement document = TexLowering.Compile("\\documentclass{article}\\usepackage{setspace}\\setstretch{2}\\begin{document}Body\n\n"
                + "{\\fontsize{14pt}{20pt}\\selectfont Big\\par}\n\n\\begin{figure}\\caption{Single}\\end{figure}\\end{document}", out List<TexError> errors);
            bool Holds(XElement block, string text) => block.Elements("Run").Any(r => ((string?)r.Attribute("Text") ?? "").Replace("­", "").Contains(text));
            XElement? big = document.Elements("Block").FirstOrDefault(b => Holds(b, "Big"));
            XElement? caption = document.Descendants("Block").FirstOrDefault(b => Holds(b, "Single"));
            t.Check(errors.Count == 0 && big != null && Near((float)big.Attribute("LineHeight")!, 2f * 20f / 14f, 0.002f),
                $"\\fontsize's \\baselineskip, stretched, over the 14pt it is set in: {big?.Attribute("LineHeight")?.Value}");
            t.Check(caption != null && Near((float)caption.Attribute("LineHeight")!, 12f / 10f, 0.002f),
                $"setspace leaves floats single-spaced: {caption?.Attribute("LineHeight")?.Value}");
            yield break;
        }

        [A_XSDActionDependency("Tex.BaselineSpacing", "Test")]
        private static IEnumerator<int> BaselineSpacing(TestContext t)
        {
            XElement layout = TexLowering.Compile("\\documentclass{article}\\begin{document}x\\end{document}", out _).Element("DocumentLayout")!;
            t.Check(Near((float)layout.Attribute("BaselineDepth")!, 2f * 96f / 72.27f, 0.002f) && (string?)layout.Attribute("WidowControl") == "true",
                $"the depth is \\baselineskip less \\topskip, and widow control is on: {layout.Attribute("BaselineDepth")?.Value}");

            IGlyphMetrics metrics = new FontAssetGlyphMetrics();
            AtlasMetaData atlas = AssetRegistries.GetRegistryByValueType<string, FontAsset>(typeof(FontAsset))["default"].atlasMetaData;
            List<TextMeasurer.Run> runs = new List<TextMeasurer.Run> { new TextMeasurer.Run("Line", 0, 4, "default", atlas, 16, FontStyle.Regular) };
            TextLine line = TextMeasurer.MeasureBlock(runs, 500f, metrics, 1.5f, baselineDepth: 5f).lines[0];
            t.Check(line.descent == 5f && line.ascent == 19f, $"a fixed depth: every line's descent is it, the rest of the box above: {line.ascent}+{line.descent}");
            yield break;
        }

        [A_XSDActionDependency("Tex.FractionalZoom", "Test")]
        private static IEnumerator<int> FractionalZoom(TestContext t)
        {
            IGlyphMetrics metrics = new FontAssetGlyphMetrics();
            AtlasMetaData atlas = AssetRegistries.GetRegistryByValueType<string, FontAsset>(typeof(FontAsset))["default"].atlasMetaData;
            List<TextMeasurer.Run> runs = new List<TextMeasurer.Run> { new TextMeasurer.Run("Line", 0, 4, "default", atlas, 16f * 0.9f, FontStyle.Regular) };
            TextLine line = TextMeasurer.MeasureBlock(runs, 500f, metrics, 1.5f).lines[0];
            t.Check(Near(line.ascent + line.descent, 16f * 0.9f * 1.5f, 0.001f),
                $"a zoomed size stays fractional, so the preview paginates as the export does: {line.ascent + line.descent}");
            yield break;
        }

        [A_XSDActionDependency("Tex.CenterLineBreak", "Test")]
        private static IEnumerator<int> CenterLineBreak(TestContext t)
        {
            List<XElement> blocks = TexLowering.Compile("\\documentclass{article}\\setlength{\\parskip}{3pt}\\begin{document}\\begin{center}a\\\\[2pt]b\\\\\nc\\end{center}"
                + "\\end{document}", out List<TexError> errors).Elements("Block").ToList();
            float px = 96f / 72.27f;
            t.Check(errors.Count == 0 && blocks.Count == 3 && Near((float?)blocks[1].Attribute("SpaceBefore") ?? 0f, 2f * px, 0.02f)
                && blocks[2].Attribute("SpaceBefore") == null,
                $"\\\\ in centred text ends the paragraph and takes its \\parskip back: {string.Join(" / ", blocks.Select(b => b.Attribute("SpaceBefore")?.Value))}");
            yield break;
        }
        #endregion

        #region ---- L11: the thesis against its reference, page and list rules ----
        [A_XSDActionDependency("Tex.PageOverhang", "Test")]
        private static IEnumerator<int> PageOverhang(TestContext t)
        {
            PageBands bands = new PageBands(0f, 100f, 200f);
            t.Check(bands.Push(85f, 20f, 6f) == 85f && bands.Push(85f, 20f) == 200f && bands.Push(90f, 20f, 6f) == 200f,
                "a line fits while its baseline does, its descent hanging below the text bottom");
            t.Check(new PageSpace(0f).Push(bands, 85f, 20f, 0f, 6f) == 85f && new PageSpace(0f).Push(bands, 85f, 20f) == 200f,
                "the paginate pass's push takes the same overhang");
            yield break;
        }

        [A_XSDActionDependency("Tex.ListTopSkip", "Test")]
        private static IEnumerator<int> ListTopSkip(TestContext t)
        {
            TexTypesetter typesetter = Typeset("\\documentclass{article}\\setlength{\\parskip}{3pt}\\begin{document}Text\n\\begin{itemize}\\item a\\end{itemize}\\end{document}",
                out List<TexError> errors);
            List<TexNode> v = typesetter.vlist;
            List<TexParagraph> pars = Pars(typesetter);
            float gap = 0f;
            for (int i = v.IndexOf(pars[0]) + 1; i < v.IndexOf(pars[1]); i++)
                if (v[i] is TexVGlue g) gap += Pt(g.glue.width);
            t.Check(errors.Count == 0 && pars.Count == 2 && Near(gap, 11f), $"a list opens \\topsep and the outer \\parskip below the text: {gap}pt");
            yield break;
        }

        [A_XSDActionDependency("Tex.ListIndent", "Test")]
        private static IEnumerator<int> ListIndent(TestContext t)
        {
            XElement document = TexLowering.Compile("\\documentclass{article}\\begin{document}\\begin{itemize}\\item a\\begin{itemize}\\item b\\end{itemize}\\end{itemize}"
                + "\\end{document}", out List<TexError> errors);
            XElement layout = document.Element("DocumentLayout")!;
            float normal = 0f;
            foreach (XElement style in layout.Elements("TextStyle"))
                if ((string?)style.Attribute("Type") == "Text") normal = (float)style.Attribute("FontSize")!;
            XElement? nested = null;
            foreach (XElement block in document.Elements("Block"))
                if ((string?)block.Attribute("Level") == "1") nested = block;
            t.Check(errors.Count == 0 && Near((float)layout.Attribute("ListIndent")!, 2.5f * normal, 0.01f), $"\\leftmargini is 2.5em: {layout.Attribute("ListIndent")?.Value}");
            t.Check(nested != null && Near(2f * (float)nested.Attribute("ListIndent")!, 4.7f * normal, 0.01f),
                $"a second-level item stands \\leftmargini plus \\leftmarginii in: {nested?.Attribute("ListIndent")?.Value}");
            yield break;
        }

        [A_XSDActionDependency("Tex.KeepWithNext", "Test")]
        private static IEnumerator<int> KeepWithNext(TestContext t)
        {
            XElement document = new XElement("Document",
                new XElement("DocumentLayout", new XElement("Page", new XAttribute("Mode", "Paged"), new XAttribute("Size", "A6"))));
            for (int i = 0; i < 40; i++)
            {
                document.Add(new XElement("Block", new XElement("Run", new XAttribute("Text", $"line {i}"))));
                document.Add(new XElement("Block", new XAttribute("StylingType", "Heading2"), new XAttribute("KeepWithNext", "true"), new XElement("Run", new XAttribute("Text", $"heading {i}"))));
            }
            document.Add(new XElement("Block", new XElement("Run", new XAttribute("Text", "end"))));
            RichTextDocument parsed = DocumentXml.Parse(document);
            bool written = false;
            foreach (XElement e in DocumentXml.ToXml(parsed).Descendants())
                if ((string?)e.Attribute("KeepWithNext") == "true") written = true;
            t.Check(parsed.blocks[1] is NoteBlock { keepWithNext: true } && written, "KeepWithNext round-trips through .axml");

            DocumentEditorControl editor = ShowDocument(t, document);
            yield return 2;
            int split = -1, moved = 0;
            for (int i = 1; i < 81; i += 2)
            {
                int? heading = editor.PageAt(i, 0), next = editor.PageAt(i + 1, 0), before = editor.PageAt(i - 1, 0);
                if (heading != next) split = i;
                if (heading > before) moved++;
            }
            t.Check(split < 0 && moved > 0 && editor.PageAt(80, 0) > 0, $"no heading ends a page apart from the block after it: split at {split}, {moved} moved");
            t.Show(new StackPanelControl());
        }
        #endregion

        #region ---- L11c: the thesis against its reference, display vertical model ----
        [A_XSDActionDependency("Tex.Interline", "Test")]
        private static IEnumerator<int> Interline(TestContext t)
        {
            TextLine text = new TextLine { ascent = 19f, descent = 5f, ink = 11f, inkDepth = 3f, pitch = 24f };
            TextLine tall = new TextLine { ascent = 30f, descent = 5f, ink = 30f, inkDepth = 2f, pitch = 24f };
            TextLine deep = new TextLine { ascent = 19f, descent = 12f, ink = 11f, inkDepth = 12f, pitch = 24f };
            TextLine note = new TextLine { ascent = 19f, descent = 5f, ink = 30f };
            t.Check(TextMeasurer.Interline(text, text, 1f) == 0f, "ordinary lines keep \\baselineskip apart");
            t.Check(Near(TextMeasurer.Interline(text, tall, 1f), 3f + 30f + 1f - 35f), $"a tall line sits \\lineskip under the depth above: {TextMeasurer.Interline(text, tall, 1f)}");
            t.Check(Near(TextMeasurer.Interline(deep, text, 1f), 24f - 31f), $"under a deep line whose ink still fits, \\baselineskip: {TextMeasurer.Interline(deep, text, 1f)}");
            t.Check(TextMeasurer.Interline(text, note, 1f) == 0f, "a line without a fixed depth takes no interline glue");
            yield break;
        }

        [A_XSDActionDependency("Tex.InlineTallLine", "Test")]
        private static IEnumerator<int> InlineTallLine(TestContext t)
        {
            IGlyphMetrics metrics = new FontAssetGlyphMetrics();
            AtlasMetaData atlas = AssetRegistries.GetRegistryByValueType<string, FontAsset>(typeof(FontAsset))["default"].atlasMetaData;
            string text = "Line ￼";
            List<TextMeasurer.Run> runs = new List<TextMeasurer.Run>
            {
                new TextMeasurer.Run(text, 0, 5, "default", atlas, 16, FontStyle.Regular),
                new TextMeasurer.Run(text, 5, 1, "default", atlas, 16, FontStyle.Regular, imageWidth: 480f, imageHeight: 40f, math: true, depth: 2f)
            };
            List<TextLine> lines = TextMeasurer.MeasureBlock(runs, 500f, metrics, 1.5f, baselineDepth: 5f, lineSkip: 1.5f).lines;
            float apart = lines.Count == 2 ? lines[1].baseline - lines[0].baseline : 0f;
            t.Check(lines.Count == 2 && Near(apart, lines[0].inkDepth + 40f + 1.5f, 0.001f),
                $"a formula taller than the line box sits \\lineskip under the line above's ink: {apart} with depth {(lines.Count > 0 ? lines[0].inkDepth : 0f)}");
            List<TextLine> plain = TextMeasurer.MeasureBlock(runs, 500f, metrics, 1.5f).lines;
            t.Check(plain.Count == 2 && Near(plain[1].baseline - plain[0].baseline, plain[0].descent + 40f, 0.001f), "notes stack line boxes as before");
            yield break;
        }

        [A_XSDActionDependency("Tex.DisplayInterline", "Test")]
        private static IEnumerator<int> DisplayInterline(TestContext t)
        {
            XElement document = new XElement("Document",
                new XElement("DocumentLayout", new XAttribute("BaselineDepth", "6"), new XAttribute("LineSkip", "1.33"), new XAttribute("BlockSpacing", "0")),
                new XElement("Block", new XElement("Run", new XAttribute("Text", "Text above"))),
                new XElement("Block", new XElement("Run", new XAttribute("Math", @"\begin{matrix}a\\b\\c\\d\end{matrix}"), new XAttribute("Display", "true"),
                    new XAttribute("FontName", TexLowering.MathFont))));
            DocumentEditorControl editor = ShowDocument(t, document);
            yield return 2;

            BlockControl? above = null, display = null;
            foreach (Entity child in Content(editor).children)
                if (child is BlockControl block)
                {
                    if (above == null) above = block;
                    else display ??= block;
                }
            if (above == null || display == null || above.Lines.Count == 0 || display.Lines.Count == 0)
            {
                t.Check(false, "the text and the display are laid out");
                yield break;
            }
            TextLine over = above.Lines[^1], formula = display.Lines[0];
            float apart = display.arrangedRect.y + formula.baseline - (above.arrangedRect.y + over.baseline);
            t.Check(formula.ink > formula.pitch - over.inkDepth && Near(apart, over.inkDepth + formula.ink + display.LineSkip, 0.05f),
                $"a display taller than the line box sits \\lineskip under the block above's ink: {apart}, ink {formula.ink} pitch {formula.pitch}");
            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("Tex.MatrixStrut", "Test")]
        private static IEnumerator<int> MatrixStrut(TestContext t)
        {
            static float Strut(string source)
            {
                foreach (XElement run in TexLowering.Compile(source, out _).Descendants("Run"))
                    if (run.Attribute("Strut") is XAttribute strut) return (float)strut;
                return 0f;
            }
            const string matrix = "Text \\[\\begin{bmatrix}a\\\\b\\end{bmatrix}\\]\\end{document}";
            float spaced = Strut("\\documentclass[12pt]{article}\\usepackage{setspace}\\onehalfspacing\\begin{document}" + matrix);
            float stretched = Strut("\\documentclass[12pt]{article}\\renewcommand{\\arraystretch}{2}\\begin{document}" + matrix);
            t.Check(Near(spaced, 1.241f * 14.5f / 12f, 0.002f), $"a matrix row's strut is the stretched \\baselineskip: {spaced} em");
            t.Check(Near(stretched, 2f * 14.5f / 12f, 0.002f), $"\\arraystretch multiplies it: {stretched} em");
            t.Check(Strut("\\documentclass{article}\\begin{document}$x$\\end{document}") == 0f, "a formula without an array carries no strut");

            FontAsset font = AssetRegistries.GetRegistryByValueType<string, FontAsset>(typeof(FontAsset))[TexLowering.MathFont];
            MathNode rows = MathParser.Parse(@"\begin{matrix}a\\b\end{matrix}");
            MathBox plain = MathLayout.Layout(rows, true, font.atlasMetaData, font.mathConstants!);
            MathBox same = MathLayout.Layout(rows, true, font.atlasMetaData, font.mathConstants!, strut: 1.2f);
            MathBox tall = MathLayout.Layout(rows, true, font.atlasMetaData, font.mathConstants!, strut: 2.4f);
            float grown = tall.height + tall.depth - (plain.height + plain.depth);
            t.Check(same.height == plain.height && same.depth == plain.depth && Near(grown, 2.4f, 0.01f),
                $"notes keep 1.2 em rows; a 2.4 em strut adds 1.2 em to each of the two rows: {grown}");
            yield break;
        }
        #endregion

        #region ---- L11c2: classic TeX math rules ----
        [A_XSDActionDependency("Tex.MathRulesMode", "Test")]
        private static IEnumerator<int> MathRulesMode(TestContext t)
        {
            XElement classic = TexLowering.Compile("\\documentclass{article}\\begin{document}$x_c$\\end{document}", out _).Element("DocumentLayout")!;
            XElement unicode = TexLowering.Compile("\\documentclass{article}\\usepackage{unicode-math}\\begin{document}$x_c$\\end{document}", out _)
                .Element("DocumentLayout")!;
            t.Check((string?)classic.Attribute("MathRules") == "Tfm" && unicode.Attribute("MathRules") == null,
                $"LaTeX without unicode-math places math by TeX's font dimensions: {classic.Attribute("MathRules")?.Value} / {unicode.Attribute("MathRules")?.Value}");
            yield break;
        }

        [A_XSDActionDependency("Tex.ClassicScripts", "Test")]
        private static IEnumerator<int> ClassicScripts(TestContext t)
        {
            FontAsset font = AssetRegistries.GetRegistryByValueType<string, FontAsset>(typeof(FontAsset))[TexLowering.MathFont];
            MathConstants c = font.mathConstants!;
            static float Below(MathBox box, char ch)
            {
                foreach (MathGlyph g in box.glyphs)
                    if (g.ch == ch) return -g.y;
                return float.NaN;
            }
            MathNode subscript = MathParser.Parse("x_c");
            float tfm = Below(MathLayout.Layout(subscript, false, font.atlasMetaData, c, rules: MathRules.Tfm), 'c');
            float open = Below(MathLayout.Layout(subscript, false, font.atlasMetaData, c), 'c');
            t.Check(Near(tfm, 0.15f, 0.001f) && Near(open, c.subscriptShiftDown, 0.001f), $"a lone subscript drops sub1 0.15 em, OpenType's 0.247: {tfm} / {open}");

            MathBox integral = MathLayout.Layout(MathParser.Parse(@"\int_\Omega f"), true, font.atlasMetaData, c, rules: MathRules.Tfm);
            MathBox sign = MathLayout.Layout(MathParser.Parse(@"\int"), true, font.atlasMetaData, c, rules: MathRules.Tfm);
            float drop = Below(integral, 'Ω');
            t.Check(Near(drop, sign.depth + c.subDrop * c.scriptPercentScaleDown / 100f, 0.002f),
                $"a big operator's subscript hangs sub_drop at script size under its depth: {drop} for depth {sign.depth}");
            yield break;
        }
        #endregion

        #region ---- L11d: floats, captions and display skips against the thesis ----
        [A_XSDActionDependency("Tex.SetspaceDisplaySkip", "Test")]
        private static IEnumerator<int> SetspaceDisplaySkip(TestContext t)
        {
            float px = 96f / 72.27f;
            XElement document = TexLowering.Compile("\\documentclass[12pt]{article}\\usepackage{setspace}\\onehalfspacing\\begin{document}Text \\[x\\] more\n\nNext\\end{document}",
                out List<TexError> errors);
            XElement? display = null, after = null;
            int index = 0;
            foreach (XElement block in document.Elements("Block"))
            {
                if (index == 1) display = block;
                else if (index == 2) after = block;
                index++;
            }
            float above = (float?)display?.Attribute("SpaceBefore") ?? 0f, below = (float?)after?.Attribute("SpaceBefore") ?? 0f;
            t.Check(errors.Count == 0 && Near(above, 12f * 1.241f * px, 0.05f) && Near(below, 12f * 1.241f * px, 0.05f),
                $"setspace stretches \\abovedisplayskip and \\belowdisplayskip by \\baselinestretch: {above} / {below}");
            t.Check(display?.Attribute("ShrinkBefore") == null && after?.Attribute("ShrinkBefore") == null, "and leaves them rigid");
            yield break;
        }

        [A_XSDActionDependency("Tex.CaptionSkip", "Test")]
        private static IEnumerator<int> CaptionSkip(TestContext t)
        {
            float px = 96f / 72.27f;
            XElement document = TexLowering.Compile("\\documentclass{article}\\begin{document}\\begin{figure}[h]Body\\caption{Below}\\end{figure}"
                + "\\begin{table}[h]\\caption{Above}\\begin{tabular}{l}x\\end{tabular}\\end{table}\\end{document}", out List<TexError> errors);
            XElement? below = BlockHolding(document, "Below"), above = BlockHolding(document, "Above");
            XElement? table = null;
            if (above != null)
                foreach (XElement next in above.ElementsAfterSelf("Table"))
                {
                    table = next;
                    break;
                }
            t.Check(errors.Count == 0 && below != null && (string?)below.Attribute("Strut") == "true" && Near((float?)below.Attribute("SpaceBefore") ?? 0f, 10f * px, 0.05f),
                $"a caption after the float's body takes \\abovecaptionskip above it and a strut: {below?.Attribute("SpaceBefore")?.Value}");
            t.Check(above != null && (string?)above.Attribute("Strut") == "true" && above.Attribute("SpaceBefore") == null
                && Near((float?)table?.Attribute("SpaceBefore") ?? 0f, 10f * px, 0.05f),
                $"a caption first takes it below instead: {table?.Attribute("SpaceBefore")?.Value}");

            IGlyphMetrics metrics = new FontAssetGlyphMetrics();
            AtlasMetaData atlas = AssetRegistries.GetRegistryByValueType<string, FontAsset>(typeof(FontAsset))["default"].atlasMetaData;
            List<TextMeasurer.Run> runs = new List<TextMeasurer.Run> { new TextMeasurer.Run("Line", 0, 4, "default", atlas, 16, FontStyle.Regular) };
            TextLine line = TextMeasurer.MeasureBlock(runs, 500f, metrics, 1.5f, baselineDepth: 5f, strut: true).lines[0];
            t.Check(Near(line.ascent, MathF.Max(line.ink, 0.7f * 24f)) && Near(line.descent, MathF.Max(line.inkDepth, 0.3f * 24f)),
                $"a strut line stands 0.7 of its box above the baseline and 0.3 below: {line.ascent}+{line.descent}");
            yield break;
        }

        [A_XSDActionDependency("Tex.HereFloatSpacing", "Test")]
        private static IEnumerator<int> HereFloatSpacing(TestContext t)
        {
            XElement layout = TexLowering.Compile("\\documentclass[12pt]{article}\\begin{document}x\\end{document}", out _).Element("DocumentLayout")!;
            float shrink = (float?)layout.Element("Page")?.Attribute("InTextSepShrink") ?? 0f;
            t.Check(Near(shrink, 4f * 25.4f / 72.27f, 0.005f), $"\\intextsep shrinks by 4pt at 12pt: {shrink} mm");

            XElement document = new XElement("Document",
                new XElement("DocumentLayout", new XAttribute("BaselineDepth", "6"), new XAttribute("LineSkip", "1.33"), new XAttribute("BlockSpacing", "0"),
                    new XElement("Page", new XAttribute("Mode", "Paged"), new XAttribute("Size", "A4"), new XAttribute("InTextSep", "5"))),
                new XElement("Block", new XElement("Run", new XAttribute("Text", "Text above"))),
                new XElement("Float", new XAttribute("Kind", "figure"), new XAttribute("Placement", "H"),
                    new XElement("Block", new XElement("Run", new XAttribute("Text", "Inside")))),
                new XElement("Block", new XElement("Run", new XAttribute("Text", "Text below"))));
            DocumentEditorControl editor = ShowDocument(t, document);
            yield return 2;

            BlockControl? before = null, inside = null, after = null;
            foreach (Entity child in Content(editor).children)
                if (child is BlockControl block)
                {
                    if (before == null) before = block;
                    else if (inside == null) inside = block;
                    else after ??= block;
                }
            if (before == null || inside == null || after == null || before.Lines.Count == 0 || inside.Lines.Count == 0 || after.Lines.Count == 0)
            {
                t.Check(false, "the text and the float are laid out");
                yield break;
            }
            float sep = 5f * PageLayout.PxPerMm;
            TextLine over = before.Lines[^1], first = after.Lines[0];
            float floatTop = inside.arrangedRect.y, floatBottom = inside.arrangedRect.y + inside.Lines[^1].top + inside.Lines[^1].height;
            float gapAbove = floatTop - (before.arrangedRect.y + over.baseline);
            float gapBelow = after.arrangedRect.y + first.baseline - floatBottom;
            t.Check(Near(gapAbove, over.inkDepth + before.LineSkip + sep, 0.05f),
                $"a float set here stands \\intextsep under the ink above and its \\lineskip: {gapAbove}, depth {over.inkDepth}");
            t.Check(Near(gapBelow, sep + first.pitch, 0.05f), $"the text after it starts a whole \\baselineskip under the gap: {gapBelow}, pitch {first.pitch}");
            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("Tex.HFloatSpacing", "Test")]
        private static IEnumerator<int> HFloatSpacing(TestContext t)
        {
            XElement document = TexLowering.Compile("\\documentclass{article}\\usepackage{setspace}\\setstretch{2}\\begin{document}Body\n\n"
                + "\\begin{figure}[H]Inside\\caption{Single}\\end{figure}\\begin{figure}[h]Here\\end{figure}\\end{document}", out List<TexError> errors);
            XElement? inside = BlockHolding(document, "Inside"), caption = BlockHolding(document, "Single"), here = BlockHolding(document, "Here");
            t.Check(errors.Count == 0 && inside != null && inside.Attribute("LineHeight") == null,
                $"an [H] float keeps the body's stretched spacing: {inside?.Attribute("LineHeight")?.Value}");
            t.Check(caption != null && Near((float?)caption.Attribute("LineHeight") ?? 0f, 1.2f, 0.002f) && here != null && Near((float?)here.Attribute("LineHeight") ?? 0f, 1.2f, 0.002f),
                $"its caption and every other float are single-spaced: {caption?.Attribute("LineHeight")?.Value} / {here?.Attribute("LineHeight")?.Value}");
            yield break;
        }

        // The first block anywhere under root with a run holding text, soft hyphens aside.
        private static XElement? BlockHolding(XElement root, string text)
        {
            foreach (XElement block in root.Descendants("Block"))
                foreach (XElement run in block.Elements("Run"))
                    if (((string?)run.Attribute("Text") ?? "").Replace("­", "").Contains(text)) return block;
            return null;
        }
        #endregion

        #region ---- L11f: line breaking as TeX ----
        [A_XSDActionDependency("Tex.ExactSize", "Test")]
        private static IEnumerator<int> ExactSize(TestContext t)
        {
            XElement document = TexLowering.Compile("\\documentclass[12pt]{article}\\begin{document}Body {\\small small}\\end{document}", out _);
            float text = 0f;
            foreach (XElement style in document.Element("DocumentLayout")!.Elements("TextStyle"))
                if ((string?)style.Attribute("Type") == "Text") text = (float)style.Attribute("FontSize")!;
            float small = 0f;
            foreach (XElement run in document.Descendants("Run"))
                if ((string?)run.Attribute("Text") == "small") small = (float)run.Attribute("FontSize")!;
            t.Check(Near(text, 12f * 96f / 72.27f, 0.001f) && Near(small, 10.95f * 96f / 72.27f, 0.001f),
                $"sizes are TeX points exactly, not whole pixels: 12pt {text}, \\small {small}");
            yield break;
        }

        [A_XSDActionDependency("Tex.SpaceFactor", "Test")]
        private static IEnumerator<int> SpaceFactor(TestContext t)
        {
            static string Text(string body)
            {
                XElement document = TexLowering.Compile("\\documentclass{article}\\begin{document}" + body + "\\end{document}", out _);
                StringBuilder text = new StringBuilder();
                foreach (XElement run in document.Descendants("Run"))
                    text.Append((string?)run.Attribute("Text"));
                return text.ToString().Replace("­", "");
            }
            char sentence = TextMeasurer.FactorSpace(3f), colon = TextMeasurer.FactorSpace(2f), semicolon = TextMeasurer.FactorSpace(1.5f),
                comma = TextMeasurer.FactorSpace(1.25f);
            string plain = Text("One. Two, three; four: seven");
            t.Check(plain == $"One.{sentence}Two,{comma}three;{semicolon}four:{colon}seven", $"punctuation sets TeX's space factors: {Escape(plain)}");
            string upper = Text("NASA. And x.) Then");
            t.Check(upper == $"NASA. And x.){sentence}Then", $"a capital before the stop keeps a plain space; a closing parenthesis is transparent: {Escape(upper)}");
            string marked = Text("NASA\\@. Then e.g.\\@ so");
            t.Check(marked == $"NASA.{sentence}Then e.g. so", $"\\@ sets the factor to 1000: {Escape(marked)}");
            string french = Text("\\frenchspacing One. Two, three");
            t.Check(french == "One. Two, three", $"\\frenchspacing makes every space plain: {Escape(french)}");

            AtlasMetaData atlas = AssetRegistries.GetRegistryByValueType<string, FontAsset>(typeof(FontAsset))["default"].atlasMetaData;
            TextMeasurer.Run run = new TextMeasurer.Run("x", 0, 1, "default", atlas, 16, FontStyle.Regular);
            float space = TextMeasurer.MeasureAdvance(' ', run);
            t.Check(Near(TextMeasurer.MeasureAdvance(sentence, run), space * 4f / 3f, 0.001f) && Near(TextMeasurer.MeasureAdvance(comma, run), space, 0.001f),
                $"from a factor of 2 a space is a third wider: {TextMeasurer.MeasureAdvance(sentence, run)} against {space}");
            yield break;
        }

        [A_XSDActionDependency("Tex.Pretolerance", "Test")]
        private static IEnumerator<int> Pretolerance(TestContext t)
        {
            XElement layout = TexLowering.Compile("\\documentclass{article}\\tolerance 1414 \\emergencystretch 1.5em\\begin{document}x\\end{document}", out _)
                .Element("DocumentLayout")!;
            t.Check((string?)layout.Attribute("Tolerance") == "1414" && (string?)layout.Attribute("Pretolerance") == "100"
                && Near((float)layout.Attribute("EmergencyStretch")!, 15f * 96f / 72.27f, 0.01f),
                $"\\tolerance, \\pretolerance and \\emergencystretch reach the layout: {layout.Attribute("Tolerance")?.Value} {layout.Attribute("Pretolerance")?.Value} {layout.Attribute("EmergencyStretch")?.Value}");

            IGlyphMetrics metrics = new FontAssetGlyphMetrics();
            AtlasMetaData atlas = AssetRegistries.GetRegistryByValueType<string, FontAsset>(typeof(FontAsset))["default"].atlasMetaData;
            const string text = "Para­graph hy­phen­ation re­mark­ably for­ward words to­gether with com­put­ers and ty­po­graph­ers through­out";
            List<TextMeasurer.Run> runs = new List<TextMeasurer.Run> { new TextMeasurer.Run(text, 0, text.Length, "default", atlas, 16, FontStyle.Regular) };
            static bool Hyphenated(BlockLayout block)
            {
                foreach (TextLine line in block.lines)
                    if (line.hyphen > 0f) return true;
                return false;
            }
            bool everHyphenates = false, firstPassHyphenates = false;
            for (float width = 200f; width < 400f; width += 3f)
            {
                everHyphenates |= Hyphenated(TextMeasurer.MeasureBlock(runs, width, metrics, 1.5f, optimal: true));
                firstPassHyphenates |= Hyphenated(TextMeasurer.MeasureBlock(runs, width, metrics, 1.5f, optimal: true, pretolerance: 10000f));
            }
            t.Check(everHyphenates && !firstPassHyphenates,
                $"a first pass within \\pretolerance tries no hyphens; without it some width hyphenates: {everHyphenates} / {firstPassHyphenates}");
            yield break;
        }

        [A_XSDActionDependency("Tex.FontspecLigatures", "Test")]
        private static IEnumerator<int> FontspecLigatures(TestContext t)
        {
            static string Text(string preamble)
            {
                XElement document = TexLowering.Compile("\\documentclass{article}" + preamble + "\\begin{document}fine -- office\\end{document}", out _);
                StringBuilder text = new StringBuilder();
                foreach (XElement run in document.Descendants("Run"))
                    text.Append((string?)run.Attribute("Text"));
                return text.ToString().Replace("­", "");
            }
            string modern = Text("");
            string times = Text("\\usepackage{fontspec}\\setmainfont{Times New Roman}");
            t.Check(modern.Contains("ﬁne") && modern.Contains("oﬃce") && times.Contains("fine") && times.Contains("office") && times.Contains("–"),
                $"f-ligatures in Latin Modern only, TeX's dashes in every font: {modern} / {times}");
            yield break;
        }

        #endregion

        #region ---- L11g, L11h: glyph widths in thousandths, kerning ----
        [A_XSDActionDependency("Tex.AdvanceGrid", "Test")]
        private static IEnumerator<int> AdvanceGrid(TestContext t)
        {
            XElement layout = TexLowering.Compile("\\documentclass{article}\\begin{document}x\\end{document}", out _).Element("DocumentLayout")!;
            t.Check((string?)layout.Attribute("AdvanceGrid") == "0", $"a TeX document keeps exact glyph widths: {layout.Attribute("AdvanceGrid")?.Value}");

            AtlasMetaData atlas = AssetRegistries.GetRegistryByValueType<string, FontAsset>(typeof(FontAsset))[TexLowering.TermesFont].atlasMetaData;
            float em = atlas.TableAdvance('a', 0);
            TextMeasurer.Run exact = new TextMeasurer.Run("a", 0, 1, TexLowering.TermesFont, atlas, 100f, FontStyle.Regular);
            TextMeasurer.Run gridded = new TextMeasurer.Run("a", 0, 1, TexLowering.TermesFont, atlas, 100f, FontStyle.Regular, grid: 1000);
            t.Check(Near(TextMeasurer.MeasureAdvance('a', exact), em * 100f, 0.0001f)
                && Near(TextMeasurer.MeasureAdvance('a', gridded), MathF.Floor(em * 1000f + 0.001f) / 10f, 0.0001f),
                $"the grid truncates, it does not round: {TextMeasurer.MeasureAdvance('a', gridded)} from {em * 100f}");
            yield break;
        }

        [A_XSDActionDependency("Font.KernPairs", "Test")]
        private static IEnumerator<int> KernPairs(TestContext t)
        {
            AtlasMetaData carlito = AssetRegistries.GetRegistryByValueType<string, FontAsset>(typeof(FontAsset))[TexLowering.CarlitoFont].atlasMetaData;
            t.Check(carlito.Kern('T', 'a', 0) < 0f && carlito.Kern('A', 'V', 0) < 0f
                && carlito.Kern('a', 'b', 0) == 0f && carlito.Kern(' ', 'A', 0) == 0f,
                $"the baked font carries its pairs, none with a space: Ta {carlito.Kern('T', 'a', 0)}, AV {carlito.Kern('A', 'V', 0)}");

            string times = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "times.ttf");
            if (!File.Exists(times)) yield break;
            char[] chars = "TaVuiv.1AWow".ToCharArray();
            List<uint> keys = new List<uint>(64);
            List<float> values = new List<float>(64);
            FontKerning.Read(times, 0, chars, keys, values);
            float Pair(char a, char b)
            {
                int k = keys.IndexOf((uint)a << 16 | b);
                return k < 0 ? 0f : TextMeasurer.Grid(values[k], 1000);
            }
            // LuaLaTeX's kerns in the thesis PDF, in em at 11.955 bp; Vu, Ti, 11 and Wi sit one thousandth nearer zero there
            (string pair, float em)[] reference = { ("Ta", -0.069f), ("Vu", -0.059f), ("Ti", -0.034f), ("11", -0.036f), ("v.", -0.064f), ("Wi", -0.039f), ("Av", -0.074f) };
            StringBuilder wrong = new StringBuilder();
            foreach ((string pair, float em) in reference)
                if (!Near(Pair(pair[0], pair[1]), em, 0.0011f)) wrong.Append($"{pair} {Pair(pair[0], pair[1])} (want {em}); ");
            t.Check(wrong.Length == 0, $"Times New Roman's pairs match the kerns LuaLaTeX set to a thousandth of an em: {wrong}");
        }

        [A_XSDActionDependency("Font.OldAtlasLoads", "Test")]
        private static IEnumerator<int> OldAtlasLoads(TestContext t)
        {
            AtlasMetaData baked = new AtlasMetaData { glyphCount = 1, chars = new[] { 'a' }, glyphs = new[] { new Glyph() }, pxRange = 4f, cellSize = 32 };
            string path = Path.Combine(Path.GetTempPath(), "aurora-old-atlas.agd");
            Serializer.SerializeAttributed(baked, path);
            byte[] bytes = File.ReadAllBytes(path);
            File.WriteAllBytes(path, bytes[..^(3 * sizeof(int))]);
            AtlasMetaData read = new AtlasMetaData();
            Serializer.DeserializeAttributed(path, ref read);
            File.Delete(path);
            read.BuildCharIndex();
            t.Check(read.glyphCount == 1 && read.cellSize == 32 && read.kernKeys.Length == 0 && read.Kern('a', 'a', 0) == 0f,
                $"an .agd baked before kerning loads with no pairs: {read.glyphCount} glyphs, {read.kernKeys.Length} pairs");
            yield break;
        }

        [A_XSDActionDependency("Text.KernLayout", "Test")]
        private static IEnumerator<int> KernLayout(TestContext t)
        {
            const string word = "AVAWAYTa";
            TextRunControl kerned = new TextRunControl { text = word, fontName = TexLowering.CarlitoFont, fontSize = 40,
                horizontalAlignment = HorizontalAlignment.Left, verticalAlignment = VerticalAlignment.Top };
            t.Show(kerned);
            yield return 2;

            AtlasMetaData atlas = AssetRegistries.GetRegistryByValueType<string, FontAsset>(typeof(FontAsset))[TexLowering.CarlitoFont].atlasMetaData;
            TextMeasurer.Run run = new TextMeasurer.Run(word, 0, word.Length, TexLowering.CarlitoFont, atlas, 40f, FontStyle.Regular);
            float plain = 0f, expected = 0f;
            for (int i = 0; i < word.Length; i++)
            {
                plain += TextMeasurer.MeasureAdvance(word[i], run);
                expected += TextMeasurer.AdvanceAt(run, i, expected);
            }
            float end = kerned.CaretAt(word.Length).x;
            t.Check(Near(end, expected, 0.01f) && end < plain - 5f, $"the caret ends where the kerned pen does, short of the unkerned width: {end} / {expected} / {plain}");

            bool hits = true;
            for (int k = 0; k < word.Length; k++)
            {
                CaretGeometry caret = kerned.CaretAt(k);
                hits &= kerned.IndexAt(new Vector2(kerned.arrangedRect.x + caret.x + 1f, kerned.arrangedRect.y + caret.top + 2f)) == k;
            }
            t.Check(hits, "a click just right of each caret position lands on that character");
        }
        #endregion

        #region ---- compiler errors: preamble, packages, @, text-mode math ----
        [A_XSDActionDependency("Tex.PreambleRegisters", "Test")]
        private static IEnumerator<int> PreambleRegisters(TestContext t)
        {
            string lines = Lines("\\documentclass{article}\\hbadness 1414\n\\hfuzz 0.3 pt\n\\widowpenalty=10000\n\\vfuzz \\hfuzz\n"
                + "\\addto\\captionenglish{\\renewcommand{\\proofname}{X}}\\setcounter{page}{2}\n"
                + "\\renewcommand*\\descriptionlabel[1]{\\hspace\\labelsep\\normalfont\\bfseries #1.}\\begin{document}x\\end{document}", out List<TexError> errors);
            t.Check(errors.Count == 0 && lines == "x", $"the thesis class's registers, \\addto, the page counter and \\descriptionlabel read clean: {Messages(errors)} / {lines}");

            lines = Lines(@"\begin{description}\item[Term] text\end{description}\renewcommand\descriptionlabel[1]{#1:}\begin{description}\item[B] c\end{description}");
            t.Check(lines == "Term text / B: c", $"a description label goes through \\descriptionlabel: {lines}");
            yield break;
        }

        [A_XSDActionDependency("Tex.Amsthm", "Test")]
        private static IEnumerator<int> Amsthm(TestContext t)
        {
            TexTypesetter typesetter = Typeset("\\documentclass{article}\\usepackage{amsthm}\\newtheorem{theorem}{Teorema}[section]\\newtheorem{lemma}[theorem]{Lema}\n"
                + "\\newtheoremstyle{custom}{}{}{}{\\parindent}{\\bfseries}{. }{}{}\\swapnumbers\\theoremstyle{definition}\\newtheorem*{remark}{Pastaba}\n"
                + "\\begin{document}\\section{A}\n\\begin{theorem}[note]\nBody\n\\end{theorem}\n\\begin{lemma}More\\end{lemma}\n\\begin{remark}N\\end{remark}\n"
                + "\\begin{proof}P\\end{proof}\\end{document}", out List<TexError> errors);
            List<TexParagraph> pars = Pars(typesetter);
            string[] texts = new string[pars.Count];
            for (int i = 0; i < pars.Count; i++) texts[i] = Line(pars[i]);
            string all = string.Join(" / ", texts);
            t.Check(errors.Count == 0 && all == "1 A / Teorema 1.1 (note). Body / Lema 1.2. More / Pastaba. N / Proof. P $\\square$",
                $"numbered within the section, a shared counter, a note, unnumbered, proof: {Messages(errors)} / {all}");

            bool bodyItalic = false, remarkUpright = false;
            foreach (TexParagraph p in pars)
                foreach (TexNode node in p.list)
                {
                    if (node is TexChar { ch: 'B' } b) bodyItalic = b.style.font.italic;
                    if (node is TexChar { ch: 'N' } n) remarkUpright = !n.style.font.italic;
                }
            t.Check(bodyItalic && remarkUpright, "plain theorems set their body italic, definition-style ones upright");
            yield break;
        }

        [A_XSDActionDependency("Tex.Appendix", "Test")]
        private static IEnumerator<int> Appendix(TestContext t)
        {
            string lines = Lines(@"\section{A}\section{B}\appendix\section{C}\section{D}", out List<TexError> errors);
            t.Check(errors.Count == 0 && lines == "1 A / 2 B / A C / B D", $"\\appendix restarts sections at A: {Messages(errors)} / {lines}");

            lines = Lines(@"\documentclass{report}\begin{document}\chapter{C}\appendix\chapter{D}\section{S}\end{document}", out errors);
            t.Check(errors.Count == 0 && lines == "Chapter 1 / C / Chapter A / D / A.1 S", $"in a report it restarts chapters: {Messages(errors)} / {lines}");
            yield break;
        }

        [A_XSDActionDependency("Tex.AtCatcode", "Test")]
        private static IEnumerator<int> AtCatcode(TestContext t)
        {
            string folder = Path.Combine(Path.GetTempPath(), "aurora-tex-at");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "AtTest.cls"),
                "\\LoadClass{article}\\makeatother\\RequirePackage{amsthm}\\newtheorem{theorem}{T}\\edef\\atcode{\\the\\catcode`\\@}\n");
            File.WriteAllText(Path.Combine(folder, "atpkg.sty"), "\\def\\pkg@x{}\n");
            TexTypesetter typesetter = new TexTypesetter("\\documentclass{AtTest}\\makeatletter\\usepackage{atpkg}\\edef\\inside{\\the\\catcode`\\@}\\makeatother\n"
                + "\\begin{document}\\atcode,\\inside, a@b\\end{document}", null, folder);
            typesetter.Run();
            Directory.Delete(folder, true);
            List<TexParagraph> pars = Pars(typesetter);
            string line = pars.Count == 1 ? Line(pars[0]) : "";
            t.Check(typesetter.errors.Count == 0 && line == "12,11, a@b",
                $"a package reads @ as a letter and puts back the catcode it found, in a class after \\makeatother and inside \\makeatletter: {Messages(typesetter.errors)} / {line}");
            yield break;
        }

        [A_XSDActionDependency("Tex.TextModeMath", "Test")]
        private static IEnumerator<int> TextModeMath(TestContext t)
        {
            string lines = MathLines(@"a \cdot b c", out List<TexError> errors);
            t.Check(errors.Count == 1 && Has(errors, "Missing $ inserted") && lines == "a $\\cdot$b c", $"a math command in text is set alone as math: {Messages(errors)} / {lines}");

            lines = MathLines(@"{x \omega y} z", out errors);
            t.Check(errors.Count == 1 && lines == "x $\\omega$y z", $"a math command in a group closes its math before the group ends: {Messages(errors)} / {lines}");

            lines = MathLines("a^2 b\n\nc", out errors);
            t.Check(errors.Count == 2 && lines == "a$^2 b$ / c", $"a superscript in text starts math up to the paragraph's end: {Messages(errors)} / {lines}");

            lines = MathLines(@"$\frac{a}{b}$ \nope", out errors);
            t.Check(errors.Count == 1 && Has(errors, @"Undefined control sequence \nope"), $"other undefined commands stay undefined: {Messages(errors)}");
            yield break;
        }

        [A_XSDActionDependency("Tex.AlgorithmFloat", "Test")]
        private static IEnumerator<int> AlgorithmFloat(TestContext t)
        {
            TexFloat? ruled = FirstFloat(Typeset(@"\documentclass{article}\usepackage{algorithm}\begin{document}\listofalgorithms\begin{algorithm}[H]body\caption{Sort}\end{algorithm}\end{document}", out List<TexError> errors));
            string shape = ruled == null ? "none" : Shape(ruled.vlist);
            t.Check(errors.Count == 0 && ruled != null && ruled.kind == "algorithm" && ruled.placement == "H" && shape == "rule,skip,Algorithm 1 Sort,skip,rule,skip,body,skip,rule",
                $"ruled by default: the caption on top between rules, a rule closing it: {Messages(errors)} / {shape}");

            ruled = FirstFloat(Typeset("\\documentclass{article}\\usepackage{algorithm}\\makeatletter\\renewcommand{\\fnum@algorithm}{\\thealgorithm}\\makeatother\n"
                + "\\renewcommand\\thealgorithm{\\arabic{algorithm} algoritmas}\\begin{document}\\begin{algorithm}\\caption{Sort}x\\end{algorithm}\\end{document}", out errors));
            shape = ruled == null ? "none" : Shape(ruled.vlist);
            t.Check(errors.Count == 0 && shape.Contains(",1 algoritmas Sort,"), $"the thesis's \\fnum@algorithm and \\thealgorithm: {Messages(errors)} / {shape}");

            TexFloat? plain = FirstFloat(Typeset(@"\documentclass{article}\usepackage[plain,section]{algorithm}\floatname{algorithm}{Algoritmas}\begin{document}\section{S}\begin{algorithm}\caption{Sort}body\end{algorithm}\end{document}", out errors));
            shape = plain == null ? "none" : Shape(plain.vlist);
            t.Check(errors.Count == 0 && shape == "body,Algoritmas 1.1: Sort", $"plain puts the caption last, numbered within the section, \\floatname renames it: {Messages(errors)} / {shape}");

            Typeset(@"\begin{algorithm}x\end{algorithm}", out errors);
            t.Check(Has(errors, "Environment algorithm undefined"), $"no algorithm without the package: {Messages(errors)}");
            yield break;
        }

        // A typesetter with the math-only names of the notes' math, as TexLowering.Compile sets it.
        private static string MathLines(string source, out List<TexError> errors)
        {
            TexTypesetter typesetter = new TexTypesetter(source);
            typesetter.expander.mathOnly = static name => MathSymbols.commands.ContainsKey(name);
            typesetter.Run();
            errors = typesetter.errors;
            List<TexParagraph> pars = Pars(typesetter);
            string[] texts = new string[pars.Count];
            for (int i = 0; i < pars.Count; i++) texts[i] = Line(pars[i]);
            return string.Join(" / ", texts);
        }

        private static TexFloat? FirstFloat(TexTypesetter typesetter)
        {
            foreach (TexNode node in typesetter.vlist)
            {
                if (node is TexFloat f) return f;
                if (node is not TexParagraph p) continue;
                foreach (TexNode inner in p.list)
                    if (inner is TexFloat g) return g;
            }
            return null;
        }

        // A float's vertical list as rule, skip or a paragraph's text.
        private static string Shape(List<TexNode> vlist)
        {
            string[] parts = new string[vlist.Count];
            for (int i = 0; i < vlist.Count; i++)
                parts[i] = vlist[i] switch { TexRuleNode => "rule", TexVGlue => "skip", TexParagraph p => Line(p), _ => vlist[i].GetType().Name };
            return string.Join(",", parts);
        }

        private static string Messages(List<TexError> errors)
        {
            string[] messages = new string[errors.Count];
            for (int i = 0; i < errors.Count; i++) messages[i] = errors[i].message;
            return string.Join("; ", messages);
        }
        #endregion

        #region ---- helpers ----
        private static string Escape(string text)
        {
            StringBuilder escaped = new StringBuilder(text.Length);
            foreach (char c in text)
                escaped.Append(c >= TextMeasurer.FirstFactorSpace && c < TextMeasurer.FirstFactorSpace + 4 ? $"[{c - TextMeasurer.FirstFactorSpace}]" : c.ToString());
            return escaped.ToString();
        }
        #endregion

        #region ---- typesetting ----
        private static TexTypesetter Typeset(string source, out List<TexError> errors)
        {
            TexTypesetter typesetter = new TexTypesetter(source);
            typesetter.Run();
            errors = typesetter.errors;
            return typesetter;
        }

        // The main list's paragraphs, a float's where it stands and after the paragraph it is in.
        private static List<TexParagraph> Pars(TexTypesetter typesetter) => Pars(typesetter.vlist).ToList();

        private static IEnumerable<TexParagraph> Pars(List<TexNode> vlist)
        {
            foreach (TexNode node in vlist)
                if (node is TexFloat f)
                    foreach (TexParagraph inner in Pars(f.vlist))
                        yield return inner;
                else if (node is TexParagraph p)
                {
                    yield return p;
                    foreach (TexFloat g in p.list.OfType<TexFloat>())
                        foreach (TexParagraph inner in Pars(g.vlist))
                            yield return inner;
                }
        }

        // A paragraph as text: glue a space, math in $ or $$, a forced break |.
        private static string Line(TexParagraph p) => string.Concat(p.list.Select(n => n switch
        {
            TexChar c => c.ch.ToString(),
            TexGlueNode => " ",
            TexMathNode { display: true } m => "$$" + m.source + "$$",
            TexMathNode m => "$" + m.source + "$",
            TexPenalty { penalty: TexPenalty.Forced } => "|",
            TexRefNode r => r.text,
            _ => ""
        }));

        private static string Lines(string source, out List<TexError> errors) =>
            string.Join(" / ", Pars(Typeset(source, out errors)).Select(Line));

        private static string Lines(string source) => Lines(source, out _);

        private static List<TexChar> Chars(string source) => Pars(Typeset(source, out _)).SelectMany(p => p.list).OfType<TexChar>().ToList();

        [A_XSDActionDependency("Tex.Typeset.Paragraphs", "Test")]
        private static IEnumerator<int> TypesetParagraphs(TestContext t)
        {
            string two = Lines("Hello   world.\n\nSecond  para\nline.", out List<TexError> errors);
            t.Check(two == "Hello world. / Second para line." && errors.Count == 0, $"a blank line ends a paragraph and spaces collapse: {two}");
            t.Check(Lines("a~b") == "a b", "~ is an interword space");
            t.Check(Pars(Typeset("a~b", out _))[0].list.Exists(n => n is TexPenalty { penalty: 10000 }), "that no line breaks at");
            t.Check(Lines(@"a \par") == "a", "a paragraph's last space is dropped");
            t.Check(Lines(@"a\\ b") == "a|b", "\\\\ is a forced break");
            t.Check(Lines(@"\begin{document}a\end{document}b") == "a", "nothing after \\end{document} is read");

            Lines("\\documentclass{article}\nx", out errors);
            t.Check(Has(errors, @"Missing \begin{document}"), "text in the preamble is an error");
            yield break;
        }

        [A_XSDActionDependency("Tex.Typeset.Fonts", "Test")]
        private static IEnumerator<int> TypesetFonts(TestContext t)
        {
            List<TexChar> abc = Chars(@"a{\bfseries b}c");
            t.Check(!abc[0].style.font.bold && abc[1].style.font.bold && !abc[2].style.font.bold, "a font change ends with its group");
            t.Check(Chars(@"\textit{x}")[0].style.font.italic && !Chars(@"\emph{\emph{y}}")[0].style.font.italic, "\\emph inside \\emph is upright");
            t.Check(Chars(@"\texttt{z}")[0].style.font.family == TexFamily.Mono, "\\texttt is the mono family");
            t.Check(Chars(@"\Large w")[0].style.font.size == (int)Math.Round(14.4 * 65536), "\\Large is 14.4pt at 10pt");
            t.Check(Chars(@"\documentclass[12pt]{article}\begin{document}n\end{document}")[0].style.font.size == 12 * 65536, "the 12pt option");
            t.Check(Chars(@"\textcolor{red}{r}")[0].style.color == "#FF0000" && Chars(@"\color{blue!50}s")[0].style.color == "#8080FF",
                "xcolor names and tints");
            t.Check(Chars(@"\underline{u}")[0].style.underline, "\\underline");
            yield break;
        }

        [A_XSDActionDependency("Tex.Typeset.Sections", "Test")]
        private static IEnumerator<int> TypesetSections(TestContext t)
        {
            List<TexParagraph> heads = Pars(Typeset(@"\section{Intro}\subsection{Sub}\section*{Star}\section{Two}\subsection{S}", out _));
            string[] texts = heads.Select(Line).ToArray();
            t.Check(texts.SequenceEqual(new[] { "1 Intro", "1.1 Sub", "Star", "2 Two", "2.1 S" }), $"numbering, starred and reset: {string.Join(" / ", texts)}");
            t.Check(heads.Select(h => h.style.level).SequenceEqual(new[] { 1, 2, 1, 1, 2 }) && heads.All(h => h.style.kind == TexParKind.Heading),
                "sections are heading levels 1 and 2");
            TexChar first = heads[0].list.OfType<TexChar>().First();
            t.Check(first.style.font.bold && first.style.font.size == (int)Math.Round(14.4 * 65536), "a section is bold \\Large");

            List<TexParagraph> report = Pars(Typeset(@"\documentclass{report}\begin{document}\chapter{C}\section{S}\end{document}", out _));
            t.Check(report.Select(Line).SequenceEqual(new[] { "Chapter 1", "C", "1.1 S" }) && report[1].style.level == 1 && report[2].style.level == 2,
                "a report's chapters are level 1 and its sections number within them");

            t.Check(Lines(@"\paragraph{P} text") == "P text", "\\paragraph runs into its text");
            yield break;
        }

        [A_XSDActionDependency("Tex.Typeset.Lists", "Test")]
        private static IEnumerator<int> TypesetLists(TestContext t)
        {
            List<TexParagraph> items = Pars(Typeset(@"\begin{itemize}\item A\item B\begin{enumerate}\item C\item D\end{enumerate}\end{itemize}", out _));
            t.Check(items.Select(Line).SequenceEqual(new[] { "A", "B", "C", "D" }), "one paragraph per item");
            t.Check(items[0].style is { list: TexListKind.Itemize, listDepth: 1, item: true }, "an itemize item");
            t.Check(items[3].style is { list: TexListKind.Enumerate, listDepth: 2, listKindDepth: 1, itemNumber: 2, item: true },
                "a nested enumerate item knows its depth and number");

            List<TexParagraph> described = Pars(Typeset(@"\begin{description}\item[x] Y\end{description}", out _));
            t.Check(Line(described[0]) == "x Y" && described[0].list.OfType<TexChar>().First().style.font.bold, "a description label is bold");

            Lines(@"\item z", out List<TexError> errors);
            t.Check(Has(errors, "Lonely \\item"), "an \\item outside a list");
            yield break;
        }

        [A_XSDActionDependency("Tex.Typeset.Math", "Test")]
        private static IEnumerator<int> TypesetMath(TestContext t)
        {
            string math = Lines(@"$x^2$ $$a+b$$ \[ \frac{a}{b} \] \(y\)", out List<TexError> errors);
            t.Check(math == @"$x^2$ $$a+b$$$$\frac {a}{b}$$$y$" && errors.Count == 0, $"inline and display forms: {math}");
            string macro = Lines(@"\newcommand\R{\mathbb{R}}$x\in\R$", out errors);
            t.Check(macro == @"$x\in \mathbb {R}$" && errors.Count == 0, $"macros expand inside math: {macro}");
            t.Check(Lines(@"\begin{equation}E=mc^2\end{equation}") == @"$$E=mc^2\tag{1}$$", "an equation is numbered display math");

            string open = Lines("$a\n\nb", out errors);
            t.Check(open == "$a$ / b" && Has(errors, "Missing $ inserted"), $"a paragraph ends unclosed math: {open}");
            yield break;
        }

        [A_XSDActionDependency("Tex.Typeset.Ligatures", "Test")]
        private static IEnumerator<int> TypesetLigatures(TestContext t)
        {
            t.Check(Lines("``Hi'' `a'") == "“Hi” ‘a’", "quotes");
            t.Check(Lines("a--b---c") == "a–b—c", "dashes");
            t.Check(Lines("office fly f{}i") == "oﬃce ﬂy fi", "f-ligatures, broken by braces");
            t.Check(Lines(@"\texttt{--``}") == "--``", "no ligatures in mono");
            yield break;
        }

        [A_XSDActionDependency("Tex.Typeset.Accents", "Test")]
        private static IEnumerator<int> TypesetAccents(TestContext t)
        {
            string accented = Lines(@"\'e\""o\^{a}\c c\v{s}\'{\i}");
            t.Check(accented == "éöâçší", $"accents compose: {accented}");
            t.Check(Lines(@"\ss\ae\o\'{}") == "ßæø´", "letters and an accent over nothing");
            t.Check(Lines(@"\%\&\$\#\_ \ldots") == "%&$#_ …", "escaped characters");
            yield break;
        }

        [A_XSDActionDependency("Tex.Typeset.Verbatim", "Test")]
        private static IEnumerator<int> TypesetVerbatim(TestContext t)
        {
            List<TexParagraph> pars = Pars(Typeset("\\begin{verbatim}\n  x = {a}%\n\\end{verbatim}\nafter \\verb|\\foo{}| end", out List<TexError> errors));
            t.Check(pars.Count == 2 && pars[0].style.kind == TexParKind.Code && Line(pars[0]) == "  x = {a}%", "verbatim lines are code, read raw");
            t.Check(Line(pars[1]) == @"after \foo{} end" && errors.Count == 0, $"\\verb reads raw: {Line(pars[1])}");
            yield break;
        }

        [A_XSDActionDependency("Tex.Typeset.Errors", "Test")]
        private static IEnumerator<int> TypesetErrors(TestContext t)
        {
            Lines(@"\section{a", out List<TexError> errors);
            t.Check(Has(errors, @"File ended while scanning use of \section"), "an unclosed argument");
            Lines("a & b", out errors);
            t.Check(Has(errors, "Misplaced alignment tab"), "& outside a table");
            Lines("x^2", out errors);
            t.Check(Has(errors, "Missing $ inserted"), "a superscript outside math");
            Lines(@"\textbf{\verb|x|}", out errors);
            t.Check(Has(errors, @"\verb illegal in command argument"), "\\verb in an argument");
            Lines(@"\color{nocolor}x", out errors);
            t.Check(Has(errors, "Undefined color"), "an unknown colour");
            Lines(@"\begin{itemize}\item a\end{enumerate}", out errors);
            t.Check(Has(errors, @"\begin{itemize} on input line 1 ended by \end{enumerate}"), "a mismatched list end");
            Lines(@"\begin{document}\usepackage{x}", out errors);
            t.Check(Has(errors, "Can be used only in preamble"), "\\usepackage in the body");
            yield break;
        }

        [A_XSDActionDependency("Tex.Lowering", "Test")]
        private static IEnumerator<int> Lowering(TestContext t)
        {
            XElement document = TexLowering.Compile(
                "\\documentclass{article}\n\\begin{document}\n\\section{Intro}\nSome \\textbf{bold} text $x^2$.\n\\begin{itemize}\n\\item One\n\\end{itemize}\n\\end{document}\n",
                out List<TexError> errors);
            t.Check(errors.Count == 0, $"compiles clean: {string.Join("; ", errors.Select(e => e.message))}");

            XElement layout = document.Element("DocumentLayout")!;
            XElement page = layout.Element("Page")!;
            t.Check((string?)page.Attribute("Size") == "Letter" && (string?)page.Attribute("Mode") == "Paged", "letter paper, paged");
            t.Check(Math.Abs((float)page.Attribute("MarginLeft")! - 47.32f) < 0.01f && Math.Abs((float)page.Attribute("MarginTop")! - 43.05f) < 0.01f,
                "article's margins");
            string Style(string type) => string.Join(",", layout.Elements("TextStyle").Where(s => (string?)s.Attribute("Type") == type)
                .Select(s => $"{s.Attribute("FontSize")?.Value} {s.Attribute("FontName")?.Value}"));
            t.Check(Style("Text") == "13.284 latin-modern" && Style("Heading1") == "19.128 latin-modern" && Style("Code") == "13.284 latin-modern-mono",
                "text, heading and code styles in Latin Modern");

            XElement[] expected =
            {
                new XElement("Block", new XAttribute("StylingType", "Heading1"), new XAttribute("KeepWithNext", "true"), new XAttribute("LineHeight", "1.25"), new XAttribute("SpaceBefore", "20.038"),
                    new XAttribute("ShrinkBefore", "1.145"),
                    new XElement("Run", new XAttribute("Text", "1"), new XAttribute("Bold", "true")),
                    new XElement("Run", new XAttribute("Text", "\u00A0"), new XAttribute("Bold", "true"), new XAttribute("Space", "19.128")),
                    new XElement("Run", new XAttribute("Text", "In\u00ADtro"), new XAttribute("Bold", "true"))),
                new XElement("Block", new XAttribute("Align", "Justify"), new XAttribute("SpaceBefore", "13.168"),
                    new XElement("Run", new XAttribute("Text", "Some ")),
                    new XElement("Run", new XAttribute("Text", "bold"), new XAttribute("Bold", "true")),
                    new XElement("Run", new XAttribute("Text", " text ")),
                    new XElement("Run", new XAttribute("Math", "x^2"), new XAttribute("FontName", "latin-modern-math")),
                    new XElement("Run", new XAttribute("Text", "."))),
                new XElement("Block", new XAttribute("Align", "Justify"), new XAttribute("List", "Bullet"), new XAttribute("Level", 0),
                    new XAttribute("Marker", "Disc"), new XAttribute("SpaceBefore", "10.627"), new XAttribute("ShrinkBefore", "5.313"), new XElement("Run", new XAttribute("Text", "One")))
            };
            string body = string.Concat(document.Elements("Block").Select(b => b.ToString(SaveOptions.DisableFormatting)));
            string want = string.Concat(expected.Select(b => b.ToString(SaveOptions.DisableFormatting)));
            t.Check(body == want, $"blocks and runs: {body}");
            RichTextDocument parsed = DocumentXml.Parse(document);
            t.Check(parsed.blocks.Length == 3, "the tree parses as a note");
            yield break;
        }

        [A_XSDActionDependency("Tex.Lowering.Lines", "Test")]
        private static IEnumerator<int> LoweringLines(TestContext t)
        {
            List<int> lines = new List<int>();
            XElement document = TexLowering.Compile(
                "\\documentclass{article}\n\\begin{document}\n\\section{Intro}\nFirst line\\\\\nsecond half.\n\n\\hrule\nNote.\\footnote{Text.}\n\\begin{verbatim}\na\nb\n\\end{verbatim}\n\\end{document}\n",
                out List<TexError> errors, lines);
            t.Check(errors.Count == 0, $"compiles clean: {string.Join("; ", errors.Select(e => e.message))}");
            t.Check(lines.Count == document.Elements("Block").Count() + document.Elements("Footnote").Elements("Block").Count(), $"one line per block: {lines.Count}");
            t.Check(string.Join(",", lines) == "3,4,4,0,8,8,10,11", $"heading, both halves of \\\\, rule, note, its footnote, verbatim lines: {string.Join(",", lines)}");
            yield break;
        }
        #endregion

        #region ---- tables, floats, references ----
        private static string CellText(TexTableCell cell) => string.Join(" / ", cell.vlist.OfType<TexParagraph>().Select(Line));

        private static string Folder(params (string name, string text)[] files)
        {
            string folder = Path.Combine(Path.GetTempPath(), $"aurora-tex-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            foreach ((string name, string text) in files)
            {
                string path = Path.Combine(folder, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text);
            }
            return folder;
        }

        [A_XSDActionDependency("Tex.Typeset.Tabular", "Test")]
        private static IEnumerator<int> TypesetTabular(TestContext t)
        {
            TexTable table = Typeset(@"\begin{tabular}{|l c|r|} \hline a & {\bf b} \bf & c \\ \hline \multicolumn{2}{c}{wide} & d \\ e & f \end{tabular}",
                out List<TexError> errors).vlist.OfType<TexTable>().Single();
            t.Check(errors.Count == 0, $"typesets clean: {string.Join("; ", errors.Select(e => e.message))}");
            t.Check(table.columns.Select(c => c.align).SequenceEqual(new[] { TexAlign.Left, TexAlign.Center, TexAlign.Right }),
                "l c r");
            t.Check(string.Join(",", table.vrules) == "1,0,1,1", $"| counted per column boundary: {string.Join(",", table.vrules)}");
            t.Check(string.Join(",", table.rules.Select(r => $"{r.kind}@{r.row}:{r.from}-{r.to}")) == "Plain@0:0-2,Plain@1:0-2",
                $"\\hline above the first and second rows: {string.Join(",", table.rules.Select(r => $"{r.kind}@{r.row}:{r.from}-{r.to}"))}");
            string rows = string.Join(" | ", table.rows.Select(r => string.Join(",", r.Select(c => CellText(c) + (c.span > 1 ? "*" + c.span : "")))));
            t.Check(rows == "a,b,c | wide*2,d | e,f", $"rows and cells: {rows}");
            t.Check(table.rows[1][0].vlist.OfType<TexParagraph>().Single().style.align == TexAlign.Center, "a multicolumn takes its own alignment");
            t.Check(table.rows[2][1].vlist.OfType<TexParagraph>().Single().style.align == TexAlign.Center, "a cell takes its column's alignment");
            t.Check(table.rows[0][1].vlist.OfType<TexParagraph>().Single().list.OfType<TexChar>().Single().style.font.bold
                && !table.rows[0][2].vlist.OfType<TexParagraph>().Single().list.OfType<TexChar>().Single().style.font.bold,
                "a cell's \\bf ends at the next &");
            XElement styled = TexLowering.Compile("\\begin{tabular}{ll} {\\textbf{Aa}} & bb\\\\ \\textbf{Cc} & dd\\\\ \\end{tabular}\n\nAfter", out _);
            t.Check((string?)FindRun(styled, "Aa")?.Attribute("Bold") == "true" && (string?)FindRun(styled, "Cc")?.Attribute("Bold") == "true",
                "a row's first cell starts before its \\textbf, so the cell is bold");
            t.Check(FindRun(styled, "bb")?.Attribute("Bold") == null && FindRun(styled, "dd")?.Attribute("Bold") == null && FindRun(styled, "After")?.Attribute("Bold") == null,
                "and its bold stays in the cell, not after the table");
            int rowCount = 0;
            foreach (XElement row in styled.Descendants("Row")) rowCount++;
            t.Check(rowCount == 2, $"a trailing \\\\ before \\end{{tabular}} adds no row: {rowCount}");

            TexTable plain = Typeset(@"\begin{tabular}{ll}a&b\end{tabular}", out _).vlist.OfType<TexTable>().Single();
            t.Check(plain.rules.Count == 0 && plain.vrules.All(n => n == 0), "no | and no rule has no rules");
            TexTable booktabs = Typeset(@"\begin{tabular}{lll}\toprule[2pt] a&b&c\\\cmidrule(lr){1-2}\cmidrule(l){3-3} c&d&e\\\cline{2-3}\hline\hline\bottomrule\end{tabular}",
                out errors).vlist.OfType<TexTable>().Single();
            string ruleList = string.Join(",", booktabs.rules.Select(r => $"{r.kind}@{r.row}:{r.from}-{r.to}{(r.trimLeft ? "l" : "")}{(r.trimRight ? "r" : "")}"));
            t.Check(booktabs.rows.Count == 2 && errors.Count == 0
                && ruleList == "Heavy@0:0-2,Cmid@1:0-1lr,Cmid@1:2-2l,Plain@2:1-2,Plain@2:0-2,Plain@2:0-2,Heavy@2:0-2",
                $"booktabs, \\cline and trims at their boundaries: {ruleList}; {string.Join("; ", errors.Select(e => e.message))}");
            t.Check(Math.Abs(booktabs.rules[0].width - 2 * 65536) < 2 && booktabs.rules[1].width == 0, "\\toprule[2pt] carries its width");

            TexTable spec = Typeset(@"\begin{tabular}{@{}*{3}{c}p{2cm}@{}}x\end{tabular}", out errors).vlist.OfType<TexTable>().Single();
            int twoCm = (int)(2f / 2.54f * 72.27f * 65536f);
            t.Check(spec.columns.Count == 4 && spec.columns[3].align == TexAlign.Justify && Math.Abs(spec.columns[3].width - twoCm) < 100,
                $"*{{3}}{{c}} and p{{2cm}}: {spec.columns.Count} columns, {spec.columns.LastOrDefault().width}sp");

            Typeset(@"\begin{tabular}{l}a&b\end{tabular}", out errors);
            t.Check(Has(errors, "Extra alignment tab"), "more cells than columns");
            Typeset(@"a & b", out errors);
            t.Check(Has(errors, "Misplaced alignment tab"), "& outside a tabular");

            TexTable outer = Typeset(@"\begin{tabular}{l}\begin{tabular}{ll}x&y\end{tabular}\end{tabular} after", out errors).vlist.OfType<TexTable>().Single();
            t.Check(outer.rows[0][0].vlist.OfType<TexTable>().Count() == 1 && errors.Count == 0, "a tabular in a cell stays in the cell");
            yield break;
        }

        [A_XSDActionDependency("Tex.Typeset.Floats", "Test")]
        private static IEnumerator<int> TypesetFloats(TestContext t)
        {
            TexTypesetter floats = Typeset(@"\begin{figure}[h]\centering x\caption{A cat}\end{figure}\begin{table}\caption{Data}\end{table}" +
                @"\renewcommand\figurename{Fig.}\begin{figure*}\caption{Dog}\end{figure*}", out List<TexError> errors);
            string text = string.Join(" / ", Pars(floats).Select(Line));
            t.Check(text == "x / Figure 1: A cat / Table 1: Data / Fig. 2: Dog" && errors.Count == 0, $"captions numbered per kind: {text}");
            t.Check(Pars(floats).Skip(1).All(p => p.style.align == TexAlign.Center), "captions are centred");

            string report = Lines(@"\documentclass{report}\begin{document}\chapter{One}\begin{figure}\caption{A}\end{figure}\end{document}");
            t.Check(report.EndsWith("Figure 1.1: A"), $"report numbers within the chapter: {report}");

            Typeset(@"\caption{Lost}", out errors);
            t.Check(Has(errors, "\\caption outside float"), "a caption outside a float");

            List<TexFloat> set = floats.vlist.OfType<TexFloat>().ToList();
            t.Check(set.Select(f => f.kind + ":" + f.placement).SequenceEqual(new[] { "figure:ht", "table:tbp", "figure:tbp" }),
                $"each float keeps its kind and placement, a lone h becoming ht: {string.Join(", ", set.Select(f => f.kind + ":" + f.placement))}");
            t.Check(!floats.vlist.OfType<TexVGlue>().Any(), "a float leaves no glue in the text");
            t.Check(Typeset(@"\begin{figure}[!b]x\end{figure}\begin{table}[H]x\end{table}\begin{figure}[pxq]x\end{figure}", out _).vlist.OfType<TexFloat>()
                .Select(f => f.placement).SequenceEqual(new[] { "!b", "H", "p" }), "! and H are kept, unknown letters dropped");

            TexTypesetter inside = Typeset(@"Before this \begin{figure}\caption{Mid}\end{figure} and after.", out errors);
            TexParagraph whole = inside.vlist.OfType<TexParagraph>().Single();
            t.Check(Line(whole) == "Before this and after." && whole.list.OfType<TexFloat>().Count() == 1 && errors.Count == 0,
                $"a float inside a paragraph leaves the paragraph whole: {Line(whole)}");

            Typeset(@"x\footnote{n \begin{figure}y\end{figure}}", out errors);
            t.Check(Has(errors, "Not in outer par mode"), "a float inside a footnote is an error");
            yield break;
        }

        [A_XSDActionDependency("Tex.Refs", "Test")]
        private static IEnumerator<int> Refs(TestContext t)
        {
            string source = "\\section{Intro}\\label{s:intro} See \\ref{s:b} and \\ref{s:intro}.\n" +
                "\\section{Back}\\label{s:b}\n" +
                "\\begin{enumerate}\\item a\\begin{enumerate}\\item q\\item b\\label{i:b}\\end{enumerate}\\end{enumerate}\n" +
                "Item \\ref{i:b}, note\\footnote{n\\label{fn}} \\ref{fn}, figure \\ref{fig}.\n" +
                "\\begin{figure}\\caption{c\\label{fig}}\\end{figure}\n" +
                "Missing \\ref{nope}.\n" +
                "$x = \\ref{s:b}$\\begin{equation}a\\label{eq}\\end{equation}\n";
            TexTypesetter refs = Typeset(source, out List<TexError> errors);
            string text = string.Join(" / ", Pars(refs).Select(Line));
            t.Check(text.Contains("See 2 and 1."), $"a forward and a backward \\ref: {text}");
            t.Check(text.Contains("Item 1b, note") && text.Contains(" 1, ﬁgure 1."),$"an item, footnote and figure label: {text}");
            t.Check(text.Contains("Missing ??."), "an unknown label prints ??");
            t.Check(errors.Count == 1 && Has(errors, "Reference `nope' undefined", 6), $"one warning, at its line: {string.Join("; ", errors)}");
            List<TexMathNode> math = Pars(refs).SelectMany(p => p.list).OfType<TexMathNode>().ToList();
            t.Check(math.Any(m => m.source == @"x = \text{2}") && math.Any(m => m.source == @"a\tag{1}"), $"\\ref in math resolved, \\label dropped: {string.Join(" | ", math.Select(m => m.source))}");

            Typeset(@"\section{A}\label{x}\section{B}\label{x}", out errors);
            t.Check(Has(errors, "Label `x' multiply defined"), "a label defined twice");
            yield break;
        }

        [A_XSDActionDependency("Tex.Equations", "Test")]
        private static IEnumerator<int> Equations(TestContext t)
        {
            string source = "\\begin{equation}a\\label{e:a}\\end{equation}\n" +
                "\\begin{equation*}b\\end{equation*}\n" +
                "\\begin{align}x &= 1 \\label{e:x}\\\\ y &= 2 \\notag\\\\ z &= 3 \\tag{A}\\label{e:z}\\\\ w &= 4\\\\\\end{align}\n" +
                "\\begin{align*}p &= q\\end{align*}\n" +
                "\\begin{multline}m \\\\ n\\label{e:m}\\end{multline}\n" +
                "See \\eqref{e:x}, \\ref{e:z} and \\eqref{e:m}. $\\eqref{e:a}$\n";
            TexTypesetter typesetter = Typeset(source, out List<TexError> errors);
            List<TexMathNode> math = Pars(typesetter).SelectMany(p => p.list).OfType<TexMathNode>().ToList();
            string all = string.Join(" | ", math.Select(m => m.source));
            string Tags(TexMathNode m) => string.Join(",", Regex.Matches(m.source, @"\\tag\s*\{([^}]*)\}").Select(x => x.Groups[1].Value));

            t.Check(errors.Count == 0, $"compiles clean: {string.Join("; ", errors)}");
            t.Check(math.Count == 6 && math[0].source == @"a\tag{1}" && math[1].source == "b", $"equation numbered, equation* not: {all}");
            t.Check(math[2].source.StartsWith(@"\begin{align}") && Tags(math[2]) == "2,A,3" && !math[2].source.Contains("notag"),
                $"align numbers each row but \\notag and \\tag rows, and not the empty last row: {math[2].source}");
            t.Check(math[3].source.StartsWith(@"\begin{align*}") && Tags(math[3]) == "", $"align* is unnumbered: {math[3].source}");
            t.Check(Tags(math[4]) == "4" && math[4].source.EndsWith(@"\tag{4}\end{multline}"), $"multline takes one number, on its last row: {math[4].source}");
            t.Check(typesetter.labels["e:a"] == "1" && typesetter.labels["e:x"] == "2" && typesetter.labels["e:z"] == "A" && typesetter.labels["e:m"] == "4",
                "labels point at their row's number or tag");

            string text = string.Join(" / ", Pars(typesetter).Select(Line));
            t.Check(text.Contains("See (2), A and (4)."), $"\\eqref and \\ref in text: {text}");
            t.Check(math[5].source == @"\text{(1)}", $"\\eqref in math: {math[5].source}");

            string report = "\\documentclass{report}\\begin{document}\\chapter{A}\\begin{equation}x\\end{equation}\\end{document}";
            t.Check(Pars(Typeset(report, out _)).SelectMany(p => p.list).OfType<TexMathNode>().Single().source == @"x\tag{1.1}",
                "report numbers equations within the chapter");
            yield break;
        }

        [A_XSDActionDependency("Tex.MathEnvironments", "Test")]
        private static IEnumerator<int> MathEnvironments(TestContext t)
        {
            string matrix = Lines(@"\[ \begin{pmatrix} a & b \\ c & d \end{pmatrix} \]", out List<TexError> errors);
            t.Check(matrix == @"$$\begin{pmatrix} a & b \\ c & d \end{pmatrix}$$" && errors.Count == 0, $"an environment inside math is written back as source: {matrix}");

            List<TexMathNode> math = Pars(Typeset(@"\begin{equation}\begin{split}a&=b\\&=c\end{split}\end{equation}" +
                @"\begin{align}\left\{\begin{aligned}a\\b\end{aligned}\right. \\ c\end{align}", out errors))
                .SelectMany(p => p.list).OfType<TexMathNode>().ToList();
            t.Check(math[0].source == @"\begin{split}a&=b\\&=c\end{split}\tag{1}", $"split inside equation takes one number: {math[0].source}");
            t.Check(Regex.Matches(math[1].source, @"\\tag").Count == 2, $"rows split only at the top level: {math[1].source}");
            t.Check(math.All(m => MathParser.Parse(m.source) is not MathError) && errors.Count == 0, "both parse as math");

            Lines(@"\begin{pmatrix} a \end{pmatrix}", out errors);
            t.Check(Has(errors, "Missing $ inserted"), "a math environment in text");

            string ops = Lines(@"\DeclareMathOperator{\tr}{tr}\DeclareMathOperator*{\argmax}{argmax}$\tr A + \argmax_x f$", out errors);
            t.Check(ops.Contains(@"\operatorname {tr}") && ops.Contains(@"\operatorname *{argmax}") && errors.Count == 0,
                $"\\DeclareMathOperator defines \\operatorname macros: {ops}");
            yield break;
        }

        [A_XSDActionDependency("Tex.Bib", "Test")]
        private static IEnumerator<int> Bib(TestContext t)
        {
            string bib = "@string{tug = \"TUGboat\"}\n@comment{ignored}\n" +
                "@book{knuth84, author = {Donald E. Knuth}, title = {The {\\TeX}book}, publisher = \"Addison-Wesley\", year = 1984}\n" +
                "@article{lamport, author = \"Leslie Lamport and others\", title = {A Document Preparation System}, journal = tug # \" Journal\",\n" +
                "  volume = 7, number = {2}, pages = {10-20}, year = {1986}, month = jun}\n" +
                "@inproceedings(vdw, author = {Johannes Diderik van der Waals and Smith, Jr., John}, title = {On Gases}, booktitle = {Proc. Physics}, year = 1873)\n" +
                "@misc{unused, title = {Never cited}, year = 2000}\n";
            string folder = Folder(("refs.bib", bib));
            string Doc(string style) => "\\documentclass{article}\\begin{document}\n\\cite{lamport} and \\cite[p.~5]{knuth84,vdw}.\n" +
                $"\\bibliographystyle{{{style}}}\\bibliography{{refs}}\nAfter \\cite{{missing}}.\n\\end{{document}}\n";

            List<TexError> errors = new List<TexError>();
            List<string> Typeset(string style)
            {
                TexTypesetter typesetter = new TexTypesetter(Doc(style), null, folder);
                typesetter.Run();
                errors = typesetter.errors;
                return Pars(typesetter).Select(BoxLine).ToList();
            }

            List<string> plain = Typeset("plain");
            t.Check(plain[0] == "[2] and [1, 3, p. 5].",$"sorted numbers, a note: {plain[0]}");
            t.Check(plain[1] == "References" && plain[^1] == "After [?].", $"the list stands where \\bibliography was: {string.Join(" / ", plain)}");
            t.Check(plain.Count == 6, $"three entries, the uncited one left out: {plain.Count}");
            t.Check(plain[2] == "[1] Donald E. Knuth. The TeXbook. Addison-Wesley, 1984.", $"a book: {plain[2]}");
            t.Check(plain[3] == "[2] Leslie Lamport et al. A document preparation system. TUGboat Journal, 7(2):10–20, June 1986.", $"an article: {plain[3]}");
            t.Check(plain[4] == "[3] Johannes Diderik van der Waals and John Smith, Jr. On gases. In Proc. Physics, 1873.", $"von and Jr names: {plain[4]}");
            t.Check(errors.Count == 1 && Has(errors, "Citation `missing' undefined"), $"one warning: {string.Join("; ", errors)}");

            t.Check(Typeset("unsrt")[0] == "[1] and [2, 3, p. 5].","unsrt numbers in citation order");
            List<string> alpha = Typeset("alpha");
            t.Check(alpha[0] == "[L+86] and [Knu84, vdWS73, p. 5]."&& alpha[2].StartsWith("[Knu84]"), $"alpha labels: {alpha[0]} / {alpha[2]}");
            t.Check(Typeset("abbrv")[2] == "[1] D. E. Knuth. The TeXbook. Addison-Wesley, 1984.", "abbrv initials");

            TexTypesetter none = new TexTypesetter(@"\cite{a}\bibliographystyle{plain}\bibliography{nope}");
            none.Run();
            t.Check(Has(none.errors, "I couldn't open database file nope.bib"), "a missing .bib");

            List<TexBibEntry> entries = new List<TexBibEntry>();
            List<string> problems = new List<string>();
            TexBibliography.Parse("@misc{x, title = \"Say {\"}hi{\"}\" # {!}, note = undefinedname, year 1}", entries, problems.Add);
            t.Check(entries.Single().fields["title"] == "Say {\"}hi{\"}!", $"quotes with braced quotes, #: {entries.Single().fields["title"]}");
            t.Check(problems.Any(p => p.Contains("undefinedname")) && problems.Any(p => p.Contains("expecting a field name")), $"parse problems: {string.Join("; ", problems)}");
            Directory.Delete(folder, true);
            yield break;
        }

        // A paragraph's text with its boxes' text, URL break marks left out, spaces collapsed.
        private static string BoxLine(TexParagraph p) =>
            Regex.Replace(BoxText(p.list).Replace("\u200B", "").Replace("\uE005", "").Replace("\uE006", ""), " {2,}", " ").Trim();

        // The paragraphs of a document typeset with a .bib beside it, as BoxLine reads them.
        private static List<string> BibLines(string source, string bib, out List<TexError> errors)
        {
            string folder = Folder(("refs.bib", bib));
            TexTypesetter typesetter = new TexTypesetter(source, null, folder);
            typesetter.Run();
            errors = typesetter.errors;
            List<string> lines = new List<string>(16);
            foreach (TexParagraph p in Pars(typesetter))
                lines.Add(BoxLine(p));
            Directory.Delete(folder, true);
            return lines;
        }

        // The line that is the entry after its [label], or null.
        private static string? Entry(List<string> lines, string text)
        {
            foreach (string line in lines)
                if (line.StartsWith('[') && line.EndsWith("] " + text, StringComparison.Ordinal)) return line;
            return null;
        }

        [A_XSDActionDependency("Tex.Biblatex", "Test")]
        private static IEnumerator<int> Biblatex(TestContext t)
        {
            const string bib = "@online{apple, author = {{Apple Inc.}}, title = {Metal}, year = 2025}\n" +
                "@online{dotnet, author = {{dotnet}}, title = {Silk}, year = 2025}\n" +
                "@online{deepl, author = {{DeepL GmbH}}, title = {Translator}, year = 2024}\n" +
                "@misc{sph, author = {Haofeng Huang and Li Yi}, title = {SPH}, year = 2024}\n" +
                "@online{vries, author = {Joey de Vries}, title = {Coordinates}, year = 2023}\n" +
                "@online{nexa, author = {{NexerG}}, title = {Project Aurora}, year = 2025}\n" +
                "@online{nexb, author = {{NexerG}}, title = {RadianceCascades2D.cs}, year = 2025}\n" +
                "@online{nexc, author = {{NexerG}}, title = {RadianceCascades2D Shaders}, year = 2025}\n" +
                "@book{four, author = {Ann One and Ben Two and Cid Three and Dan Four}, title = {Many}, year = 1999}\n" +
                "@online{tmp, author = {{tmpvar}}, title = {Cascades}, date = {2023-05-07}}\n";
            string Doc(string options) => $"\\documentclass{{article}}\\usepackage[style=alphabetic,minalphanames=3{options}]{{biblatex}}\\addbibresource{{refs.bib}}\n" +
                "\\begin{document}\nSee \\cite{nexc,apple} and \\nocite{*}.\n\\printbibliography\n\\end{document}\n";

            List<string> lines = BibLines(Doc(",sortcites"), bib, out List<TexError> errors);
            List<string> labels = new List<string>(16);
            foreach (string line in lines)
                if (line.StartsWith('[')) labels.Add(line[1..line.IndexOf(']')]);
            string order = string.Join(" ", labels);
            t.Check(order == "App25 Dee24 dot25 HY24 Nex25a Nex25b Nex25c OTT+99 tmp23 Vri23",
                $"labels: three letters of one family, braced names whole, initials of two or three, + past three, a b c in anyt order: {order}");
            t.Check(lines[0] == "See [App25; Nex25b] and .", $"a citation's labels sorted and joined by a semicolon: {lines[0]}");
            t.Check(lines[1] == "References", $"the heading: {lines[1]}");
            t.Check(errors.Count == 0, $"no errors: {string.Join("; ", errors)}");

            List<string> unsorted = BibLines(Doc(",maxalphanames=4"), bib, out _);
            t.Check(unsorted[0] == "See [Nex25b; App25] and ." && Entry(unsorted, "Ann One et al. Many. 1999.") is string four && four.StartsWith("[OTTF99]"),
                $"citation order without sortcites, four initials under maxalphanames=4, et al. past maxnames: {unsorted[0]} / {string.Join(" / ", unsorted)}");
            yield break;
        }

        // a justified block holding one long URL run in a column narrow enough to wrap it
        private static XElement UrlBlock(string url, float stretch, float breakPenalty, float bigBreakPenalty)
        {
            XElement run = new XElement("Run", new XAttribute("Text", url), new XAttribute("FontName", "latin-modern-mono"));
            if (stretch > 0f)
            {
                run.SetAttributeValue("UrlStretch", stretch);
                run.SetAttributeValue("UrlBreakPenalty", breakPenalty);
                run.SetAttributeValue("UrlBigBreakPenalty", bigBreakPenalty);
            }
            return new XElement("Document",
                new XElement("DocumentLayout", new XAttribute("BlockSpacing", "0"), new XAttribute("OptimalBreaks", "true"), new XAttribute("Tolerance", "10000"),
                    new XElement("TextStyle", new XAttribute("Type", "Text"), new XAttribute("FontSize", "16")),
                    new XElement("Page", new XAttribute("Mode", "Paged"), new XAttribute("Size", "A6"))),
                new XElement("Block", new XAttribute("Align", "Justify"), run));
        }

        // Each line's last character before its break, break markers left out.
        private static string LineEnds(BlockControl block)
        {
            StringBuilder ends = new StringBuilder(16);
            string text = block.text ?? "";
            for (int i = 0; i < block.Lines.Count - 1; i++)
            {
                LineSegment last = block.Lines[i].segments[^1];
                int k = last.charStart + last.charCount - 1;
                while (k > 0 && TextMeasurer.IsZeroWidthBreak(text[k])) k--;
                ends.Append(text[k]);
            }
            return ends.ToString();
        }

        [A_XSDActionDependency("Tex.UrlStretch", "Test")]
        private static IEnumerator<int> UrlStretch(TestContext t)
        {
            string folder = Folder(("refs.bib", "@online{a, title = {Site}, url = {https://example.com/a-long/path/to/somewhere}, year = 2025}\n"));
            XElement lowered = TexLowering.Compile("\\documentclass{article}\\usepackage{biblatex}\\addbibresource{refs.bib}\n"
                + "\\begin{document}\\nocite{*}Plain \\url{https://example.com/a/b}.\\printbibliography\\end{document}\n", out _, null, folder);
            Directory.Delete(folder, true);
            XElement? bibUrl = null, plainUrl = null;
            foreach (XElement run in lowered.Descendants("Run"))
            {
                string text = (string?)run.Attribute("Text") ?? "";
                if (text.Contains("somewhere")) bibUrl = run;
                else if (text.Contains("example.com/a")) plainUrl = run;
            }
            t.Check(bibUrl != null && (float?)bibUrl.Attribute("UrlStretch") > 0f && (string?)bibUrl.Attribute("UrlBreakPenalty") == "200"
                && (string?)bibUrl.Attribute("UrlBigBreakPenalty") == "100" && ((string?)bibUrl.Attribute("Text"))!.Contains("-\uE006"),
                $"a bibliography URL carries \\biburlsetup's stretch, penalties and break markers: {bibUrl}");
            t.Check(plainUrl != null && plainUrl.Attribute("UrlStretch") == null && ((string?)plainUrl.Attribute("Text"))!.Contains("/\u200B"),
                $"a \\url in the text keeps the url package's free breaks: {plainUrl}");

            string url = "https://e.com/abcdefghijklmnopqrstuvwxyz0123456789abcdefghijklmnopqrstuvwxyz0123456789";
            DocumentEditorControl editor = ShowDocument(t, UrlBlock(url.Replace("/", "/\uE005"), 0f, 0f, 0f));
            yield return 2;
            BlockControl plain = (BlockControl)View(editor, 0);
            float plainWidth = plain.Lines[0].width;
            editor = ShowDocument(t, UrlBlock(url.Replace("/", "/\uE005"), 2f, 0f, 0f));
            yield return 2;
            BlockControl stretched = (BlockControl)View(editor, 0);
            t.Check(plain.Lines.Count > 1 && stretched.Lines.Count > 1 && stretched.Lines[0].width > plainWidth + 1f,
                $"a justified line ending inside a URL spreads its slack between the URL's characters: {plainWidth:0.0} -> {stretched.Lines[0].width:0.0}");
            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("Tex.UrlBreakPenalty", "Test")]
        private static IEnumerator<int> UrlBreakPenalty(TestContext t)
        {
            StringBuilder url = new StringBuilder(256);
            for (int i = 0; i < 12; i++)
                url.Append("ab-\uE006cd/\uE005ef");
            DocumentEditorControl editor = ShowDocument(t, UrlBlock(url.ToString(), 1f, 10000f, 0f));
            yield return 2;
            string big = LineEnds((BlockControl)View(editor, 0));
            editor = ShowDocument(t, UrlBlock(url.ToString(), 1f, 0f, 10000f));
            yield return 2;
            string ordinary = LineEnds((BlockControl)View(editor, 0));
            t.Check(big.Length > 1 && big.Replace("-", "").Length == 0, $"a heavy ordinary penalty leaves lines ending at the big breaks: {big}");
            t.Check(ordinary.Length > 1 && ordinary.Replace("/", "").Length == 0, $"a heavy big penalty leaves lines ending at the ordinary breaks: {ordinary}");
            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("Tex.BiblatexDrivers", "Test")]
        private static IEnumerator<int> BiblatexDrivers(TestContext t)
        {
            const string bib = "@online{deepl, author = {{DeepL GmbH}}, title = {DeepL Translator}, year = {2024}, url = {https://www.deepl.com/translator},\n" +
                "  note = {[Online]}, organization = {DeepL}, urldate = {2025-05-06}}\n" +
                "@online{quat, author = {{Game Dev}}, title = {What is a quaternion?}, note = {[Online]}, year = 2015}\n" +
                "@article{lit, author = {Donald E. Knuth and Leslie Lamport}, title = {Literate Programming}, journal = {The Computer Journal},\n" +
                "  volume = 27, number = 2, pages = {97-111}, year = 1984}\n" +
                "@book{texbook, author = {Donald E. Knuth}, title = {The {\\TeX}book}, publisher = {Addison-Wesley}, address = {Reading}, edition = 2, year = 1986}\n" +
                "@phdthesis{things, author = {Ann Bee}, title = {On Things}, school = {MIT}, year = 2001}\n" +
                "@inproceedings{talk, author = {Ann Bee}, title = {Talk}, booktitle = {Proc. X}, editor = {Carl Dee}, pages = {1-2}, publisher = {ACM}, year = 2002}\n" +
                "@misc{sph, author = {Haofeng Huang and Li Yi}, title = {SPH}, eprint = {2403.11156}, archivePrefix = {arXiv}, primaryClass = {physics.flu-dyn}, year = 2024}\n";
            List<string> lines = BibLines("\\documentclass{article}\\usepackage[giveninits=true,alldates=iso]{biblatex}\\addbibresource{refs.bib}\n" +
                "\\begin{document}\n\\nocite{*}\n\\printbibliography\n\\end{document}\n", bib, out List<TexError> errors);

            foreach ((string text, string what) in new[]
                     {
                         ("DeepL GmbH. DeepL Translator. [Online]. DeepL. 2024. URL: https://www.deepl.com/translator (visited on 2025-05-06).", "online: note, organization, date, url and urldate"),
                         ("Game Dev. What is a quaternion? [Online]. 2015.", "no period after a question mark"),
                         ("D. E. Knuth and L. Lamport. “Literate Programming”. In: The Computer Journal 27.2 (1984), pp. 97–111.", "article: quoted title, In:, volume.number (date), pages"),
                         ("D. E. Knuth. The TeXbook. 2nd ed. Reading: Addison-Wesley, 1986.", "book: edition, location: publisher, date"),
                         ("A. Bee. “On Things”. PhD thesis. MIT, 2001.", "thesis: its type's string, institution, date"),
                         ("A. Bee. “Talk”. In: Proc. X. Ed. by C. Dee. ACM, 2002, pp. 1–2.", "inproceedings: booktitle, editor, publisher, pages"),
                         ("H. Huang and L. Yi. SPH. 2024. arXiv: 2403.11156 [physics.flu-dyn].", "misc: date, then the eprint")
                     })
                t.Check(Entry(lines, text) != null, $"{what}: {string.Join(" / ", lines)}");
            t.Check(lines[0] == "References" && Entry(lines, "A. Bee. “On Things”. PhD thesis. MIT, 2001.")!.StartsWith("[1]"),
                $"numeric labels, sorted by name, title, year: {string.Join(" / ", lines)}");
            t.Check(errors.Count == 0, $"no errors: {string.Join("; ", errors)}");
            yield break;
        }

        [A_XSDActionDependency("Tex.BiblatexStrings", "Test")]
        private static IEnumerator<int> BiblatexStrings(TestContext t)
        {
            const string bib = "@online{a, author = {Ann Bee and Carl Dee}, title = {Page}, url = {https://a.lt/x}, urldate = {2025-05-06}, year = 2025}\n" +
                "@article{b, author = {Ann Bee}, title = {Straipsnis}, journal = {Žurnalas}, year = 2020}\n";
            string Doc(string print) => "\\documentclass{article}\\usepackage{polyglossia}\\setmainlanguage{lithuanian}\n" +
                "\\usepackage[style=alphabetic,abbreviate=false,alldates=iso]{biblatex}\\addbibresource{refs.bib}\\usepackage[nottoc]{tocbibind}\n" +
                "\\DefineBibliographyStrings{lithuanian}{urlseen = {žiūrėta}, in = {iš}}\n" +
                $"\\begin{{document}}\n\\tableofcontents\n\\section{{Įvadas}}\n\\nocite{{*}}\n{print}\n\\end{{document}}\n";

            List<string> lines = BibLines(Doc("\\printbibliography[heading=bibintoc]"), bib, out List<TexError> errors);
            t.Check(lines.Contains("Literatūros sąrašas"), $"the heading is the references string in the document's language: {string.Join(" / ", lines)}");
            t.Check(lines.Exists(l => l.StartsWith("Literatūros sąrašas") && l != "Literatūros sąrašas"), $"and its contents entry: {string.Join(" / ", lines)}");
            t.Check(Entry(lines, "Ann Bee ir Carl Dee. Page. 2025. URL: https://a.lt/x (žiūrėta 2025-05-06).") != null,
                $"Lithuanian and, \\DefineBibliographyStrings for urlseen: {string.Join(" / ", lines)}");
            t.Check(Entry(lines, "Ann Bee. „Straipsnis“. Iš: Žurnalas (2020).") != null, $"in capitalized where a sentence starts, Lithuanian quotes: {string.Join(" / ", lines)}");
            t.Check(errors.Count == 0, $"no errors: {string.Join("; ", errors)}");

            List<string> titled = BibLines(Doc("\\printbibliography[title={Šaltiniai}]"), bib, out _);
            t.Check(titled.Contains("Šaltiniai") && !titled.Contains("Literatūros sąrašas"), $"title= replaces the heading: {string.Join(" / ", titled)}");
            yield break;
        }

        [A_XSDActionDependency("Tex.BiblatexOptions", "Test")]
        private static IEnumerator<int> BiblatexOptions(TestContext t)
        {
            const string bib = "@misc{sph, author = {Haofeng Huang and Li Yi}, title = {SPH}, year = 2024}\n" +
                "@misc{knuth, author = {Donald E. Knuth}, title = {Book}, year = 1984}\n" +
                "@misc{three, author = {Ann One and Ben Two and Cid Three}, title = {Three}, year = 2000}\n";
            string Doc(string preamble) => "\\documentclass{article}\\usepackage[maxnames=2,minnames=1,giveninits]{biblatex}\\addbibresource{refs.bib}\n" +
                preamble + "\n\\begin{document}\n\\nocite{*}\n\\printbibliography\n\\end{document}\n";

            List<string> lines = BibLines(Doc("\\DeclareDelimFormat{finalnamedelim}{\\addcomma\\space}" +
                "\\DeclareFieldFormat{doi}{\\url{#1}}\\DeclareFieldFormat[online]{title}{#1}\\renewbibmacro*{medium-type}{\\printfield{x}}" +
                "\\DeclareBibliographyDriver{misc}{x}\\defbibheading{h}[t]{x}\\WarningFilter{biblatex}{File not found!}"), bib, out List<TexError> errors);
            t.Check(Entry(lines, "H. Huang, L. Yi. SPH. 2024.") != null, $"\\DeclareDelimFormat's finalnamedelim: {string.Join(" / ", lines)}");
            t.Check(Entry(lines, "A. One et al. Three. 2000.") != null, $"maxnames=2 shortens three names to one and et al.: {string.Join(" / ", lines)}");
            t.Check(lines.Count == 4 && errors.Count == 0, $"the ignored commands print nothing and raise nothing: {string.Join(" / ", lines)}; {string.Join("; ", errors)}");

            List<string> reversed = BibLines(Doc("\\DeclareNameAlias{default}{family-given}"), bib, out _);
            t.Check(Entry(reversed, "Knuth, D. E. Book. 1984.") != null && Entry(reversed, "Huang, H. and Yi, L. SPH. 2024.") != null,
                $"\\DeclareNameAlias family-given: {string.Join(" / ", reversed)}");
            yield break;
        }

        [A_XSDActionDependency("Tex.BibList", "Test")]
        private static IEnumerator<int> BibList(TestContext t)
        {
            string folder = Folder(("refs.bib", "@misc{wide, author = {{WWW}}, title = {Wide}, year = 2025}\n@misc{thin, author = {{iiii}}, title = {Thin}, year = 2025}\n" +
                "@misc{long, author = {Ann Bee}, title = {A title long enough that this entry has to wrap onto a second line of the bibliography}, year = 2025}\n"));
            TexStyle normal = new TexStyle(new TexFont(TexFamily.Roman, false, false, 10 * 65536), null, false);
            float px = 96f / 72.27f;

            XElement Hanging(XElement document)
            {
                foreach (XElement block in document.Elements("Block"))
                    if ((float?)block.Attribute("LeftIndent") is > 0f) return block;
                return new XElement("Block");
            }

            XElement plain = TexLowering.Compile("\\documentclass{article}\\begin{document}\\nocite{*}\\bibliographystyle{plain}\\bibliography{refs}\\end{document}",
                out List<TexError> errors, null, folder);
            float margin = TexLowering.TextWidth("[3]", normal) + 5f * px;
            XElement entry = Hanging(plain);
            t.Check(Near((float?)entry.Attribute("LeftIndent") ?? 0f, margin, 0.1f) && Near((float?)entry.Attribute("Indent") ?? 0f, -margin, 0.1f),
                $"thebibliography hangs at the widest label's width plus \\labelsep: {entry.ToString(SaveOptions.DisableFormatting)}, margin {margin}");
            t.Check(errors.Count == 0, $"no errors: {string.Join("; ", errors)}");

            XElement alpha = TexLowering.Compile("\\documentclass{article}\\usepackage[style=alphabetic]{biblatex}\\addbibresource{refs.bib}" +
                "\\begin{document}\\nocite{*}\\printbibliography\\end{document}", out errors, null, folder);
            float widest = MathF.Max(TexLowering.TextWidth("[WWW25]", normal), TexLowering.TextWidth("[iii25]", normal));
            XElement alphaEntry = Hanging(alpha);
            t.Check(Near((float?)alphaEntry.Attribute("LeftIndent") ?? 0f, widest + 10f * px, 0.1f),
                $"biblatex's list hangs at the widest label measured plus \\biblabelsep: {alphaEntry.ToString(SaveOptions.DisableFormatting)}, margin {widest + 10f * px}");
            t.Check(errors.Count == 0, $"no errors: {string.Join("; ", errors)}");
            Directory.Delete(folder, true);
            yield break;
        }

        [A_XSDActionDependency("Tex.Lowering.Table", "Test")]
        private static IEnumerator<int> LoweringTable(TestContext t)
        {
            List<int> lines = new List<int>();
            XElement document = TexLowering.Compile("Before.\n\\begin{tabular}{lr}\na & bb \\\\\n\\multicolumn{2}{c}{a wide cell of text}\n\\end{tabular}\n",
                out List<TexError> errors, lines);
            t.Check(errors.Count == 0, $"compiles clean: {string.Join("; ", errors.Select(e => e.message))}");
            XElement table = document.Element("Table")!;
            t.Check((string?)table.Attribute("Borders") == "false", "an unruled tabular draws no borders");
            float[] widths = table.Elements("Column").Select(c => (float)c.Attribute("Width")!).ToArray();
            t.Check(widths.Length == 2 && widths.All(w => w >= 24f), $"two columns: {string.Join(",", widths)}");
            List<XElement> rows = table.Elements("Row").ToList();
            t.Check(rows.Count == 2 && rows[0].Elements("Cell").Count() == 2 && (int?)rows[1].Element("Cell")!.Attribute("ColumnSpan") == 2,
                "two cells, then one spanning both");
            t.Check((string?)rows[0].Elements("Cell").Last().Element("Block")!.Attribute("Align") == "Right"
                && (string?)rows[1].Element("Cell")!.Element("Block")!.Attribute("Align") == "Center", "cells carry their alignment");
            t.Check(string.Join(",", lines) == "1,2", $"the table is one block at its \\begin line: {string.Join(",", lines)}");

            RichTextDocument parsed = DocumentXml.Parse(document);
            NoteTable control = (NoteTable)parsed.blocks[1];
            t.Check(!control.showBorders && control.rows.SelectMany(r => r).ElementAt(2).span == 2, "it parses as a borderless table with a merged cell");
            t.Check(table.Attribute("Align") == null && control.alignment == TextAlignment.Left, "a tabular in a justified paragraph sits left");

            string Aligned(string body)
            {
                XElement compiled = TexLowering.Compile(body, out _);
                return (string?)compiled.Descendants("Table").Single().Attribute("Align") ?? "Left";
            }
            const string tabular = "\\begin{tabular}{l}a\\end{tabular}";
            t.Check(Aligned($"\\begin{{center}}{tabular}\\end{{center}}") == "Center", "center centres");
            t.Check(Aligned($"\\begin{{table}}\\centering{tabular}\\end{{table}}") == "Center", "\\centering in a float centres");
            t.Check(Aligned($"\\begin{{flushright}}{tabular}\\end{{flushright}}") == "Right", "flushright");
            t.Check(Aligned($"{{\\centering{tabular}}}\n\nAfter.") == "Left", "\\centering closed before the paragraph ends does not");

            XElement ruled = TexLowering.Compile(@"\begin{tabular}{|l||r|}\toprule a&b\\\cmidrule(lr){1-1}\cline{2-2} c&d\\\hline\hline\end{tabular}", out _)
                .Descendants("Table").Single();
            List<XElement> columns = ruled.Elements("Column").ToList();
            t.Check((string?)ruled.Attribute("Borders") == "false" && ((string?)ruled.Attribute("Padding"))?.EndsWith(" 0") == true,
                $"a ruled tabular draws its own rules with LaTeX's padding: {ruled.Attribute("Padding")}");
            t.Check((string?)columns[0].Attribute("RuleLeft") == "Plain" && (string?)columns[1].Attribute("RuleLeft") == "Double"
                && (string?)columns[1].Attribute("RuleRight") == "Plain" && columns[0].Attribute("RuleRight") == null, "| and || on their column edges");
            List<XElement> top = ruled.Elements("Row").First().Elements("Cell").ToList();
            List<XElement> bottom = ruled.Elements("Row").Last().Elements("Cell").ToList();
            t.Check(top.All(c => (string?)c.Attribute("RuleAbove") == "Heavy"), "\\toprule over every first-row cell");
            t.Check((string?)bottom[0].Attribute("RuleAbove") == "Cmid" && (string?)bottom[0].Attribute("TrimAbove") == "Both"
                && (string?)bottom[1].Attribute("RuleAbove") == "Plain" && bottom[1].Attribute("TrimAbove") == null, "\\cmidrule(lr) and \\cline on their columns");
            t.Check(bottom.All(c => (string?)c.Attribute("RuleBelow") == "Double"), "\\hline\\hline under the last row is a double rule");

            XElement centred = TexLowering.Compile($"\\begin{{center}}{tabular}\\end{{center}}", out _);
            TableControl table2 = new TableControl(DocumentXml.Parse(centred).blocks.OfType<NoteTable>().Single());
            t.Check(DocumentXml.WriteTable(table2.model).Attribute("Align")?.Value == "Center", "Align round-trips");
            Vector2 desired = table2.Measure(new Vector2(1000f, float.PositiveInfinity));
            table2.Arrange(new LayoutRect(0f, 0f, 1000f, desired.Y));
            StackPanelControl first = table2.Cells()[0];
            float left = first.arrangedRect.x - first.margin.left;
            t.Check(MathF.Abs(left - (1000f - desired.X) * 0.5f) < 0.5f, $"the columns sit centred: {left} of {(1000f - desired.X) * 0.5f}");
            table2.Destroy();
            yield break;
        }

        [A_XSDActionDependency("Tex.Lowering.Image", "Test")]
        private static IEnumerator<int> LoweringImage(TestContext t)
        {
            string folder = Folder(("doc.pdf", ""));
            using (SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32> png = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(40, 20))
            {
                SixLabors.ImageSharp.ImageExtensions.SaveAsPng(png, Path.Combine(folder, "pic.png"));
                Directory.CreateDirectory(Path.Combine(folder, "sub"));
                SixLabors.ImageSharp.ImageExtensions.SaveAsPng(png, Path.Combine(folder, "sub", "inner.png"));
            }

            List<XElement> Pictures(string source, out List<TexError> errors) =>
                TexLowering.Compile(source, out errors, null, folder).Descendants("Run").Where(r => r.Attribute("Image") != null).ToList();

            List<XElement> pictures = Pictures(@"\includegraphics[width=0.5\textwidth]{pic} \includegraphics[scale=2,angle=90]{pic.png}", out List<TexError> errors);
            t.Check(errors.Count == 0 && pictures.Count == 2, $"two pictures: {string.Join("; ", errors.Select(e => e.message))}");
            t.Check((string?)pictures[0].Attribute("Image") == Path.GetFullPath(Path.Combine(folder, "pic.png")), "the path is found and absolute");
            float half = 0.5f * 345f * 96f / 72.27f;
            t.Check(Math.Abs((float)pictures[0].Attribute("Width")! - half) < 0.5f && Math.Abs((float)pictures[0].Attribute("Height")! - half / 2f) < 0.5f,
                $"width=0.5\\textwidth: {pictures[0].Attribute("Width")?.Value}");
            t.Check((string?)pictures[1].Attribute("Width") == "80" && (string?)pictures[1].Attribute("Height") == "40" && (string?)pictures[1].Attribute("Rotation") == "-90",
                "scale from the picture's own size, angle turned clockwise");

            t.Check(Pictures(@"\graphicspath{{sub/}}\includegraphics{inner}", out errors).Count == 1 && errors.Count == 0, "\\graphicspath");
            Pictures(@"\includegraphics{absent}", out errors);
            t.Check(Has(errors, "File `absent' not found"), "a missing picture");
            Pictures(@"\includegraphics{doc}", out errors);
            t.Check(Has(errors, "PDF and EPS pictures are not supported"), "a PDF picture");
            TexLowering.Compile(@"\includegraphics{pic}", out errors);
            t.Check(Has(errors, "File `pic' not found"), "no folder finds nothing");
            Directory.Delete(folder, true);
            yield break;
        }
        #endregion

        #region ---- line breaking ----
        // each line but the last: how far its spaces stretch (above 0) or shrink (below 0) to fill the width
        private static List<float> Ratios(BlockLayout layout, TextMeasurer.Run run, string text, float width)
        {
            List<float> ratios = new List<float>();
            for (int l = 0; l < layout.lines.Count - 1; l++)
            {
                LineSegment segment = layout.lines[l].segments[0];
                float pen = 0f, visible = 0f, spaces = 0f, inner = 0f;
                for (int k = segment.charStart; k < segment.charStart + segment.charCount; k++)
                {
                    float advance = TextMeasurer.MeasureAdvance(text[k], run);
                    pen += advance;
                    if (text[k] == ' ') spaces += advance;
                    else
                    {
                        visible = pen;
                        inner = spaces;
                    }
                }
                ratios.Add(visible <= width ? (width - visible) / (inner * TextMeasurer.GlueStretch) : (width - visible) / (inner * TextMeasurer.GlueShrink));
            }
            return ratios;
        }

        [A_XSDActionDependency("Tex.OptimalBreaks", "Test")]
        private static IEnumerator<int> OptimalBreaks(TestContext t)
        {
            IGlyphMetrics metrics = new FontAssetGlyphMetrics();
            AtlasMetaData atlas = AssetRegistries.GetRegistryByValueType<string, FontAsset>(typeof(FontAsset))["default"].atlasMetaData;
            const string text = "In olden times when wishing still helped one, there lived a king whose daughters were all beautiful, "
                + "but the youngest was so beautiful that the sun itself, which has seen so much, was astonished whenever it shone in her face. "
                + "Close by the king's castle lay a great dark forest, and under an old lime tree in the forest was a well, and when the day "
                + "was very warm, the king's child went out into the forest and sat down by the side of the cool fountain.";
            TextMeasurer.Run run = new TextMeasurer.Run(text, 0, text.Length, "default", atlas, 16, FontStyle.Regular);
            List<TextMeasurer.Run> runs = new List<TextMeasurer.Run> { run };

            int applied = 0;
            bool looser = false;
            for (float width = 260f; width <= 900f; width += 20f)
            {
                List<float> greedy = Ratios(TextMeasurer.MeasureBlock(runs, width, metrics, 1.5f), run, text, width);
                List<float> optimal = Ratios(TextMeasurer.MeasureBlock(runs, width, metrics, 1.5f, optimal: true), run, text, width);
                bool feasible = optimal.All(r => r >= -1f && r <= 1.26f);
                t.Check(feasible || optimal.SequenceEqual(greedy),
                    $"at {width} px Knuth-Plass sets every line within tolerance or leaves the block to greedy: {string.Join(", ", optimal.Select(r => r.ToString("0.00")))}");
                if (!feasible || optimal.SequenceEqual(greedy)) continue;
                applied++;
                if (greedy.Max() > optimal.Max()) looser = true;
            }
            t.Check(applied > 0 && looser, $"Knuth-Plass applies at {applied} widths and somewhere greedy leaves a looser line than it");
            yield break;
        }

        [A_XSDActionDependency("Tex.Hyphenator", "Test")]
        private static IEnumerator<int> Hyphenator(TestContext t)
        {
            static string Show(string word, bool[]? points) =>
                points == null ? word : string.Concat(word.Select((c, i) => (points[i] ? "-" : "") + c));

            TexHyphenator liang = new TexHyphenator(new[] { "hy3ph", "he2n", "hena4", "hen5at", "1na", "n2at", "1tio", "2io", "o2n", "1b" }, new[] { "as-so-ciate" });
            t.Check(Show("hyphenation", liang.Points("hyphenation")) == "hy-phen-ation", $"Liang's example: {Show("hyphenation", liang.Points("hyphenation"))}");
            t.Check(Show("associate", liang.Points("associate")) == "as-so-ciate", "an exception is used as written");
            t.Check(Show("table", liang.Points("table")) == "ta-ble" && liang.Points("tab") == null, "a break needs 2 letters before and 3 after");
            t.Check(liang.Points("abode") == null, "a break 1 letter from the start is dropped");
            Dictionary<string, bool[]> extra = new Dictionary<string, bool[]> { ["hyphenation"] = TexHyphenator.Exception("hyphe-nation")!.Value.points };
            t.Check(Show("hyphenation", liang.Points("hyphenation", extra)) == "hyphe-nation", "a document's \\hyphenation wins over patterns");

            TexHyphenator? english = TexHyphenator.English;
            t.Check(english != null, "the US English patterns load");
            if (english != null)
            {
                string Word(string w) => Show(w, english.Points(w));
                t.Check(Word("hyphenation") == "hy-phen-ation" && Word("table") == "ta-ble" && Word("present") == "present",
                    $"US English: {Word("hyphenation")} {Word("table")} {Word("present")}");
                t.Check(Word("democrat") == "de-mo-c-rat", $"the patterns' documented TeX result, bug included: {Word("democrat")}");
            }
            yield break;
        }

        [A_XSDActionDependency("Tex.UrlBreaks", "Test")]
        private static IEnumerator<int> UrlBreaks(TestContext t)
        {
            List<TexChar> chars = Chars("\\url{https://a.lt/x-y\\%20z}");
            StringBuilder typed = new StringBuilder();
            foreach (TexChar c in chars)
                typed.Append(c.ch == '\u200B' ? '|' : c.ch);
            t.Check(typed.ToString() == "https:|/|/|a.|lt/|x-|y%20z", $"\\url breaks after the url package's characters, \\% read as %: {typed}");

            IGlyphMetrics metrics = new FontAssetGlyphMetrics();
            AtlasMetaData atlas = AssetRegistries.GetRegistryByValueType<string, FontAsset>(typeof(FontAsset))["default"].atlasMetaData;
            const string text = "see aaaaaaaa/\u200Bbbbbbbbb/\u200Bcccccccc";
            TextMeasurer.Run run = new TextMeasurer.Run(text, 0, text.Length, "default", atlas, 16, FontStyle.Regular);
            List<TextMeasurer.Run> runs = new List<TextMeasurer.Run> { run };
            float slash = TextMeasurer.MeasureAdvance('/', run), a = TextMeasurer.MeasureAdvance('a', run), b = TextMeasurer.MeasureAdvance('b', run);
            t.Check(TextMeasurer.MeasureAdvance(TextMeasurer.ZeroWidthSpace, run) == 0f, "a zero-width space takes no room");
            float fits = 8f * a + slash + 8f * b + slash + 1f;
            foreach (bool optimal in new[] { false, true })
            {
                BlockLayout layout = TextMeasurer.MeasureBlock(runs, fits, metrics, 1.5f, optimal: optimal);
                int ends = 0;
                foreach (TextLine line in layout.lines)
                {
                    LineSegment end = line.segments[^1];
                    if (text[end.charStart + end.charCount - 1] == TextMeasurer.ZeroWidthSpace && line.hyphen == 0f) ends++;
                    t.Check(line.width <= fits, $"{(optimal ? "Knuth-Plass" : "greedy")}: every line fits: {line.width} of {fits}");
                }
                t.Check(layout.lines.Count >= 2 && ends >= 1, $"{(optimal ? "Knuth-Plass" : "greedy")}: the URL breaks after a slash with no hyphen: {layout.lines.Count} lines, {ends} ending at a break");
            }
            yield break;
        }

        [A_XSDActionDependency("Tex.SoftHyphen", "Test")]
        private static IEnumerator<int> SoftHyphen(TestContext t)
        {
            IGlyphMetrics metrics = new FontAssetGlyphMetrics();
            AtlasMetaData atlas = AssetRegistries.GetRegistryByValueType<string, FontAsset>(typeof(FontAsset))["default"].atlasMetaData;
            const string text = "aaaa bbbb\u00ADcccc dddd";
            TextMeasurer.Run run = new TextMeasurer.Run(text, 0, text.Length, "default", atlas, 16, FontStyle.Regular);
            List<TextMeasurer.Run> runs = new List<TextMeasurer.Run> { run };
            float Width(string s) => s.Sum(c => TextMeasurer.MeasureAdvance(c, run));
            float hyphen = TextMeasurer.MeasureAdvance('-', run);
            t.Check(TextMeasurer.MeasureAdvance(TextMeasurer.SoftHyphen, run) == 0f && hyphen > 0f, "a soft hyphen takes no room");

            float fits = Width("aaaa bbbb") + hyphen + 1f;
            foreach (bool optimal in new[] { false, true })
            {
                BlockLayout layout = TextMeasurer.MeasureBlock(runs, fits, metrics, 1.5f, optimal: optimal);
                TextLine first = layout.lines[0];
                LineSegment end = first.segments[^1];
                t.Check(layout.lines.Count >= 2 && text[end.charStart + end.charCount - 1] == TextMeasurer.SoftHyphen && first.hyphen == hyphen
                    && first.width <= fits, $"{(optimal ? "Knuth-Plass" : "greedy")}: the line breaks at the soft hyphen and shows a hyphen inside the width");
                t.Check(layout.lines.All(l => l != first ? l.hyphen == 0f : true), "only the hyphenated line shows a hyphen");
            }

            BlockLayout tight = TextMeasurer.MeasureBlock(runs, Width("aaaa bbbb") + hyphen * 0.5f, metrics, 1.5f);
            t.Check(tight.lines[0].hyphen == 0f && tight.lines[0].segments[^1].charStart + tight.lines[0].segments[^1].charCount == 5,
                "when the hyphen does not fit the line breaks at the space instead");
            BlockLayout whole = TextMeasurer.MeasureBlock(runs, 1000f, metrics, 1.5f);
            t.Check(whole.lines.Count == 1 && whole.lines[0].hyphen == 0f, "an unbroken soft hyphen shows nothing");

            XElement document = new XElement("Document", new XElement("Block", new XElement("Run", new XAttribute("Text", text))));
            string path = Path.Combine(Path.GetTempPath(), $"aurora-tex-{Guid.NewGuid():N}.axml");
            document.Save(path);
            DocumentEditorControl editor = new DocumentEditorControl
            {
                horizontalAlignment = HorizontalAlignment.Stretch,
                verticalAlignment = VerticalAlignment.Stretch
            };
            t.Show(editor);
            editor.LoadPath(path);
            File.Delete(path);
            yield return 2;
            editor.SelectAll();
            editor.Copy();
            t.Check(ClipboardText.Get() == "aaaa bbbbcccc dddd", $"copy drops soft hyphens: {ClipboardText.Get()}");
            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("Tex.Lowering.Hyphens", "Test")]
        private static IEnumerator<int> LoweringHyphens(TestContext t)
        {
            XElement document = TexLowering.Compile("\\hyphenation{ta-ble}\nA big ex\\-tra table \\texttt{table}.", out List<TexError> errors);
            t.Check(errors.Count == 0, $"compiles clean: {string.Join("; ", errors.Select(e => e.message))}");
            List<string> runs = document.Descendants("Run").Select(r => (string?)r.Attribute("Text") ?? "").ToList();
            t.Check(runs.Count == 3 && runs[0] == "A big ex\u00ADtra ta\u00ADble " && runs[1] == "table" && runs[2] == ".",
                $"\\- and \\hyphenation become soft hyphens, monospace is left whole: {string.Join(" | ", runs).Replace('\u00AD', '~')}");
            yield break;
        }

        [A_XSDActionDependency("Tex.Lowering.Spacing", "Test")]
        private static IEnumerator<int> LoweringSpacing(TestContext t)
        {
            const string source = """
                \documentclass{article}
                \begin{document}
                \section{A}
                First paragraph.

                Second paragraph.

                \noindent Third.

                \bigskip
                Fourth \[ x \] after.
                \begin{itemize}\item a \item b\end{itemize}
                Fifth.

                Sixth\quad x\hspace{1cm}y.
                \end{document}
                """;
            XElement document = TexLowering.Compile(source, out List<TexError> errors);
            t.Check(errors.Count == 0, $"compiles clean: {string.Join("; ", errors.Select(e => e.message))}");
            List<XElement> blocks = document.Elements("Block").ToList();
            const float px = 96f / 72.27f;
            float At(int i, string name) => (float?)blocks[i].Attribute(name) ?? 0f;
            bool Near(float a, float b) => MathF.Abs(a - b) < 0.01f;
            t.Check(blocks.Count == 11, $"heading, 3 paragraphs, text / display / text, 2 items, 2 paragraphs: {blocks.Count}");
            if (blocks.Count != 11) yield break;

            t.Check(At(1, "Indent") == 0f && Near(At(1, "SpaceBefore"), 2.3f * At(0, "SpaceBefore") / 3.5f), "the first paragraph after a heading is not indented, 2.3ex below it");
            t.Check(Near(At(2, "Indent"), 15f * px) && At(2, "SpaceBefore") == 0f, "a following paragraph is indented 15pt, with no \\parskip");
            t.Check(At(3, "Indent") == 0f, "\\noindent");
            t.Check(Near(At(4, "SpaceBefore"), 12f * px) && Near(At(4, "Indent"), 15f * px), "\\bigskip above an indented paragraph");
            t.Check(blocks[5].Elements("Run").Single().Attribute("Display") != null && Near(At(5, "SpaceBefore"), 10f * px),
                "a display formula is a block of its own, \\abovedisplayskip above it");
            t.Check(Near(At(6, "SpaceBefore"), 10f * px) && At(6, "Indent") == 0f, "the text after a display continues unindented, \\belowdisplayskip above it");
            t.Check(Near(At(7, "SpaceBefore"), 8f * px) && Near(At(8, "SpaceBefore"), 8f * px), "\\topsep above a list, \\itemsep + \\parsep between items");
            t.Check(Near(At(9, "SpaceBefore"), 8f * px) && At(9, "Indent") == 0f, "\\topsep below a list, and the text right after it is not indented");
            List<float> spacers = blocks[10].Elements("Run").Select(r => (float?)r.Attribute("Space")).OfType<float>().ToList();
            t.Check(Near(At(10, "Indent"), 15f * px) && spacers.Count == 2 && Near(spacers[0], 10f * px) && Near(spacers[1], 72.27f / 2.54f * px),
                $"after a blank line the paragraph is indented; \\quad and \\hspace{{1cm}} are exact spacers: {string.Join(", ", spacers)}");
            t.Check((string?)document.Element("DocumentLayout")!.Attribute("BlockSpacing") == "0", "\\parskip is 0");
        }

        [A_XSDActionDependency("Tex.BlockSpacing", "Test")]
        private static IEnumerator<int> BlockSpacing(TestContext t)
        {
            IGlyphMetrics metrics = new FontAssetGlyphMetrics();
            AtlasMetaData atlas = AssetRegistries.GetRegistryByValueType<string, FontAsset>(typeof(FontAsset))["default"].atlasMetaData;
            string words = string.Join(" ", Enumerable.Repeat("lorem ipsum", 20));
            List<TextMeasurer.Run> runs = new List<TextMeasurer.Run> { new TextMeasurer.Run(words, 0, words.Length, "default", atlas, 16, FontStyle.Regular) };
            foreach (bool optimal in new[] { false, true })
            {
                BlockLayout layout = TextMeasurer.MeasureBlock(runs, 300f, metrics, 1.5f, 40f, optimal: optimal);
                TextLine first = layout.lines[0];
                t.Check(first.left == 40f && first.room == 260f && first.width <= 260f + 1f && layout.lines.Skip(1).All(l => l.left == 0f),
                    $"{(optimal ? "Knuth-Plass" : "greedy")}: the first line starts at the indent and fills the room left: {first.left} {first.room} {first.width}");
            }
            List<TextMeasurer.Run> spacer = new List<TextMeasurer.Run> { new TextMeasurer.Run("a b", 1, 1, "default", atlas, 16, FontStyle.Regular, space: 25f) };
            t.Check(TextMeasurer.MeasureAdvance(' ', spacer[0]) == 25f, "a spacer advances its own width");

            XElement source = new XElement("Document",
                new XElement("Block", new XElement("Run", new XAttribute("Text", "first"))),
                new XElement("Block", new XAttribute("Indent", "30"), new XAttribute("SpaceBefore", "40"), new XAttribute("Align", "Justify"),
                    new XElement("Run", new XAttribute("Text", words + " a")),
                    new XElement("Run", new XAttribute("Text", " "), new XAttribute("Space", "25")),
                    new XElement("Run", new XAttribute("Text", "b"))),
                new XElement("Block", new XElement("Run", new XAttribute("Text", "last"))));
            RichTextDocument parsed = DocumentXml.Parse(source);
            XElement written = DocumentXml.ToXml(parsed);
            List<XElement> blocks = written.Elements().Where(e => e.Name.LocalName == "Block").ToList();
            t.Check((string?)blocks[1].Attribute("Indent") == "30" && (string?)blocks[1].Attribute("SpaceBefore") == "40"
                && blocks[1].Elements().Any(r => (string?)r.Attribute("Space") == "25"), "Indent, SpaceBefore and Space round-trip through .axml");

            string path = Path.Combine(Path.GetTempPath(), $"aurora-tex-{Guid.NewGuid():N}.axml");
            source.Save(path);
            DocumentEditorControl editor = new DocumentEditorControl
            {
                horizontalAlignment = HorizontalAlignment.Stretch,
                verticalAlignment = VerticalAlignment.Stretch
            };
            t.Show(editor);
            editor.LoadPath(path);
            File.Delete(path);
            yield return 2;

            List<BlockControl> p = Content(editor).children.OfType<BlockControl>().ToList();
            float zoom = p[1].Lines[0].left / 30f;
            float gap = p[1].arrangedRect.y - (p[0].arrangedRect.y + p[0].arrangedRect.height);
            float plain = p[2].arrangedRect.y - (p[1].arrangedRect.y + p[1].arrangedRect.height);
            t.Check(zoom > 0f && MathF.Abs(gap - 40f * zoom) < 0.5f && MathF.Abs(plain - editor.session.document.layout.blockSpacing * zoom) < 0.5f,
                $"SpaceBefore replaces the block spacing above its block only: {gap} and {plain} at zoom {zoom}");
            t.Check(p[1].Lines.Count > 2 && p[1].Lines.Skip(1).All(l => l.left == 0f), "only the first line is indented");

            int spacerAt = p[1].Length - 2;
            p[1].InsertText(spacerAt + 1, "x");
            t.Check(p[1].spans.Count(s => s.spaceWidth != 0f) == 1 && p[1].spans.Single(s => s.spaceWidth != 0f).count == 1,
                "text typed after a spacer is text, not more spacer");
            p[1].RemoveText(spacerAt + 1, 1);

            BlockSnapshot snapshot = p[1].Snapshot();
            BlockControl tail = p[1].SplitAt(10);
            t.Check(tail.firstIndent == 30f && tail.spaceBefore == 40f && snapshot.firstIndent == 30f && snapshot.spaceBefore == 40f,
                "a split and a snapshot carry the indent and the space above");
            tail.Destroy();
            p[1].Restore(snapshot);

            editor.SelectAll();
            editor.Copy();
            t.Check(ClipboardText.Get().Contains(" a b"), $"a spacer copies as a space: {ClipboardText.Get().Substring(Math.Max(0, ClipboardText.Get().Length - 12))}");
            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("Tex.JustifyBeforeDisplay", "Test")]
        private static IEnumerator<int> JustifyBeforeDisplay(TestContext t)
        {
            string words = string.Join(" ", Enumerable.Repeat("lorem ipsum dolor sit", 12));
            XElement document = new XElement("Document",
                new XElement("Block", new XAttribute("Align", "Justify"),
                    new XElement("Run", new XAttribute("Text", words + " ")),
                    new XElement("Run", new XAttribute("Math", "x^2"), new XAttribute("Display", "true")),
                    new XElement("Run", new XAttribute("Text", " " + words))));
            string path = Path.Combine(Path.GetTempPath(), $"aurora-tex-{Guid.NewGuid():N}.axml");
            document.Save(path);
            DocumentEditorControl editor = new DocumentEditorControl
            {
                horizontalAlignment = HorizontalAlignment.Stretch,
                verticalAlignment = VerticalAlignment.Stretch
            };
            t.Show(editor);
            editor.LoadPath(path);
            File.Delete(path);
            yield return 2;

            IReadOnlyList<TextLine> lines = ((BlockControl)View(editor, 0)).Lines;
            int display = lines.ToList().FindIndex(l => l.segments.Count > 0 && l.segments[0].runIndex == 1);
            t.Check(display > 1, $"the text wraps onto two lines or more before the display: display on line {display} of {lines.Count}");
            if (display > 1)
            {
                t.Check(lines[0].spaceExtra > 0f, "a line inside the paragraph is stretched");
                t.Check(lines[display - 1].spaceExtra == 0f, "the line before the display is not stretched");
            }
            t.Show(new StackPanelControl());
        }
        #endregion

        #region ---- pages ----
        private static List<XElement> TopBlocks(XElement document) => document.Elements().Where(e => e.Name.LocalName is "Block" or "Table").ToList();

        private static string BlockText(XElement block) => string.Concat(block.Elements("Run").Select(r => (string?)r.Attribute("Text"))).Replace("­", "");

        // A lowered or hand-built <Document> in a note editor, from a temporary file.
        private static DocumentEditorControl ShowDocument(TestContext t, XElement document)
        {
            string path = Path.Combine(Path.GetTempPath(), $"aurora-tex-{Guid.NewGuid():N}.axml");
            document.Save(path);
            DocumentEditorControl editor = new DocumentEditorControl
            {
                horizontalAlignment = HorizontalAlignment.Stretch,
                verticalAlignment = VerticalAlignment.Stretch
            };
            t.Show(editor);
            editor.LoadPath(path);
            File.Delete(path);
            return editor;
        }

        private static DocumentControl Content(DocumentEditorControl editor) => editor.children.OfType<DocumentControl>().First();

        // The control showing a note-level entry.
        private static Control View(DocumentEditorControl editor, int block) => Content(editor).ViewOf(editor.session.document.blocks[block]);

        // Each sheet's head and foot labels, page by page, in SlotPlace order.
        private static List<string[]> Margins(DocumentEditorControl editor)
        {
            DocumentControl content = Content(editor);
            return content.children.OfType<PanelControl>().Where(p => p.children.Count == 1 && p.children[0] is ContainerControl)
                .Select(p => ((Control)p.children[0]).children.OfType<LabelControl>().Select(l => l.text).ToArray()).ToList();
        }

        [A_XSDActionDependency("Tex.Lowering.Pages", "Test")]
        private static IEnumerator<int> LoweringPages(TestContext t)
        {
            const string source = """
                \documentclass{article}
                \pagestyle{headings}
                \begin{document}
                \section{Introduction}\label{sec:intro}
                Text on page \pageref{sec:intro}.
                \newpage
                \newpage
                After a break.
                \clearpage
                \thispagestyle{empty}After a clear.

                \markboth{Left}{Right}
                \subsection{Sub}
                Unknown \pageref{nowhere}.
                \end{document}
                """;
            XElement document = TexLowering.Compile(source, out List<TexError> errors);
            t.Check(errors.Count == 1 && errors[0].message.Contains("`nowhere' undefined"), $"only the missing label is reported: {string.Join("; ", errors.Select(e => e.message))}");

            List<XElement> blocks = TopBlocks(document);
            XElement heading = blocks.First(b => BlockText(b).Contains("Introduction"));
            t.Check((string?)heading.Attribute("MarkRight") == "1  INTRODUCTION" && heading.Attribute("MarkLeft") == null,
                $"a numbered section under headings sets \\rightmark to its uppercased number and title: {(string?)heading.Attribute("MarkRight")}");
            XElement after = blocks.First(b => BlockText(b).Contains("After a break"));
            XElement cleared = blocks.First(b => BlockText(b).Contains("After a clear"));
            t.Check((string?)after.Attribute("PageBreak") == "Page" && (string?)cleared.Attribute("PageBreak") == "Clear",
                "\\newpage and \\clearpage break before the next block");
            t.Check((string?)cleared.Attribute("PageStyle") == "empty", "\\thispagestyle goes on the next block");
            XElement sub = blocks.First(b => BlockText(b).Contains("Sub"));
            t.Check((string?)sub.Attribute("MarkLeft") == "Left" && (string?)sub.Attribute("MarkRight") == "Right",
                "\\markboth in vertical mode marks the next block; a subsection under headings sets none of its own");

            XElement page = document.Descendants("Page").Single();
            List<XElement> styles = page.Elements("PageStyle").ToList();
            XElement headings = styles.Single(s => (string?)s.Attribute("Name") == "headings");
            t.Check((string?)page.Attribute("Style") == "headings" && styles.Any(s => (string?)s.Attribute("Name") == "empty"),
                $"the document's style and every \\thispagestyle are written: {string.Join(", ", styles.Select(s => (string?)s.Attribute("Name")))}");
            string Slot(XElement style, string place) => (string?)style.Elements("Slot").FirstOrDefault(s => (string?)s.Attribute("Place") == place)?.Attribute("Text") ?? "";
            t.Check(Slot(headings, "HeadLeft") == "{rightmark}" && Slot(headings, "HeadRight") == "{page}"
                && (string?)headings.Elements("Slot").First().Attribute("Italic") == "true",
                $"headings puts the slanted \\rightmark left and the page right: {Slot(headings, "HeadLeft")} | {Slot(headings, "HeadRight")}");

            TexPageRefs refs = TexLowering.PageRefs(document);
            int introBlock = blocks.IndexOf(heading);
            t.Check(refs.refs.Count == 2 && refs.labels.TryGetValue("sec:intro", out (int block, int offset) site) && site.block == introBlock,
                $"each \\pageref is a run to fill and the label stands at its heading: {refs.refs.Count} refs, {string.Join(", ", refs.labels.Select(l => $"{l.Key}@{l.Value}"))}");

            const string fancy = """
                \documentclass{article}
                \usepackage{fancyhdr}
                \pagestyle{fancy}
                \fancyhf{}
                \fancyhead[LE,RO]{\thepage}
                \fancyhead[LO]{\textbf{Draft}}
                \fancyfoot[C]{\leftmark}
                \renewcommand{\headrulewidth}{0.8pt}
                \fancypagestyle{plain}{\fancyhf{}\rfoot{p. \thepage}}
                \begin{document}
                \maketitle
                \section{Methods}
                \subsection{Setup}
                Text.
                \end{document}
                """;
            document = TexLowering.Compile(fancy, out errors);
            t.Check(errors.Count == 0, $"the fancyhdr sample compiles clean: {string.Join("; ", errors.Select(e => e.message))}");
            page = document.Descendants("Page").Single();
            XElement fancyStyle = page.Elements("PageStyle").Single(s => (string?)s.Attribute("Name") == "fancy");
            XElement plain = page.Elements("PageStyle").Single(s => (string?)s.Attribute("Name") == "plain");
            t.Check(Slot(fancyStyle, "HeadRight") == "{page}" && Slot(fancyStyle, "HeadLeft") == "Draft"
                && (string?)fancyStyle.Elements("Slot").First(s => (string?)s.Attribute("Place") == "HeadLeft").Attribute("Bold") == "true"
                && Slot(fancyStyle, "FootCenter") == "{leftmark}" && fancyStyle.Elements("Slot").Count() == 3,
                $"\\fancyhf clears, odd-page entries land, E-only ones are dropped: {string.Join(" | ", fancyStyle.Elements("Slot").Select(s => $"{(string?)s.Attribute("Place")}={(string?)s.Attribute("Text")}"))}");
            t.Check(MathF.Abs(((float?)fancyStyle.Attribute("HeadRule") ?? 0f) - 0.8f * 96f / 72.27f) < 0.01f, "\\headrulewidth sets the fancy head rule");
            t.Check(Slot(plain, "FootRight") == "p. {page}" && plain.Elements("Slot").Count() == 1, "\\fancypagestyle redefines plain");
            blocks = TopBlocks(document);
            t.Check((string?)blocks[0].Attribute("PageStyle") == "plain", "\\maketitle takes plain for its page");
            XElement methods = blocks.First(b => BlockText(b).Contains("Methods"));
            XElement setup = blocks.First(b => BlockText(b).Contains("Setup"));
            t.Check((string?)methods.Attribute("MarkLeft") == "1  METHODS" && (string?)methods.Attribute("MarkRight") == ""
                && (string?)setup.Attribute("MarkRight") == "1.1  Setup" && setup.Attribute("MarkLeft") == null,
                $"fancy's section marks both, its subsection marks right: {(string?)methods.Attribute("MarkLeft")} / {(string?)setup.Attribute("MarkRight")}");
            yield break;
        }

        [A_XSDActionDependency("Tex.PageBreak", "Test")]
        private static IEnumerator<int> PageBreaks(TestContext t)
        {
            XElement document = new XElement("Document",
                new XElement("Block", new XElement("Run", new XAttribute("Text", "first"))),
                new XElement("Block", new XAttribute("PageBreak", "Page"), new XAttribute("MarkRight", "R"), new XAttribute("PageStyle", "empty"),
                    new XElement("Run", new XAttribute("Text", "second page"))),
                new XElement("Block", new XAttribute("PageBreak", "Clear"), new XElement("Run", new XAttribute("Text", "third page"))),
                new XElement("Table", new XAttribute("PageBreak", "Page"), new XElement("Column", new XAttribute("Width", "100")),
                    new XElement("Row", new XElement("Cell", new XElement("Block", new XElement("Run", new XAttribute("Text", "cell")))))));
            RichTextDocument parsed = DocumentXml.Parse(document);
            List<XElement> written = TopBlocks(DocumentXml.ToXml(parsed));
            t.Check((string?)written[1].Attribute("PageBreak") == "Page" && (string?)written[1].Attribute("MarkRight") == "R"
                && (string?)written[1].Attribute("PageStyle") == "empty" && (string?)written[2].Attribute("PageBreak") == "Clear"
                && (string?)written[3].Attribute("PageBreak") == "Page" && written[0].Attribute("PageBreak") == null,
                "PageBreak, PageStyle and the marks round-trip through .axml, tables' breaks too");

            DocumentEditorControl editor = ShowDocument(t, document);
            yield return 2;
            t.Check(editor.PageAt(0, 0) == 1 && editor.PageAt(1, 0) == 2 && editor.PageAt(2, 0) == 3 && editor.PageAt(3, 0) == 4,
                $"each break starts a new page: {editor.PageAt(0, 0)} {editor.PageAt(1, 0)} {editor.PageAt(2, 0)} {editor.PageAt(3, 0)}");
            List<BlockControl> blocks = Content(editor).children.OfType<BlockControl>().ToList();
            BlockSnapshot snapshot = blocks[1].Snapshot();
            BlockControl tail = blocks[1].SplitAt(6);
            t.Check(tail.pageBreak == PageBreak.None && tail.markRight == null && tail.pageStyle == null
                && blocks[1].pageBreak == PageBreak.Page && snapshot.pageBreak == PageBreak.Page && snapshot.markRight == "R" && snapshot.pageStyle == "empty",
                "a split leaves the break, style and marks with the head; a snapshot carries them");
            tail.Destroy();
            blocks[1].Restore(snapshot);
            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("Tex.Lowering.Footnote", "Test")]
        private static IEnumerator<int> LoweringFootnote(TestContext t)
        {
            const string source = """
                \documentclass{article}
                \begin{document}
                First\footnote{One note.} and second.\footnote{Two notes.}

                After.
                \end{document}
                """;
            XElement document = TexLowering.Compile(source, out List<TexError> errors);
            t.Check(errors.Count == 0, $"compiles clean: {string.Join("; ", errors.Select(e => e.message))}");
            List<XElement> top = document.Elements().Where(e => e.Name.LocalName is "Block" or "Footnote").ToList();
            t.Check(top.Count == 4 && top[0].Name.LocalName == "Block" && (string?)top[1].Attribute("Id") == "1" && (string?)top[2].Attribute("Id") == "2"
                && BlockText(top[3]).Contains("After"), $"each footnote follows the paragraph its mark is in: {string.Join(", ", top.Select(e => e.Name.LocalName))}");
            List<string?> anchors = top[0].Elements("Run").Select(r => (string?)r.Attribute("Note")).Where(n => n != null).ToList();
            t.Check(anchors.SequenceEqual(new[] { "1", "2" }), $"the marks in the text name their footnotes: {string.Join(",", anchors)}");
            XElement note = top[1].Elements("Block").Single();
            float small = 8f * 96f / 72.27f;
            t.Check(BlockText(note).Contains("One note.") && note.Elements("Run").Any(r => MathF.Abs(((float?)r.Attribute("FontSize") ?? 0f) - small) < 0.001f)
                && note.Elements("Run").All(r => r.Attribute("Note") == null), $"a footnote is set in \\footnotesize and its own mark anchors nothing: {note}");
            t.Check(MathF.Abs(((float?)document.Descendants("Page").Single().Attribute("FootnoteSkip") ?? 0f) - 9f * 25.4f / 72.27f) < 0.01f, "\\skip\\footins is the page's footnote skip");

            RichTextDocument parsed = DocumentXml.Parse(document);
            List<NoteBlock> blocks = parsed.blocks.OfType<NoteBlock>().ToList();
            t.Check(blocks.Count == 4 && blocks[0].insert == null && blocks[1].insert?.footnote == "1" && blocks[2].insert?.footnote == "2" && blocks[3].insert == null,
                "a <Footnote> reads as its blocks with one PageInsert");
            XElement written = DocumentXml.ToXml(parsed);
            t.Check(written.Elements().Count(e => e.Name.LocalName == "Footnote") == 2
                && written.Elements().First(e => e.Name.LocalName == "Block").Elements().Count(r => (string?)r.Attribute("Note") != null) == 2,
                "footnotes and their anchors round-trip through .axml");

            BlockSnapshot snapshot = blocks[1].Snapshot();
            NoteBlock tail = blocks[1].SplitAt(3);
            t.Check(tail.insert == blocks[1].insert && snapshot.insert == blocks[1].insert, "Enter in a footnote makes another block of it; a snapshot keeps it");
            yield break;
        }

        [A_XSDActionDependency("Tex.Footnotes", "Test")]
        private static IEnumerator<int> Footnotes(TestContext t)
        {
            string filler = string.Join(" ", Enumerable.Repeat("lorem ipsum dolor sit", 13));
            string tall = string.Join(" ", Enumerable.Repeat("note text here", 12));
            XElement Para(string text, string? anchor = null, string? pageBreak = null)
            {
                XElement block = new XElement("Block", new XElement("Run", new XAttribute("Text", text)));
                if (anchor != null) block.Add(new XElement("Run", new XAttribute("Text", "*"), new XAttribute("Note", anchor)));
                if (pageBreak != null) block.SetAttributeValue("PageBreak", pageBreak);
                return block;
            }
            XElement Note(string id, string text) => new XElement("Footnote", new XAttribute("Id", id), Para(text));

            XElement document = new XElement("Document",
                new XElement("DocumentLayout", new XAttribute("BlockSpacing", "0"),
                    new XElement("TextStyle", new XAttribute("Type", "Text"), new XAttribute("FontSize", "18")),
                    new XElement("Page", new XAttribute("Mode", "Paged"), new XAttribute("Size", "A6"))),
                Para("intro", "a"), Note("a", "note a"),
                Para("more", "b"), Note("b", "note b"),
                Para(filler, "c", "Page"), Note("c", tall),
                Note("z", "orphan"));
            DocumentEditorControl editor = ShowDocument(t, document);
            yield return 2;

            List<Control> blocks = Enumerable.Range(0, editor.session.document.blocks.Length).Select(i => View(editor, i)).ToList();
            BlockControl a = (BlockControl)blocks[1], b = (BlockControl)blocks[3], filled = (BlockControl)blocks[4], c = (BlockControl)blocks[5], z = (BlockControl)blocks[6];
            t.Check(editor.PageAt(1, 0) == 1 && editor.PageAt(3, 0) == 1, $"footnotes a and b sit on their anchors' page: {editor.PageAt(1, 0)} {editor.PageAt(3, 0)}");
            t.Check(b.arrangedRect.y >= a.arrangedRect.Bottom - 0.5f, "two footnotes on a page stack in anchor order");

            DocumentControl content = (DocumentControl)a.parent;
            PanelControl sheet = content.children.OfType<PanelControl>().First(p => p.children.Count == 1 && p.children[0] is ContainerControl);
            float textBottom = sheet.arrangedRect.Bottom - 25.4f * PageLayout.PxPerMm * content.zoom;
            t.Check(MathF.Abs(b.arrangedRect.Bottom - textBottom) < 1f && a.arrangedRect.y > ((BlockControl)blocks[2]).arrangedRect.Bottom + 10f,
                $"footnotes end at the foot of the text area, under a gap: b ends {b.arrangedRect.Bottom}, text area ends {textBottom}");
            Control rule = (Control)((Control)sheet.children[0]).children[8];
            t.Check(rule.arrangedRect.width > 0f && rule.arrangedRect.Bottom <= a.arrangedRect.y, $"a short rule sits above the footnotes: {rule.arrangedRect.width}");

            t.Check(filled.Lines.Count == 13 && editor.PageAt(4, 0) == 2 && editor.PageAt(4, filler.Length) == 3 && editor.PageAt(5, 0) == 3,
                $"a line whose footnote does not fit goes to the next page with it: {filled.Lines.Count} lines, first on {editor.PageAt(4, 0)}, anchor line {editor.PageAt(4, filler.Length)}, note {editor.PageAt(5, 0)}");
            t.Check(editor.PageAt(6, 0) == 3 && z.arrangedRect.y >= c.arrangedRect.Bottom - 0.5f, $"a footnote no anchor names goes at the foot of the last page: {editor.PageAt(6, 0)}");
            t.Check(filled.Lines.All(l => l.top + l.height + filled.arrangedRect.y <= c.arrangedRect.y || l.top + filled.arrangedRect.y >= c.arrangedRect.Bottom),
                "no line of text runs into a footnote");

            XElement huge = new XElement("Document",
                new XElement("DocumentLayout", new XAttribute("BlockSpacing", "0"),
                    new XElement("Page", new XAttribute("Mode", "Paged"), new XAttribute("Size", "A6"))),
                Para("before"), Para("anchor", "h"), Note("h", string.Join(" ", Enumerable.Repeat("note text here", 40))));
            editor = ShowDocument(t, huge);
            yield return 2;
            BlockControl anchor = (BlockControl)View(editor, 1);
            BlockControl over = (BlockControl)View(editor, 2);
            t.Check(over.arrangedRect.y >= anchor.arrangedRect.Bottom, $"a footnote taller than the page starts below its anchor's line: {over.arrangedRect.y} vs {anchor.arrangedRect.Bottom}");
            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("Tex.Lowering.Float", "Test")]
        private static IEnumerator<int> LoweringFloat(TestContext t)
        {
            const string source = """
                \documentclass[12pt]{article}
                \begin{document}
                Before.

                \vspace{10pt}
                \begin{figure}[!t]\centering x\caption{A}\end{figure}
                After, with \begin{table}[b]\caption{B}\end{table} a table inside.
                \end{document}
                """;
            XElement document = TexLowering.Compile(source, out List<TexError> errors);
            t.Check(errors.Count == 0, $"compiles clean: {string.Join("; ", errors.Select(e => e.message))}");
            List<XElement> top = document.Elements().Where(e => e.Name.LocalName is "Block" or "Float").ToList();
            t.Check(top.Select(e => e.Name.LocalName).SequenceEqual(new[] { "Block", "Float", "Block", "Float" }),
                $"a float stands where it was set, or after the paragraph it is in: {string.Join(", ", top.Select(e => e.Name.LocalName))}");
            t.Check((string?)top[1].Attribute("Kind") == "figure" && (string?)top[1].Attribute("Placement") == "!t"
                && (string?)top[3].Attribute("Kind") == "table" && (string?)top[3].Attribute("Placement") == "b", "<Float> carries the kind and placement");
            t.Check(top[1].Elements("Block").Count() == 2 && BlockText(top[1].Elements("Block").Last()).Contains("Figure 1:" + TextMeasurer.FactorSpace(2f) + "A"), "the float holds its body and caption");
            t.Check(top[2].Attribute("SpaceBefore") != null && top[1].Elements("Block").First().Attribute("SpaceBefore") == null,
                "the space above a float stays with the next block in the text");
            XElement page = document.Descendants("Page").Single();
            float Mm(string name) => (float?)page.Attribute(name) ?? 0f;
            const float mmPerPt = 25.4f / 72.27f;
            t.Check(MathF.Abs(Mm("FloatSep") - 14f * mmPerPt) < 0.01f && MathF.Abs(Mm("TextFloatSep") - 20f * mmPerPt) < 0.01f && MathF.Abs(Mm("InTextSep") - 14f * mmPerPt) < 0.01f,
                $"the class size sets the float separations: {Mm("FloatSep")} {Mm("TextFloatSep")} {Mm("InTextSep")}");

            RichTextDocument parsed = DocumentXml.Parse(document);
            NoteNode[] blocks = parsed.blocks;
            PageInsert? figure = (blocks[1] as NoteBlock)?.insert;
            t.Check(figure is { floatKind: "figure", placement: "!t" } && (blocks[2] as NoteBlock)?.insert == figure && (blocks[3] as NoteBlock)?.insert == null,
                "a <Float> reads as its blocks with one PageInsert");
            XElement written = DocumentXml.ToXml(parsed);
            t.Check(written.Elements().Count(e => e.Name.LocalName == "Float") == 2, "floats round-trip through .axml");
            yield break;
        }

        [A_XSDActionDependency("Tex.FloatPlacement", "Test")]
        private static IEnumerator<int> FloatPlacement(TestContext t)
        {
            string filler = string.Join(" ", Enumerable.Repeat("lorem ipsum dolor sit", 40));
            XElement Para(string text, string? pageBreak = null, string? anchor = null)
            {
                XElement block = new XElement("Block", new XElement("Run", new XAttribute("Text", text)));
                if (anchor != null) block.Add(new XElement("Run", new XAttribute("Text", "*"), new XAttribute("Note", anchor)));
                if (pageBreak != null) block.SetAttributeValue("PageBreak", pageBreak);
                return block;
            }
            XElement Float(string placement, float tall = 0f, string kind = "figure") => new XElement("Float", new XAttribute("Kind", kind), new XAttribute("Placement", placement),
                Para("float " + placement), new XElement("Block", new XAttribute("SpaceBefore", tall), new XElement("Run", new XAttribute("Text", "end"))));
            XElement Document(string mode, params XElement[] content) => new XElement("Document",
                new XElement("DocumentLayout", new XAttribute("BlockSpacing", "0"),
                    new XElement("Page", new XAttribute("Mode", mode), new XAttribute("Size", "A6"))), content);
            LayoutRect Rect(DocumentEditorControl e, int block) => View(e, block).arrangedRect;
            string Span(DocumentEditorControl e, int block) => $"{Rect(e, block).y:0.#}..{Rect(e, block).Bottom:0.#}";

            DocumentEditorControl editor = ShowDocument(t, Document("Paged", Para("a"), Float("ht"), Para("b")));
            yield return 2;
            t.Check(Rect(editor, 1).y > Rect(editor, 0).Bottom && Rect(editor, 3).y > Rect(editor, 2).Bottom && editor.PageAt(1, 0) == 1,
                "h: a float that fits is set in the text where it stands");

            editor = ShowDocument(t, Document("Paged", Para("a"), Para("b"), Float("tbp"), Para("c")));
            yield return 2;
            t.Check(editor.PageAt(2, 0) == 1 && Rect(editor, 3).Bottom < Rect(editor, 0).y && Rect(editor, 4).y >= Rect(editor, 1).Bottom - 0.5f,
                $"t: a float met mid-page goes to that page's top and the text above it moves down: float {Span(editor, 2)} {Span(editor, 3)}, a {Span(editor, 0)}, b {Span(editor, 1)}, c {Span(editor, 4)}");
            Control? header = Content(editor).header;
            t.Check(header != null && Rect(editor, 2).y >= header.arrangedRect.Bottom - 0.5f,
                $"a top float on the first page sits under the note's header: float {Rect(editor, 2).y}, header ends {header?.arrangedRect.Bottom}");

            editor = ShowDocument(t, Document("Paged", Para("a", anchor: "n"), new XElement("Footnote", new XAttribute("Id", "n"), Para("note")), Float("b"), Para("c")));
            yield return 2;
            DocumentControl content = Content(editor);
            PanelControl sheet = content.children.OfType<PanelControl>().First(p => p.children.Count == 1 && p.children[0] is ContainerControl);
            float textBottom = sheet.arrangedRect.Bottom - 25.4f * PageLayout.PxPerMm * content.zoom;
            t.Check(editor.PageAt(2, 0) == 1 && MathF.Abs(Rect(editor, 3).Bottom - textBottom) < 1f && Rect(editor, 1).Bottom <= Rect(editor, 2).y && Rect(editor, 4).Bottom < Rect(editor, 1).y,
                $"b: a float goes to the foot of the page, under the footnotes: float ends {Rect(editor, 3).Bottom}, text area {textBottom}, note ends {Rect(editor, 1).Bottom}");

            float height = sheet.arrangedRect.height / content.zoom - 2f * 25.4f * PageLayout.PxPerMm;
            editor = ShowDocument(t, Document("Paged", Para("a"), Float("tbp", 0.8f * height), Float("t", 10f), Float("t", 10f, "table"), Para(filler), Para(filler)));
            yield return 2;
            t.Check(editor.PageAt(1, 0) == 2 && editor.PageAt(3, 0) == 3, $"a float too tall for a text page waits for a page of floats; the next figure may not pass it: {editor.PageAt(1, 0)} {editor.PageAt(3, 0)}");
            t.Check(editor.PageAt(5, 0) == 1 && Rect(editor, 6).Bottom < Rect(editor, 0).y, $"a table is not held back by a waiting figure: page {editor.PageAt(5, 0)}");
            BlockControl text = (BlockControl)View(editor, 7);
            t.Check(editor.PageAt(8, 0) > 2 && Enumerable.Range(0, text.Length).All(i => editor.PageAt(7, i) != 2), "no text goes on a page of floats");

            editor = ShowDocument(t, Document("Paged", Para("a"), Float("p", 10f), Para("b", "Clear"), Para("c")));
            yield return 2;
            t.Check(editor.PageAt(1, 0) == 2 && editor.PageAt(3, 0) == 3, $"\\clearpage puts out the waiting floats before it breaks: float {editor.PageAt(1, 0)}, b {editor.PageAt(3, 0)}");

            editor = ShowDocument(t, Document("Paged", Para("a"), Float("p", 10f), Para("b")));
            yield return 2;
            t.Check(editor.PageAt(1, 0) == 2 && editor.PageAt(3, 0) == 1, $"the document's end puts out what still waits: float {editor.PageAt(1, 0)}");

            editor = ShowDocument(t, Document("Paged", Para("a"), Float("H", 10f), Para("b")));
            yield return 2;
            t.Check(Rect(editor, 1).y > Rect(editor, 0).Bottom && Rect(editor, 3).y > Rect(editor, 2).Bottom, "H: set here, always");

            editor = ShowDocument(t, Document("Paged", Para("a"), Float("!t", 0.6f * height), Para("b")));
            yield return 2;
            t.Check(editor.PageAt(1, 0) == 1 && Rect(editor, 2).Bottom < Rect(editor, 0).y, $"!: a float past \\topfraction still goes at the top: page {editor.PageAt(1, 0)}, float {Span(editor, 1)} {Span(editor, 2)}, a {Span(editor, 0)}, height {height}");
            editor = ShowDocument(t, Document("Paged", Para("a"), Float("t", 0.6f * height), Para("b")));
            yield return 2;
            t.Check(editor.PageAt(1, 0) == 2, $"without !, \\topfraction holds it back: page {editor.PageAt(1, 0)}");

            editor = ShowDocument(t, Document("Pageless", Para("a"), Float("tp", 10f), Para("b")));
            yield return 2;
            t.Check(Rect(editor, 1).y > Rect(editor, 0).Bottom && Rect(editor, 3).y > Rect(editor, 2).Bottom, "pageless: every float is set where it stands");
            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("Tex.RunningHeads", "Test")]
        private static IEnumerator<int> RunningHeads(TestContext t)
        {
            string words = string.Join(" ", Enumerable.Repeat("lorem ipsum dolor", 4));
            XElement Para(string text) => new XElement("Block", new XElement("Run", new XAttribute("Text", text)));
            XElement Heading(string text, string? left, string? right)
            {
                XElement block = Para(text);
                if (left != null) block.SetAttributeValue("MarkLeft", left);
                if (right != null) block.SetAttributeValue("MarkRight", right);
                return block;
            }
            XElement Slot(string place, string text, bool italic = false) => new XElement("Slot", new XAttribute("Place", place), new XAttribute("Text", text),
                new XAttribute("FontSize", "12"), new XAttribute("Italic", italic ? "true" : "false"));

            XElement document = new XElement("Document",
                new XElement("DocumentLayout",
                    new XElement("Page", new XAttribute("Mode", "Paged"), new XAttribute("Size", "A6"), new XAttribute("Style", "heads"),
                        new XElement("PageStyle", new XAttribute("Name", "heads"), new XAttribute("HeadRule", "1"),
                            Slot("HeadLeft", "{leftmark} / {rightmark}", true), Slot("HeadRight", "{page}")),
                        new XElement("PageStyle", new XAttribute("Name", "plain"), Slot("FootCenter", "- {page} -")))),
                new XElement("Block", new XAttribute("PageStyle", "plain"), new XElement("Run", new XAttribute("Text", "Title"))),
                Heading("One", "ONE", ""),
                Para(words),
                Heading("Sub A", null, "A"),
                Para(words),
                new XElement("Block", new XAttribute("PageBreak", "Page"), new XElement("Run", new XAttribute("Text", "no marks here"))),
                new XElement("Block", new XAttribute("PageBreak", "Page"), new XElement("Run", new XAttribute("Text", "two marks"))),
                Heading("Sub B", null, "B"),
                Heading("Two", "TWO", ""));
            RichTextDocument parsed = DocumentXml.Parse(document);
            XElement page = DocumentXml.ToXml(parsed).Descendants().Single(e => e.Name.LocalName == "Page");
            t.Check(page.Elements().Count(e => e.Name.LocalName == "PageStyle") == 2 && (string?)page.Attribute("Style") == "heads"
                && page.Elements().First().Elements().Count() == 2, "page styles and their slots round-trip through .axml");

            DocumentEditorControl editor = ShowDocument(t, document);
            yield return 2;
            List<string[]> margins = Margins(editor);
            t.Check(margins.Count >= 3, $"three pages: {margins.Count}");
            if (margins.Count < 3)
            {
                t.Show(new StackPanelControl());
                yield break;
            }
            t.Check(margins[0][0] == "" && margins[0][2] == "" && margins[0][4] == "- 1 -", $"page 1 takes its block's plain: {string.Join("|", margins[0])}");
            t.Check(margins[1][0] == "ONE / A" && margins[1][2] == "2", $"page 2 carries the last marks over: {string.Join("|", margins[1])}");
            t.Check(margins[2][0] == "TWO / B" && margins[2][2] == "3",
                $"page 3: \\leftmark is the last mark's left, \\rightmark the first mark's right: {string.Join("|", margins[2])}");
            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("Tex.PageOfRounding", "Test")]
        private static IEnumerator<int> PageOfRounding(TestContext t)
        {
            PageBands bands = new PageBands(26.8728485f, 1100f, 1293.20361f);
            float next = bands.top + 9 * bands.stride + bands.stride;
            t.Check(bands.PageOf(next) == 10, $"page 9's top plus a stride falls on page 10: {bands.PageOf(next)}");
            float y = new PageSpace(0f).Push(bands, bands.top + 9 * bands.stride + 1000f, 200f);
            t.Check(bands.PageOf(y) == 10, $"a line past page 9's foot moves to page 10: {bands.PageOf(y)}");
            yield break;
        }

        // count one-line blocks 10 px apart on A6 pages, each gap shrinking by shrink px
        private static XElement ShrinkItems(int count, float shrink)
        {
            XElement document = new XElement("Document",
                new XElement("DocumentLayout", new XAttribute("BlockSpacing", "0"),
                    new XElement("TextStyle", new XAttribute("Type", "Text"), new XAttribute("FontSize", "18")),
                    new XElement("Page", new XAttribute("Mode", "Paged"), new XAttribute("Size", "A6"))));
            for (int i = 0; i < count; i++)
            {
                XElement block = new XElement("Block", new XAttribute("SpaceBefore", "10"), new XElement("Run", new XAttribute("Text", "item " + i)));
                if (shrink > 0f) block.SetAttributeValue("ShrinkBefore", shrink);
                document.Add(block);
            }
            return document;
        }

        // How many blocks the first page holds.
        private static int FirstPageCount(DocumentEditorControl editor)
        {
            int count = 0;
            while (count < editor.session.document.blocks.Length && editor.PageAt(count, 0) == 1) count++;
            return count;
        }

        [A_XSDActionDependency("Tex.PageShrink", "Test")]
        private static IEnumerator<int> PageShrink(TestContext t)
        {
            DocumentEditorControl editor = ShowDocument(t, ShrinkItems(40, 0f));
            yield return 2;
            int natural = FirstPageCount(editor);
            float naturalGap = View(editor, 1).arrangedRect.y - View(editor, 0).arrangedRect.Bottom;

            editor = ShowDocument(t, ShrinkItems(40, 9f));
            yield return 2;
            int shrunk = FirstPageCount(editor);
            t.Check(natural > 2 && shrunk == natural + 1, $"shrinking the gaps takes one more block onto the first page: {natural} -> {shrunk}");

            float first = View(editor, 1).arrangedRect.y - View(editor, 0).arrangedRect.Bottom;
            bool even = true;
            for (int i = 2; i < shrunk; i++)
                even &= MathF.Abs(View(editor, i).arrangedRect.y - View(editor, i - 1).arrangedRect.Bottom - first) < 0.05f;
            t.Check(even && first < naturalGap - 0.1f, $"every gap on the page shrinks by the same amount: {first:0.00} vs natural {naturalGap:0.00}");
            float next = View(editor, shrunk + 1).arrangedRect.y - View(editor, shrunk).arrangedRect.Bottom;
            t.Check(MathF.Abs(next - naturalGap) < 0.05f, $"the next page, not overfull, keeps its gaps natural: {next:0.00}");
            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("Tex.PageShrinkRigid", "Test")]
        private static IEnumerator<int> PageShrinkRigid(TestContext t)
        {
            DocumentEditorControl editor = ShowDocument(t, ShrinkItems(5, 0f));
            yield return 2;
            float[] tops = new float[5];
            for (int i = 0; i < tops.Length; i++)
                tops[i] = View(editor, i).arrangedRect.y;

            editor = ShowDocument(t, ShrinkItems(5, 9f));
            yield return 2;
            bool same = true;
            for (int i = 0; i < tops.Length; i++)
                same &= MathF.Abs(View(editor, i).arrangedRect.y - tops[i]) < 0.01f;
            t.Check(same, "a page that fits keeps its gaps natural, shrink or not");
            t.Show(new StackPanelControl());
        }
        #endregion

        #region ---- lengths, parameters, packages ----
        private static bool Near(float a, float b, float within = 0.02f) => MathF.Abs(a - b) <= within;

        private static float Pt(int sp) => sp / 65536f;

        [A_XSDActionDependency("Tex.Lengths", "Test")]
        private static IEnumerator<int> Lengths(TestContext t)
        {
            string gap = Run(@"\newlength{\gap}\setlength{\gap}{3pt}\addtolength{\gap}{2pt}\the\gap%", out List<TexError> gapErrors).Replace("\\relax ", "");
            t.Check(gap == "5.0pt", $"\\newlength, \\setlength, \\addtolength: {gap} {string.Join("; ", gapErrors.Select(e => e.message))}");
            string indent = Lines(@"\setlength{\parindent}{4pt}\the\parindent");
            t.Check(indent == "4.0pt", $"\\parindent is a length: {indent}");

            t.Check(Typeset(@"\documentclass[11pt]{article}\begin{document}x\end{document}", out _).parIndent == 17f, "the class sets \\parindent: 17pt at 11pt");
            TexTypesetter flush = Typeset(@"\documentclass{article}\setlength{\parindent}{0pt}\begin{document}One" + "\n\nTwo\\end{document}", out _);
            t.Check(flush.parIndent == 0f, "\\setlength{\\parindent}{0pt} in the preamble");

            TexTypesetter spaced = Typeset(@"\documentclass{article}\setlength{\parskip}{6pt}\begin{document}One" + "\n\nTwo\\end{document}", out _);
            List<TexVGlue> glue = spaced.vlist.SkipWhile(n => n is not TexParagraph).OfType<TexVGlue>().ToList();
            t.Check(glue.Any(g => Pt(g.glue.width) == 6f), $"\\parskip above the second paragraph: {string.Join(",", glue.Select(g => Pt(g.glue.width)))}");
            yield break;
        }

        [A_XSDActionDependency("Tex.Parameters", "Test")]
        private static IEnumerator<int> Parameters(TestContext t)
        {
            const string preamble = "\\documentclass{article}\n\\hyphenpenalty=10000\n\\exhyphenpenalty=10000\n\\tolerance=9999\n\\emergencystretch=3em\n";
            TexTypesetter set = Typeset(preamble + "\\begin{document}Introduction\\end{document}", out List<TexError> errors);
            t.Check(errors.Count == 0, $"TeX's parameters are registers: {string.Join("; ", errors.Select(e => e.message))}");
            t.Check(!set.hyphenate && Typeset("x", out _).hyphenate, "\\hyphenpenalty=10000 turns hyphenation off; it is on by default");
            t.Check(Lines(preamble + "\\begin{document}Body\\end{document}") == "Body", "assignments typeset nothing");

            XElement lowered = TexLowering.Compile(preamble + "\\begin{document}Introduction to organisation\\end{document}", out _);
            t.Check(!lowered.Descendants("Run").Any(r => ((string?)r.Attribute("Text"))?.Contains('\u00AD') == true), "no soft hyphens are lowered");
            yield break;
        }

        [A_XSDActionDependency("Tex.DimExpr", "Test")]
        private static IEnumerator<int> DimExpr(TestContext t)
        {
            t.Check(Run(@"\dimen0=\dimexpr 1pt+2pt*3\relax\the\dimen0%") == "7.0pt", "\\dimexpr: * before +");
            t.Check(Run(@"\dimen0=\dimexpr (1pt+2pt)/2\relax\the\dimen0%") == "1.5pt", "\\dimexpr: parentheses and /");
            t.Check(Run(@"\dimen0=\dimexpr\pagetotal+1pt\relax\the\dimen0%") == "1.0pt", "\\pagetotal reads as 0pt");

            TexTypesetter fit = Typeset("\\documentclass{article}\\begin{document}x\n\\pdfpageheight=\\dimexpr\\pagetotal+0.8in+12pt\\relax\n\\end{document}", out _);
            t.Check(fit.fitHeight is float extra && Near(extra, 20.32f + 12f * 25.4f / 72.27f, 0.05f) && fit.customHeight == null,
                $"a \\pagetotal in \\pdfpageheight cuts the last page: {fit.fitHeight}");
            TexTypesetter tall = Typeset("\\documentclass{article}\\pdfpageheight=100in\\begin{document}x\\end{document}", out _);
            t.Check(tall.customHeight is float h && Near(h, 2540f, 0.1f) && tall.fitHeight == null, $"\\pdfpageheight sets the paper: {tall.customHeight}");
            TexTypesetter geometry = Typeset("\\documentclass{article}\\usepackage[paperwidth=8.5in, paperheight=100in]{geometry}\\begin{document}x\\end{document}", out _);
            t.Check(geometry.customWidth is float w && Near(w, 215.9f, 0.1f) && geometry.customHeight is float gh && Near(gh, 2540f, 0.1f),
                "geometry's paperwidth and paperheight");

            XElement page = TexLowering.Compile("\\documentclass{article}\\usepackage[paperheight=100in]{geometry}\\begin{document}x\n"
                + "\\pdfpageheight=\\dimexpr\\pagetotal+1in\\relax\n\\end{document}", out _).Element("DocumentLayout")!.Element("Page")!;
            t.Check((string?)page.Attribute("Size") == "Custom" && Near((float)page.Attribute("Height")!, 2540f, 0.1f)
                && Near((float)page.Attribute("FitHeight")!, 25.4f, 0.05f), $"lowered as a custom, fitted page: {page}");
            yield break;
        }

        [A_XSDActionDependency("Tex.Preamble", "Test")]
        private static IEnumerator<int> Preamble(TestContext t)
        {
            string clean = Lines("\\documentclass{article}\n\\usepackage{xcolor,hyperref}\n\\definecolor{ink}{HTML}{112233}\n"
                + "\\hypersetup{colorlinks=false, hidelinks}\n\\begin{document}{\\color{ink}Body}\\end{document}", out List<TexError> errors);
            t.Check(clean == "Body" && errors.Count == 0, $"a preamble of commands typesets nothing: {clean} {string.Join("; ", errors.Select(e => e.message))}");

            TexTypesetter colored = Typeset("\\documentclass{article}\\definecolor{ink}{HTML}{112233}\\begin{document}\\textcolor{ink}{x}\\end{document}", out _);
            t.Check(Pars(colored).SelectMany(p => p.list).OfType<TexChar>().FirstOrDefault()?.style.color == "#112233", "\\definecolor names a colour");

            string junk = Lines("\\documentclass{article}\nstray words\n\\begin{document}Body\\end{document}", out errors);
            t.Check(junk == "Body", $"text in the preamble is dropped: {junk}");
            t.Check(errors.Count(e => e.message.Contains("Missing \\begin{document}")) == 1, "and reported once");
            t.Check(Lines("No class at all.") == "No class at all.", "a fragment without \\documentclass still typesets");
            t.Check(Lines(@"\textbullet\space x \uline{under}") == "• x under", "\\space and \\uline");
            yield break;
        }

        [A_XSDActionDependency("Tex.Enumitem", "Test")]
        private static IEnumerator<int> Enumitem(TestContext t)
        {
            const string source = "\\documentclass{article}\\usepackage{enumitem}\n\\newlist{tight}{itemize}{1}\n"
                + "\\setlist[tight]{label=\\raisebox{0.45ex}{\\rule{2.8pt}{2.8pt}}, leftmargin=12pt, itemsep=0pt, parsep=0pt, topsep=1pt}\n"
                + "\\begin{document}\nText\n\\begin{tight}\n\\item A\n\\item B\n\\end{tight}\n\\begin{itemize}[label=$\\circ$]\n\\item C\n\\end{itemize}\n\\end{document}\n";
            TexTypesetter typeset = Typeset(source, out List<TexError> errors);
            t.Check(errors.Count == 0, $"clean: {string.Join("; ", errors.Select(e => e.message))}");
            List<TexParagraph> pars = Pars(typeset);
            t.Check(pars.Count == 4 && string.Join("/", pars.Select(Line)) == "Text/A/B/C", $"items: {string.Join("/", pars.Select(Line))}");
            if (pars.Count != 4) yield break;
            t.Check(pars[1].style.list == TexListKind.Itemize && pars[1].style.marker == "Square" && Pt(pars[1].style.leftMargin) == 12f,
                "a \\newlist itemize with a \\rule label and its leftmargin");
            t.Check(pars[3].style.marker == "Circle" && pars[3].style.leftMargin == 0, "an itemize's own [label=...]");

            List<TexNode> v = typeset.vlist;
            int a = v.IndexOf(pars[1]), b = v.IndexOf(pars[2]);
            float between = v.Skip(a + 1).Take(b - a - 1).OfType<TexVGlue>().Sum(g => Pt(g.glue.width));
            float above = v.Skip(v.IndexOf(pars[0]) + 1).Take(a - v.IndexOf(pars[0]) - 1).OfType<TexVGlue>().Max(g => Pt(g.glue.width));
            t.Check(between == 0f && above == 1f, $"itemsep + parsep 0 between items ({between}), topsep 1pt above ({above})");

            XElement[] blocks = TexLowering.Compile(source, out _).Elements("Block").ToArray();
            t.Check(blocks.Length == 4 && (string?)blocks[1].Attribute("Marker") == "Square" && Near((float)blocks[1].Attribute("ListIndent")!, 12f * 96f / 72.27f)
                && blocks[3].Attribute("ListIndent") == null, $"lowered marker and ListIndent: {string.Concat(blocks.Select(x => x.ToString(SaveOptions.DisableFormatting)))}");
            yield break;
        }

        [A_XSDActionDependency("Tex.Titlesec", "Test")]
        private static IEnumerator<int> Titlesec(TestContext t)
        {
            const string source = "\\documentclass[11pt]{article}\\usepackage{titlesec}\n"
                + "\\titleformat{\\section}{\\large\\bfseries}{}{0pt}{}[\\vspace{-7pt}\\rule{\\textwidth}{1.5pt}]\n"
                + "\\titlespacing*{\\section}{0pt}{8pt}{2pt}\n"
                + "\\titleformat{\\subsection}{\\bfseries}{\\thesubsection}{1em}{}\n"
                + "\\setlength{\\parindent}{0pt}\n"
                + "\\begin{document}\n\\section*{WORK}\nText\n\\section{Two}\\subsection{Sub}\n\\end{document}\n";
            TexTypesetter typeset = Typeset(source, out List<TexError> errors);
            t.Check(errors.Count == 0, $"clean: {string.Join("; ", errors.Select(e => e.message))}");
            List<TexParagraph> pars = Pars(typeset);
            TexParagraph work = pars[0];
            TexChar first = work.list.OfType<TexChar>().First();
            t.Check(work.style.kind == TexParKind.Heading && first.style.font.bold && Pt(first.style.font.size) == 12f && Line(work) == "WORK",
                $"the format sets the heading: {Line(work)} {Pt(first.style.font.size)}pt");

            List<TexNode> v = typeset.vlist;
            int at = v.IndexOf(work);
            t.Check(v[at + 1] is TexVGlue { glue.width: var up } && Pt(up) == -7f, "the after-code's \\vspace follows the heading");
            t.Check(v[at + 2] is TexParagraph rule && rule.list.OfType<TexRuleBox>().SingleOrDefault() is TexRuleBox box && Pt(box.height) == 1.5f && !rule.style.indent,
                "then its \\rule, unindented");
            t.Check(v[at + 3] is TexVGlue { glue.width: var down } && Pt(down) == 2f, "then \\titlespacing's after-skip");
            t.Check(v.Take(at).OfType<TexVGlue>().LastOrDefault() is TexVGlue before && Pt(before.glue.width) == 8f, "and its before-skip above");
            t.Check(pars.Any(p => Line(p) == "Two") && pars.Any(p => Line(p) == "1.1 Sub"),
                $"an empty label shows no number; \\thesubsection is the full number: {string.Join(" / ", pars.Select(Line))}");

            XElement[] blocks = TexLowering.Compile(source, out _).Elements("Block").ToArray();
            XElement? ruled = blocks.FirstOrDefault(x => (string?)x.Attribute("StylingType") == "Rule");
            t.Check(ruled != null && Near((float)ruled.Attribute("Weight")!, 1.5f * 96f / 72.27f) && Near((float)ruled.Attribute("SpaceBefore")!, -7f * 96f / 72.27f),
                $"a paragraph of one \\rule lowers to a weighted Rule under a negative space: {ruled}");
            yield break;
        }

        [A_XSDActionDependency("Tex.Lowering.Fill", "Test")]
        private static IEnumerator<int> LoweringFill(TestContext t)
        {
            XElement document = TexLowering.Compile("\\documentclass{article}\\begin{document}\\noindent Left \\hfill Right\\end{document}", out _);
            XElement block = document.Elements("Block").First();
            XElement[] runs = block.Elements("Run").ToArray();
            t.Check(runs.Length == 3 && (string?)runs[1].Attribute("Fill") == "true" && (string?)runs[2].Attribute("Text") == "Right",
                $"\\hfill lowers to a fill run: {block.ToString(SaveOptions.DisableFormatting)}");

            DocumentEditorControl editor = ShowDocument(t, document);
            yield return 2;
            if (View(editor, 0) is BlockControl shown && shown.Lines is { Count: 1 } lines)
                t.Check(Near(lines[0].left + lines[0].width, shown.arrange.arranged.width - shown.padding.left - shown.padding.right, 1f),
                    $"the line reaches the right edge: {lines[0].left + lines[0].width} of {shown.arrange.arranged.width}");
            else t.Check(false, "one line");
            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("Tex.CenteredOverfull", "Test")]
        private static IEnumerator<int> CenteredOverfull(TestContext t)
        {
            List<string> over = new List<string>();
            for (int pt = 150; pt <= 300; pt += 3)
            {
                XElement document = TexLowering.Compile("\\documentclass{article}\\begin{document}\\begin{center}{\\small Start\\hspace{" + pt
                    + "pt}Example \\,|\\, words here \\,|\\, github.com/NexerG \\,|\\, more words to wrap \\,|\\, end}\\end{center}\\end{document}", out _);
                DocumentEditorControl editor = ShowDocument(t, document);
                yield return 2;
                if (View(editor, 0) is not BlockControl block) continue;
                DrawRecorder recorder = new DrawRecorder();
                UIEngine.recorder = recorder;
                try { UIEngine.Collect(block, Control.rootDepth); }
                finally { UIEngine.recorder = null; }
                float right = block.arrange.arranged.Right - block.padding.right;
                float ink = recorder.items.Where(g => g.kind == DrawKind.Glyph && g.character is not (' ' or ' ') && g.character < AtlasMetaData.AdvanceTableSize)
                    .Select(g => g.x + g.font!.atlasMetaData.TableAdvance(g.character, g.face) * g.size).DefaultIfEmpty(0f).Max();
                if (ink > right + 0.5f) over.Add($"{pt}pt: ink to {ink:0.0} > {right:0.0}");
            }
            t.Check(over.Count == 0, $"a centred line never runs past its room: {over.Count} overfull, {string.Join(" ;; ", over.Take(4))}");
            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("Tex.Pdf.Bullets", "Test")]
        private static IEnumerator<int> PdfBullets(TestContext t)
        {
            XElement document = TexLowering.Compile("\\documentclass{article}\\usepackage{enumitem}\\begin{document}\n"
                + "\\begin{itemize}\\item One\\end{itemize}\n\\begin{itemize}[label=\\rule{1pt}{1pt}]\\item Two\\end{itemize}\n\\end{document}\n", out _);
            string path = Path.Combine(Path.GetTempPath(), $"aurora-bullets-{Guid.NewGuid():N}.pdf");
            t.Check(PdfExport.Export(document, path, "bullets"), "exports");
            string pdf = Encoding.Latin1.GetString(File.ReadAllBytes(path));
            File.Delete(path);

            StringBuilder content = new StringBuilder();
            foreach (Match m in Regex.Matches(pdf, @"/FlateDecode[^>]*>>\nstream\n"))
            {
                int start = m.Index + m.Length;
                int end = pdf.IndexOf("\nendstream", start, StringComparison.Ordinal);
                using MemoryStream inflated = new MemoryStream();
                using (System.IO.Compression.ZLibStream zlib = new System.IO.Compression.ZLibStream(
                           new MemoryStream(Encoding.Latin1.GetBytes(pdf.Substring(start, end - start))), System.IO.Compression.CompressionMode.Decompress))
                    zlib.CopyTo(inflated);
                content.Append(Encoding.Latin1.GetString(inflated.ToArray()));
            }
            string drawn = content.ToString();
            t.Check(Regex.Matches(drawn, @" c h f\n").Count == 1, "the itemize disc is a filled curve");
            t.Check(Regex.Matches(drawn, @" l h f\n").Count == 1, "the \\rule label's square is a filled polygon");
            yield break;
        }

        [A_XSDActionDependency("Tex.FitPage", "Test")]
        private static IEnumerator<int> FitPage(TestContext t)
        {
            XElement document = TexLowering.Compile("\\documentclass{article}\\usepackage[paperheight=100in, top=1in]{geometry}\\pagestyle{empty}\n"
                + "\\begin{document}\nOne line.\n\\pdfpageheight=\\dimexpr\\pagetotal+1in\\relax\n\\end{document}\n", out _);
            DocumentEditorControl editor = ShowDocument(t, document);
            yield return 2;
            List<LayoutRect> pages = Content(editor).PageRects().ToList();
            float px = 96f * Content(editor).zoom;
            t.Check(pages.Count == 1 && pages[0].height > 1.1f * px && pages[0].height < 1.6f * px,
                $"one page cut to \\pagetotal (one line below the 1in margin) plus 1in: {string.Join(",", pages.Select(p => p.height / px))} in");
            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("Tex.Input", "Test")]
        private static IEnumerator<int> Input(TestContext t)
        {
            string folder = Folder(
                ("part.tex", "\\begin{titlepage}\n\\makeatletter\n\\centerline{\\fontsize{14pt}{16pt}\\selectfont\\MakeUppercase{\\@who\\space u}}\n"
                    + "\\@ifundefined{@nobody}{none}{some} \\the\\year{}\n\\makeatother\n\\verb|@v|\n\\end{titlepage}\n"),
                ("bad.tex", "x\n\\bogus\n"),
                ("chapter.tex", "Chapter text.\n"));
            TexTypesetter Run(string source)
            {
                TexTypesetter typesetter = new TexTypesetter(source, null, folder);
                typesetter.Run();
                return typesetter;
            }

            TexTypesetter title = Run("\\documentclass{article}\\makeatletter\\def\\@who{gytė}\\makeatother\n\\begin{document}\n\\input{part}After\n\\end{document}\n");
            List<TexParagraph> pars = Pars(title);
            string[] lines = pars.Select(p => System.Text.RegularExpressions.Regex.Replace(Line(p), " {2,}", " ").Trim()).ToArray();
            t.Check(lines.Length == 3 && lines[0] == "GYTĖ U" && lines[1] == $"none {DateTime.Today.Year} @v" && lines[2] == "After",
                $"the input file's \\makeatletter, \\verb, \\MakeUppercase, \\@ifundefined and \\year: {string.Join(" / ", lines)}");
            t.Check(pars[0].style.align == TexAlign.Center && pars[0].list.OfType<TexChar>().First().style.font.size == 14 * 65536,
                "\\centerline centres and \\fontsize with \\selectfont sets 14pt");
            t.Check(title.vlist.OfType<TexPageBreak>().Count() >= 2 && title.errors.Count == 0,
                $"the titlepage is a page of its own, no errors: {string.Join("; ", title.errors)}");

            TexTypesetter bad = Run("a\n\\input{bad}\n\\input{nope}\n");
            t.Check(Has(bad.errors, "Undefined control sequence \\bogus", 2), $"an error in an input file is reported at its \\input line: {string.Join("; ", bad.errors)}");
            t.Check(Has(bad.errors, "File `nope.tex' not found", 3), "a missing file");

            TexTypesetter included = Run("Before\\include{chapter}After");
            t.Check(included.vlist.OfType<TexPageBreak>().Count() == 2 && Pars(included).Select(Line).Any(l => l.StartsWith("Chapter text.")),
                "\\include clears the page around the file");
            Directory.Delete(folder, true);
            yield break;
        }

        [A_XSDActionDependency("Tex.LocalClass", "Test")]
        private static IEnumerator<int> LocalClass(TestContext t)
        {
            string folder = Folder(
                ("thesis.cls", "\\NeedsTeXFormat{LaTeX2e}\n\\ProvidesClass{thesis}[2024/01/01 test]\n\\def\\@mode{narrow}\\def\\@other{none}\n"
                    + "\\DeclareOption{wide}{\\def\\@mode{wide}}\n\\DeclareOption*{\\def\\@other{other}}\n\\ProcessOptions\\relax\n"
                    + "\\LoadClass[12pt, a4paper]{article}\n\\RequirePackage{extra}\n\\newcommand{\\mode}{\\@mode-\\@other}\n"),
                ("extra.sty", "\\ProvidesPackage{extra}\n\\newcommand{\\extra}[1]{E#1}\n"
                    + "\\renewcommand\\paragraph{\\@startsection{paragraph}{4}{0pt}{1ex \\@plus 1ex}{-1em}{\\normalfont\\itshape}}\n"));
            TexTypesetter typesetter = new TexTypesetter("\\documentclass[wide,foo]{thesis}\n\\begin{document}\n\\mode{} \\extra{x} \\ifcat @a L\\else O\\fi\n\n"
                + "\\paragraph{P} text\n\\end{document}\n", null, folder);
            typesetter.Run();
            List<TexParagraph> pars = Pars(typesetter);
            string[] lines = pars.Select(p => Line(p).Trim()).ToArray();
            t.Check(typesetter.paperWidth == 210f && typesetter.normalSize == 12 * 65536, $"\\LoadClass[12pt, a4paper]: {typesetter.paperWidth} mm, {typesetter.normalSize / 65536f} pt");
            t.Check(lines.Length == 2 && lines[0] == "wide-other Ex O", $"declared and other options ran, the package's command works, @ is other again: {string.Join(" / ", lines)}");
            TexChar p = pars[^1].list.OfType<TexChar>().First();
            t.Check(lines[^1] == "P text" && p.style.font.italic && !p.style.font.bold, $"\\@startsection runs \\paragraph in, in its style: {lines[^1]}");
            t.Check(typesetter.errors.Count == 0, $"no errors: {string.Join("; ", typesetter.errors)}");
            Directory.Delete(folder, true);
            yield break;
        }

        [A_XSDActionDependency("Tex.NoStrayText", "Test")]
        private static IEnumerator<int> NoStrayText(TestContext t)
        {
            List<TexParagraph> toc = Pars(Typeset("\\documentclass{article}\\usepackage{fontspec}\\setmainfont{Times New Roman}[Ligatures=TeX]"
                + "\\newfontface\\sectionfont[Path=fonts/]{Calibri}\\begin{document}\\selectlanguage{lithuanian}\\tableofcontents"
                + "\\phantomsection\\addcontentsline{toc}{section}{Priedai}{\\sectionfont Body}\\end{document}", out List<TexError> errors));
            string body = toc.Count == 3 ? BoxText(toc[0].list) + " / " + BoxText(toc[1].list) + " / " + BoxText(toc[2].list) : toc.Count.ToString();
            t.Check(body == "Turinys / Priedai?? / Body" && errors.Count == 0,
                $"the contents lists its entry under the Lithuanian title, no stray text: {body} {string.Join("; ", errors)}");

            string folder = Folder(("refs.bib", "@misc{a, title = {Title}, year = 2000}\n"));
            TexTypesetter typesetter = new TexTypesetter("\\documentclass{article}\\bibliography{refs}\\begin{document}\\cite{a}\n"
                + "\\printbibliography[heading=bibintoc]\n\\end{document}\n", null, folder);
            typesetter.Run();
            string[] lines = Pars(typesetter).Select(BoxLine).ToArray();
            t.Check(lines.Length == 3 && lines[0] == "[1]" && lines[1] == "References" && lines[2].StartsWith("[1] Title"),
                $"\\bibliography in the preamble names the files, \\printbibliography[…] sets the list: {string.Join(" / ", lines)}");
            t.Check(typesetter.errors.Count == 0, $"no errors: {string.Join("; ", typesetter.errors)}");
            Directory.Delete(folder, true);
            yield break;
        }

        [A_XSDActionDependency("Tex.ListLengths", "Test")]
        private static IEnumerator<int> ListLengths(TestContext t)
        {
            TexTypesetter typeset = Typeset("\\documentclass[12pt]{article}\\begin{document}\nText\n\n\\begin{itemize}\n\\setlength{\\itemsep}{0pt}\n"
                + "\\setlength{\\topsep}{0pt}\n\\item A\n\\item B\n\\end{itemize}\nAfter\n\\end{document}\n", out List<TexError> errors);
            List<TexParagraph> pars = Pars(typeset);
            t.Check(errors.Count == 0 && string.Join("/", pars.Select(Line)) == "Text/A/B/After",
                $"\\setlength on the list lengths sets no text: {string.Join("/", pars.Select(Line))} {string.Join("; ", errors)}");
            if (pars.Count != 4) yield break;

            List<TexNode> v = typeset.vlist;
            float Gap(int from, int to, Func<IEnumerable<float>, float> fold) =>
                fold(v.Skip(v.IndexOf(pars[from]) + 1).Take(v.IndexOf(pars[to]) - v.IndexOf(pars[from]) - 1).OfType<TexVGlue>().Select(g => Pt(g.glue.width)).DefaultIfEmpty(0f));
            float between = Gap(1, 2, g => g.Sum()), above = Gap(0, 1, g => g.Max()), below = Gap(2, 3, g => g.Max());
            t.Check(between == 5f, $"\\itemsep 0 leaves \\parsep between items: {between}pt");
            t.Check(above == 13f && below == 13f, $"\\topsep set inside the list changes neither skip, \\partopsep after a blank line: {above}pt above, {below}pt below");
            yield break;
        }

        [A_XSDActionDependency("Tex.SectionNumbers", "Test")]
        private static IEnumerator<int> SectionNumbers(TestContext t)
        {
            TexTypesetter dotted = Typeset("\\documentclass{article}\\renewcommand\\thesection{\\arabic{section}.}"
                + "\\renewcommand\\thesubsection{\\thesection\\arabic{subsection}.}\\begin{document}\\section{One}\\subsection{Sub}\\label{s}See \\ref{s}\\end{document}",
                out List<TexError> errors);
            string[] lines = Pars(dotted).Select(Line).ToArray();
            t.Check(errors.Count == 0 && lines.SequenceEqual(new[] { "1. One", "1.1. Sub", "See 1.1." }),
                $"\\thesection and \\thesubsection give the heading and \\ref: {string.Join(" / ", lines)} {string.Join("; ", errors)}");
            t.Check(Lines("\\documentclass{article}\\begin{document}\\section{A}\\subsection{B}\\end{document}") == "1 A / 1.1 B", "article numbers as before");
            t.Check(Lines("\\documentclass{report}\\begin{document}\\chapter{C}\\section{S}\\end{document}").EndsWith("1.1 S"), "report's \\thesection takes the chapter");
            yield break;
        }

        [A_XSDActionDependency("Tex.TitleArgument", "Test")]
        private static IEnumerator<int> TitleArgument(TestContext t)
        {
            const string format = "\\documentclass{article}\\usepackage{titlesec}\\titleformat{\\section}[block]{\\bfseries}{\\thesection}{0em}{\\hspace{0.25cm}\\MakeUppercase}"
                + "\\titlespacing*{\\section}{0.25cm}{0pt}{12pt}\n";
            const string body = "\\begin{document}Text\n\\section{Įvadas į darbą}\\subsection{Sub part}\\end{document}";
            TexTypesetter broken = Typeset(format + "\\newcommand{\\sectionbreak}{\\clearpage}\n" + body, out List<TexError> errors);
            List<TexParagraph> pars = Pars(broken);
            string[] lines = pars.Select(p => System.Text.RegularExpressions.Regex.Replace(Line(p), " {2,}", " ").Trim()).ToArray();
            t.Check(errors.Count == 0 && lines.SequenceEqual(new[] { "Text", "1 ĮVADAS Į DARBĄ", "1.1 Sub part" }),
                $"the before-code's last command takes the whole title: {string.Join(" / ", lines)} {string.Join("; ", errors)}");
            t.Check(pars.Count == 3 && pars[1].list.FirstOrDefault() is TexGlueNode indent && Near(Pt(indent.glue.width), 2.5f / 25.4f * 72.27f),
                "\\titlespacing's left indents the heading");
            t.Check(broken.vlist.OfType<TexPageBreak>().Count() == 1 && broken.vlist.IndexOf(broken.vlist.OfType<TexPageBreak>().First()) < broken.vlist.IndexOf(pars[1]),
                "\\sectionbreak clears the page before \\section only");
            t.Check(!Typeset(format + body, out _).vlist.OfType<TexPageBreak>().Any(), "no \\sectionbreak, no break");
            yield break;
        }

        [A_XSDActionDependency("Tex.Setspace", "Test")]
        private static IEnumerator<int> Setspace(TestContext t)
        {
            const string source = "\\documentclass[12pt]{article}\\usepackage{setspace}\\setstretch{1.2}\\begin{document}\\onehalfspacing\nText\n\n\\doublespacing\nMore\\end{document}";
            TexTypesetter spaced = Typeset(source, out List<TexError> errors);
            t.Check(errors.Count == 0 && spaced.lineSpread == 1.241f, $"the stretch the first paragraph ends under holds: {spaced.lineSpread} {string.Join("; ", errors)}");
            t.Check((string?)TexLowering.Compile(source, out _).Element("DocumentLayout")!.Attribute("LineHeight") == "1.5", "and scales the line height");
            t.Check((string?)TexLowering.Compile("\\documentclass{article}\\begin{document}x\\end{document}", out _).Element("DocumentLayout")!.Attribute("LineHeight") == "1.2",
                "no setspace keeps \\baselineskip 12pt, against the 10pt \\normalsize");
            t.Check(Typeset("\\documentclass{article}\\begin{document}\\doublespacing x\\end{document}", out _).lineSpread == 1.667f, "\\doublespacing at 10pt");
            TexChar a = Chars("\\documentclass{article}\\newfontface\\sectionfont{Calibri}\\begin{document}{\\large\\fontsize{11pt}{13pt}\\sectionfont A}\\end{document}")[0];
            t.Check(Pt(a.style.font.size) == 11f, $"a \\newfontface command selects the pending \\fontsize: {Pt(a.style.font.size)}pt");
            yield break;
        }

        [A_XSDActionDependency("Tex.MainFont", "Test")]
        private static IEnumerator<int> MainFont(TestContext t)
        {
            string termes = Registered(TexLowering.TermesFont, TexLowering.RomanFont);
            string carlito = Registered(TexLowering.CarlitoFont, TexLowering.RomanFont);
            string modern = Registered(TexLowering.RomanFont, TexLowering.MonoFont);
            XElement document = TexLowering.Compile("\\documentclass{article}\\usepackage{fontspec}\\setmainfont[Ligatures=TeX]{Times New Roman}\\setsansfont{Calibri}"
                + "\\setmonofont{Latin Modern Roman}\\begin{document}Body \\textsf{sans} \\texttt{code}\\end{document}", out List<TexError> errors);
            t.Check(errors.Count == 0 && RunFont(document, FindRun(document, "Body")) == termes,
                $"\\setmainfont{{Times New Roman}} sets the body in {termes}: {RunFont(document, FindRun(document, "Body"))}, {errors.Count} error(s)");
            t.Check(RunFont(document, FindRun(document, "sans")) == carlito && RunFont(document, FindRun(document, "code")) == modern,
                $"\\setsansfont and \\setmonofont: {RunFont(document, FindRun(document, "sans"))}, {RunFont(document, FindRun(document, "code"))}");
            document = TexLowering.Compile("\\documentclass{article}\\usepackage{fontspec}\\setmainfont{No Such Face}\\begin{document}Body\\end{document}", out errors);
            t.Check(errors.Count == 0 && RunFont(document, FindRun(document, "Body")) == TexLowering.RomanFont,
                $"an unknown font keeps Latin Modern without an error: {RunFont(document, FindRun(document, "Body"))}");
            yield break;
        }

        [A_XSDActionDependency("Tex.FontFace", "Test")]
        private static IEnumerator<int> FontFace(TestContext t)
        {
            string carlito = Registered(TexLowering.CarlitoFont, TexLowering.RomanFont);
            XElement document = TexLowering.Compile("\\documentclass{article}\\usepackage{fontspec}"
                + "\\newfontface\\hf[Path=fonts/Calibri/,Extension=.ttf,UprightFont=*-nm]{Calibri}\\newfontfamily\\ff{Carlito}\\begin{document}"
                + "Body {\\bfseries\\hf Head} {\\hf\\normalfont back} {\\ff fam} {\\fontspec{Calibri}spec} {\\hf\\verb|vrb|}\\end{document}", out List<TexError> errors);
            XElement? head = FindRun(document, "Head");
            t.Check(errors.Count == 0 && RunFont(document, head) == carlito && (string?)head?.Attribute("Bold") == "true",
                $"a \\newfontface command sets bold text in {carlito}: {RunFont(document, head)}, {errors.Count} error(s)");
            t.Check(RunFont(document, FindRun(document, "back")) == TexLowering.RomanFont && RunFont(document, FindRun(document, "vrb")) == TexLowering.MonoFont,
                $"\\normalfont and \\verb leave the face: {RunFont(document, FindRun(document, "back"))}, {RunFont(document, FindRun(document, "vrb"))}");
            t.Check(RunFont(document, FindRun(document, "fam")) == carlito && RunFont(document, FindRun(document, "spec")) == carlito,
                $"\\newfontfamily and \\fontspec select a face: {RunFont(document, FindRun(document, "fam"))}, {RunFont(document, FindRun(document, "spec"))}");
            yield break;
        }

        [A_XSDActionDependency("Tex.Lithuanian", "Test")]
        private static IEnumerator<int> Lithuanian(TestContext t)
        {
            TexHyphenator? lt = TexHyphenator.For("lithuanian");
            t.Check(lt != null && lt.leftMin == 2 && lt.rightMin == 2, "the Lithuanian patterns load, 2 letters either side");
            if (lt != null)
            {
                bool[]? virsunes = lt.Points("viršūnes");
                bool[]? renderinimo = lt.Points("renderinimo");
                bool[]? kompiuteriniuose = lt.Points("kompiuteriniuose");
                t.Check(virsunes != null && virsunes[3] && renderinimo != null && renderinimo[3] && renderinimo[5]
                    && kompiuteriniuose != null && kompiuteriniuose[6], "the breaks LuaLaTeX made in the thesis: vir-šūnes, ren-de-rinimo, kompiu-teriniuose");
            }

            XElement document = TexLowering.Compile("\\documentclass{article}\\usepackage{polyglossia}\\setmainlanguage{lithuanian}"
                + "\\begin{document}Turi tris viršūnes.\\end{document}", out List<TexError> errors);
            XElement? run = FindRun(document, "tris");
            t.Check(errors.Count == 0 && ((string?)run?.Attribute("Text"))?.Contains("vir­šū") == true,
                $"a Lithuanian paragraph is hyphenated with its patterns: {((string?)run?.Attribute("Text"))?.Replace('­', '~')}");
            yield break;
        }

        [A_XSDActionDependency("Tex.Language", "Test")]
        private static IEnumerator<int> Language(TestContext t)
        {
            const string floats = "\\begin{figure}\\caption{A}\\end{figure}\\begin{table}\\caption{B}\\end{table}";
            string lines = Lines("\\documentclass{article}\\usepackage{polyglossia}\\setmainlanguage{lithuanian}\\renewcommand{\\figurename}{}"
                + "\\begin{document}" + floats + "\\selectlanguage{english}\\begin{figure}\\caption{C}\\end{figure}\\end{document}", out List<TexError> errors);
            t.Check(errors.Count == 0 && lines == "1 pav.: A / 1 lentelė: B / Figure 2: C",
                $"\\begin{{document}} and \\selectlanguage set the caption names and their order: {lines}");
            lines = Lines("\\documentclass{article}\\usepackage[english,lithuanian]{babel}\\begin{document}" + floats + "\\end{document}");
            t.Check(lines == "1 pav.: A / 1 lentelė: B",$"babel's last language is the main one: {lines}");

            TexTypesetter typeset = Typeset("\\documentclass{article}\\usepackage{polyglossia}\\setmainlanguage{lithuanian}\\begin{document}"
                + "\\section{S}Pirmas.\\begin{otherlanguage}{english}\\section{E}First.\\end{otherlanguage}\\section{T}Antras.\\end{document}", out errors);
            TexParagraph? first = null, english = null, back = null;
            foreach (TexParagraph p in Pars(typeset))
                switch (Line(p))
                {
                    case "Pirmas.": first = p; break;
                    case "First.": english = p; break;
                    case "Antras.": back = p; break;
                }
            t.Check(first?.language == "lithuanian" && english?.language == "english" && back?.language == "lithuanian",
                $"a paragraph takes the language it starts in: {first?.language}, {english?.language}, {back?.language}");
            t.Check(first?.style.indent == true && english?.style.indent == false && back?.style.indent == true,
                "Lithuanian indents the first paragraph after a heading, English does not");
            yield break;
        }

        [A_XSDActionDependency("Tex.CaptionSetup", "Test")]
        private static IEnumerator<int> CaptionSetup(TestContext t)
        {
            TexTypesetter typeset = Typeset("\\documentclass{article}\\usepackage{caption}\\captionsetup[table]{name=Tab.,labelsep=space,labelfont=bf,font=it}"
                + "\\captionsetup*[figure]{labelsep=period}\\begin{document}"
                + "\\begin{figure}\\caption{A}\\end{figure}\\begin{table}\\captionsetup{justification=justified,singlelinecheck=false}\\caption{B}\\end{table}"
                + "\\begin{figure}\\captionsetup[figure]{labelsep=none}\\caption{C}\\end{figure}\\begin{figure}\\caption{D}\\end{figure}\\end{document}", out List<TexError> errors);
            List<TexParagraph> pars = Pars(typeset);
            string lines = string.Join(" / ", pars.ConvertAll(Line));
            t.Check(errors.Count == 0 && lines == "Figure 1. A / Tab. 1 B / Figure 2C / Figure 3. D",
                $"name and labelsep per kind, a \\captionsetup in a float stays in it, other keys print nothing: {lines}");
            bool labelBoldItalic = false, textItalic = false, textBold = true;
            foreach (TexNode node in pars[1].list)
                if (node is TexChar c && c.ch == '1') labelBoldItalic = c.style.font.bold && c.style.font.italic;
                else if (node is TexChar b && b.ch == 'B')
                {
                    textItalic = b.style.font.italic;
                    textBold = b.style.font.bold;
                }
            t.Check(labelBoldItalic && textItalic && !textBold,"font applies to the whole caption, labelfont to the label only");
            yield break;
        }

        [A_XSDActionDependency("Tex.RaggedNoHyphen", "Test")]
        private static IEnumerator<int> RaggedNoHyphen(TestContext t)
        {
            IGlyphMetrics metrics = new FontAssetGlyphMetrics();
            AtlasMetaData atlas = AssetRegistries.GetRegistryByValueType<string, FontAsset>(typeof(FontAsset))["default"].atlasMetaData;
            const string text = "aaaa bbbb­cccc dddd";
            TextMeasurer.Run run = new TextMeasurer.Run(text, 0, text.Length, "default", atlas, 16, FontStyle.Regular);
            List<TextMeasurer.Run> runs = new List<TextMeasurer.Run> { run };
            float width = TextMeasurer.MeasureAdvance('-', run) + 1f;
            foreach (char c in "aaaa bbbb")
                width += TextMeasurer.MeasureAdvance(c, run);

            BlockLayout justified = TextMeasurer.MeasureBlock(runs, width, metrics, 1.5f, optimal: true);
            BlockLayout ragged = TextMeasurer.MeasureBlock(runs, width, metrics, 1.5f, optimal: true, ragged: true);
            bool hyphenated = false;
            foreach (TextLine line in ragged.lines)
                hyphenated |= line.hyphen != 0f;
            t.Check(justified.lines[0].hyphen != 0f && !hyphenated && ragged.lines.Count == 3,
                $"a ragged paragraph takes a line more rather than a hyphen, as TeX's fil glue does: {ragged.lines.Count} lines");
            yield break;
        }

        [A_XSDActionDependency("Tex.Contents", "Test")]
        private static IEnumerator<int> Contents(TestContext t)
        {
            TexTypesetter typesetter = Typeset("\\documentclass{article}\\usepackage{tocloft}\\renewcommand{\\cftsecleader}{\\cftdotfill{\\cftdotsep}}"
                + "\\begin{document}\\tableofcontents\\section*{Abstract}\\addcontentsline{toc}{section}{Abstract}\n"
                + "\\section{Intro}\\subsection[Short]{Long title}\\subsubsection{Deep}\\paragraph{Para} text\n\\end{document}", out List<TexError> errors);
            List<TexParagraph> pars = Pars(typesetter);
            string[] lines = new string[pars.Count];
            for (int i = 0; i < pars.Count; i++)
                lines[i] = BoxText(pars[i].list);
            t.Check(lines.Length == 10 && lines[0] == "Contents" && lines[1] == "Abstract ??" && lines[2] == "1Intro ??"
                    && lines[3] == "1.1Short ??" && lines[4] == "1.1.1Deep ??",
                $"one entry a heading, \\addcontentsline adds one, [short] titles it, \\paragraph is past tocdepth: {string.Join(" / ", lines)}");

            TexParStyle sub = pars[3].style;
            t.Check(Near(Pt(sub.leftSkip), 38f) && Near(Pt(sub.rightSkip), 25.5f) && Near(Pt(sub.parFillSkip), -25.5f) && Near(Pt(sub.indentWidth), 15f),
                $"tocloft's subsection: \\leftskip indent + numwidth, \\rightskip \\@tocrmarg, \\parfillskip its negative: {Pt(sub.leftSkip)} {Pt(sub.rightSkip)} {Pt(sub.parFillSkip)} {Pt(sub.indentWidth)}");

            TexHBox? number = null;
            TexGlueNode? leader = null;
            foreach (TexNode node in pars[3].list)
                if (node is TexHBox { width: > 0 } box && number == null) number = box;
                else if (node is TexGlueNode { leader: not null } g) leader = g;
            t.Check(number != null && Near(Pt(number.width), 23f), $"\\numberline sets the number in a box \\cftsubsecnumwidth wide: {(number == null ? -1 : Pt(number.width))}");
            t.Check(leader != null && leader.leader!.list.Count == 1 && leader.leader.list[0] is TexMathNode { source: string dots } && dots.Contains("\\mkern 4.5 mu"),
                "the leader is \\cftdotfill's box of 4.5 mu kerns either side of a dot");
            t.Check(pars[1].list.Exists(n => n is TexGlueNode { leader: not null }), "\\renewcommand of \\cftsecleader gives sections dots too");
            t.Check(errors.Count == 0, $"no errors: {string.Join("; ", errors)}");

            TexTypesetter plain = Typeset("\\documentclass{article}\\begin{document}\\tableofcontents\\section{A}\\subsection{B}\\end{document}", out errors);
            pars = Pars(plain);
            t.Check(pars.Count == 5 && BoxText(pars[1].list) == "1A??" &&!pars[1].list.Exists(n => n is TexGlueNode { leader: not null })
                    && pars[2].list.Exists(n => n is TexGlueNode { leader: not null }) && errors.Count == 0,
                $"without tocloft, article's sections have no dots, subsections do: {BoxText(pars[1].list)} {string.Join("; ", errors)}");
            yield break;
        }

        [A_XSDActionDependency("Tex.ContentsLists", "Test")]
        private static IEnumerator<int> ContentsLists(TestContext t)
        {
            string folder = Folder(("refs.bib", "@misc{a, title = {Title}, year = 2000}\n"));
            TexTypesetter typesetter = new TexTypesetter("\\documentclass{article}\\usepackage{tocbibind}\\bibliography{refs}\\begin{document}"
                + "\\tableofcontents\\listoffigures\\listoftables\\section{A}\\begin{figure}\\caption[Fig]{Figure text}\\end{figure}"
                + "\\begin{table}\\caption{Tab}\\end{table}\\cite{a}\\printbibliography\\section*{After}\\addcontentsline{toc}{section}{After}\\end{document}",
                null, folder);
            typesetter.Run();
            List<TexParagraph> pars = Pars(typesetter);
            StringBuilder all = new StringBuilder();
            foreach (TexParagraph p in pars)
                all.Append(BoxText(p.list)).Append(" / ");
            string text = all.ToString();
            t.Check(text.StartsWith("Contents / Contents?? / List of Figures?? / List of Tables?? / 1A?? / References?? / After?? / "
                    + "List of Figures / 1Fig ?? / List of Tables / 1Tab ?? / "),
                $"tocbibind lists the contents and lists, the bibliography stays where it stood among the entries, captions fill lof and lot: {text}");
            t.Check(typesetter.errors.Count == 0, $"no errors: {string.Join("; ", typesetter.errors)}");
            Directory.Delete(folder, true);
            yield break;
        }

        [A_XSDActionDependency("Tex.ContentsLowering", "Test")]
        private static IEnumerator<int> ContentsLowering(TestContext t)
        {
            const float px = 96f / 72.27f;
            XElement document = TexLowering.Compile("\\documentclass{article}\\usepackage{tocloft}\\begin{document}\\tableofcontents\\section{A}"
                + "\\subsection{A subsection title long enough that it has to wrap onto a second line of the contents, past the leaders and the page number}"
                + "\\end{document}", out List<TexError> errors);
            XElement? entry = null;
            foreach (XElement block in document.Elements("Block"))
                if (entry == null && Near((float?)block.Attribute("LeftIndent") ?? 0f, 38f * px, 0.05f)) entry = block;
            t.Check(entry != null && Near((float?)entry.Attribute("LeftIndent") ?? 0f, 38f * px, 0.05f) && Near((float?)entry.Attribute("RightIndent") ?? 0f, 25.5f * px, 0.05f)
                    && (float?)entry.Attribute("RightIndentLast") == 0f && Near((float?)entry.Attribute("Indent") ?? 0f, -23f * px, 0.05f),
                $"\\leftskip, \\rightskip and \\parfillskip lower to the block's indents, \\hskip-\\leftskip to a hanging first line: {entry?.ToString(SaveOptions.DisableFormatting)}");
            if (entry == null) yield break;

            XElement? leader = null;
            foreach (XElement run in entry.Elements("Run"))
                if ((string?)run.Attribute("Leader") != null) leader = run;
            float dot = TexLowering.TextWidth(".", new TexStyle(new TexFont(TexFamily.Roman, false, false, 10 * 65536), null, false));
            t.Check(leader != null && (string?)leader.Attribute("Leader") == "." && Near((float?)leader.Attribute("LeaderWidth") ?? 0f, 5f * px + dot, 0.05f)
                    && Near((float?)leader.Attribute("LeaderInset") ?? 0f, 2.5f * px, 0.05f),
                $"\\cftdotfill lowers to a leader fill: a dot in a 9 mu + dot cell, 4.5 mu in: {leader?.ToString(SaveOptions.DisableFormatting)}");

            TexPageRefs refs = TexLowering.PageRefs(document);
            XElement? number = null;
            foreach ((XElement run, string key) in refs.refs)
                if (run.Parent == entry) number = run;
            XElement? spacer = null;
            foreach (XElement run in entry.Elements("Run"))
            {
                if (run == number) break;
                spacer = run;
            }
            float before = (float?)spacer?.Attribute("Space") ?? 0f;
            if (number != null) refs.Fit(number, "123");
            float after = (float?)spacer?.Attribute("Space") ?? 0f;
            TexStyle normal = new TexStyle(new TexFont(TexFamily.Roman, false, false, 10 * 65536), null, false);
            t.Check(number != null && Near(before - after, TexLowering.TextWidth("123", normal) - TexLowering.TextWidth("??", normal), 0.05f),
                $"the page number's box keeps its width as the number changes, its \\hss spacer giving way: {before} → {after}");
            t.Check(errors.Count == 0, $"no errors: {string.Join("; ", errors)}");

            DocumentEditorControl editor = ShowDocument(t, document);
            yield return 2;
            int index = 0;
            foreach (XElement block in document.Elements())
            {
                if (block == entry) break;
                if (block.Name.LocalName != "DocumentLayout") index++;
            }
            BlockControl? shown = View(editor, index) as BlockControl;
            if (shown != null && shown.Lines is { Count: 2 } lines)
            {
                float content = shown.arrange.arranged.width - shown.padding.left - shown.padding.right;
                float right = shown.padding.right;
                t.Check(lines[0].left < -1f && lines[0].left + lines[0].width <= content + 5f,
                    $"the first line hangs left of the indent and stays inside \\rightskip but for its trailing space: left {lines[0].left}, right {lines[0].left + lines[0].width} of {content}");
                t.Check(Near(lines[1].left + lines[1].width, content + right, 1f),
                    $"the last line runs into \\rightskip, its number flush with the margin: {lines[1].left + lines[1].width} of {content + right}");
            }
            else t.Check(false, $"the entry wraps to two lines: {shown?.Lines?.Count}");
            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("Tex.LastLineRoom", "Test")]
        private static IEnumerator<int> LastLineRoom(TestContext t)
        {
            IGlyphMetrics metrics = new FontAssetGlyphMetrics();
            AtlasMetaData atlas = AssetRegistries.GetRegistryByValueType<string, FontAsset>(typeof(FontAsset))["default"].atlasMetaData;
            const string text = "aaaa bbbb cccc";
            TextMeasurer.Run run = new TextMeasurer.Run(text, 0, text.Length, "default", atlas, 16, FontStyle.Regular);
            List<TextMeasurer.Run> runs = new List<TextMeasurer.Run> { run };
            float whole = 0f;
            foreach (char c in text)
                whole += TextMeasurer.MeasureAdvance(c, run);
            float width = whole - 4f;

            foreach (bool optimal in new[] { false, true })
            {
                BlockLayout narrow = TextMeasurer.MeasureBlock(runs, width, metrics, 1.5f, optimal: optimal);
                BlockLayout extended = TextMeasurer.MeasureBlock(runs, width, metrics, 1.5f, optimal: optimal, lastExtra: 8f);
                t.Check(narrow.lines.Count == 2 && extended.lines.Count == 1 && Near(extended.lines[0].room, width + 8f, 0.01f),
                    $"{(optimal ? "Knuth-Plass" : "greedy")}: a paragraph that fits only with the last line's extra room is one line of that room: {narrow.lines.Count} → {extended.lines.Count}");
            }
            yield break;
        }

        [A_XSDActionDependency("Tex.BlockIndents", "Test")]
        private static IEnumerator<int> BlockIndents(TestContext t)
        {
            RichTextDocument document = DocumentXml.Parse(XElement.Parse("<Document><Block LeftIndent=\"10\" RightIndent=\"20\" RightIndentLast=\"0\">"
                + "<Run Text=\"ab\"/><Run Text=\" \" Fill=\"true\" Leader=\".\" LeaderWidth=\"9\" LeaderInset=\"3\"/><Run Text=\"1\"/></Block></Document>"));
            XElement? block = null, fill = null;
            foreach (XElement element in DocumentXml.ToXml(document).Descendants())
                if (element.Name.LocalName == "Block") block ??= element;
                else if (element.Name.LocalName == "Run" && (string?)element.Attribute("Fill") == "true") fill = element;
            t.Check(block != null && (string?)block.Attribute("LeftIndent") == "10" && (string?)block.Attribute("RightIndent") == "20" && (string?)block.Attribute("RightIndentLast") == "0"
                    && fill != null && (string?)fill.Attribute("Leader") == "." && (string?)fill.Attribute("LeaderWidth") == "9" && (string?)fill.Attribute("LeaderInset") == "3",
                $"block indents and a fill's leaders survive a save: {block?.ToString(SaveOptions.DisableFormatting)}");

            NoteBlock head = (NoteBlock)document.blocks[0];
            NoteBlock tail = head.SplitAt(1);
            NoteBlock taken = new NoteBlock();
            taken.TakeKind(head.Snapshot());
            t.Check(tail.leftIndent == 10f && tail.rightIndent == 20f && tail.rightIndentLast == 0f
                    && taken.leftIndent == 10f && taken.rightIndent == 20f && taken.rightIndentLast == 0f,
                "Enter's tail and a block taking another's kind keep its indents");
            yield break;
        }

        // A list's text with boxes opened, a leader or fil glue as nothing and other glue as a space.
        private static string BoxText(List<TexNode> list)
        {
            StringBuilder text = new StringBuilder();
            foreach (TexNode node in list)
                switch (node)
                {
                    case TexChar c:
                        text.Append(c.ch);
                        break;
                    case TexGlueNode { leader: null, glue.stretchOrder: 0 } g when g.glue.width > 0:
                        text.Append(' ');
                        break;
                    case TexGlueNode { leader: not null }:
                        text.Append(' ');
                        break;
                    case TexHBox b:
                        text.Append(BoxText(b.list));
                        break;
                    case TexRefNode r:
                        text.Append(r.text);
                        break;
                }
            return text.ToString();
        }

        // The asset when it is registered in this host, else the fallback.
        private static string Registered(string asset, string fallback) =>
            AssetRegistries.GetRegistryByValueType<string, FontAsset>(typeof(FontAsset)).ContainsKey(asset) ? asset : fallback;

        // The first run whose text holds the given text.
        private static XElement? FindRun(XElement document, string text)
        {
            foreach (XElement run in document.Descendants("Run"))
                if (((string?)run.Attribute("Text"))?.Contains(text) == true) return run;
            return null;
        }

        // The font a run draws in: its own FontName, else the Text style's.
        private static string? RunFont(XElement document, XElement? run)
        {
            if (run == null) return null;
            if ((string?)run.Attribute("FontName") is string own) return own;
            foreach (XElement style in document.Element("DocumentLayout")!.Elements("TextStyle"))
                if ((string?)style.Attribute("Type") == "Text") return (string?)style.Attribute("FontName");
            return null;
        }
        #endregion
    }
}
