using System.Text.Json.Serialization;

namespace ToolBox.Core.Llm;

public class ChatMessage
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty; // system/user/assistant/tool

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("imagePath")]
    public string? ImagePath { get; set; }

    [JsonPropertyName("toolCalls")]
    public IList<ToolCallInfo>? ToolCalls { get; set; }

    [JsonPropertyName("toolCallId")]
    public string? ToolCallId { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class ToolCallInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = string.Empty;

    [JsonPropertyName("result")]
    public string? Result { get; set; }

    [JsonPropertyName("isError")]
    public bool IsError { get; set; }
}
