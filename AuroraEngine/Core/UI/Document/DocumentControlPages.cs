using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Filing;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Notes;
using ArctisAurora.Core.UI.Text;
using System.Numerics;

namespace ArctisAurora.Core.UI.Document
{
    public partial class DocumentControl
    {
        #region ---- pages ----
        // The shrink the gaps of the blocks before end give a page, px.
        private float PageShrink(PageBands bands, int page, int end)
        {
            float total = 0f;
            for (int j = end - 1; j >= 0 && bands.PageOf(blockTops[j]) >= page; j--)
                if (bands.PageOf(blockTops[j]) == page) total += blockShrinks[j];
            return total;
        }

        // Sets an overflowing page's gap shrink and returns the first block to lay out again; -1 when the page fits as it is.
        private int ShrinkFrom(PageSpace space, PageBands bands, int page, int end)
        {
            float overflow = space.Overflow(bands, page);
            if (space.Ratio(page) > 0f || overflow <= PageBands.tolerance) return -1;
            float total = PageShrink(bands, page, end);
            if (total <= 0f) return -1;

            float pageTop = bands.top + page * bands.stride;
            int first = 0;
            while (first < end && blockTops[first] + blockHeights[first] <= pageTop) first++;
            space.Rollback(page);
            space.Shrink(page, MathF.Min(1f, overflow / total));
            floats.RemoveAll(f => blockControls.IndexOf(f.block) >= first);
            return first;
        }

