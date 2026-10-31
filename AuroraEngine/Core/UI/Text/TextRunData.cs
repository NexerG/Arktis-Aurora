using ArctisAurora.Core.Filing;
using ArctisAurora.Core.UI.Document;
using System.Runtime.InteropServices;

namespace ArctisAurora.Core.UI.Text
{
    // A run's string and the spans that style it.
    public sealed class TextRunData
    {
        public string text = string.Empty;
        public readonly List<StyleSpan> spans = new List<StyleSpan>();

        public int Length => (text ?? string.Empty).Length;

        // Inserts text at a character offset. A boundary belongs to the span after it, which is the
        // run the outgoing stack's caret normalization put a caret in.
        public void InsertText(int offset, string insert)
        {
            if (string.IsNullOrEmpty(insert)) return;

            int index = SpanForInsert(offset);
            if (!Typable(spans[index])) index = TextSpanBeside(index, offset);

            CollectionsMarshal.AsSpan(spans)[index].count += insert.Length;

            text = (text ?? string.Empty).Insert(offset, insert);
        }

        public void RemoveText(int offset, int count)
        {
            if (count <= 0) return;

            int end = offset + count;
            int start = 0;

            for (int i = 0; i < spans.Count; i++)
            {
                ref StyleSpan span = ref CollectionsMarshal.AsSpan(spans)[i];
                int spanEnd = start + span.count;

                int cut = Math.Min(spanEnd, end) - Math.Max(start, offset);
                if (cut > 0) span.count -= cut;
                start = spanEnd;
            }

            text = (text ?? string.Empty).Remove(offset, count);
            DropEmptySpans();
        }

        // Puts captured spans and their text back at an offset.
        public void InsertSpans(int offset, List<StyleSpan> add, string slice)
        {
            int index = SplitSpanAt(offset);
            spans.InsertRange(index, add);
            text = (text ?? string.Empty).Insert(offset, slice);

            DropEmptySpans();
            MergeSpans();
        }

        public void AppendSpans(List<StyleSpan> add, string slice)
        {
            spans.AddRange(add);
            text = (text ?? string.Empty) + slice;

            DropEmptySpans();
            MergeSpans();
        }

        // The style the character before an offset carries, which is what a caret there takes.
        public StyleSpan StyleAt(int offset)
        {
            int start = 0;
            foreach (StyleSpan span in spans)
            {
                int spanEnd = start + span.count;
                if (offset < spanEnd || spanEnd == Length) return span.AsText();
                start = spanEnd;
            }
            return spans[^1].AsText();
        }

        // Whether every span over a character range passes a test.
        public bool AllSpans(int start, int end, Func<StyleSpan, bool> test)
        {
            int at = 0;
            foreach (StyleSpan span in spans)
            {
                int spanEnd = at + span.count;
                if (spanEnd > start && at < end && !test(span)) return false;
                at = spanEnd;
            }
            return true;
        }

        // Restyles a character range: a boundary is cut at each end, every span between takes the
        // delta, and what the change made identical folds back together.
        public void StyleRange(int start, int end, StyleDelta delta)
        {
            if (end <= start) return;

            SplitSpanAt(end);
            int first = SplitSpanAt(start);

            int at = start;
            for (int i = first; i < spans.Count && at < end; i++)
            {
                ref StyleSpan span = ref CollectionsMarshal.AsSpan(spans)[i];
                int spanEnd = i == spans.Count - 1 ? Length : at + span.count;

                if (spanEnd <= end) delta.Apply(ref span);
                at = spanEnd;
            }

            MergeSpans();
        }

        private int SpanForInsert(int offset)
        {
            int start = 0;
            for (int i = 0; i < spans.Count; i++)
            {
                int spanEnd = start + spans[i].count;
                if (offset == start && i > 0) return i;
                if (offset < spanEnd) return i;
                start = spanEnd;
            }
            return spans.Count - 1;
        }

