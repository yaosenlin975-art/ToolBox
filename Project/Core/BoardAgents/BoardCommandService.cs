using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ToolBox.Core.Boards;
using ToolBox.Core.Todo;

namespace ToolBox.Core.BoardAgents;

public sealed record BoardCommentDeliveryResult(
    BoardAgentDelivery? Delivery,
    long Revision,
    long BoardRevision,
    long CommentRevision);

public sealed class BoardCommandService
{
    private const int RequestCacheLimit = 256;
    private static readonly Lazy<BoardCommandService> DefaultInstance = new(() => new BoardCommandService(
        (request, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ProjectBoardStore.Instance.ExecuteAsync(request);
        },
        BoardAgentService.Instance));

    private static readonly HashSet<string> DeliveryReadFields = new(StringComparer.Ordinal)
    {
        "project", "cwd", "commentId", "revision", "commentRevision"
    };

    private static readonly HashSet<string> DeliveryWriteFields = new(DeliveryReadFields, StringComparer.Ordinal)
    {
        "confirm", "confirmationToken", "verifiedUnknown"
    };

    private readonly Func<TaskRequest, CancellationToken, Task<TaskResponse>> _executeStoreAsync;
    private readonly BoardAgentService _agents;
    private readonly object _cacheGate = new();
    private readonly Dictionary<Guid, RequestCacheEntry> _requestCache = [];
    private readonly Queue<Guid> _requestOrder = [];

    public static BoardCommandService Instance => DefaultInstance.Value;

    public BoardCommandService(
        Func<TaskRequest, CancellationToken, Task<TaskResponse>>? storeExecutor = null,
        BoardAgentService? agents = null)
    {
        _executeStoreAsync = storeExecutor ?? ((request, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ProjectBoardStore.Instance.ExecuteAsync(request);
        });
        _agents = agents ?? BoardAgentService.Instance;
    }

    public async Task<TaskResponse> ExecuteAsync(TaskRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null) return TaskResponse.Failure(null, "INVALID_ARGUMENT", "A request is required.", "request");

        try { TaskProtocol.Validate(request); }
        catch (TaskCommandException exception)
        {
            return TaskResponse.Failure(request, exception.Code, exception.Message, exception.Field);
        }

        if (request.Operation is "board.comment.delivery" or "board.comment.deliver")
        {
            try { ValidateDeliveryPayload(request); }
            catch (TaskCommandException exception)
            {
                return TaskResponse.Failure(request, exception.Code, exception.Message, exception.Field);
            }
            return await ExecuteCachedDeliveryAsync(request, cancellationToken).ConfigureAwait(false);
        }

