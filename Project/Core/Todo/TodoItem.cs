namespace ToolBox.Core.Todo;

public class TodoItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string? ParentId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsCompleted { get; set; }
    public int Priority { get; set; }
    public string Category { get; set; } = "\u9ED8\u8BA4";
    public List<string> Tags { get; set; } = new();
    public DateTime? DueDate { get; set; }
    public int Progress { get; set; }
    public string? SessionId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public bool IsTrashed { get; set; }
    public string StatusText => IsCompleted ? "\u2705 \u5DF2\u5B8C\u6210" : "\u25CB \u5F85\u529E";
}
