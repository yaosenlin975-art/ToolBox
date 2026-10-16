using System.Text.Json.Serialization;

namespace ToolBox.Core.Llm;

public sealed class ChatSession
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    [JsonPropertyName("actorId")]
    public Guid ActorId { get; set; } = Guid.NewGuid();

    [JsonPropertyName("title")]
    public string Title { get; set; } = "新会话";

    [JsonPropertyName("isTitleLocked")]
    public bool IsTitleLocked { get; set; }

    [JsonPropertyName("isPinned")]
    public bool IsPinned { get; set; }

    [JsonIgnore]
    public List<ChatMessage> Messages { get; set; } = [];

    [JsonPropertyName("status")]
    public string Status { get; set; } = "idle";

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
