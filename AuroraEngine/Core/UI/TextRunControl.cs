using ArctisAurora.Core.Data;
using ArctisAurora.Core.Diagnostics;
using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Registry.Assets;
using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Threading;
using ArctisAurora.EngineWork.Registry;
using System.Numerics;

namespace ArctisAurora.Core.UI
{
    // How text flows around a picture in a note.
    [A_XSDType("PictureWrap", "UI")]
    public enum PictureWrap
    {
        Inline,
        Square,
        Tight,
        TopAndBottom,
        Behind,
        InFront
    }

    // What a turned Square picture pushes text away from.
    [A_XSDType("PictureCollision", "UI")]
    public enum PictureCollision
    {
        Box,
        Shape
    }

    // One styled slice of a run's string. Spans tile the string in order and the last one absorbs
    // whatever is left, so an edit never has to re-cut the list.
    public struct StyleSpan
    {
        public int count;
        public FontStyle style;
        public string colorHex;

        // per-span overrides; null or 0 takes the run's own; a variant the font lacks draws its plain face
        public string fontName;
        public string variant;
        public float fontSize;
        public string gradient;
        public string effect;

        // decorations; a null highlight draws nothing behind the text
        public bool strikethrough;
        public bool underline;
        public string highlightHex;

        // where an unauthored size comes from, kept so a note saves back as it was written
        public TextStyleType stylingType;
        public bool fontSizeAuthored;

        // a picture: one U+FFFC drawn from this file; a 0 size takes the picture's own
        public string imageSource;
        public float imageWidth;
        public float imageHeight;

        // a floating picture's wrap and its offset from the column's left and its paragraph's top
        public PictureWrap wrap;
        public float imageX;
        public float imageY;

        // a picture's turn in clockwise degrees, and what a turned Square float wraps
        public float imageRotation;
        public PictureCollision collision;

        // a formula: one U+FFFC drawn from this TeX source, and its array rows' strut in em (0 is 1.2)
        public string mathSource;
        public bool mathDisplay;
        public float mathStrut;

        // a sheet link: one U+FFFC showing a cell's value or a range's grid
        public string sheetRef;

        // a spacer: each character advances this far and draws nothing; 0 for text
        public float spaceWidth;

        // a fill: takes its line's slack and draws nothing
        public bool fill;

        // a fill's leaders: this text in cells leaderWidth apart from the line box's left edge, leaderInset into each; null draws nothing
        public string leader;
        public float leaderWidth;
        public float leaderInset;

        // a footnote anchor: the id of the footnote its page makes room for
        public string note;

        // a biblatex URL: stretch between its characters, px, and the penalties at its two break markers
        public float urlStretch;
        public float urlBreakPenalty;
        public float urlBigBreakPenalty;

        public Quaternion Rotation => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, imageRotation * MathF.PI / 180f);
        public bool IsFloating => IsPicture && wrap != PictureWrap.Inline;
        public bool IsBold => style == FontStyle.Bold || style == FontStyle.BoldItalic;
        public bool IsItalic => style == FontStyle.Italic || style == FontStyle.BoldItalic;
        public bool IsPicture => imageSource != null;
        public bool IsMath => mathSource != null;
        public bool IsSheet => sheetRef != null;
        public bool IsObject => IsPicture || IsMath || IsSheet;

