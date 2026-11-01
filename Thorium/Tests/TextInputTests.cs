using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Testing;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Containers.Tabs;
using ArctisAurora.Core.UI.Containers.Workspace;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Document;
using ArctisAurora.Core.UI.Menus;
using ArctisAurora.Core.UI.Notes;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.EngineWork;
using System.Numerics;
using System.Runtime.InteropServices;
using Image = Arctis.Imaging.Image;
using System.Xml.Linq;

namespace Thorium.Tests
{
    internal static class TextInputTests
    {
        [A_XSDActionDependency("TextInput.TypeWritesText", "Test")]
        private static IEnumerator<int> TypeWritesText(TestContext t)
        {
            TextBoxControl box = new TextBoxControl
            {
                preferredWidth = 240f,
                preferredHeight = 28f,
                horizontalAlignment = HorizontalAlignment.Left,
                verticalAlignment = VerticalAlignment.Top
            };
            t.Show(box);
            yield return 2;

            yield return t.Click(box);
            t.Check(ReferenceEquals(UIEngine.activeControl, box), "a click focuses the text box");

            yield return t.Type("Hi 5!");
            t.Check(box.text == "Hi 5!", "typing writes letters, digits, a space and a symbol");
        }

        #region ---- basic editing ----
        [A_XSDActionDependency("TextInput.WordMoves", "Test")]
        private static IEnumerator<int> WordMoves(TestContext t)
        {
            DocumentEditorControl editor = ShowParagraphs(t, "one two, three", "four");
            DocumentControl content = Content(editor);
            List<BlockControl> p = Paragraphs(editor);
            yield return 2;

            yield return t.Key(Keys.Right, Keys.LeftControl);
            t.Check(content.caretOffset == 4, "Ctrl+Right lands at the start of the next word");
            yield return t.Key(Keys.Right, Keys.LeftControl);
            t.Check(content.caretOffset == 7, "a word stops where punctuation starts");
            yield return t.Key(Keys.Right, Keys.LeftControl);
            t.Check(content.caretOffset == 9, "punctuation and the space after it are one step");

            yield return t.Key(Keys.Left, Keys.LeftControl, Keys.LeftShift);
            t.Check(content.caretOffset == 7 && content.HasSelection, "Ctrl+Shift+Left extends by a word");

            yield return t.Key(Keys.End, Keys.LeftControl);
            t.Check(content.caretBlock == p[1] && content.caretOffset == 4, "Ctrl+End goes to the end of the note");
            yield return t.Key(Keys.Home, Keys.LeftControl);
            t.Check(content.caretBlock == p[0] && content.caretOffset == 0, "Ctrl+Home goes to the start of the note");

            content.SetCaret(p[1], 0);
            yield return t.Key(Keys.Left, Keys.LeftControl);
            t.Check(content.caretBlock == p[0] && content.caretOffset == 14, "Ctrl+Left at a block's start crosses into the previous one");

            yield return t.Key(Keys.Backspace, Keys.LeftControl);
            t.Check(p[0].text == "one two, ", "Ctrl+Backspace deletes the word before the caret");

            content.SetCaret(p[0], 0);
            yield return t.Key(Keys.Delete, Keys.LeftControl);
            t.Check(p[0].text == "two, ", "Ctrl+Delete deletes the word and the space after it");

            yield return t.Key(Keys.Z, Keys.LeftControl);
            t.Check(p[0].text == "one two, " && content.caretOffset == 0 && !content.HasSelection,
                "undoing a word delete puts the caret back, not a selection");
            yield return t.Key(Keys.Z, Keys.LeftControl);
            t.Check(p[0].text == "one two, three" && content.caretOffset == 14, "undoing Ctrl+Backspace restores the word");
        }

        [A_XSDActionDependency("TextInput.UndoReselects", "Test")]
        private static IEnumerator<int> UndoReselects(TestContext t)
        {
            DocumentEditorControl editor = ShowParagraphs(t, "hello world");
            DocumentControl content = Content(editor);
            BlockControl p = Paragraphs(editor)[0];
            yield return 2;

            content.SetCaret(p, 6);
            content.SetCaret(p, 11, true);
            yield return t.Key(Keys.X, Keys.LeftControl);
            t.Check(p.text == "hello " && ClipboardText.Get() == "world", "Ctrl+X removes the selection onto the clipboard");

            yield return t.Key(Keys.Z, Keys.LeftControl);
            t.Check(p.text == "hello world", "undo brings the cut text back");
            t.Check(editor.SelectedRange(out DocumentAddress from, out DocumentAddress to) && from.offset == 6 && to.offset == 11
                && content.caretOffset == 11, "and selects it again, caret at the end it was on");

            content.SetCaret(p, 11);
            content.SetCaret(p, 6, true);
            yield return t.Key(Keys.Delete);
            yield return t.Key(Keys.Z, Keys.LeftControl);
            t.Check(content.HasSelection && content.caretOffset == 6, "a backwards selection comes back backwards");

            content.SetCaret(p, 3);
            yield return t.Key(Keys.Backspace);
            yield return t.Key(Keys.Z, Keys.LeftControl);
            t.Check(p.text == "hello world" && content.caretOffset == 3 && !content.HasSelection,
                "undoing Backspace leaves the caret after the restored character");

            yield return t.Key(Keys.Delete);
            yield return t.Key(Keys.Z, Keys.LeftControl);
            t.Check(content.caretOffset == 3 && !content.HasSelection, "undoing Delete leaves the caret before it");
        }

        [A_XSDActionDependency("TextInput.PasteRoundTrip", "Test")]
        private static IEnumerator<int> PasteRoundTrip(TestContext t)
        {
            RichTextDocument fixture = DocumentXml.Parse(new XElement("Document",
                new XElement("Block", new XElement("Run", new XAttribute("Text", "bold"), new XAttribute("Bold", "true"))),
                Block("plain")));
            DocumentEditorControl editor = ShowFixture(t, fixture);
            DocumentControl content = Content(editor);
            List<BlockControl> p = Paragraphs(editor);
            yield return 2;

            string before = Unstamped(editor.session.document).ToString();
            t.Check(p[0].StyleAt(2).IsBold, "the fixture's first paragraph is bold");

            content.SetCaret(p[0], 0);
            content.SetCaret(p[1], 5, true);
            yield return t.Key(Keys.C, Keys.LeftControl);
            t.Check(ClipboardText.Get() == "bold" + Environment.NewLine + "plain", "Ctrl+C puts the selection on the clipboard as lines");

            content.SetCaret(p[1], 5);
            yield return t.Key(Keys.V, Keys.LeftControl);
            p = Paragraphs(editor);
            t.Check(p.Count == 3 && p[1].text == "plainbold" && p[2].text == "plain", "a two-paragraph paste splits the block it lands in");
            t.Check(p.Count == 3 && p[1].StyleAt(8).IsBold && !p[1].StyleAt(4).IsBold, "our own copy pastes with its formatting");
            t.Check(content.caretBlock == p[^1] && content.caretOffset == 5, "the caret ends after the pasted text");

            string pasted = Unstamped(editor.session.document).ToString();
            yield return t.Key(Keys.Z, Keys.LeftControl);
            t.Check(Unstamped(editor.session.document).ToString() == before, "undo restores the note exactly");
            yield return t.Key(Keys.Y, Keys.LeftControl);
            t.Check(Unstamped(editor.session.document).ToString() == pasted, "redo pastes it exactly again");
        }

        [A_XSDActionDependency("TextInput.PastePlainLines", "Test")]
        private static IEnumerator<int> PastePlainLines(TestContext t)
        {
            DocumentEditorControl editor = ShowParagraphs(t, "x");
            DocumentControl content = Content(editor);
            content.SetCaret(Paragraphs(editor)[0], 1);
            yield return 2;

            ClipboardText.Set("a\r\nb\tc\nd");
            yield return t.Key(Keys.V, Keys.LeftControl);
            List<BlockControl> p = Paragraphs(editor);
            t.Check(p.Count == 3 && p[0].text == "xa" && p[1].text == "b c" && p[2].text == "d",
                "plain text pastes as paragraphs, a tab as a space");

            yield return t.Key(Keys.Z, Keys.LeftControl);
            p = Paragraphs(editor);
            t.Check(p.Count == 1 && p[0].text == "x", "one undo takes the whole paste back");
        }

        [A_XSDActionDependency("TextInput.FieldClipboardAndUndo", "Test")]
        private static IEnumerator<int> FieldClipboardAndUndo(TestContext t)
        {
            TextBoxControl box = new TextBoxControl
            {
                preferredWidth = 240f,
                preferredHeight = 28f,
                horizontalAlignment = HorizontalAlignment.Left,
                verticalAlignment = VerticalAlignment.Top
            };
            t.Show(box);
            yield return 2;

            yield return t.Click(box);
            yield return t.Type("hello world");
            yield return t.Key(Keys.A, Keys.LeftControl);
            yield return t.Key(Keys.C, Keys.LeftControl);
            t.Check(ClipboardText.Get() == "hello world", "Ctrl+C in a field copies its selection");

            yield return t.Key(Keys.End);
            ClipboardText.Set("a\nb");
            yield return t.Key(Keys.V, Keys.LeftControl);
            t.Check(box.text == "hello worlda b", "a field pastes line breaks as spaces");

            yield return t.Key(Keys.Z, Keys.LeftControl);
            t.Check(box.text == "hello world", "undo in a field takes the paste back");
            yield return t.Key(Keys.Y, Keys.LeftControl);
            t.Check(box.text == "hello worlda b", "redo in a field puts it back");

            yield return t.Key(Keys.Backspace, Keys.LeftControl);
            t.Check(box.text == "hello worlda ", "Ctrl+Backspace in a field deletes a word");
        }

        [A_XSDActionDependency("TextInput.FieldWordAndScroll", "Test")]
        private static IEnumerator<int> FieldWordAndScroll(TestContext t)
        {
            TextBoxControl box = new TextBoxControl
            {
                preferredWidth = 240f,
                preferredHeight = 28f,
                horizontalAlignment = HorizontalAlignment.Left,
                verticalAlignment = VerticalAlignment.Top,
                text = "one two"
            };
            t.Show(box);
            yield return 2;

            yield return t.Click(box);
            yield return t.Click(box);
            yield return t.Key(Keys.C, Keys.LeftControl);
            t.Check(ClipboardText.Get() == "two", $"a double click in a field selects the word: '{ClipboardText.Get()}'");

            yield return t.Click(box);
            yield return t.Key(Keys.C, Keys.LeftControl);
            t.Check(ClipboardText.Get() == "one two", $"a third click selects the whole field: '{ClipboardText.Get()}'");

            yield return t.Key(Keys.End);
            yield return t.Type(" three four five six seven eight nine ten eleven twelve");
            yield return 2;
            CaretControl caret = box.children.OfType<CaretControl>().First();
            t.Check(caret.arrangedRect.Right <= box.arrangedRect.Right + 0.5f && caret.arrangedRect.x > box.arrangedRect.x,
                $"typing past the edge keeps the caret inside the field: caret {caret.arrangedRect.x}, field {box.arrangedRect.x}..{box.arrangedRect.Right}");

            yield return t.Key(Keys.Home);
            yield return 2;
            t.Check(caret.arrangedRect.x >= box.arrangedRect.x && caret.arrangedRect.x < box.arrangedRect.x + 12f,
                $"Home slides the text back to its start: caret {caret.arrangedRect.x}, field {box.arrangedRect.x}");
        }

        [A_XSDActionDependency("RegexText.LinesAndCommit", "Test")]
        private static IEnumerator<int> RegexTextLinesAndCommit(TestContext t)
        {
            string? committed = null;
            RegexTextControl field = RegexField(240f);
            field.onCommit = text => committed = text;
            t.Show(field);
            yield return 2;

            yield return t.Click(field);
            float oneLine = field.arrangedRect.height;
            yield return t.Type("one");
            yield return t.Key(Keys.Enter, Keys.LeftShift);
            yield return t.Type("two");
            yield return 2;
            t.Check(field.text == "one\ntwo" && field.Segments.Count == 2, $"Shift+Enter starts a new segment: '{field.text}'");
            t.Check(field.arrangedRect.height > oneLine, $"the field grows with its lines: {oneLine} -> {field.arrangedRect.height}");

            CaretControl caret = field.children.OfType<CaretControl>().First();
            float lower = caret.arrangedRect.y;
            yield return t.Key(Keys.Up);
            yield return 2;
            t.Check(caret.arrangedRect.y < lower, $"Up moves the caret to the line above: {lower} -> {caret.arrangedRect.y}");
            yield return t.Type("!");
            t.Check(field.text == "one!\ntwo", $"the caret keeps its column on the line above: '{field.text}'");

            yield return t.Key(Keys.Enter);
            t.Check(committed == "one!\ntwo" && !field.isEditing, "Enter commits the field");
        }

        [A_XSDActionDependency("RegexText.WrapMoves", "Test")]
        private static IEnumerator<int> RegexTextWrapMoves(TestContext t)
        {
            RegexTextControl field = RegexField(120f);
            field.text = "alpha beta gamma delta epsilon\nzeta";
            t.Show(field);
            yield return 2;

            yield return t.Click(field);
            yield return t.Key(Keys.Home, Keys.LeftControl);
            yield return t.Key(Keys.Down);
            yield return t.Type("X");
            int x = field.text.IndexOf('X');
            t.Check(x > 0 && x < field.text.IndexOf('\n'), $"Down from the first line stays in a wrapped segment: '{field.text}'");

            yield return t.Key(Keys.End, Keys.LeftControl);
            yield return t.Key(Keys.Home);
            yield return t.Key(Keys.Backspace);
            t.Check(!field.text.Contains('\n') && field.Segments.Count == 1, $"Backspace at a segment's start joins it to the one before: '{field.text}'");
            yield return t.Key(Keys.Z, Keys.LeftControl);
            t.Check(field.text.Contains("\nzeta"), $"undo splits them again: '{field.text}'");

            yield return t.Key(Keys.A, Keys.LeftControl);
            yield return t.Key(Keys.C, Keys.LeftControl);
            t.Check(ClipboardText.Get() == field.text, "Ctrl+A selects across segments");
        }

        [A_XSDActionDependency("RegexText.CommaPattern", "Test")]
        private static IEnumerator<int> RegexTextCommaPattern(TestContext t)
        {
            RegexTextControl field = RegexField(240f);
            field.pattern = new System.Text.RegularExpressions.Regex(@",\s*");
            field.separator = ", ";
            t.Show(field);
            yield return 2;

            yield return t.Click(field);
            yield return t.Type("ada, bo");
            t.Check(field.Segments.Count == 2, $"a typed match splits the text live: {field.Segments.Count}");
            yield return t.Key(Keys.Enter, Keys.LeftShift);
            yield return t.Type("cy");
            t.Check(field.text == "ada, bo, cy" && field.Segments.Count == 3, $"Shift+Enter inserts the separator: '{field.text}'");

            yield return t.Key(Keys.Home);
            yield return t.Key(Keys.Backspace);
            t.Check(field.text == "ada, bocy", $"Backspace at a segment's start removes the whole match: '{field.text}'");
        }

        private static RegexTextControl RegexField(float width) => new RegexTextControl
        {
            preferredWidth = width,
            horizontalAlignment = HorizontalAlignment.Left,
            verticalAlignment = VerticalAlignment.Top
        };

        [A_XSDActionDependency("TextInput.DragMovesText", "Test")]
        private static IEnumerator<int> DragMovesText(TestContext t)
        {
            DocumentEditorControl editor = ShowParagraphs(t, "move me", "target");
            DocumentControl content = Content(editor);
            List<BlockControl> p = Paragraphs(editor);
            yield return 2;

            content.SetCaret(p[0], 0);
            content.SetCaret(p[0], 7, true);
            yield return t.Drag(p[0], EndOf(p[1]));
            yield return 2;
            t.Check(p[0].text == "" && p[1].text == "targetmove me", "dragging a selection moves it to the drop");
            t.Check(content.HasSelection && content.caretBlock == p[1] && content.caretOffset == 13, "the moved text stays selected");

            yield return t.Key(Keys.Z, Keys.LeftControl);
            t.Check(p[0].text == "move me" && p[1].text == "target", "one undo puts the move back");

            content.SetCaret(p[0], 0);
            content.SetCaret(p[0], 7, true);
            yield return t.Click(p[0]);
            yield return 2;
            t.Check(!content.HasSelection && p[0].text == "move me", "a click inside the selection only places the caret");
        }

        [A_XSDActionDependency("TextInput.DropOntoRule", "Test")]
        private static IEnumerator<int> DropOntoRule(TestContext t)
        {
            DocumentEditorControl editor = ShowFixture(t, DocumentXml.Parse(new XElement("Document",
                Block("move me"),
                new XElement("Block", new XAttribute("StylingType", "Rule"), RunX("")),
                Block("after"))));
            DocumentControl content = Content(editor);
            List<BlockControl> p = Paragraphs(editor);
            yield return 2;

            content.SetCaret(p[0], 0);
            content.SetCaret(p[0], 7, true);
            LayoutRect rule = p[1].arrangedRect;
            yield return t.Drag(p[0], new Vector2(rule.x + rule.width * 0.5f, rule.y + rule.height * 0.5f));
            yield return 2;

            p = Paragraphs(editor);
            t.Check(p.Count == 4 && p[0].text == "" && p[1].stylingType == TextStyleType.Rule && p[1].text == ""
                && p[2].text == "move me" && p[2].stylingType == TextStyleType.Text && p[3].text == "after",
                $"a drop onto a rule lands in a new paragraph after it: {string.Join("|", p.Select(b => $"{b.stylingType}:{b.text}"))}");

            yield return t.Key(Keys.Z, Keys.LeftControl);
            p = Paragraphs(editor);
            t.Check(p.Count == 3 && p[0].text == "move me" && p[1].stylingType == TextStyleType.Rule && p[2].text == "after",
                "one undo puts the drop back");

            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("TextInput.DragBetweenNotes", "Test")]
        private static IEnumerator<int> DragBetweenNotes(TestContext t)
        {
            DocumentEditorControl a = NewEditor();
            DocumentEditorControl b = NewEditor();
            StackPanelControl both = new StackPanelControl
            {
                orientation = StackPanelControl.Orientation.Vertical,
                horizontalAlignment = HorizontalAlignment.Stretch,
                verticalAlignment = VerticalAlignment.Stretch
            };
            a.preferredHeight = b.preferredHeight = 300f;
            both.AddChild(a);
            both.AddChild(b);
            t.Show(both);
            Load(a, Paragraphs("from"));
            Load(b, Paragraphs("to"));
            yield return 2;

            BlockControl source = Paragraphs(a)[0];
            BlockControl target = Paragraphs(b)[0];
            Content(a).SetCaret(source, 0);
            Content(a).SetCaret(source, 4, true);
            a.FocusCaret();
            yield return t.Drag(source, EndOf(target));
            yield return 2;
            t.Check(target.text == "tofrom" && source.text == "", "a drop into another note moves the text there");
            t.Check(ReferenceEquals(UIEngine.activeControl, b), "the note that took the drop takes the focus");

            yield return t.Key(Keys.Z, Keys.LeftControl);
            t.Check(target.text == "to" && source.text == "", "undo in the target takes back only its own half");
        }

        [A_XSDActionDependency("TextInput.WheelZoom", "Test")]
        private static IEnumerator<int> WheelZoom(TestContext t)
        {
            DocumentEditorControl editor = ShowParagraphs(t, "zoom me");
            UISettings ui = SettingsRegistry.Get<UISettings>();
            float note = ui.documentZoom.percent, app = ui.zoom.percent;
            yield return 2;

            yield return t.Scroll(editor, new Vector2(0f, 1f), new[] { Keys.LeftControl });
            yield return 2;
            t.Check(ui.documentZoom.percent == note + 10f && ui.zoom.percent == app,
                $"Ctrl+wheel over a note zooms the note: {note} -> {ui.documentZoom.percent}, app {app} -> {ui.zoom.percent}");

            yield return t.Scroll(editor, new Vector2(0f, 1f), new[] { Keys.LeftControl, Keys.LeftShift });
            yield return 2;
            t.Check(ui.zoom.percent == app + 10f && ui.documentZoom.percent == note + 10f,
                $"Ctrl+Shift+wheel zooms the app, not the note: app {app} -> {ui.zoom.percent}");

            ui.documentZoom.percent = note;
            ui.zoom.percent = app;
            SettingsRegistry.Apply();
        }

        [A_XSDActionDependency("TextInput.CommandIsControl", "Test")]
        private static IEnumerator<int> CommandIsControl(TestContext t)
        {
            DocumentEditorControl editor = ShowParagraphs(t, "item");
            DocumentControl content = Content(editor);
            BlockControl p = Paragraphs(editor)[0];
            bool was = GestureMatcher.commandIsControl;
            yield return 2;

            content.SetCaret(p, 0);
            content.SetCaret(p, 4, true);
            GestureMatcher.commandIsControl = false;
            ClipboardText.Set("");
            yield return t.Key(Keys.C, Keys.LeftSuper);
            t.Check(ClipboardText.Get() == "", "off a Mac, Super+C does not fill a Ctrl bind");

            GestureMatcher.commandIsControl = true;
            yield return t.Key(Keys.C, Keys.RightSuper);
            t.Check(ClipboardText.Get() == "item", "on a Mac, Cmd+C fills the Ctrl+C bind");

            GestureMatcher.commandIsControl = was;
        }

        [A_XSDActionDependency("TextInput.RightModifiers", "Test")]
        private static IEnumerator<int> RightModifiers(TestContext t)
        {
            RichTextDocument fixture = DocumentXml.Parse(new XElement("Document",
                new XElement("Block", new XAttribute("List", "Bullet"), new XAttribute("Level", 1),
                    new XElement("Run", new XAttribute("Text", "item")))));
            DocumentEditorControl editor = ShowFixture(t, fixture);
            DocumentControl content = Content(editor);
            BlockControl p = Paragraphs(editor)[0];
            yield return 2;

            yield return t.Key(Keys.Tab, Keys.RightShift);
            t.Check(p.listLevel == 0, "Right Shift+Tab outdents");

            content.SetCaret(p, 0);
            content.SetCaret(p, 4, true);
            yield return t.Key(Keys.C, Keys.RightControl);
            t.Check(ClipboardText.Get() == "item", "Right Ctrl+C copies");

            yield return t.Key(Keys.Z, Keys.RightControl);
            t.Check(p.listLevel == 1, "Right Ctrl+Z undoes");

            TabViewControl view = new TabViewControl
            {
                horizontalAlignment = HorizontalAlignment.Stretch,
                verticalAlignment = VerticalAlignment.Stretch
            };
            view.AddChild(new TabItemControl { header = "one" });
            view.AddChild(new TabItemControl { header = "two" });
            view.AddChild(new TabItemControl { header = "three" });
            StackPanelControl host = new StackPanelControl
            {
                horizontalAlignment = HorizontalAlignment.Stretch,
                verticalAlignment = VerticalAlignment.Stretch
            };
            host.AddChild(view);
            t.Show(host);
            UIEngine.SetActiveControl(view);
            yield return 2;

            yield return t.Key(Keys.Backslash, Keys.RightControl, Keys.RightShift);
            yield return 2;
            t.Check(view.parent is SplitViewControl { orientation: StackPanelControl.Orientation.Vertical },
                "Right Ctrl+Right Shift+\\ splits down");
            t.Check(!host.children.OfType<SplitViewControl>().Any(s => s.children.OfType<SplitViewControl>().Any())
                && view.parent?.parent == host, "and the looser Ctrl+\\ bind does not also fire");
        }
        #endregion

