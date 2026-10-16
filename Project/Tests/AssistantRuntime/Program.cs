using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ToolBox.Core.Llm;
using ToolBox.Core.Memory;
using ToolBox.Core.Providers;
using ToolBox.Core.Tools;

internal static class Program
{
    private static int checks;

    private static async Task<int> Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "ToolBox.AssistantRuntime", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
#if DEBUG
            await TestDebugStorageIsolationAsync(Path.Combine(root, "debug-defaults"));
#endif
            await TestLegacyActorMigrationAsync(Path.Combine(root, "actors"));
            await TestStreamingToolsAndPersistenceAsync(Path.Combine(root, "streaming"));
            await TestSessionIsolationAsync(Path.Combine(root, "isolation"));
            await TestCancellationAndClearAsync(Path.Combine(root, "clear"));
            await TestMissingProviderAsync(Path.Combine(root, "missing-provider"));
            Console.WriteLine($"PASS ({checks} checks)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

#if DEBUG
    private static async Task TestDebugStorageIsolationAsync(string root)
    {
        string? previousRoot = Environment.GetEnvironmentVariable("TOOLBOX_TASK_TEST_ROOT");
        Environment.SetEnvironmentVariable("TOOLBOX_TASK_TEST_ROOT", root);
        try
        {
            await AssistantRuntime.Instance.SetEnabledAsync(true);
            Check(File.Exists(Path.Combine(root, "assistant", "chats.json")),
                "debug singleton stores chat index under the task test root");
            _ = MemoryStore.Instance;
            Check(File.Exists(Path.Combine(root, "assistant", "Data", "memory.db")),
                "debug default memory database uses the task test root");
        }
        finally { Environment.SetEnvironmentVariable("TOOLBOX_TASK_TEST_ROOT", previousRoot); }
    }
#endif

    private static async Task TestLegacyActorMigrationAsync(string root)
    {
        Directory.CreateDirectory(root);
        string sessionId = "12ab34cd";
        string chatsPath = Path.Combine(root, "chats.json");
        await File.WriteAllTextAsync(chatsPath,
            $$"""{"version":1,"assistantEnabled":false,"chats":[{"id":"{{sessionId}}","title":"legacy"}]}""");
        var memory = new MemoryStore(Path.Combine(root, "memory.db"));
        var runtime = new AssistantRuntime(root, () => null, memory);
        Guid actorId = runtime.GetSessionActorId(sessionId);
        Check(actorId != Guid.Empty, "legacy session gets a non-empty actor id");

        using (var index = JsonDocument.Parse(await File.ReadAllTextAsync(chatsPath)))
            Check(index.RootElement.GetProperty("chats")[0].GetProperty("actorId").GetGuid() == actorId,
                "legacy actor id is persisted during migration");

        var reopened = new AssistantRuntime(root, () => null, memory);
        Check(reopened.GetSessionActorId(sessionId) == actorId, "actor id remains stable after reload");
        CheckThrows<KeyNotFoundException>(() => reopened.GetSessionActorId("00000000"), "unknown actor session is rejected");
    }

    private static async Task TestStreamingToolsAndPersistenceAsync(string root)
    {
        string memoryPath = Path.Combine(root, "Data", "memory.db");
        var memory = new MemoryStore(memoryPath);
        var provider = new ScriptedProvider(
            [new ChatChunk
            {
                ToolCalls =
                [
                    new ToolCallInfo { Id = "call-one", Name = "echo", Arguments = "{\"value\":1}" },
                    new ToolCallInfo { Id = "call-two", Name = "echo", Arguments = "{\"value\":2}" },
                    new ToolCallInfo { Id = "call-missing", Name = "missing", Arguments = "{}" }
                ]
            }],
            [new ChatChunk { Text = "生成" }, new ChatChunk { Text = "完成" }]);
        var runtime = new AssistantRuntime(root, () => provider, memory);
        var events = new EventCollector(runtime);
        Check(!runtime.Enabled, "new assistant runtime is disabled by default");
        await CheckThrowsAsync<ArgumentException>(
            () => runtime.HandleAsync("session.create", null, Payload("{\"title\":\"not supported\"}")),
            "session.create rejects unsupported payload fields");
        await runtime.SetEnabledAsync(true);
        var session = (AssistantSessionSnapshot)await runtime.HandleAsync("session.create", null, Payload("{}"));
        Guid actorId = runtime.GetSessionActorId(session.Id);
        var executionSessions = new ConcurrentBag<string?>();
        runtime.ConfigureTools(
            [new ToolInfo { Name = "echo" }],
            (name, _, _) =>
            {
                executionSessions.Add(AssistantRuntime.CurrentExecutionSessionId);
                return Task.FromResult("private tool result body");
            });

        var ack = JsonSerializer.SerializeToElement(await runtime.HandleAsync(
            "message.send", session.Id, Payload("{\"text\":\"test request\"}")));
        string runId = ack.GetProperty("runId").GetString()!;
        Check(ack.GetProperty("status").GetString() == "running", "message.send returns a running acknowledgement");
        await events.WaitForAsync(e => e.Type == "run.status" && e.RunId == runId && Status(e) == "completed");

        var toolEvents = events.Events.Where(e => e.Type == "tool.status" && e.RunId == runId)
            .Select(e => (AssistantToolEventPayload)e.Payload).ToArray();
        foreach (string toolId in new[] { "call-one", "call-two", "call-missing" })
            Check(toolEvents.Any(e => e.ToolId == toolId && e.Status == "running"), $"tool {toolId} emits running");
        Check(toolEvents.Any(e => e.ToolId == "call-one" && e.Status == "completed"), "first tool completes");
        Check(toolEvents.Any(e => e.ToolId == "call-two" && e.Status == "completed"), "second tool completes");
        Check(toolEvents.Any(e => e.ToolId == "call-missing" && e.Status == "failed"), "unknown tool failure is visible");
        Check(executionSessions.Count == 2 && executionSessions.All(id => id == session.Id),
            "tool executor receives the current execution session id");
        Check(AssistantRuntime.CurrentExecutionSessionId == null, "execution session scope is restored after the run");

        var snapshot = (AssistantSnapshot)runtime.GetSnapshot();
        var assistantCalls = snapshot.Messages.SelectMany(message => message.ToolCalls ?? [])
            .ToDictionary(call => call.Id, StringComparer.Ordinal);
        Check(assistantCalls.Count == 3, "all provider tool call ids survive in the snapshot");
        Check(assistantCalls["call-one"].Result == "private tool result body", "tool result remains attached to its call");
        Check(snapshot.Messages.All(message => message.RunId == runId), "each message is associated with its run id");
        Check(events.Events.Where(e => e.Type == "text.delta" && e.RunId == runId)
            .Select(e => (AssistantTextDeltaPayload)e.Payload).Select(e => e.Sequence).SequenceEqual([1L, 2L]),
            "stream events have increasing sequence numbers");
        Check(events.Events.Any(e => e.Type == "snapshot"), "runtime publishes snapshot events");
        Check(actorId != Guid.Empty, "created session has a stable actor id");

        List<string> savedMemory = await memory.GetRelevantAsync(session.Id);
        Check(savedMemory.Count == 2 && savedMemory.All(value => value.Contains("echo", StringComparison.Ordinal)),
            "successful tool calls save only short completion facts");
        Check(savedMemory.All(value => !value.Contains("private tool result body", StringComparison.Ordinal)),
            "raw tool results are not persisted as memory");

        var reopened = new AssistantRuntime(root, () => null, memory);
        Check(reopened.Enabled, "enabled state persists");
        Check(reopened.GetSessionActorId(session.Id) == actorId, "new-session actor id persists");
        var restored = (AssistantSnapshot)reopened.GetSnapshot();
        Check(restored.Messages.Count == snapshot.Messages.Count, "messages restore from disk");
        Check(restored.Messages.All(message => message.RunId == runId), "run associations restore from disk");
        Check(AssistantRuntime.CurrentExecutionSessionId == null, "no-session execution context remains null");
    }

    private static async Task TestSessionIsolationAsync(string root)
    {
        var providers = new ConcurrentQueue<ILlmProvider>();
        var runtime = new AssistantRuntime(root, () => providers.TryDequeue(out var provider) ? provider : null,
            new MemoryStore(Path.Combine(root, "memory.db")));
        var events = new EventCollector(runtime);
        await runtime.SetEnabledAsync(true);
        var first = (AssistantSessionSnapshot)await runtime.HandleAsync("session.create", null, Payload("{}"));
        var second = (AssistantSessionSnapshot)await runtime.HandleAsync("session.create", null, Payload("{}"));

        var firstProvider = new GateTextProvider("response from first");
        providers.Enqueue(firstProvider);
        string firstRun = RunId(await runtime.HandleAsync("message.send", first.Id, Payload("{\"text\":\"first request\"}")));
        await firstProvider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var secondProvider = new ScriptedProvider([new ChatChunk { Text = "response from second" }]);
        providers.Enqueue(secondProvider);
        string secondRun = RunId(await runtime.HandleAsync("message.send", second.Id, Payload("{\"text\":\"second request\"}")));
        await events.WaitForAsync(e => e.Type == "run.status" && e.RunId == secondRun && Status(e) == "completed");
        Check(firstProvider.ExecutionSessionId == first.Id && secondProvider.ExecutionSessionId == second.Id,
            "concurrent agent scopes keep separate session identities");
        firstProvider.Release.TrySetResult(true);
        await events.WaitForAsync(e => e.Type == "run.status" && e.RunId == firstRun && Status(e) == "completed");

        var firstSnapshot = (AssistantSnapshot)await runtime.HandleAsync("session.select", first.Id, Payload("{}"));
        Check(firstSnapshot.Messages.Any(message => message.Content == "first request"), "first session retains its user message");
        Check(firstSnapshot.Messages.Any(message => message.Content == "response from first"), "first session retains its response");
        Check(firstSnapshot.Messages.All(message => message.RunId == firstRun), "first session messages keep the first run id");
        var secondSnapshot = (AssistantSnapshot)await runtime.HandleAsync("session.select", second.Id, Payload("{}"));
        Check(secondSnapshot.Messages.Any(message => message.Content == "second request"), "second session retains its user message");
        Check(secondSnapshot.Messages.Any(message => message.Content == "response from second"), "second session retains its response");
        Check(secondSnapshot.Messages.All(message => message.RunId == secondRun), "second session messages keep the second run id");
    }

    private static async Task TestCancellationAndClearAsync(string root)
    {
        var providers = new ConcurrentQueue<ILlmProvider>();
        var memory = new MemoryStore(Path.Combine(root, "memory.db"));
        var runtime = new AssistantRuntime(root, () => providers.TryDequeue(out var provider) ? provider : null, memory);
        var events = new EventCollector(runtime);
        await runtime.SetEnabledAsync(true);
        var session = (AssistantSessionSnapshot)await runtime.HandleAsync("session.create", null, Payload("{}"));
        await CheckThrowsAsync<ArgumentException>(
            () => runtime.HandleAsync("sessions.clear", null, Payload("{\"unexpected\":true}")),
            "sessions.clear rejects unsupported payload fields");

        var toolProvider = new ScriptedProvider([new ChatChunk
        {
            ToolCall = new ToolCallInfo { Id = "cancel-call", Name = "slow", Arguments = "{}" }
        }]);
        providers.Enqueue(toolProvider);
        runtime.ConfigureTools([new ToolInfo { Name = "slow" }], async (_, _, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return "unreachable";
        });
        string runId = RunId(await runtime.HandleAsync("message.send", session.Id, Payload("{\"text\":\"cancel this\"}")));
        await events.WaitForAsync(e => e.Type == "tool.status" && e.RunId == runId &&
            e.Payload is AssistantToolEventPayload payload && payload.ToolId == "cancel-call" && payload.Status == "running");
        var wrongCancel = JsonSerializer.SerializeToElement(await runtime.HandleAsync(
            "run.cancel", session.Id, Payload("{\"runId\":\"wrong-run\"}")));
        Check(!wrongCancel.GetProperty("canceled").GetBoolean(), "cancel rejects a different run id");
        var cancel = JsonSerializer.SerializeToElement(await runtime.HandleAsync(
            "run.cancel", session.Id, Payload($"{{\"runId\":\"{runId}\"}}")));
        Check(cancel.GetProperty("canceled").GetBoolean(), "matching run id cancels its active run");
        await events.WaitForAsync(e => e.Type == "run.status" && e.RunId == runId && Status(e) == "canceled");
        Check(events.Events.Any(e => e.Type == "tool.status" && e.RunId == runId &&
            e.Payload is AssistantToolEventPayload payload && payload.ToolId == "cancel-call" && payload.Status == "failed"),
            "canceled tool call has a terminal failure event for its id");

        await memory.SaveAsync(session.Id, "test", "temporary memory", 1, 0.5);
        var lateProvider = new LateTextProvider("late response must be dropped");
        providers.Enqueue(lateProvider);
        string lateRun = RunId(await runtime.HandleAsync("message.send", session.Id, Payload("{\"text\":\"clear this\"}")));
        await lateProvider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        long oldGeneration = ((AssistantSnapshot)runtime.GetSnapshot()).Generation;
        Task clearTask = runtime.HandleAsync("sessions.clear", null, Payload("{}"));
        await WaitUntilAsync(() => ((AssistantSnapshot)runtime.GetSnapshot()).Generation > oldGeneration);
        Check(!clearTask.IsCompleted, "clear waits for an in-flight provider callback");
        lateProvider.Release.TrySetResult(true);
        await clearTask.WaitAsync(TimeSpan.FromSeconds(5));

        var cleared = (AssistantSnapshot)runtime.GetSnapshot();
        Check(cleared.Sessions.Count == 0 && cleared.Messages.Count == 0 && cleared.Runs.Count == 0,
            "clear removes sessions, messages and run records");
        Check(cleared.Generation == oldGeneration + 1, "clear advances the generation");
        Check((await memory.GetRelevantAsync(session.Id)).Count == 0, "clear removes SQLite memory");
        Check(!events.Events.Any(e => e.Type == "text.delta" && e.RunId == lateRun), "late text is rejected after generation changes");
    }

    private static async Task TestMissingProviderAsync(string root)
    {
        var runtime = new AssistantRuntime(root, () => null, new MemoryStore(Path.Combine(root, "memory.db")));
        var events = new EventCollector(runtime);
        await runtime.SetEnabledAsync(true);
        var session = (AssistantSessionSnapshot)await runtime.HandleAsync("session.create", null, Payload("{}"));
        string runId = RunId(await runtime.HandleAsync("message.send", session.Id, Payload("{\"text\":\"without provider\"}")));
        await events.WaitForAsync(e => e.Type == "run.status" && e.RunId == runId && Status(e) == "failed");
        Check(events.Events.Any(e => e.Type == "error" && e.RunId == runId &&
            e.Payload is AssistantErrorPayload payload && payload.Code == "RUN_FAILED"),
            "missing provider is reported as a visible run failure");
    }

    private static JsonElement Payload(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string RunId(object ack) => JsonSerializer.SerializeToElement(ack).GetProperty("runId").GetString()!;

    private static string? Status(AssistantRuntimeEvent runtimeEvent) =>
        runtimeEvent.Payload is AssistantRunStatusPayload payload ? payload.Status : null;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static void Check(bool condition, string description)
    {
        checks++;
        if (!condition) throw new InvalidOperationException("FAIL: " + description);
    }

    private static void CheckThrows<T>(Action action, string description) where T : Exception
    {
        checks++;
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("FAIL: " + description);
    }

    private static async Task CheckThrowsAsync<T>(Func<Task> action, string description) where T : Exception
    {
        checks++;
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("FAIL: " + description);
    }

    private sealed class EventCollector
    {
        private readonly ConcurrentQueue<AssistantRuntimeEvent> events = new();
        private readonly SemaphoreSlim signal = new(0);

        public IReadOnlyList<AssistantRuntimeEvent> Events => events.ToArray();

        public EventCollector(AssistantRuntime runtime) => runtime.Changed += OnChanged;

        private void OnChanged(AssistantRuntimeEvent runtimeEvent)
        {
            events.Enqueue(runtimeEvent);
            signal.Release();
        }

        public async Task<AssistantRuntimeEvent> WaitForAsync(Func<AssistantRuntimeEvent, bool> predicate)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                var match = events.FirstOrDefault(predicate);
                if (match != null) return match;
                await signal.WaitAsync(timeout.Token);
            }
        }
    }

