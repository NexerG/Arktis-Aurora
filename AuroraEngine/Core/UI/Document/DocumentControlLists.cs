using ArctisAurora.Core.Animation;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI.Notes;
using ArctisAurora.Core.UI.Text;
using System.Numerics;
using System.Text.RegularExpressions;

namespace ArctisAurora.Core.UI.Document
{
    public partial class DocumentControl
    {
        #region ---- markdown ----
        // A whole line of "```lang" makes a code block; "---", "***" or "___" makes a rule.
        private bool TypeMarkdownLine()
        {
            BlockControl block = caretBlock;
            if (plainText || block.parent != this || block.listKind != ListKind.None
                || block.stylingType is TextStyleType.Code or TextStyleType.Rule) return false;

            string line = block.text ?? string.Empty;
            Match fence = fenceLine.Match(line);
            bool isRule = ruleLine.IsMatch(line);
            if (!fence.Success && !isRule) return false;

            DocumentAddress start = AddressOf(block, 0);
            undo?.Push(new TextEdit(document, start, line, false));
            document.RemoveText(start, line.Length);

            if (isRule)
            {
                SetBlockList(start.block, b => b.stylingType = TextStyleType.Rule);
                LeaveRule();
                return true;
            }

            string language = fence.Groups[1].Value;
            SetBlockList(start.block, b =>
            {
                b.stylingType = TextStyleType.Code;
                b.language = language.Length > 0 ? language : null;
            });
            return true;
        }

        // Enter on an empty last line of a code block turns that line back into text.
        private bool EndCodeBlock()
        {
            BlockControl block = caretBlock;
            if (plainText || block.stylingType != TextStyleType.Code || block.Length != 0) return false;

            List<BlockControl> blocks = Blocks();
            int index = blocks.IndexOf(block);
            if (index + 1 < blocks.Count && blocks[index + 1].stylingType == TextStyleType.Code
                && blocks[index + 1].parent == block.parent) return false;

            SetBlockList(index, b =>
            {
                b.stylingType = TextStyleType.Text;
                b.language = null;
            });
            return true;
        }

        // Tab in code: a tab at the caret, or one tab more or fewer at the head of every selected line.
        internal bool ShiftCodeIndent(int delta)
        {
            if (caretBlock?.stylingType != TextStyleType.Code) return false;

            DocumentAddress caretAt = AddressOf(caretBlock, caretOffset);
            if (delta > 0 && !HasSelection)
            {
                undo?.Push(new TextEdit(document, caretAt, "\t", true));
                document.InsertText(caretAt, "\t");
                return true;
            }

            DocumentAddress anchorAt = AddressOf(anchor.block, anchor.offset);
            List<BlockControl> blocks = Blocks();
            int step = delta > 0 ? 1 : -1;

            for (int i = Math.Min(anchorAt.block, caretAt.block); i <= Math.Max(anchorAt.block, caretAt.block); i++)
            {
                BlockControl block = blocks[i];
                if (block.stylingType != TextStyleType.Code) continue;

                DocumentAddress head = new DocumentAddress(i, 0);
                if (step > 0) document.InsertText(head, "\t");
                else if (block.text.StartsWith('\t')) document.RemoveText(head, 1);
                else continue;
                undo?.Push(new TextEdit(document, head, "\t", step > 0));

                if (anchorAt.block == i) anchorAt = new DocumentAddress(i, Math.Max(0, anchorAt.offset + step));
                if (caretAt.block == i) caretAt = new DocumentAddress(i, Math.Max(0, caretAt.offset + step));
            }

            Select(anchorAt, caretAt);
            return true;
        }

        // Text bound for a rule lands in a new paragraph after it.
        private void LeaveRule()
        {
            if (caretBlock?.stylingType != TextStyleType.Rule) return;

            OffRule(AddressOf(caretBlock, 0));
        }

        // Starts a paragraph after the rule at an address; returns where it starts.
        private DocumentAddress OffRule(DocumentAddress at)
        {
            at = new DocumentAddress(at.block, 0);
            SplitBlockAt(at);
            SetBlockList(at.block + 1, b =>
            {
                b.stylingType = TextStyleType.Text;
                b.alignment = TextAlignment.Left;
            });
            return new DocumentAddress(at.block + 1, 0);
        }

