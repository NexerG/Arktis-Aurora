using Arctis.Imaging;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Registry.Assets;
using ArctisAurora.Core.Testing;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Containers.Tabs;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Text;
using Image = Arctis.Imaging.Image;

namespace ArctisAurora.Tests
{
    internal static class VisualTests
    {
        [A_XSDActionDependency("Visual.PanelAndLabel", "Test")]
        private static IEnumerator<int> PanelAndLabel(TestContext t)
        {
            StackPanelControl panel = new StackPanelControl
            {
                preferredWidth = 200f,
                preferredHeight = 80f,
                colorHex = "#2E5C8A",
                cornerRadius = new CornerRadii(8f),
                horizontalAlignment = HorizontalAlignment.Left,
                verticalAlignment = VerticalAlignment.Top
            };
            panel.AddChild(new LabelControl { text = "Golden", fontSize = 16, colorHex = "#FFFFFF" });
            t.Show(panel);
            yield return 2;

            yield return t.Golden("Default", panel);
        }

        [A_XSDActionDependency("Visual.TabStrip", "Test")]
        private static IEnumerator<int> TabStrip(TestContext t)
        {
            TabViewControl view = new TabViewControl
            {
                preferredWidth = 400f,
                preferredHeight = 120f,
                horizontalAlignment = HorizontalAlignment.Left,
                verticalAlignment = VerticalAlignment.Top
            };
            view.AddChild(new TabItemControl { header = "One" });
            view.AddChild(new TabItemControl { header = "Two" });
            t.Show(view).paletteName = "default";
            yield return 2;

            yield return t.Golden("Default", view);
        }

        [A_XSDActionDependency("Visual.Expander", "Test")]
        private static IEnumerator<int> Expander(TestContext t)
        {
            ExpanderControl expander = new ExpanderControl
            {
                preferredWidth = 300f,
                horizontalAlignment = HorizontalAlignment.Left,
                verticalAlignment = VerticalAlignment.Top
            };
            expander.AddChild(new LabelControl { text = "Inside", fontSize = 16 });
            StackPanelControl column = new StackPanelControl
            {
                preferredWidth = 300f,
                preferredHeight = 100f,
                horizontalAlignment = HorizontalAlignment.Left,
                verticalAlignment = VerticalAlignment.Top
            };
            column.AddChild(expander);
            t.Show(column).paletteName = "default";
            yield return 2;
            yield return t.Golden("Closed", column);

            expander.expanded = true;
            yield return 2;
            yield return t.Golden("Open", column);
        }

        [A_XSDActionDependency("Visual.ImageScaled", "Test")]
        private static IEnumerator<int> ImageScaled(TestContext t)
        {
            // fixture: 1 px stripes on the left, a red-to-blue ramp on the right
            string path = Path.Combine(Path.GetTempPath(), "Aurora.Visual.ImageScaled.png");
            Image fixture = new Image(256, 256);
            for (int y = 0; y < 256; y++)
            {
                Span<byte> row = fixture.Row(y);
                for (int x = 0; x < 256; x++)
                {
                    byte stripe = x % 2 == 0 ? (byte)0 : (byte)255;
                    row[x * 4] = x < 128 ? stripe : (byte)(255 - (x - 128) * 2);
                    row[x * 4 + 1] = x < 128 ? stripe : (byte)0;
                    row[x * 4 + 2] = x < 128 ? stripe : (byte)((x - 128) * 2);
                    row[x * 4 + 3] = 255;
                }
            }
            fixture.SavePng(path);

            StackPanelControl panel = new StackPanelControl
            {
                horizontalAlignment = HorizontalAlignment.Left,
                verticalAlignment = VerticalAlignment.Top
            };
            ImageControl full = new ImageControl { source = path, horizontalAlignment = HorizontalAlignment.Left };
            ImageControl quarter = new ImageControl { source = path, preferredWidth = 64f, horizontalAlignment = HorizontalAlignment.Left };
            panel.AddChild(full);
            panel.AddChild(quarter);
            t.Show(panel);
            yield return 2;

            t.Check(full.sampler != null && full.sampler == quarter.sampler, "both controls share one texture for the file");
            t.Check(full.arrangedRect.width == 256f && full.arrangedRect.height == 256f, "unsized, the picture is its native 256x256");
            t.Check(quarter.arrangedRect.height == 64f, "a 64 px width keeps the square aspect");

            yield return t.Golden("Full", full);
            yield return t.Golden("Quarter", quarter);
        }

        [A_XSDActionDependency("Visual.Ktx2StoredMips", "Test")]
        private static IEnumerator<int> Ktx2StoredMips(TestContext t)
        {
            // fixture: a 64x64 chain, level 0 transparent on its left half, every smaller level opaque
            string path = Path.Combine(Path.GetTempPath(), "Aurora.Visual.Ktx2StoredMips.ktx2");
            Image[] levels = new Image[7];
            for (int i = 0, size = 64; i < levels.Length; i++, size >>= 1)
            {
                Image level = new Image(size, size);
                for (int y = 0; y < size; y++)
                {
                    Span<byte> row = level.Row(y);
                    for (int x = 0; x < size; x++)
                    {
                        row[x * 4] = i == 0 ? (byte)255 : (byte)0;
                        row[x * 4 + 1] = i == 0 ? (byte)0 : (byte)255;
                        row[x * 4 + 2] = 0;
                        row[x * 4 + 3] = i == 0 && x < size / 2 ? (byte)0 : (byte)255;
                    }
                }
                levels[i] = level;
            }
            using (FileStream stream = File.Create(path))
                ImageCodec.EncodeKtx2(levels, false, true, stream);

            ImageControl picture = new ImageControl { source = path, preferredWidth = 16f, horizontalAlignment = HorizontalAlignment.Left, verticalAlignment = VerticalAlignment.Top };
            t.Show(picture);
            yield return 2;

            TextureAsset? texture = TextureAsset.ForFile(path);
            t.Check(texture != null && picture.sampler != null, "the KTX2 file loads as a texture");
            t.Check(texture != null && texture.width == 64 && texture.height == 64, "the texture is level 0's 64x64");
            (float left, float right)[]? rows = texture?.OpaqueRows();
            t.Check(rows != null && rows.Length == 64 && rows[0].left == 0.5f && rows[0].right == 1f, "opaque rows come from level 0, opaque on its right half");
            t.Show(new StackPanelControl());
            File.Delete(path);
        }
    }
}
