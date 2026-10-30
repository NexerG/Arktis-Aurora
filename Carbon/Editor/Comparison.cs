using ArctisAurora.Core.Diagnostics;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Text;
using Carbon.Editor.CustomControls;

namespace Carbon.Editor
{
    // Which capture each frame strip shows, and which one the charts below follow.
    public static class Comparison
    {
        // views
        private static FrameStripControl _upper = null!;
        private static FrameStripControl _lower = null!;
        private static SpanChartControl _flame = null!;
        private static SpanChartControl _timeline = null!;
        private static ZoneTableControl _zones = null!;
        private static LabelControl _offsetLabel = null!;
        private static LabelControl _scaleLabel = null!;
        private static LabelControl _poolsLabel = null!;

        // captures
        private static CaptureSession? _loaded;
        private static CaptureSession? _baseline;

        // what the charts show
        private static CaptureSession? _shown;
        private static CapturedThread? _shownThread;
        private static int _shownFrame = -1;

        // the lower strip against the upper
        private static bool _swapped;
        private static int _offset;
        private static float _scale = 1f;

        private static bool Comparing => _loaded != null && _baseline != null && _loaded != _baseline;

        public static void Attach(FrameStripControl upper, FrameStripControl lower, SpanChartControl flame,
                                  SpanChartControl timeline, ZoneTableControl zones, SliderControl scale,
                                  LabelControl offsetLabel, LabelControl scaleLabel, LabelControl poolsLabel)
        {
            _upper = upper;
            _lower = lower;
            _flame = flame;
            _timeline = timeline;
            _zones = zones;
            _offsetLabel = offsetLabel;
            _scaleLabel = scaleLabel;
            _poolsLabel = poolsLabel;

            upper.onFrameSelected = (thread, frame) => Show(upper, lower, thread, frame);
            lower.onFrameSelected = (thread, frame) => Show(lower, upper, thread, frame);

            _scale = scale.value;
            scale.onChanged = SetScale;
            lower.SetReferenceScale(_scale);
            lower.Hide();

            Readouts();
        }

        // A capture just read, which the charts open on.
        public static void Load(CaptureSession session)
        {
            _loaded = session;
            _shown = null;
            _zones.SetSession(session);
            Refresh();
            (_lower.Session == session ? _lower : _upper).SelectPeak();
        }

        public static void Pin()
        {
            if (_loaded == null) return;

            _baseline = _loaded;
            _zones.SetBaseline(_baseline);
            _offset = 0;
            Refresh();
        }

        public static void Clear()
        {
            _baseline = null;
            _zones.SetBaseline(null);
            _offset = 0;
            Refresh();
        }

        public static void Swap()
        {
            if (!Comparing) return;

            _swapped = !_swapped;
            _offset = -_offset;
            Refresh();
        }

        public static void Slide(int frames)
        {
            if (!Comparing) return;

            _offset += frames;
            _lower.SetOffset(_offset);
            Readouts();
        }

        public static void SetScale(float scale)
        {
            _scale = scale;
            _lower.SetReferenceScale(scale);
            Readouts();
        }

        // Puts each capture in its strip and carries the charts' frame to wherever it now sits.
        private static void Refresh()
        {
            CaptureSession? upper = _loaded;
            CaptureSession? lower = null;
            if (Comparing)
            {
                upper = _swapped ? _loaded : _baseline;
                lower = _swapped ? _baseline : _loaded;
            }

            _upper.SetSession(upper, lower);
            _lower.SetSession(lower, upper);
            _lower.SetOffset(_offset);
            if (lower != null) _lower.Show(); else _lower.Hide();

            if (_shown != null)
            {
                if (_upper.Session == _shown) _upper.Mark(_shownThread, _shownFrame);
                else if (_lower.Session == _shown) _lower.Mark(_shownThread, _shownFrame);
                else _upper.SelectPeak();
            }

            Readouts();
        }

        // The latest bar clicked, from either strip.
        private static void Show(FrameStripControl from, FrameStripControl other, CapturedThread thread, int frame)
        {
            other.Mark(null, -1);

            if (from.Session != _shown && from.Session != null)
            {
                _shown = from.Session;
                _flame.SetSession(_shown);
                _timeline.SetSession(_shown);
            }

            _shownThread = thread;
            _shownFrame = frame;
            _flame.SetFrame(thread, frame);
            _timeline.SetFrame(thread, frame);
            _poolsLabel.text = PoolLine(thread, frame);
        }

        // The frame's data pools on one line, empty when its thread recorded none.
        private static string PoolLine(CapturedThread thread, int frame)
        {
            if (frame < 0 || frame >= thread.frames.Count) return string.Empty;

            CapturedFrame captured = thread.frames[frame];
            return string.Join("    ", thread.pools.GetRange(captured.firstPool, captured.poolCount)
                .Select(p => $"{thread.NameOf(p.name)} {p.count}/{p.capacity} {CapturedThread.Bytes(p.bytes)}"));
        }

        private static void Readouts()
        {
            _offsetLabel.text = _offset == 0 ? "offset 0" : $"offset {_offset:+0;-0}";
            _scaleLabel.text = $"{_scale:0.00}";
        }
    }
}
