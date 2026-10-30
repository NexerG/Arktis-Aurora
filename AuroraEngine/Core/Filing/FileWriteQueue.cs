using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Notes;

namespace ArctisAurora.Core.Filing
{
    // Journal commits applied in queued order on one background thread.
    public static class FileWriteQueue
    {
        private static readonly Queue<(FileJournal journal, SegmentPatch patch, Action<Exception?> done)> queue =
            new Queue<(FileJournal journal, SegmentPatch patch, Action<Exception?> done)>();
        private static readonly object gate = new object();
        private static Thread? worker;
        private static int pending;

        // Commits patch through journal after every write queued before it; done runs on the writer thread.
        public static void Enqueue(FileJournal journal, SegmentPatch patch, Action<Exception?> done)
        {
            lock (gate)
            {
                queue.Enqueue((journal, patch, done));
                pending++;
                if (worker == null)
                {
                    worker = new Thread(Loop) { Name = "file writes", IsBackground = true };
                    worker.Start();
                }
                Monitor.PulseAll(gate);
            }
        }

        // Waits until every queued write has landed.
        public static void Drain()
        {
            lock (gate)
                while (pending > 0) Monitor.Wait(gate);
        }

        [A_XSDActionDependency("Files.Drain", "Shutdown", "Waits for every queued file write to land")]
        public static bool DrainAll()
        {
            Drain();
            return true;
        }

        private static void Loop()
        {
            while (true)
            {
                (FileJournal journal, SegmentPatch patch, Action<Exception?> done) item;
                lock (gate)
                {
                    while (queue.Count == 0) Monitor.Wait(gate);
                    item = queue.Dequeue();
                }

                Exception? failure = null;
                try
                {
                    item.journal.Commit(item.patch.writes, item.patch.length);
                }
                catch (Exception e)
                {
                    failure = e;
                }
                item.done(failure);

                lock (gate)
                {
                    pending--;
                    Monitor.PulseAll(gate);
                }
            }
        }
    }
}
