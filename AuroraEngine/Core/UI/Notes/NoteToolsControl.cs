using ArctisAurora.Core.UI.Containers.Tabs;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Document;
using ArctisAurora.Core.UI.Text;

namespace ArctisAurora.Core.UI.Notes
{
    // A note's tab-row tools: bold, italic, underline, left alignment, bullets and Paste link. Each acts on
    // its own note, which takes the caret first if another note had it.
    internal sealed class NoteToolsControl : TabToolsControl
    {
        private readonly DocumentEditorControl editor;
        private readonly IconControl bold;
        private readonly IconControl italic;
        private readonly IconControl underline;
        private readonly IconControl left;
        private readonly IconControl bullets;

        // what the glyphs were last lit as
        private bool? shownBold;
        private bool? shownItalic;
        private bool? shownUnderline;
        private bool? shownLeft;
        private bool? shownBullets;

        public NoteToolsControl(DocumentEditorControl editor)
        {
            this.editor = editor;
            SetTicking(true);

            bold = Icon("bold", _ => On(TextInputActions.Bold));
            italic = Icon("italic", _ => On(TextInputActions.Italic));
            underline = Icon("underline", _ => On(TextInputActions.Underline));
            Separator();
            left = Icon("align-left", _ => On(TextInputActions.AlignLeft));
            bullets = Icon("bullet-disc", _ => On(TextInputActions.Bullets));
            Separator();
            Text("Paste link", _ => On(TextInputActions.PasteLink));
        }

        private void On(Action action)
        {
            if (!ReferenceEquals(TextInputActions.Editor(), editor)) editor.FocusCaret();
            action();
        }

        public override void OnTick()
        {
            base.OnTick();

            CaretStyle? source = editor.StyleSource;
            Light(bold, source?.bold == true, ref shownBold);
            Light(italic, source?.italic == true, ref shownItalic);
            Light(underline, source?.underline == true, ref shownUnderline);
            Light(left, editor.CanAlign && editor.CaretBlockAlignment == TextAlignment.Left, ref shownLeft);
            Light(bullets, editor.CaretBlock?.listKind == ListKind.Bullet, ref shownBullets);
        }
    }
}
