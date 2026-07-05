using ToolBox.Core.Tools;
using ToolBox.Core.Llm;

namespace ToolBox.Core.Providers;

public interface ILlmProvider
{
    string Name { get; }
    IAsyncEnumerable<ChatChunk> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolInfo>? tools = null,
        CancellationToken ct = default);
}