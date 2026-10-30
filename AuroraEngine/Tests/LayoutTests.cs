using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Registry.Assets;
using ArctisAurora.Core.Testing;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.EngineWork.Registry;
using System.Numerics;

namespace ArctisAurora.Tests
{
    internal static class LayoutTests
    {
        [A_XSDActionDependency("Layout.StackPanelArranges", "Test")]
        private static IEnumerator<int> StackPanelArranges(TestContext t)
        {
            StackPanelControl panel = new StackPanelControl
            {
                Spacing = 4f,
                horizontalAlignment = HorizontalAlignment.Left,
                verticalAlignment = VerticalAlignment.Top
            };
            float[] heights = { 20f, 30f, 40f };
            StackPanelControl[] children = new StackPanelControl[heights.Length];
            for (int i = 0; i < heights.Length; i++)
            {
                children[i] = new StackPanelControl { preferredWidth = 50f, preferredHeight = heights[i] };
                panel.AddChild(children[i]);
            }
            t.Show(panel);
            yield return 2;

            float top = panel.arrangedRect.y;
            t.Check(children[0].arrangedRect.y == top, "first child sits at the panel's top");
            t.Check(children[1].arrangedRect.y == top + 24f, "second child sits 20 + 4 below the first");
            t.Check(children[2].arrangedRect.y == top + 58f, "third child sits 30 + 4 below the second");
            for (int i = 0; i < heights.Length; i++)
                t.Check(children[i].arrangedRect.height == heights[i], $"child {i} keeps its {heights[i]} px height");
            t.Check(panel.DesiredSize.Y == 98f, "panel wants its children's heights plus two gaps");
        }

        [A_XSDActionDependency("Layout.WrapPanelWraps", "Test")]
        private static IEnumerator<int> WrapPanelWraps(TestContext t)
        {
            WrapPanelControl panel = new WrapPanelControl
            {
                Spacing = 4f,
                preferredWidth = 100f,
                horizontalAlignment = HorizontalAlignment.Left,
                verticalAlignment = VerticalAlignment.Top
            };
            StackPanelControl[] children = new StackPanelControl[3];
            for (int i = 0; i < children.Length; i++)
            {
                children[i] = new StackPanelControl { preferredWidth = 40f, preferredHeight = 20f };
                panel.AddChild(children[i]);
            }
            t.Show(panel);
            yield return 2;

            LayoutRect box = panel.arrangedRect;
            t.Check(children[0].arrangedRect.x == box.x && children[0].arrangedRect.y == box.y, "first child sits at the top left");
            t.Check(children[1].arrangedRect.x == box.x + 44f && children[1].arrangedRect.y == box.y, "second child fits beside the first, 40 + 4 along");
            t.Check(children[2].arrangedRect.x == box.x && children[2].arrangedRect.y == box.y + 24f, "third child does not fit in 100 px and starts a line 20 + 4 down");
            t.Check(panel.DesiredSize.Y == 44f, $"panel wants two lines plus a gap: {panel.DesiredSize.Y}");
        }

        [A_XSDActionDependency("Layout.StarChildCrossSize", "Test")]
        private static IEnumerator<int> StarChildCrossSize(TestContext t)
        {
            StackPanelControl bar = new StackPanelControl
            {
                orientation = StackPanelControl.Orientation.Horizontal,
                preferredHeight = 32f,
                horizontalAlignment = HorizontalAlignment.Stretch,
                verticalAlignment = VerticalAlignment.Top
            };
            bar.AddChild(new LabelControl { text = "Untitled", fontSize = 14, widthStar = 1f });
            bar.AddChild(new StackPanelControl { preferredWidth = 32f, preferredHeight = 32f });
            t.Show(bar);
            yield return 2;

            t.Check(bar.DesiredSize.Y == 32f, "an 8-character star label leaves a 32 px bar at 32 px");
            t.Check(bar.arrangedRect.height == 32f, "the bar is arranged 32 px tall");
        }