        #region ---- styling ----
        [A_XSDActionDependency("TextInput.StyleSurvivesEnterAndBackspace", "Test")]
        private static IEnumerator<int> StyleSurvivesEnterAndBackspace(TestContext t)
        {
            DocumentEditorControl editor = ShowFixture(t, DocumentXml.Parse(new XElement("Document",
                Block("x"),
                new XElement("Block", RunX("plain "), RunX("word", ("Bold", "true"))),
                new XElement("Block", RunX("ab"), RunX("CD", ("Bold", "true"))),
                Block("tail"))));
            DocumentControl content = Content(editor);
            List<BlockControl> p = Paragraphs(editor);
            yield return 2;

            content.SetCaret(p[0], 1);
            yield return t.Key(Keys.B, Keys.LeftControl);
            yield return t.Type("ab");
            yield return t.Key(Keys.Enter);
            yield return t.Type("c");
            p = Paragraphs(editor);
            t.Check(p[1].text == "c" && p[1].StyleAt(0).IsBold, "a style picked before Enter carries into the new paragraph");

            BlockControl word = p[2];
            content.SetCaret(word, word.Length);
            for (int i = 0; i < 4; i++) yield return t.Key(Keys.Backspace);
            yield return t.Type("z");
            t.Check(word.text == "plain z" && word.StyleAt(6).IsBold, "typing after backspacing out a bold word stays bold");

            BlockControl mixed = p[3];
            content.SetCaret(mixed, 2);
            yield return t.Key(Keys.Backspace);
            yield return t.Type("y");
            t.Check(mixed.text == "ayCD" && !mixed.StyleAt(1).IsBold, "backspacing plain text in front of bold keeps typing plain");

            content.SetCaret(p[0], 0);
            yield return t.Key(Keys.B, Keys.LeftControl);
            BlockControl tail = p[4];
            yield return t.Click(tail);
            yield return t.Type("q");
            int q = tail.text.IndexOf('q');
            t.Check(q >= 0 && !tail.StyleAt(q).IsBold, "a click drops a picked style");
        }

        [A_XSDActionDependency("TextInput.UnderlineToggles", "Test")]
        private static IEnumerator<int> UnderlineToggles(TestContext t)
        {
            DocumentEditorControl editor = ShowParagraphs(t, "under line");
            DocumentControl content = Content(editor);
            BlockControl p = Paragraphs(editor)[0];
            yield return 2;

            content.SetCaret(p, 0);
            content.SetCaret(p, 5, true);
            yield return t.Key(Keys.U, Keys.LeftControl);
            t.Check(p.StyleAt(2).underline && !p.StyleAt(7).underline, "Ctrl+U underlines the selection");
            t.Check(DocumentXml.ToXml(editor.session.document).ToString().Contains("Underline=\"true\""), "an underline saves to XML");

            yield return t.Key(Keys.U, Keys.LeftControl);
            t.Check(!p.StyleAt(2).underline, "Ctrl+U again takes it off");
        }

        [A_XSDActionDependency("TextInput.MixedSelectionToggles", "Test")]
        private static IEnumerator<int> MixedSelectionToggles(TestContext t)
        {
            DocumentEditorControl editor = ShowFixture(t, DocumentXml.Parse(new XElement("Document",
                new XElement("Block", RunX("plain "), RunX("bold", ("Bold", "true"))),
                new XElement("Block", RunX("next", ("Bold", "true"))))));
            DocumentControl content = Content(editor);
            List<BlockControl> p = Paragraphs(editor);
            yield return 2;

            content.SetCaret(p[0], 0);
            content.SetCaret(p[1], 4, true);
            yield return t.Key(Keys.B, Keys.LeftControl);
            t.Check(p[0].StyleAt(1).IsBold && p[0].StyleAt(8).IsBold && p[1].StyleAt(2).IsBold,
                "Ctrl+B over a selection starting plain and ending bold makes all of it bold");

            yield return t.Key(Keys.B, Keys.LeftControl);
            t.Check(!p[0].StyleAt(1).IsBold && !p[0].StyleAt(8).IsBold && !p[1].StyleAt(2).IsBold,
                "Ctrl+B over an all-bold selection takes it off");

            content.SetCaret(p[0], 7);
            content.SetCaret(p[1], 2, true);
            yield return t.Key(Keys.B, Keys.LeftControl);
            yield return t.Key(Keys.I, Keys.LeftControl);
            content.SetCaret(p[0], 7);
            content.SetCaret(p[1], 4, true);
            yield return t.Key(Keys.I, Keys.LeftControl);
            t.Check(p[0].StyleAt(8).IsItalic && p[1].StyleAt(4).IsItalic && p[0].StyleAt(8).IsBold && !p[1].StyleAt(4).IsBold,
                "Ctrl+I over a selection starting italic and ending plain turns italic on, leaving bold alone");

            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("TextInput.HighlightApplies", "Test")]
        private static IEnumerator<int> HighlightApplies(TestContext t)
        {
            DocumentEditorControl editor = ShowParagraphs(t, "mark this");
            DocumentControl content = Content(editor);
            BlockControl p = Paragraphs(editor)[0];
            yield return 2;

            content.SetCaret(p, 5);
            content.SetCaret(p, 9, true);
            editor.ApplyStyle(new StyleDelta(highlightHex: "#C8E6A0"));
            t.Check(p.StyleAt(6).highlightHex == "#C8E6A0" && p.StyleAt(2).highlightHex == null, "a highlight lands on the selection only");

            yield return t.Key(Keys.Z, Keys.LeftControl);
            t.Check(p.StyleAt(6).highlightHex == null, "undo takes the highlight off");
        }

        [A_XSDActionDependency("TextInput.FontVariant", "Test")]
        private static IEnumerator<int> FontVariant(TestContext t)
        {
            DocumentEditorControl editor = ShowParagraphs(t, "light text");
            DocumentControl content = Content(editor);
            BlockControl p = Paragraphs(editor)[0];
            yield return 2;

            content.SetCaret(p, 0);
            content.SetCaret(p, 5, true);
            editor.ApplyStyle(new StyleDelta(variant: "Light"));
            t.Check(p.StyleAt(2).variant == "Light" && p.StyleAt(7).variant == null, "a variant lands on the selection only");

            XElement xml = DocumentXml.ToXml(editor.session.document);
            t.Check(xml.ToString().Contains("Variant=\"Light\""), "the variant saves to XML");
            t.Check(DocumentXml.ToXml(DocumentXml.Parse(xml)).ToString().Contains("Variant=\"Light\""), "and loads back");

            yield return t.Key(Keys.Z, Keys.LeftControl);
            t.Check(p.StyleAt(2).variant == null, "undo takes the variant off");
        }

        [A_XSDActionDependency("TextInput.DecorationsDraw", "Test")]
        private static IEnumerator<int> DecorationsDraw(TestContext t)
        {
            DocumentEditorControl editor = ShowFixture(t, DocumentXml.Parse(new XElement("Document",
                new XElement("Block",
                    RunX("plain "),
                    RunX("under", ("Underline", "true")),
                    RunX(" "),
                    RunX("struck", ("Strikethrough", "true")),
                    RunX(" "),
                    RunX("highlighted text", ("HighlightHex", "#FFF3A3")),
                    RunX(" "),
                    RunX("red", ("ColorHex", "#E06C75"), ("Underline", "true"))))));
            DocumentControl content = Content(editor);
            BlockControl p = Paragraphs(editor)[0];
            content.SetCaret(p, 34);
            content.SetCaret(p, 38, true);
            yield return 4;

            yield return t.Golden("Decorations", p);
        }

        [A_XSDActionDependency("TextInput.ColorPickerPicks", "Test")]
        private static IEnumerator<int> ColorPickerPicks(TestContext t)
        {
            foreach (string hex in new[] { "#E06C75", "#61AFEF", "#808080", "#000000", "#FFFFFF" })
            {
                ColorPickerControl.TryParseHex(hex, out Vector3 rgb);
                (float h, float s, float v) = ColorPickerControl.RgbToHsv(rgb);
                t.Check(ColorPickerControl.ToHex(ColorPickerControl.HsvToRgb(h, s, v)) == hex, $"{hex} survives HSV and back");
            }

            string? picked = null;
            ColorPickerControl picker = new ColorPickerControl { hex = "#00FF00", padding = new Thickness(20f), onPicked = hex => picked = hex };
            t.Show(picker);
            yield return 2;
            t.Check(picker.hex == "#00FF00", "the picker shows the colour it was given");

            // past the field's top-right corner, which clamps to full saturation and brightness
            yield return t.Drag(picker, new Vector2(picker.arrangedRect.x + 20f + 175f, picker.arrangedRect.y + 12f));
            yield return 2;
            t.Check(picked == "#00FF00", $"dragging to the field's bright corner picks the pure hue, got {picked}");

            yield return t.Golden("ColorPicker", picker);
        }

        [A_XSDActionDependency("TextInput.MarkdownColourRoundTrip", "Test")]
        private static IEnumerator<int> MarkdownColourRoundTrip(TestContext t)
        {
            XElement written = new XElement("Document", new XElement("Block",
                RunX("a "),
                RunX("red", ("ColorHex", "#E06C75")),
                RunX(" "),
                RunX("marked", ("HighlightHex", MarkdownFormat.DefaultHighlightHex)),
                RunX(" "),
                RunX("pink", ("HighlightHex", "#F5C2DC"), ("Underline", "true"), ("Bold", "true"))));

            string md = MarkdownFormat.Write(written);
            t.Check(md.Contains("<span style=\"color:#E06C75\">red</span>") && md.Contains("==marked==")
                && md.Contains("<mark style=\"background:#F5C2DC\">") && md.Contains("<u>"), $"colour, highlight and underline write as Obsidian reads them: {md}");

            List<XElement> back = MarkdownFormat.Read(md, "n").Elements("Block").First().Elements("Run").ToList();
            XElement? pink = back.FirstOrDefault(r => (string?)r.Attribute("Text") == "pink");
            t.Check(back.Any(r => (string?)r.Attribute("Text") == "red" && (string?)r.Attribute("ColorHex") == "#E06C75")
                && back.Any(r => (string?)r.Attribute("Text") == "marked" && (string?)r.Attribute("HighlightHex") == MarkdownFormat.DefaultHighlightHex)
                && pink != null && (string?)pink.Attribute("HighlightHex") == "#F5C2DC" && (string?)pink.Attribute("Underline") == "true"
                && (string?)pink.Attribute("Bold") == "true", $"and read back into the same runs: {string.Join(" | ", back)}");

            List<XElement> obsidian = MarkdownFormat.Read(
                "x <span style=\"color: #61afef\">blue</span> <mark style=\"background: #FFB8EBA6;\">pinkish</mark> <b>not ours</b>", "n")
                .Elements("Block").First().Elements("Run").ToList();
            t.Check(obsidian.Any(r => (string?)r.Attribute("Text") == "blue" && (string?)r.Attribute("ColorHex") == "#61AFEF")
                && obsidian.Any(r => (string?)r.Attribute("Text") == "pinkish" && (string?)r.Attribute("HighlightHex") == "#FFB8EB")
                && obsidian.Any(r => ((string?)r.Attribute("Text") ?? "").Contains("<b>not ours</b>")),
                $"hand-written Obsidian spans read, other HTML stays text: {string.Join(" | ", obsidian)}");

            yield return 0;
        }

        [A_XSDActionDependency("TextInput.MarkdownTableRoundTrip", "Test")]
        private static IEnumerator<int> MarkdownTableRoundTrip(TestContext t)
        {
            string table = "| A | B \\| c |\n| --- | ---: |\n| `x\\|y` | **b**<br>two |";
            string md = "before\n" + table + "\nafter\n\\|not a table";
            XElement root = MarkdownFormat.Read(md, "n");

            List<XElement> top = new List<XElement>(root.Elements());
            t.Check(top.Count == 4 && top[1].Name.LocalName == "Table" && top[2].Name.LocalName == "Block"
                && top[3].Name.LocalName == "Block", $"a table between two paragraphs, the rows end at the first line without a pipe: {root}");

            XElement read = top[1];
            List<XElement> columns = new List<XElement>(read.Elements("Column"));
            List<XElement> rows = new List<XElement>(read.Elements("Row"));
            t.Check(columns.Count == 2 && (float?)columns[0].Attribute("Width") == 300f && rows.Count == 2,
                $"two columns splitting an A4 text width, a header and one body row: {read}");

            List<XElement> header = new List<XElement>(rows[0].Elements("Cell"));
            List<XElement> body = new List<XElement>(rows[1].Elements("Cell"));
            XElement code = body[0].Element("Block")!.Element("Run")!;
            List<XElement> split = new List<XElement>(body[1].Elements("Block"));
            t.Check((string?)header[1].Element("Block")!.Element("Run")!.Attribute("Text") == "B | c"
                && (string?)code.Attribute("Text") == "x|y" && (string?)code.Attribute("StylingType") == "Code"
                && split.Count == 2 && (string?)split[0].Attribute("Align") == "Right"
                && (string?)split[0].Element("Run")!.Attribute("Bold") == "true",
                $"escaped pipes, code spans, <br> blocks and the column's alignment read: {read}");

            string written = MarkdownFormat.Write(root);
            t.Check(written == md, $"and write back byte-identical: {written}");

            yield return 0;
        }
        #endregion

        #region ---- links ----
        [A_XSDActionDependency("TextInput.MarkdownLinkRoundTrip", "Test")]
        private static IEnumerator<int> MarkdownLinkRoundTrip(TestContext t)
        {
            string md = "see [[Bootstrapper]], [[Input#Keys|keys]], [site](https://arxtis.dev) and [[#Top]]";
            XElement block = MarkdownFormat.Read(md, "n").Element("Block")!;
            List<string> links = new List<string>();
            foreach (XElement run in block.Elements("Run"))
                if ((string?)run.Attribute("Link") is string link) links.Add(link + "=" + (string?)run.Attribute("Text"));
            t.Check(string.Join(";", links) == "Bootstrapper=Bootstrapper;Input#Keys=keys;https://arxtis.dev=site;#Top=#Top",
                $"wiki, aliased, heading, web and same-note links read as link runs: {block}");

            string written = MarkdownFormat.Write(MarkdownFormat.Read(md, "n"));
            t.Check(written == md, $"and write back byte-identical: {written}");

            yield return 0;
        }

        [A_XSDActionDependency("TextInput.LinkRunXml", "Test")]
        private static IEnumerator<int> LinkRunXml(TestContext t)
        {
            RichTextDocument note = DocumentXml.Parse(new XElement("Document", new XElement("Block",
                new XElement("Run", new XAttribute("Text", "a ")),
                new XElement("Run", new XAttribute("Text", "go"), new XAttribute("Link", "b#h")))));
            XElement? saved = null;
            foreach (XElement run in DocumentXml.ToXml(note).Descendants())
                if (run.Attribute("Link") != null) saved = run;
            t.Check(saved != null && (string?)saved.Attribute("Link") == "b#h" && (string?)saved.Attribute("Text") == "go",
                $"a link run survives a save: {saved}");

            DocumentEditorControl editor = ShowFixture(t, note);
            DocumentControl content = Content(editor);
            BlockControl p = Paragraphs(editor)[0];
            yield return 2;

            content.SetCaret(p, 3);
            yield return t.Type("o");
            t.Check(p.text == "a goo" && p.data.LinkOf(3) == "b#h" && p.data.LinkOf(4) == "b#h", $"typing inside a link extends it: {p.text}");

            content.SetCaret(p, 5);
            yield return t.Type("x");
            t.Check(p.text == "a goox" && p.data.LinkOf(5) == null && p.data.LinkOf(4) == "b#h", $"typing at its end stays outside it: {p.text}");
        }

        [A_XSDActionDependency("TextInput.TypedLinkConverts", "Test")]
        private static IEnumerator<int> TypedLinkConverts(TestContext t)
        {
            DocumentEditorControl editor = ShowParagraphs(t, "a ");
            DocumentControl content = Content(editor);
            BlockControl p = Paragraphs(editor)[0];
            yield return 2;

            content.SetCaret(p, 2);
            yield return t.Type("[[x|y]]");
            t.Check(p.text == "a y" && p.data.LinkOf(2) == "x" && content.caretOffset == 3, $"the closing ]] makes one link run: {p.text}");

            yield return t.Key(Keys.Z, Keys.LeftControl);
            t.Check(p.text == "a [[x|y]" && p.data.LinkOf(2) == null, $"one undo gives the brackets back: {p.text}");
        }

        [A_XSDActionDependency("TextInput.LinkSuggest", "Test")]
        private static IEnumerator<int> LinkSuggestNames(TestContext t)
        {
            Func<string?, string[]>? names = LinkSuggest.linkNames;
            LinkSuggest.linkNames = _ => new[] { "Bootstrapper", "Bones", "Input" };

            DocumentEditorControl editor = ShowParagraphs(t, "");
            DocumentControl content = Content(editor);
            BlockControl p = Paragraphs(editor)[0];
            yield return 2;

            yield return t.Type("[[Bo");
            t.Check(LinkSuggest.IsOpen && LinkSuggest.ShownCount == 2 && LinkSuggest.ShownAt(0) == "Bootstrapper" && LinkSuggest.ShownAt(1) == "Bones",
                "a [[ lists the names its text starts");

            yield return t.Key(Keys.Down);
            yield return t.Key(Keys.Enter);
            t.Check(!LinkSuggest.IsOpen && p.text == "Bones" && p.data.LinkOf(0) == "Bones", $"Down and Enter take the second name as a link: {p.text}");

            yield return t.Key(Keys.Z, Keys.LeftControl);
            t.Check(p.text == "[[Bo" && p.data.LinkOf(0) == null, $"one undo restores what was typed: {p.text}");

            content.SetCaret(p, p.text.Length);
            yield return t.Type("o");
            yield return t.Key(Keys.Escape);
            t.Check(!LinkSuggest.IsOpen && p.text == "[[Boo", $"Escape closes the list and keeps the text: {p.text}");

            LinkSuggest.linkNames = names;
        }

        [A_XSDActionDependency("TextInput.LinkSuggestHeadings", "Test")]
        private static IEnumerator<int> LinkSuggestHeadings(TestContext t)
        {
            string target = Path.Combine(Path.GetTempPath(), $"aurora-headings-{Guid.NewGuid():N}.md");
            File.WriteAllText(target, "# Phases\ntext\n## Stages\n");
            Func<string?, string[]>? names = LinkSuggest.linkNames;
            Func<string?, string, string?>? find = LinkIndex.findNote;
            LinkSuggest.linkNames = _ => new[] { "Bootstrapper" };
            LinkIndex.findNote = (home, name) => name == "Boot" ? target : null;

            DocumentEditorControl editor = ShowParagraphs(t, "");
            BlockControl p = Paragraphs(editor)[0];
            yield return 2;

            yield return t.Type("[[Boot");
            int before = LinkSuggest.headingLoads;
            yield return t.Type("#");
            t.Check(LinkSuggest.ShownCount == 2 && LinkSuggest.ShownAt(0) == "Phases", "a # lists the target's headings");

            yield return t.Type("Ph");
            t.Check(LinkSuggest.headingLoads == before + 1 && LinkSuggest.ShownCount == 1 && LinkSuggest.ShownAt(0) == "Phases",
                $"typing on filters without reading the note again: {LinkSuggest.headingLoads - before} reads");

            yield return t.Key(Keys.Enter);
            t.Check(p.text == "Boot#Phases" && p.data.LinkOf(0) == "Boot#Phases", $"Enter takes the heading link: {p.text}");

            LinkSuggest.linkNames = names;
            LinkIndex.findNote = find;
            File.Delete(target);
        }

        [A_XSDActionDependency("TextInput.FollowLink", "Test")]
        private static IEnumerator<int> FollowLink(TestContext t)
        {
            Func<string?, string, bool>? open = NotePropertiesControl.openLink;
            string? asked = null;
            NotePropertiesControl.openLink = (home, link) =>
            {
                asked = link;
                return true;
            };

            RichTextDocument note = DocumentXml.Parse(new XElement("Document",
                new XElement("Block", new XAttribute("StylingType", "Heading2"), new XElement("Run", new XAttribute("Text", "Phases"))),
                new XElement("Block",
                    new XElement("Run", new XAttribute("Text", "see"), new XAttribute("Link", "b#Phases")),
                    new XElement("Run", new XAttribute("Text", " ")),
                    new XElement("Run", new XAttribute("Text", "web"), new XAttribute("Link", "https://arxtis.dev")))));
            DocumentEditorControl editor = ShowFixture(t, note);
            DocumentControl content = Content(editor);
            List<BlockControl> p = Paragraphs(editor);
            yield return 2;

            string? link = p[1].LinkAt(1);
            t.Check(link == "b#Phases" && p[1].LinkAt(3) == "b#Phases" && p[1].LinkAt(5) == "https://arxtis.dev", "a slot reads the link it touches");
            t.Check(link != null && content.FollowLink(link) && asked == "[[b#Phases]]", $"a note link goes to the host as [[target]]: {asked}");
            t.Check(content.FollowLink("https://arxtis.dev") && asked == "https://arxtis.dev", $"a web address goes as it is: {asked}");

            content.SetCaret(p[1], 0);
            t.Check(editor.ShowHeading("phases") && content.caretBlock == p[0] && content.caretOffset == 0, "ShowHeading puts the caret on the heading");

            NotePropertiesControl.openLink = open;
        }

        [A_XSDActionDependency("TextInput.HeadingRenameRewrites", "Test")]
        private static IEnumerator<int> HeadingRenameRewrites(TestContext t)
        {
            RichTextDocument note = DocumentXml.Parse(new XElement("Document",
                new XElement("Block", new XAttribute("StylingType", "Heading1"), new XElement("Run", new XAttribute("Text", "Phases"))),
                new XElement("Block", new XElement("Run", new XAttribute("Text", "jump"), new XAttribute("Link", "#Phases"))),
                new XElement("Block", new XAttribute("StylingType", "Heading1"), new XElement("Run", new XAttribute("Text", "Dup"))),
                new XElement("Block", new XAttribute("StylingType", "Heading1"), new XElement("Run", new XAttribute("Text", "Dup")))));
            DocumentEditorControl editor = ShowFixture(t, note);
            DocumentControl content = Content(editor);
            List<BlockControl> p = Paragraphs(editor);
            yield return 2;

            string name = Path.GetFileNameWithoutExtension(editor.path!);
            string other = Path.Combine(Path.GetTempPath(), $"aurora-heading-{Guid.NewGuid():N}.axml");
            string otherMd = Path.ChangeExtension(other, ".md");
            File.WriteAllText(other, $"<Document><Block><Run Text=\"p\" Link=\"{name}#Phases\"/></Block></Document>\n");
            File.WriteAllText(otherMd, $"[[{name}#Phases|p]] [[{name}#Other]] [[{name}#Dup]]\n");
            Func<string?, IEnumerable<string>>? notes = NoteLinks.vaultNotes;
            NoteLinks.vaultNotes = _ => new[] { other, otherMd };

            content.SetCaret(p[0], 6);
            for (int i = 0; i < 6; i++) yield return t.Key(Keys.Backspace);
            yield return t.Type("Stages");
            t.Check(File.ReadAllText(otherMd).Contains("#Phases|p"), "nothing is rewritten while the caret is in the heading");

            content.SetCaret(p[1], 0);
            t.Check(File.ReadAllText(other).Contains($"Text=\"p\" Link=\"{name}#Stages\""), $"an axml link follows the heading: {File.ReadAllText(other)}");
            t.Check(File.ReadAllText(otherMd) == $"[[{name}#Stages|p]] [[{name}#Other]] [[{name}#Dup]]\n", $"and a markdown one: {File.ReadAllText(otherMd)}");
            t.Check(p[1].data.LinkOf(0) == "#Stages" && p[1].text == "jump", "a link in the note itself follows too");

            content.SetCaret(p[2], 3);
            for (int i = 0; i < 3; i++) yield return t.Key(Keys.Backspace);
            yield return t.Type("New");
            content.SetCaret(p[1], 0);
            t.Check(File.ReadAllText(otherMd).Contains($"[[{name}#Dup]]"), "a heading whose old text another heading still has rewrites nothing");

            NoteLinks.vaultNotes = notes;
            File.Delete(other);
            File.Delete(otherMd);
        }
        #endregion

