using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI.Containers.Tabs;
using ArctisAurora.EngineWork;
using ArctisAurora.EngineWork.Rendering;

namespace ArctisAurora.Core.UI.Notes
{
    // Settling open notes: writing the ones that were edited. Shared by the shutdown sequence, which
    // settles every window, and by closing a single window, which settles only that one.
    public static class NoteActions
    {
        #region ---- shutdown steps ----
        [A_XSDActionDependency("Notes.SaveEdited", "Shutdown", "Writes every open note that was edited since its last save")]
        public static bool SaveEdited()
        {
            foreach (RenderWindow window in Engine.windows.Values)
                SaveEditedIn(window.ui?.uiRoot);

            return true;
        }
        #endregion

        // Every window settled in turn, then onSettled; cancelling a prompt stops the chain.
        public static void SettleAll(Action onSettled)
        {
            SettleNext(Engine.windows.Values.ToList(), 0, onSettled);
        }

        private static void SettleNext(List<RenderWindow> windows, int index, Action onSettled)
        {
            if (index >= windows.Count) { onSettled(); return; }
            SettleWindow(windows[index], () => SettleNext(windows, index + 1, onSettled));
        }

        #region ---- one window ----
        // Settles a single window and then runs onSettled, for a window closing on its own rather
        // than the application going.
        internal static void SettleWindow(RenderWindow window, Action onSettled)
        {
            SaveEditedIn(window.ui?.uiRoot);
            onSettled?.Invoke();
        }
        #endregion

        // Only what was actually edited. A first save of a hand-authored note does not reproduce its
        // bytes, so writing untouched ones would rewrite every open file on every close.
        private static void SaveEditedIn(ArctisAurora.Core.UI.Control control)
        {
            if (control == null) return;
            if (control is IFileEditor file && file.isDirty)
                file.Save();

            foreach (Entity child in control.children)
                if (child is ArctisAurora.Core.UI.Control childControl)
                    SaveEditedIn(childControl);
        }
    }
}