        [A_XSDActionDependency("Layout.GridAutoRowWrapsAtColumn", "Test")]
        private static IEnumerator<int> GridAutoRowWrapsAtColumn(TestContext t)
        {
            GridListControl grid = new GridListControl
            {
                horizontalAlignment = HorizontalAlignment.Left,
                verticalAlignment = VerticalAlignment.Top
            };
            grid.columnDefinitions.Add(new ColumnDefinition { sizeMode = GridSizeMode.Fixed, value = 100f });
            grid.columnDefinitions.Add(new ColumnDefinition { sizeMode = GridSizeMode.Fixed, value = 100f });
            grid.rowDefinitions.Add(new RowDefinition { sizeMode = GridSizeMode.Auto });

            TextRunControl wrapping = new TextRunControl
            {
                text = "The quick brown fox jumps over the lazy dog",
                gridRow = 0,
                gridColumn = 0
            };
            TextRunControl single = new TextRunControl { text = "x", gridRow = 0, gridColumn = 1 };
            grid.AddChild(wrapping);
            grid.AddChild(single);
            t.Show(grid);
            yield return 2;

            t.Check(wrapping.arrangedRect.width <= 100f, "the text is arranged inside its 100 px column");
            t.Check(wrapping.DesiredSize.Y > single.DesiredSize.Y * 1.5f, "the text wraps at its column's width");
            t.Check(grid.rowDefinitions[0].resolvedSize == wrapping.DesiredSize.Y, "the auto row is as tall as the wrapped text");
        }

        [A_XSDActionDependency("Layout.RotatedHitTest", "Test")]
        private static IEnumerator<int> RotatedHitTest(TestContext t)
        {
            StackPanelControl root = new StackPanelControl
            {
                horizontalAlignment = HorizontalAlignment.Stretch,
                verticalAlignment = VerticalAlignment.Stretch
            };
            StackPanelControl bar = new StackPanelControl
            {
                preferredWidth = 100f,
                preferredHeight = 20f,
                margin = new Thickness(60f),
                horizontalAlignment = HorizontalAlignment.Left,
                verticalAlignment = VerticalAlignment.Top
            };
            root.AddChild(bar);
            t.Show(root);
            yield return 2;

            LayoutRect r = bar.arrangedRect;
            Vector2 centre = new Vector2(r.x + r.width * 0.5f, r.y + r.height * 0.5f);
            t.Check(UIEngine.HitTest(root, centre + new Vector2(40f, 0f)) == bar, "upright, the bar is hit along its length");
            t.Check(UIEngine.HitTest(root, centre + new Vector2(0f, 40f)) != bar, "and missed below it");

            bar.rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI * 0.5f);
            yield return 2;
            t.Check(UIEngine.HitTest(root, centre + new Vector2(0f, 40f)) == bar, "turned a quarter, it is hit where its end now pokes out");
            t.Check(UIEngine.HitTest(root, centre + new Vector2(40f, 0f)) != bar, "and missed where the upright bar was");
            t.Check(MathF.Abs(bar.arrange.subtreeBounds.height - 100f) < 0.01f, $"its bounds are the turned box: {bar.arrange.subtreeBounds.height}");
        }

        [A_XSDActionDependency("Layout.ExpanderOpensToContentHeight", "Test")]
        private static IEnumerator<int> ExpanderOpensToContentHeight(TestContext t)
        {
            LabelControl content = new LabelControl { text = "Inside", fontSize = 16 };
            ExpanderControl expander = new ExpanderControl
            {
                preferredWidth = 300f,
                horizontalAlignment = HorizontalAlignment.Left,
                verticalAlignment = VerticalAlignment.Top
            };
            expander.AddChild(content);
            t.Show(expander);
            yield return 2;

            Control viewport = (Control)content.parent;
            float closed = expander.arrangedRect.height;
            t.Check(viewport.hidden, "closed, the content is hidden");

            expander.Toggle();
            yield return 30;
            t.Check(!viewport.hidden, "open, the content is shown");
            t.Check(content.arrangedRect.height > 0f && MathF.Abs(content.arrangedRect.height - content.DesiredSize.Y) < 0.01f,
                $"open, the content is arranged at its own height: {content.arrangedRect.height} of {content.DesiredSize.Y}");
            t.Check(MathF.Abs(expander.arrangedRect.height - (closed + content.DesiredSize.Y)) < 0.01f,
                $"open, the expander grows by the content's height: {expander.arrangedRect.height}");

            expander.Toggle();
            yield return 30;
            t.Check(viewport.hidden, "closed again, the content is hidden");
            t.Check(MathF.Abs(expander.arrangedRect.height - closed) < 0.01f, $"closed again, the expander is back to {closed}: {expander.arrangedRect.height}");
        }

