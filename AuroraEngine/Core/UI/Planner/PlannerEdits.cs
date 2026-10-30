using ArctisAurora.Core.Editing;

namespace ArctisAurora.Core.UI.Planner
{
    // Tickets' fields before and after an edit.
    public sealed class PlannerTicketEdit : IEditRecord
    {
        private readonly PlannerDocument document;
        private readonly List<(PlannerTicket ticket, PlannerTicket before, PlannerTicket after)> tickets;

        public PlannerTicketEdit(PlannerDocument document, PlannerTicket ticket, PlannerTicket before, PlannerTicket after)
            : this(document, new List<(PlannerTicket, PlannerTicket, PlannerTicket)> { (ticket, before, after) }) { }

        public PlannerTicketEdit(PlannerDocument document, List<(PlannerTicket ticket, PlannerTicket before, PlannerTicket after)> tickets)
        {
            this.document = document;
            this.tickets = tickets;
        }

        public void Undo()
        {
            foreach ((PlannerTicket ticket, PlannerTicket before, _) in tickets)
                ticket.CopyFrom(before);
            document.Changed();
        }

        public void Redo()
        {
            foreach ((PlannerTicket ticket, _, PlannerTicket after) in tickets)
                ticket.CopyFrom(after);
            document.Changed();
        }
    }

    // A category's name and colour before and after an edit.
    public sealed class PlannerCategoryEdit : IEditRecord
    {
        private readonly PlannerDocument document;
        private readonly PlannerCategory category;
        private readonly (string name, string colorHex) before;
        private readonly (string name, string colorHex) after;

        public PlannerCategoryEdit(PlannerDocument document, PlannerCategory category, (string, string) before, (string, string) after)
        {
            this.document = document;
            this.category = category;
            this.before = before;
            this.after = after;
        }

        public void Undo() => Apply(before);

        public void Redo() => Apply(after);

        private void Apply((string name, string colorHex) fields)
        {
            category.name = fields.name;
            category.colorHex = fields.colorHex;
            document.Changed();
        }
    }

    // A category added to a planner, or removed from it.
    public sealed class PlannerCategoryAddEdit : IEditRecord
    {
        private readonly PlannerDocument document;
        private readonly PlannerCategory category;
        private readonly int index;
        private readonly bool add;

        public PlannerCategoryAddEdit(PlannerDocument document, PlannerCategory category, int index, bool add)
        {
            this.document = document;
            this.category = category;
            this.index = index;
            this.add = add;
        }

        public void Undo() => Apply(!add);

        public void Redo() => Apply(add);

        private void Apply(bool present)
        {
            if (present) document.categories.Insert(Math.Min(index, document.categories.Count), category);
            else document.categories.Remove(category);
            document.Changed();
        }
    }

    // A ticket added to a planner, or removed from it.
    public sealed class PlannerTicketAddEdit : IEditRecord
    {
        private readonly PlannerDocument document;
        private readonly PlannerTicket ticket;
        private readonly int index;
        private readonly bool add;

        public PlannerTicketAddEdit(PlannerDocument document, PlannerTicket ticket, int index, bool add)
        {
            this.document = document;
            this.ticket = ticket;
            this.index = index;
            this.add = add;
        }

        public void Undo() => Apply(!add);

        public void Redo() => Apply(add);

        private void Apply(bool present)
        {
            if (present) document.tickets.Insert(Math.Min(index, document.tickets.Count), ticket);
            else document.tickets.Remove(ticket);
            document.Changed();
        }
    }
}
