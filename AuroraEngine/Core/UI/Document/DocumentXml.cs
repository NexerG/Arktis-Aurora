using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Filing.Serialization;
using ArctisAurora.Core.Filing.Xml;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Security;
using ArctisAurora.Core.UI.Notes;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;

namespace ArctisAurora.Core.UI.Document
{
    // Load and save for the new stack's notes, over the same file format the outgoing DocumentXml
    // reads: <Document> holding <DocumentLayout> and <Block>s of <Run>s.
    //
    // Scalars still go through the engine's attribute-driven reflection, but the element names are
    // written by hand rather than resolved from [A_XSDType]: "Document", "Block" and "Run" belong to
    // the outgoing types until landing 6d, and a run here is a span on its block rather than an
    // object the reader can attach a child to.
    public static class DocumentXml
    {
        #region ---- parse ----
        public static RichTextDocument Load(string path, PasswordKey? key = null)
        {
            XElement root = EncryptedFile.LoadXml(path, LoadOptions.None, ref key).Root ?? throw new Exception($"Note '{path}' is empty.");
            ResolvePictures(root, Path.GetDirectoryName(Path.GetFullPath(path))!);
            RichTextDocument document = Parse(root);
            document.encryption = key;
            return document;
        }

        // Picture paths in a note's tree: relative to its folder on disk, absolute in memory.
        internal static void ResolvePictures(XElement root, string folder)
        {
            foreach (XAttribute image in Pictures(root))
                image.Value = Path.GetFullPath(Path.Combine(folder, image.Value));
        }

        internal static void RelativePictures(XElement root, string folder)
        {
            foreach (XAttribute image in Pictures(root))
                image.Value = Path.GetRelativePath(folder, image.Value).Replace('\\', '/');
        }

        private static List<XAttribute> Pictures(XElement root) =>
            root.Descendants().Where(e => e.Name.LocalName == "Run").Select(e => e.Attribute("Image")).OfType<XAttribute>().ToList();

        // Builds a note from a <Document> tree, whichever file format produced it.
        public static RichTextDocument Parse(XElement root)
        {
            RichTextDocument document = new RichTextDocument();
            XmlReflection.ApplyAttributes(root, document, tolerant: true);

            List<NoteNode> blocks = new List<NoteNode>();
            foreach (XElement element in root.Elements())
                switch (element.Name.LocalName)
                {
                    case "DocumentLayout": ReadLayout(element, document.layout); break;
                    case "Block": blocks.Add(ReadBlock(element)); break;
                    case "Table": blocks.Add(ReadTable(element)); break;
                    case "Footnote":
                    case "Float": blocks.AddRange(ReadInsert(element)); break;
                    case "Sealed": blocks.Add(new NoteSealed(new XElement(element))); break;
                    default: throw new Exception($"Unknown document element '{element.Name.LocalName}'.");
                }

            document.blocks = blocks.ToArray();
            return document;
        }

        // A <Footnote Id> or <Float Kind Placement>: its blocks and tables, sharing one PageInsert.
        private static IEnumerable<NoteNode> ReadInsert(XElement element)
        {
            PageInsert insert = element.Name.LocalName == "Footnote"
                ? new PageInsert { footnote = (string?)element.Attribute("Id") ?? "" }
                : new PageInsert { floatKind = (string?)element.Attribute("Kind") ?? "figure", placement = (string?)element.Attribute("Placement") ?? "tbp" };
            foreach (XElement child in element.Elements())
                switch (child.Name.LocalName)
                {
                    case "Block":
                        NoteBlock block = ReadBlock(child);
                        block.insert = insert;
                        yield return block;
                        break;
                    case "Table":
                        NoteTable table = ReadTable(child);
                        table.insert = insert;
                        yield return table;
                        break;
                }
        }

