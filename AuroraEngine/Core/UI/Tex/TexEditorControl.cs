using ArctisAurora.Core.Diagnostics;
using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Tex;
using ArctisAurora.Core.Threading;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Containers.Tabs;
using ArctisAurora.Core.UI.Containers.Workspace;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Document;
using ArctisAurora.Core.UI.Menus;
using ArctisAurora.Core.UI.Notes;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.EngineWork;
using System.Globalization;
using System.Numerics;
using System.Xml.Linq;

namespace ArctisAurora.Core.UI.Tex
{
    // One open .tex file: the source, a grip, and the typeset preview over its error list.
    public class TexEditorControl : StackPanelControl, IFileEditor
    {
        private static readonly LogChannel Log = LogChannel.For("Tex");

        public const double recompileDelay = 0.5;
        private const float rowHeight = 22f;
        private const int maxErrorRows = 5;
        private const int fontSize = 13;

        public readonly DocumentEditorControl source;
        public readonly DocumentEditorControl preview;
        public readonly ScrollableControl errors;
        private readonly StackPanelControl errorRows = new StackPanelControl { alpha = 0f, horizontalAlignment = HorizontalAlignment.Stretch };

        // each preview block's source line, 0 = none
        private readonly List<int> blockLines = new List<int>();

        // the last edit not yet compiled, and when the last compile ran
        private double editedAt;
        private double compiledAt;

        // the preview's tree while its \pageref numbers wait for pages, and how often it has been reloaded for them
        private XElement? pageTree;
        private TexPageRefs? pageRefs;
        private int pageReloads;

        // the tree the preview shows, and whether an export waits for it to settle
        private XElement? shown;
        private bool exportWaiting;

        // tab-row tools: whether edits recompile, the error count and the preview's zoom
        private static readonly int[] zoomSteps = { 50, 75, 90, 100, 125, 150, 200 };
        private TabToolsControl? texTools;
        private LabelControl? liveCaption;
        private LabelControl? errorCaption;
        private LabelControl? zoomCaption;
        private bool live = true;
        private bool errorsCollapsed;
        private int errorTotal;

        public Control tools => texTools ??= BuildTools();

        public string? path => source.path;

        bool IFileEditor.isDirty => source.session?.isDirty == true;

        public TexEditorControl()
        {
            orientation = Orientation.Horizontal;
            horizontalAlignment = HorizontalAlignment.Stretch;
            verticalAlignment = VerticalAlignment.Stretch;
            alpha = 0f;

            source = new DocumentEditorControl { contextMenu = "note", widthStar = 1f, verticalAlignment = VerticalAlignment.Stretch };
            source.onSaved = () => { if (!live) Recompile(); };

            preview = new DocumentEditorControl { heightStar = 1f, horizontalAlignment = HorizontalAlignment.Stretch };
            preview.onSave = Save;

            errors = new ScrollableControl { alpha = 0f, horizontalAlignment = HorizontalAlignment.Stretch };
            errors.AddChild(errorRows);
            errors.Hide();

            StackPanelControl column = new StackPanelControl { alpha = 0f, widthStar = 1f, verticalAlignment = VerticalAlignment.Stretch };
            column.AddChild(preview);
            column.AddChild(errors);

            SplitterControl grip = new SplitterControl { preferredWidth = 5f };
            AddChild(source);
            AddChild(grip);
            AddChild(column);
        }

        public void LoadPath(string nameOrPath)
        {
            if (source.session != null) source.activeDocument.changed -= Edited;
            source.LoadPath(nameOrPath);
            source.activeDocument.changed += Edited;
            Recompile();
        }

        public override void OnDestroy()
        {
            if (source.session != null) source.activeDocument.changed -= Edited;
            texTools?.Destroy();
            base.OnDestroy();
        }

        #region ---- file ----
        public void Save() => source.Save();

        public void Repath(string newPath, string name) => source.Repath(newPath, name);

        public SessionTab ViewState() => source.ViewState();

        public void RestoreView(SessionTab view) => source.RestoreView(view);
        #endregion

        #region ---- compile ----
        private void Edited(NoteChange change)
        {
            editedAt = Engine.totalTime;
            if (!live) return;
            SetTicking(true);
            FrameScheduler.RequestFrameAt(editedAt + recompileDelay);
        }