        [A_XSDActionDependency("Layout.RewrapMatchesFresh", "Test")]
        private static IEnumerator<int> RewrapMatchesFresh(TestContext t)
        {
            IGlyphMetrics metrics = new FontAssetGlyphMetrics();
            AtlasMetaData atlas = AssetRegistries.GetRegistryByValueType<string, FontAsset>(typeof(FontAsset))["default"].atlasMetaData;
            string first = "Lorem ipsum\tdolor sit amet, consectetur adipiscing elit, sed do ￼ eiusmod "
                + "Pneumonoultramicroscopicsilicovolcanoconiosis tempor\tincididunt ut labore et dolore magna aliqua.";
            string second = first.Replace('o', 'W').Replace('e', 'M');

            BlockLayout? reused = null;
            foreach (string text in new[] { first, second })
            {
                List<TextMeasurer.Run> runs = Runs(text, atlas);
                foreach (float width in new[] { 400f, 150f, 400f })
                {
                    reused = TextMeasurer.MeasureBlock(runs, width, metrics, 1.5f, reuse: reused);
                    BlockLayout fresh = TextMeasurer.MeasureBlock(runs, width, metrics, 1.5f);
                    t.Check(Same(reused, fresh), $"{(text == first ? "first" : "second")} text at {width} px: a reused measure equals a fresh one");
                    t.Check(reused.lines.Count > 2, $"at {width} px the text wraps: {reused.lines.Count} lines");

                    reused = TextMeasurer.MeasureBlock(runs, width, metrics, 1.5f, 0f, new BandSlots(width), reused);
                    fresh = TextMeasurer.MeasureBlock(runs, width, metrics, 1.5f, 0f, new BandSlots(width));
                    t.Check(Same(reused, fresh), $"{(text == first ? "first" : "second")} text at {width} px around a float: a reused measure equals a fresh one");
                }
            }
            yield break;
        }

        // regular, bold 20, italic, a formula, regular
        private static List<TextMeasurer.Run> Runs(string text, AtlasMetaData atlas)
        {
            int math = text.IndexOf('￼');
            return new List<TextMeasurer.Run>
            {
                new TextMeasurer.Run(text, 0, 12, "default", atlas, 16, FontStyle.Regular),
                new TextMeasurer.Run(text, 12, 18, "default", atlas, 20, FontStyle.Bold),
                new TextMeasurer.Run(text, 30, math - 30, "default", atlas, 16, FontStyle.Italic),
                new TextMeasurer.Run(text, math, 1, "default", atlas, 16, FontStyle.Regular, math: true, imageWidth: 30f, imageHeight: 14f, depth: 4f),
                new TextMeasurer.Run(text, math + 1, text.Length - math - 1, "default", atlas, 16, FontStyle.Regular),
            };
        }

        // the first 40 px start 60 px in
        private sealed class BandSlots : ILineSlots
        {
            private readonly float width;

            public BandSlots(float width) => this.width = width;

            public float Place(float y, float height, out float left, out float right)
            {
                left = y < 40f ? 60f : 0f;
                right = width;
                return y;
            }
        }

        private static bool Same(BlockLayout a, BlockLayout b)
        {
            if (a.width != b.width || a.height != b.height || a.lines.Count != b.lines.Count) return false;
            for (int i = 0; i < a.lines.Count; i++)
            {
                TextLine x = a.lines[i], y = b.lines[i];
                if (x.width != y.width || x.ascent != y.ascent || x.descent != y.descent || x.top != y.top
                    || x.left != y.left || x.room != y.room || x.segments.Count != y.segments.Count) return false;
                for (int s = 0; s < x.segments.Count; s++)
                    if (!x.segments[s].Equals(y.segments[s])) return false;
            }
            return true;
        }
    }
}