        #region ---- lists ----
        [A_XSDActionDependency("TextInput.ListMarkersPerList", "Test")]
        private static IEnumerator<int> ListMarkersPerList(TestContext t)
        {
            DocumentEditorControl editor = ShowFixture(t, DocumentXml.Parse(new XElement("Document",
                Item("a"), Item("b"), Item("c", 1), Item("d"), Block("plain"), Item("e"))));
            DocumentControl content = Content(editor);
            List<BlockControl> p = Paragraphs(editor);
            yield return 2;

            t.Check(p[0].shownMarker == ListMarker.Disc && p[2].shownMarker == ListMarker.Disc, "the default marker is a filled circle");

            ContextMenuSubmenu? markers = ContextMenus.Get("note")?.entries.OfType<ContextMenuSubmenu>().FirstOrDefault(s => s.text == "List marker");
            t.Check(markers != null && markers.entries.OfType<ContextMenuButton>().Count(b => b.action != null) == 12,
                "the note menu offers all eleven markers and Continue numbering, each bound");
            t.Check(p[0].listNumber == 1 && p[1].listNumber == 2 && p[2].listNumber == 1 && p[3].listNumber == 3 && p[5].listNumber == 1,
                "items count per level, a nested list restarts, a paragraph ends the list");

            content.SetCaret(p[1], 0);
            editor.SetListMarker(ListMarker.UpperRoman);
            yield return 2;
            t.Check(p[0].shownMarker == ListMarker.UpperRoman && p[1].shownMarker == ListMarker.UpperRoman
                && p[3].shownMarker == ListMarker.UpperRoman, "a marker applies to its whole level of the list");
            t.Check(p[2].shownMarker == ListMarker.Disc && p[5].shownMarker == ListMarker.Disc, "the nested level and the next list keep theirs");
            t.Check(ListMarkers.Format(p[3].listNumber, p[3].shownMarker) == "III." && ListMarkers.Format(28, ListMarker.LowerAlpha) == "ab."
                && ListMarkers.Format(14, ListMarker.LowerRoman) == "xiv.", "numbers write as the marker says");
            t.Check(DocumentXml.ToXml(editor.session.document).ToString().Contains("Marker=\"UpperRoman\""), "a chosen marker saves to XML");

            content.SetCaret(p[3], 1);
            yield return t.Key(Keys.Enter);
            yield return 2;
            p = Paragraphs(editor);
            t.Check(p[4].listKind == ListKind.Bullet && p[4].shownMarker == ListMarker.UpperRoman && p[4].listNumber == 4,
                "Enter continues the list in its marker");

            yield return t.Key(Keys.Z, Keys.LeftControl);
            yield return t.Key(Keys.Z, Keys.LeftControl);
            yield return 2;
            p = Paragraphs(editor);
            t.Check(p[0].shownMarker == ListMarker.Disc, "undo puts the marker back");

            yield return t.Key(Keys.End, Keys.LeftControl);
            yield return t.Key(Keys.Enter);
            yield return t.Key(Keys.Enter);
            yield return t.Type("1. x");
            yield return 2;
            BlockControl typed = Paragraphs(editor)[^1];
            t.Check(typed.listKind == ListKind.Bullet && typed.listMarker == ListMarker.Decimal && typed.text == "x",
                "typing 1. at a paragraph's start starts a numbered list");

            string md = MarkdownFormat.Write(DocumentXml.ToXml(editor.session.document));
            t.Check(md.Contains("- a") && md.EndsWith("1. x"), $"shapes write as - and numbers as N. in Markdown: {md}");
        }

        [A_XSDActionDependency("TextInput.EmptyItemEnterOutdents", "Test")]
        private static IEnumerator<int> EmptyItemEnterOutdents(TestContext t)
        {
            DocumentEditorControl editor = ShowFixture(t, DocumentXml.Parse(new XElement("Document",
                Item("top"), Item("middle", 1), Item("deep", 2))));
            DocumentControl content = Content(editor);
            yield return 2;

            content.SetCaret(Paragraphs(editor)[2], 4);
            yield return t.Key(Keys.Enter);
            BlockControl empty = Paragraphs(editor)[3];
            t.Check(empty.listLevel == 2, "Enter at an item's end makes an item at its level");

            yield return t.Key(Keys.Enter);
            t.Check(empty.listKind == ListKind.Bullet && empty.listLevel == 1, "Enter on an empty item outdents it");
            yield return t.Key(Keys.Enter);
            t.Check(empty.listLevel == 0, "and again");
            yield return t.Key(Keys.Enter);
            t.Check(empty.listKind == ListKind.None && Paragraphs(editor).Count == 4, "at the top level it becomes a plain paragraph");

            yield return t.Key(Keys.Z, Keys.LeftControl);
            t.Check(empty.listKind == ListKind.Bullet && empty.listLevel == 0, "each step is its own undo");
        }

        [A_XSDActionDependency("TextInput.ListMarkersDraw", "Test")]
        private static IEnumerator<int> ListMarkersDraw(TestContext t)
        {
            List<XElement> items = new List<XElement>();
            foreach (ListMarker marker in Enum.GetValues<ListMarker>())
            {
                XElement item = Item(marker.ToString());
                item.SetAttributeValue("Marker", marker.ToString());
                items.Add(item);
                items.Add(Block(""));
            }

            DocumentEditorControl editor = ShowFixture(t, DocumentXml.Parse(new XElement("Document", items)));
            yield return 4;

            yield return t.Golden("Markers", Content(editor));
        }
        #endregion

        #region ---- markdown insertions, code, rules, alignment ----
        [A_XSDActionDependency("TextInput.MarkdownPrefixes", "Test")]
        private static IEnumerator<int> MarkdownPrefixes(TestContext t)
        {
            DocumentEditorControl editor = ShowParagraphs(t, "");
            yield return 2;

            yield return t.Type("## Title");
            BlockControl block = Paragraphs(editor)[0];
            t.Check(block.stylingType == TextStyleType.Heading2 && block.text == "Title", $"## and a space make a heading 2: {block.stylingType} '{block.text}'");

            for (int i = 0; i < "Title".Length + 1; i++)
                yield return t.Key(Keys.Z, Keys.LeftControl);
            t.Check(block.stylingType == TextStyleType.Text && block.text == "##", $"undoing the space gives the markers back: {block.stylingType} '{block.text}'");

            yield return t.Key(Keys.End, Keys.LeftControl);
            yield return t.Key(Keys.Enter);
            yield return t.Type("> said");
            BlockControl quote = Paragraphs(editor)[^1];
            t.Check(quote.stylingType == TextStyleType.Quote && quote.text == "said", "> and a space make a quote");

            yield return t.Key(Keys.Enter);
            yield return t.Type("a #b");
            t.Check(Paragraphs(editor)[^1].text == "a #b", "a # past a block's start is text");
        }

        [A_XSDActionDependency("TextInput.MarkdownInline", "Test")]
        private static IEnumerator<int> MarkdownInline(TestContext t)
        {
            DocumentEditorControl editor = ShowParagraphs(t, "");
            yield return 2;

            yield return t.Type("a **b** *i* ~~s~~ `c` d");
            BlockControl block = Paragraphs(editor)[0];
            t.Check(block.text == "a b i s c d", $"closing markers drop both markers: '{block.text}'");
            t.Check(block.StyleAt(2).IsBold && block.StyleAt(4).IsItalic && block.StyleAt(6).strikethrough
                && block.StyleAt(8).stylingType == TextStyleType.Code, "each pair styles what it closed over");
            t.Check(!block.StyleAt(3).IsBold && !block.StyleAt(5).IsItalic && !block.StyleAt(7).strikethrough
                && block.StyleAt(10).stylingType != TextStyleType.Code && block.StyleAt(11).stylingType != TextStyleType.Code,
                "what is typed after a closed pair is unstyled");

            yield return t.Key(Keys.Enter);
            yield return t.Type("2 * 3 * 4 and a*b*c");
            BlockControl maths = Paragraphs(editor)[^1];
            t.Check(maths.text == "2 * 3 * 4 and abc" && maths.StyleAt(15).IsItalic && !maths.StyleAt(1).IsItalic,
                $"a * with a space beside it is not a marker, one inside a word is: '{maths.text}'");

            yield return t.Key(Keys.Enter);
            yield return t.Type("x **y**");
            yield return t.Key(Keys.Z, Keys.LeftControl);
            t.Check(Paragraphs(editor)[^1].text == "x **y*", $"one undo puts the markers back: '{Paragraphs(editor)[^1].text}'");
        }

        [A_XSDActionDependency("TextInput.CodeBlockEditing", "Test")]
        private static IEnumerator<int> CodeBlockEditing(TestContext t)
        {
            DocumentEditorControl editor = ShowParagraphs(t, "");
            yield return 2;

            yield return t.Type("```cs");
            yield return t.Key(Keys.Enter);
            BlockControl first = Paragraphs(editor)[0];
            t.Check(first.stylingType == TextStyleType.Code && first.language == "cs" && first.text == "", "```cs and Enter make a cs code block");
            t.Check(first.fontName == "consola", $"code is set in the monospace font: {first.fontName}");

            yield return t.Type("int x;");
            yield return t.Key(Keys.Enter);
            yield return t.Type("x++;");
            yield return t.Key(Keys.Enter);
            yield return t.Key(Keys.Enter);
            yield return t.Type("after");
            yield return 2;

            List<BlockControl> p = Paragraphs(editor);
            t.Check(p.Count == 3 && p[1].stylingType == TextStyleType.Code && p[1].language == "cs"
                && p[2].stylingType == TextStyleType.Text && p[2].text == "after", "Enter on an empty last code line ends the block");
            t.Check(MathF.Abs(p[1].arrangedRect.y - (p[0].arrangedRect.y + p[0].arrangedRect.height)) < 0.01f,
                "code lines sit with no gap between them");
            t.Check(p[2].arrangedRect.y > p[1].arrangedRect.y + p[1].arrangedRect.height + 1f, "text after code keeps its gap");

            string md = MarkdownFormat.Write(DocumentXml.ToXml(editor.session.document));
            t.Check(md.EndsWith("\n```cs\nint x;\nx++;\n```\nafter"), $"a code run writes as one fence with its language: {md.Replace('\n', '|')}");

            Content(editor).SetCaret(p[0], 0);
            yield return t.Type("**a** ");
            t.Check(p[0].text == "**a** int x;", "Markdown is not read inside code");
        }

        [A_XSDActionDependency("TextInput.RuleEditing", "Test")]
        private static IEnumerator<int> RuleEditing(TestContext t)
        {
            DocumentEditorControl editor = ShowParagraphs(t, "above");
            DocumentControl content = Content(editor);
            yield return 2;

            yield return t.Key(Keys.End, Keys.LeftControl);
            yield return t.Key(Keys.Enter);
            yield return t.Type("---");
            yield return t.Key(Keys.Enter);
            List<BlockControl> p = Paragraphs(editor);
            t.Check(p.Count == 3 && p[1].stylingType == TextStyleType.Rule && p[1].text == "" && p[2].stylingType == TextStyleType.Text,
                "--- and Enter make a rule with a paragraph after it");
            t.Check(ReferenceEquals(content.caretBlock, p[2]), "the caret is in the paragraph after the rule");

            yield return t.Type("below");
            yield return t.Key(Keys.Home);
            yield return t.Key(Keys.Backspace);
            p = Paragraphs(editor);
            t.Check(p.Count == 2 && p[1].text == "below" && p[1].stylingType == TextStyleType.Text,
                "Backspace after a rule removes the rule, not the paragraph");

            yield return t.Key(Keys.Z, Keys.LeftControl);
            p = Paragraphs(editor);
            t.Check(p.Count == 3 && p[1].stylingType == TextStyleType.Rule && p[2].text == "below", "undo puts the rule back");

            content.SetCaret(p[1], 0);
            yield return t.Type("z");
            p = Paragraphs(editor);
            t.Check(p.Count == 4 && p[1].stylingType == TextStyleType.Rule && p[2].text == "z", "typing on a rule starts a paragraph after it");

            content.SetCaret(p[0], 2);
            editor.InsertRule();
            p = Paragraphs(editor);
            t.Check(p.Count == 5 && p[0].text == "above" && p[1].stylingType == TextStyleType.Rule,
                "the menu entry puts a rule after a block with text");
        }

        [A_XSDActionDependency("TextInput.MarkdownBlocksRoundTrip", "Test")]
        private static IEnumerator<int> MarkdownBlocksRoundTrip(TestContext t)
        {
            XElement written = new XElement("Document",
                new XElement("Block", new XAttribute("StylingType", "Rule"), new XElement("Run")),
                new XElement("Block", new XAttribute("StylingType", "Code"), new XAttribute("Language", "py"), RunX("x = 1")),
                new XElement("Block", new XAttribute("StylingType", "Code"), RunX("plain")),
                Block("---"),
                new XElement("Block", new XAttribute("StylingType", "Rule"), new XElement("Run")));

            string md = MarkdownFormat.Write(written);
            t.Check(md == "***\n```py\nx = 1\n```\n```\nplain\n```\n\\---\n---", $"rules, fences and a literal --- write as Markdown: {md}");

            List<XElement> back = MarkdownFormat.Read(md, "n").Elements("Block").ToList();
            t.Check(back.Count == 5 && (string?)back[0].Attribute("StylingType") == "Rule"
                && (string?)back[1].Attribute("Language") == "py" && back[2].Attribute("Language") == null
                && back[3].Attribute("StylingType") == null && (string?)back[3].Element("Run")?.Attribute("Text") == "---"
                && (string?)back[4].Attribute("StylingType") == "Rule", $"and read back as written: {string.Join(" | ", back)}");

            t.Check(PlainTextFormat.Write(written).StartsWith("---\nx = 1"), "plain text writes a rule as ---");

            RichTextDocument document = DocumentXml.Parse(written);
            string xml = DocumentXml.ToXml(document).ToString();
            t.Check(xml.Contains("StylingType=\"Rule\"") && xml.Contains("Language=\"py\""), "rules and languages save to XML");
            yield return 0;
        }

        [A_XSDActionDependency("TextInput.MarkdownFences", "Test")]
        private static IEnumerator<int> MarkdownFences(TestContext t)
        {
            XElement written = new XElement("Document",
                new XElement("Block", new XAttribute("StylingType", "Code"), RunX("```")),
                new XElement("Block", new XAttribute("StylingType", "Code"), RunX("x")),
                Block("~~~"));

            string md = MarkdownFormat.Write(written);
            t.Check(md.StartsWith("````\n```\nx\n````\n") && !md.Split('\n')[^1].StartsWith("~~~"),
                $"a fence outgrows the backticks inside it and a literal ~~~ is escaped: {md.Replace('\n', '|')}");

            List<XElement> back = MarkdownFormat.Read(md, "n").Elements("Block").ToList();
            t.Check(back.Count == 3 && TextOf(back[0]) == "```" && TextOf(back[1]) == "x"
                && back[2].Attribute("StylingType") == null && TextOf(back[2]) == "~~~", $"and reads back as written: {string.Join(" | ", back)}");

            List<XElement> tilde = MarkdownFormat.Read("~~~py\na\n```\n~~~\nafter", "n").Elements("Block").ToList();
            t.Check(tilde.Count == 3 && (string?)tilde[0].Attribute("Language") == "py" && TextOf(tilde[1]) == "```"
                && tilde[2].Attribute("StylingType") == null, "a ~~~ fence holds a ``` line and closes only on ~~~");

            List<XElement> open = MarkdownFormat.Read("```\nnever closed", "n").Elements("Block").ToList();
            t.Check(open.Count == 1 && (string?)open[0].Attribute("StylingType") == "Code", "an unclosed fence runs to the end");
            yield return 0;
        }

        [A_XSDActionDependency("TextInput.SortedChecklist", "Test")]
        private static IEnumerator<int> SortedChecklist(TestContext t)
        {
            static XElement Task(string kind, string text, bool ticked, int level = 0)
            {
                XElement item = Block(text);
                item.SetAttributeValue("List", kind);
                if (level > 0) item.SetAttributeValue("Level", level);
                if (ticked) item.SetAttributeValue("Checked", "true");
                return item;
            }

            DocumentEditorControl editor = ShowFixture(t, DocumentXml.Parse(new XElement("Document",
                Task("SortedTask", "a", false), Task("SortedTask", "b", false), Task("SortedTask", "c", false, 1),
                Task("SortedTask", "d", true), Block("after"))));
            DocumentControl content = Content(editor);
            List<BlockControl> p = Paragraphs(editor);
            string Order() => string.Join(",", p.Select(b => b.text + (b.isChecked ? "+" : "")));
            yield return 2;

            content.SetCaret(p[0], 1);
            float aWas = p[0].arrangedRect.y;
            editor.SetChecked(p[0], true);
            t.Check(Order() == "b,c,a+,d+,after", $"ticking moves the item to the top of the ticked ones: {Order()}");
            t.Check(content.caretBlock == p[2] && content.caretOffset == 1, "the caret moves with its item");
            yield return 1;

            float aSliding = p[2].arrangedRect.y;
            t.Check(p[2].reveal > 0f, $"the moved item is still sliding a frame later: reveal {p[2].reveal}");
            yield return 20;

            t.Check(p[2].reveal == 0f && aWas <= aSliding && aSliding < p[2].arrangedRect.y,
                $"it slid down from its old spot: {aWas} -> {aSliding} -> {p[2].arrangedRect.y}");
            t.Check(p[0].arrangedRect.y == aWas, $"the item that took its slot slid up into it: {p[0].arrangedRect.y} vs {aWas}");

            editor.SetChecked(p[3], false);
            t.Check(Order() == "b,c,d,a+,after", $"unticking moves it to the bottom of the unticked ones, past sub-items: {Order()}");

            editor.Undo();
            t.Check(Order() == "b,c,a+,d+,after", $"undo puts the untick and the move back in one step: {Order()}");
            editor.Undo();
            t.Check(Order() == "a,b,c,d+,after", $"and again: {Order()}");
            editor.Redo();
            t.Check(Order() == "b,c,a+,d+,after", $"redo moves it again: {Order()}");

            XElement xml = DocumentXml.ToXml(editor.session.document);
            t.Check(xml.Descendants().Count(b => b.Name.LocalName == "Block" && (string?)b.Attribute("List") == "SortedTask") == 4, "the note saves the list as SortedTask");
            string md = MarkdownFormat.Write(xml);
            t.Check(md.Contains("- [ ] b\n\t- [ ] c\n- [x] a\n- [x] d\n"), $"Markdown writes it as a plain task list: {md.Replace('\n', '|')}");

            editor = ShowFixture(t, DocumentXml.Parse(new XElement("Document",
                Task("Task", "x", true), Task("Task", "y", false), Task("Task", "z", true))));
            content = Content(editor);
            p = Paragraphs(editor);
            yield return 2;

            content.SetCaret(p[0], 0);
            content.SetCaret(p[2], 1, true);
            editor.ToggleSortedTasks();
            t.Check(Order() == "y,x+,z+" && p.All(b => b.listKind == ListKind.SortedTask), $"converting sorts unticked above ticked: {Order()}");
            editor.Undo();
            t.Check(Order() == "x+,y,z+" && p.All(b => b.listKind == ListKind.Task), $"one undo takes the conversion and the sort back: {Order()}");

            SortedTasksSetting sorted = SettingsRegistry.Get<UISettings>().sortedTasks;
            SortedTasksSetting.CheckedEnd endBefore = sorted.checkedEnd;
            sorted.checkedEnd = SortedTasksSetting.CheckedEnd.Top;

            editor = ShowFixture(t, DocumentXml.Parse(new XElement("Document",
                Task("SortedTask", "a", true), Task("SortedTask", "b", false), Task("SortedTask", "c", false, 1),
                Task("SortedTask", "d", false))));
            p = Paragraphs(editor);
            yield return 2;

            editor.SetChecked(p[3], true);
            t.Check(Order() == "a+,d+,b,c", $"with ticked on top, ticking moves the item to the bottom of the ticked ones: {Order()}");
            editor.SetChecked(p[0], false);
            t.Check(Order() == "d+,a,b,c", $"and unticking moves it to the top of the unticked ones: {Order()}");

            editor = ShowFixture(t, DocumentXml.Parse(new XElement("Document",
                Task("Task", "x", true), Task("Task", "y", false), Task("Task", "z", true))));
            content = Content(editor);
            p = Paragraphs(editor);
            yield return 2;

            content.SetCaret(p[0], 0);
            content.SetCaret(p[2], 1, true);
            editor.ToggleSortedTasks();
            t.Check(Order() == "x+,z+,y", $"with ticked on top, converting sorts ticked above unticked: {Order()}");
            sorted.checkedEnd = endBefore;
            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("TextInput.CodeBlockTab", "Test")]
        private static IEnumerator<int> CodeBlockTab(TestContext t)
        {
            DocumentEditorControl editor = ShowFixture(t, DocumentXml.Parse(new XElement("Document",
                new XElement("Block", new XAttribute("StylingType", "Code"), RunX("a")),
                new XElement("Block", new XAttribute("StylingType", "Code"), RunX("b")),
                Item("first"), Item("item"))));
            DocumentControl content = Content(editor);
            List<BlockControl> p = Paragraphs(editor);
            yield return 2;

            content.SetCaret(p[0], 1);
            editor.ShiftListLevel(1);
            t.Check(p[0].text == "a\t" && p[0].listLevel == 0, "Tab in code types a tab at the caret");

            content.SetCaret(p[0], 0);
            content.SetCaret(p[1], 1, true);
            editor.ShiftListLevel(1);
            t.Check(p[0].text == "\ta\t" && p[1].text == "\tb", "Tab over selected code lines puts a tab at the head of each");

            editor.ShiftListLevel(-1);
            t.Check(p[0].text == "a\t" && p[1].text == "b", "Shift+Tab takes one off each");
            editor.Undo();
            t.Check(p[0].text == "\ta\t" && p[1].text == "\tb", "undo puts them back");

            content.SetCaret(p[0], 0);
            content.SetCaret(p[1], 1, true);
            editor.ShiftListLevel(-1);
            editor.ShiftListLevel(-1);
            t.Check(p[0].text == "a\t" && p[1].text == "b", "Shift+Tab on a line with no leading tab leaves it");

            content.SetCaret(p[3], 0);
            editor.ShiftListLevel(1);
            t.Check(p[3].listLevel == 1, "Tab outside code still nests a list item");

            RichTextDocument saved = DocumentXml.Parse(DocumentXml.ToXml(editor.session.document));
            t.Check(((NoteBlock)saved.blocks[0]).run.text == "a\t", "a tab survives an XML save");
            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("TextInput.MarkdownFancyLists", "Test")]
        private static IEnumerator<int> MarkdownFancyLists(TestContext t)
        {
            static XElement Marked(string text, string marker, int level = 0)
            {
                XElement item = Item(text, level);
                item.SetAttributeValue("Marker", marker);
                return item;
            }

            XElement written = new XElement("Document",
                Marked("one", "LowerAlpha"), Marked("two", "LowerAlpha"),
                Marked("deep", "LowerRoman", 1), Marked("deeper", "LowerRoman", 1),
                Marked("three", "LowerAlpha"),
                Block("plain"),
                Marked("big", "UpperAlpha"), Marked("bigger", "UpperRoman"),
                Block("a. not a list"), Block("A. Smith"));

            string md = MarkdownFormat.Write(written);
            t.Check(md == "a. one\nb. two\n\ti. deep\n\tii. deeper\nc. three\nplain\nA.  big\nI.  bigger\na\\. not a list\nA. Smith",
                $"letters and numerals write as their markers: {md.Replace('\n', '|')}");

            List<XElement> back = MarkdownFormat.Read(md, "n").Elements("Block").ToList();
            string[] markers = back.Select(b => (string?)b.Attribute("Marker") ?? "-").ToArray();
            t.Check(string.Join(",", markers) == "LowerAlpha,LowerAlpha,LowerRoman,LowerRoman,LowerAlpha,-,UpperAlpha,UpperRoman,-,-",
                $"and read back as the same markers: {string.Join(",", markers)}");
            t.Check(TextOf(back[7]) == "bigger" && TextOf(back[8]) == "a. not a list" && TextOf(back[9]) == "A. Smith"
                && (int?)back[2].Attribute("Level") == 1, "with their text, levels and the prose left alone");

            List<XElement> prose = MarkdownFormat.Read("etc. and so on\nDr. Who\nvi) six", "n").Elements("Block").ToList();
            t.Check(prose[0].Attribute("List") == null && prose[1].Attribute("List") == null
                && (string?)prose[2].Attribute("Marker") == "LowerRoman", "words stay prose, a roman numeral is a list");
            yield return 0;
        }

