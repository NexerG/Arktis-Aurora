using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Testing;
using ArctisAurora.Core.UI;
using ArctisAurora.EngineWork;
using System.Numerics;
using System.Xml.Linq;

namespace Thorium.Tests
{
    internal static class SheetTests
    {
        [A_XSDActionDependency("Sheet.XmlRoundTrip", "Test")]
        private static IEnumerator<int> XmlRoundTrip(TestContext t)
        {
            XElement source = XElement.Parse(
                "<Sheet Name=\"Budget\">" +
                "<Page Name=\"One\"><Column At=\"B\" Width=\"140\" /><Row At=\"3\" Height=\"30\" />" +
                "<Layer Name=\"Values\"><Cell At=\"A1\">Item</Cell><Cell At=\"B1\">12.5</Cell><Cell At=\"AA100\">far</Cell></Layer>" +
                "<Layer Name=\"Notes\" Visible=\"false\"><Cell At=\"A1\">hidden</Cell></Layer>" +
                "<Chart Kind=\"Bar\" /></Page>" +
                "<Page Name=\"Two\"><Layer Name=\"Layer 1\" /></Page>" +
                "</Sheet>");

            SheetDocument sheet = SheetXml.Parse(source);
            SheetPage page = sheet.pages[0];

            t.Check(XNode.DeepEquals(source, SheetXml.ToXml(sheet)), $"the sheet writes back as it was read: {SheetXml.ToXml(sheet)}");
            t.Check(sheet.pages.Count == 2 && page.layers.Count == 2, "both pages and both layers are read");
            t.Check(page.layers[0].cells.Count == 3, "only the written cells are stored");
            t.Check(page.extra.Count == 1, "an element the reader does not know is kept");
            t.Check(page.Shown(0, 0) == "Item", "a hidden layer does not cover the one under it");
            t.Check(page.ColumnWidth(1) == 140f && page.ColumnLeft(2) == 240f, "a sized column moves the ones after it");
            t.Check(page.ColumnAt(239f) == 1 && page.ColumnAt(240f) == 2, "a position finds the column it is in");
            t.Check(page.RowTop(3) == 78f && page.RowAt(77f) == 2, "a sized row moves the ones below it");
            t.Check(SheetDocument.ColumnName(26) == "AA" && SheetDocument.ColumnName(701) == "ZZ", "columns past Z take two letters");
            t.Check(SheetDocument.TryParseAddress("AA100", out int row, out int column) && row == 99 && column == 26, "an address reads back to its cell");
            yield break;
        }

        [A_XSDActionDependency("Sheet.TypeEnterUndo", "Test")]
        private static IEnumerator<int> TypeEnterUndo(TestContext t)
        {
            string path = TempSheet(SheetDocument.Blank("Typing"));
            SheetEditorControl editor = ShowSheet(t, path);
            yield return 2;

            SheetControl grid = Grid(editor);
            LayoutRect b2 = grid.CellRect(1, 1);
            Vector2 at = new Vector2(b2.x + b2.width * 0.5f, b2.y + b2.height * 0.5f);
            yield return t.Drag(grid, at, at, 1);
            t.Check(editor.activeRow == 1 && editor.activeColumn == 1, "a click selects the cell under it");

            yield return t.Type("42");
            t.Check(editor.editing, "typing opens the cell for editing");

            yield return t.Key(Keys.Enter);
            yield return 2;
            SheetPage page = editor.document.pages[0];
            t.Check(!editor.editing && page.Shown(1, 1) == "42", $"Enter commits what was typed: {page.Shown(1, 1)}");
            t.Check(editor.activeRow == 2 && editor.activeColumn == 1, "Enter steps down");

            yield return t.Key(Keys.Z, Keys.LeftControl);
            t.Check(page.Shown(1, 1) == null, "undo empties the cell again");

            yield return t.Key(Keys.Right, Keys.LeftShift);
            t.Check(editor.anchorColumn == 1 && editor.activeColumn == 2, "Shift+Right extends the selection");

            yield return t.Key(Keys.F2);
            yield return t.Type("x");
            yield return t.Key(Keys.Tab);
            yield return 2;
            t.Check(page.Shown(2, 2) == "x" && editor.activeColumn == 3, "F2, typing and Tab commit and step right");

            t.Show(new StackPanelControl());
            File.Delete(path);
        }

        [A_XSDActionDependency("Sheet.PasteBlock", "Test")]
        private static IEnumerator<int> PasteBlock(TestContext t)
        {
            string path = TempSheet(SheetDocument.Blank("Paste"));
            SheetEditorControl editor = ShowSheet(t, path);
            yield return 2;

            editor.Select(1, 1, false);
            editor.Paste("a\tb\r\nc\td\r\ne\tf\r\n");
            yield return 2;

            SheetPage page = editor.document.pages[0];
            t.Check(page.Shown(1, 1) == "a" && page.Shown(3, 2) == "f" && page.layers[0].cells.Count == 6, "three rows of two land from the active cell");
            t.Check(editor.anchorRow == 1 && editor.anchorColumn == 1 && editor.activeRow == 3 && editor.activeColumn == 2, "the pasted block is selected");

            editor.Copy();
            t.Check(ClipboardText.Get() == "a\tb\r\nc\td\r\ne\tf", $"copy writes the selection as tab-separated rows: {ClipboardText.Get()}");

            editor.Undo();
            t.Check(page.layers[0].cells.Count == 0, "one undo takes back all six");

            t.Show(new StackPanelControl());
            File.Delete(path);
        }

        [A_XSDActionDependency("Sheet.ScrollKeepsControls", "Test")]
        private static IEnumerator<int> ScrollKeepsControls(TestContext t)
        {
            SheetDocument sheet = SheetDocument.Blank("Long");
            for (int r = 0; r < 10000; r++)
                sheet.pages[0].layers[0].Set(r, 0, "r" + r);
            string path = TempSheet(sheet);

            SheetEditorControl editor = ShowSheet(t, path);
            yield return 3;
            int before = Count(editor);

            editor.scroller.SetScrollOffset(new Vector2(0f, editor.document.pages[0].RowTop(5000)));
            yield return 3;
            int after = Count(editor);

            t.Check(before < 1000, $"only the cells in view have controls: {before}");
            t.Check(after == before, $"scrolling reuses them: {before} then {after}");
            t.Check(Labels(editor).Contains("r5000"), "the rows scrolled to are drawn");

            t.Show(new StackPanelControl());
            File.Delete(path);
        }

        [A_XSDActionDependency("Sheet.TabLifecycle", "Test")]
        private static IEnumerator<int> TabLifecycle(TestContext t)
        {
            string path = TempSheet(SheetDocument.Blank("Lifecycle"));
            string before = SessionLayout.scope;
            WorkspaceControl workspace = new WorkspaceControl
            {
                paneDocument = "tab-pane",
                defaultDocument = "tab-pane",
                horizontalAlignment = HorizontalAlignment.Stretch,
                verticalAlignment = VerticalAlignment.Stretch
            };
            t.Show(workspace);
            SessionLayout.scope = "sheet-a";
            workspace.LoadPane()!.AddChild(SessionLayout.tabFactory(path, false));
            yield return 2;

            SheetEditorControl editor = (SheetEditorControl)TabViewControl.FileEditorOf(TabViewControl.FindOpenDocument(path, out _))!;
            editor.Select(3, 2, false);
            editor.Paste("kept");
            editor.Select(4, 1, false);
            editor.Save();
            yield return 2;

            SessionLayout.ChangeScope("sheet-b");
            yield return 2;
            SessionLayout.ChangeScope("sheet-a");
            yield return 2;

            TabItemControl item = TabViewControl.FindOpenDocument(path, out TabViewControl view);
            editor = TabViewControl.FileEditorOf(item) as SheetEditorControl;
            t.Check(editor != null && editor.document.pages[0].Shown(3, 2) == "kept", "the sheet tab comes back with its scope");
            t.Check(editor != null && editor.activeRow == 4 && editor.activeColumn == 1, "and with its active cell");

            editor!.Paste("settled");
            NoteActions.SaveEdited();
            t.Check(SheetDocument.Load(path).pages[0].Shown(4, 1) == "settled", "the shutdown save writes a dirty sheet");

            editor.Select(5, 1, false);
            editor.TypeOver(new Queue<char>("typed"));
            yield return 2;
            editor.Save();
            t.Check(!editor.editing && SheetDocument.Load(path).pages[0].Shown(5, 1) == "typed", "saving commits an open cell edit");

            editor.Select(4, 1, false);
            editor.Paste("closed");
            view.CloseTab(item);
            yield return 2;
            t.Check(SheetDocument.Load(path).pages[0].Shown(4, 1) == "closed", "closing the tab writes the edit");

            view.AddChild(SessionLayout.tabFactory(path, false));
            yield return 2;
            item = TabViewControl.FindOpenDocument(path, out _);
            editor = (SheetEditorControl)TabViewControl.FileEditorOf(item)!;
            item.onRename!("Renamed sheet");
            string renamed = Path.Combine(Path.GetDirectoryName(path)!, "Renamed sheet" + SheetDocument.extension);
            t.Check(File.Exists(renamed) && !File.Exists(path), $"a rename keeps the sheet extension: {editor.path}");
            t.Check(editor.path == renamed && editor.document.name == "Renamed sheet", "the open tab follows the rename");

            SessionLayout.scope = before;
            t.Show(new StackPanelControl());
            File.Delete(renamed);
            File.Delete(path);
        }

        [A_XSDActionDependency("Sheet.GridDraws", "Test")]
        private static IEnumerator<int> GridDraws(TestContext t)
        {
            SheetDocument sheet = SheetDocument.Blank("Grid");
            SheetLayer layer = sheet.pages[0].layers[0];
            for (int r = 0; r < 20; r++)
                for (int c = 0; c < 10; c++)
                    layer.Set(r, c, c == 0 ? "Item " + (r + 1) : ((r + 1) * (c + 1)).ToString());
            layer.Set(0, 3, "a long caption cut at the cell");
            string path = TempSheet(sheet);

            SheetEditorControl editor = new SheetEditorControl
            {
                preferredWidth = 640f,
                preferredHeight = 360f,
                horizontalAlignment = HorizontalAlignment.Left,
                verticalAlignment = VerticalAlignment.Top
            };
            editor.LoadPath(path);
            t.Show(editor);
            editor.Select(1, 1, false);
            editor.Select(4, 3, true);
            yield return 3;

            yield return t.Golden("Grid", editor);

            t.Show(new StackPanelControl());
            File.Delete(path);
        }

        [A_XSDActionDependency("Sheet.FormulaEval", "Test")]
        private static IEnumerator<int> FormulaEval(TestContext t)
        {
            SheetDocument sheet = SheetDocument.Blank("Eval");
            SheetPage one = sheet.pages[0];
            SheetPage two = SheetPage.Blank("My page");
            sheet.pages.Add(two);
            SheetLayer cells = one.layers[0];
            cells.Set(0, 0, "2");
            cells.Set(1, 0, "3");
            cells.Set(2, 0, "text");
            cells.Set(5, 5, "1.50");
            two.layers[0].Set(0, 0, "10");
            two.layers[0].Set(1, 0, "5");
            SheetBook.Register(sheet);

            string? Eval(string formula)
            {
                cells.Set(9, 9, formula);
                SheetBook.calc.Changed(one, new[] { (9, 9) });
                return SheetBook.calc.Value(one, 9, 9).Display();
            }

            void Expect(string formula, string shown) =>
                t.Check(Eval(formula) == shown, $"{formula} shows {shown}: {Eval(formula)}");

            Expect("=1+2*3", "7");
            Expect("=(1+2)*3", "9");
            Expect("=-2^2", "4");
            Expect("=2^3^2", "64");
            Expect("=2^-1", "0.5");
            Expect("=0.1+0.2", "0.3");
            Expect("=A1+A2*2", "8");
            Expect("=Z99", "0");
            Expect("=SUM(A1:A3)", "5");
            Expect("=sum(A1, 4, 'My page'!A1:A2)", "21");
            Expect("= 'My page'!A1 * 2", "20");
            Expect("=A3+1", SheetFormula.value);
            Expect("=A1:A2", SheetFormula.value);
            Expect("=1/0", SheetFormula.div0);
            Expect("=1/0+FOO()", SheetFormula.div0);
            Expect("=SUM(A1, 1/0)", SheetFormula.div0);
            Expect("=FOO(1)", SheetFormula.name);
            Expect("=Nope!A1", SheetFormula.reference);
            Expect("=XFE1", SheetFormula.reference);
            Expect("=1+", SheetFormula.parse);
            Expect("=10^400", SheetFormula.num);

            Expect("=1+2>2", "1");
            Expect("=1+2<2", "0");
            Expect("=A1<>2", "0");
            Expect("=A1>=2", "1");
            Expect("=A1<=1", "0");
            Expect("=A1=Z1+2", "1");
            Expect("=A3>A1", "1");
            Expect("=(A1>1)*10", "10");
            Expect("=1<1/0", SheetFormula.div0);
            Expect("=IF(A1>1, 10, 20)", "10");
            Expect("=if(A1>5, 10, 20)", "20");
            Expect("=IF(A1>5, 10)", "0");
            Expect("=IF(A1>1, 1, 1/0)", "1");
            Expect("=IF(A3, 1, 2)", SheetFormula.value);
            Expect("=IF(1)", SheetFormula.value);
            Expect("=MIN(A1:A3)", "2");
            Expect("=MAX(A1:A3, 7)", "7");
            Expect("=MIN(Z1:Z5)", "0");
            Expect("=AVERAGE(A1:A3)", "2.5");
            Expect("=AVERAGE(Z1:Z5)", SheetFormula.div0);
            Expect("=MAX(0, MIN(747, 747-0.49*(3000-1153)))", "0");
            Expect("=MAX(0, MIN(747, 747-0.49*(1500-1153)))", "576.97");
            Expect("=ROUNDUP(10/3, 0)", "4");
            Expect("=ROUNDUP(-10/3)", "-4");
            Expect("=ROUNDDOWN(-10/3)", "-3");
            Expect("=ROUNDUP(3)", "3");
            Expect("=ROUND(2.345, 2)", "2.35");
            Expect("=ROUND(-2.5)", "-3");
            Expect("=ROUND(1234, -2)", "1200");
            Expect("=ROUND()", SheetFormula.value);

            SheetValue typed = SheetBook.calc.Value(one, 5, 5);
            t.Check(typed.kind == SheetValueKind.Number && typed.Display() == "1.50", "a typed number keeps the text it was typed as");
            SheetBook.Unregister(sheet);
            yield break;
        }