        public override void OnTick()
        {
            base.OnTick();
            if (pageRefs != null && (editedAt <= compiledAt || !live))
            {
                ResolvePages();
                if (pageRefs == null && exportWaiting) WritePdf();
                return;
            }
            if (Engine.totalTime < editedAt + recompileDelay)
            {
                FrameScheduler.RequestFrameAt(editedAt + recompileDelay);
                return;
            }

            SetTicking(false);
            Recompile();
            if (pageRefs == null && exportWaiting) WritePdf();
        }

        // Typesets the source into the preview and lists what went wrong.
        public void Recompile()
        {
            if (source.activeDocument == null) return;

            Profiling.Zone.Start("Tex.Compile");
            string text = TexSourceFormat.Write(DocumentXml.ToXml(source.activeDocument), "\n");
            blockLines.Clear();
            XElement tree = TexLowering.Compile(text, out List<TexError> found, blockLines, path == null ? null : Path.GetDirectoryName(path));
            Show(tree);
            Profiling.Zone.End("Tex.Compile");

            compiledAt = Engine.totalTime;
            TexPageRefs refs = TexLowering.PageRefs(tree);
            pageTree = refs.refs.Count > 0 ? tree : null;
            pageRefs = refs.refs.Count > 0 ? refs : null;
            pageReloads = 0;
            if (pageRefs != null)
            {
                SetTicking(true);
                FrameScheduler.RequestFrameAt(Engine.totalTime);
            }

            ShowErrors(found);
        }

        private void Show(XElement tree)
        {
            shown = tree;
            RichTextDocument document = DocumentXml.Parse(tree);
            document.readOnly = true;
            Vector2 scroll = preview.GetScrollOffset();
            preview.LoadDocument(document);
            preview.SetScrollOffset(scroll);
        }

        // Writes the preview as <name>.pdf beside the .tex once it has settled; that path, or null without a file.
        public string? ExportPdf()
        {
            if (path == null) return null;
            if (editedAt > compiledAt) Recompile();
            exportWaiting = true;
            if (pageRefs == null) WritePdf();
            return Path.ChangeExtension(path, ".pdf");
        }

        private void WritePdf()
        {
            exportWaiting = false;
            if (shown == null || path == null) return;

            string target = Path.ChangeExtension(path, ".pdf");
            try
            {
                if (PdfExport.Export(shown, target, Path.GetFileNameWithoutExtension(path))) Log.Info($"exported '{target}'");
            }
            catch (IOException e)
            {
                Log.Warn($"'{target}' could not be written: {e.Message}");
            }
        }

        // Fills each \pageref from its label's page once the preview is laid out, reloading while a number changed; at most twice.
        private void ResolvePages()
        {
            bool changed = false;
            foreach ((XElement run, string key) in pageRefs!.refs)
            {
                if (!pageRefs.labels.TryGetValue(key, out (int block, int offset) site)) continue;
                int? page = preview.PageAt(site.block, site.offset);
                if (page == null)
                {
                    FrameScheduler.RequestFrameAt(Engine.totalTime);
                    return;
                }

                string number = page.Value.ToString(CultureInfo.InvariantCulture);
                if (run.Attribute("Text")?.Value == number) continue;
                run.SetAttributeValue("Text", number);
                pageRefs.Fit(run, number);
                changed = true;
            }

            if (!changed || pageReloads == 2)
            {
                pageTree = null;
                pageRefs = null;
                SetTicking(false);
                return;
            }
            pageReloads++;
            Show(pageTree!);
            FrameScheduler.RequestFrameAt(Engine.totalTime);
        }

        private void ShowErrors(List<TexError> found)
        {
            for (int i = errorRows.children.Count - 1; i >= 0; i--)
                errorRows.children[i].Destroy();

            errorTotal = found.Count;
            if (errorCaption != null) errorCaption.text = ErrorText();

            foreach (TexError error in found)
                errorRows.AddChild(ErrorRow(error));
            errors.preferredHeight = Math.Min(found.Count, maxErrorRows) * rowHeight;
            if (found.Count == 0 || errorsCollapsed) errors.Hide();
            else errors.Show();
        }

