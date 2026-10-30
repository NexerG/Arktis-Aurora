using ArctisAurora.Core.Animation;
using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI.Controls;
using System.Numerics;

namespace ArctisAurora.Core.UI.Containers
{
    // A rule with a round grip that splits in two, sliding its content open between the halves.
    [A_XSDType("Expander", "UI")]
    public class ExpanderControl : ContainerControl
    {
        // grip
        private const float diameter = 24f;
        private const float arrowSize = 10f;

        // motion
        private const float seconds = 0.2f;

        private readonly Viewport viewport = new Viewport { alpha = 0f };
        private readonly PanelControl topRule = Rule();
        private readonly PanelControl bottomRule = Rule();
        private readonly Half topHalf;
        private readonly Half bottomHalf;

        private bool open;

        [A_XSDElementProperty("Expanded", "UI", "Whether the content starts open.")]
        public bool expanded
        {
            get => open;
            set
            {
                open = value;
                Animations.StopAll(this);
                reveal = value ? 1f : 0f;
                if (value) viewport.Show();
                else viewport.Hide();
            }
        }

        // 0 closed, 1 open
        [A_Animatable(typeof(ArrangeData), nameof(ArrangeData.reveal), LayoutChange.Measure)]
        public float reveal
        {
            get => arrange.reveal;
            set
            {
                arrange.reveal = value;
                InvalidateLayout();
            }
        }

        public ExpanderControl()
        {
            topHalf = new Half(true) { pressed = Toggle };
            bottomHalf = new Half(false) { pressed = Toggle };

            base.AddChild(viewport);
            base.AddChild(topRule);
            base.AddChild(bottomRule);
            base.AddChild(topHalf);
            base.AddChild(bottomHalf);
            viewport.Hide();
        }

        // The one authored child is the content.
        public override void AddChild(Entity entity) => viewport.Set((Control)entity);

        public void Toggle()
        {
            open = !open;
            float to = open ? 1f : 0f;

            Animations.StopAll(this);
            if (open) viewport.Show();
            Animations.Tween(this, nameof(reveal), new Vector4(to, 0f, 0f, 0f), seconds, Curve.Ease(EaseKind.CubicOut), () =>
            {
                if (!open && reveal <= 0f) viewport.Hide();
            });
        }

        #region ---- layout ----
        protected override Vector2 MeasureCore(Vector2 availableSize)
        {
            Vector2 content = viewport.hidden ? Vector2.Zero : viewport.Measure(new Vector2(availableSize.X, float.MaxValue));
            topRule.Measure(availableSize);
            bottomRule.Measure(availableSize);
            topHalf.Measure(availableSize);
            bottomHalf.Measure(availableSize);

            float width = preferredWidth > 0 ? preferredWidth : availableSize.X >= float.MaxValue ? content.X : availableSize.X;
            arrange.desired = new Vector2(width, diameter + content.Y * Math.Clamp(reveal, 0f, 1f));
            SetFlag(ArrangeFlags.MeasureDirty, false);
            return arrange.desired;
        }

        protected override void ArrangeCore(LayoutRect finalRect)
        {
            WriteArranged(finalRect);

            float radius = diameter * 0.5f;
            float seam = finalRect.y + radius;
            float shown = MathF.Max(0f, finalRect.height - diameter);
            float gripX = finalRect.x + (finalRect.width - diameter) * 0.5f;

            Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI * Math.Clamp(reveal, 0f, 1f));
            topHalf.arrow.rotation = turn;
            bottomHalf.arrow.rotation = turn;

            if (!viewport.hidden) viewport.Arrange(new LayoutRect(finalRect.x, seam, finalRect.width, shown));
            topRule.Arrange(new LayoutRect(finalRect.x, seam - 1f, finalRect.width, 1f));
            bottomRule.Arrange(new LayoutRect(finalRect.x, seam + shown - 1f, finalRect.width, 1f));
            topHalf.Arrange(new LayoutRect(gripX, finalRect.y, diameter, radius));
            bottomHalf.Arrange(new LayoutRect(gripX, seam + shown, diameter, radius));
            SetFlag(ArrangeFlags.ArrangeDirty, false);
        }
        #endregion

