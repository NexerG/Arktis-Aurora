using ArctisAurora.Core.ECS.EngineEntity;

namespace ArctisAurora.Core.UI.Document
{
    // Each page's text area, repeating every stride down the document.
    public readonly struct PageBands
    {
        public readonly float top;
        public readonly float height;
        public readonly float stride;
        // the first page's header, above its text
        public readonly float header;

        internal const float tolerance = 0.5f;

        public PageBands(float top, float height, float stride, float header = 0f)
        {
            this.top = top;
            this.height = height;
            this.stride = stride;
            this.header = header;
        }

        // Whether y is the top of its page's text, below the header on the first page.
        public bool AtTextTop(float y)
        {
            int page = PageOf(y);
            return y <= top + page * stride + (page == 0 ? header : 0f) + tolerance;
        }

        // Page a y falls on, counting the margins and gap below a text area as its page.
        public int PageOf(float y) => Math.Max(0, (int)MathF.Floor((y - top + tolerance) / stride));

        // A span that would cross its text area's bottom, past the overhang it may hang below, moves to the next page's top.
        public float Push(float y, float spanHeight, float overhang = 0f)
        {
            float bandTop = top + PageOf(y) * stride;
            if (y < bandTop) return bandTop;

            float bandEnd = bandTop + height;
            if (y + spanHeight - overhang <= bandEnd + tolerance) return y;
            if (spanHeight > height && y < bandEnd) return y;
            return bandTop + stride;
        }

        // The top of the next text area, or of this one when y has not left its top.
        public float Break(float y)
        {
            float bandTop = top + PageOf(y) * stride;
            return y <= bandTop + tolerance ? bandTop : bandTop + stride;
        }
    }

    // What one paginate pass has set aside on each page: footnotes at the foot of its text area, floats at its top and foot.
    internal sealed class PageSpace
    {
        // each footnote's height by id, and the gap between a page's text and its first footnote, px
        public readonly Dictionary<string, float> notes = new Dictionary<string, float>();
        public readonly float separator;

        // px reserved at each page's foot, where each page's text ends, the footnotes each page holds, and every footnote placed so far
        private readonly List<float> bottom = new List<float>();
        private readonly List<float> textEnd = new List<float>();
        public readonly List<List<string>?> placed = new List<List<string>?>();
        private readonly HashSet<string> seen = new HashSet<string>();

        // px floats take at each page's top and foot, whether it is a page of floats, whether its waiting floats were placed, and what places them
        private readonly List<float> top = new List<float>();
        private readonly List<float> floatBottom = new List<float>();
        private readonly List<bool> floatPage = new List<bool>();
        private readonly List<bool> opened = new List<bool>();
        public Action<int>? opening;

        // each page's gap shrink ratio once set, and the lowest bottom a span was fitted to on it, px;
        // the shrink a span may count on while slackPage has no ratio yet
        private readonly List<float> ratio = new List<float>();
        private readonly List<float> fitEnd = new List<float>();
        public float slack;
        public int slackPage = -1;

        public PageSpace(float separator) => this.separator = separator;

        public float Ratio(int page) => page < ratio.Count ? ratio[page] : 0f;

        public void Shrink(int page, float r)
        {
            Grow(page);
            ratio[page] = r;
        }

        // How far a page's fitted spans reach past its text area's bottom, px.
        public float Overflow(PageBands bands, int page) =>
            page < fitEnd.Count ? fitEnd[page] - (bands.top + page * bands.stride + bands.height - Bottom(page)) : 0f;

        // A kept run moved off a page: what was fitted there now ends at y.
        public void TakeBack(int page, float y)
        {
            if (page < fitEnd.Count) fitEnd[page] = y;
        }

        public float Bottom(int page) => Notes(page) + (page < floatBottom.Count ? floatBottom[page] : 0f);

        public float Notes(int page) => page < bottom.Count ? bottom[page] : 0f;

        public float Top(int page) => page < top.Count ? top[page] : 0f;

        public float TextEnd(int page) => page < textEnd.Count ? textEnd[page] : 0f;

        public bool IsFloatPage(int page) => page < floatPage.Count && floatPage[page];

        public int Pages => bottom.Count;

        public bool Placed(string id) => seen.Contains(id);

