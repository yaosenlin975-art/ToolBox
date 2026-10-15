using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ToolBox.Core.Todo;

namespace ToolBox.Core.Boards;

public sealed class ProjectBoardStore
{
    private const int SchemaVersion = 1;
    private const int RequestCacheLimit = 256;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
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
    private bool pendingNotification;
    private readonly Dictionary<Guid, (string Fingerprint, TaskResponse Response)> requestCache = [];
    private readonly Queue<Guid> requestOrder = [];

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
            try
            {
                ValidateRequest(request);
                _currentRequest = request;
                response = await ExecuteCoreAsync(request).ConfigureAwait(false);
                if (JsonSerializer.SerializeToUtf8Bytes(response, TaskProtocol.Options).Length > TaskProtocol.MaxBytes)
                    response = TaskResponse.Failure(request, "RESULT_TOO_LARGE", "The project board result exceeds the 4 MiB response limit.");
            }
            catch (TaskCommandException ex) { response = TaskResponse.Failure(request, ex.Code, ex.Message, ex.Field); }
            catch (UnauthorizedAccessException) { response = TaskResponse.Failure(request, "IO_ERROR", "Project board data could not be accessed."); }
            catch (IOException) { response = TaskResponse.Failure(request, "IO_ERROR", "Project board data could not be accessed."); }
            catch (Exception) { response = TaskResponse.Failure(request, "INTERNAL_ERROR", "The project board command failed."); }

