using ArctisAurora.Core.Diagnostics;
using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Text;
using System.Numerics;

namespace Carbon.Editor.CustomControls
{
    // One lane per thread, one bar per frame, height against that lane's longest frame. More frames
    // than bars means a bar covers a run of them and stands for the longest one in it, so a spike
    // never averages away.
    [A_XSDType("FrameStrip", "UI")]
    public class FrameStripControl : ContainerControl
    {
        #region properties
        // lane metrics
        [A_XSDElementProperty("MaxBars", "UI", "Most bars a lane draws; beyond it one bar covers several frames.")]
        public int maxBars = 200;

        [A_XSDElementProperty("LaneSpacing", "UI", "Space between lanes in pixels.")]
        public float laneSpacing = 6f;

        [A_XSDElementProperty("LabelWidth", "UI", "Width of the thread name column in pixels.")]
        public float labelWidth = 64f;

        [A_XSDElementProperty("BarGap", "UI", "Space between bars in pixels.")]
        public float barGap = 1f;

        [A_XSDElementProperty("LabelFontSize", "UI", "Font size of a lane's thread name.")]
        public int labelFontSize = 12;

        // palette
        [A_XSDElementProperty("BarColorHex", "UI", "Ground of a frame bar.")]
        public string barColorHex = "#C9C6BC";

        [A_XSDElementProperty("SelectedBarColorHex", "UI", "Ground of the selected frame's bar.")]
        public string selectedBarColorHex = "#C4622B";

        [A_XSDElementProperty("LabelColorHex", "UI", "Text color of a lane's thread name.")]
        public string labelColorHex = "#918F87";
        #endregion

        // A thread's row: its bars, and which frame each bar stands for.
        private sealed class Lane
        {
            public CapturedThread? thread;
            public LabelControl label = null!;
            public PanelControl[] bars = Array.Empty<PanelControl>();
            public int[] frames = Array.Empty<int>();
            public long[] durations = Array.Empty<long>();
            public int columns = 1;
            public long longest = 1;
            public long reference;
        }

        private readonly List<Lane> _lanes = new List<Lane>();

        // selection
        private CapturedThread? _selectedThread;
        private int _selectedFrame = -1;
        private PanelControl? _highlighted;

        // alignment against the counterpart
        private int _offset;
        private float _referenceScale;

        public Action<CapturedThread, int>? onFrameSelected;

        public CaptureSession? Session { get; private set; }

        public CapturedThread? SelectedThread => _selectedThread;

        // Replaces every lane. A counterpart lines lanes and frame columns up with the strip it is compared against.
        public void SetSession(CaptureSession? session, CaptureSession? counterpart = null)
        {
            foreach (Entity child in children.ToArray())
                child.Destroy();

            _lanes.Clear();
            _selectedThread = null;
            _selectedFrame = -1;
            _highlighted = null;
            Session = session;

            if (session != null)
            {
                List<string> names = new List<string>();
                foreach (CapturedThread thread in session.threads)
                    if (thread.frames.Count > 0) names.Add(thread.thread);
                if (counterpart != null)
                    foreach (CapturedThread thread in counterpart.threads)
                        if (thread.frames.Count > 0 && !names.Contains(thread.thread)) names.Add(thread.thread);
                names.Sort(StringComparer.Ordinal);

                foreach (string name in names)
                    _lanes.Add(BuildLane(name, Find(session, name), counterpart != null ? Find(counterpart, name) : null));
            }

            InvalidateLayout();
        }

        private static CapturedThread? Find(CaptureSession session, string name) =>
            session.threads.Find(t => t.thread == name && t.frames.Count > 0);

        private Lane BuildLane(string name, CapturedThread? thread, CapturedThread? other)
        {
            int columns = Math.Max(1, Math.Max(thread?.frames.Count ?? 0, other?.frames.Count ?? 0));
            int count = Math.Min(columns, Math.Max(1, maxBars));

            Lane lane = new Lane
            {
                thread = thread,
                columns = columns,
                bars = new PanelControl[count],
                frames = new int[count],
                durations = new long[count],
                label = new LabelControl
                {
                    text = name,
                    fontSize = labelFontSize,
                    colorHex = labelColorHex,
                    horizontalPosition = 0f,
                    hitTestable = false
                }
            };

            for (int bar = 0; bar < count; bar++)
            {
                PanelControl panel = new PanelControl { colorHex = barColorHex, hitTestable = false };
                lane.bars[bar] = panel;
                AddChild(panel);
            }

            if (other != null)
                foreach (CapturedFrame frame in other.frames)
                    if (frame.duration > lane.reference) lane.reference = frame.duration;

            AddChild(lane.label);
            Bucket(lane);
            return lane;
        }

        private int From(Lane lane, int bar) => (int)((long)bar * lane.columns / lane.bars.Length) - _offset;

