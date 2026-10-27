namespace Lumen.Core;

/// <summary>Что сделать по расписанию.</summary>
public enum ScheduleAction
{
    MainOn,
    MainOff,
    BackOn,
    BackOff,
    AllOff,
    SyncStart,
    SyncStop
}

/// <summary>Дни недели битами: Пн = 1, Вт = 2 … Вс = 64.</summary>
[Flags]
public enum WeekDays
{
    None = 0,
    Mon = 1, Tue = 2, Wed = 4, Thu = 8, Fri = 16, Sat = 32, Sun = 64,
    Weekdays = Mon | Tue | Wed | Thu | Fri,
    Weekend = Sat | Sun,
    All = Weekdays | Weekend
}

/// <summary>Правило: в такое-то время по таким-то дням сделать действие.</summary>
public sealed class ScheduleRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    public int Hour { get; set; }
    public int Minute { get; set; }
    public WeekDays Days { get; set; } = WeekDays.All;
    public ScheduleAction Action { get; set; }

    /// <summary>Для «Включить свет»: яркость 1..100 и температура в К; null — как было.</summary>
    public int? Brightness { get; set; }
    public int? Temperature { get; set; }

    public bool RunsOn(DayOfWeek day) => (Days & ToFlag(day)) != 0;

    public static WeekDays ToFlag(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => WeekDays.Mon,
        DayOfWeek.Tuesday => WeekDays.Tue,
        DayOfWeek.Wednesday => WeekDays.Wed,
        DayOfWeek.Thursday => WeekDays.Thu,
        DayOfWeek.Friday => WeekDays.Fri,
        DayOfWeek.Saturday => WeekDays.Sat,
        _ => WeekDays.Sun
    };
}

/// <summary>
/// Расписание без таймеров и состояния: «какие правила сработали между двумя моментами»
/// и «что будет дальше». Таймер в приложении просто спрашивает это раз в несколько секунд.
/// </summary>
public static class Scheduler
{
    /// <summary>
    /// Если между проверками прошло больше этого (ПК спал), пропущенные события не догоняем:
    /// иначе после сна разом выполнились бы и «включить», и «выключить».
    /// </summary>
    public static readonly TimeSpan MaxCatchUp = TimeSpan.FromMinutes(3);

    /// <summary>Правила, чьё время попало в (after, upTo]. Порядок — по времени срабатывания.</summary>
    public static IReadOnlyList<ScheduleRule> Due(IEnumerable<ScheduleRule> rules, DateTime after, DateTime upTo)
    {
        if (upTo <= after) return [];
        if (upTo - after > MaxCatchUp) after = upTo - MaxCatchUp;

        var due = new List<(DateTime At, ScheduleRule Rule)>();
        foreach (var rule in rules)
        {
            if (!rule.Enabled || rule.Days == WeekDays.None) continue;
            // Окно не длиннее нескольких минут — достаточно проверить сегодня и вчера (переход через полночь)
            for (var day = after.Date; day <= upTo.Date; day = day.AddDays(1))
            {
                var at = day.AddHours(rule.Hour).AddMinutes(rule.Minute);
                if (at > after && at <= upTo && rule.RunsOn(day.DayOfWeek)) due.Add((at, rule));
            }
        }
        return due.OrderBy(d => d.At).Select(d => d.Rule).ToList();
    }

    /// <summary>Ближайшие срабатывания после now (для «Далее по расписанию»).</summary>
    public static IReadOnlyList<(DateTime At, ScheduleRule Rule)> Upcoming(IEnumerable<ScheduleRule> rules, DateTime now, int count)
    {
        var result = new List<(DateTime, ScheduleRule)>();
        foreach (var rule in rules)
        {
            if (!rule.Enabled || rule.Days == WeekDays.None) continue;
            for (var d = 0; d <= 7; d++)
            {
                var day = now.Date.AddDays(d);
                var at = day.AddHours(rule.Hour).AddMinutes(rule.Minute);
                if (at <= now || !rule.RunsOn(day.DayOfWeek)) continue;
                result.Add((at, rule));
                break;
            }
        }
        return result.OrderBy(r => r.Item1).Take(count).ToList();
    }
}
