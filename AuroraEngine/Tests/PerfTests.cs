using ArctisAurora.Core.Animation;
using ArctisAurora.Core.Diagnostics;
using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Testing;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Containers.Tabs;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Document;
using ArctisAurora.Core.UI.Notes;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.EngineWork;
using System.Numerics;
using System.Text;

namespace ArctisAurora.Tests
{
    internal static class PerfTests
    {
        // column shape and timeline, in ticks
        private const int labelCount = 1000;
        private const int warmupTicks = 30;
        private const int measuredTicks = 120;

        // note shape and rewrap step
        private const int blockCount = 1000;
        private const int blockChars = 1000;
        private const float noteWidth = 800f;
        private const float rewrapStep = 8f;
        private const float pageWidthMm = 210f;
        private const float rewrapStepMm = 1f;

        // saved block, the edit that outgrows its slack, and saves before measuring
        private const int saveBlock = 500;
        private const int shiftChars = 2000;
        private const int saveWarmupTicks = 150;

        // button grid shape
        private const int buttonCount = 5000;
        private const int rowLength = 100;
        private const float buttonSize = 16f;

        // per-control grid shape and table note shape
        private const int controlCount = 1000;
        private const int controlsPerRow = 25;
        private const float controlGridWidth = 1000f;
        private const float controlHeight = 16f;
        private const int tableCount = 100;
        private const int tableSide = 3;

        [A_XSDActionDependency("Perf.RelayoutLabels", "Test")]
        private static IEnumerator<int> RelayoutLabels(TestContext t)
        {
            StackPanelControl column = new StackPanelControl
            {
                preferredWidth = 400f,
                horizontalAlignment = HorizontalAlignment.Left,
                verticalAlignment = VerticalAlignment.Top
            };
            for (int i = 0; i < labelCount; i++)
                column.AddChild(new LabelControl { text = $"Label {i}", preferredHeight = 16f });
            t.Show(column);
            yield return 2;

            for (int i = 0; i < warmupTicks; i++)
            {
                column.preferredWidth = 400f + i % 2;
                yield return 1;
            }

            t.StartMeasure();
            for (int i = 0; i < measuredTicks; i++)
            {
                column.preferredWidth = 400f + i % 2;
                yield return 1;
            }
            yield return t.EndMeasure();
        }

        [A_XSDActionDependency("Perf.TypeLargeNote", "Test")]
        private static IEnumerator<int> TypeLargeNote(TestContext t)
        {
            OpenNote(t);
            yield return 2;

            for (int i = 0; i < warmupTicks; i++)
            {
                TypeOne(i);
                yield return 1;
            }

            t.StartMeasure();
            for (int i = 0; i < measuredTicks; i++)
            {
                TypeOne(i);
                yield return 1;
            }
            yield return t.EndMeasure();
            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("Perf.RewrapLargeNote", "Test")]
        private static IEnumerator<int> RewrapLargeNote(TestContext t)
        {
            DocumentEditorControl editor = OpenNote(t);
            yield return 2;

            for (int i = 0; i < warmupTicks; i++)
            {
                editor.SetPage(new PageLayout { size = PageSize.Custom, width = pageWidthMm - i % 2 * rewrapStepMm });
                yield return 1;
            }

            t.StartMeasure();
            for (int i = 1; i <= measuredTicks; i++)
            {
                int narrowed = i <= measuredTicks / 2 ? i : measuredTicks - i;
                editor.SetPage(new PageLayout { size = PageSize.Custom, width = pageWidthMm - narrowed * rewrapStepMm });
                yield return 1;
            }
            yield return t.EndMeasure();
            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("Perf.ResizeLargeNote", "Test")]
        private static IEnumerator<int> ResizeLargeNote(TestContext t)
        {
            DocumentEditorControl editor = OpenNote(t);
            yield return 2;

            for (int i = 0; i < warmupTicks; i++)
            {
                editor.preferredWidth = noteWidth - i % 2 * rewrapStep;
                yield return 1;
            }

            t.StartMeasure();
            for (int i = 1; i <= measuredTicks; i++)
            {
                int narrowed = i <= measuredTicks / 2 ? i : measuredTicks - i;
                editor.preferredWidth = noteWidth - narrowed * rewrapStep;
                yield return 1;
            }
            yield return t.EndMeasure();
            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("Perf.SaveLargeNote.InPlace", "Test")]
        private static IEnumerator<int> SaveLargeNoteInPlace(TestContext t) => SaveSegments(t, ToggleChars(1));

