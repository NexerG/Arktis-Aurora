using ArctisAurora.Core.Diagnostics;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.Testing;
using ArctisAurora.Core.UI;
using ArctisAurora.Core.UI.Containers;
using ArctisAurora.Core.UI.Planner;
using ArctisAurora.Core.Users;
using ArctisAurora.EngineWork;
using System.Numerics;

namespace ArctisAurora.Tests
{
    internal static class PlannerPerfTests
    {
        // planner shape and timeline, in ticks
        private const int categoryCount = 20;
        private const int ticketsPerCategory = 100;
        private const int hugeTicketsPerCategory = 500;
        private const int spanDays = 365;
        private const int warmupTicks = 30;
        private const int measuredTicks = 120;

        // scroll sweep, resize width and drag distance
        private const int sweep = 60;
        private const float editorWidth = 1000f;
        private const int dragDays = 10;

        [A_XSDActionDependency("Perf.Planner.Gantt.Static", "Test")]
        private static IEnumerator<int> Static(TestContext t) => Measure(t, Show(t, ticketsPerCategory), _ => { });

        [A_XSDActionDependency("Perf.Planner.Gantt.Static.Huge", "Test")]
        private static IEnumerator<int> StaticHuge(TestContext t) => Measure(t, Show(t, hugeTicketsPerCategory), _ => { });

        [A_XSDActionDependency("Perf.Planner.Gantt.ScrollVertical", "Test")]
        private static IEnumerator<int> ScrollVertical(TestContext t) => ScrollRows(t, Show(t, ticketsPerCategory));

        [A_XSDActionDependency("Perf.Planner.Gantt.ScrollVertical.Huge", "Test")]
        private static IEnumerator<int> ScrollVerticalHuge(TestContext t) => ScrollRows(t, Show(t, hugeTicketsPerCategory));

        [A_XSDActionDependency("Perf.Planner.Gantt.ScrollHorizontal", "Test")]
        private static IEnumerator<int> ScrollHorizontal(TestContext t)
        {
            PlannerEditorControl editor = Show(t, ticketsPerCategory);
            return Measure(t, editor, i =>
            {
                GanttChartControl chart = editor.chart!;
                editor.scroller.SetScrollOffset(new Vector2(chart.Offset(chart.origin.AddDays(Triangle(i))), 0f));
            });
        }

        [A_XSDActionDependency("Perf.Planner.Gantt.Zoom", "Test")]
        private static IEnumerator<int> Zoom(TestContext t)
        {
            PlannerEditorControl editor = Show(t, ticketsPerCategory);
            return Measure(t, editor, i => editor.SetZoom((PlannerZoom)(i % 4)));
        }

        [A_XSDActionDependency("Perf.Planner.Gantt.ContentZoom", "Test")]
        private static IEnumerator<int> ContentZoom(TestContext t)
        {
            PlannerEditorControl editor = Show(t, ticketsPerCategory);
            float zoom = editor.contentZoom;
            return Measure(t, editor, i => editor.SetContentZoom(i % 2 == 0 ? zoom * 1.1f : zoom));
        }

        [A_XSDActionDependency("Perf.Planner.Gantt.Resize", "Test")]
        private static IEnumerator<int> Resize(TestContext t)
        {
            PlannerEditorControl editor = Show(t, ticketsPerCategory);
            editor.horizontalAlignment = HorizontalAlignment.Left;
            editor.preferredWidth = editorWidth;
            return Measure(t, editor, i => editor.preferredWidth = editorWidth - i % 2);
        }

        [A_XSDActionDependency("Perf.Planner.Gantt.Expand", "Test")]
        private static IEnumerator<int> Expand(TestContext t)
        {
            PlannerEditorControl editor = Show(t, ticketsPerCategory);
            PlannerTicket described = editor.document.tickets[0];
            return Measure(t, editor, i => editor.Expand(i % 2 == 0 ? described : null));
        }

        [A_XSDActionDependency("Perf.Planner.Gantt.DragBar", "Test")]
        private static IEnumerator<int> DragBar(TestContext t)
        {
            PlannerEditorControl editor = Show(t, ticketsPerCategory);
            PlannerTicket ticket = editor.document.tickets[1];
            yield return 2;
            editor.scroller.SetScrollOffset(Vector2.Zero);
            yield return 2;

            GanttChartControl chart = editor.chart!;
            yield return DragDays(t, chart, ticket, warmupTicks);
            yield return 2;

            t.StartMeasure();
            yield return DragDays(t, chart, ticket, measuredTicks);
            yield return t.EndMeasure();
            t.Show(new StackPanelControl());
        }

        [A_XSDActionDependency("Perf.Planner.Gantt.Edit", "Test")]
        private static IEnumerator<int> Edit(TestContext t)
        {
            PlannerEditorControl editor = Show(t, ticketsPerCategory);
            PlannerTicket ticket = editor.document.tickets[0];
            return Measure(t, editor, i =>
            {
                TimeSpan shift = TimeSpan.FromDays(i % 2 == 0 ? 1 : -1);
                PlannerTicket draft = ticket.Clone();
                draft.time = ticket.time with { start = ticket.time.start + shift, end = ticket.time.end + shift };
                editor.Apply(ticket, draft);
            });
        }

