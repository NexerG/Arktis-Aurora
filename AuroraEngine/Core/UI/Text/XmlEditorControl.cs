using ArctisAurora.Core.Editing;
using ArctisAurora.Core.Filing.Serialization;
using ArctisAurora.Core.Threading;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Containers.Tabs;
using ArctisAurora.Core.UI.Containers.Workspace;
using ArctisAurora.Core.UI.Document;
using ArctisAurora.Core.UI.Notes;
using ArctisAurora.Core.UI.Planner;
using ArctisAurora.Core.UI.Sheets;
using ArctisAurora.EngineWork;
using System.Xml;
using System.Xml.Linq;

namespace ArctisAurora.Core.UI.Text
{
    // One XML file shown as its source, kept in step with the note, sheet or planner it holds.
    public class XmlEditorControl : StackPanelControl, IFileEditor
    {
        public readonly DocumentEditorControl source;
        private readonly LabelControl status;

        private Target? target;
        public string? path { get; private set; }

        // set while this view's text goes into the model, or the model's text into this view
        private bool applying;
        private bool regenerating;

        // work for the next tick, and a regeneration held back while the tab is hidden
        private bool applyPending;
        private bool regenPending;
        private bool stale;
        private bool stalePosted;

        // the text is well formed and its model took it
        public bool valid { get; private set; } = true;

        // the XML edit step still being extended by typing
        private XmlReplaceEdit? openEdit;

        bool IFileEditor.isDirty => valid && target != null ? target.dirty : source.session?.isDirty == true;

        public XmlEditorControl()
        {
            horizontalAlignment = HorizontalAlignment.Stretch;
            verticalAlignment = VerticalAlignment.Stretch;
            alpha = 0f;

            source = new DocumentEditorControl { contextMenu = "note", heightStar = 1f, horizontalAlignment = HorizontalAlignment.Stretch };
            source.onSaved = Saved;
            status = new LabelControl { fontSize = 12, role = PaletteRole.Danger, padding = new Thickness(3f, 6f, 3f, 6f), horizontalAlignment = HorizontalAlignment.Stretch };
            status.Hide();

            AddChild(source);
            AddChild(status);
        }

        public void LoadPath(string nameOrPath)
        {
            path = Path.GetFullPath(Path.IsPathRooted(nameOrPath) ? nameOrPath : Paths.Doc(nameOrPath));
            source.LoadSession(new DocumentEditSession(RichTextDocument.LoadSource(path, "xml"), path));
            source.activeDocument.changed += SourceChanged;

            try
            {
                target = SheetDocument.IsSheet(path) ? new SheetTarget(path)
                    : PlannerDocument.IsPlanner(path) ? new PlannerTarget(path)
                    : new NoteTarget(path);
                target.changed = ModelChanged;
            }
            catch (Exception e)
            {
                valid = false;
                ShowStatus(Describe(e));
            }
        }

        public override void OnDestroy()
        {
            if (source.activeDocument != null) source.activeDocument.changed -= SourceChanged;
            target?.Release();
            base.OnDestroy();
        }

        #region ---- file ----
        public void Save() => source.Save();

        private void Saved()
        {
            if (valid) target?.MarkSaved();
        }

        public void Repath(string newPath, string name)
        {
            path = newPath;
            source.Repath(newPath, name);
            target?.Repath(newPath);
        }

        public SessionTab ViewState()
        {
            SessionTab view = source.ViewState();
            view.asXml = true;
            return view;
        }

        public void RestoreView(SessionTab view) => source.RestoreView(view);
        #endregion

        #region ---- sync ----
        private void SourceChanged(NoteChange change)
        {
            if (regenerating) return;
            source.MarkDirty();
            applyPending = true;
            RequestTick();
        }

        private void ModelChanged()
        {
            if (applying) return;
            openEdit = null;
            regenPending = true;
            RequestTick();
        }