        TaskResponse response = await ExecuteStoreAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.Ok) return response;

        if (request.Operation == "board.comment.add")
            return await AfterCommentAddAsync(request, response, cancellationToken).ConfigureAwait(false);
        if (request.Operation == "board.comment.ack")
            return await AfterCommentAckAsync(request, response, cancellationToken).ConfigureAwait(false);
        return response;
    }

    private async Task<TaskResponse> ExecuteCachedDeliveryAsync(TaskRequest request, CancellationToken cancellationToken)
    {
        Guid requestId = Guid.Parse(request.RequestId);
        string fingerprint = Fingerprint(request);
        RequestCacheEntry entry;
        bool ownsExecution = false;

        lock (_cacheGate)
        {
            if (_requestCache.TryGetValue(requestId, out entry!))
            {
                if (!string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal))
                    return TaskResponse.Failure(request, "CONFLICT", "requestId was already used with different content.", "requestId");
            }
            else
            {
                entry = new RequestCacheEntry(fingerprint);
                _requestCache.Add(requestId, entry);
                _requestOrder.Enqueue(requestId);
                ownsExecution = true;
            }
        }

        if (!ownsExecution)
            return await entry.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            TaskResponse response = await ExecuteDeliveryAsync(request, cancellationToken).ConfigureAwait(false);
            entry.Completion.TrySetResult(response);
            TrimRequestCache();
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TaskResponse cancelled = TaskResponse.Failure(request, "CANCELLED", "The board command was cancelled.");
            entry.Completion.TrySetResult(cancelled);
            RemoveCacheEntry(requestId, entry);
            throw;
        }
        catch (Exception exception)
        {
            TaskResponse failed = FailureFromException(request, exception);
            entry.Completion.TrySetResult(failed);
            TrimRequestCache();
            return failed;
        }
    }

    private async Task<TaskResponse> ExecuteDeliveryAsync(TaskRequest request, CancellationToken cancellationToken)
    {
        JsonElement payload = request.Payload!.Value;
        string commentId = payload.GetProperty("commentId").GetString()!;
        TaskResponse contextResponse = await ReadCommentContextAsync(payload, commentId, cancellationToken).ConfigureAwait(false);
        if (!contextResponse.Ok) return CopyFailure(request, contextResponse);
        if (!TryReadCommentContext(contextResponse.Data, out ProjectBoardCommentContext? projectContext))
            return TaskResponse.Failure(request, "INTERNAL_ERROR", "The project board returned an invalid comment context.");

        if (projectContext.Comment.DeletedAt is not null && request.Operation == "board.comment.deliver")
            return TaskResponse.Failure(request, "CONFLICT", "A deleted comment cannot be delivered.", "commentId");

        BoardCommentDeliveryResult result;
        var warnings = new List<string>();
        BoardAgentCommentContext? agentContext = TryCreateAgentContext(projectContext, warnings);
        if (agentContext is null)
        {
            result = Result(null, projectContext);
            TaskResponse unavailable = TaskResponse.Success(request, result);
            unavailable.Warnings = warnings;
            return unavailable;
        }

        if (!ExpectedRevisionsMatch(payload, projectContext, out string? staleMessage))
            return TaskResponse.Failure(request, "CONFLICT", staleMessage!, "revision");

        TaskResponse cardResponse = await ReadCardAsync(payload, projectContext.CardId, cancellationToken).ConfigureAwait(false);
        if (!cardResponse.Ok) return CopyFailure(request, cardResponse);
        bool cardStateKnown = TryReadCardDeleted(cardResponse.Data, out bool deletedCard);
        if (!cardStateKnown || deletedCard)
        {
            if (request.Operation == "board.comment.deliver")
                return TaskResponse.Failure(request, deletedCard ? "CONFLICT" : "INTERNAL_ERROR",
                    deletedCard ? "A deleted card cannot be delivered to." : "The project board returned an invalid card context.", "commentId");
            warnings.Add(deletedCard ? "manualPullRequired: the comment belongs to a deleted card." : "The card deletion state could not be read.");
        }

        if (projectContext.Comment.DeletedAt is not null)
        {
            if (request.Operation == "board.comment.deliver")
                return TaskResponse.Failure(request, "CONFLICT", "A deleted comment cannot be delivered.", "commentId");
            warnings.Add("manualPullRequired: the comment has been deleted and cannot be delivered.");
        }

        BoardAgentDelivery? current;
        try { current = await _agents.GetDeliveryAsync(agentContext, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            if (request.Operation == "board.comment.delivery")
            {
                warnings.Add($"Local delivery status is unavailable: {SafeMessage(exception)}");
                TaskResponse unavailable = TaskResponse.Success(request, Result(CreateVirtualDelivery(agentContext), projectContext));
                unavailable.Warnings = warnings;
                return unavailable;
            }
            return TaskResponse.Failure(request, "IO_ERROR", "Local delivery status could not be read.");
        }

        if (request.Operation == "board.comment.delivery")
        {
            BoardAgentDelivery display = current ?? CreateVirtualDelivery(agentContext);
            TaskResponse read = TaskResponse.Success(request, Result(display, projectContext));
            if (warnings.Count > 0) read.Warnings = warnings;
            return read;
        }

        bool confirm = payload.TryGetProperty("confirm", out JsonElement confirmValue) && confirmValue.GetBoolean();
        bool verifiedUnknown = payload.TryGetProperty("verifiedUnknown", out JsonElement unknownValue) && unknownValue.GetBoolean();
        string? token = payload.TryGetProperty("confirmationToken", out JsonElement tokenValue) ? tokenValue.GetString() : null;
        string expectedHash = BoardAgentService.GetConfirmationHash(agentContext);
        if (confirm && !FixedHashEquals(token, expectedHash))
            return TaskResponse.Failure(request, "CONFLICT", "The confirmation token is stale; read the current delivery hash before confirming.", "confirmationToken");

        string policy;
        try { policy = await _agents.GetDeliveryPolicyAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return TaskResponse.Failure(request, "IO_ERROR", "The local delivery policy could not be read."); }

        if (current?.Status is BoardAgentDeliveryStatuses.Submitted or BoardAgentDeliveryStatuses.Acknowledged or
            BoardAgentDeliveryStatuses.Queued or BoardAgentDeliveryStatuses.Sending)
            return TaskResponse.Success(request, Result(current, projectContext));

        if (current?.Status == BoardAgentDeliveryStatuses.Unknown)
        {
            if (!confirm || !verifiedUnknown)
                return TaskResponse.Success(request, Result(current, projectContext));
            try
            {
                BoardAgentDelivery? retried = await _agents.RetryAsync(agentContext, expectedHash, userVerifiedUnknown: true, cancellationToken).ConfigureAwait(false);
                return TaskResponse.Success(request, Result(retried ?? current, projectContext));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { return TaskResponse.Failure(request, "IO_ERROR", "The verified delivery retry could not be queued."); }
        }

        if (current?.Status is BoardAgentDeliveryStatuses.Failed or BoardAgentDeliveryStatuses.ManualPullRequired)
        {
            if (!confirm && policy != "always") return TaskResponse.Success(request, Result(current, projectContext));
            try
            {
                BoardAgentDelivery? retried = await _agents.RetryAsync(agentContext, expectedHash, userVerifiedUnknown: false, cancellationToken).ConfigureAwait(false);
                return TaskResponse.Success(request, Result(retried ?? current, projectContext));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { return TaskResponse.Failure(request, "IO_ERROR", "The delivery retry could not be queued."); }
        }

        if (!confirm && policy != "always")
            return TaskResponse.Success(request, Result(current ?? CreateVirtualDelivery(agentContext), projectContext));

        try
        {
            BoardAgentDelivery delivery = await _agents.EnqueueAsync(agentContext, confirm ? token : null, cancellationToken).ConfigureAwait(false);
            TaskResponse queued = TaskResponse.Success(request, Result(delivery, projectContext));
            if (warnings.Count > 0) queued.Warnings = warnings;
            return queued;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return TaskResponse.Failure(request, "IO_ERROR", "The delivery request could not be queued.");
        }
    }

    private async Task<TaskResponse> AfterCommentAddAsync(TaskRequest request, TaskResponse response, CancellationToken cancellationToken)
    {
        try
        {
            if (!TryReadAddedCommentRouting(response.Data, out string? commentId, out bool hasRouting))
                return AddWarning(response, "The comment was saved, but its routing could not be read for delivery setup.");
            if (!hasRouting || commentId is null) return response;

            JsonElement payload = request.Payload!.Value;
            TaskResponse contextResponse = await ReadCommentContextAsync(payload, commentId, cancellationToken).ConfigureAwait(false);
            if (!contextResponse.Ok || !TryReadCommentContext(contextResponse.Data, out ProjectBoardCommentContext? projectContext))
                return AddWarning(response, "The comment was saved, but its current routing could not be read for delivery setup.");
            if (projectContext.Comment.Routing is null || projectContext.Comment.DeletedAt is not null) return response;

            var postWarnings = new List<string>();
            BoardAgentCommentContext? context = TryCreateAgentContext(projectContext, postWarnings);
            if (context is null) return AddWarning(response, postWarnings);
            BoardAgentDelivery delivery = await _agents.EnqueueAsync(context, null, cancellationToken).ConfigureAwait(false);
            response.Data = AddDeliveryField(response.Data, delivery);
            return response;
        }
        catch (Exception exception)
        {
            return AddWarning(response, $"The comment was saved, but delivery setup failed: {SafeMessage(exception)}");
        }
    }

    private async Task<TaskResponse> AfterCommentAckAsync(TaskRequest request, TaskResponse response, CancellationToken cancellationToken)
    {
        try
        {
            JsonElement payload = request.Payload!.Value;
            string commentId = payload.GetProperty("commentId").GetString()!;
            TaskResponse contextResponse = await ReadCommentContextAsync(payload, commentId, cancellationToken).ConfigureAwait(false);
            if (!contextResponse.Ok || !TryReadCommentContext(contextResponse.Data, out ProjectBoardCommentContext? projectContext))
                return AddWarning(response, "The comment was acknowledged, but its current routing could not be read for local delivery status.");
            var warnings = new List<string>();
            BoardAgentCommentContext? context = TryCreateAgentContext(projectContext, warnings);
            if (context is null) return warnings.Count == 0 ? response : AddWarning(response, warnings);
            await _agents.MarkAcknowledgedAsync(context, cancellationToken).ConfigureAwait(false);
            return response;
        }
        catch (Exception exception)
        {
            return AddWarning(response, $"The comment was acknowledged, but local delivery status could not be updated: {SafeMessage(exception)}");
        }
    }

    private async Task<TaskResponse> ReadCommentContextAsync(JsonElement sourcePayload, string commentId, CancellationToken cancellationToken)
    {
        Dictionary<string, object?> values = ReadProjectLocator(sourcePayload);
        values["commentId"] = commentId;
        TaskRequest query = NewStoreRequest("board.comment.get", values);
        return await ExecuteStoreAsync(query, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TaskResponse> ReadCardAsync(JsonElement sourcePayload, Guid cardId, CancellationToken cancellationToken)
    {
        Dictionary<string, object?> values = ReadProjectLocator(sourcePayload);
        values["id"] = cardId.ToString("D");
        TaskRequest query = NewStoreRequest("board.card.get", values);
        return await ExecuteStoreAsync(query, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TaskResponse> ExecuteStoreAsync(TaskRequest request, CancellationToken cancellationToken)
    {
        try { return await _executeStoreAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (TaskCommandException exception) { return TaskResponse.Failure(request, exception.Code, exception.Message, exception.Field); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { return FailureFromException(request, exception); }
    }

    private static TaskRequest NewStoreRequest(string operation, Dictionary<string, object?> payload) => new()
    {
        RequestId = Guid.NewGuid().ToString(),
        Operation = operation,
        Payload = JsonSerializer.SerializeToElement(payload, TaskProtocol.Options)
    };

    private static Dictionary<string, object?> ReadProjectLocator(JsonElement payload)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (payload.TryGetProperty("project", out JsonElement project)) values["project"] = project.Clone();
        if (payload.TryGetProperty("cwd", out JsonElement cwd)) values["cwd"] = cwd.Clone();
        return values;
    }

    private static bool TryReadCommentContext(object? data, [NotNullWhen(true)] out ProjectBoardCommentContext? context)
    {
        context = data as ProjectBoardCommentContext;
        if (context is not null) return true;
        try
        {
            JsonElement element = ToJsonElement(data);
            context = element.Deserialize<ProjectBoardCommentContext>(TaskProtocol.Options);
            return context is not null && context.Comment is not null && context.Registration is not null;
        }
        catch (Exception) { return false; }
    }

    private static bool TryReadCardDeleted(object? data, out bool deleted)
    {
        deleted = false;
        try
        {
            JsonElement element = ToJsonElement(data);
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("card", out JsonElement card)) element = card;
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("deletedAt", out JsonElement deletedAt)) return false;
            deleted = deletedAt.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
            return true;
        }
        catch (Exception) { return false; }
    }

    private static bool TryReadAddedCommentRouting(object? data, out string? commentId, out bool hasRouting)
    {
        commentId = null;
        hasRouting = false;
        try
        {
            JsonElement element = ToJsonElement(data);
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("comment", out JsonElement comment)) element = comment;
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("id", out JsonElement id) || id.ValueKind != JsonValueKind.String)
                return false;
            commentId = id.GetString();
            if (string.IsNullOrWhiteSpace(commentId)) return false;
            hasRouting = element.TryGetProperty("routing", out JsonElement routing) && routing.ValueKind == JsonValueKind.Object;
            return true;
        }
        catch (Exception) { return false; }
    }

    private static JsonElement ToJsonElement(object? value)
    {
        if (value is JsonElement element) return element;
        if (value is JsonDocument document) return document.RootElement;
        if (value is null) throw new InvalidOperationException("Data is null.");
        return JsonSerializer.SerializeToElement(value, value.GetType(), TaskProtocol.Options);
    }

    private static BoardAgentCommentContext? TryCreateAgentContext(ProjectBoardCommentContext projectContext, List<string> warnings)
    {
        if (projectContext.Comment.DeletedAt is not null)
        {
            warnings.Add("manualPullRequired: a deleted comment cannot be delivered.");
            return null;
        }
        if (projectContext.Comment.Routing is null)
        {
            warnings.Add("manualPullRequired: the comment has no saved routing and cannot be delivered to an Agent session.");
            return null;
        }
        return new BoardAgentCommentContext(
            projectContext.Comment.Id.ToString("D"),
            projectContext.CardId.ToString("D"),
            projectContext.Registration.Path,
            projectContext.Comment.Routing.Host,
            projectContext.Comment.Routing.SessionId,
            projectContext.Comment.Body);
    }

    private static BoardAgentDelivery CreateVirtualDelivery(BoardAgentCommentContext context) => new(
        context.CommentId,
        context.CardId,
        context.ProjectRoot,
        context.Host,
        context.Session,
        context.Session is null ? BoardAgentDeliveryStatuses.ManualPullRequired : BoardAgentDeliveryStatuses.NeedsConfirmation,
        BoardAgentService.GetConfirmationHash(context),
        context.Session is null ? "This comment has no exact session; pull it manually in the host." : null,
        DateTimeOffset.UtcNow);

    private static BoardCommentDeliveryResult Result(BoardAgentDelivery? delivery, ProjectBoardCommentContext context) => new(
        delivery,
        context.BoardRevision,
        context.BoardRevision,
        context.CommentRevision);

    private static bool ExpectedRevisionsMatch(JsonElement payload, ProjectBoardCommentContext context, out string? message)
    {
        if (payload.TryGetProperty("revision", out JsonElement revision) && revision.GetInt64() != context.BoardRevision)
        {
            message = "The board revision changed; read the current comment before continuing.";
            return false;
        }
        if (payload.TryGetProperty("commentRevision", out JsonElement commentRevision) && commentRevision.GetInt64() != context.CommentRevision)
        {
            message = "The comment revision changed; read the current comment before continuing.";
            return false;
        }
        message = null;
        return true;
    }

    private static void ValidateDeliveryPayload(TaskRequest request)
    {
        JsonElement payload = request.Payload!.Value;
        HashSet<string> allowed = request.Operation == "board.comment.delivery" ? DeliveryReadFields : DeliveryWriteFields;
        foreach (JsonProperty property in payload.EnumerateObject())
        {
            if (!allowed.Contains(property.Name)) throw Invalid("Unknown field.", property.Name);
            JsonElement value = property.Value;
            switch (property.Name)
            {
                case "project":
                case "cwd":
                    RequireNonEmptyString(value, property.Name, 32768);
                    break;
                case "commentId":
                    if (value.ValueKind != JsonValueKind.String || !Guid.TryParse(value.GetString(), out _))
                        throw Invalid("commentId must be a UUID.", property.Name);
                    break;
                case "revision":
                case "commentRevision":
                    if (!value.TryGetInt64(out long revision) || revision < 0) throw Invalid("A non-negative integer is required.", property.Name);
                    break;
                case "confirm":
                case "verifiedUnknown":
                    if (value.ValueKind is not JsonValueKind.True and not JsonValueKind.False) throw Invalid("A boolean is required.", property.Name);
                    break;
                case "confirmationToken":
                    RequireNonEmptyString(value, property.Name, 128);
                    break;
            }
        }

        if (!payload.TryGetProperty("commentId", out _)) throw Invalid("commentId is required.", "commentId");
        if (!payload.TryGetProperty("project", out _) && !payload.TryGetProperty("cwd", out _))
            throw Invalid("project or cwd is required.", "project");

        if (request.Operation == "board.comment.deliver")
        {
            bool confirm = payload.TryGetProperty("confirm", out JsonElement confirmValue) && confirmValue.GetBoolean();
            bool verifiedUnknown = payload.TryGetProperty("verifiedUnknown", out JsonElement verifiedValue) && verifiedValue.GetBoolean();
            bool hasToken = payload.TryGetProperty("confirmationToken", out _);
            if ((confirm || verifiedUnknown) && (!confirm || !hasToken))
                throw Invalid("confirm and confirmationToken are required for delivery or an unknown retry.", "confirmationToken");
        }
    }

    private static void RequireNonEmptyString(JsonElement value, string field, int maxLength)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } text || text.Length > maxLength || text.Any(char.IsControl))
            throw Invalid($"A non-empty string of at most {maxLength} characters is required.", field);
    }

    private static TaskCommandException Invalid(string message, string field) => new("INVALID_ARGUMENT", message, field);

    private static TaskResponse AddWarning(TaskResponse response, string warning) => AddWarning(response, [warning]);

    private static TaskResponse AddWarning(TaskResponse response, IReadOnlyList<string> warnings)
    {
        var combined = (response.Warnings ?? []).Concat(warnings).Where(warning => !string.IsNullOrWhiteSpace(warning)).ToArray();
        if (combined.Length > 0) response.Warnings = combined;
        return response;
    }

    private static object AddDeliveryField(object? data, BoardAgentDelivery delivery)
    {
        JsonNode? node = data is null
            ? new JsonObject()
            : JsonSerializer.SerializeToNode(data, data.GetType(), TaskProtocol.Options);
        if (node is not JsonObject result) result = new JsonObject { ["result"] = node };
        result["delivery"] = JsonSerializer.SerializeToNode(delivery, TaskProtocol.Options);
        return result;
    }

    private static TaskResponse CopyFailure(TaskRequest request, TaskResponse source) =>
        TaskResponse.Failure(request, source.Error?.Code ?? "INTERNAL_ERROR", source.Error?.Message ?? "The project board command failed.", source.Error?.Field);

    private static TaskResponse FailureFromException(TaskRequest request, Exception exception) => exception switch
    {
        TaskCommandException command => TaskResponse.Failure(request, command.Code, command.Message, command.Field),
        UnauthorizedAccessException => TaskResponse.Failure(request, "ACCESS_DENIED", "The local board command was denied."),
        IOException => TaskResponse.Failure(request, "IO_ERROR", "The local board command could not access its data."),
        _ => TaskResponse.Failure(request, "INTERNAL_ERROR", "The local board command failed.")
    };

    private static string SafeMessage(Exception exception)
    {
        string message = exception.Message;
        return message.Length <= 256 ? message : message[..256];
    }

    private static string Fingerprint(TaskRequest request)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            request.ProtocolVersion,
            request.Operation,
            payload = request.Payload
        }, TaskProtocol.Options);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static bool FixedHashEquals(string? candidate, string expected)
    {
        if (candidate is null || candidate.Length != expected.Length) return false;
        byte[] left;
        byte[] right;
        try
        {
            left = Convert.FromHexString(candidate);
            right = Convert.FromHexString(expected);
        }
        catch (FormatException) { return false; }
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    private void TrimRequestCache()
    {
        lock (_cacheGate)
        {
            int attempts = _requestOrder.Count;
            while (_requestCache.Count > RequestCacheLimit && attempts-- > 0 && _requestOrder.TryDequeue(out Guid oldest))
            {
                if (!_requestCache.TryGetValue(oldest, out RequestCacheEntry? entry)) continue;
                if (!entry.Completion.Task.IsCompleted)
                {
                    _requestOrder.Enqueue(oldest);
                    continue;
                }
                _requestCache.Remove(oldest);
            }
        }
    }

    private void RemoveCacheEntry(Guid requestId, RequestCacheEntry entry)
    {
        lock (_cacheGate)
        {
            if (_requestCache.TryGetValue(requestId, out RequestCacheEntry? current) && ReferenceEquals(current, entry))
                _requestCache.Remove(requestId);
        }
    }

    private sealed class RequestCacheEntry(string fingerprint)
    {
        public string Fingerprint { get; } = fingerprint;
        public TaskCompletionSource<TaskResponse> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