        [A_XSDActionDependency("Sheet.FormulaRecalc", "Test")]
        private static IEnumerator<int> FormulaRecalc(TestContext t)
        {
            SheetDocument sheet = SheetDocument.Blank("Recalc");
            SheetPage one = sheet.pages[0];
            SheetPage two = SheetPage.Blank("My page");
            sheet.pages.Add(two);
            SheetLayer cells = one.layers[0];
            SheetLayer other = two.layers[0];
            SheetCalc calc = SheetBook.calc;

            string? Shown(SheetPage page, string address)
            {
                SheetDocument.TryParseAddress(address, out int row, out int column);
                return calc.Value(page, row, column).Display();
            }

            void Write(SheetPage page, params (string address, string? raw)[] writes)
            {
                List<(int, int)> changed = new List<(int, int)>();
                foreach ((string address, string? raw) in writes)
                {
                    SheetDocument.TryParseAddress(address, out int row, out int column);
                    page.layers[^1].Set(row, column, raw);
                    changed.Add((row, column));
                }
                calc.Changed(page, changed);
            }

            cells.Set(0, 0, "1");
            cells.Set(0, 1, "=A1+1");
            cells.Set(0, 2, "=B1*10");
            other.Set(0, 0, "10");
            cells.Set(0, 3, "='My page'!A1*2");
            SheetBook.Register(sheet);
            t.Check(Shown(one, "C1") == "20" && Shown(one, "D1") == "20", "a load evaluates every formula");

            Write(one, ("A1", "5"));
            t.Check(Shown(one, "C1") == "60", $"a chain follows its first cell: {Shown(one, "C1")}");

            Write(two, ("A1", "7"));
            t.Check(Shown(one, "D1") == "14", $"a reference into another page follows it: {Shown(one, "D1")}");

            Write(one, ("E1", "=F1"), ("F1", "=E1"), ("G1", "=E1+1"));
            t.Check(Shown(one, "E1") == SheetFormula.cycle && Shown(one, "F1") == SheetFormula.cycle, "two cells reading each other are a cycle");
            t.Check(Shown(one, "G1") == SheetFormula.cycle, "a cell reading a cycle shows the cycle");

            Write(one, ("F1", "3"));
            t.Check(Shown(one, "E1") == "3" && Shown(one, "G1") == "4", "breaking the cycle recovers both");

            Write(one, ("H1", "='My page'!B1"));
            Write(two, ("B1", "='Sheet 1'!H1"));
            t.Check(Shown(one, "H1") == SheetFormula.cycle && Shown(two, "B1") == SheetFormula.cycle, "a cycle across pages is found");

            Write(one, ("I1", "=I1+1"));
            t.Check(Shown(one, "I1") == SheetFormula.cycle, "a cell reading itself is a cycle");

            List<(int, int)> chain = new List<(int, int)>();
            cells.Set(0, 20, "1");
            for (int r = 1; r < 10000; r++)
            {
                cells.Set(r, 20, "=U" + r + "+1");
                chain.Add((r, 20));
            }
            calc.Changed(one, chain);
            t.Check(Shown(one, "U10000") == "10000", $"a 10,000-cell chain evaluates: {Shown(one, "U10000")}");

            Write(one, ("U1", "2"));
            t.Check(Shown(one, "U10000") == "10001", "and follows its first cell");
            SheetBook.Unregister(sheet);
            yield break;
        }

        [A_XSDActionDependency("Sheet.FormulaView", "Test")]
        private static IEnumerator<int> FormulaView(TestContext t)
        {
            string path = TempSheet(SheetDocument.Blank("Formulas"));
            SheetEditorControl editor = ShowSheet(t, path);
            yield return 2;

            editor.Select(0, 0, false);
            editor.Paste("3");
            SheetControl grid = Grid(editor);
            LayoutRect b1 = grid.CellRect(0, 1);
            Vector2 at = new Vector2(b1.x + b1.width * 0.5f, b1.y + b1.height * 0.5f);
            yield return t.Drag(grid, at, at, 1);
            yield return t.Type("=A1*2");
            yield return t.Key(Keys.Enter);
            yield return 2;

            SheetPage page = editor.document.pages[0];
            LabelControl? InB1() => Find<LabelControl>(editor, label => label.arrangedRect.width > 0f
                && label.arrangedRect.x >= b1.x && label.arrangedRect.Right <= b1.Right
                && label.arrangedRect.y >= b1.y && label.arrangedRect.Bottom <= b1.Bottom);

            t.Check(page.Shown(0, 1) == "=A1*2", $"the cell keeps the formula: {page.Shown(0, 1)}");
            LabelControl? value = InB1();
            t.Check(value?.text == "6", $"the grid shows its value: {value?.text}");
            t.Check(value != null && value.arrangedRect.x > b1.x + b1.width * 0.5f, "a computed number aligns right");

            editor.Select(0, 1, false);
            yield return t.Key(Keys.F2);
            TextBoxControl? field = Find<TextBoxControl>(editor, _ => true);
            t.Check(editor.editing && field?.text == "=A1*2", $"F2 opens the formula: {field?.text}");
            yield return t.Key(Keys.Escape);

            editor.Select(0, 0, false);
            editor.Paste("4");
            yield return 2;
            t.Check(InB1()?.text == "8", $"an edit recalculates what reads it: {InB1()?.text}");

            editor.Undo();
            yield return 2;
            t.Check(InB1()?.text == "6", $"undo recalculates too: {InB1()?.text}");

            editor.Select(0, 0, false);
            editor.Select(0, 1, true);
            editor.Copy();
            t.Check(ClipboardText.Get() == "3\t6", $"copy writes values: {ClipboardText.Get()}");

            editor.Save();
            t.Check(SheetDocument.Load(path).pages[0].Shown(0, 1) == "=A1*2", "the file keeps the formula");

            t.Show(new StackPanelControl());
            File.Delete(path);
        }

        [A_XSDActionDependency("Sheet.CrossFile", "Test")]
        private static IEnumerator<int> CrossFile(TestContext t)
        {
            string folder = TempFolder();
            Func<string?, string, string?>?before = SheetBook.findSheet;
            SheetBook.findSheet = FolderResolver(folder);

            SheetDocument budget = SheetDocument.Blank("Budget");
            budget.pages[0].name = "Expenses";
            budget.pages[0].layers[0].Set(0, 0, "10");
            budget.pages[0].layers[0].Set(0, 1, "=A1*2");
            string budgetPath = Path.Combine(folder, "Budget" + SheetDocument.extension);
            budget.Save(budgetPath);

            SheetDocument summary = SheetDocument.Blank("Summary");
            SheetLayer cells = summary.pages[0].layers[0];
            cells.Set(0, 0, "=[Budget]Expenses!B1+1");
            cells.Set(1, 0, "='[Budget]Expenses'!A1");
            cells.Set(2, 0, "=[Nope]Expenses!A1");
            cells.Set(3, 0, "=[Budget]Missing!A1");
            string summaryPath = Path.Combine(folder, "Summary" + SheetDocument.extension);
            summary.Save(summaryPath);

            StackPanelControl both = new StackPanelControl { orientation = StackPanelControl.Orientation.Horizontal };
            SheetEditorControl editor = new SheetEditorControl { preferredWidth = 400f, preferredHeight = 300f };
            editor.LoadPath(summaryPath);
            both.AddChild(editor);
            t.Show(both);
            yield return 2;
            SheetPage page = editor.document.pages[0];
            string? Shown(int row) => SheetBook.calc.Value(page, row, 0).Display();

            t.Check(Shown(0) == "21", $"a formula reads a sheet that is not open: {Shown(0)}");
            t.Check(Shown(1) == "10", $"the quoted form reads it too: {Shown(1)}");
            t.Check(Shown(2) == SheetFormula.reference && Shown(3) == SheetFormula.reference, "a missing file or page is #REF!");

            SheetEditorControl other = new SheetEditorControl { preferredWidth = 400f, preferredHeight = 300f };
            other.LoadPath(budgetPath);
            both.AddChild(other);
            yield return 2;
            t.Check(ReferenceEquals(other.document, SheetBook.Get(budgetPath)), "a second editor shares the loaded copy");
            other.Select(0, 0, false);
            other.Paste("5");
            yield return 2;
            t.Check(Shown(0) == "11", $"an edit in the other sheet reaches this one: {Shown(0)}");
            LayoutRect a1 = Grid(editor).CellRect(0, 0);
            t.Check(Find<LabelControl>(editor, label => label.text == "11" && label.arrangedRect.x >= a1.x && label.arrangedRect.Right <= a1.Right
                && label.arrangedRect.y >= a1.y && label.arrangedRect.Bottom <= a1.Bottom) != null, "and its grid redraws");
            t.Check(!editor.unsaved && other.unsaved, "only the edited sheet is unsaved");

            other.Select(0, 2, false);
            other.Paste("='[Summary]Sheet 1'!A5");
            editor.Select(4, 0, false);
            editor.Paste("=[Budget]Expenses!C1");
            t.Check(Shown(4) == SheetFormula.cycle && SheetBook.calc.Value(other.document.pages[0], 0, 2).Display() == SheetFormula.cycle,
                "two files reading each other are a cycle");

            t.Show(new StackPanelControl());
            SheetBook.findSheet = before;
            SheetBook.Deleted(budgetPath);
            SheetBook.Deleted(summaryPath);
            Directory.Delete(folder, true);
        }

        [A_XSDActionDependency("Sheet.RenameRewrites", "Test")]
        private static IEnumerator<int> RenameRewrites(TestContext t)
        {
            string folder = TempFolder();
            Func<string?, string, string?>?before = SheetBook.findSheet;
            SheetBook.findSheet = FolderResolver(folder);
            string PathOf(string name) => Path.Combine(folder, name + SheetDocument.extension);

            SheetDocument budget = SheetDocument.Blank("Budget");
            budget.pages[0].name = "Data";
            budget.pages[0].layers[0].Set(0, 0, "10");
            budget.Save(PathOf("Budget"));

            SheetDocument summary = SheetDocument.Blank("Summary");
            summary.pages[0].layers[0].Set(0, 0, "=[Budget]Data!A1*2");
            summary.pages[0].layers[0].Set(1, 0, "='[Budget]Data'!A1");
            summary.Save(PathOf("Summary"));

            SheetDocument unopened = SheetDocument.Blank("Unopened");
            unopened.pages[0].layers[0].Set(0, 0, "=[Budget]Data!A1+1");
            unopened.Save(PathOf("Unopened"));

            SheetDocument.Blank("Plain").Save(PathOf("Plain"));
            DateTime plainTime = File.GetLastWriteTimeUtc(PathOf("Plain"));

            SheetEditorControl editor = ShowSheet(t, PathOf("Summary"));
            yield return 2;
            SheetLayer cells = editor.document.pages[0].layers[0];
            t.Check(SheetBook.calc.Value(editor.document.pages[0], 0, 0).Display() == "20", "the summary reads the budget");

            File.Move(PathOf("Budget"), PathOf("Money"));
            SheetBook.Renamed(PathOf("Budget"), PathOf("Money"), Directory.EnumerateFiles(folder));
            yield return 2;

            t.Check(cells.Get(0, 0) == "=[Money]Data!A1*2" && cells.Get(1, 0) == "=[Money]Data!A1",
                $"a loaded sheet's references follow the rename: {cells.Get(0, 0)} {cells.Get(1, 0)}");
            t.Check(SheetBook.calc.Value(editor.document.pages[0], 0, 0).Display() == "20", "and keep their value");
            t.Check(SheetDocument.Load(PathOf("Unopened")).pages[0].Shown(0, 0) == "=[Money]Data!A1+1", "a sheet on disk is rewritten");
            t.Check(File.GetLastWriteTimeUtc(PathOf("Plain")) == plainTime, "a sheet without a reference is not touched");

            File.Move(PathOf("Money"), PathOf("My money"));
            SheetBook.Renamed(PathOf("Money"), PathOf("My money"), Directory.EnumerateFiles(folder));
            t.Check(cells.Get(0, 0) == "='[My money]Data'!A1*2", $"a name with a space is quoted: {cells.Get(0, 0)}");
            t.Check(SheetBook.calc.Value(editor.document.pages[0], 0, 0).Display() == "20", "and still reads");

            t.Show(new StackPanelControl());
            SheetBook.findSheet = before;
            foreach (string file in Directory.EnumerateFiles(folder))
                SheetBook.Deleted(file);
            Directory.Delete(folder, true);
        }

