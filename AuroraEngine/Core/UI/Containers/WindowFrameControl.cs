using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Registry;
using ArctisAurora.EngineWork;
using ArctisAurora.EngineWork.Rendering;
using Silk.NET.GLFW;
using System.Numerics;

namespace ArctisAurora.Core.UI.Containers
{
    // Resize edges for a window created undecorated. The content fills the frame edge to edge and
    // four invisible strips overlay the outermost pixels, appended so the hit-test reaches them
    // first. The engine needs to know nothing about window chrome.
    [A_XSDType("WindowFrame", "UI")]
    public unsafe class WindowFrameControl : ContainerControl
    {
        private const float band = 4f;
        private const int minFrameWidth = 320;
        private const int minFrameHeight = 240;

        private readonly Control[] grips = new Control[4];

        private CursorShape shown = CursorShape.Arrow;
        private bool left, right, top, bottom;
        private Vector2 grab;
        private int grabX, grabY, grabW, grabH;

        public WindowFrameControl()
        {
            role = PaletteRole.Ground;
        }

        private bool IsGrip(Control control) => Array.IndexOf(grips, control) >= 0;

        protected override Vector2 MeasureCore(Vector2 availableSize)
        {
            LayoutRect inner = new LayoutRect(0, 0, availableSize.X, availableSize.Y)
                .Shrink(arrange.padding);

            foreach (Entity e in children)
                if (e is Control child && !IsGrip(child))
                    child.Measure(new Vector2(inner.width, inner.height));

            arrange.desired = availableSize;
            SetFlag(ArrangeFlags.MeasureDirty, false);
            return arrange.desired;
        }

        protected override void ArrangeCore(LayoutRect finalRect)
        {
            WriteArranged(finalRect);
            EnsureGrips();

            LayoutRect inner = finalRect.Shrink(arrange.padding);

            foreach (Entity e in children)
                if (e is Control child && !IsGrip(child))
                    ArrangeByAlignment(child, inner);

            grips[0].Arrange(new LayoutRect(finalRect.x, finalRect.y, band, finalRect.height));
            grips[1].Arrange(new LayoutRect(finalRect.Right - band, finalRect.y, band, finalRect.height));
            grips[2].Arrange(new LayoutRect(finalRect.x, finalRect.y, finalRect.width, band));
            grips[3].Arrange(new LayoutRect(finalRect.x, finalRect.Bottom - band, finalRect.width, band));

            // A maximized window has no edge worth dragging.
            bool active = !IsMaximized;
            foreach (Control grip in grips) grip.hitTestable = active;

            SetFlag(ArrangeFlags.ArrangeDirty, false);
        }

        // Appended after the authored children, because the hit-test walks last to first and takes
        // the first match. Rebuilt when missing.
        private void EnsureGrips()
        {
            if (grips[0] != null && children.Contains(grips[0])) return;

            for (int i = 0; i < grips.Length; i++)
            {
                Control grip = new PanelControl { alpha = 0f };
                grip.parent = this;
                children.Add(grip);
                grips[i] = grip;

                grip.RegisterOnMove(e => { Hover(e.point); return true; });
                grip.RegisterOnExit(e => { ClearCursor(); return true; });
                grip.RegisterOnPress(e => { BeginResize(grip); return true; });
                grip.RegisterOnDrag(e => ApplyResize());
            }
            MarkTreeOrderDirty();
        }

        // Only on a change — the move callback runs every tick and the shape rarely differs.
        private void Hover(Vector2 pos)
        {
            CursorShape shape = ShapeFor(EdgesAt(pos));
            if (shape == shown) return;

            UIEngine.WindowOf(this)?.os.ChangeCursor(shape);
            shown = shape;
        }

        private void ClearCursor()
        {
            if (shown == CursorShape.Arrow) return;

            UIEngine.WindowOf(this)?.os.ChangeCursor(CursorShape.Arrow);
            shown = CursorShape.Arrow;
        }

        private void BeginResize(Control grip)
        {
            RenderWindow window = UIEngine.WindowOf(this);
            if (window == null) return;

            (left, right, top, bottom) = EdgesAt(Pointer(window));
            if (!(left || right || top || bottom)) return;

            WindowHandle* handle = window.os.handle;
            AGlfwWindow._glfw.GetWindowPos(handle, out grabX, out grabY);
            AGlfwWindow._glfw.GetWindowSize(handle, out grabW, out grabH);
            grab = ScreenPos(window);
            grip.StartDrag();
        }

        // Screen space, so the arithmetic does not shift underneath itself as the window moves. An
        // edge that drags past its minimum stops the far edge rather than pushing the near one.
        private void ApplyResize()
        {
            if (!(left || right || top || bottom)) return;

            RenderWindow window = UIEngine.WindowOf(this);
            if (window == null) return;

            Vector2 now = ScreenPos(window);
            int dx = (int)(now.X - grab.X);
            int dy = (int)(now.Y - grab.Y);

            int x = grabX, y = grabY, w = grabW, h = grabH;

            if (left)
            {
                w = Math.Max(minFrameWidth, grabW - dx);
                x = grabX + (grabW - w);
            }
            else if (right) w = Math.Max(minFrameWidth, grabW + dx);

            if (top)
            {
                h = Math.Max(minFrameHeight, grabH - dy);
                y = grabY + (grabH - h);
            }
            else if (bottom) h = Math.Max(minFrameHeight, grabH + dy);

            window.os.SetBounds(x, y, w, h);
        }

        private static Vector2 Pointer(RenderWindow window) =>
            window.ui.uiRoot.ToDesignSpace(window.mousePos, window.os.windowSize);

        private static Vector2 ScreenPos(RenderWindow window)
        {
            AGlfwWindow._glfw.GetWindowPos(window.os.handle, out int wx, out int wy);
            return new Vector2(wx + window.mousePos.X, wy + window.mousePos.Y);
        }

        private (bool, bool, bool, bool) EdgesAt(Vector2 pos)
        {
            LayoutRect rect = arrangedRect;
            return (pos.X - rect.x <= band, rect.Right - pos.X <= band,
                    pos.Y - rect.y <= band, rect.Bottom - pos.Y <= band);
        }

        private static CursorShape ShapeFor((bool l, bool r, bool t, bool b) e)
        {
            if ((e.l && e.t) || (e.r && e.b)) return CursorShape.NwseResize;
            if ((e.r && e.t) || (e.l && e.b)) return CursorShape.NeswResize;
            if (e.l || e.r) return CursorShape.HResize;
            if (e.t || e.b) return CursorShape.VResize;
            return CursorShape.Arrow;
        }

        // Read during Arrange, which can run before the tree is attached to a window.
        private bool IsMaximized
        {
            get
            {
                RenderWindow window = UIEngine.WindowOf(this);
                return window != null
                    && AGlfwWindow._glfw.GetWindowAttrib(window.os.handle, WindowAttributeGetter.Maximized);
            }
        }
    }
}
