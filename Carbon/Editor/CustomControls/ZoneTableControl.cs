using System.Text;
using ArctisAurora.Core.Diagnostics;
using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Text;

namespace Carbon.Editor.CustomControls
{
    // Every zone of a capture, per thread, longest first — the numbers the one-second report prints,
    // but over the whole file. Rolled up from the span stream, which records every instance, so a
    // zone that re-enters itself totals more here than the report's outermost-only rule gives it.
    [A_XSDType("ZoneTable", "UI")]
    public class ZoneTableControl : ScrollableControl
    {
        #region properties
        [A_XSDElementProperty("RowSpacing", "UI", "Space between rows in pixels.")]
        public float rowSpacing = 2f;

        [A_XSDElementProperty("ZoneFontSize", "UI", "Font size of a zone's name and total.")]
        public int zoneFontSize = 13;

        [A_XSDElementProperty("DetailFontSize", "UI", "Font size of a zone's counts line.")]
        public int detailFontSize = 11;

        // column widths
        [A_XSDElementProperty("NameWidth", "UI", "Width of the zone name column in pixels.")]
        public float nameWidth = 210f;

        [A_XSDElementProperty("TotalWidth", "UI", "Width of the total-milliseconds column in pixels.")]
        public float totalWidth = 74f;

        [A_XSDElementProperty("Indent", "UI", "Left offset a zone gains per level of nesting, in pixels.")]
        public float indent = 14f;

        [A_XSDElementProperty("SwatchWidth", "UI", "Width of the depth colour bar ahead of a zone's name, in pixels.")]
        public float swatchWidth = 3f;

        [A_XSDElementProperty("DeltaWidth", "UI", "Width of the change-against-baseline column in pixels.")]
        public float deltaWidth = 100f;

        // palette
        [A_XSDElementProperty("DepthColorsHex", "UI", "Comma separated colours, one per nesting level, cycled. Matches the span chart.")]
        public string depthColorsHex = "#B8A48C,#C4A882,#A8B49C,#B0A8BC,#C0B098";

        [A_XSDElementProperty("SeparatorColorHex", "UI", "Rule drawn between one thread and the next.")]
        public string separatorColorHex = "#DCDAD3";

        [A_XSDElementProperty("HeaderColorHex", "UI", "Text color of a thread's heading.")]
        public string headerColorHex = "#3A3833";

        [A_XSDElementProperty("ZoneColorHex", "UI", "Text color of a zone's name and total.")]
        public string zoneColorHex = "#23221E";

        [A_XSDElementProperty("DetailColorHex", "UI", "Text color of a zone's counts line.")]
        public string detailColorHex = "#918F87";

        [A_XSDElementProperty("SlowerColorHex", "UI", "Text color of a zone's change when it got slower than the baseline.")]
        public string slowerColorHex = "#B0452E";

        [A_XSDElementProperty("FasterColorHex", "UI", "Text color of a zone's change when it got faster than the baseline.")]
        public string fasterColorHex = "#5B7F45";
        #endregion

        // one zone over a whole capture, plus where it sits in the nesting
        private struct Rolled
        {
            public long calls;
            public long total;
            public long bytes;
            public long min;
            public long max;
            public int depth;
            public string parent;
        }

        // one data pool over a whole capture
        private struct RolledPool
        {
            public long min;
            public long max;
            public long last;
            public long capacity;
            public long bytes;
        }

        private readonly StackPanelControl rows = new StackPanelControl();
        private string[] _depthColors = Array.Empty<string>();
        private CaptureSession? _session;
        private CaptureSession? _baseline;

        public ZoneTableControl()
        {
            scrollDirection = ScrollDirection.Vertical;

            rows.alpha = 0f;
            rows.orientation = StackPanelControl.Orientation.Vertical;
            AddChild(rows);
        }

