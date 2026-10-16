using System.Diagnostics;
using System.Text.Json;
using ToolBox.Core.Assistant;
using ToolBox.Core.Tools;
using ToolBox.Core.Todo;

internal static class Program
{
    private static int checks;

    private static async Task<int> Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "ToolBox.AssistantCommands." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await CheckDisabledGate(root);
            await CheckCommandsAndJobs(Path.Combine(root, "jobs"));
            await CheckRestartIsUnknown(Path.Combine(root, "restart"));
            Console.WriteLine($"PASS ({checks} checks)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static async Task CheckDisabledGate(string root)
    {
        int factories = 0;
        var service = new ToolsCommandService(() => false, () =>
        {
            factories++;
            return new ToolRegistry();
        }, root);
        foreach (TaskRequest request in new[]
        {
            Request("tools.list"), Request("tools.get", "echo"), Request("tools.call", "echo", "{\"text\":\"x\"}")
        })
        {
            TaskResponse response = await service.ExecuteAsync(request);
            Check(!response.Ok && response.Error?.Code == "ASSISTANT_DISABLED", request.Operation + " is gated");
        }
        Check(factories == 0, "disabled calls do not construct the registry");
    }

    private static async Task CheckCommandsAndJobs(string root)
    {
        bool enabled = true;
        var fixture = new Fixture();
        int factories = 0;
        ToolRegistry Registry()
        {
            factories++;
            return new ToolRegistry().Register(fixture);
        }

        var service = new ToolsCommandService(() => enabled, Registry, root);
        TaskResponse listed = await service.ExecuteAsync(Request("tools.list"));
        Check(listed.Ok && factories == 1, "list lazily creates the registry once");
        using (JsonDocument list = JsonDocument.Parse(JsonSerializer.Serialize(listed.Data, TaskProtocol.Options)))
        {
            JsonElement item = list.RootElement.GetProperty("tools").EnumerateArray().Single(value => value.GetProperty("name").GetString() == "echo");
            Check(item.GetProperty("inputSchema").ValueKind == JsonValueKind.Object, "tool schema is returned as JSON, not a string");
            Check(!item.TryGetProperty("handler", out _), "registry reflection handlers are not exposed");
        }

        TaskResponse got = await service.ExecuteAsync(Request("tools.get", "echo"));
        Check(got.Ok && factories == 1, "get uses the same registry");
        Check((await service.ExecuteAsync(Request("tools.get", "bad name"))).Error?.Code == "INVALID_ARGUMENT", "invalid tool names are rejected");

        TaskRequest[] invalidCalls =
        [
            Request("tools.call", "echo", "{}"),
            Request("tools.call", "echo", "{\"text\":7,\"count\":1}"),
            Request("tools.call", "echo", "{\"text\":\"x\",\"count\":1.5}"),
            Request("tools.call", "echo", "{\"text\":\"x\",\"count\":1,\"extra\":true}"),
            Request("tools.call", "echo", "{\"text\":\"x\",\"count\":{\"nested\":1}}"),
            Request("tools.call", "echo", "{\"text\":\"x\",\"text\":\"y\",\"count\":1}")
        ];
        foreach (TaskRequest invalid in invalidCalls)
            Check((await service.ExecuteAsync(invalid)).Error?.Code == "INVALID_ARGUMENT", "tool arguments are strictly validated");
        Check(fixture.EchoCalls == 0, "invalid arguments never execute a handler");

        TaskRequest echo = Request("tools.call", "echo", "{\"text\":\"hello\",\"count\":2}");
        TaskResponse echoQueued = await service.ExecuteAsync(echo);
        string echoId = CallId(echoQueued);
        TaskResponse echoResult = await WaitForTerminal(service, echoId);
        Check(echoResult.Ok && DataString(echoResult, "result") == "hello:2", "tool result is available by call ID");

        string requestId = Guid.NewGuid().ToString("D");
        TaskRequest waitRequest = Request("tools.call", "wait", "{}", requestId);
        var timer = Stopwatch.StartNew();
        TaskResponse waitQueued = await service.ExecuteAsync(waitRequest);
        Check(timer.Elapsed < TimeSpan.FromSeconds(1), "long call returns without waiting for the tool");
        string waitId = CallId(waitQueued);
        await fixture.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        TaskResponse retry = await service.ExecuteAsync(Request("tools.call", "wait", "{}", requestId));
        Check(CallId(retry) == waitId && fixture.WaitCalls == 1, "same request ID and fingerprint reuse one execution");
        TaskResponse conflict = await service.ExecuteAsync(Request("tools.call", "wait", "{\"unused\":1}", requestId));
        Check(conflict.Error?.Code == "CONFLICT", "any changed arguments on a reused request ID conflict");
        TaskResponse changed = await service.ExecuteAsync(Request("tools.call", "echo", "{\"text\":\"different\",\"count\":2}", requestId));
        Check(changed.Error?.Code == "CONFLICT", "same request ID with different tool arguments conflicts");

        TaskResponse canceled = await service.ExecuteAsync(Request("tools.cancel", waitId));
        Check(canceled.Ok && DataBool(canceled, "cancellationRequested"), "cancel requests cancellation of the exact call ID");
        TaskResponse canceledResult = await WaitForTerminal(service, waitId);
        Check(DataString(canceledResult, "status") == "canceled", "canceled work reaches canceled status");

        TaskResponse failedCall = await service.ExecuteAsync(Request("tools.call", "fail", "{}"));
        TaskResponse failed = await WaitForTerminal(service, CallId(failedCall));
        Check(!failed.Ok && DataString(failed, "status") == "failed" && failed.Error?.Code == "TOOL_EXECUTION_FAILED",
            "handler exceptions are returned as failed jobs");

        TaskResponse businessCall = await service.ExecuteAsync(Request("tools.call", "business_error", "{}"));
        TaskResponse business = await WaitForTerminal(service, CallId(businessCall));
        Check(!business.Ok && DataString(business, "status") == "failed", "known tool error strings are not reported as success");

        TaskResponse largeCall = await service.ExecuteAsync(Request("tools.call", "large", "{}"));
        TaskResponse large = await WaitForTerminal(service, CallId(largeCall));
        Check(!large.Ok && large.Error?.Code == "RESULT_TOO_LARGE", "oversized results use the stable size error");

        TaskResponse cancelAllCall = await service.ExecuteAsync(Request("tools.call", "wait", "{}"));
        string cancelAllId = CallId(cancelAllCall);
        await fixture.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var startedDeadline = Stopwatch.StartNew();
        while (Volatile.Read(ref fixture.WaitCalls) < 2 && startedDeadline.Elapsed < TimeSpan.FromSeconds(3))
            await Task.Delay(10);
        Check(Volatile.Read(ref fixture.WaitCalls) == 2, "cancel-all fixture has entered its second long call");
        enabled = false;
        var cancelAllTimer = Stopwatch.StartNew();
        await service.CancelAllAsync();
        Check(cancelAllTimer.Elapsed < TimeSpan.FromSeconds(1), "cancel-all requests cancellation without waiting for handlers to finish");
        TaskResponse cancelAllResult = await WaitForTerminal(service, cancelAllId);
        Check(DataString(cancelAllResult, "status") == "canceled", "cancel-all cancels active tool jobs");
        TaskResponse disabledCall = await service.ExecuteAsync(Request("tools.call", "echo", "{\"text\":\"x\",\"count\":1}"));
        Check(disabledCall.Error?.Code == "ASSISTANT_DISABLED", "disabled service rejects new calls after cancel-all");
        TaskResponse stillReadable = await service.ExecuteAsync(Request("tools.result", waitId));
        Check(DataString(stillReadable, "status") == "canceled", "result remains readable while Assistant is disabled");
    }

    private static async Task CheckRestartIsUnknown(string root)
    {
        var fixture = new Fixture();
        ToolRegistry Factory() => new ToolRegistry().Register(fixture);
        var beforeRestart = new ToolsCommandService(() => true, Factory, root);
        string requestId = Guid.NewGuid().ToString("D");
        TaskResponse queued = await beforeRestart.ExecuteAsync(Request("tools.call", "wait", "{}", requestId));
        string callId = CallId(queued);
        await fixture.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var afterRestart = new ToolsCommandService(() => true, () => throw new InvalidOperationException("registry must stay lazy"), root);
        TaskResponse unknown = await afterRestart.ExecuteAsync(Request("tools.result", callId));
        Check(unknown.Ok && DataString(unknown, "status") == "unknown", "persisted running work becomes unknown after restart");
        TaskResponse retry = await afterRestart.ExecuteAsync(Request("tools.call", "wait", "{}", requestId));
        Check(retry.Ok && CallId(retry) == callId && DataString(retry, "status") == "unknown", "a post-restart retry never replays uncertain work");

        await beforeRestart.CancelAllAsync();
    }

    private static async Task<TaskResponse> WaitForTerminal(ToolsCommandService service, string callId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (true)
        {
            TaskResponse response = await service.ExecuteAsync(Request("tools.result", callId), timeout.Token);
            string status = DataString(response, "status");
            if (status is "completed" or "failed" or "canceled" or "unknown") return response;
            await Task.Delay(10, timeout.Token);
        }
    }

    private static TaskRequest Request(string operation, string? id = null, string? payload = null, string? requestId = null)
    {
        var request = new TaskRequest { Operation = operation, Id = id, RequestId = requestId ?? Guid.NewGuid().ToString("D") };
        if (payload is not null) request.Payload = Element(payload);
        return request;
    }

    private static JsonElement Element(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string CallId(TaskResponse response)
    {
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(response.Data, TaskProtocol.Options));
        return document.RootElement.GetProperty("callId").GetString()!;
    }

    private static string DataString(TaskResponse response, string field)
    {
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(response.Data, TaskProtocol.Options));
        return document.RootElement.GetProperty(field).GetString()!;
    }

    private static bool DataBool(TaskResponse response, string field)
    {
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(response.Data, TaskProtocol.Options));
        return document.RootElement.GetProperty(field).GetBoolean();
    }

    private static void Check(bool condition, string label)
    {
        checks++;
        if (!condition) throw new InvalidOperationException("FAIL: " + label);
    }

    private sealed class Fixture
    {
        public int EchoCalls;
        public int WaitCalls;
        public TaskCompletionSource WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        [Tool("echo", "Echo typed arguments.")]
        public string Echo([ToolParam("text")] string text, [ToolParam("count")] int count)
        {
            Interlocked.Increment(ref EchoCalls);
            return $"{text}:{count}";
        }

        [Tool("wait", "Wait until canceled.")]
        public async Task<string> Wait(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref WaitCalls);
            WaitStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return "finished";
        }

        [Tool("fail", "Throw from the handler.")]
        public string Fail() => throw new InvalidOperationException("private exception detail");

        [Tool("business_error", "Return a known business error.")]
        public string BusinessError() => "搜索失败: unavailable";

        [Tool("large", "Return an oversized result.")]
        public string Large() => new('x', TaskProtocol.MaxBytes + 1);
    }
}