        [A_XSDActionDependency("TextInput.ListStartNumbers", "Test")]
        private static IEnumerator<int> ListStartNumbers(TestContext t)
        {
            const string source = "3. three\n4. four\ntext\n1. one\n1. two";
            XElement read = MarkdownFormat.Read(source, "n");
            List<XElement> back = read.Elements("Block").ToList();
            t.Check((int?)back[0].Attribute("Start") == 3 && back.Skip(1).All(b => b.Attribute("Start") == null),
                $"only a list's first number is kept, and only when it is not 1: {string.Join(",", back.Select(b => (string?)b.Attribute("Start") ?? "-"))}");
            string md = MarkdownFormat.Write(read);
            t.Check(md == "3. three\n4. four\ntext\n1. one\n2. two", $"and writes back counting on from it: {md.Replace('\n', '|')}");

            DocumentEditorControl editor = ShowFixture(t, DocumentXml.Parse(read));
            DocumentControl content = Content(editor);
            List<BlockControl> p = Paragraphs(editor);
            yield return 2;
            t.Check(p[0].listNumber == 3 && p[1].listNumber == 4 && p[3].listNumber == 1 && p[4].listNumber == 2,
                $"the note numbers from the start: {string.Join(",", p.Select(b => b.listNumber))}");

            content.SetCaret(p[4], 1);
            editor.ContinueNumbering();
            yield return 2;
            t.Check(p[3].listStart == 5 && p[3].listNumber == 5 && p[4].listNumber == 6,
                "Continue numbering starts the caret's list after the list above it");
            t.Check(DocumentXml.ToXml(editor.session.document).ToString().Contains("Start=\"5\""), "the start saves to XML");

            yield return t.Key(Keys.Z, Keys.LeftControl);
            yield return 2;
            t.Check(p[3].listStart == null && p[3].listNumber == 1, "one undo puts the numbering back");

            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("TextInput.HeaderFieldTakesPress", "Test")]
        private static IEnumerator<int> HeaderFieldTakesPress(TestContext t)
        {
            string path = Path.Combine(Path.GetTempPath(), $"aurora-edit-{Guid.NewGuid():N}.md");
            File.WriteAllText(path, "---\nauthor: me\n---\nbody");
            DocumentEditorControl editor = NewEditor();
            t.Show(editor);
            editor.LoadPath(path);
            File.Delete(path);
            ((ExpanderControl)Content(editor).header!).expanded = true;
            yield return 2;

            TextBoxControl field = DescendantsOf<TextBoxControl>(Content(editor).header!).First(f => f.text == "me");
            yield return t.Click(field);
            t.Check(field.isEditing, "a click on a field in the note's header starts editing it");
        }

        [A_XSDActionDependency("TextInput.ViewStateRoundTrip", "Test")]
        private static IEnumerator<int> ViewStateRoundTrip(TestContext t)
        {
            string words = string.Join(" ", Enumerable.Repeat("lorem ipsum dolor sit", 12));
            string path = Path.Combine(Path.GetTempPath(), $"aurora-view-{Guid.NewGuid():N}.axml");
            RichTextDocument fixture = Paragraphs(Enumerable.Repeat(words, 60).ToArray());
            DocumentXml.Save(fixture, path);

            DocumentEditorControl first = NewEditor();
            t.Show(first);
            first.LoadPath(path);
            yield return 2;

            List<BlockControl> p = Paragraphs(first);
            Content(first).SetCaret(p[30], 5);
            Content(first).SetCaret(p[31], 12, true);
            first.SetScrollOffset(new Vector2(0f, 2000f));
            yield return 2;

            SessionTab saved = first.ViewState();
            t.Check(saved.topBlock > 0 && saved.caretBlock == 31 && saved.anchorBlock == 30, $"the view records its selection and top line: top {saved.topBlock}");

            DocumentEditorControl second = NewEditor();
            t.Show(second);
            second.LoadPath(path);
            second.RestoreView(saved);
            yield return 2;
            File.Delete(path);

            SessionTab back = second.ViewState();
            t.Check(back.caretBlock == 31 && back.caretOffset == 12 && back.anchorBlock == 30 && back.anchorOffset == 5, "the selection comes back");
            t.Check(back.topBlock == saved.topBlock && back.topOffset == saved.topOffset && MathF.Abs(back.topDelta - saved.topDelta) < 0.5f,
                $"the same line is at the top: {back.topBlock}/{back.topOffset}/{back.topDelta} vs {saved.topBlock}/{saved.topOffset}/{saved.topDelta}");
            t.Check(MathF.Abs(second.GetScrollOffset().Y - 2000f) < 1f, $"at the same scroll: {second.GetScrollOffset().Y}");
        }

        [A_XSDActionDependency("TextInput.FrontmatterKeys", "Test")]
        private static IEnumerator<int> FrontmatterKeys(TestContext t)
        {
            string block = "---\ntags:\n  - one\n  - two\nrelated: \"[[Other note]]\"\n---";
            List<Frontmatter.Entry> entries = Frontmatter.Entries(block);
            t.Check(entries[0].editable && entries[0].list && entries[0].value == "one, two", "a block list reads as an editable list");
            t.Check(entries[1].value == "[[Other note]]", "a quoted link reads as its text");

            string edited = Frontmatter.Set(block, "tags", "one, three, four")!;
            t.Check(edited.Contains("tags:\n  - one\n  - three\n  - four\n"), $"an edited block list stays a block list: {edited.Replace('\n', '|')}");
            t.Check(Frontmatter.Set(block, "related", "[[Third]]")!.Contains("related: \"[[Third]]\""), "a link writes quoted");

            string added = Frontmatter.Set(block, "status", string.Empty)!;
            t.Check(added.EndsWith("status:\n---"), "a new key goes before the closing line");
            t.Check(Frontmatter.Set(added, "status", null) == block, "and removing it gives the block back");

            RichTextDocument document = DocumentXml.Parse(MarkdownFormat.Read(
                "---\nListMarkers: [Decimal, LowerAlpha]\nReadOnly: true\nPageGap: 40\nPageWidth: 100\n---\nbody", "n"));
            t.Check(document.readOnly && document.layout.listLevels.Count == 2 && document.layout.listLevels[1].marker == ListMarker.LowerAlpha,
                "ListMarkers and ReadOnly read onto the note");

            string md = MarkdownFormat.Write(DocumentXml.ToXml(document));
            t.Check(md.Contains("ListMarkers: [Decimal, LowerAlpha]") && md.Contains("ReadOnly: true") && md.Contains("PageGap: 40") && !md.Contains("PageWidth"),
                $"they write back, PageGap is the user's own key, paper width only rides a Custom size: {md.Replace('\n', '|')}");
            yield return 0;
        }

        [A_XSDActionDependency("TextInput.TabStops", "Test")]
        private static IEnumerator<int> TabStops(TestContext t)
        {
            static XElement Code(string text) => new XElement("Block", new XAttribute("StylingType", "Code"), RunX(text));

            DocumentEditorControl editor = ShowFixture(t, DocumentXml.Parse(new XElement("Document",
                Code("    x"), Code("\tx"), Code("ab\tx"), Code("abcd\tx"), Code("ab\tcd\tx"))));
            yield return 2;

            List<BlockControl> p = Paragraphs(editor);
            float stop = p[0].CaretAt(4).x - p[0].CaretAt(0).x;
            float[] at =
            {
                p[1].CaretAt(1).x - p[1].CaretAt(0).x,
                p[2].CaretAt(3).x - p[2].CaretAt(0).x,
                p[3].CaretAt(5).x - p[3].CaretAt(0).x,
                p[4].CaretAt(6).x - p[4].CaretAt(0).x
            };

            t.Check(stop > 0f && MathF.Abs(at[0] - stop) < 0.5f, $"a tab at the line start reaches the first stop: {at[0]} vs {stop}");
            t.Check(MathF.Abs(at[1] - stop) < 0.5f, $"a tab after two letters reaches the same stop: {at[1]}");
            t.Check(MathF.Abs(at[2] - 2f * stop) < 0.5f, $"a tab starting on a stop moves to the next: {at[2]}");
            t.Check(MathF.Abs(at[3] - 2f * stop) < 0.5f, $"two tabs on one line each reach their stop: {at[3]}");

            CaretGeometry x = p[2].CaretAt(3);
            t.Check(p[2].IndexAt(new Vector2(p[2].TextOrigin.X + x.x + 1f, p[2].TextOrigin.Y + x.top + 1f)) == 3,
                "a click just after the tab lands after it");
            t.Check(MathF.Abs(p[2].Lines![0].width - (p[2].CaretAt(4).x - p[2].CaretAt(0).x)) < 0.5f, "the line's width counts the tab to its stop");
        }

        [A_XSDActionDependency("TextInput.SessionScopeSwap", "Test")]
        private static IEnumerator<int> SessionScopeSwap(TestContext t)
        {
            static string TempNote(string text)
            {
                string path = Path.Combine(Path.GetTempPath(), $"aurora-scope-{Guid.NewGuid():N}.axml");
                RichTextDocument note = Paragraphs(text);
                DocumentXml.Save(note, path);
                return Path.GetFullPath(path);
            }

            static string OpenPaths(Control root) => string.Join(",", TabViewControl.TabViews(root)
                .SelectMany(v => v.Items).Select(i => TabViewControl.EditorOf(i)?.session?.path));

            string first = TempNote("first"), second = TempNote("second");
            string before = SessionLayout.scope;
            WorkspaceControl workspace = new WorkspaceControl
            {
                paneDocument = "tab-pane",
                defaultDocument = "tab-pane",
                horizontalAlignment = HorizontalAlignment.Stretch,
                verticalAlignment = VerticalAlignment.Stretch
            };
            t.Show(workspace);
            SessionLayout.scope = "test-a";
            workspace.LoadPane()!.AddChild(SessionLayout.tabFactory(first, false));
            yield return 2;

            bool restored = SessionLayout.ChangeScope("test-b");
            yield return 2;
            t.Check(!restored && workspace.children.Count == 1 && OpenPaths(workspace) == "", "a scope with nothing recorded leaves one empty pane");

            TabViewControl.TabViews(workspace).First().AddChild(SessionLayout.tabFactory(second, false));
            yield return 2;
            restored = SessionLayout.ChangeScope("test-a");
            yield return 2;
            t.Check(restored && OpenPaths(workspace) == first, $"going back brings the first scope's tab back: {OpenPaths(workspace)}");

            restored = SessionLayout.ChangeScope("test-b");
            yield return 2;
            t.Check(restored && OpenPaths(workspace) == second, $"and the second scope kept its own: {OpenPaths(workspace)}");

            SessionLayout.scope = before;
            t.Show(new StackPanelControl());
            File.Delete(first);
            File.Delete(second);
        }

        [A_XSDActionDependency("TextInput.ReadOnlyRefusesEdits", "Test")]
        private static IEnumerator<int> ReadOnlyRefusesEdits(TestContext t)
        {
            DocumentEditorControl editor = ShowParagraphs(t, "fixed");
            yield return 2;

            editor.SetReadOnly(true);
            Content(editor).SetCaret(Paragraphs(editor)[0], 5);
            editor.FocusCaret();
            yield return t.Type("x");
            yield return t.Key(Keys.Backspace);
            yield return t.Key(Keys.Enter);
            t.Check(Paragraphs(editor).Count == 1 && Paragraphs(editor)[0].text == "fixed", "a read-only note takes no typing, deletion or Enter");
            t.Check(DocumentXml.ToXml(editor.session.document).ToString().Contains("ReadOnly=\"true\""), "ReadOnly saves to XML");

            editor.SetReadOnly(false);
            yield return t.Type("x");
            t.Check(Paragraphs(editor)[0].text == "fixedx", "and takes them again once unlocked");
            t.Show(new StackPanelControl());
        }

        private static string? TextOf(XElement block) => (string?)block.Element("Run")?.Attribute("Text");

        private static IEnumerable<T> DescendantsOf<T>(Entity root) where T : Entity
        {
            foreach (Entity child in root.children)
            {
                if (child is T match) yield return match;
                foreach (T deeper in DescendantsOf<T>(child)) yield return deeper;
            }
        }

        [A_XSDActionDependency("TextInput.MarkdownSkipsPlainText", "Test")]
        private static IEnumerator<int> MarkdownSkipsPlainText(TestContext t)
        {
            DocumentEditorControl editor = NewEditor();
            t.Show(editor);
            string path = Path.Combine(Path.GetTempPath(), $"aurora-edit-{Guid.NewGuid():N}.txt");
            File.WriteAllText(path, "");
            editor.LoadPath(path);
            File.Delete(path);
            Content(editor).SetCaret(Paragraphs(editor)[0], 0);
            editor.FocusCaret();
            yield return 2;

            yield return t.Type("# x **y** - z");
            yield return t.Key(Keys.E, Keys.LeftControl);
            BlockControl block = Paragraphs(editor)[0];
            t.Check(block.text == "# x **y** - z" && block.stylingType == TextStyleType.Text, "a .txt note takes Markdown as text");
            t.Check(block.alignment == TextAlignment.Left && !editor.CanAlign, "and does not align");
        }

        [A_XSDActionDependency("TextInput.AlignLines", "Test")]
        private static IEnumerator<int> AlignLines(TestContext t)
        {
            string words = string.Join(" ", Enumerable.Repeat("lorem ipsum dolor sit", 12));
            DocumentEditorControl editor = ShowParagraphs(t, words, "short");
            DocumentControl content = Content(editor);
            yield return 2;

            List<BlockControl> p = Paragraphs(editor);
            float wrap = p[0].arrangedRect.width;
            t.Check(p[0].Lines.All(l => l.left == 0f), "text starts left");

            content.SetCaret(p[1], 0);
            yield return t.Key(Keys.E, Keys.LeftControl);
            yield return 2;
            TextLine single = p[1].Lines[0];
            t.Check(p[1].alignment == TextAlignment.Center && MathF.Abs(single.left - (wrap - single.width) * 0.5f) < 0.5f,
                $"Ctrl+E centres the line: left {single.left}, width {single.width}, wrap {wrap}");
            t.Check(MathF.Abs(p[1].CaretAt(0).x - single.left) < 0.5f, "the caret follows the line");

            yield return t.Key(Keys.A, Keys.LeftControl);
            yield return t.Key(Keys.R, Keys.LeftControl);
            yield return 2;
            t.Check(p[0].alignment == TextAlignment.Right && p[1].alignment == TextAlignment.Right, "Ctrl+R right-aligns every selected block");
            t.Check(p[0].Lines.All(l => l.left + l.width >= wrap - 0.5f && l.left + l.width <= wrap + 8f) && p[0].Lines[0].left > 0f,
                "every right-aligned line reaches the right edge, its trailing space hanging past it");
            TextLine head = p[0].Lines[0];
            t.Check(p[0].IndexAt(p[0].TextOrigin + new Vector2(head.left + 1f, head.top + head.height * 0.5f)) == 0,
                "a press at a line's new start hits its first letter");

            string xml = DocumentXml.ToXml(editor.session.document).ToString();
            t.Check(xml.Contains("Align=\"Right\""), "alignment saves to XML");

            yield return t.Key(Keys.Z, Keys.LeftControl);
            t.Check(p[0].alignment == TextAlignment.Left && p[1].alignment == TextAlignment.Center, "undo puts the alignments back");
        }

        [A_XSDActionDependency("TextInput.JustifyLines", "Test")]
        private static IEnumerator<int> JustifyLines(TestContext t)
        {
            string words = string.Join(" ", Enumerable.Repeat("lorem ipsum dolor sit", 12));
            DocumentEditorControl editor = ShowParagraphs(t, words, "short");
            List<BlockControl> p = Paragraphs(editor);
            yield return 2;

            float wrap = p[0].arrangedRect.width;
            yield return t.Key(Keys.A, Keys.LeftControl);
            yield return t.Key(Keys.J, Keys.LeftControl);
            yield return 2;

            IReadOnlyList<TextLine> lines = p[0].Lines;
            t.Check(p[0].alignment == TextAlignment.Justify && p[1].alignment == TextAlignment.Justify, "Ctrl+J justifies every selected block");
            t.Check(lines.Count > 2 && lines.Take(lines.Count - 1).All(l => l.left == 0f && l.spaceExtra > 0f
                && MathF.Abs(p[0].CaretAt(l.justifyEnd).x - wrap) < 0.5f),
                "every line but the last starts at the left edge and its last letter ends at the right one");
            t.Check(lines[^1].spaceExtra == 0f && p[1].Lines[0].spaceExtra == 0f, "a paragraph's last line is not stretched");

            TextLine head = lines[0];
            Vector2 nearEnd = p[0].TextOrigin + new Vector2(wrap - 1f, head.top + head.height * 0.5f);
            t.Check(p[0].IndexAt(nearEnd) == head.justifyEnd, $"a press at the stretched line's right edge lands after its last letter: {p[0].IndexAt(nearEnd)} vs {head.justifyEnd}");
            t.Check(DocumentXml.ToXml(editor.session.document).ToString().Contains("Align=\"Justify\""), "justify saves to XML");

            yield return t.Golden("Justified", p[0]);

            yield return t.Key(Keys.L, Keys.LeftControl);
            yield return 2;
            t.Check(p[0].Lines.All(l => l.spaceExtra == 0f && l.left == 0f), "aligning left again drops the stretch");

            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("TextInput.PageNumbersAndUndo", "Test")]
        private static IEnumerator<int> PageNumbersAndUndo(TestContext t)
        {
            DocumentEditorControl editor = ShowParagraphs(t, "page one");
            DocumentControl content = Content(editor);
            yield return 2;

            PanelControl sheet = content.children.OfType<PanelControl>().First(p => p.children.Count > 0 && p.children[0] is ContainerControl);
            LabelControl number = (LabelControl)((Control)sheet.children[0]).children[(int)SlotPlace.FootCenter];
            t.Check(sheet.arrangedRect.y - content.arrangedRect.y >= 15.5f && sheet.arrangedRect.x - content.arrangedRect.x >= 15.5f,
                $"the first page sits a gap in from the top and left: {sheet.arrangedRect.x - content.arrangedRect.x}, {sheet.arrangedRect.y - content.arrangedRect.y}");
            t.Check(number.text == "", "page numbers are off by default");

            PageLayout numbered = editor.Page!.Clone();
            numbered.pageNumbers = true;
            editor.SetPage(numbered);
            yield return 2;
            float bottomMargin = editor.Page!.marginBottom * PageLayout.PxPerMm * content.zoom;
            t.Check(number.text == "1" && number.arrangedRect.Bottom <= sheet.arrangedRect.Bottom
                && number.arrangedRect.y >= sheet.arrangedRect.Bottom - bottomMargin,
                $"the page's number sits in its bottom margin: '{number.text}' at {number.arrangedRect.y}..{number.arrangedRect.Bottom}, sheet bottom {sheet.arrangedRect.Bottom}");
            t.Check(DocumentXml.ToXml(editor.session.document).ToString().Contains("PageNumbers=\"true\""), "page numbers save to XML");

            yield return t.Key(Keys.Z, Keys.LeftControl);
            yield return 2;
            t.Check(!editor.Page!.pageNumbers && number.text == "", "one undo takes the page change back");
            yield return t.Key(Keys.Y, Keys.LeftControl);
            yield return 2;
            t.Check(editor.Page!.pageNumbers && number.text == "1", "redo puts it back");

            t.Check(!DocumentToolbarControl.SetCustomSize(editor, "abc", "150"), "a size that is not a number is refused");
            t.Check(DocumentToolbarControl.SetCustomSize(editor, "100", "150.5") && editor.Page!.size == PageSize.Custom
                && Vector2.Distance(editor.Page.SizePx(), new Vector2(100f, 150.5f) * PageLayout.PxPerMm) < 0.01f,
                "the custom fields set the paper size in millimetres");

            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("TextInput.AlignAroundPicture", "Test")]
        private static IEnumerator<int> AlignAroundPicture(TestContext t)
        {
            string dir = PictureFolder();
            string path = Path.Combine(dir, "float.png");
            Solid(80, 60, Rgba(200, 80, 40)).SavePng(path);
            string words = string.Join(" ", Enumerable.Repeat("lorem ipsum dolor sit", 10));

            XElement block = new XElement("Block", new XAttribute("Align", "Right"), PictureRun(path, ("Wrap", "Square"), ("X", "0")), RunX(words));
            DocumentEditorControl editor = ShowFixture(t, DocumentXml.Parse(new XElement("Document", block)));
            yield return 2;

            BlockControl p = Paragraphs(editor)[0];
            float wrap = p.arrangedRect.width;
            t.Check(p.Lines.All(l => l.left + l.width >= wrap - 0.5f && l.left + l.width <= wrap + 8f),
                "right-aligned lines beside the picture still end at the right edge");
            t.Check(p.Lines.Count > 1 && p.Lines[0].left >= 80f, $"and never start under the picture: {p.Lines[0].left}");
            t.Check(p.Lines[0].room > 0f && p.Lines[0].room < wrap, $"a line beside the picture has the narrowed room: {p.Lines[0].room}");
        }

        [A_XSDActionDependency("TextInput.MarkdownBlocksDraw", "Test")]
        private static IEnumerator<int> MarkdownBlocksDraw(TestContext t)
        {
            XElement Code(string text)
            {
                XElement code = Block(text);
                code.SetAttributeValue("StylingType", "Code");
                return code;
            }
            XElement Aligned(string text, string align)
            {
                XElement block = Block(text);
                block.SetAttributeValue("Align", align);
                return block;
            }

            DocumentEditorControl editor = ShowFixture(t, DocumentXml.Parse(new XElement("Document",
                Block("Before the code"),
                Code("for (int i = 0; i < 10; i++)"),
                Code("    total += i;"),
                Block("After the code"),
                new XElement("Block", new XAttribute("StylingType", "Rule"), new XElement("Run")),
                Aligned("Centred line", "Center"),
                Aligned("Right line", "Right"),
                new XElement("Block", RunX("inline "), RunX("code", ("StylingType", "Code")), RunX(" in text")))));
            yield return 4;

            yield return t.Golden("Blocks", Content(editor));
        }
        #endregion

        #region ---- math ----
        private static XElement MathRunX(string tex, bool display = false)
        {
            XElement run = new XElement("Run", new XAttribute("Math", tex));
            if (display) run.SetAttributeValue("Display", "true");
            return run;
        }

