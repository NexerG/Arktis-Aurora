using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Testing;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Containers.Tabs;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

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
            using (Image<Rgba32> fixture = new Image<Rgba32>(256, 256))
            {
                fixture.ProcessPixelRows(rows =>
                {
                    for (int y = 0; y < rows.Height; y++)
                    {
                        Span<Rgba32> row = rows.GetRowSpan(y);
                        for (int x = 0; x < 128; x++)
                            row[x] = x % 2 == 0 ? new Rgba32(0, 0, 0) : new Rgba32(255, 255, 255);
                        for (int x = 128; x < 256; x++)
                            row[x] = new Rgba32((byte)(255 - (x - 128) * 2), 0, (byte)((x - 128) * 2));
                    }
                });
                fixture.SaveAsPng(path);
            }

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
