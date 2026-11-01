using ArctisAurora.Core.Diagnostics;
using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Filing.Serialization;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Threading;
using ArctisAurora.Core.UI;
using ArctisAurora.EngineWork;
using ArctisAurora.Core.Imaging;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Xml.Linq;

namespace ArctisAurora.Core.Testing
{
    [A_XSDType("Test", "Test")]
    public class TestEntry
    {
        [A_XSDElementProperty("Action", "Test")]
        public Action? action { get; set; }

        [A_XSDElementProperty("Timeout", "Test")]
        public int timeout { get; set; } = TestRunner.defaultTimeoutMs;

        [A_XSDElementProperty("Budget", "Test")]
        public List<TestBudget> budgets { get; set; } = new();
    }

    // A ceiling on one zone's per-frame cost over a measured test; a negative limit is not checked.
    [A_XSDType("Budget", "Test")]
    public class TestBudget
    {
        [A_XSDElementProperty("Zone", "Test")]
        public string zone { get; set; } = string.Empty;

        [A_XSDElementProperty("Thread", "Test")]
        public string thread { get; set; } = "Main";

        [A_XSDElementProperty("P50", "Test")]
        public float p50 { get; set; } = -1f;

        [A_XSDElementProperty("P95", "Test")]
        public float p95 { get; set; } = -1f;

        [A_XSDElementProperty("Max", "Test")]
        public float max { get; set; } = -1f;

        [A_XSDElementProperty("AllocKB", "Test")]
        public float allocKB { get; set; } = -1f;
    }

    // --test runs every Data/XML/Documents/Tests/<Suite>.tests.axml, --test=<Suite> the one named, and exits
    // with the failure count.
    [A_XSDType("TestSuite", "Test")]
    internal static class TestRunner
    {
        private static readonly LogChannel Log = LogChannel.For("Test");

        [A_XSDElementProperty("Test", "Test")]
        public static List<TestEntry> tests { get; set; } = new();

        internal const int defaultTimeoutMs = 10000;

        // the fixed clock, and how long Boot runs the host's own tree
        private const double step = 1.0 / 60.0;
        private const int bootTicks = 60;

        public static bool active { get; private set; }

        private static readonly Dictionary<string, MethodInfo> _actions = new();
        private static readonly List<(string suite, string action, int timeoutMs, List<TestBudget> budgets, string goldens)> _queue = new();
        private static readonly List<Result> _results = new();
        private static string _resultsRoot = string.Empty;
        private static string? _runDirectory;
        private static DateTime _started;
        private static bool _approve;

        private sealed class Result
        {
            public string suite = string.Empty;
            public string name = string.Empty;
            public int ticks;
            public List<(string message, string file, int line)> failures = new();
            public string? skipped;
            public string? capture;
            public List<TestContext.Shot> shots = new();

            public string Outcome => failures.Count > 0 ? "Fail" : skipped != null ? "Skipped"
                : shots.Any(s => s.result == "New") ? "New" : "Pass";
        }

        public static void Arm()
        {
            string? filter = null;
            foreach (string arg in Environment.GetCommandLineArgs())
            {
                if (arg == "--test") active = true;
                else if (arg == "--test-approve") _approve = true;
                else if (arg.StartsWith("--test=", StringComparison.Ordinal))
                {
                    active = true;
                    filter = arg.Substring("--test=".Length);
                }
            }
            if (!active) return;

            Engine.fixedStep = step;
            _started = DateTime.Now;

            string? hostRoot = SettingsRegistry.WriteRoot;
            string? parent = hostRoot == null ? null : Directory.GetParent(hostRoot)?.FullName;
            _resultsRoot = Path.Combine(parent ?? AppContext.BaseDirectory, "Tests");
            if (parent != null)
            {
                string settings = Path.Combine(parent, "TestSettings");
                if (Directory.Exists(settings)) Directory.Delete(settings, true);
                SettingsRegistry.SetWriteRoot(settings);

                string userData = Path.Combine(parent, "TestUserData");
                if (Directory.Exists(userData)) Directory.Delete(userData, true);
                Paths.RedirectUserData(userData);
            }

            Discover();
            Load(filter);
            Engine.Post(() => new Session());
        }

