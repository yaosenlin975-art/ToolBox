using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ToolBox.Core.Memory;
using ToolBox.Core.Providers;
using ToolBox.Core.Tools;

namespace ToolBox.Core.Llm;

public sealed record AssistantRuntimeEvent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("sessionId")] string? SessionId,
    [property: JsonPropertyName("runId")] string? RunId,
    [property: JsonPropertyName("payload")] object Payload);

public sealed record AssistantTextDeltaPayload(
    [property: JsonPropertyName("messageId")] string MessageId,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("generation")] long Generation);

public sealed record AssistantToolEventPayload(
    [property: JsonPropertyName("toolId")] string ToolId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("arguments")] string Arguments,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("result")] string? Result,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("generation")] long Generation);

public sealed record AssistantRunStatusPayload(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("generation")] long Generation,
    [property: JsonPropertyName("error")] string? Error = null);

public sealed record AssistantErrorPayload(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("generation")] long Generation);

public sealed class AssistantRuntime
{
    private const int MaxMessageLength = 100_000;
    private const int MaxTitleLength = 120;
    private const int MaxRetainedRuns = 100;
    private static readonly Regex SessionIdPattern = new("^[a-fA-F0-9]{8}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SecretValuePattern = new(
        "(?i)(?<prefix>\\b(?:api[_-]?key|access[_-]?token|refresh[_-]?token|token|password|passwd|secret|authorization|credential)\\b\\s*[\\\"']?\\s*[:=]\\s*[\\\"']?)(?<value>[^,;\\s\\\"'}]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Lazy<AssistantRuntime> Singleton = new(() => new AssistantRuntime());
    private static readonly AsyncLocal<string?> executionSessionId = new();

    private readonly object stateLock = new();
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim persistenceGate = new(1, 1);
    private readonly string basePath;
    private readonly string sessionsPath;
    private readonly string chatsIndexPath;
    private readonly string memoryDatabasePath;
    private readonly Func<ILlmProvider?> providerFactory;
    private readonly Func<MemoryStore> memoryFactory;
    private readonly bool memoryStoreWasProvided;
    private readonly List<ChatSession> sessions = [];
    private readonly HashSet<string> loadedSessionIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AssistantRunState> runs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AssistantRunState> activeRuns = new(StringComparer.Ordinal);
    private IReadOnlyList<ToolInfo> configuredTools = [];
    private Func<string, Dictionary<string, object>, CancellationToken, Task<string>>? configuredToolExecutor;
    private string? activeSessionId;
    private bool enabled;
    private bool clearing;
    private long generation;

    public static AssistantRuntime Instance => Singleton.Value;

    public static string? CurrentExecutionSessionId => executionSessionId.Value;

    internal static IDisposable EnterExecutionSessionScope(string sessionId)
    {
        var previous = executionSessionId.Value;
        executionSessionId.Value = sessionId;
        return new ExecutionSessionScope(previous);
    }

    public event Action<AssistantRuntimeEvent>? Changed;

    public bool Enabled
    {
        get { lock (stateLock) return enabled; }
    }

    public long Generation
    {
        get { lock (stateLock) return generation; }
    }

    public bool IsCurrentSession(string sessionId, Guid actorId, long expectedGeneration)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || actorId == Guid.Empty) return false;
        lock (stateLock)
            return enabled && !clearing && generation == expectedGeneration &&
                sessions.Any(session => session.Id == sessionId && session.ActorId == actorId);
    }

    public Guid GetSessionActorId(string sessionId)
    {
        lock (stateLock)
        {
            Guid actorId = FindSessionLocked(RequireSessionId(sessionId)).ActorId;
            if (actorId == Guid.Empty) throw new InvalidOperationException("会话缺少有效的 ActorId。");
            return actorId;
        }
    }

    public AssistantRuntime(
        string? appDataDirectory = null,
        Func<ILlmProvider?>? providerFactory = null,
        MemoryStore? memoryStore = null)
    {
        basePath = Path.GetFullPath(appDataDirectory ?? GetDefaultAppDataDirectory());
        sessionsPath = Path.Combine(basePath, "sessions");
        chatsIndexPath = Path.Combine(basePath, "chats.json");
        memoryDatabasePath = Path.Combine(basePath, "Data", "memory.db");
        this.providerFactory = providerFactory ?? (() => ProviderManager.Instance.CreateActiveProvider());
        memoryStoreWasProvided = memoryStore != null;
        if (memoryStore != null) memoryFactory = () => memoryStore;
        else if (!string.IsNullOrWhiteSpace(appDataDirectory))
        {
            var localMemory = new Lazy<MemoryStore>(() => new MemoryStore(memoryDatabasePath));
            memoryFactory = () => localMemory.Value;
        }
        else memoryFactory = () => MemoryStore.Instance;
        Directory.CreateDirectory(sessionsPath);
        LoadIndex();
    }

    public object GetSnapshot()
    {
        lock (stateLock) return CreateSnapshotLocked();
    }

    public void ConfigureTools(
        IReadOnlyList<ToolInfo> tools,
        Func<string, Dictionary<string, object>, CancellationToken, Task<string>> executor)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(executor);
        if (tools.Any(tool => tool == null || string.IsNullOrWhiteSpace(tool.Name)) ||
            tools.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count() != tools.Count)
            throw new ArgumentException("工具名称必须有效且唯一。", nameof(tools));

        lock (stateLock)
        {
            configuredTools = tools.ToArray();
            configuredToolExecutor = executor;
        }
    }

    public async Task SetEnabledAsync(bool value)
    {
        await lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            List<AssistantRunState> pending;
            bool previous;
            lock (stateLock)
            {
                if (clearing) throw new InvalidOperationException("正在清空助手数据。");
                previous = enabled;
                if (previous == value) return;
                enabled = value;
                pending = value ? [] : activeRuns.Values.ToList();
                if (!value)
                    foreach (var run in pending) run.Cancellation.Cancel();
            }

            try { await SaveIndexAsync(CancellationToken.None).ConfigureAwait(false); }
            catch
            {
                lock (stateLock) enabled = previous;
                throw;
            }

            if (!value)
                await Task.WhenAll(pending.Select(run => run.Task)).ConfigureAwait(false);
            PublishSnapshot();
        }
        finally { lifecycleGate.Release(); }
    }

    public async Task<object> HandleAsync(
        string type,
        string? sessionId,
        JsonElement payload,
        CancellationToken ct = default)
    {
        return type switch
        {
            "session.list" => GetListSnapshot(payload),
            "session.create" => await CreateSessionAsync(payload, ct).ConfigureAwait(false),
            "session.select" => await SelectSessionAsync(RequireSessionId(sessionId), payload, ct).ConfigureAwait(false),
            "session.rename" => await RenameSessionAsync(RequireSessionId(sessionId), payload, ct).ConfigureAwait(false),
            "session.delete" => await DeleteSessionAsync(RequireSessionId(sessionId), payload, ct).ConfigureAwait(false),
            "message.send" => await SendMessageAsync(sessionId, payload, ct).ConfigureAwait(false),
            "run.cancel" => await CancelRunAsync(RequireSessionId(sessionId), payload, ct).ConfigureAwait(false),
            "sessions.clear" => await ClearSessionsAsync(payload, ct).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "未知的 AssistantRuntime 命令。")
        };
    }

    private object GetListSnapshot(JsonElement payload)
    {
        RequireEmptyObject(payload, "session.list");
        return GetSnapshot();
    }

    private async Task<object> CreateSessionAsync(JsonElement payload, CancellationToken ct)
    {
        RequireEmptyObject(payload, "session.create");
        await lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var session = new ChatSession();
            string? previousActive;
            lock (stateLock)
            {
                EnsureNotClearingLocked();
                previousActive = activeSessionId;
                sessions.Insert(0, session);
                activeSessionId = session.Id;
                loadedSessionIds.Add(session.Id);
            }
            try { await SaveIndexAsync(ct).ConfigureAwait(false); }
            catch
            {
                lock (stateLock)
                {
                    sessions.Remove(session);
                    loadedSessionIds.Remove(session.Id);
                    activeSessionId = previousActive;
                }
                throw;
            }
            PublishSnapshot(session.Id);
            return new AssistantSessionSnapshot(session.Id, session.Title);
        }
        finally { lifecycleGate.Release(); }
    }

    private async Task<object> SelectSessionAsync(string sessionId, JsonElement payload, CancellationToken ct)
    {
        RequireEmptyObject(payload, "session.select");
        await lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ChatSession session;
            lock (stateLock) session = FindSessionLocked(sessionId);
            await EnsureMessagesLoadedAsync(session, ct).ConfigureAwait(false);
            lock (stateLock)
            {
                EnsureNotClearingLocked();
                activeSessionId = sessionId;
            }
            PublishSnapshot(sessionId);
            return GetSnapshot();
        }
        finally { lifecycleGate.Release(); }
    }

    private async Task<object> RenameSessionAsync(string sessionId, JsonElement payload, CancellationToken ct)
    {
        string title = ReadRequiredString(payload, "session.rename", "title").Trim();
        if (title.Length == 0 || title.Length > MaxTitleLength)
            throw new ArgumentException($"会话标题长度必须为 1 到 {MaxTitleLength} 个字符。");

        await lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ChatSession session;
            string oldTitle;
            bool oldTitleLocked;
            lock (stateLock)
            {
                EnsureNotClearingLocked();
                session = FindSessionLocked(sessionId);
                oldTitle = session.Title;
                oldTitleLocked = session.IsTitleLocked;
                session.Title = title;
                session.IsTitleLocked = true;
            }
            try { await SaveIndexAsync(ct).ConfigureAwait(false); }
            catch
            {
                lock (stateLock)
                {
                    session.Title = oldTitle;
                    session.IsTitleLocked = oldTitleLocked;
                }
                throw;
            }
            PublishSnapshot(sessionId);
            return new AssistantSessionSnapshot(session.Id, session.Title);
        }
        finally { lifecycleGate.Release(); }
    }

    private async Task<object> DeleteSessionAsync(string sessionId, JsonElement payload, CancellationToken ct)
    {
        RequireEmptyObject(payload, "session.delete");
        await lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            AssistantRunState? run;
            lock (stateLock)
            {
                EnsureNotClearingLocked();
                FindSessionLocked(sessionId);
                activeRuns.TryGetValue(sessionId, out run);
                run?.Cancellation.Cancel();
            }
            if (run != null) await run.Task.ConfigureAwait(false);

            ChatSession session;
            string? oldActive;
            int oldIndex;
            lock (stateLock)
            {
                session = FindSessionLocked(sessionId);
                oldIndex = sessions.IndexOf(session);
                oldActive = activeSessionId;
                sessions.Remove(session);
                loadedSessionIds.Remove(sessionId);
                if (activeSessionId == sessionId)
                    activeSessionId = sessions.FirstOrDefault()?.Id;
            }
            try { await SaveIndexAsync(ct).ConfigureAwait(false); }
            catch
            {
                lock (stateLock)
                {
                    sessions.Insert(Math.Clamp(oldIndex, 0, sessions.Count), session);
                    loadedSessionIds.Add(sessionId);
                    activeSessionId = oldActive;
                }
                throw;
            }
            lock (stateLock)
                foreach (var oldRun in runs.Values.Where(item => item.SessionId == sessionId).ToArray())
                    runs.Remove(oldRun.Id);

            await DeleteSessionFilesAsync(sessionId, ct).ConfigureAwait(false);
            if (memoryStoreWasProvided || File.Exists(memoryDatabasePath))
                await memoryFactory().DeleteSessionAsync(sessionId, ct).ConfigureAwait(false);
            string? selectedSession;
            lock (stateLock) selectedSession = activeSessionId;
            if (selectedSession != null)
            {
                ChatSession nextSession;
                lock (stateLock) nextSession = FindSessionLocked(selectedSession);
                await EnsureMessagesLoadedAsync(nextSession, ct).ConfigureAwait(false);
            }
            PublishSnapshot();
            return GetSnapshot();
        }
        finally { lifecycleGate.Release(); }
    }

    private async Task<object> SendMessageAsync(string? sessionId, JsonElement payload, CancellationToken ct)
    {
        string content = ReadRequiredString(payload, "message.send", "text");
        if (string.IsNullOrWhiteSpace(content)) throw new ArgumentException("消息不能为空。");
        if (content.Length > MaxMessageLength) throw new ArgumentException($"消息不能超过 {MaxMessageLength} 个字符。");

        await lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ChatSession session;
            AssistantRunState run;
            IReadOnlyList<ToolInfo> tools;
            Func<string, Dictionary<string, object>, CancellationToken, Task<string>> executor;
            long currentGeneration;
            lock (stateLock)
            {
                if (clearing) throw new InvalidOperationException("正在清空助手数据。");
                if (!enabled) throw new InvalidOperationException("Assistant 当前已关闭。");
                string resolvedSessionId = sessionId ?? activeSessionId ?? throw new InvalidOperationException("请先创建或选择会话。");
                session = FindSessionLocked(resolvedSessionId);
                if (activeRuns.ContainsKey(resolvedSessionId)) throw new InvalidOperationException("该会话正在执行，请等待或停止当前轮次。");

                if (session.Title == "新会话" && session.Messages.Count == 0)
                {
                    session.Title = content.Length > 25 ? content[..25] + "…" : content;
                    session.IsTitleLocked = false;
                }
                currentGeneration = generation;
                run = new AssistantRunState(session.Id, currentGeneration);
                var userMessage = new ChatMessage { Role = "user", Content = content, RunId = run.Id };
                session.Messages.Add(userMessage);
                session.UpdatedAt = DateTime.UtcNow;
                SortSessionsLocked();
                runs[run.Id] = run;
                activeRuns[session.Id] = run;
                session.Status = "running";
                tools = configuredTools;
                executor = configuredToolExecutor ?? ((name, _, _) => Task.FromException<string>(new InvalidOperationException($"工具尚未配置：{name}")));
            }

            PublishSnapshot(session.Id, run.Id);
            Publish(new AssistantRuntimeEvent("run.status", session.Id, run.Id,
                new AssistantRunStatusPayload("running", currentGeneration)));
            run.Task = Task.Run(() => ExecuteRunAsync(run, session, tools, executor));
            return new { runId = run.Id, status = "running" };
        }
        finally { lifecycleGate.Release(); }
    }

    private async Task<object> CancelRunAsync(string sessionId, JsonElement payload, CancellationToken ct)
    {
        await lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            AssistantRunState? run;
            lock (stateLock)
            {
                string? requestedRunId = ReadOptionalString(payload, "run.cancel", "runId");
                run = activeRuns.TryGetValue(sessionId, out var active) &&
                      (requestedRunId == null || requestedRunId == active.Id) ? active : null;
                run?.Cancellation.Cancel();
            }
            if (run != null) await run.Task.ConfigureAwait(false);
            return new { canceled = run != null, runId = run?.Id };
        }
        finally { lifecycleGate.Release(); }
    }

    private async Task<object> ClearSessionsAsync(JsonElement payload, CancellationToken ct)
    {
        RequireEmptyObject(payload, "sessions.clear");
        await lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<AssistantRunState> pending;
            lock (stateLock)
            {
                if (clearing) throw new InvalidOperationException("已在清空助手数据。");
                clearing = true;
                generation++;
                pending = activeRuns.Values.ToList();
                foreach (var run in pending) run.Cancellation.Cancel();
            }

            try
            {
                await Task.WhenAll(pending.Select(run => run.Task)).ConfigureAwait(false);
                await persistenceGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    List<ChatSession> previousSessions;
                    HashSet<string> previousLoaded;
                    Dictionary<string, AssistantRunState> previousRuns;
                    string? previousActiveSession;
                    lock (stateLock)
                    {
                        previousSessions = sessions.ToList();
                        previousLoaded = new HashSet<string>(loadedSessionIds, StringComparer.Ordinal);
                        previousRuns = new Dictionary<string, AssistantRunState>(runs, StringComparer.Ordinal);
                        previousActiveSession = activeSessionId;
                        sessions.Clear();
                        loadedSessionIds.Clear();
                        activeSessionId = null;
                        runs.Clear();
                        activeRuns.Clear();
                    }
                    try { await WriteIndexLockedByPersistenceGateAsync(ct).ConfigureAwait(false); }
                    catch
                    {
                        lock (stateLock)
                        {
                            sessions.AddRange(previousSessions);
                            loadedSessionIds.UnionWith(previousLoaded);
                            activeSessionId = previousActiveSession;
                            foreach (var entry in previousRuns) runs[entry.Key] = entry.Value;
                        }
                        throw;
                    }

                    if (Directory.Exists(sessionsPath))
                    {
                        foreach (string file in Directory.EnumerateFiles(sessionsPath, "*", SearchOption.TopDirectoryOnly))
                            File.Delete(file);
                        foreach (string directory in Directory.EnumerateDirectories(sessionsPath))
                            Directory.Delete(directory, recursive: true);
                    }
                    if (memoryStoreWasProvided || File.Exists(memoryDatabasePath))
                        await memoryFactory().ClearAsync(ct).ConfigureAwait(false);
                }
                finally { persistenceGate.Release(); }
            }
            finally
            {
                lock (stateLock) clearing = false;
            }

            PublishSnapshot();
            return GetSnapshot();
        }
        finally { lifecycleGate.Release(); }
    }

    private async Task ExecuteRunAsync(
        AssistantRunState run,
        ChatSession session,
        IReadOnlyList<ToolInfo> tools,
        Func<string, Dictionary<string, object>, CancellationToken, Task<string>> executor)
    {
        try
        {
            await SaveSessionMessagesAsync(session, run.Generation, run.Cancellation.Token).ConfigureAwait(false);
            await SaveIndexAsync(run.Cancellation.Token).ConfigureAwait(false);
            if (!IsCurrentRun(run)) throw new OperationCanceledException(run.Cancellation.Token);
            var provider = providerFactory();
            if (provider == null) throw new InvalidOperationException("未配置可用的模型供应商。");

            ChatSession detachedSession;
            lock (stateLock)
            {
                if (!IsCurrentRunLocked(run)) throw new OperationCanceledException(run.Cancellation.Token);
                detachedSession = new ChatSession
                {
                    Id = session.Id,
                    ActorId = session.ActorId,
                    Title = session.Title,
                    Messages = session.Messages.Select(CloneMessage).ToList()
                };
            }
            var agent = new Agent(provider, tools, executor, memoryStore: memoryFactory());
            await agent.RunAsync(
                detachedSession,
                run.Id,
                run.Generation,
                (message, token) => SaveRunMessageAsync(run, session, message, token),
                () => IsCurrentRun(run),
                agentEvent => HandleAgentEvent(run, agentEvent),
                run.Cancellation.Token).ConfigureAwait(false);
            await FinishRunAsync(run, "completed", null).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested || !IsCurrentRun(run))
        {
            await FinishRunAsync(run, "canceled", null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Publish(new AssistantRuntimeEvent("error", run.SessionId, run.Id,
            new AssistantErrorPayload("RUN_FAILED", LimitSnapshot(RedactSensitiveText(ex.Message), 1_000), run.Generation)));
            await FinishRunAsync(run, "failed", LimitSnapshot(RedactSensitiveText(ex.Message), 1_000)).ConfigureAwait(false);
        }
    }

    private async Task SaveRunMessageAsync(
        AssistantRunState run,
        ChatSession session,
        ChatMessage message,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (stateLock)
        {
            if (!IsCurrentRunLocked(run)) throw new OperationCanceledException("运行已失效。", cancellationToken);
            int existing = session.Messages.FindIndex(item => item.Id == message.Id);
            if (existing >= 0) session.Messages[existing] = CloneMessage(message);
            else session.Messages.Add(CloneMessage(message));
            session.UpdatedAt = DateTime.UtcNow;
            SortSessionsLocked();
        }
        await SaveSessionMessagesAsync(session, run.Generation, cancellationToken).ConfigureAwait(false);
    }

    private void HandleAgentEvent(AssistantRunState run, AssistantRuntimeEvent agentEvent)
    {
        if (!IsCurrentRun(run)) return;
        if (agentEvent.Type == "tool.status" && agentEvent.Payload is AssistantToolEventPayload payload)
        {
            lock (stateLock)
            {
                if (!IsCurrentRunLocked(run)) return;
                var step = run.Tools.FirstOrDefault(item => item.Id == payload.ToolId);
                if (step == null)
                {
                    step = new AssistantRunToolState(payload.ToolId, payload.Name, payload.Arguments);
                    run.Tools.Add(step);
                }
                step.Status = payload.Status;
                step.Result = payload.Result;
                step.Error = payload.Error;
            }
        }
        Publish(agentEvent);
    }

    private async Task FinishRunAsync(AssistantRunState run, string status, string? error)
    {
        bool publish;
        lock (stateLock)
        {
            publish = runs.ContainsKey(run.Id);
            if (!publish) return;
            run.Status = status;
            run.Error = error;
            run.CompletedAt = DateTime.UtcNow;
            if (activeRuns.TryGetValue(run.SessionId, out var active) && active.Id == run.Id)
                activeRuns.Remove(run.SessionId);
            var session = sessions.FirstOrDefault(item => item.Id == run.SessionId);
            if (session != null)
            {
                session.Status = "idle";
                session.UpdatedAt = DateTime.UtcNow;
                SortSessionsLocked();
            }
            run.Cancellation.Dispose();
            PruneRunsLocked();
        }
        Publish(new AssistantRuntimeEvent("run.status", run.SessionId, run.Id,
                new AssistantRunStatusPayload(status, run.Generation, error == null ? null : LimitSnapshot(RedactSensitiveText(error), 1_000))));
        PublishSnapshot(run.SessionId, run.Id);
        try { await SaveIndexAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex)
        {
            Publish(new AssistantRuntimeEvent("error", run.SessionId, run.Id,
                new AssistantErrorPayload("STATE_SAVE_FAILED", LimitSnapshot(RedactSensitiveText(ex.Message), 1_000), run.Generation)));
        }
    }

    private async Task EnsureMessagesLoadedAsync(ChatSession session, CancellationToken ct)
    {
        lock (stateLock)
            if (loadedSessionIds.Contains(session.Id)) return;

        List<ChatMessage> messages = [];
        string path = SessionMessagesFile(session.Id);
        if (File.Exists(path))
        {
            try
            {
                string json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
                messages = JsonSerializer.Deserialize<List<ChatMessage>>(json) ?? [];
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"[AssistantRuntime] Could not load session {session.Id}: {ex.Message}");
            }
        }
        lock (stateLock)
        {
            if (sessions.Contains(session)) session.Messages = messages;
            loadedSessionIds.Add(session.Id);
        }
    }

    private async Task SaveSessionMessagesAsync(ChatSession session, long runGeneration, CancellationToken ct)
    {
        await persistenceGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string json;
            lock (stateLock)
            {
                if (!IsCurrentGenerationLocked(runGeneration) || !sessions.Contains(session)) return;
                json = JsonSerializer.Serialize(session.Messages.Select(CloneMessage).ToList(), JsonOptions);
            }
            await WriteAtomicallyAsync(SessionMessagesFile(session.Id), json, ct).ConfigureAwait(false);
        }
        finally { persistenceGate.Release(); }
    }

    private async Task SaveIndexAsync(CancellationToken ct)
    {
        await persistenceGate.WaitAsync(ct).ConfigureAwait(false);
        try { await WriteIndexLockedByPersistenceGateAsync(ct).ConfigureAwait(false); }
        finally { persistenceGate.Release(); }
    }

    private async Task WriteIndexLockedByPersistenceGateAsync(CancellationToken ct)
    {
        ChatsIndex index;
        lock (stateLock)
        {
            index = new ChatsIndex
            {
                Version = 1,
                AssistantEnabled = enabled,
                Chats = sessions.Select(session => new ChatSessionIndex
                {
                    Id = session.Id,
                    ActorId = session.ActorId,
                    Title = session.Title,
                    IsTitleLocked = session.IsTitleLocked,
                    IsPinned = session.IsPinned,
                    Status = session.Status,
                    CreatedAt = session.CreatedAt,
                    UpdatedAt = session.UpdatedAt
                }).ToList()
            };
        }
        await WriteAtomicallyAsync(chatsIndexPath, JsonSerializer.Serialize(index, JsonOptions), ct).ConfigureAwait(false);
    }

    private async Task WriteAtomicallyAsync(string path, string content, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(content);
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private async Task DeleteSessionFilesAsync(string sessionId, CancellationToken ct)
    {
        await persistenceGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string messagesFile = SessionMessagesFile(sessionId);
            if (File.Exists(messagesFile)) File.Delete(messagesFile);
            string legacyDirectory = Path.Combine(sessionsPath, sessionId);
            if (Directory.Exists(legacyDirectory)) Directory.Delete(legacyDirectory, recursive: true);
        }
        finally { persistenceGate.Release(); }
    }

    private void LoadIndex()
    {
        if (!File.Exists(chatsIndexPath)) return;
        bool actorMigrationNeeded = false;
        try
        {
            var index = JsonSerializer.Deserialize<ChatsIndex>(File.ReadAllText(chatsIndexPath));
            lock (stateLock)
            {
                enabled = index?.AssistantEnabled ?? false;
                foreach (var chat in index?.Chats ?? [])
                {
                    if (!SessionIdPattern.IsMatch(chat.Id) || sessions.Any(item => item.Id == chat.Id)) continue;
                    sessions.Add(new ChatSession
                    {
                        Id = chat.Id,
                        ActorId = chat.ActorId == Guid.Empty ? NewActorId() : chat.ActorId,
                        Title = chat.Title,
                        IsTitleLocked = chat.IsTitleLocked,
                        IsPinned = chat.IsPinned,
                        Status = "idle",
                        CreatedAt = chat.CreatedAt,
                        UpdatedAt = chat.UpdatedAt
                    });
                    if (chat.ActorId == Guid.Empty) actorMigrationNeeded = true;
                }
                SortSessionsLocked();
                activeSessionId = sessions.FirstOrDefault()?.Id;
            }
            if (activeSessionId is { } firstId)
                EnsureMessagesLoadedAsync(sessions.First(item => item.Id == firstId), CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"[AssistantRuntime] Could not load chats index: {ex.Message}");
            return;
        }
        if (actorMigrationNeeded) SaveIndexAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    private static Guid NewActorId() => Guid.NewGuid();

    private static string GetDefaultAppDataDirectory()
    {
#if DEBUG
        string? testRoot = Environment.GetEnvironmentVariable("TOOLBOX_TASK_TEST_ROOT");
        if (!string.IsNullOrWhiteSpace(testRoot)) return Path.Combine(Path.GetFullPath(testRoot), "assistant");
#endif
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ToolBox");
    }

    private sealed class ExecutionSessionScope(string? previous) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed) return;
            executionSessionId.Value = previous;
            disposed = true;
        }
    }

    private AssistantSnapshot CreateSnapshotLocked()
    {
        var activeSession = sessions.FirstOrDefault(session => session.Id == activeSessionId);
        return new AssistantSnapshot(
            enabled,
            generation,
            sessions.Select(session => new AssistantSessionSnapshot(session.Id, session.Title)).ToArray(),
            activeSessionId,
            activeSession?.Messages.Select(ToMessageSnapshot).ToArray() ?? [],
            runs.Values.OrderBy(run => run.StartedAt).Select(ToRunSnapshot).ToArray());
    }

    private static AssistantMessageSnapshot ToMessageSnapshot(ChatMessage message) => new(
        message.Id,
        message.Role,
        message.Content,
        message.RunId,
        message.ToolCalls?.Select(call => new AssistantMessageToolCallSnapshot(
            call.Id, call.Name, LimitSnapshot(RedactSensitiveText(call.Arguments), 4_000),
            call.Result == null ? null : LimitSnapshot(RedactSensitiveText(call.Result), 3_000), call.IsError)).ToArray());

    private static AssistantRunSnapshot ToRunSnapshot(AssistantRunState run) => new(
        run.Id,
        run.SessionId,
        run.Status,
        run.Tools.Select(step => new AssistantRunToolSnapshot(
            step.Id, step.Name, LimitSnapshot(RedactSensitiveText(step.Arguments), 4_000), step.Status,
            step.Result == null ? null : LimitSnapshot(RedactSensitiveText(step.Result), 3_000), step.Error)).ToArray(),
        run.Error == null ? null : LimitSnapshot(RedactSensitiveText(run.Error), 1_000),
        run.StartedAt,
        run.CompletedAt);

    private void PublishSnapshot(string? sessionId = null, string? runId = null) =>
        Publish(new AssistantRuntimeEvent("snapshot", sessionId, runId, GetSnapshot()));

    private void Publish(AssistantRuntimeEvent runtimeEvent)
    {
        var handlers = Changed?.GetInvocationList();
        if (handlers == null) return;
        foreach (var handler in handlers)
        {
            try { ((Action<AssistantRuntimeEvent>)handler)(runtimeEvent); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[AssistantRuntime] Event handler failed: {ex.Message}"); }
        }
    }

    private bool IsCurrentRun(AssistantRunState run)
    {
        lock (stateLock) return IsCurrentRunLocked(run);
    }

    private bool IsCurrentRunLocked(AssistantRunState run) =>
        runs.ContainsKey(run.Id) && !clearing && generation == run.Generation && sessions.Any(session => session.Id == run.SessionId);

    private bool IsCurrentGenerationLocked(long runGeneration) => !clearing && generation == runGeneration;

    private ChatSession FindSessionLocked(string sessionId) =>
        sessions.FirstOrDefault(session => session.Id == sessionId) ?? throw new KeyNotFoundException("会话不存在。");

    private void EnsureNotClearingLocked()
    {
        if (clearing) throw new InvalidOperationException("正在清空助手数据。");
    }

    private void SortSessionsLocked() => sessions.Sort((left, right) => right.UpdatedAt.CompareTo(left.UpdatedAt));

    private string SessionMessagesFile(string sessionId)
    {
        if (!SessionIdPattern.IsMatch(sessionId)) throw new ArgumentException("无效的会话 ID。", nameof(sessionId));
        string path = Path.GetFullPath(Path.Combine(sessionsPath, sessionId + ".json"));
        if (!path.StartsWith(Path.GetFullPath(sessionsPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("无效的会话路径。", nameof(sessionId));
        return path;
    }

    private static string RequireSessionId(string? sessionId) =>
        string.IsNullOrWhiteSpace(sessionId) ? throw new ArgumentException("该命令需要 sessionId。", nameof(sessionId)) : sessionId;

    private static void RequireEmptyObject(JsonElement payload, string command)
    {
        if (payload.ValueKind != JsonValueKind.Object || payload.EnumerateObject().Any())
            throw new ArgumentException($"{command} 的 payload 必须是空对象。");
    }

    private static string ReadRequiredString(JsonElement payload, string command, string name) =>
        ReadOptionalString(payload, command, name) ?? throw new ArgumentException($"缺少字符串字段：{name}。");

    private static string? ReadOptionalString(JsonElement payload, string command, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            throw new ArgumentException($"{command} 的 payload 必须是对象。");

        JsonElement value = default;
        bool found = false;
        foreach (var property in payload.EnumerateObject())
        {
            if (property.Name != name || found)
                throw new ArgumentException($"{command} 的 payload 只允许一个 {name} 字段。");
            found = true;
            value = property.Value;
        }
        if (!found) return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"{command} 的 {name} 必须是字符串。");
        return value.GetString();
    }

    private static string LimitSnapshot(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...[截断]";

    internal static string RedactSensitiveText(string value) => SecretValuePattern.Replace(value, "${prefix}[已隐藏]");

    private static ChatMessage CloneMessage(ChatMessage message) => new()
    {
        Id = message.Id,
        Role = message.Role,
        Content = message.Role == "tool" && message.Content != null ? RedactSensitiveText(message.Content) : message.Content,
        ImagePath = message.ImagePath,
        ToolCallId = message.ToolCallId,
        RunId = message.RunId,
        Timestamp = message.Timestamp,
        ToolCalls = message.ToolCalls?.Select(call => new ToolCallInfo
        {
            Id = call.Id,
            Name = call.Name,
            Arguments = RedactSensitiveText(call.Arguments),
            Result = call.Result == null ? null : RedactSensitiveText(call.Result),
            IsError = call.IsError
        }).ToList()
    };

    private sealed class AssistantRunState
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string SessionId { get; }
        public long Generation { get; }
        public string Status { get; set; } = "running";
        public List<AssistantRunToolState> Tools { get; } = [];
        public string? Error { get; set; }
        public DateTime StartedAt { get; } = DateTime.UtcNow;
        public DateTime? CompletedAt { get; set; }
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Task { get; set; } = Task.CompletedTask;

        public AssistantRunState(string sessionId, long generation)
        {
            SessionId = sessionId;
            Generation = generation;
        }
    }

    private void PruneRunsLocked()
    {
        foreach (var run in runs.Values.Where(run => run.CompletedAt != null)
            .OrderByDescending(run => run.StartedAt).Skip(MaxRetainedRuns).ToArray())
            runs.Remove(run.Id);
    }

    private sealed class AssistantRunToolState(string id, string name, string arguments)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public string Arguments { get; } = arguments;
        public string Status { get; set; } = "running";
        public string? Result { get; set; }
        public string? Error { get; set; }
    }

    private sealed class ChatsIndex
    {
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("assistantEnabled")] public bool AssistantEnabled { get; set; }
        [JsonPropertyName("chats")] public List<ChatSessionIndex>? Chats { get; set; }
    }

    private sealed class ChatSessionIndex
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("actorId")] public Guid ActorId { get; set; }
        [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
        [JsonPropertyName("isTitleLocked")] public bool IsTitleLocked { get; set; }
        [JsonPropertyName("isPinned")] public bool IsPinned { get; set; }
        [JsonPropertyName("status")] public string Status { get; set; } = "idle";
        [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; }
        [JsonPropertyName("updatedAt")] public DateTime UpdatedAt { get; set; }
    }
}

