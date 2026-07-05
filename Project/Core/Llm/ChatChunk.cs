namespace ToolBox.Core.Llm;

public class ChatChunk
{
    public string? Text { get; set; }
    public ToolCallInfo? ToolCall { get; set; }
    public bool IsDone { get; set; }
    public string? Error { get; set; }
}
