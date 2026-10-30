using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Animation;
using ArctisAurora.Core.Commands;
using ArctisAurora.Core.Data;
using ArctisAurora.Core.Diagnostics;
using ArctisAurora.Core.Threading;
using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Filing.Serialization;
using ArctisAurora.Core.Testing;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Menus;
using ArctisAurora.EngineWork.Registry;
using ArctisAurora.EngineWork.Rendering;
using ArctisAurora.EngineWork.Rendering.Modules;
using Silk.NET.GLFW;
using System.Numerics;
using System.Runtime.InteropServices;

namespace ArctisAurora.EngineWork
{
    public unsafe class Engine
    {
        private static readonly Core.Diagnostics.LogChannel Log = Core.Diagnostics.LogChannel.For("Engine");

        public static bool isDebug
        {
            get
            {
#if DEBUG
                return true;
#else
                return false;
#endif
            }
        }

        [DllImport("kernel32.dll")]
        static extern int GetCurrentThreadId();

        internal static Engine engineInstance = null;

        // Every open OS window, by name. Copy-on-write: a mutation builds a whole new dictionary and
        // publishes it, so the render thread reads the reference once and walks a map nobody will
        // touch again. The window the application boots into is "main" and is never removed —
        // closing it closes the application.
        private static Dictionary<string, RenderWindow> _windows = new Dictionary<string, RenderWindow>();
        public static Dictionary<string, RenderWindow> windows => Volatile.Read(ref _windows);
        public static RenderWindow primary = null!;

        public const string mainWindow = "main";

        // work handed back to the main thread
        private static readonly System.Collections.Concurrent.ConcurrentQueue<Action> _posted =
            new System.Collections.Concurrent.ConcurrentQueue<Action>();

        internal static Renderer renderer = null!;
        internal static InputHandler inputHandler = null!;
        internal static AssetRegistries assetRegistry = new AssetRegistries();
        internal static EntityRegistry entityManager = null!;

        // quick access
        internal static List<Entity> entitiesOnDestroy = null!;

        // systems — stepped by FrameScheduler from Frame.frame.axml, or Dedicated on a thread of their own
        internal static MainSystem mainSystem = null!;
        internal static PhysicsSystem physicsSystem = null!;
        internal static RenderSystem renderSystem = null!;
        internal static AnimationSystem animationSystem = null!;

        public bool running => mainSystem != null && mainSystem.Running;

        static bool isCaughtUp = true;

        internal static DateTime initTime;

        public static TimeSpan deltaTime;
        private static DateTime lastFrameTime = DateTime.Now;
        public static double totalTime = 0;
        // seconds per tick while the clock is fixed, 0 for real time
        public static double fixedStep;
        // the fixed clock stands still while set
        public static bool clockHeld;
        //private DateTime lastFrameTime = DateTime.Now;

        public Engine()
        {
            string? send = CommandConsole.ArgumentAfter("--send");
            if (send != null) Environment.Exit(CommandPipe.Send(send));

            engineInstance = this;
            Log.Info($"starting main thread at OS id {(OperatingSystem.IsWindows() ? GetCurrentThreadId() : Environment.ProcessId)}");
        }

        public void Init(bool startImmediately)
        {
            //Image<Rgba32> im = new Image<Rgba32>(16, 16);
            //for (int i = 0; i < 16; i++)
            //{
            //    for (int j = 0; j < 16; j++)
            //    {
            //        im[i, j] = new Rgba32(255, 255, 255, 255);
            //    }
            //}
            //im.Save(Paths.UIMASKS + "\\defaultMask.png");

            Bootstrapper.Load(Paths.BOOTSTRAP);
            Profiling.ArmBoot();
            ProfileScenario.Arm();
            TestRunner.Arm();
            CommandConsole.Arm();
            Bootstrapper.RunPhase("Bootstrap");
            Shutdown.Load(Paths.SHUTDOWN);

            mainSystem = new MainSystem();
            physicsSystem = new PhysicsSystem();
            renderSystem = new RenderSystem();
            animationSystem = new AnimationSystem();

            FrameScheduler.Load(Paths.FRAME);
            FrameScheduler.Start();

            if(startImmediately)
            {
                Run();
            }
        }

