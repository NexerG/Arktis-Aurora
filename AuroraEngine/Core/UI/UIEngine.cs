using ArctisAurora.Core.Animation;
using ArctisAurora.Core.Data;
using ArctisAurora.Core.Diagnostics;
using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Document;
using ArctisAurora.Core.UI.Menus;
using ArctisAurora.Core.UI.Planner;
using ArctisAurora.Core.UI.Sheets;
using ArctisAurora.Core.UI.Tex;
using ArctisAurora.EngineWork;
using ArctisAurora.EngineWork.Registry;
using ArctisAurora.EngineWork.Rendering;
using ArctisAurora.EngineWork.Rendering.Modules;
using System.Numerics;
using Silk.NET.Vulkan;
using System.Diagnostics;
    
namespace ArctisAurora.Core.UI
{
    // Entry point of the UI, on the main thread.
    public static class UIEngine
    {
        private static readonly LogChannel Log = LogChannel.For("UIEngine");

        private static DataPool _elements;
        private static DataPool _quads;
        private static DataPool _layoutDirty;

        public static DataPool Elements => _elements ??= DataManager.Get("UIElements");
        public static DataPool Quads => _quads ??= DataManager.Get("UIQuads");
        private static DataPool LayoutDirty => _layoutDirty ??= DataManager.Get("LayoutDirty");

        // set while an export walks a tree; Emit records into it instead of writing quads
        internal static DrawRecorder? recorder;

        // window pixels per design unit for the window being emitted
        internal static float pixelScale = 1f;

        #region ---- layout ----
        private static readonly HashSet<Control> _dirtyRoots = new HashSet<Control>();

        public static void RegisterDirtyRoot(Control control) => _dirtyRoots.Add(control);

        // Measures and arranges every dirty subtree.
        public static void ResolveLayout()
        {
            LayoutEngine.BuildStructure();
            DrainLayoutDirty();
            foreach (RenderWindow window in Engine.windows.Values)
                if (window.ui?.uiRoot is WindowRoot windowRoot && (windowRoot.isMeasureDirty || windowRoot.isArrangeDirty))
                    _dirtyRoots.Add(windowRoot);
            if (_dirtyRoots.Count == 0) return;

            Control[] roots = new Control[_dirtyRoots.Count];
            _dirtyRoots.CopyTo(roots);
            _dirtyRoots.Clear();

            foreach (Control root in roots)
            {
                // A control with an owner is not a root. Every control registers itself when it is
                // constructed, before it is attached; resolving one of those here measures it at
                // infinity and arranges it at the origin, behind the owner that lays it out.
                if (root.parent is Control || root.destroyed) continue;

                Profiling.Zone.Increment("Root");

                if (root.isMeasureDirty)
                {
                    // Pass 1 — offer the root its own current arranged size, or infinite if it has
                    // never been arranged.
                    Vector2 offer = root.arrangedRect.size == Vector2.Zero
                        ? new Vector2(float.MaxValue, float.MaxValue)
                        : root.arrangedRect.size;

                    Profiling.Zone.Start("Layout.Measure");
                    root.Measure(offer);
                    Profiling.Zone.End("Layout.Measure");

                    // Pass 2 — re-arrange from the root's current rect. A window root is fitted
                    // externally, on resize.
                    LayoutRect finalRect = root.arrangedRect.size == Vector2.Zero
                        ? new LayoutRect(0, 0, root.DesiredSize.X, root.DesiredSize.Y)
                        : root.arrangedRect;

                    Profiling.Zone.Start("Layout.Arrange");
                    root.Arrange(finalRect);
                    Profiling.Zone.End("Layout.Arrange");
                }
                else if (root.isArrangeDirty)
                {
                    Profiling.Zone.Start("Layout.Arrange");
                    root.Arrange(root.arrangedRect);
                    Profiling.Zone.End("Layout.Arrange");
                }

                Profiling.Zone.Start("Layout.VerifyCache");
                VerifySubtreeCache(root);
                Profiling.Zone.End("Layout.VerifyCache");
            }

            LayoutEngine.VerifyLayout(roots);
        }