        // A span pushed across page breaks and past pages of floats, each text area short by its page's floats and footnotes and by notes more on the page it lands.
        public float Push(PageBands bands, float y, float spanHeight, float notesHeight = 0f, float overhang = 0f)
        {
            while (true)
            {
                int page = bands.PageOf(y);
                float bandTop = bands.top + page * bands.stride;
                Open(page);
                if (floatPage[page])
                {
                    y = bandTop + bands.stride;
                    continue;
                }

                float textTop = bandTop + top[page];
                y = MathF.Max(y, textTop);
                float need = notesHeight > 0f ? notesHeight + (Notes(page) == 0f ? separator : 0f) : 0f;
                float give = page == slackPage && ratio[page] == 0f ? slack : 0f;
                if (y + spanHeight - overhang > bandTop + bands.height - Bottom(page) - need + PageBands.tolerance + give && y > textTop + PageBands.tolerance)
                {
                    y = bandTop + bands.stride;
                    continue;
                }

                textEnd[page] = MathF.Max(textEnd[page], y + spanHeight);
                fitEnd[page] = MathF.Max(fitEnd[page], y + spanHeight - overhang);
                return y;
            }
        }

        // Places the waiting floats on a page the first time anything lands there.
        public void Open(int page)
        {
            Grow(page);
            if (opened[page]) return;
            opened[page] = true;
            opening?.Invoke(page);
        }

        public void SetFloats(int page, float topArea, float bottomArea)
        {
            Grow(page);
            top[page] = topArea;
            floatBottom[page] = bottomArea;
        }

        public void MarkFloatPage(int page)
        {
            Grow(page);
            floatPage[page] = true;
            opened[page] = true;
        }

        // Takes back the footnotes set from a page on, for a pass that lays those pages out again.
        public void Rollback(int page)
        {
            for (int p = page; p < bottom.Count; p++)
            {
                if (placed[p] is List<string> ids)
                    foreach (string id in ids)
                        seen.Remove(id);
                placed[p] = null;
                bottom[p] = 0f;
                textEnd[p] = 0f;
                ratio[p] = 0f;
                fitEnd[p] = 0f;
            }
        }

        private void Grow(int page)
        {
            while (bottom.Count <= page)
            {
                bottom.Add(0f);
                textEnd.Add(0f);
                placed.Add(null);
                top.Add(0f);
                floatBottom.Add(0f);
                floatPage.Add(false);
                opened.Add(false);
                ratio.Add(0f);
                fitEnd.Add(0f);
            }
        }

        // Pushes a line, then sets its anchors' footnotes aside on the page it landed on.
        public float PushLine(PageBands bands, float y, float lineHeight, List<string>? ids, float overhang = 0f)
        {
            float notesHeight = 0f;
            if (ids != null)
                foreach (string id in ids)
                    if (!seen.Contains(id) && notes.TryGetValue(id, out float height)) notesHeight += height;
            y = Push(bands, y, lineHeight, notesHeight, overhang);
            if (notesHeight > 0f) Reserve(bands.PageOf(y), ids!);
            return y;
        }

        // Puts footnotes on a page; one already placed stays where it is.
        public void Reserve(int page, IEnumerable<string> ids)
        {
            foreach (string id in ids)
            {
                if (!notes.TryGetValue(id, out float height) || !seen.Add(id)) continue;
                Grow(page);
                if (bottom[page] == 0f) bottom[page] = separator;
                bottom[page] += height;
                (placed[page] ??= new List<string>()).Add(id);
            }
        }
    }

    // Where one paginate pass puts each figure and table float: in the text, at a page's top or foot, or on a page of floats,
    // by LaTeX's [htbp!H] rules and its default parameters.
    internal sealed class PageFloats
    {
        // \topnumber, \bottomnumber, \totalnumber, \topfraction, \bottomfraction, \textfraction, \floatpagefraction
        private const int TopNumber = 2;
        private const int BottomNumber = 1;
        private const int TotalNumber = 3;
        private const float TopFraction = 0.7f;
        private const float BottomFraction = 0.3f;
        private const float TextFraction = 0.2f;
        private const float FloatPageFraction = 0.5f;

        public enum Where { None, Waiting, Here, Top, Bottom, FloatPage }

        // one float's blocks and tables, stacked, and where it went
        public sealed class Group
        {
            public readonly PageInsert insert;
            public readonly List<Control> items = new List<Control>();
            public readonly List<float> offsets = new List<float>();
            public readonly List<float> heights = new List<float>();
            public float height;
            public Where where;
            public int page;
            public float y;

            public Group(PageInsert insert) => this.insert = insert;

            public bool Allows(char c) => insert.placement.Contains(c);
        }

