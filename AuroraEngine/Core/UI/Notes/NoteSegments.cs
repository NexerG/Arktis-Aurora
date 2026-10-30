using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Filing.Xml;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI.Document;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ArctisAurora.Core.UI.Notes
{
    public enum NoteSegmentKind
    {
        Header,
        Layout,
        Entry,
        Footer
    }

    // One top-level stretch of a note file.
    public sealed class NoteSegment
    {
        public NoteSegmentKind kind;

        // bytes in the file: the element's own, then slack up to the next segment
        public long start;
        public int length;
        public int capacity;

        // the model nodes an Entry holds, and the footnote or float it is; null for a lone block or table
        public NoteNode[] nodes = Array.Empty<NoteNode>();
        public PageInsert? insert;
    }

    // A note file's pending writes, its final length, and its segments and bytes once they land.
    public readonly record struct SegmentPatch(List<(long offset, byte[] bytes)> writes, long length, List<NoteSegment> map, byte[] file);

    // A note's .axml file as segments: written through XMLWriter, mapped back from any file.
    public static class NoteSegments
    {
        private static readonly byte[] bom = { 0xEF, 0xBB, 0xBF };
        private const string footer = "</Document>";

        #region ---- write ----
        // Writes what changed since map was taken from the file at path, or the whole file without a map; returns the file's new map.
        public static List<NoteSegment> Write(RichTextDocument document, string path, List<NoteSegment>? map)
        {
            SegmentPatch patch = Prepare(document, path, map, map != null && File.Exists(path) ? File.ReadAllBytes(path) : null);
            if (patch.writes.Count > 0) FileJournal.Commit(path, patch.writes, patch.length);
#if DEBUG
            Verify(document, path, patch.map);
#endif
            return patch.map;
        }

        // The writes that bring file, as map was taken from it, up to the note; the whole file without either. Reuses file when its length holds.
        public static SegmentPatch Prepare(RichTextDocument document, string path, List<NoteSegment>? map, byte[]? file)
        {
            SegmentPatch patch = map != null && file != null ? Patch(document, path, map, file) : Whole(document, path);
            ClearDirty(document);
            return patch;
        }

        // The whole file, and the segments it lays out.
        public static byte[] Build(RichTextDocument document, string path, out List<NoteSegment> map)
        {
            XNamespace ns = XSDGenerator.NamespaceFor("UI");
            string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;

            StringBuilder text = new StringBuilder(HeaderText(document, path));
            List<(NoteSegmentKind kind, PageInsert? insert, NoteNode[] nodes, int at)> marks = [(NoteSegmentKind.Header, null, Array.Empty<NoteNode>(), 0)];

            if (DocumentXml.WriteLayout(ns, document.layout) is XElement layout)
                Entry(text, marks, NoteSegmentKind.Layout, null, layout, Array.Empty<NoteNode>());
            foreach ((PageInsert? insert, NoteNode[] nodes) in DocumentXml.Groups(document))
            {
                XElement element = DocumentXml.WriteGroup(ns, insert, nodes);
                DocumentXml.RelativePictures(element, folder);
                Entry(text, marks, NoteSegmentKind.Entry, insert, element, nodes);
            }

            text.Append(XMLWriter.Newline);
            marks.Add((NoteSegmentKind.Footer, null, Array.Empty<NoteNode>(), text.Length));
            text.Append(footer);

            string written = text.ToString();
            map = Segments(written, bom.Length, marks);
            return [.. bom, .. Encoding.UTF8.GetBytes(written)];
        }

        private static void Entry(StringBuilder text, List<(NoteSegmentKind kind, PageInsert? insert, NoteNode[] nodes, int at)> marks,
            NoteSegmentKind kind, PageInsert? insert, XElement element, NoteNode[] nodes)
        {
            text.Append(XMLWriter.Newline).Append(XMLWriter.Indent);
            marks.Add((kind, insert, nodes, text.Length));
            XMLWriter.Element(text, element, 1);
        }

        // The declaration and the root's start tag.
        private static string HeaderText(RichTextDocument document, string path)
        {
            XNamespace ns = XSDGenerator.NamespaceFor("UI");
            XElement root = DocumentXml.WriteScalars(ns + "Document", document);
            DocumentXml.BindSchema(root, path);

            StringBuilder text = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?>").Append(XMLWriter.Newline);
            XMLWriter.StartTag(text, root, true);
            return text.Append('>').ToString();
        }

        private static SegmentPatch Whole(RichTextDocument document, string path)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            byte[] file = Build(document, path, out List<NoteSegment> built);
            return new SegmentPatch([(0, file)], file.Length, built, file);
        }
        #endregion

        #region ---- patch ----
        // One top-level stretch of the file being written: kept from the old map, or written anew.
        private sealed class Unit
        {
            public NoteSegmentKind kind;
            public PageInsert? insert;
            public NoteNode[] nodes = Array.Empty<NoteNode>();
            public int old = -1;
            public byte[]? text;
            public long start;
            public int length;
        }

        // A stretch of the old file replaced whole: from the kept element before it to the kept segment after.
        private readonly record struct Hole(long from, long to, byte[] content);

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static SegmentPatch Patch(RichTextDocument document, string path, List<NoteSegment> map, byte[] file)
        {
            XNamespace ns = XSDGenerator.NamespaceFor("UI");
            string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
            long oldLength = file.Length;
            List<Hole> holes = new List<Hole>();
            List<(long offset, byte[] bytes)> writes = new List<(long offset, byte[] bytes)>();
            long newLength = oldLength;

            List<Unit> units = Match(document, path, map, file);
            foreach (Unit unit in units)
            {
                if (unit.old >= 0 || unit.kind == NoteSegmentKind.Header) continue;
                XElement element = unit.kind == NoteSegmentKind.Layout
                    ? DocumentXml.WriteLayout(ns, document.layout)!
                    : DocumentXml.WriteGroup(ns, unit.insert, unit.nodes);
                DocumentXml.RelativePictures(element, folder);
                unit.text = Utf8(ElementText(element));
            }

            long delta = 0;
            int kept = -1;
            for (int u = 0; u < units.Count; u++)
            {
                Unit unit = units[u];
                if (unit.old < 0) continue;

                int before = kept < 0 ? -1 : units[kept].old;
                if (u - kept > 1 || unit.old - before > 1)
                {
                    Hole hole = Fill(units, kept, u, map);
                    for (int w = kept + 1; w < u; w++) units[w].start += delta;
                    holes.Add(hole);
                    delta += hole.content.Length - (hole.to - hole.from);
                }

                unit.start = map[unit.old].start + delta;
                unit.length = map[unit.old].length;
                kept = u;
            }

            int first = holes.FindIndex(h => h.content.Length != h.to - h.from);
            for (int h = 0; h < (first < 0 ? holes.Count : first); h++)
                writes.Add((holes[h].from, holes[h].content));

            if (first >= 0)
            {
                long from = holes[first].from;
                using MemoryStream tail = new MemoryStream();
                for (int h = first; h < holes.Count; h++)
                {
                    tail.Write(holes[h].content);
                    long keptTo = h + 1 < holes.Count ? holes[h + 1].from : oldLength;
                    tail.Write(file, (int)holes[h].to, (int)(keptTo - holes[h].to));
                }
                writes.Add((from, tail.ToArray()));
                newLength = from + tail.Length;
            }

            byte[] next = file;
            if (newLength != oldLength)
            {
                next = new byte[newLength];
                file.AsSpan(0, (int)Math.Min(oldLength, newLength)).CopyTo(next);
            }
            foreach ((long offset, byte[] bytes) in writes)
                bytes.CopyTo(next, offset);

            List<NoteSegment> segments = new List<NoteSegment>(units.Count);
            for (int u = 0; u < units.Count; u++)
            {
                long end = u + 1 < units.Count ? units[u + 1].start : newLength;
                segments.Add(new NoteSegment
                {
                    kind = units[u].kind, insert = units[u].insert, nodes = units[u].nodes,
                    start = units[u].start, length = units[u].length, capacity = (int)(end - units[u].start)
                });
            }
            return new SegmentPatch(writes, newLength, segments, next);
        }

        // The file's units in order, each holding the old segment it keeps when that segment is unchanged.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static List<Unit> Match(RichTextDocument document, string path, List<NoteSegment> map, byte[] file)
        {
            ReadOnlySpan<byte> oldHeader = file.AsSpan(0, map[0].length);
            bool hadBom = oldHeader.StartsWith(bom);
            byte[] header = hadBom ? [.. bom, .. Utf8(HeaderText(document, path))] : Utf8(HeaderText(document, path));

            List<Unit> units = [new Unit { kind = NoteSegmentKind.Header, text = header, old = header.AsSpan().SequenceEqual(oldHeader) ? 0 : -1 }];
            int last = units[0].old;

            if (DocumentXml.WriteLayout(XSDGenerator.NamespaceFor("UI"), document.layout) is XElement layout)
            {
                byte[] text = Utf8(ElementText(layout));
                bool same = map.Count > 1 && map[1].kind == NoteSegmentKind.Layout
                    && text.AsSpan().SequenceEqual(file.AsSpan((int)map[1].start, map[1].length));
                units.Add(new Unit { kind = NoteSegmentKind.Layout, text = same ? null : text, old = same ? 1 : -1 });
                if (same) last = 1;
            }

            Dictionary<NoteNode, int> entries = new Dictionary<NoteNode, int>();
            for (int s = 0; s < map.Count; s++)
                if (map[s].kind == NoteSegmentKind.Entry) entries[map[s].nodes[0]] = s;

            foreach ((PageInsert? insert, NoteNode[] nodes) in DocumentXml.Groups(document))
            {
                Unit unit = new Unit { kind = NoteSegmentKind.Entry, insert = insert, nodes = nodes };
                if (entries.TryGetValue(nodes[0], out int s) && s > last && map[s].insert == insert
                    && map[s].nodes.AsSpan().SequenceEqual(nodes) && !nodes.Any(Dirty))
                {
                    unit.old = s;
                    last = s;
                }
                units.Add(unit);
            }

            units.Add(new Unit { kind = NoteSegmentKind.Footer, old = map.Count - 1 });
            return units;
        }

        // The bytes between two kept units, slack after the last written one; starts are in old-file coordinates.
        private static Hole Fill(List<Unit> units, int before, int after, List<NoteSegment> map)
        {
            long from = before < 0 ? 0 : map[units[before].old].start + map[units[before].old].length;
            long to = map[units[after].old].start;

            using MemoryStream content = new MemoryStream();
            int owner = before < 0 ? 0 : map[units[before].old].length;
            for (int u = before + 1; u < after; u++)
            {
                if (units[u].kind != NoteSegmentKind.Header) content.Write(Utf8(XMLWriter.Newline + XMLWriter.Indent));
                units[u].start = from + content.Length;
                units[u].length = units[u].text!.Length;
                content.Write(units[u].text);
                owner = units[u].length;
            }

            byte[] separator = Utf8(XMLWriter.Newline + (units[after].kind == NoteSegmentKind.Footer ? "" : XMLWriter.Indent));
            long needed = content.Length + separator.Length;
            long room = to - from;
            long pad = needed <= room && room - needed <= Math.Max(256, owner / 2) ? room - needed : Math.Max(64, owner / 4);

            for (long i = 0; i < pad; i++) content.WriteByte((byte)' ');
            content.Write(separator);
            return new Hole(from, to, content.ToArray());
        }

        // A file written outside, mapped with the note's nodes on every leading and trailing entry whose bytes the note would write.
        internal static List<NoteSegment>? Rebase(byte[] file, RichTextDocument outside, RichTextDocument document, string path)
        {
            List<NoteSegment>? map = Map(file, outside);
            if (map == null) return null;

            XNamespace ns = XSDGenerator.NamespaceFor("UI");
            string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
            List<(PageInsert? insert, NoteNode[] nodes, byte[] text)> groups = DocumentXml.Groups(document).Select(g =>
            {
                XElement element = DocumentXml.WriteGroup(ns, g.insert, g.nodes);
                DocumentXml.RelativePictures(element, folder);
                return (g.insert, g.nodes, Utf8(ElementText(element)));
            }).ToList();
            List<NoteSegment> entries = map.Where(s => s.kind == NoteSegmentKind.Entry).ToList();

            bool Same(NoteSegment segment, byte[] text) =>
                segment.length == text.Length && file.AsSpan((int)segment.start, segment.length).SequenceEqual(text);
            void Take(NoteSegment segment, (PageInsert? insert, NoteNode[] nodes, byte[] text) group)
            {
                segment.nodes = group.nodes;
                segment.insert = group.insert;
            }

            int lead = 0;
            for (; lead < entries.Count && lead < groups.Count && Same(entries[lead], groups[lead].text); lead++)
                Take(entries[lead], groups[lead]);
            for (int tail = 1; tail <= entries.Count - lead && tail <= groups.Count - lead && Same(entries[^tail], groups[^tail].text); tail++)
                Take(entries[^tail], groups[^tail]);

            return map;
        }

        private static bool Dirty(NoteNode node) =>
            node.dirty || node is NoteTable table && table.rows.Any(row => row.Any(cell => cell.blocks.Any(block => block.dirty)));

        private static void ClearDirty(RichTextDocument document)
        {
            foreach (NoteNode node in document.blocks)
            {
                node.dirty = false;
                if (node is NoteTable table)
                    foreach (NoteCell[] row in table.rows)
                        foreach (NoteCell cell in row)
                            foreach (NoteBlock block in cell.blocks)
                                block.dirty = false;
            }
        }

        private static string ElementText(XElement element)
        {
            StringBuilder text = new StringBuilder();
            XMLWriter.Element(text, element, 1);
            return text.ToString();
        }

        private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

#if DEBUG
        // The file reads back as the note and maps to the segments the write reported.
        private static void Verify(RichTextDocument document, string path, List<NoteSegment> written)
        {
            if (DocumentXml.ToXml(DocumentXml.Load(path)).ToString() != DocumentXml.ToXml(document).ToString())
                throw new InvalidOperationException($"'{path}' does not read back as the note that was written.");

            List<NoteSegment>? mapped = Map(File.ReadAllBytes(path), document);
            bool same = mapped != null && mapped.Count == written.Count && mapped.Zip(written).All(p => p.First.kind == p.Second.kind
                && p.First.start == p.Second.start && p.First.length == p.Second.length && p.First.capacity == p.Second.capacity
                && p.First.insert == p.Second.insert && p.First.nodes.AsSpan().SequenceEqual(p.Second.nodes));
            if (!same) throw new InvalidOperationException($"'{path}' does not map to the segments its write reported.");
        }
#endif
        #endregion

        #region ---- map ----
        // The segments of a note file that was read into document; null when its entries don't line up with the nodes.
        public static List<NoteSegment>? Map(byte[] file, RichTextDocument document)
        {
            int skip = file.AsSpan().StartsWith(bom) ? bom.Length : 0;
            string text = Encoding.UTF8.GetString(file, skip, file.Length - skip);
            List<int> lines = LineStarts(text);

            List<(NoteSegmentKind kind, PageInsert? insert, NoteNode[] nodes, int at)> marks = [(NoteSegmentKind.Header, null, Array.Empty<NoteNode>(), 0)];
            NoteNode[] blocks = document.blocks;
            int next = 0;

            using XmlReader reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            IXmlLineInfo info = (IXmlLineInfo)reader;
            reader.MoveToContent();
            reader.Read();
            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == 0)
                {
                    marks.Add((NoteSegmentKind.Footer, null, Array.Empty<NoteNode>(), TagStart(text, lines, info)));
                    break;
                }
                if (reader.NodeType != XmlNodeType.Element || reader.Depth != 1)
                {
                    reader.Read();
                    continue;
                }

                int at = TagStart(text, lines, info);
                switch (reader.LocalName)
                {
                    case "DocumentLayout":
                        marks.Add((NoteSegmentKind.Layout, null, Array.Empty<NoteNode>(), at));
                        reader.Skip();
                        break;
                    case "Block":
                    case "Table":
                        if (next >= blocks.Length) return null;
                        marks.Add((NoteSegmentKind.Entry, null, [blocks[next++]], at));
                        reader.Skip();
                        break;
                    case "Footnote":
                    case "Float":
                        XElement group = (XElement)XNode.ReadFrom(reader);
                        int count = group.Elements().Count(e => e.Name.LocalName is "Block" or "Table");
                        if (count == 0 || next + count > blocks.Length) return null;
                        marks.Add((NoteSegmentKind.Entry, blocks[next].insert, blocks[next..(next + count)], at));
                        next += count;
                        break;
                    case "Sealed":
                        if (next >= blocks.Length) return null;
                        int end = next + 1;
                        if (blocks[next].seal is NoteSeal seal)
                            while (end < blocks.Length && blocks[end].seal == seal) end++;
                        else if (blocks[next] is not NoteSealed) return null;
                        marks.Add((NoteSegmentKind.Entry, null, blocks[next..end], at));
                        next = end;
                        reader.Skip();
                        break;
                    default:
                        return null;
                }
            }

            if (next != blocks.Length || marks[^1].kind != NoteSegmentKind.Footer) return null;
            return Segments(text, skip, marks);
        }

        private static List<int> LineStarts(string text)
        {
            List<int> lines = [0];
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                else if (text[i] != '\r' && text[i] != '\n') continue;
                lines.Add(i + 1);
            }
            return lines;
        }

        // Where the reader's current tag opens.
        private static int TagStart(string text, List<int> lines, IXmlLineInfo info)
        {
            int at = lines[info.LineNumber - 1] + info.LinePosition - 1;
            while (text[at] != '<') at--;
            return at;
        }
        #endregion

        // Char marks as byte ranges: a segment runs to the next one's start, its trailing whitespace the slack.
        private static List<NoteSegment> Segments(string text, int skip, List<(NoteSegmentKind kind, PageInsert? insert, NoteNode[] nodes, int at)> marks)
        {
            List<NoteSegment> segments = new List<NoteSegment>(marks.Count);
            long bytes = skip;
            for (int i = 0; i < marks.Count; i++)
            {
                int from = marks[i].at;
                int to = i + 1 < marks.Count ? marks[i + 1].at : text.Length;
                int span = Encoding.UTF8.GetByteCount(text.AsSpan(from, to - from));

                int trailing = 0;
                while (trailing < to - from && text[to - 1 - trailing] is ' ' or '\t' or '\r' or '\n') trailing++;

                long start = i == 0 ? 0 : bytes;
                int capacity = (int)(bytes + span - start);
                segments.Add(new NoteSegment
                {
                    kind = marks[i].kind, insert = marks[i].insert, nodes = marks[i].nodes,
                    start = start, length = capacity - trailing, capacity = capacity
                });
                bytes += span;
            }
            return segments;
        }
    }
}