        private void RequestTick()
        {
            SetTicking(true);
            FrameScheduler.RequestFrameAt(Engine.totalTime);
        }

        public override void OnTick()
        {
            base.OnTick();
            if (applyPending)
            {
                applyPending = false;
                Apply();
            }
            if (regenPending)
            {
                regenPending = false;
                if (OnScreen()) Regenerate();
                else stale = true;
            }
            SetTicking(false);
        }

        protected override void ArrangeCore(LayoutRect finalRect)
        {
            base.ArrangeCore(finalRect);
            if (!stale || stalePosted) return;

            stalePosted = true;
            Engine.Post(() =>
            {
                stalePosted = false;
                if (!stale || destroyed) return;
                stale = false;
                Regenerate();
            });
        }

        private bool OnScreen()
        {
            for (Control? at = this; at != null; at = at.parent as Control)
                if (at.hidden) return false;
            return true;
        }

        // This view's text into the model, when it is well formed and the model reads it.
        private void Apply()
        {
            if (target == null || path == null) return;

            object content;
            try
            {
                content = target.Parse(source.activeDocument.SourceText(), path);
            }
            catch (Exception e)
            {
                valid = false;
                ShowStatus(Describe(e));
                return;
            }

            valid = true;
            ShowStatus(null);

            applying = true;
            try
            {
                if (openEdit != null) openEdit.after = content;
                else
                {
                    openEdit = new XmlReplaceEdit(target.Replace, target.Snapshot(), content);
                    using (target.undo.Begin("XML edit", true)) target.undo.Push(openEdit);
                }
                target.Replace(content);
                target.MarkDirty();
            }
            finally
            {
                applying = false;
            }
        }

        // The model's text into this view, the caret kept by line and column.
        private void Regenerate()
        {
            if (target == null || path == null) return;

            RichTextDocument shown = source.activeDocument;
            using MemoryStream stream = new MemoryStream();
            target.ToFile(path).Save(stream);
            RichTextDocument fresh = RichTextDocument.SourceFromBytes(stream.ToArray(), shown.name ?? Path.GetFileNameWithoutExtension(path), "xml");
            fresh.sourceNewline = shown.sourceNewline;

            valid = true;
            ShowStatus(null);
            if (fresh.SourceText() == shown.SourceText()) return;

            regenerating = true;
            try
            {
                shown.ReplaceAll(fresh);
            }
            finally
            {
                regenerating = false;
            }
            source.session.undo.Clear();
        }

        private void ShowStatus(string? text)
        {
            if (text == null)
            {
                if (!status.hidden) status.Hide();
                return;
            }
            status.text = text;
            if (status.hidden) status.Show();
        }

        private static string Describe(Exception e) =>
            e is XmlException xml ? $"Line {xml.LineNumber}: {xml.Message}" : e.Message;
        #endregion

        #region ---- targets ----
        // The model a file's XML stands for.
        private abstract class Target
        {
            public Action? changed;

            public abstract bool dirty { get; }
            public abstract UndoStack undo { get; }
            public abstract void MarkDirty();
            public abstract void MarkSaved();
            public abstract XDocument ToFile(string path);
            public abstract object Parse(string text, string path);
            public abstract object Snapshot();
            public abstract void Replace(object content);
            public abstract void Repath(string newPath);
            public abstract void Release();

            protected void Raise() => changed?.Invoke();
        }

        private sealed class NoteTarget : Target
        {
            private readonly DocumentEditSession session;

            public NoteTarget(string path)
            {
                session = NoteSessions.Open(path);
                session.document.changed += Changed;
            }

            private void Changed(NoteChange change) => Raise();

            public override bool dirty => session.isDirty;
            public override UndoStack undo => session.undo;
            public override void MarkDirty() => session.MarkDirty();
            public override void MarkSaved() => session.MarkSaved();
            public override XDocument ToFile(string path) => DocumentXml.ToFile(session.document, path);

