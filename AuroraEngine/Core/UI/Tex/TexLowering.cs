using ArctisAurora.Core.Diagnostics;
using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Registry.Assets;
using ArctisAurora.Core.Tex;
using ArctisAurora.Core.Testing;
using ArctisAurora.Core.UI.Document;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.EngineWork.Registry;
using ArctisAurora.Core.Imaging;
using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace ArctisAurora.Core.UI.Tex
{
    // A .tex source typeset and lowered to a <Document> tree: a paragraph per block, a run per stretch of one style.
    public static class TexLowering
    {
        public const string RomanFont = "latin-modern";
        public const string MonoFont = "latin-modern-mono";
        public const string Mono12Font = "latin-modern-mono-12";
        public const string MathFont = "latin-modern-math";
        public const string TermesFont = "tex-gyre-termes";
        public const string CarlitoFont = "carlito";

        private static readonly LogChannel Log = LogChannel.For("Tex");

        // fontspec names of the bundled faces, and the bundled faces standing in for fonts that are not there
        private static readonly Dictionary<string, string> aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["TeX Gyre Termes"] = TermesFont, ["Carlito"] = CarlitoFont,
            ["Latin Modern Roman"] = RomanFont, ["Latin Modern Mono"] = MonoFont
        };
        private static readonly Dictionary<string, string> standIns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Times New Roman"] = TermesFont, ["Times"] = TermesFont, ["Nimbus Roman"] = TermesFont, ["Calibri"] = CarlitoFont
        };

        // the roman, sans and mono faces of the document being lowered, its Path= files; fontspec names resolved this compile, and names never found
        [ThreadStatic] private static string? roman, sans, mono;
        [ThreadStatic] private static Dictionary<string, (string regular, string? bold, string? italic, string? boldItalic)>? files;
        [ThreadStatic] private static Dictionary<string, string?>? named;
        [ThreadStatic] private static HashSet<string>? missing;
        private static string Roman => roman ?? RomanFont;
        private static string Mono => mono ?? MonoFont;

        internal const float Unity = 65536f;
        internal const float PxPerPt = 96f / 72.27f;
        private const float MmPerPt = 25.4f / 72.27f;

        // exact glyph widths, as TeX breaks lines
        private const int AdvanceGrid = 0;

        // \tabcolsep, either side of a column, and the narrowest column holding anything
        private const float TabColSep = 6f * PxPerPt;
        private const float MinColumn = 28f;

        // each list depth's indent in em of \normalsize, so depth n times it sums \leftmargini to the nth margin
        private static readonly float[] listIndents = { 2.5f, 4.7f / 2f, 6.57f / 3f, 8.27f / 4f };

        // blockLines, when given, gets each block's source line; 0 = none. folder is where pictures and .bib files are found.
        public static XElement Compile(string source, out List<TexError> errors, List<int>? blockLines = null, string? folder = null)
        {
            TexAtlasMetrics metrics = new TexAtlasMetrics();
            TexTypesetter typesetter = new TexTypesetter(source, metrics, folder);
            metrics.typesetter = typesetter;
            typesetter.expander.mathOnly = IsMathCommand;
            typesetter.Run();
            errors = typesetter.errors;
            return Lower(typesetter, blockLines);
        }

        private static bool IsMathCommand(string name) => MathSymbols.commands.ContainsKey(name) || MathSymbols.bigOps.ContainsKey(name)
            || MathSymbols.operatorNames.ContainsKey(name) || MathSymbols.accents.ContainsKey(name) || MathSymbols.delimiters.ContainsKey(name);

        public static XElement Lower(TexTypesetter typesetter, List<int>? blockLines = null)
        {
            XElement root = new XElement("Document");
            named?.Clear();
            Faces(typesetter);
            float normal = Px(typesetter.normalSize);
            Dictionary<int, float> headings = HeadingSizes(typesetter.vlist);
            root.Add(Layout(typesetter, normal, headings));
            Hyphenate(typesetter.vlist, typesetter.hyphenate, typesetter.hyphenation);
            foreach (TexFootnote note in typesetter.footnotes)
                Hyphenate(note.vlist, typesetter.hyphenate, typesetter.hyphenation);

            float? skipStretch = typesetter.displaySkipStretch;
            Spacing spacing = new Spacing
            {
                indent = typesetter.parIndent * PxPerPt,
                display = TexTypesetter.displaySkips[typesetter.sizeOption] * (skipStretch ?? 1f) * PxPerPt,
                belowShort = TexTypesetter.belowDisplayShortSkips[typesetter.sizeOption] * (skipStretch ?? 1f) * PxPerPt,
                displayShrink = skipStretch != null ? 0f : TexTypesetter.displaySkipShrinks[typesetter.sizeOption] * PxPerPt,
                belowShortShrink = skipStretch != null ? 0f : TexTypesetter.belowDisplayShortSkipShrinks[typesetter.sizeOption] * PxPerPt,
                skipStretch = skipStretch ?? 1f,
                emptyLine = normal * BodyLineHeight(typesetter, normal),
                lineHeight = BodyLineHeight(typesetter, normal)
            };
            foreach (TexNode node in typesetter.vlist)
                LowerVertical(node, root, normal, headings, blockLines, spacing);
            return root;
        }

        // Each footnote the marks in nodes anchor, as a <Footnote> of its blocks after them; only in the document's own flow.
        private static void Footnotes(IEnumerable<TexNode> nodes, XElement root, float normal, Dictionary<int, float> headings, List<int>? blockLines)
        {
            if (root.Name.LocalName != "Document") return;
            foreach (TexMathNode mark in nodes.OfType<TexMathNode>())
            {
                if (mark.note == null) continue;
                XElement group = new XElement("Footnote", new XAttribute("Id", mark.note.id));
                Spacing inNote = new Spacing();
                foreach (TexNode node in mark.note.vlist)
                    LowerVertical(node, group, normal, headings, blockLines, inNote);
                root.Add(group);
            }
        }

        // A float as a <Float Kind Placement> of its blocks; the space and page break pending stay with the next block.
        private static void Float(TexFloat f, XElement root, float normal, Dictionary<int, float> headings, List<int>? blockLines, Spacing spacing)
        {
            XElement group = new XElement("Float", new XAttribute("Kind", f.kind), new XAttribute("Placement", f.placement));
            float stretch = f.placement.Contains('H') ? 1f : spacing.skipStretch;
            Spacing inFloat = new Spacing
            {
                indent = spacing.indent, display = spacing.display / stretch, belowShort = spacing.belowShort / stretch, emptyLine = spacing.emptyLine, lineHeight = spacing.lineHeight
            };
            foreach (TexNode node in f.vlist)
                LowerVertical(node, group, normal, headings, blockLines, inFloat);
            if (group.HasElements) root.Add(group);
        }

        // the vertical space waiting for the next block, \parindent, the display skips and an empty line, px; the document's line height
        private sealed class Spacing
        {
            public float pending;
            // the last skip in pending, as TeX's \lastskip, which \addvspace compares against
            public float last;
            // how far pending and its last skip may shrink, px
            public float pendingShrink;
            public float lastShrink;
            public float indent;
            public float display;
            public float belowShort;
            public float displayShrink;
            public float belowShortShrink;
            public float skipStretch = 1f;
            public float emptyLine;
            public float lineHeight;

            // the page break waiting for the next block: Page, Clear or none
            public string? pageBreak;
        }

        // A block or table under the space and page break pending above it.
        private static void Place(XElement root, XElement element, Spacing spacing)
        {
            if (spacing.pending != 0f) element.SetAttributeValue("SpaceBefore", Format(spacing.pending));
            if (spacing.pendingShrink > 0f) element.SetAttributeValue("ShrinkBefore", Format(spacing.pendingShrink));
            if (spacing.pageBreak != null) element.SetAttributeValue("PageBreak", spacing.pageBreak);
            spacing.pending = spacing.last = spacing.pendingShrink = spacing.lastShrink = 0f;
            spacing.pageBreak = null;
            root.Add(element);
        }

        // what Runs leaves on the tree for PageRefs: a \pageref's run, and a label's block and offset
        private sealed record PageRefNote(string key);
        private sealed record LabelSite(string key, int offset);
        private sealed record BoxFitNote(XElement spacer, float width, float fixedWidth, float share, TexStyle style);

        // Every \pageref run and label site in a lowered tree, labels by top-level block index; a label in a table stands at the table's top.
        public static TexPageRefs PageRefs(XElement root)
        {
            TexPageRefs found = new TexPageRefs();
            int index = 0;
            foreach (XElement entry in root.Elements().SelectMany(e => e.Name.LocalName is "Footnote" or "Float" ? e.Elements() : new[] { e })
                         .Where(e => e.Name.LocalName is "Block" or "Table"))
            {
                foreach (XElement e in entry.DescendantsAndSelf())
                {
                    foreach (PageRefNote note in e.Annotations<PageRefNote>())
                        found.refs.Add((e, note.key));
                    foreach (BoxFitNote fit in e.Annotations<BoxFitNote>())
                        found.fits.Add((e, fit.spacer, fit.width - fit.fixedWidth, fit.share, fit.style));
                    foreach (LabelSite site in e.Annotations<LabelSite>())
                        found.labels.TryAdd(site.key, (index, e == entry ? site.offset : 0));
                }
                index++;
            }
            return found;
        }

        #region ---- layout ----
        private static XElement Layout(TexTypesetter typesetter, float normal, Dictionary<int, float> headings)
        {
            bool a4 = typesetter.paper == "a4";
            bool custom = typesetter.customWidth != null || typesetter.customHeight != null;
            float side = (typesetter.paperWidth - TexTypesetter.textWidths[typesetter.sizeOption] * MmPerPt) / 2f;
            float top = (typesetter.paperHeight - TexTypesetter.TextHeight * MmPerPt) / 2f;

            XElement layout = new XElement("DocumentLayout",
                new XAttribute("LineHeight", Format(BodyLineHeight(typesetter, normal))),
                new XAttribute("BaselineDepth", Format((typesetter.lineHeight - 1f) * typesetter.normalSize / Unity * PxPerPt)),
                new XAttribute("LineSkip", Format(PxPerPt)),
                typesetter.unicodeMath ? null : new XAttribute("MathRules", "Tfm"),
                new XAttribute("BlockSpacing", "0"),
                new XAttribute("ListIndent", Format(normal * listIndents[0])),
                new XAttribute("OptimalBreaks", "true"),
                new XAttribute("Tolerance", typesetter.tolerance),
                new XAttribute("Pretolerance", typesetter.pretolerance),
                new XAttribute("AdvanceGrid", AdvanceGrid),
                typesetter.emergencyStretch > 0f ? new XAttribute("EmergencyStretch", Format(typesetter.emergencyStretch * PxPerPt)) : null,
                new XAttribute("WidowControl", "true"),
                new XElement("Page",
                    new XAttribute("Mode", "Paged"),
                    new XAttribute("Size", custom ? "Custom" : a4 ? "A4" : "Letter"),
                    custom ? new XAttribute("Width", Format(typesetter.paperWidth)) : null,
                    custom ? new XAttribute("Height", Format(typesetter.paperHeight)) : null,
                    new XAttribute("MarginTop", Format(typesetter.marginTop ?? top)),
                    new XAttribute("MarginBottom", Format(typesetter.marginBottom ?? top)),
                    new XAttribute("MarginLeft", Format(typesetter.marginLeft ?? side)),
                    new XAttribute("MarginRight", Format(typesetter.marginRight ?? side)),
                    new XAttribute("Style", typesetter.pageStyle),
                    new XAttribute("FootnoteSkip", Format(TexTypesetter.footnoteSkips[typesetter.sizeOption] * MmPerPt)),
                    new XAttribute("FloatSep", Format(TexTypesetter.floatSeps[typesetter.sizeOption] * MmPerPt)),
                    new XAttribute("TextFloatSep", Format(TexTypesetter.TextFloatSep * MmPerPt)),
                    new XAttribute("InTextSep", Format(TexTypesetter.floatSeps[typesetter.sizeOption] * MmPerPt)),
                    new XAttribute("InTextSepShrink", Format(TexTypesetter.inTextSepShrinks[typesetter.sizeOption] * MmPerPt)),
                    typesetter.fitHeight is float fit ? new XAttribute("FitHeight", Format(fit)) : null,
                    typesetter.pageStyles.Select(s => PageStyle(s.Key, s.Value, typesetter))),
                Style("Text", normal, Roman),
                Style("Quote", normal, Roman),
                Style("Code", normal, Mono));
            foreach (KeyValuePair<int, float> heading in headings.OrderBy(h => h.Key))
                layout.Add(Style("Heading" + heading.Key, heading.Value, Roman));
            return layout;
        }

        // A page style's slots as text with {page}/{leftmark}/{rightmark}, each in its first character's font.
        private static XElement PageStyle(string name, TexPageStyle style, TexTypesetter typesetter)
        {
            XElement element = new XElement("PageStyle", new XAttribute("Name", name));
            if (typesetter.headSep is float headSep) element.SetAttributeValue("HeadSep", Format(headSep));
            if (typesetter.footSkip is float footSkip) element.SetAttributeValue("FootSkip", Format(footSkip));
            if (style.headRule > 0f) element.SetAttributeValue("HeadRule", Format(style.headRule * PxPerPt));
            if (style.footRule > 0f) element.SetAttributeValue("FootRule", Format(style.footRule * PxPerPt));
            foreach (KeyValuePair<string, TexParagraph> slot in style.slots)
            {
                StringBuilder text = new StringBuilder();
                foreach (TexNode node in slot.Value.list)
                    if (node is TexChar c) text.Append(c.ch switch
                    {
                        TexTypesetter.PageField => "{page}",
                        TexTypesetter.LeftMarkField => "{leftmark}",
                        TexTypesetter.RightMarkField => "{rightmark}",
                        _ => c.ch.ToString()
                    });
                    else if (node is TexGlueNode or TexKern && text.Length > 0 && text[^1] != ' ') text.Append(' ');
                string written = text.ToString().Trim();
                if (written.Length == 0 || slot.Value.list.OfType<TexChar>().FirstOrDefault() is not TexChar first) continue;

                XElement placed = new XElement("Slot", new XAttribute("Place", slot.Key), new XAttribute("Text", written),
                    new XAttribute("FontName", FontFor(first.style.font)),
                    new XAttribute("FontSize", (int)MathF.Round(Px(first.style.font.size))));
                if (first.style.font.bold) placed.SetAttributeValue("Bold", "true");
                if (first.style.font.italic) placed.SetAttributeValue("Italic", "true");
                element.Add(placed);
            }
            return element;
        }

        private static XElement Style(string type, float size, string font) =>
            new XElement("TextStyle", new XAttribute("Type", type), new XAttribute("FontSize", Format(size)), new XAttribute("FontName", font));

        // each heading level at the size its first heading is set in
        private static Dictionary<int, float> HeadingSizes(List<TexNode> vlist)
        {
            Dictionary<int, float> sizes = new Dictionary<int, float>();
            foreach (TexNode node in vlist)
                if (node is TexParagraph { style.kind: TexParKind.Heading } p && !sizes.ContainsKey(Level(p)))
                    if (p.list.OfType<TexChar>().FirstOrDefault() is TexChar first) sizes[Level(p)] = Px(first.style.font.size);
            return sizes;
        }
        #endregion

        #region ---- blocks ----
        private static void LowerVertical(TexNode node, XElement root, float normal, Dictionary<int, float> headings, List<int>? blockLines, Spacing spacing)
        {
            switch (node)
            {
                case TexVGlue g:
                    float width = g.glue.width / Unity * PxPerPt;
                    float last = spacing.last;
                    float shrink = g.glue.shrinkOrder == 0 ? g.glue.shrink / Unity * PxPerPt : 0f;
                    bool adds = !g.merge || last == 0f;
                    float added = adds ? width : last < width ? width - last : width < 0f && last >= 0f ? width : 0f;
                    float shrinkAdded = adds || width < 0f && last >= 0f ? shrink : last < width ? shrink - spacing.lastShrink : 0f;
                    spacing.pending += added;
                    spacing.last = adds ? width : last + added;
                    spacing.pendingShrink += shrinkAdded;
                    spacing.lastShrink = adds ? shrink : spacing.lastShrink + shrinkAdded;
                    break;
                case TexParagraph p:
                    List<List<TexNode>> lines = Lines(p.list);
                    bool placed = false;
                    for (int i = 0; i < lines.Count; i++)
                    {
                        List<List<TexNode>> pieces = Displays(lines[i]);
                        for (int k = 0; k < pieces.Count; k++)
                        {
                            bool display = pieces[k] is [TexMathNode { display: true }];
                            if (!display && !pieces[k].Any(n => n is not TexGlueNode and not TexKern and not TexPenalty and not TexLabelMark and not TexFloat)) continue;
                            if (!display && pieces[k].OfType<TexRuleBox>().FirstOrDefault() is TexRuleBox rule
                                && pieces[k].All(n => n is TexRuleBox or TexGlueNode or TexKern or TexPenalty or TexLabelMark))
                            {
                                float size = Px(rule.style.font.size);
                                XElement run = new XElement("Run");
                                Size(run, size);
                                XElement ruled = new XElement("Block", new XAttribute("StylingType", "Rule"),
                                    new XAttribute("Weight", Format(rule.height / Unity * PxPerPt)), run);
                                float multiple = p.baselineSkip / Unity * PxPerPt / size;
                                if (p.baselineSkip != 0 && MathF.Abs(multiple - spacing.lineHeight) > 0.005f) ruled.SetAttributeValue("LineHeight", Format(multiple));
                                Place(root, ruled, spacing);
                                blockLines?.Add(p.line);
                                continue;
                            }

                            bool skips = p.style.leftSkip != 0 || p.style.rightSkip != 0, opening = i == 0 && k == 0;
                            float hang = skips && opening ? TakeLeading(pieces[k]) : 0f;
                            XElement block = Block(p, pieces[k], opening, normal, headings);
                            LineHeight(block, p, pieces[k], spacing.lineHeight);
                            if (p.style.indent && opening && !skips) block.SetAttributeValue("Indent", Format(spacing.indent));
                            if (skips) Skips(block, p.style, opening ? (p.style.indent ? p.style.indentWidth / Unity * PxPerPt : 0f) + hang : 0f);
                            bool starts = display && !placed;
                            if (starts)
                            {
                                Place(root, new XElement("Block", new XAttribute("KeepWithNext", "true"), new XElement("Run")), spacing);
                                blockLines?.Add(p.line);
                            }
                            else if (display && spacing.display > spacing.pending)
                            {
                                spacing.pending = spacing.display;
                                spacing.pendingShrink = spacing.displayShrink;
                            }
                            if (!placed)
                            {
                                if (p.pageStyle != null) block.SetAttributeValue("PageStyle", p.pageStyle);
                                if (p.markLeft != null) block.SetAttributeValue("MarkLeft", p.markLeft);
                                if (p.markRight != null) block.SetAttributeValue("MarkRight", p.markRight);
                                placed = true;
                            }
                            Place(root, block, spacing);
                            if (display)
                            {
                                spacing.pending = starts ? spacing.belowShort : spacing.display;
                                spacing.pendingShrink = starts ? spacing.belowShortShrink : spacing.displayShrink;
                            }
                            blockLines?.Add(p.line);
                        }
                    }
                    if (lines.Count > 1 && lines[^1].All(n => n is TexGlueNode or TexKern or TexPenalty))
                        spacing.pending += p.baselineSkip > 0 ? p.baselineSkip / Unity * PxPerPt : spacing.emptyLine;
                    Footnotes(p.list, root, normal, headings, blockLines);
                    foreach (TexFloat f in p.list.OfType<TexFloat>())
                        Float(f, root, normal, headings, blockLines, spacing);
                    break;
                case TexFloat f:
                    Float(f, root, normal, headings, blockLines, spacing);
                    break;
                case TexPageBreak b:
                    spacing.pageBreak = b.clear || spacing.pageBreak == "Clear" ? "Clear" : "Page";
                    break;
                case TexRuleNode:
                    Place(root, new XElement("Block", new XAttribute("StylingType", "Rule")), spacing);
                    blockLines?.Add(0);
                    break;
                case TexTable t:
                    Place(root, Table(t, normal, headings), spacing);
                    blockLines?.Add(t.line);
                    Footnotes(t.rows.SelectMany(r => r).SelectMany(c => Flat(c.vlist)).OfType<TexParagraph>().SelectMany(p => p.list), root, normal, headings, blockLines);
                    break;
            }
        }

        // The glue, kerns, penalties and empty boxes a line opens with, taken off it, px: under \leftskip they hang the first line.
        private static float TakeLeading(List<TexNode> line)
        {
            float px = 0f;
            for (int n = 0; n < line.Count;)
                switch (line[n])
                {
                    case TexGlueNode { interword: false, leader: null, glue.stretchOrder: 0 } g:
                        px += g.glue.width / Unity * PxPerPt;
                        line.RemoveAt(n);
                        break;
                    case TexKern k:
                        px += k.width / Unity * PxPerPt;
                        line.RemoveAt(n);
                        break;
                    case TexPenalty:
                    case TexHBox { width: < 0, list.Count: 0 }:
                        line.RemoveAt(n);
                        break;
                    case TexLabelMark:
                        n++;
                        break;
                    default:
                        return px;
                }
            return px;
        }

        // \leftskip and \rightskip as the block's indents, \parfillskip under \rightskip as its last line's, and the first line's indent.
        private static void Skips(XElement block, TexParStyle s, float indent)
        {
            if (s.leftSkip != 0) block.SetAttributeValue("LeftIndent", Format(s.leftSkip / Unity * PxPerPt));
            if (s.rightSkip != 0) block.SetAttributeValue("RightIndent", Format(s.rightSkip / Unity * PxPerPt));
            if (s.rightSkip != 0 && s.parFillSkip != 0)
                block.SetAttributeValue("RightIndentLast", Format(MathF.Max(0f, (s.rightSkip + s.parFillSkip) / Unity * PxPerPt)));
            if (MathF.Abs(indent) >= 0.01f) block.SetAttributeValue("Indent", Format(indent));
        }

        // The body's \baselineskip as a multiple of the whole-pixel \normalsize the runs are set in.
        private static float BodyLineHeight(TexTypesetter typesetter, float normal) => typesetter.lineHeight * typesetter.normalSize / Unity * PxPerPt / normal;

        // A piece's own line height when its \baselineskip differs from the document's: the skip over its largest font size.
        private static void LineHeight(XElement block, TexParagraph p, List<TexNode> piece, float document)
        {
            int size = 0;
            foreach (TexNode node in piece)
                if (node is TexChar c && c.style.font.size > size) size = c.style.font.size;
            if (size == 0 || p.baselineSkip == 0) return;
            float multiple = p.baselineSkip / Unity * PxPerPt / Px(size);
            if (MathF.Abs(multiple - document) > 0.005f) block.SetAttributeValue("LineHeight", Format(multiple));
        }

        // A line cut around its display formulas, each display a piece of its own: TeX breaks the text either side separately.
        private static List<List<TexNode>> Displays(List<TexNode> line)
        {
            List<List<TexNode>> pieces = new List<List<TexNode>> { new List<TexNode>() };
            foreach (TexNode node in line)
                if (node is TexMathNode { display: true })
                {
                    pieces.Add(new List<TexNode> { node });
                    pieces.Add(new List<TexNode>());
                }
                else pieces[^1].Add(node);
            return pieces;
        }

        // a paragraph split at its forced breaks
        private static List<List<TexNode>> Lines(List<TexNode> list)
        {
            List<List<TexNode>> lines = new List<List<TexNode>> { new List<TexNode>() };
            foreach (TexNode node in list)
                if (node is TexPenalty { penalty: <= TexPenalty.Forced }) lines.Add(new List<TexNode>());
                else lines[^1].Add(node);
            return lines;
        }

        private static XElement Block(TexParagraph p, List<TexNode> line, bool first, float normal, Dictionary<int, float> headings)
        {
            XElement block = new XElement("Block");
            if (p.interlinePenalty >= 5000) block.SetAttributeValue("KeepTogether", "true");
            TexParStyle s = p.style;
            string font = Roman;
            float size = normal;
            switch (s.kind)
            {
                case TexParKind.Heading:
                    block.SetAttributeValue("StylingType", "Heading" + Level(p));
                    block.SetAttributeValue("KeepWithNext", "true");
                    size = headings.GetValueOrDefault(Level(p), normal);
                    break;
                case TexParKind.Code:
                    block.SetAttributeValue("StylingType", "Code");
                    font = Mono;
                    break;
                case TexParKind.Quote:
                    block.SetAttributeValue("StylingType", "Quote");
                    break;
            }
            if (s.align != TexAlign.Left && s.kind != TexParKind.Code) block.SetAttributeValue("Align", s.align.ToString());
            if (p.strut) block.SetAttributeValue("Strut", "true");

            if (first && s.item && s.list is TexListKind.Itemize or TexListKind.Enumerate)
            {
                block.SetAttributeValue("List", "Bullet");
                block.SetAttributeValue("Level", s.listDepth - 1);
                block.SetAttributeValue("Marker", s.marker ?? Marker(s.list, s.listKindDepth));
                if (s.leftMargin > 0) block.SetAttributeValue("ListIndent", Format(s.leftMargin / Unity * PxPerPt));
                else if (s.listDepth > 1) block.SetAttributeValue("ListIndent", Format(normal * listIndents[Math.Min(s.listDepth, listIndents.Length) - 1]));
                if (s.list == TexListKind.Enumerate) block.SetAttributeValue("Start", s.itemNumber);
            }

            Runs(line, block, font, size, s.kind == TexParKind.Code);
            if (!block.HasElements) block.Add(new XElement("Run"));
            return block;
        }

        private static int Level(TexParagraph p) => Math.Clamp(p.style.level, 1, 6);

        private static string Marker(TexListKind kind, int depth) => kind == TexListKind.Enumerate
            ? ((depth - 1) % 4) switch { 0 => "Decimal", 1 => "LowerAlpha", 2 => "LowerRoman", _ => "UpperAlpha" }
            : ((depth - 1) % 4) switch { 0 => "Disc", 1 => "Circle", 2 => "Square", _ => "SquareOutline" };

        // Columns at their widest single-column cell, p{} columns at their width, a merged cell widening its last column.
        private static XElement Table(TexTable t, float normal, Dictionary<int, float> headings)
        {
            int count = Math.Max(t.columns.Count, t.rows.Count == 0 ? 1 : t.rows.Max(r => r.Sum(c => c.span)));
            float[] widths = new float[count];
            List<List<(TexTableCell cell, List<TexNode> nodes, float width)>> rows = t.rows.Select(r => r.Select(c =>
            {
                List<TexNode> nodes = Flat(c.vlist).ToList();
                return (c, nodes, Width(nodes));
            }).ToList()).ToList();

            foreach (List<(TexTableCell cell, List<TexNode> nodes, float width)> row in rows)
            {
                int c = 0;
                foreach ((TexTableCell cell, _, float width) in row)
                {
                    if (cell.span == 1 && c < count) widths[c] = MathF.Max(widths[c], width);
                    c += cell.span;
                }
            }
            for (int i = 0; i < t.columns.Count; i++)
                if (t.columns[i].width > 0) widths[i] = t.columns[i].width / Unity * PxPerPt;
            foreach (List<(TexTableCell cell, List<TexNode> nodes, float width)> row in rows)
            {
                int c = 0;
                foreach ((TexTableCell cell, _, float width) in row)
                {
                    int end = Math.Min(c + cell.span, count);
                    if (cell.span > 1 && end > c)
                    {
                        float have = widths[c..end].Sum() + 2f * TabColSep * (end - c - 1);
                        if (width > have) widths[end - 1] += width - have;
                    }
                    c += cell.span;
                }
            }

            XElement table = new XElement("Table", new XAttribute("Borders", "false"), new XAttribute("Padding", $"{Format(MathF.Round(TabColSep, 2))} 0"));
            if (t.align != TexAlign.Left) table.SetAttributeValue("Align", t.align.ToString());
            else if (t.indent > 0) table.SetAttributeValue("Indent", Format(t.indent / Unity * PxPerPt));
            if (t.opens) table.SetAttributeValue("Axis", Format(0.25f * normal));
            for (int i = 0; i < count; i++)
            {
                XElement column = new XElement("Column", new XAttribute("Width", Format(MathF.Max(widths[i] > 0f ? MinColumn : 0f, MathF.Ceiling(widths[i] + 2f * TabColSep)))));
                int left = i < t.vrules.Count ? t.vrules[i] : 0;
                int right = i == t.columns.Count - 1 && t.vrules.Count > t.columns.Count ? t.vrules[t.columns.Count] : 0;
                if (left > 0) column.SetAttributeValue("RuleLeft", left > 1 ? "Double" : "Plain");
                if (right > 0) column.SetAttributeValue("RuleRight", right > 1 ? "Double" : "Plain");
                table.Add(column);
            }

            Dictionary<(int row, int cell, bool above), (TableRule kind, float width, RuleTrim trim)> rules = CellRules(t);
            for (int r = 0; r < rows.Count; r++)
            {
                XElement written = new XElement("Row");
                for (int i = 0; i < rows[r].Count; i++)
                {
                    (TexTableCell cell, List<TexNode> nodes, _) = rows[r][i];
                    XElement element = new XElement("Cell");
                    if (cell.span > 1) element.SetAttributeValue("ColumnSpan", cell.span);
                    foreach (bool above in new[] { true, false })
                    {
                        if (!rules.TryGetValue((r, i, above), out (TableRule kind, float width, RuleTrim trim) rule)) continue;
                        element.SetAttributeValue(above ? "RuleAbove" : "RuleBelow", rule.width > 0f ? $"{rule.kind} {Format(rule.width)}" : rule.kind.ToString());
                        if (rule.trim != RuleTrim.None) element.SetAttributeValue(above ? "TrimAbove" : "TrimBelow", rule.trim.ToString());
                    }
                    Spacing inCell = new Spacing();
                    foreach (TexNode node in nodes)
                        LowerVertical(node, element, normal, headings, null, inCell);
                    written.Add(element);
                }
                table.Add(written);
            }
            return table;
        }

        // Each rule on the cells it runs along: above its row, or under the last; two plain rules at one place are a double.
        private static Dictionary<(int row, int cell, bool above), (TableRule kind, float width, RuleTrim trim)> CellRules(TexTable t)
        {
            Dictionary<(int, int, bool), (TableRule kind, float width, RuleTrim trim)> rules = new Dictionary<(int, int, bool), (TableRule, float, RuleTrim)>();
            if (t.rows.Count == 0) return rules;

            foreach (TexTableRule rule in t.rules)
            {
                bool above = rule.row < t.rows.Count;
                int r = above ? rule.row : t.rows.Count - 1;
                TableRule kind = rule.kind switch
                {
                    TexRuleKind.Heavy => TableRule.Heavy,
                    TexRuleKind.Light => TableRule.Light,
                    TexRuleKind.Cmid => TableRule.Cmid,
                    _ => TableRule.Plain
                };
                for (int i = 0, c = 0; i < t.rows[r].Count; c += t.rows[r][i].span, i++)
                {
                    int end = c + t.rows[r][i].span - 1;
                    if (end < rule.from || c > rule.to) continue;
                    RuleTrim trim = (rule.trimLeft && c <= rule.from ? RuleTrim.Left : RuleTrim.None)
                        | (rule.trimRight && end >= rule.to ? RuleTrim.Right : RuleTrim.None);
                    bool doubled = kind == TableRule.Plain && rules.TryGetValue((r, i, above), out var had) && had.kind == TableRule.Plain;
                    rules[(r, i, above)] = (doubled ? TableRule.Double : kind, rule.width / Unity * PxPerPt, trim);
                }
            }
            return rules;
        }

        // A tabular inside a cell, which a note cannot hold, as a paragraph per row.
        private static IEnumerable<TexNode> Flat(List<TexNode> vlist)
        {
            foreach (TexNode node in vlist)
            {
                if (node is not TexTable inner)
                {
                    yield return node;
                    continue;
                }
                foreach (List<TexTableCell> row in inner.rows)
                {
                    TexParagraph line = new TexParagraph(new TexParStyle { align = TexAlign.Left });
                    foreach (TexParagraph p in row.SelectMany(c => Flat(c.vlist)).OfType<TexParagraph>())
                    {
                        if (line.list.Count > 0 && p.list.OfType<TexChar>().FirstOrDefault() is TexChar first) line.list.Add(new TexChar(' ', first.style));
                        line.list.AddRange(p.list);
                    }
                    yield return line;
                }
            }
        }
        #endregion

        #region ---- hyphenation ----
        private static readonly TexHyphenator NoPatterns = new TexHyphenator(Array.Empty<string>(), Array.Empty<string>());

        // Soft hyphens into every word of a paragraph that is not code, cells included, by the paragraph's language.
        private static void Hyphenate(List<TexNode> vlist, bool patterns, Dictionary<string, bool[]> extra)
        {
            foreach (TexNode node in vlist)
                switch (node)
                {
                    case TexParagraph { style.kind: not TexParKind.Code } p:
                        HyphenateWords(p.list, patterns ? TexHyphenator.For(p.language) ?? NoPatterns : NoPatterns, extra);
                        foreach (TexFloat f in p.list.OfType<TexFloat>())
                            Hyphenate(f.vlist, patterns, extra);
                        break;
                    case TexFloat f:
                        Hyphenate(f.vlist, patterns, extra);
                        break;
                    case TexTable t:
                        foreach (List<TexTableCell> row in t.rows)
                            foreach (TexTableCell cell in row)
                                Hyphenate(cell.vlist, patterns, extra);
                        break;
                }
        }

        // A word is a run of letters in one style, not monospaced; one holding a soft hyphen already is left as written.
        private static void HyphenateWords(List<TexNode> list, TexHyphenator hyphenator, Dictionary<string, bool[]> extra)
        {
            StringBuilder letters = new StringBuilder();
            List<int> starts = new List<int>();
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is not TexChar { style.font.family: not TexFamily.Mono } first || !char.IsLetter(first.ch)) continue;

                int end = i;
                bool marked = false, plain = true;
                letters.Clear();
                starts.Clear();
                while (end < list.Count && list[end] is TexChar c && c.style == first.style && (char.IsLetter(c.ch) || c.ch == TextMeasurer.SoftHyphen))
                {
                    marked |= c.ch == TextMeasurer.SoftHyphen;
                    starts.Add(letters.Length);
                    plain &= Append(letters, c.ch);
                    end++;
                }

                int inserted = 0;
                bool quoted = i > 0 && list[i - 1] is TexChar { straightQuote: true } || end < list.Count && list[end] is TexChar { straightQuote: true };
                if (!marked && plain && !quoted && hyphenator.Points(letters.ToString(), extra) is bool[] points)
                    for (int k = end - 1; k > i; k--)
                        if (points[starts[k - i]])
                        {
                            list.Insert(k, new TexChar(TextMeasurer.SoftHyphen, first.style));
                            inserted++;
                        }
                i = end + inserted - 1;
            }
        }

        // A letter as the patterns spell it, ligatures taken apart; false for a soft hyphen.
        private static bool Append(StringBuilder letters, char c)
        {
            if (c switch { 'ﬀ' => "ff", 'ﬁ' => "fi", 'ﬂ' => "fl", 'ﬃ' => "ffi", 'ﬄ' => "ffl", _ => null } is string parts) letters.Append(parts);
            else if (char.IsLetter(c)) letters.Append(char.ToLowerInvariant(c));
            else return false;
            return true;
        }
        #endregion

        #region ---- measuring ----
        // The widest line of a cell's paragraphs, px.
        private static float Width(List<TexNode> vlist)
        {
            float widest = 0f;
            foreach (TexParagraph p in vlist.OfType<TexParagraph>())
                foreach (List<TexNode> line in Lines(p.list))
                    widest = MathF.Max(widest, Width(line, 0));
            return widest;
        }

        private static float Width(List<TexNode> nodes, int depth)
        {
            float width = 0f;
            foreach (TexNode node in nodes)
                width += node switch
                {
                    TexChar c => Advance(c.ch, c.style),
                    TexGlueNode { interword: true } g => Advance(' ', g.style),
                    TexGlueNode g => g.glue.width / Unity * PxPerPt,
                    TexKern k => k.width / Unity * PxPerPt,
                    TexHBox b when depth < 16 => Width(b.list, depth + 1),
                    TexRefNode r => r.text.Sum(c => Advance(c, r.style)),
                    TexMathNode m => MathWidth(m),
                    TexImage i => PictureSize(i).width is > 0f and float w ? w : Native(i.path).width,
                    _ => 0f
                };
            return width;
        }

        // A string's width in a style, px.
        internal static float TextWidth(string text, TexStyle s)
        {
            float width = 0f;
            foreach (char c in text)
                width += Advance(c, s);
            return width;
        }

        // A \leaders box as its text, the text's inset and the box's width, px; null for more than characters, kerns, mu kerns and \hbox.
        private static (string text, float inset, float width)? LeaderCell(TexHBox box)
        {
            StringBuilder text = new StringBuilder();
            float width = 0f, inset = 0f;
            foreach (TexNode node in box.list)
                switch (node)
                {
                    case TexChar c:
                        if (text.Length == 0) inset = width;
                        text.Append(c.ch);
                        width += Advance(c.ch, c.style);
                        break;
                    case TexKern k:
                        width += k.width / Unity * PxPerPt;
                        break;
                    case TexGlueNode g:
                        width += g.glue.width / Unity * PxPerPt;
                        break;
                    case TexMathNode m when MathParser.Parse(m.source) is MathList list:
                        float em = m.style.font.size / Unity * PxPerPt;
                        foreach (MathNode item in list.items)
                            switch (item)
                            {
                                case MathSpace space:
                                    width += space.em * em;
                                    break;
                                case MathText run:
                                    if (text.Length == 0) inset = width;
                                    text.Append(run.text);
                                    width += TextWidth(run.text, m.style);
                                    break;
                                default:
                                    return null;
                            }
                        break;
                    default:
                        return null;
                }
            return (text.ToString(), inset, width);
        }

        private static float Advance(char c, TexStyle s)
        {
            float px = Px(s.font.size);
            if (c == TextMeasurer.SoftHyphen || TextMeasurer.IsZeroWidthBreak(c)) return 0f;
            if (Font(FontFor(s.font)) is not FontAsset font) return px * 0.5f;
            AtlasMetaData atlas = font.atlasMetaData;
            if (c < AtlasMetaData.AdvanceTableSize) return TextMeasurer.Grid(atlas.TableAdvance(c, Face(atlas, s.font)), AdvanceGrid) * px;
            Glyph glyph = atlas.GetGlyph(c) ?? atlas.GetGlyph(' ');
            if (glyph == null) return 0f;
            return TextMeasurer.Grid(atlas.Metrics(glyph, Face(atlas, s.font)).advanceWidth, AdvanceGrid) * px;
        }

        private static float MathWidth(TexMathNode m)
        {
            float px = Px(m.style.font.size);
            if (Font(MathFont) is not { mathConstants: not null } font) return m.source.Length * px * 0.5f;
            return MathLayout.Layout(MathParser.Parse(m.source), m.display, font.atlasMetaData, font.mathConstants).width * px;
        }

        private static FontAsset? Font(string name) =>
            AssetRegistries.GetRegistryByValueType<string, FontAsset>(typeof(FontAsset)).GetValueOrDefault(name);

        // The document's roman, sans and mono faces from its packages and fontspec; the defaults for none.
        internal static void Faces(TexTypesetter? typesetter)
        {
            files = typesetter?.fontFiles;
            roman =typesetter == null ? null : Named(typesetter.mainFont) ?? (typesetter.times ? TermesFont : RomanFont);
            sans = typesetter == null ? null : Named(typesetter.sansFont);
            mono = typesetter == null ? null : Named(typesetter.monoFont);
        }

        // The font asset a selection draws in.
        internal static string FontFor(TexFont font) =>
            (font.face == null ? null : Named(font.face)) ?? font.family switch
            {
                TexFamily.Mono => mono == null && font.size >= 11f * Unity &&Font(Mono12Font) != null ? Mono12Font : Mono,
                TexFamily.Sans => sans ?? Roman,
                _ => Roman
            };

        // The asset a fontspec name draws in: a bundled alias, then a registered asset or family; null, logged once, for none.
        private static string? Named(string? name)
        {
            if (name == null) return null;
            named ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (named.TryGetValue(name, out string? known)) return known;

            string? asset = aliases.TryGetValue(name, out string? alias) && Font(alias) != null ? alias
                : Font(name) != null ? name
                : FontLibrary.AssetFor(name) ?? Imported(name)
                ?? (standIns.TryGetValue(name, out string? standIn) && Font(standIn) != null ? standIn : null);
            if (asset == null && (missing ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(name))
                Log.Info($"font '{name}' is not available; its text keeps the default face");
            named[name] = asset;
            return asset;
        }

        // The installed family for a name, else fontspec's Path= files, baked now; null when neither is there.
        private static string? Imported(string name)
        {
            if (TestRunner.active) return null;
            FontLibrary.EnsureCatalog();
            InstalledFamily? family = FontCatalog.Find(name);
            if (family == null && files != null && files.TryGetValue(name, out (string regular, string? bold, string? italic, string? boldItalic) f)
                && File.Exists(f.regular))
                family = new InstalledFamily
                {
                    name = FontCatalog.ReadFace(f.regular, 0)?.family ?? name,
                    regular = f.regular,
                    bold = File.Exists(f.bold) ? f.bold : null,
                    italic = File.Exists(f.italic) ? f.italic : null,
                    boldItalic = File.Exists(f.boldItalic) ? f.boldItalic : null
                };
            return family == null ? null : FontLibrary.ImportNow(family, "Latex");
        }

        private static int Face(AtlasMetaData atlas, TexFont font) => atlas.Effective(0, font.bold
            ? font.italic ? FontStyle.BoldItalic : FontStyle.Bold
            : font.italic ? FontStyle.Italic : FontStyle.Regular);

        // \includegraphics' size in px; 0 leaves that side to the picture.
        private static (float width, float height) PictureSize(TexImage image)
        {
            float w = image.width > 0 ? image.width / Unity * PxPerPt : 0f, h = image.height > 0 ? image.height / Unity * PxPerPt : 0f;
            bool scaled = image.scale > 0f && w == 0f && h == 0f, fitted = image.keepAspect && w > 0f && h > 0f, oneSide = (w > 0f) != (h > 0f);
            if (!scaled && !fitted && !oneSide) return (w, h);

            (int nw, int nh) = Native(image.path);
            if (nw <= 0 || nh <= 0) return (w, h);
            if (scaled) return (nw * image.scale, nh * image.scale);
            if (oneSide) return w > 0f ? (w, w * nh / nw) : (h * nw / nh, h);
            float fit = MathF.Min(w / nw, h / nh);
            return (nw * fit, nh * fit);
        }

        private static (int width, int height) Native(string path)
        {
            try
            {
                return ImageFiles.TryIdentify(File.ReadAllBytes(path), out int width, out int height) ? (width, height) : (0, 0);
            }
            catch (Exception)
            {
                return (0, 0);
            }
        }
        #endregion

        #region ---- runs ----
        // Characters of one style become a run; glue a space, or no-break spaces of about its width; math its own run.
        private static void Runs(List<TexNode> line, XElement block, string blockFont, float blockSize, bool code)
        {
            StringBuilder text = new StringBuilder();
            TexStyle? current = null;
            int flushed = 0;
            (XElement run, TexRefNode node)? lastPageRef = null;

            void Flush()
            {
                if (current is TexStyle s && text.Length > 0) block.Add(TextRun(text.ToString(), s, blockFont, blockSize));
                flushed += text.Length;
                text.Clear();
            }

            void Put(string chars, TexStyle s)
            {
                if (current != s) Flush();
                current = s;
                text.Append(chars);
            }

            void Spacer(int sp, TexStyle s)
            {
                float px = sp / Unity * PxPerPt;
                if (px < 0.5f) return;
                Flush();
                current = null;
                XElement spacer = TextRun(" ", s, blockFont, blockSize);
                spacer.SetAttributeValue("Space", Format(px));
                block.Add(spacer);
                flushed++;
            }

            // \hbox to: its contents, the slack shared out at its fil glue or put after them; a page number's spacers always kept.
            void FixedBox(TexHBox b)
            {
                int fils = 0;
                TexRefNode? page = null;
                TexStyle? boxStyle = null;
                foreach (TexNode node in b.list)
                    switch (node)
                    {
                        case TexGlueNode { glue.stretchOrder: > 0 } g:
                            fils++;
                            boxStyle ??= g.style;
                            break;
                        case TexRefNode { page: true } number:
                            page = number;
                            boxStyle ??= number.style;
                            break;
                        case TexChar c:
                            boxStyle ??= c.style;
                            break;
                    }
                if (boxStyle is not TexStyle style) return;

                float target = b.width / Unity * PxPerPt, natural = Width(b.list, 0);
                float share = fils > 0 ? 1f / fils : 1f;
                float fixedWidth = page == null ? natural : natural - TextWidth(page.text, page.style);
                List<XElement>? spacers = page == null ? null : new List<XElement>(Math.Max(1, fils));
                lastPageRef = null;

                void Slack(TexStyle s)
                {
                    float px = MathF.Max(0f, target - natural) * share;
                    if (spacers == null && px < 0.5f) return;
                    Flush();
                    current = null;
                    XElement spacer = TextRun(" ", s, blockFont, blockSize);
                    spacer.SetAttributeValue("Space", Format(MathF.Max(0.01f, px)));
                    block.Add(spacer);
                    flushed++;
                    spacers?.Add(spacer);
                }

                List<TexNode> part = new List<TexNode>(b.list.Count);
                foreach (TexNode node in b.list)
                    if (node is TexGlueNode { glue.stretchOrder: > 0 } g)
                    {
                        Walk(part, true);
                        part.Clear();
                        Slack(g.style);
                    }
                    else part.Add(node);
                Walk(part, true);
                if (fils == 0) Slack(style);

                if (spacers != null && lastPageRef is (XElement numberRun, TexRefNode numbered))
                    foreach (XElement spacer in spacers)
                        numberRun.AddAnnotation(new BoxFitNote(spacer, target, fixedWidth, share, numbered.style));
            }

            void Walk(List<TexNode> nodes, bool unbreakable)
            {
                for (int n = 0; n < nodes.Count; n++)
                    switch (nodes[n])
                    {
                        case TexChar c:
                            Put(c.ch.ToString(), c.style);
                            break;
                        case TexGlueNode { interword: true } g:
                            Put(unbreakable || n > 0 && nodes[n - 1] is TexPenalty { penalty: >= 10000 } ? " " : TextMeasurer.FactorSpace(g.spaceFactor / 1000f).ToString(), g.style);
                            break;
                        case TexGlueNode { glue.stretchOrder: > 0, glue.stretch: > 0 } g:
                            Flush();
                            current = null;
                            XElement fill = TextRun(" ", g.style, blockFont, blockSize);
                            fill.SetAttributeValue("Fill", "true");
                            if (g.leader != null && LeaderCell(g.leader) is (string leader, float inset, float cell) && leader.Length > 0 && cell > 0f)
                            {
                                fill.SetAttributeValue("Leader", leader);
                                fill.SetAttributeValue("LeaderWidth", Format(cell));
                                fill.SetAttributeValue("LeaderInset", Format(inset));
                            }
                            block.Add(fill);
                            flushed++;
                            break;
                        case TexGlueNode g:
                            Spacer(g.glue.width, g.style);
                            break;
                        case TexKern k:
                            Spacer(k.width, k.style);
                            break;
                        case TexHBox { width: >= 0 } b:
                            FixedBox(b);
                            break;
                        case TexHBox b:
                            Walk(b.list, true);
                            break;
                        case TexRefNode { page: true } r:
                            Flush();
                            current = null;
                            XElement pageRef = TextRun(r.text, r.style, blockFont, blockSize);
                            pageRef.AddAnnotation(new PageRefNote(r.keys[0]));
                            block.Add(pageRef);
                            flushed += r.text.Length;
                            lastPageRef = (pageRef, r);
                            break;
                        case TexRefNode r:
                            Put(r.text, r.style);
                            break;
                        case TexLabelMark l:
                            block.AddAnnotation(new LabelSite(l.key, flushed + text.Length));
                            break;
                        case TexImage i:
                            Flush();
                            current = null;
                            XElement picture = new XElement("Run", new XAttribute("Image", i.path));
                            (float width, float height) = PictureSize(i);
                            if (width > 0f) picture.SetAttributeValue("Width", Format(width));
                            if (height > 0f) picture.SetAttributeValue("Height", Format(height));
                            if (i.angle != 0f) picture.SetAttributeValue("Rotation", Format(-i.angle));
                            block.Add(picture);
                            flushed++;
                            break;
                        case TexMathNode m:
                            Flush();
                            current = null;
                            XElement run = new XElement("Run", new XAttribute("Math", m.source), new XAttribute("FontName", MathFont));
                            if (m.display) run.SetAttributeValue("Display", "true");
                            if (m.strut > 0f) run.SetAttributeValue("Strut", Format(m.strut));
                            if (m.note != null) run.SetAttributeValue("Note", m.note.id);
                            float px = Px(m.style.font.size);
                            if (px != blockSize) Size(run, px);
                            if (m.style.color != null) run.SetAttributeValue("ColorHex", m.style.color);
                            block.Add(run);
                            flushed++;
                            break;
                    }
            }

            Walk(line, false);
            Flush();
            if (!code) Trim(block);
        }

        private static XElement TextRun(string text, TexStyle s, string blockFont, float blockSize)
        {
            XElement run = new XElement("Run", new XAttribute("Text", text));
            if (s.font.bold) run.SetAttributeValue("Bold", "true");
            if (s.font.italic) run.SetAttributeValue("Italic", "true");
            if (s.underline) run.SetAttributeValue("Underline", "true");
            if (s.color != null) run.SetAttributeValue("ColorHex", s.color);
            string font = FontFor(s.font);
            if (font != blockFont) run.SetAttributeValue("FontName", font);
            float px = Px(s.font.size);
            if (px != blockSize) Size(run, px);
            if (s.urlStretch > 0 || s.urlBreakPenalty > 0 || s.urlWidth != 0 || s.urlShrink > 0 || s.urlNumPenalty > 0 || s.urlUcPenalty > 0 || s.urlLcPenalty > 0)
            {
                if (s.urlStretch > 0) run.SetAttributeValue("UrlStretch", Format(Px(s.urlStretch)));
                if (s.urlWidth != 0) run.SetAttributeValue("UrlWidth", Format(Px(s.urlWidth)));
                if (s.urlShrink > 0) run.SetAttributeValue("UrlShrink", Format(Px(s.urlShrink)));
                run.SetAttributeValue("UrlBreakPenalty", s.urlBreakPenalty);
                run.SetAttributeValue("UrlBigBreakPenalty", s.urlBigBreakPenalty);
                if (s.urlNumPenalty > 0) UrlLetters(run, "Num", s.urlNumPenalty, s.urlNumWidth, s.urlNumStretch, s.urlNumShrink);
                if (s.urlUcPenalty > 0) UrlLetters(run, "Uc", s.urlUcPenalty, s.urlUcWidth, s.urlUcStretch, s.urlUcShrink);
                if (s.urlLcPenalty > 0) UrlLetters(run, "Lc", s.urlLcPenalty, s.urlLcWidth, s.urlLcStretch, s.urlLcShrink);
            }
            return run;
        }

        // A biblatex URL letter class's penalty and glue, px: UrlNumPenalty, UrlNumWidth and so on.
        private static void UrlLetters(XElement run, string kind, int penalty, int width, int stretch, int shrink)
        {
            run.SetAttributeValue($"Url{kind}Penalty", penalty);
            if (width != 0) run.SetAttributeValue($"Url{kind}Width", Format(Px(width)));
            if (stretch > 0) run.SetAttributeValue($"Url{kind}Stretch", Format(Px(stretch)));
            if (shrink > 0) run.SetAttributeValue($"Url{kind}Shrink", Format(Px(shrink)));
        }

        private static void Size(XElement run, float px)
        {
            run.SetAttributeValue("FontSize", Format(px));
            run.SetAttributeValue("FontSizeAuthored", "true");
        }

        // No-break spaces of about a width; nothing under 0.4 of a space.
        private static string Spaces(int width, TexStyle s)
        {
            float space = s.font.size * (s.font.family == TexFamily.Mono ? 0.525f : 1f / 3f);
            if (space <= 0f || width < 0.4f * space) return string.Empty;
            return new string(' ', Math.Max(1, (int)MathF.Round(width / space)));
        }

        // Spaces at a block's ends dropped, as a line break drops them.
        private static void Trim(XElement block)
        {
            XElement? first = block.Elements("Run").FirstOrDefault(r => r.Attribute("Text") != null && r.Attribute("Fill") == null);
            XElement? last = block.Elements("Run").LastOrDefault(r => r.Attribute("Text") != null && r.Attribute("Fill") == null);
            if (first != null && block.Elements("Run").First() == first) Cut(first, s => s.TrimStart(blanks));
            if (last != null && block.Elements("Run").Last() == last) Cut(last, s => s.TrimEnd(blanks));
        }

        // a plain space and the space-factor spaces
        private static readonly char[] blanks =
            { ' ', TextMeasurer.FirstFactorSpace, (char)(TextMeasurer.FirstFactorSpace + 1), (char)(TextMeasurer.FirstFactorSpace + 2), (char)(TextMeasurer.FirstFactorSpace + 3) };

        private static void Cut(XElement run, Func<string, string> trim)
        {
            string text = trim(run.Attribute("Text")!.Value);
            if (text.Length > 0) run.SetAttributeValue("Text", text);
            else run.Remove();
        }
        #endregion

        private static float Px(int sp) => sp / Unity * PxPerPt;

        private static string Format(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    // \pageref runs to fill, and the block and offset each label stands at
    public sealed class TexPageRefs
    {
        public readonly List<(XElement run, string key)> refs = new List<(XElement, string)>();
        public readonly Dictionary<string, (int block, int offset)> labels = new Dictionary<string, (int, int)>();

        // a \pageref's run in a fixed-width box: a spacer taking its share of the room the number leaves, px
        public readonly List<(XElement run, XElement spacer, float room, float share, TexStyle style)> fits =
            new List<(XElement, XElement, float, float, TexStyle)>(64);

        // Each spacer of run re-sized for the number it now shows.
        public void Fit(XElement run, string number)
        {
            foreach ((XElement fitted, XElement spacer, float room, float share, TexStyle style) in fits)
                if (fitted == run)
                    spacer.SetAttributeValue("Space", MathF.Max(0.01f, (room - TexLowering.TextWidth(number, style)) * share)
                        .ToString("0.###", CultureInfo.InvariantCulture));
        }
    }

    // em and ex from the baked Latin Modern atlases; cmr10's ratio where a face is missing.
    public sealed class TexAtlasMetrics : ITexFontMetrics
    {
        // the document whose packages pick the roman face
        public TexTypesetter? typesetter;

        public int Quad(TexFont font) => font.size;

        public int XHeight(TexFont font)
        {
            Dictionary<string, FontAsset> fonts = AssetRegistries.GetRegistryByValueType<string, FontAsset>(typeof(FontAsset));
            TexLowering.Faces(typesetter);
            if (!fonts.TryGetValue(TexLowering.FontFor(font), out FontAsset? asset))
                return (int)(font.size * 0.430554);

            AtlasMetaData atlas = asset.atlasMetaData;
            int face = atlas.Effective(0, font.bold
                ? font.italic ? FontStyle.BoldItalic : FontStyle.Bold
                : font.italic ? FontStyle.Italic : FontStyle.Regular);
            return (int)(atlas.Metrics(atlas.GetGlyph('x'), face).glyphHeight * font.size);
        }

        public int Width(string text, TexFont font)
        {
            TexLowering.Faces(typesetter);
            return (int)(TexLowering.TextWidth(text, new TexStyle(font, null, false)) / TexLowering.PxPerPt * TexLowering.Unity);
        }
    }
}