        // Gives the picture at an offset another's size, wrap and offset. False when none starts there.
        public bool SetPicture(int offset, StyleSpan picture)
        {
            int start = 0;
            for (int i = 0; i < spans.Count; i++)
            {
                if (offset == start && spans[i].IsPicture && spans[i].count > 0)
                {
                    ref StyleSpan span = ref CollectionsMarshal.AsSpan(spans)[i];
                    span.imageWidth = picture.imageWidth;
                    span.imageHeight = picture.imageHeight;
                    span.wrap = picture.wrap;
                    span.imageX = picture.imageX;
                    span.imageY = picture.imageY;
                    span.imageRotation = picture.imageRotation;
                    span.collision = picture.collision;
                    return true;
                }
                start += spans[i].count;
            }
            return false;
        }

        // False when no formula starts at the offset.
        public bool SetMath(int offset, string source)
        {
            int start = 0;
            for (int i = 0; i < spans.Count; i++)
            {
                if (offset == start && spans[i].IsMath && spans[i].count > 0)
                {
                    CollectionsMarshal.AsSpan(spans)[i].mathSource = source;
                    return true;
                }
                start += spans[i].count;
            }
            return false;
        }

        // The text span on the picture's side the offset touches, made empty in its style when there is none.
        private int TextSpanBeside(int picture, int offset)
        {
            int start = 0;
            for (int i = 0; i < picture; i++) start += spans[i].count;

            if (offset <= start)
            {
                if (picture > 0 && Typable(spans[picture - 1])) return picture - 1;
                spans.Insert(picture, ZeroText(spans[picture]));
                return picture;
            }

            if (picture + 1 < spans.Count && Typable(spans[picture + 1])) return picture + 1;
            spans.Insert(picture + 1, ZeroText(spans[picture]));
            return picture + 1;
        }

        // Text typed into it stays text: not a picture, formula, sheet link or spacer.
        private static bool Typable(StyleSpan span) => !span.IsObject && span.spaceWidth == 0f && !span.fill;

        private static StyleSpan ZeroText(StyleSpan picture)
        {
            StyleSpan text = picture.AsText();
            text.count = 0;
            return text;
        }

        // Makes a span boundary fall exactly on an offset and returns the index that starts there.
        // An offset already on one splits nothing, which is what keeps a repeated edit from shredding
        // a block into one span per character.
        public int SplitSpanAt(int offset)
        {
            int start = 0;
            for (int i = 0; i < spans.Count; i++)
            {
                ref StyleSpan span = ref CollectionsMarshal.AsSpan(spans)[i];
                int spanEnd = start + span.count;

                if (offset == start) return i;
                if (offset < spanEnd)
                {
                    StyleSpan right = span;
                    right.count = spanEnd - offset;
                    span.count = offset - start;

                    spans.Insert(i + 1, right);
                    return i + 1;
                }
                start = spanEnd;
            }
            return spans.Count;
        }

        // Folds neighbours nothing distinguishes back into one, so a document does not accumulate a
        // span boundary per edit.
        public void MergeSpans()
        {
            for (int i = spans.Count - 1; i > 0; i--)
                if (SameStyle(spans[i - 1], spans[i]))
                {
                    CollectionsMarshal.AsSpan(spans)[i - 1].count += spans[i].count;
                    spans.RemoveAt(i);
                }
        }

        private void DropEmptySpans()
        {
            for (int i = spans.Count - 1; i >= 0; i--)
                if (spans[i].count == 0 && spans.Count > 1) spans.RemoveAt(i);

            if (spans.Count == 1 && spans[0].count == 0 && spans[0].IsObject) spans[0] = spans[0].AsText();
        }

