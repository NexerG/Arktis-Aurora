using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Document;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.EngineWork;
using System.Globalization;

namespace ArctisAurora.Core.UI.Notes
{
    // A note's properties as rows: dates, palette, layout, and a Markdown note's other frontmatter keys.
    public class NotePropertiesControl : StackPanelControl
    {
        // metrics
        private const int captionSize = 13;
        private const float keyWidth = 110f;
        private const float paletteWidth = 180f;
        private const float rowHeight = 22f;
        private const float iconSize = 12f;

        private const string appPalette = "App default";

        private readonly DocumentEditorControl editor;
        private readonly bool markdown;

        public override bool takesActiveControl => false;

        public NotePropertiesControl(DocumentEditorControl editor, bool markdown)
        {
            this.editor = editor;
            this.markdown = markdown;
            orientation = Orientation.Vertical;
            Spacing = 4f;
            padding = new Thickness(0f, 8f);
            horizontalAlignment = HorizontalAlignment.Stretch;
            alpha = 0f;
            Refresh();
        }

        // Rebuilds every row from the note.
        public void Refresh()
        {
            foreach (Entity child in children.ToList()) child.Destroy();

            RichTextDocument document = editor.activeDocument;
            Row("Created", Caption(Date(document.created)));
            Row("Modified", Caption(Date(document.modified)));

            DropdownControl palette = new DropdownControl
            {
                options = new[] { appPalette }.Concat(Palettes.Names).ToArray(),
                selected = document.palette ?? appPalette,
                preferredWidth = paletteWidth,
                preferredHeight = rowHeight,
                role = PaletteRole.Field
            };
            palette.onPicked = picked => editor.SetPalette(picked == appPalette ? null : picked);
            Row("Palette", palette);

            DocumentLayout layout = document.layout;
            Row("Line height", Number(layout.lineHeight, 0.5f, (l, v) => l.lineHeight = v));
            Row("Block spacing", Number(layout.blockSpacing, 0f, (l, v) => l.blockSpacing = v));
            Row("List indent", Number(layout.listIndent, 0f, (l, v) => l.listIndent = v));
            Row("List markers", Field(string.Join(", ", layout.listLevels.Select(l => l.marker)), SetListMarkers));

            CheckBoxControl locked = new CheckBoxControl { isChecked = document.readOnly };
            locked.onChanged = editor.SetReadOnly;
            Row("Read only", locked);

            if (!markdown) return;

            foreach (Frontmatter.Entry entry in Frontmatter.Entries(document.frontmatter))
            {
                if (MarkdownFormat.IsProperty(entry.key)) continue;

                Control value = UserValue(entry);
                Row(entry.key, entry.key.StartsWith('[') ? value : Cell(value, Remove(entry.key)));
            }

            Row("Add property", Field(string.Empty, AddProperty));
        }

        // How the host opens a [[note]] or web link; null shows links as plain values.
        public static Func<string?, string, bool>? openLink;

        public override bool OnPointerPress(PointerEvent e) => true;

        public override bool OnPointerTap(PointerEvent e) => true;

        #region ---- parts ----
        private void Row(string key, Control value)
        {
            PropertyRow row = new PropertyRow
            {
                orientation = Orientation.Horizontal,
                Spacing = 8f,
                preferredHeight = rowHeight,
                horizontalAlignment = HorizontalAlignment.Stretch,
                alpha = 0f
            };

            LabelControl caption = Caption(key);
            caption.preferredWidth = keyWidth;
            caption.horizontalPosition = 0f;
            caption.role = PaletteRole.MutedInk;
            row.AddChild(caption);

            if (value.preferredWidth <= 0f) value.widthStar = 1f;
            value.horizontalPosition = 0f;
            row.AddChild(value);
            AddChild(row);
        }

        private static LabelControl Caption(string text) => new LabelControl
        {
            fontSize = captionSize,
            role = PaletteRole.Ink,
            hitTestable = false,
            text = text
        };

        // A field that applies on Enter or when the focus leaves it; a value apply refuses is put back.
        private TextBoxControl Field(string text, Func<string, bool> apply)
        {
            TextBoxControl box = new TextBoxControl
            {
                text = text,
                fontSize = captionSize,
                preferredHeight = rowHeight,
                role = PaletteRole.Field
            };

            string shown = text;
            void Apply(string value)
            {
                if (value == shown) return;
                if (apply(value)) shown = value;
                else box.text = shown;
            }

            box.onCommit = value =>
            {
                Apply(value);
                editor.FocusCaret();
            };
            box.onCancel = editor.FocusCaret;
            box.onBlur = () => Apply(box.text);
            return box;
        }

