using ArctisAurora.Core.Animation;
using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI.Containers;
using System.Numerics;

namespace ArctisAurora.Core.UI.Controls
{
    [A_XSDType("Button", "UI")]
    public class ButtonControl : PanelControl
    {
        // state tints; an unset one falls back to the state below it
        [A_XSDElementProperty("HoverColorHex", "UI", "Control color while the pointer is over the button.")]
        public string? hoverColorHex;
        [A_XSDElementProperty("PressColorHex", "UI", "Control color while the button is held.")]
        public string? pressColorHex;

        private string? restColorHex;
        private uint restPaint = Palettes.Inline("#8C8C8C");
        private bool hovered;
        private bool pressed;

        // the interaction signal and the spring on state that follows it, with the feel it was made with
        private SignalHandle _stateSignal = SignalHandle.None;
        private AnimationHandle _stateSpring = AnimationHandle.None;
        private float _springFrequency;
        private float _springDamping;

        private const float disabledAlpha = 0.4f;

        // Disabled swallows the pointer and dims the content.
        [A_XSDElementProperty("Enabled", "UI", "Whether the button responds to the pointer. A disabled button dims its content.")]
        public bool enabled
        {
            get => field;
            set
            {
                field = value;
                if (!value && (hovered || pressed))
                {
                    hovered = false;
                    pressed = false;
                    Signal();
                }
                foreach (Entity e in children)
                    if (e is Control child) child.alpha = value ? 1f : disabledAlpha;
            }
        } = true;

        // The authored colour, not the shown one — the state picks which of the three is painted.
        public override string colorHex
        {
            get => restColorHex ?? base.colorHex;
            set
            {
                base.colorHex = value;
                restColorHex = value;
                restPaint = Palettes.Inline(value);
            }
        }

        // 0 rest, 1 hover, 2 press, eased between by the state spring.
        [A_Animatable(typeof(VulkanControl), nameof(VulkanControl.state))]
        public float state
        {
            get => visual.state;
            set => visual.state = value;
        }

        public ButtonControl()
        {
            SetPaint(restPaint);
        }

        protected override void ApplyRole(PaletteDefinition scheme, uint ground)
        {
            if (role == PaletteRole.None) return;
            restPaint = RolePaint(scheme, ground);
            if (_stateSpring != AnimationHandle.None
                && (_springFrequency != scheme.stateFrequency || _springDamping != scheme.stateDamping))
            {
                Animations.Stop(_stateSpring);
                _stateSpring = AnimationHandle.None;
                EnsureSpring();
            }
            SetPaint(restPaint);
        }

        public override void AddChild(Entity entity)
        {
            base.AddChild(entity);
            if (!enabled && entity is Control child) child.alpha = disabledAlpha;
        }

        public override bool OnPointerEnter(PointerEvent e)
        {
            if (!enabled) return true;
            hovered = true;
            Signal();
            base.OnPointerEnter(e);
            return true;
        }

        public override bool OnPointerExit(PointerEvent e)
        {
            hovered = false;
            pressed = false;
            Signal();
            base.OnPointerExit(e);
            return true;
        }

        public override bool OnPointerPress(PointerEvent e)
        {
            if (!enabled) return true;
            pressed = true;
            Signal();
            base.OnPointerPress(e);
            return true;
        }

        public override bool OnPointerRelease(PointerEvent e)
        {
            if (!enabled) return true;
            pressed = false;
            Signal();
            if (e.button == PointerEvent.leftButton) base.OnPointerRelease(e);
            return true;
        }

        public override void OnDestroy()
        {
            Animations.Stop(_stateSpring);
            Signals.Release(_stateSignal);
            base.OnDestroy();
        }

        // Points the state signal at the current interaction.
        private void Signal()
        {
            EnsureSpring();
            Signals.Set(_stateSignal, new Vector4(pressed ? 2f : hovered ? 1f : 0f, 0f, 0f, 0f));
        }

        private void EnsureSpring()
        {
            if (_stateSignal == SignalHandle.None) _stateSignal = Signals.Create();
            if (Animations.IsLive(_stateSpring)) return;

            PaletteDefinition feel = palette ?? Palettes.Default;
            _springFrequency = feel.stateFrequency;
            _springDamping = feel.stateDamping;
            _stateSpring = Animations.Spring(this, nameof(state), _springFrequency, _springDamping, _stateSignal);
        }

        // Paints the current state. A palette surface blends on the GPU; anything else is mixed here.
        internal override void PaintRow(ref VulkanControl row)
        {
            bool fromPalette = restColorHex == null && palette != null && role != PaletteRole.None;
            float s = Math.Clamp(row.state, 0f, 2f);

            if (s == 0f || (fromPalette && hoverColorHex == null && pressColorHex == null && Palettes.IsSurfaceRest(restPaint)))
            {
                row.paint = restPaint;
                row.state = s;
            }
            else
            {
                Vector3 rest = Palettes.ColorOf(restPaint);
                Vector3 hover = hoverColorHex != null ? Control.HexToRGB(hoverColorHex)
                              : fromPalette ? Palettes.ColorOf(Palettes.Step(palette!, restPaint, 1)) : rest;
                string? pressHex = pressColorHex ?? hoverColorHex;
                Vector3 press = pressHex != null ? Control.HexToRGB(pressHex)
                              : fromPalette ? Palettes.ColorOf(Palettes.Step(palette!, restPaint, 2)) : rest;

                row.paint = Palettes.Inline(s <= 1f ? Vector3.Lerp(rest, hover, s) : Vector3.Lerp(hover, press, s - 1f));
                row.state = 0f;
            }

            if (fromPalette && role == PaletteRole.Clear) row.alpha *= Math.Min(s, 1f);
        }
    }
}
