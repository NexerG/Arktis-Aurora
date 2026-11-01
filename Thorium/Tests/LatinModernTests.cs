using ArctisAurora.Core.Diagnostics;
using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Filing.Serialization;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Testing;
using ArctisAurora.Core.Tex;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Containers.Workspace;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Document;
using ArctisAurora.Core.UI.Notes;
using ArctisAurora.Core.UI.Tex;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.EngineWork;
using Image = Arctis.Imaging.Image;
using System.Numerics;
using System.Xml.Linq;

namespace Thorium.Tests
{
    internal static class LatinModernTests
    {
        private static readonly LogChannel Log = LogChannel.For("Test");

        private static AtlasMetaData Load(string name)
        {
            AtlasMetaData atlas = new AtlasMetaData();
            Serializer.DeserializeAttributed(Paths.Font(name, name + ".agd"), ref atlas);
            atlas.BuildCharIndex();
            return atlas;
        }

        private static GlyphMetrics Of(AtlasMetaData atlas, char c) => atlas.GetGlyphAndIndex(c).Item1.regular;

        private static (int, int, int, int, float) Key(GlyphMetrics m) => (m.xMin, m.yMin, m.xMax, m.yMax, m.advanceWidth);

        [A_XSDActionDependency("LatinModern.Baked", "Test")]
        private static IEnumerator<int> Baked(TestContext t)
        {
            AtlasMetaData roman = Load("lmroman10-regular");
            char[] distinct = { 'a', 'g', 'ﬁ', '—', 'é' };
            t.Check(distinct.Select(c => Key(Of(roman, c))).Distinct().Count() == distinct.Length,
                "a, g, fi, em dash and e-acute bake distinct glyphs");

            float a = Of(roman, 'a').advanceWidth, m = Of(roman, 'm').advanceWidth;
            t.Check(MathF.Abs(a - 0.5f) < 0.01f && MathF.Abs(m - 0.833f) < 0.01f, $"advances follow cmr10: a {a}, m {m}");

            Glyph x = roman.GetGlyphAndIndex('x').Item1;
            t.Check(roman.HasFace(0, FontStyle.Italic) && roman.HasFace(0, FontStyle.Bold) && roman.HasFace(0, FontStyle.BoldItalic)
                && Key(x.regular) != Key(roman.Metrics(x, roman.Effective(0, FontStyle.Italic))),
                "the Roman family has its italic, bold and bold-italic faces");

            AtlasMetaData mono = Load("lmmono10-regular");
            t.Check(Of(mono, 'i').advanceWidth == Of(mono, 'm').advanceWidth, "the mono face is monospaced");

            AtlasMetaData math = Load("latinmodern-math");
            MathConstants constants = MathConstants.Load(Paths.Font("latinmodern-math", "latinmodern-math.math.axml"));
            t.Check(constants.axisHeight > 0f && constants.fractionRuleThickness > 0f, "LM Math constants are read");

            // A character the font lacks bakes .notdef, so several share one box.
            List<char> missing = math.chars.GroupBy(c => Key(Of(math, c))).Where(g => g.Count() > 2).SelectMany(g => g).ToList();
            Log.Info($"LM Math characters sharing one glyph box (likely missing): {new string(missing.ToArray())}");
            t.Check(!"∑∫√α⎛⎝".Any(missing.Contains), "sum, integral, root, alpha and paren pieces are in LM Math");
            yield break;
        }

        [A_XSDActionDependency("LatinModern.Draws", "Test")]
        private static IEnumerator<int> Draws(TestContext t)
        {
            StackPanelControl panel = new StackPanelControl
            {
                preferredWidth = 640f,
                preferredHeight = 180f,
                colorHex = "#FFFFFF",
                horizontalAlignment = HorizontalAlignment.Left,
                verticalAlignment = VerticalAlignment.Top
            };
            panel.AddChild(new LabelControl { text = "Fine ffi — “LaTeX” é 0123 Wq", fontName = "latin-modern", fontSize = 28, colorHex = "#000000" });
            panel.AddChild(new LabelControl { text = "\\begin{document} 0O1lI", fontName = "latin-modern-mono", fontSize = 28, colorHex = "#000000" });
            panel.AddChild(new LabelControl { text = "∑∫√ αβγ ≤≠∞ ⎛⎝", fontName = "latin-modern-math", fontSize = 28, lineHeight = 3f, colorHex = "#000000" });
            t.Show(panel);
            yield return 2;

            yield return t.Golden("Lines", panel);
        }

