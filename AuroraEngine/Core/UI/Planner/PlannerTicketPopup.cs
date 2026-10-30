using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Controls;
using ArctisAurora.Core.UI.Menus;
using ArctisAurora.Core.UI.Text;
using ArctisAurora.Core.Users;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace ArctisAurora.Core.UI.Planner
{
    // A ticket's fields in a popup; Enter in any box applies them as one step, Esc or an outside click drops them.
    internal sealed class PlannerTicketPopup
    {
        // popup sizing
        private const float width = 320f;
        private const float rowHeight = 26f;
        private const float captionWidth = 90f;
        private const int descriptionRows = 3;
        private const int fontSize = 14;

        private const string dayFormat = "yyyy-MM-dd";
        private const string timeFormat = "yyyy-MM-dd HH:mm";

        private readonly PlannerEditorControl editor;
        private readonly Control from;
        private readonly PlannerTicket? ticket;
        private readonly PlannerTicket draft;
        private readonly Guid filedUnder;
        private readonly TextBoxControl name = new TextBoxControl();
        private readonly RegexTextControl description = new RegexTextControl();
        private readonly TextBoxControl start = new TextBoxControl();
        private readonly TextBoxControl end = new TextBoxControl();
        private readonly TextBoxControl assignees = new TextBoxControl();
        private readonly TextBoxControl before = new TextBoxControl();
        private readonly TextBoxControl after = new TextBoxControl();
        private bool done;

        private const string noTicket = "(none)";

        private PlannerTicketPopup(PlannerEditorControl editor, Control from, PlannerTicket? ticket, TimeRange? time, PlannerCategory? category)
        {
            this.editor = editor;
            this.from = from;
            this.ticket = ticket;
            draft = ticket?.Clone() ?? NewTicket(editor, time, category);
            filedUnder = draft.categoryId;
        }

        // Opens on a ticket, or on a new one over time (else today) when ticket is null.
        public static void Open(PlannerEditorControl editor, Control from, Vector2 point, PlannerTicket? ticket, TimeRange? time = null, PlannerCategory? category = null) =>
            new PlannerTicketPopup(editor, from, ticket, time, category).Show(point);

        // Today, all day, created now by the current user, last in the given category, else the selected ticket's.
        private static PlannerTicket NewTicket(PlannerEditorControl editor, TimeRange? time, PlannerCategory? filed)
        {
            PlannerDocument document = editor.document;
            PlannerCategory category = filed ?? (editor.selected != null ? document.CategoryOf(editor.selected) : document.categories[0]);
            DateTime now = editor.Now;
            DateOnly today = DateOnly.FromDateTime(now);
            return new PlannerTicket
            {
                name = "New ticket",
                categoryId = category.id,
                time = time ?? TimeRange.Days(today, today),
                creator = User.current,
                created = now,
                order = document.NextOrder(category)
            };
        }

        private void Show(Vector2 point)
        {
            StackPanelControl content = new StackPanelControl { alpha = 0f, Spacing = 4f, horizontalAlignment = HorizontalAlignment.Stretch };
            content.AddChild(Row("Name", Box(name, draft.name)));

            description.text = draft.description;
            description.fontSize = fontSize;
            description.role = PaletteRole.Field;
            description.widthStar = 1f;
            description.minHeight = rowHeight * descriptionRows;
            description.onCommit = _ => Finish();
            description.onCancel = Cancel;
            content.AddChild(Row("Description", description));

            List<PlannerCategory> categories = editor.document.categories;
            DropdownControl category = new DropdownControl
            {
                preferredHeight = rowHeight,
                widthStar = 1f,
                role = PaletteRole.SubField,
                cornerRole = CornerRole.Control,
                options = categories.Select(c => c.name).ToList(),
                selected = editor.document.CategoryOf(draft).name
            };
            category.onPicked = picked => draft.categoryId = categories.First(c => c.name == picked).id;
            content.AddChild(Row("Category", category));
            content.AddChild(Row("Start", Box(start, Format(draft.time, false))));
            content.AddChild(Row("End", Box(end, Format(draft.time, true))));
            content.AddChild(Row("Assignees", Box(assignees, string.Join(", ", draft.assignees.Select(user => user.name)))));
            content.AddChild(Row("Before", Box(before, FormatAttachments(draft, true))));
            content.AddChild(Row("After", Box(after, FormatAttachments(draft, false))));

            PlannerDocument document = editor.document;
            PlannerTicket self = ticket ?? draft;
            List<PlannerTicket> leaders = document.tickets.Where(other => !ReferenceEquals(other, self) && !document.Upstream(self, other)).ToList();
            DropdownControl follows = new DropdownControl
            {
                preferredHeight = rowHeight,
                widthStar = 1f,
                role = PaletteRole.SubField,
                cornerRole = CornerRole.Control,
                options = leaders.Select(t => t.name).Prepend(noTicket).ToList(),
                selected = draft.follows is Guid id && document.Find(id) is PlannerTicket leader ? leader.name : noTicket
            };
            follows.onPicked = picked => draft.follows = picked == noTicket ? null : leaders.First(t => t.name == picked).id;
            content.AddChild(Row("Follows", follows));

            ColorPickerControl picker = new ColorPickerControl { hex = editor.document.ColorOf(draft) };
            picker.onPicked = hex => draft.colorHex = hex;
            content.AddChild(Row("Colour", picker));

            content.AddChild(Row("Created by", new LabelControl
            {
                text = draft.creator?.name ?? "unknown",
                fontSize = fontSize,
                role = PaletteRole.MutedInk,
                hitTestable = false,
                verticalPosition = 0.5f
            }));

            ButtonControl apply = new ButtonControl { preferredHeight = rowHeight, padding = new Thickness(12f, 0f), horizontalAlignment = HorizontalAlignment.Right };
            apply.PaintOr(null, PaletteRole.Accent);
            apply.AddChild(new LabelControl { text = "Apply", fontSize = fontSize, role = PaletteRole.Ink, hitTestable = false, verticalPosition = 0.5f });
            apply.RegisterOnPress(e =>
            {
                if (e.button != PointerEvent.leftButton) return false;
                Finish();
                return true;
            });
            content.AddChild(apply);
            ContextMenus.Open(new List<ContextMenuEntry> { new ContextMenuContent(content) }, from, point, width, onClosed: Cancel);

            UIEngine.SetActiveControl(name);
            name.Focus();
        }

        private TextBoxControl Box(TextBoxControl box, string text)
        {
            box.text = text;
            box.preferredHeight = rowHeight;
            box.fontSize = fontSize;
            box.role = PaletteRole.Field;
            box.widthStar = 1f;
            box.onCommit = _ => Finish();
            box.onCancel = Cancel;
            return box;
        }

        private static Control Row(string caption, Control field)
        {
            StackPanelControl row = new StackPanelControl
            {
                orientation = StackPanelControl.Orientation.Horizontal,
                alpha = 0f,
                horizontalAlignment = HorizontalAlignment.Stretch
            };
            row.AddChild(new LabelControl
            {
                text = caption,
                fontSize = fontSize,
                role = PaletteRole.MutedInk,
                hitTestable = false,
                preferredWidth = captionWidth,
                preferredHeight = rowHeight,
                verticalPosition = 0.5f
            });
            row.AddChild(field);
            return row;
        }

        private void Finish()
        {
            if (done) return;
            Close();

            if (draft.categoryId != filedUnder) draft.order = editor.document.NextOrder(editor.document.CategoryOf(draft));
            draft.name = name.text.Trim();
            draft.description = description.text.Trim();
            draft.time = Parse(start.text, end.text, draft.time);
            draft.assignees.Clear();
            foreach (string assignee in assignees.text.Split(','))
                if (assignee.Trim() is { Length: > 0 } trimmed) draft.assignees.Add(User.Named(trimmed));
            draft.attachments.Clear();
            draft.attachments.AddRange(ParseAttachments(before.text, true));
            draft.attachments.AddRange(ParseAttachments(after.text, false));
            editor.Apply(ticket, draft);
        }

        // "Travel 30m, Prep 1h" for one side.
        internal static string FormatAttachments(PlannerTicket ticket, bool side) =>
            string.Join(", ", ticket.attachments.Where(a => a.before == side).Select(a => $"{a.kind} {Length(a.duration)}"));

        private static string Length(TimeSpan duration)
        {
            int hoursIn = (int)duration.TotalHours;
            int minutes = duration.Minutes;
            return hoursIn == 0 ? $"{minutes}m" : minutes == 0 ? $"{hoursIn}h" : $"{hoursIn}h{minutes}m";
        }

        // Comma-separated "Kind length": a bare number is minutes, no length takes the kind's; an unknown kind is dropped.
        internal static List<PlannerAttachment> ParseAttachments(string text, bool side)
        {
            List<PlannerAttachment> parsed = new List<PlannerAttachment>();
            foreach (string entry in text.Split(','))
            {
                Match match = Regex.Match(entry.Trim(), @"^(?<kind>[^\d]+?)\s*(?<length>\d[\d\shm]*)?$", RegexOptions.IgnoreCase);
                if (!match.Success || PlannerAttachmentKinds.Find(match.Groups["kind"].Value.Trim()) is not PlannerAttachmentKind kind) continue;

                TimeSpan duration = TimeSpan.Zero;
                foreach (Match part in Regex.Matches(match.Groups["length"].Value, @"(\d+)\s*([hm]?)", RegexOptions.IgnoreCase))
                {
                    int amount = int.Parse(part.Groups[1].Value, CultureInfo.InvariantCulture);
                    duration += part.Groups[2].Value.Equals("h", StringComparison.OrdinalIgnoreCase) ? TimeSpan.FromHours(amount) : TimeSpan.FromMinutes(amount);
                }
                if (duration <= TimeSpan.Zero) duration = TimeSpan.FromMinutes(kind.minutes);
                parsed.Add(new PlannerAttachment(kind.name, side, duration));
            }
            return parsed;
        }

        private void Cancel()
        {
            if (done) return;
            Close();
        }

        private void Close()
        {
            done = true;
            ContextMenus.Close();
            UIEngine.SetActiveControl(from);
        }

        // An all-day end shows its last day, not the midnight after it.
        private static string Format(TimeRange time, bool isEnd)
        {
            DateTime at = isEnd ? time.allDay ? time.end.AddDays(-1) : time.end : time.start;
            return at.ToString(time.allDay ? dayFormat : timeFormat, CultureInfo.InvariantCulture);
        }

        // Two dates make an all-day range, a time in either makes a timed one; an unreadable box keeps what it had.
        private static TimeRange Parse(string startText, string endText, TimeRange was)
        {
            bool startRead = TryRead(startText, out DateTime first, out bool startDay);
            bool endRead = TryRead(endText, out DateTime last, out bool endDay);
            if (!startRead) { first = was.start; startDay = was.allDay; }
            if (!endRead) { last = was.allDay ? was.end.AddDays(-1) : was.end; endDay = was.allDay; }

            if (startDay && endDay)
            {
                DateTime stop = last.AddDays(1);
                return was with { start = first, end = stop > first ? stop : first.AddDays(1), allDay = true };
            }
            return was with { start = first, end = last > first ? last : first.AddHours(1), allDay = false };
        }

        private static bool TryRead(string text, out DateTime time, out bool day)
        {
            day = DateTime.TryParseExact(text.Trim(), dayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
            return day || DateTime.TryParseExact(text.Trim(), timeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
        }
    }
}