        [A_XSDActionDependency("Sheet.PasteLinks", "Test")]
        private static IEnumerator<int> PasteLinks(TestContext t)
        {
            string folder = TempFolder();
            Func<string?, string, string?>?before = SheetBook.findSheet;
            SheetBook.findSheet = FolderResolver(folder);
            string PathOf(string name) => Path.Combine(folder, name + SheetDocument.extension);

            SheetDocument budget = SheetDocument.Blank("Budget");
            budget.pages[0].name = "Data";
            budget.pages[0].layers[0].Set(0, 0, "10");
            budget.pages[0].layers[0].Set(0, 1, "20");
            budget.Save(PathOf("Budget"));
            SheetDocument.Blank("Summary").Save(PathOf("Summary"));

            StackPanelControl both = new StackPanelControl { orientation = StackPanelControl.Orientation.Horizontal };
            SheetEditorControl summary = new SheetEditorControl { preferredWidth = 400f, preferredHeight = 300f };
            SheetEditorControl source = new SheetEditorControl { preferredWidth = 400f, preferredHeight = 300f };
            summary.LoadPath(PathOf("Summary"));
            source.LoadPath(PathOf("Budget"));
            both.AddChild(summary);
            both.AddChild(source);
            t.Show(both);
            yield return 2;
            SheetLayer cells = summary.document.pages[0].layers[0];
            string? Shown(int row, int column) => SheetBook.calc.Value(summary.document.pages[0], row, column).Display();

            summary.Select(0, 0, false);
            summary.Paste("1\t2");
            summary.Select(0, 0, false);
            summary.Select(0, 1, true);
            summary.Copy();
            summary.Select(2, 0, false);
            t.Check(summary.PasteLink(), "Paste link takes the copy");
            t.Check(cells.Get(2, 0) == "=A1" && cells.Get(2, 1) == "=B1", $"from the same page it reads plain addresses: {cells.Get(2, 0)} {cells.Get(2, 1)}");
            t.Check(Shown(2, 0) == "1" && Shown(2, 1) == "2", "and shows their values");
            t.Check(summary.anchorRow == 2 && summary.activeRow == 2 && summary.activeColumn == 1, "the pasted block is selected");

            source.Select(0, 0, false);
            source.Select(0, 1, true);
            source.Copy();
            UIEngine.SetActiveControl(summary);
            summary.Select(4, 0, false);
            yield return t.Key(Keys.V, Keys.LeftControl);
            t.Check(cells.Get(4, 0) == "=[Budget]Data!A1" && cells.Get(4, 1) == "=[Budget]Data!B1",
                $"Ctrl+V from another file names the file and page: {cells.Get(4, 0)} {cells.Get(4, 1)}");
            t.Check(Shown(4, 0) == "10" && Shown(4, 1) == "20", "and shows its values");

            summary.Undo();
            t.Check(cells.Get(4, 0) == null && cells.Get(4, 1) == null, "one undo takes the link back");

            summary.Select(4, 0, false);
            yield return t.Key(Keys.V, Keys.LeftControl, Keys.LeftShift);
            t.Check(cells.Get(4, 0) == "10" && cells.Get(4, 1) == "20", $"Ctrl+Shift+V pastes the values: {cells.Get(4, 0)} {cells.Get(4, 1)}");

            ClipboardText.Set("x\ty");
            summary.Select(6, 0, false);
            summary.PasteLink();
            t.Check(cells.Get(6, 0) == "x" && cells.Get(6, 1) == "y", "a clipboard from elsewhere pastes plainly");

            t.Check(SheetFormula.Prefix(null, "Sheet 1") == "'Sheet 1'" && SheetFormula.Prefix("My money", "Data") == "'[My money]Data'"
                && SheetFormula.Prefix("Finance/Budget", "Data") == "[Finance/Budget]Data", "a page or file name with a space is quoted");

            t.Show(new StackPanelControl());
            SheetBook.findSheet = before;
            SheetBook.Deleted(PathOf("Budget"));
            SheetBook.Deleted(PathOf("Summary"));
            Directory.Delete(folder, true);
        }

        [A_XSDActionDependency("Sheet.NoteFormats", "Test")]
        private static IEnumerator<int> NoteFormats(TestContext t)
        {
            const string cell = "Budget.sheet.axml#Data!B1";
            const string range = "Budget.sheet.axml#Data!A1:B2";
            string markdown = $"Total ![[{cell}]] here\n\n![[{range}]]\n\n![[Other note#Heading]]\n";

            XElement read = MarkdownFormat.Read(markdown, "Links");
            List<XElement> runs = read.Descendants("Run").ToList();
            t.Check(runs.Count(run => (string?)run.Attribute("Sheet") == cell) == 1, "an inline embed of a sheet cell reads as a Sheet run");
            t.Check(runs.Count(run => (string?)run.Attribute("Sheet") == range) == 1, "a range embed reads as a Sheet run");
            t.Check(runs.Any(run => (string?)run.Attribute("Text") == "![[Other note#Heading]]"), "an embed of a note stays text");
            t.Check(MarkdownFormat.Write(read) == markdown, $"the markdown writes back as read: {MarkdownFormat.Write(read)}");

            XElement literal = new XElement("Document", new XElement("Block", new XElement("Run", new XAttribute("Text", $"![[{cell}]]"))));
            XElement reread = MarkdownFormat.Read(MarkdownFormat.Write(literal), "Literal");
            t.Check(reread.Descendants("Run").All(run => run.Attribute("Sheet") == null)
                && reread.Descendants("Run").Any(run => (string?)run.Attribute("Text") == $"![[{cell}]]"), "typed embed text stays text");

            RichTextDocument document = DocumentXml.Parse(new XElement("Document",
                new XElement("Block", new XElement("Run", new XAttribute("Text", "a ")), new XElement("Run", new XAttribute("Sheet", cell)))));
            NoteBlock block = document.blocks.OfType<NoteBlock>().First();
            t.Check(block.run.text == "a " + BlockControl.PictureChar && block.run.spans.Any(span => span.IsSheet && span.sheetRef == cell),
                "a Sheet run loads as one object character");
            t.Check(DocumentXml.ToXml(document).Descendants().Any(run => run.Name.LocalName == "Run" && (string?)run.Attribute("Sheet") == cell),
                "and saves as a Sheet run");

            t.Check(SheetLinks.Parse("Budget.sheet.axml#My page!B3:A1", out string file, out string page, out int top, out int left, out int bottom, out int right)
                && file == "Budget.sheet.axml" && page == "My page" && (top, left, bottom, right) == (0, 0, 2, 1), "a reference parses, its range ordered");
            t.Check(!SheetLinks.Parse("Budget.sheet.axml!B3", out _, out _, out _, out _, out _, out _), "a reference without a page does not parse");
            yield break;
        }