        [A_XSDActionDependency("TextInput.MathRoundTrip", "Test")]
        private static IEnumerator<int> MathRoundTrip(TestContext t)
        {
            RichTextDocument fixture = DocumentXml.Parse(new XElement("Document",
                new XElement("Block", RunX("a "), MathRunX("x^2"), RunX(" b")),
                new XElement("Block", MathRunX(@"\frac{a}{b}", true))));
            List<XElement> runs = DocumentXml.ToXml(fixture).Descendants().Where(e => e.Name.LocalName == "Run").ToList();
            t.Check(runs.Any(r => (string?)r.Attribute("Math") == "x^2" && r.Attribute("Display") == null)
                && runs.Any(r => (string?)r.Attribute("Math") == @"\frac{a}{b}" && (bool?)r.Attribute("Display") == true),
                "note XML keeps a formula's source and display flag");

            string md = "a $x^2$ b\n$$\n\\frac{a}{b}\n$$\ninline $$y$$ too";
            XElement read = MarkdownFormat.Read(md, "n");
            List<XElement> blocks = read.Elements("Block").ToList();
            t.Check(blocks.Count == 3, $"the markdown reads as three blocks ({blocks.Count})");
            if (blocks.Count == 3)
            {
                t.Check((string?)blocks[0].Elements().ElementAt(1).Attribute("Math") == "x^2", "$x^2$ reads as an inline formula");
                XElement display = blocks[1].Elements().Single();
                t.Check((string?)display.Attribute("Math") == @"\frac{a}{b}" && (bool?)display.Attribute("Display") == true,
                    "a $$ block reads as display math");
                t.Check(blocks[2].Elements().Any(r => (string?)r.Attribute("Math") == "y" && (bool?)r.Attribute("Display") == true),
                    "$$y$$ mid-line reads as display math");
            }
            string back = MarkdownFormat.Write(read);
            t.Check(back == md, $"markdown math writes back as read: {back}");

            foreach (string literal in new[] { "costs $5 and $10", "a \\$x\\$ b", "$ x$ and $y $" })
            {
                XElement doc = MarkdownFormat.Read(literal, "n");
                bool noMath = !doc.Descendants().Any(e => e.Attribute("Math") != null);
                string written = MarkdownFormat.Write(doc);
                t.Check(noMath && written == literal, $"'{literal}' stays text and writes back unchanged ({written})");
            }
            yield break;
        }

        [A_XSDActionDependency("TextInput.MathLineBox", "Test")]
        private static IEnumerator<int> MathLineBox(TestContext t)
        {
            DocumentEditorControl editor = ShowFixture(t, DocumentXml.Parse(new XElement("Document",
                Block("plain line"),
                new XElement("Block", RunX("over "), MathRunX(@"\frac{a}{b}"), RunX(" here")),
                new XElement("Block", RunX("before "), MathRunX(@"\sum_{i=1}^{n} i", true), RunX(" after")))));
            yield return 2;

            List<BlockControl> p = Paragraphs(editor);
            t.Check(p[1].Lines[0].descent > p[0].Lines[0].descent, "a fraction's depth deepens its line");

            IReadOnlyList<TextLine> lines = p[2].Lines;
            t.Check(lines.Count == 3 && lines[1].segments[0].charStart == 7 && lines[0].segments.Sum(s => s.charCount) == 7,
                $"a display formula takes a line of its own ({lines.Count} lines)");
            t.Check(lines.Count == 3 && lines[1].width >= 0.9f * p[2].arrangedRect.width,
                $"the display formula's line spans the column ({(lines.Count > 1 ? lines[1].width : 0f)} of {p[2].arrangedRect.width})");
        }

        [A_XSDActionDependency("TextInput.MathCopyText", "Test")]
        private static IEnumerator<int> MathCopyText(TestContext t)
        {
            DocumentEditorControl editor = ShowFixture(t, DocumentXml.Parse(new XElement("Document",
                new XElement("Block", RunX("a "), MathRunX("x^2"), RunX(" b")),
                new XElement("Block", MathRunX(@"\int f", true)))));
            DocumentControl content = Content(editor);
            List<BlockControl> p = Paragraphs(editor);
            yield return 2;

            content.SetCaret(p[0], 0);
            content.SetCaret(p[1], 1, true);
            yield return t.Key(Keys.C, Keys.LeftControl);
            string expected = "a $x^2$ b" + Environment.NewLine + @"$$\int f$$";
            t.Check(ClipboardText.Get() == expected, $"Ctrl+C writes formulas as their TeX: {ClipboardText.Get()}");
        }

        [A_XSDActionDependency("TextInput.MathDraws", "Test")]
        private static IEnumerator<int> MathDraws(TestContext t)
        {
            DocumentEditorControl editor = ShowFixture(t, DocumentXml.Parse(new XElement("Document",
                new XElement("Block", RunX("Inline "), MathRunX(@"x^2 + y_i^2 = \frac{a}{b}"), RunX(" and "),
                    MathRunX(@"\hat{a} \le \sqrt{x}"), RunX(" in a sentence.")),
                new XElement("Block", MathRunX(@"\sum_{i=1}^{n} i = \frac{n(n+1)}{2}", true)),
                new XElement("Block", MathRunX(@"\left(\frac{\frac{a}{b}}{\frac{\frac{c}{d}}{e}}\right) \sqrt[3]{\frac{\frac{a}{b}}{c}} \int_0^1 f(x)\,dx", true)),
                new XElement("Block", RunX("Broken: "), MathRunX(@"\frac{a"), RunX(" stays readable.")))));
            yield return 4;

            yield return t.Golden("Math", Content(editor));
        }

        [A_XSDActionDependency("TextInput.MathArrays", "Test")]
        private static IEnumerator<int> MathArrays(TestContext t)
        {
            DocumentEditorControl editor = ShowFixture(t, DocumentXml.Parse(new XElement("Document",
                new XElement("Block", RunX("Inline "), MathRunX(@"\binom{n}{k}"), RunX(" and "),
                    MathRunX(@"\begin{smallmatrix} a & b \\ c & d \end{smallmatrix}"), RunX(" in a sentence.")),
                new XElement("Block", MathRunX(@"A = \begin{pmatrix} 1 & 2 & 3 \\ 4 & 5 & 6 \end{pmatrix} \quad \det\begin{vmatrix} a & b \\ c & d \end{vmatrix} = ad - bc", true)),
                new XElement("Block", MathRunX(@"|x| = \begin{cases} x & \text{if } x \ge 0 \\ -x & \text{otherwise} \end{cases}", true)),
                new XElement("Block", MathRunX(@"\begin{aligned} f(x) &= (x+1)^2 \\ &= x^2 + 2x + 1 \end{aligned} \tag{1}", true)),
                new XElement("Block", MathRunX(@"\begin{array}{l|r} 1 & 22 \\ \hline 333 & 4 \end{array} \qquad \boxed{E = mc^2} \qquad \sum_{\substack{0<i<m \\ 0<j<n}} P(i,j)", true)))));
            yield return 4;

            yield return t.Golden("Math", Content(editor));
        }

        [A_XSDActionDependency("TextInput.MathInsertCommit", "Test")]
        private static IEnumerator<int> MathInsertCommit(TestContext t)
        {
            DocumentEditorControl editor = ShowParagraphs(t, "ab");
            yield return 2;

            Content(editor).SetCaret(Paragraphs(editor)[0], 1);
            yield return t.Key(Keys.M, Keys.LeftControl);
            t.Check(FormulaBox() != null, "Ctrl+M opens the new formula's source");
            yield return t.Type("x^2");
            yield return t.Key(Keys.Enter);
            yield return 2;
            t.Check(FormulaBox() == null && MathSources(editor) == "x^2" && Paragraphs(editor)[0].Length == 3,
                $"Enter commits the formula into the note ({MathSources(editor)})");

            yield return t.Key(Keys.Z, Keys.LeftControl);
            t.Check(MathSources(editor) == "" && Paragraphs(editor)[0].text == "ab", "one undo takes the inserted formula out");
            yield return t.Key(Keys.Y, Keys.LeftControl);
            t.Check(MathSources(editor) == "x^2", "redo puts it back");
            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("TextInput.MathInsertCancel", "Test")]
        private static IEnumerator<int> MathInsertCancel(TestContext t)
        {
            DocumentEditorControl editor = ShowParagraphs(t, "ab");
            yield return 2;

            Content(editor).SetCaret(Paragraphs(editor)[0], 1);
            yield return t.Key(Keys.M, Keys.LeftControl, Keys.LeftShift);
            yield return t.Type("y");
            t.Check(Paragraphs(editor)[0].spans.Any(s => s.IsMath && s.mathDisplay), "Ctrl+Shift+M places a display formula");
            yield return t.Key(Keys.Escape);
            t.Check(FormulaBox() == null && MathSources(editor) == "" && Paragraphs(editor)[0].text == "ab",
                "Esc on a new formula leaves nothing behind");

            yield return t.Key(Keys.M, Keys.LeftControl);
            yield return t.Key(Keys.Enter);
            t.Check(FormulaBox() == null && Paragraphs(editor)[0].text == "ab", "an empty formula committed goes away");

            yield return t.Key(Keys.Z, Keys.LeftControl);
            t.Check(Paragraphs(editor)[0].text == "ab", "neither left an undo step");
            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("TextInput.MathEditUndo", "Test")]
        private static IEnumerator<int> MathEditUndo(TestContext t)
        {
            DocumentEditorControl editor = ShowFixture(t, DocumentXml.Parse(new XElement("Document",
                new XElement("Block", RunX("a "), MathRunX("x^2"), RunX(" b")))));
            DocumentControl content = Content(editor);
            yield return 2;

            content.SetCaret(Paragraphs(editor)[0], 2);
            content.SetCaret(Paragraphs(editor)[0], 3, true);
            yield return t.Key(Keys.Enter);
            t.Check(FormulaBox()?.text == "x^2", "Enter on a selected formula opens its source");
            yield return t.Type("y");
            yield return t.Key(Keys.Enter);
            t.Check(FormulaBox() == null && MathSources(editor) == "y" && Paragraphs(editor)[0].Length == 5,
                $"the edit commits in place ({MathSources(editor)})");

            yield return t.Key(Keys.Z, Keys.LeftControl);
            t.Check(MathSources(editor) == "x^2", "undo restores the old source");
            t.Check(editor.SelectedRange(out DocumentAddress from, out DocumentAddress to) && from.offset == 2 && to.offset == 3,
                "and leaves the formula selected");
            yield return t.Key(Keys.Y, Keys.LeftControl);
            t.Check(MathSources(editor) == "y", "redo reapplies the edit");
            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("TextInput.MathLivePreview", "Test")]
        private static IEnumerator<int> MathLivePreview(TestContext t)
        {
            DocumentEditorControl editor = ShowFixture(t, DocumentXml.Parse(new XElement("Document",
                new XElement("Block", RunX("a "), MathRunX("x"), RunX(" b")))));
            DocumentControl content = Content(editor);
            yield return 2;

            content.SetCaret(Paragraphs(editor)[0], 2);
            content.SetCaret(Paragraphs(editor)[0], 3, true);
            yield return t.Key(Keys.Enter);
            yield return t.Key(Keys.End);
            yield return t.Type("^2");
            t.Check(MathSources(editor) == "x^2" && FormulaBox()?.text == "x^2", "each keystroke reaches the note");
            yield return t.Key(Keys.Backspace);
            t.Check(MathSources(editor) == "x^", "so does a deletion");
            yield return t.Key(Keys.Z, Keys.LeftControl);
            t.Check(MathSources(editor) == "x^2" && FormulaBox()?.text == "x^2", "and the field's own undo");
            yield return t.Key(Keys.Escape);
            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("TextInput.MathEscReverts", "Test")]
        private static IEnumerator<int> MathEscReverts(TestContext t)
        {
            DocumentEditorControl editor = ShowFixture(t, DocumentXml.Parse(new XElement("Document",
                new XElement("Block", RunX("a "), MathRunX("x^2"), RunX(" b")))));
            DocumentControl content = Content(editor);
            yield return 2;

            content.SetCaret(Paragraphs(editor)[0], 2);
            content.SetCaret(Paragraphs(editor)[0], 3, true);
            yield return t.Key(Keys.Enter);
            yield return t.Type("zz");
            yield return t.Key(Keys.Escape);
            t.Check(FormulaBox() == null && MathSources(editor) == "x^2", "Esc puts the old source back");
            yield return t.Key(Keys.Z, Keys.LeftControl);
            t.Check(MathSources(editor) == "x^2", "and records nothing");
            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("TextInput.MathClickSelects", "Test")]
        private static IEnumerator<int> MathClickSelects(TestContext t)
        {
            DocumentEditorControl editor = ShowFixture(t, DocumentXml.Parse(new XElement("Document",
                Block("text"),
                new XElement("Block", MathRunX(@"\sum_{i=1}^{n} i", true)))));
            yield return 2;

            yield return t.Click(Paragraphs(editor)[1]);
            t.Check(editor.SelectedRange(out DocumentAddress from, out DocumentAddress to)
                && from.block == 1 && from.offset == 0 && to.block == 1 && to.offset == 1,
                "a click on a formula selects it");
            t.Check(FormulaBox() == null, "one click does not open it");
        }

        [A_XSDActionDependency("TextInput.MathDoubleClickOpens", "Test")]
        private static IEnumerator<int> MathDoubleClickOpens(TestContext t)
        {
            DocumentEditorControl editor = ShowFixture(t, DocumentXml.Parse(new XElement("Document",
                Block("text"),
                new XElement("Block", MathRunX(@"\sum_{i=1}^{n} i", true)))));
            yield return 30;

            yield return t.Click(Paragraphs(editor)[1]);
            yield return t.Click(Paragraphs(editor)[1]);
            t.Check(FormulaBox()?.text == @"\sum_{i=1}^{n} i", "a double click opens the formula's source");
            yield return t.Key(Keys.Escape);
        }
        #endregion

        // The open formula popup's field, or null.
        private static TextBoxControl? FormulaBox() =>
            UIEngine.activeControl is TextBoxControl { isEditing: true } box ? box : null;

        // Every formula's source in the note, joined by '|'.
        private static string MathSources(DocumentEditorControl editor) =>
            string.Join("|", Paragraphs(editor).SelectMany(p => p.spans).Where(s => s.IsMath && s.count > 0).Select(s => s.mathSource));

        private static XElement RunX(string text, params (string name, string value)[] attributes)
        {
            XElement run = new XElement("Run", new XAttribute("Text", text));
            foreach ((string name, string value) in attributes)
                run.SetAttributeValue(name, value);
            return run;
        }

        private static XElement Item(string text, int level = 0)
        {
            XElement item = Block(text);
            item.SetAttributeValue("List", "Bullet");
            if (level > 0) item.SetAttributeValue("Level", level);
            return item;
        }

        // A .tex file opened in an editor, caret at the end of its last line.
        private static DocumentEditorControl ShowTex(TestContext t, string source)
        {
            string path = Path.Combine(Path.GetTempPath(), $"aurora-edit-{Guid.NewGuid():N}.tex");
            File.WriteAllText(path, source);
            DocumentEditorControl editor = NewEditor();
            t.Show(editor);
            editor.LoadPath(path);
            File.Delete(path);

            BlockControl last = Paragraphs(editor)[^1];
            Content(editor).SetCaret(last, last.Length);
            editor.FocusCaret();
            return editor;
        }

        private static bool AllLatex(List<BlockControl> blocks) =>
            blocks.All(b => b.stylingType == TextStyleType.Code && b.language == "latex");

        [A_XSDActionDependency("Tex.SourceEnterPaste", "Test")]
        private static IEnumerator<int> TexSourceEnterPaste(TestContext t)
        {
            DocumentEditorControl editor = ShowTex(t, "a\nb");
            yield return 2;

            yield return t.Key(Keys.Enter);
            yield return t.Key(Keys.Enter);
            List<BlockControl> p = Paragraphs(editor);
            t.Check(p.Count == 4 && AllLatex(p), $"Enter, and Enter again on the empty last line, add LaTeX source lines ({p.Count})");

            ClipboardText.Set("x\ny\nz");
            yield return t.Key(Keys.V, Keys.LeftControl);
            p = Paragraphs(editor);
            t.Check(p.Count == 6 && AllLatex(p) && p[3].text == "x" && p[5].text == "z",
                $"a three-line paste lands as LaTeX source lines ({p.Count})");

            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("Tex.SourceColours", "Test")]
        private static IEnumerator<int> TexSourceColours(TestContext t)
        {
            DocumentEditorControl editor = ShowTex(t,
                "\\documentclass{article} % class\n\\begin{document}\nSee $x^2$ at 12pt.\n\\[ a + b \\]\n\\end{document}");
            yield return 2;
            yield return t.Golden("Source", editor);

            t.Show(new StackPanelControl());
        }

        #region ---- helpers ----
        private static DocumentEditorControl NewEditor() => new DocumentEditorControl
        {
            horizontalAlignment = HorizontalAlignment.Stretch,
            verticalAlignment = VerticalAlignment.Stretch
        };

        // A note of plain paragraphs, opened from a file so it has undo, caret focused at the start.
        private static DocumentEditorControl ShowParagraphs(TestContext t, params string[] texts) =>
            ShowFixture(t, Paragraphs(texts));

        private static DocumentEditorControl ShowFixture(TestContext t, RichTextDocument fixture)
        {
            DocumentEditorControl editor = NewEditor();
            t.Show(editor);
            Load(editor, fixture);
            return editor;
        }

        private static void Load(DocumentEditorControl editor, RichTextDocument fixture)
        {
            string path = Path.Combine(Path.GetTempPath(), $"aurora-edit-{Guid.NewGuid():N}.axml");
            DocumentXml.Save(fixture, path);
            editor.LoadPath(path);
            File.Delete(path);

            Content(editor).SetCaret(Paragraphs(editor)[0], 0);
            editor.FocusCaret();
        }

        // The note's XML without the save stamps, which autosave rewrites.
        private static XElement Unstamped(RichTextDocument document)
        {
            XElement root = DocumentXml.ToXml(document);
            root.Attribute("Created")?.Remove();
            root.Attribute("Modified")?.Remove();
            return root;
        }

        private static RichTextDocument Paragraphs(params string[] texts) =>
            DocumentXml.Parse(new XElement("Document", texts.Select(Block)));

        private static List<BlockControl> Paragraphs(DocumentEditorControl editor) =>
            Views(Content(editor)).OfType<BlockControl>().ToList();

        private static DocumentControl Content(DocumentEditorControl editor) =>
            editor.children.OfType<DocumentControl>().First();

        // The controls showing a note's entries, in note order: blocks, and tables out of their viewports.
        private static List<Control> Views(DocumentControl content) => content.children.OfType<Control>()
            .Select(c => c is ScrollableControl viewport && viewport.children.Count > 0 && viewport.children[0] is TableControl table ? table : c)
            .Where(c => c is BlockControl or TableControl).ToList();

        // Just inside a block's right edge, which resolves to the end of its last line.
        private static Vector2 EndOf(BlockControl block) =>
            new Vector2(block.arrangedRect.x + block.arrangedRect.width - 2f, block.arrangedRect.y + block.arrangedRect.height * 0.5f);
        #endregion

        #region ---- tables ----
        [A_XSDActionDependency("TextInput.TableEnterAddsLine", "Test")]
        private static IEnumerator<int> TableEnterAddsLine(TestContext t)
        {
            DocumentControl content = ShowNote(t, TableNote(120f, 120f), out TableControl table, out RichTextDocument document);
            content.SetCaret(Cell(table, 0), Cell(table, 0).Length);
            yield return 2;

            yield return t.Key(Keys.Enter);
            yield return t.Type("x");
            t.Check(Blocks(table, 0).Count == 2, "Enter in a cell adds a second line to it");
            t.Check(Cell(table, 0, 1).text == "x", "typing lands on the cell's new line");
            t.Check(document.blocks.Length == 3, "the note's own blocks are untouched");

            yield return t.Key(Keys.Z, Keys.LeftControl);
            yield return t.Key(Keys.Z, Keys.LeftControl);
            t.Check(Blocks(table, 0).Count == 1 && Cell(table, 0).text == "r0c0", "undo joins the cell back into one line");
        }

        [A_XSDActionDependency("TextInput.TableTabStepsCells", "Test")]
        private static IEnumerator<int> TableTabStepsCells(TestContext t)
        {
            DocumentControl content = ShowNote(t, TableNote(120f, 120f), out TableControl table);
            content.SetCaret(Cell(table, 0), 2);
            yield return 2;

            yield return t.Key(Keys.Tab);
            yield return t.Type("x");
            t.Check(Cell(table, 1).text == "xr0c1", "Tab moves the caret to the start of the next cell");

            yield return t.Key(Keys.Tab, Keys.LeftShift);
            t.Check(content.caretBlock == Cell(table, 0) && content.caretOffset == 0, "Shift+Tab moves back to the previous cell");
        }

        [A_XSDActionDependency("TextInput.TableLeftCrossesCells", "Test")]
        private static IEnumerator<int> TableLeftCrossesCells(TestContext t)
        {
            DocumentControl content = ShowNote(t, TableNote(120f, 120f), out TableControl table);
            content.SetCaret(Cell(table, 1), 0);
            yield return 2;

            yield return t.Key(Keys.Left);
            t.Check(content.caretBlock == Cell(table, 0) && content.caretOffset == Cell(table, 0).Length,
                "Left from a cell's start lands at the end of the previous cell");
        }

        [A_XSDActionDependency("TextInput.TableBackspaceKeepsCells", "Test")]
        private static IEnumerator<int> TableBackspaceKeepsCells(TestContext t)
        {
            DocumentControl content = ShowNote(t, TableNote(120f, 120f), out TableControl table);
            content.SetCaret(Cell(table, 1), 0);
            yield return 2;

            yield return t.Key(Keys.Backspace);
            t.Check(Cell(table, 0).text == "r0c0" && Cell(table, 1).text == "r0c1", "Backspace at a cell's start deletes nothing");
            t.Check(!content.HasSelection && content.caretBlock == Cell(table, 1) && content.caretOffset == 0,
                "the caret stays where it was, with nothing selected");
        }

        [A_XSDActionDependency("TextInput.TableWideScrolls", "Test")]
        private static IEnumerator<int> TableWideScrolls(TestContext t)
        {
            DocumentControl content = ShowNote(t, TableNote(400f, 400f, 400f), out TableControl table);
            content.SetCaret(Cell(table, 0), 0);
            yield return 2;

            yield return t.Key(Keys.Tab);
            yield return t.Key(Keys.Tab);
            yield return 2;
            ScrollableControl viewport = (ScrollableControl)table.parent;
            t.Check(viewport.GetScrollOffset().X > 0f, "a caret in a column past the page scrolls the table sideways");
            t.Check(viewport.arrangedRect.Overlaps(Cell(table, 2).arrangedRect), "the caret's cell is in view");
        }

        [A_XSDActionDependency("TextInput.TableXmlRoundTrip", "Test")]
        private static IEnumerator<int> TableXmlRoundTrip(TestContext t)
        {
            XElement written = DocumentXml.ToXml(TableNote(120f, 160f));
            XElement cell = written.Descendants().First(e => e.Name.LocalName == "Cell");
            t.Check(written.Descendants().Count(e => e.Name.LocalName == "Column") == 2, "both column widths are written");

            XElement source = new XElement("Document",
                new XElement("Table",
                    new XElement("Column", new XAttribute("Width", 120)),
                    new XElement("Row",
                        new XElement("Cell",
                            new XElement("Block", new XElement("Run", new XAttribute("Text", "a"), new XAttribute("Bold", "true"))),
                            Block("b")))));
            XElement once = DocumentXml.ToXml(DocumentXml.Parse(source));
            XElement twice = DocumentXml.ToXml(DocumentXml.Parse(once));
            t.Check(XNode.DeepEquals(once, twice), "a table survives a save and load unchanged");
            t.Check(once.Descendants().Count(e => e.Name.LocalName == "Block") == 2, "a two-line cell keeps both blocks");
            t.Check(once.Descendants().Any(e => e.Name.LocalName == "Run" && (string?)e.Attribute("Bold") == "true"), "a bold run stays bold");
            t.Check(cell.Elements().Count() == 1, "a one-line cell writes one block");
            yield break;
        }

