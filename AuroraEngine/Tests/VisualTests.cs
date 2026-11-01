using ArctisAurora.Core.Registry;
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
            t.Show(view);
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
            t.Show(column);
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
    }
}