        // Invalidates the controls Animation wrote this frame and could not mark itself.
        private static void DrainLayoutDirty()
        {
            DataPool pool = LayoutDirty;
            int count = pool.Count;
            if (count == 0) return;

            Profiling.Zone.Start("Layout.Drain");
            DataPool elements = Elements;
            DirtyLayout[] rows = pool.Backing<DirtyLayout>();
            for (int i = 0; i < count; i++)
            {
                int dense = elements.DenseOf(rows[i].target);
                if (dense < 0 || elements.OwnerAt(dense) is not Control control || control.destroyed) continue;

                if (rows[i].change == LayoutChange.Measure) control.InvalidateLayout();
                else control.InvalidateArrange();
            }
            pool.Rewind();
            Profiling.Zone.End("Layout.Drain");
        }

        // Recomputes the caches independently and reports a mismatch. The maintained values are the
        // hit-test's early-out, so a wrong one loses clicks rather than drawing anything odd.
        [Conditional("DEBUG")]
        private static void VerifySubtreeCache(Control control)
        {
            LayoutRect bounds = control.rotation.IsIdentity ? control.arrangedRect : control.arrangedRect.Turned(control.rotation);

            foreach (Entity e in control.children)
            {
                if (e is not Control child) continue;

                VerifySubtreeCache(child);
                bounds = LayoutRect.Union(bounds, child.arrange.subtreeBounds);
            }

            ref ArrangeData a = ref control.arrange;
            if (a.subtreeBounds.x != bounds.x || a.subtreeBounds.y != bounds.y
                || a.subtreeBounds.width != bounds.width || a.subtreeBounds.height != bounds.height)
                Log.Error($"'{control.name}' subtreeBounds ({a.subtreeBounds.x}, {a.subtreeBounds.y}, " +
                          $"{a.subtreeBounds.width}, {a.subtreeBounds.height}) != recomputed " +
                          $"({bounds.x}, {bounds.y}, {bounds.width}, {bounds.height})");
        }
        #endregion

        #region ---- input ----
        // One pointer, so these are global.
        [A_ActiveContext("Hovering")]
        public static Control hovering { get; set; }

        [A_ActiveContext("ActiveControl")]
        public static Control activeControl { get; set; }

        [A_ActiveContext("PressTarget")]
        public static Control pressTarget { get; set; }

        // The control that claimed the drag. Nothing delivers to it yet — see the drag gap in
        // ClaudeMemory/Decisions/ui-engine-stack.md.
        [A_ActiveContext("Dragging")]
        public static Control dragging { get; set; }

        // what the drag is currently over, so it can be told when the drag leaves it
        private static Control _dragTarget;

        // the drag target's own window-space point, so the release does not have to recompute it
        private static Vector2 _dragPoint;

        // what consumed the last scroll, told when its gesture ends
        private static Control scrollOwner;

        // The window the pointer is over while a drag runs, null when it is over none or when an
        // overlap makes the answer ambiguous. Only meaningful mid-drag — a captured pointer is what
        // makes the geometry search necessary, and it costs a position query per window per tick.
        [A_ActiveContext("MouseOverWindow")]
        public static RenderWindow mouseOverWindow { get; set; }

        [A_ActiveContext("ActiveWindow")]
        public static RenderWindow activeWindow { get; set; }

        private static Vector2 _lastPoint;
        private static bool _sameTargetTap;

        // a drag released this close to its press is also a click
        private const float clickSlop = 4f;
        private static Vector2 _pressPoint;

        // Resolves the pointer against one window's tree and dispatches what the buttons did.
        public static void Poll(RenderWindow window)
        {
            WindowRoot root = window.ui?.uiRoot;
            if (root == null) return;

            Vector2 point = root.ToDesignSpace(window.mousePos, window.os.windowSize);
            Vector2 delta = point - _lastPoint;
            _lastPoint = point;

            // A pressed button captures the pointer to the window it went down in, which keeps
            // reporting positions far outside itself and stops every other window hearing anything.
            // So the drag's own window drives the whole gesture, wherever the pointer has gone.
            bool ownsDrag = dragging != null && ReferenceEquals(WindowOf(dragging), window);
            bool inWindow = window.isInWindow && !window.hidden;

            // The pointer left this window, so nothing of ours is under it any more. Scoped to our
            // own tree, because every window polls and the contexts are global.
            if (!inWindow && !ownsDrag)
            {
                if (RootOf(hovering) == root) SetHovering(null, point, delta);
                return;
            }

            if (inWindow) SolveHover(point, delta, root);

            if (ownsDrag && dragging.parent is not WindowFrameControl) SolveDragWindow(window);

            InputHandler.PointerState pointer = InputHandler.instance.primaryPointer;
            if (pointer != null)
                for (int button = PointerEvent.leftButton; button <= PointerEvent.rightButton; button++)
                {
                    if (pointer.JustPressed(button)) SolvePress(point, delta, button);
                    if (pointer.JustReleased(button)) SolveRelease(point, delta, button, pointer.taps[button]);
                }

            // Claimant before target, so a lost release is caught before anything is offered a drop.
            if (ownsDrag)
            {
                SolveDrag(point, delta);
                CheckDrag(window);
            }

            if (window.scrollDelta.X != 0 || window.scrollDelta.Y != 0)
                SolveScroll(point, window.scrollDelta);
            if (InputHandler.instance.scrollEnded && scrollOwner != null)
            {
                Control owner = scrollOwner;
                scrollOwner = null;
                owner.OnPointerScrollEnd();
            }
        }