        // Places every block down the pages and returns the document's height.
        private float Paginate(Vector2 paper, int from, int to)
        {
            float top = Mm(page.marginTop);
            float bottom = Mm(page.marginBottom);
            bool paged = page.mode == PageMode.Paged;
            PageBands bands = new PageBands(top, paged ? paper.Y - top - bottom : float.PositiveInfinity, paper.Y + PageGap * zoom, headerHeight);
            if (bands.top != paginatedBands.top || bands.height != paginatedBands.height || bands.stride != paginatedBands.stride)
            {
                from = 0;
                to = int.MaxValue;
            }
            paginatedBands = bands;
            PageSpace? space = inserts ? new PageSpace(Mm(page.footnoteSkip)) : null;
            insertControls.Clear();
            insertTops.Clear();
            insertHeights.Clear();
            footnoteRules.Clear();
            PageFloats? placer = null;
            if (space != null)
            {
                from = 0;
                to = int.MaxValue;
                MeasureFootnotes(space);
                placer = new PageFloats(space, bands, paged, Mm(page.floatSep), Mm(page.textFloatSep), Mm(page.inTextSep), Mm(page.inTextSepShrink), 8f * PxPerPt * zoom, headerHeight);
                MeasureFloats(placer);
            }

            float y = from == 0 ? top + headerHeight : blockTops[from - 1] + blockHeights[from - 1];
            float textWidth = MathF.Max(0f, paper.X - Mm(page.marginLeft + page.marginRight));
            floats.RemoveAll(f => f.block.parent != this || blockControls.IndexOf(f.block) is int anchor && (anchor < 0 || anchor >= from));
            int index = 0;
            bool settled = false;
            Entity previous = null;
            // the first block of the run kept with the block being placed, the run last moved to a new page, and whether it now starts one
            int keptFrom = -1;
            int keptBreak = -1;
            bool atBreak = false;
            // the block a shrunk page's second layout starts on a new page, at forcedTop
            int forcedBreak = -1;
            float forcedTop = 0f;
            // a float was just set here, and the shrink its two \intextsep gaps give the next block
            bool afterHere = false;
            float floatShrink = 0f;
            for (int c = 0; c < children.Count; c++)
            {
                Entity child = children[c];
                BlockControl block = child as BlockControl;
                TableControl table = block == null ? TableIn(child) : null;
                SealedControl locked = block == null && table == null ? child as SealedControl : null;
                if (block == null && table == null && locked == null) continue;
                if (IsInsert(child))
                {
                    keptFrom = -1;
                    if (placer == null || !placer.starts.TryGetValue(child, out PageFloats.Group? group)) continue;
                    float inkEnd = previous is BlockControl { Lines: { Count: > 0 } ended } endedBlock && ended[^1].pitch > 0f
                        ? y - ended[^1].descent + ended[^1].inkDepth + (group.Allows('H') ? endedBlock.LineSkip : 0f) : y;
                    float textEnd = y;
                    y = placer.Arrive(group, y, inkEnd, out int rewind);
                    if (group.where == PageFloats.Where.Here)
                    {
                        previous = null;
                        afterHere = true;
                        floatShrink += (bands.AtTextTop(group.y) ? 1f : 2f) * placer.inTextShrink;
                        if (paged && group.page > bands.PageOf(textEnd) && ShrinkPage(bands.PageOf(textEnd), index)) continue;
                    }
                    if (rewind < 0) continue;

                    // a new top float: lay the page's text out again from the first block that reaches it
                    float pageTop = bands.top + rewind * bands.stride;
                    int first = 0;
                    while (first < index && blockTops[first] + blockHeights[first] <= pageTop) first++;
                    space!.Rollback(rewind);
                    floats.RemoveAll(f => blockControls.IndexOf(f.block) is int anchor && anchor >= first && anchor < index);
                    c = first == 0 ? -1 : children.IndexOf(blockControls[first - 1]);
                    y = first == 0 ? top + headerHeight : blockTops[first - 1] + blockHeights[first - 1];
                    previous = first == 0 ? null : blockControls[first - 1];
                    index = first;
                    continue;
                }
                bool codeRun = block?.stylingType == TextStyleType.Code && previous is BlockControl { stylingType: TextStyleType.Code };
                BlockControl? above = previous as BlockControl;
                previous = child;
                if (index < from)
                {
                    afterHere = false;
                    floatShrink = 0f;
                    index++;
                    continue;
                }

                float? before = block != null ? block.spaceBefore : table != null ? table.spaceBefore : locked.note.spaceBefore;
                PageBreak pageBreak = block != null ? block.pageBreak : table != null ? table.pageBreak : locked.note.pageBreak;
                if (index == forcedBreak)
                {
                    y = forcedTop;
                    atBreak = true;
                    forcedBreak = -1;
                }
                float lastEnd = y;
                float shrink = 0f;
                if (index > 0 && paged && pageBreak != PageBreak.None)
                {
                    if (pageBreak == PageBreak.Clear && placer is { Waiting: true }) y = placer.Flush(bands.PageOf(bands.Break(y)));
                    y = bands.Break(y);
                }
                else if (index > 0 && !atBreak && (before.HasValue || !codeRun))
                {
                    shrink = (block?.shrinkBefore ?? table?.shrinkBefore ?? 0f) * zoom;
                    y += (before ?? blockSpacing) * zoom - (space?.Ratio(bands.PageOf(y)) ?? 0f) * shrink;
                }
                if (!atBreak && pageBreak == PageBreak.None && block is { strut: false, Lines: { Count: > 0 } below }
                    && above is { stylingType: not TextStyleType.Rule, strut: false, Lines: { Count: > 0 } over })
                    y += TextMeasurer.Interline(over[^1], below[0], block.LineSkip);
                else if (!atBreak && pageBreak == PageBreak.None && table is { Axis: > 0f }
                         && above is { stylingType: not TextStyleType.Rule, strut: false, Lines: { Count: > 0 } ended } && ended[^1].pitch > 0f)
                {
                    TextLine last = ended[^1];
                    float rise = table.AboveAxis();
                    float distance = last.pitch - last.inkDepth - rise >= 0f ? last.pitch : last.inkDepth + rise + above.LineSkip;
                    y += distance - last.descent - rise;
                }
                else if (!atBreak && afterHere && block?.Lines is { Count: > 0 } opening && opening[0].pitch > 0f)
                    y += (opening[0].pitch >= opening[0].ink ? opening[0].pitch : opening[0].ink + block.LineSkip) - opening[0].ascent;
                atBreak = afterHere = false;
                int gapPage = bands.PageOf(y);
                shrink += floatShrink;
                floatShrink = 0f;
                if (space != null)
                {
                    space.slackPage = gapPage;
                    space.slack = PageShrink(bands, gapPage, index) + shrink;
                }

                Control item = block ?? (Control)child;
                float blockTop = y;
                if (table != null) blockTop = space?.Push(bands, y, table.FirstRowHeight) ?? bands.Push(y, table.FirstRowHeight);
                else if (locked != null) blockTop = space?.Push(bands, y, locked.DesiredSize.Y) ?? bands.Push(y, locked.DesiredSize.Y);
                else if (block.Lines is { Count: > 0 } lines) blockTop = space?.Push(bands, y, lines[0].height, 0f, block.Overhang) ?? bands.Push(y, lines[0].height, block.Overhang);

                if (index > to && blockTops[index] == blockTop && !FloatsBelow(blockTop) && block is not { laidAround: true })
                {
                    settled = true;
                    break;
                }

                float height;
                if (table != null)
                {
                    height = table.Paginate(blockTop, bands, space);
                    if (space != null)
                    {
                        List<BlockControl> cells = new List<BlockControl>();
                        table.AppendBlocks(cells);
                        space.Reserve(bands.PageOf(blockTop), cells.SelectMany(c => c.Anchors()).Select(a => a.id));
                    }
                }
                else if (locked != null) height = locked.DesiredSize.Y;
                else
                {
                    RegisterFloats(block, blockTop);
                    if (WrapsAround(block, blockTop))
                        height = block.LayoutAround(new FloatSlots(this, block, blockTop, bands, textWidth));
                    else
                    {
                        if (block.laidAround) block.LayoutAround(null);
                        height = block.Paginate(blockTop, bands, space);
                    }
                }
                float gave = bands.PageOf(blockTop) == gapPage ? shrink : 0f;
                if (index < blockControls.Count)
                {
                    blockTops[index] = blockTop;
                    blockHeights[index] = height;
                    blockShrinks[index] = gave;
                    blockControls[index] = item;
                }
                else
                {
                    blockTops.Add(blockTop);
                    blockHeights.Add(height);
                    blockShrinks.Add(gave);
                    blockControls.Add(item);
                }
                if (space != null) space.slack = 0f;

                if (paged && keptFrom >= 0 && keptFrom != keptBreak)
                {
                    // the kept run ends a page this block's first line left: the run starts the next page instead
                    float firstLine = block?.Lines is { Count: > 0 } placed ? blockTop + placed[0].top : blockTop;
                    int landed = bands.PageOf(firstLine);
                    if (landed > bands.PageOf(blockTops[index - 1] + blockHeights[index - 1] - PageBands.tolerance))
                    {
                        int first = keptFrom;
                        if (space != null && first > 0)
                        {
                            // the page the run leaves ends at the block before it; overflowing, it shrinks and the run starts the next page
                            float end = blockTops[first - 1] + blockHeights[first - 1];
                            int left = bands.PageOf(end - PageBands.tolerance);
                            if (left < landed)
                            {
                                space.TakeBack(left, end - (blockControls[first - 1] is BlockControl last ? last.Overhang : 0f));
                                int shrunk = ShrinkFrom(space, bands, left, first);
                                if (shrunk >= 0)
                                {
                                    forcedBreak = first;
                                    forcedTop = bands.top + landed * bands.stride;
                                    keptBreak = first;
                                    c = shrunk == 0 ? -1 : children.IndexOf(blockControls[shrunk - 1]);
                                    y = shrunk == 0 ? top + headerHeight : blockTops[shrunk - 1] + blockHeights[shrunk - 1];
                                    previous = shrunk == 0 ? null : blockControls[shrunk - 1];
                                    from = Math.Min(from, shrunk);
                                    keptFrom = -1;
                                    index = shrunk;
                                    continue;
                                }
                            }
                        }
                        for (int f = floats.Count - 1; f >= 0; f--)
                        {
                            int anchor = blockControls.IndexOf(floats[f].block);
                            if (anchor >= first && anchor <= index) floats.RemoveAt(f);
                        }
                        c = children.IndexOf(blockControls[first]) - 1;
                        y = bands.top + landed * bands.stride;
                        previous = first == 0 ? null : blockControls[first - 1];
                        from = Math.Min(from, first);
                        keptBreak = first;
                        keptFrom = -1;
                        atBreak = true;
                        index = first;
                        continue;
                    }
                }

                // the page the content left reaches past its bottom on its gaps' shrink: it shrinks them and is laid out again
                int closed = bands.PageOf(lastEnd);
                if (space != null && paged && bands.PageOf(blockTop + height - PageBands.tolerance) > closed && ShrinkPage(closed, index + 1)) continue;
                keptFrom = block is { keepWithNext: true } ? (keptFrom >= 0 ? keptFrom : index) : -1;
                y = blockTop + height;
                index++;

                // Shrinks an overflowing page's gaps and rewinds to lay it out again; false when the page fits.
                bool ShrinkPage(int page, int end)
                {
                    int again = ShrinkFrom(space!, bands, page, end);
                    if (again < 0) return false;
                    if (keptBreak >= again && keptBreak < end)
                    {
                        forcedBreak = keptBreak;
                        forcedTop = bands.top + bands.PageOf(blockTops[keptBreak]) * bands.stride;
                    }
                    c = again == 0 ? -1 : children.IndexOf(blockControls[again - 1]);
                    y = again == 0 ? top + headerHeight : blockTops[again - 1] + blockHeights[again - 1];
                    previous = again == 0 ? null : blockControls[again - 1];
                    keptFrom = -1;
                    afterHere = false;
                    floatShrink = 0f;
                    index = again;
                    return true;
                }
            }

            if (settled)
            {
                y = blockTops[^1] + blockHeights[^1];
                for (int i = index; i < blockControls.Count; i++)
                    if (blockControls[i] is BlockControl unchanged) RegisterFloats(unchanged, blockTops[i]);
            }
            else if (index < blockControls.Count)
            {
                blockTops.RemoveRange(index, blockTops.Count - index);
                blockHeights.RemoveRange(index, blockHeights.Count - index);
                blockShrinks.RemoveRange(index, blockShrinks.Count - index);
                blockControls.RemoveRange(index, blockControls.Count - index);
            }
            foreach (FloatPicture f in floats)
                y = MathF.Max(y, f.bounds.Bottom);
            if (space != null)
            {
                ReserveOrphanNotes(bands, space, y, paged);
                if (placer!.Waiting) y = placer.Flush(Math.Max(bands.PageOf(y - PageBands.tolerance) + 1, space.Pages));
                y = PlaceFootnotes(bands, space, y, paged);
                placer.Stack((control, at, height) =>
                {
                    insertControls.Add(control);
                    insertTops.Add(at);
                    insertHeights.Add(height);
                });
            }

            if (!paged)
            {
                pageCount = 1;
                pageHeight = lastPageHeight = MathF.Max(paper.Y, y + bottom);
                return pageHeight;
            }

            pageCount = bands.PageOf(y - PageBands.tolerance) + 1;
            pageHeight = paper.Y;
            lastPageHeight = page.fitHeight > 0f
                ? Math.Clamp(y - (pageCount - 1) * bands.stride - top + Mm(page.fitHeight), top, paper.Y)
                : paper.Y;
            return (pageCount - 1) * (paper.Y + PageGap * zoom) + lastPageHeight;
        }

