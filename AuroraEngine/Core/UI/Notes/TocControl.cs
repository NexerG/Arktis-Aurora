using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Document;
using ArctisAurora.Core.UI.Text;

namespace ArctisAurora.Core.UI.Notes
{
    // A note's live table of contents: a row per heading down to its depth, a press shows the heading.
    public sealed class TocControl : StackPanelControl
    {
        // row sizing
        private const float rowHeight = 24f;
        private const float levelIndent = 16f;
        private const int captionSize = 14;

        public readonly NoteToc note;

        // the headings the rows show
        private string[] texts = Array.Empty<string>();
        private int[] levels = Array.Empty<int>();

        public TocControl(NoteToc note)
        {
            this.note = note;
            orientation = Orientation.Vertical;
            alpha = 0f;
            horizontalAlignment = HorizontalAlignment.Stretch;
            contextMenu = "toc";
            stopsContextMenu = true;
        }

        // Rebuilds the rows when the headings differ from those shown; false when nothing changed.
        public bool Rebuild(List<NoteBlock> blocks)
        {
            int count = 0;
            for (int i = 0; i < blocks.Count; i++)
                if (Level(blocks[i]) > 0) count++;

            if (count == texts.Length && Matches(blocks)) return false;

            texts = new string[count];
            levels = new int[count];
            int at = 0;
            for (int i = 0; i < blocks.Count; i++)
            {
                int level = Level(blocks[i]);
                if (level == 0) continue;
                texts[at] = (blocks[i].run.text ?? string.Empty).Trim();
                levels[at++] = level;
            }

            for (int i = children.Count - 1; i >= 0; i--)
                children[i].Destroy();
            for (int i = 0; i < texts.Length; i++)
                AddChild(Row(texts[i], levels[i]));
            return true;
        }

        // The heading texts the rows show, in order.
        public string[] Entries => texts;

        private bool Matches(List<NoteBlock> blocks)
        {
            int at = 0;
            for (int i = 0; i < blocks.Count; i++)
            {
                int level = Level(blocks[i]);
                if (level == 0) continue;
                if (levels[at] != level || texts[at] != (blocks[i].run.text ?? string.Empty).Trim()) return false;
                at++;
            }
            return true;
        }

        // A heading's level within the depth; 0 for anything else.
        private int Level(NoteBlock block)
        {
            if (!NoteHeadings.IsHeading(block.stylingType)) return 0;
            int level = block.stylingType - TextStyleType.Heading1 + 1;
            return level <= note.depth ? level : 0;
        }

        private ButtonControl Row(string text, int level)
        {
            ButtonControl row = new ButtonControl
            {
                preferredHeight = rowHeight,
                horizontalAlignment = HorizontalAlignment.Left,
                margin = new Thickness((level - 1) * levelIndent, 0f, 0f, 0f),
                padding = new Thickness(6f, 0f),
                role = PaletteRole.Surface,
                cornerRole = CornerRole.Control
            };
            row.AddChild(new LabelControl { text = text, fontSize = captionSize, preferredHeight = rowHeight, horizontalPosition = 0f });
            row.RegisterOnRelease(_ =>
            {
                Show(text);
                return true;
            });
            return row;
        }

        // Puts the editor's caret on the heading.
        public bool Show(string heading)
        {
            for (Control? c = parent as Control; c != null; c = c.parent as Control)
                if (c is DocumentEditorControl editor) return editor.ShowHeading(heading);
            return false;
        }
    }
}
