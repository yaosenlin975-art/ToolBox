using System.Text;
using ToolBox.Core.Memory;
using ToolBox.Core.Providers;
using ToolBox.Core.Tools;
using System.Runtime.CompilerServices;

namespace ToolBox.Core.Llm;

public class Agent
{
    private readonly ILlmProvider provider;
    private readonly ToolRegistry tools;
    private readonly ISystemPromptBuilder promptBuilder;
    private readonly ChatSession session;
    private readonly ContextCompressor contextCompressor;
    private CancellationTokenSource? cts;
    private List<ChatMessage>? turnMessages;
    private int runCount;

    public Agent(ILlmProvider provider, ToolRegistry tools, ISystemPromptBuilder promptBuilder, ChatSession session)
    {
        this.provider = provider;
        this.tools = tools;
        this.promptBuilder = promptBuilder;
        this.session = session;
        this.contextCompressor = new ContextCompressor();
    }

    public ChatSession Session => session;

    /// <summary>
    /// 本轮对话中由 Agent 生成的所有消息（含最终助手回复与工具调用轮次）。
    /// ChatView 在 RunAsync 结束后将其写入 session.Messages 以完成持久化。
    /// </summary>
    public IReadOnlyList<ChatMessage> TurnMessages => turnMessages ?? [];

    public async IAsyncEnumerable<ChatChunk> RunAsync(
        string userMessage,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        session.Status = "running";
        turnMessages = new List<ChatMessage>();

        // 构建消息列表：system + 历史 + user
        var messages = BuildMessages();

        try
        {
            // 工具调用循环，最多 10 轮
            for (int round = 0; round < 10; round++)
            {
                // 上下文压缩：发送 LLM 请求前检查并压缩超长上下文
                await contextCompressor.CheckAndCompressAsync(messages);

                ChatChunk? lastChunk = null;
                StringBuilder? currentText = null;
                bool llmSuccess = false;

                // 每轮最多 3 次重试（缓冲区模式：try/catch 内收集到 List，外部统一 yield return）
                for (int retry = 0; retry < 3 && !llmSuccess; retry++)
                {
                    lastChunk = null;
                    currentText = null;
                    var chunks = new List<ChatChunk>();
                    try
                    {
                        await foreach (var chunk in provider.ChatAsync(messages, tools.GetAllTools(), cts.Token))
                        {
                            lastChunk = chunk;
                            if (chunk.Text != null)
                            {
                                currentText ??= new StringBuilder();
                                currentText.Append(chunk.Text);
                            }
                            chunks.Add(chunk);
                        }
                        llmSuccess = true;
                    }
                    catch (Exception ex) when (retry < 2 && !cts.Token.IsCancellationRequested)
                    {
                        chunks.Add(new ChatChunk { Error = $"请求失败，正在重试 ({retry + 2}/3): {ex.Message}" });
                        await Task.Delay(1000, cts.Token);
                    }

                    // 统一 yield：成功数据或重试错误信息
                    foreach (var c in chunks)
                    {
                        yield return c;
                    }
                }

                if (!llmSuccess)
                {
                    yield return new ChatChunk { Error = "请求失败，已达最大重试次数（3次）" };
                    break;
                }

                // 无工具调用 → 完成：落盘并通过最后一个 text chunk 把最终文本刷新到 UI
                if (lastChunk?.ToolCall == null)
                {
                    if (currentText != null && currentText.Length > 0)
                    {
                        turnMessages.Add(new ChatMessage { Role = "assistant", Content = currentText.ToString() });
                        yield return new ChatChunk { Text = currentText.ToString() };
                    }
                    break;
                }
// 执行工具（单次最多 1 个 tool call）
                var toolResult = await ExecuteToolAsync(lastChunk.ToolCall).ConfigureAwait(false);

                // 本轮 assistant 消息（前置文本 + tool call）写入本轮持久化集合
                turnMessages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = currentText?.ToString(),
                    ToolCalls = [lastChunk.ToolCall]
                });
                turnMessages.Add(new ChatMessage
                {
                    Role = "tool",
                    Content = toolResult,
                    ToolCallId = lastChunk.ToolCall.Id
                });

                // 追加到下一轮 LLM 调用的消息上下文（不在此处持久化 session）
                messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    ToolCalls = [lastChunk.ToolCall]
                });
                messages.Add(new ChatMessage
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

            // 定期清理过期记忆（每 20 次对话清理一次）
            runCount++;
            if (runCount % 20 == 0)
            {
                try { MemoryStore.Instance.Cleanup(); } catch { /* 静默忽略清理失败 */ }
            }
        }
    }

    private List<ChatMessage> BuildMessages()
    {
        var memories = MemoryStore.Instance.GetRelevant(session.Id);
        var messages = new List<ChatMessage>
        {
            new ChatMessage { Role = "system", Content = promptBuilder.BuildWithMemory(memories) }
        };
        messages.AddRange(session.Messages);
        // User message is already in session.Messages
        return messages;
    }

    private async Task<string> ExecuteToolAsync(ToolCallInfo toolCall)
    {
        try
        {
            var args = ParseArguments(toolCall.Arguments);
            string result;
            if (tools.IsAsync(toolCall.Name))
                result = await tools.ExecuteAsync(toolCall.Name, args).ConfigureAwait(false);
            else
                result = tools.Execute(toolCall.Name, args);
            toolCall.Result = result?.Length > 3000 ? result[..3000] + "...[截断]" : result;
            toolCall.IsError = false;
            Core.Memory.MemoryStore.Instance.Save(session.Id, "tool", toolCall.Name + ": " + (result ?? ""), 0, 0.3);
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