        private static void Discover()
        {
            foreach ((MethodInfo method, A_XSDActionDependencyAttribute attr) in EngineTypes.Actions)
                if (attr.Category == "Test")
                    _actions[attr.Name] = method;
        }

        private static void Load(string? filter)
        {
            bool found = false;
            foreach (string path in VirtualFileSystem.EnumerateAll("XML/Documents/Tests", "*.tests.axml"))
            {
                string suite = Paths.DocName(path);
                if (filter != null && !string.Equals(suite, filter, StringComparison.OrdinalIgnoreCase)) continue;
                if (filter == null && string.Equals(suite, "Perf", StringComparison.OrdinalIgnoreCase)) continue;
                found = true;

                XElement root = DataFiles.LoadXml(path);
                XNamespace ns = root.GetDefaultNamespace();
                foreach (XElement test in root.Elements(ns + "Test"))
                {
                    string? action = test.Attribute("Action")?.Value;
                    if (action == null) continue;
                    int timeoutMs = int.TryParse(test.Attribute("Timeout")?.Value, out int ms) ? ms : defaultTimeoutMs;
                    List<TestBudget> budgets = test.Elements(ns + "Budget").Select(b => new TestBudget
                    {
                        zone = b.Attribute("Zone")?.Value ?? string.Empty,
                        thread = b.Attribute("Thread")?.Value ?? "Main",
                        p50 = Limit(b, "P50"),
                        p95 = Limit(b, "P95"),
                        max = Limit(b, "Max"),
                        allocKB = Limit(b, "AllocKB"),
                    }).ToList();
                    _queue.Add((suite, action, timeoutMs, budgets, Path.Combine(Path.GetDirectoryName(path)!, "Goldens")));
                }
            }

            if (filter != null && !found)
                Record(filter, filter, 0, new() { ($"no suite named {filter}", "", 0) });
        }

        private static float Limit(XElement budget, string name) =>
            float.TryParse(budget.Attribute(name)?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : -1f;

        private static void Record(string suite, string name, int ticks, List<(string message, string file, int line)> failures,
            string? skipped = null, string? capture = null, List<TestContext.Shot>? shots = null)
        {
            Result result = new Result { suite = suite, name = name, ticks = ticks, failures = failures, skipped = skipped, capture = capture,
                shots = shots ?? new() };
            _results.Add(result);

            string label = suite.Length == 0 ? name : $"{suite}/{name}";
            string outcome = result.Outcome;
            if (outcome == "Fail")
                Log.Warn($"FAIL {label} — {string.Join("; ", failures.Select(Describe))}");
            else if (outcome == "Skipped")
                Log.Info($"SKIP {label} — {skipped}");
            else if (outcome == "New")
                Log.Info($"NEW {label} — no golden for {string.Join(", ", result.shots.Where(s => s.result == "New").Select(s => s.name))}; approve with --test-approve");
            else
                Log.Info($"PASS {label} ({ticks} ticks)");
        }

        // This run's folder, chosen once.
        private static string RunDirectory()
        {
            if (_runDirectory != null) return _runDirectory;

            string stamp = _started.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string directory = Path.Combine(_resultsRoot, stamp);
            for (int i = 2; Directory.Exists(directory); i++)
                directory = Path.Combine(_resultsRoot, stamp + "-" + i.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(directory);

            _runDirectory = directory;
            return directory;
        }

        // Checks each budget against its zone's per-frame totals in the capture.
        private static void CheckBudgets(string directory, List<TestBudget> budgets, List<(string message, string file, int line)> failures)
        {
            CaptureSession session = FrameCaptureReader.LoadSession(directory);
            foreach (TestBudget budget in budgets)
            {
                string label = $"{budget.thread}/{budget.zone}";
                CapturedThread? thread = session.threads.Find(t => t.thread == budget.thread);
                if (thread == null)
                {
                    failures.Add(($"no {budget.thread} thread in the capture", "", 0));
                    continue;
                }

                int name = thread.names.IndexOf(budget.zone);
                List<double> ms = new();
                long allocMax = 0;
                foreach (CapturedFrame frame in thread.frames)
                {
                    long ticks = 0, bytes = 0;
                    bool seen = false;
                    for (int i = frame.firstSpan; i < frame.firstSpan + frame.spanCount; i++)
                    {
                        CapturedSpan span = thread.spans[i];
                        if (span.name != name) continue;
                        ticks += span.end - span.begin;
                        bytes += span.bytes;
                        seen = true;
                    }
                    if (!seen) continue;
                    ms.Add(thread.Ms(ticks));
                    allocMax = Math.Max(allocMax, bytes);
                }

                if (ms.Count == 0)
                {
                    failures.Add(($"{label} never ran in the capture", "", 0));
                    continue;
                }

                ms.Sort();
                double p50 = Percentile(ms, 0.50), p95 = Percentile(ms, 0.95), max = ms[^1], allocKB = allocMax / 1024.0;
                Log.Info($"{label} over {ms.Count} frames — p50 {p50:F3} ms, p95 {p95:F3} ms, max {max:F3} ms, alloc {allocKB:F1} KB");

                Over(failures, label, "p50", p50, budget.p50, "ms");
                Over(failures, label, "p95", p95, budget.p95, "ms");
                Over(failures, label, "max", max, budget.max, "ms");
                Over(failures, label, "alloc", allocKB, budget.allocKB, "KB");
            }
        }

        private static double Percentile(List<double> sorted, double p) =>
            sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)];

