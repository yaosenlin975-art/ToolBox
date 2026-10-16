using System.Text;
using System.Text.Json;
using ToolBox.Core.Memory;
using ToolBox.Core.Providers;
using ToolBox.Core.Tools;

namespace ToolBox.Core.Llm;

public sealed class Agent
{
    private const int MaxToolRounds = 10;
    private const int MaxToolArgumentsLength = 32_768;
    private const int MaxToolResultLength = 3_000;
    private readonly ILlmProvider provider;
    private readonly IReadOnlyList<ToolInfo> tools;
    private readonly Func<string, Dictionary<string, object>, CancellationToken, Task<string>> executeToolAsync;
    private readonly ISystemPromptBuilder promptBuilder;
    private readonly MemoryStore? memoryStore;
    private readonly ContextCompressor contextCompressor = new();

    public Agent(
        ILlmProvider provider,
        IReadOnlyList<ToolInfo> tools,
        Func<string, Dictionary<string, object>, CancellationToken, Task<string>> executeToolAsync,
        ISystemPromptBuilder? promptBuilder = null,
        MemoryStore? memoryStore = null)
    {
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
        this.tools = tools?.ToArray() ?? throw new ArgumentNullException(nameof(tools));
        this.executeToolAsync = executeToolAsync ?? throw new ArgumentNullException(nameof(executeToolAsync));
        this.promptBuilder = promptBuilder ?? new DefaultSystemPromptBuilder();
        this.memoryStore = memoryStore;
    }

