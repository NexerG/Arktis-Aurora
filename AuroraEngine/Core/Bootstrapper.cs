using ArctisAurora.Core.Diagnostics;
using ArctisAurora.Core.Filing.Serialization;
using ArctisAurora.Core.Registry;
using System.Diagnostics;
using System.Reflection;
using System.Xml.Linq;

namespace ArctisAurora.EngineWork
{
    [A_XSDType("Step", "Bootstrap")]
    public class BootstrapStep
    {
        [A_XSDElementProperty("Action", "Bootstrap")]
        public Action? action { get; set; }
    }

    [A_XSDType("Phase", "Bootstrap")]
    public class BootstrapPhase
    {
        [A_XSDElementProperty("Name", "Bootstrap")]
        public string name { get; set; } = string.Empty;

        [A_XSDElementProperty("Step", "Bootstrap")]
        public List<BootstrapStep> steps { get; set; } = new();
    }

    [A_XSDType("BootstrapSequence", "Bootstrap")]
    internal static class Bootstrapper
    {
        private static readonly LogChannel Log = LogChannel.For("Bootstrap");

        // A step below this is not worth a line of its own.
        private const double slowStepMs = 5.0;

        [A_XSDElementProperty("Phase", "Bootstrap")]
        public static List<BootstrapPhase> phases { get; set; } = new();

        private static Dictionary<string, List<string>> _phases = new();  // phase name -> ordered step names
        private static Dictionary<string, MethodInfo> _actions = new();   // step name -> method

        public static void Load(string xmlPath)
        {
            foreach ((MethodInfo method, A_XSDActionDependencyAttribute attr) in EngineTypes.Actions)
                if (attr.Category == "Bootstrap")
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

            Profiling.Frame.Begin(phaseName);

            foreach (string stepName in steps)
            {
                if (!_actions.TryGetValue(stepName, out MethodInfo method))
                {
                    Log.Warn($"action '{stepName}' not found — skipping.");
                    continue;
                }

                // Printed before the call, so a boot that wedges names the step it wedged in.
                Log.Info($"running: {stepName}");

                long stepStart = Stopwatch.GetTimestamp();
                Profiling.Zone.Start(stepName);
                bool failed = method.Invoke(null, null) is false;
                Profiling.Zone.End(stepName);
                double ms = ElapsedMs(stepStart);
                ran++;

                if (ms >= slowStepMs) Log.Info($"{stepName} — {ms:F0}ms");

                if (failed)
                {
                    Log.Error($"step '{stepName}' reported failure — phase '{phaseName}' halted.");
                    Profiling.Frame.End();
                    return false;
                }
            }

            Profiling.Frame.End();

            Log.Info($"phase '{phaseName}' — {ElapsedMs(phaseStart):F0}ms, {ran} steps");
            return true;
        }

        private static double ElapsedMs(long since) => (Stopwatch.GetTimestamp() - since) * 1000.0 / Stopwatch.Frequency;
    }
}