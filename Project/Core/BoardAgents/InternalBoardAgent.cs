// 内部助手看板代理：以独立内部身份处理已路由且已认领的评论回复，负责认领门禁、同会话串行、
// 截止时间控制，以及「回帖后 ack 前」的崩溃恢复（先核对回复再 ack，未知状态人工核实不重复调用）。
using ToolBox.Core.Boards;
using ToolBox.Core.Todo;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace ToolBox.Core.BoardAgents;

public sealed record InternalBoardTurn(
    string ProjectRoot,
    string SessionId,
    Guid ActorId,
    string CommentId,
    string CardId,
    string Body);

public sealed class InternalBoardAgentOptions
{
    public string? AppDataDirectory { get; init; }
    public Func<TaskRequest, CancellationToken, Task<TaskResponse>>? BoardExecutor { get; init; }
    public Func<InternalBoardTurn, CancellationToken, Task>? TurnRunner { get; init; }
    public Func<string, Guid>? ResolveSessionActor { get; init; }
}

public sealed class InternalBoardAgent
{
    private const string PhaseProcessing = "processing";
    private const string PhaseReplied = "replied";
    private const string PhaseAcknowledged = "acknowledged";
    private const int ReplyScanLimit = 100;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly Lazy<InternalBoardAgent> DefaultInstance = new(CreateDefault);

    private readonly string statePath;
    private readonly SemaphoreSlim stateGate = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> sessionGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> inFlight = new(StringComparer.Ordinal);
    private readonly List<InternalDeliveryState> states = [];
    private bool statesLoaded;
    private Func<TaskRequest, CancellationToken, Task<TaskResponse>> boardExecutor;
    private Func<InternalBoardTurn, CancellationToken, Task>? turnRunner;
    private Func<string, Guid>? resolveSessionActor;

    public static InternalBoardAgent Instance => DefaultInstance.Value;

    public bool IsConfigured => turnRunner is not null && resolveSessionActor is not null;

