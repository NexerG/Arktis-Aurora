using ArctisAurora.Core.UI.Containers.Workspace;

namespace ArctisAurora.Core.UI.Containers.Tabs
{
    // An editor a tab holds, as the tab, session and rename code see it.
    public interface IFileEditor
    {
        string? path { get; }

        bool isDirty { get; }

        void Save();

        // Follows a rename on disk.
        void Repath(string newPath, string name);

        SessionTab ViewState();

        void RestoreView(SessionTab view);

        // The tool group shown on the pane's tab row while this editor's tab is active.
        Control? tools => null;
    }
}
