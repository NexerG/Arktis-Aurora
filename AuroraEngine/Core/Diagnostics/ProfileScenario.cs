using ArctisAurora.Core.Animation;
using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Threading;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Document;
using ArctisAurora.Core.UI.Notes;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.Core.UI.Windows;
using ArctisAurora.EngineWork;
using ArctisAurora.EngineWork.Rendering;
using System.Numerics;
using System.Text;

namespace ArctisAurora.Core.Diagnostics
{
    // --profile-scenario: types into and resizes a 1,000,000-character note, then resizes settings, under one capture.
    // --profile-scenario=animation: stresses the animation system over a ladder of button grids, under one capture.
    public unsafe class ProfileScenario : Entity
    {
        private static readonly LogChannel Log = LogChannel.For("Profiling");

        // timeline, in main ticks
        private const int openTick = 2;
        private const int settleTicks = 30;
        private const int typeTicks = 120;
        private const int resizeTicks = 120;
        private const int resizeStep = 8;
        private const int settingsSettleTicks = 30;
        private const int settingsResizeTicks = 120;
        private const int settingsHoldTicks = 900;

        // document shape
        private const int blockCount = 1000;
        private const int blockChars = 1000;

        private RichTextDocument document = null!;
        private int tick;
        private int startWidth;
        private int startHeight;
        private RenderWindow settings = null!;
        private int settingsWidth;
        private int settingsHeight;

        // animation mode: grid sizes, grid shape, and timeline in main ticks
        private static readonly int[] ladder = { 200000 };
        private const int rowLength = 100;
        private const float buttonSize = 16f;
        private const int startBatch = 1000;
        private const int stopBatch = 250;
        private const int rampTicks = 20;
        private const int stopTicks = 80;
        private const int stopAllSample = 1000;
        private const int sparseCount = 1000;
        private const int animHoldTicks = 240;
        private const int flipTicks = 60;

        private readonly IEnumerator<int>? animation;
        private readonly List<ButtonControl> buttons = new List<ButtonControl>();
        private readonly List<AnimationHandle> handles = new List<AnimationHandle>();
        private WindowRoot? grid;

        // --dump-tree: writes the laid-out tree at settled points, for comparing layout between builds
        private readonly bool dumpTree;

        private ProfileScenario(bool animation, bool dumpTree)
        {
            if (animation) this.animation = RunAnimation();
            this.dumpTree = dumpTree;
            SettingsRegistry.Get<ThreadingSettings>().idle.wait = false;
            SetTicking(true);
        }

        public static void Arm()
        {
            string[] args = Environment.GetCommandLineArgs();
            bool animation = Array.IndexOf(args, "--profile-scenario=animation") >= 0;
            if (!animation && Array.IndexOf(args, "--profile-scenario") < 0) return;
            bool dumpTree = Array.IndexOf(args, "--dump-tree") >= 0;

            string? hostRoot = SettingsRegistry.WriteRoot;
            if (hostRoot != null)
                SettingsRegistry.SetWriteRoot(Path.Combine(Directory.GetParent(hostRoot)!.FullName, "ProfileScenario"));
            Engine.Post(() => new ProfileScenario(animation, dumpTree));
        }

        // Replaces the primary window's tree with an editor holding the generated note.
        private void Open()
        {
            StringBuilder builder = new StringBuilder(blockChars);
            while (builder.Length < blockChars)
                builder.Append("The quick brown fox jumps over the lazy dog while every line of the note wraps again. ");
            string paragraph = builder.ToString(0, blockChars);

            document = new RichTextDocument { blocks = new NoteNode[blockCount] };
            for (int i = 0; i < blockCount; i++)
            {
                NoteBlock block = new NoteBlock();
                block.AppendRun(new Run { text = paragraph });
                document.blocks[i] = block;
            }

            DocumentEditorControl editor = new DocumentEditorControl
            {
                horizontalAlignment = HorizontalAlignment.Stretch,
                verticalAlignment = VerticalAlignment.Stretch
            };
            WindowRoot root = new WindowRoot();
            root.AddChild(editor);
            Engine.primary.ui.uiRoot = root;
            editor.LoadDocument(document);

            DocumentControl content = editor.children.OfType<DocumentControl>().First();
            BlockControl first = content.Blocks()[0];
            content.SetCaret(first, first.Length);
            editor.FocusCaret();

            AGlfwWindow._glfw.GetWindowSize(Engine.primary.os.handle, out startWidth, out startHeight);
            Log.Info($"scenario — {document.blocks.OfType<NoteBlock>().Sum(b => b.run.Length)} chars in {blockCount} blocks, window {startWidth}x{startHeight}");
        }