        [A_XSDActionDependency("Perf.Planner.Gantt.Load", "Test")]
        private static IEnumerator<int> Load(TestContext t) => FileTicks(t, path =>
        {
            Profiling.Zone.Start("Planner.Load");
            PlannerXml.Load(path);
            Profiling.Zone.End("Planner.Load");
        });

        [A_XSDActionDependency("Perf.Planner.Gantt.Save", "Test")]
        private static IEnumerator<int> Save(TestContext t)
        {
            PlannerDocument planner = LargePlanner(ticketsPerCategory);
            return FileTicks(t, path =>
            {
                Profiling.Zone.Start("Planner.Save");
                PlannerXml.Save(planner, path);
                Profiling.Zone.End("Planner.Save");
            });
        }

        // Runs work once a tick through warm-up and the measured ticks.
        private static IEnumerator<int> Measure(TestContext t, PlannerEditorControl editor, Action<int> work)
        {
            yield return 2;
            for (int i = 0; i < warmupTicks; i++)
            {
                work(i);
                yield return 1;
            }

            t.StartMeasure();
            for (int i = 0; i < measuredTicks; i++)
            {
                work(i);
                yield return 1;
            }
            yield return t.EndMeasure();
            t.Show(new StackPanelControl());
        }

        // Moves the view a row a tick down and back up.
        private static IEnumerator<int> ScrollRows(TestContext t, PlannerEditorControl editor) =>
            Measure(t, editor, i => editor.scroller.SetScrollOffset(new Vector2(editor.scroller.GetScrollOffset().X, Triangle(i) * GanttChartControl.rowHeight)));

        // Drags a bar dragDays to the right over steps ticks.
        private static int DragDays(TestContext t, GanttChartControl chart, PlannerTicket ticket, int steps)
        {
            LayoutRect bar = chart.BarRect(ticket);
            Vector2 from = new Vector2(bar.x + bar.width * 0.5f, bar.y + bar.height * 0.5f);
            Vector2 to = from + new Vector2(chart.X(chart.origin.AddDays(dragDays)) - chart.X(chart.origin), 0f);
            return t.Drag(chart, from, to, steps);
        }

        // Writes the large planner to a temp file, then runs work on its path once a tick.
        private static IEnumerator<int> FileTicks(TestContext t, Action<string> work)
        {
            string path = Path.Combine(Path.GetTempPath(), $"aurora-planner-{Guid.NewGuid():N}{PlannerDocument.extension}");
            PlannerXml.Save(LargePlanner(ticketsPerCategory), path);
            for (int i = 0; i < warmupTicks + measuredTicks; i++)
            {
                if (i == warmupTicks) t.StartMeasure();
                work(path);
                yield return 1;
            }
            yield return t.EndMeasure();
            File.Delete(path);
        }

        private static int Triangle(int i) => i % sweep < sweep / 2 ? i % sweep : sweep - i % sweep;

        // categoryCount categories of perCategory tickets over a year; every 10th described, assigned and attached, every 20th following the one before.
        private static PlannerDocument LargePlanner(int perCategory)
        {
            PlannerDocument planner = new PlannerDocument { name = "Perf" };
            int total = categoryCount * perCategory;
            DateOnly first = new DateOnly(2026, 1, 5);
            PlannerTicket? previous = null;
            for (int c = 0; c < categoryCount; c++)
            {
                PlannerCategory category = new PlannerCategory { name = $"Category {c}", colorHex = c % 2 == 0 ? PlannerDocument.defaultColor : "#30A46C" };
                planner.categories.Add(category);
                for (int i = 0; i < perCategory; i++)
                {
                    int n = c * perCategory + i;
                    DateOnly start = first.AddDays(n * spanDays / total);
                    PlannerTicket ticket = new PlannerTicket
                    {
                        name = $"Ticket {n}",
                        categoryId = category.id,
                        time = TimeRange.Days(start, start.AddDays(n % 5)),
                        creator = User.current,
                        order = i
                    };
                    if (n % 10 == 0)
                    {
                        ticket.description = "Gather the inputs.\nDraft the first pass.\nReview it with the team.";
                        ticket.assignees.Add(User.current);
                        ticket.attachments.Add(new PlannerAttachment("Travel", true, TimeSpan.FromMinutes(30)));
                    }
                    if (n % 20 == 1 && previous != null) ticket.follows = previous.id;
                    planner.tickets.Add(ticket);
                    previous = ticket;
                }
            }
            return planner;
        }

        // Shows the large planner in the Gantt at Day zoom, clock and date settings pinned.
        private static PlannerEditorControl Show(TestContext t, int perCategory)
        {
            DateTimeSettings dates = SettingsRegistry.Get<DateTimeSettings>();
            dates.weekStart.day = WeekDay.Monday;
            dates.timeFormat.clock = ClockFormat.H24;
            dates.dateFormat.order = DateOrder.DayMonthYear;

            PlannerEditorControl editor = new PlannerEditorControl { clock = () => new DateTime(2026, 10, 6, 12, 0, 0) };
            editor.Load(LargePlanner(perCategory));
            t.Show(editor);
            return editor;
        }
    }
}
