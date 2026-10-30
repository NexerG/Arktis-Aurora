using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Text;
using System.Globalization;
using System.Numerics;

namespace ArctisAurora.Core.UI.Controls
{
    // A colour chosen from a saturation/brightness field and a hue strip, or typed as hex.
    [A_XSDType("ColorPicker", "UI")]
    public class ColorPickerControl : ContainerControl
    {
        // a drag released or a hex committed
        public Action<string>? onPicked;

        // part sizes
        private const float fieldWidth = 168f;
        private const float fieldHeight = 120f;
        private const float stripWidth = 16f;
        private const float gap = 8f;
        private const float rowHeight = 24f;
        private const float handleSize = 10f;
        private const float stripHandleHeight = 4f;

        // parts, in paint order
        private readonly PanelControl field = new PanelControl();
        private readonly PanelControl whiteWash = new PanelControl();
        private readonly PanelControl blackWash = new PanelControl();
        private readonly PanelControl fieldHandle = new PanelControl();
        private readonly PanelControl strip = new PanelControl();
        private readonly PanelControl stripHandle = new PanelControl();
        private readonly PanelControl swatch = new PanelControl();
        private readonly TextBoxControl hexBox = new TextBoxControl();

        // the colour in HSV, so the handles keep their place through greys
        private float hue;
        private float saturation;
        private float brightness;

        private enum Part { None, Field, Strip }
        private Part dragging;
        private LayoutRect fieldRect;
        private LayoutRect stripRect;

        [A_XSDElementProperty("Hex", "UI", "The colour shown, as #RRGGBB.")]
        public string hex
        {
            get => ToHex(HsvToRgb(hue, saturation, brightness));
            set
            {
                if (!TryParseHex(value, out Vector3 rgb)) return;

                (float h, float s, float v) = RgbToHsv(rgb);
                if (s > 0f) hue = h;
                saturation = s;
                brightness = v;
                Sync();
            }
        }

        public ColorPickerControl()
        {
            horizontalAlignment = HorizontalAlignment.Left;
            verticalAlignment = VerticalAlignment.Top;

            foreach (PanelControl part in new[] { field, whiteWash, blackWash, fieldHandle, strip, stripHandle, swatch })
                part.hitTestable = false;

            foreach (PanelControl ramp in new[] { whiteWash, blackWash, strip })
                ramp.role = PaletteRole.Ground;
            whiteWash.gradient = "picker-white";
            blackWash.gradient = "picker-black";
            strip.gradient = "picker-hue";

            Handle(fieldHandle, handleSize * 0.5f);
            Handle(stripHandle, 1f);
            swatch.cornerRadius = new CornerRadii(3f);

            hexBox.preferredHeight = rowHeight;
            hexBox.fontSize = 13;
            hexBox.role = PaletteRole.Field;
            hexBox.onCommit = CommitHex;
            hexBox.onCancel = Sync;

            foreach (Control part in new Control[] { field, whiteWash, blackWash, fieldHandle, strip, stripHandle, swatch, hexBox })
                AddChild(part);

            hex = "#FFFFFF";
        }

        // Filled with the colour it marks, so it needs no transparent centre.
        private static void Handle(PanelControl handle, float radius)
        {
            handle.edgeThickness = new Thickness(2f);
            handle.edgeColorHex = "#FFFFFF";
            handle.cornerRadius = new CornerRadii(radius);
        }

        #region ---- pointer ----
        public override bool OnPointerPress(PointerEvent e)
        {
            if (e.button != PointerEvent.leftButton) return false;

            dragging = fieldRect.Contains(e.point) ? Part.Field
                     : stripRect.Contains(e.point) ? Part.Strip
                     : Part.None;
            if (dragging == Part.None) return false;

            PickAt(e.point);
            StartDrag();
            return true;
        }

        public override void OnDrag(PointerEvent e)
        {
            base.OnDrag(e);
            PickAt(e.point);
        }

        public override void OnDragStop(bool accepted)
        {
            base.OnDragStop(accepted);
            if (dragging == Part.None) return;

            dragging = Part.None;
            onPicked?.Invoke(hex);
        }

        private void PickAt(Vector2 point)
        {
            if (dragging == Part.Field)
            {
                saturation = Math.Clamp((point.X - fieldRect.x) / fieldRect.width, 0f, 1f);
                brightness = 1f - Math.Clamp((point.Y - fieldRect.y) / fieldRect.height, 0f, 1f);
            }
            else if (dragging == Part.Strip)
                hue = Math.Clamp((point.Y - stripRect.y) / stripRect.height, 0f, 1f) * 360f % 360f;

            Sync();
        }