        public override void OnTick()
        {
            base.OnTick();
            tick++;

            if (animation != null)
            {
                animation.MoveNext();
                return;
            }

            if (tick == openTick)
            {
                Open();
                return;
            }

            if (tick == settleTicks)
            {
                if (dumpTree) UITreeDump.Dump("open");
                Profiling.CaptureUntilFlush();
                return;
            }

            int step = tick - settleTicks;
            if (step <= 0) return;
            if (dumpTree && step == typeTicks + 1) UITreeDump.Dump("typed");

            if (step <= typeTicks)
            {
                Profiling.Zone.Start("Scenario.Type");
                InputHandler.charInputReadQueue.Enqueue((char)('a' + step % 26));
                TextInputActions.Write();
                Profiling.Zone.End("Scenario.Type");
            }
            else if (step <= typeTicks + resizeTicks)
            {
                Profiling.Zone.Start("Scenario.Resize");
                int r = step - typeTicks;
                int narrowed = r <= resizeTicks / 2 ? r : resizeTicks - r;
                AGlfwWindow._glfw.SetWindowSize(Engine.primary.os.handle, startWidth - narrowed * resizeStep, startHeight);
                Profiling.Zone.End("Scenario.Resize");
            }
            else if (step == typeTicks + resizeTicks + 1)
            {
                SettingsWindow.Open(Engine.primary);
                SettingsWindow.ShowCategory(SettingsWindow.keybindsCategory);
                settings = Engine.windows["settings"];
                AGlfwWindow._glfw.GetWindowSize(settings.os.handle, out settingsWidth, out settingsHeight);
            }
            else if (step <= typeTicks + resizeTicks + 1 + settingsSettleTicks) { }
            else if (step <= typeTicks + resizeTicks + 1 + settingsSettleTicks + settingsResizeTicks)
            {
                Profiling.Zone.Start("Scenario.ResizeSettings");
                int r = step - (typeTicks + resizeTicks + 1 + settingsSettleTicks);
                int widened = r <= settingsResizeTicks / 2 ? r : settingsResizeTicks - r;
                AGlfwWindow._glfw.SetWindowSize(settings.os.handle, settingsWidth + widened * resizeStep, settingsHeight);
                Profiling.Zone.End("Scenario.ResizeSettings");
            }
            else if (step == typeTicks + resizeTicks + 1 + settingsSettleTicks + settingsResizeTicks + settingsHoldTicks + 1)
            {
                if (dumpTree) UITreeDump.Dump("settings");
                AGlfwWindow._glfw.GetWindowSize(Engine.primary.os.handle, out int width, out int height);
                Log.Info($"scenario done — {document.blocks.OfType<NoteBlock>().Sum(b => b.run.Length)} chars, window {width}x{height}");
                Engine.Post(Shutdown.Request);
            }
        }

