using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Registry.Assets;
using ArctisAurora.Core.Testing;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.EngineWork;

namespace Carbon.Editor.CustomControls
{
    // One test run: a line per test, and under a test that did not pass, a line per failure.
    [A_XSDType("TestResults", "UI")]
    public class TestResultsControl : ScrollableControl
    {
        #region properties
        // row metrics
        [A_XSDElementProperty("TestHeight", "UI", "Height of a test's line in pixels.")]
        public int testHeight = 20;

        [A_XSDElementProperty("FailureHeight", "UI", "Height of a failure's line in pixels.")]
        public int failureHeight = 16;

        [A_XSDElementProperty("Indent", "UI", "Left inset of a failure's line in pixels.")]
        public float indent = 16f;

        [A_XSDElementProperty("ButtonHeight", "UI", "Height of a failed test's Open capture button in pixels.")]
        public int buttonHeight = 22;

        [A_XSDElementProperty("ShotWidth", "UI", "Widest a golden, actual or diff image is drawn, in pixels.")]
        public float shotWidth = 240f;

        // palette
        [A_XSDElementProperty("HeaderColorHex", "UI", "Text color of the run's heading.")]
        public string headerColorHex = "#3A3833";

        [A_XSDElementProperty("PassColorHex", "UI", "Text color of a test that passed.")]
        public string passColorHex = "#5B7F45";

        [A_XSDElementProperty("FailColorHex", "UI", "Text color of a test that failed.")]
        public string failColorHex = "#B0452E";

        [A_XSDElementProperty("SkipColorHex", "UI", "Text color of a test that was skipped.")]
        public string skipColorHex = "#918F87";

        [A_XSDElementProperty("DetailColorHex", "UI", "Text color of a failure's line.")]
        public string detailColorHex = "#5F5D56";

        [A_XSDElementProperty("ButtonColorHex", "UI", "Ground of the Open capture button at rest.")]
        public string buttonColorHex = "#E3E1D9";

        [A_XSDElementProperty("ButtonHoverColorHex", "UI", "Ground of a hovered Open capture button.")]
        public string buttonHoverColorHex = "#DCDAD3";

        [A_XSDElementProperty("ButtonPressColorHex", "UI", "Ground of a held Open capture button.")]
        public string buttonPressColorHex = "#D0CEC6";
        #endregion

        private readonly StackPanelControl rows = new StackPanelControl();

        // shot images already uploaded, by path
        private static readonly Dictionary<string, (TextureAsset texture, int width, int height)> _textures = new();

        public Action<string>? onOpenCapture;

        public TestResultsControl()
        {
            scrollDirection = ScrollDirection.Vertical;

            rows.alpha = 0f;
            rows.orientation = StackPanelControl.Orientation.Vertical;
            AddChild(rows);
        }

        // Replaces every line with the given run.
        public void ShowRun(TestRun run)
        {
            foreach (Entity row in rows.children.ToArray())
                row.Destroy();

            rows.AddChild(Line($"{run.info.name} — {run.info.host} {run.info.build} — {run.info.passed} passed, " +
                               $"{run.info.failed} failed, {run.info.skipped} skipped, {run.info.newCount} new", 14, headerColorHex, testHeight + 8, 0f));

            foreach (TestResult test in run.tests)
            {
                string label = test.suite.Length == 0 ? test.name : $"{test.suite}/{test.name}";
                string mark = test.result == "Pass" ? "PASS" : test.result == "Skipped" ? "SKIP" : test.result == "New" ? "NEW" : "FAIL";
                string color = test.result == "Pass" ? passColorHex : test.result == "Skipped" || test.result == "New" ? skipColorHex : failColorHex;
                rows.AddChild(Line($"{mark}  {label}  ({test.ticks} ticks)", 13, color, testHeight, 0f));

                if (test.result == "Skipped" && test.reason.Length > 0)
                    rows.AddChild(Line(test.reason, 12, detailColorHex, failureHeight, indent));

                foreach (TestFailure failure in test.failures)
                {
                    string where = failure.file.Length == 0 ? string.Empty : $"  {failure.file}:{failure.line}";
                    rows.AddChild(Line(failure.message + where, 12, detailColorHex, failureHeight, indent));
                }

                if (test.result == "Fail" && test.capture != null)
                    rows.AddChild(OpenCapture(test.capture));

                foreach (TestShot shot in test.shots)
                    if (shot.result == "Fail" || shot.result == "New")
                        rows.AddChild(Shot(shot));
            }

            InvalidateLayout();
        }

