namespace ToolBox.Core.Todo;

public class TodoItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string? ParentId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsCompleted { get; set; }
    public int Priority { get; set; }
    public string Category { get; set; } = "默认";
    public List<string> Tags { get; set; } = new();
    public DateTime? DueDate { get; set; }
    public int Progress { get; set; }
    public string? SessionId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public bool IsTrashed { get; set; }

    // --- 重复待办字段 ---
    public RepeatConfig? RepeatConfig { get; set; }
    public DateTime? LastResetAt { get; set; }

    public bool IsRepeating => RepeatConfig != null;

    public bool HasChildren => false; // 实际由 TodoStore 判断，此处仅为标记意图

    public string StatusText => IsCompleted ? "✅ 已完成" : "○ 待办";
}