        // An empty caret block becomes a rule, or one goes in after the caret's block; the caret ends on it.
        public bool InsertRule()
        {
            if (caretBlock == null || caretBlock.stylingType == TextStyleType.Rule) return false;

            BlockControl block = caretBlock;
            int index = Blocks().IndexOf(block);
            if (block.Length > 0)
            {
                SplitBlockAt(AddressOf(block, block.Length));
                index++;
            }

            SetBlockList(index, b =>
            {
                b.stylingType = TextStyleType.Rule;
                b.alignment = TextAlignment.Left;
                b.language = null;
                b.listKind = ListKind.None;
                b.listLevel = 0;
                b.listMarker = null;
                b.listStart = null;
                b.isChecked = false;
            });
            SetCaret(Blocks()[index], 0);
            return true;
        }

        // A closing "**", "*", "~~" or "`" typed after its opener styles what is between and drops both.
        private bool TypeInlineMarkdown(BlockControl block, int index, int typedEnd, char c, out StyleDelta closed)
        {
            closed = default;
            if (plainText || block.stylingType is TextStyleType.Code or TextStyleType.Rule) return false;
            if (c != '*' && c != '~' && c != '`') return false;

            string s = block.text ?? string.Empty;
            string marker = c switch
            {
                '*' => typedEnd >= 2 && s[typedEnd - 2] == '*' ? "**" : "*",
                '~' => "~~",
                _ => "`"
            };

            int open = InlineOpener(s, typedEnd, marker);
            if (open < 0) return false;

            int contentStart = open + marker.Length;
            int contentEnd = typedEnd - marker.Length;

            List<BlockSnapshot> before = SnapshotBlocks(index, index);
            int from = open;
            int to = contentEnd - marker.Length;

            StyleDelta delta = default;
            if (marker == "`") closed = new StyleDelta(code: false);
            else
            {
                delta = marker switch
                {
                    "**" => new StyleDelta(bold: true),
                    "*" => new StyleDelta(italic: true),
                    _ => new StyleDelta(strikethrough: true)
                };
                closed = marker switch
                {
                    "**" => new StyleDelta(bold: false),
                    "*" => new StyleDelta(italic: false),
                    _ => new StyleDelta(strikethrough: false)
                };
            }

            document.ChangeBlocks(index, index, b =>
            {
                b.run.RemoveText(contentEnd, marker.Length);
                b.run.RemoveText(open, marker.Length);
                if (marker == "`") RichTextDocument.MarkCode(b, from, to);
                else if (to > from) b.run.StyleRange(from, to, delta);
            });
            undo?.Push(new BlockStateEdit(document, index, before, SnapshotBlocks(index, index)));
            SetCaret(block, to);
            return true;
        }

        // A closing "]]" typed after "[[target]]" or "[[target|text]]" makes the brackets one link.
        private bool TypeLinkClose(BlockControl block, int index, int typedEnd)
        {
            if (plainText || block.stylingType is TextStyleType.Code or TextStyleType.Rule) return false;

            string s = block.text ?? string.Empty;
            if (typedEnd < 5 || s[typedEnd - 2] != ']') return false;

            int open = s.LastIndexOf("[[", typedEnd - 3, StringComparison.Ordinal);
            if (open < 0) return false;

            string inner = s[(open + 2)..(typedEnd - 2)];
            if (inner.IndexOfAny(linkBreakers) >= 0) return false;

            int bar = inner.IndexOf('|');
            string target = (bar < 0 ? inner : inner[..bar]).Trim();
            string shown = bar < 0 ? target : inner[(bar + 1)..].Trim();
            if (target.Length == 0) return false;

            ReplaceWithLink(block, index, open, typedEnd, shown.Length > 0 ? shown : target, target);
            return true;
        }

        private static readonly char[] linkBreakers = { '[', ']', '\n', BlockControl.PictureChar[0] };

        // Turns a block's characters from..to into one link, as one block-state record.
        internal void ReplaceWithLink(BlockControl block, int index, int from, int to, string shown, string target)
        {
            List<BlockSnapshot> before = SnapshotBlocks(index, index);
            document.ChangeBlocks(index, index, b =>
            {
                b.run.RemoveText(from, to - from);
                b.run.InsertLink(from, shown, target);
            });
            undo?.Push(new BlockStateEdit(document, index, before, SnapshotBlocks(index, index)));
            SetCaret(block, from + shown.Length);
        }

