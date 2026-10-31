using ArctisAurora.Core.Editing;
using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Security;
using ArctisAurora.Core.UI.Notes;
using ArctisAurora.Core.UI.Sheets;
using ArctisAurora.Core.UI.Text;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;

namespace ArctisAurora.Core.UI.Document
{
    // The note model, and the on-disk format.
    [A_XSDType("Document", "UI")]
    public class RichTextDocument
    {
        public NoteNode[] blocks = Array.Empty<NoteNode>();

        // Absent until the note is named. Nothing derives it from the file, so a note that has never
        // been named reads as unnamed however many times it is saved.
        [A_XSDElementProperty("Name", "UI", "Display name of the note. Absent means the file name stands in.")]
        public string? name;

        [A_XSDElementProperty("DocumentLayout", "UI", "Layout parameters for this note.")]
        public DocumentLayout layout = new DocumentLayout();

        // note properties
        [A_XSDElementProperty("Palette", "UI", "Palette the note is shown in. Absent means the app's.")]
        public string? palette;

        [A_XSDElementProperty("Created", "UI", "When the note was created, ISO 8601.")]
        public string? created;

        [A_XSDElementProperty("Modified", "UI", "When the note was last saved with changes, ISO 8601.")]
        public string? modified;

        [A_XSDElementProperty("ReadOnly", "UI", "Whether the editor refuses changes to the note's text.")]
        public bool readOnly;

        [A_XSDElementProperty("Frontmatter", "UI", "A Markdown note's metadata block as written, delimiter lines included.")]
        public string? frontmatter;

        // source text: its code language, line ending and byte-order mark, kept for the save
        public string? sourceLanguage;
        public string sourceNewline = "\n";
        public bool sourceBom;

        // the key the file is encrypted under; null for a plain file
        public PasswordKey? encryption;

        // Whether the document editor opens this file.
        public static bool Opens(string path) =>
            FileTypes.KindOf(path) is FileKind.Note or FileKind.Markdown or FileKind.PlainText or FileKind.Tex or FileKind.Xml;

        public static RichTextDocument Load(string path, PasswordKey? key = null)
        {
            FileJournal.Replay(path);
            RichTextDocument document = FileTypes.KindOf(path) switch
            {
                FileKind.Markdown => DocumentXml.Parse(MarkdownPictures(MarkdownFormat.Read(VaultFiles.ReadText(path), Path.GetFileNameWithoutExtension(path)), path)),
                FileKind.PlainText => DocumentXml.Parse(PlainTextFormat.Read(VaultFiles.ReadText(path), Path.GetFileNameWithoutExtension(path))),
                FileKind.Tex => LoadSource(path, "latex"),
                FileKind.Xml => LoadSource(path, "xml"),
                _ => DocumentXml.Load(path, key)
            };

            document.created ??= Stamp(File.GetCreationTime(path));
            if (!SettingsRegistry.Get<DocumentSettings>().stampModified) document.modified = Stamp(File.GetLastWriteTime(path));
            return document;
        }

        // A file's text as code lines in a language.
        public static RichTextDocument LoadSource(string path, string language) =>
            SourceFromBytes(VaultFiles.ReadBytes(path), Path.GetFileNameWithoutExtension(path), language);

        public static RichTextDocument SourceFromBytes(byte[] bytes, string name, string language)
        {
            bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            string text = Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));