        [A_XSDActionDependency("Tex.Preview", "Test")]
        private static IEnumerator<int> TexPreview(TestContext t)
        {
            int xHeight = new TexAtlasMetrics().XHeight(new TexFont(TexFamily.Roman, false, false, 10 * 65536));
            t.Check(xHeight > 4.2f * 65536 && xHeight < 4.45f * 65536, $"ex at 10pt is Latin Modern's x-height, about 4.31pt: {xHeight / 65536f}pt");

            const string source = """
                \documentclass{article}
                \title{A Sample Article}
                \author{Aurora}
                \date{5 October 2026}
                \begin{document}
                \maketitle
                \section{Introduction}
                The ``first'' paragraph---with ligatures: office, flow, and caf\'e, na\"ive.
                Inline math $e^{i\pi} + 1 = 0$ and a note.\footnote{An endnote.}
                \[ \sum_{k=1}^{n} k = \frac{n(n+1)}{2} \]
                \subsection{Lists}
                \begin{itemize}
                \item \textbf{Bold} and \emph{emphasis}
                \begin{enumerate}
                \item First
                \item Second
                \end{enumerate}
                \end{itemize}
                \begin{verbatim}
                int x = 1;
                \end{verbatim}
                \end{document}
                """;
            XElement document = TexLowering.Compile(source, out List<TexError> errors);
            t.Check(errors.Count == 0, $"the sample compiles clean: {string.Join("; ", errors.Select(e => $"{e.line}: {e.message}"))}");

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
            yield return t.Golden("Page", editor);
        }