        // Where the opener a closing marker ending at end pairs with starts; -1 when there is none.
        // Content may not start or end with a space, and a single "*" or "`" never touches its twin.
        private static int InlineOpener(string s, int end, string marker)
        {
            int close = end - marker.Length;
            if (close <= 0 || string.CompareOrdinal(s, close, marker, 0, marker.Length) != 0) return -1;
            if (char.IsWhiteSpace(s[close - 1])) return -1;
            if (marker.Length == 1 && s[close - 1] == marker[0]) return -1;

            for (int open = close - marker.Length - 1; open >= 0; open--)
            {
                if (string.CompareOrdinal(s, open, marker, 0, marker.Length) != 0) continue;

                int content = open + marker.Length;
                if (content >= close || char.IsWhiteSpace(s[content])) continue;
                if (marker.Length == 1 && (s[content] == marker[0] || (open > 0 && s[open - 1] == marker[0]))) continue;
                if (marker == "**" && open > 0 && s[open - 1] == '*') continue;
                return open;
            }
            return -1;
        }

        // "```lang" on a line of its own
        private static readonly Regex fenceLine = new Regex(@"^```(\S*)$");

        // three or more of one of -, * or _, spaces between allowed
        private static readonly Regex ruleLine = new Regex(@"^([-*_])(?:[ \t]*\1){2,}[ \t]*$");

        // "# " to "###### "
        private static readonly Regex headingPrefix = new Regex(@"^#{1,6} $");

        // Colours each run of note-level code lines sharing a language; a run is re-read whole when any
        // of its lines changed.
        private void HighlightCode()
        {
            int start = -1;
            bool changed = false;
            for (int i = 0; i <= children.Count; i++)
            {
                BlockControl? block = i < children.Count ? children[i] as BlockControl : null;
                bool code = block?.stylingType == TextStyleType.Code;
                if (start >= 0 && !(code && ((BlockControl)children[start]).language == block!.language))
                {
                    if (changed) Tokenize(start, i);
                    start = -1;
                }
                if (!code)
                {
                    if (block != null) block.syntax = null;
                    continue;
                }

                if (start < 0)
                {
                    start = i;
                    changed = false;
                }
                changed |= block!.isMeasureDirty || block.syntax == null || block.syntax.Length != block.Length;
            }
        }

        private void Tokenize(int from, int to)
        {
            SyntaxState state = SyntaxState.None;
            for (int i = from; i < to; i++)
            {
                BlockControl block = (BlockControl)children[i];
                string line = block.text ?? string.Empty;
                if (block.syntax == null || block.syntax.Length != line.Length) block.syntax = new SyntaxToken[line.Length];
                state = SyntaxTokenizer.Tokenize(line, block.language, state, block.syntax);
            }
        }

        // An unwrapped code line's arranged width: its own when wider than the text column.
        private static float CodeWidth(BlockControl block, float textWidth) =>
            MathF.Max(textWidth, block.arrange.desired.X + block.padding.left + block.padding.right);
        #endregion

        #region ---- lists ----
        // "- " at a block's start makes a bullet; "[ ] " or "[x] " at a bullet's start makes a task;
        // "# " to "###### " makes a heading and "> " a quote.
        private bool TypeMarkdownPrefix(BlockControl block, int index, int typedEnd)
        {
            if (plainText || block.stylingType is TextStyleType.Code or TextStyleType.Rule) return false;

            string head = (block.text ?? string.Empty)[..typedEnd];
            TextStyleType? styling = null;
            if (block.listKind == ListKind.None && headingPrefix.IsMatch(head))
                styling = (TextStyleType)((int)TextStyleType.Heading1 + head.Length - 2);
            else if (block.listKind == ListKind.None && head == "> ") styling = TextStyleType.Quote;

            if (styling is TextStyleType type)
            {
                DocumentAddress at = new DocumentAddress(index, 0);
                undo?.Push(new TextEdit(document, at, head, false));
                document.RemoveText(at, head.Length);

                List<BlockSnapshot> before = SnapshotBlocks(index, index);
                document.SetBlockStylingBetween(index, index, type);
                undo?.Push(new StyleRangeEdit(document, index, before, type));
                return true;
            }

            ListKind kind;
            ListMarker? marker = null;
            if (block.listKind == ListKind.None && head == "- ") kind = ListKind.Bullet;
            else if (block.listKind == ListKind.None && numberPrefix.IsMatch(head))
            {
                kind = ListKind.Bullet;
                marker = ListMarker.Decimal;
            }
            else if (block.listKind == ListKind.Bullet && (head == "[ ] " || head == "[x] ")) kind = ListKind.Task;
            else return false;

            DocumentAddress start = new DocumentAddress(index, 0);
            undo?.Push(new TextEdit(document, start, head, false));
            document.RemoveText(start, head.Length);

            SetBlockList(index, b =>
            {
                b.listKind = kind;
                b.isChecked = head == "[x] ";
                if (kind == ListKind.Bullet) b.listMarker = marker;
            });
            return true;
        }