            RichTextDocument document = DocumentXml.Parse(TexSourceFormat.Read(text, name, language));
            document.sourceLanguage = language;
            document.sourceNewline = text.Contains("\r\n") ? "\r\n" : "\n";
            document.sourceBom = bom;
            return document;
        }

        public string SourceText() => TexSourceFormat.Write(DocumentXml.ToXml(this), sourceNewline);

        private static XElement MarkdownPictures(XElement root, string path)
        {
            DocumentXml.ResolvePictures(root, Path.GetDirectoryName(Path.GetFullPath(path))!);
            return root;
        }

        // A local time as the properties store it.
        public static string Stamp(DateTime time) =>
            new DateTimeOffset(time).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);

        public void Save(string path)
        {
            if (sourceLanguage != null)
            {
                VaultFiles.WriteText(path, SourceText(), new UTF8Encoding(sourceBom));
                return;
            }

            switch (FileTypes.KindOf(path))
            {
                case FileKind.Markdown:
                    XElement root = DocumentXml.ToXml(this);
                    DocumentXml.RelativePictures(root, Path.GetDirectoryName(Path.GetFullPath(path))!);
                    VaultFiles.WriteText(path, MarkdownFormat.Write(root));
                    break;
                case FileKind.PlainText: VaultFiles.WriteText(path, PlainTextFormat.Write(DocumentXml.ToXml(this))); break;
                case FileKind.Tex: VaultFiles.WriteText(path, SourceText(), new UTF8Encoding(sourceBom)); break;
                default: DocumentXml.Save(this, path); break;
            }
        }

        #region ---- editing ----
        public event Action<NoteChange>? changed;

        private void Raise(NoteChangeKind kind, int first, int count = 1, DocumentAddress at = default, int length = 0,
            DocumentAddress? anchor = null, DocumentAddress? caret = null)
        {
            MarkDirty(kind, first, count);
            changed?.Invoke(new NoteChange(kind, first, count, at, length, anchor, caret));
        }

        // Flags the nodes an edit changed in place; a node an edit put in is new and carries no flag.
        private void MarkDirty(NoteChangeKind kind, int first, int count)
        {
            switch (kind)
            {
                case NoteChangeKind.Text:
                case NoteChangeKind.Spans:
                case NoteChangeKind.Kind:
                    if (count == 1)
                    {
                        if (BlockAt(first) is NoteBlock block) block.dirty = true;
                        break;
                    }
                    List<NoteBlock> flat = Blocks();
                    for (int b = Math.Max(first, 0); b < first + count && b < flat.Count; b++)
                        flat[b].dirty = true;
                    break;
                case NoteChangeKind.Inserted:
                    if (TableHolding(first) is NoteTable into) into.dirty = true;
                    break;
                case NoteChangeKind.Removed:
                    if (TableHolding(first - 1) is NoteTable from) from.dirty = true;
                    break;
            }
        }

        // The table whose cells hold the block at a flat index; null for a block of the note itself.
        private NoteTable? TableHolding(int index)
        {
            if (index < 0) return null;

            foreach (NoteNode node in blocks)
            {
                if (node is NoteBlock)
                {
                    if (index-- == 0) return null;
                }
                else if (node is NoteTable table)
                    foreach (NoteCell[] row in table.rows)
                        foreach (NoteCell cell in row)
                        {
                            if (index < cell.blocks.Length) return table;
                            index -= cell.blocks.Length;
                        }
            }

            return null;
        }

        // Every block in reading order, a table's cells included.
        public List<NoteBlock> Blocks()
        {
            List<NoteBlock> flat = new List<NoteBlock>();
            foreach (NoteNode node in blocks)
            {
                if (node is NoteBlock block) flat.Add(block);
                else if (node is NoteTable table)
                    foreach (NoteCell[] row in table.rows)
                        foreach (NoteCell cell in row)
                            flat.AddRange(cell.blocks);
            }

            return flat;
        }

        // The block at a flat index without building the list; null past the end.
        public NoteBlock? BlockAt(int index)
        {
            if (index < 0) return null;

            foreach (NoteNode node in blocks)
            {
                if (node is NoteBlock block)
                {
                    if (index-- == 0) return block;
                }
                else if (node is NoteTable table)
                    foreach (NoteCell[] row in table.rows)
                        foreach (NoteCell cell in row)
                        {
                            if (index < cell.blocks.Length) return cell.blocks[index];
                            index -= cell.blocks.Length;
                        }
            }

            return null;
        }

        public bool Resolve(DocumentAddress at, out NoteBlock block, out int offset)
        {
            block = null!;
            offset = 0;

            if (BlockAt(at.block) is not NoteBlock found) return false;

            block = found;
            offset = Math.Clamp(at.offset, 0, block.run.Length);
            return true;
        }

        // The cell holding a block; null for a block of the note itself.
        public NoteCell? CellOf(NoteBlock block)
        {
            foreach (NoteNode node in blocks)
                if (node is NoteTable table)
                    foreach (NoteCell[] row in table.rows)
                        foreach (NoteCell cell in row)
                            if (Array.IndexOf(cell.blocks, block) >= 0) return cell;

            return null;
        }

        private void InsertAfter(NoteBlock after, NoteBlock[] added)
        {
            if (CellOf(after) is NoteCell cell) cell.blocks = Inserted(cell.blocks, Array.IndexOf(cell.blocks, after) + 1, added);
            else blocks = Inserted(blocks, Array.IndexOf(blocks, after) + 1, added);
        }

        private void Detach(NoteBlock block)
        {
            if (CellOf(block) is NoteCell cell) cell.blocks = Removed(cell.blocks, block);
            else blocks = Removed(blocks, block);
        }

        private static T[] Inserted<T>(T[] array, int at, IEnumerable<T> items) => [.. array.AsSpan(0, at), .. items, .. array.AsSpan(at)];

        private static T[] Removed<T>(T[] array, T item)
        {
            int at = Array.IndexOf(array, item);
            return at < 0 ? array : [.. array.AsSpan(0, at), .. array.AsSpan(at + 1)];
        }

        public void InsertText(DocumentAddress at, string insert)
        {
            if (Resolve(at, out NoteBlock block, out _)) InsertText(block, at, insert);
        }

        // For a caller that already holds the block at the address.
        public void InsertText(NoteBlock block, DocumentAddress at, string insert)
        {
            int offset = Math.Clamp(at.offset, 0, block.run.Length);
            block.run.InsertText(offset, insert);
            Raise(NoteChangeKind.Text, at.block, at: new DocumentAddress(at.block, offset), length: insert.Length,
                caret: new DocumentAddress(at.block, offset + insert.Length));
        }

        public void RemoveText(DocumentAddress at, int count)
        {
            if (!Resolve(at, out NoteBlock block, out int offset)) return;
            if (offset + count > block.run.Length) return;

            block.run.RemoveText(offset, count);
            DocumentAddress start = new DocumentAddress(at.block, offset);
            Raise(NoteChangeKind.Text, at.block, at: start, length: -count, caret: start);
        }

        // The head block keeps its prefix and the caret, the tail block's suffix joins it, and every
        // block the range crossed whole goes with the tail.
        public void DeleteBetween(DocumentAddress from, DocumentAddress to)
        {
            List<NoteBlock> flat = Blocks();
            if (from.block < 0 || to.block >= flat.Count || from.block > to.block) return;

            NoteBlock head = flat[from.block];

            if (from.block == to.block)
            {
                head.run.RemoveText(from.offset, to.offset - from.offset);
                Raise(NoteChangeKind.Text, from.block, at: from, length: from.offset - to.offset, caret: from);
                return;
            }

            NoteBlock tail = flat[to.block];
            head.run.RemoveText(from.offset, head.run.Length - from.offset);
            tail.run.RemoveText(0, to.offset);
            head.run.AppendSpans(tail.run.spans, tail.run.text ?? string.Empty);
            if (head.stylingType == TextStyleType.Rule) head.TakeKind(tail.SliceSnapshot(0, 0));
            head.seal ??= tail.seal;

            for (int i = to.block; i > from.block; i--)
                Detach(flat[i]);

            Raise(NoteChangeKind.Removed, from.block + 1, to.block - from.block, at: from, length: to.offset);
            Raise(NoteChangeKind.Kind, from.block, caret: from);
        }

        // Everything the range covers, as data: the head block's cut suffix, then whole blocks, then
        // the tail block's cut prefix.
        public DocumentFragment CaptureFragment(DocumentAddress from, DocumentAddress to)
        {
            DocumentFragment fragment = new DocumentFragment();
            List<NoteBlock> flat = Blocks();

            for (int i = from.block; i <= to.block && i < flat.Count; i++)
                fragment.blocks.Add(flat[i].SliceSnapshot(
                    i == from.block ? from.offset : 0,
                    i == to.block ? to.offset : flat[i].run.Length));

            return fragment;
        }

        // The inverse of a range delete. The head block takes its cut text back, and when the range
        // crossed blocks the survivors that were merged into it move back out into rebuilt ones.
        public void InsertFragment(DocumentAddress at, DocumentFragment fragment,
            DocumentAddress? anchor = null, DocumentAddress? caret = null)
        {
            if (fragment.blocks.Count == 0) return;
            if (!Resolve(at, out NoteBlock head, out int offset)) return;

            DocumentAddress start = new DocumentAddress(at.block, offset);
            caret ??= start;

            if (fragment.blocks.Count == 1)
            {
                head.run.InsertSpans(offset, fragment.blocks[0].spans, fragment.blocks[0].text);
                Raise(NoteChangeKind.Text, at.block, at: start, length: fragment.blocks[0].text.Length, anchor: anchor, caret: caret);
                return;
            }

            // everything after the insertion point in the head block was moved there by the merge
            BlockSnapshot rest = head.SliceSnapshot(offset, head.run.Length);
            head.run.RemoveText(offset, head.run.Length - offset);
            head.run.AppendSpans(fragment.blocks[0].spans, fragment.blocks[0].text);

            NoteBlock[] added = new NoteBlock[fragment.blocks.Count - 1];
            for (int i = 1; i < fragment.blocks.Count; i++)
            {
                added[i - 1] = NoteBlock.From(fragment.blocks[i]);
                added[i - 1].seal = head.seal;
            }
            added[^1].run.AppendSpans(rest.spans, rest.text);
            InsertAfter(head, added);

            Raise(NoteChangeKind.Inserted, at.block + 1, added.Length, at: start, length: fragment.blocks[^1].text.Length);
            Raise(NoteChangeKind.Kind, at.block, anchor: anchor, caret: caret);
        }

        // Puts a block's kind back without touching its text; what undoing a merge into a rule needs.
        public void RestoreKind(DocumentAddress at, BlockSnapshot kind,
            DocumentAddress? anchor = null, DocumentAddress? caret = null)
        {
            if (!Resolve(at, out NoteBlock block, out _)) return;

            block.TakeKind(kind);
            Raise(NoteChangeKind.Kind, at.block, anchor: anchor, caret: caret);
        }

        // A fragment put in forwards, for a paste or a drop and their redo; the caret ends after it.
        public void InsertBetween(DocumentAddress from, DocumentAddress to, DocumentFragment fragment) =>
            InsertFragment(from, fragment, caret: to);

        // The split itself, addressed rather than read off the caret, so redo can replay it.
        public void SplitBlockAt(DocumentAddress at)
        {
            if (!Resolve(at, out NoteBlock block, out int offset)) return;

            NoteBlock tail = block.SplitAt(offset);
            InsertAfter(block, [tail]);

            DocumentAddress start = new DocumentAddress(at.block, offset);
            Raise(NoteChangeKind.Spans, at.block);
            Raise(NoteChangeKind.Inserted, at.block + 1, at: start, caret: new DocumentAddress(at.block + 1, 0));
        }

        // The inverse of a split.
        public void JoinBlockWithNext(DocumentAddress at)
        {
            List<NoteBlock> flat = Blocks();
            if (at.block < 0 || at.block + 1 >= flat.Count) return;

            NoteBlock head = flat[at.block];
            NoteBlock tail = flat[at.block + 1];

            head.run.AppendSpans(tail.run.spans, tail.run.text ?? string.Empty);
            head.seal ??= tail.seal;
            Detach(tail);

            Raise(NoteChangeKind.Removed, at.block + 1, at: at);
            Raise(NoteChangeKind.Kind, at.block, caret: at);
        }

        // Rewrites blocks in place, text and kind alike.
        public void ChangeBlocks(int first, int last, Action<NoteBlock> change)
        {
            List<NoteBlock> flat = Blocks();
            first = Math.Max(first, 0);
            last = Math.Min(last, flat.Count - 1);
            if (last < first) return;

            for (int b = first; b <= last; b++)
                change(flat[b]);

            Raise(NoteChangeKind.Kind, first, last - first + 1);
        }

        // Undo for both styling primitives. Neither adds or removes a block, so the blocks
        // themselves survive and only their spans are rewritten.
        public void RestoreBlocks(int firstBlock, List<BlockSnapshot> before)
        {
            List<NoteBlock> flat = Blocks();
            int first = Math.Max(firstBlock, 0);
            int last = Math.Min(firstBlock + before.Count, flat.Count) - 1;
            if (last < first) return;

            for (int b = first; b <= last; b++)
                flat[b].Restore(before[b - firstBlock]);

            Raise(NoteChangeKind.Kind, first, last - first + 1);
        }

        public void SetBlockStylingBetween(int first, int last, TextStyleType type) =>
            ChangeBlocks(first, last, b => b.stylingType = type);

        // Addressed rather than read off the selection, so redo can replay it against the spans undo
        // restored. Offsets are block-relative, so nothing here has to survive a re-partition.
        public void ApplyStyleBetween(DocumentAddress from, DocumentAddress to, StyleDelta delta)
        {
            List<NoteBlock> flat = Blocks();
            if (from.block < 0 || to.block >= flat.Count || to.block < from.block) return;

            for (int b = from.block; b <= to.block; b++)
            {
                int start = b == from.block ? Math.Clamp(from.offset, 0, flat[b].run.Length) : 0;
                int end = b == to.block ? Math.Clamp(to.offset, 0, flat[b].run.Length) : flat[b].run.Length;
                if (end > start) flat[b].run.StyleRange(start, end, delta);
            }

            Raise(NoteChangeKind.Spans, from.block, to.block - from.block + 1, anchor: from, caret: to);
        }

        // Gives a character range the inline code styling.
        public static void MarkCode(NoteBlock block, int from, int to)
        {
            List<StyleSpan> spans = block.run.spans;
            block.run.SplitSpanAt(to);
            int first = block.run.SplitSpanAt(from);
            int at = from;
            for (int i = first; i < spans.Count && at < to; i++)
            {
                StyleSpan span = spans[i];
                at += span.count;
                span.stylingType = TextStyleType.Code;
                span.fontSizeAuthored = false;
                spans[i] = span;
            }
            block.run.MergeSpans();
        }

        // Undo and redo: the picture as stored, left selected.
        public void SetPicture(DocumentAddress at, StyleSpan picture)
        {
            if (!Resolve(at, out NoteBlock block, out int index)) return;

            block.run.SetPicture(index, picture);
            Raise(NoteChangeKind.Spans, at.block, anchor: at, caret: new DocumentAddress(at.block, at.offset + 1));
        }

        // Rewrites a formula's source and leaves it selected; the preview, undo and redo.
        public void SetMath(DocumentAddress at, string source)
        {
            if (!Resolve(at, out NoteBlock block, out int index)) return;

            block.run.SetMath(index, source);
            Raise(NoteChangeKind.Spans, at.block, anchor: at, caret: new DocumentAddress(at.block, at.offset + 1));
        }

        // Rewrites links the rename answers for; true when any changed.
        public bool RenameSheetLinks(Func<string, string?> rename)
        {
            bool any = false;
            List<NoteBlock> flat = Blocks();
            for (int b = 0; b < flat.Count; b++)
            {
                Span<StyleSpan> spans = CollectionsMarshal.AsSpan(flat[b].run.spans);
                bool changed = false;
                for (int i = 0; i < spans.Length; i++)
                {
                    if (spans[i].IsMath && SheetLinks.HasMathLinks(spans[i].mathSource))
                    {
                        string source = SheetLinks.RenameMath(spans[i].mathSource, rename);
                        if (source == spans[i].mathSource) continue;
                        spans[i].mathSource = source;
                        changed = true;
                        continue;
                    }
                    if (!spans[i].IsSheet || rename(spans[i].sheetRef) is not string renamed) continue;
                    spans[i].sheetRef = renamed;
                    changed = true;
                }
                if (!changed) continue;
                Raise(NoteChangeKind.Spans, b);
                any = true;
            }
            return any;
        }

        // Rewrites note link targets the rename answers for, and a link's text where it showed its target; true when any changed.
        public bool RenameNoteLinks(Func<string, string?> rename)
        {
            bool any = false;
            List<NoteBlock> flat = Blocks();
            for (int b = 0; b < flat.Count; b++)
            {
                TextRunData run = flat[b].run;
                Span<StyleSpan> spans = CollectionsMarshal.AsSpan(run.spans);
                bool changed = false, retexted = false;
                int start = 0;
                for (int i = 0; i < spans.Length; i++)
                {
                    string text = run.text ?? string.Empty;
                    int count = i == spans.Length - 1 ? text.Length - start : spans[i].count;
                    if (spans[i].link != null && rename(spans[i].link) is string renamed)
                    {
                        if (count == spans[i].link.Length && string.CompareOrdinal(text, start, spans[i].link, 0, count) == 0)
                        {
                            run.text = text.Remove(start, count).Insert(start, renamed);
                            count = renamed.Length;
                            spans[i].count = count;
                            retexted = true;
                        }
                        spans[i].link = renamed;
                        changed = true;
                    }
                    start += count;
                }
                if (!changed) continue;
                Raise(retexted ? NoteChangeKind.Text : NoteChangeKind.Spans, b);
                any = true;
            }
            return any;
        }

        // Takes the table at a note-level index out when present, and puts one built from xml there.
        public NoteTable? PutTable(int index, bool present, XElement? xml, DocumentAddress? caret = null)
        {
            NoteSeal? seal = present ? blocks[index].seal
                : index > 0 && index < blocks.Length && blocks[index - 1].seal == blocks[index].seal ? blocks[index].seal : null;
            if (present && blocks[index] is NoteTable old) blocks = Removed(blocks, old);

            NoteTable? table = null;
            if (xml != null)
            {
                table = DocumentXml.ReadTable(xml);
                table.seal = seal;
                blocks = Inserted(blocks, index, [table]);
            }

            Raise(NoteChangeKind.Table, index, caret: caret);
            return table;
        }

        // Swaps the note-level entries [first, first + removed) for added; the change counts flat blocks.
        public void ReplaceNodes(int first, int removed, NoteNode[] added)
        {
            int flat = 0;
            for (int i = 0; i < first; i++) flat += FlatCount(blocks[i]);
            int gone = 0;
            for (int i = first; i < first + removed; i++) gone += FlatCount(blocks[i]);

            blocks = [.. blocks.AsSpan(0, first), .. added, .. blocks.AsSpan(first + removed)];
            Raise(NoteChangeKind.Nodes, flat, gone, length: added.Sum(FlatCount));
        }

        private static int FlatCount(NoteNode node) => node switch
        {
            NoteTable table => table.rows.Sum(row => row.Sum(cell => cell.blocks.Length)),
            NoteSealed => 0,
            NoteToc => 0,
            _ => 1
        };

        // The note-level entry holding a flat block.
        public int NodeIndexOf(int flat)
        {
            for (int i = 0; i < blocks.Length; i++)
            {
                int count = FlatCount(blocks[i]);
                if (flat < count) return i;
                flat -= count;
            }
            return blocks.Length - 1;
        }

        // Puts entries into a sealed run, or takes them out of one with null; one seal per entry from first.
        public void SetSeals(int first, NoteSeal?[] seals)
        {
            int flat = 0;
            for (int i = 0; i < first; i++) flat += FlatCount(blocks[i]);
            int count = 0;
            for (int i = 0; i < seals.Length; i++)
            {
                blocks[first + i].seal = seals[i];
                blocks[first + i].dirty = true;
                count += FlatCount(blocks[first + i]);
            }
            Raise(NoteChangeKind.Kind, flat, count);
        }

        public void SetPage(PageLayout? page)
        {
            layout.page = page;
            Raise(NoteChangeKind.Page, 0);
        }

        public void SetPalette(string? name)
        {
            palette = name;
            Raise(NoteChangeKind.Palette, 0);
        }

        public void SetLayout(DocumentLayout layout)
        {
            this.layout = layout;
            Raise(NoteChangeKind.Layout, 0);
        }

        // One frontmatter key, removed when value is null.
        public void SetFrontmatterValue(string key, string? value)
        {
            frontmatter = Frontmatter.Set(frontmatter, key, value);
            Raise(NoteChangeKind.Properties, 0);
        }

        public void SetReadOnly(bool value)
        {
            readOnly = value;
            Raise(NoteChangeKind.ReadOnly, 0);
        }

        // Takes on another note's content whole; the source fields stay this note's.
        public void ReplaceAll(RichTextDocument from)
        {
            blocks = from.blocks;
            name = from.name;
            layout = from.layout;
            palette = from.palette;
            created = from.created;
            modified = from.modified;
            readOnly = from.readOnly;
            frontmatter = from.frontmatter;
            Raise(NoteChangeKind.Replaced, 0);
        }
        #endregion
    }

    // One open note: the document the editor is showing and the file it came from.
    public class DocumentEditSession
    {
        public RichTextDocument document { get; }
        public string path { get; private set; }

        // History is per open note, so undo in one tab cannot reach the note in another.
        public UndoStack undo { get; } = new UndoStack();

        // Edited since the last write. Read by the close paths, which must not prompt over a note
        // that was only ever looked at.
        public bool isDirty { get; private set; }

        // open views, and the one that follows an edit's caret
        public int views { get; internal set; }
        internal DocumentControl? lead;

        // autosave, for a session NoteSessions holds
        internal NoteFileSync? sync;

        public DocumentEditSession(RichTextDocument document, string path)
        {
            this.document = document;
            this.path = path;
        }

        public void MarkDirty()
        {
            isDirty = true;
            sync?.Edited();
        }

        public void MarkSaved() => isDirty = false;

        internal void MarkUnsaved() => isDirty = true;

        // Follows the file after it is renamed on disk, so the next save writes where the note is now.
        public void Repath(string newPath)
        {
            NoteSessions.Rekey(this, newPath);
            path = newPath;
            sync?.Watch();
        }

        public void Save()
        {
            bool stamp = SettingsRegistry.Get<DocumentSettings>().stampModified;
            if (isDirty || !stamp) document.modified = stamp ? RichTextDocument.Stamp(DateTime.Now) : null;
            if (sync != null) sync.Write();
            else document.Save(path);
            if (!stamp) document.modified = RichTextDocument.Stamp(File.GetLastWriteTime(path));
            isDirty = false;
        }
    }

    // One session per open path, shared by every view of it.
    public static class NoteSessions
    {
        private static readonly Dictionary<string, DocumentEditSession> open =
            new Dictionary<string, DocumentEditSession>(StringComparer.OrdinalIgnoreCase);

        public static DocumentEditSession Open(string path, PasswordKey? key = null)
        {
            if (!open.TryGetValue(path, out DocumentEditSession? session))
            {
                session = new DocumentEditSession(RichTextDocument.Load(path, key), path);
                session.sync = new NoteFileSync(session);
                open[path] = session;
            }

            session.views++;
            return session;
        }

        // The session holding a path open; null when none does.
        public static DocumentEditSession? Find(string path) => open.TryGetValue(path, out DocumentEditSession? session) ? session : null;

        public static void Close(DocumentEditSession session)
        {
            if (--session.views > 0) return;
            session.sync?.Dispose();
            session.sync = null;
            if (open.TryGetValue(session.path, out DocumentEditSession? held) && held == session) open.Remove(session.path);
            if (session.document.encryption is PasswordKey key && !FileKeys.IsRemembered(key)) key.Dispose();
            foreach (NoteSeal seal in session.document.blocks.Select(b => b.seal).OfType<NoteSeal>().Distinct())
                seal.key.Dispose();
        }

        internal static void Rekey(DocumentEditSession session, string newPath)
        {
            if (!open.TryGetValue(session.path, out DocumentEditSession? held) || held != session) return;

            open.Remove(session.path);
            open[newPath] = session;
        }
    }

    // Which style entry a piece of text takes. A run says Inherit and follows its block; a block that
    // says nothing is body text. Members without an entry in the styles file are legal — a heading
    // past the end of the scheme falls back to the last heading the scheme does define.
    [A_XSDType("TextStyleType", "UI")]
    public enum TextStyleType
    {
        Inherit,
        Text,
        Heading1,
        Heading2,
        Heading3,
        Heading4,
        Heading5,
        Heading6,
        Comment,
        Code,
        Quote,
        Rule
    }

    // Where a block's lines sit across the text width.
    [A_XSDType("TextAlignment", "UI")]
    public enum TextAlignment
    {
        Left,
        Center,
        Right,
        Justify
    }

    // How one styling type is rendered. However many of these a layout carries is the whole scheme,
    // so adding a level is data, not a code change.
    [A_XSDType("TextStyle", "UI")]
    public class TextStyle
    {
        [A_XSDElementProperty("Type", "UI", "Styling type this entry applies to.")]
        public TextStyleType type { get; set; } = TextStyleType.Text;

        [A_XSDElementProperty("FontSize", "UI", "Text size in pixels.")]
        public float fontSize { get; set; } = 18;

        [A_XSDElementProperty("FontName", "UI", "Font asset this type is set in; absent is the default font.")]
        public string fontName { get; set; }

        public TextStyle Clone() => new TextStyle { type = type, fontSize = fontSize, fontName = fontName };
    }

    [A_XSDType("ListLevel", "UI")]
    public class ListLevel
    {
        [A_XSDElementProperty("Marker", "UI", "The marker items at this level show.")]
        public ListMarker marker { get; set; } = ListMarker.Disc;
    }

    // Layout parameters for one document: line height and text sizing now, the home for content
    // width on viewport resize and the paged/pageless mode as those land. A class rather than a
    // struct because it owns a list — copying it by value would hand a working copy the original's
    // styles to mutate.
    [A_XSDType("DocumentLayout", "UI")]
    public class DocumentLayout
    {
        // Editor-wide defaults, owned by the settings registry.
        public static DocumentLayout Defaults => SettingsRegistry.Get<DocumentSettings>().layout;

        // Line box height as a multiple of font size, the way CSS line-height works — so a line is
        // as tall as the styles on it, never as tall as the particular letters that landed there.
        // 1.5 matches Obsidian's --line-height-normal.
        [A_XSDElementProperty("LineHeight", "UI", "Line box height as a multiple of the font size.")]
        public float lineHeight { get; set; } = 1.5f;

        [A_XSDElementProperty("BaselineDepth", "UI", "Every line's depth below its baseline in pixels, so baselines stand a line box apart as in TeX; 0 centres the font in its line box.")]
        public float baselineDepth { get; set; }

        [A_XSDElementProperty("LineSkip", "UI", "TeX's \\lineskip in pixels: the gap kept between fixed-depth lines whose ink would come closer than their baselines allow; 0 lets ink touch.")]
        public float lineSkip { get; set; }

        [A_XSDElementProperty("Tolerance", "UI", "TeX's \\tolerance: the worst line badness optimal breaks accept with hyphenation.")]
        public float tolerance { get; set; } = 200f;

        [A_XSDElementProperty("Pretolerance", "UI", "TeX's \\pretolerance: the worst badness of a first pass that tries no hyphenation; negative skips that pass.")]
        public float pretolerance { get; set; } = -1f;

        [A_XSDElementProperty("EmergencyStretch", "UI", "TeX's \\emergencystretch in pixels: stretch a last pass adds to every line when the others find no breaks; 0 skips that pass.")]
        public float emergencyStretch { get; set; }

        [A_XSDElementProperty("AdvanceGrid", "UI", "Glyph widths truncated to 1/AdvanceGrid em, as LuaTeX's font loader keeps them in thousandths; 0 keeps the font's exact widths.")]
        public int advanceGrid { get; set; }

        [A_XSDElementProperty("MathRules", "UI", "What places math scripts and limits: the font's OpenType constants, or classic TeX's (Tfm) as LaTeX without unicode-math does.")]
        public MathRules mathRules { get; set; }

        // Used when neither the note nor the editor scheme names a size for the type asked for.
        private const int fallbackFontSize = 18;

        // Vertical gap between blocks. Lives here rather than on the view because the layout cache
        // stacks blocks by it too — a number the two disagreed on would put every cached block top
        // further out of step with the drawn one the further down the document you scrolled.
        [A_XSDElementProperty("BlockSpacing", "UI", "Vertical gap between blocks in pixels.")]
        public float blockSpacing { get; set; } = 8f;

        [A_XSDElementProperty("ListIndent", "UI", "Horizontal space per list level in pixels.")]
        public float listIndent { get; set; } = 24f;

        [A_XSDElementProperty("OptimalBreaks", "UI", "Break lines Knuth-Plass style over the whole paragraph instead of greedily.")]
        public bool optimalBreaks { get; set; }

        [A_XSDElementProperty("WidowControl", "UI", "Never leave one line of a paragraph alone at the bottom or top of a page.")]
        public bool widowControl { get; set; }

        // Empty means inherit: a note that declares no styles of its own uses the editor's. Declaring
        // even one replaces the whole set, so a note's heading scheme is read as written rather than
        // merged level-by-level with defaults it cannot see.
        [A_XSDElementProperty("TextStyle", "UI", "Per-type text styles; empty inherits the editor's.")]
        public List<TextStyle> textStyles = new List<TextStyle>();

        [A_XSDElementProperty("Page", "UI", "Page format; absent inherits the editor's.")]
        public PageLayout? page;

        // one entry per level, cycling past the last; empty inherits the editor's
        [A_XSDElementProperty("ListLevel", "UI", "The marker each list level takes by default; empty inherits the editor's.")]
        public List<ListLevel> listLevels = new List<ListLevel>();

        // The marker a list item at a level shows when it names none of its own.
        public ListMarker MarkerFor(int level)
        {
            List<ListLevel> levels = listLevels.Count > 0 ? listLevels : Defaults.listLevels;
            return levels.Count == 0 ? ListMarker.Disc : levels[Math.Max(0, level) % levels.Count].marker;
        }

        private static readonly PageLayout fallbackPage = new PageLayout();

        // The page this layout resolves to.
        public PageLayout Page => page ?? Defaults.page ?? fallbackPage;

        // Resolved here rather than in the view so the measurer and the controls drawn from it cannot
        // disagree about how big a heading is.
        public float FontSizeFor(TextStyleType type)
        {
            if (type == TextStyleType.Inherit) type = TextStyleType.Text;

            List<TextStyle> styles = textStyles.Count > 0 ? textStyles : Defaults.textStyles;

            foreach (TextStyle style in styles)
                if (style.type == type) return style.fontSize;

            // A heading deeper than the scheme defines takes the last heading it does define, rather
            // than collapsing to body text.
            if (IsHeading(type))
                for (int i = styles.Count - 1; i >= 0; i--)
                    if (IsHeading(styles[i].type)) return styles[i].fontSize;

            return fallbackFontSize;
        }

        // The font a type is set in; null is the default font.
        public string FontNameFor(TextStyleType type)
        {
            if (type == TextStyleType.Inherit) type = TextStyleType.Text;

            List<TextStyle> styles = textStyles.Count > 0 ? textStyles : Defaults.textStyles;
            foreach (TextStyle style in styles)
                if (style.type == type) return style.fontName;
            return null;
        }

        private static bool IsHeading(TextStyleType type) =>
            type >= TextStyleType.Heading1 && type <= TextStyleType.Heading6;

        public DocumentLayout Clone()
        {
            DocumentLayout copy = new DocumentLayout
            {
                lineHeight = lineHeight,
                baselineDepth = baselineDepth,
                lineSkip = lineSkip,
                tolerance = tolerance,
                pretolerance = pretolerance,
                emergencyStretch = emergencyStretch,
                advanceGrid = advanceGrid,
                mathRules = mathRules,
                blockSpacing = blockSpacing,
                listIndent = listIndent,
                optimalBreaks = optimalBreaks,
                widowControl = widowControl,
                page = page?.Clone()
            };
            foreach (TextStyle style in textStyles)
                copy.textStyles.Add(style.Clone());
            foreach (ListLevel level in listLevels)
                copy.listLevels.Add(new ListLevel { marker = level.marker });
            return copy;
        }
    }

    [A_XSDType("PageMode", "UI")]
    public enum PageMode
    {
        Paged,
        Pageless
    }

    // Which parameters place math scripts and limits: the font's OpenType MATH constants, or classic TeX's font dimensions.
    [A_XSDType("MathRules", "UI")]
    public enum MathRules
    {
        OpenType,
        Tfm
    }

    [A_XSDType("PageSize", "UI")]
    public enum PageSize
    {
        A0, A1, A2, A3, A4, A5, A6,
        B4, B5,
        Letter, Legal, Tabloid, Executive,
        Custom
    }

    // Page format, in millimetres; drawn at 96 px per inch.
    [A_XSDType("Page", "UI")]
    public class PageLayout
    {
        public const float PxPerMm = 96f / 25.4f;

        [A_XSDElementProperty("Mode", "UI", "Paged breaks the note into pages; Pageless is one page of unbounded height.")]
        public PageMode mode { get; set; } = PageMode.Paged;

        [A_XSDElementProperty("Size", "UI", "Paper size; Custom reads Width and Height.")]
        public PageSize size { get; set; } = PageSize.A4;

        [A_XSDElementProperty("Landscape", "UI", "Swaps the paper's width and height.")]
        public bool landscape { get; set; }

        // custom paper, mm
        [A_XSDElementProperty("Width", "UI", "Paper width in millimetres when Size is Custom.")]
        public float width { get; set; } = 210f;

        [A_XSDElementProperty("Height", "UI", "Paper height in millimetres when Size is Custom.")]
        public float height { get; set; } = 297f;

        // margins, mm
        [A_XSDElementProperty("MarginTop", "UI", "Top margin in millimetres.")]
        public float marginTop { get; set; } = 25.4f;

        [A_XSDElementProperty("MarginBottom", "UI", "Bottom margin in millimetres.")]
        public float marginBottom { get; set; } = 25.4f;

        [A_XSDElementProperty("MarginLeft", "UI", "Left margin in millimetres.")]
        public float marginLeft { get; set; } = 25.4f;

        [A_XSDElementProperty("MarginRight", "UI", "Right margin in millimetres.")]
        public float marginRight { get; set; } = 25.4f;

        [A_XSDElementProperty("Gap", "UI", "Space between pages in pixels.")]
        public float gap { get; set; } = 16f;
        [A_XSDElementProperty("PageNumbers", "UI", "Prints each page's number in its bottom margin.")]
        public bool pageNumbers { get; set; }

        // running heads: the style every page takes unless a block names its own; none declared keeps PageNumbers
        [A_XSDElementProperty("Style", "UI", "The page style every page takes unless a block names its own.")]
        public string? style { get; set; }

        [A_XSDElementProperty("PageStyle", "UI", "Running heads and feet by name; empty leaves PageNumbers in charge.")]
        public List<PageStyle> styles = new List<PageStyle>();

        [A_XSDElementProperty("FootnoteSkip", "UI", "Millimetres between a page's text and its footnotes.")]
        public float footnoteSkip { get; set; } = 3.2f;

        // float separations in millimetres: between floats, between floats and text, around a float set here
        [A_XSDElementProperty("FloatSep", "UI", "Millimetres between two floats at a page's top or foot.")]
        public float floatSep { get; set; } = 4.22f;

        [A_XSDElementProperty("TextFloatSep", "UI", "Millimetres between the text and the floats at a page's top or foot.")]
        public float textFloatSep { get; set; } = 7.03f;

        [A_XSDElementProperty("InTextSep", "UI", "Millimetres above and below a float set in the text.")]
        public float inTextSep { get; set; } = 4.22f;

        [A_XSDElementProperty("InTextSepShrink", "UI", "Millimetres each gap around a float set in the text may shrink by on a full page.")]
        public float inTextSepShrink { get; set; }

        [A_XSDElementProperty("FitHeight", "UI", "Cuts the last page to the height of its text below the top margin plus this many millimetres, as \\pdfpageheight=\\pagetotal+…; 0 keeps the paper's height.")]
        public float fitHeight { get; set; }

        // Paper size in pixels, orientation applied.
        public Vector2 SizePx()
        {
            Vector2 mm = size switch
            {
                PageSize.A0 => new Vector2(841f, 1189f),
                PageSize.A1 => new Vector2(594f, 841f),
                PageSize.A2 => new Vector2(420f, 594f),
                PageSize.A3 => new Vector2(297f, 420f),
                PageSize.A4 => new Vector2(210f, 297f),
                PageSize.A5 => new Vector2(148f, 210f),
                PageSize.A6 => new Vector2(105f, 148f),
                PageSize.B4 => new Vector2(250f, 353f),
                PageSize.B5 => new Vector2(176f, 250f),
                PageSize.Letter => new Vector2(215.9f, 279.4f),
                PageSize.Legal => new Vector2(215.9f, 355.6f),
                PageSize.Tabloid => new Vector2(279.4f, 431.8f),
                PageSize.Executive => new Vector2(184.15f, 266.7f),
                _ => new Vector2(width, height)
            };

            if (landscape) mm = new Vector2(mm.Y, mm.X);
            return mm * PxPerMm;
        }

        public PageLayout Clone() => (PageLayout)MemberwiseClone();

        public PageStyle? StyleNamed(string? name) => styles.Find(s => s.name == name);
    }

    // Where a running head or foot slot sits.
    [A_XSDType("SlotPlace", "UI")]
    public enum SlotPlace
    {
        HeadLeft, HeadCenter, HeadRight,
        FootLeft, FootCenter, FootRight
    }

    // A page's head and foot: text with {page}, {leftmark} and {rightmark} fields, and the rules under and over them.
    [A_XSDType("PageStyle", "UI")]
    public class PageStyle
    {
        [A_XSDElementProperty("Name", "UI", "What PageLayout.Style and a block's PageStyle call it.")]
        public string name { get; set; } = string.Empty;

        // rule thickness, px; 0 draws none
        [A_XSDElementProperty("HeadRule", "UI", "Thickness of the rule under the head, in pixels; 0 draws none.")]
        public float headRule { get; set; }

        [A_XSDElementProperty("FootRule", "UI", "Thickness of the rule over the foot, in pixels; 0 draws none.")]
        public float footRule { get; set; }

        // distance from the text area, mm
        [A_XSDElementProperty("HeadSep", "UI", "Millimetres from the bottom of the head to the top of the text.")]
        public float headSep { get; set; } = 8.8f;

        [A_XSDElementProperty("FootSkip", "UI", "Millimetres from the bottom of the text to the baseline of the foot.")]
        public float footSkip { get; set; } = 10.5f;

        [A_XSDElementProperty("Slot", "UI", "The text in each place; a place with no slot is blank.")]
        public List<RunningSlot> slots = new List<RunningSlot>();
    }

    [A_XSDType("RunningSlot", "UI")]
    public class RunningSlot
    {
        [A_XSDElementProperty("Place", "UI", "Which corner or centre of the head or foot.")]
        public SlotPlace place { get; set; }

        [A_XSDElementProperty("Text", "UI", "The text; {page}, {leftmark} and {rightmark} are filled per page.")]
        public string text { get; set; } = string.Empty;

        [A_XSDElementProperty("FontName", "UI", "Font family; empty takes the default.")]
        public string? fontName { get; set; }

        [A_XSDElementProperty("FontSize", "UI", "Type size in pixels.")]
        public int fontSize { get; set; } = 12;

        [A_XSDElementProperty("Bold", "UI", "Set in bold.")]
        public bool bold { get; set; }

        [A_XSDElementProperty("Italic", "UI", "Set in italic.")]
        public bool italic { get; set; }
    }

    // What a footnote's or a float's blocks share; a block in the flow has none.
    public sealed class PageInsert
    {
        // the id a footnote's anchor run names, or the float's kind (figure, table) and its [htbp!H] placement
        public string? footnote;
        public string? floatKind;
        public string placement = "tbp";
    }

    // Where a block starts: in the flow, on a new page, or on a new page after every waiting float.
    public enum PageBreak
    {
        None,
        Page,
        Clear
    }

    // Editor-wide document defaults as a settings group, so the styles scheme cascades across mounts
    // and saves the same way every other group does.
    [A_XSDType("DocumentSettings", "Settings")]
    public class DocumentSettings : ISettingsGroup
    {
        [A_XSDElementProperty("DocumentLayout", "Settings", "Editor-wide layout and text styles.")]
        public DocumentLayout layout { get; set; } = new DocumentLayout();

        [A_XSDElementProperty("StampModified", "Settings", "Whether a save writes Modified into the note; off shows the file's own time.")]
        public bool stampModified { get; set; } = true;
    }
}