        private static void ReadLayout(XElement element, DocumentLayout layout)
        {
            XmlReflection.ApplyAttributes(element, layout, tolerant: true);

            foreach (XElement child in element.Elements())
            {
                if (child.Name.LocalName == "Page")
                {
                    layout.page = new PageLayout();
                    XmlReflection.ApplyAttributes(child, layout.page, tolerant: true);
                    foreach (XElement named in child.Elements().Where(e => e.Name.LocalName == "PageStyle"))
                    {
                        PageStyle pageStyle = new PageStyle();
                        XmlReflection.ApplyAttributes(named, pageStyle, tolerant: true);
                        foreach (XElement slot in named.Elements().Where(e => e.Name.LocalName == "Slot"))
                        {
                            RunningSlot read = new RunningSlot();
                            XmlReflection.ApplyAttributes(slot, read, tolerant: true);
                            pageStyle.slots.Add(read);
                        }
                        layout.page.styles.Add(pageStyle);
                    }
                    continue;
                }

                if (child.Name.LocalName == "ListLevel")
                {
                    ListLevel level = new ListLevel();
                    XmlReflection.ApplyAttributes(child, level, tolerant: true);
                    layout.listLevels.Add(level);
                    continue;
                }

                TextStyle style = new TextStyle();
                XmlReflection.ApplyAttributes(child, style, tolerant: true);
                layout.textStyles.Add(style);
            }
        }

        // A block carries few attributes of its own; everything else about it is its runs. Read by
        // name rather than through ApplyAttributes, which would also apply every control attribute
        // the block inherits and a note has no business carrying.
        private static NoteBlock ReadBlock(XElement element)
        {
            NoteBlock block = new NoteBlock();

            XAttribute styling = element.Attribute("StylingType");
            if (styling != null && Enum.TryParse(styling.Value, true, out TextStyleType type))
                block.stylingType = type;

            XAttribute align = element.Attribute("Align");
            if (align != null && Enum.TryParse(align.Value, true, out TextAlignment alignment))
                block.alignment = alignment;
            block.firstIndent = (float?)element.Attribute("Indent") ?? 0f;
            block.leftIndent = (float?)element.Attribute("LeftIndent") ?? 0f;
            block.rightIndent = (float?)element.Attribute("RightIndent") ?? 0f;
            block.rightIndentLast = (float?)element.Attribute("RightIndentLast");
            block.ruleWeight = (float?)element.Attribute("Weight");
            block.spaceBefore = (float?)element.Attribute("SpaceBefore");
            block.shrinkBefore = (float?)element.Attribute("ShrinkBefore") ?? 0f;
            block.pageBreak = ReadPageBreak(element);
            block.pageStyle = (string?)element.Attribute("PageStyle");
            block.markLeft = (string?)element.Attribute("MarkLeft");
            block.markRight = (string?)element.Attribute("MarkRight");
            block.language = (string?)element.Attribute("Language");
            block.codeWrap = (bool?)element.Attribute("Wrap") ?? false;

            XAttribute list = element.Attribute("List");
            if (list != null && Enum.TryParse(list.Value, true, out ListKind kind))
                block.listKind = kind;
            block.listLevel = (int?)element.Attribute("Level") ?? 0;
            block.isChecked = (bool?)element.Attribute("Checked") ?? false;
            XAttribute marker = element.Attribute("Marker");
            if (marker != null && Enum.TryParse(marker.Value, true, out ListMarker style))
                block.listMarker = style;
            block.listIndent = (float?)element.Attribute("ListIndent");
            block.lineHeight = (float?)element.Attribute("LineHeight");
            block.keepWithNext = (bool?)element.Attribute("KeepWithNext") ?? false;
            block.strut = (bool?)element.Attribute("Strut") ?? false;
            block.listStart = (int?)element.Attribute("Start");

            foreach (XElement child in element.Elements())
            {
                Run run = new Run();
                XmlReflection.ApplyAttributes(child, run, tolerant: true);
                block.AppendRun(run);
            }

            // A block with no runs still needs one span, or it can hold neither a caret nor a style.
            if (block.run.spans.Count == 0) block.AppendRun(new Run());

            return block;
        }