    public InternalBoardAgent(InternalBoardAgentOptions? options = null)
    {
        options ??= new InternalBoardAgentOptions();
        string? configured = options.AppDataDirectory;
        string directory = Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ToolBox")
            : configured);
        statePath = Path.Combine(directory, "internal-deliveries.json");
        Directory.CreateDirectory(directory);
        boardExecutor = options.BoardExecutor ?? DefaultBoardExecutor;
        turnRunner = options.TurnRunner;
        resolveSessionActor = options.ResolveSessionActor;
    }

    public InternalBoardAgent Configure(
        Func<TaskRequest, CancellationToken, Task<TaskResponse>> executor,
        Func<InternalBoardTurn, CancellationToken, Task> runner,
        Func<string, Guid> actorResolver)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(actorResolver);
        boardExecutor = executor;
        turnRunner = runner;
        resolveSessionActor = actorResolver;
        return this;
    }

    public void CancelAll()
    {
        foreach (CancellationTokenSource source in inFlight.Values)
        {
            try { source.Cancel(); }
            catch (Exception exception) { Debug.WriteLine($"[ToolBox] Internal round cancel failed: {exception.Message}"); }
        }
    }

    public async Task<BoardAgentProcessResult> ExecuteAsync(
        BoardAgentCommentContext context,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        string? session = context.Session;
        if (string.IsNullOrWhiteSpace(session))
            return Failed("内部评论缺少会话身份，无法投递。");

        Guid actorId;
        try { actorId = RequireSessionActor(session); }
        catch (Exception exception) { return Failed($"内部会话身份不可用：{exception.Message}"); }

        SemaphoreSlim sessionGate = sessionGates.GetOrAdd(session, _ => new SemaphoreSlim(1, 1));
        await sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var runCancellation = new CancellationTokenSource();
        inFlight[context.CommentId] = runCancellation;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, runCancellation.Token);
        try
        {
            return await ProcessAsync(context, session, actorId, timeout, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new BoardAgentProcessResult(null, true, "内部轮次已被取消，副作用可能已发生。");
        }
        finally
        {
            inFlight.TryRemove(context.CommentId, out _);
            sessionGate.Release();
        }
    }

    private async Task<BoardAgentProcessResult> ProcessAsync(
        BoardAgentCommentContext context,
        string session,
        Guid actorId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        string actor = actorId.ToString("D");
        InternalDeliveryState? state = await FindStateAsync(context.CommentId).ConfigureAwait(false);
        if (state is not null && !string.Equals(state.Session, session, StringComparison.Ordinal)) state = null;

        TaskResponse commentResponse = await ExecuteBoardAsync("board.comment.get",
            Payload("commentId", context.CommentId, "project", context.ProjectRoot), cancellationToken).ConfigureAwait(false);
        if (!commentResponse.Ok) return Failed($"无法读取来源评论：{Describe(commentResponse)}");
        if (!TryReadComment(commentResponse.Data, out ProjectBoardComment? comment)) return Failed("来源评论数据无效。");

        if (state?.Phase == PhaseAcknowledged) return Success();
        if (state is not null && !string.Equals(state.ActorId, actor, StringComparison.Ordinal))
            return Manual("来源评论与内部身份不一致，需人工核实。");

        if (state?.Phase == PhaseReplied)
            return await CompleteAckAsync(state, actor, session, cancellationToken).ConfigureAwait(false);

        if (state?.Phase == PhaseProcessing)
        {
            Guid? recovered = await FindReplyAsync(context, actor, cancellationToken).ConfigureAwait(false);
            if (recovered is null) return Manual("内部轮次状态未知且未找到回复，需人工核实。");
            state.Phase = PhaseReplied;
            state.ReplyCommentId = recovered.Value.ToString("D");
            await SaveStateAsync(state).ConfigureAwait(false);
            return await CompleteAckAsync(state, actor, session, cancellationToken).ConfigureAwait(false);
        }

        if (comment!.Routing is not { Host: "internal" } routing || !string.Equals(routing.SessionId, session, StringComparison.Ordinal))
            return Failed("该评论未路由到内部会话，拒绝处理（等待用户操作）。");

        DateTime now = DateTime.UtcNow;
        ProjectBoardInboxClaim? claim = OwnValidClaim(comment, actor, session, now);
        if (claim is null)
        {
            if (comment.AcknowledgedAt is not null)
            {
                await SaveStateAsync(NewState(context, session, actor, null, PhaseAcknowledged)).ConfigureAwait(false);
                return Success();
            }
            return Failed("该评论未持有内部会话的有效认领，拒绝抢占（等待用户操作）。");
        }

        TimeSpan hostBudget = timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(120);
        TimeSpan inboxRemaining = claim.ExpiresAt - now;
        if (inboxRemaining <= TimeSpan.Zero) return Failed("收件认领已过期，拒绝处理。");
        TimeSpan budget = inboxRemaining < hostBudget ? inboxRemaining : hostBudget;
        DateTime? cardExpiry = await ReadOwnCardClaimExpiryAsync(context, actor, session, cancellationToken).ConfigureAwait(false);
        if (cardExpiry is DateTime expiry)
        {
            TimeSpan cardRemaining = expiry - now;
            if (cardRemaining <= TimeSpan.Zero) return Failed("卡片租约已过期，拒绝处理。");
            if (cardRemaining < budget) budget = cardRemaining;
        }

        InternalDeliveryState current = NewState(context, session, actor, claim.ClaimId.ToString("D"), PhaseProcessing);
        await SaveStateAsync(current).ConfigureAwait(false);

        using (var deadline = new CancellationTokenSource(budget))
        using (var turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token))
        {
            try
            {
                await RunTurnAsync(context, session, actorId, turnCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (turnCancellation.IsCancellationRequested)
            {
                // 截止时间到或上游取消：不立即失败，先核对是否已产生回复。
            }
            catch (Exception exception)
            {
                current.Error = Limit(exception.Message);
                await SaveStateAsync(current).ConfigureAwait(false);
            }
        }

        Guid? reply = await FindReplyAsync(context, actor, CancellationToken.None).ConfigureAwait(false);
        if (reply is null)
        {
            current.Error ??= "内部轮次结束但未找到回复。";
            await SaveStateAsync(current).ConfigureAwait(false);
            return Manual("内部轮次未产生可识别的回复，需人工核实（不会重复调用）。");
        }
        current.Phase = PhaseReplied;
        current.ReplyCommentId = reply.Value.ToString("D");
        current.Error = null;
        await SaveStateAsync(current).ConfigureAwait(false);
        return await CompleteAckAsync(current, actor, session, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<BoardAgentProcessResult> CompleteAckAsync(
        InternalDeliveryState state,
        string actor,
        string session,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(state.ClaimId, out Guid claimId)) return Manual("内部认领标识无效，需人工核实。");

        TaskResponse currentResponse = await ExecuteBoardAsync("board.comment.get",
            Payload("commentId", state.CommentId, "project", state.ProjectRoot), cancellationToken).ConfigureAwait(false);
        if (!currentResponse.Ok) return Failed($"ack 前核对失败：{Describe(currentResponse)}");
        if (!TryReadComment(currentResponse.Data, out ProjectBoardComment? latest)) return Manual("ack 前评论数据无效，需人工核实。");

        if (latest!.AcknowledgedAt is not null)
        {
            state.Phase = PhaseAcknowledged;
            await SaveStateAsync(state).ConfigureAwait(false);
            return Success();
        }

        ProjectBoardInboxClaim? claim = OwnValidClaim(latest, actor, session, DateTime.UtcNow);
        if (claim is null || claim.ClaimId != claimId) return Failed("认领已过期或已变更，ack 中止（保留可重试事实）。");

        var payload = Payload("commentId", state.CommentId, "claimId", claimId.ToString("D"), "project", state.ProjectRoot,
            "host", "internal", "actor", actor, "session", session);
        if (TryReadRevisions(currentResponse.Data, out long boardRevision, out long commentRevision))
        {
            payload["revision"] = boardRevision;
            payload["commentRevision"] = commentRevision;
        }

        TaskResponse ackResponse = await ExecuteBoardAsync("board.comment.ack", payload, cancellationToken).ConfigureAwait(false);
        if (ackResponse.Ok)
        {
            state.Phase = PhaseAcknowledged;
            state.Error = null;
            await SaveStateAsync(state).ConfigureAwait(false);
            return Success();
        }

        TaskResponse recheck = await ExecuteBoardAsync("board.comment.get",
            Payload("commentId", state.CommentId, "project", state.ProjectRoot), cancellationToken).ConfigureAwait(false);
        if (recheck.Ok && TryReadComment(recheck.Data, out ProjectBoardComment? reloaded) && reloaded!.AcknowledgedAt is not null)
        {
            state.Phase = PhaseAcknowledged;
            state.Error = null;
            await SaveStateAsync(state).ConfigureAwait(false);
            return Success();
        }

        state.Error = Limit(Describe(ackResponse));
        await SaveStateAsync(state).ConfigureAwait(false);
        return Failed($"ack 失败（保留可重试事实）：{state.Error}");
    }

    private async Task<Guid?> FindReplyAsync(BoardAgentCommentContext context, string actor, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(context.CommentId, out Guid parentId)) return null;
        TaskResponse response = await ExecuteBoardAsync("board.comment.list",
            Payload("id", context.CardId, "project", context.ProjectRoot, "limit", ReplyScanLimit), cancellationToken).ConfigureAwait(false);
        if (!response.Ok || !TryReadComments(response.Data, out IReadOnlyList<ProjectBoardComment> comments)) return null;
        ProjectBoardComment? reply = comments
            .Where(candidate => candidate.ParentId == parentId && candidate.DeletedAt is null &&
                candidate.Author.Kind == "agent" && string.Equals(candidate.Author.Id, actor, StringComparison.Ordinal))
            .OrderByDescending(candidate => candidate.CreatedAt).ThenByDescending(candidate => candidate.Id)
            .FirstOrDefault();
        return reply?.Id;
    }

    private async Task<DateTime?> ReadOwnCardClaimExpiryAsync(
        BoardAgentCommentContext context,
        string actor,
        string session,
        CancellationToken cancellationToken)
    {
        TaskResponse response = await ExecuteBoardAsync("board.card.get",
            Payload("id", context.CardId, "project", context.ProjectRoot), cancellationToken).ConfigureAwait(false);
        if (!response.Ok || !TryReadCardClaim(response.Data, out ProjectBoardClaim? claim) || claim is null) return null;
        bool ours = claim.HostId == "internal" && string.Equals(claim.SessionId, session, StringComparison.Ordinal) &&
            string.Equals(claim.ActorId, actor, StringComparison.Ordinal);
        if (!ours) return null;

        // 卡片租约可按既有 claim 接口续租；仅在仍有效但剩余不足时点一次心跳，
        // 已过期的租约直接交给调用方拒绝（重取 ≠ 续期）。
        TimeSpan remaining = claim.ExpiresAt - DateTime.UtcNow;
        if (remaining > TimeSpan.Zero && remaining <= TimeSpan.FromSeconds(30))
        {
            TaskResponse renewed = await ExecuteBoardAsync("board.card.claim",
                Payload("id", context.CardId, "project", context.ProjectRoot, "host", "internal", "actor", actor, "session", session, "ttlMinutes", 30),
                cancellationToken).ConfigureAwait(false);
            if (renewed.Ok && TryReadCardClaim(renewed.Data, out ProjectBoardClaim? renewedClaim) && renewedClaim is not null)
                return renewedClaim.ExpiresAt;
        }
        return claim.ExpiresAt;
    }

    private async Task RunTurnAsync(BoardAgentCommentContext context, string session, Guid actorId, CancellationToken cancellationToken)
    {
        Func<InternalBoardTurn, CancellationToken, Task>? runner = turnRunner;
        if (runner is null) throw new InvalidOperationException("内部轮次执行器尚未配置。");
        await runner(new InternalBoardTurn(context.ProjectRoot, session, actorId, context.CommentId, context.CardId, context.Body),
            cancellationToken).ConfigureAwait(false);
    }

    private Guid RequireSessionActor(string session)
    {
        Func<string, Guid>? resolver = resolveSessionActor;
        if (resolver is null) throw new InvalidOperationException("内部会话身份解析器尚未配置。");
        Guid actorId = resolver(session);
        if (actorId == Guid.Empty) throw new InvalidOperationException("内部会话缺少有效 ActorId。");
        return actorId;
    }

    private async Task<TaskResponse> ExecuteBoardAsync(
        string operation,
        Dictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        var request = new TaskRequest
        {
            Operation = operation,
            Payload = JsonSerializer.SerializeToElement(payload, TaskProtocol.Options)
        };
        try
        {
            return await boardExecutor(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { return TaskResponse.Failure(request, "IO_ERROR", Limit(exception.Message)); }
    }

    private async Task<InternalDeliveryState?> FindStateAsync(string commentId)
    {
        await stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await LoadStatesUnsafeAsync().ConfigureAwait(false);
            return states.FirstOrDefault(state => string.Equals(state.CommentId, commentId, StringComparison.Ordinal));
        }
        finally { stateGate.Release(); }
    }

    private async Task SaveStateAsync(InternalDeliveryState value)
    {
        value.UpdatedAt = DateTimeOffset.UtcNow;
        await stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await LoadStatesUnsafeAsync().ConfigureAwait(false);
            states.RemoveAll(state => string.Equals(state.CommentId, value.CommentId, StringComparison.Ordinal));
            states.Add(value);
            await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(states, JsonOptions)).ConfigureAwait(false);
        }
        finally { stateGate.Release(); }
    }

    private async Task LoadStatesUnsafeAsync()
    {
        if (statesLoaded) return;
        statesLoaded = true;
        if (!File.Exists(statePath)) return;
        try
        {
            List<InternalDeliveryState>? loaded = JsonSerializer.Deserialize<List<InternalDeliveryState>>(
                await File.ReadAllTextAsync(statePath).ConfigureAwait(false), JsonOptions);
            if (loaded is not null) states.AddRange(loaded);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[ToolBox] Internal delivery state could not be loaded: {exception.Message}");
        }
    }

    private static InternalDeliveryState NewState(
        BoardAgentCommentContext context,
        string session,
        string actor,
        string? claimId,
        string phase) => new()
        {
            CommentId = context.CommentId,
            CardId = context.CardId,
            ProjectRoot = context.ProjectRoot,
            Session = session,
            ActorId = actor,
            ClaimId = claimId,
            Phase = phase
        };

    private static ProjectBoardInboxClaim? OwnValidClaim(ProjectBoardComment comment, string actor, string session, DateTime now) =>
        comment.InboxClaim is { } claim && claim.ExpiresAt > now && claim.HostId == "internal" &&
        string.Equals(claim.SessionId, session, StringComparison.Ordinal) &&
        string.Equals(claim.ActorId, actor, StringComparison.Ordinal) ? claim : null;

    private static Dictionary<string, object?> Payload(params object[] pairs)
    {
        var payload = new Dictionary<string, object?>(pairs.Length / 2, StringComparer.Ordinal);
        for (int index = 0; index + 1 < pairs.Length; index += 2)
            payload[(string)pairs[index]] = pairs[index + 1];
        return payload;
    }

    private static bool TryReadComment(object? data, out ProjectBoardComment? comment)
    {
        comment = data as ProjectBoardComment;
        if (comment is not null) return true;
        if (!TryToJson(data, out JsonElement element)) { comment = null; return false; }
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("comment", out JsonElement value)) element = value;
        try { comment = element.Deserialize<ProjectBoardComment>(JsonOptions); }
        catch (JsonException) { comment = null; }
        return comment is not null;
    }

    private static bool TryReadComments(object? data, out IReadOnlyList<ProjectBoardComment> comments)
    {
        comments = [];
        if (!TryToJson(data, out JsonElement element) || element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("comments", out JsonElement array) || array.ValueKind != JsonValueKind.Array)
            return false;
        try
        {
            comments = array.Deserialize<List<ProjectBoardComment>>(JsonOptions) ?? [];
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool TryReadCardClaim(object? data, out ProjectBoardClaim? claim)
    {
        claim = null;
        if (!TryToJson(data, out JsonElement element) || element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("card", out JsonElement card) || card.ValueKind != JsonValueKind.Object ||
            !card.TryGetProperty("claim", out JsonElement claimElement) || claimElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return false;
        try { claim = claimElement.Deserialize<ProjectBoardClaim>(JsonOptions); }
        catch (JsonException) { return false; }
        return claim is not null;
    }

    private static bool TryReadRevisions(object? data, out long boardRevision, out long commentRevision)
    {
        boardRevision = 0;
        commentRevision = 0;
        if (!TryToJson(data, out JsonElement element) || element.ValueKind != JsonValueKind.Object) return false;
        if (element.TryGetProperty("boardRevision", out JsonElement board) && board.TryGetInt64(out long revision))
            boardRevision = revision;
        if (element.TryGetProperty("commentRevision", out JsonElement comment) && comment.TryGetInt64(out long value))
            commentRevision = value;
        return boardRevision > 0 || commentRevision > 0;
    }

    private static bool TryToJson(object? value, out JsonElement element)
    {
        element = default;
        if (value is null) return false;
        if (value is JsonElement direct) { element = direct; return true; }
        try { element = JsonSerializer.SerializeToElement(value, JsonOptions); return true; }
        catch (Exception) { return false; }
    }

    private static Task<TaskResponse> DefaultBoardExecutor(TaskRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return BoardCommandService.Instance.ExecuteAsync(request, cancellationToken);
    }

    private static InternalBoardAgent CreateDefault()
    {
        string appDataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ToolBox");
#if DEBUG
        string? testRoot = Environment.GetEnvironmentVariable("TOOLBOX_TASK_TEST_ROOT");
        if (!string.IsNullOrWhiteSpace(testRoot)) appDataDirectory = Path.Combine(testRoot, "ToolBox");
#endif
        return new InternalBoardAgent(new InternalBoardAgentOptions { AppDataDirectory = appDataDirectory });
    }

    private static BoardAgentProcessResult Success() => new(0, false, string.Empty);

    private static BoardAgentProcessResult Failed(string error) => new(1, false, Limit(error));

    private static BoardAgentProcessResult Manual(string error) => new(null, true, Limit(error));

    private static string Describe(TaskResponse response) => $"{response.Error?.Code}: {response.Error?.Message}";

    private static string Limit(string message) => message.Length <= 1024 ? message : message[..1024];

    private sealed class InternalDeliveryState
    {
        public string CommentId { get; set; } = string.Empty;
        public string CardId { get; set; } = string.Empty;
        public string ProjectRoot { get; set; } = string.Empty;
        public string Session { get; set; } = string.Empty;
        public string ActorId { get; set; } = string.Empty;
        public string? ClaimId { get; set; }
        public string Phase { get; set; } = PhaseProcessing;
        public string? ReplyCommentId { get; set; }
        public string? Error { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