        private static void Over(List<(string message, string file, int line)> failures, string label, string stat,
            double value, float limit, string unit)
        {
            if (limit >= 0f && value > limit)
                failures.Add(($"{label} {stat} {value:F3} {unit} > {limit.ToString(CultureInfo.InvariantCulture)}", "", 0));
        }

        // Compares a shot with its golden; writes the actual and a diff into the test's folder, or the golden under --test-approve.
        private static void CompareShot(TestContext.Shot shot, Arctis.Imaging.Image? frame, string? error, string action, string goldens,
            string directory, List<(string message, string file, int line)> failures)
        {
            shot.golden = Path.Combine(goldens, $"{action}.{shot.name}.png");
            if (frame == null)
            {
                shot.result = "Fail";
                failures.Add(($"golden {shot.name}: {error}", shot.file, shot.line));
                return;
            }

            if (shot.region != null)
            {
                LayoutRect r = shot.region.arrangedRect;
                Silk.NET.Vulkan.Extent2D size = new((uint)frame.Width, (uint)frame.Height);
                WindowRoot root = Engine.primary.ui.uiRoot;
                Vector2 a = root.ToWindowSpace(new Vector2(r.x, r.y), size);
                Vector2 b = root.ToWindowSpace(new Vector2(r.x + r.width, r.y + r.height), size);
                int left = Math.Clamp((int)MathF.Floor(a.X), 0, frame.Width), top = Math.Clamp((int)MathF.Floor(a.Y), 0, frame.Height);
                int right = Math.Clamp((int)MathF.Ceiling(b.X), 0, frame.Width), bottom = Math.Clamp((int)MathF.Ceiling(b.Y), 0, frame.Height);
                if (right <= left || bottom <= top)
                {
                    shot.result = "Fail";
                    failures.Add(($"golden {shot.name}: the region is off screen or empty", shot.file, shot.line));
                    return;
                }
                frame = frame.Crop(left, top, right - left, bottom - top);
            }

            string actual = Path.Combine(action, $"{shot.name}.actual.png");
            string diff = Path.Combine(action, $"{shot.name}.diff.png");
            string? mismatch = null;
            Arctis.Imaging.Image? diffImage = null;
            if (DataFiles.Exists(shot.golden))
            {
                Arctis.Imaging.Image expected;
                using (Stream goldenStream = DataFiles.Open(shot.golden))
                    expected = Arctis.Imaging.Image.Load(goldenStream, Arctis.Imaging.PixelLayout.RGBA8, ImageFiles.Limits);
                mismatch = Compare(expected, frame, out diffImage);
                if (mismatch == null)
                {
                    shot.result = "Pass";
                    return;
                }
            }

            if (_approve)
            {
                Directory.CreateDirectory(goldens);
                frame.SavePng(shot.golden);
                shot.result = "Approved";
                Log.Info($"APPROVED {shot.golden}");
                return;
            }

            Directory.CreateDirectory(directory);
            frame.SavePng(Path.Combine(RunDirectory(), actual));
            shot.actual = actual;
            if (mismatch == null)
            {
                shot.result = "New";
                return;
            }

            shot.result = "Fail";
            failures.Add(($"golden {shot.name}: {mismatch}", shot.file, shot.line));
            if (diffImage == null) return;
            diffImage.SavePng(Path.Combine(RunDirectory(), diff));
            shot.diff = diff;
        }