        [A_XSDActionDependency("Tex.Floats", "Test")]
        private static IEnumerator<int> TexFloats(TestContext t)
        {
            string folder = Path.Combine(Path.GetTempPath(), $"aurora-tex-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            GradientPng(Path.Combine(folder, "gradient.png"));
            File.WriteAllText(Path.Combine(folder, "refs.bib"), """
                @book{knuth84, author = {Donald E. Knuth}, title = {The {\TeX}book}, publisher = {Addison-Wesley}, year = 1984}
                @article{lamport86, author = {Leslie Lamport}, title = {Document Preparation}, journal = {TUGboat}, volume = 7, pages = {10--20}, year = 1986}
                """);

            const string source = """
                \documentclass{article}
                \usepackage{graphicx,booktabs}
                \begin{document}
                \section{Results}\label{sec:results}
                Figure~\ref{fig:gradient} and Table~\ref{tab:data} are in Section~\ref{sec:results}, after \cite{knuth84,lamport86}.
                \begin{figure}[h]
                \centering
                \includegraphics[width=0.4\textwidth]{gradient}
                \caption{A gradient.}\label{fig:gradient}
                \end{figure}
                \begin{table}[h]
                \caption{Measurements}\label{tab:data}
                \begin{tabular}{lrr}
                \toprule
                \multicolumn{3}{c}{Run times} \\
                \midrule
                Case & Before & After \\
                Layout & 4.2 & 1.1 \\
                Typing & 12 & 3 \\
                \bottomrule
                \end{tabular}
                \end{table}
                \begin{tabular}{|c|c|}
                \hline
                a & b \\
                \hline
                \end{tabular}
                \bibliographystyle{plain}
                \bibliography{refs}
                \end{document}
                """;
            XElement document = TexLowering.Compile(source, out List<TexError> errors, null, folder);
            t.Check(errors.Count == 0, $"the sample compiles clean: {string.Join("; ", errors.Select(e => $"{e.line}: {e.message}"))}");

            string path = Path.Combine(folder, "floats.axml");
            document.Save(path);
            DocumentEditorControl editor = new DocumentEditorControl
            {
                horizontalAlignment = HorizontalAlignment.Stretch,
                verticalAlignment = VerticalAlignment.Stretch
            };
            t.Show(editor);
            editor.LoadPath(path);
            yield return 2;
            yield return t.Golden("Page", editor);
            t.Show(new StackPanelControl());
            Directory.Delete(folder, true);
        }

        [A_XSDActionDependency("Tex.Booktabs", "Test")]
        private static IEnumerator<int> TexBooktabs(TestContext t)
        {
            const string source = """
                \documentclass{article}
                \usepackage{booktabs}
                \begin{document}
                \begin{center}
                \begin{tabular}{lrr}
                \toprule
                & \multicolumn{2}{c}{Time (ms)} \\
                \cmidrule(lr){2-3}
                Case & Before & After \\
                \midrule
                Layout & 4.2 & 1.1 \\
                Typing & 12 & 3 \\
                \bottomrule
                \end{tabular}
                \end{center}
                A ruled grid at the left margin:

                \noindent\begin{tabular}{|l||c|}
                \hline
                a & b \\
                \cline{2-2}
                c & d \\
                \hline\hline
                \end{tabular}

                \begin{flushright}
                \begin{tabular}{ll}
                \toprule[2pt]
                x & y \\
                \bottomrule
                \end{tabular}
                \end{flushright}
                \end{document}
                """;
            XElement document = TexLowering.Compile(source, out List<TexError> errors);
            t.Check(errors.Count == 0, $"the sample compiles clean: {string.Join("; ", errors.Select(e => $"{e.line}: {e.message}"))}");

            string path = Path.Combine(Path.GetTempPath(), $"aurora-booktabs-{Guid.NewGuid():N}.axml");
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
            yield return t.Golden("Page", editor);
            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("Tex.Resume", "Test")]
        private static IEnumerator<int> TexResume(TestContext t)
        {
            const string source = """
                \documentclass[11pt,letterpaper]{article}
                \usepackage[left=0.5in, right=0.5in, top=0.4in, bottom=0.4in, paperwidth=8.5in, paperheight=100in]{geometry}
                \usepackage{enumitem}
                \usepackage{titlesec}
                \usepackage{hyperref}
                \usepackage{xcolor}
                \usepackage{mathptmx}
                \usepackage[normalem]{ulem}
                \hyphenpenalty=10000
                \exhyphenpenalty=10000
                \tolerance=9999
                \emergencystretch=3em
                \definecolor{sectioncolor}{HTML}{000000}
                \hypersetup{colorlinks=false, hidelinks}
                \newcommand{\ulink}[2]{\href{#1}{\uline{#2}}}
                \pagestyle{empty}
                \titleformat{\section}{\large\bfseries}{}{0pt}{}[\vspace{-7pt}{\color{sectioncolor}\rule{\textwidth}{1.5pt}}]
                \titlespacing*{\section}{0pt}{8pt}{2pt}
                \setlength{\parindent}{0pt}
                \setlength{\parskip}{0pt}
                \newlist{resumelist}{itemize}{1}
                \setlist[resumelist]{label=\raisebox{0.45ex}{\rule{2.8pt}{2.8pt}}, leftmargin=12pt, itemsep=0pt, parsep=0pt, topsep=1pt}
                \begin{document}
                \begin{center}
                    {\LARGE\bfseries Grexen Example} \\[3pt]
                    {\small Somewhere \,|\, \ulink{https://example.org}{example.org} \,|\, \ulink{https://example.org/docs}{Documentation}}
                \end{center}
                \section*{EXPERIENCE}
                \textbf{Example Studio} -- \ulink{https://example.org}{Website} \hfill \textbf{March 2024 -- May 2025} \\
                \textit{Engine Programmer} \textbullet \space \textit{Full-time} \hfill Somewhere
                \begin{resumelist}
                    \item Wrote the renderer and the tools around it.
                    \item Profiled and trimmed the frame until it fit the budget on every target platform we shipped.
                \end{resumelist}
                \section*{SKILLS}
                \textbf{Languages:} C\#, C++, GLSL, Python. \\
                \textbf{Interests:} rendering; shaders; typesetting.
                \pdfpageheight=\dimexpr\pagetotal+0.8in+12pt\relax
                \end{document}
                """;
            XElement document = TexLowering.Compile(source, out List<TexError> errors);
            t.Check(errors.Count == 0, $"the sample compiles clean: {string.Join("; ", errors.Select(e => $"{e.line}: {e.message}"))}");
            XElement page = document.Element("DocumentLayout")!.Element("Page")!;
            t.Check(page.Attribute("FitHeight") != null && (string?)page.Attribute("Size") == "Custom", "a custom paper cut to its content");
            t.Check(document.Descendants("Run").Count(r => (string?)r.Attribute("Fill") == "true") == 2, "both \\hfill lines carry a fill");
            t.Check(document.Elements("Block").Count(b => (string?)b.Attribute("StylingType") == "Rule") == 2, "a rule under each section");
            t.Check(document.Elements("Block").Count(b => (string?)b.Attribute("Marker") == "Square") == 2, "square bullets");
            t.Check(document.Elements("DocumentLayout").Elements("TextStyle").All(s => (string?)s.Attribute("FontName") != TexLowering.RomanFont),
                "mathptmx sets the text in Termes");

            string path = Path.Combine(Path.GetTempPath(), $"aurora-resume-{Guid.NewGuid():N}.axml");
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
            yield return t.Golden("Page", editor);
            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("Tex.Amsmath", "Test")]
        private static IEnumerator<int> TexAmsmath(TestContext t)
        {
            const string source = """
                \documentclass{article}
                \usepackage{amsmath}
                \DeclareMathOperator{\tr}{tr}
                \begin{document}
                \section{Equations}
                Euler's identity is \eqref{eq:euler}; the sum of squares is \eqref{eq:sum}.
                \begin{equation}
                e^{i\pi} + 1 = 0 \label{eq:euler}
                \end{equation}
                \begin{align}
                (a+b)^2 &= a^2 + 2ab + b^2 \label{eq:sum} \\
                (a-b)^2 &= a^2 - 2ab + b^2 \notag \\
                a^2 - b^2 &= (a+b)(a-b) \tag{A}
                \end{align}
                \begin{gather}
                \tr A = \sum_i a_{ii} \\
                \binom{n}{k} = \frac{n!}{k!\,(n-k)!}
                \end{gather}
                \[ A = \begin{pmatrix} 1 & 2 \\ 3 & 4 \end{pmatrix}, \quad
                   B = \begin{bmatrix} a & b \\ c & d \end{bmatrix}, \quad
                   \det\begin{vmatrix} a & b \\ c & d \end{vmatrix} = ad - bc \]
                \[ f(x) = \begin{cases} x^2 & \text{if } x \ge 0 \\ -x & \text{otherwise} \end{cases} \]
                \end{document}
                """;
            XElement document = TexLowering.Compile(source, out List<TexError> errors);
            t.Check(errors.Count == 0, $"the sample compiles clean: {string.Join("; ", errors.Select(e => $"{e.line}: {e.message}"))}");

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
            yield return t.Golden("Page", editor);
        }

        [A_XSDActionDependency("Tex.Hyphenation", "Test")]
        private static IEnumerator<int> TexHyphenation(TestContext t)
        {
            const string source = """
                \documentclass{article}
                \begin{document}
                Characteristically, internationalization requirements complicate straightforward implementations
                of typographical conventions; incomprehensibility follows whenever administrators misunderstand
                the representational responsibilities of documentation, notwithstanding considerable
                experimentation with alternative hyphenation algorithms, extraordinarily comprehensive
                dictionaries and counterintuitive transformations.
                \end{document}
                """;
            XElement document = TexLowering.Compile(source, out List<TexError> errors);
            t.Check(errors.Count == 0, $"the sample compiles clean: {string.Join("; ", errors.Select(e => $"{e.line}: {e.message}"))}");

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
            IReadOnlyList<TextLine> lines = ((BlockControl)Views(editor)[0]).Lines;
            t.Check(lines.Count > 2 && lines.Any(l => l.hyphen > 0f), $"some line of {lines.Count} ends at a hyphen");            yield return t.Golden("Page", editor);
        }

        [A_XSDActionDependency("Tex.SplitView.Recompile", "Test")]
        private static IEnumerator<int> SplitViewRecompile(TestContext t)
        {
            string body = string.Concat(Enumerable.Range(1, 60).Select(i => $"Paragraph {i} of the sample.\n\n"));
            TexEditorControl editor = ShowSplit(t, "\\documentclass{article}\n\\begin{document}\n" + body + "\\end{document}\n");
            yield return 2;

            RichTextDocument first = editor.preview.activeDocument;
            t.Check(first != null && first.blocks.Length == 60, $"the preview is typeset on open: {first?.blocks.Length}");
            editor.preview.SetScrollOffset(new Vector2(0f, 200f));
            yield return 1;
            float scrolled = editor.preview.GetScrollOffset().Y;

            editor.source.GoTo(2, "Paragraph 1".Length);
            editor.source.FocusCaret();
            yield return t.Type("Z");
            t.Check(ReferenceEquals(editor.preview.activeDocument, first), "no recompile while typing");

            double until = Engine.totalTime + TexEditorControl.recompileDelay + 0.1;
            for (int i = 0; i < 2000 && Engine.totalTime < until; i++) yield return 1;
            yield return 1;

            RichTextDocument second = editor.preview.activeDocument;
            string text = ((NoteBlock)second.blocks[0]).run.text;
            t.Check(!ReferenceEquals(second, first) && text.StartsWith("Paragraph 1Z"), $"recompiled after the pause: {text}");
            t.Check(scrolled > 0f && Math.Abs(editor.preview.GetScrollOffset().Y - scrolled) < 0.5f,
                $"the preview keeps its scroll: {scrolled} → {editor.preview.GetScrollOffset().Y}");

            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("Tex.SplitView.Errors", "Test")]
        private static IEnumerator<int> SplitViewErrors(TestContext t)
        {
            TexEditorControl editor = ShowSplit(t,
                "\\documentclass{article}\n\\begin{document}\n\\section{Errors}\nText with \\nosuch{} in it.\n\\end{document}\n");
            yield return 2;

            t.Check(!editor.errors.hidden, "the error list shows");
            List<ButtonControl> rows = editor.errors.children.OfType<StackPanelControl>().SelectMany(p => p.children.OfType<ButtonControl>()).ToList();
            t.Check(rows.Count == 1, $"one error row: {rows.Count}");
            yield return t.Golden("Split", editor);

            if (rows.Count > 0) yield return t.Click(rows[0]);
            yield return 1;
            DocumentControl content = Content(editor.source);
            int index = Array.IndexOf(editor.source.session.document.blocks, content.caretBlock?.note);
            t.Check(index == 3 && content.caretOffset == "Text with ".Length,
                $"the row puts the source caret at the error: block {index}, offset {content.caretOffset}");

            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("Tex.SplitView.ClickToSource", "Test")]
        private static IEnumerator<int> SplitViewClickToSource(TestContext t)
        {
            TexEditorControl editor = ShowSplit(t,
                "\\documentclass{article}\n\\begin{document}\nFirst paragraph.\n\nSecond\nparagraph.\n\\end{document}\n");
            yield return 30;

            BlockControl second = (BlockControl)Views(editor.preview)[1];
            LayoutRect r = second.arrangedRect;
            Vector2 at = new Vector2(r.x + 4f, r.y + r.height * 0.5f);
            yield return t.Click(second, at);
            yield return 1;
            DocumentControl content = Content(editor.source);
            int index = Array.IndexOf(editor.source.session.document.blocks, content.caretBlock?.note);
            t.Check(index != 4, $"a single click leaves the source caret: block {index}");

            yield return t.Click(second, at);
            yield return t.Click(second, at);
            yield return 1;
            index = Array.IndexOf(editor.source.session.document.blocks, content.caretBlock?.note);
            t.Check(index == 4 && content.caretOffset == 0, $"a double click on the second paragraph goes to its source line: block {index}, offset {content.caretOffset}");

            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("Tex.Pages", "Test")]
        private static IEnumerator<int> TexPages(TestContext t)
        {
            string words = string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 6));
            TexEditorControl editor = ShowSplit(t, $$"""
                \documentclass{article}
                \pagestyle{headings}
                \begin{document}
                \section{Introduction}\label{sec:intro}
                {{words}}See page~\pageref{sec:results}.
                \newpage
                \section{Results}\label{sec:results}
                {{words}}Back to page~\pageref{sec:intro}.
                \end{document}
                """);
            for (int i = 0; i < 12; i++) yield return 1;