    public async Task RunAsync(
        ChatSession session,
        string runId,
        long generation,
        Func<ChatMessage, CancellationToken, Task> saveMessageAsync,
        Func<bool> isCurrent,
        Action<AssistantRuntimeEvent> changed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(saveMessageAsync);
        ArgumentNullException.ThrowIfNull(isCurrent);
        ArgumentNullException.ThrowIfNull(changed);
        using var executionSessionScope = AssistantRuntime.EnterExecutionSessionScope(session.Id);
        var memory = memoryStore ?? MemoryStore.Instance;
        EnsureCurrent(isCurrent, cancellationToken);
        var memories = await memory.GetRelevantAsync(session.Id, cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureCurrent(isCurrent, cancellationToken);

        var messages = new List<ChatMessage>
        {
            new() { Role = "system", Content = promptBuilder.BuildWithMemory(memories) }
        };
        messages.AddRange(session.Messages.Select(CloneMessage));
        long sequence = 0;

        for (int round = 0; round < MaxToolRounds; round++)
        {
            EnsureCurrent(isCurrent, cancellationToken);
            await contextCompressor.CheckAndCompressAsync(messages, cancellationToken).ConfigureAwait(false);
            var assistantMessage = new ChatMessage { Role = "assistant", RunId = runId };
            var response = await StreamResponseAsync(
                messages, assistantMessage.Id, session.Id, runId, generation,
                () => ++sequence, isCurrent, changed, cancellationToken).ConfigureAwait(false);
            EnsureCurrent(isCurrent, cancellationToken);
            assistantMessage.Content = response.Text;

            if (response.Error != null)
            {
                if (response.Text.Length > 0)
                    await SaveMessageAsync(assistantMessage, messages, session, saveMessageAsync, isCurrent, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException(response.Error);
            }

            if (response.ToolCalls.Count == 0)
            {
                if (response.Text.Length > 0)
                    await SaveMessageAsync(assistantMessage, messages, session, saveMessageAsync, isCurrent, cancellationToken).ConfigureAwait(false);
                return;
            }

            var seenCallIds = new HashSet<string>(StringComparer.Ordinal);
            var oversizedCalls = new HashSet<string>(StringComparer.Ordinal);
            foreach (var call in response.ToolCalls)
            {
                if (string.IsNullOrWhiteSpace(call.Id) || !seenCallIds.Add(call.Id))
                {
                    call.Id = Guid.NewGuid().ToString("N");
                    seenCallIds.Add(call.Id);
                }
                if (call.Arguments.Length > MaxToolArgumentsLength)
                {
                    oversizedCalls.Add(call.Id);
                    call.Arguments = call.Arguments[..MaxToolArgumentsLength];
                }
            }
            assistantMessage.ToolCalls = response.ToolCalls.ToList();
            await SaveMessageAsync(assistantMessage, messages, session, saveMessageAsync, isCurrent, cancellationToken).ConfigureAwait(false);
            foreach (var call in response.ToolCalls)
            {
                string toolResult = await ExecuteToolAsync(
                    call, oversizedCalls.Contains(call.Id), session.Id, runId, generation, isCurrent,
                    changed, memory, cancellationToken).ConfigureAwait(false);
                await SaveMessageAsync(assistantMessage, messages, session, saveMessageAsync, isCurrent, cancellationToken).ConfigureAwait(false);
                var toolMessage = new ChatMessage
                {
                    Role = "tool",
                    Content = toolResult,
                    ToolCallId = call.Id,
                    RunId = runId
                };
                await SaveMessageAsync(toolMessage, messages, session, saveMessageAsync, isCurrent, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException($"Agent 已达到工具调用轮次上限（{MaxToolRounds}）。");
    }

    private async Task<string> ExecuteToolAsync(
        ToolCallInfo call,
        bool oversizedArguments,
        string sessionId,
        string runId,
        long generation,
        Func<bool> isCurrent,
        Action<AssistantRuntimeEvent> changed,
        MemoryStore memory,
        CancellationToken cancellationToken)
    {
        string arguments = AssistantRuntime.RedactSensitiveText(Limit(call.Arguments, 4_000));
        PublishTool("running", null, null);
        try
        {
            EnsureCurrent(isCurrent, cancellationToken);
            if (oversizedArguments) throw new InvalidOperationException("工具参数超过 32 KiB 限制。");
            if (!tools.Any(tool => tool.Name == call.Name)) throw new ToolNotFoundException(call.Name);
            var parsed = ParseArguments(call.Arguments);
            string result = await executeToolAsync(call.Name, parsed, cancellationToken).ConfigureAwait(false);
            EnsureCurrent(isCurrent, cancellationToken);
            result ??= string.Empty;
            call.Result = Limit(result, MaxToolResultLength);
            call.IsError = false;
            PublishTool("completed", call.Result, null);

            if (tools.Any(tool => tool.Name == call.Name) && !cancellationToken.IsCancellationRequested && isCurrent())
            {
                try
                {
                    await memory.SaveAsync(sessionId, "tool", $"工具「{call.Name}」已成功完成。", 8, 0.3, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    changed(new AssistantRuntimeEvent("error", sessionId, runId,
                        new AssistantErrorPayload("MEMORY_SAVE_FAILED", ex.Message, generation)));
                }
            }
            return call.Result;
        }
        catch (OperationCanceledException)
        {
            call.IsError = true;
            call.Result = "工具执行已取消。";
            PublishTool("failed", null, call.Result);
            throw;
        }
        catch (Exception ex)
        {
            call.IsError = true;
            call.Result = Limit(ex.Message, MaxToolResultLength);
            PublishTool("failed", null, call.Result);
            return "[工具执行失败: " + call.Result + "]";
        }

        void PublishTool(string status, string? result, string? error)
        {
            if (status == "completed" && cancellationToken.IsCancellationRequested) return;
            if (!isCurrent()) return;
            changed(new AssistantRuntimeEvent("tool.status", sessionId, runId,
                new AssistantToolEventPayload(call.Id, call.Name, arguments, status,
                    result == null ? null : AssistantRuntime.RedactSensitiveText(result),
                    error == null ? null : AssistantRuntime.RedactSensitiveText(error), generation)));
        }
    }

    private async Task<ChatResponse> StreamResponseAsync(
        IReadOnlyList<ChatMessage> messages,
        string messageId,
        string sessionId,
        string runId,
        long generation,
        Func<long> nextSequence,
        Func<bool> isCurrent,
        Action<AssistantRuntimeEvent> changed,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var text = new StringBuilder();
            var toolCalls = new List<ToolCallInfo>();
            string? error = null;
            bool sawChunk = false;
            try
            {
                await foreach (var chunk in provider.ChatAsync(messages, tools, cancellationToken)
                    .WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    EnsureCurrent(isCurrent, cancellationToken);
                    sawChunk = true;
                    if (chunk.Text != null)
                    {
                        text.Append(chunk.Text);
                        changed(new AssistantRuntimeEvent("text.delta", sessionId, runId,
                            new AssistantTextDeltaPayload(messageId, chunk.Text, nextSequence(), generation)));
                    }
                    if (chunk.ToolCall != null) toolCalls.Add(chunk.ToolCall);
                    if (chunk.ToolCalls != null) toolCalls.AddRange(chunk.ToolCalls);
                    if (!string.IsNullOrWhiteSpace(chunk.Error))
                    {
                        error = chunk.Error;
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { error = ex.Message; }

            if (error == null) return new ChatResponse(text.ToString(), toolCalls, null);
            if (text.Length == 0 && toolCalls.Count == 0 && !sawChunk && attempt < 2)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                continue;
            }
            return new ChatResponse(text.ToString(), toolCalls, error);
        }
        return new ChatResponse(string.Empty, [], "请求失败。");
    }

    private static async Task SaveMessageAsync(
        ChatMessage message,
        List<ChatMessage> messages,
        ChatSession session,
        Func<ChatMessage, CancellationToken, Task> saveMessageAsync,
        Func<bool> isCurrent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!isCurrent()) throw new OperationCanceledException("运行已失效。", cancellationToken);
        await saveMessageAsync(message, cancellationToken).ConfigureAwait(false);
        int historyIndex = messages.FindIndex(item => item.Id == message.Id);
        if (historyIndex >= 0) messages[historyIndex] = CloneMessage(message);
        else messages.Add(CloneMessage(message));
        int sessionIndex = session.Messages.FindIndex(item => item.Id == message.Id);
        if (sessionIndex >= 0) session.Messages[sessionIndex] = CloneMessage(message);
        else session.Messages.Add(CloneMessage(message));
    }

    private static Dictionary<string, object> ParseArguments(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("工具参数必须是 JSON 对象。");
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
            result[property.Name] = ConvertJson(property.Value)!;
        return result;
    }

    private static object? ConvertJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number when element.TryGetInt64(out long value) => value,
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Array => element.EnumerateArray().Select(ConvertJson).ToList(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ConvertJson(p.Value)!, StringComparer.Ordinal),
        _ => throw new JsonException("工具参数包含不支持的 JSON 值。")
    };

    private static ChatMessage CloneMessage(ChatMessage message) => new()
    {
        Id = message.Id,
        Role = message.Role,
        RunId = message.RunId,
        Content = message.Content,
        ImagePath = message.ImagePath,
        ToolCallId = message.ToolCallId,
        Timestamp = message.Timestamp,
        ToolCalls = message.ToolCalls?.Select(call => new ToolCallInfo
        {
            Id = call.Id,
            Name = call.Name,
            Arguments = call.Arguments,
            Result = call.Result,
            IsError = call.IsError
        }).ToList()
    };

    private static string Limit(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...[截断]";

    private static void EnsureCurrent(Func<bool> isCurrent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!isCurrent()) throw new OperationCanceledException("运行已失效。", cancellationToken);
    }

    private sealed record ChatResponse(string Text, IReadOnlyList<ToolCallInfo> ToolCalls, string? Error);
}
