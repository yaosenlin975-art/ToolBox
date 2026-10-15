using System.Collections.Concurrent;
using ToolBox.Core.BoardAgents;

var tests = new (string Name, Func<Task> Run)[]
{
    ("confirmation hash binds project, card, routing and body", HashBindsContextAsync),
    ("npm shim uses native node and exact session delivery", NpmShimSessionDeliveryAsync),
    ("native npm shim resolves executable without running shim", NativeNpmShimResolutionAsync),
    ("prompt-looking options stay prompt data", PromptOptionInjectionAsync),
    ("unknown requires verification and retry token", UnknownRequiresVerifiedRetryAsync),
    ("post-start runner I/O failure becomes unknown", RunnerIoFailureBecomesUnknownAsync),
    ("no-session route stays manual pull", NoSessionStaysManualAsync),
    ("stale manual path re-probes PATH", StaleManualPathReprobesAsync),
    ("common-root app requires explicit path confirmation", CommonRootNeedsConfirmationAsync),
    ("registry install location never launches uninstaller", RegistryInstallLocationAsync),
    ("registered URL protocol resolves an explicit launch target", UrlProtocolLaunchAsync),
    ("recovered sending becomes unknown without replay", RecoveredSendingIsUnknownAsync),
    ("changed event runs outside gate and isolates handlers", ChangedEventIsolatedAsync)
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
    public Task<BoardAgentProcessResult> ExecuteAsync(BoardAgentProcessSpec spec, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Enqueue(spec);
        if (!_results.TryDequeue(out BoardAgentProcessResult? result)) result = new BoardAgentProcessResult(0, false, string.Empty);
        return Task.FromResult(result);
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
