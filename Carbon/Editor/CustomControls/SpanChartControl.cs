using ArctisAurora.Core.Diagnostics;
using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Text;
using System.Numerics;

namespace Carbon.Editor.CustomControls
{
    [A_XSDType("SpanChartMode", "UI")]
    public enum SpanChartMode
    {
        Frame,
        Timeline
    }

    // Spans as rectangles over a window of the process-wide clock. One frame of one thread is the
    // flame chart; every thread over the whole capture is the timeline — the same layout, because
    // <F T> is absolute and the lanes already share an axis.
    //
    // Row 0 of a lane is the frame itself and rows below it are its spans, so the gap between the
    // root span's end and the frame's end draws the parked tail without computing it.
    [A_XSDType("SpanChart", "UI")]
    public class SpanChartControl : ContainerControl
    {
        #region properties
        [A_XSDElementProperty("Mode", "UI", "Frame draws the selected frame alone; Timeline draws every thread on the absolute clock.")]
        public SpanChartMode mode = SpanChartMode.Frame;

        // metrics
        [A_XSDElementProperty("RowHeight", "UI", "Height of one nesting level in pixels.")]
        public float rowHeight = 20f;

        [A_XSDElementProperty("RowGap", "UI", "Space between rows in pixels.")]
        public float rowGap = 2f;

        [A_XSDElementProperty("LaneSpacing", "UI", "Space between threads in pixels.")]
        public float laneSpacing = 10f;

        [A_XSDElementProperty("LabelWidth", "UI", "Width of the thread name column in pixels.")]
        public float labelWidth = 64f;

        [A_XSDElementProperty("LabelFontSize", "UI", "Font size of a span's name.")]
        public int labelFontSize = 11;

        // budgets — a span too narrow to read is not built at all
        [A_XSDElementProperty("MaxRects", "UI", "Most span rectangles built for one window.")]
        public int maxRects = 2000;

        [A_XSDElementProperty("MaxLabels", "UI", "Most span names built for one window.")]
        public int maxLabels = 150;

        [A_XSDElementProperty("MinSpanWidth", "UI", "Narrowest span drawn, in pixels.")]
        public float minSpanWidth = 1.5f;

        [A_XSDElementProperty("LabelMinWidth", "UI", "Narrowest span that gets a name, in pixels.")]
        public float labelMinWidth = 70f;

        [A_XSDElementProperty("RulerHeight", "UI", "Height of the time ruler above the lanes, in pixels.")]
        public float rulerHeight = 18f;

        [A_XSDElementProperty("DefaultWindowFrames", "UI", "Frames of the densest thread a Timeline opens on; the whole capture is too wide to read a span in.")]
        public int defaultWindowFrames = 10;

        // palette
        [A_XSDElementProperty("FrameColorHex", "UI", "Ground of the bar standing for a whole frame.")]
        public string frameColorHex = "#E3E1D9";

        [A_XSDElementProperty("DepthColorsHex", "UI", "Comma separated grounds, one per nesting level, cycled.")]
        public string depthColorsHex = "#B8A48C,#C4A882,#A8B49C,#B0A8BC,#C0B098";

        [A_XSDElementProperty("SpanLabelColorHex", "UI", "Text color of a span's name.")]
        public string spanLabelColorHex = "#2C2B26";

        [A_XSDElementProperty("LaneLabelColorHex", "UI", "Text color of a thread's name.")]
        public string laneLabelColorHex = "#918F87";

        [A_XSDElementProperty("TickColorHex", "UI", "Ground of a ruler tick mark.")]
        public string tickColorHex = "#C9C6BC";

        [A_XSDElementProperty("RulerLabelColorHex", "UI", "Text color of a ruler time label.")]
        public string rulerLabelColorHex = "#918F87";

        // bottom scroll bar
        [A_XSDElementProperty("ScrollBarHeight", "UI", "Height of the scroll bar along the bottom edge, in pixels.")]
        public float scrollBarHeight = 8f;

        [A_XSDElementProperty("TrackColorHex", "UI", "Ground of the scroll bar's track.")]
        public string trackColorHex { get => field; set { field = value; if (_track != null) _track.colorHex = value; } } = "#EBEAE5";

