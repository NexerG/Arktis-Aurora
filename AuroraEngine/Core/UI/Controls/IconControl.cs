using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Registry.Assets;
using ArctisAurora.EngineWork.Registry;
using System.Numerics;

namespace ArctisAurora.Core.UI.Controls
{
    // One cell of an icon set's MTSDF atlas.
    [A_XSDType("Icon", "UI")]
    public class IconControl : Control
    {
        private static readonly Core.Diagnostics.LogChannel Log = Core.Diagnostics.LogChannel.For("UI");

        private const int defaultSize = 16;

        // the bound set's cell quad over the artwork
        private float cellScale = 1f;

        public override Control? ActiveContextTarget() => (parent as Control)?.ActiveContextTarget();

        [A_XSDElementProperty("Set", "UI", "Icon set to draw from, as named in the asset manifest.")]
        public string setName
        {
            get => field;
            set { field = value; Rebind(); }
        } = "default";

        [A_XSDElementProperty("Icon", "UI", "Icon within the set — the source .svg's file name.")]
        public string iconName
        {
            get => field;
            set { field = value; Rebind(); }
        } = "";

        public IconControl()
        {
            kind = VulkanControlType.MTSDFControl;
            role = PaletteRole.MutedInk;
        }

        // Both attributes arrive one at a time during an XML parse, so this runs on each and does
        // nothing until there is a pair to look up.
        private void Rebind()
        {
            if (string.IsNullOrEmpty(setName) || string.IsNullOrEmpty(iconName)) return;

            IconSetAsset set = AssetRegistries.GetAsset<IconSetAsset>(setName);
            (_, int index) = set.metaData.GetIconAndIndex(iconName);
            if (index < 0)
            {
                Log.Warn($"icon '{iconName}' is not in set '{setName}'.");
                return;
            }

            sampler = set.textureAsset;
            cellScale = set.metaData.cellScale;

            float k = MathF.Ceiling(MathF.Sqrt(set.metaData.iconCount));
            float cellUV = 1f / k;
            float xOffset = index % k * cellUV;
            float yOffset = MathF.Floor(index / k) * cellUV;

            float texelPad = 1f / set.textureAsset.width;

            SetUVRect(xOffset + texelPad, yOffset + texelPad,
                      xOffset + cellUV - texelPad, yOffset + cellUV - texelPad);

            InvalidateLayout();
        }

        // Recorded by name over its artwork box while an export records; otherwise drawn as a quad.
        internal override void Emit(float z)
        {
            if (sampler == null) return;
            if (UIEngine.recorder == null)
            {
                base.Emit(z);
                return;
            }

            ref ArrangeData a = ref arrange;
            if (!a.arranged.Overlaps(a.clip)) return;
            VulkanControl row = visual;
            PaintRow(ref row);
            LayoutRect r = a.arranged;
            float w = r.width / cellScale, h = r.height / cellScale;
            UIEngine.recorder.Icon(iconName, new LayoutRect(r.x + (r.width - w) * 0.5f, r.y + (r.height - h) * 0.5f, w, h), row.paint,
                new Vector4(a.clip.x, a.clip.y, a.clip.Right, a.clip.Bottom));
        }

        // Width/Height are the artwork, not the cell; cellScale is the set's bake margin undone.
        protected override Vector2 MeasureCore(Vector2 availableSize)
        {
            float w = (preferredWidth > 0 ? preferredWidth : defaultSize) * cellScale;
            float h = (preferredHeight > 0 ? preferredHeight : defaultSize) * cellScale;
            arrange.desired = new Vector2(w, h);
            SetFlag(ArrangeFlags.MeasureDirty, false);
            return arrange.desired;
        }
    }
}