        // Each footnote's blocks, the first group to name an id, and their heights stacked.
        private void MeasureFootnotes(PageSpace space)
        {
            footnoteGroups.Clear();
            PageInsert? current = null;
            foreach (Entity child in children)
            {
                if (child is not BlockControl { insert.footnote: string id } block) continue;
                if (block.insert != current && footnoteGroups.ContainsKey(id)) continue;
                current = block.insert;
                if (!footnoteGroups.TryGetValue(id, out List<BlockControl>? blocks)) footnoteGroups[id] = blocks = new List<BlockControl>();
                float height = block.Paginate(0f, unbounded) + (blocks.Count > 0 ? (block.spaceBefore ?? 0f) * zoom : 0f);
                blocks.Add(block);
                space.notes[id] = space.notes.GetValueOrDefault(id) + height;
            }
        }

        // Each float's blocks and tables, stacked, one group per PageInsert.
        private void MeasureFloats(PageFloats placer)
        {
            PageFloats.Group? current = null;
            foreach (Entity child in children)
            {
                BlockControl? block = child as BlockControl;
                TableControl? table = block == null ? TableIn(child) : null;
                PageInsert? insert = block?.insert ?? table?.insert;
                if (insert?.floatKind == null) continue;
                if (current?.insert != insert)
                {
                    placer.groups.Add(current = new PageFloats.Group(insert));
                    placer.starts[child] = current;
                }

                float gap = current.items.Count > 0 ? ((block != null ? block.spaceBefore : table!.spaceBefore) ?? 0f) * zoom : 0f;
                if (block is { strut: true } && current.items.Count > 0 && current.items[^1] is BlockControl { Lines: { Count: > 0 } over } && over[^1].pitch > 0f)
                    gap -= over[^1].descent - over[^1].inkDepth;
                float height = block != null ? block.Paginate(0f, unbounded) : table!.Paginate(0f, unbounded);
                current.offsets.Add(current.height + gap);
                current.items.Add((Control)child);
                current.heights.Add(height);
                current.height += gap + height;
            }
        }