        private static void SolveHover(Vector2 point, Vector2 delta, WindowRoot root)
        {
            Control deepest = HitTest(root, point);
            if (ReferenceEquals(deepest, root)) deepest = null;

            SetHovering(deepest, point, delta);

            if (hovering != null)
                Dispatch(Event(hovering, point, delta, 0, 0), PointerPhase.Move);
        }

        private static void SetHovering(Control control, Vector2 point, Vector2 delta)
        {
            if (ReferenceEquals(hovering, control)) return;

            Control previous = hovering;
            (previous as IContext)?.OnContextRemoved("Hovering");
            if (previous != null)
                Dispatch(Event(previous, point, delta, 0, 0), PointerPhase.Exit);

            Context.Set("Hovering", control);
            (control as IContext)?.OnContextAdded("Hovering");
            if (control != null)
                Dispatch(Event(control, point, delta, 0, 0), PointerPhase.Enter);
        }

        private static void SolvePress(Vector2 point, Vector2 delta, int button)
        {
            ContextMenus.DismissUnlessInside(hovering);
            if (hovering == null) return;

            if (button == PointerEvent.leftButton)
            {
                _pressPoint = point;
                Control target = hovering.ActiveContextTarget();
                Control previous = pressTarget;
                _sameTargetTap = ReferenceEquals(target, previous);
                if (!_sameTargetTap)
                {
                    Context.Set("PressTarget", target);
                    (previous as IContext)?.OnContextRemoved("PressTarget");
                    (target as IContext)?.OnContextAdded("PressTarget");
                }
                if (target?.takesActiveControl != false) SetActiveControl(target);
            }

            Dispatch(Event(hovering, point, delta, button, 0), PointerPhase.Press);
        }

        // A release only counts on the control the press landed on, so dragging off a button cancels it.
        private static void SolveRelease(Vector2 point, Vector2 delta, int button, int tapCount)
        {
            // Ahead of the guards below: a drag ends wherever the pointer is, which is rarely still
            // over the control the press landed on.
            if (button == PointerEvent.leftButton && dragging != null)
            {
                EndDrag();
                if (Vector2.Distance(point, _pressPoint) > clickSlop) return;
            }

            if (hovering == null) return;
            if (button == PointerEvent.leftButton && !ReferenceEquals(hovering.ActiveContextTarget(), pressTarget)) return;

            Dispatch(Event(hovering, point, delta, button, tapCount), PointerPhase.Release);

            if (button == PointerEvent.leftButton && tapCount >= 2 && _sameTargetTap)
                Dispatch(Event(hovering, point, delta, button, tapCount), PointerPhase.Tap);

            if (button == PointerEvent.rightButton)
                ContextMenus.OpenOn(hovering, point);
        }

        // Which window the pointer is over, and what that means for focus. An overlap is not
        // resolvable — GLFW publishes no z-order — so an ambiguous answer changes nothing rather
        // than guessing, and only an unambiguous one moves the active window.
        private static unsafe void SolveDragWindow(RenderWindow source)
        {
            int count = WindowsAt(ScreenPoint(source), out RenderWindow single);

            SetMouseOverWindow(count == 1 ? single : null);

            if (count == 0) SetActiveWindow(null);
            else if (count == 1 && !ReferenceEquals(single, activeWindow))
            {
                single.os.Raise();
                SetActiveWindow(single);
            }
        }

        // How many windows hold the point, and which one when that is exactly one.
        private static unsafe int WindowsAt(Vector2 screen, out RenderWindow single)
        {
            single = null;
            int count = 0;

            foreach (RenderWindow window in Engine.windows.Values)
            {
                // the preview sits under the pointer by definition, so it is never a drop target
                if (window.closeRequested || window.isGhost) continue;

                AGlfwWindow._glfw.GetWindowPos(window.os.handle, out int x, out int y);
                Extent2D size = window.os.windowSize;
                if (screen.X < x || screen.Y < y || screen.X >= x + size.Width || screen.Y >= y + size.Height)
                    continue;

                count++;
                single = window;
            }
            return count;
        }