            public override object Parse(string text, string path)
            {
                XElement root = XDocument.Parse(text).Root ?? throw new XmlException("The file has no root element.");
                DocumentXml.ResolvePictures(root, Path.GetDirectoryName(Path.GetFullPath(path))!);
                return DocumentXml.Parse(root);
            }

            public override object Snapshot()
            {
                RichTextDocument d = session.document;
                return new RichTextDocument
                {
                    blocks = d.blocks, name = d.name, layout = d.layout, palette = d.palette, created = d.created,
                    modified = d.modified, readOnly = d.readOnly, frontmatter = d.frontmatter
                };
            }

            public override void Replace(object content) => session.document.ReplaceAll((RichTextDocument)content);
            public override void Repath(string newPath) => session.Repath(newPath);

            public override void Release()
            {
                session.document.changed -= Changed;
                NoteSessions.Close(session);
            }
        }

        private sealed class SheetTarget : Target
        {
            private readonly SheetDocument document;

            public SheetTarget(string path)
            {
                document = SheetBook.Get(path);
                SheetBook.changed += Changed;
            }

            private void Changed(SheetDocument? edited)
            {
                if (ReferenceEquals(edited, document)) Raise();
            }

            public override bool dirty => document.unsaved;
            public override UndoStack undo => document.undo;
            public override void MarkDirty() => document.unsaved = true;
            public override void MarkSaved() => document.unsaved = false;
            public override XDocument ToFile(string path) => new XDocument(new XDeclaration("1.0", "utf-8", null), SheetXml.ToXml(document));

            public override object Parse(string text, string path) =>
                SheetXml.Parse(XDocument.Parse(text, LoadOptions.PreserveWhitespace).Root ?? throw new XmlException("The file has no root element."));

            public override object Snapshot()
            {
                SheetDocument copy = new SheetDocument { name = document.name, fixedSize = document.fixedSize };
                copy.pages.AddRange(document.pages);
                copy.extra.AddRange(document.extra);
                return copy;
            }

            public override void Replace(object content) => document.ReplaceAll((SheetDocument)content);
            public override void Repath(string newPath) { }
            public override void Release() => SheetBook.changed -= Changed;
        }

        private sealed class PlannerTarget : Target
        {
            private readonly PlannerDocument document;
            private string path;

            public PlannerTarget(string path)
            {
                this.path = path;
                document = PlannerBook.Open(path);
                document.changed += Raise;
            }

            public override bool dirty => document.unsaved;
            public override UndoStack undo => document.undo;
            public override void MarkDirty() => document.unsaved = true;
            public override void MarkSaved() => document.unsaved = false;
            public override XDocument ToFile(string path) => new XDocument(new XDeclaration("1.0", "utf-8", null), PlannerXml.ToXml(document));

            public override object Parse(string text, string path) =>
                PlannerXml.Parse(XDocument.Parse(text, LoadOptions.PreserveWhitespace).Root ?? throw new XmlException("The file has no root element."));

            public override object Snapshot()
            {
                PlannerDocument copy = new PlannerDocument { name = document.name, nameWidth = document.nameWidth };
                copy.categories.AddRange(document.categories);
                copy.tickets.AddRange(document.tickets);
                copy.extra.AddRange(document.extra);
                return copy;
            }

            public override void Replace(object content) => document.ReplaceAll((PlannerDocument)content);
            public override void Repath(string newPath) => path = newPath;

            public override void Release()
            {
                document.changed -= Raise;
                PlannerBook.Close(path, document);
            }
        }
        #endregion
    }

    // XML typed into a model: its content before, and after the latest keystroke.
    public sealed class XmlReplaceEdit : IEditRecord
    {
        private readonly Action<object> replace;
        private readonly object before;
        public object after;

        public XmlReplaceEdit(Action<object> replace, object before, object after)
        {
            this.replace = replace;
            this.before = before;
            this.after = after;
        }

        public void Undo() => replace(before);

        public void Redo() => replace(after);
    }
}
