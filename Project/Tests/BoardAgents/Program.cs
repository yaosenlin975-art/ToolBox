using System.Collections.Concurrent;
using System.Text.Json;
using ToolBox.Core.Boards;
using ToolBox.Core.BoardAgents;
using ToolBox.Core.Todo;

if (args.Length == 2 && args[0] == "--probe-agent-env")
{
    await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(new
    {
        actor = Environment.GetEnvironmentVariable("TOOLBOX_BOARD_ACTOR"),
        codexThread = Environment.GetEnvironmentVariable("CODEX_THREAD_ID"),
        codexSession = Environment.GetEnvironmentVariable("CODEX_SESSION_ID"),
        path = Environment.GetEnvironmentVariable("PATH"),
        marker = Environment.GetEnvironmentVariable("TOOLBOX_AGENT_TEST_MARKER")
    }));
    return 0;
}

if (args.Length == 1 && args[0] == "--probe-installed")
{
    string probeAppData = Path.Combine(Path.GetTempPath(), "toolbox-boardagents-probe-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(probeAppData);
    try
    {
        await using var service = new BoardAgentService(new BoardAgentServiceOptions { AppDataDirectory = probeAppData });
        IReadOnlyList<BoardAgentHostInfo> hosts = await service.DiscoverAsync(refresh: true);
        foreach (BoardAgentHostInfo host in hosts)
            Console.WriteLine($"{host.Host}: available={host.Available}; mode={host.Mode}; via={host.DetectedVia}; path={host.ResolvedPath ?? host.CandidatePath ?? "-"}; entry={host.EntryPath ?? "-"}");
    }
    finally
    {
        try { Directory.Delete(probeAppData, recursive: true); } catch (IOException) { }
    }
    return 0;
}

var tests = new (string Name, Func<Task> Run)[]
{
    ("confirmation hash binds project, card, routing and body", HashBindsContextAsync),
    ("npm shim uses native node and exact session delivery", NpmShimSessionDeliveryAsync),
    ("native npm shim resolves executable without running shim", NativeNpmShimResolutionAsync),
    ("npm node shim resolves an extensionless shebang entry to node argv", ExtensionlessNpmNodeShimResolutionAsync),
    ("extensionless npm entry without Node evidence is rejected", UnsafeExtensionlessNpmEntryIsRejectedAsync),
    ("prompt-looking options stay prompt data", PromptOptionInjectionAsync),
    ("unknown requires verification and retry token", UnknownRequiresVerifiedRetryAsync),
    ("post-start runner I/O failure becomes unknown", RunnerIoFailureBecomesUnknownAsync),
    ("no-session route stays manual pull", NoSessionStaysManualAsync),
    ("stale manual path re-probes PATH", StaleManualPathReprobesAsync),
    ("common-root app requires explicit path confirmation", CommonRootNeedsConfirmationAsync),
    ("registry install location never launches uninstaller", RegistryInstallLocationAsync),
    ("registered URL protocol resolves an explicit launch target", UrlProtocolLaunchAsync),
    ("recovered sending becomes unknown without replay", RecoveredSendingIsUnknownAsync),
    ("changed event runs outside gate and isolates handlers", ChangedEventIsolatedAsync),
    ("delivery query returns confirmation without queueing", DeliveryQueryHasNoSendSideEffectAsync),
    ("comment add queues only a local confirmation and returns delivery", CommentAddCreatesConfirmationAsync),
    ("saved comment remains successful when delivery setup fails", SavedCommentSurvivesDeliveryFailureAsync),
    ("comment acknowledgement updates local delivery independently", CommentAckUpdatesDeliveryAsync),
    ("acknowledged comment is terminal without a local delivery record", AcknowledgedCommentIsTerminalAsync),
    ("acknowledgement records missing and pending delivery states", AcknowledgementClosesPendingStatesAsync),
    ("acknowledgement suppresses a queued send", QueuedAcknowledgementStopsPendingWorkAsync),
    ("agent child process clears inherited actor and session ids", AgentChildEnvironmentIsIsolatedAsync),
    ("host JSON disables launch and applies safe argv and timeout", HostJsonConfigurationAsync),
    ("invalid host argv templates fail before process start", InvalidHostTemplatesAreRejectedAsync),
    ("stale confirmation and invalid delivery payload are rejected", StaleConfirmationAndPayloadValidationAsync),
    ("unknown retry requires verification and request id is idempotent", CommandUnknownRetryIsVerifiedAndIdempotentAsync)
};

int failed = 0;
foreach ((string name, Func<Task> run) in tests)
{
    try
    {
        await run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {name}: {exception}");
    }
}

Console.WriteLine($"{tests.Length - failed}/{tests.Length} tests passed");
return failed == 0 ? 0 : 1;

static Task HashBindsContextAsync()
{
    string root = NewTempDirectory();
    BoardAgentCommentContext context = Context(root);
    string hash = BoardAgentService.GetConfirmationHash(context);
    Assert(hash != BoardAgentService.GetConfirmationHash(context with { CardId = "card-other" }), "card id was omitted from hash");
    Assert(hash != BoardAgentService.GetConfirmationHash(context with { ProjectRoot = Path.GetTempPath() }), "project root was omitted from hash");
    Assert(hash != BoardAgentService.GetConfirmationHash(context with { Host = "opencode" }), "host was omitted from hash");
    Assert(hash != BoardAgentService.GetConfirmationHash(context with { Session = "session-other" }), "session was omitted from hash");
    Assert(hash != BoardAgentService.GetConfirmationHash(context with { Body = "body-other" }), "body was omitted from hash");
    return Task.CompletedTask;
}

static async Task DeliveryQueryHasNoSendSideEffectAsync()
{
    using var fixture = new Fixture();
    var runner = new FakeRunner(new BoardAgentProcessResult(0, false, string.Empty));
    await using var agents = fixture.CreateService(new FakePlatform(fixture.Root, fixture.Root, []), runner);
    ProjectBoardCommentContext context = CreateBoardContext(fixture.ProjectRoot);
    int commentQueries = 0;
    var command = new BoardCommandService((request, _) =>
    {
        if (request.Operation == "board.comment.get")
        {
            commentQueries++;
            AssertPayloadFields(request, "project", "commentId");
            return Task.FromResult(TaskResponse.Success(request, context));
        }
        if (request.Operation == "board.card.get") return Task.FromResult(TaskResponse.Success(request, new { card = new { deletedAt = (DateTime?)null } }));
        return Task.FromResult(TaskResponse.Failure(request, "NOT_FOUND", "unexpected operation"));
    }, agents);

    TaskResponse response = await command.ExecuteAsync(BoardRequest("board.comment.delivery", new { project = "demo", commentId = context.Comment.Id }));
    var result = response.Data as BoardCommentDeliveryResult;
    Assert(response.Ok && result?.Delivery?.Status == BoardAgentDeliveryStatuses.NeedsConfirmation, "read did not return a virtual confirmation state");
    Assert(result!.Delivery!.ConfirmationHash == BoardAgentService.GetConfirmationHash(ToAgentContext(context)), "read returned the wrong confirmation hash");
    TaskResponse unconfirmed = await command.ExecuteAsync(BoardRequest("board.comment.deliver", new { project = "demo", commentId = context.Comment.Id }));
    Assert(((BoardCommentDeliveryResult)unconfirmed.Data!).Delivery?.Status == BoardAgentDeliveryStatuses.NeedsConfirmation, "unconfirmed delivery did not return needsConfirmation");
    string deliveryPath = Path.Combine(Path.GetDirectoryName(agents.GetConfigPath())!, "board-deliveries.json");
    Assert(commentQueries == 2 && runner.Calls.Count == 0 && !File.Exists(deliveryPath), "read or unconfirmed delivery queued or ran a host process");
}

static async Task SavedCommentSurvivesDeliveryFailureAsync()
{
    using var fixture = new Fixture();
    await using var agents = fixture.CreateService(new FakePlatform(fixture.Root, fixture.Root, []), new FakeRunner());
    ProjectBoardCommentContext context = CreateBoardContext(fixture.ProjectRoot);
    var command = new BoardCommandService((request, _) =>
    {
        if (request.Operation == "board.comment.add")
            return Task.FromResult(TaskResponse.Success(request, new
            {
                comment = context.Comment,
                revision = context.BoardRevision,
                boardRevision = context.BoardRevision,
                commentRevision = context.CommentRevision
            }));
        if (request.Operation == "board.comment.get") return Task.FromResult(TaskResponse.Failure(request, "IO_ERROR", "fake read failure"));
        return Task.FromResult(TaskResponse.Failure(request, "NOT_FOUND", "unexpected operation"));
    }, agents);

    TaskResponse response = await command.ExecuteAsync(BoardRequest("board.comment.add", new { project = "demo", id = context.CardId, body = context.Comment.Body }));
    Assert(response.Ok, "a delivery setup failure changed the already-saved comment result to failure");
    Assert(response.Warnings?.Any(warning => warning.Contains("saved", StringComparison.OrdinalIgnoreCase)) == true, "delivery setup failure was not returned as a warning");
}

static async Task CommentAddCreatesConfirmationAsync()
{
    using var fixture = new Fixture();
    var runner = new FakeRunner(new BoardAgentProcessResult(0, false, string.Empty));
    await using var agents = fixture.CreateService(new FakePlatform(fixture.Root, fixture.Root, []), runner);
    ProjectBoardCommentContext context = CreateBoardContext(fixture.ProjectRoot);
    await agents.SetManualHostPathAsync("claude", Touch(Path.Combine(fixture.Root, "claude", "claude.exe")));
    var command = new BoardCommandService((request, _) => request.Operation switch
    {
        "board.comment.add" => Task.FromResult(TaskResponse.Success(request, new
        {
            comment = context.Comment,
            revision = context.BoardRevision,
            boardRevision = context.BoardRevision,
            commentRevision = context.CommentRevision
        })),
        "board.comment.get" => Task.FromResult(TaskResponse.Success(request, context)),
        _ => Task.FromResult(TaskResponse.Failure(request, "NOT_FOUND", "unexpected operation"))
    }, agents);

    TaskResponse response = await command.ExecuteAsync(BoardRequest("board.comment.add", new { project = "demo", id = context.CardId, body = context.Comment.Body }));
    Assert(response.Ok && runner.Calls.Count == 0, "comment add sent a host message without an explicit confirmation");
    using JsonDocument data = JsonDocument.Parse(JsonSerializer.Serialize(response.Data, TaskProtocol.Options));
    Assert(data.RootElement.GetProperty("comment").GetProperty("id").GetString() == context.Comment.Id.ToString("D"), "comment result shape was not preserved");
    Assert(data.RootElement.GetProperty("delivery").GetProperty("status").GetString() == BoardAgentDeliveryStatuses.NeedsConfirmation, "default delivery confirmation was not attached to the add result");
}

static async Task CommentAckUpdatesDeliveryAsync()
{
    using var fixture = new Fixture();
    var runner = new FakeRunner(new BoardAgentProcessResult(0, false, string.Empty));
    await using var agents = fixture.CreateService(new FakePlatform(fixture.Root, fixture.Root, []), runner);
    ProjectBoardCommentContext context = CreateBoardContext(fixture.ProjectRoot);
    BoardAgentCommentContext agentContext = ToAgentContext(context);
    string hash = BoardAgentService.GetConfirmationHash(agentContext);
    await agents.SetManualHostPathAsync("claude", Touch(Path.Combine(fixture.Root, "claude", "claude.exe")));
    await agents.EnqueueAsync(agentContext, hash);
    await agents.DrainAsync();

    var command = new BoardCommandService((request, _) =>
    {
        if (request.Operation == "board.comment.ack") return Task.FromResult(TaskResponse.Success(request, new { comment = context.Comment, revision = context.BoardRevision }));
        if (request.Operation == "board.comment.get") return Task.FromResult(TaskResponse.Success(request, context));
        return Task.FromResult(TaskResponse.Failure(request, "NOT_FOUND", "unexpected operation"));
    }, agents);
    TaskResponse response = await command.ExecuteAsync(BoardRequest("board.comment.ack", new { project = "demo", commentId = context.Comment.Id }));

    Assert(response.Ok, "the board acknowledgement failed");
    Assert((await agents.GetDeliveryAsync(agentContext))?.Status == BoardAgentDeliveryStatuses.Acknowledged, "ack did not independently update local delivery state");
    Assert(runner.Calls.Count == 1, "ack started a second host process");
}

static async Task AcknowledgedCommentIsTerminalAsync()
{
    using var fixture = new Fixture();
    var runner = new FakeRunner(new BoardAgentProcessResult(0, false, string.Empty));
    await using var agents = fixture.CreateService(new FakePlatform(fixture.Root, fixture.Root, []), runner);
    ProjectBoardCommentContext context = CreateBoardContext(fixture.ProjectRoot, acknowledgedAt: DateTime.UtcNow);
    await agents.SetManualHostPathAsync("claude", Touch(Path.Combine(fixture.Root, "claude", "claude.exe")));
    var command = new BoardCommandService((request, _) => request.Operation switch
    {
        "board.comment.get" => Task.FromResult(TaskResponse.Success(request, context)),
        "board.card.get" => Task.FromResult(TaskResponse.Success(request, new { card = new { deletedAt = (DateTime?)null } })),
        _ => Task.FromResult(TaskResponse.Failure(request, "NOT_FOUND", "unexpected operation"))
    }, agents);

    TaskResponse read = await command.ExecuteAsync(BoardRequest("board.comment.delivery", new { project = "demo", commentId = context.Comment.Id }));
    BoardAgentDelivery? readDelivery = ((BoardCommentDeliveryResult)read.Data!).Delivery;
    Assert(read.Ok && readDelivery?.Status == BoardAgentDeliveryStatuses.Acknowledged, "acknowledged comment without local state was displayed as pending");

    string token = BoardAgentService.GetConfirmationHash(ToAgentContext(context));
    TaskResponse deliver = await command.ExecuteAsync(BoardRequest("board.comment.deliver", new
    {
        project = "demo", commentId = context.Comment.Id, confirm = true, confirmationToken = token
    }));
    await agents.DrainAsync();
    Assert(deliver.Ok && ((BoardCommentDeliveryResult)deliver.Data!).Delivery?.Status == BoardAgentDeliveryStatuses.Acknowledged,
        "deliver changed an already acknowledged comment to a sendable state");
    ProjectBoardCommentContext unrouted = context with { Comment = context.Comment with { Routing = null } };
    var unroutedCommand = new BoardCommandService((request, _) => Task.FromResult(TaskResponse.Success(request, unrouted)), agents);
    TaskResponse unroutedRead = await unroutedCommand.ExecuteAsync(BoardRequest("board.comment.delivery", new { project = "demo", commentId = context.Comment.Id }));
    BoardAgentDelivery? unroutedDelivery = ((BoardCommentDeliveryResult)unroutedRead.Data!).Delivery;
    Assert(unroutedDelivery?.Status == BoardAgentDeliveryStatuses.Acknowledged && unroutedDelivery.Host.Length == 0,
        "an acknowledged unrouted comment used an invented or origin-derived host");

    await agents.SetDeliveryPolicyAsync("always");
    var addCommand = new BoardCommandService((request, _) => request.Operation switch
    {
        "board.comment.add" => Task.FromResult(TaskResponse.Success(request, new { comment = context.Comment, cardId = context.CardId })),
        "board.comment.get" => Task.FromResult(TaskResponse.Success(request, context)),
        _ => Task.FromResult(TaskResponse.Failure(request, "NOT_FOUND", "unexpected operation"))
    }, agents);
    TaskResponse add = await addCommand.ExecuteAsync(BoardRequest("board.comment.add", new { project = "demo", id = context.CardId, body = context.Comment.Body }));
    using JsonDocument addData = JsonDocument.Parse(JsonSerializer.Serialize(add.Data, TaskProtocol.Options));
    Assert(addData.RootElement.GetProperty("delivery").GetProperty("status").GetString() == BoardAgentDeliveryStatuses.Acknowledged,
        "an already acknowledged saved comment did not retain its terminal delivery state");
    Assert(runner.Calls.Count == 0, "an acknowledged comment started a host process");
}

static async Task AcknowledgementClosesPendingStatesAsync()
{
    using var fixture = new Fixture();
    var runner = new FakeRunner(new BoardAgentProcessResult(0, false, string.Empty));
    await using var agents = fixture.CreateService(new FakePlatform(fixture.Root, fixture.Root, []), runner);
    await agents.SetManualHostPathAsync("claude", Touch(Path.Combine(fixture.Root, "claude", "claude.exe")));

    BoardAgentCommentContext missing = Context(fixture.ProjectRoot, host: "claude");
    BoardAgentDelivery? created = await agents.MarkAcknowledgedAsync(missing);
    Assert(created?.Status == BoardAgentDeliveryStatuses.Acknowledged, "acknowledgement did not create a local record for a portable project");
    string deliveryPath = Path.Combine(Path.GetDirectoryName(agents.GetConfigPath())!, "board-deliveries.json");
    Assert((await File.ReadAllTextAsync(deliveryPath)).Contains("acknowledged", StringComparison.Ordinal), "new acknowledgement was not persisted");

    BoardAgentCommentContext pending = missing with { CommentId = "comment-pending" };
    BoardAgentDelivery before = await agents.EnqueueAsync(pending);
    Assert(before.Status == BoardAgentDeliveryStatuses.NeedsConfirmation, "setup did not create the expected confirmation record");
    BoardAgentDelivery? acknowledged = await agents.MarkAcknowledgedAsync(pending);
    Assert(acknowledged?.Status == BoardAgentDeliveryStatuses.Acknowledged, "acknowledgement did not close a needsConfirmation record");
    Assert(runner.Calls.Count == 0, "acknowledging a pending comment started a host process");
}

static async Task QueuedAcknowledgementStopsPendingWorkAsync()
{
    using var fixture = new Fixture();
    var runner = new BlockingRunner();
    await using var agents = fixture.CreateService(new FakePlatform(fixture.Root, fixture.Root, []), runner);
    await agents.SetManualHostPathAsync("claude", Touch(Path.Combine(fixture.Root, "claude", "claude.exe")));
    await agents.SetDeliveryPolicyAsync("always");

    BoardAgentCommentContext first = Context(fixture.ProjectRoot, host: "claude") with { CommentId = "comment-active" };
    BoardAgentCommentContext queued = first with { CommentId = "comment-queued" };
    Assert((await agents.EnqueueAsync(first)).Status == BoardAgentDeliveryStatuses.Queued, "first delivery did not queue");
    await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Assert((await agents.MarkAcknowledgedAsync(first))?.Status == BoardAgentDeliveryStatuses.Acknowledged,
        "active delivery could not be marked acknowledged");
    Assert((await agents.EnqueueAsync(queued)).Status == BoardAgentDeliveryStatuses.Queued, "second delivery did not remain queued");

    BoardAgentDelivery? acknowledged = await agents.MarkAcknowledgedAsync(queued);
    Assert(acknowledged?.Status == BoardAgentDeliveryStatuses.Acknowledged, "queued delivery was not marked acknowledged");
    runner.Release.TrySetResult(new BoardAgentProcessResult(0, false, string.Empty));
    await agents.DrainAsync();

    Assert(runner.Calls.Count == 1, "a queued acknowledged comment started a host process");
    Assert((await agents.GetDeliveryAsync(queued))?.Status == BoardAgentDeliveryStatuses.Acknowledged, "worker downgraded acknowledged state after another send completed");
    Assert((await agents.GetDeliveryAsync(first))?.Status == BoardAgentDeliveryStatuses.Acknowledged, "an active process result downgraded acknowledged state");
}

static async Task AgentChildEnvironmentIsIsolatedAsync()
{
    using var fixture = new Fixture();
    string outputPath = Path.Combine(fixture.Root, "child-env.json");
    string[] clearedNames = ["TOOLBOX_BOARD_ACTOR", "CODEX_THREAD_ID", "CODEX_SESSION_ID"];
    Dictionary<string, string?> oldValues = clearedNames.Append("TOOLBOX_AGENT_TEST_MARKER")
        .ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
    string? oldPath = Environment.GetEnvironmentVariable("PATH");
    try
    {
        Environment.SetEnvironmentVariable("TOOLBOX_BOARD_ACTOR", "parent-actor-marker");
        Environment.SetEnvironmentVariable("CODEX_THREAD_ID", "parent-thread-marker");
        Environment.SetEnvironmentVariable("CODEX_SESSION_ID", "parent-session-marker");
        Environment.SetEnvironmentVariable("TOOLBOX_AGENT_TEST_MARKER", "keep-this-marker");
        var runner = new SystemBoardAgentProcessRunner();
        BoardAgentProcessResult result = await runner.ExecuteAsync(new BoardAgentProcessSpec(
            Environment.ProcessPath ?? throw new InvalidOperationException("The test process path is unavailable."),
            ["--probe-agent-env", outputPath],
            fixture.Root,
            null), TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert(result.ExitCode == 0 && !result.TimedOut, "fake child probe failed");
        using JsonDocument probe = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
        JsonElement child = probe.RootElement;
        Assert(clearedNames.All(name => child.GetProperty(name switch
        {
            "TOOLBOX_BOARD_ACTOR" => "actor",
            "CODEX_THREAD_ID" => "codexThread",
            _ => "codexSession"
        }).ValueKind == JsonValueKind.Null), "agent child inherited a parent actor or execution session id");
        Assert(child.GetProperty("path").GetString() == oldPath, "agent child did not retain PATH");
        Assert(child.GetProperty("marker").GetString() == "keep-this-marker", "agent child lost an unrelated environment variable");
    }
    finally
    {
        foreach ((string name, string? value) in oldValues) Environment.SetEnvironmentVariable(name, value);
    }
}

static async Task HostJsonConfigurationAsync()
{
    using (var disabledFixture = new Fixture())
    {
        string exe = Touch(Path.Combine(disabledFixture.Root, "claude", "claude.exe"));
        await WriteHostConfigurationAsync(disabledFixture, "claude", new
        {
            manualPath = exe,
            enabled = false,
            timeoutSeconds = 120
        });
        var runner = new FakeRunner(new BoardAgentProcessResult(0, false, string.Empty));
        await using var service = disabledFixture.CreateService(new FakePlatform(disabledFixture.Root, disabledFixture.Root, []), runner);
        BoardAgentHostInfo host = (await service.DiscoverAsync(refresh: true)).Single(info => info.Host == "claude");
        Assert(!host.Available, "disabled host became available during discovery");
        BoardAgentCommentContext context = Context(disabledFixture.ProjectRoot);
        BoardAgentDelivery delivery = await service.EnqueueAsync(context, BoardAgentService.GetConfirmationHash(context));
        await service.DrainAsync();
        Assert(delivery.Status == BoardAgentDeliveryStatuses.Failed && runner.Calls.Count == 0, "disabled host launched a process");
        string saved = await File.ReadAllTextAsync(service.GetConfigPath());
        Assert(saved.Contains("\"enabled\": false", StringComparison.Ordinal), "discovery re-enabled the disabled host");
    }

    using var configuredFixture = new Fixture();
    string configuredExe = Touch(Path.Combine(configuredFixture.Root, "claude", "claude.exe"));
    await WriteHostConfigurationAsync(configuredFixture, "claude", new
    {
        manualPath = configuredExe,
        enabled = true,
        timeoutSeconds = 37,
        argumentTemplate = new[] { "--resume", "{session}", "--prompt={message}" },
        messageTransport = "argument"
    });
    var configuredRunner = new FakeRunner(new BoardAgentProcessResult(0, false, string.Empty));
    await using var configuredService = configuredFixture.CreateService(new FakePlatform(configuredFixture.Root, configuredFixture.Root, []), configuredRunner);
    string body = "--dangerously-skip-permissions\n--help \"中文\"\\tail";
    BoardAgentCommentContext custom = Context(configuredFixture.ProjectRoot) with { Body = body };
    await configuredService.EnqueueAsync(custom, BoardAgentService.GetConfirmationHash(custom));
    await configuredService.DrainAsync();
    BoardAgentProcessSpec spec = configuredRunner.Calls.Single();
    Assert(spec.Arguments.SequenceEqual(["--resume", "session-1", "--prompt=" + body]), "custom template did not preserve safe bound prompt argv");
    Assert(spec.StandardInput is null, "argument transport also wrote the prompt to stdin");
    Assert(configuredRunner.Timeouts.Single() == TimeSpan.FromSeconds(37), "per-host timeout override was ignored");
}

static async Task InvalidHostTemplatesAreRejectedAsync()
{
    foreach ((string[] template, int timeoutSeconds, string transport) in new[]
    {
        (new[] { "--resume", "{session}", "--prompt={message}", "{unknown}" }, 30, "argument"),
        (new[] { "--resume", "{session}", "{message}" }, 30, "argument"),
        (new[] { "--resume", "{session}" }, 3601, "stdin"),
        (new[] { "--resume", "{session}" }, 30, "shell")
    })
    {
        using var fixture = new Fixture();
        string exe = Touch(Path.Combine(fixture.Root, "claude", "claude.exe"));
        await WriteHostConfigurationAsync(fixture, "claude", new
        {
            manualPath = exe,
            enabled = true,
            timeoutSeconds,
            argumentTemplate = template,
            messageTransport = transport
        });
        var runner = new FakeRunner(new BoardAgentProcessResult(0, false, string.Empty));
        await using var service = fixture.CreateService(new FakePlatform(fixture.Root, fixture.Root, []), runner);
        BoardAgentCommentContext context = Context(fixture.ProjectRoot);
        await service.EnqueueAsync(context, BoardAgentService.GetConfirmationHash(context));
        await service.DrainAsync();
        Assert((await service.GetDeliveryAsync(context))?.Status == BoardAgentDeliveryStatuses.Failed, "invalid argument template was not marked failed");
        Assert(runner.Calls.Count == 0, "invalid argument template started a host process");
    }
}

static async Task StaleConfirmationAndPayloadValidationAsync()
{
    using var fixture = new Fixture();
    var runner = new FakeRunner(new BoardAgentProcessResult(0, false, string.Empty));
    await using var agents = fixture.CreateService(new FakePlatform(fixture.Root, fixture.Root, []), runner);
    ProjectBoardCommentContext context = CreateBoardContext(fixture.ProjectRoot);
    var command = new BoardCommandService((request, _) => request.Operation switch
    {
        "board.comment.get" => Task.FromResult(TaskResponse.Success(request, context)),
        "board.card.get" => Task.FromResult(TaskResponse.Success(request, new { card = new { deletedAt = (DateTime?)null } })),
        _ => Task.FromResult(TaskResponse.Failure(request, "NOT_FOUND", "unexpected operation"))
    }, agents);
    TaskResponse read = await command.ExecuteAsync(BoardRequest("board.comment.delivery", new { cwd = fixture.ProjectRoot, commentId = context.Comment.Id }));
    string staleToken = ((BoardCommentDeliveryResult)read.Data!).Delivery!.ConfirmationHash;
    context = context with { Comment = context.Comment with { Body = "changed body" } };
    TaskResponse stale = await command.ExecuteAsync(BoardRequest("board.comment.deliver", new
    {
        cwd = fixture.ProjectRoot,
        commentId = context.Comment.Id,
        confirm = true,
        confirmationToken = staleToken
    }));
    Assert(!stale.Ok && stale.Error?.Code == "CONFLICT", "a body change did not invalidate the old confirmation token");
    Assert(runner.Calls.Count == 0, "stale confirmation started a process");

    int storeCalls = 0;
    var strictCommand = new BoardCommandService((request, _) =>
    {
        storeCalls++;
        return Task.FromResult(TaskResponse.Failure(request, "NOT_FOUND", "must not reach Store"));
    }, agents);
    TaskResponse unknownField = await strictCommand.ExecuteAsync(BoardRequest("board.comment.delivery", new { cwd = fixture.ProjectRoot, commentId = context.Comment.Id, surprise = true }));
    Assert(!unknownField.Ok && unknownField.Error?.Code == "INVALID_ARGUMENT" && storeCalls == 0, "unknown delivery fields were not rejected locally");
    TaskResponse decimalRevision = await strictCommand.ExecuteAsync(BoardRequest("board.comment.delivery", new { cwd = fixture.ProjectRoot, commentId = context.Comment.Id, revision = 7.5 }));
    Assert(!decimalRevision.Ok && decimalRevision.Error?.Code == "INVALID_ARGUMENT" && storeCalls == 0, "a non-integer revision reached Store");
    TaskResponse wrongConfirmType = await strictCommand.ExecuteAsync(BoardRequest("board.comment.deliver", new { cwd = fixture.ProjectRoot, commentId = context.Comment.Id, confirm = "yes" }));
    Assert(!wrongConfirmType.Ok && wrongConfirmType.Error?.Code == "INVALID_ARGUMENT" && storeCalls == 0, "a non-boolean confirm value reached Store");

    using JsonDocument duplicatePayload = JsonDocument.Parse("{\"cwd\":\"C:\\\\project\",\"commentId\":\"00000000-0000-0000-0000-000000000001\",\"commentId\":\"00000000-0000-0000-0000-000000000002\"}");
    TaskRequest duplicate = new()
    {
        Operation = "board.comment.delivery",
        Payload = duplicatePayload.RootElement.Clone()
    };
    TaskResponse duplicateResponse = await strictCommand.ExecuteAsync(duplicate);
    Assert(!duplicateResponse.Ok && duplicateResponse.Error?.Code == "INVALID_ARGUMENT" && storeCalls == 0, "duplicate delivery fields reached Store");
}

static async Task CommandUnknownRetryIsVerifiedAndIdempotentAsync()
{
    using var fixture = new Fixture();
    var runner = new FakeRunner(
        new BoardAgentProcessResult(null, true, "simulated timeout"),
        new BoardAgentProcessResult(0, false, string.Empty));
    await using var agents = fixture.CreateService(new FakePlatform(fixture.Root, fixture.Root, []), runner);
    ProjectBoardCommentContext context = CreateBoardContext(fixture.ProjectRoot, host: "qoder", session: "qoder-session");
    BoardAgentCommentContext agentContext = ToAgentContext(context);
    await agents.SetManualHostPathAsync("qoder", Touch(Path.Combine(fixture.Root, "qoder", "qoder.exe")));
    var command = new BoardCommandService((request, _) => request.Operation switch
    {
        "board.comment.get" => Task.FromResult(TaskResponse.Success(request, context)),
        "board.card.get" => Task.FromResult(TaskResponse.Success(request, new { card = new { deletedAt = (DateTime?)null } })),
        _ => Task.FromResult(TaskResponse.Failure(request, "NOT_FOUND", "unexpected operation"))
    }, agents);
    string token = BoardAgentService.GetConfirmationHash(agentContext);
    TaskRequest first = BoardRequest("board.comment.deliver", new { project = "demo", commentId = context.Comment.Id, confirm = true, confirmationToken = token });
    TaskResponse queued = await command.ExecuteAsync(first);
    TaskResponse duplicate = await command.ExecuteAsync(first);
    Assert(queued.Ok && duplicate.Ok && runner.Calls.Count == 0, "duplicate requestId was not served from the delivery request cache");
    TaskRequest requestIdConflict = BoardRequest("board.comment.deliver", new
    {
        project = "demo", commentId = context.Comment.Id, confirm = true, confirmationToken = token, verifiedUnknown = false
    }, first.RequestId);
    TaskResponse conflict = await command.ExecuteAsync(requestIdConflict);
    Assert(!conflict.Ok && conflict.Error?.Code == "CONFLICT", "requestId reuse with different content was accepted");
    await agents.DrainAsync();
    Assert(runner.Calls.Count == 1, "the first delivery did not run exactly once");

    TaskResponse unknown = await command.ExecuteAsync(BoardRequest("board.comment.delivery", new { project = "demo", commentId = context.Comment.Id }));
    Assert(((BoardCommentDeliveryResult)unknown.Data!).Delivery?.Status == BoardAgentDeliveryStatuses.Unknown, "timeout did not expose unknown state");
    TaskResponse unverified = await command.ExecuteAsync(BoardRequest("board.comment.deliver", new
    {
        project = "demo", commentId = context.Comment.Id, confirm = true, confirmationToken = token, verifiedUnknown = false
    }));
    Assert(unverified.Ok && ((BoardCommentDeliveryResult)unverified.Data!).Delivery?.Status == BoardAgentDeliveryStatuses.Unknown, "unknown delivery retried without explicit verification");
    Assert(runner.Calls.Count == 1, "unverified unknown delivery started a second process");

    TaskResponse verified = await command.ExecuteAsync(BoardRequest("board.comment.deliver", new
    {
        project = "demo", commentId = context.Comment.Id, confirm = true, confirmationToken = token, verifiedUnknown = true
    }));
    Assert(verified.Ok, "verified unknown delivery was rejected");
    await agents.DrainAsync();
    Assert(runner.Calls.Count == 2, "verified unknown delivery did not retry exactly once");
    TaskResponse submitted = await command.ExecuteAsync(BoardRequest("board.comment.delivery", new { project = "demo", commentId = context.Comment.Id }));
    Assert(((BoardCommentDeliveryResult)submitted.Data!).Delivery?.Status == BoardAgentDeliveryStatuses.Submitted, "verified retry did not reach submitted state");
}

static async Task NpmShimSessionDeliveryAsync()
{
    using var fixture = new Fixture();
    string npm = Path.Combine(fixture.Root, "npm");
    string nodeBin = Path.Combine(fixture.Root, "node");
    string node = Touch(Path.Combine(nodeBin, "node.exe"));
    string shim = Touch(Path.Combine(npm, "claude.cmd"), "@ECHO off\r\n\"%dp0%\\node_modules\\claude-code\\cli.js\" %*\r\n");
    string entry = Touch(Path.Combine(npm, "node_modules", "claude-code", "cli.js"));
    var platform = new FakePlatform(Path.Combine(fixture.Root, "profile"), Path.Combine(fixture.Root, "local"), [nodeBin, npm]);
    var runner = new FakeRunner(new BoardAgentProcessResult(0, false, string.Empty));
    await using var service = fixture.CreateService(platform, runner);

    BoardAgentCommentContext context = Context(fixture.ProjectRoot, host: "claude", session: "session-17");
    BoardAgentDelivery pending = await service.EnqueueAsync(context);
    Assert(pending.Status == BoardAgentDeliveryStatuses.NeedsConfirmation, "default policy did not require confirmation");
    Assert(pending.ConfirmationHash == BoardAgentService.GetConfirmationHash(context), "confirmation hash missing from response");
    Assert(runner.Calls.Count == 0, "confirmation prompt started a process");

    BoardAgentDelivery queued = await service.EnqueueAsync(context, pending.ConfirmationHash);
    Assert(queued.Status == BoardAgentDeliveryStatuses.Queued, "confirmation did not queue delivery");
    await service.DrainAsync();
    BoardAgentProcessSpec spec = runner.Calls.Single();
    Assert(spec.FileName == node, "npm shim was executed instead of native node");
    Assert(spec.Arguments.SequenceEqual([entry, "--resume", "session-17", "-p"]), "Claude argv did not bind exact session");
    Assert(spec.StandardInput == context.Body, "Claude prompt was not passed on stdin");
    Assert(runner.Timeouts.Single() == TimeSpan.FromSeconds(120), "the default host timeout was not 120 seconds");
    Assert(!string.Equals(spec.FileName, shim, StringComparison.OrdinalIgnoreCase), "shell shim was launched");
    Assert((await service.GetDeliveryAsync(context))?.Status == BoardAgentDeliveryStatuses.Submitted, "zero exit did not become submitted");

    await service.EnqueueAsync(context, pending.ConfirmationHash);
    Assert(runner.Calls.Count == 1, "submitted delivery was replayed");
    Assert((await service.MarkAcknowledgedAsync(context))?.Status == BoardAgentDeliveryStatuses.Acknowledged, "acknowledgement was not independent");
    await service.RetryAsync(context, pending.ConfirmationHash, userVerifiedUnknown: true);
    Assert(runner.Calls.Count == 1, "acknowledged delivery was replayed");

    string config = await File.ReadAllTextAsync(service.GetConfigPath());
    string deliveryFile = Path.Combine(Path.GetDirectoryName(service.GetConfigPath())!, "board-deliveries.json");
    string persistedDeliveries = await File.ReadAllTextAsync(deliveryFile);
    Assert(!config.Contains(context.Body, StringComparison.Ordinal), "comment body was written to the editable configuration");
    Assert(!persistedDeliveries.Contains(context.Body, StringComparison.Ordinal), "terminal comment body was retained in delivery history");
}

static async Task UnknownRequiresVerifiedRetryAsync()
{
    using var fixture = new Fixture();
    var runner = new FakeRunner(
        new BoardAgentProcessResult(null, true, "timed out"),
        new BoardAgentProcessResult(0, false, string.Empty));
    await using var service = fixture.CreateService(
        new FakePlatform(Path.Combine(fixture.Root, "npm"), Path.Combine(fixture.Root, "local"), []), runner);
    await service.SetManualHostPathAsync("qoder", Touch(Path.Combine(fixture.Root, "qoder", "qoder.exe")));
    BoardAgentCommentContext context = Context(fixture.ProjectRoot, host: "qoder", session: "qoder-session");
    string hash = BoardAgentService.GetConfirmationHash(context);

    await service.EnqueueAsync(context, hash);
    await service.DrainAsync();
    Assert((await service.GetDeliveryAsync(context))?.Status == BoardAgentDeliveryStatuses.Unknown, "timeout was not marked unknown");
    await service.RetryAsync(context, hash, userVerifiedUnknown: false);
    Assert(runner.Calls.Count == 1, "unknown was replayed without explicit verification");
    await service.RetryAsync(context, hash, userVerifiedUnknown: true);
    await service.DrainAsync();
    Assert(runner.Calls.Count == 2, "verified unknown did not retry");
    Assert((await service.GetDeliveryAsync(context))?.Status == BoardAgentDeliveryStatuses.Submitted, "verified retry did not submit");
}

static async Task RunnerIoFailureBecomesUnknownAsync()
{
    using var fixture = new Fixture();
    var runner = new FaultingRunner();
    await using var service = fixture.CreateService(
        new FakePlatform(fixture.Root, Path.Combine(fixture.Root, "local"), []), runner);
    string codex = Touch(Path.Combine(fixture.Root, "codex", "codex.exe"));
    await service.SetManualHostPathAsync("codex", codex);
    BoardAgentCommentContext context = Context(fixture.ProjectRoot, "codex", "session-io");
    string hash = BoardAgentService.GetConfirmationHash(context);
    await service.EnqueueAsync(context, hash);
    await service.DrainAsync();

    Assert((await service.GetDeliveryAsync(context))?.Status == BoardAgentDeliveryStatuses.Unknown, "runner I/O failure was marked definitely failed");
    await service.RetryAsync(context, hash, userVerifiedUnknown: false);
    Assert(runner.Calls == 1, "unknown I/O result was automatically replayed");
}

static async Task PromptOptionInjectionAsync()
{
    string body = "--dangerously-skip-permissions\n--help \"中文\"\\tail";
    foreach (string host in new[] { "opencode", "zcode", "qoder" })
    {
        using var fixture = new Fixture();
        string? node = null;
        string hostPath;
        if (host == "zcode")
        {
            node = Touch(Path.Combine(fixture.Root, "node", "node.exe"));
            string appRoot = Path.Combine(fixture.Root, "ZCode");
            hostPath = Touch(Path.Combine(appRoot, "ZCode.exe"));
            Touch(Path.Combine(appRoot, "resources", "glm", "zcode.cjs"));
        }
        else
        {
            hostPath = Touch(Path.Combine(fixture.Root, host, host + ".exe"));
        }
        var platform = new FakePlatform(fixture.Root, Path.Combine(fixture.Root, "local"), node is null ? [] : [Path.GetDirectoryName(node)!]);
        var runner = new FakeRunner(new BoardAgentProcessResult(0, false, string.Empty));
        await using var service = fixture.CreateService(platform, runner);
        await service.SetManualHostPathAsync(host, hostPath);
        BoardAgentCommentContext context = Context(fixture.ProjectRoot, host, "session-safe") with { Body = body };
        string hash = BoardAgentService.GetConfirmationHash(context);
        await service.EnqueueAsync(context, hash);
        await service.DrainAsync();

        BoardAgentProcessSpec spec = runner.Calls.Single();
        Assert(spec.StandardInput == (host == "zcode" ? null : body), $"{host} did not use the safe prompt transport");
        Assert(!spec.Arguments.Contains("--dangerously-skip-permissions") && !spec.Arguments.Contains("--help"), $"{host} split prompt text into option tokens");
        if (host == "zcode") Assert(spec.Arguments.Contains($"--prompt={body}"), "ZCode prompt was not bound to its declared --prompt option");
        else Assert(!spec.Arguments.Any(argument => argument.Contains(body, StringComparison.Ordinal)), $"{host} copied the body into argv instead of stdin");
    }
}

static async Task NativeNpmShimResolutionAsync()
{
    using var fixture = new Fixture();
    string npm = Path.Combine(fixture.Root, "npm");
    string native = Touch(Path.Combine(npm, "node_modules", "opencode-ai", "bin", "opencode.exe"));
    Touch(Path.Combine(npm, "opencode.ps1"), "$basedir=Split-Path $MyInvocation.MyCommand.Definition -Parent\r\n& \"$basedir/node_modules/opencode-ai/bin/opencode.exe\" $args\r\n");
    await using var service = fixture.CreateService(
        new FakePlatform(fixture.Root, Path.Combine(fixture.Root, "local"), [npm]), new FakeRunner());
    BoardAgentHostInfo opencode = (await service.DiscoverAsync()).Single(host => host.Host == "opencode");
    Assert(opencode.Available && opencode.Mode == "cli", "native npm shim was not resolved");
    Assert(opencode.ResolvedPath == native && opencode.EntryPath is null, "service did not target the shim's native executable");
    Assert(opencode.DetectedVia == "npm-shim", "native npm shim discovery source was lost");
}

static async Task ExtensionlessNpmNodeShimResolutionAsync()
{
    using var fixture = new Fixture();
    string npm = Path.Combine(fixture.Root, "npm");
    string nodeDirectory = Path.Combine(fixture.Root, "node");
    string node = Touch(Path.Combine(nodeDirectory, "node.exe"));
    string entry = Touch(Path.Combine(npm, "node_modules", "@tencent-ai", "codebuddy-code", "bin", "codebuddy"),
        "#!/usr/bin/env node\r\nprocess.exit(0);\r\n");
    string shim = Touch(Path.Combine(npm, "codebuddy.ps1"), CodeBuddyNodeShimText("$basedir/node_modules/@tencent-ai/codebuddy-code/bin/codebuddy"));
    var runner = new FakeRunner(new BoardAgentProcessResult(0, false, string.Empty));
    await using var service = fixture.CreateService(
        new FakePlatform(Path.Combine(fixture.Root, "profile"), Path.Combine(fixture.Root, "local"), [nodeDirectory, npm]), runner);

    BoardAgentHostInfo codebuddy = (await service.DiscoverAsync()).Single(host => host.Host == "codebuddy");
    Assert(codebuddy.Available && codebuddy.Mode == "cli", "extensionless CodeBuddy npm entry was not discovered as CLI");
    Assert(codebuddy.ResolvedPath == node && codebuddy.EntryPath == entry, "discovery did not select PATH node.exe and exact extensionless entry");
    Assert(codebuddy.DetectedVia == "npm-shim", "extensionless Node shim source was lost");

    BoardAgentCommentContext context = Context(fixture.ProjectRoot, host: "codebuddy", session: "codebuddy-session");
    await service.EnqueueAsync(context, BoardAgentService.GetConfirmationHash(context));
    await service.DrainAsync();
    BoardAgentProcessSpec spec = runner.Calls.Single();
    Assert(spec.FileName == node, "CodeBuddy delivery did not launch the independently discovered Node executable");
    Assert(spec.Arguments.SequenceEqual([entry, "--resume", "codebuddy-session", "-p"]), "CodeBuddy argv did not use the exact extensionless entry and session");
    Assert(spec.StandardInput == context.Body, "CodeBuddy prompt was not passed on stdin");
    Assert(!string.Equals(spec.FileName, shim, StringComparison.OrdinalIgnoreCase), "CodeBuddy PowerShell shim was executed");
}

static async Task UnsafeExtensionlessNpmEntryIsRejectedAsync()
{
    foreach ((string leaf, string contents) in new[]
    {
        ("codebuddy", "process.exit(0);\r\n"),
        ("codebuddy.txt", "#!/usr/bin/env node\r\nprocess.exit(0);\r\n")
    })
    {
        using var fixture = new Fixture();
        string npm = Path.Combine(fixture.Root, "npm");
        string nodeDirectory = Path.Combine(fixture.Root, "node");
        Touch(Path.Combine(nodeDirectory, "node.exe"));
        Touch(Path.Combine(npm, "node_modules", "@tencent-ai", "codebuddy-code", "bin", leaf), contents);
        Touch(Path.Combine(npm, "codebuddy.ps1"), CodeBuddyNodeShimText("$basedir/node_modules/@tencent-ai/codebuddy-code/bin/" + leaf));
        await using var service = fixture.CreateService(
            new FakePlatform(Path.Combine(fixture.Root, "profile"), Path.Combine(fixture.Root, "local"), [nodeDirectory, npm]), new FakeRunner());

        BoardAgentHostInfo codebuddy = (await service.DiscoverAsync()).Single(host => host.Host == "codebuddy");
        Assert(!codebuddy.Available && codebuddy.Mode == "none", $"unsafe entry '{leaf}' was accepted as a Node CLI");
    }
}

static string CodeBuddyNodeShimText(string entryPath) =>
    "$basedir=Split-Path $MyInvocation.MyCommand.Definition -Parent\r\n" +
    "if (Test-Path \"$basedir/node.exe\") {\r\n" +
    "  & \"$basedir/node$exe\"  \"" + entryPath + "\" $args\r\n" +
    "} else {\r\n" +
    "  & \"node$exe\"  \"" + entryPath + "\" $args\r\n" +
    "}\r\n";

static async Task NoSessionStaysManualAsync()
{
    using var fixture = new Fixture();
    var runner = new FakeRunner(new BoardAgentProcessResult(0, false, string.Empty));
    await using var service = fixture.CreateService(new FakePlatform(fixture.Root, fixture.Root, []), runner);
    BoardAgentCommentContext context = Context(fixture.ProjectRoot, host: "trae", session: null);
    BoardAgentDelivery result = await service.EnqueueAsync(context);
    await service.DrainAsync();
    Assert(result.Status == BoardAgentDeliveryStatuses.ManualPullRequired, "no-session delivery was not manual pull");
    Assert(runner.Calls.Count == 0, "no-session delivery used a speculative session");
}

static async Task StaleManualPathReprobesAsync()
{
    using var fixture = new Fixture();
    string path = Path.Combine(fixture.Root, "path");
    string alternate = Path.Combine(fixture.Root, "manual");
    Directory.CreateDirectory(path);
    Directory.CreateDirectory(alternate);
    Touch(Path.Combine(path, "codex.exe"));
    string manualExe = Touch(Path.Combine(alternate, "codex.exe"));
    var platform = new FakePlatform(fixture.Root, Path.Combine(fixture.Root, "local"), [path]);
    await using var service = fixture.CreateService(platform, new FakeRunner());
    await service.SetManualHostPathAsync("codex", manualExe);
    File.Delete(manualExe);
    BoardAgentHostInfo codex = (await service.DiscoverAsync()).Single(host => host.Host == "codex");
    Assert(codex.Available && codex.DetectedVia == "path", "stale manual path was not cleared and rediscovered from PATH");
}

static async Task CommonRootNeedsConfirmationAsync()
{
    using var fixture = new Fixture();
    string common = Path.Combine(fixture.Root, "common");
    string zcodeExe = Touch(Path.Combine(common, "ZCode", "ZCode.exe"));
    var platform = new FakePlatform(fixture.Root, Path.Combine(fixture.Root, "local"), [], [common]);
    await using var service = fixture.CreateService(platform, new FakeRunner());
    BoardAgentHostInfo zcode = (await service.DiscoverAsync()).Single(host => host.Host == "zcode");
    Assert(!zcode.Available && zcode.RequiresPathConfirmation && zcode.CandidatePath == zcodeExe, "common-root candidate was silently trusted");
}

static async Task RegistryInstallLocationAsync()
{
    using var fixture = new Fixture();
    string installRoot = Path.Combine(fixture.Root, "WorkBuddyInstall");
    string appExe = Touch(Path.Combine(installRoot, "WorkBuddy.exe"));
    var platform = new FakePlatform(fixture.Root, Path.Combine(fixture.Root, "local"), [],
        installedApps: [new BoardAgentInstalledApp("WorkBuddy", null, "C:\\Program Files\\WorkBuddy\\uninstall.exe", installRoot)]);
    await using var service = fixture.CreateService(platform, new FakeRunner());
    BoardAgentHostInfo workBuddy = (await service.DiscoverAsync()).Single(host => host.Host == "workbuddy");
    Assert(workBuddy.Available && workBuddy.ResolvedPath == appExe, "registry install location did not resolve the application executable");
    Assert(!workBuddy.ResolvedPath!.Contains("uninstall", StringComparison.OrdinalIgnoreCase), "uninstall command was treated as launch executable");
}

static async Task UrlProtocolLaunchAsync()
{
    using var fixture = new Fixture();
    string qoderExe = Touch(Path.Combine(fixture.Root, "Qoder CN.exe"));
    var platform = new FakePlatform(fixture.Root, Path.Combine(fixture.Root, "local"), [],
        protocolCommands: new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["qoder"] = [$"\"{qoderExe}\" \"%1\""]
        });
    await using var service = fixture.CreateService(platform, new FakeRunner());
    BoardAgentHostInfo qoder = (await service.DiscoverAsync()).Single(host => host.Host == "qoder");
    BoardAgentLaunchTarget? launch = service.ResolveLaunch("qoder", fixture.ProjectRoot);
    Assert(qoder.Available && qoder.Mode == "app", "URL protocol did not discover app mode");
    Assert(launch is { Kind: "uri", Uri: "qoder://", ProjectRoot: var root } && root == fixture.ProjectRoot, "URI launch target was not resolved safely");
}

static async Task RecoveredSendingIsUnknownAsync()
{
    using var fixture = new Fixture();
    var platform = new FakePlatform(fixture.Root, Path.Combine(fixture.Root, "local"), []);
    var runner = new FakeRunner(new BoardAgentProcessResult(0, false, string.Empty));
    await using var service = fixture.CreateService(platform, runner);
    BoardAgentCommentContext context = Context(fixture.ProjectRoot, host: "codex", session: "old-session");
    string hash = BoardAgentService.GetConfirmationHash(context);
    string deliveryPath = Path.Combine(fixture.AppData, "board-deliveries.json");
    Directory.CreateDirectory(fixture.AppData);
    var persisted = new[]
    {
        new
        {
            commentId = context.CommentId,
            cardId = context.CardId,
            projectRoot = context.ProjectRoot,
            host = context.Host,
            session = context.Session,
            body = context.Body,
            confirmationHash = hash,
            status = "sending",
            error = (string?)null,
            updatedAt = DateTimeOffset.UtcNow
        }
    };
    await File.WriteAllTextAsync(deliveryPath, System.Text.Json.JsonSerializer.Serialize(persisted));
    Assert((await service.GetDeliveryAsync(context))?.Status == BoardAgentDeliveryStatuses.Unknown, "persisted sending was not recovered as unknown");
    Assert(runner.Calls.Count == 0, "recovered sending was automatically replayed");
    string saved = await File.ReadAllTextAsync(deliveryPath);
    Assert(saved.Contains("unknown", StringComparison.Ordinal), "unknown recovery was not persisted");
}

static async Task ChangedEventIsolatedAsync()
{
    using var fixture = new Fixture();
    await using var service = fixture.CreateService(new FakePlatform(fixture.Root, Path.Combine(fixture.Root, "local"), []), new FakeRunner());
    int called = 0;
    service.Changed += () => throw new InvalidOperationException("subscriber failure");
    service.Changed += () => Interlocked.Increment(ref called);
    BoardAgentCommentContext context = Context(fixture.ProjectRoot, host: "trae", session: null);
    await service.EnqueueAsync(context);
    Assert(called > 0, "change event did not fire after status persistence");
}

static BoardAgentCommentContext Context(string root, string host = "claude", string? session = "session-1") =>
    new("comment-1", "card-1", root, host, session, "Send this comment safely.");

static ProjectBoardCommentContext CreateBoardContext(string root, string host = "claude", string? session = "session-1", DateTime? acknowledgedAt = null)
{
    DateTime now = DateTime.UtcNow;
    var comment = new ProjectBoardComment(
        Guid.Parse("00000000-0000-0000-0000-000000000101"),
        null,
        "Send this comment safely.",
        new ProjectBoardAssignee("human", "author", "Author", host, session),
        new ProjectBoardCommentOrigin(host, session),
        new ProjectBoardCommentRouting(host, session),
        now,
        now,
        null,
        acknowledgedAt,
        acknowledgedAt is null ? null : "human");
    return new ProjectBoardCommentContext(
        new ProjectBoardRegistration(Guid.Parse("00000000-0000-0000-0000-000000000100"), "demo", root),
        Guid.Parse("00000000-0000-0000-0000-000000000102"),
        comment,
        7,
        11);
}

static BoardAgentCommentContext ToAgentContext(ProjectBoardCommentContext context) => new(
    context.Comment.Id.ToString("D"),
    context.CardId.ToString("D"),
    context.Registration.Path,
    context.Comment.Routing!.Host,
    context.Comment.Routing.SessionId,
    context.Comment.Body);

static TaskRequest BoardRequest(string operation, object payload, string? requestId = null) => new()
{
    RequestId = requestId ?? Guid.NewGuid().ToString(),
    Operation = operation,
    Payload = JsonSerializer.SerializeToElement(payload, TaskProtocol.Options)
};

static void AssertPayloadFields(TaskRequest request, params string[] expected)
{
    string[] fields = request.Payload!.Value.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
    string[] sortedExpected = expected.OrderBy(name => name, StringComparer.Ordinal).ToArray();
    Assert(fields.SequenceEqual(sortedExpected), $"query forwarded unexpected fields: {string.Join(",", fields)}");
}

static Task WriteHostConfigurationAsync(Fixture fixture, string host, object hostConfiguration)
{
    Directory.CreateDirectory(fixture.AppData);
    var state = new
    {
        policy = "confirm",
        hosts = new Dictionary<string, object> { [host] = hostConfiguration }
    };
    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
    return File.WriteAllTextAsync(Path.Combine(fixture.AppData, "board-agents.json"), JsonSerializer.Serialize(state, options));
}

static string NewTempDirectory()
{
    string path = Path.Combine(Path.GetTempPath(), "toolbox-board-agents-test", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return path;
}

static string Touch(string path, string contents = "")
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, contents);
    return Path.GetFullPath(path);
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class Fixture : IDisposable
{
    public string Root { get; } = CreateTempDirectory();
    public string ProjectRoot { get; }
    public string AppData { get; }

    public Fixture()
    {
        ProjectRoot = Directory.CreateDirectory(Path.Combine(Root, "project")).FullName;
        AppData = Path.Combine(Root, "appdata");
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "toolbox-board-agents-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public BoardAgentService CreateService(IBoardAgentDiscoveryPlatform platform, IBoardAgentProcessRunner runner) =>
        new(new BoardAgentServiceOptions { AppDataDirectory = AppData, DiscoveryPlatform = platform, ProcessRunner = runner });

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}

sealed class FakePlatform(
    string appData,
    string localAppData,
    IReadOnlyList<string> pathDirectories,
    IReadOnlyList<string>? commonRoots = null,
    IReadOnlyList<BoardAgentInstalledApp>? installedApps = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? protocolCommands = null) : IBoardAgentDiscoveryPlatform
{
    public string? GetEnvironmentVariable(string name) => name.Equals("PATH", StringComparison.OrdinalIgnoreCase) ? string.Join(Path.PathSeparator, pathDirectories) : null;
    public string? GetFolderPath(Environment.SpecialFolder folder) => folder switch
    {
        Environment.SpecialFolder.ApplicationData => appData,
        Environment.SpecialFolder.LocalApplicationData => localAppData,
        _ => null
    };
    public IEnumerable<string> GetPathDirectories() => pathDirectories;
    public IEnumerable<string> GetUrlProtocolCommands(string scheme) => protocolCommands?.GetValueOrDefault(scheme) ?? [];
    public IEnumerable<BoardAgentInstalledApp> GetUninstallEntries() => installedApps ?? [];
    public IEnumerable<string> GetCommonRoots() => commonRoots ?? [];
}

sealed class FakeRunner(params BoardAgentProcessResult[] results) : IBoardAgentProcessRunner
{
    private readonly ConcurrentQueue<BoardAgentProcessResult> _results = new(results);
    public ConcurrentQueue<BoardAgentProcessSpec> Calls { get; } = new();
    public ConcurrentQueue<TimeSpan> Timeouts { get; } = new();
    public Task<BoardAgentProcessResult> ExecuteAsync(BoardAgentProcessSpec spec, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Enqueue(spec);
        Timeouts.Enqueue(timeout);
        if (!_results.TryDequeue(out BoardAgentProcessResult? result)) result = new BoardAgentProcessResult(0, false, string.Empty);
        return Task.FromResult(result);
    }
}

sealed class BlockingRunner : IBoardAgentProcessRunner
{
    public ConcurrentQueue<BoardAgentProcessSpec> Calls { get; } = new();
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<BoardAgentProcessResult> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<BoardAgentProcessResult> ExecuteAsync(BoardAgentProcessSpec spec, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Calls.Enqueue(spec);
        Started.TrySetResult();
        return Release.Task.WaitAsync(cancellationToken);
    }
}

sealed class FaultingRunner : IBoardAgentProcessRunner
{
    public int Calls { get; private set; }
    public Task<BoardAgentProcessResult> ExecuteAsync(BoardAgentProcessSpec spec, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromException<BoardAgentProcessResult>(new IOException("simulated pipe write failure after process start"));
    }
}
