// 项目看板 Agent 投递服务：宿主发现（PATH/npm、URL 协议、卸载注册表、版本目录、常见目录）与会话投递适配器配置，
// 以及确认摘要、本地投递队列（含 worker）和状态持久化（board-agents.json / board-deliveries.json）。
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace ToolBox.Core.BoardAgents;

public sealed class BoardAgentService : IAsyncDisposable
{
    private const int MAX_BODY_BYTES = 64 * 1024;
    private const int MAXIMUM_HOST_TIMEOUT_SECONDS = 3600;
    private static readonly TimeSpan DiscoveryCacheTtl = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] HostIds = ["codex", "claude", "codebuddy", "opencode", "zcode", "qoder", "trae", "workbuddy", "internal"];
    private static readonly Lazy<BoardAgentService> DefaultInstance = new(CreateDefault);
    private readonly string appDataDirectory;
    private readonly string statePath;
    private readonly string deliveryPath;
    private readonly IBoardAgentDiscoveryPlatform platform;
    private readonly IBoardAgentProcessRunner processRunner;
    private readonly TimeSpan defaultTimeout;
    private Func<bool>? internalEnabled;
    private Func<BoardAgentCommentContext, TimeSpan, CancellationToken, Task<BoardAgentProcessResult>>? internalExecutor;
    private readonly SemaphoreSlim stateGate = new(1, 1);
    private readonly Channel<string> queue = Channel.CreateBounded<string>(new BoundedChannelOptions(128)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });
    private readonly ConcurrentDictionary<string, BoardAgentHostResolution> resolutions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> queuedKeys = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task worker;
    private PersistedState? state;
    private DateTime stateLastWriteUtc;
    private bool stateFileObserved;
    private List<PersistedDelivery>? deliveries;
    private DateTime deliveryLastWriteUtc;
    private bool deliveryFileObserved;
    private TaskCompletionSource idle = CompletedSource();
    private int pendingJobs;
    private int deliveryChangePending;
    private bool disposed;
    private DateTime discoveryStampUtc;

    public static BoardAgentService Instance => DefaultInstance.Value;
    public event Action? Changed;

    public BoardAgentService(BoardAgentServiceOptions? options = null)
    {
        options ??= new BoardAgentServiceOptions();
        string? configuredDirectory = options.AppDataDirectory;
        appDataDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(configuredDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ToolBox")
            : configuredDirectory);
        statePath = Path.Combine(appDataDirectory, "board-agents.json");
        deliveryPath = Path.Combine(appDataDirectory, "board-deliveries.json");
        platform = options.DiscoveryPlatform ?? new WindowsBoardAgentDiscoveryPlatform();
        processRunner = options.ProcessRunner ?? new SystemBoardAgentProcessRunner();
        internalEnabled = options.InternalEnabled;
        internalExecutor = options.InternalExecutor;
        defaultTimeout = options.DefaultTimeout > TimeSpan.Zero ? options.DefaultTimeout : TimeSpan.FromSeconds(120);
        worker = Task.Run(WorkerAsync);
    }

    public void ConfigureInternal(
        Func<bool> isEnabled,
        Func<BoardAgentCommentContext, TimeSpan, CancellationToken, Task<BoardAgentProcessResult>> executeAsync)
    {
        ArgumentNullException.ThrowIfNull(isEnabled);
        ArgumentNullException.ThrowIfNull(executeAsync);
        internalEnabled = isEnabled;
        internalExecutor = executeAsync;
        resolutions.TryRemove("internal", out _);
        discoveryStampUtc = DateTime.MinValue;
        Interlocked.Exchange(ref deliveryChangePending, 1);
        RaisePendingChanged();
    }

    public string GetConfigPath() => statePath;

    public static string GetConfirmationHash(BoardAgentCommentContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        string root = NormalizeProjectRoot(context.ProjectRoot);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            context.CommentId, context.CardId, root, NormalizeHost(context.Host), context.Session ?? string.Empty, context.Body
        });
        return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }

    public async Task<IReadOnlyList<BoardAgentHostInfo>> DiscoverAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        await stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PersistedState state = await LoadStateUnsafeAsync(cancellationToken).ConfigureAwait(false);
            await LoadDeliveriesUnsafeAsync(cancellationToken).ConfigureAwait(false);
            if (!refresh && resolutions.Count == HostIds.Length && DateTime.UtcNow - discoveryStampUtc < DiscoveryCacheTtl)
            {
                var cached = new List<BoardAgentHostInfo>(HostIds.Length);
                foreach (string host in HostIds)
                {
                    BoardAgentHostResolution resolution = host == "internal"
                        ? CreateInternalResolution()
                        : resolutions[host];
                    resolutions[host] = resolution;
                    cached.Add(ToHostInfo(resolution));
                }
                return cached;
            }
            var result = new List<BoardAgentHostInfo>(HostIds.Length);
            bool changed = false;
            foreach (string host in HostIds)
            {
                BoardAgentHostResolution resolution = ResolveHost(host, state, refresh, out bool configChanged);
                changed |= configChanged;
                resolutions[host] = resolution;
                result.Add(ToHostInfo(resolution));
            }
            discoveryStampUtc = DateTime.UtcNow;
            if (changed) await SaveStateUnsafeAsync(state, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            stateGate.Release();
            RaisePendingChanged();
        }
    }

    public async Task<BoardAgentDelivery> EnqueueAsync(
        BoardAgentCommentContext context,
        string? confirmedHash = null,
        CancellationToken cancellationToken = default)
    {
        context = ValidateContext(context);
        string key = GetConfirmationHash(context);
        await DiscoverAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        await stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PersistedState state = await LoadStateUnsafeAsync(cancellationToken).ConfigureAwait(false);
            List<PersistedDelivery> deliveries = await LoadDeliveriesUnsafeAsync(cancellationToken).ConfigureAwait(false);
            PersistedDelivery? existing = deliveries.SingleOrDefault(d => d.ConfirmationHash == key);
            BoardAgentHostResolution resolution = resolutions[context.Host];
            if (existing is not null)
            {
                if (existing.Status == BoardAgentDeliveryStatuses.NeedsConfirmation && context.Session is not null &&
                    resolution.Enabled && resolution.SupportsSession && (resolution.Mode == "cli" || resolution.Mode == "internal") &&
                    (string.Equals(state.Policy, "always", StringComparison.Ordinal) || FixedEquals(confirmedHash, key)))
                {
                    existing.Status = BoardAgentDeliveryStatuses.Queued;
                    existing.Body = context.Body;
                    existing.Error = null;
                    existing.UpdatedAt = DateTimeOffset.UtcNow;
                    await SaveDeliveriesUnsafeAsync(deliveries, cancellationToken).ConfigureAwait(false);
                    if (!QueueUnsafe(key))
                    {
                        existing.Status = BoardAgentDeliveryStatuses.Failed;
                        existing.Error = "The local delivery queue is full.";
                        existing.UpdatedAt = DateTimeOffset.UtcNow;
                        await SaveDeliveriesUnsafeAsync(deliveries, cancellationToken).ConfigureAwait(false);
                    }
                }
                return ToDelivery(existing);
            }
            string status;
            string? error = null;
            if (context.Session is null)
            {
                status = BoardAgentDeliveryStatuses.ManualPullRequired;
            }
            else if (!resolution.Enabled || !resolution.SupportsSession || (resolution.Mode != "cli" && resolution.Mode != "internal"))
            {
                status = BoardAgentDeliveryStatuses.Failed;
                error = resolution.Mode == "internal"
                    ? "The internal assistant is unavailable. Enable Assistant and configure its executor."
                    : "Exact session delivery is unavailable for this host. Use its inbox pull or manual launch.";
            }
            else if (!string.Equals(state.Policy, "always", StringComparison.Ordinal) && !FixedEquals(confirmedHash, key))
            {
                status = BoardAgentDeliveryStatuses.NeedsConfirmation;
            }
            else
            {
                status = BoardAgentDeliveryStatuses.Queued;
            }

            var delivery = new PersistedDelivery
            {
                CommentId = context.CommentId,
                CardId = context.CardId,
                ProjectRoot = context.ProjectRoot,
                Host = context.Host,
                Session = context.Session,
                Body = status == BoardAgentDeliveryStatuses.Queued ? context.Body : string.Empty,
                ConfirmationHash = key,
                Status = status,
                Error = error,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            deliveries.Add(delivery);
            await SaveDeliveriesUnsafeAsync(deliveries, cancellationToken).ConfigureAwait(false);
            if (status == BoardAgentDeliveryStatuses.Queued && !QueueUnsafe(key))
            {
                delivery.Status = BoardAgentDeliveryStatuses.Failed;
                delivery.Error = "The local delivery queue is full.";
                delivery.UpdatedAt = DateTimeOffset.UtcNow;
                await SaveDeliveriesUnsafeAsync(deliveries, cancellationToken).ConfigureAwait(false);
            }
            return ToDelivery(delivery);
        }
        finally
        {
            stateGate.Release();
            RaisePendingChanged();
        }
    }

    public async Task<BoardAgentDelivery?> GetDeliveryAsync(BoardAgentCommentContext context, CancellationToken cancellationToken = default)
    {
        context = ValidateContext(context);
        string key = GetConfirmationHash(context);
        await stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PersistedState state = await LoadStateUnsafeAsync(cancellationToken).ConfigureAwait(false);
            List<PersistedDelivery> deliveries = await LoadDeliveriesUnsafeAsync(cancellationToken).ConfigureAwait(false);
            PersistedDelivery? delivery = deliveries.SingleOrDefault(d => d.ConfirmationHash == key);
            return delivery is null ? null : ToDelivery(delivery);
        }
        finally
        {
            stateGate.Release();
            RaisePendingChanged();
        }
    }

    public async Task<BoardAgentDelivery?> MarkAcknowledgedAsync(BoardAgentCommentContext context, CancellationToken cancellationToken = default)
    {
        context = ValidateContext(context);
        string key = GetConfirmationHash(context);
        await stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PersistedState state = await LoadStateUnsafeAsync(cancellationToken).ConfigureAwait(false);
            List<PersistedDelivery> deliveries = await LoadDeliveriesUnsafeAsync(cancellationToken).ConfigureAwait(false);
            PersistedDelivery? delivery = deliveries.SingleOrDefault(d => d.ConfirmationHash == key);
            bool created = delivery is null;
            if (delivery is null)
            {
                delivery = new PersistedDelivery
                {
                    CommentId = context.CommentId,
                    CardId = context.CardId,
                    ProjectRoot = context.ProjectRoot,
                    Host = context.Host,
                    Session = context.Session,
                    Body = string.Empty,
                    ConfirmationHash = key,
                    Status = BoardAgentDeliveryStatuses.Acknowledged,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                deliveries.Add(delivery);
            }
            if (!created && delivery.Status == BoardAgentDeliveryStatuses.Acknowledged) return ToDelivery(delivery);
            delivery.Status = BoardAgentDeliveryStatuses.Acknowledged;
            delivery.Body = string.Empty;
            delivery.Error = null;
            delivery.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveDeliveriesUnsafeAsync(deliveries, cancellationToken).ConfigureAwait(false);
            return ToDelivery(delivery);
        }
        finally
        {
            stateGate.Release();
            RaisePendingChanged();
        }
    }

    public async Task<BoardAgentDelivery?> RetryAsync(
        BoardAgentCommentContext context,
        string confirmedHash,
        bool userVerifiedUnknown,
        CancellationToken cancellationToken = default)
    {
        context = ValidateContext(context);
        string key = GetConfirmationHash(context);
        if (!FixedEquals(confirmedHash, key)) throw new InvalidOperationException("A current confirmation hash is required to retry delivery.");

        await DiscoverAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PersistedState state = await LoadStateUnsafeAsync(cancellationToken).ConfigureAwait(false);
            List<PersistedDelivery> deliveries = await LoadDeliveriesUnsafeAsync(cancellationToken).ConfigureAwait(false);
            PersistedDelivery? delivery = deliveries.SingleOrDefault(d => d.ConfirmationHash == key);
            if (delivery is null || delivery.Status is BoardAgentDeliveryStatuses.NeedsConfirmation or BoardAgentDeliveryStatuses.Submitted or BoardAgentDeliveryStatuses.Acknowledged or BoardAgentDeliveryStatuses.Queued or BoardAgentDeliveryStatuses.Sending)
                return delivery is null ? null : ToDelivery(delivery);
            if (delivery.Status == BoardAgentDeliveryStatuses.Unknown && !userVerifiedUnknown) return ToDelivery(delivery);
            BoardAgentHostResolution resolution = resolutions[context.Host];
            if (context.Session is null || !resolution.Enabled || !resolution.SupportsSession || (resolution.Mode != "cli" && resolution.Mode != "internal"))
            {
                delivery.Status = BoardAgentDeliveryStatuses.ManualPullRequired;
                delivery.Error = null;
            }
            else
            {
                delivery.Status = BoardAgentDeliveryStatuses.Queued;
                delivery.Body = context.Body;
                delivery.Error = null;
                delivery.UpdatedAt = DateTimeOffset.UtcNow;
                await SaveDeliveriesUnsafeAsync(deliveries, cancellationToken).ConfigureAwait(false);
                if (!QueueUnsafe(key))
                {
                    delivery.Status = BoardAgentDeliveryStatuses.Failed;
                    delivery.Error = "The local delivery queue is full.";
                    delivery.UpdatedAt = DateTimeOffset.UtcNow;
                    await SaveDeliveriesUnsafeAsync(deliveries, cancellationToken).ConfigureAwait(false);
                }
            }
            delivery.UpdatedAt = DateTimeOffset.UtcNow;
            if (delivery.Status == BoardAgentDeliveryStatuses.ManualPullRequired)
                await SaveDeliveriesUnsafeAsync(deliveries, cancellationToken).ConfigureAwait(false);
            return ToDelivery(delivery);
        }
        finally
        {
            stateGate.Release();
            RaisePendingChanged();
        }
    }

    public async Task SetDeliveryPolicyAsync(string policy, CancellationToken cancellationToken = default)
    {
        if (policy is not ("confirm" or "always")) throw new ArgumentOutOfRangeException(nameof(policy));
        await stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PersistedState state = await LoadStateUnsafeAsync(cancellationToken).ConfigureAwait(false);
            state.Policy = policy;
            await SaveStateUnsafeAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            stateGate.Release();
        }
    }

    public async Task<string> GetDeliveryPolicyAsync(CancellationToken cancellationToken = default)
    {
        await stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PersistedState state = await LoadStateUnsafeAsync(cancellationToken).ConfigureAwait(false);
            return state.Policy;
        }
        finally
        {
            stateGate.Release();
            RaisePendingChanged();
        }
    }

    public Task SetManualHostPathAsync(string host, string path, CancellationToken cancellationToken = default) =>
        SetHostPathAsync(host, path, "manual", cancellationToken);

    public Task ConfirmHostPathAsync(string host, string path, CancellationToken cancellationToken = default) =>
        SetHostPathAsync(host, path, "confirmed-common-root", cancellationToken);

    public async Task SetHostPathAsync(string host, string path, string detectedVia, CancellationToken cancellationToken = default)
    {
        host = NormalizeHost(host);
        if (!HostIds.Contains(host, StringComparer.Ordinal)) throw new ArgumentException("Unknown host.", nameof(host));
        if (host == "internal") throw new ArgumentException("The internal host does not use an executable path.", nameof(host));
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("The selected host path does not exist.", fullPath);
        if (IsShellShim(fullPath)) throw new ArgumentException("Select the native executable or the host's JavaScript entry, not a command shim.", nameof(path));

        BoardAgentHostResolution resolution = CreateManualResolution(host, fullPath, detectedVia);
        await stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PersistedState state = await LoadStateUnsafeAsync(cancellationToken).ConfigureAwait(false);
            PersistedHost configuration = state.Hosts.GetValueOrDefault(host) ?? new PersistedHost();
            configuration.ManualPath = fullPath;
            SaveResolution(configuration, resolution);
            configuration.DetectedVia = detectedVia;
            state.Hosts[host] = configuration;
            resolutions[host] = ApplyHostSettings(resolution, configuration);
            discoveryStampUtc = DateTime.MinValue;
            await SaveStateUnsafeAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            stateGate.Release();
        }
    }

    public BoardAgentLaunchTarget? ResolveLaunch(string host, string projectRoot)
    {
        host = NormalizeHost(host);
        if (host == "internal") return null;
        string fullRoot = NormalizeProjectRoot(projectRoot);
        if (!resolutions.TryGetValue(host, out BoardAgentHostResolution? resolution)) return null;
        if (!resolution.Enabled) return null;
        if (!string.IsNullOrWhiteSpace(resolution.UriScheme))
            return new BoardAgentLaunchTarget("uri", null, $"{resolution.UriScheme}://", fullRoot);
        return resolution.Mode == "app" && File.Exists(resolution.ExecutablePath)
            ? new BoardAgentLaunchTarget("executable", resolution.ExecutablePath, null, fullRoot)
            : null;
    }

    public async Task DrainAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            await stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            Task waitTask;
            try
            {
                if (pendingJobs == 0) return;
                waitTask = idle.Task;
            }
            finally { stateGate.Release(); }
            await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        queue.Writer.TryComplete();
        shutdown.Cancel();
        try { await worker.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        shutdown.Dispose();
        stateGate.Dispose();
    }

    private static BoardAgentService CreateDefault()
    {
        string appDataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ToolBox");
#if DEBUG
        string? testRoot = Environment.GetEnvironmentVariable("TOOLBOX_TASK_TEST_ROOT");
        if (!string.IsNullOrWhiteSpace(testRoot)) appDataDirectory = Path.Combine(testRoot, "ToolBox");
#endif
        return new BoardAgentService(new BoardAgentServiceOptions
        {
            AppDataDirectory = appDataDirectory,
            InternalEnabled = () => InternalBoardAgent.Instance.IsConfigured,
            InternalExecutor = InternalBoardAgent.Instance.ExecuteAsync
        });
    }

    private async Task WorkerAsync()
    {
        await foreach (string key in queue.Reader.ReadAllAsync(shutdown.Token).ConfigureAwait(false))
        {
            try { await ProcessDeliveryAsync(key, shutdown.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
            catch (Exception exception)
            {
                try { await SetDeliveryErrorAsync(key, BoardAgentDeliveryStatuses.Failed, LimitUtf8(exception.Message, 2048)).ConfigureAwait(false); }
                catch (Exception persistException) { Debug.WriteLine($"[ToolBox] Failed to persist pre-send delivery error: {persistException.Message}"); }
            }
            finally { await FinishQueueItemAsync(key).ConfigureAwait(false); }
        }
    }

    private async Task ProcessDeliveryAsync(string key, CancellationToken cancellationToken)
    {
        PersistedDelivery? delivery;
        BoardAgentHostResolution resolution;
        await stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PersistedState state = await LoadStateUnsafeAsync(cancellationToken).ConfigureAwait(false);
            List<PersistedDelivery> deliveries = await LoadDeliveriesUnsafeAsync(cancellationToken).ConfigureAwait(false);
            delivery = deliveries.SingleOrDefault(d => d.ConfirmationHash == key);
            if (delivery is null || delivery.Status != BoardAgentDeliveryStatuses.Queued) return;
            resolution = ResolveHost(delivery.Host, state, false, out bool changed);
            resolutions[delivery.Host] = resolution;
            if (changed) await SaveStateUnsafeAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally { stateGate.Release(); RaisePendingChanged(); }

        if (delivery.Session is null)
        {
            await SetDeliveryErrorAsync(key, BoardAgentDeliveryStatuses.ManualPullRequired, null).ConfigureAwait(false);
            return;
        }
        if (resolution.Mode == "internal")
        {
            await ProcessInternalDeliveryAsync(key, delivery, resolution, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (resolution.Mode != "cli" || !resolution.SupportsSession || !File.Exists(resolution.ExecutablePath) ||
            (resolution.EntryPath is not null && !File.Exists(resolution.EntryPath)))
        {
            await SetDeliveryErrorAsync(key, BoardAgentDeliveryStatuses.Failed, "The host CLI is unavailable. Use host inbox pull or choose a valid executable.").ConfigureAwait(false);
            return;
        }

        BoardAgentProcessSpec processSpec = BuildProcessSpec(delivery, resolution);
        if (!await TrySetSendingAsync(key).ConfigureAwait(false)) return;
        BoardAgentProcessResult result;
        try { result = await processRunner.ExecuteAsync(processSpec, TimeSpan.FromSeconds(resolution.TimeoutSeconds), cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            await MarkUnknownBestEffortAsync(key, "Host process failed after sending began; delivery may have occurred. " + exception.Message).ConfigureAwait(false);
            return;
        }

        string finalStatus = result.TimedOut
            ? BoardAgentDeliveryStatuses.Unknown
            : result.ExitCode == 0 ? BoardAgentDeliveryStatuses.Submitted : BoardAgentDeliveryStatuses.Failed;
        string? finalError = result.TimedOut
            ? LimitUtf8("Host process timed out; delivery may have occurred. " + result.ErrorSummary, 2048)
            : result.ExitCode == 0 ? null : LimitUtf8(result.ErrorSummary, 2048);
        try { await SetDeliveryErrorAsync(key, finalStatus, finalError).ConfigureAwait(false); }
        catch (Exception exception)
        {
            await MarkUnknownBestEffortAsync(key, "Could not persist the host result; delivery may have occurred. " + exception.Message).ConfigureAwait(false);
        }
    }

    private async Task ProcessInternalDeliveryAsync(
        string key,
        PersistedDelivery delivery,
        BoardAgentHostResolution resolution,
        CancellationToken cancellationToken)
    {
        Func<BoardAgentCommentContext, TimeSpan, CancellationToken, Task<BoardAgentProcessResult>>? executeAsync = internalExecutor;
        if (!resolution.Enabled || executeAsync is null || !IsInternalEnabled())
        {
            await SetDeliveryErrorAsync(key, BoardAgentDeliveryStatuses.Failed, "The internal assistant is unavailable.").ConfigureAwait(false);
            return;
        }
        if (!await TrySetSendingAsync(key).ConfigureAwait(false)) return;

        TimeSpan timeout = TimeSpan.FromSeconds(resolution.TimeoutSeconds);
        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);
        BoardAgentProcessResult result;
        try
        {
            result = await executeAsync(new BoardAgentCommentContext(
                delivery.CommentId, delivery.CardId, delivery.ProjectRoot, delivery.Host, delivery.Session, delivery.Body),
                timeout,
                linkedCancellation.Token).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
        {
            await MarkUnknownBestEffortAsync(key, "Internal assistant timed out; its board side effects may have occurred.").ConfigureAwait(false);
            return;
        }
        catch (TimeoutException)
        {
            timeoutCancellation.Cancel();
            await MarkUnknownBestEffortAsync(key, "Internal assistant timed out; its board side effects may have occurred.").ConfigureAwait(false);
            return;
        }
        catch (Exception exception)
        {
            await MarkUnknownBestEffortAsync(key, "Internal assistant failed after execution began; its board side effects may have occurred. " + exception.Message).ConfigureAwait(false);
            return;
        }

        string finalStatus = result.TimedOut
            ? BoardAgentDeliveryStatuses.Unknown
            : result.ExitCode == 0 ? BoardAgentDeliveryStatuses.Submitted : BoardAgentDeliveryStatuses.Failed;
        string? finalError = result.TimedOut
            ? LimitUtf8("Internal assistant timed out; its board side effects may have occurred. " + result.ErrorSummary, 2048)
            : result.ExitCode == 0 ? null : LimitUtf8(result.ErrorSummary, 2048);
        try { await SetDeliveryErrorAsync(key, finalStatus, finalError).ConfigureAwait(false); }
        catch (Exception exception)
        {
            await MarkUnknownBestEffortAsync(key, "Could not persist the internal assistant result; side effects may have occurred. " + exception.Message).ConfigureAwait(false);
        }
    }

    private async Task MarkUnknownBestEffortAsync(string key, string error)
    {
        try { await SetDeliveryErrorAsync(key, BoardAgentDeliveryStatuses.Unknown, LimitUtf8(error, 2048)).ConfigureAwait(false); }
        catch (Exception exception) { Debug.WriteLine($"[ToolBox] Delivery outcome remains uncertain: {exception.Message}"); }
    }

    private async Task SetDeliveryErrorAsync(string key, string status, string? error)
    {
        await stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            PersistedState state = await LoadStateUnsafeAsync(CancellationToken.None).ConfigureAwait(false);
            List<PersistedDelivery> deliveries = await LoadDeliveriesUnsafeAsync(CancellationToken.None).ConfigureAwait(false);
            PersistedDelivery? delivery = deliveries.SingleOrDefault(d => d.ConfirmationHash == key);
            if (delivery is null) return;
            if (delivery.Status == BoardAgentDeliveryStatuses.Acknowledged && status != BoardAgentDeliveryStatuses.Acknowledged) return;
            delivery.Status = status;
            delivery.Error = error;
            if (status is not (BoardAgentDeliveryStatuses.Queued or BoardAgentDeliveryStatuses.Sending)) delivery.Body = string.Empty;
            delivery.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveDeliveriesUnsafeAsync(deliveries, CancellationToken.None).ConfigureAwait(false);
        }
        finally { stateGate.Release(); RaisePendingChanged(); }
    }

    private async Task<bool> TrySetSendingAsync(string key)
    {
        await stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            List<PersistedDelivery> deliveries = await LoadDeliveriesUnsafeAsync(CancellationToken.None).ConfigureAwait(false);
            PersistedDelivery? delivery = deliveries.SingleOrDefault(d => d.ConfirmationHash == key);
            if (delivery is null || delivery.Status != BoardAgentDeliveryStatuses.Queued) return false;
            delivery.Status = BoardAgentDeliveryStatuses.Sending;
            delivery.Error = null;
            delivery.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveDeliveriesUnsafeAsync(deliveries, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        finally { stateGate.Release(); RaisePendingChanged(); }
    }

    private bool QueueUnsafe(string key)
    {
        if (!queuedKeys.Add(key)) return true;
        if (pendingJobs++ == 0) idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (queue.Writer.TryWrite(key)) return true;
        queuedKeys.Remove(key);
        if (--pendingJobs == 0) idle.TrySetResult();
        return false;
    }

    private async Task FinishQueueItemAsync(string key)
    {
        await stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            queuedKeys.Remove(key);
            if (--pendingJobs == 0) idle.TrySetResult();
        }
        finally { stateGate.Release(); }
    }

    private async Task<PersistedState> LoadStateUnsafeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(appDataDirectory);
        DateTime fileWriteUtc = File.Exists(statePath) ? File.GetLastWriteTimeUtc(statePath) : DateTime.MinValue;
        if (state is not null && stateFileObserved && fileWriteUtc == stateLastWriteUtc) return state;
        if (!File.Exists(statePath)) state = new PersistedState();
        else
        {
            string json = await File.ReadAllTextAsync(statePath, cancellationToken).ConfigureAwait(false);
            state = JsonSerializer.Deserialize<PersistedState>(json, JsonOptions) ?? throw new InvalidDataException("Board agent configuration is empty.");
        }
        stateFileObserved = true;
        stateLastWriteUtc = fileWriteUtc;
        resolutions.Clear();

        return state;
    }

    private async Task<List<PersistedDelivery>> LoadDeliveriesUnsafeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(appDataDirectory);
        DateTime fileWriteUtc = File.Exists(deliveryPath) ? File.GetLastWriteTimeUtc(deliveryPath) : DateTime.MinValue;
        if (deliveries is not null && deliveryFileObserved && fileWriteUtc == deliveryLastWriteUtc) return deliveries;
        if (!File.Exists(deliveryPath)) deliveries = [];
        else
        {
            string json = await File.ReadAllTextAsync(deliveryPath, cancellationToken).ConfigureAwait(false);
            deliveries = JsonSerializer.Deserialize<List<PersistedDelivery>>(json, JsonOptions) ?? throw new InvalidDataException("Board agent delivery records are empty.");
        }
        deliveryFileObserved = true;
        deliveryLastWriteUtc = fileWriteUtc;
        bool changed = false;
        foreach (PersistedDelivery delivery in deliveries)
        {
            if (delivery.Status == BoardAgentDeliveryStatuses.Sending)
            {
                delivery.Status = BoardAgentDeliveryStatuses.Unknown;
                delivery.Body = string.Empty;
                delivery.Error = "The application stopped while delivery was in progress; it was not replayed.";
                delivery.UpdatedAt = DateTimeOffset.UtcNow;
                changed = true;
            }
            else if (delivery.Status == BoardAgentDeliveryStatuses.Queued && !QueueUnsafe(delivery.ConfirmationHash))
            {
                delivery.Status = BoardAgentDeliveryStatuses.Failed;
                delivery.Error = "The local delivery queue is full.";
                delivery.UpdatedAt = DateTimeOffset.UtcNow;
                changed = true;
            }
        }
        if (changed) await SaveDeliveriesUnsafeAsync(deliveries, cancellationToken).ConfigureAwait(false);
        return deliveries;
    }

    private async Task SaveStateUnsafeAsync(PersistedState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(appDataDirectory);
        string temporaryPath = statePath + ".tmp";
        string json = JsonSerializer.Serialize(state, JsonOptions);
        await File.WriteAllTextAsync(temporaryPath, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(temporaryPath, statePath, true);
        stateLastWriteUtc = File.GetLastWriteTimeUtc(statePath);
        stateFileObserved = true;
    }

    private async Task SaveDeliveriesUnsafeAsync(List<PersistedDelivery> deliveries, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(appDataDirectory);
        string temporaryPath = deliveryPath + ".tmp";
        string json = JsonSerializer.Serialize(deliveries, JsonOptions);
        Interlocked.Exchange(ref deliveryChangePending, 1);
        await File.WriteAllTextAsync(temporaryPath, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(temporaryPath, deliveryPath, true);
        deliveryLastWriteUtc = File.GetLastWriteTimeUtc(deliveryPath);
        deliveryFileObserved = true;
    }

    private BoardAgentService RaisePendingChanged()
    {
        if (Interlocked.Exchange(ref deliveryChangePending, 0) == 0) return this;
        Action? handlers = Changed;
        if (handlers is null) return this;
        foreach (Action handler in handlers.GetInvocationList().Cast<Action>())
        {
            try { handler(); }
            catch (Exception exception) { Debug.WriteLine($"[ToolBox] Board agent change notification failed: {exception.Message}"); }
        }
        return this;
    }

    private BoardAgentHostResolution ResolveHost(string host, PersistedState state, bool refresh, out bool configChanged)
    {
        configChanged = false;
        if (host == "internal") return CreateInternalResolution();
        PersistedHost configuration = state.Hosts.GetValueOrDefault(host) ?? new PersistedHost();
        if (!configuration.Enabled)
            return ApplyHostSettings(new BoardAgentHostResolution(host, "none", null, null, "disabled", UriScheme(host), false, true), configuration);

        if (!string.IsNullOrWhiteSpace(configuration.ManualPath))
        {
            BoardAgentHostResolution? manual = ResolveManual(host, configuration.ManualPath!, configuration.DetectedVia ?? "manual");
            if (manual is not null) return ApplyHostSettings(manual, configuration);
            configuration.ManualPath = null;
            configuration.ResolvedPath = null;
            configuration.EntryPath = null;
            configuration.Mode = null;
            configuration.SupportsSession = false;
            configChanged = true;
        }

        if (!refresh && IsValidResolution(configuration.ResolvedPath, configuration.EntryPath))
        {
            BoardAgentHostResolution saved = FromPersisted(host, configuration);
            if (host != "qoder" || !TryFindLatestQoderVersionNear(saved.ExecutablePath, out string? newest) ||
                string.Equals(newest, saved.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                return ApplyHostSettings(saved, configuration);
        }
        if (configuration.ResolvedPath is not null || configuration.EntryPath is not null)
        {
            configuration.ResolvedPath = null;
            configuration.EntryPath = null;
            configuration.Mode = null;
            configuration.SupportsSession = false;
            configuration.DetectedVia = null;
            configChanged = true;
        }

        if (TryFindCli(host, out string? executable, out string? entryPath, out string via))
        {
            var cli = new BoardAgentHostResolution(host, "cli", executable, entryPath, via, UriScheme(host), true, true);
            SaveResolution(configuration, cli);
            state.Hosts[host] = configuration;
            configChanged = true;
            return ApplyHostSettings(cli, configuration);
        }

        foreach (string scheme in GetSchemes(host))
        {
            string? registeredExe = platform.GetUrlProtocolCommands(scheme).Select(ExtractExecutable).FirstOrDefault(p => p is not null && File.Exists(p));
            if (registeredExe is not null)
            {
                if (host == "qoder" && TryFindLatestQoderVersionNear(registeredExe, out string? newest)) registeredExe = newest;
                BoardAgentHostResolution app = CreateAppResolution(host, registeredExe!, "uri-registry", scheme);
                SaveResolution(configuration, app);
                state.Hosts[host] = configuration;
                configChanged = true;
                return ApplyHostSettings(app, configuration);
            }
        }

        var installedCandidate = platform.GetUninstallEntries()
            .Where(a => MatchesHost(host, a.DisplayName))
            .Select(a => (App: a, Exe: ExtractExecutable(a.DisplayIcon) ?? FindInstalledExecutable(host, a.InstallLocation)))
            .FirstOrDefault(x => x.Exe is not null && File.Exists(x.Exe));
        string? installedExe = installedCandidate.Exe;
        if (installedExe is not null)
        {
            if (host == "qoder" && TryFindLatestQoderVersionNear(installedExe, out string? newest)) installedExe = newest;
            BoardAgentHostResolution app = CreateAppResolution(host, installedExe!, "uninstall-registry", UriScheme(host));
            SaveResolution(configuration, app);
            state.Hosts[host] = configuration;
            configChanged = true;
            return ApplyHostSettings(app, configuration);
        }

        string? candidate = FindCommonRootCandidate(host);
        if (candidate is not null)
        {
            BoardAgentHostResolution pending = CreateAppResolution(host, candidate, "common-root", UriScheme(host)) with { PendingPath = candidate };
            return ApplyHostSettings(pending, configuration);
        }

        string? availableScheme = GetSchemes(host).FirstOrDefault(s => platform.GetUrlProtocolCommands(s).Any());
        BoardAgentHostResolution fallback = availableScheme is not null
            ? new BoardAgentHostResolution(host, "app", null, null, "uri-registry", availableScheme, false, true)
            : new BoardAgentHostResolution(host, "none", null, null, "none", null, false, true);
        return ApplyHostSettings(fallback, configuration);
    }

    private bool TryFindCli(string host, out string? executable, out string? entryPath, out string detectedVia)
    {
        executable = null;
        entryPath = null;
        detectedVia = "none";
        if (host == "zcode") return false;
        string command = host switch { "codebuddy" => "codebuddy", _ => host };
        string? nodePath = FindNativeOnPath("node");
        foreach ((string candidate, string via) in FindCommands(command))
        {
            if (Path.GetExtension(candidate).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                executable = candidate;
                detectedVia = via;
                return true;
            }
            if (TryResolveShimTarget(candidate, command, nodePath, out string? shimExecutable, out string? jsEntry))
            {
                executable = shimExecutable;
                entryPath = jsEntry;
                detectedVia = via;
                return true;
            }
        }
        return false;
    }

    private IEnumerable<(string Path, string Via)> FindCommands(string command)
    {
        var directories = platform.GetPathDirectories()
            .Concat(new[] { Path.Combine(platform.GetFolderPath(Environment.SpecialFolder.ApplicationData) ?? string.Empty, "npm") })
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in directories)
        {
            foreach (string extension in new[] { ".exe", ".cmd", ".ps1" })
            {
                string candidate = Path.Combine(directory.Trim('"'), command + extension);
                if (File.Exists(candidate)) yield return (Path.GetFullPath(candidate), extension == ".exe" ? "path" : "npm-shim");
            }
        }
    }

    private string? FindNativeOnPath(string command)
    {
        foreach (string directory in platform.GetPathDirectories())
        {
            string candidate = Path.Combine(directory.Trim('"'), command + ".exe");
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }

    private bool TryResolveShimTarget(string shimPath, string command, string? nodePath, out string? executablePath, out string? entryPath)
    {
        executablePath = null;
        entryPath = null;
        if (TryReadNodeShimEntry(shimPath, command, out string? nodeEntry))
        {
            if (nodePath is not null)
            {
                executablePath = nodePath;
                entryPath = nodeEntry;
                return true;
            }
        }

        if (TryReadNativeShimTarget(shimPath, command, out string? nativeEntry))
        {
            executablePath = nativeEntry;
            return true;
        }
        return false;
    }

    private bool TryReadNativeShimTarget(string shimPath, string command, out string? executablePath)
    {
        executablePath = null;
        string text;
        try { text = File.ReadAllText(shimPath); }
        catch (IOException) { return false; }
        string directory = Path.GetDirectoryName(shimPath)!;
        text = text.Replace("%~dp0%", directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            .Replace("%dp0%", directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            .Replace("$basedir", directory, StringComparison.OrdinalIgnoreCase)
            .Replace("$PSScriptRoot", directory, StringComparison.OrdinalIgnoreCase);
        text = Environment.ExpandEnvironmentVariables(text);
        string expression = "\\\"(?<path>[^\\\"]+?\\.exe)\\\"|(?<path>(?:[A-Z]:[\\\\/])[^\\s\\\"']+?\\.exe)(?=[\\s\\\"']|$)";
        foreach (Match match in Regex.Matches(text, expression, RegexOptions.IgnoreCase))
        {
            string raw = match.Groups["path"].Value.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            string candidate;
            try { candidate = Path.GetFullPath(Path.IsPathRooted(raw) ? raw : Path.Combine(directory, raw)); }
            catch (ArgumentException) { continue; }
            catch (NotSupportedException) { continue; }
            if (!Path.GetFileName(candidate).Equals(command + ".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate)) continue;
            executablePath = candidate;
            return true;
        }
        return false;
    }

    private bool TryReadNodeShimEntry(string shimPath, string command, out string? entryPath)
    {
        entryPath = null;
        string text;
        try { text = File.ReadAllText(shimPath); }
        catch (IOException) { return false; }
        string directory = Path.GetDirectoryName(shimPath)!;
        text = text.Replace("%~dp0%", directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            .Replace("%dp0%", directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            .Replace("$basedir", directory, StringComparison.OrdinalIgnoreCase)
            .Replace("$PSScriptRoot", directory, StringComparison.OrdinalIgnoreCase);
        text = Environment.ExpandEnvironmentVariables(text);
        Match quoted = Regex.Match(text, "\"(?<path>[^\"]*node_modules[\\\\/][^\"]+?\\.(?:mjs|cjs|js))\"", RegexOptions.IgnoreCase);
        Match unquoted = Regex.Match(text, "(?<path>(?:[A-Z]:[\\\\/])?[^\\s\"']*node_modules[\\\\/][^\\s\"']+?\\.(?:mjs|cjs|js))", RegexOptions.IgnoreCase);
        string? raw = quoted.Success ? quoted.Groups["path"].Value : unquoted.Success ? unquoted.Groups["path"].Value : null;
        if (raw is null) return TryReadExtensionlessNodeShimEntry(shimPath, command, text, out entryPath);
        try
        {
            string expanded = Path.IsPathRooted(raw) ? raw : Path.Combine(directory, raw);
            expanded = Path.GetFullPath(expanded);
            if (!File.Exists(expanded)) return false;
            entryPath = expanded;
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    private static bool TryReadExtensionlessNodeShimEntry(string shimPath, string command, string text, out string? entryPath)
    {
        entryPath = null;
        if (!Path.GetExtension(shimPath).Equals(".ps1", StringComparison.OrdinalIgnoreCase)) return false;

        Match invocation = Regex.Match(
            text,
            "(?:^\\s*|\\|\\s*)&\\s*\"(?<node>(?:[^\"]*[\\\\/])?node(?:\\.exe|\\$exe)?)\"\\s+\"(?<entry>[^\"]+)\"(?=\\s|$)",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        if (!invocation.Success) return false;

        string raw = invocation.Groups["entry"].Value.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        try
        {
            string candidate = Path.GetFullPath(Path.IsPathRooted(raw) ? raw : Path.Combine(Path.GetDirectoryName(shimPath)!, raw));
            if (Path.GetExtension(candidate).Length != 0 ||
                !Path.GetFileName(candidate).Equals(command, StringComparison.OrdinalIgnoreCase) ||
                !candidate.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(segment => segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) ||
                !File.Exists(candidate) || !HasNodeShebang(candidate))
                return false;

            entryPath = candidate;
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
        catch (PathTooLongException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool HasNodeShebang(string path)
    {
        try
        {
            using var reader = File.OpenText(path);
            string? firstLine = reader.ReadLine();
            if (firstLine?.StartsWith('\uFEFF') == true) firstLine = firstLine[1..];
            return firstLine == "#!/usr/bin/env node";
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private BoardAgentHostResolution CreateAppResolution(string host, string exePath, string via, string? scheme)
    {
        if (host == "zcode")
        {
            string root = Path.GetDirectoryName(exePath)!;
            string entry = Path.Combine(root, "resources", "glm", "zcode.cjs");
            string? node = FindNativeOnPath("node");
            if (node is not null && File.Exists(entry))
                return new BoardAgentHostResolution(host, "cli", node, Path.GetFullPath(entry), via, scheme, true, true);
        }
        return new BoardAgentHostResolution(host, "app", Path.GetFullPath(exePath), null, via, scheme, false, true);
    }

    private BoardAgentHostResolution CreateInternalResolution() => new(
        "internal", "internal", null, null, "assistant", null, true, true,
        Enabled: internalExecutor is not null && IsInternalEnabled(),
        TimeoutSeconds: Math.Clamp((int)Math.Ceiling(defaultTimeout.TotalSeconds), 1, MAXIMUM_HOST_TIMEOUT_SECONDS));

    private bool IsInternalEnabled()
    {
        Func<bool>? isEnabled = internalEnabled;
        if (isEnabled is null || internalExecutor is null) return false;
        try { return isEnabled(); }
        catch (Exception exception)
        {
            Debug.WriteLine($"[ToolBox] Internal assistant availability check failed: {exception.Message}");
            return false;
        }
    }

    private BoardAgentHostResolution? ResolveManual(string host, string path, string via)
    {
        if (!File.Exists(path) || IsShellShim(path)) return null;
        string extension = Path.GetExtension(path);
        if (extension.Equals(".js", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cjs", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".mjs", StringComparison.OrdinalIgnoreCase))
        {
            string? node = FindNativeOnPath("node");
            return host == "zcode" && node is not null
                ? new BoardAgentHostResolution(host, "cli", node, path, via, UriScheme(host), true, true)
                : null;
        }
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
            ((host is "codex" or "claude" or "codebuddy" or "opencode") ||
             (host == "qoder" && Path.GetFileName(path).Equals("qoder.exe", StringComparison.OrdinalIgnoreCase))))
            return new BoardAgentHostResolution(host, "cli", Path.GetFullPath(path), null, via, UriScheme(host), true, true);
        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            ? CreateAppResolution(host, path, via, UriScheme(host))
            : null;
    }

    private BoardAgentHostResolution CreateManualResolution(string host, string path, string via) =>
        ResolveManual(host, path, via) ?? throw new ArgumentException("The selected path cannot be used by this host.", nameof(path));

    private static BoardAgentHostResolution FromPersisted(string host, PersistedHost config)
    {
        bool inferredCli = config.EntryPath is not null ||
            ((config.DetectedVia is "path" or "npm-shim") && (host is "codex" or "claude" or "codebuddy" or "opencode" or "qoder"));
        string mode = config.Mode ?? (inferredCli ? "cli" : "app");
        return new BoardAgentHostResolution(
            host, mode, config.ResolvedPath, config.EntryPath, config.DetectedVia ?? "config",
            config.UriScheme ?? UriScheme(host), config.Mode is null ? mode == "cli" : config.SupportsSession, true);
    }

    private BoardAgentHostResolution ApplyHostSettings(BoardAgentHostResolution resolution, PersistedHost configuration)
    {
        if (!configuration.Enabled)
        {
            return resolution with
            {
                Mode = "none",
                ExecutablePath = null,
                EntryPath = null,
                DetectedVia = "disabled",
                UriScheme = null,
                SupportsSession = false,
                PendingPath = null,
                Enabled = false,
                TimeoutSeconds = configuration.TimeoutSeconds ?? (int)Math.Ceiling(defaultTimeout.TotalSeconds),
                ArgumentTemplate = configuration.ArgumentTemplate,
                MessageTransport = configuration.MessageTransport
            };
        }

        return resolution with
        {
            Enabled = true,
            TimeoutSeconds = configuration.TimeoutSeconds ?? (int)Math.Ceiling(defaultTimeout.TotalSeconds),
            ArgumentTemplate = configuration.ArgumentTemplate,
            MessageTransport = configuration.MessageTransport
        };
    }

    private static PersistedHost SaveResolution(PersistedHost config, BoardAgentHostResolution resolution)
    {
        config.ResolvedPath = resolution.ExecutablePath;
        config.EntryPath = resolution.EntryPath;
        config.Mode = resolution.Mode;
        config.SupportsSession = resolution.SupportsSession;
        config.DetectedVia = resolution.DetectedVia;
        config.UriScheme = resolution.UriScheme;
        return config;
    }

    private static bool IsValidResolution(string? executable, string? entry) =>
        !string.IsNullOrWhiteSpace(executable) && File.Exists(executable) && (entry is null || File.Exists(entry));

    private bool TryFindLatestQoderVersionNear(string? existingPath, out string? executable)
    {
        executable = null;
        if (string.IsNullOrWhiteSpace(existingPath)) return false;
        DirectoryInfo? versionDirectory = Directory.GetParent(existingPath);
        DirectoryInfo? versionsDirectory = versionDirectory?.Parent;
        if (versionsDirectory is null || !string.Equals(versionsDirectory.Name, ".qoder-versions", StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(versionsDirectory.FullName)) return false;
        Version? selectedVersion = null;
        foreach (string directory in Directory.EnumerateDirectories(versionsDirectory.FullName))
        {
            if (Version.TryParse(Path.GetFileName(directory), out Version? version))
            {
                string candidate = Path.Combine(directory, "Qoder CN.exe");
                if (File.Exists(candidate) && (selectedVersion is null || version > selectedVersion))
                {
                    selectedVersion = version;
                    executable = Path.GetFullPath(candidate);
                }
            }
        }
        return executable is not null;
    }

    private string? FindCommonRootCandidate(string host)
    {
        foreach (string root in platform.GetCommonRoots())
        {
            string? candidate = host switch
            {
                "zcode" => Existing(Path.Combine(root, "ZCode", "ZCode.exe")),
                "qoder" => FindQoderUnder(Path.Combine(root, "Qoder CN")),
                "trae" => Existing(Path.Combine(root, "Trae CN", "Trae CN.exe")) ?? Existing(Path.Combine(root, "Trae SOLO CN", "Trae SOLO CN.exe")),
                "workbuddy" => Existing(Path.Combine(root, "WorkBuddy", "WorkBuddy.exe")),
                _ => null
            };
            if (candidate is not null) return Path.GetFullPath(candidate);
        }
        return null;
    }

    private static string? FindInstalledExecutable(string host, string? installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation) || !Directory.Exists(installLocation)) return null;
        string root = Path.GetFullPath(installLocation);
        string? candidate = host switch
        {
            "zcode" => Existing(Path.Combine(root, "ZCode.exe")) ?? Existing(Path.Combine(root, "ZCode", "ZCode.exe")),
            "qoder" => FindQoderUnder(root),
            "trae" => Existing(Path.Combine(root, "Trae CN.exe")) ?? Existing(Path.Combine(root, "Trae.exe")),
            "workbuddy" => Existing(Path.Combine(root, "WorkBuddy.exe")),
            _ => null
        };
        return candidate is null ? null : Path.GetFullPath(candidate);
    }

    private static string? FindQoderUnder(string root)
    {
        if (!Directory.Exists(root)) return null;
        var paths = Directory.Exists(Path.Combine(root, ".qoder-versions"))
            ? Directory.EnumerateDirectories(Path.Combine(root, ".qoder-versions"))
                .Select(d => (Dir: d, Version: Version.TryParse(Path.GetFileName(d), out Version? v) ? v : null))
                .Where(x => x.Version is not null)
                .OrderByDescending(x => x.Version)
                .Select(x => Path.Combine(x.Dir, "Qoder CN.exe"))
            : [];
        return paths.FirstOrDefault(File.Exists) ?? Existing(Path.Combine(root, "Qoder CN.exe"));
    }

    private static string? ExtractExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        string expanded = Environment.ExpandEnvironmentVariables(command.Trim());
        Match quoted = Regex.Match(expanded, "\"(?<path>[^\"]+?\\.exe)\"", RegexOptions.IgnoreCase);
        Match unquoted = Regex.Match(expanded, "(?<path>[^\\s,]+?\\.exe)(?:[,\\s]|$)", RegexOptions.IgnoreCase);
        string? path = quoted.Success ? quoted.Groups["path"].Value : unquoted.Success ? unquoted.Groups["path"].Value : null;
        if (path is null) return null;
        try { return Path.GetFullPath(path); }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    private static bool MatchesHost(string host, string displayName)
    {
        string name = displayName.ToLowerInvariant();
        return host switch
        {
            "zcode" => name.Contains("zcode"),
            "qoder" => name.Contains("qoder") && !name.Contains("cli"),
            "trae" => name.Contains("trae"),
            "workbuddy" => name.Contains("workbuddy"),
            _ => false
        };
    }

    private static string? Existing(string path) => File.Exists(path) ? path : null;

    private static string? UriScheme(string host) => host switch
    {
        "qoder" => "qoder",
        "trae" => "trae-cn",
        "workbuddy" => "workbuddy",
        "zcode" => "zcode",
        _ => null
    };

    private static IEnumerable<string> GetSchemes(string host)
    {
        string? scheme = UriScheme(host);
        if (scheme is not null) yield return scheme;
    }

    private BoardAgentHostInfo ToHostInfo(BoardAgentHostResolution resolution) => new(
        resolution.Host,
        resolution.Host switch
        {
            "codex" => "Codex", "claude" => "Claude Code", "codebuddy" => "CodeBuddy", "opencode" => "OpenCode",
            "zcode" => "ZCode", "qoder" => "Qoder", "trae" => "Trae", "workbuddy" => "WorkBuddy",
            "internal" => "ToolBox Assistant", _ => resolution.Host
        },
        resolution.Mode,
        resolution.ExecutablePath,
        resolution.EntryPath,
        resolution.DetectedVia,
        resolution.Enabled && resolution.Mode != "none" && resolution.PendingPath is null,
        resolution.PendingPath is not null,
        resolution.PendingPath,
        resolution.SupportsSession,
        resolution.InboxPull,
        resolution.UriScheme);

    private static BoardAgentProcessSpec BuildProcessSpec(PersistedDelivery delivery, BoardAgentHostResolution resolution)
    {
        if (delivery.Session is null || delivery.Session.StartsWith("-", StringComparison.Ordinal))
            throw new InvalidOperationException("An exact session id is required for host delivery.");
        if (resolution.TimeoutSeconds is < 1 or > MAXIMUM_HOST_TIMEOUT_SECONDS)
            throw new InvalidOperationException($"Host timeout must be between 1 and {MAXIMUM_HOST_TIMEOUT_SECONDS} seconds.");

        string[] builtInTemplate = delivery.Host switch
        {
            "codex" => ["exec", "resume", "{session}", "-"],
            "claude" or "codebuddy" => ["--resume", "{session}", "-p"],
            "opencode" => ["run", "-s", "{session}"],
            "zcode" => ["--prompt={message}", "--resume", "{session}"],
            "qoder" => ["--resume", "{session}", "--print"],
            _ => throw new InvalidOperationException("The host has no exact-session CLI adapter.")
        };
        string transport = resolution.MessageTransport ?? (delivery.Host == "zcode" ? "argument" : "stdin");
        string[] template = resolution.ArgumentTemplate ?? builtInTemplate;
        ValidateArgumentTemplate(template, transport);

        var arguments = new List<string>();
        if (resolution.EntryPath is not null) arguments.Add(resolution.EntryPath);
        foreach (string argument in template)
        {
            arguments.Add(Regex.Replace(argument, @"\{([^{}]*)\}", match => match.Groups[1].Value switch
            {
                "session" => delivery.Session!,
                "message" => delivery.Body,
                "project" => delivery.ProjectRoot,
                _ => throw new InvalidOperationException("Host argument template contains an unsupported token.")
            }));
        }
        if (arguments.Sum(argument => argument.Length) > 30000)
            throw new InvalidOperationException("Host argument template expands beyond the supported command line length.");
        string? standardInput = transport == "stdin" ? delivery.Body : null;
        return new BoardAgentProcessSpec(resolution.ExecutablePath!, arguments, delivery.ProjectRoot, standardInput);
    }

    private static string[] ValidateArgumentTemplate(string[] template, string transport)
    {
        if (transport is not ("stdin" or "argument"))
            throw new InvalidOperationException("MessageTransport must be 'stdin' or 'argument'.");
        if (template.Length is < 1 or > 128)
            throw new InvalidOperationException("ArgumentTemplate must contain between 1 and 128 arguments.");

        int sessionCount = 0;
        int messageCount = 0;
        foreach (string? argument in template)
        {
            if (string.IsNullOrWhiteSpace(argument) || argument.Length > 4096 || argument.Any(char.IsControl))
                throw new InvalidOperationException("ArgumentTemplate contains an empty, oversized, or control-character argument.");

            MatchCollection tokens = Regex.Matches(argument, @"\{([^{}]*)\}");
            foreach (Match token in tokens)
            {
                switch (token.Groups[1].Value)
                {
                    case "session": sessionCount++; break;
                    case "message": messageCount++; break;
                    case "project": break;
                    default: throw new InvalidOperationException("ArgumentTemplate contains an unsupported token.");
                }
            }
            string withoutTokens = Regex.Replace(argument, @"\{[^{}]*\}", string.Empty);
            if (withoutTokens.Contains('{') || withoutTokens.Contains('}'))
                throw new InvalidOperationException("ArgumentTemplate contains an unmatched token brace.");

            if (argument.Contains("{message}", StringComparison.Ordinal))
            {
                int marker = argument.IndexOf("{message}", StringComparison.Ordinal);
                int equals = argument.LastIndexOf('=', marker);
                if (transport != "argument" || !argument.StartsWith("--", StringComparison.Ordinal) || equals < 2)
                    throw new InvalidOperationException("An argument prompt must be bound to an option value, such as --prompt={message}.");
            }
        }

        if (sessionCount == 0) throw new InvalidOperationException("ArgumentTemplate must bind the exact {session} token.");
        if (transport == "argument" && messageCount != 1)
            throw new InvalidOperationException("Argument transport requires exactly one safely bound {message} token.");
        if (transport == "stdin" && messageCount != 0)
            throw new InvalidOperationException("Stdin transport must not include a {message} token.");
        return template;
    }

    private static BoardAgentCommentContext ValidateContext(BoardAgentCommentContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        string commentId = Required(context.CommentId, nameof(context.CommentId));
        string cardId = Required(context.CardId, nameof(context.CardId));
        string host = NormalizeHost(context.Host);
        if (!HostIds.Contains(host, StringComparer.Ordinal)) throw new ArgumentException("Unknown agent host.", nameof(context.Host));
        string projectRoot = NormalizeProjectRoot(context.ProjectRoot);
        if (!Directory.Exists(projectRoot)) throw new DirectoryNotFoundException(projectRoot);
        if (Encoding.UTF8.GetByteCount(context.Body ?? string.Empty) > MAX_BODY_BYTES) throw new ArgumentException("Comment body exceeds 64 KiB.", nameof(context.Body));
        if (context.Session is not null && (string.IsNullOrWhiteSpace(context.Session) || context.Session.Length > 256 ||
            context.Session.Any(char.IsControl) || context.Session.StartsWith("-", StringComparison.Ordinal)))
            throw new ArgumentException("Session id is invalid.", nameof(context.Session));
        return context with { CommentId = commentId, CardId = cardId, ProjectRoot = projectRoot, Host = host, Body = context.Body ?? string.Empty };
    }

    private static string Required(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl)) throw new ArgumentException("A valid value is required.", field);
        return value;
    }

    private static string NormalizeHost(string host) => (host ?? string.Empty).Trim().ToLowerInvariant();

    private static string NormalizeProjectRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Project root is required.", nameof(path));
        string fullPath = Path.GetFullPath(path);
        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private static bool FixedEquals(string? left, string right)
    {
        if (left is null || left.Length != right.Length) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));
    }

    private static bool IsShellShim(string path) => Path.GetExtension(path) is string extension &&
        (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) || extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase));

    private static string LimitUtf8(string value, int byteLimit)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        if (bytes.Length <= byteLimit) return value ?? string.Empty;
        int length = byteLimit;
        while (length > 0 && (bytes[length] & 0xC0) == 0x80) length--;
        return Encoding.UTF8.GetString(bytes, 0, length);
    }

    private static BoardAgentDelivery ToDelivery(PersistedDelivery delivery) => new(
        delivery.CommentId, delivery.CardId, delivery.ProjectRoot, delivery.Host, delivery.Session,
        delivery.Status, delivery.ConfirmationHash, delivery.Error, delivery.UpdatedAt);

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

    private sealed class PersistedState
    {
        public string Policy { get; set; } = "confirm";
        public Dictionary<string, PersistedHost> Hosts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class PersistedHost
    {
        public bool Enabled { get; set; } = true;
        public int? TimeoutSeconds { get; set; }
        public string[]? ArgumentTemplate { get; set; }
        public string? MessageTransport { get; set; }
        public string? ManualPath { get; set; }
        public string? ResolvedPath { get; set; }
        public string? EntryPath { get; set; }
        public string? Mode { get; set; }
        public bool SupportsSession { get; set; }
        public string? DetectedVia { get; set; }
        public string? UriScheme { get; set; }
    }

    private sealed class PersistedDelivery
    {
        public string CommentId { get; set; } = string.Empty;
        public string CardId { get; set; } = string.Empty;
        public string ProjectRoot { get; set; } = string.Empty;
        public string Host { get; set; } = string.Empty;
        public string? Session { get; set; }
        public string Body { get; set; } = string.Empty;
        public string ConfirmationHash { get; set; } = string.Empty;
        public string Status { get; set; } = BoardAgentDeliveryStatuses.NeedsConfirmation;
        public string? Error { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }
}

public sealed class SystemBoardAgentProcessRunner : IBoardAgentProcessRunner
{
    private const int ERROR_SUMMARY_BYTE_LIMIT = 2048;

    public async Task<BoardAgentProcessResult> ExecuteAsync(BoardAgentProcessSpec spec, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = spec.StandardInput is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = spec.WorkingDirectory
        };
        startInfo.Environment.Remove("TOOLBOX_BOARD_ACTOR");
        startInfo.Environment.Remove("CODEX_THREAD_ID");
        startInfo.Environment.Remove("CODEX_SESSION_ID");
        foreach (string argument in spec.Arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        Task outputDrain = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, 4096, CancellationToken.None);
        Task<string> errorDrain = DrainErrorAsync(process.StandardError.BaseStream, timeoutSource.Token);

        try
        {
            if (spec.StandardInput is not null)
            {
                byte[] input = new UTF8Encoding(false).GetBytes(spec.StandardInput);
                await process.StandardInput.BaseStream.WriteAsync(input, timeoutSource.Token).ConfigureAwait(false);
                await process.StandardInput.BaseStream.FlushAsync(timeoutSource.Token).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch (InvalidOperationException) { }
            await Task.WhenAll(outputDrain, errorDrain).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) throw;
            return new BoardAgentProcessResult(null, true, await errorDrain.ConfigureAwait(false));
        }

        await Task.WhenAll(outputDrain, errorDrain).ConfigureAwait(false);
        return new BoardAgentProcessResult(process.ExitCode, false, await errorDrain.ConfigureAwait(false));
    }

    private static async Task<string> DrainErrorAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[4096];
        byte[] summary = new byte[ERROR_SUMMARY_BYTE_LIMIT];
        int retained = 0;
        while (true)
        {
            int count;
            try { count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            if (count <= 0) break;
            int copy = Math.Min(count, summary.Length - retained);
            if (copy > 0)
            {
                Buffer.BlockCopy(buffer, 0, summary, retained, copy);
                retained += copy;
            }
        }
        return Encoding.UTF8.GetString(summary, 0, retained);
    }
}
