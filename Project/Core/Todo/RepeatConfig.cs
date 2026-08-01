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
}
