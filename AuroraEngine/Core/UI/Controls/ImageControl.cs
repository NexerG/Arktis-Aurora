using ArctisAurora.Core.Filing.Serialization;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Registry.Assets;
using ArctisAurora.EngineWork.Registry;
using System.Numerics;

namespace ArctisAurora.Core.UI.Controls
{
    // A picture from a file, fitted to the width it is given at its own aspect.
    [A_XSDType("Image", "UI")]
    public class ImageControl : Control
    {
        [A_XSDElementProperty("Source", "UI", "Picture file: an absolute path, or one relative to the data mounts.")]
        public string source
        {
            get => field;
            set
            {
                field = value;
                sampler = string.IsNullOrEmpty(value) ? null
                    : TextureAsset.ForFile(Path.IsPathRooted(value) ? value : VirtualFileSystem.ResolveFile(value));
                InvalidateLayout();
            }
        } = "";

        public ImageControl()
        {
            kind = VulkanControlType.ImageControl;
            colorHex = "#FFFFFF";
        }

        // Sizes from Width/Height at the picture's aspect, or its native size capped to the space.
        protected override Vector2 MeasureCore(Vector2 availableSize)
        {
            Vector2 native = sampler != null ? new Vector2(sampler.width, sampler.height) : Vector2.Zero;
            float aspect = native.X > 0f ? native.Y / native.X : 0f;

            float w, h;
            if (preferredWidth > 0f)
            {
                w = preferredWidth;
                h = preferredHeight > 0f ? preferredHeight : w * aspect;
            }
            else if (preferredHeight > 0f)
            {
                h = preferredHeight;
                w = aspect > 0f ? h / aspect : 0f;
            }
            else
            {
                w = MathF.Min(native.X, availableSize.X);
                h = w * aspect;
            }

            arrange.desired = new Vector2(w, h);
            SetFlag(ArrangeFlags.MeasureDirty, false);
            return arrange.desired;
        }
    }
}