        [A_XSDActionDependency("TextInput.TableDraws", "Test")]
        private static IEnumerator<int> TableDraws(TestContext t)
        {
            DocumentControl content = ShowNote(t, TableNote(120f, 160f, 120f), out TableControl table);
            content.SetCaret(Cell(table, 1), Cell(table, 1).Length);
            yield return 2;

            yield return t.Type(" wraps onto a second line");
            yield return 2;
            yield return t.Golden("Table", (Control)table.parent);
        }

        [A_XSDActionDependency("TextInput.TableOverflowDraws", "Test")]
        private static IEnumerator<int> TableOverflowDraws(TestContext t)
        {
            DocumentControl content = ShowNote(t, TableNote(400f, 400f, 400f), out TableControl table);
            content.SetCaret(Cell(table, 2), Cell(table, 2).Length);
            yield return 2;

            ((ScrollableControl)table.parent).SetScrollOffset(new System.Numerics.Vector2(10000f, 0f));
            yield return 2;
            yield return t.Golden("Scrolled", (Control)table.parent);
        }

        [A_XSDActionDependency("TextInput.TableWheelAxes", "Test")]
        private static IEnumerator<int> TableWheelAxes(TestContext t)
        {
            ShowNote(t, TableNote(400f, 400f, 400f), out TableControl table);
            yield return 2;

            ScrollableControl viewport = (ScrollableControl)table.parent;
            bool took = viewport.OnPointerScroll(new PointerEvent { delta = new System.Numerics.Vector2(0f, -1f) });
            t.Check(!took && viewport.GetScrollOffset().X == 0f, "a vertical wheel over a sideways table passes it on");

            took = viewport.OnPointerScroll(new PointerEvent { delta = new System.Numerics.Vector2(-1f, 0f) });
            t.Check(took && viewport.GetScrollOffset().X > 0f, "a horizontal wheel scrolls the table");
        }

        [A_XSDActionDependency("TextInput.TableSplitsAcrossPages", "Test")]
        private static IEnumerator<int> TableSplitsAcrossPages(TestContext t)
        {
            XElement table = new XElement("Table", new XElement("Column", new XAttribute("Width", 400)));
            for (int r = 0; r < 12; r++)
                table.Add(new XElement("Row", new XElement("Cell", Enumerable.Range(0, 8).Select(i => Block($"row {r} line {i}")))));
            RichTextDocument fixture = DocumentXml.Parse(new XElement("Document", Block("before"), table, Block("after")));

            DocumentControl content = ShowNote(t, fixture, out TableControl shown);
            yield return 2;

            List<StackPanelControl> cells = shown.Cells();
            int pushed = -1;
            for (int r = 1; r < cells.Count && pushed < 0; r++)
                if (cells[r].arrangedRect.y - cells[r].margin.top > cells[r - 1].arrangedRect.Bottom + cells[r - 1].margin.bottom + 1f)
                    pushed = r;
            t.Check(pushed > 0, "a row that would cross the page break starts on the next page");
            if (pushed < 0) yield break;

            content.SetCaret(Blocks(shown, pushed)[0], 0);
            yield return t.Key(Keys.Right);
            yield return t.Key(Keys.Left);
            yield return 2;
            yield return t.Golden("PageBreak");
        }

        [A_XSDActionDependency("TextInput.TableInsertUndo", "Test")]
        private static IEnumerator<int> TableInsertUndo(TestContext t)
        {
            DocumentEditorControl editor = ShowParagraphs(t, "only");
            RichTextDocument document = editor.session.document;
            XElement original = Unstamped(document);
            yield return 2;

            TextInputActions.InsertTable();
            yield return 2;
            TableControl? table = Views(Content(editor)).ElementAtOrDefault(1) as TableControl;
            t.Check(document.blocks.Length == 3 && table != null && document.blocks[2] is NoteBlock,
                "a table goes after the last paragraph, with a paragraph kept after it");
            if (table == null) yield break;

            PageLayout page = editor.Page!;
            float text = page.SizePx().X - (page.marginLeft + page.marginRight) * PageLayout.PxPerMm;
            t.Check(table.RowCount == 3 && table.widths.Length == 3, "the default table is 3x3");
            t.Check(table.widths.All(w => w == MathF.Floor(text / 3f)), "its columns split the text width evenly");
            t.Check(Content(editor).caretBlock == Cell(table, 0), "the caret lands in the first cell");
            XElement inserted = Unstamped(document);

            yield return t.Key(Keys.Z, Keys.LeftControl);
            t.Check(XNode.DeepEquals(Unstamped(document), original), "one undo takes the table and its paragraph back out");
            yield return t.Key(Keys.Y, Keys.LeftControl);
            t.Check(XNode.DeepEquals(Unstamped(document), inserted), "redo puts the same table back");
            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("TextInput.TableInsertRefusedInMarkdown", "Test")]
        private static IEnumerator<int> TableInsertRefusedInMarkdown(TestContext t)
        {
            DocumentEditorControl editor = NewEditor();
            t.Show(editor);
            string path = Path.Combine(Path.GetTempPath(), $"aurora-table-{Guid.NewGuid():N}.md");
            File.WriteAllText(path, "text\n");
            editor.LoadPath(path);
            File.Delete(path);
            Content(editor).SetCaret(Paragraphs(editor)[0], 0);
            editor.FocusCaret();
            yield return 2;

            TextInputActions.InsertTable();
            t.Check(!editor.session.document.blocks.OfType<NoteTable>().Any(), "a Markdown note refuses a table");
            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("TextInput.TocXmlRoundTrip", "Test")]
        private static IEnumerator<int> TocXmlRoundTrip(TestContext t)
        {
            XElement source = new XElement("Document", new XElement("Toc"), new XElement("Toc", new XAttribute("Depth", 2)), Block("text"));
            RichTextDocument document = DocumentXml.Parse(source);
            t.Check(document.blocks.Length == 3 && document.blocks[0] is NoteToc { depth: 6 } && document.blocks[1] is NoteToc { depth: 2 },
                "<Toc/> reads with every level, <Toc Depth> with its own");

            XElement written = DocumentXml.ToXml(document);
            XElement? first = null, second = null;
            foreach (XElement element in written.Elements())
            {
                if (element.Name.LocalName != "Toc") continue;
                if (first == null) first = element;
                else second = element;
            }
            t.Check(first != null && first.Attribute("Depth") == null && !first.HasElements, "a full-depth table of contents writes back as <Toc/>");
            t.Check(second != null && (int?)second.Attribute("Depth") == 2, "a limited one keeps its Depth");
            t.Check(document.Blocks().Count == 1, "a table of contents holds no blocks of the note");
            yield break;
        }

        [A_XSDActionDependency("TextInput.TocRebuilds", "Test")]
        private static IEnumerator<int> TocRebuilds(TestContext t)
        {
            DocumentEditorControl editor = ShowFixture(t, DocumentXml.Parse(new XElement("Document",
                new XElement("Toc"), new XElement("Toc", new XAttribute("Depth", 1)),
                Heading("One", 1), Block("body"), Heading("Two", 2))));
            DocumentControl content = Content(editor);
            yield return 2;

            TocControl? full = null, top = null;
            foreach (Entity child in content.children)
                if (child is TocControl toc)
                {
                    if (full == null) full = toc;
                    else top = toc;
                }
            t.Check(full != null && top != null, "each <Toc/> gets its own view");
            if (full == null || top == null) yield break;
            t.Check(Entries(full) == "One|Two", "the table lists the headings in order");
            t.Check(Entries(top) == "One", "Depth 1 lists only first-level headings");

            List<BlockControl> blocks = Paragraphs(editor);
            content.SetCaret(blocks[0], blocks[0].Length);
            yield return t.Type("x");
            t.Check(Entries(full) == "Onex|Two", "typing in a heading renames its entry");

            content.SetCaret(blocks[1], 0);
            TextInputActions.Heading2();
            yield return 2;
            t.Check(Entries(full) == "Onex|body|Two", "a paragraph made a heading joins the table");
            t.Check(Entries(top) == "Onex", "a second-level heading stays out of a Depth 1 table");

            TextInputActions.Paragraph();
            yield return 2;
            t.Check(Entries(full) == "Onex|Two", "a heading made a paragraph leaves the table");
            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("TextInput.TocFollow", "Test")]
        private static IEnumerator<int> TocFollow(TestContext t)
        {
            DocumentEditorControl editor = ShowFixture(t, DocumentXml.Parse(new XElement("Document",
                Block("intro"), new XElement("Toc"), Heading("One", 1), Block("body"), Heading("Two", 2))));
            DocumentControl content = Content(editor);
            yield return 2;

            TocControl? toc = null;
            foreach (Entity child in content.children)
                if (child is TocControl found) toc = found;
            t.Check(toc != null && toc.children.Count == 2, "the table shows a row per heading");
            if (toc == null || toc.children.Count != 2) yield break;

            yield return t.Click((Control)toc.children[1]);
            yield return 2;
            t.Check(content.caretBlock?.text == "Two" && content.caretOffset == 0, "pressing a row puts the caret at the start of its heading");
            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("TextInput.TocInsertUndo", "Test")]
        private static IEnumerator<int> TocInsertUndo(TestContext t)
        {
            DocumentEditorControl editor = ShowFixture(t, DocumentXml.Parse(new XElement("Document", Heading("only", 1))));
            RichTextDocument document = editor.session.document;
            XElement original = Unstamped(document);
            yield return 2;

            TextInputActions.InsertToc();
            yield return 2;
            bool shown = false;
            foreach (Entity child in Content(editor).children)
                if (child is TocControl) shown = true;
            t.Check(document.blocks.Length == 2 && document.blocks[1] is NoteToc && shown, "a table of contents goes after the caret's paragraph and is shown");
            XElement inserted = Unstamped(document);

            yield return t.Key(Keys.Z, Keys.LeftControl);
            t.Check(XNode.DeepEquals(Unstamped(document), original), "one undo takes the table of contents back out");
            yield return t.Key(Keys.Y, Keys.LeftControl);
            t.Check(XNode.DeepEquals(Unstamped(document), inserted), "redo puts it back");

            TocControl? toc = null;
            foreach (Entity child in Content(editor).children)
                if (child is TocControl found) toc = found;
            if (toc == null) yield break;
            List<ContextMenuEntry> entries = ContextMenus.Collect(toc);
            t.Check(entries.Count == 1 && entries[0] is ContextMenuButton { text: "Delete table of contents" }, "a table of contents has its own one-entry menu");

            yield return t.Click(toc, Keys.MouseRight);
            yield return 2;
            TextInputActions.DeleteToc();
            ContextMenus.Close();
            yield return 2;
            t.Check(XNode.DeepEquals(Unstamped(document), original), "Delete table of contents takes it out");
            yield return t.Key(Keys.Z, Keys.LeftControl);
            t.Check(XNode.DeepEquals(Unstamped(document), inserted), "one undo brings the deleted table of contents back");
            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("TextInput.TocInsertRefusedInMarkdown", "Test")]
        private static IEnumerator<int> TocInsertRefusedInMarkdown(TestContext t)
        {
            DocumentEditorControl editor = NewEditor();
            t.Show(editor);
            string path = Path.Combine(Path.GetTempPath(), $"aurora-toc-{Guid.NewGuid():N}.md");
            File.WriteAllText(path, "text\n");
            editor.LoadPath(path);
            File.Delete(path);
            Content(editor).SetCaret(Paragraphs(editor)[0], 0);
            editor.FocusCaret();
            yield return 2;

            TextInputActions.InsertToc();
            NoteNode[] blocks = editor.session.document.blocks;
            bool refused = true;
            for (int i = 0; i < blocks.Length; i++)
                if (blocks[i] is NoteToc) refused = false;
            t.Check(refused, "a Markdown note refuses a table of contents");
            t.Show(new StackPanelControl());
        }

        private static XElement Heading(string text, int level) =>
            new XElement("Block", new XAttribute("StylingType", "Heading" + level), new XElement("Run", new XAttribute("Text", text)));

        private static string Entries(TocControl toc) => string.Join("|", toc.Entries);

        [A_XSDActionDependency("TextInput.TableRowsAndColumns", "Test")]
        private static IEnumerator<int> TableRowsAndColumns(TestContext t)
        {
            DocumentControl content = ShowNote(t, TableNote(120f, 160f), out TableControl table, out RichTextDocument document);
            content.SetCaret(Cell(table, 1), 2);
            yield return 2;

            (string name, Action command, Func<TableControl, bool> shape)[] commands =
            {
                ("row above", TextInputActions.InsertRowAbove, s => s.RowCount == 3 && Cell(s, 0).text == "" && Cell(s, 3).text == "r0c1"),
                ("row below", TextInputActions.InsertRowBelow, s => s.RowCount == 3 && Cell(s, 1).text == "r0c1" && Cell(s, 2).text == ""),
                ("column left", TextInputActions.InsertColumnLeft, s => s.widths.SequenceEqual(new[] { 120f, 160f, 160f }) && Cell(s, 1).text == "" && Cell(s, 2).text == "r0c1"),
                ("column right", TextInputActions.InsertColumnRight, s => s.widths.SequenceEqual(new[] { 120f, 160f, 160f }) && Cell(s, 1).text == "r0c1" && Cell(s, 2).text == ""),
                ("row delete", TextInputActions.DeleteRow, s => s.RowCount == 1 && Cell(s, 0).text == "r1c0"),
                ("column delete", TextInputActions.DeleteColumn, s => s.widths.Length == 1 && Cell(s, 0).text == "r0c0")
            };

            foreach ((string name, Action command, Func<TableControl, bool> shape) in commands)
            {
                XElement before = Unstamped(document);
                command();
                yield return 2;
                TableControl changed = (TableControl)Views(content)[1];
                t.Check(shape(changed), $"{name}: the table has its new shape");
                XElement after = Unstamped(document);

                yield return t.Key(Keys.Z, Keys.LeftControl);
                t.Check(XNode.DeepEquals(Unstamped(document), before), $"{name}: undo restores the table exactly");
                yield return t.Key(Keys.Y, Keys.LeftControl);
                t.Check(XNode.DeepEquals(Unstamped(document), after), $"{name}: redo rebuilds the same table");
                yield return t.Key(Keys.Z, Keys.LeftControl);
                content.SetCaret(Cell((TableControl)Views(content)[1], 1), 2);
            }

            TextInputActions.InsertRowAbove();
            yield return 2;
            yield return t.Type("x");
            TableControl shifted = (TableControl)Views(content)[1];
            t.Check(Cell(shifted, 3).text == "r0xc1", "typing after a row insert lands in the caret's own cell");
            yield return t.Key(Keys.Z, Keys.LeftControl);
            t.Check(Cell(shifted, 3).text == "r0c1", "undoing the typing finds the cell by its new flat address");

            TextInputActions.DeleteTable();
            yield return 2;
            t.Check(document.blocks.Length == 2 && content.caretBlock == Views(content)[1], "Delete table leaves the caret on the paragraph after it");
            yield return t.Key(Keys.Z, Keys.LeftControl);
            t.Check(Views(content)[1] is TableControl { RowCount: 3 }, "undo brings the table back");
            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("TextInput.TableTabAddsRow", "Test")]
        private static IEnumerator<int> TableTabAddsRow(TestContext t)
        {
            DocumentControl content = ShowNote(t, TableNote(120f, 120f), out TableControl table, out RichTextDocument document);
            content.SetCaret(Cell(table, 3), 1);
            yield return 2;

            yield return t.Key(Keys.Tab);
            yield return t.Type("x");
            TableControl grown = (TableControl)Views(content)[1];
            t.Check(grown.RowCount == 3 && Cell(grown, 4).text == "x", "Tab past the last cell adds a row and moves into it");

            yield return t.Key(Keys.Z, Keys.LeftControl);
            yield return t.Key(Keys.Z, Keys.LeftControl);
            t.Check(((TableControl)Views(content)[1]).RowCount == 2, "undo takes the row back out");
            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("TextInput.TableListInCell", "Test")]
        private static IEnumerator<int> TableListInCell(TestContext t)
        {
            DocumentControl content = ShowNote(t, TableNote(160f, 160f), out TableControl table);
            content.SetCaret(Cell(table, 0), 0);
            yield return 2;

            yield return t.Type("- ");
            t.Check(Cell(table, 0).listKind == ListKind.Bullet && Cell(table, 0).text == "r0c0", "a list prefix in a cell makes a list");

            content.SetCaret(Cell(table, 0), Cell(table, 0).Length);
            yield return t.Key(Keys.Enter);
            yield return t.Type("b");
            BlockControl second = Cell(table, 0, 1);
            t.Check(second.listKind == ListKind.Bullet, "Enter on an item in a cell continues the list");

            content.SetCaret(second, 0);
            yield return t.Key(Keys.Tab);
            t.Check(second.listLevel == 1 && content.caretBlock == second, "Tab at an item's start nests it");

            content.SetCaret(second, 1);
            yield return t.Key(Keys.Tab);
            t.Check(content.caretBlock == Cell(table, 1), "Tab inside the item's text moves to the next cell");

            content.SetCaret(second, 0);
            yield return t.Type("[ ] ");
            CheckBoxControl? box = second.children.OfType<CheckBoxControl>().FirstOrDefault();
            t.Check(second.listKind == ListKind.Task && box != null, "a task prefix in a cell makes a checkbox");
            if (box == null) yield break;

            yield return t.Click(box);
            t.Check(second.isChecked, "the checkbox in a cell ticks its item");
            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("TextInput.NoteMenuFollowsCaret", "Test")]
        private static IEnumerator<int> NoteMenuFollowsCaret(TestContext t)
        {
            string dir = PictureFolder();
            string png = PicturePng(Path.Combine(dir, "small.png"), 50, 40);
            XElement table = new XElement("Table", new XElement("Column", new XAttribute("Width", 120f)),
                new XElement("Row", new XElement("Cell", Block("cell"))));
            DocumentEditorControl editor = ShowFixture(t, DocumentXml.Parse(new XElement("Document",
                Block("text"), table, new XElement("Block", MathRunX("x^2")), new XElement("Block", RunX("a"), PictureRun(png)))));
            editor.contextMenu = "note";
            yield return 2;

            DocumentControl content = Content(editor);
            List<BlockControl> p = Paragraphs(editor);
            List<string> Captions() => ContextMenus.Collect(editor)
                .Select(e => e switch { ContextMenuButton b => b.text, ContextMenuSubmenu s => s.text, _ => "" }).ToList();

            List<string> menu = Captions();
            t.Check(menu.Contains("Insert table") && !menu.Contains("Table") && !menu.Contains("Edit formula")
                && !menu.Contains("Wrap text") && !menu.Contains("Collision"), $"in a paragraph: {string.Join(", ", menu)}");

            content.SetCaret(Cell((TableControl)Views(content)[1], 0), 0);
            yield return 1;
            menu = Captions();
            t.Check(menu.Contains("Table") && !menu.Contains("Insert table"), $"in a cell: {string.Join(", ", menu)}");

            content.SetCaret(p[1], 0);
            content.SetCaret(p[1], 1, true);
            yield return 1;
            menu = Captions();
            t.Check(menu.Contains("Edit formula") && !menu.Contains("Wrap text"), $"formula selected: {string.Join(", ", menu)}");

            content.SetCaret(p[2], 1);
            content.SetCaret(p[2], 2, true);
            yield return 1;
            menu = Captions();
            t.Check(menu.Contains("Wrap text") && menu.Contains("Collision") && !menu.Contains("Edit formula"),
                $"picture selected: {string.Join(", ", menu)}");

            Directory.Delete(dir, true);
        }

        [A_XSDActionDependency("TextInput.TableResizeColumn", "Test")]
        private static IEnumerator<int> TableResizeColumn(TestContext t)
        {
            DocumentControl content = ShowNote(t, TableNote(120f, 160f), out TableControl table, out RichTextDocument document);
            yield return 2;

            Control grip = table.children.OfType<PanelControl>().First(p => p.hitTestable);
            LayoutRect at = grip.arrangedRect;
            yield return t.Drag(grip, new System.Numerics.Vector2(at.x + at.width * 0.5f + 60f, at.y + at.height * 0.5f));
            yield return 2;
            TableControl resized = (TableControl)Views(content)[1];
            t.Check(resized.widths[0] == 180f && resized.widths[1] == 160f, "dragging the first column's edge widens it alone");
            yield return t.Golden("Resized", (Control)resized.parent);

            yield return t.Key(Keys.Z, Keys.LeftControl);
            t.Check(((TableControl)Views(content)[1]).widths[0] == 120f, "one undo puts the width back");
            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("TextInput.CodeColoursAndOverflow", "Test")]
        private static IEnumerator<int> CodeColoursAndOverflow(TestContext t)
        {
            static XElement Code(string text, bool wrap = false) => new XElement("Block",
                new XAttribute("StylingType", "Code"), new XAttribute("Language", "cs"),
                wrap ? new XAttribute("Wrap", "true") : null, new XElement("Run", new XAttribute("Text", text)));

            string longLine = "var total = items.Where(item => item.IsVisible).Select(item => item.Width * 2).Sum(); // " + new string('x', 60);
            DocumentEditorControl editor = ShowFixture(t, DocumentXml.Parse(new XElement("Document",
                Block("before"), Code("int count = 42; // items"), Code("string name = \"note\";"), Code(longLine),
                Block("between"), Code(longLine, true))));
            List<BlockControl> p = Paragraphs(editor);
            DocumentControl content = Content(editor);
            yield return 2;

            BlockControl unwrapped = p[3];
            BlockControl wrapped = p[5];
            t.Check(unwrapped.Lines!.Count == 1 && unwrapped.arrangedRect.width > p[1].arrangedRect.width, "a code line runs on past the text column");
            t.Check(wrapped.Lines!.Count > 1, "Wrap=\"true\" wraps a code line");
            t.Check(content.arrangedRect.width > editor.Page!.SizePx().X + 1f, "the note is widened to hold the long line");

            XElement saved = DocumentXml.ToXml(editor.session.document);
            t.Check(saved.Elements().Count(e => (string?)e.Attribute("Wrap") == "true") == 1, "only the wrapping block writes Wrap");
            t.Check(!MarkdownFormat.Write(saved).Contains("Wrap"), "Markdown carries no wrap setting");

            yield return t.Golden("Coloured", p[1]);
            content.SetCaret(p[1], 0);
            yield return t.Type("/* ");
            yield return 2;
            yield return t.Golden("Commented", p[2]);

            content.SetCaret(unwrapped, 0);
            yield return t.Key(Keys.End);
            yield return 2;
            t.Check(editor.GetScrollOffset().X > 0f, "the caret at the end of a long code line scrolls the note sideways");
            t.Show(new StackPanelControl());
        }

