using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI.Document;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.EngineWork;

namespace ArctisAurora.Core.UI.Sheets
{
    // Keybind actions for sheets. Each does nothing unless a sheet holds the active control.
    public static class SheetActions
    {
        #region ---- moving ----
        [A_XSDActionDependency("Sheet.Up", "Input", "Moves the active cell up; Shift extends the selection")]
        public static void Up() => Move(-1, 0);

        [A_XSDActionDependency("Sheet.Down", "Input", "Moves the active cell down; Shift extends the selection")]
        public static void Down() => Move(1, 0);

        [A_XSDActionDependency("Sheet.Left", "Input", "Moves the active cell left; Shift extends the selection")]
        public static void Left() => Move(0, -1);

        [A_XSDActionDependency("Sheet.Right", "Input", "Moves the active cell right; Shift extends the selection")]
        public static void Right() => Move(0, 1);

        private static void Move(int rows, int columns)
        {
            SheetEditorControl? sheet = Editor();
            if (sheet == null || sheet.editing) return;

            sheet.Move(rows, columns, InputHandler.instance.IsModifierDown(InputModifier.Extend));
        }

        [A_XSDActionDependency("Sheet.Enter", "Input", "Commits the cell being edited and steps down; Shift steps up")]
        public static void Enter() => Editor()?.Enter(InputHandler.instance.IsModifierDown(InputModifier.Extend));

        [A_XSDActionDependency("Sheet.Tab", "Input", "Commits the cell being edited and steps right")]
        public static void Tab()
        {
            if (!SheetGrowPopup.Tab()) Editor()?.Tab(false);
        }

        [A_XSDActionDependency("Sheet.TabBack", "Input", "Commits the cell being edited and steps left")]
        public static void TabBack()
        {
            if (!SheetGrowPopup.Tab()) Editor()?.Tab(true);
        }

        [A_XSDActionDependency("Sheet.SelectAll", "Input", "Selects every used cell of the sheet")]
        public static void SelectAll()
        {
            SheetEditorControl? sheet = Editor();
            if (sheet != null && !sheet.editing) sheet.SelectAll();
        }
        #endregion

        #region ---- editing ----
        [A_XSDActionDependency("Sheet.Write", "Input", "Starts editing the active cell with the typed characters")]
        public static void Write() => Editor()?.TypeOver(InputHandler.charInputReadQueue);

        [A_XSDActionDependency("Sheet.Edit", "Input", "Edits the active cell, keeping its text")]
        public static void Edit() => Editor()?.BeginEdit(true);

        [A_XSDActionDependency("Sheet.Clear", "Input", "Empties the selected cells")]
        public static void Clear() => Editor()?.Clear();

        [A_XSDActionDependency("Sheet.Undo", "Input", "Reverses the last change to the sheet")]
        public static void Undo()
        {
            SheetEditorControl? sheet = Editor();
            if (sheet != null && !sheet.editing) sheet.Undo();
        }

        [A_XSDActionDependency("Sheet.Redo", "Input", "Reapplies the last change undone in the sheet")]
        public static void Redo()
        {
            SheetEditorControl? sheet = Editor();
            if (sheet != null && !sheet.editing) sheet.Redo();
        }

        [A_XSDActionDependency("Sheet.PasteLink", "Input", "Pastes formulas that read the copied cells; a plain paste outside a sheet")]
        public static void PasteLink()
        {
            SheetEditorControl? sheet = Editor();
            if (sheet == null || !sheet.PasteLink()) TextInputActions.PasteLink();
        }

        [A_XSDActionDependency("Sheet.Save", "Input", "Writes the sheet back to its file")]
        public static void Save() => Editor()?.Save();

        [A_XSDActionDependency("Sheet.InsertRowsAbove", "Input", "Inserts as many empty rows as are selected, above the selection")]
        public static void InsertRowsAbove() => Editor()?.Insert(false);

        [A_XSDActionDependency("Sheet.InsertColumnsLeft", "Input", "Inserts as many empty columns as are selected, left of the selection")]
        public static void InsertColumnsLeft() => Editor()?.Insert(true);
        #endregion

        #region ---- formatting ----
        [A_XSDActionDependency("Sheet.Bold", "Input", "Bolds the selected cells, or unbolds them when the active cell is bold")]
        public static void Bold() => Editor()?.ToggleBold();

        [A_XSDActionDependency("Sheet.FillNone", "Input", "Removes the fill of the selected cells")]
        public static void FillNone() => Fill("None");

        [A_XSDActionDependency("Sheet.FillYellow", "Input", "Fills the selected cells yellow")]
        public static void FillYellow() => Fill("Yellow");

        [A_XSDActionDependency("Sheet.FillGreen", "Input", "Fills the selected cells green")]
        public static void FillGreen() => Fill("Green");

        [A_XSDActionDependency("Sheet.FillBlue", "Input", "Fills the selected cells blue")]
        public static void FillBlue() => Fill("Blue");

        [A_XSDActionDependency("Sheet.FillPink", "Input", "Fills the selected cells pink")]
        public static void FillPink() => Fill("Pink");

        [A_XSDActionDependency("Sheet.FillOrange", "Input", "Fills the selected cells orange")]
        public static void FillOrange() => Fill("Orange");

        [A_XSDActionDependency("Sheet.FillPurple", "Input", "Fills the selected cells purple")]
        public static void FillPurple() => Fill("Purple");

        [A_XSDActionDependency("Sheet.FormatGeneral", "Input", "Shows the selected numbers as typed or calculated")]
        public static void FormatGeneral() => Editor()?.SetNumberFormat(null);

        [A_XSDActionDependency("Sheet.FormatNumber", "Input", "Shows the selected numbers with two decimals and grouped thousands")]
        public static void FormatNumber() => Editor()?.SetNumberFormat("#,##0.00");

        [A_XSDActionDependency("Sheet.FormatPercent", "Input", "Shows the selected numbers as percentages")]
        public static void FormatPercent() => Editor()?.SetNumberFormat("0.00%");

        [A_XSDActionDependency("Sheet.FormatCurrency", "Input", "Shows the selected numbers in euros")]
        public static void FormatCurrency() => Editor()?.SetNumberFormat("€#,##0.00");

        // A note highlight swatch by its caption.
        private static void Fill(string caption) =>
            Editor()?.SetFill(Array.Find(DocumentToolbarControl.highlightOptions, option => option.caption == caption).hex);
        #endregion

        // Nearest sheet at or above the active control.
        internal static SheetEditorControl? Editor()
        {
            for (Control? control = UIEngine.activeControl; control != null; control = control.parent as Control)
                if (control is SheetEditorControl sheet) return sheet;
            return null;
        }
    }
}