        #region animation
        // Fades every paint slot, then runs burst, clip, spring and stop stages on each grid size; one step per main tick.
        private IEnumerator<int> RunAnimation()
        {
            for (int i = 0; i < settleTicks; i++) yield return 0;
            Profiling.CaptureUntilFlush();

            Log.Info($"animation scenario — fading every paint slot");
            Profiling.Zone.Start("Scenario.FadeStart");
            Animations.FadeSlots(0, 0, int.MaxValue, 1f, Curve.Ease(EaseKind.CubicInOut), () => { });
            Profiling.Zone.End("Scenario.FadeStart");
            foreach (int t in Hold("Scenario.Fade", animHoldTicks)) yield return t;

            foreach (int count in ladder)
            {
                Log.Info($"animation scenario — {count} buttons");
                Profiling.Zone.Start("Scenario.Build");
                BuildGrid(count);
                Profiling.Zone.End("Scenario.Build");
                foreach (int t in Hold("Scenario.Settle", settleTicks)) yield return t;
                if (dumpTree) UITreeDump.Dump($"grid-{count}");

                Profiling.Zone.Start("Scenario.Burst");
                foreach (ButtonControl button in buttons)
                    Animations.Tween(button, "state", new Vector4(2f, 0f, 0f, 0f), 0.5f, Curve.Ease(EaseKind.CubicInOut));
                Profiling.Zone.End("Scenario.Burst");
                foreach (int t in Hold("Scenario.BurstHold", animHoldTicks)) yield return t;

                foreach (int t in Ramp("Scenario.StateRamp", b => Keep(Animations.Play(b, "profile-state", false)))) yield return t;
                foreach (int t in Hold("Scenario.StateHold", animHoldTicks)) yield return t;
                foreach (int t in StopHandles()) yield return t;

                foreach (int t in Ramp("Scenario.MarginRamp", b => Keep(Animations.Play(b, "profile-margin", false)))) yield return t;
                foreach (int t in Hold("Scenario.MarginHold", animHoldTicks)) yield return t;
                foreach (int t in StopHandles()) yield return t;

                int stride = Math.Max(1, buttons.Count / sparseCount);
                Profiling.Zone.Start("Scenario.SparseMarginStart");
                for (int i = 0; i < buttons.Count; i += stride) Keep(Animations.Play(buttons[i], "profile-margin", false));
                Profiling.Zone.End("Scenario.SparseMarginStart");
                foreach (int t in Hold("Scenario.SparseMarginHold", animHoldTicks)) yield return t;
                foreach (int t in StopHandles()) yield return t;

                SignalHandle signal = Signals.Create();
                foreach (int t in Ramp("Scenario.SpringRamp", b => Keep(Animations.Spring(b, "state", 6f, 1f, signal)))) yield return t;
                for (int i = 1; i <= animHoldTicks; i++)
                {
                    Profiling.Zone.Start("Scenario.SpringHold");
                    if (i % flipTicks == 0) Signals.Set(signal, new Vector4(i / flipTicks % 2 * 2f, 0f, 0f, 0f));
                    Profiling.Zone.End("Scenario.SpringHold");
                    yield return 0;
                }

                int sample = Math.Min(stopAllSample, buttons.Count);
                for (int i = 0; i < sample; i += stopBatch)
                {
                    Profiling.Zone.Start("Scenario.StopAll");
                    for (int j = i; j < Math.Min(i + stopBatch, sample); j++) Animations.StopAll(buttons[j]);
                    Profiling.Zone.End("Scenario.StopAll");
                    yield return 0;
                }
                foreach (int t in StopHandles()) yield return t;
                Signals.Release(signal);

                BuildGrid(0);
                foreach (int t in Hold("Scenario.Teardown", settleTicks)) yield return t;
            }

            Log.Info($"animation scenario done");
            Engine.Post(Shutdown.Request);
            while (true) yield return 0;
        }

        // Replaces the primary window's tree with count buttons in rows, destroying the previous grid.
        private void BuildGrid(int count)
        {
            StackPanelControl rows = new StackPanelControl();
            StackPanelControl row = null!;
            buttons.Clear();
            for (int i = 0; i < count; i++)
            {
                if (i % rowLength == 0)
                {
                    row = new StackPanelControl { orientation = StackPanelControl.Orientation.Horizontal };
                    rows.AddChild(row);
                }
                ButtonControl button = new ButtonControl { preferredWidth = buttonSize, preferredHeight = buttonSize };
                row.AddChild(button);
                buttons.Add(button);
            }

            WindowRoot? previous = grid;
            grid = new WindowRoot();
            grid.AddChild(rows);
            Engine.primary.ui.uiRoot = grid;
            previous?.Destroy();
        }

        // Starts buttons over rampTicks ticks, resuming at the first one the request lane refused.
        private IEnumerable<int> Ramp(string zone, Func<ButtonControl, bool> start)
        {
            int next = 0;
            int batch = Math.Max(startBatch, buttons.Count / rampTicks);
            while (next < buttons.Count)
            {
                Profiling.Zone.Start(zone);
                int end = Math.Min(next + batch, buttons.Count);
                while (next < end && start(buttons[next])) next++;
                Profiling.Zone.End(zone);
                yield return 0;
            }
        }

        // Stops kept handles over stopTicks ticks.
        private IEnumerable<int> StopHandles()
        {
            int batch = Math.Max(stopBatch, handles.Count / stopTicks);
            for (int i = 0; i < handles.Count; i += batch)
            {
                for (int j = i; j < Math.Min(i + batch, handles.Count); j++) Animations.Stop(handles[j]);
                yield return 0;
            }
            handles.Clear();
        }

        private static IEnumerable<int> Hold(string zone, int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                Profiling.Zone.Start(zone);
                Profiling.Zone.End(zone);
                yield return 0;
            }
        }

        private bool Keep(AnimationHandle[] started)
        {
            if (started.Length == 0) return false;
            handles.Add(started[0]);
            return true;
        }

        private bool Keep(AnimationHandle started)
        {
            if (started == AnimationHandle.None) return false;
            handles.Add(started);
            return true;
        }
        #endregion
    }
}