        public void SetSession(CaptureSession session)
        {
            _session = session;

            foreach (Entity row in rows.children.ToArray())
                row.Destroy();

            rows.Spacing = rowSpacing;

            _depthColors = depthColorsHex.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (_depthColors.Length == 0) _depthColors = new[] { "#B8A48C" };

            if (_baseline != null) rows.AddChild(Header($"compared with {_baseline.name}"));

            bool first = true;
            foreach (CapturedThread thread in session.threads)
            {
                if (!first) rows.AddChild(Separator());
                first = false;
                Fill(thread, _baseline?.threads.Find(t => t.thread == thread.thread));
                Pools(thread);
            }

            if (_baseline != null)
                foreach (CapturedThread gone in _baseline.threads)
                    if (!session.threads.Exists(t => t.thread == gone.thread))
                    {
                        rows.AddChild(Separator());
                        rows.AddChild(Header($"{gone.thread} — only in baseline"));
                    }

            InvalidateLayout();
        }

        // Compares every session shown after this against the baseline, or stops comparing on null.
        public void SetBaseline(CaptureSession? baseline)
        {
            _baseline = baseline;
            if (_session != null) SetSession(_session);
        }

        private Control Header(string text) => new LabelControl
        {
            text = text,
            fontSize = zoneFontSize,
            colorHex = headerColorHex,
            preferredHeight = 22,
            horizontalPosition = 0f
        };

        private Control Separator() => new PanelControl
        {
            preferredHeight = 1,
            horizontalAlignment = HorizontalAlignment.Stretch,
            colorHex = separatorColorHex,
            margin = new Thickness(6, 0, 6, 0),
            hitTestable = false
        };

        private void Fill(CapturedThread thread, CapturedThread? baseThread)
        {
            Dictionary<(string zone, string name), long> counters = new Dictionary<(string, string), long>();
            Dictionary<string, Rolled> zones = Roll(thread, counters, out long threadBytes);
            Dictionary<string, Rolled>? baseZones = baseThread != null
                ? Roll(baseThread, new Dictionary<(string, string), long>(), out _)
                : null;

            string compared = _baseline == null ? "" : baseThread != null ? $" vs {baseThread.frames.Count}" : ", not in baseline";
            rows.AddChild(Header($"{thread.thread} — {thread.frames.Count} frames{compared}, {CapturedThread.Bytes(threadBytes)} allocated{(thread.dropped > 0 ? $", {thread.dropped} dropped" : "")}{(thread.truncated ? ", truncated" : "")}"));

            HashSet<string> emitted = new HashSet<string>();
            Emit(thread, zones, counters, string.Empty, 0, emitted, baseThread, baseZones);

            // A zone whose parent never became a row would otherwise vanish from a diagnostic table.
            foreach (KeyValuePair<string, Rolled> zone in zones.OrderByDescending(z => z.Value.total))
                if (emitted.Add(zone.Key))
                    rows.AddChild(Row(thread, zone.Key, zone.Value, counters, 0, baseThread, baseZones));

            if (baseThread == null || baseZones == null) return;

            foreach (KeyValuePair<string, Rolled> zone in baseZones.OrderByDescending(z => z.Value.total))
                if (!zones.ContainsKey(zone.Key))
                    rows.AddChild(new LabelControl
                    {
                        text = $"{zone.Key} — gone, was {PerFrame(baseThread, zone.Value.total):F2}ms/f",
                        fontSize = detailFontSize,
                        colorHex = detailColorHex,
                        preferredHeight = 17,
                        horizontalPosition = 0f
                    });
        }