        // Footnotes no anchor placed go on the last page, or one more if they do not fit.
        private void ReserveOrphanNotes(PageBands bands, PageSpace space, float y, bool paged)
        {
            List<string> orphans = footnoteGroups.Keys.Where(id => !space.Placed(id)).ToList();
            if (orphans.Count == 0) return;
            int last = bands.PageOf(y - PageBands.tolerance);
            float need = orphans.Sum(id => space.notes[id]) + (space.Notes(last) == 0f ? space.separator : 0f);
            float room = paged ? bands.top + last * bands.stride + bands.height - space.Bottom(last) - y : float.PositiveInfinity;
            space.Reserve(paged && need > room ? last + 1 : last, orphans);
        }

        // Stacks each page's footnotes at the foot of its text area under a short rule, above the page's bottom floats.
        // Pageless puts them all under the text. Returns the document's new end.
        private float PlaceFootnotes(PageBands bands, PageSpace space, float y, bool paged)
        {
            float end = y;
            float at = y + space.separator;
            for (int p = 0; p < space.placed.Count; p++)
            {
                if (space.placed[p] is not List<string> ids) continue;
                if (paged) at = MathF.Max(bands.top + p * bands.stride + bands.height - space.Bottom(p), space.TextEnd(p)) + space.separator;
                if (paged || footnoteRules.Count == 0) footnoteRules.Add((paged ? p : 0, at - 2.6f * PxPerPt * zoom));
                foreach (string id in ids)
                    for (int i = 0; i < footnoteGroups[id].Count; i++)
                    {
                        BlockControl block = footnoteGroups[id][i];
                        if (i > 0) at += (block.spaceBefore ?? 0f) * zoom;
                        float height = block.Paginate(at, unbounded);
                        insertControls.Add(block);
                        insertTops.Add(at);
                        insertHeights.Add(height);
                        at += height;
                    }
                end = MathF.Max(end, at);
            }
            return end;
        }

