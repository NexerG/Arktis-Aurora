using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Document;
using ArctisAurora.Core.UI.Text;

namespace ArctisAurora.Core.UI.Notes
{
    // A locked sealed run in a note: one [ENCRYPTED] button that asks for its password.
    public class SealedControl : ButtonControl
    {
        public readonly NoteSealed note;

        public SealedControl(NoteSealed note)
        {
            this.note = note;
            preferredHeight = 28;
            horizontalAlignment = HorizontalAlignment.Left;
            padding = new Thickness(12f, 0f);
            role = PaletteRole.Chrome;
            cornerRole = CornerRole.Control;
            AddChild(new LabelControl { text = "[ENCRYPTED]", fontSize = 14, role = PaletteRole.MutedInk });
            RegisterOnRelease(_ =>
            {
                for (Control? c = parent as Control; c != null; c = c.parent as Control)
                    if (c is DocumentEditorControl editor)
                    {
                        editor.UnlockSeal(note, null);
                        break;
                    }
                return true;
            });
        }
    }
}
