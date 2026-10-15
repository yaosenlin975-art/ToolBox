using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using ToolBox.Core.Boards;
using ToolBox.Core.Todo;

internal static class Program
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions Storage = new(JsonSerializerDefaults.Web) { IgnoreReadOnlyProperties = true };

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
        if (args.Contains("--state-tree", StringComparer.Ordinal))
        {
            Directory.CreateDirectory(root);
            try { await CheckStateAndTreeAsync(root); }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            Console.WriteLine("Project board state and tree checks passed.");
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

    private static async Task CheckStateAndTreeAsync(string root)
    {
        string testRoot = Path.Combine(root, "state-tree");
        string registryRoot = Path.Combine(testRoot, "registry");
        string projectPath = Path.Combine(testRoot, "project");
        Directory.CreateDirectory(projectPath);
        var store = new ProjectBoardStore(registryRoot, request => Task.FromResult(TaskResponse.Failure(request, "NOT_FOUND", "Todo is unavailable in board checks.")));
        var registration = Read<ProjectBoardRegistration>(await CallAsync(store, "project.register", new { path = projectPath, init = true }));
        string project = registration.BoardId.ToString();
        ProjectBoard board = Read<ProjectBoard>(await CallAsync(store, "board.get", new { project }));
        Check(board.SchemaVersion == 2 && board.CommentRevision == 0 && board.Columns.Select(c => c.Status).ToHashSet().SetEquals(new[] { "notStarted", "inProgress", "pendingReview", "closed" }), "new boards use schema 2 and map all four statuses once");
        using (JsonDocument boardResponse = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(
            await CallAsync(store, "board.get", new { project }), TaskProtocol.Options)))
        {
            Check(boardResponse.RootElement.GetProperty("data").GetProperty("boardRevision").GetInt64() == board.Revision &&
                boardResponse.RootElement.GetProperty("data").GetProperty("commentRevision").GetInt64() == board.CommentRevision,
                "board.get exposes boardRevision and commentRevision aliases");
        }
        using (JsonDocument boardFile = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(projectPath, ".toolbox", "board.json"))) )
            Check(!boardFile.RootElement.TryGetProperty("boardRevision", out _), "the computed boardRevision alias is not written to disk");

        TaskResponse duplicateStatus = await CallAsync(store, "board.column.create", new { project, title = "Second in progress", status = "inProgress" });
        CheckCode(duplicateStatus, "CONFLICT", "a status cannot map to two columns");

        ProjectBoardCard releaseCard = ReadWrapped<ProjectBoardCard>(await CallAsync(store, "board.card.create", new { project, title = "release" }), "card");
        ProjectBoardCard claimed = ReadWrapped<ProjectBoardCard>(await CallAsync(store, "board.card.claim", new
        {
            project, id = releaseCard.Id, actor = "actor-a", host = "codex", session = "session-a", ttlMinutes = 5
        }), "card");
        Check(claimed.Status == "inProgress" && claimed.Claim?.ExpiresAt - claimed.Claim?.ClaimedAt == TimeSpan.FromMinutes(5), "claim atomically sets in-progress state and the requested lease");
        CheckCode(await CallAsync(store, "board.card.release", new { project, id = releaseCard.Id, actor = "actor-a", host = "codex", session = "other-session" }), "CONFLICT", "session is part of claim ownership");
        ProjectBoardCard released = ReadWrapped<ProjectBoardCard>(await CallAsync(store, "board.card.release", new { project, id = releaseCard.Id, actor = "actor-a", host = "codex", session = "session-a" }), "card");
        Check(released.Status == "notStarted" && released.Claim == null && released.Assignee == null, "release clears owner and returns to not-started");

        ProjectBoardCard contested = ReadWrapped<ProjectBoardCard>(await CallAsync(store, "board.card.create", new { project, title = "contest" }), "card");
        TaskResponse[] claims = await Task.WhenAll(
            CallAsync(store, "board.card.claim", new { project, id = contested.Id, actor = "actor-a", host = "codex", session = "a" }),
            CallAsync(store, "board.card.claim", new { project, id = contested.Id, actor = "actor-b", host = "claude", session = "b" }));
        Check(claims.Count(response => response.Ok) == 1 && claims.Count(response => response.Error?.Code == "CONFLICT") == 1, "concurrent claims grant exactly one execution owner");
        JsonElement conflictData = JsonSerializer.SerializeToElement(claims.Single(response => !response.Ok).Data, Web);
        Check(conflictData.GetProperty("conflictType").GetString() == "CLAIMED_BY_OTHER" && conflictData.GetProperty("expiresAt").ValueKind == JsonValueKind.String, "claim conflict includes the owner and expiry");
        string winningActor = claims[0].Ok ? "actor-a" : "actor-b";
        string winningHost = claims[0].Ok ? "codex" : "claude";
        string winningSession = claims[0].Ok ? "a" : "b";
        ProjectBoardCard reorderedClaim = ReadWrapped<ProjectBoardCard>(await CallAsync(store, "board.card.create", new { project, title = "claimed sibling" }), "card");
        CheckCode(await CallAsync(store, "board.card.claim", new
        {
            project, id = reorderedClaim.Id, actor = "inserter", host = "toolbox", columnId = board.Columns.Single(c => c.Status == "inProgress").Id, order = 0
        }), "CONFLICT", "claiming into an earlier position cannot reorder another owner's claimed card");
        Check(ReadWrapped<ProjectBoardCard>(await CallAsync(store, "board.card.get", new { project, id = reorderedClaim.Id }), "card").Status == "notStarted",
            "a blocked sibling reorder does not partially claim the inserted card");
        CheckCode(await CallAsync(store, "board.card.update", new { project, id = contested.Id, title = "other actor", actor = "other", host = "other-host" }), "CONFLICT", "an unowned update cannot change a claimed card");
        CheckCode(await CallAsync(store, "board.card.move", new { project, id = contested.Id, targetColumnId = board.Columns.Single(c => c.Status == "notStarted").Id, actor = "other", host = "other-host" }), "CONFLICT", "an unowned move cannot change a claimed card");
        ProjectBoardCard ownerEdit = ReadWrapped<ProjectBoardCard>(await CallAsync(store, "board.card.update", new
        {
            project, id = contested.Id, title = "owner edit", actor = winningActor, host = winningHost, session = winningSession
        }), "card");
        Check(ownerEdit.Title == "owner edit", "the matching host, actor, and session can update a claimed card");
        ProjectBoardCard heartbeat = ReadWrapped<ProjectBoardCard>(await CallAsync(store, "board.card.claim", new
        {
            project, id = contested.Id, actor = winningActor, host = winningHost, session = winningSession, ttlMinutes = 5
        }), "card");
        Check(heartbeat.Claim!.HeartbeatAt >= claimed.Claim!.HeartbeatAt, "the owner can renew the claim");

        string contestedBoardPath = Path.Combine(projectPath, ".toolbox", "board.json");
        _ = await CallAsync(store, "board.get", new { project });
        JsonObject expiredBoard = JsonNode.Parse(File.ReadAllBytes(contestedBoardPath))!.AsObject();
        JsonObject expiredCard = expiredBoard["cards"]!.AsArray().Single(node => node!["id"]!.GetValue<Guid>() == contested.Id)!.AsObject();
        JsonObject expiredClaim = expiredCard["claim"]!.AsObject();
        expiredClaim["claimedAt"] = "2020-01-01T00:00:00Z";
        expiredClaim["heartbeatAt"] = "2020-01-01T00:00:00Z";
        expiredClaim["expiresAt"] = "2020-01-01T00:05:00Z";
        File.WriteAllText(contestedBoardPath, expiredBoard.ToJsonString());
        CheckCode(await CallAsync(store, "board.get", new { project }), "CONFLICT", "same-store external file edits remain protected by the hash check");
        TaskResponse expiredWrite = await CallAsync(store, "board.card.update", new { project, id = contested.Id, title = "expired write" });
        CheckCode(expiredWrite, "CONFLICT", "an expired lease blocks task writes until it is reclaimed");
        using (JsonDocument expiredConflict = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(expiredWrite, TaskProtocol.Options)))
            Check(expiredConflict.RootElement.GetProperty("data").GetProperty("conflictType").GetString() == "CLAIM_EXPIRED",
                "expired claim conflicts identify expiry and retain claim owner context");
        var restartedStore = new ProjectBoardStore(registryRoot, request => Task.FromResult(TaskResponse.Failure(request, "NOT_FOUND", "Todo is unavailable in board checks.")));
        ProjectBoardCard takeover = ReadWrapped<ProjectBoardCard>(await CallAsync(restartedStore, "board.card.claim", new
        {
            project, id = contested.Id, actor = "actor-c", host = "codex", session = "c", ttlMinutes = 5
        }), "card");
        Check(takeover.Claim?.ActorId == "actor-c", "a fresh store atomically takes over an expired lease");

        ProjectBoardCard workflow = ReadWrapped<ProjectBoardCard>(await CallAsync(restartedStore, "board.card.create", new { project, title = "workflow" }), "card");
        ProjectBoardCard workflowChild = ReadWrapped<ProjectBoardCard>(await CallAsync(restartedStore, "board.card.create", new { project, title = "child", parentId = workflow.Id }), "card");
        Guid inProgressColumn = board.Columns.Single(c => c.Status == "inProgress").Id;
        ProjectBoardCard transitionCard = ReadWrapped<ProjectBoardCard>(await CallAsync(restartedStore, "board.card.create", new { project, title = "move transition" }), "card");
        CheckCode(await CallAsync(restartedStore, "board.card.move", new
        {
            project, id = transitionCard.Id, targetColumnId = inProgressColumn
        }), "CONFLICT", "moving to a mapped workflow column requires an explicit transition");
        ProjectBoardCard moved = ReadWrapped<ProjectBoardCard>(await CallAsync(restartedStore, "board.card.move", new
        {
            project, id = transitionCard.Id, targetColumnId = inProgressColumn, transition = "claim",
            actor = "worker", host = "codex", session = "move-session"
        }), "card");
        Check(moved.Status == "inProgress" && moved.ColumnId == inProgressColumn && moved.Claim?.ActorId == "worker",
            "a move transition atomically claims the card and places it in the mapped column");
        ProjectBoardCard submitted = ReadWrapped<ProjectBoardCard>(await CallAsync(restartedStore, "board.card.submit", new
        {
            project, id = transitionCard.Id, actor = "worker", host = "codex", session = "move-session"
        }), "card");
        Check(submitted.Status == "pendingReview" && submitted.ColumnId == board.Columns.Single(c => c.Status == "pendingReview").Id,
            "submit updates status and mapped column atomically");
        ProjectBoardCard completed = ReadWrapped<ProjectBoardCard>(await CallAsync(restartedStore, "board.card.close", new
        {
            project, id = transitionCard.Id, actor = "local-user", host = "toolbox"
        }), "card");
        Check(completed.Status == "closed", "pending-review cards can be closed by the local user");
        ProjectBoardCard reopened = ReadWrapped<ProjectBoardCard>(await CallAsync(restartedStore, "board.card.reopen", new
        {
            project, id = transitionCard.Id, actor = "local-user", host = "toolbox"
        }), "card");
        Check(reopened.Status == "notStarted" && reopened.Claim == null && reopened.Assignee == null,
            "reopen returns a card to not-started and clears stale ownership");

        ProjectBoardCard workingRoot = ReadWrapped<ProjectBoardCard>(await CallAsync(restartedStore, "board.card.claim", new
        {
            project, id = workflow.Id, actor = "worker", host = "codex", session = "workflow"
        }), "card");
        _ = workingRoot;
        _ = await CallAsync(restartedStore, "board.card.submit", new { project, id = workflow.Id, actor = "worker", host = "codex", session = "workflow" });
        CheckCode(await CallAsync(restartedStore, "board.card.close", new { project, id = workflow.Id, actor = "local-user", host = "toolbox" }),
            "CONFLICT", "a parent cannot close while an active descendant is open");
        _ = await CallAsync(restartedStore, "board.card.claim", new { project, id = workflowChild.Id, actor = "worker", host = "codex", session = "workflow" });
        _ = await CallAsync(restartedStore, "board.card.submit", new { project, id = workflowChild.Id, actor = "worker", host = "codex", session = "workflow" });
        _ = await CallAsync(restartedStore, "board.card.close", new { project, id = workflowChild.Id, actor = "local-user", host = "toolbox" });
        _ = await CallAsync(restartedStore, "board.card.close", new { project, id = workflow.Id, actor = "local-user", host = "toolbox" });
        CheckCode(await CallAsync(restartedStore, "board.card.create", new { project, title = "under closed", parentId = workflow.Id }),
            "CONFLICT", "a closed parent cannot receive an open child");
        CheckCode(await CallAsync(restartedStore, "board.card.create", new
        {
            project, title = "in closed column", columnId = board.Columns.Single(c => c.Status == "closed").Id
        }), "CONFLICT", "a new card cannot bypass workflow state by choosing the closed column");
        _ = await CallAsync(restartedStore, "board.card.reopen", new { project, id = workflow.Id, actor = "local-user", host = "toolbox" });
        ProjectBoardCard cycleChild = ReadWrapped<ProjectBoardCard>(await CallAsync(restartedStore, "board.card.create", new { project, title = "cycle child", parentId = workflow.Id }), "card");
        CheckCode(await CallAsync(restartedStore, "board.card.update", new { project, id = workflow.Id, parentId = cycleChild.Id }),
            "CYCLE_DETECTED", "reparenting cannot create a cycle");
        CheckCode(await CallAsync(restartedStore, "board.card.update", new { project, id = workflow.Id, parentId = workflow.Id }),
            "CYCLE_DETECTED", "a card cannot become its own parent");

        ProjectBoardCard partialRoot = ReadWrapped<ProjectBoardCard>(await CallAsync(restartedStore, "board.card.create", new { project, title = "closed root with old deletion" }), "card");
        ProjectBoardCard partialChild = ReadWrapped<ProjectBoardCard>(await CallAsync(restartedStore, "board.card.create", new { project, title = "previously deleted", parentId = partialRoot.Id }), "card");
        _ = await CallAsync(restartedStore, "board.card.delete", new { project, id = partialChild.Id });
        _ = await CallAsync(restartedStore, "board.card.claim", new { project, id = partialRoot.Id, actor = "worker", host = "codex", session = "partial" });
        _ = await CallAsync(restartedStore, "board.card.submit", new { project, id = partialRoot.Id, actor = "worker", host = "codex", session = "partial" });
        _ = await CallAsync(restartedStore, "board.card.close", new { project, id = partialRoot.Id, actor = "local-user", host = "toolbox" });
        _ = await CallAsync(restartedStore, "board.card.delete", new { project, id = partialRoot.Id });
        _ = await CallAsync(restartedStore, "board.card.restore", new { project, id = partialRoot.Id });
        ProjectBoardCard[] partialCards = Array(Read(await CallAsync(restartedStore, "board.card.list", new { project, includeDeleted = true })), "cards")
            .Select(card => card.Deserialize<ProjectBoardCard>(Web)!).ToArray();
        Check(partialCards.Single(card => card.Id == partialRoot.Id).DeletedAt == null && partialCards.Single(card => card.Id == partialChild.Id).DeletedAt != null,
            "restoring a deleted ancestor preserves descendants that were already deleted before the subtree operation");

        await CheckDeepTreeAsync(restartedStore, project, projectPath);
        await CheckV1MigrationAsync(root);
        await CheckColumnDeleteClaimAsync(root);
    }

    private static async Task CheckColumnDeleteClaimAsync(string root)
    {
        string projectPath = Path.Combine(root, "column-delete-claim", "project");
        var store = new ProjectBoardStore(Path.Combine(root, "column-delete-claim", "registry"));
        Directory.CreateDirectory(projectPath);
        var registration = Read<ProjectBoardRegistration>(await CallAsync(store, "project.register", new { path = projectPath, init = true }));
        string project = registration.BoardId.ToString();
        ProjectBoard board = Read<ProjectBoard>(await CallAsync(store, "board.get", new { project }));
        Guid inProgressColumn = board.Columns.Single(column => column.Status == "inProgress").Id;
        Guid notStartedColumn = board.Columns.Single(column => column.Status == "notStarted").Id;
        ProjectBoardCard card = ReadWrapped<ProjectBoardCard>(await CallAsync(store, "board.card.create", new { project, title = "claimed before column delete" }), "card");
        _ = await CallAsync(store, "board.card.claim", new { project, id = card.Id, actor = "deleter", host = "codex", session = "column-delete" });
        CheckCode(await CallAsync(store, "board.column.delete", new { project, id = inProgressColumn, targetColumnId = notStartedColumn }),
            "CONFLICT", "deleting a mapped column still protects its claimed cards");
        _ = await CallAsync(store, "board.column.delete", new
        {
            project, id = inProgressColumn, targetColumnId = notStartedColumn, actor = "deleter", host = "codex", session = "column-delete"
        });
        ProjectBoardCard relocated = ReadWrapped<ProjectBoardCard>(await CallAsync(store, "board.card.get", new { project, id = card.Id }), "card");
        Check(relocated.Status == "inProgress" && relocated.ColumnId == notStartedColumn,
            "deleting a mapped column relocates an owned card without changing its workflow state");
    }

    private static async Task CheckDeepTreeAsync(ProjectBoardStore initialStore, string project, string projectPath)
    {
        ProjectBoard board = Read<ProjectBoard>(await CallAsync(initialStore, "board.get", new { project }));
        Guid columnId = board.Columns.Single(c => c.Status == "notStarted").Id;
        DateTime now = DateTime.UtcNow;
        var cards = new List<ProjectBoardCard>();
        Guid? parentId = null;
        const int depth = 1024;
        for (int i = 0; i < depth; i++)
        {
            var card = new ProjectBoardCard(Guid.NewGuid(), $"deep-{i}", "", columnId, 0, 0, [], null, now, now, null,
                "notStarted", parentId, null, null, [], []);
            cards.Add(card);
            parentId = card.Id;
        }
        ProjectBoard deepBoard = board with { Cards = ReorderTestCards(cards), Revision = board.Revision + 1 };
        string boardPath = Path.Combine(projectPath, ".toolbox", "board.json");
        File.WriteAllBytes(boardPath, JsonSerializer.SerializeToUtf8Bytes(deepBoard, Storage));
        var store = new ProjectBoardStore(Path.GetFullPath(Path.Combine(projectPath, "..", "registry")));
        ProjectBoard read = Read<ProjectBoard>(await CallAsync(store, "board.get", new { project }));
        Check(read.Cards.Count == depth && read.Cards.Count(c => c.ParentId != null) == depth - 1, "the store reads a 1024-level card tree iteratively");
        Guid rootId = cards[0].Id;
        TaskResponse deleted = await CallAsync(store, "board.card.delete", new { project, id = rootId });
        Check(deleted.Ok, "subtree delete handles a 1024-level tree");
        ProjectBoardCard[] removed = Array(Read(await CallAsync(store, "board.card.list", new { project, includeDeleted = true })), "cards")
            .Select(card => card.Deserialize<ProjectBoardCard>(Web)!).ToArray();
        Check(removed.Length == depth && removed.All(card => card.DeletedAt != null), "subtree deletion soft-deletes every descendant");
        CheckCode(await CallAsync(store, "board.card.restore", new { project, id = cards[1].Id }), "CONFLICT", "a child cannot be restored before its deleted ancestor");
        TaskResponse restored = await CallAsync(store, "board.card.restore", new { project, id = rootId });
        ProjectBoardCard[] restoredCards = Array(Read(await CallAsync(store, "board.card.list", new { project, includeDeleted = true })), "cards")
            .Select(card => card.Deserialize<ProjectBoardCard>(Web)!).ToArray();
        Check(restored.Ok && restoredCards.All(card => card.DeletedAt == null), "subtree restore restores the full deep tree");
    }

    private static IReadOnlyList<ProjectBoardCard> ReorderTestCards(IEnumerable<ProjectBoardCard> cards) => cards.ToArray();

    private static async Task CheckV1MigrationAsync(string root)
    {
        string testRoot = Path.Combine(root, "v1-migration");
        string registryRoot = Path.Combine(testRoot, "registry");
        string projectPath = Path.Combine(testRoot, "project");
        Directory.CreateDirectory(projectPath);
        var store = new ProjectBoardStore(registryRoot, request => Task.FromResult(TaskResponse.Failure(request, "NOT_FOUND", "Todo is unavailable in board checks.")));
        var registration = Read<ProjectBoardRegistration>(await CallAsync(store, "project.register", new { path = projectPath, init = true }));
        string project = registration.BoardId.ToString();
        ProjectBoard board = Read<ProjectBoard>(await CallAsync(store, "board.get", new { project }));
        ProjectBoardCard card = ReadWrapped<ProjectBoardCard>(await CallAsync(store, "board.card.create", new { project, title = "legacy active" }), "card");
        JsonObject v1 = JsonNode.Parse(File.ReadAllBytes(Path.Combine(projectPath, ".toolbox", "board.json")))!.AsObject();
        v1["schemaVersion"] = 1;
        v1.Remove("commentRevision");
        v1.Remove("commentsExternal");
        foreach (JsonObject column in v1["columns"]!.AsArray().Select(node => node!.AsObject())) column.Remove("status");
        v1["columns"]!.AsArray()[0]!["title"] = "未开始";
        v1["columns"]!.AsArray().Add(JsonSerializer.SerializeToNode(new
        {
            id = Guid.NewGuid(), title = "待办", order = 4
        }, Web));
        JsonObject cardNode = v1["cards"]!.AsArray().Single(node => node!["id"]!.GetValue<Guid>() == card.Id)!.AsObject();
        cardNode["columnId"] = board.Columns.Single(column => column.Status == "inProgress").Id;
        cardNode["status"] = null;
        cardNode["parentId"] = null;
        cardNode["assignee"] = null;
        cardNode["claim"] = null;
        cardNode.Remove("status");
        cardNode.Remove("parentId");
        cardNode.Remove("assignee");
        cardNode.Remove("claim");
        cardNode.Remove("attachments");
        cardNode.Remove("comments");
        string boardPath = Path.Combine(projectPath, ".toolbox", "board.json");
        File.WriteAllText(boardPath, v1.ToJsonString());
        byte[] legacyBytes = File.ReadAllBytes(boardPath);
        var migratedStore = new ProjectBoardStore(registryRoot, request => Task.FromResult(TaskResponse.Failure(request, "NOT_FOUND", "Todo is unavailable in board checks.")));
        TaskResponse migratedResponse = await CallAsync(migratedStore, "board.get", new { project });
        ProjectBoard migrated = Read<ProjectBoard>(migratedResponse);
        ProjectBoardCard migratedCard = migrated.Cards.Single();
        Check(migrated.SchemaVersion == 2 && migratedCard.Status == "notStarted" && migratedCard.ColumnId == cardNode["columnId"]!.GetValue<Guid>() && migratedCard.Order == 0, "v1 in-progress cards migrate in memory while preserving their column and order");
        Check(migrated.Columns.Single(column => column.Title == "未开始").Status == "notStarted" && migrated.Columns.Single(column => column.Title == "待办").Status == null,
            "duplicate legacy aliases deterministically keep only the first ordered status mapping");
        Check(migratedResponse.Warnings?.Count == 1 && migratedResponse.Warnings[0].Contains("duplicate", StringComparison.Ordinal) && File.ReadAllBytes(boardPath).SequenceEqual(legacyBytes),
            "v1 reads report migration and duplicate mappings without persisting changes");
        TaskResponse write = await CallAsync(migratedStore, "board.card.update", new { project, id = card.Id, title = "upgraded" });
        using JsonDocument upgraded = JsonDocument.Parse(File.ReadAllBytes(boardPath));
        Check(write.Ok && upgraded.RootElement.GetProperty("schemaVersion").GetInt32() == 2 && upgraded.RootElement.GetProperty("cards")[0].GetProperty("status").GetString() == "notStarted", "the first successful board write upgrades v1 to schema 2");
    }

    private static async Task CheckRegistrationAndCrudAsync(ProjectBoardStore store, string path, FakeTodo todo)
    {
        var registration = Read<ProjectBoardRegistration>(await CallAsync(store, "project.register", new { path, init = true }));
        Check(registration.BoardId != Guid.Empty && !Directory.Exists(Path.Combine(path, ".git")), "registers a non-Git directory at a Unicode path");
        Check(File.Exists(Path.Combine(path, ".toolbox", "board.json")), "initialization creates the project board file");
        var board = Read<ProjectBoard>(await CallAsync(store, "board.get", new { project = registration.BoardId.ToString() }));
        Check(board.SchemaVersion == 2 && board.Columns.Count == 4 && board.Columns.All(c => c.Id != Guid.Empty) && board.Columns.All(c => c.Status != null), "creates four stable UUID status columns");
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
        ProjectBoardCard defaultCard = ReadWrapped<ProjectBoardCard>(first, "card");
        ProjectBoard board = Read<ProjectBoard>(await CallAsync(store, "board.get", new { project }));
        Check(defaultCard.Status == "notStarted" && defaultCard.ColumnId == board.Columns.Single(column => column.Status == "notStarted").Id,
            "default card creation follows the not-started mapping after column reorder");
        TaskResponse duplicate = await CallAsync(store, "board.card.create", new { project, title = "dedup" }, requestId);
        Check(defaultCard.Id == ReadWrapped<ProjectBoardCard>(duplicate, "card").Id, "same requestId returns the original card result");
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
