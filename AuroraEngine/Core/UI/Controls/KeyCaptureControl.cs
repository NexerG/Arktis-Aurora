using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.EngineWork;

namespace ArctisAurora.Core.UI.Controls
{
    // Shows a bind's combo, and on release hands the next key press to whoever is listening instead
    // of to the keybinds.
    [A_XSDType("KeyCapture", "UI")]
    public class KeyCaptureControl : ButtonControl
    {
        // caption
        private const int captionSize = 13;
        private const string listeningCaption = "press a key...";

        private readonly LabelControl label = new LabelControl
        {
            fontSize = captionSize
        };

        public Action<Keys, List<Keys>>? onCaptured;

        public KeyCaptureControl()
        {
            clipOutOfBounds = true;
            AddChild(label);
        }

        public void SetCombo(Keys trigger, IEnumerable<Keys> modifiers) =>
            label.text = Describe(trigger, modifiers);

        public static string Describe(Keys trigger, IEnumerable<Keys> modifiers)
        {
            string mods = string.Join(" + ", modifiers);
            return mods.Length == 0 ? trigger.ToString() : $"{mods} + {trigger}";
        }

        public override void OnDestroy()
        {
            if (InputHandler.isCapturing) InputHandler.CancelCapture();
            base.OnDestroy();
        }

        public override bool OnPointerRelease(PointerEvent e)
        {
            base.OnPointerRelease(e);
            if (e.button != PointerEvent.leftButton || InputHandler.isCapturing) return true;

            label.text = listeningCaption;
            InputHandler.Capture((trigger, modifiers) =>
            {
                SetCombo(trigger, modifiers);
                onCaptured?.Invoke(trigger, modifiers);
            });
            return true;
        }
    }
}