        // Every data pool the thread recorded, with its item range and peak size.
        private void Pools(CapturedThread thread)
        {
            if (thread.pools.Count == 0) return;

            Dictionary<int, RolledPool> pools = new Dictionary<int, RolledPool>();
            foreach (CapturedPool pool in thread.pools)
            {
                if (!pools.TryGetValue(pool.name, out RolledPool rolled))
                    rolled = new RolledPool { min = long.MaxValue };

                rolled.min = Math.Min(rolled.min, pool.count);
                rolled.max = Math.Max(rolled.max, pool.count);
                rolled.last = pool.count;
                rolled.capacity = Math.Max(rolled.capacity, pool.capacity);
                rolled.bytes = Math.Max(rolled.bytes, pool.bytes);
                pools[pool.name] = rolled;
            }

            rows.AddChild(Header("pools"));

            float gutter = swatchWidth + 6;
            foreach (KeyValuePair<int, RolledPool> pool in pools)
            {
                StackPanelControl head = new StackPanelControl
                {
                    orientation = StackPanelControl.Orientation.Horizontal,
                    alpha = 0f,
                    preferredHeight = 17
                };

                head.AddChild(new LabelControl
                {
                    text = thread.NameOf(pool.Key),
                    fontSize = zoneFontSize,
                    colorHex = zoneColorHex,
                    preferredWidth = nameWidth - gutter,
                    preferredHeight = 17,
                    margin = new Thickness(0, 0, 0, gutter),
                    horizontalPosition = 0f,
                    clipOutOfBounds = true
                });

                head.AddChild(new LabelControl
                {
                    text = CapturedThread.Bytes(pool.Value.bytes),
                    fontSize = zoneFontSize,
                    colorHex = zoneColorHex,
                    preferredWidth = totalWidth,
                    preferredHeight = 17,
                    horizontalPosition = 1f
                });

                StackPanelControl row = new StackPanelControl
                {
                    orientation = StackPanelControl.Orientation.Vertical,
                    alpha = 0f,
                    preferredHeight = 34,
                    horizontalPosition = 0f
                };
                row.AddChild(head);

                row.AddChild(new LabelControl
                {
                    text = $"items {pool.Value.min}-{pool.Value.max}, last {pool.Value.last}, capacity {pool.Value.capacity}",
                    fontSize = detailFontSize,
                    colorHex = detailColorHex,
                    preferredHeight = 15,
                    margin = new Thickness(0, 0, 0, gutter),
                    horizontalPosition = 0f
                });

                rows.AddChild(row);
            }
        }

        // Every zone of one thread over the whole capture, and its counters into the table given.
        private static Dictionary<string, Rolled> Roll(CapturedThread thread,
            Dictionary<(string zone, string name), long> counters, out long threadBytes)
        {
            Dictionary<string, Rolled> zones = new Dictionary<string, Rolled>();

            // Spans arrive in pre-order, so whatever is open one level up is the parent.
            string[] open = new string[32];
            threadBytes = 0;

            foreach (CapturedFrame frame in thread.frames)
            {
                threadBytes += frame.bytes;

                for (int i = 0; i < frame.spanCount; i++)
                {
                    CapturedSpan span = thread.spans[frame.firstSpan + i];
                    long elapsed = span.end - span.begin;
                    string name = thread.NameOf(span.name);

                    while (span.depth >= open.Length) Array.Resize(ref open, open.Length * 2);
                    open[span.depth] = name;

                    zones.TryGetValue(name, out Rolled rolled);
                    if (rolled.calls == 0)
                    {
                        rolled.min = elapsed;
                        rolled.max = elapsed;
                        rolled.depth = span.depth;
                        rolled.parent = span.depth > 0 ? open[span.depth - 1] : string.Empty;
                    }
                    rolled.calls++;
                    rolled.total += elapsed;
                    rolled.bytes += span.bytes;
                    if (elapsed < rolled.min) rolled.min = elapsed;
                    if (elapsed > rolled.max) rolled.max = elapsed;
                    zones[name] = rolled;
                }

                for (int i = 0; i < frame.counterCount; i++)
                {
                    CapturedCounter counter = thread.counters[frame.firstCounter + i];
                    string zone = counter.span >= 0 ? thread.NameOf(thread.spans[counter.span].name) : "(frame)";
                    counters.TryGetValue((zone, thread.NameOf(counter.name)), out long tally);
                    counters[(zone, thread.NameOf(counter.name))] = tally + counter.value;
                }
            }

            return zones;
        }

        // Children under their parent, and siblings by cost.
        private void Emit(CapturedThread thread, Dictionary<string, Rolled> zones,
            Dictionary<(string zone, string name), long> counters, string parent, int depth, HashSet<string> emitted,
            CapturedThread? baseThread, Dictionary<string, Rolled>? baseZones)
        {
            foreach (KeyValuePair<string, Rolled> zone in zones
                .Where(z => z.Value.depth == depth && z.Value.parent == parent)
                .OrderByDescending(z => z.Value.total)
                .ToList())
            {
                if (!emitted.Add(zone.Key)) continue;

                rows.AddChild(Row(thread, zone.Key, zone.Value, counters, depth, baseThread, baseZones));
                Emit(thread, zones, counters, zone.Key, depth + 1, emitted, baseThread, baseZones);
            }
        }

