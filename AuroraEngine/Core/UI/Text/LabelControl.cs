using ArctisAurora.Core.Registry;

namespace ArctisAurora.Core.UI.Text
{
    // Text that is drawn and never edited — a button's caption, a row in a list.
    [A_XSDType("Label", "UI")]
    public class LabelControl : TextRunControl
    {
        protected override bool Wraps => false;

        public override Control? ActiveContextTarget() => (parent as Control)?.ActiveContextTarget();
    }
}