        [A_XSDActionDependency("Perf.SaveLargeNote.Shift", "Test")]
        private static IEnumerator<int> SaveLargeNoteShift(TestContext t) => SaveSegments(t, ToggleChars(shiftChars));

        [A_XSDActionDependency("Perf.AnimationBurst", "Test")]
        private static IEnumerator<int> AnimationBurst(TestContext t)
        {
            List<ButtonControl> buttons = ShowGrid(t);
            yield return 2;

            foreach (ButtonControl button in buttons)
                Animations.Tween(button, "state", new Vector4(2f, 0f, 0f, 0f), warmupTicks / 60f, Curve.Ease(EaseKind.CubicInOut));
            yield return warmupTicks;

            t.StartMeasure();
            foreach (ButtonControl button in buttons)
                Animations.Tween(button, "state", Vector4.Zero, measuredTicks / 60f, Curve.Ease(EaseKind.CubicInOut));
            yield return measuredTicks;
            yield return t.EndMeasure();
            StopAll(buttons);
        }

        [A_XSDActionDependency("Perf.AnimationLayoutClip", "Test")]
        private static IEnumerator<int> AnimationLayoutClip(TestContext t)
        {
            List<ButtonControl> buttons = ShowGrid(t);
            yield return 2;

            foreach (ButtonControl button in buttons)
                Animations.Play(button, "profile-margin", false);
            yield return warmupTicks;

            t.StartMeasure();
            yield return measuredTicks;
            yield return t.EndMeasure();
            StopAll(buttons);
        }

        #region controls
        [A_XSDActionDependency("Perf.Controls.Panel.Static", "Test")]
        private static IEnumerator<int> PanelStatic(TestContext t) => MeasureControls(t, MakePanel, false);
        [A_XSDActionDependency("Perf.Controls.Panel.Relayout", "Test")]
        private static IEnumerator<int> PanelRelayout(TestContext t) => MeasureControls(t, MakePanel, true);

        [A_XSDActionDependency("Perf.Controls.Button.Static", "Test")]
        private static IEnumerator<int> ButtonStatic(TestContext t) => MeasureControls(t, MakeButton, false);
        [A_XSDActionDependency("Perf.Controls.Button.Relayout", "Test")]
        private static IEnumerator<int> ButtonRelayout(TestContext t) => MeasureControls(t, MakeButton, true);

        [A_XSDActionDependency("Perf.Controls.CheckBox.Static", "Test")]
        private static IEnumerator<int> CheckBoxStatic(TestContext t) => MeasureControls(t, MakeCheckBox, false);
        [A_XSDActionDependency("Perf.Controls.CheckBox.Relayout", "Test")]
        private static IEnumerator<int> CheckBoxRelayout(TestContext t) => MeasureControls(t, MakeCheckBox, true);

        [A_XSDActionDependency("Perf.Controls.Slider.Static", "Test")]
        private static IEnumerator<int> SliderStatic(TestContext t) => MeasureControls(t, MakeSlider, false);
        [A_XSDActionDependency("Perf.Controls.Slider.Relayout", "Test")]
        private static IEnumerator<int> SliderRelayout(TestContext t) => MeasureControls(t, MakeSlider, true);

        [A_XSDActionDependency("Perf.Controls.Dropdown.Static", "Test")]
        private static IEnumerator<int> DropdownStatic(TestContext t) => MeasureControls(t, MakeDropdown, false);
        [A_XSDActionDependency("Perf.Controls.Dropdown.Relayout", "Test")]
        private static IEnumerator<int> DropdownRelayout(TestContext t) => MeasureControls(t, MakeDropdown, true);

        [A_XSDActionDependency("Perf.Controls.Expander.Static", "Test")]
        private static IEnumerator<int> ExpanderStatic(TestContext t) => MeasureControls(t, MakeExpander, false);
        [A_XSDActionDependency("Perf.Controls.Expander.Relayout", "Test")]
        private static IEnumerator<int> ExpanderRelayout(TestContext t) => MeasureControls(t, MakeExpander, true);

        [A_XSDActionDependency("Perf.Controls.KeyCapture.Static", "Test")]
        private static IEnumerator<int> KeyCaptureStatic(TestContext t) => MeasureControls(t, MakeKeyCapture, false);
        [A_XSDActionDependency("Perf.Controls.KeyCapture.Relayout", "Test")]
        private static IEnumerator<int> KeyCaptureRelayout(TestContext t) => MeasureControls(t, MakeKeyCapture, true);

        [A_XSDActionDependency("Perf.Controls.Icon.Static", "Test")]
        private static IEnumerator<int> IconStatic(TestContext t) => MeasureControls(t, MakeIcon, false);
        [A_XSDActionDependency("Perf.Controls.Icon.Relayout", "Test")]
        private static IEnumerator<int> IconRelayout(TestContext t) => MeasureControls(t, MakeIcon, true);

