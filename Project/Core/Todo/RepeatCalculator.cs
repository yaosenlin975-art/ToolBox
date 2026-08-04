namespace ToolBox.Core.Todo;

public static class RepeatCalculator
{
    public static DateTime CalculateNextResetTime(
        DateTime lastReset,
        ERepeatFrequency frequency,
        int reminderHour = 9,
        int reminderMinute = 0)
    {
        // ReminderHour/Minute 为用户本地时间（与旧 Cron 语义一致），日期运算在本地时区进行，
        // 避免 UTC 日期与本地日期错位（如本地 6:00 在 UTC 是前一天 22:00）。
        var anchor = lastReset.Kind switch
        {
            DateTimeKind.Utc => lastReset,
            // 兼容旧数据：无时区后缀的 JSON 时间戳按 UTC 处理
            _ => DateTime.SpecifyKind(lastReset, DateTimeKind.Utc),
        };
        anchor = anchor.ToLocalTime();

        var baseTime = frequency switch
        {
            ERepeatFrequency.Daily => anchor.AddDays(1),
            ERepeatFrequency.Weekly => anchor.AddDays(7),
            ERepeatFrequency.Monthly => AddMonthsSafe(anchor, 1),
            ERepeatFrequency.Yearly => AddYearsSafe(anchor, 1),
            _ => anchor.AddDays(1)
        };
        var nextLocal = new DateTime(
            baseTime.Year, baseTime.Month, baseTime.Day,
            reminderHour, reminderMinute, 0);
        return TimeZoneInfo.ConvertTimeToUtc(nextLocal);
    }

    /// <summary>
    /// 计算下一次提醒时间：从 lastReset 起按频率推进。
    /// 与 CalculateNextResetTime 同一套相对计算（唯一时间基准），
    /// 提醒注册与补齐/重置共用，避免 Cron 固定星期/日期造成的不一致。
    /// </summary>
    public static DateTime GetNextOccurrence(
        DateTime lastReset,
        ERepeatFrequency frequency,
        DateTime afterUtc,
        int reminderHour = 9,
        int reminderMinute = 0)
    {
        var next = CalculateNextResetTime(lastReset, frequency, reminderHour, reminderMinute);
        while (next <= afterUtc)
            next = CalculateNextResetTime(next, frequency, reminderHour, reminderMinute);
        return next;
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