        [A_XSDElementProperty("ThumbColorHex", "UI", "Ground of the scroll thumb at rest.")]
        public string thumbColorHex { get => field; set { field = value; if (_thumb != null) _thumb.colorHex = value; } } = "#D7D5CD";

        [A_XSDElementProperty("ThumbHoverColorHex", "UI", "Ground of a hovered scroll thumb.")]
        public string thumbHoverColorHex { get => field; set { field = value; if (_thumb != null) _thumb.hoverColorHex = value; } } = "#C9C6BC";

        [A_XSDElementProperty("ThumbPressColorHex", "UI", "Ground of a held scroll thumb.")]
        public string thumbPressColorHex { get => field; set { field = value; if (_thumb != null) _thumb.pressColorHex = value; } } = "#BAB7AC";
        #endregion

        // ruler shape — marks any closer together than this have no room for their own label
        private const float tickMinSpacing = 80f;
        private const float tickMarkHeight = 5f;
        private const int maxTicks = 32;

        // the narrowest a thumb gets, however far the capture is zoomed out of
        private const float minThumbWidth = 24f;

        // one rectangle to draw, in absolute ticks
        private struct Placed
        {
            public int lane;
            public int row;
            public long begin;
            public long end;
            public long bytes;
            public string name;
        }

        private readonly List<Placed> _placed = new List<Placed>();
        private readonly List<PanelControl> _rects = new List<PanelControl>();
        private readonly List<LabelControl> _labels = new List<LabelControl>();
        private readonly List<LabelControl> _laneLabels = new List<LabelControl>();
        private readonly List<CapturedThread> _lanes = new List<CapturedThread>();
        private readonly List<int> _laneRows = new List<int>();
        private readonly List<int> _labelled = new List<int>();

        // ruler marks, in ticks from the capture's start so panning slides them rather than renumbering
        private readonly List<long> _tickOffsets = new List<long>();
        private readonly List<PanelControl> _ticks = new List<PanelControl>();
        private readonly List<LabelControl> _tickLabels = new List<LabelControl>();
        private long _tickStep;

        // the bottom scroll bar — where the window sits in the whole capture
        private readonly PanelControl _track;
        private readonly ChartScrollThumbControl _thumb;

        private string[] _depthColors = Array.Empty<string>();

        private CaptureSession? _session;
        private CapturedThread? _frameThread;
        private int _frameIndex = -1;

        // the drawn window, in absolute Stopwatch ticks
        private long _windowStart;
        private long _windowSpan = 1;
        private long _boundsStart;
        private long _boundsSpan = 1;

        private long _frequency = 1;
        private long _dragStart;
        private float _dragFrom;

        public SpanChartControl()
        {
            _track = new PanelControl { colorHex = trackColorHex, hitTestable = false };
            _thumb = new ChartScrollThumbControl(this)
            {
                colorHex = thumbColorHex,
                hoverColorHex = thumbHoverColorHex,
                pressColorHex = thumbPressColorHex
            };

            AddChild(_track);
            AddChild(_thumb);
        }

        // Every thread of a capture, and in Timeline mode the whole of it becomes the window.
        public void SetSession(CaptureSession session)
        {
            _session = session;

            long first = long.MaxValue;
            long last = long.MinValue;

            foreach (CapturedThread thread in session.threads)
            {
                if (thread.frames.Count == 0) continue;
                CapturedFrame head = thread.frames[0];
                CapturedFrame tail = thread.frames[^1];
                if (head.start < first) first = head.start;
                if (tail.start + tail.duration > last) last = tail.start + tail.duration;
                if (thread.frequency > 0) _frequency = thread.frequency;
            }

            if (first > last) { first = 0; last = 1; }

            _boundsStart = first;
            _boundsSpan = Math.Max(1, last - first);

            // Left unset: the frame that arrives next is what sizes the window, since a capture's
            // threads tick at rates that differ by orders of magnitude.
            if (mode == SpanChartMode.Timeline) _windowSpan = 0;
        }

        // How far apart this thread's frames are. Threads run free, so the burst that records 300
        // frames of a 1000Hz render thread covers a fraction of the same 300 frames of main.
        private static long Interval(CapturedThread thread)
        {
            if (thread.frames.Count < 2) return 0;
            return (thread.frames[^1].start - thread.frames[0].start) / (thread.frames.Count - 1);
        }

