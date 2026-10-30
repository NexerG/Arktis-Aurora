using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Registry.Assets;
using System.Numerics;

namespace ArctisAurora.Core.UI
{
    public enum DrawKind
    {
        Glyph,
        Rect,
        Image,
        Icon
    }

    // One draw call as an export sees it, in layout pixels.
    public struct DrawItem
    {
        public DrawKind kind;

        // glyph: x is the pen, y the baseline
        public FontAsset? font;
        public int face;
        public char character;
        public float size;

        // rect and image box
        public float x, y, width, height;

        // image file and turn; an icon's name
        public string? source;
        public Quaternion rotation;

        public Vector3 color;
        public Vector4 clip;
    }

    // Collects what a subtree draws while it is UIEngine.recorder, in place of quads.
    public sealed class DrawRecorder
    {
        public readonly List<DrawItem> items = new List<DrawItem>();

        public void Glyph(FontAsset font, int face, char character, float size, float pen, float baseline, uint paint, Vector4 clip) =>
            items.Add(new DrawItem
            {
                kind = DrawKind.Glyph, font = font, face = face, character = character, size = size,
                x = pen, y = baseline, color = Palettes.ColorOf(paint), clip = clip
            });

        public void Rect(float x, float y, float width, float height, uint paint, Vector4 clip) =>
            items.Add(new DrawItem
            {
                kind = DrawKind.Rect, x = x, y = y, width = width, height = height,
                color = Palettes.ColorOf(paint), clip = clip
            });

        public void Image(string source, LayoutRect box, Vector2 size, Quaternion rotation, Vector4 clip) =>
            items.Add(new DrawItem
            {
                kind = DrawKind.Image, source = source, rotation = rotation, clip = clip,
                x = box.x + (box.width - size.X) * 0.5f, y = box.y + (box.height - size.Y) * 0.5f, width = size.X, height = size.Y
            });

        // An icon by name over its artwork box.
        public void Icon(string name, LayoutRect box, uint paint, Vector4 clip) =>
            items.Add(new DrawItem
            {
                kind = DrawKind.Icon, source = name, x = box.x, y = box.y, width = box.width, height = box.height,
                color = Palettes.ColorOf(paint), clip = clip
            });
    }
}