        // Null when the images match exactly; otherwise what differs, and a diff image when the sizes agree.
        private static string? Compare(Arctis.Imaging.Image expected, Arctis.Imaging.Image actual, out Arctis.Imaging.Image? diff)
        {
            diff = null;
            if (expected.Width != actual.Width || expected.Height != actual.Height)
                return $"size {actual.Width}x{actual.Height}, golden {expected.Width}x{expected.Height}";

            Arctis.Imaging.Image image = new Arctis.Imaging.Image(expected.Width, expected.Height);
            byte[] e = expected.Pixels, a = actual.Pixels, d = image.Pixels;
            int differ = 0, maxDelta = 0;
            for (int i = 0; i < e.Length; i += 4)
            {
                int delta = Math.Max(Math.Max(Math.Abs(e[i] - a[i]), Math.Abs(e[i + 1] - a[i + 1])), Math.Abs(e[i + 2] - a[i + 2]));
                if (delta > 0)
                {
                    differ++;
                    maxDelta = Math.Max(maxDelta, delta);
                    d[i] = 255;
                }
                else
                {
                    byte grey = (byte)(170 + (0.299f * e[i] + 0.587f * e[i + 1] + 0.114f * e[i + 2]) * 0.3f);
                    d[i] = grey;
                    d[i + 1] = grey;
                    d[i + 2] = grey;
                }
                d[i + 3] = 255;
            }

            if (differ == 0) return null;
            diff = image;
            return $"{differ} px differ, max channel delta {maxDelta}";
        }

        private static string Describe((string message, string file, int line) failure)
            => failure.file.Length == 0 ? failure.message : $"{failure.message} ({failure.file}:{failure.line})";

        private static void Finish()
        {
            int failed = _results.Count(r => r.Outcome == "Fail");
            int skipped = _results.Count(r => r.Outcome == "Skipped");
            int fresh = _results.Count(r => r.Outcome == "New");
            string path = Write();
            Log.Info($"{_results.Count - failed - skipped - fresh} passed, {failed} failed, {skipped} skipped, {fresh} new — {path}");
            Environment.ExitCode = failed;
            Engine.Post(Shutdown.Request);
        }

        private static string Write()
        {
            XElement run = new XElement("TestRun",
                new XAttribute("Host", Assembly.GetEntryAssembly()?.GetName().Name ?? string.Empty),
                new XAttribute("Build", Engine.isDebug ? "Debug" : "Release"),
                new XAttribute("Started", _started.ToString("o", CultureInfo.InvariantCulture)));

            foreach (Result result in _results)
            {
                string outcome = result.Outcome;
                run.Add(new XElement("Test",
                    new XAttribute("Suite", result.suite),
                    new XAttribute("Name", result.name),
                    new XAttribute("Result", outcome),
                    new XAttribute("Ticks", result.ticks),
                    outcome == "Skipped" ? new XAttribute("Reason", result.skipped!) : null,
                    result.capture != null ? new XAttribute("Capture", result.capture) : null,
                    result.failures.Select(f => new XElement("Failure",
                        new XAttribute("Message", f.message),
                        new XAttribute("File", f.file),
                        new XAttribute("Line", f.line))),
                    result.shots.Select(s => new XElement("Shot",
                        new XAttribute("Name", s.name),
                        new XAttribute("Result", s.result),
                        new XAttribute("Golden", s.golden),
                        s.actual != null ? new XAttribute("Actual", s.actual) : null,
                        s.diff != null ? new XAttribute("Diff", s.diff) : null))));
            }

            string path = Path.Combine(RunDirectory(), TestResultsReader.fileName);
            run.Save(path);
            return path;
        }

        // Runs Boot, then each queued test to its end.
        private sealed class Session : Entity
        {
            private int _boot;
            private int _index = -1;
            private (string suite, string action, int timeoutMs, List<TestBudget> budgets, string goldens) _current;
            private TestContext _context = null!;
            private IEnumerator<int>? _test;
            private int _ticks;
            private int _wait;
            private int _errorsAtStart;
            private bool _finished;

            public Session()
            {
                SettingsRegistry.Get<ThreadingSettings>().idle.wait = false;
                SetTicking(true);
            }