        #region ---- BOOTSTRAPPING ----
        [A_XSDActionDependency("Engine.SystemSetup", "Bootstrap")]
        public static bool SetupSystems()
        {
            entityManager = EntityRegistry.manager;
            return true;
        }

        [A_XSDActionDependency("Engine.InitWindowing", "Bootstrap")]
        public static bool InitWindowing()
        {
            GraphicsSettings settings = SettingsRegistry.Get<GraphicsSettings>();
            float scale = (TestRunner.active ? 1f : AGlfwWindow.PrimaryContentScale()) * UIScaling.Zoom;
            RenderWindow window = new RenderWindow((uint)MathF.Ceiling(settings.window.width * scale),
                                                   (uint)MathF.Ceiling(settings.window.height * scale));
            primary = window;
            window.hidden = Background.primaryStartsHidden;
            Publish(mainWindow, window);

            window.os.CreateWindow();
            WireInput(window);
            window.os.SeedIsInWindow();
            window.gpuReady = true;   // the bootstrap steps build the primary's resources in stages
            return true;
        }

        // A window opened after bootstrap. Only the OS window is made here — the render thread owns
        // Vulkan, so it builds the GPU side at the top of its next tick and Draw skips until then.
        public static RenderWindow OpenWindow(string name, uint width, uint height, int x, int y)
        {
            RenderWindow window = new RenderWindow(width, height);
            window.os.CreateWindow(name, x, y);
            WireInput(window);

            Publish(name, window);
            return window;
        }

        // Top left of a width x height window centred in the primary monitor's work area.
        public static (int x, int y) CenterOnPrimary(int width, int height) => AGlfwWindow.CenterOnPrimary(width, height);

        // The OS title of a window, as the taskbar and Alt+Tab show it.
        public static void SetTitle(RenderWindow window, string title) => window.os.SetTitle(title);

        // A menu is clicked, so unlike the preview it wires input and is an ordinary window to every
        // walk that skips ghosts.
        public static RenderWindow OpenMenuWindow(string name, uint width, uint height, bool withChrome = false)
        {
            RenderWindow window = new RenderWindow(width, height);
            window.os.CreateMenuWindow(withChrome);
            WireInput(window);

            Publish(name, window);
            return window;
        }

        // No input callbacks — a preview window is looked at, never clicked.
        public static RenderWindow OpenGhostWindow(string name, uint width, uint height)
        {
            RenderWindow window = new RenderWindow(width, height) { isGhost = true };
            window.os.CreateGhostWindow();

            Publish(name, window);
            return window;
        }

        // Marks a window for teardown. The render thread frees its Vulkan objects, then MainTick
        // destroys the OS window and drops it from the map.
        public static void CloseWindow(RenderWindow window)
        {
            if (window == primary)
            {
                engineInstance?.Stop();
                return;
            }

            window.ui.uiRoot = null;
            window.closeRequested = true;
        }

        private static void Publish(string name, RenderWindow window)
        {
            Dictionary<string, RenderWindow> next = new Dictionary<string, RenderWindow>(_windows);
            next[name] = window;
            Volatile.Write(ref _windows, next);
        }

        private static void Unpublish(string name)
        {
            Dictionary<string, RenderWindow> next = new Dictionary<string, RenderWindow>(_windows);
            next.Remove(name);
            Volatile.Write(ref _windows, next);
        }

        // Queues work for the next main tick, from any thread.
        public static void Post(Action work)
        {
            _posted.Enqueue(work);
            AGlfwWindow._glfw?.PostEmptyEvent();
        }

        internal static bool HasPosted => !_posted.IsEmpty;

        private static void DrainPosted()
        {
            while (_posted.TryDequeue(out Action work)) work();
        }

