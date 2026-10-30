using ArctisAurora.Core.Diagnostics;
using ArctisAurora.Core.Filing;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Security;
using ArctisAurora.Core.UI.Document;
using ArctisAurora.EngineWork;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Xml.Linq;

namespace ArctisAurora.Core.UI.Notes
{
    // Keeps an open note and its file in step: saved on the tick after an edit, then at most once every 250 ms,
    // and a write made outside taken in once it settles.
    internal sealed class NoteFileSync : IDisposable
    {
        private const int interval = 250;
        private const int settleMs = 150;

        private static readonly LogChannel Log = LogChannel.For("UI");

        private readonly DocumentEditSession session;
        private readonly Timer timer;
        private readonly Timer settle;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private FileSystemWatcher? watcher;

        private long lastSave = -interval;
        private bool pending;
        private bool flushing;
        private bool closed;
        private bool warned;
        private bool unreadable;

        // queued writes not yet settled, and the outcomes the writer thread has handed back
        private int inFlight;
        private readonly ConcurrentQueue<Exception?> landed = new ConcurrentQueue<Exception?>();

        // the .axml file's segments and bytes, and its length and write time as this last wrote or read it
        private List<NoteSegment>? map;
        private byte[]? file;
        private FileJournal? journal;
        private readonly object stamp = new object();
        private long writtenLength;
        private DateTime writtenAt;

        internal NoteFileSync(DocumentEditSession session)
        {
            this.session = session;
            timer = new Timer(_ => Engine.Post(Flush));
            settle = new Timer(_ => Settled());
            Watch();
            if (!File.Exists(session.path)) return;

            if (Segmented) Remap(File.ReadAllBytes(session.path));
            Stamp();
        }

        // .axml notes are written by segment, every other format whole
        internal bool Segmented => session.document.sourceLanguage == null && session.document.encryption == null && !VaultLock.Covers(session.path) && !VaultLock.Refuses(session.path)
            && FileTypes.KindOf(session.path) == FileKind.Note;

        internal void Edited()
        {
            if (pending || closed) return;
            pending = true;

            long wait = lastSave + interval - clock.ElapsedMilliseconds;
            if (wait <= 0) Engine.Post(Flush);
            else timer.Change(wait, Timeout.Infinite);
        }

        // The note's changed segments, or the whole file when it is not the one this last wrote; an autosave does not wait for it to land.
        internal void Write() => Write(!flushing);

        private void Write(bool wait)
        {
            if (!Segmented)
            {
                session.document.Save(session.path);
                Stamp();
                return;
            }

            if (map != null && inFlight == 0 && !Unchanged()) map = null;
            SegmentPatch patch = NoteSegments.Prepare(session.document, session.path, map, file);
            map = patch.map;
            file = patch.file;
            if (patch.writes.Count > 0)
            {
                Interlocked.Increment(ref inFlight);
                FileWriteQueue.Enqueue(journal ??= FileJournal.Open(session.path), patch, Done);
            }
            if (!wait) return;

            FileWriteQueue.Drain();
            if (Landed(false) is Exception failure) ExceptionDispatchInfo.Throw(failure);
        }

        // On the writer thread, once a queued write has landed or failed.
        private void Done(Exception? failure)
        {
            if (failure == null) Stamp();
            landed.Enqueue(failure);
            Engine.Post(() => Landed(true));
        }

        // Settles the writes that have landed; the first that failed, if any.
        private Exception? Landed(bool warn)
        {
            Exception? failure = null;
            int count = 0;
            while (landed.TryDequeue(out Exception? e))
            {
                count++;
                failure ??= e;
            }
            if (count == 0) return null;
            Interlocked.Add(ref inFlight, -count);

            if (failure != null)
            {
                map = null;
                file = null;
                session.MarkUnsaved();
                if (warn && !warned) Log.Warn($"Could not save '{session.path}': {failure.Message}");
                warned |= warn;
            }
            else
            {
                warned = false;
                if (!SettingsRegistry.Get<DocumentSettings>().stampModified)
                    session.document.modified = RichTextDocument.Stamp(File.GetLastWriteTime(session.path));
            }

            if (inFlight == 0 && !closed) Settled();
            return failure;
        }

        // Waits for this note's queued writes, then releases its journal.
        private void DropJournal()
        {
            if (inFlight > 0)
            {
                FileWriteQueue.Drain();
                Landed(true);
            }
            journal?.Dispose();
            journal = null;
        }

        private void Remap(byte[] bytes)
        {
            map = NoteSegments.Map(bytes, session.document);
            file = map != null ? bytes : null;
        }

        #region ---- outside writes ----
        // Follows the note's file for writes made outside the app.
        internal void Watch()
        {
            watcher?.Dispose();
            watcher = null;
            DropJournal();

            string full = Path.GetFullPath(session.path);
            string? dir = Path.GetDirectoryName(full);
            if (dir == null || !Directory.Exists(dir)) return;

            watcher = new FileSystemWatcher(dir, Path.GetFileName(full))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
            };
            watcher.Changed += OnDisk;
            watcher.Created += OnDisk;
            watcher.Renamed += OnDisk;
            watcher.EnableRaisingEvents = true;
        }