        // The frame the strip selected. Frame mode becomes that frame; Timeline keeps its zoom and
        // scrolls to it, which is what makes clicking a spike in the strip land on it here.
        public void SetFrame(CapturedThread thread, int frameIndex)
        {
            _frameThread = thread;
            _frameIndex = frameIndex;
            if (frameIndex < 0 || frameIndex >= thread.frames.Count) return;

            CapturedFrame frame = thread.frames[frameIndex];

            if (mode == SpanChartMode.Frame)
            {
                _windowStart = frame.start;
                _windowSpan = Math.Max(1, frame.duration);
            }
            else
            {
                // A whole capture across one pane is milliseconds per pixel, where every span culls
                // itself for being too narrow to read. The first frame selected sizes the window,
                // in frames of its own thread rather than in a fixed duration.
                if (_windowSpan <= 0)
                {
                    long interval = Interval(thread);
                    long span = interval > 0 ? interval * defaultWindowFrames : Math.Max(1, frame.duration) * defaultWindowFrames;
                    _windowSpan = Math.Clamp(span, 1000, _boundsSpan);
                }

                long centre = frame.start + frame.duration / 2;
                _windowStart = Math.Clamp(centre - _windowSpan / 2, _boundsStart, _boundsStart + _boundsSpan - _windowSpan);
            }

            Rebuild();
        }

        #region ---- building ----
        // Collects what falls inside the window, then sizes the control pools to it. Never called
        // from Arrange — adding a child invalidates layout, and layout is what would be calling.
        private void Rebuild()
        {
            _placed.Clear();
            _labelled.Clear();
            _lanes.Clear();
            _laneRows.Clear();

            if (mode == SpanChartMode.Frame)
            {
                if (_frameThread != null) _lanes.Add(_frameThread);
            }
            else if (_session != null)
            {
                foreach (CapturedThread thread in _session.threads)
                    if (thread.frames.Count > 0) _lanes.Add(thread);
            }

            long windowEnd = _windowStart + _windowSpan;
            long minTicks = (long)(_windowSpan * minSpanWidth / MathF.Max(1, PlotWidth()));
            long labelTicks = (long)(_windowSpan * labelMinWidth / MathF.Max(1, PlotWidth()));

            for (int lane = 0; lane < _lanes.Count && _placed.Count < maxRects; lane++)
            {
                CapturedThread thread = _lanes[lane];
                int rows = 1;

                int from = mode == SpanChartMode.Frame ? _frameIndex : 0;
                int to = mode == SpanChartMode.Frame ? _frameIndex + 1 : thread.frames.Count;

                for (int f = from; f < to && _placed.Count < maxRects; f++)
                {
                    CapturedFrame frame = thread.frames[f];
                    long frameEnd = frame.start + frame.duration;
                    if (frameEnd < _windowStart || frame.start > windowEnd) continue;

                    Add(lane, 0, frame.start, frameEnd, minTicks, labelTicks, thread.thread, frame.bytes);

                    for (int i = 0; i < frame.spanCount && _placed.Count < maxRects; i++)
                    {
                        CapturedSpan span = thread.spans[frame.firstSpan + i];
                        long begin = frame.start + span.begin;
                        long end = frame.start + span.end;
                        if (end < _windowStart || begin > windowEnd) continue;

                        if (Add(lane, span.depth + 1, begin, end, minTicks, labelTicks, thread.NameOf(span.name), span.bytes))
                            rows = Math.Max(rows, span.depth + 2);
                    }
                }

                _laneRows.Add(rows);
            }

            Ticks();
            Fit();
            InvalidateLayout();
        }

        // Marks on a 1/2/5 step, spaced far enough apart to carry their own label. Offsets are from
        // the capture's start rather than the window's, so panning slides them instead of renumbering.
        private void Ticks()
        {
            _tickOffsets.Clear();

            double perTickMs = 1000.0 / Math.Max(1, _frequency);
            double minimumMs = _windowSpan * perTickMs * tickMinSpacing / PlotWidth();

            long step = (long)(NiceStep(minimumMs) * _frequency / 1000.0);
            _tickStep = step;
            if (step <= 0) return;

            long from = _windowStart - _boundsStart;
            for (long at = (from + step - 1) / step * step;
                 at - from <= _windowSpan && _tickOffsets.Count < maxTicks;
                 at += step)
                _tickOffsets.Add(at);
        }