            List<BlockControl> blocks = Views(editor.preview).OfType<BlockControl>().ToList();
            string Plain(BlockControl b) => b.text.Replace("­", "").Replace(' ', ' ');
            BlockControl first = blocks.First(b => Plain(b).Contains("See page"));
            BlockControl second = blocks.First(b => Plain(b).Contains("Back to page"));
            t.Check(Plain(first).EndsWith("See page 2.") && Plain(second).EndsWith("Back to page 1."),
                $"\\pageref takes its label's page once the preview is laid out: '{Plain(first)[^12..]}' '{Plain(second)[^16..]}'");
            t.Check(editor.preview.PageAt(Array.IndexOf(editor.preview.activeDocument.blocks, second.note), 0) == 2, "\\newpage puts Results on page 2");

            BlockControl results = blocks.First(b => Plain(b).Contains("Results"));
            editor.preview.SetScrollOffset(new Vector2(120f, results.arrangedRect.y - editor.preview.arrangedRect.y - 260f));
            yield return 2;
            yield return t.Golden("Break", editor.preview);
            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("Tex.FootnotePage", "Test")]
        private static IEnumerator<int> TexFootnotePage(TestContext t)
        {
            string words = string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 9));
            TexEditorControl editor = ShowSplit(t, $$"""
                \documentclass{article}
                \begin{document}
                \section{Notes at the foot}
                {{words}}A claim.\footnote{The first footnote, set in \emph{footnotesize}.}
                {{words}}Another.\footnote{A second one, with math $a^2+b^2=c^2$.}

                {{words}}{{words}}
                \end{document}
                """);
            yield return 4;

            List<BlockControl> blocks = Views(editor.preview).OfType<BlockControl>().ToList();
            BlockControl second = blocks.Last(b => b.insert?.footnote != null);
            t.Check(blocks.Count(b => b.insert?.footnote != null) == 2 && editor.preview.PageAt(Array.IndexOf(editor.preview.activeDocument.blocks, second.note), 0) == 1,
                "both footnotes are on page 1");
            editor.preview.SetScrollOffset(new Vector2(120f, second.arrangedRect.Bottom - editor.preview.arrangedRect.y - 420f));
            yield return 2;
            yield return t.Golden("Foot", editor.preview);
            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("Tex.FloatPage", "Test")]
        private static IEnumerator<int> TexFloatPage(TestContext t)
        {
            string folder = Path.Combine(Path.GetTempPath(), $"aurora-tex-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            GradientPng(Path.Combine(folder, "gradient.png"));

            string words = string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 9));
            string source = $$"""
                \documentclass{article}
                \usepackage{graphicx}
                \begin{document}
                \section{Placement}
                {{words}}

                \begin{figure}[t]\centering\includegraphics[width=0.5\textwidth]{gradient}\caption{Met mid-page, set at its top.}\end{figure}
                {{words}}

                \begin{figure}[p]\centering\includegraphics[height=10cm]{gradient}\caption{Too tall to share a page.}\end{figure}
                {{words}}{{words}}{{words}}

                {{words}}{{words}}{{words}}
                \end{document}
                """;
            XElement document = TexLowering.Compile(source, out List<TexError> errors, null, folder);
            t.Check(errors.Count == 0, $"the sample compiles clean: {string.Join("; ", errors.Select(e => $"{e.line}: {e.message}"))}");

            string path = Path.Combine(folder, "floatpage.axml");
            document.Save(path);
            DocumentEditorControl editor = new DocumentEditorControl
            {
                horizontalAlignment = HorizontalAlignment.Stretch,
                verticalAlignment = VerticalAlignment.Stretch
            };
            t.Show(editor);
            editor.LoadPath(path);
            yield return 2;

            List<Control> blocks = Views(editor);
            int top = blocks.FindIndex(b => b is BlockControl { insert.placement: "t" });
            int page = blocks.FindIndex(b => b is BlockControl { insert.placement: "p" });
            int heading = blocks.FindIndex(b => b is BlockControl { insert: null });
            t.Check(top >= 0 && editor.PageAt(top, 0) == 1 && blocks[top].arrangedRect.y < blocks[heading].arrangedRect.y, "the [t] figure heads page 1, above the section");
            t.Check(page >= 0 && editor.PageAt(page, 0) == 2
                && Enumerable.Range(0, blocks.Count).Where(i => blocks[i] is BlockControl { insert: null }).All(i => editor.PageAt(i, 0) != 2),
                "the [p] figure has page 2 to itself");
            DocumentControl content = Content(editor);
            PanelControl sheet = content.children.OfType<PanelControl>().Where(p => p.children.Count == 1 && p.children[0] is ContainerControl).ElementAt(1);
            float perMm = PageLayout.PxPerMm * content.zoom;
            float above = blocks[page].arrangedRect.y - (sheet.arrangedRect.y + content.page.marginTop * perMm);
            float below = sheet.arrangedRect.Bottom - content.page.marginBottom * perMm - blocks.Last(b => b is BlockControl { insert.placement: "p" }).arrangedRect.Bottom;
            t.Check(above > 10f && MathF.Abs(above - below) < 1f, $"a page of floats centres them: {above} above, {below} below");
            yield return t.Golden("Top", editor);

            editor.SetScrollOffset(new Vector2(0f, blocks[page].arrangedRect.y - editor.arrangedRect.y - 300f));
            yield return 2;
            yield return t.Golden("FloatPage", editor);
            t.Show(new StackPanelControl());
            Directory.Delete(folder, true);
        }

        [A_XSDActionDependency("Tex.Pdf.Structure", "Test")]
        private static IEnumerator<int> PdfStructure(TestContext t)
        {
            byte[]? file = null;
            IEnumerator<int> export = ExportFixture(t, bytes => file = bytes);
            while (export.MoveNext()) yield return export.Current;
            if (file == null) yield break;

            string pdf = System.Text.Encoding.Latin1.GetString(file);
            t.Check(pdf.StartsWith("%PDF-1.7"), "a PDF 1.7 header");
            int start = pdf.LastIndexOf("startxref\n", StringComparison.Ordinal);
            int xref = int.Parse(pdf.Substring(start + 10).Split('\n')[0]);
            t.Check(pdf.Substring(xref).StartsWith("xref\n"), $"startxref points at the table: {xref}");
            string[] rows = pdf.Substring(xref).Split('\n');
            int count = int.Parse(rows[1].Split(' ')[1]);
            bool aligned = true;
            for (int i = 1; i < count; i++)
                aligned &= pdf.Substring(int.Parse(rows[2 + i][..10])).StartsWith($"{i} 0 obj\n");
            t.Check(aligned, "every xref offset points at its object");

            t.Check(System.Text.RegularExpressions.Regex.Matches(pdf, @"/Type /Page ").Count == 2, "one page per sheet");
            t.Check(pdf.Contains("/MediaBox [0 0 612 792]"), "letter paper in points");
            t.Check(pdf.Contains("/Subtype /Type3"), "text in Type 3 fonts");
            t.Check(pdf.Contains("/Filter /DCTDecode"), "the JPEG passes through");
            t.Check(pdf.Contains("/SMask"), "the translucent PNG keeps its alpha");
        }

        [A_XSDActionDependency("Tex.Pdf.Text", "Test")]
        private static IEnumerator<int> PdfText(TestContext t)
        {
            byte[]? file = null;
            IEnumerator<int> export = ExportFixture(t, bytes => file = bytes);
            while (export.MoveNext()) yield return export.Current;
            if (file == null) yield break;

            string text = PdfPlainText(file);
            string shown = text.Replace('\n', '|');
            t.Check(text.StartsWith("1 Export"), $"the heading first: {shown}");
            t.Check(text.Contains("Hello world, a fine figure."), $"spaces kept and ligatures back as letters: {shown}");
            t.Check(text.Contains("Figure 1: Two"), $"the caption: {shown}");
            t.Check(text.Contains("see page 1."), $"\\pageref resolved before the export: {shown}");
            t.Check(text.EndsWith("2\n"), $"the page number last: {shown}");
            t.Check(!text.Contains("??"), "no unresolved reference");
        }

        [A_XSDActionDependency("Tex.FocusCommand", "Test")]
        private static IEnumerator<int> FocusCommand(TestContext t)
        {
            string folder = Path.Combine(Path.GetTempPath(), $"aurora-focus-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "doc.tex");
            File.WriteAllText(path, "\\documentclass{article}\n\\begin{document}\nFocused.\n\\end{document}\n");

            string before = SessionLayout.scope;
            SessionLayout.scope = "focus-command";
            WorkspaceControl workspace = new WorkspaceControl
            {
                paneDocument = "tab-pane",
                defaultDocument = "tab-pane",
                horizontalAlignment = HorizontalAlignment.Stretch,
                verticalAlignment = VerticalAlignment.Stretch
            };
            t.Show(workspace);
            workspace.LoadPane(workspace.AddPage("General", WorkspaceKind.General)).AddChild(SessionLayout.tabFactory(path, false));
            yield return 3;

            t.Check(UIEngine.activeControl is not DocumentEditorControl, "nothing holds the caret before the command");
            List<string> replies = new List<string>();
            ArctisAurora.Core.Commands.CommandConsole.Execute("Focus; Tex.ExportPdf", replies.Add);
            string target = Path.Combine(folder, "doc.pdf");
            for (int i = 0; i < 30 && !File.Exists(target); i++) yield return 1;
            t.Check(replies.Count > 0 && !replies[0].Contains("error"), $"Focus answers ok: {string.Join(" | ", replies)}");
            t.Check(UIEngine.activeControl is DocumentEditorControl, "the .tex source holds the caret");
            t.Check(File.Exists(target), "Tex.ExportPdf acts on the focused file");

            SessionLayout.scope = before;
            t.Show(new StackPanelControl());
            yield return 3;
            Directory.Delete(folder, true);
        }

        // Exports a two-page .tex with a heading, a ligature, a JPEG, a translucent PNG and a \pageref; hands over the file.
        private static IEnumerator<int> ExportFixture(TestContext t, Action<byte[]> done)
        {
            string folder = Path.Combine(Path.GetTempPath(), $"aurora-pdf-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            Image picture = new Image(40, 20);
            for (int y = 0; y < 20; y++)
                for (int x = 0; x < 40; x++)
                {
                    Span<byte> px = picture.Row(y).Slice(x * 4, 4);
                    px[0] = (byte)(x * 6);
                    px[1] = 80;
                    px[2] = 160;
                    px[3] = (byte)(x < 20 ? 255 : 0);
                }
            picture.SavePng(Path.Combine(folder, "pic.png"));
            using (SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32> jpeg = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(picture.Pixels, 40, 20))
                SixLabors.ImageSharp.ImageExtensions.SaveAsJpeg(jpeg, Path.Combine(folder, "photo.jpg"));
            string path = Path.Combine(folder, "doc.tex");
            File.WriteAllText(path, "\\documentclass{article}\n\\begin{document}\n\\section{Export}\nHello world, a fine figure.\\label{here}\n\n" +
                                    "\\begin{figure}[h]\\centering\\includegraphics{pic}\\includegraphics{photo}\\caption{Two}\\end{figure}\n\n" +
                                    "\\newpage\nSecond page, see page \\pageref{here}.\n\\end{document}\n");

            TexEditorControl editor = new TexEditorControl();
            t.Show(editor);
            editor.LoadPath(path);
            string? target = editor.ExportPdf();
            t.Check(target == Path.Combine(folder, "doc.pdf"), $"the PDF goes beside the .tex: {target}");
            for (int i = 0; i < 30 && !File.Exists(target); i++) yield return 1;
            t.Check(File.Exists(target), "the export waits for \\pageref, then writes");
            if (File.Exists(target)) done(File.ReadAllBytes(target!));

            t.Show(new StackPanelControl());
            Directory.Delete(folder, true);
        }

        // The text a PDF's pages draw, each code read back through its font's ToUnicode map.
        private static string PdfPlainText(byte[] file)
        {
            string pdf = System.Text.Encoding.Latin1.GetString(file);
            Dictionary<int, (string head, byte[]? data)> objects = new Dictionary<int, (string, byte[]?)>();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(pdf, @"(\d+) 0 obj\n"))
            {
                int body = m.Index + m.Length;
                int end = pdf.IndexOf("\nendobj", body, StringComparison.Ordinal);
                string whole = pdf.Substring(body, end - body);
                int stream = whole.IndexOf(">>\nstream\n", StringComparison.Ordinal);
                if (stream < 0) { objects[int.Parse(m.Groups[1].Value)] = (whole, null); continue; }
                byte[] raw = System.Text.Encoding.Latin1.GetBytes(whole.Substring(stream + 10, whole.Length - stream - 10 - "\nendstream".Length));
                bool packed = whole.Substring(0, stream).Contains("/FlateDecode");
                byte[] data = raw;
                if (packed)
                {
                    using MemoryStream output = new MemoryStream();
                    using (System.IO.Compression.ZLibStream zlib = new System.IO.Compression.ZLibStream(new MemoryStream(raw), System.IO.Compression.CompressionMode.Decompress))
                        zlib.CopyTo(output);
                    data = output.ToArray();
                }
                objects[int.Parse(m.Groups[1].Value)] = (whole.Substring(0, stream + 2), data);
            }

            Dictionary<int, Dictionary<int, string>> maps = new Dictionary<int, Dictionary<int, string>>();
            Dictionary<int, string> MapOf(int font)
            {
                if (maps.TryGetValue(font, out Dictionary<int, string>? known)) return known;
                Dictionary<int, string> map = new Dictionary<int, string>();
                int cmap = int.Parse(System.Text.RegularExpressions.Regex.Match(objects[font].head, @"/ToUnicode (\d+) 0 R").Groups[1].Value);
                string body = System.Text.Encoding.Latin1.GetString(objects[cmap].data!);
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(body, @"<([0-9A-F]{2})> <([0-9A-F]+)>"))
                {
                    string hex = m.Groups[2].Value;
                    map[Convert.ToInt32(m.Groups[1].Value, 16)] = string.Concat(Enumerable.Range(0, hex.Length / 4).Select(i => (char)Convert.ToInt32(hex.Substring(i * 4, 4), 16)));
                }
                return maps[font] = map;
            }

            System.Text.StringBuilder text = new System.Text.StringBuilder();
            foreach ((string head, byte[]? data) page in objects.Values.Where(o => o.head.Contains("/Type /Page ")).ToList())
            {
                int contents = int.Parse(System.Text.RegularExpressions.Regex.Match(page.head, @"/Contents (\d+) 0 R").Groups[1].Value);
                string content = System.Text.Encoding.Latin1.GetString(objects[contents].data!);
                int font = -1;
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(content, @"/F(\d+) [\d.]+ Tf|<([0-9A-F]{2})> Tj"))
                {
                    if (m.Groups[1].Success) font = int.Parse(m.Groups[1].Value);
                    else text.Append(MapOf(font).GetValueOrDefault(Convert.ToInt32(m.Groups[2].Value, 16), "\uFFFD"));
                }
                text.Append('\n');
            }
            return text.ToString();
        }

        // A .tex opened in the split view from a temporary file.
        private static TexEditorControl ShowSplit(TestContext t, string source)
        {
            string path = Path.Combine(Path.GetTempPath(), $"aurora-split-{Guid.NewGuid():N}.tex");
            File.WriteAllText(path, source);
            TexEditorControl editor = new TexEditorControl();
            t.Show(editor);
            editor.LoadPath(path);
            File.Delete(path);
            return editor;
        }

        private static DocumentControl Content(DocumentEditorControl editor) =>
            editor.children.OfType<DocumentControl>().First();

        // The controls showing a note's entries, in note order: blocks, and tables out of their viewports.
        private static List<Control> Views(DocumentEditorControl editor) => Content(editor).children.OfType<Control>()
            .Select(c => c is ScrollableControl viewport && viewport.children.Count > 0 && viewport.children[0] is TableControl table ? table : c)
            .Where(c => c is BlockControl or TableControl).ToList();

        // A 120x60 red-green ramp over constant blue.
        private static void GradientPng(string path)
        {
            Image picture = new Image(120, 60);
            for (int y = 0; y < 60; y++)
                for (int x = 0; x < 120; x++)
                {
                    Span<byte> px = picture.Row(y).Slice(x * 4, 4);
                    px[0] = (byte)(x * 2);
                    px[1] = (byte)(y * 4);
                    px[2] = 160;
                    px[3] = 255;
                }
            picture.SavePng(path);
        }
    }
}