        private Control Row(CapturedThread thread, string name, Rolled rolled,
            Dictionary<(string zone, string name), long> counters, int depth,
            CapturedThread? baseThread, Dictionary<string, Rolled>? baseZones)
        {
            float inset = indent * depth;
            float gutter = swatchWidth + 6;

            StackPanelControl head = new StackPanelControl
            {
                orientation = StackPanelControl.Orientation.Horizontal,
                alpha = 0f,
                preferredHeight = 17
            };

            head.AddChild(new PanelControl
            {
                preferredWidth = swatchWidth,
                preferredHeight = 17,
                colorHex = _depthColors[depth % _depthColors.Length],
                margin = new Thickness(0, 6, 0, 0),
                hitTestable = false
            });

            head.AddChild(new LabelControl
            {
                text = name,
                fontSize = zoneFontSize,
                colorHex = zoneColorHex,
                preferredWidth = MathF.Max(40, nameWidth - inset - gutter),
                preferredHeight = 17,
                horizontalPosition = 0f,
                clipOutOfBounds = true
            });

            head.AddChild(new LabelControl
            {
                text = _baseline != null ? $"{PerFrame(thread, rolled.total):F2}ms/f" : $"{thread.Ms(rolled.total):F2}ms",
                fontSize = zoneFontSize,
                colorHex = zoneColorHex,
                preferredWidth = totalWidth,
                preferredHeight = 17,
                horizontalPosition = 1f
            });

            if (_baseline != null)
                head.AddChild(Delta(PerFrame(thread, rolled.total), name, baseThread, baseZones));

            StackPanelControl row = new StackPanelControl
            {
                orientation = StackPanelControl.Orientation.Vertical,
                alpha = 0f,
                preferredHeight = 34,
                margin = new Thickness(0, 0, 0, inset),
                horizontalPosition = 0f
            };
            row.AddChild(head);

            row.AddChild(new LabelControl
            {
                text = $"x{rolled.calls}  min {thread.Ms(rolled.min):F3}  max {thread.Ms(rolled.max):F3}  {CapturedThread.Bytes(rolled.bytes)}{Counters(name, counters)}",
                fontSize = detailFontSize,
                colorHex = detailColorHex,
                preferredHeight = 15,
                margin = new Thickness(0, 0, 0, gutter),
                horizontalPosition = 0f
            });

            return row;
        }

        // A zone's change in milliseconds per frame against the same zone in the baseline.
        private Control Delta(double now, string name, CapturedThread? baseThread, Dictionary<string, Rolled>? baseZones)
        {
            string text = "new";
            string color = detailColorHex;

            if (baseThread != null && baseZones != null && baseZones.TryGetValue(name, out Rolled before))
            {
                double then = PerFrame(baseThread, before.total);
                double change = now - then;
                text = then > 0
                    ? $"{change:+0.00;-0.00;0.00} {change / then * 100:+0;-0;0}%"
                    : $"{change:+0.00;-0.00;0.00}";

                double shown = Math.Round(change, 2);
                color = shown > 0 ? slowerColorHex : shown < 0 ? fasterColorHex : detailColorHex;
            }

            return new LabelControl
            {
                text = text,
                fontSize = zoneFontSize,
                colorHex = color,
                preferredWidth = deltaWidth,
                preferredHeight = 17,
                horizontalPosition = 1f
            };
        }

        private static double PerFrame(CapturedThread thread, long ticks) =>
            thread.Ms(ticks) / Math.Max(1, thread.frames.Count);

        private static string Counters(string zone, Dictionary<(string zone, string name), long> counters)
        {
            StringBuilder? line = null;
            foreach (KeyValuePair<(string zone, string name), long> entry in counters)
            {
                if (entry.Key.zone != zone) continue;
                line ??= new StringBuilder("  —");
                line.Append(' ').Append(entry.Key.name).Append(' ').Append(entry.Value);
            }
            return line?.ToString() ?? string.Empty;
        }
    }
}
