using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI.Menus;
using ArctisAurora.Core.UI.Text;
using System.Numerics;

namespace ArctisAurora.Core.UI.Controls
{
    // A caption that drops its options as a menu under it.
    [A_XSDType("Dropdown", "UI")]
    public class DropdownControl : ButtonControl
    {
        // caption
        private const int captionSize = 13;

        // chevron
        private const float chevronSize = 14f;
        private const float chevronGap = 6f;

        private readonly LabelControl label = new LabelControl
        {
            fontSize = captionSize
        };

        private readonly IconControl chevron = new IconControl
        {
            setName = "default",
            iconName = "chevron-down",
            preferredWidth = chevronSize,
            preferredHeight = chevronSize,
            hitTestable = false
        };

        public IReadOnlyList<string> options = Array.Empty<string>();
        public Action<string>? onPicked;

        public string selected
        {
            get => label.text;
            set => label.text = value;
        }

        public DropdownControl()
        {
            AddChild(label);
            AddChild(chevron);
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
            Vector2 c = label.Measure(availableSize);
            Vector2 a = chevron.Measure(availableSize);

            float w = preferredWidth > 0 ? preferredWidth : c.X + 2f * (chevronGap * 2f + a.X) + padding.totalHorizontal;
            float h = preferredHeight > 0 ? preferredHeight : MathF.Max(c.Y, a.Y) + padding.totalVertical;

            arrange.desired = new Vector2(w, h);
            SetFlag(ArrangeFlags.MeasureDirty, false);
            return arrange.desired;
        }

        protected override void ArrangeCore(LayoutRect finalRect)
        {
            WriteArranged(finalRect);
            LayoutRect inner = finalRect.Shrink(padding);

            Vector2 c = label.DesiredSize;
            label.Arrange(new LayoutRect(inner.x + (inner.width - c.X) * 0.5f, inner.y + (inner.height - c.Y) * 0.5f, c.X, c.Y));

            Vector2 a = chevron.DesiredSize;
            chevron.Arrange(new LayoutRect(inner.x + inner.width - chevronGap - a.X, inner.y + (inner.height - a.Y) * 0.5f, a.X, a.Y));
            SetFlag(ArrangeFlags.ArrangeDirty, false);
        }

        public override bool OnPointerRelease(PointerEvent e)
        {
            base.OnPointerRelease(e);
            if (e.button != PointerEvent.leftButton || ContextMenus.ClosedOn(this)) return true;

            List<ContextMenuEntry> entries = new List<ContextMenuEntry>();
            foreach (string option in options)
                entries.Add(new ContextMenuButton(option, () => Pick(option)));

            ContextMenus.OpenFrom(entries, this,
                new Vector2(arrangedRect.x, arrangedRect.y + arrangedRect.height), arrangedRect.width, centered: true);
            return true;
        }

        private void Pick(string option)
        {
            selected = option;
            onPicked?.Invoke(option);
        }
    }
}