        // <Column Width>s, then <Row>s of <Cell>s of <Block>s; a row wider than the columns adds columns.
        internal static NoteTable ReadTable(XElement element)
        {
            List<float> widths = new List<float>();
            foreach (XElement column in element.Elements().Where(e => e.Name.LocalName == "Column"))
                widths.Add((float?)column.Attribute("Width") ?? defaultColumnWidth);

            List<List<NoteCell>> rows = new List<List<NoteCell>>();
            foreach (XElement row in element.Elements().Where(e => e.Name.LocalName == "Row"))
            {
                List<NoteCell> cells = new List<NoteCell>();
                int spanned = 0;
                foreach (XElement cell in row.Elements().Where(e => e.Name.LocalName == "Cell"))
                {
                    NoteCell read = new NoteCell
                    {
                        span = Math.Max(1, (int?)cell.Attribute("ColumnSpan") ?? 1),
                        blocks = cell.Elements().Where(e => e.Name.LocalName == "Block").Select(ReadBlock).ToArray()
                    };
                    if (cell.Attribute("RuleAbove") != null || cell.Attribute("RuleBelow") != null)
                        read.rules = ReadCellRules(cell);
                    cells.Add(read);
                    spanned += read.span;
                }

                while (widths.Count < spanned) widths.Add(defaultColumnWidth);
                rows.Add(cells);
            }

            NoteTable table = new NoteTable(widths.ToArray())
            {
                showBorders = (bool?)element.Attribute("Borders") ?? true,
                spaceBefore = (float?)element.Attribute("SpaceBefore"),
                shrinkBefore = (float?)element.Attribute("ShrinkBefore") ?? 0f,
                alignment = Enum.TryParse((string?)element.Attribute("Align"), true, out TextAlignment align) ? align : TextAlignment.Left,
                indent = (float?)element.Attribute("Indent") ?? 0f,
                pageBreak = ReadPageBreak(element),
                cellPadding = ReadPadding((string?)element.Attribute("Padding"))
            };
            table.rows = rows.Select(cells => Fit(cells, table.widths.Length)).ToArray();

            List<XElement> columns = element.Elements().Where(e => e.Name.LocalName == "Column").ToList();
            for (int c = 0; c < columns.Count; c++)
            {
                table.leftRules[c] = ReadRule((string?)columns[c].Attribute("RuleLeft")).kind;
                table.rightRules[c] = ReadRule((string?)columns[c].Attribute("RuleRight")).kind;
            }

            return table;
        }

        // A row's cells clamped to the columns, a short row filled with empty cells, every cell holding a block.
        private static NoteCell[] Fit(List<NoteCell> cells, int columns)
        {
            List<NoteCell> fitted = new List<NoteCell>();
            for (int c = 0, i = 0; c < columns; i++)
            {
                NoteCell cell = i < cells.Count ? cells[i] : new NoteCell();
                cell.span = Math.Clamp(cell.span, 1, columns - c);
                c += cell.span;

                if (cell.blocks.Length == 0)
                {
                    NoteBlock empty = new NoteBlock();
                    empty.AppendRun(new Run());
                    cell.blocks = [empty];
                }
                fitted.Add(cell);
            }

            return fitted.ToArray();
        }

        private static CellRules ReadCellRules(XElement cell)
        {
            (TableRule above, float aboveWidth) = ReadRule((string?)cell.Attribute("RuleAbove"));
            (TableRule below, float belowWidth) = ReadRule((string?)cell.Attribute("RuleBelow"));
            return new CellRules
            {
                above = above,
                aboveWidth = aboveWidth,
                aboveTrim = Enum.TryParse((string?)cell.Attribute("TrimAbove"), true, out RuleTrim a) ? a : RuleTrim.None,
                below = below,
                belowWidth = belowWidth,
                belowTrim = Enum.TryParse((string?)cell.Attribute("TrimBelow"), true, out RuleTrim b) ? b : RuleTrim.None
            };
        }

        // "Kind" or "Kind width", the width in px.
        private static (TableRule kind, float width) ReadRule(string? value)
        {
            string[] parts = (value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !Enum.TryParse(parts[0], true, out TableRule kind)) return (TableRule.None, 0f);
            float width = parts.Length > 1 && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float w) ? w : 0f;
            return (kind, width);
        }

