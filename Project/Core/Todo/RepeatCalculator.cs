namespace ToolBox.Core.Todo;

public static class RepeatCalculator
{
    public static DateTime CalculateNextResetTime(
        DateTime lastReset,
        ERepeatFrequency frequency,
        int reminderHour = 9,
        int reminderMinute = 0,
        int? repeatDayOfWeek = null,
        int? repeatDayOfMonth = null,
        int? repeatMonth = null,
        int? repeatDay = null)
    {
        var anchor = lastReset.Kind switch
        {
            DateTimeKind.Utc => lastReset,
            _ => DateTime.SpecifyKind(lastReset, DateTimeKind.Utc),
        };
        anchor = anchor.ToLocalTime();

        var baseTime = frequency switch
        {
            ERepeatFrequency.Daily => anchor.AddDays(1),
            ERepeatFrequency.Weekly => NextWeekday(anchor, repeatDayOfWeek),
            ERepeatFrequency.Monthly => NextMonthDay(anchor, repeatDayOfMonth),
            ERepeatFrequency.Yearly => NextYearDate(anchor, repeatMonth, repeatDay),
            _ => anchor.AddDays(1)
        };
        var nextLocal = new DateTime(
            baseTime.Year, baseTime.Month, baseTime.Day,
            reminderHour, reminderMinute, 0);
        return TimeZoneInfo.ConvertTimeToUtc(nextLocal);
    }

    public static DateTime CalculateNextResetTime(
        DateTime lastReset,
        ERepeatFrequency frequency,
        int reminderHour,
        int reminderMinute,
        RepeatConfig config)
    {
        return CalculateNextResetTime(
            lastReset, frequency, reminderHour, reminderMinute,
            config.RepeatDayOfWeek, config.RepeatDayOfMonth,
            config.RepeatMonth, config.RepeatDay);
    }

    public static DateTime GetNextOccurrence(
        DateTime lastReset,
        ERepeatFrequency frequency,
        DateTime afterUtc,
        int reminderHour = 9,
        int reminderMinute = 0,
        int? repeatDayOfWeek = null,
        int? repeatDayOfMonth = null,
        int? repeatMonth = null,
        int? repeatDay = null)
    {
        var next = CalculateNextResetTime(
            lastReset, frequency, reminderHour, reminderMinute,
            repeatDayOfWeek, repeatDayOfMonth, repeatMonth, repeatDay);
        while (next <= afterUtc)
            next = CalculateNextResetTime(
                next, frequency, reminderHour, reminderMinute,
                repeatDayOfWeek, repeatDayOfMonth, repeatMonth, repeatDay);
        return next;
    }

    public static DateTime GetNextOccurrence(
        DateTime lastReset,
        ERepeatFrequency frequency,
        DateTime afterUtc,
        int reminderHour,
        int reminderMinute,
        RepeatConfig config)
    {
        return GetNextOccurrence(
            lastReset, frequency, afterUtc, reminderHour, reminderMinute,
            config.RepeatDayOfWeek, config.RepeatDayOfMonth,
            config.RepeatMonth, config.RepeatDay);
    }

    /// <summary>
    /// 判断某天是否为该重复配置的发生日（如"每周一""每月5号"只在对应日子发生），
    /// 用于"今日重复"等按日聚合的显示，避免每周/每月/每年待办被当作每日任务展示。
    /// </summary>
    public static bool IsOccurrenceDay(DateTime day, RepeatConfig config, DateTime anchor)
    {
        var localDay = day.ToLocalTime().Date;
        var localAnchor = anchor.ToLocalTime();

        return config.Frequency switch
        {
            ERepeatFrequency.Daily => true,
            ERepeatFrequency.Weekly => WeekdayOf(localDay) == (config.RepeatDayOfWeek ?? WeekdayOf(localAnchor)),
            ERepeatFrequency.Monthly => localDay.Day == ClampDay(config.RepeatDayOfMonth ?? localAnchor.Day, localDay.Year, localDay.Month),
            ERepeatFrequency.Yearly => localDay.Month == (config.RepeatMonth ?? localAnchor.Month)
                                       && localDay.Day == ClampDay(config.RepeatDay ?? localAnchor.Day, localDay.Year, localDay.Month),
            _ => true
        };
    }

    /// <summary>超出当月天数时 clamp（与 NextMonthDay/NextYearDate 的重置语义一致，如 31 号 → 2 月 28 号）</summary>
    private static int ClampDay(int day, int year, int month)
        => Math.Min(day, DateTime.DaysInMonth(year, month));

    /// <summary>1=周一 ~ 7=周日（与 RepeatConfig.RepeatDayOfWeek 语义一致）</summary>
    private static int WeekdayOf(DateTime date)
    {
        var dow = (int)date.DayOfWeek; // .NET: Sunday=0 ~ Saturday=6
        return dow == 0 ? 7 : dow;
    }

    private static DateTime NextWeekday(DateTime anchor, int? targetDayOfWeek)
    {
        if (!targetDayOfWeek.HasValue)
            return anchor.AddDays(7);

        // targetDayOfWeek: 1=周一 ~ 7=周日 (与 .NET DayOfWeek 对齐: Monday=1 .. Sunday=7)
        var currentDow = (int)anchor.DayOfWeek == 0 ? 7 : (int)anchor.DayOfWeek;
        int daysAhead = targetDayOfWeek.Value - currentDow;
        if (daysAhead <= 0) daysAhead += 7;
        return anchor.AddDays(daysAhead);
    }

    private static DateTime NextMonthDay(DateTime anchor, int? targetDay)
    {
        var nextMonth = AddMonthsSafe(anchor, 1);
        if (!targetDay.HasValue)
            return nextMonth;

        int clampedDay = Math.Min(targetDay.Value, DateTime.DaysInMonth(nextMonth.Year, nextMonth.Month));
        return new DateTime(nextMonth.Year, nextMonth.Month, clampedDay,
            anchor.Hour, anchor.Minute, anchor.Second, anchor.Kind);
    }

    private static DateTime NextYearDate(DateTime anchor, int? targetMonth, int? targetDay)
    {
        int m = targetMonth ?? anchor.Month;
        int d = targetDay ?? anchor.Day;

        // 先尝试今年的目标日期
        int currentYear = anchor.Year;
        int clampedD = Math.Min(d, DateTime.DaysInMonth(currentYear, m));
        var candidate = new DateTime(currentYear, m, clampedD,
            anchor.Hour, anchor.Minute, anchor.Second, anchor.Kind);

        // 如果目标日期已过，推到明年
        if (candidate <= anchor)
        {
            int nextYear = currentYear + 1;
            clampedD = Math.Min(d, DateTime.DaysInMonth(nextYear, m));
            candidate = new DateTime(nextYear, m, clampedD,
                anchor.Hour, anchor.Minute, anchor.Second, anchor.Kind);
        }
        return candidate;
    }

    private static DateTime AddMonthsSafe(DateTime date, int months)
    {
        var result = date.AddMonths(months);
        if (result.Day != date.Day)
        {
            var daysInMonth = DateTime.DaysInMonth(result.Year, result.Month);
            return result.AddDays(-result.Day + Math.Min(date.Day, daysInMonth));
        }
        return result;
    }

    private static DateTime AddYearsSafe(DateTime date, int years)
    {
        var result = date.AddYears(years);
        if (result.Day != date.Day)
        {
            var daysInMonth = DateTime.DaysInMonth(result.Year, result.Month);
            return result.AddDays(-result.Day + Math.Min(date.Day, daysInMonth));
        }
        return result;
    }
}