        // "1. " or "1) ", the prefix that starts a numbered list
        private static readonly System.Text.RegularExpressions.Regex numberPrefix =
            new System.Text.RegularExpressions.Regex(@"^\d{1,9}[.)] $");

        // Gives every item at the caret's level of the caret's list one marker, as one step.
        internal bool SetListMarker(ListMarker marker)
        {
            if (caretBlock == null || caretBlock.listKind != ListKind.Bullet) return false;

            List<BlockControl> blocks = Blocks();
            BlockControl caretItem = caretBlock;
            int level = caretItem.listLevel;
            bool SameList(BlockControl b) =>
                b.parent == caretItem.parent && b.listKind == ListKind.Bullet && b.listLevel >= level;

            int first = blocks.IndexOf(caretItem);
            int last = first;
            while (first > 0 && SameList(blocks[first - 1])) first--;
            while (last + 1 < blocks.Count && SameList(blocks[last + 1])) last++;

            List<BlockSnapshot> before = SnapshotBlocks(first, last);
            document.ChangeBlocks(first, last, b =>
            {
                if (b.listLevel == level) b.listMarker = marker;
            });

            undo?.Push(new BlockStateEdit(document, first, before, SnapshotBlocks(first, last)));
            return true;
        }

        // Starts the caret's list at the number after the last item of the same kind above it, as one step.
        internal bool ContinueNumbering()
        {
            if (caretBlock == null || caretBlock.listKind != ListKind.Bullet) return false;
            if (listsDirty) RenumberLists();

            List<BlockControl> blocks = Blocks();
            BlockControl caretItem = caretBlock;
            int level = caretItem.listLevel;
            bool SameList(BlockControl b) =>
                b.parent == caretItem.parent && b.listKind == ListKind.Bullet && b.listLevel >= level;

            int head = blocks.IndexOf(caretItem);
            while (head > 0 && SameList(blocks[head - 1])) head--;
            while (blocks[head].listLevel != level) head++;
            BlockControl first = blocks[head];
            if (!ListMarkers.IsNumbered(first.shownMarker)) return false;

            for (int b = head - 1; b >= 0; b--)
            {
                BlockControl above = blocks[b];
                if (above.parent != caretItem.parent) break;
                if (above.listKind != ListKind.Bullet || above.listLevel != level || above.shownMarker != first.shownMarker) continue;

                List<BlockSnapshot> before = SnapshotBlocks(head, head);
                int start = above.listNumber + 1;
                document.ChangeBlocks(head, head, b => b.listStart = start);
                undo?.Push(new BlockStateEdit(document, head, before, SnapshotBlocks(head, head)));
                return true;
            }
            return false;
        }

        internal void ListsChanged()
        {
            listsDirty = true;
            InvalidateLayout();
        }

        // Numbers every list item and hands it the marker it shows. A plain block ends every list above
        // it; a task ends the numbering at its own level and deeper; a marker change at a level starts
        // a new list there; a container starts afresh.
        private void RenumberLists()
        {
            listsDirty = false;
            List<(int count, ListMarker marker)> counters = new List<(int, ListMarker)>();
            object container = null;

            foreach (BlockControl block in Blocks())
            {
                if (block.parent != container)
                {
                    counters.Clear();
                    container = block.parent;
                }
                if (block.listKind == ListKind.None)
                {
                    counters.Clear();
                    continue;
                }

                int level = block.listLevel;
                if (counters.Count > level + 1) counters.RemoveRange(level + 1, counters.Count - level - 1);
                if (block.listKind is ListKind.Task or ListKind.SortedTask)
                {
                    if (counters.Count > level) counters.RemoveRange(level, counters.Count - level);
                    continue;
                }

                ListMarker marker = block.listMarker ?? document.layout.MarkerFor(level);
                while (counters.Count <= level) counters.Add((0, marker));
                int number = block.listStart ?? (counters[level].marker == marker ? counters[level].count + 1 : 1);
                counters[level] = (number, marker);
                block.ShowMarker(marker, number);
            }
        }

