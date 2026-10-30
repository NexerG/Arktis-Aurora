using ArctisAurora.Core.Animation;
using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Text;
using System.Numerics;

namespace ArctisAurora.Core.UI.Controls
{
    // Several options in pills of one or more rows, with a thumb that slides behind the chosen one.
    [A_XSDType("Switch", "UI")]
    public class SwitchControl : ButtonControl
    {
        // geometry
        private const int captionSize = 13;
        private const float trackHeight = 24f;
        private const float thumbInset = 3f;
        private const float segmentPadding = 10f;
        private const float rowGap = 4f;
        private const float slideSeconds = 0.18f;

        private readonly PanelControl thumb = new PanelControl
        {
            cornerRadius = new CornerRadii((trackHeight - 2f * thumbInset) * 0.5f),
            hitTestable = false
        };

        private readonly List<PanelControl> tracks = new List<PanelControl>();
        private readonly List<LabelControl> captions = new List<LabelControl>();
        private LayoutRect[] segments = Array.Empty<LayoutRect>();
        private string[] _options = Array.Empty<string>();

        public Action<int>? onChanged;

        public IReadOnlyList<string> options
        {
            get => _options;
            set
            {
                _options = value.ToArray();
                Rebuild();
            }
        }

        [A_XSDElementProperty("Options", "UI", "The options' captions, comma separated, in order.")]
        public string optionList
        {
            get => string.Join(",", _options);
            set => options = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        }

        [A_XSDElementProperty("Columns", "UI", "Most options on one row; 0 keeps them all on one row.")]
        public int columns
        {
            get => _columns;
            set
            {
                _columns = Math.Max(0, value);
                Rebuild();
            }
        }
        private int _columns;

        [A_XSDElementProperty("Selected", "UI", "Index of the chosen option.")]
        public int selected
        {
            get => _selected;
            set
            {
                _selected = Math.Clamp(value, 0, Math.Max(0, _options.Length - 1));
                _from = _selected;
                thumb.horizontalPosition = 1f;
                Light();
                InvalidateArrange();
            }
        }
        private int _selected;
        private int _from;

        private int PerRow => _columns > 0 ? _columns : Math.Max(1, _options.Length);
        private int Rows => (_options.Length + PerRow - 1) / PerRow;

        public SwitchControl()
        {
            alpha = 0f;
            thumb.PaintOr(null, PaletteRole.Ground);
            Rebuild();
        }

        public override void AddChild(Entity entity)
        {
            children.Add(entity);
            entity.parent = this;
            MarkTreeOrderDirty();
            InvalidateLayout();
        }

        // Tracks, then the thumb, then the captions, so the captions draw over the thumb.
        private void Rebuild()
        {
            foreach (Entity child in children.ToArray()) RemoveChild(child);
            foreach (PanelControl track in tracks) track.Destroy();
            foreach (LabelControl caption in captions) caption.Destroy();
            tracks.Clear();
            captions.Clear();

            for (int r = 0; r < Rows; r++)
            {
                PanelControl track = new PanelControl { cornerRadius = new CornerRadii(trackHeight * 0.5f), hitTestable = false };
                track.PaintOr(null, PaletteRole.Chrome);
                tracks.Add(track);
                AddChild(track);
            }
            AddChild(thumb);
            foreach (string option in _options)
            {
                LabelControl caption = new LabelControl { text = option, fontSize = captionSize, hitTestable = false };
                captions.Add(caption);
                AddChild(caption);
            }

            segments = new LayoutRect[_options.Length];
            selected = _selected;
        }

        // A row's width at its captions' own sizes, without the inset.
        private float RowWidth(int row)
        {
            float width = 0f;
            for (int i = row * PerRow; i < Math.Min(captions.Count, (row + 1) * PerRow); i++)
                width += captions[i].DesiredSize.X + 2f * segmentPadding;
            return width;
        }

        private float Widest()
        {
            float widest = 0f;
            for (int r = 0; r < Rows; r++) widest = MathF.Max(widest, RowWidth(r));
            return widest;
        }

        private float BlockHeight => Rows == 0 ? trackHeight : Rows * trackHeight + (Rows - 1) * rowGap;

        protected override Vector2 MeasureCore(Vector2 availableSize)
        {
            foreach (LabelControl caption in captions) caption.Measure(availableSize);
            float trackWidth = Widest() + 2f * thumbInset;
            foreach (PanelControl track in tracks) track.Measure(new Vector2(trackWidth, trackHeight));

            float w = preferredWidth > 0 ? preferredWidth : trackWidth + padding.totalHorizontal;
            float h = preferredHeight > 0 ? preferredHeight : BlockHeight + padding.totalVertical;

            arrange.desired = new Vector2(w, h);
            SetFlag(ArrangeFlags.MeasureDirty, false);
            return arrange.desired;
        }

        protected override void ArrangeCore(LayoutRect finalRect)
        {
            WriteArranged(finalRect);
            LayoutRect inner = finalRect.Shrink(padding);
            float top = inner.y + (inner.height - BlockHeight) * 0.5f;
            float widest = Widest();

            for (int r = 0; r < Rows; r++)
            {
                float y = top + r * (trackHeight + rowGap);
                tracks[r].Arrange(new LayoutRect(inner.x, y, widest + 2f * thumbInset, trackHeight));

                int first = r * PerRow;
                int last = Math.Min(captions.Count, first + PerRow);
                float share = (widest - RowWidth(r)) / (last - first);
                float x = inner.x + thumbInset;
                for (int i = first; i < last; i++)
                {
                    Vector2 c = captions[i].DesiredSize;
                    float w = c.X + 2f * segmentPadding + share;
                    segments[i] = new LayoutRect(x, y + thumbInset, w, trackHeight - 2f * thumbInset);
                    captions[i].Arrange(new LayoutRect(x + (w - c.X) * 0.5f, y + (trackHeight - c.Y) * 0.5f, c.X, c.Y));
                    x += w;
                }
            }

            if (segments.Length > 0)
            {
                LayoutRect a = segments[_from], b = segments[_selected];
                float t = thumb.horizontalPosition;
                LayoutRect rect = new LayoutRect(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t,
                    a.width + (b.width - a.width) * t, a.height + (b.height - a.height) * t);
                thumb.Measure(rect.size);
                thumb.Arrange(rect);
            }
            SetFlag(ArrangeFlags.ArrangeDirty, false);
        }

        public override bool OnPointerRelease(PointerEvent e)
        {
            base.OnPointerRelease(e);
            if (e.button != PointerEvent.leftButton || !enabled || captions.Count == 0) return true;

            int picked = 0;
            float nearest = float.MaxValue;
            for (int i = 0; i < captions.Count; i++)
            {
                LayoutRect r = captions[i].arrangedRect;
                float distance = Vector2.DistanceSquared(e.point, new Vector2(r.x + r.width * 0.5f, r.y + r.height * 0.5f));
                if (distance < nearest) { nearest = distance; picked = i; }
            }
            if (picked == _selected) return true;

            _from = _selected;
            _selected = picked;
            thumb.horizontalPosition = 0f;
            Animations.Tween(thumb, "horizontalPosition", new Vector4(1f, 0f, 0f, 0f), slideSeconds, Curve.Ease(EaseKind.CubicInOut));
            Light();
            onChanged?.Invoke(picked);
            return true;
        }

        // The chosen caption in Ink, the rest muted.
        private void Light()
        {
            for (int i = 0; i < captions.Count; i++)
                captions[i].role = i == _selected ? PaletteRole.Ink : PaletteRole.MutedInk;
        }
    }
}
