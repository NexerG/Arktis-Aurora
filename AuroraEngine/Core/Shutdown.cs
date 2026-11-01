using ArctisAurora.Core.Diagnostics;
using ArctisAurora.Core.Filing.Serialization;
using ArctisAurora.Core.Registry;
using System.Diagnostics;
using System.Reflection;
using System.Xml.Linq;

namespace ArctisAurora.EngineWork
{
    [A_XSDType("Step", "Shutdown")]
    public class ShutdownStep
    {
        [A_XSDElementProperty("Action", "Shutdown")]
        public Action? action { get; set; }
    }

    [A_XSDType("Phase", "Shutdown")]
    public class ShutdownPhase
    {
        [A_XSDElementProperty("Name", "Shutdown")]
        public string name { get; set; } = string.Empty;

        [A_XSDElementProperty("Step", "Shutdown")]
        public List<ShutdownStep> steps { get; set; } = new();
    }

    // The bootstrap sequence run backwards: phases of ordered steps, declared in Shutdown.shutdown.axml and
    // resolved to methods by name. Two phases, because they answer different questions — Request may
    // refuse and is where anything that asks the user lives, Commit is past the point of no return.
    [A_XSDType("ShutdownSequence", "Shutdown")]
    internal static class Shutdown
    {
        private static readonly LogChannel Log = LogChannel.For("Shutdown");

        private const double slowStepMs = 5.0;

        [A_XSDElementProperty("Phase", "Shutdown")]
        public static List<ShutdownPhase> phases { get; set; } = new();

        public const string requestPhase = "Request";
        public const string commitPhase = "Commit";

        private static Dictionary<string, List<string>> _phases = new();  // phase name -> ordered step names
        private static Dictionary<string, MethodInfo> _actions = new();   // step name -> method

        // True from the first Request until the process goes, so a step can tell a shutdown that is
        // under way from one that has not started.
        public static bool isClosing { get; private set; }

        public static void Load(string xmlPath)
        {
            foreach ((MethodInfo method, A_XSDActionDependencyAttribute attr) in EngineTypes.Actions)
                if (attr.Category == "Shutdown")
                    _actions[attr.Name] = method;

            XElement root = DataFiles.LoadXml(xmlPath);
            XNamespace ns = root.GetDefaultNamespace();
            foreach (XElement phaseElem in root.Elements(ns + "Phase"))
            {
                string phaseName = phaseElem.Attribute("Name")?.Value ?? "Default";
                List<string> steps = new List<string>();
                foreach (XElement step in phaseElem.Elements(ns + "Step"))
                {
                    string action = step.Attribute("Action")?.Value;
                    if (action != null)
                        steps.Add(action);
                }
                _phases[phaseName] = steps;
            }
        }

        // A fresh attempt, from a user gesture. Anything a previous attempt remembered is dropped.
        public static void Request()
        {
            Run();
        }

        // Re-enters the sequence after a step that returned false went off to ask the user something.
        // The step that refused is re-run and answers differently this time.
        public static void Resume() => Run();

        private static void Run()
        {
            isClosing = true;

            if (!RunPhase(requestPhase))
            {
                isClosing = false;
                return;
            }

            // Nothing here may refuse — a failed step is logged and the rest of its phase is skipped,
            // but the application still goes, or one broken handler makes it unquittable.
            RunPhase(commitPhase);

            Engine.CloseWindow(Engine.primary);
        }

        // A step that reports failure halts its phase — nothing after it runs.
        public static bool RunPhase(string phaseName)
        {
            if (!_phases.TryGetValue(phaseName, out List<string> steps))
            {
                Log.Error($"phase '{phaseName}' not found.");
                return false;
            }

            long phaseStart = Stopwatch.GetTimestamp();
            int ran = 0;

            foreach (string stepName in steps)
            {
                if (!_actions.TryGetValue(stepName, out MethodInfo method))
                {
                    Log.Warn($"action '{stepName}' not found — skipping.");
                    continue;
                }

                Log.Info($"running: {stepName}");

                long stepStart = Stopwatch.GetTimestamp();
                bool failed = method.Invoke(null, null) is false;
                double ms = ElapsedMs(stepStart);
                ran++;

                if (ms >= slowStepMs) Log.Info($"{stepName} — {ms:F0}ms");

                if (failed)
                {
                    Log.Error($"step '{stepName}' reported failure — phase '{phaseName}' halted.");
                    return false;
                }
            }

            Log.Info($"phase '{phaseName}' — {ElapsedMs(phaseStart):F0}ms, {ran} steps");
            return true;
        }

        private static double ElapsedMs(long since) => (Stopwatch.GetTimestamp() - since) * 1000.0 / Stopwatch.Frequency;
    }
}