        // The 1-based page a block's character lands on, a table's top for a table; null until the block is placed.
        internal int? PageAt(Control item, int offset)
        {
            int index = blockControls.FindIndex(c => c == item || TableIn(c) == item);
            float y;
            if (index >= 0) y = blockTops[index];
            else if (insertControls.FindIndex(c => c == item || TableIn(c) == item) is int insert and >= 0) y = insertTops[insert];
            else return null;
            if (item is BlockControl block) y += block.CaretAt(Math.Clamp(offset, 0, block.Length)).top;
            return paginatedBands.PageOf(y + PageBands.tolerance) + 1;
        }

        private static bool IsInsert(Entity child) => (child as BlockControl)?.insert != null || TableIn(child)?.insert != null;

        private static bool Remeasured(Control control) => ((ArrangeFlags)control.arrange.flags & ArrangeFlags.Remeasured) != 0;

        // Millimetres on the page to design pixels at the current zoom.
        private float Mm(float mm) => mm * PageLayout.PxPerMm * zoom;

        // Page panels live at the head of the child list, behind the highlights and the text.
        private void EnsurePages()
        {
            while (pages.Count < pageCount)
            {
                PanelControl sheet = new PanelControl
                {
                    hitTestable = false,
                    edgeRole = PaletteRole.Line,
                    edgeThickness = new Thickness(1f)
                };
                if (fitWidth)
                {
                    sheet.edgeThickness = new Thickness(0f);
                    sheet.alpha = 0f;
                }
                else sheet.PaintOr(null, PaletteRole.Surface);
                sheet.AddChild(new PageMargins());
                sheet.parent = this;
                children.Insert(pages.Count, sheet);
                pages.Add(sheet);
                MarkTreeOrderDirty();
            }
        }

