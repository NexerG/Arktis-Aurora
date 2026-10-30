using ArctisAurora.Core.Animation;
using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.EngineWork.Rendering;
using System.Numerics;

namespace ArctisAurora.Core.UI.Menus
{
    // One panel of an open context menu, built from its entries. Never authored, so untagged.
    public class ContextMenuControl : StackPanelControl
    {
        private const int captionSize = 13;

        // placement — position is in the origin window's design space, even when hosted in its own
        public int depth;
        public Vector2 position;
        public RenderWindow? window;
        public Row? opener;
        public bool centered;

        // 0 hidden above its anchor, 1 fully slid out
        [A_Animatable(typeof(ArrangeData), nameof(ArrangeData.reveal), LayoutChange.Arrange)]
        public float reveal
        {
            get => arrange.reveal;
            set
            {
                arrange.reveal = value;
                InvalidateArrange();
            }
        }

        public ContextMenuControl(List<ContextMenuEntry> entries, int depth)
        {
            this.depth = depth;
            clip = "menu-open";
            orientation = Orientation.Vertical;
            padding = new Thickness(4f);
            role = PaletteRole.Ground;
            cornerRole = CornerRole.Popup;
            edgeThickness = new Thickness(1f);
            stopsContextMenu = true;

            foreach (ContextMenuEntry entry in entries)
                AddChild(entry is ContextMenuLine ? Line()
                       : entry is ContextMenuContent hosted ? hosted.content
                       : new Row(entry));
        }

        // At its own position and size, whatever box the root offers, slid up by the unrevealed part and cut at its anchor.
        protected override void ArrangeCore(LayoutRect finalRect)
        {
            Vector2 at = window != null ? Vector2.Zero : position;
            float offset = DesiredSize.Y * (1f - Math.Clamp(reveal, 0f, 1f));
            base.ArrangeCore(new LayoutRect(at.X, at.Y - offset, DesiredSize.X, DesiredSize.Y));
            ClipSubtree(this, new LayoutRect(at.X, at.Y, DesiredSize.X, DesiredSize.Y - offset));
        }

        // The edge takes the palette's Line alongside the panel's own role.
        protected override void ApplyRole(PaletteDefinition scheme, uint ground)
        {
            base.ApplyRole(scheme, ground);
            visual.edgePaint = Palettes.Surface(scheme, PaletteRole.Line);
        }

        private static Control Line() => new PanelControl
        {
            preferredWidth = 1f,
            preferredHeight = 1f,
            horizontalAlignment = HorizontalAlignment.Stretch,
            margin = new Thickness(4f, 0f, 4f, 0f),
            role = PaletteRole.Line
        };

        // A button or submenu entry: its caption, and an arrow flush right when it opens a submenu.
        public sealed class Row : ButtonControl
        {
            private const float arrowGap = 24f;

            public readonly ContextMenuEntry entry;
            private readonly LabelControl caption;
            private readonly LabelControl? arrow;

            public Row(ContextMenuEntry entry)
            {
                this.entry = entry;
                padding = new Thickness(4f, 10f, 4f, 10f);
                cornerRadius = new CornerRadii(4f);
                stateBinding = "menu-row";
                edgeRole = PaletteRole.Accent;

                string text = entry is ContextMenuSubmenu submenu ? submenu.text : ((ContextMenuButton)entry).text;
                caption = new LabelControl { text = text, fontSize = captionSize };
                AddChild(caption);

                if (entry is not ContextMenuSubmenu) return;
                arrow = new LabelControl { text = "›", fontSize = captionSize };
                AddChild(arrow);
            }

            public override bool takesActiveControl => false;

            public override void AddChild(Entity entity)
            {
                children.Add(entity);
                entity.parent = this;
                MarkTreeOrderDirty();
                InvalidateLayout();
            }

            protected override Vector2 MeasureCore(Vector2 availableSize)
            {
                Vector2 c = caption.Measure(availableSize);
                Vector2 a = arrow?.Measure(availableSize) ?? Vector2.Zero;

                float w = c.X + (arrow != null ? arrowGap + a.X : 0f) + padding.totalHorizontal;
                float h = MathF.Max(c.Y, a.Y) + padding.totalVertical;

                arrange.desired = new Vector2(w, h);
                SetFlag(ArrangeFlags.MeasureDirty, false);
                return arrange.desired;
            }

            protected override void ArrangeCore(LayoutRect finalRect)
            {
                WriteArranged(finalRect);
                LayoutRect inner = finalRect.Shrink(padding);

                Vector2 c = caption.DesiredSize;
                float cx = ((ContextMenuControl)parent).centered ? inner.x + (inner.width - c.X) * 0.5f : inner.x;
                caption.Arrange(new LayoutRect(cx, inner.y + (inner.height - c.Y) * 0.5f, c.X, c.Y));

                if (arrow != null)
                {
                    Vector2 a = arrow.DesiredSize;
                    arrow.Arrange(new LayoutRect(inner.x + inner.width - a.X, inner.y + (inner.height - a.Y) * 0.5f, a.X, a.Y));
                }
                SetFlag(ArrangeFlags.ArrangeDirty, false);
            }

            public override bool OnPointerEnter(PointerEvent e)
            {
                base.OnPointerEnter(e);
                ContextMenus.Entered((ContextMenuControl)parent, this);
                return true;
            }

            public override bool OnPointerRelease(PointerEvent e)
            {
                base.OnPointerRelease(e);
                ContextMenus.Clicked(this);
                return true;
            }
        }
    }
}
