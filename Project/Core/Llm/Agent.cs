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

                // 每轮最多 3 次重试。流式直出：chunk 到达即 yield，UI 可实时逐字渲染；
                // 仅当一条 chunk 都未发出时（连接级失败）才可安全重试，中途失败无法回滚已输出内容。
                for (int retry = 0; retry < 3 && !llmSuccess; retry++)
                {
                    lastChunk = null;
                    currentText = null;
                    string? streamError = null;

                    // 手动推进枚举器：yield return 不能位于带 catch 的 try 块内（CS1626），
                    // 因此 try/catch 只包裹 MoveNextAsync，yield 放在循环体外层。
                    var enumerator = provider.ChatAsync(messages, tools.GetAllTools(), cts.Token).GetAsyncEnumerator(cts.Token);
                    try
                    {
                        while (true)
                        {
                            bool moved;
                            try
                            {
                                moved = await enumerator.MoveNextAsync();
                            }
                            catch (Exception ex) when (!cts.Token.IsCancellationRequested)
                            {
                                streamError = ex.Message;
                                break;
                            }
                            if (!moved) break;

                            lastChunk = enumerator.Current;
                            if (lastChunk.Text != null)
                            {
                                currentText ??= new StringBuilder();
                                currentText.Append(lastChunk.Text);
                            }
                            yield return lastChunk;
                        }
                    }
                    finally
                    {
                        await enumerator.DisposeAsync();
                    }

                    if (streamError == null)
                    {
                        llmSuccess = true;
                        break;
                    }

                    if (lastChunk == null && retry < 2)
                    {
                        // 一条 chunk 都未发出（如连接失败），可安全重试
                        yield return new ChatChunk { Error = $"请求失败，正在重试 ({retry + 2}/3): {streamError}" };
                        await Task.Delay(1000, cts.Token);
                        continue;
                    }

                    // 已输出部分内容或重试耗尽，无法回滚，直接上报错误并终止本轮
                    yield return new ChatChunk
                    {
                        Error = lastChunk == null
                            ? $"请求失败，已达最大重试次数（3次）: {streamError}"
                            : $"请求中断: {streamError}"
                    };
                    break;
                }

                // 失败轮次：错误已通过 chunk 上报，不落盘、不执行工具
                if (!llmSuccess) break;

                // 无工具调用 → 完成：最终文本写入本轮持久化集合。
                // 文本已随流式逐字输出到 UI，此处不再重复 yield（避免 UI 重复追加）。
                if (lastChunk?.ToolCall == null)
                {
                    if (currentText != null && currentText.Length > 0)
                    {
                        turnMessages.Add(new ChatMessage { Role = "assistant", Content = currentText.ToString() });
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
            Core.Memory.MemoryStore.Instance.Save(session.Id, "tool", toolCall.Name + ": " + (toolCall.Result ?? ""), 0, 0.3);
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