        // every float in document order, each by its first block or table, and those still waiting
        public readonly List<Group> groups = new List<Group>();
        public readonly Dictionary<Entity, Group> starts = new Dictionary<Entity, Group>();
        private readonly List<Group> waiting = new List<Group>();

        private readonly PageSpace space;
        private readonly PageBands bands;
        private readonly bool paged;

        // \floatsep, \textfloatsep, \intextsep and \@fpsep, px
        private readonly float floatSep;
        private readonly float textFloatSep;
        private readonly float inTextSep;
        private readonly float pageSep;
        // how far each \intextsep may shrink, px
        public readonly float inTextShrink;

        // the editor's header above the first page's text, px
        private readonly float header;

        public PageFloats(PageSpace space, PageBands bands, bool paged, float floatSep, float textFloatSep, float inTextSep, float inTextShrink, float pageSep, float header)
        {
            this.space = space;
            this.bands = bands;
            this.paged = paged;
            this.floatSep = floatSep;
            this.textFloatSep = textFloatSep;
            this.inTextSep = inTextSep;
            this.inTextShrink = inTextShrink;
            this.pageSep = pageSep;
            this.header = header;
            space.opening = Open;
        }

        private float Head(int page) => page == 0 ? header : 0f;

        public bool Waiting => waiting.Count > 0;

        private float BandTop(int page) => bands.top + page * bands.stride;

        // A float met in the text at y: here, at the current page's top or foot, or waiting; returns the text's y after it.
        // inkEnd: the text's last ink, +\lineskip under [H]; a float set here stands \intextsep below it.
        // rewind is the page whose text must be laid out again under a new top float, else -1.
        public float Arrive(Group g, float y, float inkEnd, out int rewind)
        {
            rewind = -1;
            if (g.where == Where.Here) return Here(g, y, inkEnd);
            if (g.where != Where.None) return y;
            if (!paged || g.Allows('H')) return Here(g, y, inkEnd);

            int p = bands.PageOf(y);
            space.Open(p);
            while (space.IsFloatPage(p))
            {
                y = BandTop(++p);
                space.Open(p);
            }

            if (!waiting.Any(w => w.insert.floatKind == g.insert.floatKind))
            {
                bool force = g.Allows('!');
                float textTop = BandTop(p) + space.Top(p);
                float textY = MathF.Max(y, textTop);
                if (g.Allows('h') && textY + inTextSep + g.height <= BandTop(p) + bands.height - space.Bottom(p) + PageBands.tolerance) return Here(g, y, inkEnd);
                if (g.Allows('t') && TopFits(g, p, force, textY))
                {
                    Put(g, Where.Top, p);
                    rewind = p;
                    return y;
                }
                if (g.Allows('b') && BottomFits(g, p, force, textY))
                {
                    Put(g, Where.Bottom, p);
                    return y;
                }
            }
            g.where = Where.Waiting;
            waiting.Add(g);
            return y;
        }

        private float Here(Group g, float y, float inkEnd)
        {
            g.where = Where.Here;
            float sep = inTextSep - space.Ratio(bands.PageOf(y)) * inTextShrink;
            g.y = space.Push(bands, inkEnd + sep, g.height);
            g.page = bands.PageOf(g.y);
            return g.y + g.height + inTextSep - space.Ratio(g.page) * inTextShrink;
        }

        // A fresh page takes a page of floats if the waiting ones fill enough of it, else what fits at its top and foot, in order.
        private void Open(int p)
        {
            if (!paged || waiting.Count == 0) return;

            List<Group> taken = new List<Group>();
            HashSet<string?> failed = new HashSet<string?>();
            float used = 0f;
            foreach (Group g in waiting)
            {
                float need = (taken.Count > 0 ? pageSep : 0f) + g.height;
                if (failed.Contains(g.insert.floatKind) || !g.Allows('p') || used + need > bands.height + PageBands.tolerance)
                {
                    failed.Add(g.insert.floatKind);
                    continue;
                }
                taken.Add(g);
                used += need;
            }
            if (taken.Count > 0 && used >= FloatPageFraction * bands.height)
            {
                foreach (Group g in taken)
                    Put(g, Where.FloatPage, p);
                space.MarkFloatPage(p);
                return;
            }

            failed.Clear();
            foreach (Group g in waiting.ToList())
            {
                if (failed.Contains(g.insert.floatKind)) continue;
                bool force = g.Allows('!');
                if (g.Allows('t') && TopFits(g, p, force, BandTop(p))) Put(g, Where.Top, p);
                else if (g.Allows('b') && BottomFits(g, p, force, BandTop(p) + space.Top(p))) Put(g, Where.Bottom, p);
                else failed.Add(g.insert.floatKind);
            }
        }