        private static unsafe Vector2 ScreenPoint(RenderWindow source)
        {
            AGlfwWindow._glfw.GetWindowPos(source.os.handle, out int sx, out int sy);
            return new Vector2(sx + source.mousePos.X, sy + source.mousePos.Y);
        }

        private static void SetMouseOverWindow(RenderWindow window)
        {
            if (ReferenceEquals(mouseOverWindow, window)) return;

            bool wasIn = mouseOverWindow != null;
            Context.Set("MouseOverWindow", window);

            if (wasIn) dragging?.DraggedOutOfWindow();
            if (window != null) dragging?.DraggedIntoWindow();
        }

        private static void SetActiveWindow(RenderWindow window)
        {
            if (ReferenceEquals(activeWindow, window)) return;
            Context.Set("ActiveWindow", window);
        }

        // The control under the pointer mid-drag, in the target window's own design space. Found by
        // geometry rather than by hover: a captured pointer means no other window is ever told the
        // pointer is over it.
        private static unsafe Control HitFor(RenderWindow source, out Vector2 local)
        {
            local = Vector2.Zero;

            RenderWindow target = mouseOverWindow;
            WindowRoot root = target?.ui?.uiRoot;
            if (root == null) return null;

            Vector2 screen = ScreenPoint(source);
            AGlfwWindow._glfw.GetWindowPos(target.os.handle, out int tx, out int ty);

            local = root.ToDesignSpace(new Vector2(screen.X - tx, screen.Y - ty), target.os.windowSize);
            return HitTest(root, local, dragging);
        }

        // What the drag is over, and the offer to whatever that is. The hover's own walk, minus the
        // dragged subtree — it sits under the pointer by definition, so it would answer every time.
        // One control is the target, as one control is hovered; the events bubble from it.
        private static void CheckDrag(RenderWindow source)
        {
            if (dragging == null) return;

            Control target = HitFor(source, out Vector2 point);
            _dragPoint = point;

            if (!ReferenceEquals(target, _dragTarget))
            {
                for (Control c = _dragTarget; c != null; c = c.parent as Control)
                    if (c.DraggingOverEnd(dragging)) break;

                _dragTarget = target;

                for (Control c = target; c != null; c = c.parent as Control)
                    if (c.DraggingOverStart(dragging, point)) break;
            }

            for (Control c = target; c != null; c = c.parent as Control)
                if (c.DraggingOver(dragging, point)) break;
        }

        // The window a control is drawn into — its root is some window's ui root. Null for a
        // subtree detached from every window.
        public static RenderWindow WindowOf(Control control)
        {
            Control root = RootOf(control);
            if (root == null) return null;

            foreach (RenderWindow window in Engine.windows.Values)
                if (ReferenceEquals(window.ui?.uiRoot, root))
                    return window;
            return null;
        }

        // Ends the drag: the target hears the drag leave, the drop is offered up its ancestors until
        // one takes it, then the claimant hears the gesture end and whether anything took it. The
        // contexts are cleared first, so a handler asking whether a drag is live gets no.
        public static void EndDrag()
        {
            Control target = _dragTarget;
            Control dragged = dragging;

            _dragTarget = null;
            SetMouseOverWindow(null);
            SetDragging(null);

            for (Control c = target; c != null; c = c.parent as Control)
                if (c.DraggingOverEnd(dragged)) break;

            bool accepted = false;
            for (Control c = target; c != null && !accepted; c = c.parent as Control)
                accepted = c.FinishDrag(dragged, _dragPoint);

            dragged?.OnDragStop(accepted);
        }

        // Feeds the claimant each tick, and abandons a claim whose release went unseen. The lost
        // release ends the gesture without offering a drop, because where it happened is unknown.
        private static void SolveDrag(Vector2 point, Vector2 delta)
        {
            if (dragging == null) return;

            if (!InputHandler.instance.IsKeyDown(Keys.MouseLeft))
            {
                Control stale = dragging;

                for (Control c = _dragTarget; c != null; c = c.parent as Control)
                    if (c.DraggingOverEnd(stale)) break;

                _dragTarget = null;
                SetMouseOverWindow(null);
                SetDragging(null);
                stale.OnDragStop(false);
                return;
            }

            dragging.OnDrag(Event(dragging, point, delta, PointerEvent.leftButton, 0));
        }

