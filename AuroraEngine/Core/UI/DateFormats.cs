using ArctisAurora.Core.ECS.EngineEntity;
using ArctisAurora.Core.Registry;
using ArctisAurora.Core.UI.Notes;
using ArctisAurora.Core.UI.Planner;
using ArctisAurora.EngineWork;
using ArctisAurora.EngineWork.Rendering;
using System.Globalization;

namespace ArctisAurora.Core.UI
{
    // Week start, time and date text from DateTimeSettings, falling back to the system's culture.
    public static class DateFormats
    {
        private static DateTimeSettings Settings => SettingsRegistry.Get<DateTimeSettings>();

        private static DateTimeFormatInfo SystemFormat => CultureInfo.CurrentCulture.DateTimeFormat;

        public static DayOfWeek FirstDay =>
            Settings.weekStart.day == WeekDay.System ? SystemFormat.FirstDayOfWeek : (DayOfWeek)((int)Settings.weekStart.day % 7);

        public static DateOnly WeekStart(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek - (int)FirstDay + 7) % 7));

        public static DateTime WeekStart(DateTime time) => time.Date.AddDays(-(((int)time.DayOfWeek - (int)FirstDay + 7) % 7));

        private static bool TwelveHour => Settings.timeFormat.clock switch
        {
            ClockFormat.H12 => true,
            ClockFormat.H24 => false,
            _ => SystemFormat.ShortTimePattern.Contains('h')
        };

        private static DateOrder Order
        {
            get
            {
                DateOrder order = Settings.dateFormat.order;
                if (order != DateOrder.System) return order;
                string pattern = SystemFormat.ShortDatePattern;
                int y = pattern.IndexOf('y'), m = pattern.IndexOf('M'), d = pattern.IndexOf('d');
                if (y >= 0 && y < m && y < d) return DateOrder.YearMonthDay;
                return m >= 0 && m < d ? DateOrder.MonthDayYear : DateOrder.DayMonthYear;
            }
        }

        // "14:30" or "2:30 PM".
        public static string Time(DateTime time) => time.ToString(TwelveHour ? "h:mm tt" : "HH:mm", CultureInfo.InvariantCulture);

        // A whole hour: "14:00" or "2 PM".
        public static string Hour(DateTime time) => time.ToString(TwelveHour ? "h tt" : "HH:00", CultureInfo.InvariantCulture);

        // A whole hour where there is little room: "14" or "2PM".
        public static string ShortHour(DateTime time) => time.ToString(TwelveHour ? "htt" : "HH", CultureInfo.InvariantCulture);

        // All digits: the system's own pattern, or the chosen order.
        public static string Date(DateTime day)
        {
            if (Settings.dateFormat.order == DateOrder.System)
                return day.ToString(SystemFormat.ShortDatePattern, CultureInfo.CurrentCulture);
            return day.ToString(Order switch
            {
                DateOrder.MonthDayYear => "MM/dd/yyyy",
                DateOrder.YearMonthDay => "yyyy-MM-dd",
                _ => "dd/MM/yyyy"
            }, CultureInfo.InvariantCulture);
        }

        // Month by name: "7 Oct 2026", "Oct 7, 2026" or "2026 Oct 7".
        public static string DayMonth(DateTime day, bool year)
        {
            string format = Order switch
            {
                DateOrder.MonthDayYear => year ? "MMM d, yyyy" : "MMM d",
                DateOrder.YearMonthDay => year ? "yyyy MMM d" : "MMM d",
                _ => year ? "d MMM yyyy" : "d MMM"
            };
            return day.ToString(format, CultureInfo.InvariantCulture);
        }

        [A_XSDActionDependency("Planner.Reformat", "Settings", "Redraws open planners and note properties with the date and time settings")]
        public static void Reformat()
        {
            foreach (RenderWindow window in Engine.windows.Values)
                if (window.ui?.uiRoot is Control root) Reformat(root);
        }

        private static void Reformat(Control control)
        {
            if (control is PlannerEditorControl editor)
            {
                editor.chart?.RowsChanged();
                editor.board?.RebuildSoon();
                editor.calendar?.Changed();
                return;
            }
            if (control is NotePropertiesControl properties)
            {
                properties.Refresh();
                return;
            }

            foreach (Entity child in control.children)
                if (child is Control next) Reformat(next);
        }
    }
}