        // The same style with the picture or formula taken off.
        public StyleSpan AsText()
        {
            StyleSpan text = this;
            text.sheetRef = null;
            text.spaceWidth = 0f;
            text.fill = false;
            text.leader = null;
            text.note = null;
            text.mathSource = null;
            text.mathDisplay = false;
            text.mathStrut = 0f;
            text.imageSource = null;
            text.imageWidth = 0f;
            text.imageHeight = 0f;
            text.wrap = PictureWrap.Inline;
            text.imageX = 0f;
            text.imageY = 0f;
            text.imageRotation = 0f;
            text.collision = PictureCollision.Box;
            return text;
        }
    }

    // What a run tells when a glyph is pressed.
    public interface IGlyphPressTarget
    {
        void GlyphPressed(TextRunControl run, int index);
        void PicturePressed(TextRunControl run, int index, int button);
    }

    // A block of text as one control: the string, the spans styling it, and one emitted quad per
    // visible character. Glyphs are quads and not controls, so the hit-test lands on the run and
    // IndexAt resolves which character was under the point.
    [A_XSDType("TextRun", "UI", isAbstract: true)]
    public class TextRunControl : Control
    {
        private static FontAssetGlyphMetrics metrics = null!;

        private FontAsset _fontAsset;
        private BlockLayout _layout;
        private float _wrapWidth;

        // one entry per span, rebuilt each measure; LineSegment.runIndex indexes all of them
        private List<TextMeasurer.Run> _runs = null!;
        private RunStyle[] runStyles = Array.Empty<RunStyle>();
        private RunObject[]? runObjects;
        private int runCount;

        private struct RunStyle
        {
            public FontAsset font;
            public uint paint;
            public uint gradient;
            public uint effect;
            public uint highlight;
            public bool underline;
            public bool strike;
        }

        // a picture, sheet or formula run's object
        private struct RunObject
        {
            public TextureAsset? texture;
            public MathBox? math;
            public SheetBox? sheet;
            public Vector2 size;
            public Quaternion rotation;
        }

        // formulas, laid out once per source; the font they draw from
        private static readonly Dictionary<(string source, bool display, FontAsset font, float strut, MathRules rules), MathBox> mathBoxes = new();

        // width-aware displays at this build's widths and the last build's
        private Dictionary<(string, FontAsset, float, float), MathBox>? _fitted;
        private Dictionary<(string, FontAsset, float, float), MathBox>? _fittedBefore;
        private const string MathFont = "math";

        // characters under a selection, which a highlight leaves a gap for; -1 when none
        internal int selectedFrom = -1;
        internal int selectedTo = -1;

        // a code colour per character, set by the document; null draws every character in its run's paint
        internal SyntaxToken[]? syntax;

        // decoration geometry, as fractions of the font size
        private const float underlineDrop = 0.12f;
        private const float strikeRise = 0.28f;
        private const float decorationWeight = 0.07f;

        // where the first line's pen starts, in design space
        private Vector2 _origin;

        private uint _paint = Palettes.Inline(Vector3.One);

        // authored text
        public readonly TextRunData data;
        public List<StyleSpan> spans => data.spans;
        public FontStyle style = FontStyle.Regular;
        public float lineHeight = 1.5f;
        // every line's descent, px; 0 centres the font in its line box; TeX's \lineskip between fixed-depth lines, px
        public float baselineDepth;
        public float lineSkip;
        // TeX's \strut on every fixed-depth line
        public bool strut;
        // TeX's line-breaking passes for optimal breaks; emergency stretch in px
        public float tolerance = 200f;
        public float pretolerance = -1f;
        public float emergencyStretch;
        // glyph widths truncated to 1/advanceGrid em; 0 keeps them exact
        public int advanceGrid;
        // what places the formulas' scripts and limits
        public MathRules mathRules;

        // multiplies every font size at layout; the authored sizes are untouched
        internal float textZoom = 1f;

        protected virtual bool Wraps => true;

        public TextRunControl() : this(new TextRunData()) { }

        // A run laid out over string and spans that live elsewhere.
        protected TextRunControl(TextRunData data)
        {
            this.data = data;
            _fontAsset = ResolveFont(fontName);
            role = PaletteRole.Ink;
            kind = VulkanControlType.MTSDFControl;
        }

        [A_XSDElementProperty("Text", "UI", "The string this run lays out.")]
        public string text
        {
            get => data.text;
            set
            {
                if (data.text == value) return;
                data.text = value;
                InvalidateLayout();
            }
        }

        // Re-measures when an edit through data changed the string.
        protected void Edited(string before)
        {
            if (data.text != before) InvalidateLayout();
        }

        [A_XSDElementProperty("FontSize", "UI", "Type size in design-space pixels.")]
        public float fontSize
        {
            get => field;
            set
            {
                if (field == value) return;
                field = value;
                InvalidateLayout();
            }
        } = 16;

        [A_XSDElementProperty("FontName", "UI", "Font family, as named in the asset manifest.")]
        public string fontName
        {
            get => field;
            set
            {
                if (field == value) return;
                field = value;
                _fontAsset = ResolveFont(value);
                InvalidateLayout();
            }
        } = "default";

        // Colour and alpha reach the glyphs, never the run's own quad, which is never emitted.
        public override string colorHex
        {
            get => base.colorHex;
            set
            {
                base.colorHex = value;
                InvalidateLayout();
            }
        }

        // Repaints the runs that take the run's own colour, without a re-measure.
        protected override void SetPaint(uint paint)
        {
            base.SetPaint(paint);
            if (_paint == paint) return;
            _paint = paint;
            if (isMeasureDirty || runCount != Math.Max(1, spans.Count)) return;

            Span<RunStyle> styles = runStyles.AsSpan(0, runCount);
            for (int i = 0; i < styles.Length; i++)
                if (spans.Count == 0 || spans[i].colorHex == null)
                    styles[i].paint = paint;
        }

        // Replaces the span list and re-measures.
        public void SetSpans(params StyleSpan[] value)
        {
            spans.Clear();
            spans.AddRange(value);
            InvalidateLayout();
        }

        // Resolves a font name, falling back to default.
        private static FontAsset ResolveFont(string name)
        {
            Dictionary<string, FontAsset> fonts =
                AssetRegistries.GetRegistryByValueType<string, FontAsset>(typeof(FontAsset));

            return fonts.TryGetValue(name ?? "default", out FontAsset named) ? named : fonts["default"];
        }

        #region ---- layout ----
        // Spans in order over the shared string, the last one taking whatever is left. An empty span
        // still takes a slot, so runIndex keeps indexing the span list.
        private void BuildRuns(float wrapWidth)
        {
            runCount = Math.Max(1, spans.Count);
            _runs ??= new List<TextMeasurer.Run>(runCount);
            _runs.Clear();
            if (runStyles.Length < runCount) runStyles = new RunStyle[runCount];
            if (runObjects != null)
                if (runObjects.Length < runCount) runObjects = null;
                else Array.Clear(runObjects, 0, runCount);
            (_fitted, _fittedBefore) = (_fittedBefore, _fitted);
            _fitted?.Clear();

            RunStyle[] styles = runStyles;
            string s = text ?? string.Empty;
            if (spans.Count == 0)
            {
                _runs.Add(new TextMeasurer.Run(s, 0, s.Length, fontName, _fontAsset.atlasMetaData, Zoomed(fontSize), style, grid: advanceGrid));
                styles[0] = new RunStyle { font = _fontAsset, paint = _paint, gradient = gradientId, effect = visual.effect };
                return;
            }

            int start = 0;
            for (int i = 0; i < spans.Count; i++)
            {
                int count = i == spans.Count - 1
                    ? s.Length - start
                    : Math.Clamp(spans[i].count, 0, s.Length - start);
                if (count < 0) count = 0;

                string spanFont = FontFor(spans[i]);
                float spanSize = spans[i].fontSize > 0 ? spans[i].fontSize : fontSize;
                FontAsset font = spanFont == fontName ? _fontAsset : ResolveFont(spanFont);

                if (spans[i].IsPicture)
                {
                    TextureAsset? picture = TextureAsset.ForFile(spans[i].imageSource);
                    (float w, float h) = PictureSize(spans[i], picture, wrapWidth);
                    Quaternion rotation = spans[i].Rotation;
                    Vector2 box = spans[i].IsFloating ? new Vector2(w, h) : new LayoutRect(0f, 0f, w, h).Turned(rotation).size;
                    _runs.Add(new TextMeasurer.Run(s, start, count, spanFont, font.atlasMetaData, Zoomed(spanSize), spans[i].style, true, box.X, box.Y,
                        spans[i].IsFloating));
                    ObjectSlots()[i] = new RunObject { texture = picture, size = new Vector2(w, h), rotation = rotation };
                }
                else if (spans[i].IsSheet)
                {
                    float size = Zoomed(spanSize);
                    TextMeasurer.Run plain = new TextMeasurer.Run(s, start, count, spanFont, font.atlasMetaData, size, spans[i].style);
                    SheetBox box = SheetLinks.Layout(NotePath(), spans[i].sheetRef, plain, metrics.GetLineMetrics(spanFont), textZoom);
                    float w = box.range && float.IsFinite(wrapWidth) && wrapWidth < float.MaxValue ? wrapWidth : box.width;
                    _runs.Add(new TextMeasurer.Run(s, start, count, spanFont, font.atlasMetaData, size, spans[i].style, false,
                        w, box.height, false, true));
                    ObjectSlots()[i].sheet = box;
                }
                else if (spans[i].IsMath)
                {
                    FontAsset mathFont = font.mathConstants != null ? font : ResolveFont(MathFont);
                    MathBox box = MathBoxFor(spans[i], mathFont);
                    float size = Zoomed(spanSize);
                    if (box.widthAware && float.IsFinite(wrapWidth) && wrapWidth < float.MaxValue)
                        box = FittedBox(spans[i], mathFont, wrapWidth / size);
                    float w = box.width * size;
                    if (spans[i].mathDisplay && float.IsFinite(wrapWidth) && wrapWidth < float.MaxValue)
                        w = MathF.Max(w, wrapWidth);
                    _runs.Add(new TextMeasurer.Run(s, start, count, spanFont, font.atlasMetaData, Zoomed(spanSize), spans[i].style, false,
                        w, box.height * size, false, true, box.depth * size));
                    ObjectSlots()[i].math = box;
                    font = mathFont;
                }
                else
                    _runs.Add(new TextMeasurer.Run(s, start, count, spanFont, font.atlasMetaData, Zoomed(spanSize), spans[i].style,
                        space: spans[i].spaceWidth * textZoom, fill: spans[i].fill, grid: advanceGrid,
                        urlStretch: spans[i].urlStretch * textZoom, urlBreakPenalty: spans[i].urlBreakPenalty, urlBigBreakPenalty: spans[i].urlBigBreakPenalty,
                        variant: font.atlasMetaData.VariantOf(spans[i].variant)));
                styles[i] = new RunStyle
                {
                    font = font,
                    paint = spans[i].colorHex == null ? _paint : Palettes.Inline(spans[i].colorHex),
                    gradient = spans[i].gradient == null ? gradientId : Gradients.IndexOf(spans[i].gradient),
                    effect = spans[i].effect == null ? visual.effect : Effects.IndexOf(spans[i].effect),
                    highlight = spans[i].highlightHex == null ? 0u : Palettes.Inline(spans[i].highlightHex),
                    underline = spans[i].underline,
                    strike = spans[i].strikethrough
                };
                start += count;
            }
        }

        // This build's object slots, made on its first picture, sheet or formula.
        private RunObject[] ObjectSlots() => runObjects ??= new RunObject[runCount];

        // A span's formula laid out in em; an error box when there is no math font.
        private MathBox MathBoxFor(in StyleSpan span, FontAsset font)
        {
            string source = SheetLinks.HasMathLinks(span.mathSource) ? SheetLinks.ExpandMath(NotePath(), span.mathSource) : span.mathSource;
            if (mathBoxes.TryGetValue((source, span.mathDisplay, font, span.mathStrut, mathRules), out MathBox cached)) return cached;

            MathBox box = font.mathConstants == null
                ? new MathBox { error = true }
                : MathLayout.Layout(MathParser.Parse(source), span.mathDisplay, font.atlasMetaData, font.mathConstants, strut: span.mathStrut, rules: mathRules);
            mathBoxes[(source, span.mathDisplay, font, span.mathStrut, mathRules)] = box;
            return box;
        }

        // A width-aware display laid out at the line width, em; kept while the next build asks for the same width.
        private MathBox FittedBox(in StyleSpan span, FontAsset font, float lineWidth)
        {
            string source = SheetLinks.HasMathLinks(span.mathSource) ? SheetLinks.ExpandMath(NotePath(), span.mathSource) : span.mathSource;
            (string, FontAsset, float, float) key = (source, font, lineWidth, span.mathStrut);
            _fitted ??= new Dictionary<(string, FontAsset, float, float), MathBox>();
            if ((_fittedBefore == null || !_fittedBefore.TryGetValue(key, out MathBox? box)) && !_fitted.TryGetValue(key, out box))
                box = MathLayout.Layout(MathParser.Parse(source), true, font.atlasMetaData, font.mathConstants!, lineWidth, span.mathStrut, mathRules);
            _fitted[key] = box;
            return box;
        }

        // The file of the note this run is drawn in; null outside one.
        private string? NotePath()
        {
            for (Entity? current = parent; current != null; current = current.parent)
                if (current is DocumentControl document) return document.session?.path;
            return null;
        }

        // A formula's left edge inside its run's advance: centred, kept a quad clear of its tag.
        private static float MathLeft(in TextMeasurer.Run run, MathBox math)
        {
            float width = math.width * run.fontSize;
            float left = (run.imageWidth - width) * 0.5f;
            if (math.tag == null) return left;
            return MathF.Max(0f, MathF.Min(left, run.imageWidth - (math.tag.width + 1f) * run.fontSize - width));
        }

        // The authored size at the picture's aspect, or its own size capped to the column; zoomed.
        private (float width, float height) PictureSize(StyleSpan span, TextureAsset? picture, float wrapWidth)
        {
            Vector2 native = picture != null ? new Vector2(picture.image.Width, picture.image.Height) : Vector2.Zero;
            float aspect = native.X > 0f ? native.Y / native.X : 0f;
            float w = span.imageWidth;
            float h = span.imageHeight;

            if (w <= 0f && h <= 0f)
            {
                w = MathF.Min(native.X * textZoom, wrapWidth);
                return (w, w * aspect);
            }

            if (w <= 0f) w = aspect > 0f ? h / aspect : 0f;
            else if (h <= 0f) h = w * aspect;
            return (w * textZoom, h * textZoom);
        }

        private float Zoomed(float size) => size * textZoom;

        protected virtual string FontFor(in StyleSpan span) => span.fontName ?? fontName;

        protected virtual TextAlignment Alignment => TextAlignment.Left;

        protected virtual bool OptimalBreaks => false;

        protected virtual bool WidowControl => false;

        // How far a line may hang below the text bottom: its descent, so a line fits while its baseline does.
        internal float Overhang => baselineDepth * textZoom;

        internal float LineSkip => lineSkip * textZoom;

        protected virtual float FirstLineIndent => 0f;

        // how far the last line may run past the wrap width, px at this zoom
        protected virtual float LastLineExtra => 0f;

        // Shifts each line across its room by the alignment, trailing spaces left hanging.
        private void Align()
        {
            if (_layout == null) return;
            Fill();
            if (Alignment == TextAlignment.Justify)
            {
                Justify();
                return;
            }
            if (OptimalBreaks)
                foreach (TextLine line in _layout.lines)
                    Spread(line, false);

            float factor = Alignment switch { TextAlignment.Center => 0.5f, TextAlignment.Right => 1f, _ => 0f };
            if (factor == 0f) return;

            string s = text ?? string.Empty;
            foreach (TextLine line in _layout.lines)
            {
                float room = line.room > 0f ? line.room : _wrapWidth;
                if (room == float.MaxValue || line.fillExtra != 0f) continue;

                float pen = 0f, visible = 0f;
                foreach (LineSegment segment in line.segments)
                    for (int k = segment.charStart; k < segment.charStart + segment.charCount && k < s.Length; k++)
                    {
                        pen += TextMeasurer.AdvanceAt(_runs[segment.runIndex], k, pen);
                        if (s[k] != '\t' && (s[k] == TextMeasurer.NoBreakSpace || TextMeasurer.SpaceFactor(s[k]) == 0f)) visible = pen;
                    }

                line.left += MathF.Max(0f, room - visible - line.hyphen) * factor;
            }

            _layout.width = 0f;
            foreach (TextLine line in _layout.lines)
                _layout.width = MathF.Max(_layout.width, line.left + line.width);
        }

        // Spreads every line but the last and one before a display across its room; those only shrink.
        private void Justify()
        {
            for (int i = 0; i < _layout.lines.Count; i++)
                Spread(_layout.lines[i], i < _layout.lines.Count - 1 && !BeforeDisplay(i));
        }

        // Fits a line to its room through its inner spaces: wider only when stretch, tighter where Knuth-Plass set it over;
        // a line holding a tab or a fill stays as it is.
        private void Spread(TextLine line, bool stretch)
        {
            string s = text ?? string.Empty;
            float room = line.room > 0f ? line.room : _wrapWidth;
            if (room == float.MaxValue || line.segments.Count == 0 || line.fillExtra != 0f) return;

            float pen = 0f, visible = 0f, shrinkable = 0f, inner = 0f, stretchWeight = 0f, shrinkWeight = 0f, innerStretch = 0f, innerShrink = 0f;
            int end = 0;
            bool tab = false;
            foreach (LineSegment segment in line.segments)
                for (int k = segment.charStart; k < segment.charStart + segment.charCount && k < s.Length; k++)
                {
                    TextMeasurer.Run run = _runs[segment.runIndex];
                    float advance = TextMeasurer.AdvanceAt(run, k, pen);
                    float factor = TextMeasurer.SpaceFactor(s[k]);
                    pen += advance;
                    if (s[k] == '\t') tab = true;
                    else if (factor > 0f && (s[k] != TextMeasurer.NoBreakSpace || run.space == 0f))
                    {
                        float glue = factor >= 2f ? advance * 0.75f : advance;
                        shrinkable += glue / factor;
                        stretchWeight += glue * TextMeasurer.GlueStretch * factor;
                        shrinkWeight += glue * TextMeasurer.GlueShrink / factor;
                    }
                    else
                    {
                        stretchWeight += UrlStretch(run, k);
                        visible = pen;
                        end = k + 1;
                        inner = shrinkable;
                        innerStretch = stretchWeight;
                        innerShrink = shrinkWeight;
                    }
                }

            visible += line.hyphen;
            if (tab || innerStretch == 0f || visible == room || visible < room && !stretch) return;
            if (visible > room && (!OptimalBreaks || visible - room > inner * TextMeasurer.GlueShrink + 0.01f)) return;

            line.spaceExtra = (room - visible) / (visible < room ? innerStretch : innerShrink);
            line.justifyEnd = end;
            line.width += room - visible;
        }

        // Gives each line's slack to the fill characters on it, shared evenly.
        private void Fill()
        {
            string s = text ?? string.Empty;
            foreach (TextLine line in _layout.lines)
            {
                float room = line.room > 0f ? line.room : _wrapWidth;
                if (room == float.MaxValue) continue;

                float pen = 0f, visible = 0f;
                int fills = 0;
                foreach (LineSegment segment in line.segments)
                    for (int k = segment.charStart; k < segment.charStart + segment.charCount && k < s.Length; k++)
                    {
                        if (segment.runIndex < spans.Count && spans[segment.runIndex].fill) fills++;
                        pen += TextMeasurer.AdvanceAt(_runs[segment.runIndex], k, pen);
                        if (s[k] != '\t' && (s[k] == TextMeasurer.NoBreakSpace || TextMeasurer.SpaceFactor(s[k]) == 0f)) visible = pen;
                    }

                float slack = room - visible - line.hyphen;
                if (fills == 0 || slack <= 0f) continue;
                line.fillExtra = slack / fills;
                line.width = MathF.Max(line.width, room);
            }
        }

        // Whether the line after this one opens with a display formula.
        private bool BeforeDisplay(int line) =>
            _layout.lines[line + 1].segments is { Count: > 0 } next && next[0].runIndex < spans.Count && spans[next[0].runIndex].mathDisplay;

        // The justify stretch a character takes after its own advance.
        private float Stretch(TextLine line, int index, in TextMeasurer.Run run)
        {
            if (line.fillExtra != 0f && FillAt(index)) return line.fillExtra;
            if (line.spaceExtra == 0f || index >= line.justifyEnd || run.space != 0f) return 0f;
            float factor = TextMeasurer.SpaceFactor(text[index]);
            if (factor == 0f) return line.spaceExtra > 0f ? line.spaceExtra * UrlStretch(run, index) : 0f;
            float glue = TextMeasurer.AdvanceAt(run, index, 0f) * (factor >= 2f ? 0.75f : 1f);
            return line.spaceExtra > 0f ? line.spaceExtra * glue * TextMeasurer.GlueStretch * factor : line.spaceExtra * glue * TextMeasurer.GlueShrink / factor;
        }

        // The \Urlmuskip stretch after a URL character, px: none after a break marker or the run's last character.
        private float UrlStretch(in TextMeasurer.Run run, int index) =>
            run.urlStretch > 0f && index + 1 < run.charStart + run.charCount && !TextMeasurer.IsZeroWidthBreak(text[index]) ? run.urlStretch : 0f;

        // Whether character index belongs to a fill span.
        private bool FillAt(int index)
        {
            int start = 0;
            foreach (StyleSpan span in spans)
            {
                if (index < start + span.count) return span.fill;
                start += span.count;
            }
            return false;
        }

        // A segment's width with the justify stretch of its spaces.
        private float Stretched(TextLine line, LineSegment segment)
        {
            if (line.spaceExtra == 0f && line.fillExtra == 0f) return segment.width;

            float width = segment.width;
            TextMeasurer.Run run = _runs[segment.runIndex];
            for (int k = segment.charStart; k < segment.charStart + segment.charCount; k++)
                width += Stretch(line, k, run);
            return width;
        }

        protected override Vector2 MeasureCore(Vector2 availableSize)
        {
            metrics ??= new FontAssetGlyphMetrics();

            ref ArrangeData a = ref arrange;
            float contentWidth = a.preferredWidth > 0 ? a.preferredWidth : availableSize.X;
            float wrapWidth = Wraps ? contentWidth : float.MaxValue;

            if (!isMeasureDirty && _layout != null && wrapWidth == _wrapWidth) return a.desired;

            Profiling.Zone.Start("Text.BuildRuns");
            BuildRuns(wrapWidth);
            Profiling.Zone.End("Text.BuildRuns");

            Profiling.Zone.Start("Text.MeasureBlock");
            _layout = TextMeasurer.MeasureBlock(_runs, wrapWidth, metrics, lineHeight, FirstLineIndent, reuse: _layout, optimal: OptimalBreaks,
                                                ragged: Alignment != TextAlignment.Justify, lastExtra: LastLineExtra, baselineDepth: baselineDepth * textZoom,
                                                lineSkip: LineSkip, strut: strut, tolerance: tolerance, pretolerance: pretolerance, emergencyStretch: emergencyStretch * textZoom);
            laidAround = false;
            Profiling.Zone.End("Text.MeasureBlock");
            _wrapWidth = wrapWidth;
            Align();

            float w = a.preferredWidth > 0 ? a.preferredWidth : _layout.width;
            float h = a.preferredHeight > 0 ? a.preferredHeight : _layout.height;

            a.desired = new Vector2(w, h);
            SetFlag(ArrangeFlags.MeasureDirty, false);
            return a.desired;
        }

        // Re-lays the measured runs line by line through the slots; null lays them plainly again.
        internal float LayoutAround(ILineSlots? slots)
        {
            if (_layout == null) return 0f;

            _layout = TextMeasurer.MeasureBlock(_runs, _wrapWidth, metrics, lineHeight, FirstLineIndent, slots, _layout, lastExtra: LastLineExtra,
                                                baselineDepth: baselineDepth * textZoom, lineSkip: LineSkip, strut: strut);
            laidAround = slots != null;
            Align();
            return _layout.height;
        }

        // the lines were last laid around something rather than plainly
        internal bool laidAround;

        // A picture's drawn size at this run's zoom.
        internal Vector2 PictureSizeAt(int index)
        {
            if (_runs == null) return Vector2.Zero;
            for (int i = 0; i < _runs.Count; i++)
                if (_runs[i].picture && index >= _runs[i].charStart && index < _runs[i].charStart + _runs[i].charCount)
                    return runObjects![i].size;
            return Vector2.Zero;
        }

        // Places the text block. The glyphs are cut at emit, against the clip of the moment.
        protected override void ArrangeCore(LayoutRect finalRect)
        {
            WriteArranged(finalRect);
            SetFlag(ArrangeFlags.ArrangeDirty, false);
            if (_layout == null || _fontAsset == null) return;

            ref ArrangeData a = ref arrange;
            LayoutRect inner = finalRect.Shrink(a.padding);

            // text position inside an authored box, the same handshake the outgoing stack has
            float slackX = a.preferredWidth > 0
                ? MathF.Max(0f, inner.width - _layout.width) * a.horizontalPosition : 0f;
            float slackY = a.preferredHeight > 0
                ? MathF.Max(0f, inner.height - _layout.height) * a.verticalPosition : 0f;
            _origin = new Vector2(inner.x + slackX, inner.y + slackY);
        }

        // The run's own box is not ink, so only glyphs land. A line outside the clip is skipped
        // whole — the pen restarts per line, so dropping one costs the next nothing.
        internal override void Emit(float z)
        {
            if (_layout == null || _fontAsset == null) return;

            LayoutRect box = arrange.clip;
            LayoutRect arranged = arrange.arranged;
            z += depthStep;
            Vector4 clip = new Vector4(box.x, box.y, box.Right, box.Bottom);
            Vector4 gradientRect = new Vector4(arranged.x, arranged.y, arranged.Right, arranged.Bottom);
            DataPool quads = UIEngine.Quads;
            string s = text ?? string.Empty;
            float started = visual.effectStart;
            float alpha = this.alpha;

            foreach (TextLine line in _layout.lines)
            {
                float lineTop = _origin.Y + line.top;
                if (lineTop + line.height <= box.y) continue;
                if (lineTop >= box.Bottom) break;

                float baselineY = _origin.Y + line.baseline;
                float pen = _origin.X + line.left;
                float lineStart = pen;

                foreach (LineSegment segment in line.segments)
                {
                    ref readonly RunStyle runStyle = ref runStyles[segment.runIndex];
                    uint paint = runStyle.gradient != 0
                        ? Gradients.Word(runStyle.gradient, palette ?? Palettes.Default)
                        : runStyle.paint;
                    TextMeasurer.Run run = _runs[segment.runIndex];
                    FontAsset font = runStyle.font;
                    uint effect = runStyle.effect;
                    float stagger = Effects.Stagger(effect);
                    bool underline = runStyle.underline, strike = runStyle.strike;
                    uint highlight = runStyle.highlight;
                    float segmentX = pen;

                    if (run.picture)
                    {
                        for (int k = 0; k < segment.charCount && !run.floating; k++)
                        {
                            ref readonly RunObject picture = ref runObjects![segment.runIndex];
                            WriteImage(quads, picture.texture, spans[segment.runIndex].imageSource,
                                       new LayoutRect(pen, baselineY - run.imageHeight, run.imageWidth, run.imageHeight),
                                       picture.size, picture.rotation, alpha, z, clip);
                            pen += run.imageWidth;
                        }
                        continue;
                    }

                    if (highlight != 0)
                        WriteHighlight(quads, segment, Stretched(line, segment), segmentX, lineTop, line.height, highlight, alpha, z - depthStep, clip, gradientRect);

                    if (runObjects?[segment.runIndex].sheet is SheetBox sheet)
                    {
                        for (int k = 0; k < segment.charCount; k++)
                        {
                            WriteSheet(quads, sheet, run, font, paint, alpha, pen, baselineY, z, clip, gradientRect);
                            pen += run.imageWidth;
                        }
                        continue;
                    }

                    if (run.math)
                    {
                        MathBox math = runObjects![segment.runIndex].math!;
                        uint ink = math.error ? Palettes.Surface(palette ?? Palettes.Default, PaletteRole.Danger) : paint;
                        float size = run.fontSize;
                        for (int k = 0; k < segment.charCount; k++)
                        {
                            float x = pen + MathLeft(run, math);
                            foreach (MathGlyph g in math.glyphs)
                                WriteGlyph(quads, g.ch, font.atlasMetaData.Effective(0, g.face), ink, alpha, font, size * g.scale, 0u, 0f,
                                           x + g.x * size, baselineY - g.y * size, z, clip, gradientRect);
                            foreach (MathRule r in math.rules)
                                WriteRect(quads, x + r.x * size, MathF.Round(baselineY - r.y * size), MathF.Max(1f, r.width * size),
                                          MathF.Max(1f, MathF.Round(r.height * size)), ink, alpha, z, clip, gradientRect);
                            if (math.tag != null)
                                foreach (MathGlyph g in math.tag.glyphs)
                                    WriteGlyph(quads, g.ch, font.atlasMetaData.Effective(0, g.face), ink, alpha, font, size * g.scale, 0u, 0f,
                                               pen + run.imageWidth + g.x * size, baselineY - g.y * size, z, clip, gradientRect);
                            pen += run.imageWidth;
                        }
                        continue;
                    }

                    SyntaxToken[]? tokens = runStyle.gradient == 0 && (spans.Count == 0 || spans[segment.runIndex].colorHex == null) ? syntax : null;
                    for (int k = 0; k < segment.charCount; k++)
                    {
                        int index = segment.charStart + k;
                        if (index >= s.Length) break;

                        float effectStart = started + (index - run.charStart) * stagger;
                        if (run.space != 0f || run.fill)
                        {
                            UIEngine.recorder?.Glyph(font, run.face, ' ', run.fontSize, pen, baselineY, paint, clip);
                            float advance = run.fill ? Stretch(line, index, run) : run.space;
                            if (run.fill && spans[segment.runIndex].leader != null)
                                WriteLeaders(quads, spans[segment.runIndex], run, font, paint, alpha, pen, pen + advance, baselineY, z, clip, gradientRect);
                            pen += advance;
                            continue;
                        }
                        if (s[index] == '\t')
                        {
                            pen += TextMeasurer.TabAdvance(run, pen - lineStart);
                            continue;
                        }
                        if (s[index] == TextMeasurer.SoftHyphen)
                        {
                            if (line.hyphen > 0f && index == line.segments[^1].charStart + line.segments[^1].charCount - 1)
                            {
                                WriteGlyph(quads, '-', run.face, paint, alpha, font, run.fontSize, effect, effectStart, pen, baselineY, z, clip, gradientRect);
                                pen += TextMeasurer.MeasureAdvance('-', run);
                            }
                            continue;
                        }
                        if (TextMeasurer.IsZeroWidthBreak(s[index])) continue;
                        uint ink = tokens != null && index < tokens.Length && tokens[index] != SyntaxToken.Plain
                            ? Palettes.Code(palette ?? Palettes.Default, tokens[index]) : paint;
                        WriteGlyph(quads, TextMeasurer.SpaceFactor(s[index]) > 0f ? ' ' : s[index], run.face, ink, alpha, font, run.fontSize,
                                   effect, effectStart, pen, baselineY, z, clip, gradientRect);
                        pen += TextMeasurer.AdvanceAt(run, index, pen - lineStart) + Stretch(line, index, run);
                    }

                    float weight = MathF.Max(1f, run.fontSize * decorationWeight);
                    if (underline)
                        WriteRect(quads, segmentX, baselineY + run.fontSize * underlineDrop, pen - segmentX, weight, paint, alpha, z, clip, gradientRect);
                    if (strike)
                        WriteRect(quads, segmentX, baselineY - run.fontSize * strikeRise - weight * 0.5f, pen - segmentX, weight, paint, alpha, z, clip, gradientRect);

                    if (effect != 0)
                        FrameScheduler.RequestFrameAt(started + (segment.charStart + segment.charCount - run.charStart) * stagger + Effects.Duration(effect));
                }
            }
        }

        // A sheet link: a value on the baseline, or a range's grid cut at the column, rows outside the clip skipped.
        private void WriteSheet(DataPool quads, SheetBox sheet, in TextMeasurer.Run run, FontAsset font, uint paint, float alpha,
                                float x, float baselineY, float z, Vector4 clip, Vector4 gradientRect)
        {
            PaletteDefinition colors = palette ?? Palettes.Default;
            uint danger = Palettes.Surface(colors, PaletteRole.Danger);

            if (!sheet.range)
            {
                foreach (SheetBoxCell cell in sheet.cells)
                {
                    float pen = x;
                    string text = cell.text;
                    for (int i = 0; i < text.Length; i++)
                        pen += WriteGlyph(quads, text[i], run.face, cell.error ? danger : paint, alpha, font, run.fontSize, 0u, 0f,
                                          pen, baselineY, z, clip, gradientRect) + (i + 1 < text.Length ? TextMeasurer.Kern(text[i], text[i + 1], run) : 0f);
                }
                return;
            }

            float top = baselineY - run.imageHeight;
            Vector4 column = new Vector4(clip.X, clip.Y, MathF.Min(clip.Z, x + run.imageWidth), clip.W);
            uint line = Palettes.Surface(colors, PaletteRole.Line);

            foreach (LayoutRect rule in sheet.rules)
            {
                if (top + rule.Bottom <= clip.Y || top + rule.y >= clip.W) continue;
                WriteRect(quads, x + rule.x, MathF.Round(top + rule.y), rule.width, rule.height, line, alpha, z, column, gradientRect);
            }

            foreach (SheetBoxCell cell in sheet.cells)
            {
                if (top + cell.rect.Bottom <= clip.Y) continue;
                if (top + cell.rect.y >= clip.W) break;

                Vector4 cellClip = new Vector4(MathF.Max(column.X, x + cell.rect.x), MathF.Max(column.Y, top + cell.rect.y),
                                               MathF.Min(column.Z, x + cell.rect.Right), MathF.Min(column.W, top + cell.rect.Bottom));
                float pen = x + cell.pen;
                string text = cell.text;
                for (int i = 0; i < text.Length; i++)
                    pen += WriteGlyph(quads, text[i], run.face, cell.error ? danger : paint, alpha, font, run.fontSize, 0u, 0f,
                                      pen, top + cell.baseline, z, cellClip, gradientRect) + (i + 1 < text.Length ? TextMeasurer.Kern(text[i], text[i + 1], run) : 0f);
            }
        }

        // A segment's highlight, with a gap where the selection covers it.
        private void WriteHighlight(DataPool quads, LineSegment segment, float width, float x, float top, float height,
                                    uint paint, float alpha, float z, Vector4 clip, Vector4 gradientRect)
        {
            float right = x + width;
            int end = segment.charStart + segment.charCount;

            if (selectedTo <= segment.charStart || selectedFrom >= end || selectedFrom < 0)
            {
                WriteRect(quads, x, top, width, height, paint, alpha, z, clip, gradientRect);
                return;
            }

            float gapLeft = selectedFrom <= segment.charStart ? x : _origin.X + CaretAt(selectedFrom).x;
            float gapRight = selectedTo >= end ? right : _origin.X + CaretAt(selectedTo).x;
            if (gapLeft > x) WriteRect(quads, x, top, gapLeft - x, height, paint, alpha, z, clip, gradientRect);
            if (right > gapRight) WriteRect(quads, gapRight, top, right - gapRight, height, paint, alpha, z, clip, gradientRect);
        }

        // One flat rectangle: a highlight or a text decoration.
        protected static void WriteRect(DataPool quads, float x, float y, float width, float height,
                                      uint paint, float alpha, float z, Vector4 clip, Vector4 gradientRect)
        {
            if (width <= 0f || height <= 0f) return;
            if (UIEngine.recorder != null)
            {
                UIEngine.recorder.Rect(x, y, width, height, paint, clip);
                return;
            }

            int row = quads.Append();
            ref ControlGeometry g = ref quads.GetSpan<ControlGeometry>()[row];
            g.matrix = Matrix4x4.CreateScale(width, height, 1f)
                     * Matrix4x4.CreateTranslation(x + width * 0.5f, y + height * 0.5f, z);
            g.clip = clip;
            g.gradientRect = gradientRect;

            ref VulkanControl v = ref quads.GetSpan<VulkanControl>()[row];
            v = default;
            v.type = VulkanControlType.PanelControl;
            v.paint = paint;
            v.alpha = alpha;
            v.textureIndex = VulkanControl.noTexture;
        }

        // One picture, the whole texture turned about the centre of its box.
        private static void WriteImage(DataPool quads, TextureAsset? picture, string source, LayoutRect box, Vector2 size, Quaternion rotation,
                                       float alpha, float z, Vector4 clip)
        {
            if (picture == null || size.X <= 0f || size.Y <= 0f) return;
            if (UIEngine.recorder != null)
            {
                UIEngine.recorder.Image(source, box, size, rotation, clip);
                return;
            }

            float cx = box.x + box.width * 0.5f;
            float cy = box.y + box.height * 0.5f;
            int row = quads.Append();
            ref ControlGeometry g = ref quads.GetSpan<ControlGeometry>()[row];
            g.matrix = Matrix4x4.CreateScale(size.X, size.Y, 1f)
                     * Matrix4x4.CreateFromQuaternion(rotation)
                     * Matrix4x4.CreateTranslation(cx, cy, z);
            g.clip = clip;
            g.gradientRect = new Vector4(cx - size.X * 0.5f, cy - size.Y * 0.5f, cx + size.X * 0.5f, cy + size.Y * 0.5f);

            ref VulkanControl v = ref quads.GetSpan<VulkanControl>()[row];
            v = default;
            v.type = VulkanControlType.ImageControl;
            v.uvs.uv1 = new Vector2(1f, 0f);
            v.uvs.uv2 = new Vector2(0f, 1f);
            v.uvs.uv3 = new Vector2(0f, 0f);
            v.uvs.uv4 = new Vector2(1f, 1f);
            v.paint = Palettes.Inline(Vector3.One);
            v.alpha = alpha;
            v.textureIndex = picture.textureIndex;
        }

        // Cuts one glyph's quad out of the atlas and writes both of its columns, returning the pen
        // advance. Cell geometry is GlyphControl's, which is also what FontAssetGlyphMetrics
        // reproduces — three copies of it would drift.
        // A leader's text in every whole cell between from and to, the cells counted from the line box's left edge.
        private void WriteLeaders(DataPool quads, in StyleSpan span, TextMeasurer.Run run, FontAsset font, uint paint, float alpha,
                                  float from, float to, float baselineY, float z, Vector4 clip, Vector4 gradientRect)
        {
            float width = span.leaderWidth * textZoom;
            if (width <= 0f) return;
            float origin = _origin.X - LeaderOffset;
            for (float cell = origin + MathF.Ceiling((from - origin) / width - 0.001f) * width; cell + width <= to + 0.01f; cell += width)
            {
                float x = cell + span.leaderInset * textZoom;
                foreach (char c in span.leader)
                    x += WriteGlyph(quads, c, run.face, paint, alpha, font, run.fontSize, 0u, 0f, x, baselineY, z, clip, gradientRect);
            }
        }

        // how far the line box's left edge sits left of the text's, px at this zoom
        protected virtual float LeaderOffset => 0f;

        private float WriteGlyph(DataPool quads, char character, int face, uint paint, float alpha,
                                 FontAsset font, float size, uint effect, float effectStart,
                                 float penX, float baselineY, float z,
                                 Vector4 clip, Vector4 gradientRect)
        {
            AtlasMetaData atlas = font.atlasMetaData;
            (Glyph glyph, int index) = atlas.GetGlyphAndIndex(character);

            // An unimported character draws as a blank of space width rather than throwing.
            if (glyph == null)
            {
                (glyph, index) = atlas.GetGlyphAndIndex(' ');
                if (glyph == null) return 0f;
            }

            GlyphMetrics m = atlas.Metrics(glyph, face);
            if (UIEngine.recorder != null)
            {
                UIEngine.recorder.Glyph(font, face, atlas.chars[index], size, penX, baselineY, paint, clip);
                return m.advanceWidth * size;
            }

            float margin = atlas.inkMargin;
            float cellW = m.glyphWidth * size * atlas.cellScale;
            float cellH = m.glyphHeight * size * atlas.cellScale;

            float ascent = 0f;
            int range = m.yMax - m.yMin;
            if (range != 0)
            {
                // baselineFromTop is a fraction OF THE CELL, so it scales the cell height
                float baselineFromTop = margin + (1f - 2f * margin) * m.yMax / range;
                ascent = baselineFromTop * cellH;
            }

            float s = UIEngine.pixelScale;
            float ink = (penX + m.leftSideOffset * size) * s;
            float x = MathF.Round(ink * 3f) / (3f * s) - cellW * margin;
            float y = MathF.Round(baselineY * s) / s - ascent;

            Matrix4x4 matrix = Matrix4x4.Identity;
            matrix *= Matrix4x4.CreateScale(cellW, cellH, 1f);
            matrix *= Matrix4x4.CreateTranslation(x + cellW * 0.5f, y + cellH * 0.5f, z);

            int row = quads.Append();
            ref ControlGeometry g = ref quads.GetSpan<ControlGeometry>()[row];
            g.matrix = matrix;
            g.clip = clip;
            g.gradientRect = gradientRect;

            int cell = atlas.CellIndex(index, face);
            float k = MathF.Ceiling(MathF.Sqrt(atlas.cellCount));
            float cellUV = 1f / k;
            float xOffset = cell % k * cellUV;
            float yOffset = MathF.Floor(cell / k) * cellUV;
            float texelPad = 1f / font.textureAsset.image.Width;

            float u0 = xOffset + texelPad;
            float v0 = yOffset + texelPad;
            float u1 = xOffset + cellUV - texelPad;
            float v1 = yOffset + cellUV - texelPad;

            ref VulkanControl v = ref quads.GetSpan<VulkanControl>()[row];
            v.type = VulkanControlType.MTSDFControl;
            v.uvs.uv1 = new Vector2(u1, v1);
            v.uvs.uv2 = new Vector2(u0, v0);
            v.uvs.uv3 = new Vector2(u0, v1);
            v.uvs.uv4 = new Vector2(u1, v0);
            v.paint = paint;
            v.alpha = alpha;
            v.textureIndex = font.textureAsset.textureIndex;
            v.cornerRadius = Vector4.Zero;
            v.edgePaint = 0;
            v.edgeThickness = Thickness.Zero;
            v.state = 0f;
            v.effect = effect;
            v.effectStart = effectStart;

            return m.advanceWidth * size;
        }
        #endregion

        #region ---- caret geometry ----
        // Design space point to the character slot it belongs to. A press past a character's
        // midpoint takes the slot after it, or the end of a word could never be clicked.
        public int IndexAt(Vector2 point)
        {
            if (_layout == null) return 0;

            TextLine line = _layout.lines[LineAt(point.Y - _origin.Y)];
            float localX = point.X - _origin.X - line.left;
            float pen = 0f;

            foreach (LineSegment segment in line.segments)
            {
                TextMeasurer.Run run = _runs[segment.runIndex];
                for (int i = 0; i < segment.charCount; i++)
                {
                    float advance = TextMeasurer.AdvanceAt(run, segment.charStart + i, pen) + Stretch(line, segment.charStart + i, run);
                    if (localX < pen + advance * 0.5f) return segment.charStart + i;
                    pen += advance;
                }
            }

            LineSegment last = line.segments[line.segments.Count - 1];
            return last.charStart + last.charCount;
        }

        // Character slot to a caret rect, local to TextOrigin. The slot at the end of a wrapped line
        // is claimed by the line below, or the caret sits off the right edge instead of before the
        // word that wrapped.
        public CaretGeometry CaretAt(int offset)
        {
            if (_layout == null) return new CaretGeometry(0f, 0f, fontSize * lineHeight, 0f);

            for (int i = 0; i < _layout.lines.Count; i++)
            {
                TextLine line = _layout.lines[i];
                bool isLastLine = i == _layout.lines.Count - 1;
                float x = line.left;

                for (int s = 0; s < line.segments.Count; s++)
                {
                    LineSegment segment = line.segments[s];
                    int end = segment.charStart + segment.charCount;

                    bool endBelongsHere = isLastLine || s < line.segments.Count - 1;
                    bool holds = offset >= segment.charStart
                                 && (offset < end || (endBelongsHere && offset == end));

                    if (!holds)
                    {
                        x += Stretched(line, segment);
                        continue;
                    }

                    TextMeasurer.Run run = _runs[segment.runIndex];
                    for (int c = segment.charStart; c < offset; c++)
                        x += TextMeasurer.AdvanceAt(run, c, x - line.left) + Stretch(line, c, run);

                    return new CaretGeometry(x, line.top, line.height, line.baseline);
                }
            }

            TextLine lastLine = _layout.lines[_layout.lines.Count - 1];
            return new CaretGeometry(lastLine.left + lastLine.width, lastLine.top, lastLine.height, lastLine.baseline);
        }

        // What CaretAt is relative to: the design-space point the first line's pen starts at.
        public Vector2 TextOrigin => _origin;

        // The measured lines, in this run's own space. Null until the first measure.
        public IReadOnlyList<TextLine> Lines => _layout?.lines;

        public int Length => data.Length;

        // The picture under a design-space point, or -1.
        public int PictureAt(Vector2 point)
        {
            if (_layout == null) return -1;

            TextLine line = _layout.lines[LineAt(point.Y - _origin.Y)];
            foreach (LineSegment segment in line.segments)
            {
                if (!_runs[segment.runIndex].picture) continue;

                for (int i = segment.charStart; i < segment.charStart + segment.charCount; i++)
                    if (PictureFrame(i, out LayoutRect rect, out Quaternion rotation) && rect.Contains(rect.Unturned(point, rotation))) return i;
            }
            return -1;
        }

        // A picture character's drawn box in design space; false when the index is not a picture.
        public bool PictureBox(int index, out LayoutRect box)
        {
            box = LayoutRect.Empty;
            if (_layout == null) return false;

            foreach (TextLine line in _layout.lines)
                foreach (LineSegment segment in line.segments)
                {
                    if (index < segment.charStart || index >= segment.charStart + segment.charCount) continue;

                    TextMeasurer.Run run = _runs[segment.runIndex];
                    if (!run.picture || run.floating) return false;

                    float x = line.left;
                    foreach (LineSegment before in line.segments)
                    {
                        if (before.charStart == segment.charStart) break;
                        x += Stretched(line, before);
                    }
                    x += (index - segment.charStart) * run.imageWidth;

                    box = new LayoutRect(_origin.X + x, _origin.Y + line.baseline - run.imageHeight, run.imageWidth, run.imageHeight);
                    return true;
                }
            return false;
        }

        // An inline picture's drawn rect, unturned and centred in its box, and its turn.
        public bool PictureFrame(int index, out LayoutRect rect, out Quaternion rotation)
        {
            rect = LayoutRect.Empty;
            rotation = Quaternion.Identity;
            if (!PictureBox(index, out LayoutRect box)) return false;

            for (int i = 0; i < _runs.Count; i++)
                if (index >= _runs[i].charStart && index < _runs[i].charStart + _runs[i].charCount)
                {
                    Vector2 size = runObjects![i].size;
                    rotation = runObjects[i].rotation;
                    rect = new LayoutRect(box.x + (box.width - size.X) * 0.5f, box.y + (box.height - size.Y) * 0.5f, size.X, size.Y);
                    return true;
                }
            return false;
        }

        // The formula under a design-space point, or -1.
        public int MathAt(Vector2 point)
        {
            if (_layout == null) return -1;

            TextLine line = _layout.lines[LineAt(point.Y - _origin.Y)];
            foreach (LineSegment segment in line.segments)
            {
                if (!_runs[segment.runIndex].math) continue;

                for (int i = segment.charStart; i < segment.charStart + segment.charCount; i++)
                    if (MathBox(i, out LayoutRect box) && box.Contains(point)) return i;
            }
            return -1;
        }

        // A formula character's drawn box in design space; false when the index is not a formula.
        public bool MathBox(int index, out LayoutRect box)
        {
            box = LayoutRect.Empty;
            if (_layout == null) return false;

            foreach (TextLine line in _layout.lines)
                foreach (LineSegment segment in line.segments)
                {
                    if (index < segment.charStart || index >= segment.charStart + segment.charCount) continue;

                    TextMeasurer.Run run = _runs[segment.runIndex];
                    MathBox? math = runObjects?[segment.runIndex].math;
                    if (!run.math || math == null) return false;

                    float x = line.left;
                    foreach (LineSegment before in line.segments)
                    {
                        if (before.charStart == segment.charStart) break;
                        x += Stretched(line, before);
                    }
                    x += (index - segment.charStart) * run.imageWidth;

                    float width = math.width * run.fontSize;
                    float height = math.height * run.fontSize;
                    box = new LayoutRect(_origin.X + x + MathLeft(run, math), _origin.Y + line.baseline - height,
                                         width, height + math.depth * run.fontSize);
                    return true;
                }
            return false;
        }

        // Re-stacks the lines from blockTop, pushing each across page breaks; returns the height.
        internal float Paginate(float blockTop, PageBands bands, PageSpace? space = null)
        {
            if (_layout == null) return 0f;

            List<(int offset, string id)>? anchors = space == null ? null : Anchors();
            List<TextLine> lines = _layout.lines;
            bool keep = WidowControl && lines.Count > 1;
            float overhang = Overhang;
            float skip = LineSkip;
            float y = blockTop;
            for (int i = 0; i < lines.Count; i++)
            {
                TextLine line = lines[i];
                if (i > 0) y += TextMeasurer.Interline(lines[i - 1], line, skip);
                float at = space == null ? bands.Push(y, line.height, overhang) : space.PushLine(bands, y, line.height, AnchorsIn(anchors!, line), overhang);
                if (keep && i > 0 && at > y + PageBands.tolerance && bands.PageOf(at) > bands.PageOf(y))
                {
                    // a club line left behind or a widow carried over: the lines before it follow onto the new page
                    int from = i == lines.Count - 1 ? i - 1 : i;
                    if (from <= 1) from = 0;
                    if (from < i && !HasAnchors(anchors, lines, from, i))
                    {
                        float top = at;
                        for (int j = from; j < i; j++)
                        {
                            lines[j].top = top - blockTop;
                            top += lines[j].height + TextMeasurer.Interline(lines[j], lines[j + 1], skip);
                        }
                        at = space == null ? bands.Push(top, line.height, overhang) : space.PushLine(bands, top, line.height, AnchorsIn(anchors!, line), overhang);
                    }
                }
                if (line.ink > line.ascent && bands.AtTextTop(at)) at += line.ink - line.ascent;
                line.top = at - blockTop;
                y = at + line.height;
            }
            return y - blockTop;
        }

        // Each footnote anchor's character offset and the footnote it names.
        internal List<(int offset, string id)> Anchors()
        {
            List<(int, string)> anchors = new List<(int, string)>();
            int offset = 0;
            foreach (StyleSpan span in spans)
            {
                if (span.note != null) anchors.Add((offset, span.note));
                offset += span.count;
            }
            return anchors;
        }

        private static bool HasAnchors(List<(int offset, string id)>? anchors, List<TextLine> lines, int from, int to)
        {
            if (anchors == null) return false;
            for (int j = from; j < to; j++)
                if (AnchorsIn(anchors, lines[j]) != null) return true;
            return false;
        }

        private static List<string>? AnchorsIn(List<(int offset, string id)> anchors, TextLine line)
        {
            if (anchors.Count == 0 || line.segments.Count == 0) return null;
            int start = line.segments[0].charStart;
            int end = line.segments[^1].charStart + line.segments[^1].charCount;
            List<string>? ids = null;
            foreach ((int offset, string id) in anchors)
                if (offset >= start && offset < end) (ids ??= new List<string>()).Add(id);
            return ids;
        }

        // Lowest line not past y; clamps at both ends.
        private int LineAt(float y)
        {
            int low = 0;
            int high = _layout.lines.Count - 1;

            while (low < high)
            {
                int mid = (low + high + 1) / 2;
                if (_layout.lines[mid].top <= y) low = mid;
                else high = mid - 1;
            }
            return low;
        }
        #endregion

        public override bool OnPointerPress(PointerEvent e)
        {
            if (base.OnPointerPress(e)) return true;

            IGlyphPressTarget target = FindPressTarget();
            if (target == null) return false;

            int picture = PictureAt(e.point);
            if (picture < 0) picture = MathAt(e.point);
            if (picture >= 0) target.PicturePressed(this, picture, e.button);
            else target.GlyphPressed(this, IndexAt(e.point));
            return true;
        }

        private IGlyphPressTarget FindPressTarget()
        {
            Entity current = parent;
            while (current != null)
            {
                if (current is IGlyphPressTarget target) return target;
                current = current.parent;
            }
            return null;
        }
    }
}
