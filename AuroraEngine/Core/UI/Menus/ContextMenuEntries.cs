using ArctisAurora.Core.Registry;

namespace ArctisAurora.Core.UI.Menus
{
    // A menu document's root: what a control naming it offers on right click.
    [A_XSDType("ContextMenu", "UI", AllowedChildren = typeof(ContextMenuEntry))]
    public class ContextMenu
    {
        public readonly List<ContextMenuEntry> entries = new List<ContextMenuEntry>();
    }

    [A_XSDType("ContextEntry", "UI", isAbstract: true)]
    public abstract class ContextMenuEntry
    {
        [A_XSDElementProperty("When", "UI", "A condition registered with ContextMenus.RegisterCondition; the entry shows only while it holds for the control the menu opened on.")]
        public string? when;
    }

    [A_XSDType("ContextButton", "UI")]
    public class ContextMenuButton : ContextMenuEntry
    {
        [A_XSDElementProperty("Text", "UI", "The caption shown on the row.")]
        public string text = string.Empty;

        [A_XSDElementProperty("Action", "UI", "Runs when the row is clicked.")]
        public Action? action;

        public ContextMenuButton() { }

        public ContextMenuButton(string text, Action action)
        {
            this.text = text;
            this.action = action;
        }
    }

    [A_XSDType("ContextLine", "UI")]
    public class ContextMenuLine : ContextMenuEntry { }

    // A control shown as itself among a menu's rows; built in code, never authored.
    public class ContextMenuContent : ContextMenuEntry
    {
        public readonly Control content;

        public ContextMenuContent(Control content) => this.content = content;
    }

    [A_XSDType("ContextSubmenu", "UI", AllowedChildren = typeof(ContextMenuEntry))]
    public class ContextMenuSubmenu : ContextMenuEntry
    {
        [A_XSDElementProperty("Text", "UI", "The caption shown on the row.")]
        public string text = string.Empty;

        [A_XSDElementProperty("Source", "UI", "A builder registered with ContextMenus.RegisterSource; its entries are built each time the submenu opens, in place of the authored ones.")]
        public string? source;

        public readonly List<ContextMenuEntry> entries = new List<ContextMenuEntry>();
    }
}
