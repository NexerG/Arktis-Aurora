using ArctisAurora.Core.Animation;
using ArctisAurora.Core.Diagnostics;
using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Editing;
using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Registry.Assets;
using ArctisAurora.EngineWork;
using Silk.NET.GLFW;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ArctisAurora.Core.UI
{
    // Where a caret sits: which block, and how many characters into it. A block is one control, so
    // there is no run level to address and no boundary slot to normalize away.
    public readonly struct CaretSlot
    {
        public readonly BlockControl block;
        public readonly int offset;

        public CaretSlot(BlockControl block, int offset)
        {
            this.block = block;
            this.offset = offset;
        }

        public bool Equals(CaretSlot other) => block == other.block && offset == other.offset;
    }

    // A style change as data, so an edit record can carry one and redo can replay it. A member left
    // null is one the change does not speak to — which is what lets bold over a multicoloured
    // selection keep every colour in it.
    public readonly struct StyleDelta
    {
        public readonly bool? bold;
        public readonly bool? italic;
        public readonly bool? strikethrough;
        public readonly bool? underline;
        public readonly string colorHex;
        public readonly string highlightHex;
        public readonly float? fontSize;
        public readonly bool? code;
        public readonly string fontName;
        public readonly string variant;

        public StyleDelta(bool? bold = null, bool? italic = null, bool? strikethrough = null,
            string colorHex = null, float? fontSize = null, bool? underline = null, string highlightHex = null,
            bool? code = null, string fontName = null, string variant = null)
        {
            this.bold = bold;
            this.italic = italic;
            this.strikethrough = strikethrough;
            this.underline = underline;
            this.colorHex = colorHex;
            this.highlightHex = highlightHex;
            this.fontSize = fontSize;
            this.code = code;
            this.fontName = fontName;
            this.variant = variant;
        }

        // Everything a span carries, as a delta that would give another span the same look.
        public static StyleDelta Of(StyleSpan span) => new StyleDelta(
            span.IsBold, span.IsItalic, span.strikethrough, span.colorHex ?? string.Empty,
            span.fontSizeAuthored ? span.fontSize : null, span.underline, span.highlightHex ?? string.Empty,
            fontName: span.fontName ?? string.Empty, variant: span.variant ?? string.Empty);

        public void Apply(ref StyleSpan span)
        {
            bool isBold = bold ?? span.IsBold;
            bool isItalic = italic ?? span.IsItalic;
            span.style = isBold ? (isItalic ? FontStyle.BoldItalic : FontStyle.Bold)
                                : isItalic ? FontStyle.Italic : FontStyle.Regular;

            if (strikethrough.HasValue) span.strikethrough = strikethrough.Value;
            if (underline.HasValue) span.underline = underline.Value;
            if (colorHex != null) span.colorHex = colorHex.Length == 0 ? null : colorHex;
            if (highlightHex != null) span.highlightHex = highlightHex.Length == 0 ? null : highlightHex;
            if (fontName != null) span.fontName = fontName.Length == 0 ? null : fontName;
            if (variant != null) span.variant = variant.Length == 0 ? null : variant;
            if (fontSize.HasValue)
            {
                span.fontSizeAuthored = true;
                span.fontSize = fontSize.Value;
            }
            if (code.HasValue && code.Value != (span.stylingType == TextStyleType.Code))
            {
                span.stylingType = code.Value ? TextStyleType.Code : TextStyleType.Inherit;
                span.fontSizeAuthored = false;
                if (!code.Value) span.fontSize = 0;
            }
        }

        // This delta with another laid over it; the newer one wins wherever it speaks.
        public StyleDelta With(StyleDelta over) => new StyleDelta(
            over.bold ?? bold, over.italic ?? italic, over.strikethrough ?? strikethrough,
            over.colorHex ?? colorHex, over.fontSize ?? fontSize, over.underline ?? underline,
            over.highlightHex ?? highlightHex, over.code ?? code, over.fontName ?? fontName, over.variant ?? variant);

        // Whether applying this would move anything on the span.
        public bool Changes(StyleSpan span) =>
            (bold.HasValue && bold != span.IsBold)
            || (italic.HasValue && italic != span.IsItalic)
            || (strikethrough.HasValue && strikethrough != span.strikethrough)
            || (underline.HasValue && underline != span.underline)
            || (colorHex != null && (colorHex.Length == 0 ? null : colorHex) != span.colorHex)
            || (highlightHex != null && (highlightHex.Length == 0 ? null : highlightHex) != span.highlightHex)
            || (fontSize.HasValue && fontSize != span.fontSize)
            || (code.HasValue && code != (span.stylingType == TextStyleType.Code))
            || (fontName != null && (fontName.Length == 0 ? null : fontName) != span.fontName)
            || (variant != null && (variant.Length == 0 ? null : variant) != span.variant);
    }

    // The style the next character will take: the span the caret sits in, with an armed change laid
    // over it. Resolved against the block, so an unset span member reads as what it draws as.
    public readonly struct CaretStyle
    {
        public readonly bool bold;
        public readonly bool italic;
        public readonly bool strikethrough;
        public readonly bool underline;
        public readonly bool code;
        public readonly string colorHex;
        public readonly string highlightHex;
        public readonly float fontSize;
        public readonly string fontName;
        public readonly string variant;

        public CaretStyle(BlockControl block, StyleSpan span, StyleDelta armed)
        {
            if (armed.fontName != null) span.fontName = armed.fontName.Length == 0 ? null : armed.fontName;
            fontName = block.FontAt(span);
            variant = armed.variant == null ? span.variant : armed.variant.Length == 0 ? null : armed.variant;
            bold = armed.bold ?? span.IsBold;
            italic = armed.italic ?? span.IsItalic;
            strikethrough = armed.strikethrough ?? span.strikethrough;
            underline = armed.underline ?? span.underline;
            code = armed.code ?? span.stylingType == TextStyleType.Code;
            colorHex = armed.colorHex == null ? span.colorHex : armed.colorHex.Length == 0 ? null : armed.colorHex;
            highlightHex = armed.highlightHex == null ? span.highlightHex : armed.highlightHex.Length == 0 ? null : armed.highlightHex;
            fontSize = armed.fontSize ?? (span.fontSize > 0 ? span.fontSize : block.fontSize);
        }
    }

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
        // inkEnd is the text's last ink plus \lineskip, which a float set here stands \intextsep below.
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
            return g.y + g.height + sep;
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

    // The note's content area: blocks stacked top to bottom, with the caret over them.
    public class DocumentControl : ContainerControl, IGlyphPressTarget
    {
        public float blockSpacing;

        // page format, assigned by the editor before the first measure
        public PageLayout page = new PageLayout();

        // paper as wide as the viewport, no sheet and no inset; assigned by the editor before the first measure
        public bool fitWidth;

        // refuses picture drags, assigned by the editor
        public bool readOnly;

        // app-wide space between pages, and the page number's type size
        private static float PageGap => DocumentLayout.Defaults.Page.gap;
        private float Inset => fitWidth ? 0f : PageGap * zoom;
        private const float pageNumberSize = 12f;

        // document zoom, assigned by the editor; 1 is 100%
        public float zoom = 1f;

        // page panels at the head of children, and where the last paginate put each block
        private readonly List<PanelControl> pages = new List<PanelControl>();
        private readonly List<float> blockTops = new List<float>();
        private readonly List<float> blockHeights = new List<float>();
        // the shrink each block's gap gave the page its first line is on, px
        private readonly List<float> blockShrinks = new List<float>();
        private readonly List<Control> blockControls = new List<Control>();
        // footnote and float blocks, out of the flow: where the last paginate put each, each page's footnote rule,
        // each footnote's blocks by id, and whether the document has any
        private readonly List<Control> insertControls = new List<Control>();
        private readonly List<float> insertTops = new List<float>();
        private readonly List<float> insertHeights = new List<float>();
        private readonly List<(int page, float y)> footnoteRules = new List<(int, float)>();
        private readonly Dictionary<string, List<BlockControl>> footnoteGroups = new Dictionary<string, List<BlockControl>>();
        private bool inserts;
        private static readonly PageBands unbounded = new PageBands(0f, float.PositiveInfinity, float.MaxValue);
        private const float PxPerPt = 96f / 72.27f;

        private Vector2 measuredPaper;
        private PageBands paginatedBands;
        private int pageCount;
        private float pageHeight, lastPageHeight;

        // a sorted checklist reorder's slide, and the flow blocks still sliding
        private const float slideSeconds = 0.25f;
        private readonly List<BlockControl> sliding = new List<BlockControl>();

        // A control above the first block on the first page, assigned by the editor.
        public Control? header
        {
            get => field;
            set
            {
                if (field != null) field.Destroy();
                field = value;
                if (value != null) AddChild(value);
            }
        }
        private float headerHeight;

        // the widest unwrapped code line, margins included; the note is at least this wide
        private float contentWidth;

        // caret and highlight paint, assigned by the editor before either is built
        public string? caretColorHex;
        public string? selectionColorHex;

        // the model these blocks came from; the file is written from its block list
        internal RichTextDocument document
        {
            get => field;
            set
            {
                if (field != null) field.changed -= OnNoteChanged;
                field = value;
                field.changed += OnNoteChanged;
            }
        } = null!;

        // the open note's history, assigned by the editor; null until a session exists
        internal UndoStack undo;
        internal DocumentEditSession? session;

        // a .txt note, which takes no Markdown as it is typed
        internal bool plainText;

        private CaretControl caret;
        private readonly List<PanelControl> highlights = new List<PanelControl>();
        private readonly List<BlockControl> selectedBlocks = new List<BlockControl>();

        public BlockControl caretBlock { get; private set; }
        public int caretOffset { get; private set; }

        // the caret block's flat index; -1 until it is looked up
        private int caretIndex = -1;

        // Where a selection started; equal to the caret means nothing is selected.
        private CaretSlot anchor;

        // a style change with nothing to apply it to, spent on the next character typed
        private StyleDelta pending;

        // a selection picked up by a press, and where it would land
        internal bool textDragging { get; private set; }
        internal bool textDragHovered;
        private CaretSlot textDragPress;
        private bool textDragPicture;
        private CaretControl dropCaret;

        // the selected picture's frame lines and handles, and the resize in progress
        private readonly List<PanelControl> pictureFrame = new List<PanelControl>();
        private readonly List<PictureHandle> pictureHandles = new List<PictureHandle>();
        private PictureRotator? pictureRotator;
        private DocumentAddress resizeAt;
        private StyleSpan resizeStored;
        private Quaternion resizeRotation;

        // the turn in progress, measured from the press about the picture's centre
        private DocumentAddress rotateAt;
        private StyleSpan rotateStored;
        private Vector2 rotateCentre;
        private Vector2 rotateGrab;
        private bool rotating;

        // floating pictures as the last pagination placed them, in column space, and the views drawing them
        private readonly List<FloatPicture> floats = new List<FloatPicture>();
        private readonly List<FloatingPicture> behindViews = new List<FloatingPicture>();
        private readonly List<FloatingPicture> frontViews = new List<FloatingPicture>();
        private DocumentAddress moveAt;
        private StyleSpan moveStored;
        private Vector2 moveGrab;
        private bool moving;
        private LayoutRect resizeBox;
        private Vector2 resizeGrab;
        private bool resizing;
        private CaretSlot? dropSlot;

        // list numbers and markers are stale; resolved at the next measure
        private bool listsDirty = true;

        // the last copy with its formatting, for a paste of the same text
        private static string? copiedText;
        private static DocumentFragment? copiedFragment;

        // A line above the caret ends exactly where the caret's line begins; the slack is for the
        // float arithmetic that got them both there.
        private const float bandTolerance = 0.5f;

        public CaretSlot Focus => new CaretSlot(caretBlock, caretOffset);

        // The editor above takes the focus, so the caret survives anything still inside it.
        public override Control? ActiveContextTarget() => (parent as Control)?.ActiveContextTarget();

        #region ---- caret ----
        // extend keeps the anchor where it is, which is what shift does; otherwise the selection
        // collapses onto the new position.
        public void SetCaret(BlockControl block, int offset, bool extend = false)
        {
            if (block == null) return;

            if (block != caretBlock) caretIndex = -1;
            caretBlock = block;
            caretOffset = Math.Clamp(offset, 0, block.Length);
            if (!extend) anchor = Focus;

            if (caret == null)
            {
                caret = new CaretControl { hitTestable = false };
                caret.PaintOr(caretColorHex, PaletteRole.Ink);
                AddChild(caret);
            }
            caret.Focus();

            InvalidateArrange();
        }

        public void CollapseSelection() => anchor = Focus;

        internal void Blur() => caret?.Blur();

        internal void FocusCaret() => caret?.Focus();

        // The run tells the document which character was pressed; the document owns the caret.
        public void GlyphPressed(TextRunControl run, int index)
        {
            if (run is not BlockControl block) return;

            PressAt(block, index);
        }

        // A press on a picture selects it; on the selected inline one it picks it up. Any other button
        // only selects it.
        public void PicturePressed(TextRunControl run, int index, int button)
        {
            if (run is not BlockControl block) return;
            if (button == PointerEvent.leftButton && Extending)
            {
                PressAt(block, index);
                return;
            }

            DisarmStyle();
            if (button == PointerEvent.leftButton && !StoredPicture(block, index).IsFloating
                && SelectedPicture(out BlockControl selected, out int at) && selected == block && at == index)
            {
                textDragPicture = true;
                if (BeginTextDrag(new CaretSlot(block, index))) return;
                textDragPicture = false;
            }

            SetCaret(block, index);
            SetCaret(block, index + 1, true);
        }

        // A press that landed on the document itself — the gap between blocks, or past the last one.
        public override bool OnPointerPress(PointerEvent e)
        {
            if (CaretOffText(e.point, out BlockControl block, out int offset))
                PressAt(block, offset);

            return true;
        }

        // A press inside the selection picks it up; anywhere else places the caret.
        private void PressAt(BlockControl block, int offset)
        {
            DisarmStyle();
            if (!Extending && InSelection(block, offset) && BeginTextDrag(new CaretSlot(block, offset))) return;

            SetCaret(block, offset, Extending);
            Editor?.BeginSelectionDrag();
        }

        // Two clicks take the word, three the visual line.
        public override bool OnPointerTap(PointerEvent e)
        {
            if (e.tapCount == 2 && SelectedMath(out _, out _) && Editor?.EditFormula() == true) return true;
            if (e.tapCount >= 2) DisarmStyle();
            if (e.tapCount == 2) SelectWord();
            else if (e.tapCount >= 3) Editor?.SelectLine();
            else return false;

            return true;
        }

        private DocumentEditorControl Editor => parent as DocumentEditorControl;

        internal static bool Extending => InputHandler.instance.IsModifierDown(InputModifier.Extend);
        #endregion

        #region ---- caret navigation ----
        // The caret's rect in the space the blocks are arranged in.
        internal bool CaretPoint(out float x, out float y, out float height)
        {
            x = y = height = 0f;
            if (caretBlock == null) return false;

            CaretGeometry geometry = caretBlock.CaretAt(caretOffset);
            Vector2 origin = caretBlock.TextOrigin;

            x = origin.X + geometry.x;
            y = origin.Y + geometry.top;
            height = geometry.height;
            return true;
        }

        // Nearest caret slot to a point. The candidate is a visual line rather than a block — a line
        // closer in y always wins and x only breaks ties within a band, which is what makes one
        // primitive answer up/down, line start/end and page moves.
        // bandMin/bandMax restrict which lines may answer: up and down need the line the caret is
        // already on excluded, or the gap between blocks makes it the nearest one to a point just
        // outside it and the caret never leaves.
        internal bool CaretAtPoint(float x, float y, out BlockControl block, out int offset,
            float bandMin = float.NegativeInfinity, float bandMax = float.PositiveInfinity)
        {
            block = null;
            offset = 0;

            BlockControl best = null;
            float bestLineTop = 0f;
            float bestY = float.MaxValue;
            float bestX = float.MaxValue;

            foreach (BlockControl candidate in Blocks())
            {
                IReadOnlyList<TextLine> lines = candidate.Lines;
                if (lines == null) continue;

                Vector2 origin = candidate.TextOrigin;

                foreach (TextLine line in lines)
                {
                    float top = origin.Y + line.top;

                    if (top < bandMin - bandTolerance || top + line.height > bandMax + bandTolerance)
                        continue;

                    float dy = Distance(y, top, top + line.height);
                    float dx = Distance(x, origin.X + line.left, origin.X + line.left + line.width);
                    if (dy > bestY || (dy == bestY && dx >= bestX)) continue;

                    bestY = dy;
                    bestX = dx;
                    best = candidate;
                    bestLineTop = line.top;
                }
            }

            if (best == null) return false;

            block = best;
            offset = best.IndexAt(new Vector2(x, best.TextOrigin.Y + bestLineTop + 1f));
            return true;
        }

        // A point that landed on no line at all. Below every block there is no line worth resolving
        // against, so the document's end stands in; anywhere else the nearest slot does.
        internal bool CaretOffText(Vector2 point, out BlockControl block, out int offset)
        {
            BlockControl last = LastBlock();
            if (last != null && point.Y > last.arrangedRect.Bottom)
            {
                block = last;
                offset = last.Length;
                return true;
            }

            return CaretAtPoint(point.X, point.Y, out block, out offset);
        }

        // Blocks in document order, so a caret can step out of one and into its neighbour.
        internal BlockControl AdjacentBlock(BlockControl from, int direction)
        {
            List<BlockControl> blocks = Blocks();

            int index = blocks.IndexOf(from);
            if (index < 0) return null;

            int adjacent = index + direction;
            return adjacent >= 0 && adjacent < blocks.Count ? blocks[adjacent] : null;
        }

        private BlockControl LastBlock()
        {
            List<BlockControl> blocks = Blocks();
            return blocks.Count > 0 ? blocks[^1] : null;
        }

        private static float Distance(float value, float low, float high) =>
            value < low ? low - value : (value > high ? value - high : 0f);
        #endregion

        #region ---- selection ----
        public bool HasSelection => caretBlock != null && !anchor.Equals(Focus);

        // The run of one character class around the caret, inside the block.
        public void SelectWord()
        {
            if (caretBlock == null) return;

            string s = caretBlock.text ?? string.Empty;
            TextInputActions.CharClass? right = caretOffset < s.Length ? TextInputActions.ClassOf(s[caretOffset]) : null;
            TextInputActions.CharClass? left = caretOffset > 0 ? TextInputActions.ClassOf(s[caretOffset - 1]) : null;

            // either edge of a word takes the word, not the space beside it
            TextInputActions.CharClass? picked = right == TextInputActions.CharClass.Word || left == TextInputActions.CharClass.Word
                ? TextInputActions.CharClass.Word : right ?? left;
            if (picked == null) return;

            int start = caretOffset;
            while (start > 0 && TextInputActions.ClassOf(s[start - 1]) == picked) start--;

            int end = caretOffset;
            while (end < s.Length && TextInputActions.ClassOf(s[end]) == picked) end++;

            anchor = new CaretSlot(caretBlock, start);
            SetCaret(caretBlock, end, true);
        }

        // The first block's start to the last block's end, whatever the caret was doing.
        public void SelectAll()
        {
            List<BlockControl> blocks = Blocks();
            if (blocks.Count == 0) return;

            DisarmStyle();
            anchor = new CaretSlot(blocks[0], 0);
            SetCaret(blocks[^1], blocks[^1].Length, true);
        }

        // The two ends in reading order, since a drag can run backwards.
        internal bool OrderedSelection(out DocumentAddress from, out DocumentAddress to)
        {
            from = to = default;
            if (!HasSelection) return false;

            DocumentAddress a = AddressOf(anchor.block, anchor.offset);
            DocumentAddress b = AddressOf(caretBlock, caretOffset);
            if (a.block < 0 || b.block < 0) return false;

            bool forward = a.block < b.block || (a.block == b.block && a.offset <= b.offset);
            from = forward ? a : b;
            to = forward ? b : a;
            return true;
        }

        internal DocumentAddress AnchorAddress => AddressOf(anchor.block, anchor.offset);

        // Puts a selection back by address; equal ends leave only a caret.
        internal void Select(DocumentAddress from, DocumentAddress to)
        {
            if (!Resolve(from, out BlockControl anchorBlock, out int anchorOffset)) return;
            if (!Resolve(to, out BlockControl block, out int offset)) return;

            anchor = new CaretSlot(anchorBlock, anchorOffset);
            SetCaret(block, offset, true);
            caretIndex = to.block;
        }

        internal bool InSelection(BlockControl block, int offset)
        {
            if (!OrderedSelection(out DocumentAddress from, out DocumentAddress to)) return false;

            DocumentAddress at = AddressOf(block, offset);
            return !Before(at, from) && !Before(to, at);
        }

        private static bool Before(DocumentAddress a, DocumentAddress b) =>
            a.block < b.block || (a.block == b.block && a.offset < b.offset);

        // The selection as data, when it sits in one container and so can be lifted out.
        internal DocumentFragment? SelectedFragment()
        {
            if (!OrderedSelection(out DocumentAddress from, out DocumentAddress to)) return null;
            return OneContainer(from.block, to.block) ? document.CaptureFragment(from, to) : null;
        }

        // Puts the selection on the clipboard as plain text, keeping its formatting for our own paste.
        internal bool CopySelection()
        {
            if (!OrderedSelection(out DocumentAddress from, out DocumentAddress to)) return false;

            DocumentFragment fragment = document.CaptureFragment(from, to);
            string text =string.Join(Environment.NewLine, fragment.blocks.Select(block => PlainText(block, session?.path))).Replace(TextMeasurer.SoftHyphen.ToString(), string.Empty)
                .Replace(TextMeasurer.ZeroWidthSpace.ToString(), string.Empty).Replace(TextMeasurer.UrlBreak.ToString(), string.Empty)
                .Replace(TextMeasurer.UrlBigBreak.ToString(), string.Empty);

            copiedText = text;
            copiedFragment = fragment;
            ClipboardText.Set(text);
            return true;
        }

        // A snapshot's text with each formula written as its TeX source.
        private static string PlainText(BlockSnapshot block, string? home)
        {
            StringBuilder plain = new StringBuilder(block.text.Length);
            int start = 0;
            foreach (StyleSpan span in block.spans)
            {
                int count = Math.Clamp(span.count, 0, block.text.Length - start);
                if (span.IsMath && count > 0)
                {
                    string fence = span.mathDisplay ? "$$" : "$";
                    plain.Append(fence).Append(span.mathSource).Append(fence);
                }
                else if (span.IsSheet && count > 0)
                    plain.Append(SheetLinks.Plain(home, span.sheetRef));
                else if (span.spaceWidth != 0f)
                    plain.Append(' ', count);
                else
                    plain.Append(block.text, start, count);
                start += count;
            }
            if (start < block.text.Length) plain.Append(block.text, start, block.text.Length - start);
            return plain.ToString();
        }

        // Boxes for the selected range, one per visual line it covers. Everything unused is arranged
        // to nothing rather than destroyed — a drag would otherwise create and free controls every
        // tick, each one a pool allocation and a full paint-order permute.
        private void ArrangeSelection()
        {
            int used = 0;

            foreach (BlockControl marked in selectedBlocks)
                marked.selectedFrom = marked.selectedTo = -1;
            selectedBlocks.Clear();

            if (OrderedSelection(out DocumentAddress from, out DocumentAddress to))
            {
                List<BlockControl> blocks = Blocks();

                for (int i = from.block; i <= to.block && i < blocks.Count; i++)
                {
                    BlockControl block = blocks[i];
                    block.selectedFrom = i == from.block ? from.offset : 0;
                    block.selectedTo = i == to.block ? to.offset : block.Length;
                    selectedBlocks.Add(block);

                    used = HighlightBlock(block, block.selectedFrom, block.selectedTo, used);
                }
            }

            for (int i = used; i < highlights.Count; i++)
                highlights[i].Arrange(new LayoutRect(arrangedRect.x, arrangedRect.y, 0f, 0f));
        }

        // The x span comes from CaretAt, the same function that places the caret — so the highlight
        // cannot drift from the caret drawn inside it.
        private int HighlightBlock(BlockControl block, int from, int to, int used)
        {
            IReadOnlyList<TextLine> lines = block.Lines;
            if (lines == null || to <= from) return used;

            Vector2 origin = block.TextOrigin;
            ScrollableControl? viewport = TableViewport(block);

            foreach (TextLine line in lines)
            {
                if (line.segments.Count == 0) continue;

                LineSegment last = line.segments[line.segments.Count - 1];
                int lineStart = line.segments[0].charStart;
                int lineEnd = last.charStart + last.charCount;

                int start = Math.Max(from, lineStart);
                int end = Math.Min(to, lineEnd);
                if (end <= start) continue;

                // A wrapped line's end slot belongs to the line below, so CaretAt would answer for
                // the wrong line — the line's own width is the right edge in that case.
                float left = start == lineStart ? line.left : block.CaretAt(start).x;
                float right = end == lineEnd ? line.left + line.width : block.CaretAt(end).x;

                LayoutRect box = new LayoutRect(origin.X + left, origin.Y + line.top, right - left, line.height);
                if (viewport != null) box = LayoutRect.Intersect(box, viewport.arrangedRect);
                if (box.width <= 0f) continue;

                Highlight(used++).Arrange(box);
            }

            return used;
        }

        // Highlights live at the head of the child list so they draw behind the blocks — paint order
        // is the tree's DFS order, which is why the caret, added last, draws over the text.
        private PanelControl Highlight(int index)
        {
            while (highlights.Count <= index)
            {
                PanelControl box = new PanelControl { hitTestable = false };
                box.PaintOr(selectionColorHex, PaletteRole.Line);
                box.parent = this;
                children.Insert(pages.Count + highlights.Count, box);
                highlights.Add(box);
                MarkTreeOrderDirty();
                box.Measure(arrange.measuredOffer);
            }
            return highlights[index];
        }
        #endregion

        #region ---- editing ----
        // Removes the selected range; false when nothing was selected.
        public bool DeleteSelection(bool restoreSelection = true)
        {
            if (!OrderedSelection(out DocumentAddress from, out DocumentAddress to)) return false;
            if (!OneContainer(from.block, to.block)) return false;

            DocumentAddress anchorAt = AddressOf(anchor.block, anchor.offset);
            DocumentAddress caretAt = restoreSelection ? AddressOf(caretBlock, caretOffset) : anchorAt;

            DocumentFragment fragment = document.CaptureFragment(from, to);
            undo?.Push(new DeleteRangeEdit(document, from, to, fragment, anchorAt, caretAt));
            document.DeleteBetween(from, to);
            KeepDeletedStyle(fragment);
            return true;
        }

        // What is typed next looks like the first character deleted; a style chosen outright still wins.
        private void KeepDeletedStyle(DocumentFragment fragment)
        {
            foreach (BlockSnapshot block in fragment.blocks)
                foreach (StyleSpan span in block.spans)
                {
                    if (span.count == 0) continue;

                    StyleDelta kept = StyleDelta.Of(span);
                    if (kept.Changes(caretBlock.StyleAt(caretOffset))) pending = kept.With(pending);
                    return;
                }
        }

        internal void DisarmStyle() => pending = default;

        // Replaces the selection with text; the caret ends after it.
        internal bool PasteText(string text)
        {
            if (HasSelection && !DeleteSelection()) return false;
            if (caretBlock == null) return false;
            LeaveRule();

            DocumentFragment fragment = text == copiedText && copiedFragment != null
                ? copiedFragment
                : FragmentFromText(text, caretBlock, caretOffset);

            Insert(AddressOf(caretBlock, caretOffset), ForDestination(fragment, caretBlock));
            DisarmStyle();
            return true;
        }

        // Replaces the selection with a picture in the caret's style; the caret ends after it.
        internal bool PasteImage(string path)
        {
            if (HasSelection && !DeleteSelection()) return false;
            if (caretBlock == null) return false;
            LeaveRule();

            StyleSpan span = caretBlock.StyleAt(caretOffset);
            span.count = 1;
            span.imageSource = path;

            BlockSnapshot block = new BlockSnapshot { text = BlockControl.PictureChar };
            block.spans.Add(span);
            DocumentFragment fragment = new DocumentFragment();
            fragment.blocks.Add(block);

            Insert(AddressOf(caretBlock, caretOffset), fragment);
            DisarmStyle();
            return true;
        }

        // Puts a fragment in at a slot and leaves it selected — a drop from another note.
        internal void InsertAt(CaretSlot slot, DocumentFragment fragment)
        {
            DocumentAddress from = AddressOf(slot.block, slot.offset);
            if (slot.block.stylingType == TextStyleType.Rule) from = OffRule(from);
            if (!Resolve(from, out BlockControl block, out _)) return;

            DocumentAddress to = Insert(from, ForDestination(fragment, block));
            Select(from, to);
            DisarmStyle();
        }

        // Moves or copies the selection to a slot, selected there; false for a slot inside it.
        internal bool DropSelection(CaretSlot slot, bool copy)
        {
            if (!OrderedSelection(out DocumentAddress from, out DocumentAddress to)) return false;
            if (InSelection(slot.block, slot.offset)) return false;

            DocumentFragment fragment = SelectedFragment();
            if (fragment == null) return false;

            DocumentAddress at = AddressOf(slot.block, slot.offset);
            if (!copy)
            {
                DeleteSelection();
                if (Before(to, at))
                    at = at.block == to.block
                        ? new DocumentAddress(from.block, from.offset + at.offset - to.offset)
                        : new DocumentAddress(at.block - (to.block - from.block), at.offset);
            }

            if (!Resolve(at, out BlockControl block, out _)) return false;
            if (block.stylingType == TextStyleType.Rule)
            {
                at = OffRule(at);
                if (!Resolve(at, out block, out _)) return false;
            }
            DocumentAddress end = Insert(at, ForDestination(fragment, block));
            Select(at, end);
            DisarmStyle();
            return true;
        }

        // Records and performs an insert; returns where it ends.
        private DocumentAddress Insert(DocumentAddress from, DocumentFragment fragment)
        {
            DocumentAddress to = fragment.blocks.Count == 1
                ? new DocumentAddress(from.block, from.offset + fragment.blocks[0].text.Length)
                : new DocumentAddress(from.block + fragment.blocks.Count - 1, fragment.blocks[^1].text.Length);

            undo?.Push(new InsertRangeEdit(document, from, to, fragment));
            document.InsertBetween(from, to, fragment);
            return to;
        }

        // Plain text as paragraphs, each in the style and block kind at the caret.
        private static DocumentFragment FragmentFromText(string text, BlockControl at, int offset)
        {
            StyleSpan style = at.StyleAt(offset);
            DocumentFragment fragment = new DocumentFragment();

            foreach (string line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                StringBuilder clean = new StringBuilder(line.Length);
                foreach (char c in line)
                    if (c == '\t') clean.Append(' ');
                    else if (!char.IsControl(c) && c != BlockControl.PictureChar[0]) clean.Append(c);

                BlockSnapshot block = new BlockSnapshot
                {
                    stylingType = at.stylingType,
                    alignment = at.alignment,
                    firstIndent = at.firstIndent,
                    leftIndent = at.note.leftIndent,
                    rightIndent = at.note.rightIndent,
                    rightIndentLast = at.note.rightIndentLast,
                    spaceBefore = at.spaceBefore,
                    shrinkBefore = at.shrinkBefore,
                    insert = at.insert,
                    language = at.language,
                    codeWrap = at.codeWrap,
                    listKind = at.listKind,
                    listLevel = at.listLevel,
                    listMarker = at.listMarker,
                    listIndent = at.note.listIndent,
                    lineHeight = at.note.lineHeight,
                    keepWithNext = at.note.keepWithNext,
                    strut = at.note.strut,
                    text = clean.ToString()
                };

                StyleSpan span = style;
                span.count = block.text.Length;
                block.spans.Add(span);
                fragment.blocks.Add(block);
            }

            return fragment;
        }

        // The last block of a multi-block fragment takes the destination block's kind.
        private static DocumentFragment ForDestination(DocumentFragment fragment, BlockControl destination)
        {
            if (fragment.blocks.Count < 2) return fragment;

            BlockSnapshot last = fragment.blocks[^1];
            BlockSnapshot landed = new BlockSnapshot
            {
                stylingType = destination.stylingType,
                alignment = destination.alignment,
                firstIndent = destination.firstIndent,
                leftIndent = destination.note.leftIndent,
                rightIndent = destination.note.rightIndent,
                rightIndentLast = destination.note.rightIndentLast,
                spaceBefore = destination.spaceBefore,
                shrinkBefore = destination.shrinkBefore,
                insert = destination.insert,
                language = destination.language,
                codeWrap = destination.codeWrap,
                listKind = destination.listKind,
                listLevel = destination.listLevel,
                listMarker = destination.listMarker,
                listIndent = destination.note.listIndent,
                lineHeight = destination.note.lineHeight,
                keepWithNext = destination.note.keepWithNext,
                strut = destination.note.strut,
                isChecked = destination.isChecked,
                text = last.text
            };
            landed.spans.AddRange(last.spans);

            DocumentFragment reshaped = new DocumentFragment();
            reshaped.blocks.AddRange(fragment.blocks.Take(fragment.blocks.Count - 1));
            reshaped.blocks.Add(landed);
            return reshaped;
        }

        // Splits the caret's block in two, the second carrying everything from the caret on.
        public void SplitBlock()
        {
            DeleteSelection();

            if (caretBlock == null) return;
            if (caretBlock.stylingType == TextStyleType.Rule)
            {
                LeaveRule();
                return;
            }
            if (caretBlock.Length == 0 && caretBlock.listKind != ListKind.None)
            {
                if (caretBlock.listLevel > 0) ShiftListLevel(-1);
                else ClearListAtCaret();
                return;
            }
            if (TypeMarkdownLine() || EndCodeBlock()) return;
            SplitBlockAt(AddressOf(caretBlock, caretOffset));
        }

        // The split itself, addressed rather than read off the caret, so redo can replay it.
        internal void SplitBlockAt(DocumentAddress at)
        {
            if (!Resolve(at, out _, out _)) return;

            document.SplitBlockAt(at);
            undo?.Push(new SplitEdit(document, at));
        }

        // Records the insert before the write, because the address is read off the caret the write is
        // about to advance. An armed style is spent here: the character is written with the style
        // beside it and then restyled, so the partition and both records are a selection restyle's.
        internal void TypeChar(char c)
        {
            if (caretBlock == null) return;
            LeaveRule();

            BlockControl block = caretBlock;
            DocumentAddress at = AddressOf(block, caretOffset);
            StyleDelta armed = pending;
            pending = default;

            undo?.Push(new TextEdit(document, at, c.ToString(), true));
            document.InsertText(block.note, at, c.ToString());

            if (c == ' ' && TypeMarkdownPrefix(block, at.block, at.offset + 1))
            {
                pending = armed;
                return;
            }

            if (TypeInlineMarkdown(block, at.block, at.offset + 1, c, out StyleDelta closed))
            {
                pending = armed.With(closed);
                return;
            }

            if (!armed.Changes(block.StyleAt(at.offset + 1))) return;

            ApplyStyleTo(at, new DocumentAddress(at.block, at.offset + 1), armed);
            CollapseSelection();
        }

        // Blocks sit between the highlight boxes at the head of the child list and the caret at its
        // end, so a new one goes in beside its neighbour rather than at either end.
        private void InsertBlockAfter(BlockControl after, BlockControl block)
        {
            if (after.parent is StackPanelControl cell)
            {
                cell.children.Insert(cell.children.IndexOf(after) + 1, block);
                block.parent = cell;
                MarkTreeOrderDirty();
                cell.InvalidateLayout();
                ListsChanged();
                return;
            }

            children.Insert(children.IndexOf(after) + 1, block);
            block.parent = this;
            MarkTreeOrderDirty();
            InvalidateLayout();
            ListsChanged();
        }

        private void RemoveBlock(BlockControl block)
        {
            block.Destroy();
            ListsChanged();
        }

        // Every block in reading order, a table's cells included.
        internal List<BlockControl> Blocks()
        {
            List<BlockControl> blocks = new List<BlockControl>();
            foreach (Entity child in children)
            {
                if (child is BlockControl block) blocks.Add(block);
                else if (TableIn(child) is TableControl table) table.AppendBlocks(blocks);
            }

            return blocks;
        }

        // The table a child of the document holds, when it is a table's viewport.
        private static TableControl? TableIn(Entity child)
        {
            if (child is not ScrollableControl viewport) return null;

            foreach (Entity entry in viewport.children)
                if (entry is TableControl table) return table;

            return null;
        }

        // The viewport of the table a block sits in; null for a block of the note itself.
        internal static ScrollableControl? TableViewport(BlockControl? block) =>
            block?.parent?.parent is TableControl table ? table.parent as ScrollableControl : null;

        // Whether every block from first to last shares one parent — the note, or one cell.
        private bool OneContainer(int first, int last)
        {
            List<BlockControl> blocks = Blocks();
            if (first < 0 || last >= blocks.Count) return false;

            for (int b = first + 1; b <= last; b++)
                if (blocks[b].parent != blocks[first].parent) return false;

            return true;
        }
        #endregion

        #region ---- text drag ----
        // Picks the selection up; the drop marker is what the drag carries.
        private bool BeginTextDrag(CaretSlot press)
        {
            if (SelectedFragment() == null) return false;

            EnsureDropCaret();
            textDragging = true;
            textDragHovered = false;
            textDragPress = press;
            dropCaret.RegisterOnDragStop(EndTextDrag);
            dropCaret.StartDrag();
            return true;
        }

        // A release nothing took, never over an editor, is a click at the press.
        private void EndTextDrag(bool accepted)
        {
            textDragging = false;
            HideDrop();

            if (!accepted && !textDragHovered) SetCaret(textDragPress.block, textDragPress.offset);
            if (!accepted && !textDragHovered && textDragPicture) SetCaret(textDragPress.block, textDragPress.offset + 1, true);
            textDragPicture = false;
        }

        // The document whose selection a drag carries, or null for any other drag.
        internal static DocumentControl? TextDragSource(Control dragged) =>
            dragged is CaretControl { parent: DocumentControl { textDragging: true } source } ? source : null;

        internal void ShowDropAt(Vector2 point)
        {
            if (!CaretOffText(point, out BlockControl block, out int offset)) return;

            EnsureDropCaret();
            dropSlot = new CaretSlot(block, offset);
            dropCaret.Focus();
            InvalidateArrange();
        }

        internal void HideDrop()
        {
            if (dropSlot == null) return;

            dropSlot = null;
            dropCaret?.Blur();
            InvalidateArrange();
        }

        private void EnsureDropCaret()
        {
            if (dropCaret != null) return;

            dropCaret = new CaretControl { hitTestable = false };
            dropCaret.PaintOr(caretColorHex, PaletteRole.Ink);
            dropCaret.Blur();
            AddChild(dropCaret);
        }
        #endregion

        #region ---- pictures ----
        private const float handleSize = 8f;
        private const float minPicture = 8f;

        // the rotate ring: its distance past the corners, drawn width, grab band either side, and the Shift step in degrees
        private const float ringGap = 16f;
        private const float ringWidth = 1.5f;
        private const float ringBand = 6f;
        private const float rotateSnap = 15f;

        // The selection is exactly one picture character.
        internal bool SelectedPicture(out BlockControl block, out int index)
        {
            block = null!;
            index = 0;
            if (!OrderedSelection(out DocumentAddress from, out DocumentAddress to)) return false;
            if (from.block != to.block || to.offset != from.offset + 1) return false;
            if (!Resolve(from, out block, out index)) return false;
            return PictureFrame(block, index, out _, out _);
        }

        // A picture's drawn rect, unturned, and its turn: inline from its run, floating from its view.
        private bool PictureFrame(BlockControl block, int index, out LayoutRect rect, out Quaternion rotation)
        {
            rect = LayoutRect.Empty;
            rotation = Quaternion.Identity;
            if (!StoredPicture(block, index).IsFloating) return block.PictureFrame(index, out rect, out rotation);

            foreach (FloatingPicture view in FloatViews())
                if (view.floatIndex >= 0 && floats[view.floatIndex].block == block && floats[view.floatIndex].index == index)
                {
                    rect = view.arrangedRect;
                    rotation = view.rotation;
                    return true;
                }
            return false;
        }

        // The lowest stored Y a turned picture of this drawn size may take: its bounding box's top at its paragraph's top.
        private float MinPictureY(Vector2 size, Quaternion rotation) =>
            MathF.Ceiling((size.Y - new LayoutRect(0f, 0f, size.X, size.Y).Turned(rotation).height) * 0.5f / zoom);

        // Frame, handles and ring around the selected picture, turned with it, or out of the way.
        private void ArrangePictureFrame()
        {
            LayoutRect box = LayoutRect.Empty;
            Quaternion rotation = Quaternion.Identity;
            bool shown = SelectedPicture(out BlockControl block, out int index) && PictureFrame(block, index, out box, out rotation);
            if (!shown && pictureFrame.Count == 0) return;

            EnsurePictureFrame();
            if (!shown)
            {
                foreach (PanelControl line in pictureFrame) line.Arrange(LayoutRect.Empty);
                foreach (PictureHandle handle in pictureHandles) handle.Arrange(LayoutRect.Empty);
                pictureRotator!.Arrange(LayoutRect.Empty);
                return;
            }

            Vector2 centre = new Vector2(box.x + box.width * 0.5f, box.y + box.height * 0.5f);
            Vector2 half = box.size * 0.5f;
            void Place(Control part, Vector2 local, Vector2 size)
            {
                part.rotation = rotation;
                Vector2 at = centre + Vector2.Transform(local, rotation);
                part.Arrange(new LayoutRect(at.X - size.X * 0.5f, at.Y - size.Y * 0.5f, size.X, size.Y));
            }

            Place(pictureFrame[0], new Vector2(0f, 0.5f - half.Y), new Vector2(box.width, 1f));
            Place(pictureFrame[1], new Vector2(0f, half.Y - 0.5f), new Vector2(box.width, 1f));
            Place(pictureFrame[2], new Vector2(0.5f - half.X, 0f), new Vector2(1f, box.height));
            Place(pictureFrame[3], new Vector2(half.X - 0.5f, 0f), new Vector2(1f, box.height));

            foreach (PictureHandle handle in pictureHandles)
                Place(handle, new Vector2(handle.Side.X * half.X, handle.Side.Y * half.Y), new Vector2(handleSize));

            float radius = half.Length() + ringGap;
            pictureRotator!.cornerRadius = new CornerRadii(radius);
            pictureRotator.Arrange(new LayoutRect(centre.X - radius, centre.Y - radius, radius * 2f, radius * 2f));
        }

        private void EnsurePictureFrame()
        {
            if (pictureFrame.Count > 0) return;

            for (int i = 0; i < 4; i++)
            {
                PanelControl line = new PanelControl { hitTestable = false };
                line.PaintOr(caretColorHex, PaletteRole.Ink);
                AddChild(line);
                pictureFrame.Add(line);
            }

            pictureRotator = new PictureRotator(this);
            if (caretColorHex != null) pictureRotator.edgeColorHex = caretColorHex;
            else pictureRotator.edgeRole = PaletteRole.Ink;
            AddChild(pictureRotator);

            foreach ((bool l, bool r, bool t, bool b) in new[]
            {
                (true, false, true, false), (false, false, true, false), (false, true, true, false), (false, true, false, false),
                (false, true, false, true), (false, false, false, true), (true, false, false, true), (true, false, false, false)
            })
            {
                PictureHandle handle = new PictureHandle(this, l, r, t, b);
                if (caretColorHex != null) handle.edgeColorHex = caretColorHex;
                else handle.edgeRole = PaletteRole.Ink;
                AddChild(handle);
                pictureHandles.Add(handle);
            }
        }

        private void BeginPictureResize(Vector2 point)
        {
            resizing = SelectedPicture(out BlockControl block, out int index)
                && PictureFrame(block, index, out resizeBox, out resizeRotation) && resizeBox.width > 0f && resizeBox.height > 0f && !readOnly;
            if (!resizing) return;

            resizeAt = AddressOf(block, index);
            resizeStored = StoredPicture(block, index);
            resizeGrab = point;
        }

        // Sized from the press in the picture's own frame, not by per-tick deltas; corners keep the aspect unless free.
        private void ResizePicture(PictureHandle handle, Vector2 point, bool free)
        {
            if (!resizing || !Resolve(resizeAt, out BlockControl block, out int index)) return;

            Vector2 drag = Vector2.Transform(point - resizeGrab, Quaternion.Conjugate(resizeRotation));
            float dx = drag.X * handle.Side.X;
            float dy = drag.Y * handle.Side.Y;
            float w0 = resizeBox.width;
            float h0 = resizeBox.height;
            float column = MathF.Max(minPicture, block.arrangedRect.Shrink(block.arrange.padding).width);
            bool corner = (handle.left || handle.right) && (handle.top || handle.bottom);

            float w, h;
            if (corner && !free)
            {
                float scale = MathF.Abs(dx / w0) >= MathF.Abs(dy / h0) ? (w0 + dx) / w0 : (h0 + dy) / h0;
                scale = MathF.Min(MathF.Max(scale, minPicture / MathF.Min(w0, h0)), column / w0);
                w = w0 * scale;
                h = h0 * scale;
            }
            else
            {
                w = Math.Clamp(w0 + dx, minPicture, column);
                h = MathF.Max(minPicture, h0 + dy);
            }

            bool widthOnly = corner && !free && resizeStored.imageHeight <= 0f;
            StyleSpan picture = resizeStored;
            picture.imageWidth = MathF.Round(w / zoom);
            picture.imageHeight = widthOnly ? 0f : MathF.Round(h / zoom);
            if (resizeStored.IsFloating)
            {
                Vector2 kept = Vector2.Transform(-handle.Side * new Vector2(w0 - w, h0 - h) * 0.5f, resizeRotation);
                Vector2 shift = kept - new Vector2(w - w0, h - h0) * 0.5f;
                picture.imageX = resizeStored.imageX + MathF.Round(shift.X / zoom);
                picture.imageY = MathF.Max(MinPictureY(new Vector2(w, h), resizeRotation), resizeStored.imageY + MathF.Round(shift.Y / zoom));
            }
            document.SetPicture(resizeAt, picture);
        }

        private void EndPictureResize()
        {
            if (!resizing) return;
            resizing = false;
            if (!Resolve(resizeAt, out BlockControl block, out int index)) return;

            StyleSpan after = StoredPicture(block, index);
            if (SamePicture(after, resizeStored) || Editor is not DocumentEditorControl editor) return;

            using (editor.BeginStep("Resize picture"))
                undo?.Push(new PictureEdit(document, resizeAt, resizeStored, after));
            editor.MarkDirty();
        }

        // Changes the selected picture's wrap; false when no picture outside a table is selected.
        internal bool SetPictureWrap(PictureWrap wrap)
        {
            if (!SelectedPicture(out BlockControl block, out int index) || TableViewport(block) != null) return false;

            StyleSpan before = StoredPicture(block, index);
            if (before.wrap == wrap) return false;

            StyleSpan after = before;
            after.wrap = wrap;
            if (wrap == PictureWrap.Inline)
            {
                after.imageX = 0f;
                after.imageY = 0f;
            }
            else if (!before.IsFloating && block.PictureFrame(index, out LayoutRect box, out Quaternion rotation))
            {
                after.imageX = MathF.Round((box.x - block.arrangedRect.x) / zoom);
                after.imageY = MathF.Max(MinPictureY(box.size, rotation), MathF.Round((box.y - block.arrangedRect.y) / zoom));
            }

            DocumentAddress at = AddressOf(block, index);
            undo?.Push(new PictureEdit(document, at, before, after));
            document.SetPicture(at, after);
            return true;
        }

        // Changes the selected picture's collision; false when nothing changes.
        internal bool SetPictureCollision(PictureCollision collision)
        {
            if (!SelectedPicture(out BlockControl block, out int index)) return false;

            StyleSpan before = StoredPicture(block, index);
            if (before.collision == collision) return false;

            StyleSpan after = before;
            after.collision = collision;
            DocumentAddress at = AddressOf(block, index);
            undo?.Push(new PictureEdit(document, at, before, after));
            document.SetPicture(at, after);
            return true;
        }

        private void BeginPictureRotate(Vector2 point)
        {
            LayoutRect box = LayoutRect.Empty;
            rotating = SelectedPicture(out BlockControl block, out int index) && PictureFrame(block, index, out box, out _) && !readOnly;
            if (!rotating) return;

            rotateAt = AddressOf(block, index);
            rotateStored = StoredPicture(block, index);
            rotateCentre = new Vector2(box.x + box.width * 0.5f, box.y + box.height * 0.5f);
            rotateGrab = point;
        }

        // Turned from the press by the arc between the grab and the pointer; whole degrees, Shift steps.
        private void RotatePicture(Vector2 point, bool snap)
        {
            if (!rotating || !Resolve(rotateAt, out BlockControl block, out int index)) return;

            Vector2 from = rotateGrab - rotateCentre;
            Vector2 to = point - rotateCentre;
            if (from.LengthSquared() < 1f || to.LengthSquared() < 1f) return;
            from = Vector2.Normalize(from);
            to = Vector2.Normalize(to);

            float dot = Vector2.Dot(from, to);
            Quaternion turn = dot < -0.9999f
                ? new Quaternion(0f, 0f, 1f, 0f)
                : Quaternion.Normalize(new Quaternion(0f, 0f, from.X * to.Y - from.Y * to.X, 1f + dot));
            Quaternion q = Quaternion.Concatenate(rotateStored.Rotation, turn);

            float degrees = 2f * MathF.Atan2(q.Z, q.W) * 180f / MathF.PI;
            degrees = snap ? MathF.Round(degrees / rotateSnap) * rotateSnap : MathF.Round(degrees);
            degrees = ((degrees % 360f) + 360f) % 360f;

            StyleSpan picture = rotateStored;
            picture.imageRotation = degrees;
            document.SetPicture(rotateAt, picture);
        }

        // One step: the new turn, a float re-anchored when its turned box rises above its paragraph.
        private void EndPictureRotate()
        {
            if (!rotating) return;
            rotating = false;
            if (!Resolve(rotateAt, out BlockControl block, out int index)) return;

            StyleSpan after = StoredPicture(block, index);
            if (SamePicture(after, rotateStored) || Editor is not DocumentEditorControl editor)
            {
                document.SetPicture(rotateAt, rotateStored);
                return;
            }

            if (after.IsFloating) PlaceFloat(block, index, rotateAt, rotateStored, after, editor, "Rotate picture");
            else
            {
                using (editor.BeginStep("Rotate picture"))
                    undo?.Push(new PictureEdit(document, rotateAt, rotateStored, after));
            }
            editor.MarkDirty();
        }

        // The picture span at an offset; default when there is none.
        private static StyleSpan StoredPicture(BlockControl block, int index)
        {
            int start = 0;
            foreach (StyleSpan span in block.spans)
            {
                if (start == index && span.IsPicture && span.count > 0) return span;
                start += span.count;
            }
            return default;
        }

        private static bool SamePicture(StyleSpan a, StyleSpan b) =>
            a.imageWidth == b.imageWidth && a.imageHeight == b.imageHeight
            && a.wrap == b.wrap && a.imageX == b.imageX && a.imageY == b.imageY
            && a.imageRotation == b.imageRotation && a.collision == b.collision;

        #region floating pictures
        private const float wrapGap = 8f;
        private const float minSlot = 48f;

        private struct FloatPicture
        {
            public BlockControl block;
            public int index;
            public LayoutRect rect;
            public PictureWrap wrap;
            public string source;
            public (float left, float right)[]? rows;

            // the turn about the rect's centre, what a turned Square wraps, and the turned rect's bounding box
            public Quaternion rotation;
            public PictureCollision collision;
            public LayoutRect bounds;

            public bool Wraps => wrap != PictureWrap.Behind && wrap != PictureWrap.InFront;
        }

        // Replaces a paragraph's floats with where they sit for this top.
        private void RegisterFloats(BlockControl block, float blockTop)
        {
            for (int i = floats.Count - 1; i >= 0; i--)
                if (floats[i].block == block) floats.RemoveAt(i);

            int start = 0;
            foreach (StyleSpan span in block.spans)
            {
                if (span.IsFloating && span.count > 0)
                {
                    Vector2 size = block.PictureSizeAt(start);
                    LayoutRect rect = new LayoutRect(span.imageX * zoom, blockTop + span.imageY * zoom, size.X, size.Y);
                    Quaternion rotation = span.Rotation;
                    floats.Add(new FloatPicture
                    {
                        block = block,
                        index = start,
                        rect = rect,
                        wrap = span.wrap,
                        source = span.imageSource,
                        rows = span.wrap == PictureWrap.Tight ? TextureAsset.ForFile(span.imageSource)?.OpaqueRows() : null,
                        rotation = rotation,
                        collision = span.collision,
                        bounds = rect.Turned(rotation)
                    });
                }
                start += span.count;
            }
        }

        // A wrapping float reaches into the paragraph as it stands.
        private bool WrapsAround(BlockControl block, float blockTop)
        {
            if (floats.Count == 0) return false;

            IReadOnlyList<TextLine> lines = block.Lines;
            float bottom = blockTop + (lines is { Count: > 0 } ? lines[^1].top + lines[^1].height : 0f);
            float gap = wrapGap * zoom;
            foreach (FloatPicture f in floats)
                if (f.Wraps && f.bounds.y - gap < bottom && f.bounds.Bottom + gap > blockTop) return true;
            return false;
        }

        private bool FloatsBelow(float y)
        {
            foreach (FloatPicture f in floats)
                if (f.Wraps && f.bounds.Bottom + wrapGap * zoom > y) return true;
            return false;
        }

        // A paragraph's lines placed down the pages and around the wrapping floats; the widest gap wins.
        private sealed class FloatSlots : ILineSlots
        {
            private readonly DocumentControl document;
            private readonly float blockTop;
            private readonly PageBands bands;
            private readonly float columnLeft;
            private readonly float columnRight;
            private readonly List<(float left, float right)> gaps = new List<(float, float)>();

            public FloatSlots(DocumentControl document, BlockControl block, float blockTop, PageBands bands, float textWidth)
            {
                this.document = document;
                this.blockTop = blockTop;
                this.bands = bands;
                columnLeft = block.arrange.padding.left;
                columnRight = MathF.Max(columnLeft, textWidth - block.arrange.padding.right);
            }

            public float Place(float y, float height, out float left, out float right)
            {
                float gap = wrapGap * document.zoom;
                float slot = MathF.Min(minSlot * document.zoom, columnRight - columnLeft);
                float top = blockTop + y;

                for (int guard = 0; guard < 64; guard++)
                {
                    top = bands.Push(top, height);
                    gaps.Clear();
                    gaps.Add((columnLeft, columnRight));
                    float below = float.MaxValue;

                    foreach (FloatPicture f in document.floats)
                    {
                        if (!f.Wraps || f.bounds.y - gap >= top + height || f.bounds.Bottom + gap <= top) continue;

                        if (f.wrap == PictureWrap.TopAndBottom) Cut(float.MinValue, float.MaxValue);
                        else if (f.rows == null && (f.collision == PictureCollision.Box || f.rotation.IsIdentity)) Cut(f.bounds.x - gap, f.bounds.Right + gap);
                        else if (Outline(f, top, height, out float from, out float to)) Cut(from - gap, to + gap);
                        else continue;
                        below = MathF.Min(below, f.bounds.Bottom + gap);
                    }

                    (float l, float r) widest = (columnLeft, columnLeft);
                    foreach ((float l, float r) g in gaps)
                        if (g.r - g.l > widest.r - widest.l) widest = g;

                    if (below == float.MaxValue || widest.r - widest.l >= slot)
                    {
                        left = widest.l - columnLeft;
                        right = widest.r - columnLeft;
                        return top - blockTop;
                    }
                    top = below;
                }

                left = 0f;
                right = columnRight - columnLeft;
                return top - blockTop;
            }

            // The opaque span of a Tight picture's rows, or a turned Shape's rect, beside the line; false when none of it is.
            private static bool Outline(FloatPicture f, float top, float height, out float from, out float to)
            {
                from = float.MaxValue;
                to = float.MinValue;
                if (f.rows == null)
                {
                    TurnedSpan(f, f.rect, top, top + height, ref from, ref to);
                    return from <= to;
                }

                int count = f.rows.Length;
                bool upright = f.rotation.IsIdentity;
                int first = upright ? Math.Max(0, (int)MathF.Floor((top - f.rect.y) / f.rect.height * count)) : 0;
                int last = upright ? Math.Min(count - 1, (int)MathF.Ceiling((top + height - f.rect.y) / f.rect.height * count) - 1) : count - 1;
                float rowHeight = f.rect.height / count;

                for (int r = first; r <= last; r++)
                {
                    (float left, float right) = f.rows[r];
                    if (left > right) continue;

                    float x0 = f.rect.x + left * f.rect.width;
                    float x1 = f.rect.x + right * f.rect.width;
                    if (upright)
                    {
                        from = MathF.Min(from, x0);
                        to = MathF.Max(to, x1);
                    }
                    else TurnedSpan(f, new LayoutRect(x0, f.rect.y + r * rowHeight, x1 - x0, rowHeight), top, top + height, ref from, ref to);
                }
                return from <= to;
            }

            // Widens from/to by the x extent, between top and bottom, of a part of the picture turned with it.
            private static void TurnedSpan(FloatPicture f, LayoutRect part, float top, float bottom, ref float from, ref float to)
            {
                Vector2 centre = new Vector2(f.rect.x + f.rect.width * 0.5f, f.rect.y + f.rect.height * 0.5f);
                Span<Vector2> corners = stackalloc Vector2[4];
                corners[0] = centre + Vector2.Transform(new Vector2(part.x, part.y) - centre, f.rotation);
                corners[1] = centre + Vector2.Transform(new Vector2(part.Right, part.y) - centre, f.rotation);
                corners[2] = centre + Vector2.Transform(new Vector2(part.Right, part.Bottom) - centre, f.rotation);
                corners[3] = centre + Vector2.Transform(new Vector2(part.x, part.Bottom) - centre, f.rotation);

                for (int i = 0; i < 4; i++)
                {
                    Vector2 p = corners[i];
                    Vector2 q = corners[(i + 1) & 3];
                    if (p.Y >= top && p.Y <= bottom) Widen(p.X, ref from, ref to);
                    Cross(p, q, top, ref from, ref to);
                    Cross(p, q, bottom, ref from, ref to);
                }
            }

            // Widens from/to by where the edge p-q crosses the height y, if it does.
            private static void Cross(Vector2 p, Vector2 q, float y, ref float from, ref float to)
            {
                if ((p.Y < y) != (q.Y < y)) Widen(p.X + (y - p.Y) / (q.Y - p.Y) * (q.X - p.X), ref from, ref to);
            }

            private static void Widen(float x, ref float from, ref float to)
            {
                from = MathF.Min(from, x);
                to = MathF.Max(to, x);
            }

            private void Cut(float from, float to)
            {
                for (int i = gaps.Count - 1; i >= 0; i--)
                {
                    (float l, float r) = gaps[i];
                    if (to <= l || from >= r) continue;

                    gaps.RemoveAt(i);
                    if (from > l) gaps.Add((l, from));
                    if (to < r) gaps.Add((to, r));
                }
            }
        }

        // One view per float: behind ones ahead of the paragraphs in the tree, the rest after them.
        private void SyncFloatViews(Vector2 availableSize)
        {
            int behind = 0, front = 0;
            for (int i = 0; i < floats.Count; i++)
            {
                FloatPicture f = floats[i];
                bool isBehind = f.wrap == PictureWrap.Behind;
                FloatingPicture view = FloatView(isBehind ? behindViews : frontViews, isBehind ? behind++ : front++, isBehind);

                if (view.source != f.source) view.source = f.source;
                view.floatIndex = i;
                view.preferredWidth = f.rect.width;
                view.preferredHeight = f.rect.height;
                view.rotation = f.rotation;
                view.Measure(availableSize);
            }

            for (int i = behind; i < behindViews.Count; i++) behindViews[i].floatIndex = -1;
            for (int i = front; i < frontViews.Count; i++) frontViews[i].floatIndex = -1;
        }

        private FloatingPicture FloatView(List<FloatingPicture> pool, int index, bool behind)
        {
            if (index < pool.Count) return pool[index];

            FloatingPicture view = new FloatingPicture(this);
            view.parent = this;
            int at = behind
                ? pages.Count + highlights.Count + behindViews.Count
                : pictureFrame.Count > 0 ? children.IndexOf(pictureFrame[0]) : children.Count;
            children.Insert(at, view);
            MarkTreeOrderDirty();
            pool.Add(view);
            return view;
        }

        private IEnumerable<FloatingPicture> FloatViews() => behindViews.Concat(frontViews);

        private void ArrangeFloats(float textX, float top)
        {
            foreach (FloatingPicture view in FloatViews())
            {
                if (view.floatIndex < 0)
                {
                    view.Arrange(LayoutRect.Empty);
                    continue;
                }

                LayoutRect r = floats[view.floatIndex].rect;
                view.Arrange(new LayoutRect(textX + r.x, top + r.y, r.width, r.height));
            }
        }

        private void BeginPictureMove(Vector2 point)
        {
            moving = SelectedPicture(out BlockControl block, out int index) && StoredPicture(block, index).IsFloating && !readOnly;
            if (!moving) return;

            moveAt = AddressOf(block, index);
            moveStored = StoredPicture(block, index);
            moveGrab = point;
        }

        // Offset from the press, kept on the paper; Y may go above the anchor until the drop.
        private void MovePicture(Vector2 point)
        {
            if (!moving || !Resolve(moveAt, out BlockControl block, out int index)) return;

            float marginLeft = page.marginLeft * PageLayout.PxPerMm;
            float width = block.PictureSizeAt(index).X / zoom;
            StyleSpan picture = moveStored;
            picture.imageX = Math.Clamp(MathF.Round(moveStored.imageX + (point.X - moveGrab.X) / zoom),
                -marginLeft, MathF.Max(-marginLeft, (fitWidth ? measuredPaper.X / zoom : page.SizePx().X) - marginLeft - width));
            picture.imageY = MathF.Round(moveStored.imageY + (point.Y - moveGrab.Y) / zoom);
            document.SetPicture(moveAt, picture);
        }

        // One step: the new offset, or the picture re-anchored to the paragraph at or above its top.
        private void EndPictureMove()
        {
            if (!moving) return;
            moving = false;
            if (!Resolve(moveAt, out BlockControl block, out int index)) return;

            StyleSpan after = StoredPicture(block, index);
            if (SamePicture(after, moveStored) || blockControls.IndexOf(block) < 0 || Editor is not DocumentEditorControl editor)
            {
                document.SetPicture(moveAt, moveStored);
                return;
            }

            PlaceFloat(block, index, moveAt, moveStored, after, editor, "Move picture");
            editor.MarkDirty();
        }

        // One step from stored to after: kept in its paragraph, or re-anchored to the one at or above its turned box's top.
        private void PlaceFloat(BlockControl block, int index, DocumentAddress at, StyleSpan stored, StyleSpan after,
                                DocumentEditorControl editor, string step)
        {
            int anchor = blockControls.IndexOf(block);
            if (anchor < 0)
            {
                using (editor.BeginStep(step))
                    undo?.Push(new PictureEdit(document, at, stored, after));
                return;
            }

            float lift = MinPictureY(block.PictureSizeAt(index), after.Rotation);
            float pictureTop = blockTops[anchor] + after.imageY * zoom;
            int target = AnchorFor(pictureTop + lift * zoom);
            BlockControl targetBlock = (BlockControl)blockControls[target];
            StyleSpan moved = after;
            moved.imageY = MathF.Max(lift, MathF.Round((pictureTop - blockTops[target]) / zoom));

            using (editor.BeginStep(step))
            {
                document.SetPicture(at, stored);
                if (targetBlock == block)
                {
                    undo?.Push(new PictureEdit(document, at, stored, moved));
                    document.SetPicture(at, moved);
                }
                else
                {
                    Select(at, new DocumentAddress(at.block, at.offset + 1));
                    DeleteSelection();

                    BlockSnapshot slice = new BlockSnapshot { text = BlockControl.PictureChar };
                    slice.spans.Add(moved);
                    DocumentFragment fragment = new DocumentFragment();
                    fragment.blocks.Add(slice);

                    DocumentAddress from = AddressOf(targetBlock, 0);
                    Select(from, Insert(from, fragment));
                    DisarmStyle();
                }
            }
        }

        // The last top-level paragraph whose top is at or above y; the first one when none is.
        private int AnchorFor(float y)
        {
            int first = -1, found = -1;
            for (int i = 0; i < blockControls.Count; i++)
            {
                if (blockControls[i] is not BlockControl) continue;
                if (first < 0) first = i;
                if (blockTops[i] <= y + PageBands.tolerance) found = i;
            }
            return found >= 0 ? found : first;
        }

        // A floating picture's drawing: a press selects it, a press on the selected one moves it.
        public sealed class FloatingPicture : ImageControl
        {
            private readonly DocumentControl document;
            internal int floatIndex = -1;

            internal FloatingPicture(DocumentControl document) => this.document = document;

            public override bool OnPointerEnter(PointerEvent e)
            {
                UIEngine.WindowOf(this)?.os.ChangeCursor(CursorShape.AllResize);
                return base.OnPointerEnter(e);
            }

            public override bool OnPointerExit(PointerEvent e)
            {
                UIEngine.WindowOf(this)?.os.ChangeCursor(CursorShape.Arrow);
                return base.OnPointerExit(e);
            }

            public override bool OnPointerPress(PointerEvent e)
            {
                if (floatIndex < 0 || floatIndex >= document.floats.Count) return false;

                FloatPicture f = document.floats[floatIndex];
                if (e.button == PointerEvent.leftButton && document.SelectedPicture(out BlockControl block, out int index)
                    && block == f.block && index == f.index)
                {
                    document.BeginPictureMove(e.point);
                    StartDrag();
                    return true;
                }

                document.PicturePressed(f.block, f.index, e.button);
                return true;
            }

            public override void OnDrag(PointerEvent e)
            {
                document.MovePicture(e.point);
                base.OnDrag(e);
            }

            public override void OnDragStop(bool accepted)
            {
                document.EndPictureMove();
                base.OnDragStop(accepted);
            }
        }
        #endregion

        // A resize grip on the selected picture's frame.
        public sealed class PictureHandle : PanelControl
        {
            // the sides it moves
            public readonly bool left;
            public readonly bool right;
            public readonly bool top;
            public readonly bool bottom;

            private readonly DocumentControl document;

            internal PictureHandle(DocumentControl document, bool left, bool right, bool top, bool bottom)
            {
                this.document = document;
                this.left = left;
                this.right = right;
                this.top = top;
                this.bottom = bottom;
                colorHex = "#FFFFFF";
                edgeThickness = new Thickness(1f);
            }

            // the sides it moves as signs along the picture's own axes
            internal Vector2 Side => new Vector2(left ? -1f : right ? 1f : 0f, top ? -1f : bottom ? 1f : 0f);

            // The resize cursor nearest the handle's turned direction.
            private CursorShape Shape
            {
                get
                {
                    const float diagonalSlope = 0.41421357f;
                    Vector2 d = Vector2.Transform(Side, rotation);
                    float x = MathF.Abs(d.X);
                    float y = MathF.Abs(d.Y);
                    return y <= x * diagonalSlope ? CursorShape.HResize
                        : x <= y * diagonalSlope ? CursorShape.VResize
                        : d.X * d.Y > 0f ? CursorShape.NwseResize : CursorShape.NeswResize;
                }
            }

            public override bool OnPointerEnter(PointerEvent e)
            {
                UIEngine.WindowOf(this)?.os.ChangeCursor(Shape);
                return base.OnPointerEnter(e);
            }

            public override bool OnPointerExit(PointerEvent e)
            {
                UIEngine.WindowOf(this)?.os.ChangeCursor(CursorShape.Arrow);
                return base.OnPointerExit(e);
            }

            public override bool OnPointerPress(PointerEvent e)
            {
                document.BeginPictureResize(e.point);
                StartDrag();
                return true;
            }

            public override void OnDrag(PointerEvent e)
            {
                document.ResizePicture(this, e.point, Extending);
                base.OnDrag(e);
            }

            public override void OnDragStop(bool accepted)
            {
                document.EndPictureResize();
                base.OnDragStop(accepted);
            }
        }

        // The ring around the selected picture; a press on its band turns the picture.
        public sealed class PictureRotator : PanelControl
        {
            private readonly DocumentControl document;

            internal PictureRotator(DocumentControl document)
            {
                this.document = document;
                edgeThickness = new Thickness(ringWidth);
            }

            protected internal override bool HitsShape(Vector2 point)
            {
                LayoutRect r = arrangedRect;
                float radius = r.width * 0.5f;
                float distance = Vector2.Distance(point, new Vector2(r.x + radius, r.y + r.height * 0.5f));
                return MathF.Abs(distance - radius + ringWidth * 0.5f) <= ringBand;
            }

            internal override void PaintRow(ref VulkanControl row)
            {
                base.PaintRow(ref row);
                row.paint = Palettes.clear;
                row.alpha = 1f;
            }

            public override bool OnPointerEnter(PointerEvent e)
            {
                UIEngine.WindowOf(this)?.os.ChangeCursor(CursorShape.Crosshair);
                return base.OnPointerEnter(e);
            }

            public override bool OnPointerExit(PointerEvent e)
            {
                UIEngine.WindowOf(this)?.os.ChangeCursor(CursorShape.Arrow);
                return base.OnPointerExit(e);
            }

            public override bool OnPointerPress(PointerEvent e)
            {
                document.BeginPictureRotate(e.point);
                StartDrag();
                return true;
            }

            public override void OnDrag(PointerEvent e)
            {
                document.RotatePicture(e.point, Extending);
                base.OnDrag(e);
            }

            public override void OnDragStop(bool accepted)
            {
                document.EndPictureRotate();
                base.OnDragStop(accepted);
            }
        }
        #endregion

        #region ---- formulas ----
        // The selection is exactly one formula character.
        internal bool SelectedMath(out BlockControl block, out int index)
        {
            block = null!;
            index = 0;
            if (!OrderedSelection(out DocumentAddress from, out DocumentAddress to)) return false;
            if (from.block != to.block || to.offset != from.offset + 1) return false;
            if (!Resolve(from, out block, out index)) return false;
            return StoredMath(block, index).IsMath;
        }

        // The formula span at an offset; default when there is none.
        internal static StyleSpan StoredMath(BlockControl block, int index)
        {
            int start = 0;
            foreach (StyleSpan span in block.spans)
            {
                if (start == index && span.IsMath && span.count > 0) return span;
                start += span.count;
            }
            return default;
        }

        // An empty formula at the caret, unrecorded; null where a formula cannot go.
        internal DocumentAddress? PlaceMath(bool display)
        {
            if (caretBlock == null || readOnly || plainText
                || caretBlock.stylingType is TextStyleType.Code or TextStyleType.Rule) return null;

            StyleSpan span = caretBlock.StyleAt(caretOffset).AsText();
            span.count = 1;
            span.mathSource = string.Empty;
            span.mathDisplay = display;

            BlockSnapshot block = new BlockSnapshot { text = BlockControl.PictureChar };
            block.spans.Add(span);
            DocumentFragment fragment = new DocumentFragment();
            fragment.blocks.Add(block);

            DocumentAddress at = AddressOf(caretBlock, caretOffset);
            document.InsertBetween(at, new DocumentAddress(at.block, at.offset + 1), fragment);
            Select(at, new DocumentAddress(at.block, at.offset + 1));
            DisarmStyle();
            return at;
        }

        // A placed formula as an insert record, its source final.
        internal void RecordPlaced(DocumentAddress at)
        {
            if (!Resolve(at, out BlockControl block, out int index)) return;

            BlockSnapshot snapshot = new BlockSnapshot { text = BlockControl.PictureChar };
            StyleSpan span = StoredMath(block, index);
            span.count = 1;
            snapshot.spans.Add(span);
            DocumentFragment fragment = new DocumentFragment();
            fragment.blocks.Add(snapshot);

            undo?.Push(new InsertRangeEdit(document, at, new DocumentAddress(at.block, at.offset + 1), fragment));
        }

        internal void RecordMath(DocumentAddress at, string before, string after) =>
            undo?.Push(new MathEdit(document, at, before, after));

        internal void RemovePlaced(DocumentAddress at) =>
            document.DeleteBetween(at, new DocumentAddress(at.block, at.offset + 1));

        // A formula's drawn box; its caret slot while it waits to be laid out.
        internal LayoutRect MathAnchor(DocumentAddress at)
        {
            if (!Resolve(at, out BlockControl block, out int index)) return LayoutRect.Empty;
            if (block.MathBox(index, out LayoutRect box)) return box;

            CaretGeometry slot = block.CaretAt(index);
            return new LayoutRect(block.TextOrigin.X + slot.x, block.TextOrigin.Y + slot.top, 0f, slot.height);
        }
        #endregion

        #region ---- sheet links ----
        // Replaces the selection with a live link to the copied cells; false where a plain paste should run.
        internal bool PasteLink(string text)
        {
            string? reference = SheetEditorControl.CopiedReference(text, session?.path);
            if (reference == null || caretBlock == null || readOnly || plainText
                || caretBlock.stylingType is TextStyleType.Code or TextStyleType.Rule) return false;

            if (HasSelection && !DeleteSelection()) return true;
            if (caretBlock == null) return true;

            StyleSpan span = caretBlock.StyleAt(caretOffset).AsText();
            span.count = 1;
            span.sheetRef = reference;

            BlockSnapshot block = new BlockSnapshot { text = BlockControl.PictureChar };
            block.spans.Add(span);
            DocumentFragment fragment = new DocumentFragment();
            fragment.blocks.Add(block);

            Insert(AddressOf(caretBlock, caretOffset), fragment);
            DisarmStyle();
            return true;
        }

        // Re-measures every block showing a sheet link.
        internal void RefreshSheetLinks()
        {
            foreach (BlockControl block in Blocks())
                foreach (StyleSpan span in block.spans)
                    if (span.IsSheet || span.IsMath && SheetLinks.HasMathLinks(span.mathSource))
                    {
                        block.InvalidateLayout();
                        break;
                    }
        }

        // Rewrites links the rename answers for; true when any changed.
        internal bool RenameSheetLinks(Func<string, string?> rename) => document.RenameSheetLinks(rename);
        #endregion

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

        #region ---- styling ----
        // What a toggle reads its current state from, and what the format bar reflects. The
        // selection's start rather than the caret's own end, so a backwards drag reads the same
        // style a forwards one does.
        public CaretStyle? StyleSource
        {
            get
            {
                BlockControl block = caretBlock;
                int offset = caretOffset;

                if (OrderedSelection(out DocumentAddress from, out _)
                    && Resolve(from, out BlockControl start, out int startOffset))
                {
                    block = start;
                    offset = startOffset;
                }

                return block == null ? null : new CaretStyle(block, block.StyleAt(offset), pending);
            }
        }

        // Whether every selected character passes a test; null with nothing selected.
        public bool? SelectionAll(Func<StyleSpan, bool> test)
        {
            if (!OrderedSelection(out DocumentAddress from, out DocumentAddress to)) return null;

            List<BlockControl> blocks = Blocks();
            for (int b = from.block; b <= to.block && b < blocks.Count; b++)
            {
                int start = b == from.block ? Math.Clamp(from.offset, 0, blocks[b].Length) : 0;
                int end = b == to.block ? Math.Clamp(to.offset, 0, blocks[b].Length) : blocks[b].Length;
                if (!blocks[b].AllSpans(start, end, test)) return false;
            }
            return true;
        }

        public TextStyleType CaretBlockStyling =>caretBlock?.stylingType ?? TextStyleType.Text;

        public TextAlignment CaretBlockAlignment => caretBlock?.alignment ?? TextAlignment.Left;

        // The alignment of every block the range touches; with nothing selected, the caret's own.
        public bool SetBlockAlignment(TextAlignment alignment)
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

            List<BlockSnapshot> before = SnapshotBlocks(first, last);
            document.ChangeBlocks(first, last, b => b.alignment = alignment);
            undo?.Push(new BlockStateEdit(document, first, before, SnapshotBlocks(first, last)));
            return true;
        }

        // Makes the selected paragraphs a bulleted list, or text again when the caret's paragraph is one.
        public bool ToggleBullets() => ToggleList(ListKind.Bullet, null);

        // Makes the selected paragraphs a list of the kind, numbered when the marker is, or text again when the
        // caret's paragraph already is that list.
        public bool ToggleList(ListKind kind, ListMarker? marker)
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

            bool already = caretBlock.listKind == kind && (marker == null
                || ListMarkers.IsNumbered(caretBlock.listMarker ?? ListMarker.Disc) == ListMarkers.IsNumbered(marker.Value));
            List<BlockSnapshot> before = SnapshotBlocks(first, last);
            document.ChangeBlocks(first, last, b =>
            {
                b.listKind = already ? ListKind.None : kind;
                b.listMarker = already ? null : marker;
            });
            undo?.Push(new BlockStateEdit(document, first, before, SnapshotBlocks(first, last)));
            if (!already && kind == ListKind.SortedTask) SortChecklists(first, last);
            return true;
        }

        // Restyles the selected range; with nothing selected the style is armed for the next
        // character instead. False either way when nothing was written.
        public bool ApplyStyle(StyleDelta delta)
        {
            if (OrderedSelection(out DocumentAddress from, out DocumentAddress to))
                return ApplyStyleTo(from, to, delta);

            ArmStyle(delta);
            return false;
        }

        // Holds a style for the next character typed. Merged into what is already armed, so bold
        // then italic types both; dropped once it agrees with the span the caret is in, which is
        // what makes a second toggle disarm rather than pin the span's own style onto it.
        public void ArmStyle(StyleDelta delta)
        {
            if (caretBlock == null) return;

            pending = pending.With(delta);
            if (!pending.Changes(caretBlock.StyleAt(caretOffset))) pending = default;
        }

        // Restyles an addressed range; false when an end names a block the document does not have.
        public bool ApplyStyleTo(DocumentAddress from, DocumentAddress to, StyleDelta delta)
        {
            int count = Blocks().Count;
            if (from.block < 0 || to.block < 0 || from.block >= count || to.block >= count) return false;

            List<BlockSnapshot> before = SnapshotBlocks(from.block, to.block);

            document.ApplyStyleBetween(from, to, delta);
            undo?.Push(new StyleRangeEdit(document, from.block, before, from, to, delta));
            return true;
        }

        // The styling type of every block the range touches; with nothing selected, the caret's own.
        public bool SetBlockStyling(TextStyleType type)
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

            List<BlockSnapshot> before = SnapshotBlocks(first, last);
            document.SetBlockStylingBetween(first, last, type);
            undo?.Push(new StyleRangeEdit(document, first, before, type));
            return true;
        }

        // A style change leaves the text alone, so a span of blocks as data is the whole inverse.
        internal List<BlockSnapshot> SnapshotBlocks(int first, int last)
        {
            List<BlockControl> blocks = Blocks();
            List<BlockSnapshot> snapshots = new List<BlockSnapshot>();

            for (int b = first; b <= last && b < blocks.Count; b++)
                snapshots.Add(blocks[b].Snapshot());

            return snapshots;
        }
        #endregion

        #region ---- addressing ----
        internal DocumentAddress AddressOf(BlockControl block, int offset) =>
            new DocumentAddress(block != null && block == caretBlock ? CaretIndex : Blocks().IndexOf(block), offset);

        private int CaretIndex
        {
            get
            {
                if (caretIndex < 0) caretIndex = Blocks().IndexOf(caretBlock);
                else CheckCaretIndex();
                return caretIndex;
            }
        }

        // Throws when the cached caret index no longer names the caret's block.
        [System.Diagnostics.Conditional("DEBUG")]
        private void CheckCaretIndex()
        {
            int walked = Blocks().IndexOf(caretBlock);
            if (walked != caretIndex)
                throw new Exception($"[DocumentControl] cached caret index {caretIndex} is stale; the caret's block is at {walked}.");
        }

        internal bool Resolve(DocumentAddress at, out BlockControl block, out int offset)
        {
            block = null;
            offset = 0;

            if (BlockAt(at.block) is not BlockControl found) return false;

            block = found;
            offset = Math.Clamp(at.offset, 0, block.Length);
            return true;
        }

        // The block at a flat index without building the list; null past the end.
        private BlockControl? BlockAt(int index)
        {
            if (index < 0) return null;
            if (index == caretIndex && caretBlock != null)
            {
                CheckCaretIndex();
                return caretBlock;
            }

            foreach (Entity child in children)
            {
                if (child is BlockControl block)
                {
                    if (index-- == 0) return block;
                }
                else if (TableIn(child) is TableControl table)
                    foreach (Entity entry in table.children)
                    {
                        if (entry is not StackPanelControl cell) continue;

                        foreach (Entity line in cell.children)
                            if (line is BlockControl cellBlock && index-- == 0) return cellBlock;
                    }
            }

            return null;
        }

        private void CaretTo(DocumentAddress at)
        {
            if (!Resolve(at, out BlockControl block, out int offset)) return;

            SetCaret(block, offset);
            caretIndex = at.block;
        }
        #endregion

        #region ---- model changes ----
        public override void OnDestroy()
        {
            if (document != null) document.changed -= OnNoteChanged;
            base.OnDestroy();
        }

        // Where another view's change moves the caret, the anchor and a text drag's press. A selected
        // picture or formula the change touches collapses onto the caret.
        private bool MapAddress(NoteChange change, out DocumentAddress caretTo, out DocumentAddress anchorTo, out DocumentAddress pressTo)
        {
            DocumentAddress caretAt = AddressOf(caretBlock, caretOffset);
            caretTo = anchorTo = pressTo = default;
            if (caretAt.block < 0) return false;

            DocumentAddress anchorAt = anchor.Equals(Focus) ? caretAt : AddressOf(anchor.block, anchor.offset);
            caretTo = change.Map(caretAt);
            anchorTo = change.Map(anchorAt);
            if (textDragging) pressTo = change.Map(AddressOf(textDragPress.block, textDragPress.offset));

            bool moves = change.kind != NoteChangeKind.Spans;
            if (!HasSelection || !(SelectedPicture(out _, out _) || SelectedMath(out _, out _))) return moves;

            int selected = Math.Min(caretAt.block, anchorAt.block);
            bool touched = change.kind switch
            {
                NoteChangeKind.Table => true,
                NoteChangeKind.Nodes => selected >= change.first && selected < change.first + change.count,
                NoteChangeKind.Text or NoteChangeKind.Spans or NoteChangeKind.Kind or NoteChangeKind.Inserted or NoteChangeKind.Removed =>
                    (selected >= change.first && selected < change.first + change.count)
                    || (change.kind is NoteChangeKind.Inserted or NoteChangeKind.Removed && selected == change.at.block),
                _ => false
            };
            if (touched) anchorTo = caretTo;
            return moves || touched;
        }

        // Brings the controls in line with a change the model made.
        private void OnNoteChanged(NoteChange change)
        {
            if (change.kind == NoteChangeKind.Replaced) return;

            bool follows = session == null || session.lead == null || session.lead == this;
            DocumentAddress caretTo = default, anchorTo = default, pressTo = default;
            bool remap = (!follows || change.kind == NoteChangeKind.Nodes) && caretBlock != null && MapAddress(change, out caretTo, out anchorTo, out pressTo);

            if (change.kind is NoteChangeKind.Inserted or NoteChangeKind.Removed or NoteChangeKind.Table or NoteChangeKind.Nodes) caretIndex = -1;

            switch (change.kind)
            {
                case NoteChangeKind.Text:
                case NoteChangeKind.Spans:
                {
                    if (change.count == 1)
                    {
                        BlockAt(change.first)?.InvalidateLayout();
                        break;
                    }

                    List<BlockControl> blocks = Blocks();
                    for (int b = change.first; b < change.first + change.count && b < blocks.Count; b++)
                        blocks[b].InvalidateLayout();
                    break;
                }
                case NoteChangeKind.Kind:
                {
                    List<BlockControl> blocks = Blocks();
                    for (int b = change.first; b < change.first + change.count && b < blocks.Count; b++)
                        blocks[b].ApplyLayout(document.layout);
                    ListsChanged();
                    break;
                }
                case NoteChangeKind.Inserted:
                {
                    List<NoteBlock> notes = document.Blocks();
                    BlockControl after = Blocks()[change.first - 1];
                    for (int i = 0; i < change.count; i++)
                    {
                        BlockControl block = new BlockControl(notes[change.first + i]);
                        block.CopyPaint(after);
                        InsertBlockAfter(after, block);
                        block.ApplyLayout(document.layout);
                        after = block;
                    }
                    break;
                }
                case NoteChangeKind.Removed:
                {
                    List<BlockControl> blocks = Blocks();
                    for (int i = change.first + change.count - 1; i >= change.first; i--)
                        RemoveBlock(blocks[i]);
                    break;
                }
                case NoteChangeKind.Table:
                    ShowTable(change.first);
                    break;
                case NoteChangeKind.Nodes:
                    ShowNodes();
                    break;
                case NoteChangeKind.Page:
                    if (!fitWidth) page = document.layout.Page;
                    InvalidateLayout();
                    break;
                case NoteChangeKind.Layout:
                    blockSpacing = document.layout.blockSpacing;
                    foreach (Entity child in children)
                    {
                        if (child is BlockControl block) block.ApplyLayout(document.layout);
                        else if (TableIn(child) is TableControl table) table.ApplyLayout(document.layout);
                    }
                    ListsChanged();
                    break;
                case NoteChangeKind.ReadOnly:
                    readOnly = document.readOnly;
                    break;
            }

            if (remap)
            {
                if (!Resolve(caretTo, out BlockControl block, out int offset))
                {
                    caretTo = new DocumentAddress(Blocks().Count - 1, int.MaxValue);
                    Resolve(caretTo, out block, out offset);
                }
                caretBlock = block;
                caretOffset = offset;
                caretIndex = block != null ? caretTo.block : -1;
                anchor = Resolve(anchorTo, out BlockControl anchorBlock, out int anchorOffset) ? new CaretSlot(anchorBlock, anchorOffset) : Focus;
                if (textDragging && Resolve(pressTo, out BlockControl pressBlock, out int pressOffset)) textDragPress = new CaretSlot(pressBlock, pressOffset);
                InvalidateArrange();
            }

            if (!follows || change.caret is not DocumentAddress caretAt) return;
            if (change.anchor is DocumentAddress anchorAt) Select(anchorAt, caretAt);
            else CaretTo(caretAt);
        }
        #endregion

        #region ---- tables ----
        private TableControl? CaretTable() => caretBlock?.parent?.parent as TableControl;

        // The caret at the very start of a list item with nothing selected, where Tab in a cell nests.
        internal bool AtListItemStart => caretBlock is { listKind: not ListKind.None } && caretOffset == 0 && !HasSelection;

        // Empty cells after the caret's block, with a paragraph kept after the table; false in a cell.
        internal bool InsertTable(int rows, int columns, float width)
        {
            if (caretBlock == null || caretBlock.parent != this) return false;

            BlockControl block = caretBlock;
            DocumentAddress caretBefore = AddressOf(block, caretOffset);
            int index = Array.IndexOf(document.blocks, block.note) + 1;
            if (index == document.blocks.Length) SplitBlockAt(AddressOf(block, block.Length));

            NoteTable model = document.PutTable(index, false, DocumentXml.NewTable(rows, columns, width))!;
            BlockControl first = ((TableControl)ViewOf(model)).CellBlocks(0, 0)[0];
            SetCaret(first, 0);
            undo?.Push(new TableEdit(document, index, null, DocumentXml.WriteTable(model), caretBefore, AddressOf(first, 0)));
            return true;
        }

        // toNew puts the caret in the new row's first cell, as Tab past the last cell does.
        internal bool InsertTableRow(bool below, bool toNew = false) => ChangeTable((xml, row, column) =>
        {
            List<XElement> rows = Named(xml, "Row");
            XElement added = new XElement(rows[row].Name,
                Enumerable.Range(0, Named(xml, "Column").Count).Select(_ => new XElement(xml.Name.Namespace + "Cell")));
            if (below) rows[row].AddAfterSelf(added);
            else rows[row].AddBeforeSelf(added);

            if (toNew) return (below ? row + 1 : row, 0, false);
            return (below ? row : row + 1, column, true);
        });

        // The new column takes the caret column's width; a merged cell it falls inside widens over it.
        internal bool InsertTableColumn(bool right) => ChangeTable((xml, row, column) =>
        {
            XNamespace ns = xml.Name.Namespace;
            List<XElement> columns = Named(xml, "Column");
            int at = right ? column + Span(Covering(Named(xml, "Row")[row], column).cell) : column;
            XElement added = new XElement(ns + "Column", new XAttribute("Width", (string)columns[column].Attribute("Width")!));
            if (at < columns.Count) columns[at].AddBeforeSelf(added);
            else columns[^1].AddAfterSelf(added);

            foreach (XElement r in Named(xml, "Row"))
            {
                if (at >= columns.Count)
                {
                    r.Add(new XElement(ns + "Cell"));
                    continue;
                }
                (XElement cell, int start) = Covering(r, at);
                if (start < at) SetSpan(cell, Span(cell) + 1);
                else cell.AddBeforeSelf(new XElement(ns + "Cell"));
            }
            return (row, right ? column : column + 1, true);
        });

        // The last row deletes the table.
        internal bool DeleteTableRow()
        {
            if (CaretTable() is { RowCount: 1 }) return DeleteTable();

            return ChangeTable((xml, row, column) =>
            {
                List<XElement> rows = Named(xml, "Row");
                rows[row].Remove();
                return (Math.Min(row, rows.Count - 2), column, false);
            });
        }

        // The last column deletes the table.
        internal bool DeleteTableColumn()
        {
            if (CaretTable() is { widths.Length: 1 }) return DeleteTable();

            return ChangeTable((xml, row, column) =>
            {
                List<XElement> columns = Named(xml, "Column");
                columns[column].Remove();
                foreach (XElement r in Named(xml, "Row"))
                {
                    XElement cell = Covering(r, column).cell;
                    if (Span(cell) > 1) SetSpan(cell, Span(cell) - 1);
                    else cell.Remove();
                }
                return (row, Math.Min(column, columns.Count - 2), false);
            });
        }

        // The next cell's blocks join the caret's, an empty side dropped; refused on a row's last cell.
        internal bool MergeTableCellRight()
        {
            if (caretBlock?.parent is not StackPanelControl caretCell || caretCell.parent is not TableControl table
                || caretCell.gridColumn + table.ColumnSpan(caretCell) >= table.widths.Length) return false;

            return ChangeTable((xml, row, column) =>
            {
                XElement cell = Covering(Named(xml, "Row")[row], column).cell;
                XElement next = cell.ElementsAfterSelf().First();
                if (EmptyCell(cell)) cell.RemoveNodes();
                if (!EmptyCell(next) || !cell.HasElements) cell.Add(next.Elements());
                SetSpan(cell, Span(cell) + Span(next));
                next.Remove();
                return (row, column, true);
            });
        }

        // A merged cell back to single cells, its blocks kept in the first; refused on a single cell.
        internal bool SplitTableCell()
        {
            if (caretBlock?.parent is not StackPanelControl caretCell || caretCell.parent is not TableControl table
                || table.ColumnSpan(caretCell) == 1) return false;

            return ChangeTable((xml, row, column) =>
            {
                XElement cell = Covering(Named(xml, "Row")[row], column).cell;
                for (int i = 1; i < Span(cell); i++)
                    cell.AddAfterSelf(new XElement(cell.Name, cell.Attributes().Where(a => a.Name.LocalName.StartsWith("Rule")).Select(a => new XAttribute(a))));
                XElement last = cell.ElementsAfterSelf().ElementAt(Span(cell) - 2);
                foreach (string name in new[] { "TrimAbove", "TrimBelow" })
                    if (Enum.TryParse((string?)cell.Attribute(name), out RuleTrim trim))
                    {
                        if ((trim & RuleTrim.Right) != 0) last.SetAttributeValue(name, "Right");
                        cell.SetAttributeValue(name, (trim & RuleTrim.Left) != 0 ? "Left" : null);
                    }
                SetSpan(cell, 1);
                return (row, column, true);
            });
        }

        // Refused when the note would be left with no paragraph to hold the caret.
        internal bool DeleteTable()
        {
            if (CaretTable() is not TableControl table || !document.blocks.Any(b => b is NoteBlock)) return false;

            int index = Array.IndexOf(document.blocks, table.model);
            int flat = Blocks().IndexOf(table.CellBlocks(0, 0)[0]);
            DocumentAddress caretBefore = AddressOf(caretBlock, caretOffset);
            XElement before = DocumentXml.WriteTable(table.model);
            document.PutTable(index, true, null);

            List<BlockControl> blocks = Blocks();
            BlockControl landing = blocks[Math.Min(flat, blocks.Count - 1)];
            SetCaret(landing, 0);
            undo?.Push(new TableEdit(document, index, before, null, caretBefore, AddressOf(landing, 0)));
            return true;
        }

        // A column drag finished: one step from the table as it was at the grab to as it is.
        internal void RecordTableResize(TableControl table, XElement before)
        {
            if (Editor is not DocumentEditorControl editor) return;

            DocumentAddress caret = caretBlock != null ? AddressOf(caretBlock, caretOffset) : default;
            using (editor.BeginStep("Resize column"))
                undo?.Push(new TableEdit(document, Array.IndexOf(document.blocks, table.model), before, DocumentXml.WriteTable(table.model), caret, caret));
            editor.MarkDirty();
        }

        // Rebuilds the caret's table from an edited copy of its XML, as one record. change gets the
        // caret's row and column and returns the cell the caret lands in; keep holds its line and offset.
        private bool ChangeTable(Func<XElement, int, int, (int row, int column, bool keep)> change)
        {
            if (caretBlock?.parent is not StackPanelControl cell || cell.parent is not TableControl table) return false;

            int index = Array.IndexOf(document.blocks, table.model);
            int line = cell.children.OfType<BlockControl>().ToList().IndexOf(caretBlock);
            int offset = caretOffset;
            DocumentAddress caretBefore = AddressOf(caretBlock, caretOffset);
            XElement before = DocumentXml.WriteTable(table.model);
            XElement edited = new XElement(before);

            (int row, int column, bool keep) = change(edited, cell.gridRow, cell.gridColumn);
            NoteTable model = document.PutTable(index, true, edited)!;

            List<BlockControl> lines = ((TableControl)ViewOf(model)).CellBlocks(row, column);
            BlockControl landing = lines[keep ? Math.Min(line, lines.Count - 1) : 0];
            SetCaret(landing, keep ? offset : 0);
            undo?.Push(new TableEdit(document, index, before, DocumentXml.WriteTable(model), caretBefore, AddressOf(landing, caretOffset)));
            return true;
        }

        // Drops the view of a table the model no longer holds, and builds one for the table at a note-level index.
        private void ShowTable(int index)
        {
            for (int i = children.Count - 1; i >= 0; i--)
                if (TableIn(children[i]) is TableControl stale && Array.IndexOf(document.blocks, stale.model) < 0)
                    children[i].Destroy();

            if (index < document.blocks.Length && document.blocks[index] is NoteTable model && ViewOf(model) == null)
            {
                TableControl table = new TableControl(model);
                table.ApplyLayout(document.layout);
                ScrollableControl viewport = table.Hosted();
                int at = index + 1 < document.blocks.Length
                    ? children.IndexOf(Hosting(ViewOf(document.blocks[index + 1])))
                    : children.IndexOf(Hosting(ViewOf(document.blocks[index - 1]))) + 1;
                children.Insert(at, viewport);
                viewport.parent = this;
            }

            MarkTreeOrderDirty();
            InvalidateLayout();
            ListsChanged();
        }

        // Drops the views of entries the note no longer holds and builds the missing ones in note order.
        private void ShowNodes()
        {
            HashSet<NoteNode> live = new HashSet<NoteNode>(document.blocks);
            for (int i = children.Count - 1; i >= 0; i--)
                if (NodeOf(children[i]) is NoteNode gone && !live.Contains(gone))
                    children[i].Destroy();

            Dictionary<NoteNode, Entity> shown = new Dictionary<NoteNode, Entity>();
            int firstView = -1;
            for (int i = 0; i < children.Count; i++)
            {
                if (NodeOf(children[i]) is not NoteNode node) continue;
                shown[node] = children[i];
                if (firstView < 0) firstView = i;
            }

            Entity? previous = null;
            foreach (NoteNode node in document.blocks)
            {
                if (shown.TryGetValue(node, out Entity? view))
                {
                    previous = view;
                    continue;
                }

                Entity added;
                if (node is NoteTable model)
                {
                    TableControl table = new TableControl(model);
                    table.ApplyLayout(document.layout);
                    added = table.Hosted();
                }
                else if (node is NoteSealed locked) added = new SealedControl(locked);
                else
                {
                    BlockControl block = new BlockControl((NoteBlock)node);
                    block.StartEffects();
                    block.ApplyLayout(document.layout);
                    added = block;
                }

                int at = previous != null ? children.IndexOf(previous) + 1 : firstView >= 0 ? firstView : children.Count;
                children.Insert(at, added);
                added.parent = this;
                if (previous == null && firstView >= 0) firstView++;
                previous = added;
            }

            MarkTreeOrderDirty();
            InvalidateLayout();
            ListsChanged();
        }

        private static NoteNode? NodeOf(Entity child) => child switch
        {
            BlockControl block => block.note,
            SealedControl locked => locked.note,
            _ => TableIn(child)?.model
        };

        // A note-level entry as it sits among the children: a block itself, a table its viewport.
        private static Entity Hosting(Control entry) => entry is TableControl table ? table.parent : entry;

        // The control showing a note-level entry.
        internal Control ViewOf(NoteNode node)
        {
            foreach (Entity child in children)
            {
                if (child is BlockControl block && block.note == node) return block;
                if (child is SealedControl locked && locked.note == node) return locked;
                if (TableIn(child) is TableControl table && table.model == node) return table;
            }

            return null!;
        }

        private static List<XElement> Named(XElement parent, string name) =>
            parent.Elements().Where(e => e.Name.LocalName == name).ToList();

        // The <Cell> of a <Row> covering a column, and the column it starts at.
        private static (XElement cell, int start) Covering(XElement row, int column)
        {
            List<XElement> cells = Named(row, "Cell");
            int start = 0;
            foreach (XElement cell in cells)
            {
                if (column < start + Span(cell)) return (cell, start);
                start += Span(cell);
            }
            return (cells[^1], start - Span(cells[^1]));
        }

        private static int Span(XElement cell) => Math.Max(1, (int?)cell.Attribute("ColumnSpan") ?? 1);

        // A cell holding at most one block whose runs carry nothing.
        private static bool EmptyCell(XElement cell) => Named(cell, "Block").Count <= 1
            && !cell.Descendants().Any(e => e.Name.LocalName == "Run" && e.Attributes().Any(a => a.Value.Length > 0));

        private static void SetSpan(XElement cell, int span) => cell.SetAttributeValue("ColumnSpan", span > 1 ? span : (object?)null);
        #endregion

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
                        ? y - ended[^1].descent + ended[^1].inkDepth + endedBlock.LineSkip : y;
                    y = placer.Arrive(group, y, inkEnd, out int rewind);
                    if (group.where == PageFloats.Where.Here)
                    {
                        previous = null;
                        afterHere = true;
                        floatShrink += 2f * placer.inTextShrink;
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
                if (space != null && paged && bands.PageOf(blockTop + height - PageBands.tolerance) > closed && ShrinkFrom(space, bands, closed, index + 1) is int again and >= 0)
                {
                    if (keptBreak >= again && keptBreak <= index)
                    {
                        forcedBreak = keptBreak;
                        forcedTop = bands.top + bands.PageOf(blockTops[keptBreak]) * bands.stride;
                    }
                    c = again == 0 ? -1 : children.IndexOf(blockControls[again - 1]);
                    y = again == 0 ? top + headerHeight : blockTops[again - 1] + blockHeights[again - 1];
                    previous = again == 0 ? null : blockControls[again - 1];
                    keptFrom = -1;
                    index = again;
                    continue;
                }
                keptFrom = block is { keepWithNext: true } ? (keptFrom >= 0 ? keptFrom : index) : -1;
                y = blockTop + height;
                index++;
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

        #region ---- layout ----
        protected override Vector2 MeasureCore(Vector2 availableSize)
        {
            if (listsDirty) RenumberLists();

            Vector2 paper = fitWidth && float.IsFinite(availableSize.X) ? new Vector2(availableSize.X, page.SizePx().Y * zoom) : page.SizePx() * zoom;
            if (isMeasureDirty || paper != measuredPaper || LayoutEngine.NoSkip)
            {
                measuredPaper = paper;
                float textWidth = MathF.Max(0f, paper.X - Mm(page.marginLeft + page.marginRight));

                HighlightCode();

                Profiling.Zone.Start("Document.MeasureBlocks");
                int from = -1;
                int to = -1;
                int count = 0;
                inserts = false;
                contentWidth = paper.X;
                foreach (Entity child in children)
                {
                    Control item;
                    if (child is BlockControl block)
                    {
                        block.SetZoom(zoom);
                        item = block;
                    }
                    else if (TableIn(child) is TableControl table)
                    {
                        table.SetZoom(zoom);
                        item = (Control)child;
                    }
                    else if (child is SealedControl locked) item = locked;
                    else continue;

                    item.Measure(new Vector2(textWidth, float.MaxValue));
                    if (item is BlockControl { stylingType: TextStyleType.Code, codeWrap: false } code)
                        contentWidth = MathF.Max(contentWidth, CodeWidth(code, textWidth) + Mm(page.marginLeft + page.marginRight));
                    if (IsInsert(child))
                    {
                        inserts = true;
                        continue;
                    }
                    if (item is BlockControl { shrinkBefore: > 0f } || TableIn(child) is { shrinkBefore: > 0f }) inserts = true;
                    if (Remeasured(item) || count >= blockControls.Count || blockControls[count] != item)
                    {
                        if (from < 0) from = count;
                        to = count;
                    }
                    count++;
                }
                if (from < 0) from = count;
                Profiling.Zone.End("Document.MeasureBlocks");

                float headerBefore = headerHeight;
                headerHeight = header?.Measure(new Vector2(textWidth, float.MaxValue)).Y ?? 0f;
                if (headerHeight != headerBefore) from = 0;

                Profiling.Zone.Start("Document.Paginate");
                float height = Paginate(paper, from, to);
                Profiling.Zone.End("Document.Paginate");
                SyncFloatViews(availableSize);

                EnsurePages();
                NumberPages();
                float inset = Inset;
                arrange.desired = new Vector2(contentWidth + 2f * inset, height + 2f * inset);
            }

            caret?.Measure(availableSize);
            foreach (PanelControl box in highlights)
                box.Measure(availableSize);
            foreach (PanelControl sheet in pages)
                sheet.Measure(availableSize);

            SetFlag(ArrangeFlags.MeasureDirty, false);
            return arrange.desired;
        }

        protected override void ArrangeCore(LayoutRect finalRect)
        {
            WriteArranged(finalRect);

            LayoutRect inner = finalRect.Shrink(arrange.padding);
            Vector2 paper = fitWidth ? measuredPaper : page.SizePx() * zoom;
            float inset = Inset;
            float x = inner.x + MathF.Max(inset, (inner.width - contentWidth) * 0.5f);
            float top = inner.y + inset;
            float textX = x + Mm(page.marginLeft);
            float textWidth = MathF.Max(0f, paper.X - Mm(page.marginLeft + page.marginRight));

            ArrangePages(x, top, paper.X);
            header?.Arrange(new LayoutRect(textX, top + Mm(page.marginTop), textWidth, headerHeight));

            Profiling.Zone.Start("Document.ArrangeBlocks");
            int index = 0;
            foreach (Entity child in children)
            {
                if ((child is not BlockControl && child is not SealedControl && TableIn(child) == null) || IsInsert(child) || index >= blockTops.Count) continue;

                float width = child is BlockControl { stylingType: TextStyleType.Code, codeWrap: false } code ? CodeWidth(code, textWidth)
                    : child is SealedControl locked ? MathF.Min(textWidth, locked.DesiredSize.X) : textWidth;
                ((Control)child).Arrange(new LayoutRect(textX, top + blockTops[index], width, blockHeights[index]));
                index++;
            }
            for (int i = 0; i < insertControls.Count; i++)
                insertControls[i].Arrange(new LayoutRect(textX, top + insertTops[i], textWidth, insertHeights[i]));
            Profiling.Zone.End("Document.ArrangeBlocks");

            // after the blocks, so every line's geometry is this frame's
            Profiling.Zone.Start("Document.ArrangeOverlays");
            ArrangeFloats(textX, top);
            ArrangeSelection();
            ArrangePictureFrame();
            ArrangeCaret();
            Profiling.Zone.End("Document.ArrangeOverlays");

            SetFlag(ArrangeFlags.ArrangeDirty, false);
        }

        // After the blocks, so the caret resolves against this frame's line geometry.
        private void ArrangeCaret()
        {
            if (caret != null) ArrangeCaretAt(caret, caretBlock, caretOffset);
            if (dropCaret != null) ArrangeCaretAt(dropCaret, dropSlot?.block, dropSlot?.offset ?? 0);
        }

        private static void ArrangeCaretAt(CaretControl marker, BlockControl block, int offset)
        {
            if (block == null)
            {
                marker.Arrange(LayoutRect.Empty);
                return;
            }

            CaretGeometry geometry = block.CaretAt(offset);
            Vector2 origin = block.TextOrigin;

            LayoutRect rect = new LayoutRect(origin.X + geometry.x, origin.Y + geometry.top,
                CaretControl.Width, geometry.height);
            if (TableViewport(block) is ScrollableControl viewport && !viewport.arrangedRect.Overlaps(rect))
                rect = LayoutRect.Empty;

            marker.Arrange(rect);
        }

        // Walks only the pages and blocks the clip touches, in child order.
        internal override int CollectChildren(float z)
        {
            LayoutRect clip = arrange.clip;
            float top = arrange.arranged.Shrink(arrange.padding).y + Inset;
            float from = clip.y - top;
            float to = clip.Bottom - top;
            int walked = 0;

            float stride = pageHeight + PageGap * zoom;
            int sheets = Math.Min(pageCount, pages.Count);
            for (int i = Math.Max(0, (int)(from / stride)); i < sheets && i * stride < to; i++)
                walked += UIEngine.Collect(pages[i], z);

            foreach (PanelControl box in highlights)
                walked += UIEngine.Collect(box, z);
            foreach (FloatingPicture view in behindViews)
                walked += UIEngine.Collect(view, z);
            if (header != null) walked += UIEngine.Collect(header, z);

            int low = 0;
            int high = blockControls.Count;
            while (low < high)
            {
                int mid = (low + high) / 2;
                if (blockTops[mid] + blockHeights[mid] <= from) low = mid + 1;
                else high = mid;
            }
            int end = low;
            for (; end < blockControls.Count && blockTops[end] < to; end++)
                walked += UIEngine.Collect(blockControls[end], z);
            foreach (BlockControl block in sliding)
            {
                int i = blockControls.IndexOf(block);
                if (i >= 0 && (i < low || i >= end)) walked += UIEngine.Collect(block, z);
            }
            for (int i = 0; i < insertControls.Count; i++)
                if (insertTops[i] < to && insertTops[i] + insertHeights[i] > from) walked += UIEngine.Collect(insertControls[i], z);

            foreach (FloatingPicture view in frontViews)
                walked += UIEngine.Collect(view, z);

            if (caret != null) walked += UIEngine.Collect(caret, z);
            if (dropCaret != null) walked += UIEngine.Collect(dropCaret, z);
            foreach (PanelControl line in pictureFrame)
                walked += UIEngine.Collect(line, z);
            if (pictureRotator != null) walked += UIEngine.Collect(pictureRotator, z);
            foreach (PictureHandle handle in pictureHandles)
                walked += UIEngine.Collect(handle, z);
            return walked;
        }
        #endregion
    }
}