        private static double NiceStep(double minimum)
        {
            if (minimum <= 0 || double.IsNaN(minimum)) return 0;

            double magnitude = Math.Pow(10, Math.Floor(Math.Log10(minimum)));
            if (magnitude * 1 >= minimum) return magnitude;
            if (magnitude * 2 >= minimum) return magnitude * 2;
            if (magnitude * 5 >= minimum) return magnitude * 5;
            return magnitude * 10;
        }

        private double Ms(long ticks) => ticks * 1000.0 / Math.Max(1, _frequency);

        // A duration beside a span's name.
        private string Duration(long ticks)
        {
            double ms = Ms(ticks);
            return ms < 1 ? $"{ms:0.###}ms" : $"{ms:0.##}ms";
        }

        // A point on the ruler, measured from the capture's start. Precision comes from the step,
        // not the magnitude — a frame's worth of window sits a whole second in, so rounding to the
        // magnitude prints the same number on every mark.
        private string Stamp(long ticks)
        {
            double ms = Ms(ticks);
            double step = Ms(_tickStep);

            return ms >= 1000
                ? (ms / 1000).ToString("F" + Decimals(step / 1000)) + "s"
                : ms.ToString("F" + Decimals(step)) + "ms";
        }

        private static int Decimals(double step) =>
            step <= 0 ? 0 : Math.Clamp((int)Math.Ceiling(-Math.Log10(step)), 0, 6);

        private bool Add(int lane, int row, long begin, long end, long minTicks, long labelTicks, string name, long bytes = 0)
        {
            if (end - begin < minTicks) return false;

            if (end - begin >= labelTicks && _labelled.Count < maxLabels) _labelled.Add(_placed.Count);

            _placed.Add(new Placed { lane = lane, row = row, begin = begin, end = end, bytes = bytes, name = name });
            return true;
        }

        // Grows the pools to what was collected and parks the surplus out of the draw.
        private void Fit()
        {
            _depthColors = depthColorsHex.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (_depthColors.Length == 0) _depthColors = new[] { "#B8A48C" };

            while (_rects.Count < _placed.Count)
            {
                PanelControl rect = new PanelControl { hitTestable = false };
                _rects.Add(rect);
                AddChild(rect);
            }

            for (int i = 0; i < _placed.Count; i++)
            {
                PanelControl rect = _rects[i];
                if (rect.hidden) rect.Show();
                rect.colorHex = _placed[i].row == 0
                    ? frameColorHex
                    : _depthColors[(_placed[i].row - 1) % _depthColors.Length];
            }

            for (int i = _placed.Count; i < _rects.Count; i++)
                if (!_rects[i].hidden) _rects[i].Hide();

            while (_labels.Count < _labelled.Count)
            {
                LabelControl label = new LabelControl
                {
                    fontSize = labelFontSize,
                    colorHex = spanLabelColorHex,
                    horizontalPosition = 0f,
                    hitTestable = false,
                    // to its own span, not to the chart, or a long name runs over the next one
                    clipOutOfBounds = true
                };
                _labels.Add(label);
                AddChild(label);
            }

            for (int i = 0; i < _labelled.Count; i++)
            {
                LabelControl label = _labels[i];
                if (label.hidden) label.Show();

                Placed placed = _placed[_labelled[i]];
                string caption = placed.bytes != 0
                    ? $"{placed.name}  {Duration(placed.end - placed.begin)}  {CapturedThread.Bytes(placed.bytes)}"
                    : $"{placed.name}  {Duration(placed.end - placed.begin)}";
                if (label.text != caption) label.text = caption;
            }

            for (int i = _labelled.Count; i < _labels.Count; i++)
                if (!_labels[i].hidden) _labels[i].Hide();

            while (_laneLabels.Count < _lanes.Count)
            {
                LabelControl label = new LabelControl
                {
                    fontSize = labelFontSize,
                    colorHex = laneLabelColorHex,
                    horizontalPosition = 0f,
                    hitTestable = false
                };
                _laneLabels.Add(label);
                AddChild(label);
            }

            for (int i = 0; i < _lanes.Count; i++)
            {
                LabelControl label = _laneLabels[i];
                if (label.hidden) label.Show();
                if (label.text != _lanes[i].thread) label.text = _lanes[i].thread;
            }

            for (int i = _lanes.Count; i < _laneLabels.Count; i++)
                if (!_laneLabels[i].hidden) _laneLabels[i].Hide();

            while (_ticks.Count < _tickOffsets.Count)
            {
                PanelControl mark = new PanelControl { hitTestable = false };
                LabelControl stamp = new LabelControl
                {
                    fontSize = labelFontSize,
                    colorHex = rulerLabelColorHex,
                    horizontalPosition = 0f,
                    hitTestable = false
                };
                _ticks.Add(mark);
                _tickLabels.Add(stamp);
                AddChild(mark);
                AddChild(stamp);
            }

            for (int i = 0; i < _tickOffsets.Count; i++)
            {
                if (_ticks[i].hidden) _ticks[i].Show();
                if (_tickLabels[i].hidden) _tickLabels[i].Show();

                _ticks[i].colorHex = tickColorHex;

                string stamp = Stamp(_tickOffsets[i]);
                if (_tickLabels[i].text != stamp) _tickLabels[i].text = stamp;
            }

            for (int i = _tickOffsets.Count; i < _ticks.Count; i++)
            {
                if (!_ticks[i].hidden) _ticks[i].Hide();
                if (!_tickLabels[i].hidden) _tickLabels[i].Hide();
            }
        }
        #endregion