        #region ---- parts ----
        private static PanelControl Rule() => new PanelControl
        {
            role = PaletteRole.Line,
            hitTestable = false
        };

        // Cuts the content at the rules, holding it against the lower one so it slides with it.
        private class Viewport : Control
        {
            public void Set(Control content)
            {
                foreach (Entity child in children.ToList()) child.Destroy();
                base.AddChild(content);
            }

            protected override Vector2 MeasureCore(Vector2 availableSize)
            {
                arrange.desired = children.Count > 0 && children[0] is Control content ? content.Measure(availableSize) : Vector2.Zero;
                SetFlag(ArrangeFlags.MeasureDirty, false);
                return arrange.desired;
            }

            protected override void ArrangeCore(LayoutRect finalRect)
            {
                WriteArranged(finalRect);
                if (children.Count > 0 && children[0] is Control content)
                    content.Arrange(new LayoutRect(finalRect.x, finalRect.Bottom - content.DesiredSize.Y, finalRect.width, content.DesiredSize.Y));
                SetFlag(ArrangeFlags.ArrangeDirty, false);
            }
        }

        // One half of the grip: a clip the height of half a circle over a whole one.
        private class Half : Control
        {
            public readonly Circle circle;
            private readonly bool top;

            public Action? pressed { set => circle.pressed = value; }
            public IconControl arrow => circle.arrow;

            public Half(bool top)
            {
                this.top = top;
                alpha = 0f;
                circle = new Circle(top);
                AddChild(circle);
            }

            protected override Vector2 MeasureCore(Vector2 availableSize)
            {
                circle.Measure(availableSize);
                arrange.desired = new Vector2(diameter, diameter * 0.5f);
                SetFlag(ArrangeFlags.MeasureDirty, false);
                return arrange.desired;
            }

            protected override void ArrangeCore(LayoutRect finalRect)
            {
                WriteArranged(finalRect);
                circle.Arrange(new LayoutRect(finalRect.x, top ? finalRect.y : finalRect.y - diameter * 0.5f, diameter, diameter));
                SetFlag(ArrangeFlags.ArrangeDirty, false);
            }
        }

        // The round grip, its arrow in the shown half. Acts on press and leaves the active control
        // where it was, like the format bar's buttons.
        private class Circle : ButtonControl
        {
            public readonly IconControl arrow;
            public Action? pressed;
            private readonly bool top;

            public override bool takesActiveControl => false;

            public Circle(bool top)
            {
                this.top = top;
                role = PaletteRole.Surface;
                edgeRole = PaletteRole.Line;
                edgeThickness = new Thickness(1f);
                cornerRadius = new CornerRadii(diameter * 0.5f);

                arrow = Arrow(top ? "chevron-up" : "chevron-down");
                AddChild(arrow);
            }

            public override void AddChild(Entity entity)
            {
                children.Add(entity);
                entity.parent = this;
                MarkTreeOrderDirty();
                InvalidateLayout();
            }

            public override bool OnPointerPress(PointerEvent e)
            {
                base.OnPointerPress(e);
                if (e.button == PointerEvent.leftButton) pressed?.Invoke();
                return true;
            }

            protected override Vector2 MeasureCore(Vector2 availableSize)
            {
                arrow.Measure(availableSize);
                arrange.desired = new Vector2(diameter, diameter);
                SetFlag(ArrangeFlags.MeasureDirty, false);
                return arrange.desired;
            }

            protected override void ArrangeCore(LayoutRect finalRect)
            {
                WriteArranged(finalRect);
                float middle = finalRect.y + finalRect.height * (top ? 0.25f : 0.75f);
                arrow.Arrange(new LayoutRect(finalRect.x + (finalRect.width - arrowSize) * 0.5f,
                    middle - arrowSize * 0.5f, arrowSize, arrowSize));
                SetFlag(ArrangeFlags.ArrangeDirty, false);
            }

            private static IconControl Arrow(string icon) => new IconControl
            {
                setName = "default",
                iconName = icon,
                preferredWidth = arrowSize,
                preferredHeight = arrowSize,
                hitTestable = false,
                role = PaletteRole.MutedInk
            };
        }
        #endregion
    }
}
