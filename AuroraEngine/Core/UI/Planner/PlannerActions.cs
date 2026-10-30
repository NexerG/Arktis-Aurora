using ArctisAurora.Core.Registry;

namespace ArctisAurora.Core.UI.Planner
{
    // Keybind and menu actions for planners. Each does nothing unless a planner holds the active control.
    public static class PlannerActions
    {
        [A_XSDActionDependency("Planner.Undo", "Input", "Reverses the last change to the planner")]
        public static void Undo() => Editor()?.Undo();

        [A_XSDActionDependency("Planner.Redo", "Input", "Reapplies the last change undone in the planner")]
        public static void Redo() => Editor()?.Redo();

        [A_XSDActionDependency("Planner.Delete", "Input", "Deletes the selected ticket")]
        public static void Delete() => Editor()?.DeleteSelected();

        [A_XSDActionDependency("Planner.Edit", "Input", "Opens the selected ticket's popup")]
        public static void Edit() => Editor()?.EditSelected();

        [A_XSDActionDependency("Planner.Collapse", "Input", "Folds away the expanded ticket's description")]
        public static void Collapse() => Editor()?.Expand(null);

        [A_XSDActionDependency("Planner.EditCategory", "Input", "Opens the popup of the category last pressed on")]
        public static void EditCategory() => Editor()?.EditPickedCategory();

        [A_XSDActionDependency("Planner.AddTicketHere", "Input", "Opens the popup for a new ticket in the category last pressed on")]
        public static void AddTicketHere() => Editor()?.AddTicketToPickedCategory();

        [A_XSDActionDependency("Planner.DeleteCategoryWithTickets", "Input", "Asks, then deletes the category last pressed on and all its tickets")]
        public static void DeleteCategoryWithTickets() => Editor()?.ConfirmDeletePickedCategory();

        [A_XSDActionDependency("Planner.AddCategory", "Input", "Opens the popup for a new category")]
        public static void AddCategory() => Editor()?.AddCategory();

        [A_XSDActionDependency("Planner.Save", "Input", "Writes the planner back to its file")]
        public static void Save() => Editor()?.Save();

        // Nearest planner at or above the active control.
        internal static PlannerEditorControl? Editor()
        {
            for (Control? control = UIEngine.activeControl; control != null; control = control.parent as Control)
                if (control is PlannerEditorControl planner) return planner;
            return null;
        }
    }
}