        // A paragraph, a two-row table whose cells read rNcM, and a paragraph.
        [A_XSDActionDependency("TextInput.TableColumnSpan", "Test")]
        private static IEnumerator<int> TableColumnSpan(TestContext t)
        {
            XElement source = new XElement("Document", Block("before"),
                new XElement("Table", new XAttribute("Borders", "false"),
                    new[] { 100, 120, 140 }.Select(w => new XElement("Column", new XAttribute("Width", w))),
                    new XElement("Row", new XElement("Cell", new XAttribute("ColumnSpan", 2), Block("wide")), new XElement("Cell", Block("r0c2"))),
                    new XElement("Row", Enumerable.Range(0, 3).Select(c => new XElement("Cell", Block($"r1c{c}"))))),
                Block("after"));
            XElement once = DocumentXml.ToXml(DocumentXml.Parse(source));
            t.Check(XNode.DeepEquals(once, DocumentXml.ToXml(DocumentXml.Parse(once))), "a merged, borderless table survives a save and load");
            t.Check(once.Descendants().Any(e => e.Name.LocalName == "Cell" && (string?)e.Attribute("ColumnSpan") == "2")
                && once.Descendants().Any(e => e.Name.LocalName == "Table" && (string?)e.Attribute("Borders") == "false"), "ColumnSpan and Borders are written");

            DocumentControl content = ShowNote(t, DocumentXml.Parse(source), out TableControl table, out RichTextDocument document);
            yield return 2;
            List<StackPanelControl> cells = table.Cells();
            float Outer(StackPanelControl cell) => cell.arrangedRect.width + cell.margin.totalHorizontal;
            int Span(int cell) => DocumentXml.ToXml(document).Descendants().Where(e => e.Name.LocalName == "Cell")
                .Select(c => (int?)c.Attribute("ColumnSpan") ?? 1).ElementAt(cell);
            t.Check(cells.Count == 5 && Span(0) == 2, "one row of two cells, one of three");
            t.Check(Math.Abs(Outer(cells[0]) - Outer(cells[2]) - Outer(cells[3])) < 1f, "the merged cell is as wide as the two columns under it");

            (string name, int caret, Action command, Func<TableControl, bool> shape)[] commands =
            {
                ("column right inside the span", 2, TextInputActions.InsertColumnRight, s => s.widths.Length ==4 && Span(0) == 3 && s.Cells().Count == 6
                    && Cell(s, 1).text == "r0c2" && Cell(s, 3).text == ""),
                ("column left of the span", 0, TextInputActions.InsertColumnLeft, s => s.widths.Length ==4 && Cell(s, 0).text == "" && Span(1) == 2
                    && Cell(s, 1).text == "wide"),
                ("column delete inside the span", 3, TextInputActions.DeleteColumn, s => s.widths.Length ==2 && Span(0) == 1
                    && Cell(s, 0).text == "wide" && Cell(s, 1).text == "r0c2" && Cell(s, 2).text == "r1c0" && Cell(s, 3).text == "r1c2"),
                ("merge right", 2, TextInputActions.MergeCellRight, s => s.Cells().Count == 4 && Span(2) == 2
                    && Blocks(s, 2).Select(b => b.text).SequenceEqual(new[] { "r1c0", "r1c1" })),
                ("split", 0, TextInputActions.SplitCell, s => s.Cells().Count == 6 && Span(0) == 1 && Cell(s, 0).text == "wide" && Cell(s, 1).text == "")
            };

            foreach ((string name, int caret, Action command, Func<TableControl, bool> shape) in commands)
            {
                content.SetCaret(Cell((TableControl)Views(content)[1], caret), 0);
                string at = content.caretBlock!.text;
                XElement before = Unstamped(document);
                command();
                yield return 2;
                TableControl changed = (TableControl)Views(content)[1];
                t.Check(shape(changed), $"{name}: the table has its new shape");
                if (!name.Contains("delete")) t.Check(content.caretBlock?.text == at, $"{name}: the caret stays in its cell");
                yield return t.Key(Keys.Z, Keys.LeftControl);
                t.Check(XNode.DeepEquals(Unstamped(document), before), $"{name}: undo restores the table exactly");
            }

            foreach ((string name, int caret, Action command) in new (string, int, Action)[]
                { ("merge on a row's last cell", 1, TextInputActions.MergeCellRight), ("split on a single cell", 2, TextInputActions.SplitCell) })
            {
                content.SetCaret(Cell((TableControl)Views(content)[1], caret), 0);
                XElement before = Unstamped(document);
                command();
                yield return 2;
                t.Check(XNode.DeepEquals(Unstamped(document), before), $"{name} is refused");
            }
            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("TextInput.FillRun", "Test")]
        private static IEnumerator<int> FillRun(TestContext t)
        {
            XElement source = new XElement("Document",
                new XElement("Block", new XAttribute("Align", "Justify"), RunX("Left side"), RunX(" ", ("Fill", "true")), RunX("Right")),
                new XElement("Block", new XAttribute("StylingType", "Rule"), new XAttribute("Weight", "3"), RunX("")),
                new XElement("Block", new XAttribute("List", "Bullet"), new XAttribute("ListIndent", "10"), RunX("item")));
            XElement once = DocumentXml.ToXml(DocumentXml.Parse(source));
            t.Check(XNode.DeepEquals(once, DocumentXml.ToXml(DocumentXml.Parse(once))) && once.Descendants().Any(e => (string?)e.Attribute("Fill") == "true")
                && once.Descendants().Any(e => (string?)e.Attribute("Weight") == "3") && once.Descendants().Any(e => (string?)e.Attribute("ListIndent") == "10"),
                $"Fill, Weight and ListIndent survive a save and load: {once.ToString(SaveOptions.DisableFormatting)}");

            DocumentEditorControl editor = ShowFixture(t, DocumentXml.Parse(source));
            yield return 2;
            float zoom = Content(editor).zoom;
            List<BlockControl> p = Paragraphs(editor);
            TextLine line = p[0].Lines[0];
            float inner = p[0].arrangedRect.width - p[0].padding.left - p[0].padding.right;
            t.Check(p[0].Lines.Count == 1 && MathF.Abs(line.left + line.width - inner) < 1f && line.fillExtra > 0f && line.spaceExtra == 0f,
                $"the fill takes the slack, the line is not justified: {line.left + line.width} of {inner}, fill {line.fillExtra}, space {line.spaceExtra}");
            t.Check(p[1].arrangedRect.height > 6f * zoom, $"a weighted rule keeps a text line, not just its weight: {p[1].arrangedRect.height}");
            t.Check(MathF.Abs(p[2].padding.left - 10f * zoom) < 0.5f, $"ListIndent replaces the layout's indent: {p[2].padding.left}");

            Content(editor).SetCaret(p[0], "Left side ".Length);
            yield return t.Type("X");
            XElement typed = DocumentXml.ToXml(editor.session.document).Descendants().First(e => e.Name.LocalName == "Block");
            string runs = string.Join("|", typed.Elements().Select(r => $"{r.Attribute("Text")?.Value}{(r.Attribute("Fill") != null ? "(fill)" : "")}"));
            t.Check(runs == "Left side| (fill)|XRight", $"text typed beside the fill stays text: {runs}");
            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("TextInput.TableRules", "Test")]
        private static IEnumerator<int> TableRules(TestContext t)
        {
            XElement RuledCell(string text, params object[] attributes) => new XElement("Cell", attributes, Block(text));
            XElement source = new XElement("Document", Block("before"),
                new XElement("Table", new XAttribute("Borders", "false"), new XAttribute("Padding", "8 0"),
                    new XElement("Column", new XAttribute("Width", 100), new XAttribute("RuleLeft", "Plain")),
                    new XElement("Column", new XAttribute("Width", 100)),
                    new XElement("Column", new XAttribute("Width", 100), new XAttribute("RuleRight", "Double")),
                    new XElement("Row", RuledCell("a", new XAttribute("RuleAbove", "Heavy")), RuledCell("b", new XAttribute("RuleAbove", "Heavy")),
                        RuledCell("c", new XAttribute("RuleAbove", "Heavy 3"))),
                    new XElement("Row",
                        RuledCell("wide", new XAttribute("ColumnSpan", 2), new XAttribute("RuleAbove", "Cmid"), new XAttribute("TrimAbove", "Both"),
                            new XAttribute("RuleBelow", "Heavy")),
                        RuledCell("d", new XAttribute("RuleAbove", "Light"), new XAttribute("RuleBelow", "Heavy")))),
                Block("after"));
            XElement once = DocumentXml.ToXml(DocumentXml.Parse(source));
            t.Check(XNode.DeepEquals(once, DocumentXml.ToXml(DocumentXml.Parse(once))), "rules, trims and padding survive a save and load");
            t.Check(once.Descendants().Any(e => (string?)e.Attribute("RuleAbove") == "Heavy 3") && once.Descendants().Any(e => (string?)e.Attribute("TrimAbove") == "Both")
                && once.Descendants().Any(e => (string?)e.Attribute("RuleRight") == "Double")
                && (string?)once.Descendants().First(e => e.Name.LocalName == "Table").Attribute("Padding") == "8 0", "rules, a rule width and padding are written");

            DocumentControl content = ShowNote(t, DocumentXml.Parse(source), out TableControl table, out RichTextDocument document);
            yield return 2;
            foreach (float zoom in new[] { 1f, 2f })
            {
                content.zoom = zoom;
                content.InvalidateLayout();
                yield return 2;
                table = (TableControl)Views(content)[1];
                List<StackPanelControl> cells = table.Cells();
                float em = cells[0].children.OfType<BlockControl>().First().fontSize * zoom;
                List<LayoutRect> lines = table.children.OfType<PanelControl>()
                    .Where(p => !p.hitTestable && p.arrangedRect.width > 0f && p.arrangedRect.height > 0f)
                    .Select(p => p.arrangedRect).ToList();
                LayoutRect Box(StackPanelControl cell) => new LayoutRect(cell.arrangedRect.x - cell.margin.left, cell.arrangedRect.y - cell.margin.top,
                    cell.arrangedRect.width + cell.margin.totalHorizontal, cell.arrangedRect.height + cell.margin.totalVertical);
                bool Near(float a, float b) => MathF.Abs(a - b) < 0.05f;

                float heavy = MathF.Max(1f, 0.08f * em);
                LayoutRect first = Box(cells[0]);
                t.Check(lines.Count(l => Near(l.y, first.y) && Near(l.height, heavy) && Near(l.x, first.x) && Near(l.Right, Box(cells[1]).Right)) == 1,
                    $"zoom {zoom}: two \\toprule cells joined into one line at 0.08 em ({heavy})");
                t.Check(lines.Any(l => Near(l.y, first.y) && Near(l.height, 3f * zoom)), $"zoom {zoom}: a rule's own width, zoomed");
                t.Check(Near(cells[1].margin.top, MathF.Max(heavy, 3f * zoom) + 0.65f * 0.430555f * em),
                    $"zoom {zoom}: the row's thickest rule and \\belowrulesep under it: {cells[1].margin.top}");
                t.Check(Near(cells[0].margin.left, 8f * zoom), $"zoom {zoom}: the padding across");

                LayoutRect wide = Box(cells[3]);
                t.Check(lines.Any(l => Near(l.y, wide.y) && Near(l.x, wide.x + 0.5f * em) && Near(l.width, wide.width - em) && Near(l.height, MathF.Max(1f, 0.03f * em))),
                    $"zoom {zoom}: a \\cmidrule trimmed by \\cmidrulekern at both ends");
                t.Check(lines.Any(l => Near(l.y, Box(cells[4]).y) && Near(l.height, MathF.Max(1f, 0.05f * em))), $"zoom {zoom}: \\midrule at 0.05 em");
                t.Check(Near(cells[0].margin.bottom, 0.4f * 0.430555f * em), $"zoom {zoom}: \\aboverulesep over the next row's rule");

                float plain = MathF.Max(1f, 0.4f * 96f / 72.27f * zoom);
                float left = first.x;
                float right = Box(cells[2]).Right;
                t.Check(lines.Count(l => Near(l.x, left) && Near(l.width, plain)) == 1, $"zoom {zoom}: a | down the left edge");
                t.Check(lines.Count(l => Near(l.x + l.width, right) && Near(l.width, plain)) == 1
                    && lines.Count(l => Near(l.x, right - 2f * plain - 2f * 96f / 72.27f * zoom) && Near(l.width, plain)) == 1,
                    $"zoom {zoom}: || down the right edge, \\doublerulesep apart");
            }
            content.zoom = 1f;
            content.InvalidateLayout();
            yield return 2;

            IEnumerable<XElement> Row(int row) => DocumentXml.ToXml(document).Descendants().Where(e => e.Name.LocalName == "Row").ElementAt(row).Elements();
            content.SetCaret(Cell((TableControl)Views(content)[1], 3), 0);
            XElement before = Unstamped(document);
            TextInputActions.SplitCell();
            yield return 2;
            List<XElement> split = Row(1).ToList();
            t.Check(split.Count == 3 && split.Take(2).All(c => (string?)c.Attribute("RuleAbove") == "Cmid" && (string?)c.Attribute("RuleBelow") == "Heavy")
                && (string?)split[0].Attribute("TrimAbove") == "Left" && (string?)split[1].Attribute("TrimAbove") == "Right",
                "a split copies the rules, the left trim to the first half and the right to the second");
            yield return t.Key(Keys.Z, Keys.LeftControl);
            t.Check(XNode.DeepEquals(Unstamped(document), before), "undo restores the merged cell's rules");

            content.SetCaret(Cell((TableControl)Views(content)[1], 0), 0);
            TextInputActions.InsertRowBelow();
            yield return 2;
            t.Check(Row(1).All(c => c.Attribute("RuleAbove") == null && c.Attribute("RuleBelow") == null)
                && Row(2).Any(c => (string?)c.Attribute("RuleAbove") == "Cmid") && Row(0).All(c => c.Attribute("RuleAbove") != null),
                "a new row has no rules; the rows around it keep theirs");
            yield return t.Key(Keys.Z, Keys.LeftControl);
            t.Show(new StackPanelControl());
        }

        private static RichTextDocument TableNote(params float[] widths)
        {
            XElement table = new XElement("Table", widths.Select(w => new XElement("Column", new XAttribute("Width", w))));
            for (int r = 0; r < 2; r++)
                table.Add(new XElement("Row", widths.Select((_, c) => new XElement("Cell", Block($"r{r}c{c}")))));

            return DocumentXml.Parse(new XElement("Document", Block("before"), table, Block("after")));
        }

        private static XElement Block(string text) => new XElement("Block", new XElement("Run", new XAttribute("Text", text)));

        // Opens the note from a file, so it has undo, with the caret focused; the table is its second entry.
        private static DocumentControl ShowNote(TestContext t, RichTextDocument fixture, out TableControl table) =>
            ShowNote(t, fixture, out table, out _);

        private static DocumentControl ShowNote(TestContext t, RichTextDocument fixture, out TableControl table, out RichTextDocument document)
        {
            DocumentEditorControl editor = new DocumentEditorControl
            {
                horizontalAlignment = HorizontalAlignment.Stretch,
                verticalAlignment = VerticalAlignment.Stretch
            };
            t.Show(editor);

            string path = Path.Combine(Path.GetTempPath(), $"aurora-table-{Guid.NewGuid():N}.axml");
            DocumentXml.Save(fixture, path);
            editor.LoadPath(path);
            File.Delete(path);

            document = editor.session.document;
            DocumentControl content = Content(editor);
            BlockControl first = (BlockControl)Views(content)[0];
            content.SetCaret(first, 0);
            editor.FocusCaret();

            table = (TableControl)Views(content)[1];
            return content;
        }

        private static List<BlockControl> Blocks(TableControl table, int cell) =>
            table.Cells()[cell].children.OfType<BlockControl>().ToList();

        private static BlockControl Cell(TableControl table, int cell, int block = 0) => Blocks(table, cell)[block];
        #endregion

        #region ---- pictures ----
        [A_XSDActionDependency("TextInput.PictureInline", "Test")]
        private static IEnumerator<int> PictureInline(TestContext t)
        {
            string dir = PictureFolder();
            string small = PicturePng(Path.Combine(dir, "small.png"), 50, 40);
            string wide = PicturePng(Path.Combine(dir, "wide.png"), 4000, 100);
            RichTextDocument fixture = DocumentXml.Parse(new XElement("Document",
                new XElement("Block", RunX("abc"), PictureRun(small, ("Width", "100"), ("Height", "80")), RunX("def")),
                new XElement("Block", RunX("abc"), PictureRun(wide))));
            DocumentEditorControl editor = ShowFixture(t, fixture);
            yield return 2;

            List<BlockControl> p = Paragraphs(editor);
            float advance = p[0].CaretAt(4).x - p[0].CaretAt(3).x;
            float zoom = advance / 100f;
            t.Check(advance > 0f && MathF.Abs(p[0].Lines[0].ascent - 80f * zoom) < 0.01f,
                $"an inline picture advances its width and its height stands on the baseline (advance {advance}, ascent {p[0].Lines[0].ascent})");
            t.Check(p[0].Lines.Count == 1, "a picture that fits stays on the line");

            IReadOnlyList<TextLine> lines = p[1].Lines;
            t.Check(lines.Count == 2 && lines[0].segments.Sum(s => s.charCount) == 3 && lines[1].segments[0].charStart == 3,
                "a picture that does not fit breaks before itself, leaving the word in front of it");
            t.Check(lines.Count == 2 && lines[1].width <= p[1].arrangedRect.width,
                "an unsized picture is fitted to the column");

            Directory.Delete(dir, true);
        }

        [A_XSDActionDependency("TextInput.PictureEditUndo", "Test")]
        private static IEnumerator<int> PictureEditUndo(TestContext t)
        {
            DocumentEditorControl editor = ShowParagraphs(t, "ab");
            DocumentControl content = Content(editor);
            content.SetCaret(Paragraphs(editor)[0], 2);
            yield return 2;

            string attachments = Path.Combine(Path.GetDirectoryName(editor.session.path)!, "attachments");
            editor.PasteImage(Solid(30, 20, Rgba(200, 80, 40)));
            yield return 2;

            BlockControl p = Paragraphs(editor)[0];
            string? saved = p.spans.FirstOrDefault(s => s.IsPicture).imageSource;
            t.Check(p.text == "ab" + BlockControl.PictureChar && PictureCount(p) == 1, "a pasted picture is one character with a picture span");
            t.Check(saved != null && File.Exists(saved) && Path.GetDirectoryName(saved) == attachments, "the pasted picture is saved under attachments");

            yield return t.Type("x");
            t.Check(p.text == "ab" + BlockControl.PictureChar + "x" && PictureCount(p) == 1, "typing after a picture at the block's end starts a text span");
            content.SetCaret(p, 2);
            yield return t.Type("y");
            t.Check(p.text == "aby" + BlockControl.PictureChar + "x" && PictureCount(p) == 1, "typing before a picture extends the text in front of it");

            string before = Unstamped(editor.session.document).ToString();
            content.SetCaret(p, 1);
            content.SetCaret(p, 5, true);
            yield return t.Key(Keys.Delete);
            p = Paragraphs(editor)[0];
            t.Check(p.text == "a" && PictureCount(p) == 0, "a range delete takes the picture with it");

            yield return t.Key(Keys.Z, Keys.LeftControl);
            p = Paragraphs(editor)[0];
            t.Check(Unstamped(editor.session.document).ToString() == before && PictureCount(p) == 1, "undo puts the picture back exactly");

            if (saved != null) File.Delete(saved);
            if (Directory.Exists(attachments) && !Directory.EnumerateFileSystemEntries(attachments).Any()) Directory.Delete(attachments);
        }

        [A_XSDActionDependency("TextInput.PictureRoundTrip", "Test")]
        private static IEnumerator<int> PictureRoundTrip(TestContext t)
        {
            string dir = PictureFolder();
            string picture = PicturePng(Path.Combine(dir, "attachments", "my pic.png"), 30, 20);

            foreach (string extension in new[] { ".axml", ".md" })
            {
                RichTextDocument fixture = DocumentXml.Parse(new XElement("Document",
                    new XElement("Block", RunX("a"), PictureRun(picture, ("Width", "120")))));
                string path = Path.Combine(dir, "note" + extension);
                fixture.Save(path);

                string written = File.ReadAllText(path);
                t.Check(written.Contains(extension == ".md" ? "![|120](attachments/my%20pic.png)" : "Image=\"attachments/my pic.png\""),
                    $"{extension} writes the picture relative to the note: {written}");

                RichTextDocument back = RichTextDocument.Load(path);
                StyleSpan? span = back.blocks.OfType<NoteBlock>().First().run.spans.Where(s => s.IsPicture).Cast<StyleSpan?>().FirstOrDefault();
                t.Check(span is { count: 1, imageWidth: 120f } && string.Equals(span.Value.imageSource, picture, StringComparison.OrdinalIgnoreCase),
                    $"{extension} reads it back as the same picture span");
            }

            RichTextDocument plain = DocumentXml.Parse(new XElement("Document",
                new XElement("Block", RunX("a"), PictureRun(picture))));
            string txt = Path.Combine(dir, "note.txt");
            plain.Save(txt);
            t.Check(File.ReadAllText(txt) == "a", "plain text drops the picture");

            Directory.Delete(dir, true);
            yield break;
        }

        [A_XSDActionDependency("TextInput.PictureResize", "Test")]
        private static IEnumerator<int> PictureResize(TestContext t)
        {
            string dir = PictureFolder();
            string small = PicturePng(Path.Combine(dir, "small.png"), 50, 40);
            string wide = PicturePng(Path.Combine(dir, "wide.png"), 4000, 100);
            RichTextDocument fixture = DocumentXml.Parse(new XElement("Document",
                new XElement("Block", PictureRun(wide)),
                new XElement("Block", RunX("ab"), PictureRun(small, ("Width", "100")))));
            DocumentEditorControl editor = ShowFixture(t, fixture);
            DocumentControl content = Content(editor);
            yield return 2;

            List<BlockControl> p = Paragraphs(editor);
            yield return t.Click(p[0]);
            t.Check(content.HasSelection && content.caretBlock == p[0] && content.caretOffset == 1, "a click on a picture selects it");
            t.Check(Handles(content).Count(h => h.arrangedRect.width > 0f) == 8, "a selected picture shows eight handles");

            content.SetCaret(p[1], 2);
            content.SetCaret(p[1], 3, true);
            yield return 2;
            p[1].PictureBox(2, out LayoutRect box);
            float zoom = box.width / 100f;

            DocumentControl.PictureHandle corner = Handles(content).First(h => h.right && h.bottom);
            yield return t.Drag(corner, Centre(corner) + new Vector2(50f * zoom, 0f));
            t.Check(Stored(p[1]) == new Vector2(150f, 0f), $"a corner keeps the aspect and writes the width only: {Stored(p[1])}");
            p[1].PictureBox(2, out box);
            t.Check(MathF.Abs(box.height - 120f * zoom) < 0.5f, $"and the height follows it: {box.height}");

            DocumentControl.PictureHandle side = Handles(content).First(h => h.right && !h.top && !h.bottom);
            yield return t.Drag(side, Centre(side) + new Vector2(20f * zoom, 0f));
            t.Check(Stored(p[1]) == new Vector2(170f, 120f), $"a side stretches one axis and writes both: {Stored(p[1])}");

            yield return t.Key(Keys.Z, Keys.LeftControl);
            t.Check(Stored(Paragraphs(editor)[1]) == new Vector2(150f, 0f), "undo takes back the side drag");
            yield return t.Key(Keys.Z, Keys.LeftControl);
            t.Check(Stored(Paragraphs(editor)[1]) == new Vector2(100f, 0f), "and then the corner drag");
            t.Check(content.HasSelection && content.caretOffset == 3, "undo leaves the picture selected");

            Directory.Delete(dir, true);
        }

