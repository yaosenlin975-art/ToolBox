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
    private const int MaxBodyBytes = 64 * 1024;
    private const int MaximumHostTimeoutSeconds = 3600;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string[] HostIds = ["codex", "claude", "codebuddy", "opencode", "zcode", "qoder", "trae", "workbuddy"];
    private static readonly Lazy<BoardAgentService> DefaultInstance = new(CreateDefault);
    private readonly string _appDataDirectory;
    private readonly string _statePath;
    private readonly string _deliveryPath;
    private readonly IBoardAgentDiscoveryPlatform _platform;
    private readonly IBoardAgentProcessRunner _processRunner;
    private readonly TimeSpan _defaultTimeout;
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(128)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });
    private readonly ConcurrentDictionary<string, BoardAgentHostResolution> _resolutions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _queuedKeys = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private PersistedState? _state;
    private DateTime _stateLastWriteUtc;
    private bool _stateFileObserved;
    private List<PersistedDelivery>? _deliveries;
    private DateTime _deliveryLastWriteUtc;
    private bool _deliveryFileObserved;
    private TaskCompletionSource _idle = CompletedSource();
    private int _pendingJobs;
    private int _deliveryChangePending;
    private bool _disposed;

    public static BoardAgentService Instance => DefaultInstance.Value;
    public event Action? Changed;

    public BoardAgentService(BoardAgentServiceOptions? options = null)
    {
        options ??= new BoardAgentServiceOptions();
        string? configuredDirectory = options.AppDataDirectory;
        _appDataDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(configuredDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ToolBox")
            : configuredDirectory);
        _statePath = Path.Combine(_appDataDirectory, "board-agents.json");
        _deliveryPath = Path.Combine(_appDataDirectory, "board-deliveries.json");
        _platform = options.DiscoveryPlatform ?? new WindowsBoardAgentDiscoveryPlatform();
        _processRunner = options.ProcessRunner ?? new SystemBoardAgentProcessRunner();
        _defaultTimeout = options.DefaultTimeout > TimeSpan.Zero ? options.DefaultTimeout : TimeSpan.FromSeconds(120);
        _worker = Task.Run(WorkerAsync);
    }

    public string GetConfigPath() => _statePath;

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
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PersistedState state = await LoadStateUnsafeAsync(cancellationToken).ConfigureAwait(false);
            await LoadDeliveriesUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var result = new List<BoardAgentHostInfo>(HostIds.Length);
            bool changed = false;
            foreach (string host in HostIds)
            {
                BoardAgentHostResolution resolution = ResolveHost(host, state, refresh, out bool configChanged);
                changed |= configChanged;
                _resolutions[host] = resolution;
                result.Add(ToHostInfo(resolution));
            }
            if (changed) await SaveStateUnsafeAsync(state, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _stateGate.Release();
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

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PersistedState state = await LoadStateUnsafeAsync(cancellationToken).ConfigureAwait(false);
            List<PersistedDelivery> deliveries = await LoadDeliveriesUnsafeAsync(cancellationToken).ConfigureAwait(false);
            PersistedDelivery? existing = deliveries.SingleOrDefault(d => d.ConfirmationHash == key);
            BoardAgentHostResolution resolution = _resolutions[context.Host];
            if (existing is not null)
            {
                if (existing.Status == BoardAgentDeliveryStatuses.NeedsConfirmation && context.Session is not null &&
                    resolution.SupportsSession && resolution.Mode == "cli" &&
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
            bool needsConfirmation = false;
            if (context.Session is null)
            {
                status = BoardAgentDeliveryStatuses.ManualPullRequired;
            }
            else if (!resolution.SupportsSession || resolution.Mode != "cli")
            {
                status = BoardAgentDeliveryStatuses.Failed;
                error = "Exact session delivery is unavailable for this host. Use its inbox pull or manual launch.";
            }
            else if (!string.Equals(state.Policy, "always", StringComparison.Ordinal) && !FixedEquals(confirmedHash, key))
            {
                status = BoardAgentDeliveryStatuses.NeedsConfirmation;
                needsConfirmation = true;
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
            var response = ToDelivery(delivery);
            return needsConfirmation ? response with { ConfirmationHash = key } : response;
        }
        finally
        {
            _stateGate.Release();
            RaisePendingChanged();
        }
    }

    public async Task<BoardAgentDelivery?> GetDeliveryAsync(BoardAgentCommentContext context, CancellationToken cancellationToken = default)
    {
        context = ValidateContext(context);
        string key = GetConfirmationHash(context);
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PersistedState state = await LoadStateUnsafeAsync(cancellationToken).ConfigureAwait(false);
            List<PersistedDelivery> deliveries = await LoadDeliveriesUnsafeAsync(cancellationToken).ConfigureAwait(false);
            PersistedDelivery? delivery = deliveries.SingleOrDefault(d => d.ConfirmationHash == key);
            return delivery is null ? null : ToDelivery(delivery);
        }
        finally
        {
            _stateGate.Release();
            RaisePendingChanged();
        }
    }

    public async Task<BoardAgentDelivery?> MarkAcknowledgedAsync(BoardAgentCommentContext context, CancellationToken cancellationToken = default)
    {
        context = ValidateContext(context);
        string key = GetConfirmationHash(context);
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            _stateGate.Release();
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
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PersistedState state = await LoadStateUnsafeAsync(cancellationToken).ConfigureAwait(false);
            List<PersistedDelivery> deliveries = await LoadDeliveriesUnsafeAsync(cancellationToken).ConfigureAwait(false);
            PersistedDelivery? delivery = deliveries.SingleOrDefault(d => d.ConfirmationHash == key);
            if (delivery is null || delivery.Status is BoardAgentDeliveryStatuses.NeedsConfirmation or BoardAgentDeliveryStatuses.Submitted or BoardAgentDeliveryStatuses.Acknowledged or BoardAgentDeliveryStatuses.Queued or BoardAgentDeliveryStatuses.Sending)
                return delivery is null ? null : ToDelivery(delivery);
            if (delivery.Status == BoardAgentDeliveryStatuses.Unknown && !userVerifiedUnknown) return ToDelivery(delivery);
            BoardAgentHostResolution resolution = _resolutions[context.Host];
            if (context.Session is null || !resolution.SupportsSession || resolution.Mode != "cli")
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
            _stateGate.Release();
            RaisePendingChanged();
        }
    }

    public async Task SetDeliveryPolicyAsync(string policy, CancellationToken cancellationToken = default)
    {
        if (policy is not ("confirm" or "always")) throw new ArgumentOutOfRangeException(nameof(policy));
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PersistedState state = await LoadStateUnsafeAsync(cancellationToken).ConfigureAwait(false);
            state.Policy = policy;
            await SaveStateUnsafeAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task<string> GetDeliveryPolicyAsync(CancellationToken cancellationToken = default)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PersistedState state = await LoadStateUnsafeAsync(cancellationToken).ConfigureAwait(false);
            return state.Policy;
        }
        finally
        {
            _stateGate.Release();
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
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("The selected host path does not exist.", fullPath);
        if (IsShellShim(fullPath)) throw new ArgumentException("Select the native executable or the host's JavaScript entry, not a command shim.", nameof(path));

        BoardAgentHostResolution resolution = CreateManualResolution(host, fullPath, detectedVia);
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PersistedState state = await LoadStateUnsafeAsync(cancellationToken).ConfigureAwait(false);
            PersistedHost configuration = state.Hosts.GetValueOrDefault(host) ?? new PersistedHost();
            configuration.ManualPath = fullPath;
            SaveResolution(configuration, resolution);
            configuration.DetectedVia = detectedVia;
            state.Hosts[host] = configuration;
            _resolutions[host] = ApplyHostSettings(resolution, configuration);
            await SaveStateUnsafeAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public BoardAgentLaunchTarget? ResolveLaunch(string host, string projectRoot)
    {
        host = NormalizeHost(host);
        string fullRoot = NormalizeProjectRoot(projectRoot);
        if (!_resolutions.TryGetValue(host, out BoardAgentHostResolution? resolution)) return null;
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
            await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            Task waitTask;
            try
            {
                if (_pendingJobs == 0) return;
                waitTask = _idle.Task;
            }
            finally { _stateGate.Release(); }
            await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.Writer.TryComplete();
        _shutdown.Cancel();
        try { await _worker.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _shutdown.Dispose();
        _stateGate.Dispose();
    }

    private static BoardAgentService CreateDefault()
    {
        string appDataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ToolBox");
#if DEBUG
        string? testRoot = Environment.GetEnvironmentVariable("TOOLBOX_TASK_TEST_ROOT");
        if (!string.IsNullOrWhiteSpace(testRoot)) appDataDirectory = Path.Combine(testRoot, "ToolBox");
#endif
        return new BoardAgentService(new BoardAgentServiceOptions { AppDataDirectory = appDataDirectory });
    }

    private async Task WorkerAsync()
    {
        await foreach (string key in _queue.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
        {
            try { await ProcessDeliveryAsync(key, _shutdown.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
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
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PersistedState state = await LoadStateUnsafeAsync(cancellationToken).ConfigureAwait(false);
            List<PersistedDelivery> deliveries = await LoadDeliveriesUnsafeAsync(cancellationToken).ConfigureAwait(false);
            delivery = deliveries.SingleOrDefault(d => d.ConfirmationHash == key);
            if (delivery is null || delivery.Status != BoardAgentDeliveryStatuses.Queued) return;
            resolution = ResolveHost(delivery.Host, state, false, out bool changed);
            _resolutions[delivery.Host] = resolution;
            if (changed) await SaveStateUnsafeAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally { _stateGate.Release(); RaisePendingChanged(); }

        if (delivery.Session is null)
        {
            await SetDeliveryErrorAsync(key, BoardAgentDeliveryStatuses.ManualPullRequired, null).ConfigureAwait(false);
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
        try { result = await _processRunner.ExecuteAsync(processSpec, TimeSpan.FromSeconds(resolution.TimeoutSeconds), cancellationToken).ConfigureAwait(false); }
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

    private async Task MarkUnknownBestEffortAsync(string key, string error)
    {
        try { await SetDeliveryErrorAsync(key, BoardAgentDeliveryStatuses.Unknown, LimitUtf8(error, 2048)).ConfigureAwait(false); }
        catch (Exception exception) { Debug.WriteLine($"[ToolBox] Delivery outcome remains uncertain: {exception.Message}"); }
    }

    private async Task SetDeliveryErrorAsync(string key, string status, string? error)
    {
        await _stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
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
        finally { _stateGate.Release(); RaisePendingChanged(); }
    }

    private async Task<bool> TrySetSendingAsync(string key)
    {
        await _stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
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
        finally { _stateGate.Release(); RaisePendingChanged(); }
    }

    private bool QueueUnsafe(string key)
    {
        if (!_queuedKeys.Add(key)) return true;
        if (_pendingJobs++ == 0) _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_queue.Writer.TryWrite(key)) return true;
        _queuedKeys.Remove(key);
        if (--_pendingJobs == 0) _idle.TrySetResult();
        return false;
    }

    private async Task FinishQueueItemAsync(string key)
    {
        await _stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _queuedKeys.Remove(key);
            if (--_pendingJobs == 0) _idle.TrySetResult();
        }
        finally { _stateGate.Release(); }
    }

    private async Task<PersistedState> LoadStateUnsafeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_appDataDirectory);
        DateTime fileWriteUtc = File.Exists(_statePath) ? File.GetLastWriteTimeUtc(_statePath) : DateTime.MinValue;
        if (_state is not null && _stateFileObserved && fileWriteUtc == _stateLastWriteUtc) return _state;
        if (!File.Exists(_statePath)) _state = new PersistedState();
        else
        {
            string json = await File.ReadAllTextAsync(_statePath, cancellationToken).ConfigureAwait(false);
            _state = JsonSerializer.Deserialize<PersistedState>(json, JsonOptions) ?? throw new InvalidDataException("Board agent configuration is empty.");
        }
        _stateFileObserved = true;
        _stateLastWriteUtc = fileWriteUtc;
        _resolutions.Clear();

        return _state;
    }

    private async Task<List<PersistedDelivery>> LoadDeliveriesUnsafeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_appDataDirectory);
        DateTime fileWriteUtc = File.Exists(_deliveryPath) ? File.GetLastWriteTimeUtc(_deliveryPath) : DateTime.MinValue;
        if (_deliveries is not null && _deliveryFileObserved && fileWriteUtc == _deliveryLastWriteUtc) return _deliveries;
        if (!File.Exists(_deliveryPath)) _deliveries = [];
        else
        {
            string json = await File.ReadAllTextAsync(_deliveryPath, cancellationToken).ConfigureAwait(false);
            _deliveries = JsonSerializer.Deserialize<List<PersistedDelivery>>(json, JsonOptions) ?? throw new InvalidDataException("Board agent delivery records are empty.");
        }
        _deliveryFileObserved = true;
        _deliveryLastWriteUtc = fileWriteUtc;
        bool changed = false;
        foreach (PersistedDelivery delivery in _deliveries)
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
        if (changed) await SaveDeliveriesUnsafeAsync(_deliveries, cancellationToken).ConfigureAwait(false);
        return _deliveries;
    }

    private async Task SaveStateUnsafeAsync(PersistedState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_appDataDirectory);
        string temporaryPath = _statePath + ".tmp";
        string json = JsonSerializer.Serialize(state, JsonOptions);
        await File.WriteAllTextAsync(temporaryPath, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(temporaryPath, _statePath, true);
        _stateLastWriteUtc = File.GetLastWriteTimeUtc(_statePath);
        _stateFileObserved = true;
    }

    private async Task SaveDeliveriesUnsafeAsync(List<PersistedDelivery> deliveries, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_appDataDirectory);
        string temporaryPath = _deliveryPath + ".tmp";
        string json = JsonSerializer.Serialize(deliveries, JsonOptions);
        Interlocked.Exchange(ref _deliveryChangePending, 1);
        await File.WriteAllTextAsync(temporaryPath, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(temporaryPath, _deliveryPath, true);
        _deliveryLastWriteUtc = File.GetLastWriteTimeUtc(_deliveryPath);
        _deliveryFileObserved = true;
    }

    private void RaisePendingChanged()
    {
        if (Interlocked.Exchange(ref _deliveryChangePending, 0) == 0) return;
        Action? handlers = Changed;
        if (handlers is null) return;
        foreach (Action handler in handlers.GetInvocationList().Cast<Action>())
        {
            try { handler(); }
            catch (Exception exception) { Debug.WriteLine($"[ToolBox] Board agent change notification failed: {exception.Message}"); }
        }
    }

    private BoardAgentHostResolution ResolveHost(string host, PersistedState state, bool refresh, out bool configChanged)
    {
        configChanged = false;
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
            string? registeredExe = _platform.GetUrlProtocolCommands(scheme).Select(ExtractExecutable).FirstOrDefault(p => p is not null && File.Exists(p));
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

        var installedCandidate = _platform.GetUninstallEntries()
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

        string? availableScheme = GetSchemes(host).FirstOrDefault(s => _platform.GetUrlProtocolCommands(s).Any());
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
        var directories = _platform.GetPathDirectories()
            .Concat(new[] { Path.Combine(_platform.GetFolderPath(Environment.SpecialFolder.ApplicationData) ?? string.Empty, "npm") })
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
        foreach (string directory in _platform.GetPathDirectories())
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
        if (TryReadNodeShimEntry(shimPath, out string? nodeEntry))
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

    private bool TryReadNodeShimEntry(string shimPath, out string? entryPath)
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
        if (raw is null) return false;
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
                TimeoutSeconds = configuration.TimeoutSeconds ?? (int)Math.Ceiling(_defaultTimeout.TotalSeconds),
                ArgumentTemplate = configuration.ArgumentTemplate,
                MessageTransport = configuration.MessageTransport
            };
        }

        return resolution with
        {
            Enabled = true,
            TimeoutSeconds = configuration.TimeoutSeconds ?? (int)Math.Ceiling(_defaultTimeout.TotalSeconds),
            ArgumentTemplate = configuration.ArgumentTemplate,
            MessageTransport = configuration.MessageTransport
        };
    }

    private static void SaveResolution(PersistedHost config, BoardAgentHostResolution resolution)
    {
        config.ResolvedPath = resolution.ExecutablePath;
        config.EntryPath = resolution.EntryPath;
        config.Mode = resolution.Mode;
        config.SupportsSession = resolution.SupportsSession;
        config.DetectedVia = resolution.DetectedVia;
        config.UriScheme = resolution.UriScheme;
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
        foreach (string root in _platform.GetCommonRoots())
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
            "zcode" => "ZCode", "qoder" => "Qoder", "trae" => "Trae", "workbuddy" => "WorkBuddy", _ => resolution.Host
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
        if (resolution.TimeoutSeconds is < 1 or > MaximumHostTimeoutSeconds)
            throw new InvalidOperationException($"Host timeout must be between 1 and {MaximumHostTimeoutSeconds} seconds.");

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

    private static void ValidateArgumentTemplate(string[] template, string transport)
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
        if (Encoding.UTF8.GetByteCount(context.Body ?? string.Empty) > MaxBodyBytes) throw new ArgumentException("Comment body exceeds 64 KiB.", nameof(context.Body));
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
    private const int ErrorSummaryByteLimit = 2048;

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
        Task outputDrain = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, 4096, CancellationToken.None);
        Task<string> errorDrain = DrainErrorAsync(process.StandardError.BaseStream);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

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

    private static async Task<string> DrainErrorAsync(Stream stream)
    {
        byte[] buffer = new byte[4096];
        byte[] summary = new byte[ErrorSummaryByteLimit];
        int retained = 0;
        int count;
        while ((count = await stream.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false)) > 0)
        {
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