            public override void OnTick()
            {
                base.OnTick();
                if (_finished) return;

                if (_boot < bootTicks)
                {
                    if (++_boot == bootTicks) EndBoot();
                    return;
                }

                if (_test == null && !StartNext())
                {
                    _finished = true;
                    Finish();
                    return;
                }

                Step();
            }

            private void EndBoot()
            {
                int errors = Volatile.Read(ref LogSpool.errorCount);
                List<(string message, string file, int line)> failures = new();
                if (errors > 0) failures.Add(($"{errors} error(s) logged since launch", "", 0));
                Record("", "Boot", bootTicks, failures);
            }

            // Starts the next queued test that resolves; false once the queue is done.
            private bool StartNext()
            {
                while (++_index < _queue.Count)
                {
                    _current = _queue[_index];
                    if (!_actions.TryGetValue(_current.action, out MethodInfo? method))
                    {
                        Record(_current.suite, _current.action, 0, new() { ($"no test named {_current.action}", "", 0) });
                        continue;
                    }

                    _context = new TestContext { captureDirectory = Path.Combine(RunDirectory(), _current.action) };
                    _ticks = 0;
                    _wait = 0;
                    _errorsAtStart = Volatile.Read(ref LogSpool.errorCount);
                    try
                    {
                        _test = (IEnumerator<int>)method.Invoke(null, new object[] { _context })!;
                        return true;
                    }
                    catch (Exception exception)
                    {
                        End(exception);
                    }
                }
                return false;
            }

            private void Step()
            {
                _ticks++;
                if (_ticks * step * 1000.0 > _current.timeoutMs)
                {
                    End(null, $"timed out after {_current.timeoutMs} ms");
                    return;
                }
                _context.RunStep();
                if (_context.ending)
                {
                    if (!Profiling.TryFinishCapture()) return;
                    _context.ending = false;
                    _context.closed = true;
                }
                if (_context.pendingShot != null)
                {
                    if (!ScreenReadback.TryTake(Engine.primary, out Arctis.Imaging.Image? frame, out string? error)) return;
                    TestContext.Shot shot = _context.pendingShot;
                    _context.pendingShot = null;
                    Engine.clockHeld = false;
                    CompareShot(shot, frame, error, _current.action, _current.goldens, _context.captureDirectory, _context.failures);
                    _context.shots.Add(shot);
                }
                if (--_wait > 0) return;

                bool running;
                try
                {
                    running = _test!.MoveNext();
                }
                catch (Exception exception)
                {
                    End(exception);
                    return;
                }

                if (running) _wait = Math.Max(_test.Current, 1);
                else End(null);
            }

            // Records the test that just ended, with whatever failed it.
            private void End(Exception? exception, string? reason = null)
            {
                List<(string message, string file, int line)> failures = new(_context.failures);
                if (reason != null) failures.Add((reason, "", 0));
                if (exception != null)
                {
                    StackFrame? frame = new StackTrace(exception, true).GetFrame(0);
                    failures.Add(($"{exception.GetType().Name}: {exception.Message}",
                        Path.GetFileName(frame?.GetFileName() ?? ""), frame?.GetFileLineNumber() ?? 0));
                }
                string? capture = null;
                if (_context.measured)
                {
                    capture = _current.action;
                    if (!_context.closed)
                    {
                        Profiling.EndCapture();
                        failures.Add(("the test ended before its capture closed — EndMeasure never finished", "", 0));
                    }
                    else CheckBudgets(_context.captureDirectory, _current.budgets, failures);
                }
                else if (_current.budgets.Count > 0 && _context.skipped == null)
                    failures.Add(("the test has budgets but measured nothing", "", 0));

                if (_context.pendingShot != null)
                {
                    Engine.clockHeld = false;
                    failures.Add(($"golden {_context.pendingShot.name} was never read back", _context.pendingShot.file, _context.pendingShot.line));
                }

                int errors = Volatile.Read(ref LogSpool.errorCount) - _errorsAtStart;
                if (errors > 0) failures.Add(($"{errors} error(s) logged", "", 0));

                Record(_current.suite, _current.action, _ticks, failures, _context.skipped, capture, _context.shots);
                if (exception != null) Log.Exception(LogLevel.Warn, exception, $"{_current.action} threw");
                _test = null;
            }
        }
    }
}