        private TextBoxControl Number(float current, float min, Action<DocumentLayout, float> change) =>
            Field(current.ToString(CultureInfo.InvariantCulture), value =>
            {
                if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) || parsed < min)
                    return false;

                DocumentLayout layout = editor.activeDocument.layout.Clone();
                change(layout, parsed);
                editor.SetLayout(layout);
                return true;
            });

        // A user key's value: a box for true/false, a field with Open for a link, a field otherwise.
        private Control UserValue(Frontmatter.Entry entry)
        {
            string key = entry.key;
            if (!entry.editable) return Caption(entry.value);

            if (!entry.list && entry.value is "true" or "false")
            {
                CheckBoxControl box = new CheckBoxControl { isChecked = entry.value == "true" };
                box.onChanged = value => editor.SetFrontmatterValue(key, value ? "true" : "false");
                return box;
            }

            TextBoxControl field = Field(entry.value, value => { editor.SetFrontmatterValue(key, value); return true; });
            string link = entry.value.Trim();
            bool isLink = (link.StartsWith("[[") && link.EndsWith("]]") && link.IndexOf("]]") == link.Length - 2)
                          || link.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                          || link.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            if (!isLink || openLink == null) return field;

            return Cell(field, Button(Caption("Open"), 48f, () => openLink(editor.path, link)));
        }

        private bool AddProperty(string key)
        {
            key = key.Trim();
            if (key.Length == 0 || key.IndexOfAny(new[] { ':', '=', '#', '[' }) >= 0 || MarkdownFormat.IsProperty(key)) return false;
            if (Frontmatter.Entries(editor.activeDocument.frontmatter).Any(e => string.Equals(e.key, key, StringComparison.OrdinalIgnoreCase)))
                return false;

            editor.SetFrontmatterValue(key, string.Empty);
            Engine.Post(Refresh);
            return true;
        }

        private ButtonControl Remove(string key) => Button(new IconControl
        {
            setName = "default",
            iconName = "close",
            preferredWidth = iconSize,
            preferredHeight = iconSize,
            hitTestable = false,
            horizontalPosition = 0.5f,
            verticalPosition = 0.5f
        }, rowHeight, () =>
        {
            editor.SetFrontmatterValue(key, null);
            Engine.Post(Refresh);
        });

        private bool SetListMarkers(string value)
        {
            List<ListLevel> levels = new List<ListLevel>();
            foreach (string name in value.Split(',').Select(n => n.Trim()).Where(n => n.Length > 0))
            {
                if (!Enum.TryParse(name, true, out ListMarker marker) || !Enum.IsDefined(marker)) return false;
                levels.Add(new ListLevel { marker = marker });
            }

            DocumentLayout layout = editor.activeDocument.layout.Clone();
            layout.listLevels = levels;
            editor.SetLayout(layout);
            return true;
        }

        private static ButtonControl Button(Control face, float width, Action pressed)
        {
            ButtonControl button = new ButtonControl
            {
                preferredWidth = width,
                preferredHeight = rowHeight,
                role = PaletteRole.Chrome,
                cornerRole = CornerRole.Control
            };
            button.AddChild(face);
            button.RegisterOnRelease(_ => { pressed(); return true; });
            return button;
        }

        // Several controls side by side in one row's value slot.
        private static Control Cell(params Control[] parts)
        {
            PropertyRow cell = new PropertyRow
            {
                orientation = Orientation.Horizontal,
                Spacing = 4f,
                preferredHeight = rowHeight,
                horizontalAlignment = HorizontalAlignment.Stretch,
                alpha = 0f
            };

            foreach (Control part in parts)
            {
                if (part.preferredWidth <= 0f) part.widthStar = 1f;
                part.horizontalPosition = 0f;
                cell.AddChild(part);
            }
            return cell;
        }

        private static string Date(string? stamp) =>
            DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset time)
                ? $"{DateFormats.Date(time.LocalDateTime)} {DateFormats.Time(time.LocalDateTime)}"
                : stamp ?? "-";

        // A press on a row's gap leaves the caret's note active.
        private class PropertyRow : StackPanelControl
        {
            public override bool takesActiveControl => false;
        }
        #endregion
    }
}