        [A_XSDActionDependency("Perf.Controls.Label.Static", "Test")]
        private static IEnumerator<int> LabelStatic(TestContext t) => MeasureControls(t, MakeLabel, false);
        [A_XSDActionDependency("Perf.Controls.Label.Relayout", "Test")]
        private static IEnumerator<int> LabelRelayout(TestContext t) => MeasureControls(t, MakeLabel, true);

        [A_XSDActionDependency("Perf.Controls.TextBox.Static", "Test")]
        private static IEnumerator<int> TextBoxStatic(TestContext t) => MeasureControls(t, MakeTextBox, false);
        [A_XSDActionDependency("Perf.Controls.TextBox.Relayout", "Test")]
        private static IEnumerator<int> TextBoxRelayout(TestContext t) => MeasureControls(t, MakeTextBox, true);

        [A_XSDActionDependency("Perf.Controls.EditableLabel.Static", "Test")]
        private static IEnumerator<int> EditableLabelStatic(TestContext t) => MeasureControls(t, MakeEditableLabel, false);
        [A_XSDActionDependency("Perf.Controls.EditableLabel.Relayout", "Test")]
        private static IEnumerator<int> EditableLabelRelayout(TestContext t) => MeasureControls(t, MakeEditableLabel, true);

        [A_XSDActionDependency("Perf.Controls.StackPanel.Static", "Test")]
        private static IEnumerator<int> StackPanelStatic(TestContext t) => MeasureControls(t, MakeStackPanel, false);
        [A_XSDActionDependency("Perf.Controls.StackPanel.Relayout", "Test")]
        private static IEnumerator<int> StackPanelRelayout(TestContext t) => MeasureControls(t, MakeStackPanel, true);

        [A_XSDActionDependency("Perf.Controls.GridList.Static", "Test")]
        private static IEnumerator<int> GridListStatic(TestContext t) => MeasureControls(t, MakeGridList, false);
        [A_XSDActionDependency("Perf.Controls.GridList.Relayout", "Test")]
        private static IEnumerator<int> GridListRelayout(TestContext t) => MeasureControls(t, MakeGridList, true);

        [A_XSDActionDependency("Perf.Controls.Scrollable.Static", "Test")]
        private static IEnumerator<int> ScrollableStatic(TestContext t) => MeasureControls(t, MakeScrollable, false);
        [A_XSDActionDependency("Perf.Controls.Scrollable.Relayout", "Test")]
        private static IEnumerator<int> ScrollableRelayout(TestContext t) => MeasureControls(t, MakeScrollable, true);

        [A_XSDActionDependency("Perf.Controls.SplitView.Static", "Test")]
        private static IEnumerator<int> SplitViewStatic(TestContext t) => MeasureControls(t, MakeSplitView, false);
        [A_XSDActionDependency("Perf.Controls.SplitView.Relayout", "Test")]
        private static IEnumerator<int> SplitViewRelayout(TestContext t) => MeasureControls(t, MakeSplitView, true);

        [A_XSDActionDependency("Perf.Controls.TabView.Static", "Test")]
        private static IEnumerator<int> TabViewStatic(TestContext t) => MeasureControls(t, MakeTabView, false);
        [A_XSDActionDependency("Perf.Controls.TabView.Relayout", "Test")]
        private static IEnumerator<int> TabViewRelayout(TestContext t) => MeasureControls(t, MakeTabView, true);

        [A_XSDActionDependency("Perf.Controls.Table.Static", "Test")]
        private static IEnumerator<int> TableStatic(TestContext t) => MeasureTables(t, false);
        [A_XSDActionDependency("Perf.Controls.Table.Relayout", "Test")]
        private static IEnumerator<int> TableRelayout(TestContext t) => MeasureTables(t, true);

        // Shows controlCount controls in rows, then measures them still or with the grid's width flipping every tick.
        private static IEnumerator<int> MeasureControls(TestContext t, Func<Control> make, bool relayout)
        {
            StackPanelControl rows = new StackPanelControl
            {
                preferredWidth = controlGridWidth,
                horizontalAlignment = HorizontalAlignment.Left,
                verticalAlignment = VerticalAlignment.Top
            };
            StackPanelControl row = null!;
            for (int i = 0; i < controlCount; i++)
            {
                if (i % controlsPerRow == 0)
                {
                    row = new StackPanelControl { orientation = StackPanelControl.Orientation.Horizontal, preferredHeight = controlHeight };
                    rows.AddChild(row);
                }
                Control control = make();
                control.widthStar = 1f;
                control.preferredHeight = controlHeight;
                row.AddChild(control);
            }
            t.Show(rows);
            yield return 2;

            for (int i = 0; i < warmupTicks; i++)
            {
                if (relayout) rows.preferredWidth = controlGridWidth + i % 2;
                yield return 1;
            }

            t.StartMeasure();
            for (int i = 0; i < measuredTicks; i++)
            {
                if (relayout) rows.preferredWidth = controlGridWidth + i % 2;
                yield return 1;
            }
            yield return t.EndMeasure();
        }

