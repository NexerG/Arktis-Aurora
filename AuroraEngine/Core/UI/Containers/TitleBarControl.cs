using ArctisAurora.Core.Registry;
using ArctisAurora.EngineWork;
using ArctisAurora.EngineWork.Rendering;

namespace ArctisAurora.Core.UI.Containers
{
    // A stack panel that moves the window while it is dragged. The window is created undecorated,
    // so this is the only thing that can move it. The move itself is Windows', not ours — snap,
    // drag-to-restore and the snap preview come with it and nothing here implements them.
    [A_XSDType("TitleBar", "UI")]
    public unsafe class TitleBarControl : StackPanelControl
    {
        public TitleBarControl()
        {
            role = PaletteRole.Chrome;
        }

        public override bool OnPointerPress(PointerEvent e)
        {
            RenderWindow window = UIEngine.WindowOf(this);
            if (e.button != PointerEvent.leftButton || window == null)
                return base.OnPointerPress(e);

            if (InputHandler.instance.keyTracker.GetState(EngineWork.Keys.MouseLeft)?.tapCount == 2)
            {
                WindowActions.MaximizeRestore(window);
                base.OnPointerPress(e);
                return true;
            }

            window.os.DragByCaption(Pump);

            // The OS loop consumes the button-up that ends it, so GLFW never reports one and the
            // key tracker would hold the button down forever.
            InputHandler.Post(new RawInputEvent { kind = InputEventKind.Button, key = EngineWork.Keys.MouseLeft, action = RawAction.Up, window = window, timestamp = Engine.totalTime });

            // The tick this ran on lasted as long as the drag did, and key repeat, the tap window
            // and the caret blink all count seconds off the tick that follows it.
            Engine.mainSystem.ResyncClock();

            base.OnPointerPress(e);
            return true;
        }

        // Everything main owes the OS loop: a snap resizes the window, whose GLFW callback fits the
        // root, and the arrange writes straight into the pool the render thread is already reading.
        private static void Pump()
        {
            UIEngine.ResolveLayout();
            UIEngine.BuildDrawLists();
        }
    }
}