    private sealed class ScriptedProvider(params ChatChunk[][] rounds) : ILlmProvider
    {
        private readonly Queue<ChatChunk[]> rounds = new(rounds);

        public string? ExecutionSessionId { get; private set; }
        public string Name => "fake";

        public async IAsyncEnumerable<ChatChunk> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolInfo>? tools = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ExecutionSessionId ??= AssistantRuntime.CurrentExecutionSessionId;
            ChatChunk[] chunks = rounds.Count == 0 ? [new ChatChunk { Text = "done" }] : rounds.Dequeue();
            foreach (var chunk in chunks)
            {
                ct.ThrowIfCancellationRequested();
                yield return chunk;
                await Task.Yield();
            }
        }
    }

    private sealed class GateTextProvider(string text) : ILlmProvider
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? ExecutionSessionId { get; private set; }
        public string Name => "gate";

        public async IAsyncEnumerable<ChatChunk> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolInfo>? tools = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ExecutionSessionId = AssistantRuntime.CurrentExecutionSessionId;
            Started.TrySetResult(true);
            await Release.Task.WaitAsync(ct);
            yield return new ChatChunk { Text = text };
        }
    }

    private sealed class LateTextProvider(string text) : ILlmProvider
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => "late";

        public async IAsyncEnumerable<ChatChunk> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolInfo>? tools = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Started.TrySetResult(true);
            await Release.Task;
            yield return new ChatChunk { Text = text };
        }
    }
}
