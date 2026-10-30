using ArctisAurora.Core.Diagnostics;
using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Testing;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.EngineWork;

namespace Carbon.Editor.CustomControls
{
    // Test run folders under the configured root, newest first. Selecting one hands its results on.
    [A_XSDType("TestRunList", "UI")]
    public class TestRunListControl : ScrollableControl
    {
        private static readonly LogChannel Log = LogChannel.For("Carbon");

        #region properties
        // row metrics
        [A_XSDElementProperty("RowHeight", "UI", "Height of a run row in pixels.")]
        public int rowHeight = 44;

        [A_XSDElementProperty("RowSpacing", "UI", "Space between rows in pixels.")]
        public float rowSpacing = 4f;

        // row palette
        [A_XSDElementProperty("RowColorHex", "UI", "Ground of a row at rest.")]
        public string rowColorHex = "#E3E1D9";

        [A_XSDElementProperty("RowHoverColorHex", "UI", "Ground of a hovered row.")]
        public string rowHoverColorHex = "#DCDAD3";

        [A_XSDElementProperty("RowPressColorHex", "UI", "Ground of a held row.")]
        public string rowPressColorHex = "#EAE8E2";

        [A_XSDElementProperty("NameColorHex", "UI", "Text color of a run's name.")]
        public string nameColorHex = "#23221E";

        [A_XSDElementProperty("SelectedColorHex", "UI", "Text color of the run that is loaded.")]
        public string selectedColorHex = "#3A3833";

        [A_XSDElementProperty("DetailColorHex", "UI", "Text color of a run's count line.")]
        public string detailColorHex = "#918F87";

        [A_XSDElementProperty("FailColorHex", "UI", "Text color of the count line of a run with a failure.")]
        public string failColorHex = "#B0452E";
        #endregion

        private readonly StackPanelControl rows = new StackPanelControl();

        public TestRun? Loaded { get; private set; }

        public Action<TestRun>? onRunLoaded;

        public TestRunListControl()
        {
            scrollDirection = ScrollDirection.Vertical;

            rows.alpha = 0f;
            rows.orientation = StackPanelControl.Orientation.Vertical;
            AddChild(rows);

            Rebuild();
        }

        private static string Root() =>
            SettingsRegistry.Get<CarbonSettings>().testRoot.Resolved;

        // Re-reads the test root and replaces every row.
        public void Rebuild()
        {
            foreach (Entity row in rows.children.ToArray())
                row.Destroy();

            rows.Spacing = rowSpacing;

            string root = Root();
            List<TestRunInfo> runs = TestResultsReader.Enumerate(root);
            if (runs.Count == 0) Log.Info($"no test runs under {root}");

            foreach (TestRunInfo run in runs)
                rows.AddChild(Row(run));

            InvalidateLayout();
        }

        // Reads one run folder and keeps it as the loaded run.
        public void Load(string directory)
        {
            TestRun? run = TestResultsReader.Load(directory);
            if (run == null)
            {
                Log.Warn($"no readable {TestResultsReader.fileName} in {directory}");
                return;
            }

            Loaded = run;
            Rebuild();
            onRunLoaded?.Invoke(run);
        }

        private Control Row(TestRunInfo run)
        {
            bool loaded = Loaded != null && string.Equals(Loaded.info.directory, run.directory, StringComparison.OrdinalIgnoreCase);

            StackPanelControl content = new StackPanelControl
            {
                orientation = StackPanelControl.Orientation.Vertical,
                alpha = 0f,
                horizontalPosition = 0f
            };

            content.AddChild(new LabelControl
            {
                text = $"{run.name}  {run.host} {run.build}",
                fontSize = 14,
                colorHex = loaded ? selectedColorHex : nameColorHex,
                preferredHeight = 18,
                horizontalPosition = 0f
            });

            string counts = $"{run.passed} passed, {run.failed} failed";
            if (run.skipped > 0) counts += $", {run.skipped} skipped";
            if (run.newCount > 0) counts += $", {run.newCount} new";
            content.AddChild(new LabelControl
            {
                text = counts,
                fontSize = 11,
                colorHex = run.failed > 0 ? failColorHex : detailColorHex,
                preferredHeight = 14,
                horizontalPosition = 0f
            });

            ButtonControl row = new ButtonControl
            {
                preferredHeight = rowHeight,
                horizontalAlignment = HorizontalAlignment.Stretch,
                padding = new Thickness(0, 0, 0, 8),
                colorHex = rowColorHex,
                hoverColorHex = rowHoverColorHex,
                pressColorHex = rowPressColorHex,
                cornerRadius = new CornerRadii(4)
            };
            row.AddChild(content);

            row.RegisterOnRelease(_ => { Engine.Post(() => Load(run.directory)); return true; });

            return row;
        }
    }
}