        // Turns the caret's list item back into a plain block, when the caret sits at its start.
        internal bool ClearListAtCaret()
        {
            if (caretBlock == null || HasSelection || caretOffset != 0 || caretBlock.listKind == ListKind.None)
                return false;

            SetBlockList(Blocks().IndexOf(caretBlock), b =>
            {
                b.listKind = ListKind.None;
                b.listLevel = 0;
                b.listMarker = null;
                b.listStart = null;
                b.isChecked = false;
            });
            return true;
        }

        // Nests or un-nests the list items the range touches, never deeper than one past the item above.
        internal bool ShiftListLevel(int delta)
        {
            if (caretBlock == null) return false;

            int first, last;
            if (OrderedSelection(out DocumentAddress from, out DocumentAddress to))
            {
                first = from.block;
                last = to.block;
            }
            else first = last = Blocks().IndexOf(caretBlock);

            if (first < 0 || last < 0) return false;

            List<BlockControl> blocks = Blocks();
            List<BlockSnapshot> before = SnapshotBlocks(first, last);
            bool changed = false;

            for (int b = first; b <= last && b < blocks.Count; b++)
            {
                BlockControl block = blocks[b];
                if (block.listKind == ListKind.None) continue;

                int deepest = b > 0 && blocks[b - 1].listKind != ListKind.None && blocks[b - 1].parent == block.parent
                    ? blocks[b - 1].listLevel + 1 : 0;
                int level = delta > 0 ? Math.Min(block.listLevel + 1, deepest) : block.listLevel - 1;
                if (level < 0 || (delta > 0 && level <= block.listLevel)) continue;

                document.ChangeBlocks(b, b, n => n.listLevel = level);
                changed = true;
            }

            if (changed) undo?.Push(new BlockStateEdit(document, first, before, SnapshotBlocks(first, last)));
            return changed;
        }

        // One block's list state rewritten as one undoable record.
        internal void SetBlockList(int index, Action<NoteBlock> change)
        {
            List<BlockControl> blocks = Blocks();
            if (index < 0 || index >= blocks.Count) return;

            List<BlockSnapshot> before = SnapshotBlocks(index, index);
            document.ChangeBlocks(index, index, change);
            undo?.Push(new BlockStateEdit(document, index, before, SnapshotBlocks(index, index)));
        }

        // Ticks or unticks a sorted checklist item and moves it, with its sub-items, to where the
        // ticked and unticked items meet.
        internal void SetCheckedSorted(int index, bool value)
        {
            List<BlockControl> blocks = Blocks();
            if (index < 0 || index >= blocks.Count) return;

            bool top = SettingsRegistry.Get<UISettings>().sortedTasks.checkedEnd == SortedTasksSetting.CheckedEnd.Top;
            bool toLate = value != top;
            List<(int start, int count)> groups = SortedGroups(blocks, index);
            int moved = groups.FindIndex(g => g.start == index);
            List<int> order = new List<int>();
            int at = toLate ? 0 : -1;
            for (int g = 0; g < groups.Count; g++)
            {
                if (g == moved) continue;
                bool late = blocks[groups[g].start].isChecked != top;
                if (toLate && !late) at = order.Count + 1;
                if (!toLate && late && at < 0) at = order.Count;
                order.Add(g);
            }
            order.Insert(at < 0 ? order.Count : at, moved);

            int first = groups[0].start;
            ReorderGroups(groups, order, fresh => fresh[index - first].isChecked = value);
        }

        // Re-sorts the sorted checklist a block sits in, nested ones included; true when anything moved.
        internal bool SortChecklistAt(int index)
        {
            List<BlockControl> blocks = Blocks();
            if (index < 0 || index >= blocks.Count || blocks[index].listKind != ListKind.SortedTask) return false;

            List<(int start, int count)> groups = SortedGroups(blocks, index);
            return SortChecklists(groups[0].start, groups[^1].start + groups[^1].count - 1);
        }

