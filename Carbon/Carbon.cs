using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.EngineWork;
using Carbon.Editor;
using Carbon.Editor.CustomControls;

namespace Carbon
{
    internal class Carbon
    {
        static void Main(string[] args)
        {
            Engine engine = new Engine();
            XSDGenerator.GenerateXSD();

            SettingsRegistry.SetWriteRoot(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Arktis", "Carbon", "Settings"));

            engine.Init(false);
            InputHandler.SetActiveKeybindGroup("InputMap");

            WindowRoot root = (WindowRoot)Control.ParseXML("main");

            Engine.primary.uiDocument = "main";
            Engine.primary.ui.uiRoot = root;

            Wire(root);

            engine.Run();
        }

        // The views only ever hear about a capture through Comparison, so no control looks another up.
        private static void Wire(WindowRoot root)
        {
            SessionListControl sessions = (SessionListControl)root.FindByName("Sessions");

            Comparison.Attach(
                (FrameStripControl)root.FindByName("Strip"),
                (FrameStripControl)root.FindByName("CompareStrip"),
                (SpanChartControl)root.FindByName("Flame"),
                (SpanChartControl)root.FindByName("Timeline"),
                (ZoneTableControl)root.FindByName("Zones"),
                (SliderControl)root.FindByName("Scale"),
                (LabelControl)root.FindByName("Offset"),
                (LabelControl)root.FindByName("ScaleValue"),
                (LabelControl)root.FindByName("Pools"));

            sessions.onSessionLoaded = Comparison.Load;

            TestResultsControl results = (TestResultsControl)root.FindByName("TestView");
            ((TestRunListControl)root.FindByName("TestSide")).onRunLoaded = results.ShowRun;
            results.onOpenCapture = directory =>
            {
                CarbonActions.Switch(true);
                sessions.Load(directory);
            };

            CarbonActions.Switch(true);
        }
    }
}
