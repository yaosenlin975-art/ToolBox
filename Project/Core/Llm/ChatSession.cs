using System.Text.Json.Serialization;

namespace ToolBox.Core.Llm;

public class ChatSession
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    [JsonPropertyName("title")]
    public string Title { get; set; } = "新会话";

    [JsonPropertyName("isTitleLocked")]
    public bool IsTitleLocked { get; set; }

    [JsonPropertyName("isPinned")]
    public bool IsPinned { get; set; }

    [JsonIgnore]
    public DateTime UpdatedAtLocal => UpdatedAt.ToLocalTime();

    [JsonIgnore]
    public DateTime CreatedAtLocal => CreatedAt.ToLocalTime();

    [JsonIgnore] // 运行时字段，不参与 chats.json 序列化
    public List<ChatMessage> Messages { get; set; } = [];

    [JsonPropertyName("status")]
    public string Status { get; set; } = "idle"; // idle/running/paused

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