        // Each page sheet's arranged rect, first page first.
        internal IEnumerable<LayoutRect> PageRects() => pages.Take(pageCount).Select(sheet => sheet.arrangedRect);

        // Writes each sheet's running head and foot, or its number in the bottom margin, or clears them.
        private void NumberPages()
        {
            bool paged = page.mode == PageMode.Paged;
            bool running = paged && page.styles.Count > 0;
            (string left, string right, string? style)[] heads = running ? RunningHeads() : Array.Empty<(string, string, string?)>();
            float textLeft = Mm(page.marginLeft);
            float textRight = measuredPaper.X - Mm(page.marginRight);
            float textTop = Mm(page.marginTop);
            float textBottom = measuredPaper.Y - Mm(page.marginBottom);

            for (int i = 0; i < pages.Count; i++)
            {
                PageMargins margins = (PageMargins)pages[i].children[0];
                margins.Begin(measuredPaper);
                if (!running && paged && page.pageNumbers)
                {
                    LabelControl number = margins.Slot(SlotPlace.FootCenter, (i + 1).ToString(), null, pageNumberSize * zoom, FontStyle.Regular, PaletteRole.MutedInk);
                    float bottom = MathF.Max(0f, (Mm(page.marginBottom) - number.fontSize * number.lineHeight) * 0.5f);
                    margins.Place(SlotPlace.FootCenter, 0f, measuredPaper.X, measuredPaper.Y - bottom);
                }
                else if (running && i < heads.Length && page.StyleNamed(heads[i].style ?? page.style) is PageStyle style)
                {
                    float headBottom = textTop - Mm(style.headSep);
                    float footBaseline = textBottom + Mm(style.footSkip);
                    foreach (RunningSlot slot in style.slots)
                    {
                        string text = slot.text.Replace("{page}", (i + 1).ToString()).Replace("{leftmark}", heads[i].left).Replace("{rightmark}", heads[i].right);
                        FontStyle face = slot.bold ? slot.italic ? FontStyle.BoldItalic : FontStyle.Bold : slot.italic ? FontStyle.Italic : FontStyle.Regular;
                        margins.Slot(slot.place, text, slot.fontName, slot.fontSize * zoom, face, PaletteRole.Ink);
                        bool head = slot.place <= SlotPlace.HeadRight;
                        margins.Place(slot.place, textLeft, textRight, head ? headBottom : footBaseline, !head);
                    }
                    if (style.headRule > 0f)
                        margins.Rule(0, new LayoutRect(textLeft, headBottom + 2f * zoom, textRight - textLeft, MathF.Max(1f, style.headRule * zoom)));
                    if (style.footRule > 0f)
                        margins.Rule(1, new LayoutRect(textLeft, (textBottom + footBaseline) * 0.5f, textRight - textLeft, MathF.Max(1f, style.footRule * zoom)));
                }
                foreach ((int p, float y) in footnoteRules)
                    if (p == i)
                        margins.Rule(2, new LayoutRect(textLeft, y - i * paginatedBands.stride, 0.4f * (textRight - textLeft), MathF.Max(1f, 0.4f * PxPerPt * zoom)));
                margins.End();
            }
        }

        // Each page's \leftmark (its last mark's left half), \rightmark (its first mark's right half) and \thispagestyle.
        private (string left, string right, string? style)[] RunningHeads()
        {
            (string left, string right, string? style)[] heads = new (string, string, string?)[pageCount];
            string left = string.Empty;
            string right = string.Empty;
            int b = 0;
            for (int p = 0; p < pageCount; p++)
            {
                string? firstRight = null;
                string? style = null;
                for (; b < blockControls.Count && paginatedBands.PageOf(blockTops[b]) <= p; b++)
                {
                    if (blockControls[b] is not BlockControl block) continue;
                    style ??= block.pageStyle;
                    if (block.markLeft == null && block.markRight == null) continue;
                    left = block.markLeft ?? left;
                    right = block.markRight ?? right;
                    firstRight ??= right;
                }
                heads[p] = (left, firstRight ?? right, style);
            }
            return heads;
        }