        // one bucket per bar, standing for its longest frame
        private void Bucket(Lane lane)
        {
            lane.longest = 1;

            for (int bar = 0; bar < lane.bars.Length; bar++)
            {
                int from = From(lane, bar);
                int to = From(lane, bar + 1);
                if (to <= from) to = from + 1;

                int peak = -1;
                if (lane.thread != null)
                    for (int i = Math.Max(0, from); i < to && i < lane.thread.frames.Count; i++)
                        if (peak < 0 || lane.thread.frames[i].duration > lane.thread.frames[peak].duration) peak = i;

                lane.frames[bar] = peak;
                lane.durations[bar] = peak >= 0 ? lane.thread!.frames[peak].duration : 0;
                if (lane.durations[bar] > lane.longest) lane.longest = lane.durations[bar];
            }
        }

        // Shifts this strip's frames right by offset columns, keeping its bars.
        public void SetOffset(int offset)
        {
            _offset = offset;
            foreach (Lane lane in _lanes)
                Bucket(lane);

            Highlight();
            InvalidateLayout();
        }

        // Lane heights against the lane's own longest frame at 0, the counterpart's at 1.
        public void SetReferenceScale(float scale)
        {
            _referenceScale = Math.Clamp(scale, 0f, 1f);
            InvalidateLayout();
        }

        private static int Peak(Lane lane)
        {
            int bar = 0;
            for (int i = 1; i < lane.durations.Length; i++)
                if (lane.durations[i] > lane.durations[bar]) bar = i;
            return bar;
        }

        // Reports the longest frame of the first lane, so the views below open on something worth looking at.
        public void SelectPeak()
        {
            Lane? lane = _lanes.Find(l => l.thread != null);
            if (lane != null) Select(lane, lane.frames[Peak(lane)]);
        }

        private void Select(Lane lane, int frame)
        {
            if (lane.thread == null || frame < 0) return;

            Mark(lane.thread, frame);
            onFrameSelected?.Invoke(lane.thread, frame);
        }

        // Highlights the bar holding a frame without reporting it; null clears.
        public void Mark(CapturedThread? thread, int frame)
        {
            _selectedThread = thread;
            _selectedFrame = frame;
            Highlight();
        }

        private void Highlight()
        {
            if (_highlighted != null) _highlighted.colorHex = barColorHex;
            _highlighted = null;

            Lane? lane = _selectedThread != null ? _lanes.Find(l => l.thread == _selectedThread) : null;
            if (lane == null) return;

            for (int bar = 0; bar < lane.bars.Length; bar++)
                if (_selectedFrame >= From(lane, bar) && _selectedFrame < From(lane, bar + 1))
                {
                    _highlighted = lane.bars[bar];
                    _highlighted.colorHex = selectedBarColorHex;
                    return;
                }
        }

        // The bars are not hit-testable, so the strip maps the point itself.
        public override bool OnPointerPress(PointerEvent e)
        {
            if (e.button != PointerEvent.leftButton || _lanes.Count == 0) return base.OnPointerPress(e);

            LayoutRect inner = arrangedRect.Shrink(padding);
            float laneHeight = LaneHeight(inner);
            if (laneHeight <= 0) return true;

            int laneIndex = (int)((e.point.Y - inner.y) / (laneHeight + laneSpacing));
            if (laneIndex < 0 || laneIndex >= _lanes.Count) return true;

            Lane lane = _lanes[laneIndex];
            float plotX = inner.x + labelWidth;
            float plotWidth = MathF.Max(1, inner.Right - plotX);

            int bar = (int)((e.point.X - plotX) / plotWidth * lane.bars.Length);
            Select(lane, lane.frames[Math.Clamp(bar, 0, lane.bars.Length - 1)]);
            return true;
        }

        private float LaneHeight(LayoutRect inner) =>
            (inner.height - laneSpacing * MathF.Max(0, _lanes.Count - 1)) / MathF.Max(1, _lanes.Count);

        protected override Vector2 MeasureCore(Vector2 availableSize)
        {
            Vector2 size = base.MeasureCore(availableSize);

            foreach (Entity child in children)
                if (child is Control control) control.Measure(size);

            return size;
        }

        protected override void ArrangeCore(LayoutRect finalRect)
        {
            base.ArrangeCore(finalRect);

            LayoutRect inner = finalRect.Shrink(padding);
            float laneHeight = LaneHeight(inner);
            float plotX = inner.x + labelWidth;
            float plotWidth = MathF.Max(1, inner.Right - plotX);

            for (int i = 0; i < _lanes.Count; i++)
            {
                Lane lane = _lanes[i];
                float top = inner.y + i * (laneHeight + laneSpacing);

                lane.label.Arrange(new LayoutRect(inner.x, top, labelWidth, labelFontSize + 2));

                float longest = lane.reference > 0 ? lane.longest + (lane.reference - lane.longest) * _referenceScale : lane.longest;
                float slot = plotWidth / lane.bars.Length;
                for (int bar = 0; bar < lane.bars.Length; bar++)
                {
                    if (lane.frames[bar] < 0)
                    {
                        lane.bars[bar].Arrange(LayoutRect.Empty);
                        continue;
                    }

                    float height = MathF.Min(laneHeight, MathF.Max(1, laneHeight * lane.durations[bar] / longest));
                    lane.bars[bar].Arrange(new LayoutRect(
                        plotX + bar * slot,
                        top + laneHeight - height,
                        MathF.Max(1, slot - barGap),
                        height));
                }
            }
        }
    }
}
