using ArctisAurora.Core.Animation;
using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Text;
using System.Numerics;

namespace ArctisAurora.Core.UI.Controls
{
    // On or off: a pill with a sliding knob and the current state's caption opposite it.
    [A_XSDType("ToggleSwitch", "UI")]
    public class ToggleSwitchControl : ButtonControl
    {
        // geometry
        private const int captionSize = 13;
        private const float trackHeight = 24f;
        private const float knobInset = 3f;
        private const float captionPadding = 10f;
        private const float slideSeconds = 0.18f;

        private readonly PanelControl track = new PanelControl
        {
            cornerRadius = new CornerRadii(trackHeight * 0.5f),
            hitTestable = false
        };

        private readonly PanelControl lit = new PanelControl
        {
            cornerRadius = new CornerRadii(trackHeight * 0.5f),
            hitTestable = false
        };

        private readonly PanelControl knob = new PanelControl
        {
            cornerRadius = new CornerRadii((trackHeight - 2f * knobInset) * 0.5f),
            hitTestable = false
        };

        private readonly LabelControl onLabel = new LabelControl { fontSize = captionSize, hitTestable = false };
        private readonly LabelControl offLabel = new LabelControl { fontSize = captionSize, hitTestable = false };

        public Action<bool>? onChanged;

        [A_XSDElementProperty("OnText", "UI", "Caption shown while the switch is on.")]
        public string onText
        {
            get => onLabel.text;
            set => onLabel.text = value;
        }

        [A_XSDElementProperty("OffText", "UI", "Caption shown while the switch is off.")]
        public string offText
        {
            get => offLabel.text;
            set => offLabel.text = value;
        }

        [A_XSDElementProperty("On", "UI", "Whether the switch is on.")]
        public bool on
        {
            get => _on;
            set
            {
                _on = value;
                knob.horizontalPosition = value ? 1f : 0f;
                lit.alpha = value ? 1f : 0f;
                Light();
            }
        }
        private bool _on;

        public ToggleSwitchControl()
        {
            alpha = 0f;
            track.PaintOr(null, PaletteRole.Chrome);
            lit.PaintOr(null, PaletteRole.Accent);
            lit.alpha = 0f;
            track.AddChild(lit);
            knob.PaintOr("#FFFFFF", PaletteRole.Ink);
            onLabel.PaintOr("#FFFFFF", PaletteRole.Ink);
            offLabel.PaintOr(null, PaletteRole.MutedInk);
            knob.horizontalPosition = 0f;
            AddChild(track);
            AddChild(onLabel);
            AddChild(offLabel);
            AddChild(knob);
            Light();
        }

        public override void AddChild(Entity entity)
        {
            children.Add(entity);
            entity.parent = this;
            MarkTreeOrderDirty();
            InvalidateLayout();
        }

        protected override Vector2 MeasureCore(Vector2 availableSize)
        {
            float caption = MathF.Max(onLabel.Measure(availableSize).X, offLabel.Measure(availableSize).X);
            track.Measure(new Vector2(TrackWidth(caption), trackHeight));

            float w = preferredWidth > 0 ? preferredWidth : TrackWidth(caption) + padding.totalHorizontal;
            float h = preferredHeight > 0 ? preferredHeight : trackHeight + padding.totalVertical;

            arrange.desired = new Vector2(w, h);
            SetFlag(ArrangeFlags.MeasureDirty, false);
            return arrange.desired;
        }

        protected override void ArrangeCore(LayoutRect finalRect)
        {
            WriteArranged(finalRect);
            LayoutRect inner = finalRect.Shrink(padding);
            float middle = inner.y + inner.height * 0.5f;

            float caption = MathF.Max(onLabel.DesiredSize.X, offLabel.DesiredSize.X);
            float trackWidth = TrackWidth(caption);
            track.Arrange(new LayoutRect(inner.x, middle - trackHeight * 0.5f, trackWidth, trackHeight));

            float knobSize = trackHeight - 2f * knobInset;
            float travel = trackWidth - 2f * knobInset - knobSize;
            knob.Measure(new Vector2(knobSize, knobSize));
            knob.Arrange(new LayoutRect(inner.x + knobInset + travel * knob.horizontalPosition,
                middle - knobSize * 0.5f, knobSize, knobSize));

            LabelControl shown = _on ? onLabel : offLabel;
            Vector2 c = shown.DesiredSize;
            float side = _on ? inner.x : inner.x + knobInset + knobSize;
            shown.Arrange(new LayoutRect(side + captionPadding + (caption - c.X) * 0.5f, middle - c.Y * 0.5f, c.X, c.Y));
            SetFlag(ArrangeFlags.ArrangeDirty, false);
        }

        public override bool OnPointerRelease(PointerEvent e)
        {
            base.OnPointerRelease(e);
            if (e.button != PointerEvent.leftButton || !enabled) return true;

            _on = !_on;
            Animations.Tween(knob, "horizontalPosition", new Vector4(_on ? 1f : 0f, 0f, 0f, 0f), slideSeconds, Curve.Ease(EaseKind.CubicInOut));
            Animations.Tween(lit, "alpha", new Vector4(_on ? 1f : 0f, 0f, 0f, 0f), slideSeconds, Curve.Ease(EaseKind.CubicInOut));
            Light();
            onChanged?.Invoke(_on);
            return true;
        }

        private float TrackWidth(float caption) => knobInset + (trackHeight - 2f * knobInset) + 2f * captionPadding + caption;

        // Only the current state's caption shown.
        private void Light()
        {
            if (_on) { offLabel.Hide(); onLabel.Show(); }
            else { onLabel.Hide(); offLabel.Show(); }
            InvalidateArrange();
        }
    }
}
