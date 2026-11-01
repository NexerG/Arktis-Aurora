using ArctisAurora.Core.Diagnostics;
using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Testing;
using ArctisAurora.Core.Threading;
using ArctisAurora.EngineWork;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace ArctisAurora.Core.Commands
{
    // Typed commands: every parameterless static void action in Input, UI and Any, plus Console actions, which may
    // take arguments and return a reply. Lines arrive from the console window, --exec and the command pipe.
    public static class CommandConsole
    {
        private static readonly LogChannel Log = LogChannel.For("Console");

        private static readonly string[] plainCategories = { "Input", "UI", "Any" };
        private const string consoleCategory = "Console";
        private const string waitCommand = "Wait";

        private static readonly Dictionary<string, MethodInfo> _commands = new(StringComparer.OrdinalIgnoreCase);

        // lines paused on a Wait, and the entity that resumes them
        private static readonly List<Line> _waiting = new();
        private static Pump? _pump;

        private sealed class Line
        {
            public readonly Queue<string> commands;
            public readonly StringBuilder replies = new();
            public readonly Action<string>? reply;
            public double resumeAt;
            public long resumeAfterFrame;

            public Line(string text, Action<string>? reply)
            {
                commands = new Queue<string>(text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                this.reply = reply;
            }
        }

        public static void Arm()
        {
            Build();
            if (TestRunner.active) return;

            string? exec = ArgumentAfter("--exec");
            if (exec != null) Engine.Post(() =>
            {
                Log.Info($"> {exec}");
                Wait(new Line(exec, null), 0);
            });

            new Thread(ReadConsoleWindow) { Name = "console input", IsBackground = true }.Start();
            if (Engine.isDebug) CommandPipe.Serve();
        }

        // The argument that follows flag on the command line.
        internal static string? ArgumentAfter(string flag)
        {
            string[] args = Environment.GetCommandLineArgs();
            int at = Array.IndexOf(args, flag);
            return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
        }

        // Runs a line of ';'-separated commands on the main thread; reply gets one result per command once all have run.
        public static void Execute(string text, Action<string>? reply)
        {
            Log.Info($"> {text}");
            Continue(new Line(text, reply));
        }

        private static void Build()
        {
            foreach ((MethodInfo method, A_XSDActionDependencyAttribute attr) in EngineTypes.Actions)
            {
                bool plain = Array.IndexOf(plainCategories, attr.Category) >= 0
                    && method.ReturnType == typeof(void) && method.GetParameters().Length == 0;
                if (plain || attr.Category == consoleCategory)
                    _commands.TryAdd(attr.Name, method);
            }
        }

        private static void Continue(Line line)
        {
            while (line.commands.TryDequeue(out string? command))
            {
                string[] words = Split(command);
                if (words.Length == 0) continue;

                if (string.Equals(words[0], waitCommand, StringComparison.OrdinalIgnoreCase))
                {
                    if (words.Length == 2 && int.TryParse(words[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ms) && ms >= 0)
                    {
                        Wait(line, ms);
                        return;
                    }
                    Answer(line, "error: Wait takes one whole number of milliseconds");
                    continue;
                }

                Answer(line, Run(words));
            }

            line.reply?.Invoke(line.replies.ToString().TrimEnd('\n'));
        }

        private static string Run(string[] words)
        {
            if (!_commands.TryGetValue(words[0], out MethodInfo? method))
                return $"error: no command named {words[0]}";

            ParameterInfo[] parameters = method.GetParameters();
            if (words.Length - 1 != parameters.Length)
                return $"error: {words[0]} takes {parameters.Length} argument(s)";

            object?[] values = new object?[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
                if (!TryConvert(words[i + 1], parameters[i].ParameterType, out values[i]))
                    return $"error: {words[0]}: '{words[i + 1]}' is not a {parameters[i].ParameterType.Name}";

            try
            {
                return method.Invoke(null, values) as string ?? "ok";
            }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            {
                Log.Exception(LogLevel.Warn, exception.InnerException, $"{words[0]} threw");
                return $"error: {exception.InnerException.GetType().Name}: {exception.InnerException.Message}";
            }
        }

        private static void Answer(Line line, string result)
        {
            if (result.StartsWith("error:", StringComparison.Ordinal)) Log.Warn($"{result}");
            else Log.Info($"{result}");
            line.replies.Append(result).Append('\n');
        }

        // Splits on whitespace; double quotes group words.
        private static string[] Split(string command)
        {
            List<string> words = new List<string>();
            StringBuilder word = new StringBuilder();
            bool quoted = false;
            foreach (char c in command)
            {
                if (c == '"')
                {
                    quoted = !quoted;
                    continue;
                }
                if (char.IsWhiteSpace(c) && !quoted)
                {
                    if (word.Length > 0) words.Add(word.ToString());
                    word.Clear();
                    continue;
                }
                word.Append(c);
            }
            if (word.Length > 0) words.Add(word.ToString());
            return words.ToArray();
        }

        internal static bool TryConvert(string text, Type type, out object? value)
        {
            value = null;
            if (type == typeof(string)) value = text;
            else if (type == typeof(int) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)) value = i;
            else if (type == typeof(float) && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) value = f;
            else if (type == typeof(bool) && bool.TryParse(text, out bool b)) value = b;
            else if (type.IsEnum && Enum.TryParse(type, text, true, out object? e)) value = e;
            return value != null;
        }

        private static void Wait(Line line, int ms)
        {
            line.resumeAt = Engine.totalTime + ms / 1000.0;
            line.resumeAfterFrame = FrameScheduler.Frame;
            _waiting.Add(line);
            FrameScheduler.RequestFrameAt(Engine.totalTime + Math.Max(ms, 1) / 1000.0);

            _pump ??= new Pump();
            _pump.SetTicking(true);
        }

        private static void ReadConsoleWindow()
        {
            string? text;
            while ((text = Console.In.ReadLine()) != null)
            {
                string line = text;
                if (line.Trim().Length > 0) Engine.Post(() => Execute(line, null));
            }
        }

        [A_XSDActionDependency("Help", "Console")]
        private static string Help() => string.Join(", ", _commands.Keys.Append(waitCommand).Order(StringComparer.OrdinalIgnoreCase));

        [A_XSDActionDependency("Quit", "Console")]
        private static void Quit() => Engine.Post(Shutdown.Request);

        // Resumes lines whose Wait has run out; ticks only while a line is waiting.
        private sealed class Pump : Entity
        {
            public override void OnTick()
            {
                base.OnTick();
                Line[] due = _waiting.Where(l => Engine.totalTime >= l.resumeAt && FrameScheduler.Frame > l.resumeAfterFrame).ToArray();
                foreach (Line line in due)
                {
                    _waiting.Remove(line);
                    Continue(line);
                }
                if (_waiting.Count == 0) SetTicking(false);
            }
        }
    }
}
