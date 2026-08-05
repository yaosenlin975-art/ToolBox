namespace ToolBox.Core.Todo;

public enum ERepeatFrequency
{
    Daily,
    Weekly,
    Monthly,
    Yearly
}

public class RepeatConfig
{
    public ERepeatFrequency Frequency { get; set; } = ERepeatFrequency.Daily;
    public DateTime StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public int ReminderHour { get; set; } = 9;
    public int ReminderMinute { get; set; } = 0;

    /// <summary>每周：星期几 (1=周一 ~ 7=周日)，null 则跟随 StartDate 的星期</summary>
    public int? RepeatDayOfWeek { get; set; }

    /// <summary>每月：几号 (1~31)，超出当月天数时自动 clamp，null 则跟随 StartDate 的日期</summary>
    public int? RepeatDayOfMonth { get; set; }

    /// <summary>每年：月份 (1~12)，null 则跟随 StartDate 的月份</summary>
    public int? RepeatMonth { get; set; }

    /// <summary>每年：几号 (1~31)，null 则跟随 StartDate 的日期</summary>
    public int? RepeatDay { get; set; }
}