        // Puts every sorted checklist starting in the range in order, ticked items at the set end.
        internal bool SortChecklists(int first, int last)
        {
            bool moved = false;
            bool top = SettingsRegistry.Get<UISettings>().sortedTasks.checkedEnd == SortedTasksSetting.CheckedEnd.Top;
            int deepest = 0;
            List<BlockControl> blocks = Blocks();
            for (int b = first; b <= last && b < blocks.Count; b++)
                deepest = Math.Max(deepest, blocks[b].listLevel);

            for (int level = deepest; level >= 0; level--)
                for (int b = first; b <= last && b < blocks.Count; b++)
                {
                    if (blocks[b].listKind != ListKind.SortedTask || blocks[b].listLevel != level) continue;

                    List<(int start, int count)> groups = SortedGroups(blocks, b);
                    if (groups[0].start != b) continue;

                    List<int> order = new List<int>();
                    for (int g = 0; g < groups.Count; g++)
                        if (blocks[groups[g].start].isChecked == top) order.Add(g);
                    for (int g = 0; g < groups.Count; g++)
                        if (blocks[groups[g].start].isChecked != top) order.Add(g);
                    moved |= ReorderGroups(groups, order, null);
                }
            return moved;
        }

        // The sorted checklist a block sits in, as one group per item at its level, sub-items included.
        private static List<(int start, int count)> SortedGroups(List<BlockControl> blocks, int index)
        {
            BlockControl item = blocks[index];
            int level = item.listLevel;
            bool InRun(BlockControl b) => b.parent == item.parent && b.listKind != ListKind.None
                && (b.listLevel > level || b.listLevel == level && b.listKind == ListKind.SortedTask);

            int first = index;
            while (first > 0 && InRun(blocks[first - 1])) first--;
            while (blocks[first].listLevel > level) first++;
            int last = index;
            while (last + 1 < blocks.Count && InRun(blocks[last + 1])) last++;

            List<(int start, int count)> groups = new List<(int start, int count)>();
            for (int b = first; b <= last; b++)
                if (blocks[b].listLevel == level) groups.Add((b, 1));
                else groups[^1] = (groups[^1].start, groups[^1].count + 1);
            return groups;
        }

        // Rewrites a run's groups in a new order as one undoable record; the caret stays on its item.
        private bool ReorderGroups(List<(int start, int count)> groups, List<int> order, Action<List<BlockSnapshot>>? change)
        {
            int first = groups[0].start;
            int last = groups[^1].start + groups[^1].count - 1;
            List<BlockSnapshot> before = SnapshotBlocks(first, last);
            List<BlockSnapshot> fresh = SnapshotBlocks(first, last);
            change?.Invoke(fresh);

            List<BlockSnapshot> after = new List<BlockSnapshot>();
            List<int> source = new List<int>();
            foreach (int g in order)
                for (int b = groups[g].start; b < groups[g].start + groups[g].count; b++)
                {
                    after.Add(fresh[b - first]);
                    source.Add(b);
                }
            if (change == null && source.SequenceEqual(Enumerable.Range(first, source.Count))) return false;

            List<BlockControl> blocks = Blocks();
            int caretAt = caretBlock == null ? -1 : blocks.IndexOf(caretBlock);
            List<float> drawnAt = source.Select(b => blocks[b].arrangedRect.y).ToList();
            document.RestoreBlocks(first, after);
            undo?.Push(new BlockStateEdit(document, first, before, after));

            blocks = Blocks();
            for (int k = 0; k < source.Count; k++)
            {
                if (source[k] == first + k) continue;
                BlockControl block = blocks[first + k];
                block.slideFrom = drawnAt[k];
                block.reveal = 1f;
                if (block.parent == this && !sliding.Contains(block)) sliding.Add(block);
                Animations.Tween(block, nameof(BlockControl.reveal), Vector4.Zero, slideSeconds, Curve.Ease(EaseKind.CubicOut),
                    () => sliding.Remove(block));
            }

            int caretTo = source.IndexOf(caretAt);
            if (caretTo >= 0 && first + caretTo != caretAt) SetCaret(blocks[first + caretTo], caretOffset);
            return true;
        }
        #endregion
    }
}