        #region ---- interaction ----
        // Timeline only — the frame view's window is whatever the strip selected.
        public override bool OnPointerScroll(PointerEvent e)
        {
            if (e.delta.Y > 0) return Zoom(0.8f);
            if (e.delta.Y < 0) return Zoom(1.25f);
            return base.OnPointerScroll(e);
        }

        private bool Zoom(float factor)
        {
            if (mode != SpanChartMode.Timeline) return false;

            long centre = _windowStart + _windowSpan / 2;
            long span = Math.Clamp((long)(_windowSpan * factor), 1000, _boundsSpan);

            _windowSpan = span;
            _windowStart = Math.Clamp(centre - span / 2, _boundsStart, _boundsStart + _boundsSpan - span);
            Rebuild();
            return true;
        }

        public override bool OnPointerPress(PointerEvent e)
        {
            if (mode != SpanChartMode.Timeline || e.button != PointerEvent.leftButton) return base.OnPointerPress(e);

            _dragStart = _windowStart;
            _dragFrom = e.point.X;
            StartDrag();
            return true;
        }

        // Panned from where the grab started, so a clamped window cannot drift from the pointer.
        public override void OnDrag(PointerEvent e)
        {
            if (mode != SpanChartMode.Timeline) return;

            float moved = e.point.X - _dragFrom;
            long shifted = _dragStart - (long)(moved / MathF.Max(1, PlotWidth()) * _windowSpan);

            long clamped = Math.Clamp(shifted, _boundsStart, _boundsStart + _boundsSpan - _windowSpan);
            if (clamped == _windowStart) return;

            _windowStart = clamped;
            Rebuild();
        }

        // what the thumb reads and writes
        public float ThumbTravel { get; private set; }

        public long WindowStart => _windowStart;

        public long ScrollRange => Math.Max(0, _boundsSpan - _windowSpan);

        public void ScrollTo(long start)
        {
            long clamped = Math.Clamp(start, _boundsStart, _boundsStart + _boundsSpan - _windowSpan);
            if (clamped == _windowStart) return;

            _windowStart = clamped;
            Rebuild();
        }
        #endregion

        #region ---- layout ----
        private float PlotWidth() => MathF.Max(1, arrangedRect.width - padding.totalHorizontal - labelWidth);

        protected override Vector2 MeasureCore(Vector2 availableSize)
        {
            Vector2 size = base.MeasureCore(availableSize);

            foreach (Entity child in children)
                if (child is Control control && !control.hidden) control.Measure(size);

            return size;
        }