            _currentRequest = null;
            if (requestId != Guid.Empty) CacheResponse(requestId, fingerprint, response);
            return response;
        }
        finally
        {
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
            var columns = new[] { "待办", "进行中", "待验收", "完成" }
                .Select((title, order) => new ProjectBoardColumn(Guid.NewGuid(), title, order)).ToArray();
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
        return TaskResponse.Success(_currentRequest!, snapshot.Board);
    }

    private TaskResponse ColumnList(JsonElement payload)
    {
        var (_, snapshot) = ReadProjectBoard(payload);
        return TaskResponse.Success(_currentRequest!, new { columns = snapshot.Board.Columns, revision = snapshot.Board.Revision });
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
        var created = new ProjectBoardColumn(Guid.NewGuid(), title, order);
        columns.Insert(order, created);
        columns = ReorderColumns(columns);
        var updated = snapshot.Board with { Columns = columns, Revision = NextRevision(snapshot.Board) };
        TaskResponse result = TaskResponse.Success(_currentRequest!, new { column = updated.Columns.Single(c => c.Id == created.Id), revision = updated.Revision });
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
        TaskResponse result = TaskResponse.Success(_currentRequest!, new { column = updated.Columns.Single(c => c.Id == id), revision = updated.Revision });
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
        if (targetId != null && !snapshot.Board.Columns.Any(c => c.Id == targetId.Value))
            throw new TaskCommandException("NOT_FOUND", "Target board column not found.", "targetColumnId");

        var remainingColumns = snapshot.Board.Columns.Where(c => c.Id != id).OrderBy(c => c.Order).ToList();
        var newCards = cards.Where(c => c.ColumnId != id).ToList();
        if (targetId != null)
        {
            int nextOrder = newCards.Where(c => c.ColumnId == targetId.Value).Select(c => c.Order).DefaultIfEmpty(-1).Max() + 1;
            DateTime now = DateTime.UtcNow;
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
        TaskResponse result = TaskResponse.Success(_currentRequest!, new { column = deleted, revision = updated.Revision });
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
        TaskResponse result = TaskResponse.Success(_currentRequest!, new { column = columns.Single(c => c.Id == id), revision = updated.Revision });
        SaveBoard(registration.Path, snapshot, updated, result);
        NotifyChanged();
        return result;
    }

    private TaskResponse CardList(JsonElement payload)
    {
        var (_, snapshot) = ReadProjectBoard(payload);
        bool includeDeleted = OptionalBoolean(payload, "includeDeleted") ?? false;
        Guid? columnId = OptionalGuid(payload, "columnId");
        if (columnId != null && !snapshot.Board.Columns.Any(c => c.Id == columnId.Value))
            throw new TaskCommandException("NOT_FOUND", "Board column not found.", "columnId");
        var columnOrder = snapshot.Board.Columns.ToDictionary(column => column.Id, column => column.Order);
        var cards = snapshot.Board.Cards.Where(c => (includeDeleted || c.DeletedAt == null) && (columnId == null || c.ColumnId == columnId.Value))
            .OrderBy(c => columnOrder[c.ColumnId]).ThenBy(c => c.Order).ThenBy(c => c.Id).ToArray();
        return TaskResponse.Success(_currentRequest!, new { cards, revision = snapshot.Board.Revision });
    }

    private TaskResponse CardGet(JsonElement payload)
    {
        var (_, snapshot) = ReadProjectBoard(payload);
        Guid id = RequiredGuid(payload, "id");
        var card = snapshot.Board.Cards.FirstOrDefault(c => c.Id == id)
            ?? throw new TaskCommandException("NOT_FOUND", "Board card not found.", "id");
        return TaskResponse.Success(_currentRequest!, new { card, revision = snapshot.Board.Revision });
    }

    private TaskResponse CardCreate(JsonElement payload)
    {
        var (registration, snapshot) = ReadProjectBoard(payload);
        CheckRevision(payload, snapshot.Board);
        string title = RequiredText(payload, "title");
        Guid columnId = OptionalGuid(payload, "columnId") ?? snapshot.Board.Columns.OrderBy(c => c.Order).First().Id;
        if (!snapshot.Board.Columns.Any(c => c.Id == columnId)) throw new TaskCommandException("NOT_FOUND", "Board column not found.", "columnId");
        int order = OptionalInt(payload, "order") ?? snapshot.Board.Cards.Where(c => c.ColumnId == columnId).Select(c => c.Order).DefaultIfEmpty(-1).Max() + 1;
        if (order < 0) throw Invalid("order must be non-negative.", "order");
        var cards = snapshot.Board.Cards.ToList();
        var inColumn = cards.Where(c => c.ColumnId == columnId).OrderBy(c => c.Order).ThenBy(c => c.Id).ToList();
        int insertAt = Math.Min(order, inColumn.Count);
        DateTime now = DateTime.UtcNow;
        var card = new ProjectBoardCard(Guid.NewGuid(), title,
            OptionalString(payload, "description") ?? "", columnId, insertAt,
            OptionalInt(payload, "priority") ?? 0, OptionalTags(payload), OptionalDate(payload, "dueDate"), now, now, null);
        inColumn.Insert(insertAt, card);
        cards.RemoveAll(c => c.ColumnId == columnId);
        cards.AddRange(ReorderOneColumn(inColumn));
        var updated = snapshot.Board with { Cards = ReorderCards(cards, snapshot.Board.Columns), Revision = NextRevision(snapshot.Board) };
        TaskResponse result = TaskResponse.Success(_currentRequest!, new { card = updated.Cards.Single(c => c.Id == card.Id), revision = updated.Revision });
        SaveBoard(registration.Path, snapshot, updated, result);
        NotifyChanged();
        return result;
    }

    private TaskResponse CardUpdate(JsonElement payload)
    {
        var (registration, snapshot) = ReadProjectBoard(payload);
        CheckRevision(payload, snapshot.Board);
        Guid id = RequiredGuid(payload, "id");
        int index = snapshot.Board.Cards.ToList().FindIndex(c => c.Id == id);
        if (index < 0) throw new TaskCommandException("NOT_FOUND", "Board card not found.", "id");
        var card = snapshot.Board.Cards[index];
        if (card.DeletedAt != null) throw new TaskCommandException("CONFLICT", "Restore the board card before updating it.", "id");
        card = card with
        {
            Title = payload.TryGetProperty("title", out _) ? RequiredText(payload, "title") : card.Title,
            Description = payload.TryGetProperty("description", out _) ? RequiredString(payload, "description") : card.Description,
            Priority = payload.TryGetProperty("priority", out _) ? RequiredInt(payload, "priority") : card.Priority,
            Tags = payload.TryGetProperty("tags", out _) ? RequiredTags(payload, "tags") : card.Tags,
            DueDate = payload.TryGetProperty("dueDate", out _) ? OptionalDate(payload, "dueDate") : card.DueDate,
            UpdatedAt = DateTime.UtcNow
        };
        Guid targetColumn = payload.TryGetProperty("columnId", out _) ? RequiredGuid(payload, "columnId") : card.ColumnId;
        if (!snapshot.Board.Columns.Any(c => c.Id == targetColumn)) throw new TaskCommandException("NOT_FOUND", "Board column not found.", "columnId");
        var cards = snapshot.Board.Cards.Where(c => c.Id != id).ToList();
        var targetCards = cards.Where(c => c.ColumnId == targetColumn).OrderBy(c => c.Order).ThenBy(c => c.Id).ToList();
        int targetOrder = payload.TryGetProperty("order", out _) ? RequiredInt(payload, "order") :
            targetColumn == card.ColumnId ? Math.Min(card.Order, targetCards.Count) : targetCards.Count;
        if (targetOrder < 0) throw Invalid("order must be non-negative.", "order");
        targetOrder = Math.Min(targetOrder, targetCards.Count);
        targetCards.Insert(targetOrder, card with { ColumnId = targetColumn, UpdatedAt = DateTime.UtcNow });
        cards.RemoveAll(c => c.ColumnId == targetColumn);
        cards.AddRange(ReorderOneColumn(targetCards));
        var updated = snapshot.Board with { Cards = cards, Revision = NextRevision(snapshot.Board) };
        updated = updated with { Cards = ReorderCards(updated.Cards, updated.Columns) };
        TaskResponse result = TaskResponse.Success(_currentRequest!, new { card = updated.Cards.Single(c => c.Id == id), revision = updated.Revision });
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
        int index = snapshot.Board.Cards.ToList().FindIndex(c => c.Id == id);
        if (index < 0) throw new TaskCommandException("NOT_FOUND", "Board card not found.", "id");
        var card = snapshot.Board.Cards[index];
        if (card.DeletedAt != null) throw new TaskCommandException("CONFLICT", "Restore the board card before moving it.", "id");
        var cards = snapshot.Board.Cards.Where(c => c.Id != id).ToList();
        var target = cards.Where(c => c.ColumnId == targetId).OrderBy(c => c.Order).ThenBy(c => c.Id).ToList();
        int order = OptionalInt(payload, "order") ?? target.Count;
        if (order < 0) throw Invalid("order must be non-negative.", "order");
        order = Math.Min(order, target.Count);
        target.Insert(order, card with { ColumnId = targetId, UpdatedAt = DateTime.UtcNow });
        cards.RemoveAll(c => c.ColumnId == targetId);
        cards.AddRange(ReorderOneColumn(target));
        var updated = snapshot.Board with { Cards = ReorderCards(cards, snapshot.Board.Columns), Revision = NextRevision(snapshot.Board) };
        TaskResponse result = TaskResponse.Success(_currentRequest!, new { card = updated.Cards.Single(c => c.Id == id), revision = updated.Revision });
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
        var cards = snapshot.Board.Cards.ToArray();
        int index = Array.FindIndex(cards, c => c.Id == id);
        if (index < 0) throw new TaskCommandException("NOT_FOUND", "Board card not found.", "id");
        if ((cards[index].DeletedAt != null) == deleted)
            throw new TaskCommandException("CONFLICT", deleted ? "Board card is already deleted." : "Board card is not deleted.", "id");
        DateTime now = DateTime.UtcNow;
        cards[index] = cards[index] with { DeletedAt = deleted ? now : null, UpdatedAt = now };
        var updated = snapshot.Board with { Cards = cards, Revision = NextRevision(snapshot.Board) };
        TaskResponse result = TaskResponse.Success(_currentRequest!, new { card = cards[index], revision = updated.Revision });
        SaveBoard(registration.Path, snapshot, updated, result);
        NotifyChanged();
        return result;
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
        string file = GetBoardPath(root, allowMissingDirectory: false);
        RejectReparsePoint(file);
        if (!File.Exists(file)) throw new TaskCommandException("NOT_FOUND", "The registered project board file was not found.", "project");
        try
        {
            byte[] bytes = File.ReadAllBytes(file);
            using JsonDocument document = JsonDocument.Parse(bytes);
            ValidateBoardJson(document.RootElement);
            var board = JsonSerializer.Deserialize<ProjectBoard>(bytes, JsonOptions)
                ?? throw Corrupt("Board data is empty.");
            board = NormalizeBoard(board);
            ValidateBoard(board);
            string hash = Hash(bytes);
            if (knownBoardHashes.TryGetValue(board.BoardId, out string? knownHash) && !string.Equals(hash, knownHash, StringComparison.Ordinal))
            {
                knownBoardHashes[board.BoardId] = hash;
                throw new TaskCommandException("CONFLICT", "Project board changed outside this application; reload before retrying.", "revision");
            }
            knownBoardHashes[board.BoardId] = hash;
            return new BoardSnapshot(board, hash);
        }
        catch (TaskCommandException) { throw; }
        catch (JsonException) { throw Corrupt("Board data is not valid JSON or contains unknown fields."); }
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
        WriteAtomic(registryPath, JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions));
    }

    private void SaveNewBoard(string root, ProjectBoard board, ProjectBoardRegistration registration)
    {
        string boardPath = GetBoardPath(root, allowMissingDirectory: true);
        EnsureResponseFits(TaskResponse.Success(_currentRequest!, registration));
        EnsureResponseFits(TaskResponse.Success(_currentRequest!, board));
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
        EnsureResponseFits(result);
        EnsureResponseFits(TaskResponse.Success(_currentRequest!, updated));
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(updated, JsonOptions);
        WriteAtomic(boardPath, bytes);
        knownBoardHashes[updated.BoardId] = Hash(bytes);
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

    private static void WriteAtomic(string path, byte[] bytes)
    {
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
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
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
        ["board.column.create"] = Set("project", "cwd", "title", "order", "revision"),
        ["board.column.update"] = Set("project", "cwd", "id", "title", "order", "revision"),
        ["board.column.delete"] = Set("project", "cwd", "id", "targetColumnId", "revision"),
        ["board.column.move"] = Set("project", "cwd", "id", "order", "revision"),
        ["board.card.list"] = Set("project", "cwd", "columnId", "includeDeleted"),
        ["board.card.get"] = Set("project", "cwd", "id"),
        ["board.card.create"] = Set("project", "cwd", "title", "description", "priority", "tags", "dueDate", "columnId", "order", "revision"),
        ["board.card.update"] = Set("project", "cwd", "id", "title", "description", "priority", "tags", "dueDate", "columnId", "order", "revision"),
        ["board.card.move"] = Set("project", "cwd", "id", "targetColumnId", "order", "revision"),
        ["board.card.delete"] = Set("project", "cwd", "id", "revision"),
        ["board.card.restore"] = Set("project", "cwd", "id", "revision"),
        ["board.card.add-to-todo"] = Set("project", "cwd", "id", "revision")
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
                    if (value.ValueKind != JsonValueKind.String) throw Invalid("A string is required.", property.Name);
                    break;
                case "init":
                case "replacePath":
                case "includeDeleted":
                    if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid("A boolean is required.", property.Name);
                    break;
                case "id":
                case "columnId":
                case "targetColumnId":
                    if (value.ValueKind != JsonValueKind.String || !Guid.TryParse(value.GetString(), out Guid id) || id == Guid.Empty)
                        throw Invalid("A non-empty UUID is required.", property.Name);
                    break;
                case "order":
                case "priority":
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out _)) throw Invalid("An integer is required.", property.Name);
                    break;
                case "revision":
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long revision) || revision < 1)
                        throw Invalid("revision must be a positive integer.", property.Name);
                    break;
                case "tags":
                    if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(tag => tag.ValueKind != JsonValueKind.String))
                        throw Invalid("tags must be an array of strings.", property.Name);
                    break;
                case "dueDate":
                    if (value.ValueKind != JsonValueKind.Null) _ = TaskProtocol.ParseDate(value);
                    break;
            }
        }

        if (operation is "project.get" or "project.unregister" or "board.get" or "board.column.list" or
            "board.column.create" or "board.column.update" or "board.column.delete" or "board.column.move" or
            "board.card.list" or "board.card.get" or "board.card.create" or "board.card.update" or
            "board.card.move" or "board.card.delete" or "board.card.restore" or "board.card.add-to-todo")
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
                if (!payload.TryGetProperty("title", out _) && !payload.TryGetProperty("order", out _)) throw Invalid("At least one column field is required.", "payload");
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
                if (!payload.EnumerateObject().Any(p => p.Name is "title" or "description" or "priority" or "tags" or "dueDate" or "columnId" or "order"))
                    throw Invalid("At least one card field is required.", "payload");
                if (payload.TryGetProperty("title", out _)) _ = RequiredText(payload, "title");
                if (payload.TryGetProperty("description", out _)) _ = RequiredString(payload, "description");
                CheckPriorityRange(payload);
                if (payload.TryGetProperty("tags", out _)) _ = RequiredTags(payload, "tags");
                break;
            case "board.card.move": _ = RequiredGuid(payload, "id"); _ = RequiredGuid(payload, "targetColumnId"); break;
        }
        CheckNonNegative(payload, "order");
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

    private static void ValidateBoardJson(JsonElement root)
    {
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
            DeletedAt = card.DeletedAt?.ToUniversalTime()
        }).ToArray()
    };

    private static void ValidateBoard(ProjectBoard board)
    {
        if (board.SchemaVersion != SchemaVersion || board.BoardId == Guid.Empty || string.IsNullOrWhiteSpace(board.Name) || board.Revision < 1 || board.Columns.Count == 0)
            throw Corrupt("Board header is invalid or uses an unknown schema version.");
        if (board.Columns.Any(c => c.Id == Guid.Empty || string.IsNullOrWhiteSpace(c.Title) || c.Order < 0) ||
            board.Columns.Select(c => c.Id).Distinct().Count() != board.Columns.Count ||
            !IsContiguous(board.Columns.Select(c => c.Order))) throw Corrupt("Board columns are invalid.");
        var columnIds = board.Columns.Select(c => c.Id).ToHashSet();
        if (board.Cards.Any(c => c.Id == Guid.Empty || string.IsNullOrWhiteSpace(c.Title) || c.Description == null ||
                c.Priority is < 0 or > 2 || c.Order < 0 || !columnIds.Contains(c.ColumnId) || c.Tags == null ||
                c.Tags.Any(tag => tag == null) ||
                c.CreatedAt.Kind != DateTimeKind.Utc || c.UpdatedAt.Kind != DateTimeKind.Utc ||
                (c.DueDate.HasValue && c.DueDate.Value.Kind != DateTimeKind.Utc) ||
                (c.DeletedAt.HasValue && c.DeletedAt.Value.Kind != DateTimeKind.Utc)) ||
            board.Cards.Select(c => c.Id).Distinct().Count() != board.Cards.Count)
            throw Corrupt("Board cards are invalid.");
        foreach (Guid columnId in columnIds)
        {
            var orders = board.Cards.Where(c => c.ColumnId == columnId).Select(c => c.Order);
            if (!IsContiguous(orders)) throw Corrupt("Board card ordering is invalid.");
        }
    }

    private static void ValidateRegistry(RegistryState state)
    {
        if (state.SchemaVersion != SchemaVersion || state.Projects == null || state.TodoLinks == null) throw Corrupt("Project board registry uses an unknown schema version.");
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
            result.AddRange(ReorderOneColumn(cards.Where(c => c.ColumnId == column.Id).OrderBy(c => c.Order).ThenBy(c => c.Id)));
        return result;
    }

    private void NotifyChanged() => pendingNotification = true;

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ToolBox] Project board change notification failed: {ex.Message}"); }
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
        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));
        return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(property, out JsonElement element) && element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.GetBoolean() : null;
    }

    private static string? ReadOptionalString(object? value, string property)
    {
        if (value == null) return null;
        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));
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
    private sealed record BoardSnapshot(ProjectBoard Board, string Hash);
}