        // Destroys the OS window of anything the render thread has finished freeing.
        private static void ReapClosedWindows()
        {
            Dictionary<string, RenderWindow> snapshot = windows;
            foreach (KeyValuePair<string, RenderWindow> entry in snapshot)
            {
                if (!entry.Value.closeRequested || !entry.Value.gpuDestroyed) continue;

                Context.Forget(entry.Value);
                Unpublish(entry.Key);
                entry.Value.os.DestroyWindow();
            }
        }

        // The window a GLFW callback fired for.
        public static RenderWindow WindowFor(WindowHandle* handle)
        {
            foreach (RenderWindow window in windows.Values)
                if (window.os.handle == handle)
                    return window;
            return null;
        }

        private static void WireInput(RenderWindow window)
        {
            if (TestRunner.active) return;

            GlfwInput.Wire(window);
        }

        [A_XSDActionDependency("Renderer.InitRenderer", "Bootstrap")]
        public static bool InitiateRenderer()
        {
            renderer = new Renderer();
            return true;
        }

        [A_XSDActionDependency("Renderer.PreInitialize", "Bootstrap")]
        public static bool SetupModules()
        {
            renderer.PreInitialize();
            return true;
        }
        #endregion

        // Blocks on the calling thread — main cannot be handed a spawned thread, GLFW requires
        // PollEvents on the one that created the window.
        public void Run() => FrameScheduler.Run();

        // The Main.Input step. MainSystem drives this; the body stays here because it touches the
        // registries, input handler and UI state that Engine owns.
        internal void Input()
        {
            Profiling.Zone.Start("PollEvents");
            AGlfwWindow._glfw.PollEvents();
            Profiling.Zone.End("PollEvents");

            Profiling.Zone.Start("WindowingTick");
            ReapClosedWindows();
            DrainPosted();
            Profiling.Zone.End("WindowingTick");

            Profiling.Zone.Start("ActivateKeybinds");
            InputHandler.instance.ActivateKeybinds();
            Profiling.Zone.End("ActivateKeybinds");

            Profiling.Zone.Start("HandleUI");
            foreach (RenderWindow window in windows.Values)
            {
                Profiling.Zone.Increment("Window");
                HandleUI(window);
            }
            DragGhost.Follow();
            ContextMenus.Tick();
            Profiling.Zone.End("HandleUI");
        }

        private void HandleUI(RenderWindow window)
        {
            if (window.closeRequested) return;

            UIEngine.Poll(window);
        }

        internal void Interpolate()
        {
            // have we caught up?
            if (isCaughtUp)
            {
                isCaughtUp = false;
                // switch active buffers
            }

            // The tick's one lifecycle phase, in order: start, tear down, then notify enable
            // changes, so a callback fired here can create or destroy entities of its own.
            EntityRegistry.ProcessStarts();
            EntityRegistry.ProcessDestroys();
            EntityRegistry.ProcessEnableChanges();

            /*if (EntityRegistry.onDestroyEntities.Count > 0)
            {
                foreach (Entity entity in EntityRegistry.onDestroyEntities)
                {
                    entity.OnDestroy();
                }
                EntityRegistry.ClearOnDestroy();
            }*/

            EntityRegistry.ProcessTicks();

            /*if(EntityRegistry.entitiesToUpdate.Count > 0)
            {
                List<Entity> entitiesCopy;
                lock (EntityRegistry.entitiesToUpdate)
                {
                    entitiesCopy = new List<Entity>(EntityRegistry.entitiesToUpdate);
                    EntityRegistry.RemoveEntityUpdate(0, EntityRegistry.entitiesToUpdate.Count);
                }
                foreach (Entity e in entitiesCopy)
                {
                    //e.Invalidate();
                }
                renderer.UpdateModules();
            }*/

            // some if clause to check if we caught up
            isCaughtUp = true;
        }

        public void Stop()
        {
            FrameScheduler.Stop();
            mainSystem?.Stop();
            physicsSystem?.Stop();
            renderSystem?.Stop();
            animationSystem?.Stop();
        }
    }
}