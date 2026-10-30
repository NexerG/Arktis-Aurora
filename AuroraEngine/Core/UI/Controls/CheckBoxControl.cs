using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI.Containers;

namespace ArctisAurora.Core.UI.Controls
{
    // A box that fills when set.
    [A_XSDType("CheckBox", "UI")]
    public class CheckBoxControl : ButtonControl
    {
        private readonly PanelControl mark = new PanelControl
        {
            role = PaletteRole.Ink,
            hitTestable = false
        };

        public Action<bool>? onChanged;

        private bool _isChecked;

        public bool isChecked
        {
            get => _isChecked;
            set
            {
                _isChecked = value;
                if (value) mark.Show(); else mark.Hide();
            }
        }

        public CheckBoxControl()
        {
            SetScale(1f);

            AddChild(mark);
            mark.Hide();
        }

        // Sizes the box and its mark; 1 is the default size.
        public void SetScale(float scale)
        {
            preferredWidth = 18 * scale;
            preferredHeight = 18 * scale;
            cornerRadius = new CornerRadii(3 * scale);

            mark.preferredWidth = 10 * scale;
            mark.preferredHeight = 10 * scale;
            mark.cornerRadius = new CornerRadii(2 * scale);
        }

        public override bool OnPointerRelease(PointerEvent e)
        {
            base.OnPointerRelease(e);
            if (e.button != PointerEvent.leftButton) return true;

            isChecked = !isChecked;
            onChanged?.Invoke(isChecked);
            return true;
        }
    }
}
