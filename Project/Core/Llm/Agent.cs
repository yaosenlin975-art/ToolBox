using ToolBox.Core.Providers;
using ToolBox.Core.Tools;
using System.Runtime.CompilerServices;
using ToolBox.Core.Tools;

namespace ToolBox.Core.Llm;

public class Agent
{
    private readonly ILlmProvider provider;
    private readonly ToolRegistry tools;
    private readonly ISystemPromptBuilder promptBuilder;
    private readonly ChatSession session;
    private CancellationTokenSource? cts;

    public Agent(ILlmProvider provider, ToolRegistry tools, ISystemPromptBuilder promptBuilder, ChatSession session)
    {
        this.provider = provider;
        this.tools = tools;
        this.promptBuilder = promptBuilder;
        this.session = session;
    }

    public ChatSession Session => session;

    public async IAsyncEnumerable<ChatChunk> RunAsync(
        string userMessage,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        session.Status = "running";

        // 构建消息列表：system + 历史 + user
        var messages = BuildMessages();

        try
        {
            // 工具调用循环，最多 10 轮
            for (int round = 0; round < 10; round++)
            {
                ChatChunk? lastChunk = null;
                var chunks = new List<ChatChunk>();
                Exception? lastError = null;
                for (int retry = 0; retry < 3; retry++)
                {
                    chunks.Clear();
                    try
                    {
                        var response = provider.ChatAsync(
                            messages,
                            tools.GetAllTools(),
                            cts.Token);

                        await foreach (var chunk in response)
                        {
                            chunks.Add(chunk);
                            lastChunk = chunk;
                        }
                        lastError = null;
                        break;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        if (retry < 2)
                            await Task.Delay(1000 * (1 << retry), cts.Token);
                    }
                }
                foreach (var chunk in chunks)
                    yield return chunk;
                if (lastError != null)
                {
                    yield return new ChatChunk { Text = "[LLM 调用失败: " + lastError.Message + "]" };
                    session.Status = "idle";
                    yield break;
                }

                // 无工具调用 → 完成
                if (lastChunk?.ToolCall == null)
                    break;

                // 执行工具（单次最多 1 个 tool call）
                var toolResult = ExecuteTool(lastChunk.ToolCall);

                // 追加 assistant 消息（含 tool call）
                messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    ToolCalls = [lastChunk.ToolCall]
                });

                // 追加 tool 结果消息
                messages.Add(new ChatMessage
                {
                    Role = "tool",
                    Content = toolResult,
                    ToolCallId = lastChunk.ToolCall.Id
                });

                // 追加到 session 历史
                session.Messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = lastChunk.Text,
                    ToolCalls = [lastChunk.ToolCall]
                });
                session.Messages.Add(new ChatMessage
                {
                    Role = "tool",
                    Content = toolResult,
                    ToolCallId = lastChunk.ToolCall.Id
                });
            }
        }
        finally
        {
            session.Status = "idle";
            session.UpdatedAt = DateTime.UtcNow;
        }
    }

    private List<ChatMessage> BuildMessages()
    {
        var messages = new List<ChatMessage>
        {
            new ChatMessage { Role = "system", Content = promptBuilder.Build() }
        };
        messages.AddRange(session.Messages);
        // User message is already in session.Messages
        return messages;
    }

    private string ExecuteTool(ToolCallInfo toolCall)
    {
        try
        {
            var args = ParseArguments(toolCall.Arguments);
            var result = tools.Execute(toolCall.Name, args);
            toolCall.Result = result?.Length > 3000 ? result[..3000] + "...[截断]" : result;
            toolCall.IsError = false;
            return toolCall.Result;
        }
        catch (Exception ex)
        {
            toolCall.Result = ex.Message;
            toolCall.IsError = true;
            return "[工具执行异常: " + ex.Message + "]";
        }
    }

    private static Dictionary<string, object> ParseArguments(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public void Cancel()
    {
        cts?.Cancel();
    }
}
