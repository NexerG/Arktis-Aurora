using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.EngineWork.Rendering;

namespace Thorium.Editor
{
    // Status bar mockup: storage, connection and git items with no backing yet.
    public static class StatusBarActions
    {
        private static readonly string[] stores = { "Local machine", "Local server", "Online server" };
        private static readonly string[] storeIcons = { "monitor", "server", "cloud" };

        // mockup state
        private static int store = 2;
        private static bool connected = true;
        private static bool pending = true;

        private static Control Find(string name)
        {
            RenderWindow window = UIEngine.WindowOf(UIEngine.hovering);
            return window?.ui.uiRoot?.FindByName(name);
        }

        [A_XSDActionDependency("Status.CycleStore", "UI", "Cycles the status bar's storage location")]
        public static void CycleStore()
        {
            store = (store + 1) % stores.Length;
            if (Find("StatusStore") is LabelControl label) label.text = stores[store];
            if (Find("StatusStoreIcon") is IconControl icon) icon.iconName = storeIcons[store];

            Control conn = Find("StatusConnection");
            if (store == 0) conn?.Hide();
            else conn?.Show();
        }

        [A_XSDActionDependency("Status.ToggleConnection", "UI", "Flips the status bar's connection dot")]
        public static void ToggleConnection()
        {
            connected = !connected;
            if (Find("StatusConnectionDot") is Control dot) dot.colorHex = connected ? "#3C7A3A" : "#C42B1E";
        }

        [A_XSDActionDependency("Status.TogglePending", "UI", "Flips the status bar's git state")]
        public static void TogglePending()
        {
            pending = !pending;
            if (Find("StatusGitDot") is Control dot) dot.colorHex = pending ? "#D9A21B" : "#3C7A3A";
            if (Find("StatusGit") is LabelControl label) label.text = pending ? "Changes pending" : "Up to date";
        }
    }
}