        private void OnDisk(object sender, FileSystemEventArgs e)
        {
            try
            {
                settle.Change(settleMs, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void Settled()
        {
            if (Volatile.Read(ref inFlight) > 0) return;
            FileInfo file = new FileInfo(session.path);
            if (!file.Exists) return;
            lock (stamp)
                if (file.Length == writtenLength && file.LastWriteTimeUtc == writtenAt) return;
            Engine.Post(Reload);
        }

        // A write made outside: taken into the note when it has nothing unsaved, written over when it has.
        private void Reload()
        {
            if (closed || inFlight > 0 || Unchanged()) return;
            if (session.isDirty)
            {
                Overwrite();
                return;
            }

            Stamp();
            RichTextDocument fresh;
            try
            {
                fresh = RichTextDocument.Load(session.path, session.document.encryption);
            }
            catch (Exception e)
            {
                if (!unreadable) Log.Warn($"'{session.path}' changed on disk but does not read as a note: {e.Message}");
                unreadable = true;
                map = null;
                return;
            }

            unreadable = false;
            Take(fresh);
            if (Segmented) Remap(File.ReadAllBytes(session.path));
        }

        // The entries between the ones both sides agree on at the start and end, swapped in as one undo step with the header.
        private void Take(RichTextDocument fresh)
        {
            RichTextDocument document = session.document;
            NoteNode[] old = document.blocks;
            NoteNode[] now = fresh.blocks;
            string[] a = old.Select(Key).ToArray();
            string[] b = now.Select(Key).ToArray();

            int lead = 0;
            while (lead < old.Length && lead < now.Length && a[lead] == b[lead]) lead++;
            int tail = 0;
            while (tail < old.Length - lead && tail < now.Length - lead && a[^(tail + 1)] == b[^(tail + 1)]) tail++;

            bool nodes = lead < old.Length || lead < now.Length;
            if (nodes)
            {
                while (lead > 0 && old[lead - 1].insert != null) lead--;
                while (tail > 0 && old[^tail].insert != null) tail--;
            }

            NoteHeader before = NoteHeader.Of(document);
            NoteHeader after = NoteHeader.Of(fresh) with { layout = LayoutKey(fresh.layout) == LayoutKey(document.layout) ? document.layout : fresh.layout };
            if (!nodes && before == after) return;

            NodesEdit edit = new NodesEdit(document, lead, nodes ? old[lead..^tail] : [], nodes ? now[lead..^tail] : [], before, after);
            using (session.undo.Begin("Reloaded from disk")) session.undo.Push(edit);
            edit.Redo();
        }

        private static string Key(NoteNode node)
        {
            string xml = DocumentXml.WriteGroup(XSDGenerator.NamespaceFor("UI"), null, [node]).ToString(SaveOptions.DisableFormatting);
            PageInsert? insert = node.insert;
            return insert == null ? xml : $"{insert.footnote}|{insert.floatKind}|{insert.placement}|{xml}";
        }

        private static string LayoutKey(DocumentLayout layout) =>
            DocumentXml.WriteLayout(XSDGenerator.NamespaceFor("UI"), layout)?.ToString(SaveOptions.DisableFormatting) ?? "";

        // The note written over an outside write, keeping the outside file's bytes wherever they already match.
        private void Overwrite()
        {
            if (Segmented)
            {
                byte[] file = File.ReadAllBytes(session.path);
                try
                {
                    RichTextDocument outside = DocumentXml.Parse(XDocument.Load(new MemoryStream(file)).Root!);
                    map = NoteSegments.Rebase(file, outside, session.document, session.path);
                }
                catch (Exception)
                {
                    map = null;
                }
                this.file = map != null ? file : null;
                Stamp();
            }
            TrySave();
        }
        #endregion

        private bool Unchanged()
        {
            FileInfo file = new FileInfo(session.path);
            lock (stamp)
                return file.Exists && file.Length == writtenLength && file.LastWriteTimeUtc == writtenAt;
        }

        private void Stamp()
        {
            FileInfo file = new FileInfo(session.path);
            lock (stamp)
            {
                writtenLength = file.Exists ? file.Length : -1;
                writtenAt = file.Exists ? file.LastWriteTimeUtc : default;
            }
        }

        private void Flush()
        {
            pending = false;
            if (closed || !session.isDirty) return;

            flushing = true;
            try
            {
                TrySave();
            }
            finally
            {
                flushing = false;
            }
            lastSave = clock.ElapsedMilliseconds;
        }

        // A failed write leaves the note dirty, so the next edit tries again.
        private void TrySave()
        {
            try
            {
                session.Save();
                if (!Segmented) warned = false;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (!warned) Log.Warn($"Could not save '{session.path}': {e.Message}");
                warned = true;
            }
        }

        public void Dispose()
        {
            watcher?.Dispose();
            watcher = null;
            if (session.isDirty) TrySave();
            DropJournal();
            closed = true;
            timer.Dispose();
            settle.Dispose();
        }
    }
}
