namespace ArctisAurora.Core.UI.Sheets
{
    public readonly record struct SheetCellId(SheetPage page, long key);

    // Formula values of every loaded sheet, kept current through the cells each formula reads.
    public sealed class SheetCalc
    {
        // formula cells, their values, and the edges both ways
        private readonly Dictionary<SheetCellId, SheetFormula> formulas = new Dictionary<SheetCellId, SheetFormula>();
        private readonly Dictionary<SheetCellId, SheetValue> values = new Dictionary<SheetCellId, SheetValue>();
        private readonly Dictionary<SheetCellId, HashSet<SheetCellId>> precedents = new Dictionary<SheetCellId, HashSet<SheetCellId>>();
        private readonly Dictionary<SheetCellId, HashSet<SheetCellId>> dependents = new Dictionary<SheetCellId, HashSet<SheetCellId>>();

        // documents known, and those loaded mid-link waiting for theirs
        private readonly HashSet<SheetDocument> documents = new HashSet<SheetDocument>();
        private readonly Queue<SheetDocument> pending = new Queue<SheetDocument>();
        private bool settling;

        public SheetValue Value(SheetPage page, int row, int column) => Read(page, row, column);

        // A cell as a formula sees it.
        internal SheetValue Read(SheetPage page, int row, int column)
        {
            SheetCellId id = new SheetCellId(page, SheetDocument.Key(row, column));
            if (formulas.ContainsKey(id)) return values.TryGetValue(id, out SheetValue v) ? v : SheetValue.Empty;
            return SheetValue.FromRaw(page.Shown(row, column));
        }

        // A page of the home page's file, or of the file a reference names; a CSV reaches no other file.
        internal SheetPage? PageNamed(SheetPage home, string? file, string name)
        {
            if (file != null && SheetBook.OwnerOf(home)?.isCsv == true) return null;
            SheetDocument? document = file == null ? SheetBook.OwnerOf(home) : SheetBook.Resolve(SheetBook.PathOf(SheetBook.OwnerOf(home)), file);
            if (document == null) return null;
            foreach (SheetPage page in document.pages)
                if (page.name.Equals(name, StringComparison.OrdinalIgnoreCase)) return page;
            return null;
        }

        // Cells of a page were written: relink them and re-evaluate everything downstream.
        public void Changed(SheetPage page, IEnumerable<(int row, int column)> cells)
        {
            List<SheetCellId> seeds = new List<SheetCellId>();
            foreach ((int row, int column) in cells)
            {
                SheetCellId id = new SheetCellId(page, SheetDocument.Key(row, column));
                Relink(id);
                seeds.Add(id);
            }
            Settle(seeds);
        }

        // Links a document's formulas, or relinks them when pages were added.
        public void Add(SheetDocument document)
        {
            documents.Add(document);
            pending.Enqueue(document);
            Settle(new List<SheetCellId>());
        }

        public void Remove(SheetDocument document)
        {
            documents.Remove(document);
            foreach (SheetPage page in document.pages)
                foreach (long key in Keys(page))
                    Unlink(new SheetCellId(page, key));
        }

        // Every known document read afresh; for layer visibility, a sheet created, renamed or deleted.
        public void RecalcAll()
        {
            ClearGraph();
            foreach (SheetDocument document in documents)
                pending.Enqueue(document);
            Settle(new List<SheetCellId>());
        }

        public void Clear()
        {
            ClearGraph();
            documents.Clear();
            pending.Clear();
        }

        private void ClearGraph()
        {
            formulas.Clear();
            values.Clear();
            precedents.Clear();
            dependents.Clear();
        }

        // Links what is pending, then evaluates downstream; a load inside either waits for the next turn.
        private void Settle(List<SheetCellId> seeds)
        {
            if (settling) return;
            settling = true;
            do
            {
                while (pending.TryDequeue(out SheetDocument? document))
                    foreach (SheetPage page in document.pages)
                        foreach (long key in Keys(page))
                        {
                            SheetCellId id = new SheetCellId(page, key);
                            Relink(id);
                            seeds.Add(id);
                        }
                Recalc(Downstream(seeds));
                seeds.Clear();
            }
            while (pending.Count > 0);
            settling = false;
        }

        private static HashSet<long> Keys(SheetPage page)
        {
            HashSet<long> keys = new HashSet<long>();
            foreach (SheetLayer layer in page.layers)
                keys.UnionWith(layer.cells.Keys);
            return keys;
        }

        #region ---- graph ----
        private void Link(SheetCellId id, string raw)
        {
            SheetFormula formula = SheetFormula.Parse(raw);
            formulas[id] = formula;

            HashSet<SheetCellId> reads = new HashSet<SheetCellId>();
            formula.References(this, id.page, reads);
            precedents[id] = reads;
            foreach (SheetCellId read in reads)
            {
                if (!dependents.TryGetValue(read, out HashSet<SheetCellId>? set))
                    dependents[read] = set = new HashSet<SheetCellId>();
                set.Add(id);
            }
        }

        private void Relink(SheetCellId id)
        {
            Unlink(id);
            string? raw = id.page.Shown(SheetDocument.RowOf(id.key), SheetDocument.ColumnOf(id.key));
            if (SheetFormula.IsFormula(raw)) Link(id, raw!);
        }

        private void Unlink(SheetCellId id)
        {
            formulas.Remove(id);
            values.Remove(id);
            if (!precedents.Remove(id, out HashSet<SheetCellId>? reads)) return;
            foreach (SheetCellId read in reads)
                if (dependents.TryGetValue(read, out HashSet<SheetCellId>? set) && set.Remove(id) && set.Count == 0)
                    dependents.Remove(read);
        }

        // The formula cells among the seeds and everything reading them, transitively.
        private HashSet<SheetCellId> Downstream(List<SheetCellId> seeds)
        {
            HashSet<SheetCellId> dirty = new HashSet<SheetCellId>();
            Queue<SheetCellId> queue = new Queue<SheetCellId>();
            foreach (SheetCellId seed in seeds)
            {
                if (formulas.ContainsKey(seed)) dirty.Add(seed);
                queue.Enqueue(seed);
            }

            while (queue.Count > 0)
                if (dependents.TryGetValue(queue.Dequeue(), out HashSet<SheetCellId>? readers))
                    foreach (SheetCellId reader in readers)
                        if (dirty.Add(reader)) queue.Enqueue(reader);
            return dirty;
        }

        // Kahn order over the dirty cells; whatever never frees is in or behind a cycle.
        private void Recalc(HashSet<SheetCellId> dirty)
        {
            Dictionary<SheetCellId, int> waiting = new Dictionary<SheetCellId, int>(dirty.Count);
            Queue<SheetCellId> ready = new Queue<SheetCellId>();
            foreach (SheetCellId id in dirty)
            {
                int count = 0;
                foreach (SheetCellId read in precedents[id])
                    if (dirty.Contains(read)) count++;
                waiting[id] = count;
                if (count == 0) ready.Enqueue(id);
            }

            while (ready.Count > 0)
            {
                SheetCellId id = ready.Dequeue();
                values[id] = formulas[id].Evaluate(this, id.page);
                if (!dependents.TryGetValue(id, out HashSet<SheetCellId>? readers)) continue;
                foreach (SheetCellId reader in readers)
                    if (waiting.TryGetValue(reader, out int left) && left > 0)
                    {
                        waiting[reader] = left - 1;
                        if (left == 1) ready.Enqueue(reader);
                    }
            }

            foreach ((SheetCellId id, int left) in waiting)
                if (left > 0) values[id] = SheetValue.Error(SheetFormula.cycle);
        }
        #endregion
    }
}