        // "across down", px.
        private static Vector2? ReadPadding(string? value)
        {
            string[] parts = (value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2
                || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float across)
                || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float down)) return null;
            return new Vector2(across, down);
        }

        private static PageBreak ReadPageBreak(XElement element) =>
            Enum.TryParse((string?)element.Attribute("PageBreak"), true, out PageBreak value) ? value : PageBreak.None;

        private const float defaultColumnWidth = 150f;
        #endregion

        #region ---- write ----
        public static void Save(RichTextDocument document, string path)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            XmlWriterSettings settings = new XmlWriterSettings { Indent = true, IndentChars = XMLWriter.Indent, NewLineChars = XMLWriter.Newline };
            VaultFiles.SaveXml(ToFile(document, path), path, document.encryption, settings);
        }

        // The note as Save writes it to this path.
        public static XDocument ToFile(RichTextDocument document, string path)
        {
            XElement root = ToXml(document);
            RelativePictures(root, Path.GetDirectoryName(Path.GetFullPath(path))!);
            BindSchema(root, path);
            return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        }

        // Bind the note to its schema so an editor can validate it on open. Resolved relative to
        // the note rather than fixed, because a vault sits wherever the user put it.
        internal static void BindSchema(XElement root, string path)
        {
            XNamespace ns = XSDGenerator.NamespaceFor("UI");
            string dir = Path.GetDirectoryName(path);
            string schemaFile = Path.Combine(Paths.XMLSCHEMAS, "UITypeSchema.xsd");
            string relative = Path.GetRelativePath(string.IsNullOrEmpty(dir) ? "." : dir, schemaFile).Replace('\\', '/');

            XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
            root.SetAttributeValue(XNamespace.Xmlns + "xsi", xsi.NamespaceName);
            root.SetAttributeValue(xsi + "schemaLocation", $"{ns.NamespaceName} {relative}");
        }

        // The note as a <Document> tree, for any file format to write out.
        public static XElement ToXml(RichTextDocument document)
        {
            XNamespace ns = XSDGenerator.NamespaceFor("UI");
            XElement root = WriteScalars(ns + "Document", document);
            if (WriteLayout(ns, document.layout) is XElement layout) root.Add(layout);
            foreach ((XElement element, _) in WriteEntries(ns, document))
                root.Add(element);

            return root;
        }

        // The note's <DocumentLayout>; null when it holds nothing.
        internal static XElement? WriteLayout(XNamespace ns, DocumentLayout source)
        {
            XElement layout = WriteScalars(ns + "DocumentLayout", source);
            foreach (TextStyle style in source.textStyles)
                layout.Add(WriteScalars(ns + "TextStyle", style));
            if (source.page != null)
            {
                XElement page = WriteScalars(ns + "Page", source.page);
                foreach (PageStyle style in source.page.styles)
                {
                    XElement named = WriteScalars(ns + "PageStyle", style);
                    named.Add(style.slots.Select(s => WriteScalars(ns + "Slot", s)));
                    page.Add(named);
                }
                layout.Add(page);
            }
            foreach (ListLevel level in source.listLevels)
                layout.Add(WriteScalars(ns + "ListLevel", level));
            return layout.HasAttributes || layout.HasElements ? layout : null;
        }

        // The <Document>'s entries after its layout: a block, a table, or a footnote or float holding its run of nodes.
        internal static IEnumerable<(XElement element, NoteNode[] nodes)> WriteEntries(XNamespace ns, RichTextDocument document) =>
            Groups(document).Select(g => (WriteGroup(ns, g.insert, g.nodes), g.nodes));

        // The nodes each entry holds: a lone block or table, or the run of nodes sharing a footnote or float.
        internal static IEnumerable<(PageInsert? insert, NoteNode[] nodes)> Groups(RichTextDocument document)
        {
            NoteNode[] blocks = document.blocks;
            for (int i = 0; i < blocks.Length;)
            {
                PageInsert? insert = blocks[i].insert;
                NoteSeal? seal = insert == null ? blocks[i].seal : null;
                int end = i + 1;
                if (insert != null)
                    while (end < blocks.Length && blocks[end].insert == insert) end++;
                else if (seal != null)
                    while (end < blocks.Length && blocks[end].seal == seal && blocks[end].insert == null) end++;

                yield return (insert, blocks[i..end]);
                i = end;
            }
        }

        internal static XElement WriteGroup(XNamespace ns, PageInsert? insert, NoteNode[] nodes)
        {
            if (insert == null) return nodes[0].seal is NoteSeal seal ? NoteSeals.Write(ns, seal, nodes) : WriteNode(ns, nodes[0]);

            XElement group = insert.footnote != null ? new XElement(ns + "Footnote", new XAttribute("Id", insert.footnote))
                : new XElement(ns + "Float", new XAttribute("Kind", insert.floatKind ?? "figure"), new XAttribute("Placement", insert.placement));
            foreach (NoteNode node in nodes)
                group.Add(WriteNode(ns, node));
            return group;
        }

        internal static XElement WriteNode(XNamespace ns, NoteNode node) => node switch
        {
            NoteTable table => WriteTable(ns, table),
            NoteSealed locked => new XElement(locked.element),
            _ => WriteBlock(ns, (NoteBlock)node)
        };

        // A <Block> or <Table> read back; null for anything else.
        internal static NoteNode? ReadEntry(XElement element) => element.Name.LocalName switch
        {
            "Block" => ReadBlock(element),
            "Table" => ReadTable(element),
            _ => null
        };

        private static XElement WriteBlock(XNamespace ns, NoteBlock block)
        {
            XElement element = new XElement(ns + "Block");
            if (block.stylingType != TextStyleType.Text)
                element.SetAttributeValue("StylingType", block.stylingType.ToString());
            if (block.alignment != TextAlignment.Left)
                element.SetAttributeValue("Align", block.alignment.ToString());
            if (block.firstIndent != 0f)
                element.SetAttributeValue("Indent", Format(block.firstIndent));
            if (block.leftIndent != 0f)
                element.SetAttributeValue("LeftIndent", Format(block.leftIndent));
            if (block.rightIndent != 0f)
                element.SetAttributeValue("RightIndent", Format(block.rightIndent));
            if (block.rightIndentLast.HasValue)
                element.SetAttributeValue("RightIndentLast", Format(block.rightIndentLast.Value));
            if (block.ruleWeight.HasValue)
                element.SetAttributeValue("Weight", Format(block.ruleWeight.Value));
            if (block.spaceBefore.HasValue)
                element.SetAttributeValue("SpaceBefore", Format(block.spaceBefore.Value));
            if (block.shrinkBefore > 0f)
                element.SetAttributeValue("ShrinkBefore", Format(block.shrinkBefore));
            if (block.pageBreak != PageBreak.None)
                element.SetAttributeValue("PageBreak", block.pageBreak.ToString());
            if (block.pageStyle != null)
                element.SetAttributeValue("PageStyle", block.pageStyle);
            if (block.markLeft != null)
                element.SetAttributeValue("MarkLeft", block.markLeft);
            if (block.markRight != null)
                element.SetAttributeValue("MarkRight", block.markRight);
            if (!string.IsNullOrEmpty(block.language))
                element.SetAttributeValue("Language", block.language);
            if (block.codeWrap && block.stylingType == TextStyleType.Code)
                element.SetAttributeValue("Wrap", "true");
            if (block.listKind != ListKind.None)
                element.SetAttributeValue("List", block.listKind.ToString());
            if (block.listLevel > 0)
                element.SetAttributeValue("Level", block.listLevel);
            if (block.isChecked)
                element.SetAttributeValue("Checked", "true");
            if (block.listMarker.HasValue)
                element.SetAttributeValue("Marker", block.listMarker.Value.ToString());
            if (block.listIndent.HasValue)
                element.SetAttributeValue("ListIndent", Format(block.listIndent.Value));
            if (block.lineHeight.HasValue)
                element.SetAttributeValue("LineHeight", Format(block.lineHeight.Value));
            if (block.keepWithNext)
                element.SetAttributeValue("KeepWithNext", "true");
            if (block.strut)
                element.SetAttributeValue("Strut", "true");
            if (block.listStart.HasValue)
                element.SetAttributeValue("Start", block.listStart.Value);

            foreach (Run run in block.run.Runs())
                element.Add(WriteScalars(ns + "Run", run));

            return element;
        }

        internal static XElement WriteTable(NoteTable table) => WriteTable(XSDGenerator.NamespaceFor("UI"), table);

        // A table of empty cells, every column one width.
        internal static XElement NewTable(int rows, int columns, float width)
        {
            XNamespace ns = XSDGenerator.NamespaceFor("UI");
            return new XElement(ns + "Table",
                Enumerable.Range(0, columns).Select(_ => new XElement(ns + "Column", new XAttribute("Width", Format(width)))),
                Enumerable.Range(0, rows).Select(_ => new XElement(ns + "Row",
                    Enumerable.Range(0, columns).Select(_ => new XElement(ns + "Cell")))));
        }

        private static XElement WriteTable(XNamespace ns, NoteTable table)
        {
            XElement element = new XElement(ns + "Table");
            if (!table.showBorders) element.SetAttributeValue("Borders", "false");
            if (table.spaceBefore.HasValue) element.SetAttributeValue("SpaceBefore", Format(table.spaceBefore.Value));
            if (table.shrinkBefore > 0f) element.SetAttributeValue("ShrinkBefore", Format(table.shrinkBefore));
            if (table.alignment != TextAlignment.Left) element.SetAttributeValue("Align", table.alignment.ToString());
            if (table.indent > 0f) element.SetAttributeValue("Indent", Format(table.indent));
            if (table.pageBreak != PageBreak.None) element.SetAttributeValue("PageBreak", table.pageBreak.ToString());
            if (table.cellPadding is Vector2 pad) element.SetAttributeValue("Padding", $"{Format(pad.X)} {Format(pad.Y)}");
            for (int c = 0; c < table.widths.Length; c++)
            {
                XElement column = new XElement(ns + "Column", new XAttribute("Width", Format(table.widths[c])));
                if (table.leftRules[c] != TableRule.None) column.SetAttributeValue("RuleLeft", table.leftRules[c].ToString());
                if (table.rightRules[c] != TableRule.None) column.SetAttributeValue("RuleRight", table.rightRules[c].ToString());
                element.Add(column);
            }

            foreach (NoteCell[] cells in table.rows)
            {
                XElement row = new XElement(ns + "Row");
                element.Add(row);

                foreach (NoteCell cell in cells)
                {
                    XElement written = new XElement(ns + "Cell");
                    if (cell.span > 1) written.SetAttributeValue("ColumnSpan", cell.span);
                    if (cell.rules is CellRules rules)
                    {
                        if (rules.above != TableRule.None) written.SetAttributeValue("RuleAbove", WriteRule(rules.above, rules.aboveWidth));
                        if (rules.aboveTrim != RuleTrim.None) written.SetAttributeValue("TrimAbove", rules.aboveTrim.ToString());
                        if (rules.below != TableRule.None) written.SetAttributeValue("RuleBelow", WriteRule(rules.below, rules.belowWidth));
                        if (rules.belowTrim != RuleTrim.None) written.SetAttributeValue("TrimBelow", rules.belowTrim.ToString());
                    }
                    foreach (NoteBlock block in cell.blocks)
                        written.Add(WriteBlock(ns, block));
                    row.Add(written);
                }
            }

            return element;
        }

        // Only what differs from a fresh instance, mirroring the reader: absent attribute -> default.
        internal static XElement WriteScalars(XName name, object node)
        {
            XElement element = new XElement(name);
            Dictionary<MemberInfo, object> defaults = XmlReflection.Defaults(node.GetType());

            foreach (MemberInfo member in XmlReflection.ScalarMembers(node.GetType()))
            {
                object value = XmlReflection.GetMember(member, node);
                if (value == null) continue;
                if (Equals(value, defaults[member])) continue;

                A_XSDElementPropertyAttribute meta = member.GetCustomAttribute<A_XSDElementPropertyAttribute>();
                element.SetAttributeValue(meta.Name, Format(value));
            }

            return element;
        }

        // xs:boolean has no True — a note carrying one fails its own schema, and Convert.ToString
        // spells a bool the C# way.
        private static string WriteRule(TableRule kind, float width) => width > 0f ? $"{kind} {Format(width)}" : kind.ToString();

        private static string Format(object value) =>
            value is bool flag ? (flag ? "true" : "false") : Convert.ToString(value, CultureInfo.InvariantCulture);
        #endregion
    }
}