        [A_XSDActionDependency("Sheet.NoteLinks", "Test")]
        private static IEnumerator<int> NoteLinks(TestContext t)
        {
            string folder = TempFolder();
            Func<string?, string, string?>?before = SheetBook.findSheet;
            SheetBook.findSheet = FolderResolver(folder);
            string budgetPath = BudgetSheet(folder);

            string notePath = Path.Combine(folder, "Links.md");
            File.WriteAllText(notePath, "Total ![[Budget.sheet.axml#Data!B1]] here\n\n![[Budget.sheet.axml#Data!A1:B2]]\n\n![[Gone.sheet.axml#Data!A1]]\n");

            StackPanelControl both = new StackPanelControl { orientation = StackPanelControl.Orientation.Horizontal };
            DocumentEditorControl note = new DocumentEditorControl { preferredWidth = 600f, preferredHeight = 400f };
            SheetEditorControl sheet = new SheetEditorControl { preferredWidth = 300f, preferredHeight = 400f };
            note.LoadPath(notePath);
            sheet.LoadPath(budgetPath);
            both.AddChild(note);
            both.AddChild(sheet);
            t.Show(both);
            yield return 2;

            List<BlockControl> p = NoteBlocks(note);
            float Width() => p[0].CaretAt(7).x - p[0].CaretAt(6).x;
            float narrow = Width();
            t.Check(narrow > 0f, $"an inline cell takes its value's width: {narrow}");
            BlockControl rangeBlock = p.First(block => block.spans.Any(span => span.IsSheet && span.sheetRef.Contains(':')));
            t.Check(rangeBlock.arrangedRect.height >= 48f, $"a range is as tall as its rows: {rangeBlock.arrangedRect.height}");
            t.Check(SheetLinks.Plain(null, "Budget.sheet.axml#Data!A1:B2") == "10\t20" + Environment.NewLine + "text\t" + SheetFormula.div0,
                $"a range reads the sheet's values: {SheetLinks.Plain(null, "Budget.sheet.axml#Data!A1:B2")}");
            t.Check(SheetLinks.Plain(null, "Gone.sheet.axml#Data!A1") == SheetFormula.reference, "a missing sheet is #REF!");
            yield return t.Golden("Links", note);

            sheet.Select(0, 0, false);
            sheet.Paste("1000");
            yield return 2;
            t.Check(Width() > narrow, $"an edit in the sheet widens the note's value: {narrow} -> {Width()}");
            t.Check(!note.session.isDirty, "a sheet edit leaves the note unedited");
            yield return t.Golden("Edited", note);

            t.Show(new StackPanelControl());
            SheetBook.findSheet = before;
            SheetBook.Deleted(budgetPath);
            Directory.Delete(folder, true);
        }

        [A_XSDActionDependency("Sheet.NotePasteLinks", "Test")]
        private static IEnumerator<int> NotePasteLinks(TestContext t)
        {
            string folder = TempFolder();
            Func<string?, string, string?>?before = SheetBook.findSheet;
            SheetBook.findSheet = FolderResolver(folder);
            string budgetPath = BudgetSheet(folder);

            string notePath = Path.Combine(folder, "Paste.md");
            File.WriteAllText(notePath, "start\n");
            string plainPath = Path.Combine(folder, "Plain.txt");
            File.WriteAllText(plainPath, "start\n");

            StackPanelControl both = new StackPanelControl { orientation = StackPanelControl.Orientation.Horizontal };
            DocumentEditorControl note = new DocumentEditorControl { preferredWidth = 400f, preferredHeight = 300f };
            DocumentEditorControl plain = new DocumentEditorControl { preferredWidth = 200f, preferredHeight = 300f };
            SheetEditorControl sheet = new SheetEditorControl { preferredWidth = 300f, preferredHeight = 300f };
            note.LoadPath(notePath);
            plain.LoadPath(plainPath);
            sheet.LoadPath(budgetPath);
            both.AddChild(note);
            both.AddChild(plain);
            both.AddChild(sheet);
            t.Show(both);
            yield return 2;

            DocumentControl content = NoteContent(note);
            StyleSpan? Link(BlockControl block) => block.spans.Any(span => span.IsSheet) ? block.spans.First(span => span.IsSheet) : null;

            sheet.Select(0, 1, false);
            sheet.Copy();
            content.SetCaret(NoteBlocks(note)[0], 5);
            note.FocusCaret();
            yield return t.Key(Keys.V, Keys.LeftControl);
            BlockControl first = NoteBlocks(note)[0];
            t.Check(Link(first)?.sheetRef == "Budget.sheet.axml#Data!B1" && first.text == "start" + BlockControl.PictureChar,
                $"Ctrl+V in a note puts in a live cell: {Link(first)?.sheetRef} '{first.text}'");

            content.SetCaret(first, 5);
            content.SetCaret(first, 6, true);
            yield return t.Key(Keys.C, Keys.LeftControl);
            t.Check(ClipboardText.Get() == "20", $"copying the link copies its value: {ClipboardText.Get()}");

            yield return t.Key(Keys.Z, Keys.LeftControl);
            first = NoteBlocks(note)[0];
            t.Check(Link(first) == null && first.text == "start", $"one undo takes the link back: '{first.text}'");

            sheet.Select(0, 0, false);
            sheet.Select(1, 1, true);
            sheet.Copy();
            content.SetCaret(first, 5);
            note.FocusCaret();
            yield return t.Key(Keys.V, Keys.LeftControl);
            first = NoteBlocks(note)[0];
            t.Check(Link(first)?.sheetRef == "Budget.sheet.axml#Data!A1:B2", $"a range pastes as a range link: {Link(first)?.sheetRef}");

            NoteContent(plain).SetCaret(NoteBlocks(plain)[0], 5);
            plain.FocusCaret();
            yield return t.Key(Keys.V, Keys.LeftControl);
            BlockControl plainFirst = NoteBlocks(plain)[0];
            t.Check(Link(plainFirst) == null && plainFirst.text.StartsWith("start10"), $"a .txt note pastes the values: '{plainFirst.text}'");

            ClipboardText.Set("elsewhere");
            content.SetCaret(NoteBlocks(note)[0], 0);
            note.FocusCaret();
            yield return t.Key(Keys.V, Keys.LeftControl);
            first = NoteBlocks(note)[0];
            t.Check(first.text.StartsWith("elsewhere") && first.spans.Count(span => span.IsSheet) == 1, "a clipboard from elsewhere pastes plainly");

            yield return t.Key(Keys.Z, Keys.LeftControl);
            t.Check(!NoteBlocks(note)[0].text.StartsWith("elsewhere"), $"one undo takes the plain paste back: '{NoteBlocks(note)[0].text}'");

            t.Show(new StackPanelControl());
            SheetBook.findSheet = before;
            SheetBook.Deleted(budgetPath);
            Directory.Delete(folder, true);
        }

        [A_XSDActionDependency("Sheet.NoteRenameRewrites", "Test")]
        private static IEnumerator<int> NoteRenameRewrites(TestContext t)
        {
            string folder = TempFolder();
            Func<string?, string, string?>?before = SheetBook.findSheet;
            SheetBook.findSheet = FolderResolver(folder);
            string budgetPath = BudgetSheet(folder);
            string moneyPath = Path.Combine(folder, "Money" + SheetDocument.extension);

            string mdPath = Path.Combine(folder, "a.md");
            File.WriteAllText(mdPath, "x ![[Budget.sheet.axml#Data!A1]] y ![[Budgetary.sheet.axml#Data!A1]]\r\n");
            string xmlPath = Path.Combine(folder, "b.axml");
            File.WriteAllText(xmlPath, "<Document>\r\n  <Block><Run Text=\"a\"/><Run Sheet=\"Budget.sheet.axml#Data!A1:B2\"/></Block>\r\n</Document>\r\n",
                new System.Text.UTF8Encoding(true));
            string plainPath = Path.Combine(folder, "c.md");
            File.WriteAllText(plainPath, "nothing here\n");
            DateTime plainTime = File.GetLastWriteTimeUtc(plainPath);
            string openPath = Path.Combine(folder, "d.md");
            File.WriteAllText(openPath, "open ![[Budget.sheet.axml#Data!A1]]\n");

            string scope = SessionLayout.scope;
            WorkspaceControl workspace = new WorkspaceControl
            {
                paneDocument = "tab-pane",
                defaultDocument = "tab-pane",
                horizontalAlignment = HorizontalAlignment.Stretch,
                verticalAlignment = VerticalAlignment.Stretch
            };
            t.Show(workspace);
            SessionLayout.scope = "note-rename";
            workspace.LoadPane()!.AddChild(SessionLayout.tabFactory(openPath, false));
            yield return 2;
            DocumentEditorControl note = (DocumentEditorControl)TabViewControl.FileEditorOf(TabViewControl.FindOpenDocument(openPath, out _))!;

            File.Move(budgetPath, moneyPath);
            SheetBook.Renamed(budgetPath, moneyPath, Directory.EnumerateFiles(folder, "*" + SheetDocument.extension));
            SheetLinks.Renamed(budgetPath, moneyPath, new[] { mdPath, xmlPath, plainPath, openPath });
            yield return 2;

            t.Check(File.ReadAllText(mdPath) == "x ![[Money.sheet.axml#Data!A1]] y ![[Budgetary.sheet.axml#Data!A1]]\r\n",
                $"a .md note's link is rewritten and nothing else: {File.ReadAllText(mdPath)}");
            byte[] xml = File.ReadAllBytes(xmlPath);
            t.Check(xml.Length > 3 && xml[0] == 0xEF && xml[1] == 0xBB && xml[2] == 0xBF, "a .axml note keeps its BOM");
            t.Check(File.ReadAllText(xmlPath) == "<Document>\r\n  <Block><Run Text=\"a\"/><Run Sheet=\"Money.sheet.axml#Data!A1:B2\"/></Block>\r\n</Document>\r\n",
                $"a .axml note's link is rewritten and nothing else: {File.ReadAllText(xmlPath)}");
            t.Check(File.GetLastWriteTimeUtc(plainPath) == plainTime, "a note without a link is not touched");

            BlockControl open = NoteBlocks(note)[0];
            t.Check(open.spans.Any(span => span.sheetRef == "Money.sheet.axml#Data!A1"), "an open note's link follows the rename");
            t.Check(!note.session.isDirty, "and the open note stays unedited");
            t.Check(SheetLinks.Plain(null, "Money.sheet.axml#Data!A1") == "10", "the renamed link still reads");

            SessionLayout.scope = scope;
            t.Show(new StackPanelControl());
            SheetBook.findSheet = before;
            SheetBook.Deleted(moneyPath);
            Directory.Delete(folder, true);
        }

        [A_XSDActionDependency("Sheet.MathLinks", "Test")]
        private static IEnumerator<int> MathLinks(TestContext t)
        {
            string folder = TempFolder();
            Func<string?, string, string?>?before = SheetBook.findSheet;
            SheetBook.findSheet = FolderResolver(folder);
            string budgetPath = BudgetSheet(folder);
            string moneyPath = Path.Combine(folder, "Money" + SheetDocument.extension);

            t.Check(SheetLinks.ExpandMath(null, @"a = \sheet{Budget.sheet.axml#Data!B1}") == "a = {20}", "a number reads in as itself");
            t.Check(SheetLinks.ExpandMath(null, @"\sheet{Budget.sheet.axml#Data!A2}") == @"\text{text}", "text reads in as \\text");
            t.Check(SheetLinks.ExpandMath(null, @"\sheet{Budget.sheet.axml#Data!A1:B2}") == @"\text{" + SheetFormula.value + "}", "a range is #VALUE!");
            t.Check(SheetLinks.ExpandMath(null, @"\sheet{Gone.sheet.axml#Data!A1}") == @"\text{" + SheetFormula.reference + "}", "a missing sheet is #REF!");

            string notePath = Path.Combine(folder, "Math.md");
            File.WriteAllText(notePath, "x $a = \\sheet{Budget.sheet.axml#Data!B1}$ y\n\n$\\sheet{Gone.sheet.axml#Data!A1}$ end\n");
            string xmlPath = Path.Combine(folder, "Math.axml");
            File.WriteAllText(xmlPath, "<Document><Block><Run Math=\"a&#xA;\\sheet{Budget.sheet.axml#Data!A1} &lt; b\" Display=\"true\"/></Block></Document>\n");

            string scope = SessionLayout.scope;
            WorkspaceControl workspace = new WorkspaceControl
            {
                paneDocument = "tab-pane",
                defaultDocument = "tab-pane",
                preferredWidth = 600f,
                preferredHeight = 400f
            };
            SheetEditorControl sheet = new SheetEditorControl { preferredWidth = 300f, preferredHeight = 400f };
            sheet.LoadPath(budgetPath);
            StackPanelControl both = new StackPanelControl { orientation = StackPanelControl.Orientation.Horizontal };
            both.AddChild(workspace);
            both.AddChild(sheet);
            t.Show(both);
            SessionLayout.scope = "math-links";
            workspace.LoadPane()!.AddChild(SessionLayout.tabFactory(notePath, false));
            yield return 2;
            DocumentEditorControl note = (DocumentEditorControl)TabViewControl.FileEditorOf(TabViewControl.FindOpenDocument(notePath, out _))!;

            List<BlockControl> p = NoteBlocks(note);
            float Width() => p[0].CaretAt(3).x - p[0].CaretAt(2).x;
            float narrow = Width();
            t.Check(narrow > 0f, $"a formula holding a link is laid out: {narrow}");
            yield return t.Golden("Math", note);

            sheet.Select(0, 0, false);
            sheet.Paste("1000");
            yield return 2;
            t.Check(Width() > narrow, $"an edit in the sheet widens the formula: {narrow} -> {Width()}");
            t.Check(!note.session.isDirty, "a sheet edit leaves the note unedited");

            sheet.Select(0, 1, false);
            sheet.Copy();
            NoteContent(note).SetCaret(p[0], p[0].text.Length);
            note.FocusCaret();
            yield return t.Key(Keys.M, Keys.LeftControl);
            yield return t.Key(Keys.V, Keys.LeftControl);
            TextBoxControl? box = UIEngine.activeControl as TextBoxControl;
            t.Check(box?.text == @"\sheet{Budget.sheet.axml#Data!B1}", $"Ctrl+V in the formula's source puts in a reference: {box?.text}");
            yield return t.Key(Keys.Enter);
            yield return 2;
            p = NoteBlocks(note);
            t.Check(p[0].spans.Count(span => span.IsMath && span.mathSource == @"\sheet{Budget.sheet.axml#Data!B1}") == 1, "and commits it into the note");

            ClipboardText.Set("q");
            yield return t.Key(Keys.M, Keys.LeftControl);
            yield return t.Key(Keys.V, Keys.LeftControl);
            box = UIEngine.activeControl as TextBoxControl;
            t.Check(box?.text == "q", $"a clipboard from elsewhere pastes plainly: {box?.text}");
            yield return t.Key(Keys.Escape);
            yield return 2;

            sheet.Copy();
            yield return t.Key(Keys.M, Keys.LeftControl);
            ContextMenus.Close();
            yield return 2;
            t.Check(UIEngine.activeControl is not TextBoxControl, "a popup closed from outside is cancelled and gives the focus back");
            yield return t.Key(Keys.V, Keys.LeftControl);
            t.Check(NoteBlocks(note)[0].spans.Count(span => span.IsMath && span.mathSource == @"\sheet{Budget.sheet.axml#Data!B1}") == 1,
                "Paste link after it reaches no closed popup");
            yield return 2;

            File.Move(budgetPath, moneyPath);
            SheetBook.Renamed(budgetPath, moneyPath, new[] { moneyPath });
            SheetLinks.Renamed(budgetPath, moneyPath, new[] { notePath, xmlPath });
            yield return 2;

            t.Check(NoteBlocks(note)[0].spans.Any(span => span.IsMath && span.mathSource == @"a = \sheet{Money.sheet.axml#Data!B1}"),
                "an open note's formula follows the rename");
            t.Check(File.ReadAllText(xmlPath) == "<Document><Block><Run Math=\"a&#xA;\\sheet{Money.sheet.axml#Data!A1} &lt; b\" Display=\"true\"/></Block></Document>\n",
                $"a .axml note's formula is rewritten and nothing else: {File.ReadAllText(xmlPath)}");
            t.Check(SheetLinks.ExpandMath(null, @"\sheet{Money.sheet.axml#Data!B1}") == "{2000}", "the renamed reference still reads");

            SessionLayout.scope = scope;
            t.Show(new StackPanelControl());
            string written = File.ReadAllText(notePath);
            string body = Frontmatter.Split(written, out _, out string rest) ? rest : written;
            t.Check(body.StartsWith("x $a = \\sheet{Money.sheet.axml#Data!B1}$"), $"a .md note's formula is rewritten: {written}");
            SheetBook.findSheet = before;
            SheetBook.Deleted(moneyPath);
            Directory.Delete(folder, true);
        }

        #region ---- fixtures ----
        // Budget.sheet.axml, page Data: 10, =A1*2 / text, =1/0.
        [A_XSDActionDependency("Sheet.Formats", "Test")]
        private static IEnumerator<int> Formats(TestContext t)
        {
            XElement source = XElement.Parse(
                "<Sheet Name=\"Styled\"><Page Name=\"One\">" +
                "<Format At=\"A1\" Bold=\"true\" Fill=\"#C8E6A0\" Number=\"0.00%\" /><Format At=\"C4\" Number=\"#,##0.00\" />" +
                "<Layer Name=\"Layer 1\" /></Page></Sheet>");
            SheetDocument styled = SheetXml.Parse(source);
            t.Check(XNode.DeepEquals(source, SheetXml.ToXml(styled)), $"formats write back as they were read: {SheetXml.ToXml(styled)}");
            t.Check(styled.pages[0].Format(0, 0) == new SheetFormat(true, "#C8E6A0", "0.00%") && styled.pages[0].formats.Count == 2,
                "every format attribute is read");
            SheetDocument badFills = SheetXml.Parse(XElement.Parse(
                "<Sheet><Page Name=\"One\"><Format At=\"A1\" Bold=\"true\" Fill=\"Yellow\" /><Format At=\"A2\" Fill=\"#FFF3A3AA\" />" +
                "<Format At=\"A3\" Fill=\"FFF3A3\" /><Layer Name=\"Layer 1\" /></Page></Sheet>"));
            t.Check(badFills.pages[0].Format(0, 0) == new SheetFormat(true, null, null) && badFills.pages[0].Format(1, 0).fill == null
                && badFills.pages[0].Format(2, 0).fill == "FFF3A3", "a fill that is not six hex digits is dropped");

            string folder = TempFolder();
            Func<string?, string, string?>?before = SheetBook.findSheet;
            SheetBook.findSheet = FolderResolver(folder);
            SheetDocument fmt = SheetDocument.Blank("Fmt");
            fmt.pages[0].name = "Data";
            fmt.pages[0].layers[0].Set(0, 0, "1234.5");
            fmt.pages[0].layers[0].Set(0, 1, "=A1/5000");
            fmt.pages[0].layers[0].Set(1, 0, "text");
            string sheetPath = Path.Combine(folder, "Fmt" + SheetDocument.extension);
            fmt.Save(sheetPath);

            string notePath = Path.Combine(folder, "Shown.md");
            File.WriteAllText(notePath, "Total ![[Fmt.sheet.axml#Data!A1]] here\n\n![[Fmt.sheet.axml#Data!A1:B2]]\n");

            StackPanelControl both = new StackPanelControl { orientation = StackPanelControl.Orientation.Horizontal };
            DocumentEditorControl note = new DocumentEditorControl { preferredWidth = 420f, preferredHeight = 300f };
            SheetEditorControl editor = new SheetEditorControl { preferredWidth = 420f, preferredHeight = 300f };
            note.LoadPath(notePath);
            editor.LoadPath(sheetPath);
            both.AddChild(note);
            both.AddChild(editor);
            t.Show(both);
            yield return 2;

            SheetPage page = editor.document.pages[0];
            string? Shown(int row, int column) => SheetBook.calc.Value(page, row, column).Display(page.Format(row, column).number);

            SheetControl grid = Grid(editor);
            LayoutRect a1 = grid.CellRect(0, 0);
            Vector2 at = new Vector2(a1.x + a1.width * 0.5f, a1.y + a1.height * 0.5f);
            yield return t.Drag(grid, at, at, 1);
            editor.Select(0, 1, true);
            yield return t.Key(Keys.B, Keys.LeftControl);
            t.Check(page.Format(0, 0).bold && page.Format(0, 1).bold, "Ctrl+B bolds the selection");
            yield return t.Key(Keys.B, Keys.LeftControl);
            t.Check(page.formats.Count == 0, "a second Ctrl+B unbolds it and leaves no format behind");
            yield return t.Key(Keys.B, Keys.LeftControl);

            editor.SetFill("#F7CFA0");
            t.Check(page.Format(0, 1).fill == "#F7CFA0", "a fill reaches every selected cell");

            editor.Select(0, 0, false);
            editor.SetNumberFormat("#,##0.00");
            t.Check(Shown(0, 0) == "1,234.50", $"Number groups thousands with two decimals: {Shown(0, 0)}");
            editor.SetNumberFormat("€#,##0.00");
            t.Check(Shown(0, 0) == "€1,234.50", $"Currency is euros: {Shown(0, 0)}");
            editor.Select(0, 1, false);
            editor.SetNumberFormat("0.00%");
            t.Check(Shown(0, 1) == "24.69%", $"Percent scales a formula's result: {Shown(0, 1)}");
            editor.Select(1, 0, false);
            editor.SetNumberFormat("#,##0.00");
            t.Check(Shown(1, 0) == "text", "text ignores a number format");

            editor.Select(0, 0, false);
            editor.Copy();
            t.Check(ClipboardText.Get() == "1234.5", $"a formatted number copies as the number: {ClipboardText.Get()}");
            t.Check(SheetLinks.ExpandMath(null, @"\sheet{Fmt.sheet.axml#Data!A1}") == "{1234.5}", "note math reads the number unformatted");

            editor.Clear();
            t.Check(page.Shown(0, 0) == null && page.Format(0, 0).number == "€#,##0.00", "Delete empties the cell and keeps its format");
            editor.Undo();
            editor.Select(1, 0, false);
            editor.Undo();
            t.Check(page.Format(1, 0) == default, "undo takes a format back off");
            editor.Redo();
            yield return 2;
            t.Check(editor.unsaved, "a format change leaves the sheet unsaved");

            yield return t.Golden("Grid", both);

            editor.Save();
            SheetPage saved = SheetDocument.Load(sheetPath).pages[0];
            t.Check(saved.Format(0, 0) == new SheetFormat(true, "#F7CFA0", "€#,##0.00") && saved.Format(0, 1) == new SheetFormat(true, "#F7CFA0", "0.00%"),
                "formats are saved with the sheet");

            t.Show(new StackPanelControl());
            SheetBook.findSheet = before;
            SheetBook.Deleted(sheetPath);
            Directory.Delete(folder, true);
        }

        [A_XSDActionDependency("Sheet.Resize", "Test")]
        private static IEnumerator<int> Resize(TestContext t)
        {
            string folder = TempFolder();
            Func<string?, string, string?>?before = SheetBook.findSheet;
            SheetBook.findSheet = FolderResolver(folder);
            string budgetPath = BudgetSheet(folder);

            string notePath = Path.Combine(folder, "Range.md");
            File.WriteAllText(notePath, "![[Budget.sheet.axml#Data!A1:B2]]\n");

            StackPanelControl both = new StackPanelControl { orientation = StackPanelControl.Orientation.Horizontal };
            DocumentEditorControl note = new DocumentEditorControl { preferredWidth = 500f, preferredHeight = 300f };
            SheetEditorControl editor = new SheetEditorControl { preferredWidth = 500f, preferredHeight = 300f };
            note.LoadPath(notePath);
            editor.LoadPath(budgetPath);
            both.AddChild(note);
            both.AddChild(editor);
            t.Show(both);
            yield return 2;

            SheetPage page = editor.document.pages[0];
            SheetControl grid = Grid(editor);
            BlockControl range = NoteBlocks(note).First(block => block.spans.Any(span => span.IsSheet));
            float shallow = range.arrangedRect.height;

            float edge = grid.CellRect(0, 2).x;
            float headerY = grid.arrangedRect.y + SheetControl.headerHeight * 0.5f;
            yield return t.Drag(grid, new Vector2(edge, headerY), new Vector2(edge + 60f, headerY));
            yield return 2;
            t.Check(page.ColumnWidth(1) == SheetPage.defaultColumnWidth + 60f, $"dragging B's right edge widens B: {page.ColumnWidth(1)}");
            t.Check(editor.unsaved, "a resize leaves the sheet unsaved");

            editor.Undo();
            t.Check(!page.columnWidths.ContainsKey(1), "undo puts B back to the default width");
            editor.Redo();
            t.Check(page.ColumnWidth(1) == SheetPage.defaultColumnWidth + 60f, "redo widens it again");

            float headerX = grid.arrangedRect.x + SheetControl.headerWidth * 0.5f;
            float first = grid.CellRect(1, 0).y;
            yield return t.Drag(grid, new Vector2(headerX, first), new Vector2(headerX, first + 30f));
            yield return 2;
            t.Check(page.RowHeight(0) == SheetPage.defaultRowHeight + 30f, $"dragging row 1's bottom edge deepens it: {page.RowHeight(0)}");
            t.Check(range.arrangedRect.height > shallow, $"the note's range grows with it: {shallow} -> {range.arrangedRect.height}");

            float bottom = grid.CellRect(3, 0).y;
            yield return t.Drag(grid, new Vector2(headerX, bottom), new Vector2(headerX, bottom - 60f));
            yield return 2;
            t.Check(page.RowHeight(2) == 8f, $"a row dragged past its top stops at the minimum: {page.RowHeight(2)}");

            Vector2 cell = new Vector2(grid.CellRect(5, 0).x + 10f, grid.CellRect(5, 0).y + 5f);
            yield return t.Drag(grid, cell, cell, 1);
            t.Check(editor.activeRow == 5 && page.RowHeight(5) == SheetPage.defaultRowHeight, "a press away from a header edge still selects");

            editor.Save();
            SheetPage saved = SheetDocument.Load(budgetPath).pages[0];
            t.Check(saved.ColumnWidth(1) == SheetPage.defaultColumnWidth + 60f && saved.RowHeight(0) == SheetPage.defaultRowHeight + 30f
                && saved.RowHeight(2) == 8f, "sizes are saved with the sheet");

            t.Show(new StackPanelControl());
            SheetBook.findSheet = before;
            SheetBook.Deleted(budgetPath);
            Directory.Delete(folder, true);
        }

        [A_XSDActionDependency("Sheet.Pages", "Test")]
        private static IEnumerator<int> Pages(TestContext t)
        {
            string folder = TempFolder();
            Func<string?, string, string?>?findBefore = SheetBook.findSheet;
            Func<string?, IEnumerable<string>>?sheetsBefore = SheetBook.vaultSheets;
            Func<string?, IEnumerable<string>>?notesBefore = SheetBook.vaultNotes;
            SheetBook.findSheet = FolderResolver(folder);
            SheetBook.vaultSheets = _ =>Directory.EnumerateFiles(folder, "*" + SheetDocument.extension);
            SheetBook.vaultNotes = _ =>Directory.EnumerateFiles(folder, "*.md");
            string PathOf(string name) => Path.Combine(folder, name + SheetDocument.extension);

            SheetDocument budget = SheetDocument.Blank("Budget");
            budget.pages[0].name = "Data";
            budget.pages[0].layers[0].Set(0, 0, "10");
            budget.pages[0].layers[0].Set(1, 0, "=Data!A1*2");
            budget.Save(PathOf("Budget"));

            SheetDocument summary = SheetDocument.Blank("Summary");
            summary.pages[0].layers[0].Set(0, 0, "=[Budget]Data!A1+1");
            summary.pages[0].layers[0].Set(1, 0, "='[Budget]Sheet 1'!A1");
            summary.Save(PathOf("Summary"));

            SheetDocument unopened = SheetDocument.Blank("Unopened");
            unopened.pages[0].layers[0].Set(0, 0, "='[Budget]Data'!A1");
            unopened.Save(PathOf("Unopened"));

            string notePath = Path.Combine(folder, "a.md");
            File.WriteAllText(notePath, "x ![[Budget.sheet.axml#Data!A1]] $\\sheet{Budget.sheet.axml#Data!A1}$\n");

            SheetEditorControl editor = ShowSheet(t, PathOf("Budget"));
            SheetPage summaryPage = SheetBook.Get(PathOf("Summary")).pages[0];
            yield return 2;
            SheetPageStripControl strip = Find<SheetPageStripControl>(editor, _ => true)!;
            t.Check(Labels(strip).Contains("Data") && editor.pageIndex == 0, "the strip shows the one page");
            t.Check(SheetBook.calc.Value(summaryPage, 1, 0).Display() == SheetFormula.reference, "a missing page reads #REF!");

            editor.AddPage();
            yield return 2;
            SheetPage second = editor.document.pages[^1];
            t.Check(editor.document.pages.Count == 2 && second.name == "Sheet 1" && editor.pageIndex == 1, $"a page is added and shown: {second.name}");
            t.Check(Labels(strip).Contains("Sheet 1") && editor.unsaved, "it has a tab and the file is unsaved");
            second.layers[0].Set(0, 0, "=Data!A1+5");
            SheetBook.Changed(editor.document, second, new[] { (0, 0) });
            t.Check(SheetBook.calc.Value(second, 0, 0).Display() == "15", "the new page reads another page");
            t.Check(SheetBook.calc.Value(summaryPage, 1, 0).Display() == "15", "a waiting reference now resolves");

            editor.ShowPage(0);
            yield return 2;
            t.Check(editor.pageIndex == 0 && Grid(editor).page == editor.document.pages[0], "a tab press shows its page");

            t.Check(!editor.RenamePage(0, "a!b") && !editor.RenamePage(0, "sheet 1") && !editor.RenamePage(0, " "),
                "a name with !, another page's name or an empty one is refused");
            t.Check(editor.RenamePage(0, "Spend") && editor.document.pages[0].name == "Spend", "a page is renamed");
            yield return 2;
            t.Check(editor.document.pages[0].layers[0].Get(1, 0) == "=Spend!A1*2", "its own page's reference follows");
            t.Check(second.layers[0].Get(0, 0) == "=Spend!A1+5", "another page's reference follows");
            t.Check(summaryPage.layers[0].Get(0, 0) == "=[Budget]Spend!A1+1", "a loaded sheet's reference follows");
            t.Check(SheetBook.calc.Value(summaryPage, 0, 0).Display() == "11", "and keeps its value");
            t.Check(SheetDocument.Load(PathOf("Unopened")).pages[0].Shown(0, 0) == "=[Budget]Spend!A1", "a sheet on disk is rewritten");
            t.Check(File.ReadAllText(notePath) == "x ![[Budget.sheet.axml#Spend!A1]] $\\sheet{Budget.sheet.axml#Spend!A1}$\n",
                $"a note's link and formula follow: {File.ReadAllText(notePath)}");
            t.Check(Labels(strip).Contains("Spend"), "the tab shows the new name");

            editor.Undo();
            t.Check(editor.document.pages[0].name == "Data" && summaryPage.layers[0].Get(0, 0) == "=[Budget]Data!A1+1"
                && File.ReadAllText(notePath).Contains("#Data!A1]]"), "undo renames back everywhere");
            editor.Redo();
            t.Check(editor.document.pages[0].name == "Spend" && second.layers[0].Get(0, 0) == "=Spend!A1+5", "redo renames again");

            editor.ShowPage(1);
            SessionTab view = editor.ViewState();
            t.Check(view.topBlock == 1, "the view keeps the page");
            SheetEditorControl restored = new SheetEditorControl();
            restored.LoadPath(PathOf("Budget"));
            restored.RestoreView(view);
            t.Check(restored.pageIndex == 1, "a restored view shows the page");
            restored.Destroy();

            editor.DeletePage(1);
            yield return 2;
            t.Check(editor.document.pages.Count == 1 && editor.pageIndex == 0, "deleting the shown page shows its neighbour");
            t.Check(SheetBook.calc.Value(summaryPage, 1, 0).Display() == SheetFormula.reference, "a reference to it reads #REF!");
            editor.DeletePage(0);
            t.Check(editor.document.pages.Count == 1, "the last page stays");
            editor.Undo();
            t.Check(editor.document.pages.Count == 2 && ReferenceEquals(editor.document.pages[1], second), "undo brings the page back");
            t.Check(SheetBook.calc.Value(summaryPage, 1, 0).Display() == "15", "and its references read again");

            editor.ShowPage(1);
            yield return 2;
            yield return t.Golden("Strip", strip);

            editor.Save();
            SheetDocument saved = SheetDocument.Load(PathOf("Budget"));
            t.Check(saved.pages.Count == 2 && saved.pages[0].name == "Spend" && saved.pages[1].name == "Sheet 1", "pages are saved");

            t.Show(new StackPanelControl());
            SheetBook.findSheet = findBefore;
            SheetBook.vaultSheets = sheetsBefore;
            SheetBook.vaultNotes = notesBefore;
            foreach (string file in Directory.EnumerateFiles(folder, "*" + SheetDocument.extension))
                SheetBook.Deleted(file);
            Directory.Delete(folder, true);
        }

        [A_XSDActionDependency("Sheet.Layers", "Test")]
        private static IEnumerator<int> Layers(TestContext t)
        {
            SheetDocument sheet = SheetDocument.Blank("Layers");
            sheet.pages[0].layers[0].Set(0, 0, "10");
            sheet.pages[0].layers[0].Set(0, 1, "=A1*2");
            string path = TempSheet(sheet);

            SheetEditorControl editor = ShowSheet(t, path);
            yield return 2;
            SheetPage page = editor.page;
            SheetLayer bottom = page.layers[0];
            t.Check(ReferenceEquals(editor.editLayer, bottom), "one layer is the edited one");

            editor.AddLayer();
            SheetLayer top = page.layers[^1];
            t.Check(page.layers.Count == 2 && top.name == "Layer 2" && ReferenceEquals(editor.editLayer, top), $"an added layer goes on top and is edited: {top.name}");
            t.Check(editor.unsaved, "adding a layer makes the file unsaved");
            editor.Select(0, 0, false);
            editor.Paste("5");
            t.Check(top.Get(0, 0) == "5" && bottom.Get(0, 0) == "10", "an edit lands in the edited layer");
            t.Check(SheetBook.calc.Value(page, 0, 1).Display() == "10", "the top layer covers the cell a formula reads");

            editor.EditLayer(bottom);
            editor.Paste("7");
            t.Check(bottom.Get(0, 0) == "7" && page.Shown(0, 0) == "5", "an edit to a covered cell lands in the picked layer, the top one still shows");
            editor.BeginEdit(true);
            t.Check(Find<TextBoxControl>(editor, box => true)!.text == "7", "the open field holds the edited layer's text");
            editor.Enter(false);
            t.Check(bottom.Get(0, 0) == "7", "committing the unchanged field writes nothing new");

            editor.ToggleLayer(top);
            t.Check(!top.visible && page.Shown(0, 0) == "7" && SheetBook.calc.Value(page, 0, 1).Display() == "14", "hiding the top layer shows what is under it, formulas too");
            editor.Undo();
            t.Check(top.visible && SheetBook.calc.Value(page, 0, 1).Display() == "10", "undo shows it again");
            editor.Redo();
            t.Check(!top.visible, "redo hides it");

            editor.EditLayer(top);
            editor.DeleteLayer();
            t.Check(page.layers.Count == 1 && ReferenceEquals(editor.editLayer, bottom), "deleting the edited layer leaves the one below edited");
            editor.DeleteLayer();
            t.Check(page.layers.Count == 1, "the last layer stays");
            editor.Undo();
            t.Check(page.layers.Count == 2 && ReferenceEquals(page.layers[1], top) && !top.visible, "undo brings the layer back, still hidden");

            editor.Save();
            SheetDocument saved = SheetDocument.Load(path);
            t.Check(saved.pages[0].layers.Count == 2 && !saved.pages[0].layers[1].visible, "layers and visibility are saved");

            ButtonControl open = Find<ButtonControl>(editor, b => b.children.OfType<LabelControl>().Any(l => l.text == "Layers"))!;
            yield return t.Click(open);
            yield return 30;
            SheetLayersControl panel = Find<SheetLayersControl>(Engine.primary.ui.uiRoot, _ => true)!;
            t.Check(panel != null, "the layers button opens the panel");
            if (panel == null) yield break;

            ButtonControl name = Find<ButtonControl>(panel, b => b.children.OfType<LabelControl>().Any(l => l.text == "Layer 1"))!;
            yield return t.Click(name);
            t.Check(ReferenceEquals(editor.editLayer, bottom), "a click on a name picks the edited layer");
            yield return 2;
            yield return t.Golden("Panel", panel);

            ContextMenus.Close();
            t.Show(new StackPanelControl());
            SheetBook.Deleted(path);
            File.Delete(path);
        }

        [A_XSDActionDependency("Sheet.Csv", "Test")]
        private static IEnumerator<int> Csv(TestContext t)
        {
            string Rows(List<List<string>> rows) => string.Join(" / ", rows.Select(row => string.Join("|", row)));

            List<List<string>> read = SheetCsv.Read("﻿a,\"b,c\",\"say \"\"hi\"\"\"\r\n\"two\r\nlines\",,x\n3", ',');
            t.Check(Rows(read) == "a|b,c|say \"hi\" / two\r\nlines||x / 3", $"quotes, escaped quotes, line breaks, empty fields and a BOM read: {Rows(read)}");
            t.Check(SheetCsv.Read("a,b\r\n", ',').Count == 1, "a closing line break makes no empty record");
            t.Check(SheetCsv.Delimiter("a;b;\"c,d\";e\r\n1,2,3,4,5,6") == ';', "the first record's most used delimiter wins, quoted ones not counted");
            t.Check(SheetCsv.Delimiter("a\tb\tc") == '\t' && SheetCsv.Delimiter("abc") == ',', "tab is found, and none reads as comma");

            string written = SheetCsv.Write(new List<IReadOnlyList<string>> { new[] { "a", "b,c", "say \"hi\"" }, new[] { "two\r\nlines", " pad", "" } }, ',');
            t.Check(written == "a,\"b,c\",\"say \"\"hi\"\"\"\r\n\"two\r\nlines\",\" pad\",\r\n", $"fields needing it are quoted: {written}");
            t.Check(Rows(SheetCsv.Read(written, ',')) == "a|b,c|say \"hi\" / two\r\nlines| pad|", "what is written reads back the same");

            string folder = TempFolder();
            string path = Path.Combine(folder, "Budget" + SheetDocument.extension);
            SheetDocument budget = SheetDocument.Blank("Budget");
            budget.pages[0].name = "Data";
            SheetLayer cells = budget.pages[0].layers[0];
            cells.Set(0, 0, "Item");
            cells.Set(0, 1, "Cost, EUR");
            cells.Set(1, 0, "Rent");
            cells.Set(1, 1, "1234.5");
            cells.Set(2, 1, "=B2*2");
            budget.pages[0].SetFormat(1, 1, new SheetFormat(true, null, "C2"));
            budget.Save(path);

            SheetEditorControl editor = ShowSheet(t, path);
            yield return 2;
            string? exported = editor.ExportPage(0);
            t.Check(exported == Path.Combine(folder, "Budget - Data.csv"), $"the export sits beside the sheet, named for its page: {exported}");
            byte[] bytes = File.ReadAllBytes(exported!);
            t.Check(bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "the export starts with a UTF-8 BOM");
            string text = File.ReadAllText(exported!);
            t.Check(text == "Item,\"Cost, EUR\"\r\nRent,1234.5\r\n,2469\r\n", $"values are exported, numbers unformatted, formulas as results: {text}");

            editor.document.pages[0].layers[0].Set(1, 1, "10");
            SheetBook.Changed(editor.document, editor.document.pages[0], new[] { (1, 1) });
            editor.ExportPage(0);
            t.Check(File.ReadAllText(exported!).Contains("Rent,10\r\n,20"), "a second export writes over the first");
            t.Check(Directory.EnumerateFiles(folder, "*.csv").Count() == 1, "and adds no second file");

            string csvPath = Path.Combine(folder, "data.csv");
            File.WriteAllText(csvPath, "name;cost\r\nrent;\"1,5\"\r\nfee;=2*3\r\n", new System.Text.UTF8Encoding(false));
            TabItemControl tab = SessionLayout.tabFactory(csvPath, false);
            SheetEditorControl csv = (SheetEditorControl)TabViewControl.FileEditorOf(tab)!;
            t.Show(tab);
            yield return 2;
            SheetPage csvPage = csv.document.pages[0];
            t.Check(csv.document.pages.Count == 1 && csvPage.name == "data" && csv.document.name == "data", "a CSV opens in a sheet tab as one page named after the file");
            t.Check(csvPage.Shown(1, 1) == "1,5" && SheetBook.calc.Value(csvPage, 2, 1).Display() == "6", "fields read as typed, formulas evaluate");
            t.Check(Find<SheetPageStripControl>(csv, _ => true)!.hidden, "a CSV tab has no page strip");

            csv.Select(3, 0, false);
            csv.Paste("tax\t=B3+1");
            csv.Save();
            byte[] saved = File.ReadAllBytes(csvPath);
            t.Check(saved[0] != 0xEF, "a CSV read without a BOM is written without one");
            t.Check(File.ReadAllText(csvPath) == "name;cost\r\nrent;1,5\r\nfee;=2*3\r\ntax;=B3+1\r\n",
                $"saving writes the cells back in the file's own delimiter, formulas as written: {File.ReadAllText(csvPath)}");
            t.Check(!csv.unsaved, "and the tab is saved");

            t.Show(new StackPanelControl());
            SheetBook.Deleted(csvPath);
            SheetBook.Deleted(path);
            Directory.Delete(folder, true);
        }

        [A_XSDActionDependency("Sheet.CsvLinks", "Test")]
        private static IEnumerator<int> CsvLinks(TestContext t)
        {
            string folder = TempFolder();
            Func<string?, string, string?>?findBefore = SheetBook.findSheet;
            Func<string?, string, string?> sheets = FolderResolver(folder);
            SheetBook.findSheet = (_, file) => SheetCsv.IsCsv(file) ? (File.Exists(Path.Combine(folder, file)) ? Path.Combine(folder, file) : null) : sheets(null, file);
            string SheetPath(string name) => Path.Combine(folder, name + SheetDocument.extension);

            string dataPath = Path.Combine(folder, "data.csv");
            File.WriteAllText(dataPath, "item,cost,ext\r\nrent,100,=[Budget]Data!A1\r\nfee,=data!B2*2,\r\n");

            SheetDocument other = SheetDocument.Blank("data");
            other.pages[0].name = "Data";
            other.pages[0].layers[0].Set(0, 0, "999");
            other.Save(SheetPath("data"));

            SheetDocument unopened = SheetDocument.Blank("Unopened");
            unopened.pages[0].layers[0].Set(0, 0, "=[data.csv]data!B2");
            unopened.Save(SheetPath("Unopened"));

            SheetDocument budget = SheetDocument.Blank("Budget");
            budget.pages[0].name = "Data";
            SheetLayer cells = budget.pages[0].layers[0];
            cells.Set(0, 0, "10");
            cells.Set(0, 1, "=[data.csv]data!B2+1");
            cells.Set(1, 1, "=[data.csv]data!B3");
            cells.Set(0, 2, "=[data]Data!A1");
            budget.Save(SheetPath("Budget"));

            string notePath = Path.Combine(folder, "a.md");
            File.WriteAllText(notePath, "x ![[data.csv#data!B2]] $\\sheet{data.csv#data!B3}$\n");

            SheetEditorControl editor = new SheetEditorControl { preferredWidth = 400f, preferredHeight = 400f };
            editor.LoadPath(SheetPath("Budget"));
            SheetEditorControl csv = new SheetEditorControl { preferredWidth = 400f, preferredHeight = 400f };
            csv.LoadPath(dataPath);
            StackPanelControl both = new StackPanelControl { orientation = StackPanelControl.Orientation.Horizontal };
            both.AddChild(editor);
            both.AddChild(csv);
            t.Show(both);
            SheetPage page = editor.document.pages[0];
            SheetPage csvPage = csv.document.pages[0];
            yield return 2;

            t.Check(SheetBook.calc.Value(page, 0, 1).Display() == "101" && SheetBook.calc.Value(page, 1, 1).Display() == "200",
                "a sheet formula reads a CSV, formulas inside it included");
            t.Check(SheetBook.calc.Value(page, 0, 2).Display() == "999", "data.csv and data.sheet.axml resolve separately");
            t.Check(SheetBook.calc.Value(csvPage, 1, 2).Display() == SheetFormula.reference, "a CSV reaches no other file");
            t.Check(SheetLinks.IsLink("data.csv#data!B2") && SheetLinks.Plain(null, "data.csv#data!B2") == "100", "a note link reads a CSV cell");
            t.Check(SheetLinks.ExpandMath(null, @"\sheet{data.csv#data!B3}") == "{200}", "note math reads a CSV cell");

            csv.Select(1, 1, false);
            csv.Copy();
            editor.Select(5, 0, false);
            editor.PasteLink();
            t.Check(page.layers[0].Get(5, 0) == "=[data.csv]data!B2", $"Paste link from a CSV writes a reference to it: {page.layers[0].Get(5, 0)}");
            editor.Select(0, 0, false);
            editor.Copy();
            csv.Select(5, 0, false);
            csv.PasteLink();
            t.Check(csvPage.layers[0].Get(5, 0) == "10", $"Paste link into a CSV from another file pastes the value: {csvPage.layers[0].Get(5, 0)}");
            csv.Undo();

            string costPath = Path.Combine(folder, "cost.csv");
            File.Move(dataPath, costPath);
            SheetBook.Renamed(dataPath, costPath, Directory.EnumerateFiles(folder, "*" + SheetDocument.extension));
            SheetLinks.Renamed(dataPath, costPath, new[] { notePath });
            yield return 2;

            t.Check(page.layers[0].Get(0, 1) == "=[cost.csv]cost!B2+1" && page.layers[0].Get(5, 0) == "=[cost.csv]cost!B2",
                $"renaming a CSV rewrites file and page in a loaded sheet: {page.layers[0].Get(0, 1)}");
            t.Check(SheetBook.calc.Value(page, 0, 1).Display() == "101", "and the reference still reads");
            t.Check(csvPage.name == "cost" && csvPage.layers[0].Get(2, 1) == "=cost!B2*2" && SheetBook.calc.Value(csvPage, 2, 1).Display() == "200",
                $"the CSV's page follows its file name, its own references too: {csvPage.layers[0].Get(2, 1)}");
            t.Check(page.layers[0].Get(0, 2) == "=[data]Data!A1", "a reference to data.sheet.axml is left alone");
            t.Check(SheetDocument.Load(SheetPath("Unopened")).pages[0].Shown(0, 0) == "=[cost.csv]cost!B2", "a sheet on disk is rewritten");
            t.Check(File.ReadAllText(notePath) == "x ![[cost.csv#cost!B2]] $\\sheet{cost.csv#cost!B3}$\n", $"a note's link and formula follow: {File.ReadAllText(notePath)}");

            csv.document.Save(SheetPath("cost"));
            SheetBook.Renamed(costPath, SheetPath("cost"), Directory.EnumerateFiles(folder, "*" + SheetDocument.extension));
            SheetLinks.Renamed(costPath, SheetPath("cost"), new[] { notePath });
            File.Delete(costPath);
            SheetBook.Deleted(costPath);
            yield return 2;

            t.Check(page.layers[0].Get(0, 1) == "=[cost.sheet.axml]cost!B2+1" && SheetBook.calc.Value(page, 0, 1).Display() == "101",
                $"turning the CSV into a sheet points references at the sheet: {page.layers[0].Get(0, 1)}");
            t.Check(File.ReadAllText(notePath) == "x ![[cost.sheet.axml#cost!B2]] $\\sheet{cost.sheet.axml#cost!B3}$\n", $"notes too: {File.ReadAllText(notePath)}");
            t.Check(!csv.document.isCsv && SheetBook.calc.Value(csvPage, 1, 2).Display() == "10", "and as a sheet it reaches other files");

            t.Show(new StackPanelControl());
            SheetBook.findSheet = findBefore;
            foreach (string file in Directory.EnumerateFiles(folder, "*" + SheetDocument.extension))
                SheetBook.Deleted(file);
            Directory.Delete(folder, true);
        }

        [A_XSDActionDependency("Sheet.FixedSize", "Test")]
        private static IEnumerator<int> FixedSize(TestContext t)
        {
            SheetDocument blank = SheetDocument.Blank("Fixed", true);
            t.Check(blank.fixedSize && blank.pages[0].rows == 25 && blank.pages[0].columns == 25, "a new fixed sheet is 25 x 25");
            XElement xml = SheetXml.ToXml(blank);
            t.Check((string?)xml.Attribute("Fixed") == "true" && (string?)xml.Element("Page")!.Attribute("Rows") == "25"
                && (string?)xml.Element("Page")!.Attribute("Columns") == "25", "the type and size are written");
            XElement loose = SheetXml.ToXml(SheetDocument.Blank("Loose"));
            t.Check(loose.Attribute("Fixed") == null && loose.Element("Page")!.Attribute("Rows") == null, "an unfixed sheet writes neither");

            SheetDocument old = SheetXml.Parse(XElement.Parse("<Sheet><Page Name=\"P\"><Layer><Cell At=\"C40\">x</Cell></Layer></Page></Sheet>"));
            t.Check(!old.fixedSize, "a file without Fixed opens unfixed");
            SheetDocument held = SheetXml.Parse(XElement.Parse(
                "<Sheet Fixed=\"true\"><Page Name=\"P\" Rows=\"5\" Columns=\"4\"><Layer><Cell At=\"F9\">x</Cell></Layer></Page></Sheet>"));
            t.Check(held.pages[0].rows == 9 && held.pages[0].columns == 6, "a fixed page grows to hold its cells");

            SheetDocument sheet = SheetDocument.Blank("Small", true);
            sheet.pages[0].rows = 5;
            sheet.pages[0].columns = 4;
            sheet.pages[0].layers[0].Set(0, 0, "Item");
            sheet.pages[0].layers[0].Set(4, 3, "42");
            string path = TempSheet(sheet);

            SheetEditorControl editor = new SheetEditorControl
            {
                preferredWidth = 640f,
                preferredHeight = 360f,
                horizontalAlignment = HorizontalAlignment.Left,
                verticalAlignment = VerticalAlignment.Top
            };
            editor.LoadPath(path);
            t.Show(editor);
            yield return 3;
            SheetPage page = editor.page;
            SheetControl grid = Grid(editor);
            t.Check(page.rows == 5 && page.columns == 4, "the size loads");
            yield return t.Golden("Page", editor);

            editor.Select(10, 10, false);
            t.Check(editor.activeRow == 4 && editor.activeColumn == 3, "selection stops at the page edge");
            editor.Enter(false);
            editor.Tab(false);
            t.Check(editor.activeRow == 4 && editor.activeColumn == 3, "Enter and Tab stay on the last row and column");

            LayoutRect last = grid.CellRect(4, 0);
            Vector2 bottomEdge = new Vector2(last.x + last.width * 0.5f, last.Bottom + 6f);
            yield return t.Drag(grid, bottomEdge, bottomEdge, 1);
            t.Check(page.rows == 6 && page.columns == 4, $"a click on the bottom + adds a row: {page.rows} x {page.columns}");

            SheetGrowSetting grow = SettingsRegistry.Get<SheetSettings>().grow;
            int stepBefore = grow.step;
            grow.step = 3;
            yield return 2;
            LayoutRect corner = grid.CellRect(0, 3);
            Vector2 rightEdge = new Vector2(corner.Right + 6f, corner.y + corner.height * 0.5f);
            yield return t.Drag(grid, rightEdge, rightEdge, 1, Keys.LeftShift);
            grow.step = stepBefore;
            t.Check(page.rows == 6 && page.columns == 7, $"Shift-click on the right + adds the set step: {page.rows} x {page.columns}");
            t.Check(!editor.editing, "two quick clicks on the + edges open no cell");

            editor.Undo();
            t.Check(page.columns == 4, "undo takes the columns back");
            editor.Undo();
            t.Check(page.rows == 5, "and then the row");

            editor.Select(3, 2, false);
            editor.Paste("1\t2\t3\n4\t5\t6\n7\t8\t9");
            t.Check(page.rows == 6 && page.columns == 5 && page.layers[0].Get(5, 4) == "9",
                $"a paste past the edge grows the page: {page.rows} x {page.columns}, {page.layers[0].Get(5, 4)}, active {editor.activeRow},{editor.activeColumn}");
            editor.Undo();
            t.Check(page.rows == 5 && page.columns == 4 && page.layers[0].Get(3, 2) == null, "one undo takes back the paste and the growth");

            editor.Grow(2, 1);
            editor.Save();
            SheetDocument saved = SheetDocument.Load(path);
            t.Check(saved.fixedSize && saved.pages[0].rows == 7 && saved.pages[0].columns == 5, "the grown size is saved");

            t.Show(new StackPanelControl());
            File.Delete(path);
        }

        [A_XSDActionDependency("Sheet.FixedSizeChosen", "Test")]
        private static IEnumerator<int> FixedSizeChosen(TestContext t)
        {
            t.Show(new StackPanelControl());
            (string name, int rows, int columns)? chosen = null;
            bool cancelled = false;

            NoteNameWindow.AskSheet(Engine.primary, "Sized", SheetPage.defaultSize, SheetPage.defaultSize,
                (name, rows, columns) => chosen = (name, rows, columns), () => cancelled = true);
            yield return 2;
            TextBoxControl field = (TextBoxControl)UIEngine.activeControl!;
            List<TextBoxControl> sizes = field.parent!.children.OfType<StackPanelControl>().First()
                .children.OfType<TextBoxControl>().ToList();
            t.Check(NoteNameWindow.isOpen && sizes.Count == 2 && sizes[0].text == "25" && sizes[1].text == "25", "the size starts at 25 x 25");

            sizes[0].text = "8";
            sizes[1].text = "3";
            yield return t.Click(sizes[1]);
            yield return t.Key(Keys.Enter);
            t.Check(!NoteNameWindow.isOpen && chosen == ("Sized", 8, 3), "the chosen size is handed back");

            chosen = null;
            NoteNameWindow.AskSheet(Engine.primary, "Sized", SheetPage.defaultSize, SheetPage.defaultSize,
                (name, rows, columns) => chosen = (name, rows, columns), () => cancelled = true);
            yield return 2;
            sizes[0].text = "0";
            yield return t.Click(sizes[0]);
            yield return t.Key(Keys.Enter);
            t.Check(NoteNameWindow.isOpen && chosen == null, "a zero row count is refused");
            yield return t.Click(sizes[0]);
            yield return t.Key(Keys.Escape);
            t.Check(!NoteNameWindow.isOpen && cancelled, "the prompt cancels");

            NoteNameWindow.Ask(Engine.primary, "Note", _ => { }, null);
            yield return 2;
            t.Check(sizes[0].parent is Control row && row.hidden, "a note prompt shows no size");
            yield return t.Key(Keys.Escape);
        }

        [A_XSDActionDependency("Sheet.GrowPopup", "Test")]
        private static IEnumerator<int> GrowPopup(TestContext t)
        {
            SheetDocument sheet = SheetDocument.Blank("Popup", true);
            sheet.pages[0].rows = 5;
            sheet.pages[0].columns = 4;
            string path = TempSheet(sheet);

            SheetEditorControl editor = new SheetEditorControl
            {
                preferredWidth = 640f,
                preferredHeight = 360f,
                horizontalAlignment = HorizontalAlignment.Left,
                verticalAlignment = VerticalAlignment.Top
            };
            editor.LoadPath(path);
            t.Show(editor);
            yield return 3;
            SheetPage page = editor.page;
            SheetControl grid = Grid(editor);

            LayoutRect corner = grid.CellRect(0, 3);
            yield return t.Click(grid, new Vector2(corner.Right + 10f, corner.y + corner.height * 0.5f), Keys.MouseRight);
            yield return 2;
            t.Check(FocusedCaption() == "Add horizontal", $"a right click on the right + opens the popup on Add horizontal: {FocusedCaption()}");
            yield return t.Type("3");
            yield return t.Key(Keys.Tab);
            t.Check(FocusedCaption() == "Add vertical", $"Tab moves to Add vertical: {FocusedCaption()}");
            yield return t.Type("2");
            yield return t.Key(Keys.Enter);
            yield return 2;
            t.Check(page.rows == 7 && page.columns == 7, $"Enter adds both: {page.rows} x {page.columns}");
            editor.Undo();
            t.Check(page.rows == 5 && page.columns == 4 && !editor.undo.CanUndo, "one undo takes both back");
            yield return 2;

            LayoutRect last = grid.CellRect(4, 0);
            yield return t.Click(grid, new Vector2(last.x + last.width * 0.5f, last.Bottom + 10f), Keys.MouseRight);
            yield return 2;
            t.Check(FocusedCaption() == "Add vertical", $"a right click on the bottom + opens the popup on Add vertical: {FocusedCaption()}");
            yield return t.Type("4");
            yield return t.Key(Keys.Escape);
            yield return 2;
            t.Check(page.rows == 5 && page.columns == 4 && !editor.undo.CanUndo, "Esc adds nothing");
            t.Check(FocusedCaption() == null, "and closes the popup");

            t.Show(new StackPanelControl());
            File.Delete(path);

            static string? FocusedCaption() => UIEngine.activeControl is TextBoxControl box && box.parent is Control row
                ? row.children.OfType<LabelControl>().FirstOrDefault()?.text
                : null;
        }

        [A_XSDActionDependency("Sheet.WheelZoom", "Test")]
        private static IEnumerator<int> WheelZoom(TestContext t)
        {
            string path = TempSheet(SheetDocument.Blank("Zoom"));
            SheetEditorControl editor = ShowSheet(t, path);
            DocumentZoomSetting zoom = SettingsRegistry.Get<UISettings>().documentZoom;
            float before = zoom.percent;
            yield return 2;

            SheetControl grid = Grid(editor);
            SheetPage page = editor.document.pages[0];
            yield return t.Scroll(editor, new Vector2(0f, 1f), new[] { Keys.LeftControl });
            yield return 2;
            t.Check(zoom.percent == before + 10f, $"Ctrl+wheel over a sheet steps the content zoom: {before} -> {zoom.percent}");

            zoom.percent = 200f;
            SettingsRegistry.Apply();
            yield return 2;
            LayoutRect b1 = grid.CellRect(0, 1);
            t.Check(MathF.Abs(b1.width - page.ColumnWidth(1) * 2f) < 0.5f && MathF.Abs(b1.height - page.RowHeight(0) * 2f) < 0.5f,
                $"at 200% a cell is drawn twice its size: {b1.width} x {b1.height}");

            float edge = b1.Right;
            float headerY = grid.arrangedRect.y + grid.HeaderHeight * 0.5f;
            yield return t.Drag(grid, new Vector2(edge, headerY), new Vector2(edge + 60f, headerY));
            yield return 2;
            t.Check(page.ColumnWidth(1) == SheetPage.defaultColumnWidth + 30f, $"a 60 px resize at 200% stores 30: {page.ColumnWidth(1)}");

            zoom.percent = before;
            SettingsRegistry.Apply();
        }

        [A_XSDActionDependency("Sheet.InsertShifts", "Test")]
        private static IEnumerator<int> InsertShifts(TestContext t)
        {
            string folder = TempFolder();
            Func<string?, string, string?>?findBefore = SheetBook.findSheet;
            Func<string?, IEnumerable<string>>?sheetsBefore = SheetBook.vaultSheets;
            Func<string?, IEnumerable<string>>?notesBefore = SheetBook.vaultNotes;
            SheetBook.findSheet = FolderResolver(folder);
            SheetBook.vaultSheets = _ =>Directory.EnumerateFiles(folder, "*" + SheetDocument.extension);
            SheetBook.vaultNotes = _ =>Directory.EnumerateFiles(folder, "*.md");
            string PathOf(string name) => Path.Combine(folder, name + SheetDocument.extension);

            SheetDocument budget = SheetDocument.Blank("Budget", true);
            budget.pages[0].name = "Data";
            SheetLayer cells = budget.pages[0].layers[0];
            cells.Set(0, 0, "1");
            cells.Set(1, 0, "2");
            cells.Set(2, 0, "3");
            cells.Set(3, 0, "=SUM(A1:A3)");
            cells.Set(0, 1, "=A3*10");
            budget.pages[0].SetFormat(2, 0, new SheetFormat(true, null, null));
            budget.pages[0].rowHeights[2] = 40f;
            budget.Save(PathOf("Budget"));

            SheetDocument summary = SheetDocument.Blank("Summary");
            summary.pages[0].layers[0].Set(0, 0, "=[Budget]Data!A3+1");
            summary.pages[0].layers[0].Set(1, 0, "=SUM([Budget]Data!A1:A3)");
            summary.Save(PathOf("Summary"));

            SheetDocument unopened = SheetDocument.Blank("Unopened");
            unopened.pages[0].layers[0].Set(0, 0, "=[Budget]Data!A3");
            unopened.Save(PathOf("Unopened"));

            string notePath = Path.Combine(folder, "a.md");
            string note = "x ![[Budget.sheet.axml#Data!A2:A3]] $\\sheet{Budget.sheet.axml#Data!A3}$\n";
            File.WriteAllText(notePath, note);

            SheetEditorControl editor = ShowSheet(t, PathOf("Budget"));
            SheetPage summaryPage = SheetBook.Get(PathOf("Summary")).pages[0];
            yield return 2;
            SheetPage page = editor.page;
            int rowsBefore = page.rows;
            cells = page.layers[0];

            editor.Select(1, 0, false);
            editor.Select(2, 0, true);
            editor.Insert(false);
            yield return 2;
            t.Check(cells.Get(1, 0) == null && cells.Get(2, 0) == null && cells.Get(3, 0) == "2" && cells.Get(4, 0) == "3",
                "two empty rows go in above the selection");
            t.Check(cells.Get(5, 0) == "=SUM(A1:A5)" && cells.Get(0, 1) == "=A5*10", $"its own formulas follow: {cells.Get(5, 0)} {cells.Get(0, 1)}");
            t.Check(SheetBook.calc.Value(page, 5, 0).Display() == "6" && SheetBook.calc.Value(page, 0, 1).Display() == "30", "and keep their values");
            t.Check(page.Format(4, 0).bold && page.rowHeights.TryGetValue(4, out float height) && height == 40f && !page.rowHeights.ContainsKey(2),
                "formats and row heights move with their rows");
            t.Check(page.rows == rowsBefore + 2, "a fixed page grows by the rows inserted");
            t.Check(summaryPage.layers[0].Get(0, 0) == "=[Budget]Data!A5+1" && summaryPage.layers[0].Get(1, 0) == "=SUM([Budget]Data!A1:A5)",
                $"a loaded sheet's references follow: {summaryPage.layers[0].Get(1, 0)}");
            t.Check(SheetBook.calc.Value(summaryPage, 0, 0).Display() == "4", "and keep their values");
            t.Check(SheetDocument.Load(PathOf("Unopened")).pages[0].Shown(0, 0) == "=[Budget]Data!A5", "a sheet on disk is rewritten");
            t.Check(File.ReadAllText(notePath) == "x ![[Budget.sheet.axml#Data!A4:A5]] $\\sheet{Budget.sheet.axml#Data!A5}$\n",
                $"a note's link and formula follow: {File.ReadAllText(notePath)}");

            editor.Undo();
            t.Check(cells.Get(2, 0) == "3" && cells.Get(3, 0) == "=SUM(A1:A3)" && cells.Get(0, 1) == "=A3*10" && page.rows == rowsBefore
                && page.rowHeights.ContainsKey(2), "undo takes the rows out again");
            t.Check(summaryPage.layers[0].Get(0, 0) == "=[Budget]Data!A3+1" && File.ReadAllText(notePath) == note, "and moves every reference back");

            editor.Select(0, 0, false);
            editor.Insert(true);
            t.Check(cells.Get(0, 0) == null && cells.Get(0, 1) == "1" && cells.Get(0, 2) == "=B3*10" && cells.Get(3, 1) == "=SUM(B1:B3)",
                "a column goes in left of the selection and its formulas follow");
            t.Check(summaryPage.layers[0].Get(0, 0) == "=[Budget]Data!B3+1", "a loaded sheet's references follow the column");
            editor.Undo();
            t.Check(cells.Get(0, 0) == "1" && cells.Get(0, 1) == "=A3*10", "undo takes the column out");

            t.Show(new StackPanelControl());
            SheetBook.findSheet = findBefore;
            SheetBook.vaultSheets = sheetsBefore;
            SheetBook.vaultNotes = notesBefore;
            foreach (string file in Directory.EnumerateFiles(folder, "*" + SheetDocument.extension))
                SheetBook.Deleted(file);
            Directory.Delete(folder, true);
        }

        private static string BudgetSheet(string folder)
        {
            SheetDocument budget = SheetDocument.Blank("Budget");
            budget.pages[0].name = "Data";
            SheetLayer cells = budget.pages[0].layers[0];
            cells.Set(0, 0, "10");
            cells.Set(0, 1, "=A1*2");
            cells.Set(1, 0, "text");
            cells.Set(1, 1, "=1/0");
            string path = Path.Combine(folder, "Budget" + SheetDocument.extension);
            budget.Save(path);
            return path;
        }

        private static List<BlockControl> NoteBlocks(DocumentEditorControl editor) =>
            NoteContent(editor).children.OfType<BlockControl>().ToList();

        private static DocumentControl NoteContent(DocumentEditorControl editor) => editor.children.OfType<DocumentControl>().First();

        private static string TempFolder()
        {
            string folder = Path.Combine(Path.GetTempPath(), $"aurora-sheets-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static Func<string?, string, string?> FolderResolver(string folder) => (_, file) =>
        {
            if (file.EndsWith(SheetDocument.extension, StringComparison.OrdinalIgnoreCase)) file = file[..^SheetDocument.extension.Length];
            string path = Path.Combine(folder, file + SheetDocument.extension);
            return File.Exists(path) ? path : null;
        };

        private static T? Find<T>(Entity entity, Func<T, bool> match) where T : class
        {
            if (entity is T found && match(found)) return found;
            foreach (Entity child in entity.children)
                if (Find(child, match) is T hit) return hit;
            return null;
        }

        private static string TempSheet(SheetDocument sheet)
        {
            string path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"aurora-sheet-{Guid.NewGuid():N}{SheetDocument.extension}"));
            sheet.Save(path);
            return path;
        }

        private static SheetEditorControl ShowSheet(TestContext t, string path)
        {
            SheetEditorControl editor = new SheetEditorControl
            {
                horizontalAlignment = HorizontalAlignment.Stretch,
                verticalAlignment = VerticalAlignment.Stretch
            };
            editor.LoadPath(path);
            t.Show(editor);
            return editor;
        }

        private static SheetControl Grid(SheetEditorControl editor) => editor.scroller.children.OfType<SheetControl>().First();

        private static int Count(Entity entity)
        {
            int count = 1;
            foreach (Entity child in entity.children)
                count += Count(child);
            return count;
        }

        // Text of every label laid out on screen.
        private static HashSet<string> Labels(Entity entity)
        {
            HashSet<string> found = new HashSet<string>();
            Collect(entity, found);
            return found;

            static void Collect(Entity entity, HashSet<string> into)
            {
                if (entity is LabelControl label && label.arrangedRect.width > 0f) into.Add(label.text);
                foreach (Entity child in entity.children)
                    Collect(child, into);
            }
        }
        #endregion
    }
}