        // Shows a note of tableCount tables, then measures it still or with the page width flipping every tick.
        private static IEnumerator<int> MeasureTables(TestContext t, bool relayout)
        {
            RichTextDocument document = new RichTextDocument { blocks = new NoteNode[tableCount] };
            for (int i = 0; i < tableCount; i++)
            {
                NoteTable table = new NoteTable(Enumerable.Repeat(150f, tableSide).ToArray()) { rows = new NoteCell[tableSide][] };
                for (int r = 0; r < tableSide; r++)
                {
                    table.rows[r] = new NoteCell[tableSide];
                    for (int c = 0; c < tableSide; c++)
                    {
                        NoteBlock cell = new NoteBlock();
                        cell.AppendRun(new Run { text = $"Cell {r},{c}" });
                        table.rows[r][c] = new NoteCell { blocks = [cell] };
                    }
                }
                document.blocks[i] = table;
            }

            DocumentEditorControl editor = new DocumentEditorControl
            {
                horizontalAlignment = HorizontalAlignment.Stretch,
                verticalAlignment = VerticalAlignment.Stretch
            };
            t.Show(editor);
            editor.LoadDocument(document);
            yield return 2;

            for (int i = 0; i < warmupTicks; i++)
            {
                if (relayout) editor.SetPage(new PageLayout { size = PageSize.Custom, width = pageWidthMm - i % 2 * rewrapStepMm });
                yield return 1;
            }

            t.StartMeasure();
            for (int i = 0; i < measuredTicks; i++)
            {
                if (relayout) editor.SetPage(new PageLayout { size = PageSize.Custom, width = pageWidthMm - i % 2 * rewrapStepMm });
                yield return 1;
            }
            yield return t.EndMeasure();
            t.Show(new StackPanelControl());
        }

        private static Control MakePanel() => new PanelControl();
        private static Control MakeButton() => new ButtonControl();
        private static Control MakeCheckBox() => new CheckBoxControl { isChecked = true };
        private static Control MakeSlider() => new SliderControl { value = 0.5f };
        private static Control MakeDropdown() => new DropdownControl { options = new[] { "One", "Two" }, selected = "One" };
        private static Control MakeKeyCapture() => new KeyCaptureControl();
        private static Control MakeIcon() => new IconControl { iconName = "chevron-up" };
        private static Control MakeLabel() => new LabelControl { text = "Label" };
        private static Control MakeTextBox() => new TextBoxControl { text = "Text" };
        private static Control MakeEditableLabel() => new EditableLabelControl { text = "Label" };

        private static Control MakeExpander()
        {
            ExpanderControl expander = new ExpanderControl();
            expander.AddChild(new LabelControl { text = "Inside" });
            return expander;
        }

        private static Control MakeStackPanel()
        {
            StackPanelControl inner = new StackPanelControl();
            inner.AddChild(new LabelControl { text = "Nested" });
            StackPanelControl outer = new StackPanelControl { orientation = StackPanelControl.Orientation.Horizontal };
            outer.AddChild(inner);
            return outer;
        }

        private static Control MakeGridList()
        {
            GridListControl grid = new GridListControl();
            grid.columnDefinitions.Add(new ColumnDefinition { sizeMode = GridSizeMode.Star });
            grid.columnDefinitions.Add(new ColumnDefinition { sizeMode = GridSizeMode.Star });
            grid.AddChild(new PanelControl { gridColumn = 0 });
            grid.AddChild(new PanelControl { gridColumn = 1 });
            return grid;
        }

        private static Control MakeScrollable()
        {
            ScrollableControl scrollable = new ScrollableControl();
            scrollable.AddChild(new LabelControl { text = "Scrolled", preferredHeight = controlHeight * 3f });
            return scrollable;
        }

        private static Control MakeSplitView()
        {
            SplitViewControl split = new SplitViewControl { orientation = StackPanelControl.Orientation.Horizontal };
            split.AddChild(new PanelControl { widthStar = 1f });
            split.AddChild(new PanelControl { widthStar = 1f });
            return split;
        }

