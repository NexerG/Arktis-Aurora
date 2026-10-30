using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Text;

namespace ArctisAurora.Core.UI.Files
{
    // One row of a file browser, carrying the entry it was built for.
    [A_XSDType("FileRow", "UI")]
    public class FileRowControl : ButtonControl
    {
        // Public, because a context-menu action is zero-argument and reaches the entry only through
        // the row ContextMenus.target sits in.
        public FileObject file = null!;
        public FileBrowserControl browser = null!;
        internal EditableLabelControl label = null!;
        internal IconControl gutter = null!;
    }
}
