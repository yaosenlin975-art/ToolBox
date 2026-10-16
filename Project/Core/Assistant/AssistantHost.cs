using System.IO;
using System.Text;
using System.Text.Json;
using ToolBox.Core.BoardAgents;
using ToolBox.Core.Llm;
using ToolBox.Core.Tools;
using ToolBox.Core.Todo;

namespace ToolBox.Core.Assistant;

public static class AssistantHost
{
    private static readonly object sync = new();
    private static readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private static AssistantRuntime? runtime;
    private static ToolRegistry? registry;
    private static ToolsCommandService? tools;
    private static volatile bool enabled;
    private static bool internalDeliveryConfigured;

    public static event Action<bool>? EnabledChanged;

    public static bool IsEnabled => enabled;

    public static string DataDirectory
    {
        get
        {
#if DEBUG
            string? testRoot = Environment.GetEnvironmentVariable("TOOLBOX_TASK_TEST_ROOT");
            if (!string.IsNullOrWhiteSpace(testRoot)) return Path.Combine(Path.GetFullPath(testRoot), "assistant");
#endif
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ToolBox");
        }
    }

    public static string SessionsDirectory => Path.Combine(DataDirectory, "sessions");

    public static AssistantRuntime Runtime => GetRuntime();

    public static ToolsCommandService Tools
    {
        get
        {
            lock (sync)
                return tools ??= new ToolsCommandService(() => IsEnabled, GetRegistry, DataDirectory);
        }
    }

    public static void Initialize(bool value) => SetEnabledAsync(value).GetAwaiter().GetResult();

    public static async Task SetEnabledAsync(bool value)
    {
        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            bool previous = enabled;
            if (previous == value) return;

            if (value)
            {
                AssistantRuntime current = GetRuntime();
                await current.SetEnabledAsync(true).ConfigureAwait(false);
                enabled = true;
                ConfigureInternalDelivery();
            }
            else
            {
                enabled = false;
                InternalBoardAgent.Instance.CancelAll();
                AssistantRuntime? current;
                ToolsCommandService? commandService;
                lock (sync)
                {
                    current = runtime;
                    commandService = tools;
                }

                try
                {
                    Task cancelTools = commandService?.CancelAllAsync() ?? Task.CompletedTask;
                    Task disableRuntime = current?.SetEnabledAsync(false) ?? Task.CompletedTask;
                    await Task.WhenAll(cancelTools, disableRuntime).ConfigureAwait(false);
                }
                catch
                {
                    enabled = previous;
                    throw;
                }
            }

            NotifyEnabledChanged(value);
        }
        finally { lifecycleGate.Release(); }
    }

    private static AssistantRuntime GetRuntime()
    {
        lock (sync)
        {
            if (runtime != null) return runtime;
            var current = AssistantRuntime.Instance;
            ToolRegistry currentRegistry = GetRegistryLocked();
            current.ConfigureTools(currentRegistry.GetAllTools(),
                (name, arguments, cancellationToken) => currentRegistry.ExecuteAsync(name, arguments, cancellationToken));
            return runtime = current;
        }
    }

    private static ToolRegistry GetRegistry()
    {
        lock (sync) return GetRegistryLocked();
    }

    private static ToolRegistry GetRegistryLocked() => registry ??= new ToolRegistry()
        .Register(typeof(FileTools))
        .Register(typeof(ImageTools))
        .Register(typeof(TodoTools))
        .Register(typeof(WebSearchTools))
        .Register(typeof(BoardTools));

    private static void ConfigureInternalDelivery()
    {
        if (internalDeliveryConfigured) return;
        internalDeliveryConfigured = true;
        InternalBoardAgent.Instance.Configure(
            (request, token) => BoardCommandService.Instance.ExecuteAsync(request, token),
            RunInternalTurnAsync,
            sessionId => GetRuntime().GetSessionActorId(sessionId));
        BoardAgentService.Instance.ConfigureInternal(() => IsEnabled, InternalBoardAgent.Instance.ExecuteAsync);
    }

    private static async Task RunInternalTurnAsync(InternalBoardTurn turn, CancellationToken cancellationToken)
    {
        AssistantRuntime current = GetRuntime();
        long generation = current.Generation;
        if (!current.IsCurrentSession(turn.SessionId, turn.ActorId, generation))
            throw new InvalidOperationException("内部会话已失效或被清空，已中止本轮次。");

        JsonElement payload = JsonSerializer.SerializeToElement(new { text = BuildInternalPrompt(turn) });
        object started = await current.HandleAsync("message.send", turn.SessionId, payload, cancellationToken).ConfigureAwait(false);
        await WaitForRunAsync(current, turn.SessionId, ReadStartedRunId(started), cancellationToken).ConfigureAwait(false);
    }

    private static string ReadStartedRunId(object started)
    {
        JsonElement element = JsonSerializer.SerializeToElement(started);
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("runId", out JsonElement runId) &&
            runId.GetString() is { Length: > 0 } value)
            return value;
        throw new InvalidOperationException("内部轮次未返回运行标识。");
    }

    private static async Task WaitForRunAsync(AssistantRuntime runtime, string sessionId, string runId, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (runtime.GetSnapshot() is AssistantSnapshot snapshot)
            {
                AssistantRunSnapshot? run = snapshot.Runs.FirstOrDefault(candidate => candidate.Id == runId && candidate.SessionId == sessionId);
                if (run?.Status is "completed") return;
                if (run?.Status is "failed" or "canceled") throw new InvalidOperationException($"内部轮次未完成：{run.Status}。");
            }
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string BuildInternalPrompt(InternalBoardTurn turn)
    {
        var builder = new StringBuilder();
        builder.AppendLine("你在处理一条已认领的项目看板评论回复。");
        builder.AppendLine($"项目根：{turn.ProjectRoot}");
        builder.AppendLine($"卡片：{turn.CardId}");
        builder.AppendLine($"来源评论：{turn.CommentId}");
        builder.AppendLine("请先用 board_list_cards / board_list_comments 读取事实，再用 board_reply_comment 回复该评论；不要改动其他卡片。");
        builder.AppendLine("评论内容：");
        builder.Append(turn.Body);
        return builder.ToString();
    }

    private static void NotifyEnabledChanged(bool value)
    {
        if (EnabledChanged is not { } handlers) return;
        foreach (Action<bool> handler in handlers.GetInvocationList())
        {
            try { handler(value); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[AssistantHost] State listener failed: {ex.Message}"); }
        }
    }
}