        // The wheel walks up from the hovered control until something consumes it.
        private static void SolveScroll(Vector2 point, Vector2 offset)
        {
            if (InputHandler.instance.IsModifierDown(InputModifier.Zoom))
            {
                if (InputHandler.instance.IsModifierDown(InputModifier.AppZoom))
                {
                    if (offset.Y > 0) UIScaling.ZoomIn();
                    else if (offset.Y < 0) UIScaling.ZoomOut();
                }
                else if (offset.Y != 0)
                    ZoomContent(offset.Y > 0);
                return;
            }
            if (hovering == null) return;
            PointerEvent e = Event(hovering, point, offset, 0, 0);
            for (Control control = hovering; control != null; control = control.parent as Control)
                if (control.OnPointerScroll(e))
                {
                    scrollOwner = control;
                    return;
                }
        }

        // Steps the zoom of the editor under the pointer.
        private static void ZoomContent(bool larger)
        {
            for (Control c = hovering; c != null; c = c.parent as Control)
            {
                if (c is DocumentEditorControl { zoomOverride: float own })
                {
                    for (Control t = c; t != null; t = t.parent as Control)
                        if (t is TexEditorControl tex)
                        {
                            tex.SetPreviewZoom((int)Math.Clamp(MathF.Round(own * 100f) + (larger ? 10f : -10f), 25f, 400f));
                            return;
                        }
                    return;
                }
                if (c is DocumentEditorControl or SheetEditorControl or PlannerEditorControl)
                {
                    if (larger) DocumentEditorControl.ZoomIn();
                    else DocumentEditorControl.ZoomOut();
                    return;
                }
            }
        }

        // Also called with no click behind it, by anything that has to hold the active context before
        // the pointer ever reaches it.
        public static void SetActiveControl(Control control)
        {
            if (ReferenceEquals(activeControl, control)) return;

            Control previous = activeControl;
            Context.Set("ActiveControl", control);
            (previous as IContext)?.OnContextRemoved("ActiveControl");
            (control as IContext)?.OnContextAdded("ActiveControl");
        }

        // Assigns the drag context and notifies both sides. Null ends the claim.
        public static void SetDragging(Control control)
        {
            if (ReferenceEquals(dragging, control)) return;

            Control previous = dragging;
            Context.Set("Dragging", control);
            (previous as IContext)?.OnContextRemoved("Dragging");
            (control as IContext)?.OnContextAdded("Dragging");
        }

        // Drops a destroyed control out of every context holding it. Assigns directly — the control
        // is going away, so notifying it is the thing to avoid.
        public static void Forget(Control control)
        {
            if (ReferenceEquals(hovering, control)) hovering = null;
            if (ReferenceEquals(activeControl, control)) activeControl = null;
            if (ReferenceEquals(pressTarget, control)) pressTarget = null;
            if (ReferenceEquals(dragging, control)) dragging = null;
            if (ReferenceEquals(_dragTarget, control)) _dragTarget = null;
            if (ReferenceEquals(scrollOwner, control)) scrollOwner = null;
            Context.Forget(control);
        }

        // Deepest control whose clip and box hold the point. Children last to first, because depth
        // testing is off and the later sibling is the one drawn on top. skip takes a whole subtree
        // out of the answer, rejected at its root so nothing beneath it is reached either.
        public static Control HitTest(Control control, Vector2 point, Control skip = null)
        {
            if (control.hidden || ReferenceEquals(control, skip)) return null;
            if (!control.arrange.subtreeBounds.Contains(point)) return null;

            for (int i = control.children.Count - 1; i >= 0; i--)
            {
                if (control.children[i] is not Control child || !child.hitTestable) continue;

                Control deeper = HitTest(child, point, skip);
                if (deeper != null) return deeper;
            }
            return HitsNode(control, point) ? control : null;
        }

        // The clip is inherited, so a point outside it rules the control out wherever it was arranged.
        private static bool HitsNode(Control control, Vector2 point)
        {
            ref ArrangeData a = ref control.arrange;
            if (!a.clip.Contains(point)) return false;

            Vector2 local = ((ArrangeFlags)a.flags & ArrangeFlags.Rotated) != 0 ? a.arranged.Unturned(point, control.rotation) : point;
            return a.arranged.Contains(local) && control.HitsShape(local);
        }

