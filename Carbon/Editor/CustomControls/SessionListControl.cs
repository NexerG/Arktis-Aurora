using ArctisAurora.Core.Diagnostics;
using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.EngineWork;

namespace Carbon.Editor.CustomControls
{
    // Capture session folders under the configured root, newest first. Selecting one reads every
    // thread file in it; the folders themselves are listed without parsing anything.
    [A_XSDType("SessionList", "UI")]
    public class SessionListControl : ScrollableControl
    {
        private static readonly LogChannel Log = LogChannel.For("Carbon");

        #region properties
        // row metrics
        [A_XSDElementProperty("RowHeight", "UI", "Height of a session row in pixels.")]
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

        [A_XSDElementProperty("NameColorHex", "UI", "Text color of a session's name.")]
        public string nameColorHex = "#23221E";

        [A_XSDElementProperty("SelectedColorHex", "UI", "Text color of the session that is loaded.")]
        public string selectedColorHex = "#3A3833";

        [A_XSDElementProperty("DetailColorHex", "UI", "Text color of a session's thread line.")]
        public string detailColorHex = "#918F87";
        #endregion

        private readonly StackPanelControl rows = new StackPanelControl();

        public CaptureSession? Loaded { get; private set; }

        public Action<CaptureSession>? onSessionLoaded;

        public SessionListControl()
        {
            scrollDirection = ScrollDirection.Vertical;

            rows.alpha = 0f;
            rows.orientation = StackPanelControl.Orientation.Vertical;
            AddChild(rows);

            Rebuild();
        }

        private static string Root() =>
            SettingsRegistry.Get<CarbonSettings>().captureRoot.Resolved;

        // Re-reads the capture root and replaces every row.
        public void Rebuild()
        {
            foreach (Entity row in rows.children.ToArray())
                row.Destroy();

            rows.Spacing = rowSpacing;

            string root = Root();
            List<CaptureSessionInfo> sessions = FrameCaptureReader.Enumerate(root);
            if (sessions.Count == 0) Log.Info($"no capture sessions under {root}");

            foreach (CaptureSessionInfo session in sessions)
                rows.AddChild(Row(session));

            InvalidateLayout();
        }

        // Reads every thread file in one session folder and keeps it as the loaded capture.
        public void Load(string directory)
        {
            CaptureSession session = FrameCaptureReader.LoadSession(directory);
            if (session.threads.Count == 0)
            {
                Log.Warn($"no frame files in {directory}");
                return;
            }

            Loaded = session;

            foreach (CapturedThread thread in session.threads)
                Log.Info($"{session.name}/{thread.thread} — {thread.frames.Count} frames, {thread.spans.Count} spans, " +
                         $"{thread.counters.Count} counters, {thread.dropped} dropped{(thread.truncated ? ", truncated" : "")}");

            Rebuild();
            onSessionLoaded?.Invoke(session);
        }

        private Control Row(CaptureSessionInfo session)
        {
            bool loaded = Loaded != null && string.Equals(Loaded.directory, session.directory, StringComparison.OrdinalIgnoreCase);

            StackPanelControl content = new StackPanelControl
            {
                orientation = StackPanelControl.Orientation.Vertical,
                alpha = 0f,
                horizontalPosition = 0f
            };

            content.AddChild(new LabelControl
            {
                text = session.name,
                fontSize = 14,
                colorHex = loaded ? selectedColorHex : nameColorHex,
                preferredHeight = 18,
                horizontalPosition = 0f
            });

            content.AddChild(new LabelControl
            {
                text = string.Join(", ", session.threads),
                fontSize = 11,
                colorHex = detailColorHex,
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

            // Posted, so the rows this click is still bubbling through are not torn down under it.
            row.RegisterOnRelease(_ => { Engine.Post(() => Load(session.directory)); return true; });

            return row;
        }
    }
}