        private void CommitHex(string value)
        {
            if (!TryParseHex(value, out _))
            {
                Sync();
                return;
            }

            hex = value;
            onPicked?.Invoke(hex);
        }
        #endregion

        // Repaints the parts from the HSV state.
        private void Sync()
        {
            field.colorHex = ToHex(HsvToRgb(hue, 1f, 1f));
            stripHandle.colorHex = field.colorHex;
            fieldHandle.colorHex = hex;
            swatch.colorHex = hex;
            if (!hexBox.isEditing) hexBox.text = hex;
            InvalidateArrange();
        }

        #region ---- colour ----
        // #RRGGBB, or #RRGGBBAA with the alpha dropped; the hash is optional.
        public static bool TryParseHex(string? value, out Vector3 rgb)
        {
            rgb = Vector3.Zero;
            if (value == null) return false;

            string digits = value.Trim().TrimStart('#');
            if (digits.Length == 8) digits = digits[..6];
            if (digits.Length != 6 || !int.TryParse(digits, NumberStyles.HexNumber, null, out int packed)) return false;

            rgb = new Vector3((packed >> 16 & 0xFF) / 255f, (packed >> 8 & 0xFF) / 255f, (packed & 0xFF) / 255f);
            return true;
        }

        public static string ToHex(Vector3 rgb) =>
            $"#{(int)MathF.Round(rgb.X * 255f):X2}{(int)MathF.Round(rgb.Y * 255f):X2}{(int)MathF.Round(rgb.Z * 255f):X2}";

        public static Vector3 HsvToRgb(float h, float s, float v)
        {
            float c = v * s;
            float x = c * (1f - MathF.Abs(h / 60f % 2f - 1f));
            float m = v - c;

            (float r, float g, float b) = (h % 360f) switch
            {
                < 60f => (c, x, 0f),
                < 120f => (x, c, 0f),
                < 180f => (0f, c, x),
                < 240f => (0f, x, c),
                < 300f => (x, 0f, c),
                _ => (c, 0f, x)
            };
            return new Vector3(r + m, g + m, b + m);
        }

        public static (float h, float s, float v) RgbToHsv(Vector3 rgb)
        {
            float max = MathF.Max(rgb.X, MathF.Max(rgb.Y, rgb.Z));
            float min = MathF.Min(rgb.X, MathF.Min(rgb.Y, rgb.Z));
            float d = max - min;

            float h = d == 0f ? 0f
                    : max == rgb.X ? 60f * ((rgb.Y - rgb.Z) / d % 6f)
                    : max == rgb.Y ? 60f * ((rgb.Z - rgb.X) / d + 2f)
                    : 60f * ((rgb.X - rgb.Y) / d + 4f);
            if (h < 0f) h += 360f;

            return (h, max == 0f ? 0f : d / max, max);
        }
        #endregion

        #region ---- layout ----
        protected override Vector2 MeasureCore(Vector2 availableSize)
        {
            hexBox.Measure(new Vector2(fieldWidth + gap + stripWidth, rowHeight));
            arrange.desired = new Vector2(fieldWidth + gap + stripWidth, fieldHeight + gap + rowHeight)
                            + new Vector2(padding.totalHorizontal, padding.totalVertical);
            SetFlag(ArrangeFlags.MeasureDirty, false);
            return arrange.desired;
        }

        protected override void ArrangeCore(LayoutRect finalRect)
        {
            WriteArranged(finalRect);
            LayoutRect inner = finalRect.Shrink(padding);

            fieldRect = new LayoutRect(inner.x, inner.y, fieldWidth, fieldHeight);
            stripRect = new LayoutRect(inner.x + fieldWidth + gap, inner.y, stripWidth, fieldHeight);

            field.Arrange(fieldRect);
            whiteWash.Arrange(fieldRect);
            blackWash.Arrange(fieldRect);
            strip.Arrange(stripRect);

            float hx = fieldRect.x + saturation * fieldRect.width - handleSize * 0.5f;
            float hy = fieldRect.y + (1f - brightness) * fieldRect.height - handleSize * 0.5f;
            fieldHandle.Arrange(new LayoutRect(hx, hy, handleSize, handleSize));

            float sy = stripRect.y + hue / 360f * stripRect.height - stripHandleHeight * 0.5f;
            stripHandle.Arrange(new LayoutRect(stripRect.x - 2f, sy, stripWidth + 4f, stripHandleHeight));

            float rowY = inner.y + fieldHeight + gap;
            swatch.Arrange(new LayoutRect(inner.x, rowY, rowHeight, rowHeight));
            hexBox.Arrange(new LayoutRect(inner.x + rowHeight + gap, rowY, fieldWidth + stripWidth - rowHeight, rowHeight));

            SetFlag(ArrangeFlags.ArrangeDirty, false);
        }
        #endregion
    }
}
