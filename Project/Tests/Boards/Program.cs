using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using ToolBox.Core.Boards;
using ToolBox.Core.Todo;

internal static class Program
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static async Task Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "ToolBox Board Checks", Guid.NewGuid().ToString("N"));
        if (args.Contains("--targeted-fixes", StringComparer.Ordinal))
        {
            Directory.CreateDirectory(root);
            try
            {
                await CheckBoardWritePreflightAsync(root);
                CheckNewBoardAtomicNoOverwrite(root);
                await CheckInvalidTodoIdAsync(root);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            Console.WriteLine("Targeted project board checks passed.");
            return;
        }

        string registryRoot = Path.Combine(root, "registry");
        string projectPath = Path.Combine(root, "项目 看板 (no git)");
        Directory.CreateDirectory(projectPath);
        var todo = new FakeTodo();
        var store = new ProjectBoardStore(registryRoot, todo.ExecuteAsync);
        try
        {
            Console.WriteLine("registration and CRUD");
            await CheckRegistrationAndCrudAsync(store, projectPath, todo);
            Console.WriteLine("request deduplication");
            await CheckRequestDeduplicationAsync(store, projectPath);
            Console.WriteLine("concurrent writes");
            await CheckConcurrentWritesAsync(store, projectPath);
            Console.WriteLine("duplicate board");
            projectPath = await CheckDuplicateBoardAsync(store, projectPath, root);
            Console.WriteLine("external changes and corruption");
            await CheckExternalChangesAndCorruptionAsync(store, projectPath);
            await CheckUnregisterAsync(store, projectPath, Path.Combine(root, "项目 看板 (no git)"));
            Console.WriteLine("Todo pending recovery");
            await CheckTodoPendingRecoveryAsync(root);
            Console.WriteLine("path escape");
            await CheckPathEscapeAsync(store, root);
            Console.WriteLine("write preflight and revision limits");
            await CheckBoardWritePreflightAsync(root);
            Console.WriteLine("new board atomic no-overwrite");
            CheckNewBoardAtomicNoOverwrite(root);
            Console.WriteLine("invalid Todo ID");
            await CheckInvalidTodoIdAsync(root);
        }
        finally
        {
            todo.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        Console.WriteLine("Project board checks passed.");
    }

    private static async Task CheckRegistrationAndCrudAsync(ProjectBoardStore store, string path, FakeTodo todo)
    {
        var registration = Read<ProjectBoardRegistration>(await CallAsync(store, "project.register", new { path, init = true }));
        Check(registration.BoardId != Guid.Empty && !Directory.Exists(Path.Combine(path, ".git")), "registers a non-Git directory at a Unicode path");
        Check(File.Exists(Path.Combine(path, ".toolbox", "board.json")), "initialization creates the project board file");
        var board = Read<ProjectBoard>(await CallAsync(store, "board.get", new { project = registration.BoardId.ToString() }));
        Check(board.SchemaVersion == 1 && board.Columns.Count == 4 && board.Columns.All(c => c.Id != Guid.Empty), "creates four stable UUID columns");
        string nestedPath = Path.Combine(path, "nested", "work");
        Directory.CreateDirectory(nestedPath);
        Check(Read<ProjectBoard>(await CallAsync(store, "board.get", new { cwd = nestedPath })).BoardId == registration.BoardId, "cwd resolves the nearest registered project root");

        var createdColumn = ReadWrapped<ProjectBoardColumn>(await CallAsync(store, "board.column.create", new
        {
            project = registration.BoardId.ToString(), title = "Review", order = 1, revision = board.Revision
        }), "column");
        Check(createdColumn.Id != Guid.Empty, "column create returns a stable UUID");
        board = Read<ProjectBoard>(await CallAsync(store, "board.get", new { project = registration.BoardId.ToString() }));
        var todoColumn = board.Columns.Single(c => c.Title == "待办");
        var activeColumn = board.Columns.Single(c => c.Title == "进行中");

        var card = ReadWrapped<ProjectBoardCard>(await CallAsync(store, "board.card.create", new
        {
            project = registration.BoardId.ToString(), title = "修复看板", description = "details", priority = 2,
            tags = new[] { "中文", "one" }, dueDate = "2026-10-20T12:00:00+08:00", columnId = createdColumn.Id,
            revision = board.Revision
        }), "card");
        Check(card.Id != Guid.Empty && card.Priority == 2 && card.DueDate?.Kind == DateTimeKind.Utc, "card create stores UUID, priority and UTC due date");
        CheckCode(await CallAsync(store, "board.card.update", new
        {
            project = registration.BoardId.ToString(), id = card.Id, title = "stale", revision = board.Revision
        }), "CONFLICT", "stale board revision is rejected");
        Check(Array(Read(await CallAsync(store, "board.card.list", new { project = registration.BoardId.ToString(), columnId = createdColumn.Id })), "cards").Length == 1, "card list filters by column");

        card = ReadWrapped<ProjectBoardCard>(await CallAsync(store, "board.card.update", new
        {
            project = registration.BoardId.ToString(), id = card.Id, description = "", priority = 1, tags = System.Array.Empty<string>(), dueDate = (string?)null
        }), "card");
        Check(card.Description == "" && card.Tags.Count == 0 && card.DueDate == null && card.Priority == 1, "card update clears optional values");

        card = ReadWrapped<ProjectBoardCard>(await CallAsync(store, "board.card.move", new
        {
            project = registration.BoardId.ToString(), id = card.Id, targetColumnId = todoColumn.Id, order = 0
        }), "card");
        Check(card.ColumnId == todoColumn.Id && card.Order == 0, "card move changes column and order");

        card = ReadWrapped<ProjectBoardCard>(await CallAsync(store, "board.card.delete", new { project = registration.BoardId.ToString(), id = card.Id }), "card");
        Check(card.DeletedAt.HasValue, "card delete soft-deletes");
        Check(Array(Read(await CallAsync(store, "board.card.list", new { project = registration.BoardId.ToString() })), "cards").Length == 0, "card list hides deleted cards by default");
        Check(Array(Read(await CallAsync(store, "board.card.list", new { project = registration.BoardId.ToString(), includeDeleted = true })), "cards").Length == 1, "card list can include deleted cards");
        CheckCode(await CallAsync(store, "board.card.add-to-todo", new { project = registration.BoardId.ToString(), id = card.Id }), "CONFLICT", "deleted cards cannot be copied to Todo");
        card = ReadWrapped<ProjectBoardCard>(await CallAsync(store, "board.card.restore", new { project = registration.BoardId.ToString(), id = card.Id }), "card");
        Check(card.DeletedAt == null, "card restore clears soft-delete time");

        TaskResponse added = await CallAsync(store, "board.card.add-to-todo", new { project = registration.BoardId.ToString(), id = card.Id });
        string todoId = String(Read(added), "todoId");
        TaskResponse addedAgain = await CallAsync(store, "board.card.add-to-todo", new { project = registration.BoardId.ToString(), id = card.Id });
        Check(String(Read(addedAgain), "todoId") == todoId && todo.Created.Count == 1, "Todo link survives repeat calls without creating a duplicate");

        var secondDeleted = ReadWrapped<ProjectBoardCard>(await CallAsync(store, "board.card.create", new
        {
            project = registration.BoardId.ToString(), title = "soft deleted", columnId = createdColumn.Id
        }), "card");
        await CallAsync(store, "board.card.delete", new { project = registration.BoardId.ToString(), id = secondDeleted.Id });
        var deletedColumn = ReadWrapped<ProjectBoardColumn>(await CallAsync(store, "board.column.delete", new
        {
            project = registration.BoardId.ToString(), id = createdColumn.Id, targetColumnId = activeColumn.Id
        }), "column");
        Check(deletedColumn.Id == createdColumn.Id, "column delete returns the deleted column");
        var movedDeleted = ReadWrapped<ProjectBoardCard>(await CallAsync(store, "board.card.get", new { project = registration.BoardId.ToString(), id = secondDeleted.Id }), "card");
        Check(movedDeleted.ColumnId == activeColumn.Id && movedDeleted.DeletedAt != null, "column delete transfers soft-deleted cards to the target");
        CheckCode(await CallAsync(store, "board.column.delete", new
        {
            project = registration.BoardId.ToString(), id = activeColumn.Id
        }), "INVALID_ARGUMENT", "column with cards requires a target");
        await CallAsync(store, "board.card.restore", new { project = registration.BoardId.ToString(), id = secondDeleted.Id });
        await CallAsync(store, "board.card.delete", new { project = registration.BoardId.ToString(), id = secondDeleted.Id });

        var updatedColumn = ReadWrapped<ProjectBoardColumn>(await CallAsync(store, "board.column.update", new
        {
            project = registration.BoardId.ToString(), id = activeColumn.Id, title = "工作中", order = 0
        }), "column");
        Check(updatedColumn.Title == "工作中" && updatedColumn.Order == 0, "column update changes title and order");
        var movedColumn = ReadWrapped<ProjectBoardColumn>(await CallAsync(store, "board.column.move", new
        {
            project = registration.BoardId.ToString(), id = todoColumn.Id, order = 1
        }), "column");
        Check(movedColumn.Order == 1, "column move reorders columns");

        board = Read<ProjectBoard>(await CallAsync(store, "board.get", new { project = registration.BoardId.ToString() }));
        var boardFile = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(path, ".toolbox", "board.json")));
        Check(boardFile.RootElement.GetProperty("boardId").GetGuid() == board.BoardId && board.Revision > 1, "board JSON uses the stable public schema and increments revision");
    }

    private static async Task CheckRequestDeduplicationAsync(ProjectBoardStore store, string path)
    {
        string project = Read<ProjectBoardRegistration[]>(await CallAsync(store, "project.list", new { }))[0].BoardId.ToString();
        Guid requestId = Guid.NewGuid();
        bool reentered = false;
        bool nestedReadSucceeded = false;
        Action handler = () =>
        {
            if (reentered) return;
            reentered = true;
            nestedReadSucceeded = store.ExecuteAsync(new TaskRequest
            {
                RequestId = Guid.NewGuid().ToString(), Operation = "board.get",
                Payload = JsonSerializer.SerializeToElement(new { project }, Web)
            }).GetAwaiter().GetResult().Ok;
        };
        store.Changed += handler;
        TaskResponse first = await CallAsync(store, "board.card.create", new { project, title = "dedup" }, requestId);
        store.Changed -= handler;
        Check(reentered && nestedReadSucceeded, "Changed handlers can read the store without semaphore reentry deadlock");
        TaskResponse duplicate = await CallAsync(store, "board.card.create", new { project, title = "dedup" }, requestId);
        Check(ReadWrapped<ProjectBoardCard>(first, "card").Id == ReadWrapped<ProjectBoardCard>(duplicate, "card").Id, "same requestId returns the original card result");
        TaskResponse reused = await CallAsync(store, "board.card.create", new { project, title = "different" }, requestId);
        CheckCode(reused, "CONFLICT", "requestId reused with different content is rejected");
        Check(Array(Read(await CallAsync(store, "board.card.list", new { project })), "cards").Length == 2, "requestId dedupe prevents a second card");

        TaskResponse unknown = await CallRawAsync(store, "board.card.create", "{\"project\":\"" + project + "\",\"title\":\"bad\",\"extra\":true}");
        CheckCode(unknown, "INVALID_ARGUMENT", "unknown payload fields are rejected");
        TaskResponse duplicateField = await CallRawAsync(store, "board.card.list", "{\"project\":\"" + project + "\",\"project\":\"" + project + "\"}");
        CheckCode(duplicateField, "INVALID_ARGUMENT", "duplicate payload fields are rejected");
    }

    private static async Task CheckConcurrentWritesAsync(ProjectBoardStore store, string path)
    {
        string project = Read<ProjectBoardRegistration[]>(await CallAsync(store, "project.list", new { }))[0].BoardId.ToString();
        TaskResponse[] results = await Task.WhenAll(Enumerable.Range(0, 24)
            .Select(i => CallAsync(store, "board.card.create", new { project, title = $"parallel-{i}" })));
        Check(results.All(result => result.Ok), "serialized concurrent creates all succeed");
        var cards = Array(Read(await CallAsync(store, "board.card.list", new { project })), "cards");
        Check(cards.Length == 26 && cards.Select(c => c.GetProperty("id").GetGuid()).Distinct().Count() == 26, "concurrent writes keep all cards with unique UUIDs");
    }

    private static async Task CheckExternalChangesAndCorruptionAsync(ProjectBoardStore store, string path)
    {
        string project = Read<ProjectBoardRegistration[]>(await CallAsync(store, "project.list", new { }))[0].BoardId.ToString();
        var board = Read<ProjectBoard>(await CallAsync(store, "board.get", new { project }));
        string boardPath = Path.Combine(path, ".toolbox", "board.json");
        string before = await File.ReadAllTextAsync(boardPath);
        JsonObject external = JsonNode.Parse(before)!.AsObject();
        external["name"] = "external update";
        await File.WriteAllTextAsync(boardPath, external.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        TaskResponse stale = await CallAsync(store, "board.card.create", new { project, title = "must not overwrite", revision = board.Revision });
        CheckCode(stale, "CONFLICT", "same-revision external edits are detected by file fingerprint");
        Check((await File.ReadAllTextAsync(boardPath)).Contains("external update", StringComparison.Ordinal), "external board edits remain intact");

        string corrupted = "{\"schemaVersion\":1,\"boardId\":\"bad\",\"name\":\"broken\",\"revision\":1,\"columns\":[],\"cards\":[],\"unknown\":true}";
        await File.WriteAllTextAsync(boardPath, corrupted);
        TaskResponse readCorrupt = await CallAsync(store, "board.get", new { project });
        CheckCode(readCorrupt, "DATA_CORRUPT", "unknown schema fields are rejected");
        TaskResponse writeCorrupt = await CallAsync(store, "board.card.create", new { project, title = "must not overwrite" });
        CheckCode(writeCorrupt, "DATA_CORRUPT", "corrupt board is never overwritten");
        Check(await File.ReadAllTextAsync(boardPath) == corrupted, "corrupt board bytes remain unchanged");
    }

    private static async Task<string> CheckDuplicateBoardAsync(ProjectBoardStore store, string sourcePath, string root)
    {
        string project = Read<ProjectBoardRegistration[]>(await CallAsync(store, "project.list", new { }))[0].BoardId.ToString();
        string copy = Path.Combine(root, "copy 项目");
        CopyDirectory(sourcePath, copy);
        TaskResponse conflict = await CallAsync(store, "project.register", new { path = copy });
        CheckCode(conflict, "CONFLICT", "a copied board ID cannot register at another path implicitly");
        var moved = Read<ProjectBoardRegistration>(await CallAsync(store, "project.register", new { path = copy, replacePath = true }));
        Check(moved.Path == Path.GetFullPath(copy), "replacePath updates the local association");
        return copy;
    }

    private static async Task CheckUnregisterAsync(ProjectBoardStore store, string copy, string source)
    {
        string project = Read<ProjectBoardRegistration[]>(await CallAsync(store, "project.list", new { }))[0].BoardId.ToString();
        TaskResponse unregistered = await CallAsync(store, "project.unregister", new { project });
        Check(unregistered.Ok && File.Exists(Path.Combine(copy, ".toolbox", "board.json")) && File.Exists(Path.Combine(source, ".toolbox", "board.json")), "unregister removes only the local index, not project data");
        Check(Array(Read(await CallAsync(store, "project.list", new { }))).Length == 0, "project list no longer includes an unregistered board");
    }

    private static async Task CheckTodoPendingRecoveryAsync(string root)
    {
        string testRoot = Path.Combine(root, "pending-test");
        string registryRoot = Path.Combine(testRoot, "registry");
        string projectPath = Path.Combine(testRoot, "project");
        Directory.CreateDirectory(projectPath);
        var todo = new FakeTodo { HoldRegistryOnNextCreate = true, RegistryFilePath = Path.Combine(registryRoot, "project-boards.json") };
        var store = new ProjectBoardStore(registryRoot, todo.ExecuteAsync);
        ProjectBoardRegistration? project = null;
        ProjectBoardCard? createdCard = null;
        try
        {
            project = Read<ProjectBoardRegistration>(await CallAsync(store, "project.register", new { path = projectPath, init = true }));
            createdCard = ReadWrapped<ProjectBoardCard>(await CallAsync(store, "board.card.create", new { project = project.BoardId.ToString(), title = "one copy" }), "card");
            TaskResponse first = await CallAsync(store, "board.card.add-to-todo", new { project = project.BoardId.ToString(), id = createdCard.Id });
            CheckCode(first, "IO_ERROR", "Todo ID is reported when association persistence is blocked");
            Check(first.Error!.Message.Contains(todo.LastCreatedId!, StringComparison.Ordinal), "association error names the created Todo ID");
        }
        finally { todo.Dispose(); }

        var retry = new ProjectBoardStore(registryRoot, todo.ExecuteAsync);
        TaskResponse retried = await CallAsync(retry, "board.card.add-to-todo", new { project = project!.BoardId.ToString(), id = createdCard!.Id });
        CheckCode(retried, "CONFLICT", "pending association requires the user to inspect personal Todo");
        Check(todo.Created.Count == 1, "ambiguous Todo creation does not silently create another active Todo");
    }

    private static async Task CheckPathEscapeAsync(ProjectBoardStore store, string root)
    {
        string project = Path.Combine(root, "escape project");
        string outside = Path.Combine(root, "outside data");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(outside);
        try { Directory.CreateSymbolicLink(Path.Combine(project, ".toolbox"), outside); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Console.WriteLine("Symlink escape check skipped: this Windows account cannot create a directory symlink.");
            return;
        }
        TaskResponse escaped = await CallAsync(store, "project.register", new { path = project, init = true });
        CheckCode(escaped, "ACCESS_DENIED", "reparse-point board directory is rejected");
        Check(!File.Exists(Path.Combine(outside, "board.json")), "path escape did not write outside the project root");
    }

    private static async Task CheckBoardWritePreflightAsync(string root)
    {
        string testRoot = Path.Combine(root, "write-preflight");
        string registryRoot = Path.Combine(testRoot, "registry");
        string projectPath = Path.Combine(testRoot, "project");
        Directory.CreateDirectory(projectPath);
        var store = new ProjectBoardStore(registryRoot, request => Task.FromResult(TaskResponse.Failure(request, "NOT_FOUND", "Task not found.")));
        var registration = Read<ProjectBoardRegistration>(await CallAsync(store, "project.register", new { path = projectPath, init = true }));
        string boardPath = Path.Combine(projectPath, ".toolbox", "board.json");
        byte[] beforeOversizedWrite = File.ReadAllBytes(boardPath);

        TaskResponse oversized = await CallAsync(store, "board.card.create", new
        {
            project = registration.BoardId.ToString(), title = "too large", description = new string('x', TaskProtocol.MaxBytes)
        });
        CheckCode(oversized, "RESULT_TOO_LARGE", "an oversized card response is rejected before committing the card");
        Check(beforeOversizedWrite.SequenceEqual(File.ReadAllBytes(boardPath)), "oversized create leaves board bytes unchanged");

        JsonObject external = JsonNode.Parse(File.ReadAllBytes(boardPath))!.AsObject();
        external["revision"] = long.MaxValue;
        File.WriteAllText(boardPath, external.ToJsonString());
        byte[] maxRevisionFile = File.ReadAllBytes(boardPath);
        var freshStore = new ProjectBoardStore(registryRoot, request => Task.FromResult(TaskResponse.Failure(request, "NOT_FOUND", "Task not found.")));
        TaskResponse overflow = await CallAsync(freshStore, "board.card.create", new
        {
            project = registration.BoardId.ToString(), title = "revision overflow", revision = long.MaxValue
        });
        CheckCode(overflow, "CONFLICT", "revision at Int64.MaxValue cannot be incremented");
        Check(maxRevisionFile.SequenceEqual(File.ReadAllBytes(boardPath)), "revision overflow leaves board bytes unchanged");
    }

    private static void CheckNewBoardAtomicNoOverwrite(string root)
    {
        string directory = Path.Combine(root, "atomic-create");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "board.json");
        byte[] original = Encoding.UTF8.GetBytes("external board");
        File.WriteAllBytes(path, original);
        try
        {
            ProjectBoardStore.WriteNewAtomic(path, Encoding.UTF8.GetBytes("replacement board"));
            throw new InvalidOperationException("Check failed: atomic initialization should reject a pre-existing file");
        }
        catch (TaskCommandException exception) when (exception.Code == "CONFLICT") { }
        Check(original.SequenceEqual(File.ReadAllBytes(path)), "atomic new-board write never replaces an existing file");
    }

    private static async Task CheckInvalidTodoIdAsync(string root)
    {
        string testRoot = Path.Combine(root, "invalid-todo-id");
        string projectPath = Path.Combine(testRoot, "project");
        string registryRoot = Path.Combine(testRoot, "registry");
        Directory.CreateDirectory(projectPath);
        int createCalls = 0;
        Task<TaskResponse> ExecuteTodo(TaskRequest request)
        {
            if (request.Operation == "task.create")
            {
                createCalls++;
                return Task.FromResult(TaskResponse.Success(request, new { id = "not-a-todo-id" }));
            }
            return Task.FromResult(TaskResponse.Failure(request, "NOT_FOUND", "Task not found."));
        }

        var store = new ProjectBoardStore(registryRoot, ExecuteTodo);
        var registration = Read<ProjectBoardRegistration>(await CallAsync(store, "project.register", new { path = projectPath, init = true }));
        var card = ReadWrapped<ProjectBoardCard>(await CallAsync(store, "board.card.create", new { project = registration.BoardId.ToString(), title = "copy safely" }), "card");
        TaskResponse invalid = await CallAsync(store, "board.card.add-to-todo", new { project = registration.BoardId.ToString(), id = card.Id });
        CheckCode(invalid, "DATA_CORRUPT", "a malformed Todo ID is rejected");

        TaskResponse retry = await CallAsync(store, "board.card.add-to-todo", new { project = registration.BoardId.ToString(), id = card.Id });
        CheckCode(retry, "CONFLICT", "invalid Todo ID keeps a pending association and blocks duplicate creation");
        Check(createCalls == 1, "malformed Todo ID cannot trigger a second Todo create");
        using JsonDocument registry = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(registryRoot, "project-boards.json")));
        Check(registry.RootElement.GetProperty("todoLinks")[0].GetProperty("todoId").ValueKind == JsonValueKind.Null, "invalid ID did not contaminate the Todo link index");
    }

    private static async Task<TaskResponse> CallAsync(ProjectBoardStore store, string operation, object payload, Guid? requestId = null)
    {
        return await store.ExecuteAsync(new TaskRequest
        {
            RequestId = (requestId ?? Guid.NewGuid()).ToString(),
            Operation = operation,
            Payload = JsonSerializer.SerializeToElement(payload, Web)
        });
    }

    private static async Task<TaskResponse> CallRawAsync(ProjectBoardStore store, string operation, string payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        return await store.ExecuteAsync(new TaskRequest { RequestId = Guid.NewGuid().ToString(), Operation = operation, Payload = document.RootElement.Clone() });
    }

    private static JsonElement Read(TaskResponse response)
    {
        Check(response.Ok, $"expected success, got {response.Error?.Code}: {response.Error?.Message}");
        return JsonSerializer.SerializeToElement(response.Data, Web);
    }

    private static T Read<T>(TaskResponse response) => Read(response).Deserialize<T>(Web)!;

    private static T ReadWrapped<T>(TaskResponse response, string field) => Read(response).GetProperty(field).Deserialize<T>(Web)!;

    private static JsonElement[] Array(JsonElement element, string? field = null)
    {
        if (field != null) element = element.GetProperty(field);
        return element.EnumerateArray().ToArray();
    }

    private static string String(JsonElement element, string property) => element.GetProperty(property).GetString()!;

    private static void CheckCode(TaskResponse response, string code, string label) =>
        Check(!response.Ok && response.Error?.Code == code, $"{label}; got {(response.Ok ? "success" : response.Error?.Code)}");

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException($"Check failed: {label}");
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (string child in Directory.GetDirectories(source)) CopyDirectory(child, Path.Combine(destination, Path.GetFileName(child)));
    }

    private sealed class FakeTodo : IDisposable
    {
        private int nextId;
        private FileStream? lockedRegistry;
        public List<string> Created { get; } = [];
        public string? LastCreatedId { get; private set; }
        public bool HoldRegistryOnNextCreate { get; set; }
        public string? RegistryFilePath { get; set; }

        public Task<TaskResponse> ExecuteAsync(TaskRequest request)
        {
            if (request.Operation == "task.create")
            {
                string id = (++nextId).ToString("x12");
                LastCreatedId = id;
                Created.Add(id);
                if (HoldRegistryOnNextCreate)
                {
                    HoldRegistryOnNextCreate = false;
                    lockedRegistry = new FileStream(RegistryFilePath!, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
                return Task.FromResult(TaskResponse.Success(request, new { id }));
            }
            if (request.Operation == "task.get" && Created.Contains(request.Id ?? ""))
                return Task.FromResult(TaskResponse.Success(request, new { id = request.Id, isTrashed = false }));
            return Task.FromResult(TaskResponse.Failure(request, "NOT_FOUND", "Task not found."));
        }

        public void Dispose() => lockedRegistry?.Dispose();
    }
}