        private static void Dispatch(PointerEvent e, PointerPhase phase)
        {
            Control control = e.target;
            while (control != null)
            {
                bool consumed = phase switch
                {
                    PointerPhase.Enter => control.OnPointerEnter(e),
                    PointerPhase.Exit => control.OnPointerExit(e),
                    PointerPhase.Move => control.OnPointerMove(e),
                    PointerPhase.Press => control.OnPointerPress(e),
                    PointerPhase.Release => control.OnPointerRelease(e),
                    PointerPhase.Tap => control.OnPointerTap(e),
                    PointerPhase.Scroll => control.OnPointerScroll(e),
                    _ => false
                };
                if (consumed) return;
                control = control.parent as Control;
            }
        }

        private static PointerEvent Event(Control target, Vector2 point, Vector2 delta,
                                          int button, int tapCount) =>
            new PointerEvent
            {
                target = target,
                point = point,
                delta = delta,
                button = button,
                tapCount = tapCount,
                pointerId = InputHandler.instance.primaryPointer?.id ?? 0,
                kind = InputHandler.instance.primaryPointer?.kind ?? PointerKind.Mouse
            };

        private static Control RootOf(Control control)
        {
            while (control?.parent is Control parent)
                control = parent;
            return control;
        }
        #endregion

        #region ---- dense order ----
        // Canonical dense order for UIElements = DFS pre-order of the control tree, which is painter
        // order and layout-dependency order at once.
        [A_XSDActionDependency("UI.ElementOrder", "PoolSort")]
        public static IReadOnlyList<int> ElementOrder(DataPool pool)
        {
            int count = pool.Count;
            List<int> order = new List<int>(count);

            HashSet<Control> collected = new HashSet<Control>();
            foreach (RenderWindow window in Engine.windows.Values)
            {
                WindowRoot root = window.ui?.uiRoot;
                if (root == null || !collected.Add(root)) continue;
                CollectDFS(root, order);
            }

            // Detached subtrees are roots too, and no window draws them.
            for (int i = 0; i < count; i++)
            {
                if (pool.OwnerAt(i) is Control control && control.parent is not Control
                    && collected.Add(control))
                    CollectDFS(control, order);
            }
            return order;
        }

        private static void CollectDFS(Control control, List<int> order)
        {
            order.Add(control.dataHandle.StableId);

            foreach (Entity child in control.children)
                if (child is Control childControl)
                    CollectDFS(childControl, order);
        }
        #endregion

        #region ---- draw lists ----
        // below this width or height a control draws but its children do not
        private const float detailCullSize = 12f;

        // Refills every window's UIQuads range from its tree, in DFS pre-order — painter order, the same
        // order the draw pool used to be sorted into. Runs at the frame edge, after Arrange has
        // settled every rect and clip.
        //
        // Unconditional for now. The rebuild is bounded by what fits on screen rather than by the
        // size of the tree, so this is affordable; a dirty flag makes an idle window free later.
        public static void BuildDrawLists()
        {
            DataPool quads = Quads;
            quads.Rewind();

            foreach (RenderWindow window in Engine.windows.Values)
            {
                UIEngineModule ui = window.ui;
                if (ui == null) continue;

                int first = quads.Count;
                float viewport = ui.uiRoot?.ViewportSize(window.os.windowSize).X ?? 0f;
                pixelScale = viewport > 0f ? window.os.windowSize.Width / viewport : 1f;
                Control root = ui.rangeRoot ?? ui.uiRoot;
                int walked = root == null ? 0 : Collect(root, Control.rootDepth);
                int count = quads.Count - first;
                ui.PublishQuadRange(first, count);

                Log.Every(1000).Debug($"'{ui.uiRoot?.name}' walked {walked} controls, " +
                                      $"emitted {count} quads");
            }
        }

        // A subtree whose bounds miss the clip it inherited contributes nothing, so the walk stops
        // there — which is what keeps a scrolled document's cost proportional to the visible part.
        // Returns how many controls it reached.
        internal static int Collect(Control control, float z)
        {
            if (control.hidden) return 0;

            ref ArrangeData a = ref control.arrange;
            if (!a.subtreeBounds.Overlaps(a.clip)) return 0;

            control.InheritPaint();
            control.Emit(z);
            if (a.arranged.width < detailCullSize || a.arranged.height < detailCullSize) return 1;

            return 1 + control.CollectChildren(z + Control.depthStep);
        }
        #endregion
    }
}