public sealed record AssistantSnapshot(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("generation")] long Generation,
    [property: JsonPropertyName("sessions")] IReadOnlyList<AssistantSessionSnapshot> Sessions,
    [property: JsonPropertyName("activeSessionId")] string? ActiveSessionId,
    [property: JsonPropertyName("messages")] IReadOnlyList<AssistantMessageSnapshot> Messages,
    [property: JsonPropertyName("runs")] IReadOnlyList<AssistantRunSnapshot> Runs);

public sealed record AssistantSessionSnapshot(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title);

public sealed record AssistantMessageSnapshot(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("runId")] string? RunId,
    [property: JsonPropertyName("toolCalls")] IReadOnlyList<AssistantMessageToolCallSnapshot>? ToolCalls);

public sealed record AssistantMessageToolCallSnapshot(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("arguments")] string Arguments,
    [property: JsonPropertyName("result")] string? Result,
    [property: JsonPropertyName("isError")] bool IsError);

public sealed record AssistantRunSnapshot(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("tools")] IReadOnlyList<AssistantRunToolSnapshot> Tools,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("startedAt")] DateTime StartedAt,
    [property: JsonPropertyName("completedAt")] DateTime? CompletedAt);

public sealed record AssistantRunToolSnapshot(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("arguments")] string Arguments,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("result")] string? Result,
    [property: JsonPropertyName("error")] string? Error);
