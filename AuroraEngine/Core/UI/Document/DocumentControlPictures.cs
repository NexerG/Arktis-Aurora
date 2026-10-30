using ArctisAurora.Core.Registry.Assets;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Text;
using Silk.NET.GLFW;
using System.Numerics;

namespace ArctisAurora.Core.UI.Document
{
    public partial class DocumentControl
    {
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
    }
}