        private ButtonControl ErrorRow(TexError error)
        {
            ButtonControl row = new ButtonControl
            {
                preferredHeight = rowHeight,
                horizontalAlignment = HorizontalAlignment.Stretch,
                padding = new Thickness(6f, 0f)
            };
            row.PaintOr(null, PaletteRole.Clear);
            row.AddChild(new LabelControl
            {
                text = error.line > 0 ? $"{error.line}:{error.column}  {error.message}" : error.message,
                fontSize = fontSize,
                role = PaletteRole.MutedInk,
                hitTestable = false,
                horizontalPosition = 0f,
                verticalPosition = 0.5f
            });

            if (error.line > 0)
                row.RegisterOnPress(e =>
                {
                    if (e.button != PointerEvent.leftButton) return false;
                    source.GoTo(error.line - 1, Math.Max(0, error.column - 1));
                    source.FocusCaret();
                    return true;
                });
            return row;
        }
        #endregion

        #region ---- tools ----
        private TabToolsControl BuildTools()
        {
            TabToolsControl row = new TabToolsControl();
            row.Text("Build", _ => Recompile());
            liveCaption = row.Text("Live", _ => SetLive(!live));
            Light(liveCaption, live);
            row.Separator();
            errorCaption = row.Text(ErrorText(), _ => ToggleErrors());
            row.Separator();
            row.Text("Sync", _ => SyncPreview());
            zoomCaption = row.Text(preview.zoomOverride is float zoom ? $"{MathF.Round(zoom * 100f)}%" : "Zoom", owner => TabToolsControl.Drop(owner,
                zoomSteps.Select(step => (ContextMenuEntry)new ContextMenuButton($"{step}%", () => SetPreviewZoom(step))).ToList()), true);
            return row;
        }

        // Live recompiles a moment after each edit; off, only Build and a save do.
        public void SetLive(bool on)
        {
            live = on;
            if (liveCaption != null) Light(liveCaption, on);
            if (on && editedAt > compiledAt) Recompile();
        }

        public void SetPreviewZoom(int percent)
        {
            preview.zoomOverride = percent / 100f;
            if (zoomCaption != null) zoomCaption.text = $"{percent}%";
        }

        private void ToggleErrors()
        {
            errorsCollapsed = !errorsCollapsed;
            if (errorTotal == 0 || errorsCollapsed) errors.Hide();
            else errors.Show();
        }

        // Scrolls the preview to the block typeset from the source caret's line, or the last one before it.
        public void SyncPreview()
        {
            if (source.CaretBlock == null || source.activeDocument == null || preview.activeDocument == null) return;

            int line = Array.IndexOf(source.activeDocument.blocks, source.CaretBlock.note) + 1;
            int best = -1;
            for (int i = 0; i < blockLines.Count; i++)
                if (blockLines[i] > 0 && blockLines[i] <= line && (best < 0 || blockLines[i] >= blockLines[best])) best = i;
            if (best >= 0) preview.GoTo(best, 0);
        }

        private string ErrorText() => errorTotal switch { 0 => "No errors", 1 => "1 error", _ => $"{errorTotal} errors" };

        private static void Light(LabelControl caption, bool on)
        {
            caption.PaintOr(null, on ? PaletteRole.Accent : PaletteRole.MutedInk);
            caption.InvalidateLayout();
        }
        #endregion

        #region ---- click to source ----
        // A double-click in the preview moves the source caret to that block's line.
        public override bool OnPointerRelease(PointerEvent e)
        {
            if (e.tapCount != 2 || e.button != PointerEvent.leftButton || !InPreview(e.target)) return false;

            int index = preview.CaretBlock != null ? Array.IndexOf(preview.activeDocument.blocks, preview.CaretBlock.note) : -1;
            if (index >= 0 && index < blockLines.Count && blockLines[index] > 0) source.GoTo(blockLines[index] - 1, 0);
            return false;
        }

        private bool InPreview(Control? control)
        {
            for (; control != null; control = control.parent as Control)
                if (ReferenceEquals(control, preview)) return true;
            return false;
        }
        #endregion
    }
}
