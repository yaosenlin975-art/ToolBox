using Cronos;

namespace ToolBox.Core.Reminder;

public enum ReminderType
{
    Once,       // 一次性提醒
    Recurring   // 重复提醒
}

public class ReminderItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Title { get; set; } = string.Empty;          // 提醒标题（必填）
    public string Message { get; set; } = string.Empty;        // 提醒内容（可选）
    public ReminderType Type { get; set; }                     // 一次性 / 重复
    public DateTime? FireAtUtc { get; set; }                   // 一次性提醒触发时间（UTC）
    public string? CronExpression { get; set; }                // 重复提醒 Cron 表达式
    public bool IsEnabled { get; set; } = true;                // 启用/禁用开关
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastTriggeredAt { get; set; }             // 最近一次触发时间

    /// <summary>计算下次触发时间（本地时间）。一次性：指定时间（已过则 null）；重复：从当前时刻按 Cron 取下一次。</summary>
    public DateTime? GetNextFireLocal()
    {
        if (Type == ReminderType.Once)
        {
            var fireAt = FireAtUtc?.ToLocalTime();
            return fireAt != null && fireAt > DateTime.Now ? fireAt : null;
        }

        if (string.IsNullOrWhiteSpace(CronExpression)) return null;
        try
        {
            var cron = Cronos.CronExpression.Parse(CronExpression);
            var next = cron.GetNextOccurrence(DateTime.UtcNow);
            return next?.ToLocalTime();
        }
        catch
        {
            return null;
        }
    }
}