        private static bool SameStyle(StyleSpan a, StyleSpan b) =>
            !a.IsObject && !b.IsObject
            && a.style == b.style
            && a.colorHex == b.colorHex
            && a.gradient == b.gradient
            && a.effect == b.effect
            && a.fontName == b.fontName
            && a.variant == b.variant
            && a.fontSize == b.fontSize
            && a.strikethrough == b.strikethrough
            && a.underline == b.underline
            && a.highlightHex == b.highlightHex
            && a.stylingType == b.stylingType
            && a.fontSizeAuthored == b.fontSizeAuthored
            && a.spaceWidth == b.spaceWidth
            && a.fill == b.fill
            && a.leader == b.leader && a.leaderWidth == b.leaderWidth && a.leaderInset == b.leaderInset
            && a.note == b.note
            && a.urlStretch == b.urlStretch && a.urlBreakPenalty == b.urlBreakPenalty && a.urlBigBreakPenalty == b.urlBigBreakPenalty
            && a.urlWidth == b.urlWidth && a.urlShrink == b.urlShrink
            && a.urlNumPenalty == b.urlNumPenalty && a.urlNumWidth == b.urlNumWidth && a.urlNumStretch == b.urlNumStretch && a.urlNumShrink == b.urlNumShrink
            && a.urlUcPenalty == b.urlUcPenalty && a.urlUcWidth == b.urlUcWidth && a.urlUcStretch == b.urlUcStretch && a.urlUcShrink == b.urlUcShrink
            && a.urlLcPenalty == b.urlLcPenalty && a.urlLcWidth == b.urlLcWidth && a.urlLcStretch == b.urlLcStretch && a.urlLcShrink == b.urlLcShrink;

        // Save: the spans cut back into runs. The last span absorbs whatever is left of the string,
        // so its count is read off the text rather than trusted.
        public List<Run> Runs()
        {
            string whole = text ?? string.Empty;
            List<Run> runs = new List<Run>(spans.Count);
            int start = 0;

            for (int i = 0; i < spans.Count; i++)
            {
                int count = i == spans.Count - 1
                    ? whole.Length - start
                    : Math.Clamp(spans[i].count, 0, whole.Length - start);
                if (count < 0) count = 0;

                StyleSpan span = spans[i];
                runs.Add(new Run
                {
                    text = span.IsObject ? string.Empty : whole.Substring(start, count),
                    image = span.imageSource,
                    math = span.mathSource,
                    display = span.mathDisplay,
                    strut = span.mathStrut,
                    sheet = span.sheetRef,
                    space = span.spaceWidth,
                    fill = span.fill,
                    leader = span.leader,
                    leaderWidth = span.leaderWidth,
                    leaderInset = span.leaderInset,
                    note = span.note,
                    urlStretch = span.urlStretch,
                    urlWidth = span.urlWidth,
                    urlShrink = span.urlShrink,
                    urlBreakPenalty = span.urlBreakPenalty,
                    urlBigBreakPenalty = span.urlBigBreakPenalty,
                    urlNumPenalty = span.urlNumPenalty, urlNumWidth = span.urlNumWidth, urlNumStretch = span.urlNumStretch, urlNumShrink = span.urlNumShrink,
                    urlUcPenalty = span.urlUcPenalty, urlUcWidth = span.urlUcWidth, urlUcStretch = span.urlUcStretch, urlUcShrink = span.urlUcShrink,
                    urlLcPenalty = span.urlLcPenalty, urlLcWidth = span.urlLcWidth, urlLcStretch = span.urlLcStretch, urlLcShrink = span.urlLcShrink,
                    width = span.imageWidth,
                    height = span.imageHeight,
                    wrap = span.wrap,
                    x = span.imageX,
                    y = span.imageY,
                    rotation = span.imageRotation,
                    collision = span.collision,
                    bold = span.style == FontStyle.Bold || span.style == FontStyle.BoldItalic,
                    italic = span.style == FontStyle.Italic || span.style == FontStyle.BoldItalic,
                    strikethrough = span.strikethrough,
                    underline = span.underline,
                    highlightHex = span.highlightHex,
                    colorHex = span.colorHex,
                    gradient = span.gradient,
                    effect = span.effect,
                    fontName = span.fontName,
                    variant = span.variant,
                    fontSize = span.fontSizeAuthored ? span.fontSize : 0,
                    fontSizeAuthored = span.fontSizeAuthored,
                    stylingType = span.stylingType
                });
                start += count;
            }

            return runs;
        }
    }
}