        // A shot's golden, actual and diff side by side.
        private Control Shot(TestShot shot)
        {
            StackPanelControl strip = new StackPanelControl
            {
                alpha = 0f,
                orientation = StackPanelControl.Orientation.Horizontal,
                horizontalPosition = 0f,
                margin = new Thickness(2, 0, 6, indent),
                Spacing = 8f
            };
            strip.AddChild(ShotImage($"{shot.name} — golden", shot.result == "New" ? null : shot.golden));
            strip.AddChild(ShotImage("actual", shot.actual));
            if (shot.diff != null) strip.AddChild(ShotImage("diff", shot.diff));
            return strip;
        }

        private Control ShotImage(string caption, string? path)
        {
            StackPanelControl column = new StackPanelControl { alpha = 0f, orientation = StackPanelControl.Orientation.Vertical, preferredWidth = shotWidth };
            column.AddChild(Line(caption, 12, detailColorHex, failureHeight, 0f));

            if (path == null || !Texture(path, out var image))
            {
                column.AddChild(Line(path == null ? "none" : "missing " + Path.GetFileName(path), 11, detailColorHex, failureHeight, 0f));
                return column;
            }

            float scale = Math.Min(1f, shotWidth / image.width);
            PanelControl picture = new PanelControl
            {
                kind = VulkanControlType.ImageControl,
                sampler = image.texture,
                colorHex = "#FFFFFF",
                preferredWidth = image.width * scale,
                preferredHeight = image.height * scale,
                horizontalPosition = 0f
            };
            picture.SetUVRect(0f, 1f, 1f, 0f);
            column.AddChild(picture);
            return column;
        }

        // False when the file is gone or the texture table is full.
        private static bool Texture(string path, out (TextureAsset texture, int width, int height) image)
        {
            if (_textures.TryGetValue(path, out image)) return true;
            if (!File.Exists(path) || TextureAsset.Table.Count >= TextureAsset.MaxTextures) return false;

            ArctisAurora.Core.Imaging.ImageFiles.TryIdentify(path, out int width, out int height);
            TextureAsset texture = new TextureAsset();
            texture.LoadFile(path, Silk.NET.Vulkan.Format.R8G8B8A8Unorm);
            image = (texture, width, height);
            _textures[path] = image;
            return true;
        }

        private Control OpenCapture(string directory)
        {
            ButtonControl button = new ButtonControl
            {
                preferredWidth = 110f,
                preferredHeight = buttonHeight,
                horizontalPosition = 0f,
                margin = new Thickness(2, 0, 4, indent),
                colorHex = buttonColorHex,
                hoverColorHex = buttonHoverColorHex,
                pressColorHex = buttonPressColorHex,
                cornerRadius = new CornerRadii(4)
            };
            button.AddChild(new LabelControl { text = "Open capture", fontSize = 12, colorHex = detailColorHex });
            button.RegisterOnRelease(_ => { Engine.Post(() => onOpenCapture?.Invoke(directory)); return true; });
            return button;
        }

        private static Control Line(string text, int fontSize, string colorHex, int height, float left) =>
            new LabelControl
            {
                text = text,
                fontSize = fontSize,
                colorHex = colorHex,
                preferredHeight = height,
                horizontalPosition = 0f,
                margin = new Thickness(0, 0, 0, left)
            };
    }
}