        // Every waiting float onto pages of floats from start on, each page as full as it goes; returns the next text page's top.
        public float Flush(int start)
        {
            int p = start;
            while (waiting.Count > 0)
            {
                float used = 0f;
                for (int n = 0; waiting.Count > 0 && (n == 0 || used + pageSep + waiting[0].height <= bands.height + PageBands.tolerance); n++)
                {
                    used += (n > 0 ? pageSep : 0f) + waiting[0].height;
                    Put(waiting[0], Where.FloatPage, p);
                }
                space.MarkFloatPage(p);
                p++;
            }
            return BandTop(p);
        }

        // Within \topnumber, \totalnumber, \topfraction and \textfraction unless forced, and with the text already on the page still fitting under it.
        private bool TopFits(Group g, int p, bool force, float textY)
        {
            if (!force && (Count(Where.Top, p) >= TopNumber || Count(Where.Top, p) + Count(Where.Bottom, p) >= TotalNumber
                || Heights(Where.Top, p) + g.height > TopFraction * bands.height
                || Heights(Where.Top, p) + Heights(Where.Bottom, p) + g.height > (1f - TextFraction) * bands.height)) return false;
            float text = MathF.Max(0f, textY - BandTop(p) - MathF.Max(Head(p), space.Top(p)));
            return Head(p) + Area(Where.Top, p, g) + text + space.Bottom(p) <= bands.height + PageBands.tolerance;
        }

        // Within \bottomnumber, \totalnumber, \bottomfraction and \textfraction unless forced, and below the text set so far.
        private bool BottomFits(Group g, int p, bool force, float textY)
        {
            if (!force && (Count(Where.Bottom, p) >= BottomNumber || Count(Where.Top, p) + Count(Where.Bottom, p) >= TotalNumber
                || Heights(Where.Bottom, p) + g.height > BottomFraction * bands.height
                || Heights(Where.Top, p) + Heights(Where.Bottom, p) + g.height > (1f - TextFraction) * bands.height)) return false;
            return textY + space.Notes(p) + Area(Where.Bottom, p, g) <= BandTop(p) + bands.height + PageBands.tolerance;
        }

        private void Put(Group g, Where where, int p)
        {
            waiting.Remove(g);
            g.where = where;
            g.page = p;
            if (where == Where.FloatPage) return;
            float top = Area(Where.Top, p);
            space.SetFloats(p, top > 0f ? Head(p) + top : 0f, Area(Where.Bottom, p));
        }

        private int Count(Where where, int p) => groups.Count(g => g.where == where && g.page == p);

        private float Heights(Where where, int p) => groups.Where(g => g.where == where && g.page == p).Sum(g => g.height);

        // The floats' heights, \floatsep between them and \textfloatsep to the text; extra counted as one more.
        private float Area(Where where, int p, Group? extra = null)
        {
            int n = Count(where, p) + (extra != null ? 1 : 0);
            return n == 0 ? 0f : Heights(where, p) + (extra?.height ?? 0f) + (n - 1) * floatSep + textFloatSep;
        }

        // Each float's top: where it was set in the text, stacked down from a page's top, up from its foot, or spread down a page of floats.
        public void Stack(Action<Control, float, float> put)
        {
            foreach (IGrouping<(Where, int), Group> page in groups.Where(g => g.where != Where.Waiting && g.where != Where.None).GroupBy(g => (g.where, g.page)))
            {
                (Where where, int p) = page.Key;
                List<Group> list = page.ToList();
                float at = BandTop(p) + (where == Where.Top ? Head(p) : 0f);
                float sep = floatSep;
                if (where == Where.Bottom) at += bands.height - (Area(Where.Bottom, p) - textFloatSep);
                else if (where == Where.FloatPage)
                {
                    float spread = MathF.Max(0f, bands.height - list.Sum(g => g.height) - (list.Count - 1) * pageSep) / (2 * list.Count);
                    at += spread;
                    sep = pageSep + 2f * spread;
                }

                foreach (Group g in list)
                {
                    float top = where == Where.Here ? g.y : at;
                    for (int i = 0; i < g.items.Count; i++)
                        put(g.items[i], top + g.offsets[i], g.heights[i]);
                    at += g.height + sep;
                }
            }
        }
    }
}