        // A sheet's head and foot: six slot labels and the head, foot and footnote rules, each placed by the document.
        private sealed class PageMargins : ContainerControl
        {
            private readonly LabelControl[] slots = new LabelControl[6];
            private readonly PanelControl[] rules = new PanelControl[3];

            // each slot's span across the sheet, the bottom (or baseline) it sits on and where in the span it aligns; each rule's rect; what this pass wrote
            private readonly (float left, float right, float bottom, float align, bool baseline)[] places = new (float, float, float, float, bool)[6];
            private readonly LayoutRect[] ruleRects = new LayoutRect[3];
            private readonly bool[] written = new bool[9];
            private Vector2 paper;

            public PageMargins()
            {
                hitTestable = false;
                horizontalAlignment = HorizontalAlignment.Left;
                verticalAlignment = VerticalAlignment.Top;
                horizontalPosition = 0f;
                verticalPosition = 0f;
                for (int i = 0; i < slots.Length; i++)
                {
                    slots[i] = new LabelControl { hitTestable = false };
                    AddChild(slots[i]);
                }
                for (int i = 0; i < rules.Length; i++)
                {
                    rules[i] = new PanelControl { hitTestable = false };
                    rules[i].PaintOr(null, PaletteRole.Ink);
                    AddChild(rules[i]);
                }
            }

            public void Begin(Vector2 paper)
            {
                if (this.paper != paper) InvalidateLayout();
                this.paper = paper;
                Array.Clear(written);
            }

            // Blanks whatever this pass did not write.
            public void End()
            {
                for (int i = 0; i < slots.Length; i++)
                    if (!written[i]) slots[i].text = string.Empty;
                for (int i = 0; i < rules.Length; i++)
                    if (!written[6 + i]) Rule(i, default);
            }

            public LabelControl Slot(SlotPlace place, string text, string? fontName, float size, FontStyle face, PaletteRole role)
            {
                written[(int)place] = true;
                LabelControl label = slots[(int)place];
                label.text = text;
                label.fontName = fontName ?? "default";
                label.fontSize = Math.Max(1, (int)MathF.Round(size));
                label.role = role;
                if (label.style != face)
                {
                    label.style = face;
                    label.InvalidateLayout();
                }
                return label;
            }

            public void Place(SlotPlace place, float left, float right, float bottom, bool baseline = false)
            {
                (float, float, float, float, bool) at = (left, right, bottom, (int)place % 3 * 0.5f, baseline);
                if (places[(int)place] == at) return;
                places[(int)place] = at;
                InvalidateArrange();
            }

            public void Rule(int index, LayoutRect rect)
            {
                written[6 + index] = rect.width > 0f;
                ref LayoutRect at = ref ruleRects[index];
                if (at.x == rect.x && at.y == rect.y && at.width == rect.width && at.height == rect.height) return;
                at = rect;
                InvalidateArrange();
            }

            protected override Vector2 MeasureCore(Vector2 availableSize)
            {
                foreach (Entity child in children)
                    ((Control)child).Measure(paper);
                arrange.desired = paper;
                return paper;
            }

            protected override void ArrangeCore(LayoutRect finalRect)
            {
                WriteArranged(finalRect);
                for (int i = 0; i < slots.Length; i++)
                {
                    (float left, float right, float bottom, float align, bool baseline) = places[i];
                    Vector2 size = slots[i].arrange.desired;
                    float above = baseline && slots[i].Lines is { Count: > 0 } lines ? lines[0].top + lines[0].ascent : size.Y;
                    slots[i].Arrange(new LayoutRect(finalRect.x + left + (right - left - size.X) * align, finalRect.y + bottom - above, size.X, size.Y));
                }
                for (int i = 0; i < rules.Length; i++)
                {
                    LayoutRect rect = ruleRects[i];
                    rules[i].Arrange(new LayoutRect(finalRect.x + rect.x, finalRect.y + rect.y, rect.width, rect.height));
                }
            }
        }

        private void ArrangePages(float x, float y, float width)
        {
            for (int i = 0; i < pages.Count; i++)
                pages[i].Arrange(i < pageCount
                    ? new LayoutRect(x, y + i * (pageHeight + PageGap * zoom), width, i == pageCount - 1 ? lastPageHeight : pageHeight)
                    : new LayoutRect(x, y, 0f, 0f));
        }
        #endregion
    }
}
