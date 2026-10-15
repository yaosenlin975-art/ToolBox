using System;
using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using ToolBox.Core.Todo;

namespace ToolBox.Core.Boards;

public sealed partial class ProjectBoardStore
{
    private const int SchemaVersion = 2;
    private const int LegacySchemaVersion = 1;
    private const int RegistrySchemaVersion = 1;
    private const int RequestCacheLimit = 256;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        IgnoreReadOnlyProperties = true
    };
    private static readonly Lazy<ProjectBoardStore> InstanceValue = new(() => new ProjectBoardStore(
        GetDefaultRegistryRoot(),
        request => TodoStore.Instance.ExecuteTaskAsync(request)));

    private static string GetDefaultRegistryRoot()
    {
#if DEBUG
        var testRoot = Environment.GetEnvironmentVariable("TOOLBOX_TASK_TEST_ROOT");
        if (!string.IsNullOrWhiteSpace(testRoot)) return Path.GetFullPath(testRoot);
#endif
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ToolBox");
    }

    private readonly string registryPath;
    private readonly Func<TaskRequest, Task<TaskResponse>> executeTodoAsync;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<Guid, string> knownBoardHashes = [];
    private readonly Dictionary<Guid, string> knownCommentHashes = [];
    private bool pendingNotification;
    private readonly Dictionary<Guid, (string Fingerprint, TaskResponse Response)> requestCache = [];
    private readonly Queue<Guid> requestOrder = [];
    private IReadOnlyList<string>? currentWarnings;
    private ProjectBoardCard? claimConflictCard;
    private string? claimConflictType;

    public static ProjectBoardStore Instance => InstanceValue.Value;
    public event Action? Changed;

    public ProjectBoardStore(string registryRoot, Func<TaskRequest, Task<TaskResponse>>? todoExecutor = null)
    {
        registryPath = Path.Combine(Path.GetFullPath(registryRoot), "project-boards.json");
        executeTodoAsync = todoExecutor ?? (request => TodoStore.Instance.ExecuteTaskAsync(request));
    }

    public async Task<TaskResponse> ExecuteAsync(TaskRequest request)
    {
        if (request == null) return TaskResponse.Failure(null, "INVALID_ARGUMENT", "A request is required.", "request");
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Guid requestId = Guid.TryParse(request.RequestId, out Guid parsedId) ? parsedId : Guid.Empty;
            string fingerprint = Fingerprint(request);
            if (requestId != Guid.Empty && requestCache.TryGetValue(requestId, out var cached))
            {
                return cached.Fingerprint == fingerprint
                    ? cached.Response
                    : TaskResponse.Failure(request, "CONFLICT", "requestId was already used with different content.", "requestId");
            }

            TaskResponse response;
            currentWarnings = null;
            claimConflictCard = null;
            claimConflictType = null;
            try
            {
                ValidateRequest(request);
                _currentRequest = request;
                response = await ExecuteCoreAsync(request).ConfigureAwait(false);
                if (currentWarnings is { Count: > 0 }) response.Warnings = currentWarnings;
                if (JsonSerializer.SerializeToUtf8Bytes(response, TaskProtocol.Options).Length > TaskProtocol.MaxBytes)
                    response = TaskResponse.Failure(request, "RESULT_TOO_LARGE", "The project board result exceeds the 4 MiB response limit.");
            }
            catch (TaskCommandException ex)
            {
                response = claimConflictCard is { } conflict
                    ? ClaimConflictResponse(conflict, claimConflictType ?? "CLAIMED_BY_OTHER")
                    : TaskResponse.Failure(request, ex.Code, ex.Message, ex.Field);
            }
            catch (UnauthorizedAccessException ex) { LogIoFailure(ex); response = TaskResponse.Failure(request, "IO_ERROR", "Project board data could not be accessed."); }
            catch (IOException ex) { LogIoFailure(ex); response = TaskResponse.Failure(request, "IO_ERROR", "Project board data could not be accessed."); }
            catch (Exception) { response = TaskResponse.Failure(request, "INTERNAL_ERROR", "The project board command failed."); }

            if (response.Ok && currentWarnings is { Count: > 0 }) response.Warnings = currentWarnings;
            _currentRequest = null;
            if (requestId != Guid.Empty) CacheResponse(requestId, fingerprint, response);
            return response;
        }
        finally
        {
            currentWarnings = null;
            claimConflictCard = null;
            claimConflictType = null;
            bool notify = pendingNotification;
            pendingNotification = false;
            gate.Release();
            if (notify) RaiseChanged();
        }
    }

    private async Task<TaskResponse> ExecuteCoreAsync(TaskRequest request)
    {
        JsonElement payload = request.Payload!.Value;
        return request.Operation switch
        {
            "project.register" => Register(payload),
            "project.list" => ProjectList(),
            "project.get" => ProjectGet(payload),
            "project.unregister" => Unregister(payload),
            "board.get" => BoardGet(payload),
            "board.column.list" => ColumnList(payload),
            "board.column.create" => ColumnCreate(payload),
            "board.column.update" => ColumnUpdate(payload),
            "board.column.delete" => ColumnDelete(payload),
            "board.column.move" => ColumnMove(payload),
            "board.card.list" => CardList(payload),
            "board.card.get" => CardGet(payload),
            "board.card.create" => CardCreate(payload),
            "board.card.update" => CardUpdate(payload),
            "board.card.move" => CardMove(payload),
            "board.card.delete" => CardDelete(payload),
            "board.card.restore" => CardRestore(payload),
            "board.card.claim" => CardStatusAction(payload, "claim"),
            "board.card.release" => CardStatusAction(payload, "release"),
            "board.card.submit" => CardStatusAction(payload, "submit"),
            "board.card.close" => CardStatusAction(payload, "close"),
            "board.card.reopen" => CardStatusAction(payload, "reopen"),
            "board.comment.add" => CommentAdd(payload),
            "board.comment.get" => CommentGet(payload),
            "board.comment.list" => CommentList(payload),
            "board.comment.update" => CommentUpdate(payload),
            "board.comment.delete" => CommentDelete(payload),
            "board.comment.inbox" => CommentInbox(payload),
            "board.comment.ack" => CommentAck(payload),
            "board.comment.bind" => CommentBind(payload),
            "board.card.add-to-todo" => await AddToTodoAsync(request, payload).ConfigureAwait(false),
            _ => throw Invalid("Unknown operation.", "operation")
        };
    }

    private TaskResponse Register(JsonElement payload)
    {
        string path = NormalizePath(RequiredString(payload, "path"));
        bool init = OptionalBoolean(payload, "init") ?? false;
        bool replacePath = OptionalBoolean(payload, "replacePath") ?? false;
        if (!Directory.Exists(path)) throw new TaskCommandException("NOT_FOUND", "The project directory was not found.", "path");

        string boardPath = GetBoardPath(path, allowMissingDirectory: init);
        ProjectBoard board;
        bool initialize = !File.Exists(boardPath);
        if (initialize)
        {
            if (!init) throw new TaskCommandException("NOT_FOUND", "The project board does not exist; register with init=true to create it.", "init");
            var columns = new[]
            {
                new ProjectBoardColumn(Guid.NewGuid(), "待办", 0, "notStarted"),
                new ProjectBoardColumn(Guid.NewGuid(), "进行中", 1, "inProgress"),
                new ProjectBoardColumn(Guid.NewGuid(), "待验收", 2, "pendingReview"),
                new ProjectBoardColumn(Guid.NewGuid(), "完成", 3, "closed")
            };
            board = new ProjectBoard(SchemaVersion, Guid.NewGuid(), new DirectoryInfo(path).Name, 1, columns, []);
        }
        else board = ReadBoard(path).Board;

        RegistrySnapshot registry = ReadRegistry();
        var samePath = registry.State.Projects.FirstOrDefault(p => PathsEqual(p.Path, path));
        if (samePath != null && samePath.BoardId != board.BoardId)
            throw new TaskCommandException("CONFLICT", "This path is already registered with a different board ID.", "path");

        var sameBoard = registry.State.Projects.FirstOrDefault(p => p.BoardId == board.BoardId);
        if (sameBoard != null && !PathsEqual(sameBoard.Path, path) && !replacePath)
            throw new TaskCommandException("CONFLICT", "This board ID is registered at another path; set replacePath=true to update it.", "replacePath");

        var registration = new ProjectBoardRegistration(board.BoardId, board.Name, path);
        EnsureResponseFits(TaskResponse.Success(_currentRequest!, registration));
        if (initialize) SaveNewBoard(path, board, registration);
        if (sameBoard != null) registry.State.Projects[registry.State.Projects.IndexOf(sameBoard)] = registration;
        else registry.State.Projects.Add(registration);
        WriteRegistry(registry.State, registry.Hash);
        NotifyChanged();
        return TaskResponse.Success(_currentRequest!, registration);
    }

    private TaskRequest? _currentRequest;

    private TaskResponse ProjectList()
    {
        var registry = ReadRegistry();
        return TaskResponse.Success(_currentRequest!, registry.State.Projects.ToArray());
    }

    private TaskResponse ProjectGet(JsonElement payload)
    {
        var (registration, _) = ResolveProject(payload);
        return TaskResponse.Success(_currentRequest!, registration);
    }

    private TaskResponse Unregister(JsonElement payload)
    {
        RegistrySnapshot registry = ReadRegistry();
        ProjectBoardRegistration registration = ResolveProject(payload, registry.State);
        TaskResponse result = TaskResponse.Success(_currentRequest!, registration);
        EnsureResponseFits(result);
        registry.State.Projects.RemoveAll(p => p.BoardId == registration.BoardId);
        registry.State.TodoLinks.RemoveAll(link => link.BoardId == registration.BoardId);
        WriteRegistry(registry.State, registry.Hash);
        NotifyChanged();
        return result;
    }

    private TaskResponse BoardGet(JsonElement payload)
    {
        var (registration, snapshot) = ReadProjectBoard(payload);
        return TaskResponse.Success(_currentRequest!, ProjectBoardResponse(snapshot.Board));
    }

    private TaskResponse ColumnList(JsonElement payload)
    {
        var (_, snapshot) = ReadProjectBoard(payload);
        return TaskResponse.Success(_currentRequest!, new { columns = snapshot.Board.Columns, revision = snapshot.Board.Revision, boardRevision = snapshot.Board.Revision, commentRevision = snapshot.Board.CommentRevision });
    }

    private TaskResponse ColumnCreate(JsonElement payload)
    {
        var (registration, snapshot) = ReadProjectBoard(payload);
        CheckRevision(payload, snapshot.Board);
        string title = RequiredText(payload, "title");
        var columns = snapshot.Board.Columns.OrderBy(c => c.Order).ToList();
        int order = OptionalInt(payload, "order") ?? columns.Count;
        if (order < 0) throw Invalid("order must be non-negative.", "order");
        order = Math.Min(order, columns.Count);
        string? status = OptionalStatus(payload, "status");
        CheckUniqueColumnStatus(columns, status, null);
        var created = new ProjectBoardColumn(Guid.NewGuid(), title, order, status);
        columns.Insert(order, created);
        columns = ReorderColumns(columns);
        var updated = snapshot.Board with { Columns = columns, Revision = NextRevision(snapshot.Board) };
        TaskResponse result = TaskResponse.Success(_currentRequest!, new { column = updated.Columns.Single(c => c.Id == created.Id), revision = updated.Revision, boardRevision = updated.Revision, commentRevision = updated.CommentRevision });
        SaveBoard(registration.Path, snapshot, updated, result);
        NotifyChanged();
        return result;
    }

    private TaskResponse ColumnUpdate(JsonElement payload)
    {
        var (registration, snapshot) = ReadProjectBoard(payload);
        CheckRevision(payload, snapshot.Board);
        Guid id = RequiredGuid(payload, "id");
        var columns = snapshot.Board.Columns.OrderBy(c => c.Order).ToList();
        int index = columns.FindIndex(c => c.Id == id);
        if (index < 0) throw new TaskCommandException("NOT_FOUND", "Board column not found.", "id");
        var column = columns[index];
        if (payload.TryGetProperty("title", out _)) column = column with { Title = RequiredText(payload, "title") };
        if (payload.TryGetProperty("status", out _))
        {
            string? status = OptionalStatus(payload, "status");
            CheckUniqueColumnStatus(columns, status, id);
            column = column with { Status = status };
        }
        if (payload.TryGetProperty("order", out _))
        {
            int order = RequiredInt(payload, "order");
            if (order < 0) throw Invalid("order must be non-negative.", "order");
            columns.RemoveAt(index);
            columns.Insert(Math.Min(order, columns.Count), column);
            columns = ReorderColumns(columns);
            column = columns.Single(c => c.Id == id);
        }
        else columns[index] = column;
        var updated = snapshot.Board with { Columns = ReorderColumns(columns), Revision = NextRevision(snapshot.Board) };
        TaskResponse result = TaskResponse.Success(_currentRequest!, new { column = updated.Columns.Single(c => c.Id == id), revision = updated.Revision, boardRevision = updated.Revision, commentRevision = updated.CommentRevision });
        SaveBoard(registration.Path, snapshot, updated, result);
        NotifyChanged();
        return result;
    }

    private TaskResponse ColumnDelete(JsonElement payload)
    {
        var (registration, snapshot) = ReadProjectBoard(payload);
        CheckRevision(payload, snapshot.Board);
        Guid id = RequiredGuid(payload, "id");
        var deleted = snapshot.Board.Columns.FirstOrDefault(c => c.Id == id)
            ?? throw new TaskCommandException("NOT_FOUND", "Board column not found.", "id");
        if (snapshot.Board.Columns.Count == 1) throw new TaskCommandException("CONFLICT", "The last board column cannot be deleted.", "id");
        var cards = snapshot.Board.Cards.OrderBy(c => c.Order).ThenBy(c => c.Id).ToList();
        var movedCards = cards.Where(c => c.ColumnId == id).ToArray();
        Guid? targetId = OptionalGuid(payload, "targetColumnId");
        if (movedCards.Length > 0 && targetId == null) throw Invalid("targetColumnId is required when the column contains cards.", "targetColumnId");
        if (targetId == id) throw Invalid("targetColumnId must be a different column.", "targetColumnId");
        ProjectBoardColumn? targetColumn = targetId is Guid columnId
            ? snapshot.Board.Columns.FirstOrDefault(column => column.Id == columnId)
            : null;
        if (targetId != null && targetColumn == null)
            throw new TaskCommandException("NOT_FOUND", "Target board column not found.", "targetColumnId");
        var identity = ReadIdentity(payload);
        DateTime now = DateTime.UtcNow;
        foreach (ProjectBoardCard movedCard in movedCards) EnsureWritable(movedCard, identity, now);

        var remainingColumns = snapshot.Board.Columns.Where(c => c.Id != id).OrderBy(c => c.Order).ToList();
        var newCards = cards.Where(c => c.ColumnId != id).ToList();
        if (targetId != null)
        {
            int nextOrder = newCards.Where(c => c.ColumnId == targetId.Value).Select(c => c.Order).DefaultIfEmpty(-1).Max() + 1;
            foreach (var card in movedCards.OrderBy(c => c.Order).ThenBy(c => c.Id))
                newCards.Add(card with { ColumnId = targetId.Value, Order = nextOrder++, UpdatedAt = now });
        }
        newCards = ReorderCards(newCards, remainingColumns);
        var updated = snapshot.Board with
        {
            Columns = ReorderColumns(remainingColumns),
            Cards = newCards,
            Revision = NextRevision(snapshot.Board)
        };
        TaskResponse result = TaskResponse.Success(_currentRequest!, new { column = deleted, revision = updated.Revision, boardRevision = updated.Revision, commentRevision = updated.CommentRevision });
        SaveBoard(registration.Path, snapshot, updated, result);
        NotifyChanged();
        return result;
    }

    private TaskResponse ColumnMove(JsonElement payload)
    {
        var (registration, snapshot) = ReadProjectBoard(payload);
        CheckRevision(payload, snapshot.Board);
        Guid id = RequiredGuid(payload, "id");
        int order = RequiredInt(payload, "order");
        if (order < 0) throw Invalid("order must be non-negative.", "order");
        var columns = snapshot.Board.Columns.OrderBy(c => c.Order).ToList();
        int index = columns.FindIndex(c => c.Id == id);
        if (index < 0) throw new TaskCommandException("NOT_FOUND", "Board column not found.", "id");
        var moved = columns[index];
        columns.RemoveAt(index);
        columns.Insert(Math.Min(order, columns.Count), moved);
        columns = ReorderColumns(columns);
        var updated = snapshot.Board with { Columns = columns, Revision = NextRevision(snapshot.Board) };
        TaskResponse result = TaskResponse.Success(_currentRequest!, new { column = columns.Single(c => c.Id == id), revision = updated.Revision, boardRevision = updated.Revision, commentRevision = updated.CommentRevision });
        SaveBoard(registration.Path, snapshot, updated, result);
        NotifyChanged();
        return result;
    }

    private TaskResponse CardList(JsonElement payload)
    {
        var (_, snapshot) = ReadProjectBoard(payload);
        bool includeDeleted = OptionalBoolean(payload, "includeDeleted") ?? false;
        Guid? columnId = OptionalGuid(payload, "columnId");
        string? status = OptionalStatus(payload, "status");
        Guid? parentId = OptionalNullableGuid(payload, "parentId");
        bool hasParentFilter = payload.TryGetProperty("parentId", out _);
        bool rootsOnly = OptionalBoolean(payload, "rootsOnly") ?? false;
        bool available = OptionalBoolean(payload, "available") ?? false;
        bool mine = OptionalBoolean(payload, "mine") ?? false;
        string? actor = mine ? RequiredText(payload, "actor") : OptionalString(payload, "actor");
        string? host = mine ? RequiredText(payload, "host") : OptionalString(payload, "host");
        string? session = OptionalSession(payload);
        DateTime now = DateTime.UtcNow;
        if (columnId != null && !snapshot.Board.Columns.Any(c => c.Id == columnId.Value))
            throw new TaskCommandException("NOT_FOUND", "Board column not found.", "columnId");
        var columnOrder = snapshot.Board.Columns.ToDictionary(column => column.Id, column => column.Order);
        var cards = snapshot.Board.Cards.Where(c =>
                (includeDeleted || c.DeletedAt == null) &&
                (columnId == null || c.ColumnId == columnId.Value) &&
                (status == null || c.Status == status) &&
                (!hasParentFilter || c.ParentId == parentId) &&
                (!rootsOnly || c.ParentId == null) &&
                (!available || (c.DeletedAt == null && (c.Status == "notStarted" || (c.Status == "inProgress" && c.Claim!.ExpiresAt <= now)))) &&
                (!mine || (c.Claim != null && SameIdentity(c.Claim, host!, actor!, session!))))
            .OrderBy(c => columnOrder[c.ColumnId]).ThenBy(c => c.ParentId).ThenBy(c => c.Order).ThenBy(c => c.Id)
            .Select(card => ProjectCardResponse(snapshot.Board, card)).ToArray();
        return TaskResponse.Success(_currentRequest!, new { cards, revision = snapshot.Board.Revision, boardRevision = snapshot.Board.Revision, commentRevision = snapshot.Board.CommentRevision });
    }

    private TaskResponse CardGet(JsonElement payload)
    {
        var (_, snapshot) = ReadProjectBoard(payload);
        Guid id = RequiredGuid(payload, "id");
        var card = snapshot.Board.Cards.FirstOrDefault(c => c.Id == id)
            ?? throw new TaskCommandException("NOT_FOUND", "Board card not found.", "id");
        var children = snapshot.Board.Cards.Where(c => c.ParentId == id && c.DeletedAt == null).ToArray();
        return TaskResponse.Success(_currentRequest!, new
        {
            card = ProjectCardResponse(snapshot.Board, card),
            commentCount = card.Comments?.Count(comment => comment.DeletedAt == null) ?? 0,
            attachmentCount = card.Attachments?.Count ?? 0,
            childCount = children.Length,
            childClosedCount = children.Count(c => c.Status == "closed"),
            revision = snapshot.Board.Revision,
            boardRevision = snapshot.Board.Revision,
            commentRevision = snapshot.Board.CommentRevision
        });
    }

    private TaskResponse CardCreate(JsonElement payload)
    {
        var (registration, snapshot) = ReadProjectBoard(payload);
        CheckRevision(payload, snapshot.Board);
        string title = RequiredText(payload, "title");
        Guid columnId = OptionalGuid(payload, "columnId") ??
            FindMappedColumn(snapshot.Board.Columns, "notStarted")?.Id ??
            snapshot.Board.Columns.FirstOrDefault(column => column.Status == null)?.Id ??
            snapshot.Board.Columns.OrderBy(column => column.Order).First().Id;
        ProjectBoardColumn targetColumn = snapshot.Board.Columns.FirstOrDefault(c => c.Id == columnId)
            ?? throw new TaskCommandException("NOT_FOUND", "Board column not found.", "columnId");
        if (targetColumn.Status is not (null or "notStarted"))
            throw new TaskCommandException("CONFLICT", "A new card must start in a not-started or unmapped column.", "columnId");
        Guid? parentId = OptionalNullableGuid(payload, "parentId");
        DateTime now = DateTime.UtcNow;
        if (parentId is Guid parent)
        {
            ProjectBoardCard parentCard = FindCard(snapshot.Board, parent);
            EnsureCardActive(parentCard, "parentId");
            if (parentCard.Status == "closed") throw new TaskCommandException("CONFLICT", "A closed card cannot receive an open child.", "parentId");
            EnsureWritable(parentCard, ReadIdentity(payload), now);
        }
        int order = OptionalInt(payload, "order") ?? snapshot.Board.Cards.Where(c => c.ColumnId == columnId && c.ParentId == parentId).Select(c => c.Order).DefaultIfEmpty(-1).Max() + 1;
        if (order < 0) throw Invalid("order must be non-negative.", "order");
        var cards = snapshot.Board.Cards.ToList();
        var inColumn = cards.Where(c => c.ColumnId == columnId && c.ParentId == parentId).OrderBy(c => c.Order).ThenBy(c => c.Id).ToList();
        int insertAt = Math.Min(order, inColumn.Count);
        var card = new ProjectBoardCard(Guid.NewGuid(), title,
            OptionalString(payload, "description") ?? "", columnId, insertAt,
            OptionalInt(payload, "priority") ?? 0, OptionalTags(payload), OptionalDate(payload, "dueDate"), now, now, null,
            "notStarted", parentId, null, null, [], []);
        inColumn.Insert(insertAt, card);
        cards.RemoveAll(c => c.ColumnId == columnId && c.ParentId == parentId);
        cards.AddRange(ReorderOneColumn(inColumn));
        var updated = snapshot.Board with { Cards = ReorderCards(cards, snapshot.Board.Columns), Revision = NextRevision(snapshot.Board) };
        TaskResponse result = TaskResponse.Success(_currentRequest!, new { card = updated.Cards.Single(c => c.Id == card.Id), revision = updated.Revision, boardRevision = updated.Revision, commentRevision = updated.CommentRevision });
        SaveBoard(registration.Path, snapshot, updated, result);
        NotifyChanged();
        return result;
    }

    private TaskResponse CardUpdate(JsonElement payload)
    {
        var (registration, snapshot) = ReadProjectBoard(payload);
        CheckRevision(payload, snapshot.Board);
        Guid id = RequiredGuid(payload, "id");
        ProjectBoardCard card = FindCard(snapshot.Board, id);
        EnsureCardActive(card, "id");
        var identity = ReadIdentity(payload);
        DateTime now = DateTime.UtcNow;
        EnsureWritable(card, identity, now);
        bool clearParent = OptionalBoolean(payload, "clearParentId") ?? false;
        if (clearParent && payload.TryGetProperty("parentId", out _)) throw Invalid("parentId and clearParentId cannot be used together.", "parentId");
        Guid? targetParent = clearParent ? null : payload.TryGetProperty("parentId", out _) ? OptionalNullableGuid(payload, "parentId") : card.ParentId;
        if (targetParent != card.ParentId)
        {
            foreach (ProjectBoardCard affected in GetSubtree(snapshot.Board.Cards, id)) EnsureWritable(affected, identity, now);
            if (card.ParentId is Guid oldParentId) EnsureWritable(FindCard(snapshot.Board, oldParentId), identity, now);
            if (targetParent is Guid parentId)
            {
                ProjectBoardCard parent = FindCard(snapshot.Board, parentId);
                EnsureCardActive(parent, "parentId");
                if (parentId == id || IsDescendant(snapshot.Board.Cards, id, parentId)) throw new TaskCommandException("CYCLE_DETECTED", "A card cannot become its own descendant.", "parentId");
                if (parent.Status == "closed" && card.Status != "closed") throw new TaskCommandException("CONFLICT", "A closed card cannot receive an open child.", "parentId");
                EnsureWritable(parent, identity, now);
            }
        }
        card = card with
        {
            Title = payload.TryGetProperty("title", out _) ? RequiredText(payload, "title") : card.Title,
            Description = payload.TryGetProperty("description", out _) ? RequiredString(payload, "description") : card.Description,
            Priority = payload.TryGetProperty("priority", out _) ? RequiredInt(payload, "priority") : card.Priority,
            Tags = payload.TryGetProperty("tags", out _) ? RequiredTags(payload, "tags") : card.Tags,
            DueDate = payload.TryGetProperty("dueDate", out _) ? OptionalDate(payload, "dueDate") : card.DueDate,
            ParentId = targetParent,
            UpdatedAt = now
        };
        Guid targetColumn = payload.TryGetProperty("columnId", out _) ? RequiredGuid(payload, "columnId") : card.ColumnId;
        ProjectBoardColumn targetColumnModel = snapshot.Board.Columns.FirstOrDefault(c => c.Id == targetColumn)
            ?? throw new TaskCommandException("NOT_FOUND", "Board column not found.", "columnId");
        if (targetColumn != card.ColumnId && targetColumnModel.Status != null && targetColumnModel.Status != card.Status)
            throw new TaskCommandException("CONFLICT", "A mapped status column requires an explicit status transition.", "columnId");
        var cards = snapshot.Board.Cards.Where(c => c.Id != id).ToList();
        var targetCards = cards.Where(c => c.ColumnId == targetColumn && c.ParentId == targetParent).OrderBy(c => c.Order).ThenBy(c => c.Id).ToList();
        int targetOrder = payload.TryGetProperty("order", out _) ? RequiredInt(payload, "order") :
            targetColumn == card.ColumnId && targetParent == card.ParentId ? Math.Min(card.Order, targetCards.Count) : targetCards.Count;
        if (targetOrder < 0) throw Invalid("order must be non-negative.", "order");
        targetOrder = Math.Min(targetOrder, targetCards.Count);
        targetCards.Insert(targetOrder, card with { ColumnId = targetColumn, UpdatedAt = now });
        cards.RemoveAll(c => c.ColumnId == targetColumn && c.ParentId == targetParent);
        cards.AddRange(ReorderOneColumn(targetCards));
        var updated = snapshot.Board with { Cards = ReorderCards(cards, snapshot.Board.Columns), Revision = NextRevision(snapshot.Board) };
        TaskResponse result = TaskResponse.Success(_currentRequest!, new { card = updated.Cards.Single(c => c.Id == id), revision = updated.Revision, boardRevision = updated.Revision, commentRevision = updated.CommentRevision });
        SaveBoard(registration.Path, snapshot, updated, result);
        NotifyChanged();
        return result;
    }

    private TaskResponse CardMove(JsonElement payload)
    {
        var (registration, snapshot) = ReadProjectBoard(payload);
        CheckRevision(payload, snapshot.Board);
        Guid id = RequiredGuid(payload, "id");
        Guid targetId = RequiredGuid(payload, "targetColumnId");
        if (!snapshot.Board.Columns.Any(c => c.Id == targetId)) throw new TaskCommandException("NOT_FOUND", "Board column not found.", "targetColumnId");
        ProjectBoardCard card = FindCard(snapshot.Board, id);
        EnsureCardActive(card, "id");
        DateTime now = DateTime.UtcNow;
        BoardIdentity? identity = ReadIdentity(payload);
        string? transition = OptionalTransition(payload);
        if (transition != null) identity = ReadRequiredIdentity(payload);
        if (transition != "claim") EnsureWritable(card, identity, now);
        ProjectBoardColumn targetColumn = snapshot.Board.Columns.Single(c => c.Id == targetId);
        if (transition == "claim" && card.Status == "inProgress" && card.Claim is { } activeClaim && activeClaim.ExpiresAt > now && !SameIdentity(activeClaim, identity!))
            return ClaimConflictResponse(card, "CLAIMED_BY_OTHER");
        if (targetId != card.ColumnId && targetColumn.Status != null && targetColumn.Status != card.Status && transition == null)
            throw new TaskCommandException("CONFLICT", "A mapped status column requires an explicit status transition.", "transition");
        if (transition != null)
        {
            card = ApplyTransition(snapshot.Board, card, transition, identity!, now, OptionalInt(payload, "ttlMinutes"));
            if (targetColumn.Status != null && targetColumn.Status != card.Status)
                throw new TaskCommandException("CONFLICT", "The transition does not match the target status column.", "transition");
        }
        var cards = snapshot.Board.Cards.Where(c => c.Id != id).ToList();
        var target = cards.Where(c => c.ColumnId == targetId && c.ParentId == card.ParentId).OrderBy(c => c.Order).ThenBy(c => c.Id).ToList();
        int order = OptionalInt(payload, "order") ?? target.Count;
        if (order < 0) throw Invalid("order must be non-negative.", "order");
        order = Math.Min(order, target.Count);
        target.Insert(order, card with { ColumnId = targetId, UpdatedAt = now });
        cards.RemoveAll(c => c.ColumnId == targetId && c.ParentId == card.ParentId);
        cards.AddRange(ReorderOneColumn(target));
        var updated = snapshot.Board with { Cards = ReorderCards(cards, snapshot.Board.Columns), Revision = NextRevision(snapshot.Board) };
        TaskResponse result = TaskResponse.Success(_currentRequest!, new { card = updated.Cards.Single(c => c.Id == id), revision = updated.Revision, boardRevision = updated.Revision, commentRevision = updated.CommentRevision });
        SaveBoard(registration.Path, snapshot, updated, result);
        NotifyChanged();
        return result;
    }

    private TaskResponse CardDelete(JsonElement payload) => SetCardDeleted(payload, true);
    private TaskResponse CardRestore(JsonElement payload) => SetCardDeleted(payload, false);

    private TaskResponse SetCardDeleted(JsonElement payload, bool deleted)
    {
        var (registration, snapshot) = ReadProjectBoard(payload);
        CheckRevision(payload, snapshot.Board);
        Guid id = RequiredGuid(payload, "id");
        ProjectBoardCard card = FindCard(snapshot.Board, id);
        if ((card.DeletedAt != null) == deleted)
            throw new TaskCommandException("CONFLICT", deleted ? "Board card is already deleted." : "Board card is not deleted.", "id");
        ProjectBoardCard[] subtree = GetSubtree(snapshot.Board.Cards, id).ToArray();
        DateTime now = DateTime.UtcNow;
        HashSet<Guid> changedIds;
        if (!deleted)
        {
            DateTime deletedAt = card.DeletedAt!.Value;
            changedIds = [];
            foreach (ProjectBoardCard affected in subtree)
            {
                bool parentRestored = affected.Id == id || (affected.ParentId is Guid parentId && changedIds.Contains(parentId));
                if (parentRestored && affected.DeletedAt == deletedAt) changedIds.Add(affected.Id);
            }
            bool closedAncestor = false;
            for (Guid? parentId = card.ParentId; parentId != null;)
            {
                ProjectBoardCard parent = FindCard(snapshot.Board, parentId.Value);
                if (parent.DeletedAt != null) throw new TaskCommandException("CONFLICT", "Restore deleted ancestors before restoring this card.", "id");
                closedAncestor |= parent.Status == "closed";
                parentId = parent.ParentId;
            }
            var closedByAncestor = new Dictionary<Guid, bool>();
            foreach (ProjectBoardCard affected in subtree)
            {
                if (!changedIds.Contains(affected.Id)) continue;
                bool hasClosedAncestor = affected.Id == id
                    ? closedAncestor
                    : closedByAncestor[affected.ParentId!.Value];
                if (hasClosedAncestor && affected.Status != "closed")
                    throw new TaskCommandException("CONFLICT", "Reopen closed ancestors before restoring this card.", "id");
                closedByAncestor[affected.Id] = hasClosedAncestor || affected.Status == "closed";
            }
        }
        else
        {
            while (subtree.Any(affected => affected.DeletedAt == now)) now = now.AddTicks(1);
            changedIds = subtree.Where(affected => affected.DeletedAt == null).Select(affected => affected.Id).ToHashSet();
        }
        var identity = ReadIdentity(payload);
        foreach (ProjectBoardCard affected in subtree.Where(affected => changedIds.Contains(affected.Id))) EnsureWritable(affected, identity, now);
        var cards = snapshot.Board.Cards.Select(item => changedIds.Contains(item.Id)
            ? item with { DeletedAt = deleted ? now : null, UpdatedAt = now }
            : item).ToArray();
        var updated = snapshot.Board with { Cards = cards, Revision = NextRevision(snapshot.Board) };
        TaskResponse result = TaskResponse.Success(_currentRequest!, new { card = cards.Single(c => c.Id == id), affectedCount = changedIds.Count, revision = updated.Revision, boardRevision = updated.Revision, commentRevision = updated.CommentRevision });
        SaveBoard(registration.Path, snapshot, updated, result);
        NotifyChanged();
        return result;
    }

    private TaskResponse CardStatusAction(JsonElement payload, string action)
    {
        var (registration, snapshot) = ReadProjectBoard(payload);
        CheckRevision(payload, snapshot.Board);
        Guid id = RequiredGuid(payload, "id");
        ProjectBoardCard card = FindCard(snapshot.Board, id);
        BoardIdentity identity = ReadRequiredIdentity(payload);
        DateTime now = DateTime.UtcNow;
        EnsureCardActive(card, "id");
        if (action == "claim" && card.Status == "inProgress" && card.Claim is { ExpiresAt: var expiresAt } && expiresAt > now && !SameIdentity(card.Claim, identity))
            return ClaimConflictResponse(card, "CLAIMED_BY_OTHER");
        int? ttlMinutes = OptionalInt(payload, "ttlMinutes");
        ProjectBoardCard transitioned = ApplyTransition(snapshot.Board, card, action, identity, now, ttlMinutes);
        if (action == "close")
        {
            if (GetSubtree(snapshot.Board.Cards, id).Skip(1).Any(descendant => descendant.DeletedAt == null && descendant.Status != "closed"))
                throw new TaskCommandException("CONFLICT", "All active descendants must be closed before closing this card.", "id");
        }
        if (action == "reopen" && card.ParentId is Guid parentId)
        {
            for (ProjectBoardCard parent = FindCard(snapshot.Board, parentId); ;)
            {
                if (parent.DeletedAt != null) throw new TaskCommandException("CONFLICT", "Restore deleted ancestors before reopening this card.", "id");
                if (parent.Status == "closed") throw new TaskCommandException("CONFLICT", "Reopen closed ancestors before reopening this card.", "id");
                if (parent.ParentId is not Guid nextParentId) break;
                parent = FindCard(snapshot.Board, nextParentId);
            }
        }
        Guid targetColumnId = OptionalGuid(payload, "columnId") ?? FindMappedColumn(snapshot.Board.Columns, transitioned.Status)?.Id ?? transitioned.ColumnId;
        ProjectBoardColumn targetColumn = snapshot.Board.Columns.FirstOrDefault(c => c.Id == targetColumnId)
            ?? throw new TaskCommandException("NOT_FOUND", "Board column not found.", "columnId");
        if (targetColumn.Status != null && targetColumn.Status != transitioned.Status)
            throw new TaskCommandException("CONFLICT", "The target column does not map to the resulting status.", "columnId");
        var updatedCards = RelocateCard(snapshot.Board, transitioned with { UpdatedAt = now }, targetColumnId, OptionalInt(payload, "order"));
        var updated = snapshot.Board with { Cards = updatedCards, Revision = NextRevision(snapshot.Board) };
        TaskResponse result = TaskResponse.Success(_currentRequest!, new { card = updated.Cards.Single(c => c.Id == id), revision = updated.Revision, boardRevision = updated.Revision, commentRevision = updated.CommentRevision });
        SaveBoard(registration.Path, snapshot, updated, result);
        NotifyChanged();
        return result;
    }

    private TaskResponse ClaimConflictResponse(ProjectBoardCard card, string type)
    {
        TaskResponse response = TaskResponse.Failure(_currentRequest, "CONFLICT", "Card is being worked on by another agent.", "id");
        response.Data = new
        {
            conflictType = type,
            cardId = card.Id,
            owner = card.Assignee,
            expiresAt = card.Claim?.ExpiresAt
        };
        return response;
    }

    private static ProjectBoardCard ApplyTransition(
        ProjectBoard board,
        ProjectBoardCard card,
        string action,
        BoardIdentity identity,
        DateTime now,
        int? ttlMinutes)
    {
        switch (action)
        {
            case "claim":
                int ttl = ttlMinutes ?? 30;
                if (ttl is < 5 or > 480) throw Invalid("ttlMinutes must be between 5 and 480.", "ttlMinutes");
                if (card.Status == "inProgress")
                {
                    if (card.Claim == null) throw Corrupt("An in-progress card has no claim.");
                    if (card.Claim.ExpiresAt > now && !SameIdentity(card.Claim, identity))
                        throw new TaskCommandException("CLAIMED_BY_OTHER", "Card is being worked on by another agent.", "id");
                    if (card.Claim.ExpiresAt > now && SameIdentity(card.Claim, identity))
                    {
                        ProjectBoardClaim heartbeat = card.Claim with { HeartbeatAt = now, ExpiresAt = now.AddMinutes(ttl) };
                        return card with { Claim = heartbeat, UpdatedAt = now };
                    }
                }
                else if (card.Status != "notStarted")
                    throw new TaskCommandException("CONFLICT", "Only a not-started or expired in-progress card can be claimed.", "id");
                var claim = new ProjectBoardClaim(identity.HostId, identity.ActorId, identity.SessionId, now, now, now.AddMinutes(ttl));
                var assignee = new ProjectBoardAssignee("agent", identity.ActorId, identity.ActorId, identity.HostId, identity.SessionId);
                return card with { Status = "inProgress", Claim = claim, Assignee = assignee, UpdatedAt = now };

            case "release":
                EnsureOwner(card, identity, now);
                return card with { Status = "notStarted", Claim = null, Assignee = null, UpdatedAt = now };

            case "submit":
                EnsureOwner(card, identity, now);
                return card with { Status = "pendingReview", Claim = null, UpdatedAt = now };

            case "close":
                if (card.Status != "pendingReview") throw new TaskCommandException("CONFLICT", "Only a pending-review card can be closed.", "id");
                return card with { Status = "closed", Claim = null, UpdatedAt = now };

            case "reopen":
                if (card.Status is not ("pendingReview" or "closed"))
                    throw new TaskCommandException("CONFLICT", "Only pending-review or closed cards can be reopened.", "id");
                return card with { Status = "notStarted", Claim = null, Assignee = null, UpdatedAt = now };

            default: throw Invalid("Unknown status transition.", "transition");
        }
    }

    private static void EnsureOwner(ProjectBoardCard card, BoardIdentity identity, DateTime now)
    {
        if (card.Status != "inProgress" || card.Claim == null)
            throw new TaskCommandException("CONFLICT", "Only the current claim owner can perform this action.", "id");
        if (card.Claim.ExpiresAt <= now)
            throw new TaskCommandException("CONFLICT", "The claim expired; claim the card again before writing.", "id");
        if (!SameIdentity(card.Claim, identity)) throw new TaskCommandException("CONFLICT", "Only the current claim owner can perform this action.", "actor");
    }

    private void EnsureWritable(ProjectBoardCard card, BoardIdentity? identity, DateTime now)
    {
        if (card.Status != "inProgress") return;
        if (card.Claim == null) throw Corrupt("An in-progress card has no claim.");
        if (card.Claim.ExpiresAt <= now)
        {
            claimConflictCard = card;
            claimConflictType = "CLAIM_EXPIRED";
            throw new TaskCommandException("CONFLICT", "The claim expired; claim the card again before writing.", "id");
        }
        if (identity == null || !SameIdentity(card.Claim, identity))
        {
            claimConflictCard = card;
            claimConflictType = "CLAIMED_BY_OTHER";
            throw new TaskCommandException("CONFLICT", "Card is being worked on by another agent.", "id");
        }
    }

    private static void EnsureCardActive(ProjectBoardCard card, string field)
    {
        if (card.DeletedAt != null) throw new TaskCommandException("CONFLICT", "Restore the board card before changing it.", field);
    }

    private static ProjectBoardCard FindCard(ProjectBoard board, Guid id) =>
        board.Cards.FirstOrDefault(card => card.Id == id)
        ?? throw new TaskCommandException("NOT_FOUND", "Board card not found.", "id");

    private static IEnumerable<ProjectBoardCard> GetSubtree(IReadOnlyList<ProjectBoardCard> cards, Guid rootId)
    {
        var byParent = cards.Where(card => card.ParentId != null).GroupBy(card => card.ParentId!.Value)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var byId = cards.ToDictionary(card => card.Id);
        if (!byId.TryGetValue(rootId, out ProjectBoardCard? root)) throw new TaskCommandException("NOT_FOUND", "Board card not found.", "id");
        var result = new List<ProjectBoardCard>();
        var pending = new Queue<Guid>();
        pending.Enqueue(rootId);
        while (pending.Count > 0)
        {
            Guid id = pending.Dequeue();
            result.Add(byId[id]);
            if (byParent.TryGetValue(id, out ProjectBoardCard[]? children))
                foreach (ProjectBoardCard child in children) pending.Enqueue(child.Id);
        }
        return result;
    }

    private static bool IsDescendant(IReadOnlyList<ProjectBoardCard> cards, Guid ancestorId, Guid candidateId) =>
        GetSubtree(cards, ancestorId).Skip(1).Any(card => card.Id == candidateId);

    private static bool SameIdentity(ProjectBoardClaim claim, BoardIdentity identity) =>
        SameIdentity(claim, identity.HostId, identity.ActorId, identity.SessionId);

    private static bool SameIdentity(ProjectBoardClaim claim, string hostId, string actorId, string? sessionId) =>
        string.Equals(claim.HostId, hostId, StringComparison.Ordinal) &&
        string.Equals(claim.ActorId, actorId, StringComparison.Ordinal) &&
        string.Equals(claim.SessionId, sessionId, StringComparison.Ordinal);

    private static BoardIdentity? ReadIdentity(JsonElement payload)
    {
        bool actor = payload.TryGetProperty("actor", out _);
        bool host = payload.TryGetProperty("host", out _);
        if (!actor && !host && !payload.TryGetProperty("session", out _)) return null;
        if (!actor || !host) throw Invalid("actor and host are both required when an execution identity is supplied.", actor ? "host" : "actor");
        return new BoardIdentity(RequiredText(payload, "host"), RequiredText(payload, "actor"), OptionalSession(payload));
    }

    private static BoardIdentity ReadRequiredIdentity(JsonElement payload) =>
        ReadIdentity(payload) ?? throw Invalid("actor and host are required.", "actor");

    private static string? OptionalSession(JsonElement payload) =>
        payload.TryGetProperty("session", out _) ? RequiredText(payload, "session") : null;

    private static ProjectBoardColumn? FindMappedColumn(IEnumerable<ProjectBoardColumn> columns, string status) =>
        columns.FirstOrDefault(column => column.Status == status);

    private static IReadOnlyList<ProjectBoardCard> RelocateCard(ProjectBoard board, ProjectBoardCard card, Guid targetColumnId, int? requestedOrder)
    {
        var siblings = board.Cards.Where(item => item.Id != card.Id && item.ColumnId == targetColumnId && item.ParentId == card.ParentId)
            .OrderBy(item => item.Order).ThenBy(item => item.Id).ToList();
        int order = requestedOrder ?? (card.ColumnId == targetColumnId ? Math.Min(card.Order, siblings.Count) : siblings.Count);
        if (order < 0) throw Invalid("order must be non-negative.", "order");
        order = Math.Min(order, siblings.Count);
        siblings.Insert(order, card with { ColumnId = targetColumnId });
        var cards = board.Cards.Where(item => item.Id != card.Id && !(item.ColumnId == targetColumnId && item.ParentId == card.ParentId)).ToList();
        cards.AddRange(ReorderOneColumn(siblings));
        return ReorderCards(cards, board.Columns);
    }

    private static void CheckUniqueColumnStatus(IEnumerable<ProjectBoardColumn> columns, string? status, Guid? exceptId)
    {
        if (status != null && columns.Any(column => column.Id != exceptId && column.Status == status))
            throw new TaskCommandException("CONFLICT", "Only one column can map to each status.", "status");
    }

    private async Task<TaskResponse> AddToTodoAsync(TaskRequest request, JsonElement payload)
    {
        var (registration, snapshot) = ReadProjectBoard(payload);
        CheckRevision(payload, snapshot.Board);
        Guid cardId = RequiredGuid(payload, "id");
        var card = snapshot.Board.Cards.FirstOrDefault(c => c.Id == cardId)
            ?? throw new TaskCommandException("NOT_FOUND", "Board card not found.", "id");
        if (card.DeletedAt != null) throw new TaskCommandException("CONFLICT", "Restore the board card before adding it to personal Todo.", "id");

        RegistrySnapshot registry = ReadRegistry();
        var existing = registry.State.TodoLinks.FirstOrDefault(link => link.BoardId == registration.BoardId && link.CardId == cardId);
        if (existing?.TodoId is { Length: > 0 } todoId)
        {
            TaskResponse check = await executeTodoAsync(new TaskRequest { RequestId = Guid.NewGuid().ToString(), Operation = "task.get", Id = todoId }).ConfigureAwait(false);
            if (!check.Ok)
            {
                if (check.Error?.Code == "NOT_FOUND")
                    throw new TaskCommandException("CONFLICT", "The linked personal Todo is missing; the association was kept to prevent creating a duplicate.", "id");
                return TaskResponse.Failure(request, check.Error?.Code ?? "INTERNAL_ERROR", check.Error?.Message ?? "The linked personal Todo could not be checked.", check.Error?.Field);
            }
            if (check.Data is null || ReadOptionalBoolean(check.Data, "isTrashed") is not bool isTrashed)
                throw new TaskCommandException("DATA_CORRUPT", "The linked personal Todo returned an invalid result; the association was kept.", "id");
            if (isTrashed)
                throw new TaskCommandException("CONFLICT", "The linked personal Todo is missing or deleted; the association was kept to prevent creating a duplicate.", "id");
            return TaskResponse.Success(request, new { todoId });
        }
        if (existing != null)
            throw new TaskCommandException("CONFLICT", "A previous add-to-todo result is uncertain. Check personal Todo before retrying; no duplicate was created.", "id");

        EnsureResponseFits(TaskResponse.Success(request, new { todoId = "000000000000" }));
        registry.State.TodoLinks.Add(new ProjectTodoLink(registration.BoardId, cardId, null));
        WriteRegistry(registry.State, registry.Hash);
        TaskResponse created;
        try
        {
            created = await executeTodoAsync(new TaskRequest
            {
                RequestId = Guid.NewGuid().ToString(),
                Operation = "task.create",
                Payload = JsonSerializer.SerializeToElement(new
                {
                    title = card.Title,
                    description = card.Description,
                    priority = card.Priority,
                    tags = card.Tags,
                    dueDate = card.DueDate
                }, JsonOptions)
            }).ConfigureAwait(false);
        }
        catch
        {
            throw new TaskCommandException("CONFLICT", "Todo creation result is uncertain. Check personal Todo before retrying; the pending association was kept to prevent a duplicate.", "id");
        }
        if (!created.Ok)
        {
            try
            {
                RegistrySnapshot current = ReadRegistry();
                current.State.TodoLinks.RemoveAll(link => link.BoardId == registration.BoardId && link.CardId == cardId && link.TodoId == null);
                WriteRegistry(current.State, current.Hash);
            }
            catch
            {
                throw new TaskCommandException("IO_ERROR", "Todo creation failed, but its pending association could not be cleared. Check personal Todo before retrying.", "id");
            }
            return TaskResponse.Failure(request, created.Error?.Code ?? "INTERNAL_ERROR", created.Error?.Message ?? "Personal Todo could not be created.", created.Error?.Field);
        }

        string? createdTodoId = ReadOptionalString(created.Data, "id");
        if (!IsValidTodoId(createdTodoId))
            throw new TaskCommandException("DATA_CORRUPT", "Todo returned an invalid ID; the pending association was kept. Check personal Todo before retrying.", "id");
        TaskResponse success = TaskResponse.Success(request, new { todoId = createdTodoId });
        EnsureResponseFits(success);
        try
        {
            RegistrySnapshot current = ReadRegistry();
            int linkIndex = current.State.TodoLinks.FindIndex(link => link.BoardId == registration.BoardId && link.CardId == cardId && link.TodoId == null);
            if (linkIndex < 0) throw new TaskCommandException("CONFLICT", "Todo was created, but its pending association is unavailable. Check personal Todo before retrying.", "id");
            current.State.TodoLinks[linkIndex] = current.State.TodoLinks[linkIndex] with { TodoId = createdTodoId };
            EnsureResponseFits(success);
            WriteRegistry(current.State, current.Hash);
        }
        catch (Exception)
        {
            string message = $"Todo {createdTodoId} was created, but its association could not be saved. Check that Todo before retrying.";
            throw new TaskCommandException("IO_ERROR", message, "id");
        }
        NotifyChanged();
        return success;
    }

    private TaskResponse ProjectGetOrListRequest(TaskRequest request, object data) => TaskResponse.Success(request, data);

    private (ProjectBoardRegistration Registration, BoardSnapshot Snapshot) ReadProjectBoard(JsonElement payload)
    {
        var (registration, _) = ResolveProject(payload);
        BoardSnapshot board = ReadBoard(registration.Path);
        if (board.Board.BoardId != registration.BoardId)
            throw new TaskCommandException("CONFLICT", "The registered board ID does not match the project file.", "project");
        return (registration, board);
    }

    private (ProjectBoardRegistration Registration, RegistrySnapshot Registry) ResolveProject(JsonElement payload)
    {
        RegistrySnapshot registry = ReadRegistry();
        return (ResolveProject(payload, registry.State), registry);
    }

    private ProjectBoardRegistration ResolveProject(JsonElement payload, RegistryState state)
    {
        if (payload.TryGetProperty("project", out _))
        {
            string project = RequiredString(payload, "project");
            if (Guid.TryParse(project, out Guid boardId))
                return state.Projects.FirstOrDefault(p => p.BoardId == boardId)
                    ?? throw new TaskCommandException("NOT_FOUND", "Registered project board not found.", "project");
            string path = NormalizePath(project);
            return state.Projects.FirstOrDefault(p => PathsEqual(p.Path, path))
                ?? throw new TaskCommandException("NOT_FOUND", "No project board is registered at this path.", "project");
        }
        if (!payload.TryGetProperty("cwd", out _)) throw Invalid("project or cwd is required.", "project");
        string cwd = NormalizePath(RequiredString(payload, "cwd"));
        return state.Projects.Where(p => IsWithinRoot(p.Path, cwd)).OrderByDescending(p => p.Path.Length).FirstOrDefault()
            ?? throw new TaskCommandException("NOT_FOUND", "No registered project board contains cwd.", "cwd");
    }

    private BoardSnapshot ReadBoard(string root)
    {
        RecoverCommentsMigration(root);
        string file = GetBoardPath(root, allowMissingDirectory: false);
        RejectReparsePoint(file);
        if (!File.Exists(file)) throw new TaskCommandException("NOT_FOUND", "The registered project board file was not found.", "project");
        try
        {
            byte[] bytes = File.ReadAllBytes(file);
            using JsonDocument document = JsonDocument.Parse(bytes);
            JsonElement rootElement = document.RootElement;
            if (rootElement.ValueKind != JsonValueKind.Object) throw Corrupt("Board data must be an object.");
            if (!rootElement.TryGetProperty("schemaVersion", out JsonElement versionElement) || !versionElement.TryGetInt32(out int version))
                throw Corrupt("Board schema version is missing or invalid.");
            ProjectBoard board;
            string[] migrationNotices = [];
            if (version == LegacySchemaVersion)
            {
                ValidateBoardV1Json(rootElement);
                ProjectBoardV1 legacy = JsonSerializer.Deserialize<ProjectBoardV1>(bytes, JsonOptions)
                    ?? throw Corrupt("Board data is empty.");
                if (legacy.Columns == null || legacy.Cards == null || legacy.Columns.Any(column => column == null) || legacy.Cards.Any(card => card == null))
                    throw Corrupt("Legacy board columns or cards are invalid.");
                var mappedStatuses = new HashSet<string>(StringComparer.Ordinal);
                var mappedStatusByColumn = new Dictionary<Guid, string?>();
                int duplicateStatusColumns = 0;
                foreach (ProjectBoardColumnV1 column in legacy.Columns.OrderBy(column => column.Order).ThenBy(column => column.Id))
                {
                    string? status = MapLegacyColumnStatus(column.Title);
                    if (status != null && !mappedStatuses.Add(status))
                    {
                        duplicateStatusColumns++;
                        status = null;
                    }
                    mappedStatusByColumn.Add(column.Id, status);
                }
                ProjectBoardColumn[] columns = legacy.Columns.Select(column =>
                    new ProjectBoardColumn(column.Id, column.Title, column.Order, mappedStatusByColumn[column.Id])).ToArray();
                var statusByColumn = columns.ToDictionary(column => column.Id, column => column.Status);
                if (legacy.Cards.Any(card => !statusByColumn.ContainsKey(card.ColumnId))) throw Corrupt("A legacy board card references a missing column.");
                int resetInProgress = legacy.Cards.Count(card => statusByColumn[card.ColumnId] == "inProgress");
                ProjectBoardCard[] cards = legacy.Cards.Select(card => new ProjectBoardCard(
                    card.Id, card.Title, card.Description, card.ColumnId, card.Order, card.Priority, card.Tags,
                    card.DueDate, card.CreatedAt, card.UpdatedAt, card.DeletedAt,
                    statusByColumn[card.ColumnId] == "inProgress" ? "notStarted" : statusByColumn[card.ColumnId] ?? "notStarted",
                    null, null, null, [], [])).ToArray();
                board = new ProjectBoard(SchemaVersion, legacy.BoardId, legacy.Name, legacy.Revision, columns, cards, 0, false);
                var notices = new List<string>();
                if (resetInProgress > 0)
                    notices.Add($"Migrated {resetInProgress} in-progress v1 card(s) to notStarted. Their column and order were preserved; claim each card before continuing work.");
                if (duplicateStatusColumns > 0)
                    notices.Add($"Left {duplicateStatusColumns} duplicate v1 status column(s) unmapped; the first column by order keeps each status mapping.");
                if (notices.Count > 0) migrationNotices = [string.Join(" ", notices)];
            }
            else
            {
                ValidateBoardV2Json(rootElement);
                board = JsonSerializer.Deserialize<ProjectBoard>(bytes, JsonOptions)
                    ?? throw Corrupt("Board data is empty.");
            }
            board = NormalizeBoard(board);
            ValidateBoard(board);
            string hash = Hash(bytes);
            if (knownBoardHashes.TryGetValue(board.BoardId, out string? knownHash) && !string.Equals(hash, knownHash, StringComparison.Ordinal))
            {
                knownBoardHashes[board.BoardId] = hash;
                throw new TaskCommandException("CONFLICT", "Project board changed outside this application; reload before retrying.", "revision");
            }
            knownBoardHashes[board.BoardId] = hash;
            string? commentsHash = null;
            if (board.CommentsExternal)
            {
                CommentSidecarSnapshot comments = ReadCommentSidecar(root, board.BoardId);
                if (comments.Revision < board.CommentRevision)
                    throw Corrupt("External comment revision is older than the board revision.");
                if (knownCommentHashes.TryGetValue(board.BoardId, out string? knownCommentsHash) &&
                    !string.Equals(comments.Hash, knownCommentsHash, StringComparison.Ordinal))
                {
                    knownCommentHashes[board.BoardId] = comments.Hash;
                    throw new TaskCommandException("CONFLICT", "Project comments changed outside this application; reload before retrying.", "commentRevision");
                }
                knownCommentHashes[board.BoardId] = comments.Hash;
                commentsHash = comments.Hash;
                board = AttachComments(board, comments.Data);
                ValidateBoard(board);
            }
            else
            {
                if (File.Exists(GetCommentsPath(root)))
                    throw Corrupt("A comment sidecar exists while commentsExternal is false.");
                knownCommentHashes.Remove(board.BoardId);
            }
            currentWarnings = migrationNotices;
            return new BoardSnapshot(board, hash, migrationNotices, commentsHash);
        }
        catch (TaskCommandException) { throw; }
        catch (JsonException) { throw Corrupt("Board data is not valid JSON or contains unknown fields."); }
        catch (KeyNotFoundException) { throw Corrupt("Board data is missing a required field."); }
        catch (InvalidOperationException) { throw Corrupt("Board data has an invalid shape."); }
    }

    private RegistrySnapshot ReadRegistry()
    {
        EnsureRegistryPath();
        if (!File.Exists(registryPath)) return new RegistrySnapshot(new RegistryState(), null);
        RejectReparsePoint(registryPath);
        try
        {
            byte[] bytes = File.ReadAllBytes(registryPath);
            using JsonDocument document = JsonDocument.Parse(bytes);
            ValidateRegistryJson(document.RootElement);
            var state = JsonSerializer.Deserialize<RegistryState>(bytes, JsonOptions)
                ?? throw Corrupt("Project board registry is empty.");
            ValidateRegistry(state);
            return new RegistrySnapshot(state, Hash(bytes));
        }
        catch (TaskCommandException) { throw; }
        catch (JsonException) { throw Corrupt("Project board registry is not valid JSON or contains unknown fields."); }
    }

    private void WriteRegistry(RegistryState state, string? expectedHash)
    {
        EnsureRegistryPath(createDirectory: true);
        string? currentHash = null;
        if (File.Exists(registryPath))
        {
            RejectReparsePoint(registryPath);
            currentHash = Hash(File.ReadAllBytes(registryPath));
        }
        if (!string.Equals(currentHash, expectedHash, StringComparison.Ordinal))
            throw new TaskCommandException("CONFLICT", "Project board registry changed outside this command; reload before retrying.", "registry");
        WriteAtomic(registryPath, JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions), expectedHash, "registry");
    }

    private void SaveNewBoard(string root, ProjectBoard board, ProjectBoardRegistration registration)
    {
        string boardPath = GetBoardPath(root, allowMissingDirectory: true);
        ValidateBoard(board);
        EnsureResponseFits(TaskResponse.Success(_currentRequest!, registration));
        EnsureResponseFits(TaskResponse.Success(_currentRequest!, ProjectBoardResponse(board)));
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(board, JsonOptions);
        EnsureBoardDirectory(root, create: true);
        WriteNewAtomic(boardPath, bytes);
        knownBoardHashes[board.BoardId] = Hash(bytes);
    }

    private void SaveBoard(string root, BoardSnapshot before, ProjectBoard updated, TaskResponse result)
    {
        string boardPath = GetBoardPath(root, allowMissingDirectory: false);
        RejectReparsePoint(boardPath);
        if (!File.Exists(boardPath)) throw new TaskCommandException("CONFLICT", "Project board file was removed outside this command.", "project");
        if (!string.Equals(Hash(File.ReadAllBytes(boardPath)), before.Hash, StringComparison.Ordinal))
            throw new TaskCommandException("CONFLICT", "Project board changed outside this command; reload before retrying.", "revision");
        EnsureChangedCardsWritable(before.Board, updated);
        ValidateBoard(updated);
        if (before.MigrationNotices.Count > 0) result.Warnings = before.MigrationNotices;
        EnsureResponseFits(result);
        EnsureResponseFits(TaskResponse.Success(_currentRequest!, ProjectBoardResponse(updated)));
        if (!updated.CommentsExternal && ShouldExternalizeComments(updated))
        {
            ExternalizeComments(root, before, updated);
            return;
        }
        byte[] bytes = SerializeBoardStorage(updated);
        WriteAtomic(boardPath, bytes, before.Hash, "revision");
        knownBoardHashes[updated.BoardId] = Hash(bytes);
    }

    private void EnsureChangedCardsWritable(ProjectBoard before, ProjectBoard updated)
    {
        var updatedById = updated.Cards.ToDictionary(card => card.Id);
        JsonElement payload = _currentRequest?.Payload ?? default;
        BoardIdentity? identity = payload.ValueKind == JsonValueKind.Object ? ReadIdentity(payload) : null;
        Guid? claimTransitionCard = null;
        if (_currentRequest?.Operation == "board.card.claim" ||
            (_currentRequest?.Operation == "board.card.move" && OptionalTransition(payload) == "claim"))
            claimTransitionCard = RequiredGuid(payload, "id");
        DateTime now = DateTime.UtcNow;
        foreach (ProjectBoardCard card in before.Cards)
        {
            if (updatedById.TryGetValue(card.Id, out ProjectBoardCard? updatedCard) && card == updatedCard) continue;
            if (card.Id == claimTransitionCard) continue;
            EnsureWritable(card, identity, now);
        }
    }

    internal static void WriteNewAtomic(string path, byte[] bytes)
    {
        if (File.Exists(path) || Directory.Exists(path))
            throw new TaskCommandException("CONFLICT", "The project board was created by another process.", "path");

        string? temporary = null;
        try
        {
            string directory = Path.GetDirectoryName(path)!;
            temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            try { File.Move(temporary, path, overwrite: false); }
            catch (IOException) when (File.Exists(path) || Directory.Exists(path))
            {
                throw new TaskCommandException("CONFLICT", "The project board was created by another process.", "path");
            }
            temporary = null;
        }
        finally
        {
            if (temporary != null)
            {
                try { File.Delete(temporary); }
                catch { }
            }
        }
    }

    private static void EnsureResponseFits(TaskResponse response)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(response, TaskProtocol.Options).Length > TaskProtocol.MaxBytes)
            throw new TaskCommandException("RESULT_TOO_LARGE", "The project board result exceeds the 4 MiB response limit.");
    }

    private static void WriteAtomic(string path, byte[] bytes, string? expectedExistingHash, string conflictField)
    {
        string? temporary = null;
        string replacementHash = Hash(bytes);
        try
        {
            string directory = Path.GetDirectoryName(path)!;
            temporary = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (expectedExistingHash == null)
            {
                if (File.Exists(path))
                    throw new TaskCommandException("CONFLICT", "Project board data was created outside this command; reload before retrying.", conflictField);
                try { File.Move(temporary, path); }
                catch (IOException) when (File.Exists(path))
                {
                    throw new TaskCommandException("CONFLICT", "Project board data was created outside this command; reload before retrying.", conflictField);
                }
            }
            else
            {
                ReplaceAtomicWithRetry(temporary, path, expectedExistingHash, replacementHash, conflictField);
            }
            temporary = null;
        }
        finally
        {
            if (temporary != null)
            {
                try
                {
                    if (File.Exists(temporary) && string.Equals(Hash(File.ReadAllBytes(temporary)), replacementHash, StringComparison.Ordinal))
                        File.Delete(temporary);
                }
                catch { }
            }
        }
    }

    private static void ReplaceAtomicWithRetry(string temporary, string path, string? expectedExistingHash, string replacementHash, string conflictField)
    {
        int[] retryDelaysMilliseconds = [25, 50, 100, 200];
        int retries = 0;
        while (true)
        {
            if (expectedExistingHash == null || !File.Exists(path) ||
                !string.Equals(Hash(File.ReadAllBytes(path)), expectedExistingHash, StringComparison.Ordinal))
                throw new TaskCommandException("CONFLICT", "Project board data changed outside this command; reload before retrying.", conflictField);
            if (!File.Exists(temporary) || !string.Equals(Hash(File.ReadAllBytes(temporary)), replacementHash, StringComparison.Ordinal))
                throw new TaskCommandException("CONFLICT", "The atomic replacement candidate changed; the write was not retried.", conflictField);
            try
            {
                File.Replace(temporary, path, null);
                return;
            }
            catch (IOException ex) when (IsRetryableReplaceFailure(ex) && retries < retryDelaysMilliseconds.Length)
            {
                Thread.Sleep(retryDelaysMilliseconds[retries++]);
            }
        }
    }

    private static bool IsRetryableReplaceFailure(IOException exception)
    {
        uint hresult = unchecked((uint)exception.HResult);
        if ((hresult & 0xFFFF0000) != 0x80070000) return false;
        return (hresult & 0xFFFF) is 32 or 33 or 1175;
    }

    private void EnsureRegistryPath(bool createDirectory = false)
    {
        string directory = Path.GetDirectoryName(registryPath)!;
        if (createDirectory) Directory.CreateDirectory(directory);
        if (Directory.Exists(directory)) RejectReparsePoint(directory);
        RejectReparsePoint(registryPath);
    }

    private static string GetBoardPath(string root, bool allowMissingDirectory)
    {
        string directory = Path.Combine(root, ".toolbox");
        if (Directory.Exists(directory)) RejectReparsePoint(directory);
        else if (File.Exists(directory)) throw new TaskCommandException("IO_ERROR", "The project .toolbox path is not a directory.", "path");
        else if (!allowMissingDirectory) throw new TaskCommandException("NOT_FOUND", "The project board directory was not found.", "project");
        return Path.Combine(directory, "board.json");
    }

    private static void EnsureBoardDirectory(string root, bool create)
    {
        string directory = Path.Combine(root, ".toolbox");
        if (create) Directory.CreateDirectory(directory);
        if (!Directory.Exists(directory)) throw new TaskCommandException("NOT_FOUND", "The project board directory was not found.", "project");
        RejectReparsePoint(directory);
    }

    private static void RejectReparsePoint(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new TaskCommandException("ACCESS_DENIED", "Project board paths cannot use symbolic links or reparse points.", "path");
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal)) throw Invalid("An absolute local path is required.", "path");
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw Invalid("The path is invalid.", "path"); }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

    private static bool IsWithinRoot(string root, string path)
    {
        string relative;
        try { relative = Path.GetRelativePath(root, path); }
        catch { return false; }
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static void CheckRevision(JsonElement payload, ProjectBoard board)
    {
        if (payload.TryGetProperty("revision", out _) && RequiredLong(payload, "revision") != board.Revision)
            throw new TaskCommandException("CONFLICT", "Project board revision changed; reload before retrying.", "revision");
    }

    private static void ValidateRequest(TaskRequest request)
    {
        if (request.ProtocolVersion != 1) throw new TaskCommandException("PROTOCOL_ERROR", "Unsupported protocol version.");
        if (!Guid.TryParse(request.RequestId, out Guid requestId) || requestId == Guid.Empty)
            throw new TaskCommandException("PROTOCOL_ERROR", "requestId must be a non-empty GUID.", "requestId");
        string op = request.Operation ?? "";
        if (!OperationFields.TryGetValue(op, out HashSet<string>? allowed)) throw Invalid("Unknown operation.", "operation");
        if (request.Id != null) throw Invalid("id must be supplied in payload.", "id");
        if (request.Status != null) throw Invalid("status is not accepted for project board operations.", "status");
        if (request.Arguments != null) throw Invalid("arguments are not accepted for project board operations.", "arguments");
        if (request.Payload is not { ValueKind: JsonValueKind.Object } payload) throw Invalid("A JSON object is required.", "payload");
        RejectDuplicates(payload);
        foreach (JsonProperty property in payload.EnumerateObject())
            if (!allowed.Contains(property.Name)) throw Invalid("Unknown field.", property.Name);
        ValidatePayloadTypes(op, payload);
    }

    private static readonly Dictionary<string, HashSet<string>> OperationFields = new(StringComparer.Ordinal)
    {
        ["project.register"] = Set("path", "init", "replacePath"),
        ["project.list"] = Set(),
        ["project.get"] = Set("project", "cwd"),
        ["project.unregister"] = Set("project", "cwd"),
        ["board.get"] = Set("project", "cwd"),
        ["board.column.list"] = Set("project", "cwd"),
        ["board.column.create"] = Set("project", "cwd", "title", "status", "order", "revision"),
        ["board.column.update"] = Set("project", "cwd", "id", "title", "status", "order", "revision"),
        ["board.column.delete"] = Set("project", "cwd", "id", "targetColumnId", "revision", "actor", "host", "session"),
        ["board.column.move"] = Set("project", "cwd", "id", "order", "revision"),
        ["board.card.list"] = Set("project", "cwd", "columnId", "includeDeleted", "status", "parentId", "rootsOnly", "available", "mine", "actor", "host", "session"),
        ["board.card.get"] = Set("project", "cwd", "id"),
        ["board.card.create"] = Set("project", "cwd", "title", "description", "priority", "tags", "dueDate", "columnId", "parentId", "order", "revision", "actor", "host", "session"),
        ["board.card.update"] = Set("project", "cwd", "id", "title", "description", "priority", "tags", "dueDate", "columnId", "parentId", "clearParentId", "order", "revision", "actor", "host", "session"),
        ["board.card.move"] = Set("project", "cwd", "id", "targetColumnId", "transition", "ttlMinutes", "actor", "host", "session", "order", "revision"),
        ["board.card.delete"] = Set("project", "cwd", "id", "revision", "actor", "host", "session"),
        ["board.card.restore"] = Set("project", "cwd", "id", "revision", "actor", "host", "session"),
        ["board.card.add-to-todo"] = Set("project", "cwd", "id", "revision"),
        ["board.card.claim"] = Set("project", "cwd", "id", "actor", "host", "session", "ttlMinutes", "columnId", "order", "revision"),
        ["board.card.release"] = Set("project", "cwd", "id", "actor", "host", "session", "columnId", "order", "revision"),
        ["board.card.submit"] = Set("project", "cwd", "id", "actor", "host", "session", "columnId", "order", "revision"),
        ["board.card.close"] = Set("project", "cwd", "id", "actor", "host", "session", "columnId", "order", "revision"),
        ["board.card.reopen"] = Set("project", "cwd", "id", "actor", "host", "session", "columnId", "order", "revision"),
        ["board.comment.add"] = Set("project", "cwd", "id", "parentId", "body", "actor", "host", "session", "revision", "commentRevision"),
        ["board.comment.get"] = Set("project", "cwd", "commentId", "revision", "commentRevision"),
        ["board.comment.list"] = Set("project", "cwd", "id", "includeDeleted", "limit", "cursor", "commentRevision"),
        ["board.comment.update"] = Set("project", "cwd", "commentId", "body", "actor", "host", "session", "revision", "commentRevision"),
        ["board.comment.delete"] = Set("project", "cwd", "commentId", "actor", "host", "session", "revision", "commentRevision"),
        ["board.comment.inbox"] = Set("project", "cwd", "host", "session", "actor", "ttlMinutes", "limit", "cursor", "commentRevision"),
        ["board.comment.ack"] = Set("project", "cwd", "commentId", "claimId", "actor", "host", "session", "revision", "commentRevision"),
        ["board.comment.bind"] = Set("project", "cwd", "commentId", "targetHost", "targetSession", "clear", "actor", "host", "session", "revision", "commentRevision")
    };

    private static void ValidatePayloadTypes(string operation, JsonElement payload)
    {
        foreach (JsonProperty property in payload.EnumerateObject())
        {
            JsonElement value = property.Value;
            switch (property.Name)
            {
                case "project":
                case "cwd":
                case "path":
                case "title":
                case "description":
                case "body":
                case "cursor":
                case "actor":
                case "host":
                case "session":
                case "targetHost":
                case "targetSession":
                    if (value.ValueKind != JsonValueKind.String) throw Invalid("A string is required.", property.Name);
                    break;
                case "init":
                case "replacePath":
                case "includeDeleted":
                case "rootsOnly":
                case "available":
                case "mine":
                case "clearParentId":
                case "clear":
                    if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid("A boolean is required.", property.Name);
                    break;
                case "id":
                case "commentId":
                case "claimId":
                case "columnId":
                case "targetColumnId":
                    if (value.ValueKind != JsonValueKind.String || !Guid.TryParse(value.GetString(), out Guid id) || id == Guid.Empty)
                        throw Invalid("A non-empty UUID is required.", property.Name);
                    break;
                case "order":
                case "priority":
                case "ttlMinutes":
                case "limit":
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out _)) throw Invalid("An integer is required.", property.Name);
                    break;
                case "revision":
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long revision) || revision < 1)
                        throw Invalid("revision must be a positive integer.", property.Name);
                    break;
                case "commentRevision":
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long commentRevision) || commentRevision < 0)
                        throw Invalid("commentRevision must be a non-negative integer.", property.Name);
                    break;
                case "tags":
                    if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(tag => tag.ValueKind != JsonValueKind.String))
                        throw Invalid("tags must be an array of strings.", property.Name);
                    break;
                case "dueDate":
                    if (value.ValueKind != JsonValueKind.Null) _ = TaskProtocol.ParseDate(value);
                    break;
                case "parentId":
                    if (value.ValueKind != JsonValueKind.Null && (value.ValueKind != JsonValueKind.String || !Guid.TryParse(value.GetString(), out Guid parentId) || parentId == Guid.Empty))
                        throw Invalid("parentId must be a non-empty UUID or null.", property.Name);
                    break;
                case "status":
                    if (value.ValueKind != JsonValueKind.Null && (value.ValueKind != JsonValueKind.String || !IsBoardStatus(value.GetString()!)))
                        throw Invalid("status is not a recognized board status.", property.Name);
                    break;
                case "transition":
                    if (value.ValueKind != JsonValueKind.String || value.GetString() is not ("claim" or "release" or "submit" or "close" or "reopen"))
                        throw Invalid("transition must be claim, release, submit, close, or reopen.", property.Name);
                    break;
            }
        }

        if (operation is "project.get" or "project.unregister" or "board.get" or "board.column.list" or
            "board.column.create" or "board.column.update" or "board.column.delete" or "board.column.move" or
            "board.card.list" or "board.card.get" or "board.card.create" or "board.card.update" or
            "board.card.move" or "board.card.delete" or "board.card.restore" or "board.card.add-to-todo" or
            "board.card.claim" or "board.card.release" or "board.card.submit" or "board.card.close" or "board.card.reopen" or
            "board.comment.add" or "board.comment.get" or "board.comment.list" or "board.comment.update" or
            "board.comment.delete" or "board.comment.inbox" or "board.comment.ack" or "board.comment.bind")
        {
            if (!payload.TryGetProperty("project", out _) && !payload.TryGetProperty("cwd", out _))
                throw Invalid("project or cwd is required.", "project");
            if (payload.TryGetProperty("cwd", out JsonElement cwd) && (cwd.ValueKind != JsonValueKind.String || cwd.GetString() is not { } cwdPath || !Path.IsPathFullyQualified(cwdPath)))
                throw Invalid("cwd must be an absolute path.", "cwd");
            if (payload.TryGetProperty("project", out JsonElement project) && string.IsNullOrWhiteSpace(project.GetString()))
                throw Invalid("project cannot be empty.", "project");
        }

        switch (operation)
        {
            case "project.register": _ = RequiredString(payload, "path"); break;
            case "board.column.create": _ = RequiredText(payload, "title"); CheckPriorityRange(payload); break;
            case "board.column.update":
                _ = RequiredGuid(payload, "id");
                if (!payload.TryGetProperty("title", out _) && !payload.TryGetProperty("status", out _) && !payload.TryGetProperty("order", out _)) throw Invalid("At least one column field is required.", "payload");
                if (payload.TryGetProperty("title", out _)) _ = RequiredText(payload, "title");
                break;
            case "board.column.delete": _ = RequiredGuid(payload, "id"); _ = OptionalGuid(payload, "targetColumnId"); break;
            case "board.column.move": _ = RequiredGuid(payload, "id"); _ = RequiredInt(payload, "order"); break;
            case "board.card.get":
            case "board.card.delete":
            case "board.card.restore":
            case "board.card.add-to-todo": _ = RequiredGuid(payload, "id"); break;
            case "board.card.create":
                _ = RequiredText(payload, "title");
                CheckPriorityRange(payload);
                if (payload.TryGetProperty("tags", out _)) _ = RequiredTags(payload, "tags");
                if (payload.TryGetProperty("columnId", out _)) _ = RequiredGuid(payload, "columnId");
                break;
            case "board.card.update":
                _ = RequiredGuid(payload, "id");
                if (!payload.EnumerateObject().Any(p => p.Name is "title" or "description" or "priority" or "tags" or "dueDate" or "columnId" or "order" or "parentId" or "clearParentId"))
                    throw Invalid("At least one card field is required.", "payload");
                if (payload.TryGetProperty("title", out _)) _ = RequiredText(payload, "title");
                if (payload.TryGetProperty("description", out _)) _ = RequiredString(payload, "description");
                CheckPriorityRange(payload);
                if (payload.TryGetProperty("tags", out _)) _ = RequiredTags(payload, "tags");
                break;
            case "board.card.move":
                _ = RequiredGuid(payload, "id"); _ = RequiredGuid(payload, "targetColumnId");
                if (payload.TryGetProperty("transition", out _)) _ = ReadRequiredIdentity(payload);
                break;
            case "board.card.claim":
            case "board.card.release":
            case "board.card.submit":
            case "board.card.close":
            case "board.card.reopen":
                _ = RequiredGuid(payload, "id"); _ = ReadRequiredIdentity(payload);
                if (payload.TryGetProperty("ttlMinutes", out _))
                {
                    int ttl = RequiredInt(payload, "ttlMinutes");
                    if (ttl is < 5 or > 480) throw Invalid("ttlMinutes must be between 5 and 480.", "ttlMinutes");
                    if (operation != "board.card.claim") throw Invalid("ttlMinutes is only accepted for claim.", "ttlMinutes");
                }
                break;
            case "board.comment.add":
                _ = RequiredGuid(payload, "id");
                _ = RequiredBody(payload);
                _ = ReadRequiredIdentity(payload);
                if (payload.TryGetProperty("parentId", out _)) _ = OptionalNullableGuid(payload, "parentId");
                break;
            case "board.comment.get": _ = RequiredGuid(payload, "commentId"); break;
            case "board.comment.list":
                _ = RequiredGuid(payload, "id");
                ValidateCommentLimit(payload);
                break;
            case "board.comment.update":
                _ = RequiredGuid(payload, "commentId");
                _ = RequiredBody(payload);
                _ = ReadRequiredIdentity(payload);
                break;
            case "board.comment.delete":
                _ = RequiredGuid(payload, "commentId");
                _ = ReadRequiredIdentity(payload);
                break;
            case "board.comment.inbox":
                _ = RequiredText(payload, "host");
                string actor = RequiredText(payload, "actor");
                if (!Guid.TryParse(actor, out Guid actorId) || actorId == Guid.Empty) throw Invalid("actor must be a non-empty UUID.", "actor");
                if (payload.TryGetProperty("ttlMinutes", out _))
                {
                    int ttl = RequiredInt(payload, "ttlMinutes");
                    if (ttl is < 5 or > 480) throw Invalid("ttlMinutes must be between 5 and 480.", "ttlMinutes");
                }
                ValidateCommentLimit(payload);
                break;
            case "board.comment.ack":
                _ = RequiredGuid(payload, "commentId");
                _ = RequiredGuid(payload, "claimId");
                _ = ReadRequiredIdentity(payload);
                break;
            case "board.comment.bind":
                _ = RequiredGuid(payload, "commentId");
                _ = ReadRequiredIdentity(payload);
                bool clear = OptionalBoolean(payload, "clear") ?? false;
                bool hasTarget = payload.TryGetProperty("targetHost", out _) || payload.TryGetProperty("targetSession", out _);
                if (clear == hasTarget) throw Invalid("Set either clear=true or targetHost, but not both.", "clear");
                if (payload.TryGetProperty("targetHost", out _)) _ = RequiredText(payload, "targetHost");
                if (payload.TryGetProperty("targetSession", out _))
                {
                    _ = RequiredText(payload, "targetHost");
                    _ = RequiredText(payload, "targetSession");
                }
                break;
        }
        if ((operation is "board.card.claim" or "board.card.move") && OptionalTransition(payload) == "claim")
        {
            int ttl = OptionalInt(payload, "ttlMinutes") ?? 30;
            if (ttl is < 5 or > 480) throw Invalid("ttlMinutes must be between 5 and 480.", "ttlMinutes");
        }
        if (operation == "board.card.move" && OptionalTransition(payload) != "claim" && payload.TryGetProperty("ttlMinutes", out _))
            throw Invalid("ttlMinutes is only accepted for claim.", "ttlMinutes");
        if (operation == "board.card.list" && (OptionalBoolean(payload, "mine") ?? false)) _ = ReadRequiredIdentity(payload);
        if (payload.TryGetProperty("cursor", out JsonElement cursor) && (cursor.GetString() is not { Length: > 0 } cursorValue || cursorValue.Length > 8192))
            throw Invalid("cursor must be a non-empty string no longer than 8192 characters.", "cursor");
        CheckNonNegative(payload, "order");
    }

    private static void ValidateCommentLimit(JsonElement payload)
    {
        int limit = OptionalInt(payload, "limit") ?? 100;
        if (limit is < 1 or > 100) throw Invalid("limit must be between 1 and 100.", "limit");
    }

    private static void CheckPriorityRange(JsonElement payload)
    {
        if (payload.TryGetProperty("priority", out JsonElement priority) && (!priority.TryGetInt32(out int value) || value is < 0 or > 2))
            throw Invalid("priority must be an integer from 0 to 2.", "priority");
    }

    private static long NextRevision(ProjectBoard board)
    {
        if (board.Revision == long.MaxValue)
            throw new TaskCommandException("CONFLICT", "The project board revision cannot be increased further.", "revision");
        return checked(board.Revision + 1);
    }

    private static bool IsValidTodoId(string? id) =>
        id is { Length: 12 } && id.All(char.IsAsciiHexDigit);

    private static void CheckNonNegative(JsonElement payload, string field)
    {
        if (payload.TryGetProperty(field, out JsonElement value) && value.TryGetInt32(out int number) && number < 0)
            throw Invalid($"{field} must be non-negative.", field);
    }

    private static void ValidateBoardV1Json(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || root.GetProperty("schemaVersion").GetInt32() != LegacySchemaVersion)
            throw Corrupt("Board schema version is invalid.");
        RequireProperties(root, "schemaVersion", "boardId", "name", "revision", "columns", "cards");
        if (root.GetProperty("columns").ValueKind != JsonValueKind.Array || root.GetProperty("cards").ValueKind != JsonValueKind.Array)
            throw Corrupt("Board columns and cards must be arrays.");
        foreach (JsonElement column in root.GetProperty("columns").EnumerateArray()) RequireProperties(column, "id", "title", "order");
        foreach (JsonElement card in root.GetProperty("cards").EnumerateArray())
        {
            RequireProperties(card, "id", "title", "description", "columnId", "order", "priority", "tags", "dueDate", "createdAt", "updatedAt", "deletedAt");
            foreach (string field in new[] { "dueDate", "createdAt", "updatedAt", "deletedAt" })
            {
                JsonElement date = card.GetProperty(field);
                if (date.ValueKind == JsonValueKind.Null && field is "dueDate" or "deletedAt") continue;
                try { _ = TaskProtocol.ParseDate(date); }
                catch (TaskCommandException) { throw Corrupt($"Board card {field} must be an ISO timestamp with a timezone."); }
            }
        }
    }

    private static void ValidateBoardV2Json(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || root.GetProperty("schemaVersion").GetInt32() != SchemaVersion)
            throw Corrupt("Board schema version is invalid.");
        RequireProperties(root, "schemaVersion", "boardId", "name", "revision", "commentRevision", "commentsExternal", "columns", "cards");
        if (root.GetProperty("columns").ValueKind != JsonValueKind.Array || root.GetProperty("cards").ValueKind != JsonValueKind.Array)
            throw Corrupt("Board columns and cards must be arrays.");
        foreach (JsonElement column in root.GetProperty("columns").EnumerateArray())
        {
            RequireProperties(column, "id", "title", "order", "status");
            if (column.GetProperty("status").ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
                throw Corrupt("Column status must be a string or null.");
        }
        foreach (JsonElement card in root.GetProperty("cards").EnumerateArray())
        {
            RequireProperties(card, "id", "title", "description", "columnId", "order", "priority", "tags", "dueDate", "createdAt", "updatedAt", "deletedAt", "status", "parentId", "assignee", "claim", "attachments", "comments");
            foreach (string field in new[] { "dueDate", "createdAt", "updatedAt", "deletedAt" }) ValidateDateField(card.GetProperty(field), field, field is "dueDate" or "deletedAt");
            if (card.GetProperty("parentId").ValueKind is not (JsonValueKind.Null or JsonValueKind.String)) throw Corrupt("Card parentId must be a UUID or null.");
            ValidateAssigneeJson(card.GetProperty("assignee"));
            JsonElement claim = card.GetProperty("claim");
            if (claim.ValueKind != JsonValueKind.Null)
            {
                RequireProperties(claim, "hostId", "actorId", "sessionId", "claimedAt", "heartbeatAt", "expiresAt");
                ValidateDateField(claim.GetProperty("claimedAt"), "claimedAt", false);
                ValidateDateField(claim.GetProperty("heartbeatAt"), "heartbeatAt", false);
                ValidateDateField(claim.GetProperty("expiresAt"), "expiresAt", false);
            }
            JsonElement attachments = card.GetProperty("attachments");
            if (attachments.ValueKind != JsonValueKind.Array) throw Corrupt("Card attachments must be an array.");
            foreach (JsonElement attachment in attachments.EnumerateArray())
            {
                RequireProperties(attachment, "id", "fileName", "relativePath", "size", "sha256", "addedAt", "addedBy");
                ValidateDateField(attachment.GetProperty("addedAt"), "addedAt", false);
            }
            JsonElement comments = card.GetProperty("comments");
            if (comments.ValueKind != JsonValueKind.Array) throw Corrupt("Card comments must be an array.");
            if (root.GetProperty("commentsExternal").GetBoolean() && comments.GetArrayLength() != 0)
                throw Corrupt("An external-comments board cannot also store inline comments.");
            foreach (JsonElement comment in comments.EnumerateArray()) ValidateCommentJson(comment);
        }
    }

    private static void ValidateAssigneeJson(JsonElement assignee)
    {
        if (assignee.ValueKind == JsonValueKind.Null) return;
        RequireProperties(assignee, "kind", "id", "name", "hostId", "sessionId");
    }

    private static void ValidateCommentJson(JsonElement comment)
    {
        RequireProperties(comment, "id", "parentId", "body", "author", "origin", "routing", "createdAt", "updatedAt", "deletedAt", "acknowledgedAt", "acknowledgedBy", "inboxClaim");
        ValidateAssigneeJson(comment.GetProperty("author"));
        ValidateDateField(comment.GetProperty("createdAt"), "createdAt", false);
        ValidateDateField(comment.GetProperty("updatedAt"), "updatedAt", false);
        ValidateDateField(comment.GetProperty("deletedAt"), "deletedAt", true);
        ValidateDateField(comment.GetProperty("acknowledgedAt"), "acknowledgedAt", true);
        JsonElement origin = comment.GetProperty("origin");
        if (origin.ValueKind != JsonValueKind.Null) RequireProperties(origin, "host", "sessionId");
        JsonElement routing = comment.GetProperty("routing");
        if (routing.ValueKind != JsonValueKind.Null) RequireProperties(routing, "host", "sessionId");
        JsonElement inboxClaim = comment.GetProperty("inboxClaim");
        if (inboxClaim.ValueKind != JsonValueKind.Null)
        {
            RequireProperties(inboxClaim, "actorId", "hostId", "sessionId", "claimId", "expiresAt");
            ValidateDateField(inboxClaim.GetProperty("expiresAt"), "expiresAt", false);
        }
    }

    private static void ValidateDateField(JsonElement value, string field, bool nullable)
    {
        if (nullable && value.ValueKind == JsonValueKind.Null) return;
        try { _ = TaskProtocol.ParseDate(value); }
        catch (TaskCommandException) { throw Corrupt($"Stored {field} must be an ISO timestamp with a timezone."); }
    }

    private static string? MapLegacyColumnStatus(string title)
    {
        if (title.Contains("未开始", StringComparison.Ordinal) || title.Contains("待办", StringComparison.Ordinal)) return "notStarted";
        if (title.Contains("进行中", StringComparison.Ordinal)) return "inProgress";
        if (title.Contains("待验收", StringComparison.Ordinal)) return "pendingReview";
        if (title.Contains("完成", StringComparison.Ordinal) || title.Contains("已关闭", StringComparison.Ordinal)) return "closed";
        return null;
    }

    private static void ValidateRegistryJson(JsonElement root)
    {
        RequireProperties(root, "schemaVersion", "projects", "todoLinks");
        if (root.GetProperty("projects").ValueKind != JsonValueKind.Array || root.GetProperty("todoLinks").ValueKind != JsonValueKind.Array)
            throw Corrupt("Project board registry arrays are invalid.");
        foreach (JsonElement project in root.GetProperty("projects").EnumerateArray()) RequireProperties(project, "boardId", "name", "path");
        foreach (JsonElement link in root.GetProperty("todoLinks").EnumerateArray()) RequireProperties(link, "boardId", "cardId", "todoId");
    }

    private static void RequireProperties(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Corrupt("A stored object has an invalid shape.");
        var expected = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
            if (!expected.Remove(property.Name)) throw Corrupt("Stored data contains a duplicate or unknown field.");
        if (expected.Count != 0) throw Corrupt("Stored data is missing a required field.");
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid("Duplicate field.", property.Name);
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (JsonElement child in element.EnumerateArray()) RejectDuplicates(child);
    }

    private static ProjectBoard NormalizeBoard(ProjectBoard board) => board with
    {
        Cards = board.Cards.Select(card => card with
        {
            DueDate = card.DueDate?.ToUniversalTime(),
            CreatedAt = card.CreatedAt.ToUniversalTime(),
            UpdatedAt = card.UpdatedAt.ToUniversalTime(),
            DeletedAt = card.DeletedAt?.ToUniversalTime(),
            Claim = card.Claim is null ? null : card.Claim with
            {
                ClaimedAt = card.Claim.ClaimedAt.ToUniversalTime(),
                HeartbeatAt = card.Claim.HeartbeatAt.ToUniversalTime(),
                ExpiresAt = card.Claim.ExpiresAt.ToUniversalTime()
            },
            Attachments = (card.Attachments ?? []).Select(attachment => attachment with { AddedAt = attachment.AddedAt.ToUniversalTime() }).ToArray(),
            Comments = (card.Comments ?? []).Select(comment => comment with
            {
                CreatedAt = comment.CreatedAt.ToUniversalTime(),
                UpdatedAt = comment.UpdatedAt.ToUniversalTime(),
                DeletedAt = comment.DeletedAt?.ToUniversalTime(),
                AcknowledgedAt = comment.AcknowledgedAt?.ToUniversalTime(),
                InboxClaim = comment.InboxClaim is null ? null : comment.InboxClaim with { ExpiresAt = comment.InboxClaim.ExpiresAt.ToUniversalTime() }
            }).ToArray()
        }).ToArray()
    };

    private static void ValidateBoard(ProjectBoard board)
    {
        if (board.SchemaVersion != SchemaVersion || board.BoardId == Guid.Empty || string.IsNullOrWhiteSpace(board.Name) || board.Revision < 1 || board.CommentRevision < 0 || board.Columns.Count == 0 || board.Cards == null)
            throw Corrupt("Board header is invalid or uses an unknown schema version.");
        if (board.Columns.Any(c => c.Id == Guid.Empty || string.IsNullOrWhiteSpace(c.Title) || c.Order < 0) ||
            board.Columns.Select(c => c.Id).Distinct().Count() != board.Columns.Count ||
            !IsContiguous(board.Columns.Select(c => c.Order)) ||
            board.Columns.Any(c => c.Status != null && !IsBoardStatus(c.Status)) ||
            board.Columns.Where(c => c.Status != null).Select(c => c.Status).Distinct(StringComparer.Ordinal).Count() != board.Columns.Count(c => c.Status != null))
            throw Corrupt("Board columns are invalid.");
        var columnIds = board.Columns.Select(c => c.Id).ToHashSet();
        var cardIds = board.Cards.Select(c => c.Id).ToHashSet();
        if (board.Cards.Any(c => c.Id == Guid.Empty || string.IsNullOrWhiteSpace(c.Title) || c.Description == null ||
                c.Priority is < 0 or > 2 || c.Order < 0 || !columnIds.Contains(c.ColumnId) || c.Tags == null || c.Attachments == null || c.Comments == null ||
                c.Tags.Any(tag => tag == null) ||
                !IsBoardStatus(c.Status) ||
                c.CreatedAt.Kind != DateTimeKind.Utc || c.UpdatedAt.Kind != DateTimeKind.Utc ||
                (c.DueDate.HasValue && c.DueDate.Value.Kind != DateTimeKind.Utc) ||
                (c.DeletedAt.HasValue && c.DeletedAt.Value.Kind != DateTimeKind.Utc) ||
                (c.ParentId.HasValue && !cardIds.Contains(c.ParentId.Value)) ||
                (c.Status == "inProgress" ? !IsValidClaim(c) : c.Claim != null) ||
                (c.Assignee != null && (string.IsNullOrWhiteSpace(c.Assignee.Kind) || string.IsNullOrWhiteSpace(c.Assignee.Id) || string.IsNullOrWhiteSpace(c.Assignee.Name)))) ||
            board.Cards.Select(c => c.Id).Distinct().Count() != board.Cards.Count)
            throw Corrupt("Board cards are invalid.");
        foreach (var group in board.Cards.GroupBy(c => (c.ColumnId, c.ParentId)))
        {
            if (!IsContiguous(group.Select(c => c.Order))) throw Corrupt("Board card ordering is invalid.");
        }
        ValidateCardTree(board.Cards);
        var cardsById = board.Cards.ToDictionary(card => card.Id);
        if (board.Cards.Any(card => card.ParentId is Guid parentId && card.DeletedAt == null && cardsById[parentId].DeletedAt != null))
            throw Corrupt("An active card cannot have a deleted parent.");
        if (board.Cards.Any(card => card.ParentId is Guid parentId && card.DeletedAt == null && card.Status != "closed" &&
                cardsById[parentId].DeletedAt == null && cardsById[parentId].Status == "closed"))
            throw Corrupt("A closed card cannot have an active open child.");
        ValidateCommentsInBoard(board);
    }

    private static bool IsValidClaim(ProjectBoardCard card) => card.Claim is { } claim &&
        !string.IsNullOrWhiteSpace(claim.HostId) && !string.IsNullOrWhiteSpace(claim.ActorId) &&
        claim.ClaimedAt.Kind == DateTimeKind.Utc && claim.HeartbeatAt.Kind == DateTimeKind.Utc && claim.ExpiresAt.Kind == DateTimeKind.Utc &&
        claim.ClaimedAt <= claim.HeartbeatAt && claim.HeartbeatAt < claim.ExpiresAt &&
        card.Assignee is { Kind: "agent" } assignee && assignee.Id == claim.ActorId && assignee.HostId == claim.HostId && assignee.SessionId == claim.SessionId;

    private static void ValidateCardTree(IReadOnlyList<ProjectBoardCard> cards)
    {
        var byId = cards.ToDictionary(card => card.Id);
        var complete = new HashSet<Guid>();
        foreach (ProjectBoardCard card in cards)
        {
            Guid current = card.Id;
            var path = new HashSet<Guid>();
            while (!complete.Contains(current))
            {
                if (!path.Add(current)) throw Corrupt("Board card parents contain a cycle.");
                if (byId[current].ParentId is not Guid parentId) break;
                current = parentId;
            }
            complete.UnionWith(path);
        }
    }

    private static bool IsBoardStatus(string status) => status is "notStarted" or "inProgress" or "pendingReview" or "closed";

    private static void ValidateRegistry(RegistryState state)
    {
        if (state.SchemaVersion != RegistrySchemaVersion || state.Projects == null || state.TodoLinks == null) throw Corrupt("Project board registry uses an unknown schema version.");
        if (state.Projects.Any(p => p == null || p.BoardId == Guid.Empty || string.IsNullOrWhiteSpace(p.Name) || string.IsNullOrWhiteSpace(p.Path) ||
                !Path.IsPathFullyQualified(p.Path) || p.Path.StartsWith("\\\\", StringComparison.Ordinal)) ||
            state.Projects.Select(p => p.BoardId).Distinct().Count() != state.Projects.Count)
            throw Corrupt("Project board registrations are invalid.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ProjectBoardRegistration project in state.Projects)
        {
            string path;
            try { path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project.Path)); }
            catch (Exception) { throw Corrupt("Project board registration path is invalid."); }
            if (!paths.Add(path)) throw Corrupt("Project board registrations contain duplicate paths.");
        }
        if (state.TodoLinks.Any(link => link == null || link.BoardId == Guid.Empty || link.CardId == Guid.Empty ||
                (link.TodoId != null && !IsValidTodoId(link.TodoId))) ||
            state.TodoLinks.Select(link => (link.BoardId, link.CardId)).Distinct().Count() != state.TodoLinks.Count ||
            state.TodoLinks.Any(link => !state.Projects.Any(project => project.BoardId == link.BoardId)))
            throw Corrupt("Project Todo links are invalid.");
    }

    private static bool IsContiguous(IEnumerable<int> values)
    {
        int[] sorted = values.Order().ToArray();
        return sorted.Select((value, index) => value == index).All(valid => valid);
    }

    private static List<ProjectBoardColumn> ReorderColumns(IEnumerable<ProjectBoardColumn> columns) =>
        columns.Select((column, order) => column with { Order = order }).ToList();

    private static List<ProjectBoardCard> ReorderOneColumn(IEnumerable<ProjectBoardCard> cards) =>
        cards.Select((card, order) => card with { Order = order }).ToList();

    private static List<ProjectBoardCard> ReorderCards(IEnumerable<ProjectBoardCard> cards, IEnumerable<ProjectBoardColumn> columns)
    {
        var result = new List<ProjectBoardCard>();
        foreach (var column in columns.OrderBy(c => c.Order))
            foreach (var siblings in cards.Where(c => c.ColumnId == column.Id).GroupBy(c => c.ParentId).OrderBy(group => group.Key))
                result.AddRange(ReorderOneColumn(siblings.OrderBy(c => c.Order).ThenBy(c => c.Id)));
        return result;
    }

    private void NotifyChanged() => pendingNotification = true;

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ToolBox] Project board change notification failed: {ex.Message}"); }
    }

    private static void LogIoFailure(Exception exception)
    {
#if DEBUG
        var target = exception.TargetSite;
        string method = target == null ? "unknown" : $"{target.DeclaringType?.FullName}.{target.Name}";
        string message = $"[ToolBox] Project board I/O failure: {exception.GetType().FullName}, HResult=0x{exception.HResult:X8}, Method={method}{Environment.NewLine}{exception.StackTrace}";
        Debug.WriteLine(message);
        Console.Error.WriteLine(message);
#endif
    }

    private void CacheResponse(Guid id, string fingerprint, TaskResponse response)
    {
        requestCache[id] = (fingerprint, response);
        requestOrder.Enqueue(id);
        while (requestOrder.Count > RequestCacheLimit)
        {
            Guid expired = requestOrder.Dequeue();
            requestCache.Remove(expired);
        }
    }

    private static string Fingerprint(TaskRequest request)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("protocolVersion", request.ProtocolVersion);
            writer.WriteString("operation", request.Operation);
            WriteNullable(writer, "id", request.Id);
            WriteNullable(writer, "status", request.Status);
            writer.WritePropertyName("payload");
            if (request.Payload is { } payload && payload.ValueKind != JsonValueKind.Undefined) WriteCanonical(writer, payload);
            else writer.WriteNullValue();
            writer.WritePropertyName("arguments");
            if (request.Arguments == null) writer.WriteNullValue();
            else { writer.WriteStartArray(); foreach (string arg in request.Arguments) writer.WriteStringValue(arg); writer.WriteEndArray(); }
            writer.WriteEndObject();
        }
        return Hash(stream.ToArray());
    }

    private static void WriteNullable(Utf8JsonWriter writer, string name, string? value)
    {
        if (value == null) writer.WriteNull(name);
        else writer.WriteString(name, value);
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value); }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray(); foreach (JsonElement child in element.EnumerateArray()) WriteCanonical(writer, child); writer.WriteEndArray(); break;
            default: element.WriteTo(writer); break;
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);
    private static TaskCommandException Invalid(string message, string field) => new("INVALID_ARGUMENT", message, field);
    private static TaskCommandException Corrupt(string message) => new("DATA_CORRUPT", message, "storage");

    private static string RequiredString(JsonElement payload, string field)
    {
        if (!payload.TryGetProperty(field, out JsonElement value) || value.ValueKind != JsonValueKind.String)
            throw Invalid($"{field} is required and must be a string.", field);
        return value.GetString()!;
    }

    private static string RequiredText(JsonElement payload, string field)
    {
        string value = RequiredString(payload, field);
        if (string.IsNullOrWhiteSpace(value)) throw Invalid($"{field} must not be empty.", field);
        return value.Trim();
    }

    private static string? OptionalString(JsonElement payload, string field) =>
        payload.TryGetProperty(field, out _) ? RequiredString(payload, field) : null;

    private static int RequiredInt(JsonElement payload, string field)
    {
        if (!payload.TryGetProperty(field, out JsonElement value) || !value.TryGetInt32(out int number)) throw Invalid($"{field} must be an integer.", field);
        return number;
    }

    private static int? OptionalInt(JsonElement payload, string field) => payload.TryGetProperty(field, out _) ? RequiredInt(payload, field) : null;

    private static long RequiredLong(JsonElement payload, string field)
    {
        if (!payload.TryGetProperty(field, out JsonElement value) || !value.TryGetInt64(out long number)) throw Invalid($"{field} must be an integer.", field);
        return number;
    }

    private static bool? OptionalBoolean(JsonElement payload, string field)
    {
        if (!payload.TryGetProperty(field, out JsonElement value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Invalid($"{field} must be a boolean.", field)
        };
    }

    private static Guid RequiredGuid(JsonElement payload, string field)
    {
        if (!payload.TryGetProperty(field, out JsonElement value) || value.ValueKind != JsonValueKind.String || !Guid.TryParse(value.GetString(), out Guid id) || id == Guid.Empty)
            throw Invalid($"{field} must be a non-empty UUID.", field);
        return id;
    }

    private static Guid? OptionalGuid(JsonElement payload, string field) => payload.TryGetProperty(field, out _) ? RequiredGuid(payload, field) : null;

    private static Guid? OptionalNullableGuid(JsonElement payload, string field)
    {
        if (!payload.TryGetProperty(field, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        return RequiredGuid(payload, field);
    }

    private static string? OptionalStatus(JsonElement payload, string field)
    {
        if (!payload.TryGetProperty(field, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        string status = RequiredText(payload, field);
        if (!IsBoardStatus(status)) throw Invalid("status is not a recognized board status.", field);
        return status;
    }

    private static string? OptionalTransition(JsonElement payload) =>
        payload.TryGetProperty("transition", out _) ? RequiredString(payload, "transition") : null;

    private static IReadOnlyList<string> RequiredTags(JsonElement payload, string field)
    {
        if (!payload.TryGetProperty(field, out JsonElement value) || value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(tag => tag.ValueKind != JsonValueKind.String))
            throw Invalid($"{field} must be an array of strings.", field);
        return value.EnumerateArray().Select(tag => tag.GetString()!).ToArray();
    }

    private static IReadOnlyList<string> OptionalTags(JsonElement payload) =>
        payload.TryGetProperty("tags", out _) ? RequiredTags(payload, "tags") : [];

    private static DateTime? OptionalDate(JsonElement payload, string field)
    {
        if (!payload.TryGetProperty(field, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        return TaskProtocol.ParseDate(value);
    }

    private static bool? ReadOptionalBoolean(object value, string property)
    {
        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(value, TaskProtocol.Options));
        return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(property, out JsonElement element) && element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.GetBoolean() : null;
    }

    private static string? ReadOptionalString(object? value, string property)
    {
        if (value == null) return null;
        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(value, TaskProtocol.Options));
        return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(property, out JsonElement element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() : null;
    }

    private sealed class RegistryState
    {
        public RegistryState() { }
        public int SchemaVersion { get; set; } = 1;
        public List<ProjectBoardRegistration> Projects { get; set; } = [];
        public List<ProjectTodoLink> TodoLinks { get; set; } = [];
    }

    private sealed record ProjectTodoLink(Guid BoardId, Guid CardId, string? TodoId);
    private sealed record RegistrySnapshot(RegistryState State, string? Hash);
    private sealed record BoardSnapshot(ProjectBoard Board, string Hash, IReadOnlyList<string> MigrationNotices, string? CommentsHash);
    private sealed record ProjectBoardV1(int SchemaVersion, Guid BoardId, string Name, long Revision, IReadOnlyList<ProjectBoardColumnV1> Columns, IReadOnlyList<ProjectBoardCardV1> Cards);
    private sealed record ProjectBoardColumnV1(Guid Id, string Title, int Order);
    private sealed record ProjectBoardCardV1(
        Guid Id, string Title, string Description, Guid ColumnId, int Order, int Priority, IReadOnlyList<string> Tags,
        DateTime? DueDate, DateTime CreatedAt, DateTime UpdatedAt, DateTime? DeletedAt);
    private sealed record BoardIdentity(string HostId, string ActorId, string? SessionId);
}