        protected override void ArrangeCore(LayoutRect finalRect)
        {
            base.ArrangeCore(finalRect);

            LayoutRect inner = finalRect.Shrink(padding);
            float plotX = inner.x + labelWidth;
            float plotWidth = MathF.Max(1, inner.Right - plotX);
            float row = rowHeight + rowGap;

            // the ruler owns the top strip; lanes start below it
            float offset = _windowStart - _boundsStart;
            for (int i = 0; i < _tickOffsets.Count; i++)
            {
                float x = plotX + (_tickOffsets[i] - offset) / _windowSpan * plotWidth;

                _ticks[i].Arrange(new LayoutRect(x, inner.y + rulerHeight - tickMarkHeight, 1, tickMarkHeight));
                _tickLabels[i].Arrange(new LayoutRect(x + 3, inner.y, tickMinSpacing, labelFontSize + 2));
            }

            // lane tops, from each lane's own depth
            float[] tops = new float[_lanes.Count];
            float cursor = inner.y + rulerHeight;
            for (int lane = 0; lane < _lanes.Count; lane++)
            {
                tops[lane] = cursor;
                _laneLabels[lane].Arrange(new LayoutRect(inner.x, cursor, labelWidth, labelFontSize + 2));
                cursor += (lane < _laneRows.Count ? _laneRows[lane] : 1) * row + laneSpacing;
            }

            for (int i = 0; i < _placed.Count; i++)
            {
                Placed placed = _placed[i];
                float x = plotX + (float)(placed.begin - _windowStart) / _windowSpan * plotWidth;
                float width = MathF.Max(1, (float)(placed.end - placed.begin) / _windowSpan * plotWidth);

                // a span running off either edge is clipped to the plot, not dropped
                float left = MathF.Max(x, plotX);
                float right = MathF.Min(x + width, plotX + plotWidth);
                if (right <= left) right = left + 1;

                _rects[i].Arrange(new LayoutRect(left, tops[placed.lane] + placed.row * row, right - left, rowHeight));
            }

            for (int i = 0; i < _labelled.Count; i++)
            {
                LayoutRect rect = _rects[_labelled[i]].arrangedRect;
                _labels[i].Arrange(new LayoutRect(rect.x + 4, rect.y + (rowHeight - labelFontSize) * 0.5f,
                    MathF.Max(1, rect.width - 8), labelFontSize + 2));
            }

            ArrangeScrollBar(inner, plotX, plotWidth);
        }

        // Frame mode has nothing to scroll — its window is the frame the strip picked — so the bar
        // arranges to nothing, which draws no pixels and fails the hit-test.
        private void ArrangeScrollBar(LayoutRect inner, float plotX, float plotWidth)
        {
            if (mode != SpanChartMode.Timeline || ScrollRange <= 0)
            {
                ThumbTravel = 0f;
                _track.Arrange(LayoutRect.Empty);
                _thumb.Arrange(LayoutRect.Empty);
                return;
            }

            float top = inner.Bottom - scrollBarHeight;
            _track.Arrange(new LayoutRect(plotX, top, plotWidth, scrollBarHeight));

            float width = MathF.Min(plotWidth, MathF.Max(minThumbWidth, (float)_windowSpan / _boundsSpan * plotWidth));
            ThumbTravel = plotWidth - width;

            _thumb.Arrange(new LayoutRect(
                plotX + (float)(_windowStart - _boundsStart) / ScrollRange * ThumbTravel,
                top, width, scrollBarHeight));
        }
        #endregion
    }

    // The bottom scroll bar's thumb. Built and positioned by the chart, never authored in XML.
    internal sealed class ChartScrollThumbControl : ButtonControl
    {
        private readonly SpanChartControl chart;
        private float grab;
        private long grabStart;

        public ChartScrollThumbControl(SpanChartControl chart)
        {
            this.chart = chart;
        }

        public override bool OnPointerPress(PointerEvent e)
        {
            grab = e.point.X;
            grabStart = chart.WindowStart;
            StartDrag();
            return base.OnPointerPress(e);
        }

        // Pointer travel along the track maps onto the capture by the ratio between the two.
        public override void OnDrag(PointerEvent e)
        {
            float travel = chart.ThumbTravel;
            if (travel > 0f)
            {
                float moved = e.point.X - grab;
                chart.ScrollTo(grabStart + (long)(moved / travel * chart.ScrollRange));
            }
            base.OnDrag(e);
        }
    }
}
