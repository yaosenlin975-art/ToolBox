namespace ToolBox.Core.Todo;

public static class RepeatCalculator
{
    public static DateTime CalculateNextResetTime(
        DateTime lastReset,
        ERepeatFrequency frequency,
        int reminderHour = 9,
        int reminderMinute = 0)
    {
        var baseTime = frequency switch
        {
            ERepeatFrequency.Daily => lastReset.AddDays(1),
            ERepeatFrequency.Weekly => lastReset.AddDays(7),
            ERepeatFrequency.Monthly => AddMonthsSafe(lastReset, 1),
            ERepeatFrequency.Yearly => AddYearsSafe(lastReset, 1),
            _ => lastReset.AddDays(1)
        };
        return new DateTime(
            baseTime.Year, baseTime.Month, baseTime.Day,
            reminderHour, reminderMinute, 0, DateTimeKind.Utc);
    }

    public static string ToCron(this ERepeatFrequency frequency, int hour = 9, int minute = 0)
    {
        // hour/minute 为本地时间，CronScheduler 以 UTC 计算下次触发，需转换为 UTC
        var utc = DateTime.Today.AddHours(hour).AddMinutes(minute).ToUniversalTime();
        return frequency switch
        {
            ERepeatFrequency.Daily => $"{utc.Minute} {utc.Hour} * * *",
            ERepeatFrequency.Weekly => $"{utc.Minute} {utc.Hour} * * 1",
            ERepeatFrequency.Monthly => $"{utc.Minute} {utc.Hour} 1 * *",
            ERepeatFrequency.Yearly => $"{utc.Minute} {utc.Hour} 1 1 *",
            _ => $"{utc.Minute} {utc.Hour} * * *"
        };
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