        [A_XSDActionDependency("TextInput.PictureHandlesDraw", "Test")]
        private static IEnumerator<int> PictureHandlesDraw(TestContext t)
        {
            string dir = PictureFolder();
            string picture = PicturePng(Path.Combine(dir, "picture.png"), 60, 40);
            RichTextDocument fixture = DocumentXml.Parse(new XElement("Document",
                new XElement("Block", RunX("ab "), PictureRun(picture), RunX(" cd"))));
            DocumentEditorControl editor = ShowFixture(t, fixture);
            DocumentControl content = Content(editor);
            BlockControl p = Paragraphs(editor)[0];
            content.SetCaret(p, 3);
            content.SetCaret(p, 4, true);
            yield return 2;

            yield return t.Golden("Selected", editor);
            Directory.Delete(dir, true);
        }

        [A_XSDActionDependency("TextInput.PictureWrapLayout", "Test")]
        private static IEnumerator<int> PictureWrapLayout(TestContext t)
        {
            string dir = PictureFolder();
            string picture = PicturePng(Path.Combine(dir, "square.png"), 100, 100);
            string words = string.Join(" ", Enumerable.Repeat("lorem ipsum dolor", 60));

            foreach ((string mode, bool right) in new[] { ("Square", false), ("Square", true), ("TopAndBottom", false), ("Behind", false), ("InFront", false) })
            {
                DocumentEditorControl editor = ShowFixture(t, DocumentXml.Parse(new XElement("Document",
                    new XElement("Block", PictureRun(picture, ("Width", "100"), ("Height", "100"), ("Wrap", mode)), RunX(words)))));
                yield return 2;

                DocumentControl content = Content(editor);
                BlockControl p = Paragraphs(editor)[0];
                float z = content.zoom;
                float column = p.arrangedRect.width;
                if (right)
                {
                    p.SetPicture(0, new StyleSpan { imageWidth = 100f, imageHeight = 100f, wrap = PictureWrap.Square, imageX = column / z - 100f });
                    yield return 2;
                }

                IReadOnlyList<TextLine> lines = p.Lines;
                float edge = 108f * z;
                List<TextLine> beside = lines.Where(l => l.top < edge).ToList();
                List<TextLine> under = lines.Where(l => l.top >= edge).ToList();

                if (mode == "Square" && !right)
                    t.Check(beside.Count > 0 && beside.All(l => l.left >= edge - 0.5f) && under.Count > 0 && under.All(l => l.left == 0f),
                        "Square on the left indents the lines beside it and not the ones below");
                else if (mode == "Square")
                    t.Check(beside.Count > 0 && beside.All(l => l.left == 0f && l.width <= column - edge + p.fontSize * 0.35f * z),
                        $"Square on the right narrows the lines beside it, a trailing space allowed to hang: {string.Join(" ", lines.Select(l => $"{l.top}:{l.width}"))}");
                else if (mode == "TopAndBottom")
                    t.Check(lines[0].top >= edge - 0.5f, $"Top and bottom moves the first line below the picture: {lines[0].top}");
                else
                    t.Check(lines[0].top == 0f && lines.All(l => l.left == 0f), $"{mode} leaves the lines where they were");
            }

            Directory.Delete(dir, true);
        }

        [A_XSDActionDependency("TextInput.PictureFloatEdit", "Test")]
        private static IEnumerator<int> PictureFloatEdit(TestContext t)
        {
            string dir = PictureFolder();
            string picture = PicturePng(Path.Combine(dir, "pic.png"), 50, 40);
            DocumentEditorControl editor = ShowFixture(t, DocumentXml.Parse(new XElement("Document",
                new XElement("Block", RunX("ab"), PictureRun(picture, ("Width", "100"))),
                Block(string.Join(" ", Enumerable.Repeat("lorem ipsum", 40))))));
            DocumentControl content = Content(editor);
            yield return 2;

            BlockControl p = Paragraphs(editor)[0];
            content.SetCaret(p, 0);
            content.PicturePressed(p, 2, PointerEvent.rightButton);
            t.Check(content.HasSelection && content.caretOffset == 3, "a right press on a picture selects it");

            editor.SetPictureWrap(PictureWrap.Square);
            yield return 2;
            StyleSpan span = Paragraphs(editor)[0].spans.First(s => s.IsPicture);
            t.Check(span.wrap == PictureWrap.Square && span.imageX > 0f && span.imageY >= 0f,
                $"Square floats the picture where it stood inline: {span.imageX}, {span.imageY}");
            t.Check(Handles(content).Count(h => h.arrangedRect.width > 0f) == 8, "the floating picture keeps its handles");

            yield return t.Key(Keys.Z, Keys.LeftControl);
            span = Paragraphs(editor)[0].spans.First(s => s.IsPicture);
            t.Check(span.wrap == PictureWrap.Inline && span.imageX == 0f, "undo puts it back in line");

            foreach (string extension in new[] { ".axml", ".md" })
            {
                RichTextDocument fixture = DocumentXml.Parse(new XElement("Document", new XElement("Block",
                    RunX("a"), PictureRun(picture, ("Width", "100"), ("Wrap", "Square"), ("X", "10"), ("Y", "20")))));
                string path = Path.Combine(dir, "note" + extension);
                fixture.Save(path);

                string written = File.ReadAllText(path);
                t.Check(extension == ".axml" || written.Contains("data-wrap=\"square\" data-x=\"10\" data-y=\"20\""),
                    $"Markdown writes a floating picture as <img>: {written}");

                RichTextDocument back = RichTextDocument.Load(path);
                StyleSpan read = back.blocks.OfType<NoteBlock>().First().run.spans.First(s => s.IsPicture);
                t.Check(read.wrap == PictureWrap.Square && read.imageX == 10f && read.imageY == 20f && read.imageWidth == 100f,
                    $"{extension} reads the wrap and offset back");
            }

            Directory.Delete(dir, true);
        }

        [A_XSDActionDependency("TextInput.PictureWrapDraws", "Test")]
        private static IEnumerator<int> PictureWrapDraws(TestContext t)
        {
            string dir = PictureFolder();
            string words = string.Join(" ", Enumerable.Repeat("lorem ipsum dolor sit", 24));
            (string mode, uint colour, string x)[] modes =
            {
                ("Square", Rgba(200, 80, 40), "0"), ("TopAndBottom", Rgba(40, 140, 200), "120"),
                ("Behind", Rgba(240, 200, 60), "60"), ("InFront", Rgba(60, 160, 80), "200")
            };

            foreach (string mode in new[] { "Pageless", "Paged" })
            {
                XElement document = new XElement("Document",
                    new XElement("DocumentLayout", new XElement("Page", new XAttribute("Mode", mode))));
                foreach ((string wrap, uint colour, string x) in modes)
                {
                    string path = Path.Combine(dir, wrap + ".png");
                    Solid(80, 60, colour).SavePng(path);
                    document.Add(new XElement("Block", PictureRun(path, ("Wrap", wrap), ("X", x)), RunX(words)));
                }
                document.Add(new XElement("Block", RunX("inline "), PictureRun(Path.Combine(dir, "Square.png"), ("Width", "40")), RunX(" after")));

                DocumentEditorControl editor = ShowFixture(t, DocumentXml.Parse(document));
                yield return 2;
                t.Check(Content(editor).page.mode.ToString() == mode, $"the note is {mode}");
                if (mode == "Paged")
                {
                    BlockControl last = Paragraphs(editor)[^1];
                    Content(editor).SetCaret(last, last.Length);
                    yield return t.Key(Keys.Left);
                    yield return t.Key(Keys.Right);
                    yield return 2;
                }
                yield return t.Golden(mode);
            }

            Directory.Delete(dir, true);
        }

        [A_XSDActionDependency("TextInput.PictureTightLayout", "Test")]
        private static IEnumerator<int> PictureTightLayout(TestContext t)
        {
            string dir = PictureFolder();
            string path = Path.Combine(dir, "half.png");
            HalfPng(path);

            DocumentEditorControl editor = ShowFixture(t, DocumentXml.Parse(new XElement("Document", new XElement("Block",
                PictureRun(path, ("Width", "100"), ("Height", "100"), ("Wrap", "Tight")), RunX(string.Join(" ", Enumerable.Repeat("lorem ipsum dolor", 60)))))));
            yield return 2;

            float z = Content(editor).zoom;
            List<TextLine> beside = Paragraphs(editor)[0].Lines.Where(l => l.top < 100f * z).ToList();
            t.Check(beside.Count > 0 && beside.All(l => l.left >= 58f * z - 0.5f && l.left < 108f * z - 0.5f),
                $"Tight wraps the opaque half, not the box: {string.Join(" ", beside.Select(l => l.left))}");

            Directory.Delete(dir, true);
        }

        [A_XSDActionDependency("TextInput.PictureFloatMove", "Test")]
        private static IEnumerator<int> PictureFloatMove(TestContext t)
        {
            string dir = PictureFolder();
            string picture = PicturePng(Path.Combine(dir, "pic.png"), 100, 100);
            string words = string.Join(" ", Enumerable.Repeat("lorem ipsum dolor", 40));
            DocumentEditorControl editor = ShowFixture(t, DocumentXml.Parse(new XElement("Document",
                Block(words),
                new XElement("Block", PictureRun(picture, ("Width", "100"), ("Height", "100"), ("Wrap", "Square")), RunX(words)))));
            DocumentControl content = Content(editor);
            yield return 2;

            float z = content.zoom;
            DocumentControl.FloatingPicture view = content.children.OfType<DocumentControl.FloatingPicture>().First(v => v.arrangedRect.width > 0f);
            yield return t.Click(view);
            t.Check(content.HasSelection && content.caretBlock == Paragraphs(editor)[1], "a click selects the floating picture");

            yield return t.Drag(view, Centre(view) + new Vector2(40f * z, 30f * z));
            StyleSpan span = Paragraphs(editor)[1].spans.First(s => s.IsPicture);
            t.Check(span.imageX == 40f && span.imageY == 30f, $"a drag moves it within its paragraph: {span.imageX}, {span.imageY}");

            List<BlockControl> p = Paragraphs(editor);
            float rise = view.arrangedRect.y - p[1].arrangedRect.y + 60f * z;
            yield return t.Drag(view, Centre(view) - new Vector2(0f, rise));
            p = Paragraphs(editor);
            t.Check(p[0].spans.Count(s => s.IsPicture) == 1 && p[0].text.StartsWith(BlockControl.PictureChar) && !p[1].spans.Any(s => s.IsPicture),
                "dropped beside the paragraph above, the picture is re-anchored to its start");
            StyleSpan moved = p[0].spans.FirstOrDefault(s => s.IsPicture);
            t.Check(moved.imageY >= 0f && moved.imageX == 40f, $"with its offset taken from the new paragraph: {moved.imageX}, {moved.imageY}");

            yield return t.Key(Keys.Z, Keys.LeftControl);
            p = Paragraphs(editor);
            span = p[1].spans.FirstOrDefault(s => s.IsPicture);
            t.Check(!p[0].spans.Any(s => s.IsPicture) && span.imageX == 40f && span.imageY == 30f, "undo puts it back in its paragraph as one step");
            yield return t.Key(Keys.Z, Keys.LeftControl);
            span = Paragraphs(editor)[1].spans.First(s => s.IsPicture);
            t.Check(span.imageX == 0f && span.imageY == 0f, "and the first move back too");

            Directory.Delete(dir, true);
        }

        [A_XSDActionDependency("TextInput.PictureTightDraws", "Test")]
        private static IEnumerator<int> PictureTightDraws(TestContext t)
        {
            string dir = PictureFolder();
            string path = Path.Combine(dir, "circle.png");
            Image circle = new Image(120, 120);
            Span<uint> circlePixels = MemoryMarshal.Cast<byte, uint>(circle.Pixels.AsSpan());
            for (int y = 0; y < 120; y++)
                for (int x = 0; x < 120; x++)
                    if ((x - 59.5f) * (x - 59.5f) + (y - 59.5f) * (y - 59.5f) <= 60f * 60f) circlePixels[y * 120 + x] = Rgba(200, 80, 40);
            circle.SavePng(path);

            ShowFixture(t, DocumentXml.Parse(new XElement("Document", new XElement("Block",
                PictureRun(path, ("Wrap", "Tight"), ("X", "200")), RunX(string.Join(" ", Enumerable.Repeat("lorem ipsum dolor sit", 30)))))));
            yield return 2;
            yield return t.Golden("Circle");

            Directory.Delete(dir, true);
        }

        [A_XSDActionDependency("TextInput.PictureRotateRoundTrip", "Test")]
        private static IEnumerator<int> PictureRotateRoundTrip(TestContext t)
        {
            string dir = PictureFolder();
            string picture = PicturePng(Path.Combine(dir, "pic.png"), 30, 20);

            foreach (string extension in new[] { ".axml", ".md" })
            {
                RichTextDocument fixture = DocumentXml.Parse(new XElement("Document",
                    new XElement("Block", RunX("a"), PictureRun(picture, ("Width", "120"), ("Rotation", "30"))),
                    new XElement("Block", PictureRun(picture, ("Width", "60"), ("Wrap", "Square"), ("X", "10"), ("Y", "5"),
                        ("Rotation", "45"), ("Collision", "Shape")), RunX("b"))));
                string path = Path.Combine(dir, "note" + extension);
                fixture.Save(path);

                string written = File.ReadAllText(path);
                if (extension == ".md")
                    t.Check(written.Contains("<img src=\"pic.png\" width=\"120\" data-rotate=\"30\">")
                            && written.Contains("data-collision=\"shape\" data-rotate=\"45\""),
                        $".md writes a turned picture as <img> with its turn and collision: {written}");

                RichTextDocument back = RichTextDocument.Load(path);
                List<StyleSpan> spans = back.blocks.OfType<NoteBlock>().SelectMany(b => b.run.spans.Where(s => s.IsPicture)).ToList();
                t.Check(spans.Count == 2 && spans[0] is { wrap: PictureWrap.Inline, imageRotation: 30f, imageWidth: 120f }
                        && spans[1] is { wrap: PictureWrap.Square, imageRotation: 45f, collision: PictureCollision.Shape, imageX: 10f, imageY: 5f },
                    $"{extension} reads back the turn, the wrap and the collision");
            }

            Directory.Delete(dir, true);
            yield break;
        }

        [A_XSDActionDependency("TextInput.PictureRotateInline", "Test")]
        private static IEnumerator<int> PictureRotateInline(TestContext t)
        {
            string dir = PictureFolder();
            string picture = PicturePng(Path.Combine(dir, "pic.png"), 100, 40);
            DocumentEditorControl editor = ShowFixture(t, DocumentXml.Parse(new XElement("Document",
                new XElement("Block", RunX("ab "), PictureRun(picture, ("Width", "100"), ("Height", "40"), ("Rotation", "90")), RunX(" cd")))));
            yield return 2;

            BlockControl p = Paragraphs(editor)[0];
            float z = Content(editor).zoom;
            p.PictureBox(3, out LayoutRect box);
            t.Check(MathF.Abs(box.width - 40f * z) < 0.5f && MathF.Abs(box.height - 100f * z) < 0.5f,
                $"a quarter-turned inline picture takes its turned box in the line: {box.width} x {box.height}");
            p.PictureFrame(3, out LayoutRect rect, out _);
            t.Check(MathF.Abs(rect.width - 100f * z) < 0.5f && MathF.Abs(rect.height - 40f * z) < 0.5f,
                $"and keeps its own size for the frame: {rect.width} x {rect.height}");

            Content(editor).SetCaret(p, 3);
            Content(editor).SetCaret(p, 4, true);
            yield return 2;
            yield return t.Golden("Selected", editor);

            Directory.Delete(dir, true);
        }

        [A_XSDActionDependency("TextInput.PictureRotateLayout", "Test")]
        private static IEnumerator<int> PictureRotateLayout(TestContext t)
        {
            string dir = PictureFolder();
            string picture = PicturePng(Path.Combine(dir, "square.png"), 100, 100);
            string words = string.Join(" ", Enumerable.Repeat("lorem ipsum dolor", 60));
            float[] firstLeft = new float[2];

            for (int i = 0; i < 2; i++)
            {
                string collision = i == 0 ? "Box" : "Shape";
                DocumentEditorControl editor = ShowFixture(t, DocumentXml.Parse(new XElement("Document", new XElement("Block",
                    PictureRun(picture, ("Width", "100"), ("Height", "100"), ("Wrap", "Square"), ("Rotation", "45"), ("Collision", collision)),
                    RunX(words)))));
                yield return 2;

                float z = Content(editor).zoom;
                IReadOnlyList<TextLine> lines = Paragraphs(editor)[0].Lines;
                firstLeft[i] = lines[0].left / z;
                if (i == 0)
                    t.Check(lines.Where(l => l.top < 100f * z).All(l => l.left >= 128.5f * z - 0.5f),
                        $"Box wraps the 45-degree picture's bounding box: {string.Join(" ", lines.Select(l => l.left))}");
            }
            t.Check(firstLeft[1] < firstLeft[0] - 10f && firstLeft[1] > 70f,
                $"Shape lets the first line in closer, beside the diamond's tip: {firstLeft[1]} vs {firstLeft[0]}");

            string path = Path.Combine(dir, "half.png");
            HalfPng(path);

            DocumentEditorControl tight = ShowFixture(t, DocumentXml.Parse(new XElement("Document", new XElement("Block",
                PictureRun(path, ("Width", "100"), ("Height", "100"), ("Wrap", "Tight"), ("Rotation", "90")), RunX(words)))));
            yield return 2;

            float tz = Content(tight).zoom;
            IReadOnlyList<TextLine> tl = Paragraphs(tight)[0].Lines;
            List<TextLine> top = tl.Where(l => l.top + l.height < 50f * tz).ToList();
            List<TextLine> lower = tl.Where(l => l.top > 52f * tz && l.top < 100f * tz).ToList();
            t.Check(top.Count > 0 && top.All(l => l.left >= 108f * tz - 0.5f) && lower.Count > 0 && lower.All(l => l.left == 0f),
                $"Tight turned a quarter wraps the opaque half now on top: {string.Join(" ", tl.Select(l => $"{l.top}:{l.left}"))}");

            yield return t.Golden("Tight");
            Directory.Delete(dir, true);
        }

        [A_XSDActionDependency("TextInput.PictureRotate", "Test")]
        private static IEnumerator<int> PictureRotate(TestContext t)
        {
            string dir = PictureFolder();
            string picture = PicturePng(Path.Combine(dir, "pic.png"), 100, 100);
            string words = string.Join(" ", Enumerable.Repeat("lorem ipsum dolor", 40));
            DocumentEditorControl editor = ShowFixture(t, DocumentXml.Parse(new XElement("Document",
                Block(words),
                new XElement("Block", PictureRun(picture, ("Width", "100"), ("Height", "100"), ("Wrap", "Square"), ("X", "100")), RunX(words)))));
            DocumentControl content = Content(editor);
            yield return 2;

            DocumentControl.FloatingPicture view = content.children.OfType<DocumentControl.FloatingPicture>().First(v => v.arrangedRect.width > 0f);
            yield return t.Click(view);
            DocumentControl.PictureRotator ring = content.children.OfType<DocumentControl.PictureRotator>().First();
            t.Check(ring.arrangedRect.width > 0f, "a selected picture shows the rotate ring");

            Vector2 c = Centre(ring);
            float r = ring.arrangedRect.width * 0.5f - 1f;
            yield return t.Drag(ring, c - new Vector2(0f, r), c + new Vector2(r, 0f));
            t.Check(PictureSpan(editor).imageRotation == 90f, $"dragging the ring a quarter clockwise turns it 90: {PictureSpan(editor).imageRotation}");

            c = Centre(ring);
            r = ring.arrangedRect.width * 0.5f - 1f;
            float a = 50f * MathF.PI / 180f;
            yield return t.Drag(ring, c - new Vector2(0f, r), c + new Vector2(MathF.Sin(a), -MathF.Cos(a)) * r, 8, Keys.LeftShift);
            t.Check(PictureSpan(editor).imageRotation == 135f, $"Shift snaps the turn to 15 degrees: {PictureSpan(editor).imageRotation}");
            t.Check(Paragraphs(editor)[0].spans.Any(s => s.IsPicture),
                "turned so its box rises above its paragraph, the picture is re-anchored to the one above");

            editor.SetPictureWrap(PictureWrap.TopAndBottom);
            yield return 2;
            t.Check(PictureSpan(editor) is { wrap: PictureWrap.TopAndBottom, imageRotation: 135f }, "a wrap change keeps the turn");

            yield return t.Key(Keys.Z, Keys.LeftControl);
            yield return t.Key(Keys.Z, Keys.LeftControl);
            t.Check(PictureSpan(editor).imageRotation == 90f && Paragraphs(editor)[1].spans.Any(s => s.IsPicture),
                "undo takes back the snapped turn and its re-anchoring as one step");
            yield return t.Key(Keys.Z, Keys.LeftControl);
            t.Check(PictureSpan(editor).imageRotation == 0f, "and then the first turn");

            Directory.Delete(dir, true);
        }

        private static StyleSpan PictureSpan(DocumentEditorControl editor) =>
            Paragraphs(editor).SelectMany(p => p.spans).First(s => s.IsPicture && s.count > 0);

        [A_XSDActionDependency("TextInput.PictureRotateDraws", "Test")]
        private static IEnumerator<int> PictureRotateDraws(TestContext t)
        {
            string dir = PictureFolder();
            string picture = PicturePng(Path.Combine(dir, "pic.png"), 120, 60);
            string words = string.Join(" ", Enumerable.Repeat("lorem ipsum dolor sit", 30));

            foreach (string collision in new[] { "Box", "Shape" })
            {
                ShowFixture(t, DocumentXml.Parse(new XElement("Document", new XElement("Block",
                    PictureRun(picture, ("Wrap", "Square"), ("X", "200"), ("Y", "20"), ("Rotation", "30"), ("Collision", collision)), RunX(words)))));
                yield return 2;
                yield return t.Golden(collision);
            }

            Directory.Delete(dir, true);
        }

        private static List<DocumentControl.PictureHandle> Handles(DocumentControl content) =>
            content.children.OfType<DocumentControl.PictureHandle>().ToList();

        private static Vector2 Centre(Control control) =>
            new Vector2(control.arrangedRect.x + control.arrangedRect.width * 0.5f, control.arrangedRect.y + control.arrangedRect.height * 0.5f);

        private static Vector2 Stored(BlockControl block)
        {
            StyleSpan span = block.spans.First(s => s.IsPicture);
            return new Vector2(span.imageWidth, span.imageHeight);
        }

        private static XElement PictureRun(string source, params (string name, string value)[] attributes)
        {
            XElement run = new XElement("Run", new XAttribute("Image", source));
            foreach ((string name, string value) in attributes)
                run.SetAttributeValue(name, value);
            return run;
        }

        private static string PictureFolder()
        {
            string dir = Path.Combine(Path.GetTempPath(), $"aurora-pictures-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string PicturePng(string path, int width, int height)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Solid(width, height, Rgba(200, 80, 40)).SavePng(path);
            return path;
        }

        // A picture of one colour; the colour packed by Rgba.
        private static Image Solid(int width, int height, uint colour)
        {
            Image image = new Image(width, height);
            MemoryMarshal.Cast<byte, uint>(image.Pixels.AsSpan()).Fill(colour);
            return image;
        }

        // One RGBA8 pixel as a little-endian uint.
        private static uint Rgba(byte r, byte g, byte b, byte a = 255) => (uint)(r | g << 8 | b << 16 | a << 24);

        // The left half of a 100x100 picture filled, the right half transparent.
        private static void HalfPng(string path)
        {
            Image image = new Image(100, 100);
            Span<uint> pixels = MemoryMarshal.Cast<byte, uint>(image.Pixels.AsSpan());
            for (int y = 0; y < 100; y++)
                pixels.Slice(y * 100, 50).Fill(Rgba(40, 140, 200));
            image.SavePng(path);
        }

        private static int PictureCount(BlockControl block) => block.spans.Count(s => s.IsPicture && s.count > 0);
        #endregion
    }
}
