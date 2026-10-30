using ArctisAurora.Core.Threading;
using ArctisAurora.EngineWork;

namespace ArctisAurora.Core.UI.Controls
{
    // The insertion point — a narrow filled quad, positioned by whoever owns the text.
    public class CaretControl : Control
    {
        public const float Width = 2f;

        private const double blinkInterval = 0.53;

        // blink state
        private double phase;
        private bool focused = true;

        public CaretControl()
        {
            role = PaletteRole.Ink;
            SetTicking(true);
        }

        public override void OnTick()
        {
            base.OnTick();
            if (!focused) return;

            phase += Engine.deltaTime.TotalSeconds;
            SetAlpha(phase % (blinkInterval * 2.0) < blinkInterval ? 1f : 0f);
            FrameScheduler.RequestFrameAt(Engine.totalTime + blinkInterval - phase % blinkInterval);
        }

        // Shows the caret solid, from the top of the cycle.
        internal void Focus()
        {
            focused = true;
            phase = 0.0;
            SetAlpha(1f);
            SetTicking(true);
        }

        internal void Blur()
        {
            focused = false;
            SetAlpha(0f);
            SetTicking(false);
        }

        private void SetAlpha(float value)
        {
            if (alpha != value) alpha = value;
        }
    }
}