        private static Control MakeTabView()
        {
            TabViewControl view = new TabViewControl();
            view.AddChild(new TabItemControl { header = "One" });
            view.AddChild(new TabItemControl { header = "Two" });
            return view;
        }
        #endregion

        // Shows an editor holding a generated note, caret at the end of the first block.
        private static DocumentEditorControl OpenNote(TestContext t)
        {
            StringBuilder builder = new StringBuilder(blockChars);
            while (builder.Length < blockChars)
                builder.Append("The quick brown fox jumps over the lazy dog while every line of the note wraps again. ");
            string paragraph = builder.ToString(0, blockChars);

            RichTextDocument document = new RichTextDocument { blocks = new NoteNode[blockCount] };
            for (int i = 0; i < blockCount; i++)
            {
                NoteBlock block = new NoteBlock();
                block.AppendRun(new Run { text = paragraph });
                document.blocks[i] = block;
            }

            DocumentEditorControl editor = new DocumentEditorControl
            {
                preferredWidth = noteWidth,
                horizontalAlignment = HorizontalAlignment.Left,
                verticalAlignment = VerticalAlignment.Stretch
            };
            t.Show(editor);
            editor.LoadDocument(document);

            DocumentControl content = editor.children.OfType<DocumentControl>().First();
            BlockControl first = content.Blocks()[0];
            content.SetCaret(first, first.Length);
            editor.FocusCaret();
            return editor;
        }

        private static void TypeOne(int i)
        {
            InputHandler.charInputReadQueue.Enqueue((char)('a' + i % 26));
            TextInputActions.Write();
        }

        // Edits and saves the generated note once a tick as an open note's autosave does, the main-thread part in a Note.Save zone.
        private static IEnumerator<int> SaveSegments(TestContext t, Action<RichTextDocument, int> edit)
        {
            string path = Path.Combine(Path.GetTempPath(), $"aurora-save-{Guid.NewGuid():N}.axml");
            RichTextDocument document = LargeDocument();
            List<NoteSegment>? map = null;
            byte[]? file = null;
            FileJournal journal = FileJournal.Open(path);
            for (int i = 0; i < saveWarmupTicks + measuredTicks; i++)
            {
                if (i == saveWarmupTicks) t.StartMeasure();
                edit(document, i);
                Profiling.Zone.Start("Note.Save");
                SegmentPatch patch = NoteSegments.Prepare(document, path, map, file);
                map = patch.map;
                file = patch.file;
                Profiling.Zone.End("Note.Save");
                journal.Commit(patch.writes, patch.length);
                yield return 1;
            }
            yield return t.EndMeasure();
            journal.Dispose();
            File.Delete(path);
        }

        // Inserts count chars at the start of the saved block on even ticks and removes them on odd ones.
        private static Action<RichTextDocument, int> ToggleChars(int count) => (document, i) =>
        {
            if (i % 2 == 0) document.InsertText(new DocumentAddress(saveBlock, 0), new string('x', count));
            else document.RemoveText(new DocumentAddress(saveBlock, 0), count);
        };

        private static RichTextDocument LargeDocument()
        {
            StringBuilder builder = new StringBuilder(blockChars);
            while (builder.Length < blockChars)
                builder.Append("The quick brown fox jumps over the lazy dog while every line of the note wraps again. ");
            string paragraph = builder.ToString(0, blockChars);

            RichTextDocument document = new RichTextDocument { blocks = new NoteNode[blockCount] };
            for (int i = 0; i < blockCount; i++)
            {
                NoteBlock block = new NoteBlock();
                block.AppendRun(new Run { text = paragraph });
                document.blocks[i] = block;
            }
            return document;
        }

        // Shows buttonCount buttons in rows of rowLength.
        private static List<ButtonControl> ShowGrid(TestContext t)
        {
            List<ButtonControl> buttons = new List<ButtonControl>(buttonCount);
            StackPanelControl rows = new StackPanelControl();
            StackPanelControl row = null!;
            for (int i = 0; i < buttonCount; i++)
            {
                if (i % rowLength == 0)
                {
                    row = new StackPanelControl { orientation = StackPanelControl.Orientation.Horizontal };
                    rows.AddChild(row);
                }
                ButtonControl button = new ButtonControl { preferredWidth = buttonSize, preferredHeight = buttonSize };
                row.AddChild(button);
                buttons.Add(button);
            }
            t.Show(rows);
            return buttons;
        }

        private static void StopAll(List<ButtonControl> buttons)
        {
            foreach (ButtonControl button in buttons)
                Animations.StopAll(button);
        }
    }
}
