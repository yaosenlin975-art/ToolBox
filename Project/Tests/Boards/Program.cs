using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using ToolBox.Core.Boards;
using ToolBox.Core.Todo;

internal static class Program
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions Storage = new(JsonSerializerDefaults.Web) { IgnoreReadOnlyProperties = true };
    private static readonly JsonSerializerOptions BoardStorageOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        IgnoreReadOnlyProperties = true
    };

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
        if (args.Contains("--comments", StringComparer.Ordinal))
        {
            Directory.CreateDirectory(root);
            try { await CheckCommentsAsync(root); }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            Console.WriteLine("Project board comment checks passed.");
            return;
        }
        if (args.Contains("--attachments", StringComparer.Ordinal))
        {
            Directory.CreateDirectory(root);
            try { await CheckAttachmentsAsync(root); }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            Console.WriteLine("Project board attachment checks passed.");
            return;
        }
        if (args.Contains("--dedup", StringComparer.Ordinal))
        {
            string dedupProjectPath = Path.Combine(root, "project");
            Directory.CreateDirectory(dedupProjectPath);
            var dedupTodo = new FakeTodo();
            var dedupStore = new ProjectBoardStore(Path.Combine(root, "registry"), dedupTodo.ExecuteAsync);
            try
            {
                ProjectBoardRegistration dedupRegistration = Read<ProjectBoardRegistration>(await CallAsync(dedupStore, "project.register", new { path = dedupProjectPath, init = true }));
                _ = await CallAsync(dedupStore, "board.card.create", new { project = dedupRegistration.BoardId.ToString(), title = "seed" });
                await CheckRequestDeduplicationAsync(dedupStore, dedupProjectPath);
            }
            finally
            {
                dedupTodo.Dispose();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            Console.WriteLine("Project board deduplication checks passed.");
            return;
        }
        if (args.Contains("--comment-inline-stress", StringComparer.Ordinal))
        {
            Directory.CreateDirectory(root);
            try { await CheckCommentInlineWriteStressAsync(root); }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            Console.WriteLine("Project board inline comment write stress passed.");
            return;
        }
        if (args.Contains("--atomic-retry", StringComparer.Ordinal))
        {
            Directory.CreateDirectory(root);
            try { await CheckAtomicReplacementRetryAsync(root); }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            Console.WriteLine("Project board atomic replacement retry checks passed.");
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
            Console.WriteLine("attachments");
            await CheckAttachmentsAsync(root);
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

    private static async Task CheckCommentsAsync(string root)
    {
        await CheckCommentPermissionsRoutingInboxAndPagingAsync(Path.Combine(root, "comment-core"));
        await CheckCommentExternalizationThresholdsAsync(Path.Combine(root, "comment-thresholds"));
        await CheckCommentMigrationRecoveryAsync(Path.Combine(root, "comment-recovery"));
        await CheckCommentResponseLimitAsync(Path.Combine(root, "comment-response-limit"));
        await CheckInternalCommentClaimAsync(Path.Combine(root, "internal-claim"));
    }

    private static async Task CheckInternalCommentClaimAsync(string root)
    {
        string registryRoot = Path.Combine(root, "registry");
        string projectPath = Path.Combine(root, "project");
        Directory.CreateDirectory(projectPath);
        Func<TaskRequest, Task<TaskResponse>> todo = request =>
            Task.FromResult(TaskResponse.Failure(request, "NOT_FOUND", "Todo is unavailable in internal claim checks."));
        var store = new ProjectBoardStore(registryRoot, todo);
        var registration = Read<ProjectBoardRegistration>(await CallAsync(store, "project.register", new { path = projectPath, init = true }));
        string project = registration.BoardId.ToString();
        string boardPath = Path.Combine(projectPath, ".toolbox", "board.json");
        Guid internalActor = Guid.NewGuid();
        const string internalSession = "internal-session";

        ProjectBoardCard bareCard = ReadWrapped<ProjectBoardCard>(await CallAsync(store, "board.card.create", new { project, title = "no claim" }), "card");
        byte[] beforeReject = File.ReadAllBytes(boardPath);
        TaskResponse rejected = await CallAsync(store, "board.comment.add", new
        {
            project, id = bareCard.Id, body = "rejected", actor = internalActor, host = "internal", session = internalSession
        });
        CheckCode(rejected, "CONFLICT", "an internal identity without any claim is rejected");
        Check(File.ReadAllBytes(boardPath).SequenceEqual(beforeReject), "a rejected internal comment writes nothing");
        Check(Array(Read(await CallAsync(store, "board.comment.list", new { project, id = bareCard.Id })), "comments").Length == 0,
            "a rejected internal comment never reaches the card comment list");

        ProjectBoardCard inboxCard = ReadWrapped<ProjectBoardCard>(await CallAsync(store, "board.card.create", new { project, title = "inbox claim" }), "card");
        Guid seedActor = Guid.NewGuid();
        ProjectBoardComment seedRoot = ReadWrapped<ProjectBoardComment>(await CallAsync(store, "board.comment.add", new
        {
            project, id = inboxCard.Id, body = "root", actor = seedActor, host = "seed-host", session = "seed-session"
        }), "comment");
        _ = await CallAsync(store, "board.comment.bind", new
        {
            project, commentId = seedRoot.Id, targetHost = "internal", targetSession = internalSession,
            actor = seedActor, host = "seed-host", session = "seed-session"
        });
        ProjectBoardComment held = ReadWrapped<ProjectBoardComment>(await CallAsync(store, "board.comment.add", new
        {
            project, id = inboxCard.Id, parentId = seedRoot.Id, body = "held reply", actor = seedActor, host = "seed-host", session = "seed-session"
        }), "comment");
        Check(held.Routing is { Host: "internal", SessionId: internalSession }, "the reservable reply routes to the internal host");
        JsonElement inbox = Read(await CallAsync(store, "board.comment.inbox", new
        {
            project, host = "internal", actor = internalActor, session = internalSession, limit = 1
        }));
        Check(Array(inbox, "comments")[0].GetProperty("id").GetGuid() == held.Id, "the internal inbox reserves the routed reply");
        TaskResponse inboxReply = await CallAsync(store, "board.comment.add", new
        {
            project, id = inboxCard.Id, parentId = held.Id, body = "internal inbox reply", actor = internalActor, host = "internal", session = internalSession
        });
        Check(inboxReply.Ok, $"an internal reply is allowed while its parent holds a matching inbox claim: {inboxReply.Error?.Code}");

        ProjectBoardCard claimedCard = ReadWrapped<ProjectBoardCard>(await CallAsync(store, "board.card.create", new { project, title = "card claim" }), "card");
        _ = await CallAsync(store, "board.card.claim", new { project, id = claimedCard.Id, actor = internalActor, host = "internal", session = internalSession });
        TaskResponse claimedComment = await CallAsync(store, "board.comment.add", new
        {
            project, id = claimedCard.Id, body = "owner comment", actor = internalActor, host = "internal", session = internalSession
        });
        Check(claimedComment.Ok, $"an internal comment is allowed while holding the card claim: {claimedComment.Error?.Code}");

        ProjectBoardCard humanCard = ReadWrapped<ProjectBoardCard>(await CallAsync(store, "board.card.create", new { project, title = "human host" }), "card");
        ProjectBoardComment humanComment = ReadWrapped<ProjectBoardComment>(await CallAsync(store, "board.comment.add", new
        {
            project, id = humanCard.Id, body = "human comment", actor = Guid.NewGuid(), host = "human", session = "human-session"
        }), "comment");
        Check(humanComment.Body == "human comment" && humanComment.Author.HostId == "human",
            "a non-internal host without any claim keeps full comment semantics");

        JsonObject inboxBoard = JsonNode.Parse(File.ReadAllBytes(boardPath))!.AsObject();
        JsonObject inboxCardNode = inboxBoard["cards"]!.AsArray()
            .Select(node => node!.AsObject())
            .First(card => card["id"]!.GetValue<Guid>() == inboxCard.Id);
        JsonObject heldNode = inboxCardNode["comments"]!.AsArray()
            .Select(node => node!.AsObject())
            .First(comment => comment["id"]!.GetValue<Guid>() == held.Id);
        heldNode["inboxClaim"]!["expiresAt"] = "2020-01-01T00:00:30Z";
        File.WriteAllText(boardPath, inboxBoard.ToJsonString());
        var expiredInboxStore = new ProjectBoardStore(registryRoot, todo);
        TaskResponse expiredInboxReply = await CallAsync(expiredInboxStore, "board.comment.add", new
        {
            project, id = inboxCard.Id, parentId = held.Id, body = "expired inbox reply", actor = internalActor, host = "internal", session = internalSession
        });
        CheckCode(expiredInboxReply, "CONFLICT", "an expired inbox claim no longer authorizes an internal reply");

        ProjectBoardCard expiredCard = ReadWrapped<ProjectBoardCard>(await CallAsync(expiredInboxStore, "board.card.create", new { project, title = "expired claim" }), "card");
        _ = await CallAsync(expiredInboxStore, "board.card.claim", new { project, id = expiredCard.Id, actor = internalActor, host = "internal", session = internalSession });
        JsonObject expiredBoard = JsonNode.Parse(File.ReadAllBytes(boardPath))!.AsObject();
        JsonObject expiredCardNode = expiredBoard["cards"]!.AsArray()
            .Select(node => node!.AsObject())
            .First(card => card["id"]!.GetValue<Guid>() == expiredCard.Id);
        JsonObject expiredClaimNode = expiredCardNode["claim"]!.AsObject();
        expiredClaimNode["claimedAt"] = "2020-01-01T00:00:00Z";
        expiredClaimNode["heartbeatAt"] = "2020-01-01T00:00:00Z";
        expiredClaimNode["expiresAt"] = "2020-01-01T00:05:00Z";
        File.WriteAllText(boardPath, expiredBoard.ToJsonString());
        var expiredCardStore = new ProjectBoardStore(registryRoot, todo);
        TaskResponse expiredCardComment = await CallAsync(expiredCardStore, "board.comment.add", new
        {
            project, id = expiredCard.Id, body = "expired card claim", actor = internalActor, host = "internal", session = internalSession
        });
        CheckCode(expiredCardComment, "CONFLICT", "an expired card claim no longer authorizes an internal comment");
    }

    private static async Task CheckCommentInlineWriteStressAsync(string root)
    {
        string projectPath = Path.Combine(root, "project");
        Directory.CreateDirectory(projectPath);
        var store = new ProjectBoardStore(Path.Combine(root, "registry"), request => Task.FromResult(TaskResponse.Failure(request, "NOT_FOUND", "Todo unavailable.")));
        ProjectBoardRegistration registration = Read<ProjectBoardRegistration>(await CallAsync(store, "project.register", new { path = projectPath, init = true }));
        string project = registration.BoardId.ToString();
        ProjectBoardCard card = ReadWrapped<ProjectBoardCard>(await CallAsync(store, "board.card.create", new { project, title = "inline write stress" }), "card");
        string body = new string('界', 5400);
        string actor = Guid.NewGuid().ToString("D");
        for (int index = 0; index < 40; index++)
        {
            TaskResponse response = await CallAsync(store, "board.comment.add", new
            {
                project, id = card.Id, body, actor, host = "external-host", session = "external-session", commentRevision = index
            });
            if (!response.Ok)
                throw new InvalidOperationException($"Inline comment write {index + 1} failed: {response.Error?.Code}: {response.Error?.Message}");
            long commentRevision = Read(response).GetProperty("commentRevision").GetInt64();
            Check(commentRevision == index + 1, $"inline comment write {index + 1} advances commentRevision");
        }
        Check(!Read<ProjectBoard>(await CallAsync(store, "board.get", new { project })).CommentsExternal,
            "the inline stress fixture stays below the sidecar threshold");
    }

    private static async Task CheckAtomicReplacementRetryAsync(string root)
    {
        string projectPath = Path.Combine(root, "project");
        Directory.CreateDirectory(projectPath);
        var store = new ProjectBoardStore(Path.Combine(root, "registry"), request => Task.FromResult(TaskResponse.Failure(request, "NOT_FOUND", "Todo unavailable.")));
        ProjectBoardRegistration registration = Read<ProjectBoardRegistration>(await CallAsync(store, "project.register", new { path = projectPath, init = true }));
        string project = registration.BoardId.ToString();
        _ = await CallAsync(store, "board.card.create", new { project, title = "seed" });
        string boardPath = Path.Combine(projectPath, ".toolbox", "board.json");
        ProjectBoard before = Read<ProjectBoard>(await CallAsync(store, "board.get", new { project }));

        var releasedLock = new FileStream(boardPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Task releaseAfterRetry = Task.Run(async () =>
        {
            await Task.Delay(120);
            releasedLock.Dispose();
        });
        TaskResponse recovered = await CallAsync(store, "board.card.create", new { project, title = "after transient lock" });
        await releaseAfterRetry;
        Check(recovered.Ok, $"the transient sharing violation recovers: {recovered.Error?.Code}: {recovered.Error?.Message}");
        ProjectBoard afterRetry = Read<ProjectBoard>(await CallAsync(store, "board.get", new { project }));
        Check(afterRetry.Revision == before.Revision + 1, "a transient sharing violation retries one board write and advances revision once");
        Check(afterRetry.Cards.Count == before.Cards.Count + 1 && afterRetry.Cards.Any(card => card.Title == "after transient lock"),
            "a transient sharing violation creates exactly one card");

        byte[] beforePermanentFailure = File.ReadAllBytes(boardPath);
        var permanentLock = new FileStream(boardPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        TaskResponse permanentFailure = await CallAsync(store, "board.card.create", new { project, title = "must not be written" });
        permanentLock.Dispose();
        CheckCode(permanentFailure, "IO_ERROR", "a permanent sharing violation exhausts bounded retries");
        Check(File.ReadAllBytes(boardPath).SequenceEqual(beforePermanentFailure), "a permanent sharing violation leaves board bytes unchanged");

        ProjectBoard current = Read<ProjectBoard>(await CallAsync(store, "board.get", new { project }));
        byte[] beforeExternalChange = File.ReadAllBytes(boardPath);
        var contestedLock = new FileStream(boardPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] externallyChanged = beforeExternalChange.Concat(new byte[] { (byte)' ' }).ToArray();
        Task externalChange = Task.Run(async () =>
        {
            await Task.Delay(80);
            File.WriteAllBytes(boardPath, externallyChanged);
            contestedLock.Dispose();
        });
        TaskResponse externalConflict = await CallAsync(store, "board.card.create", new { project, title = "must lose to external change" });
        await externalChange;
        CheckCode(externalConflict, "CONFLICT", "a retry detects an external board change before replacing it");
        Check(externalConflict.Error?.Field == "revision", "an external board change reports the revision field");
        Check(File.ReadAllBytes(boardPath).SequenceEqual(externallyChanged), "the retry preserves externally changed board bytes");
        using JsonDocument changedDocument = JsonDocument.Parse(externallyChanged);
        Check(changedDocument.RootElement.GetProperty("revision").GetInt64() == current.Revision &&
            changedDocument.RootElement.GetProperty("cards").GetArrayLength() == current.Cards.Count &&
            !changedDocument.RootElement.GetProperty("cards").EnumerateArray().Any(card => card.GetProperty("title").GetString() == "must lose to external change"),
            "a retry does not apply its board mutation after the external change");
    }


    private static async Task CheckCommentPermissionsRoutingInboxAndPagingAsync(string root)
    {
        string registryRoot = Path.Combine(root, "registry");
        string projectPath = Path.Combine(root, "project");
        Directory.CreateDirectory(projectPath);
        var store = new ProjectBoardStore(registryRoot, request => Task.FromResult(TaskResponse.Failure(request, "NOT_FOUND", "Todo is unavailable in comment checks.")));
        var registration = Read<ProjectBoardRegistration>(await CallAsync(store, "project.register", new { path = projectPath, init = true }));
        string project = registration.BoardId.ToString();
        var card = ReadWrapped<ProjectBoardCard>(await CallAsync(store, "board.card.create", new { project, title = "comment card" }), "card");
        Guid writerActor = Guid.NewGuid();
        Guid replyActor = Guid.NewGuid();
        ProjectBoardComment rootComment = ReadWrapped<ProjectBoardComment>(await CallAsync(store, "board.comment.add", new
        {
            project, id = card.Id, body = "root", actor = writerActor, host = "writer-host", session = "writer-session"
        }), "comment");
        ProjectBoardComment userReply = ReadWrapped<ProjectBoardComment>(await CallAsync(store, "board.comment.add", new
        {
            project, id = card.Id, parentId = rootComment.Id, body = "first reply", actor = "local-user", host = "desktop", session = "ui-session"
        }), "comment");
        ProjectBoardComment agentReply = ReadWrapped<ProjectBoardComment>(await CallAsync(store, "board.comment.add", new
        {
            project, id = card.Id, parentId = userReply.Id, body = "nested reply", actor = replyActor, host = "reply-host", session = "reply-session"
        }), "comment");
        Check(userReply.Routing is { Host: "writer-host", SessionId: "writer-session" } &&
            agentReply.Routing is { Host: "writer-host", SessionId: "writer-session" }, "reply routing snapshots nearest ancestor origin across local-user comments");

        TaskResponse forbidden = await CallAsync(store, "board.comment.update", new
        {
            project, commentId = agentReply.Id, body = "stolen", actor = Guid.NewGuid(), host = "reply-host", session = "reply-session"
        });
        CheckCode(forbidden, "ACCESS_DENIED", "only the comment author can update a comment");
        ProjectBoard board = Read<ProjectBoard>(await CallAsync(store, "board.get", new { project }));
        TaskResponse listResponse = await CallAsync(store, "board.comment.list", new { project, id = card.Id, limit = 1 });
        JsonElement firstPage = Read(listResponse);
        string cursor = firstPage.GetProperty("cursor").GetString()!;
        long pageRevision = firstPage.GetProperty("commentRevision").GetInt64();
        JsonElement secondPage = Read(await CallAsync(store, "board.comment.list", new { project, id = card.Id, limit = 1, cursor, commentRevision = pageRevision }));
        Check(Array(firstPage, "comments").Length == 1 && Array(secondPage, "comments").Length == 1 &&
            Array(firstPage, "comments")[0].GetProperty("id").GetGuid() != Array(secondPage, "comments")[0].GetProperty("id").GetGuid(),
            "comment cursor pages stable ordered comments without duplicates");
        TaskResponse changed = await CallAsync(store, "board.comment.add", new
        {
            project, id = card.Id, body = "after page", actor = writerActor, host = "writer-host", session = "writer-session"
        });
        Check(changed.Ok, "comment add succeeds after paging snapshot");
        CheckCode(await CallAsync(store, "board.comment.list", new { project, id = card.Id, limit = 1, cursor, commentRevision = pageRevision }),
            "CONFLICT", "a comment cursor is rejected after the collection revision changes");
        ProjectBoardComment rebound = ReadWrapped<ProjectBoardComment>(await CallAsync(store, "board.comment.bind", new
        {
            project, commentId = rootComment.Id, targetHost = "replacement-host", targetSession = "replacement-session",
            actor = writerActor, host = "writer-host", session = "writer-session"
        }), "comment");
        ProjectBoardComment routedAfterBind = ReadWrapped<ProjectBoardComment>(await CallAsync(store, "board.comment.add", new
        {
            project, id = card.Id, parentId = userReply.Id, body = "new route", actor = Guid.NewGuid(), host = "later-host", session = "later-session"
        }), "comment");
        Check(rebound.Origin is { Host: "replacement-host", SessionId: "replacement-session" } &&
            userReply.Routing is { Host: "writer-host", SessionId: "writer-session" } &&
            routedAfterBind.Routing is { Host: "replacement-host", SessionId: "replacement-session" },
            "binding changes origin for future replies without changing saved routing");
        _ = await CallAsync(store, "board.comment.bind", new
        {
            project, commentId = rootComment.Id, clear = true, actor = writerActor, host = "writer-host", session = "writer-session"
        });
        ProjectBoardComment unroutedAfterClear = ReadWrapped<ProjectBoardComment>(await CallAsync(store, "board.comment.add", new
        {
            project, id = card.Id, parentId = userReply.Id, body = "route cleared", actor = Guid.NewGuid(), host = "later-host", session = "later-session"
        }), "comment");
        Check(unroutedAfterClear.Routing == null, "clearing an origin affects only replies added afterward");

        string boardPath = Path.Combine(projectPath, ".toolbox", "board.json");
        string boardBeforeClaims = HashBytes(File.ReadAllBytes(boardPath));
        JsonElement inboxOne = Read(await CallAsync(store, "board.comment.inbox", new { project, host = "writer-host", actor = Guid.NewGuid(), limit = 1 }));
        JsonElement firstClaimed = Array(inboxOne, "comments")[0];
        Guid claimedId = firstClaimed.GetProperty("id").GetGuid();
        Guid claimId = firstClaimed.GetProperty("inboxClaim").GetProperty("claimId").GetGuid();
        TimeSpan firstTtl = firstClaimed.GetProperty("inboxClaim").GetProperty("expiresAt").GetDateTime() - DateTime.UtcNow;
        Check(firstTtl > TimeSpan.FromMinutes(29) && firstTtl <= TimeSpan.FromMinutes(30), "inbox defaults to a 30 minute live claim");
        ProjectBoardComment firstComment = claimedId == userReply.Id ? userReply : agentReply;
        TaskResponse idempotent = await CallAsync(store, "board.comment.update", new
        {
            project, commentId = firstComment.Id, body = firstComment.Body,
            actor = firstComment.Id == userReply.Id ? "local-user" : replyActor.ToString("D"),
            host = firstComment.Id == userReply.Id ? "desktop" : "reply-host",
            session = firstComment.Id == userReply.Id ? "ui-session" : "reply-session"
        });
        Check(idempotent.Ok && ReadWrapped<ProjectBoardComment>(idempotent, "comment").InboxClaim?.ClaimId == claimId,
            "an identical body update is idempotent and preserves the inbox claim");

        Guid secondActor = Guid.NewGuid();
        JsonElement inboxTwo = Read(await CallAsync(store, "board.comment.inbox", new { project, host = "writer-host", actor = secondActor, ttlMinutes = 5, limit = 1 }));
        Guid secondClaimedId = Array(inboxTwo, "comments")[0].GetProperty("id").GetGuid();
        Check(secondClaimedId != claimedId, "two inbox actors cannot reserve the same active reply");
        JsonElement secondClaimed = Array(inboxTwo, "comments")[0];
        Guid secondClaimId = secondClaimed.GetProperty("inboxClaim").GetProperty("claimId").GetGuid();
        TimeSpan secondTtl = secondClaimed.GetProperty("inboxClaim").GetProperty("expiresAt").GetDateTime() - DateTime.UtcNow;
        Check(secondTtl > TimeSpan.FromMinutes(4) && secondTtl <= TimeSpan.FromMinutes(5), "inbox accepts the requested five minute TTL");
        ProjectBoardComment firstBodyChanged = ReadWrapped<ProjectBoardComment>(await CallAsync(store, "board.comment.update", new
        {
            project, commentId = firstComment.Id, body = "body changed", actor = firstComment.Id == userReply.Id ? "local-user" : replyActor.ToString("D"),
            host = firstComment.Id == userReply.Id ? "desktop" : "reply-host",
            session = firstComment.Id == userReply.Id ? "ui-session" : "reply-session"
        }), "comment");
        Check(firstBodyChanged.InboxClaim == null && firstBodyChanged.AcknowledgedAt == null,
            "changing a reply body clears its acknowledgement and active inbox claim");
        TaskResponse wrongAck = await CallAsync(store, "board.comment.ack", new
        {
            project, commentId = secondClaimedId, claimId = secondClaimId, actor = Guid.NewGuid(), host = "writer-host"
        });
        CheckCode(wrongAck, "CONFLICT", "ack rejects a different actor even when the claim ID is known");
        CheckCode(await CallAsync(store, "board.comment.ack", new
        {
            project, commentId = secondClaimedId, claimId = secondClaimId, actor = secondActor, host = "writer-host", session = "wrong-session"
        }), "CONFLICT", "ack rejects a different target session");
        ProjectBoardComment acknowledged = ReadWrapped<ProjectBoardComment>(await CallAsync(store, "board.comment.ack", new
        {
            project, commentId = secondClaimedId, claimId = secondClaimId, actor = secondActor, host = "writer-host"
        }), "comment");
        Check(acknowledged.AcknowledgedAt != null && acknowledged.AcknowledgedBy == secondActor.ToString("D") && acknowledged.InboxClaim == null,
            "ack persists the core acknowledgement and releases the claim");
        ProjectBoardComment secondComment = secondClaimedId == userReply.Id ? userReply : agentReply;
        ProjectBoardComment ackReset = ReadWrapped<ProjectBoardComment>(await CallAsync(store, "board.comment.update", new
        {
            project, commentId = secondComment.Id, body = "edited after ack",
            actor = secondComment.Id == userReply.Id ? "local-user" : replyActor.ToString("D"),
            host = secondComment.Id == userReply.Id ? "desktop" : "reply-host",
            session = secondComment.Id == userReply.Id ? "ui-session" : "reply-session"
        }), "comment");
        Check(ackReset.AcknowledgedAt == null && ackReset.AcknowledgedBy == null,
            "changing the acknowledged reply body clears the acknowledgement");
        TaskResponse forbiddenDelete = await CallAsync(store, "board.comment.delete", new
        {
            project, commentId = agentReply.Id, actor = Guid.NewGuid(), host = "reply-host", session = "reply-session"
        });
        CheckCode(forbiddenDelete, "ACCESS_DENIED", "only the author or local user can delete a comment");
        _ = await CallAsync(store, "board.comment.delete", new
        {
            project, commentId = changed.Data == null ? Guid.Empty : ReadWrapped<ProjectBoardComment>(changed, "comment").Id,
            actor = "local-user", host = "desktop", session = "ui-session"
        });
        JsonElement deletedList = Read(await CallAsync(store, "board.comment.list", new { project, id = card.Id, includeDeleted = true }));
        Check(Array(deletedList, "comments").Any(comment => comment.GetProperty("deletedAt").ValueKind == JsonValueKind.String),
            "comment deletion is a soft delete and remains listable");
        Check(HashBytes(File.ReadAllBytes(boardPath)) != boardBeforeClaims && board.Revision ==
            Read<ProjectBoard>(await CallAsync(store, "board.get", new { project })).Revision,
            "inline comment writes advance only commentRevision, not boardRevision");

        using JsonDocument getContext = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(
            await CallAsync(store, "board.comment.get", new { project, commentId = rootComment.Id }), TaskProtocol.Options));
        JsonElement context = getContext.RootElement.GetProperty("data");
        Check(context.GetProperty("revision").GetInt64() == context.GetProperty("boardRevision").GetInt64() &&
            context.GetProperty("commentRevision").GetInt64() > 0, "comment.get returns typed context revision aliases");
        JsonElement boardResponse = Read(await CallAsync(store, "board.get", new { project }));
        JsonElement cardResponse = Array(Read(await CallAsync(store, "board.card.list", new { project })), "cards")[0];
        Check(Array(boardResponse.GetProperty("cards")[0], "comments").Length == 0 &&
            boardResponse.GetProperty("cards")[0].GetProperty("commentCount").GetInt32() >= 4 &&
            cardResponse.GetProperty("commentCount").GetInt32() >= 4,
            "board/card projections hide comment bodies and retain comment counts");
        using JsonDocument storedBoard = JsonDocument.Parse(File.ReadAllBytes(boardPath));
        JsonElement storedCard = storedBoard.RootElement.GetProperty("cards")[0];
        Check(!storedBoard.RootElement.TryGetProperty("boardRevision", out _) && !storedCard.TryGetProperty("commentCount", out _) &&
            !storedCard.TryGetProperty("attachmentCount", out _) && !storedCard.TryGetProperty("childCount", out _) &&
            !storedCard.TryGetProperty("childClosedCount", out _), "API revision aliases and summary counts never enter board storage");
    }

    private static async Task CheckCommentExternalizationThresholdsAsync(string root)
    {
        string registryRoot = Path.Combine(root, "registry");
        string projectPath = Path.Combine(root, "project");
        Directory.CreateDirectory(projectPath);
        var initialStore = new ProjectBoardStore(registryRoot, request => Task.FromResult(TaskResponse.Failure(request, "NOT_FOUND", "Todo unavailable.")));
        var registration = Read<ProjectBoardRegistration>(await CallAsync(initialStore, "project.register", new { path = projectPath, init = true }));
        string project = registration.BoardId.ToString();
        ProjectBoardCard card = ReadWrapped<ProjectBoardCard>(await CallAsync(initialStore, "board.card.create", new { project, title = "threshold" }), "card");
        ProjectBoard board = Read<ProjectBoard>(await CallAsync(initialStore, "board.get", new { project }));
        DateTime now = DateTime.UtcNow;
        string authorId = Guid.NewGuid().ToString("D");
        ProjectBoardComment[] seedComments = Enumerable.Range(0, 500).Select(index => new ProjectBoardComment(
            Guid.NewGuid(), null, "seed", new ProjectBoardAssignee("agent", authorId, authorId, "seed-host", "seed-session"),
            new ProjectBoardCommentOrigin("seed-host", "seed-session"), null, now.AddSeconds(index), now.AddSeconds(index), null)).ToArray();
            board = board with
        {
            CommentRevision = 500,
            Cards = board.Cards.Select(item => item.Id == card.Id ? item with { Comments = seedComments } : item).ToArray()
        };
        string boardPath = Path.Combine(projectPath, ".toolbox", "board.json");
        File.WriteAllBytes(boardPath, SerializeBoard(board));
        var store = new ProjectBoardStore(registryRoot, request => Task.FromResult(TaskResponse.Failure(request, "NOT_FOUND", "Todo unavailable.")));
        ProjectBoard at500 = Read<ProjectBoard>(await CallAsync(store, "board.get", new { project }));
        Check(!at500.CommentsExternal && at500.Cards.Single().CommentCount == 500, "exactly 500 comments remain inline");
        ProjectBoardComment added = ReadWrapped<ProjectBoardComment>(await CallAsync(store, "board.comment.add", new
        {
            project, id = card.Id, body = "501", actor = Guid.NewGuid(), host = "author-host", session = "author-session"
        }), "comment");
        ProjectBoard external = Read<ProjectBoard>(await CallAsync(store, "board.get", new { project }));
        Check(added.Id != Guid.Empty && external.CommentsExternal && external.CommentRevision == 501 &&
            File.Exists(Path.Combine(projectPath, ".toolbox", "comments.json")), "the 501st comment triggers sidecar migration");

        string stableBoardHash = HashBytes(File.ReadAllBytes(boardPath));
        long boardRevision = external.Revision;
        TaskResponse afterExternal = await CallAsync(store, "board.comment.add", new
        {
            project, id = card.Id, body = "sidecar only", actor = Guid.NewGuid(), host = "author-host", session = "author-session"
        });
        ProjectBoard after = Read<ProjectBoard>(await CallAsync(store, "board.get", new { project }));
        Check(afterExternal.Ok && after.Revision == boardRevision && after.CommentRevision == 502 &&
            HashBytes(File.ReadAllBytes(boardPath)) == stableBoardHash,
            "external comment writes advance only sidecar revision and preserve board bytes");

        string sizeProjectPath = Path.Combine(root, "size-project");
        Directory.CreateDirectory(sizeProjectPath);
        string sizeRegistry = Path.Combine(root, "size-registry");
        var sizeInitial = new ProjectBoardStore(sizeRegistry, request => Task.FromResult(TaskResponse.Failure(request, "NOT_FOUND", "Todo unavailable.")));
        var sizeRegistration = Read<ProjectBoardRegistration>(await CallAsync(sizeInitial, "project.register", new { path = sizeProjectPath, init = true }));
        string sizeProject = sizeRegistration.BoardId.ToString();
        ProjectBoardCard sizeCard = ReadWrapped<ProjectBoardCard>(await CallAsync(sizeInitial, "board.card.create", new { project = sizeProject, title = "2MiB" }), "card");
        ProjectBoard sizeBoard = Read<ProjectBoard>(await CallAsync(sizeInitial, "board.get", new { project = sizeProject }));
        ProjectBoardCard emptyCard = sizeBoard.Cards.Single() with { Description = "" };
        ProjectBoard emptyDescriptionBoard = sizeBoard with { Cards = [emptyCard] };
        int baseBytes = SerializeBoard(emptyDescriptionBoard).Length;
        int descriptionLength = 2 * 1024 * 1024 - baseBytes;
        ProjectBoard exactThresholdBoard = sizeBoard with
        {
            Cards = [sizeCard with { Description = new string('x', descriptionLength) }]
        };
        Check(SerializeBoard(exactThresholdBoard).Length == 2 * 1024 * 1024, "the threshold fixture is exactly 2 MiB");
        File.WriteAllBytes(Path.Combine(sizeProjectPath, ".toolbox", "board.json"), SerializeBoard(exactThresholdBoard));
        var sizeStore = new ProjectBoardStore(sizeRegistry, request => Task.FromResult(TaskResponse.Failure(request, "NOT_FOUND", "Todo unavailable.")));
        ProjectBoard exactBoard = Read<ProjectBoard>(await CallAsync(sizeStore, "board.get", new { project = sizeProject }));
        Check(!exactBoard.CommentsExternal, "exactly 2 MiB remains inline before a comment write");
        TaskResponse overThreshold = await CallAsync(sizeStore, "board.comment.add", new
        {
            project = sizeProject, id = sizeCard.Id, body = "x", actor = Guid.NewGuid(), host = "size-host", session = "size-session"
        });
        Check(overThreshold.Ok && Read<ProjectBoard>(await CallAsync(sizeStore, "board.get", new { project = sizeProject })).CommentsExternal,
            "a comment that pushes serialized board size over 2 MiB triggers migration");
    }

    private static async Task CheckCommentMigrationRecoveryAsync(string root)
    {
        foreach (string mode in new[] { "rollback", "commit", "unknown" })
        {
            string scenario = Path.Combine(root, mode);
            string registryRoot = Path.Combine(scenario, "registry");
            string projectPath = Path.Combine(scenario, "project");
            Directory.CreateDirectory(projectPath);
            var firstStore = new ProjectBoardStore(registryRoot, request => Task.FromResult(TaskResponse.Failure(request, "NOT_FOUND", "Todo unavailable.")));
            var registration = Read<ProjectBoardRegistration>(await CallAsync(firstStore, "project.register", new { path = projectPath, init = true }));
            string project = registration.BoardId.ToString();
            ProjectBoardCard card = ReadWrapped<ProjectBoardCard>(await CallAsync(firstStore, "board.card.create", new { project, title = "recovery" }), "card");
            ProjectBoardComment comment = ReadWrapped<ProjectBoardComment>(await CallAsync(firstStore, "board.comment.add", new
            {
                project, id = card.Id, body = "preserve me", actor = Guid.NewGuid(), host = "recover-host", session = "recover-session"
            }), "comment");
            string directory = Path.Combine(projectPath, ".toolbox");
            string boardPath = Path.Combine(directory, "board.json");
            byte[] oldBoard = File.ReadAllBytes(boardPath);
            ProjectBoard inline = Read<ProjectBoard>(await CallAsync(firstStore, "board.get", new { project })) with
            {
                Cards = Read<ProjectBoard>(await CallAsync(firstStore, "board.get", new { project })).Cards
                    .Select(item => item.Id == card.Id ? item with { Comments = [comment] } : item).ToArray()
            };
            ProjectBoard externalBoard = inline with
            {
                CommentsExternal = true,
                Cards = inline.Cards.Select(item => item with { Comments = [] }).ToArray()
            };
            byte[] newBoard = SerializeBoard(externalBoard);
            byte[] newComments = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                boardId = inline.BoardId,
                revision = inline.CommentRevision,
                comments = inline.Cards.SelectMany(item => item.Comments!.Select(saved => new { cardId = item.Id, comment = saved })).ToArray()
            }, Web);
            string oldHash = HashBytes(oldBoard);
            string newBoardHash = HashBytes(newBoard);
            string newCommentsHash = HashBytes(newComments);
            File.WriteAllBytes(Path.Combine(directory, "board.comments.old"), oldBoard);
            File.WriteAllBytes(Path.Combine(directory, "board.comments.next"), newBoard);
            File.WriteAllBytes(Path.Combine(directory, "comments.next"), newComments);
            File.WriteAllBytes(Path.Combine(directory, "comments.migration.json"), JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                boardId = inline.BoardId,
                oldBoardHash = oldHash,
                newBoardHash,
                newCommentsHash
            }, Web));
            File.Move(Path.Combine(directory, "comments.next"), Path.Combine(directory, "comments.json"));
            if (mode != "rollback") File.WriteAllBytes(boardPath, newBoard);
            if (mode == "unknown") File.WriteAllText(boardPath, "unknown external board bytes");

            var recoveredStore = new ProjectBoardStore(registryRoot, request => Task.FromResult(TaskResponse.Failure(request, "NOT_FOUND", "Todo unavailable.")));
            TaskResponse recovered = await CallAsync(recoveredStore, "board.get", new { project });
            if (mode == "rollback")
            {
                ProjectBoard rolledBack = Read<ProjectBoard>(recovered);
                Check(!rolledBack.CommentsExternal && rolledBack.Cards.Single().CommentCount == 1 && !File.Exists(Path.Combine(directory, "comments.json")) &&
                    !File.Exists(Path.Combine(directory, "comments.migration.json")), "interrupted migration rolls back to inline comments");
            }
            else if (mode == "commit")
            {
                ProjectBoard committed = Read<ProjectBoard>(recovered);
                Check(committed.CommentsExternal && committed.Cards.Single().CommentCount == 1 &&
                    !File.Exists(Path.Combine(directory, "comments.migration.json")), "fully installed migration recovers as committed");
            }
            else
            {
                CheckCode(recovered, "DATA_CORRUPT", "unknown migration hash is rejected");
                Check(File.Exists(Path.Combine(directory, "comments.migration.json")) &&
                    File.Exists(Path.Combine(directory, "comments.json")) && File.ReadAllText(boardPath) == "unknown external board bytes",
                    "unknown migration state preserves all files for inspection");
            }
        }
    }

    private static async Task CheckCommentResponseLimitAsync(string root)
    {
        string projectPath = Path.Combine(root, "project");
        Directory.CreateDirectory(projectPath);
        string registryRoot = Path.Combine(root, "registry");
        var initial = new ProjectBoardStore(registryRoot, request => Task.FromResult(TaskResponse.Failure(request, "NOT_FOUND", "Todo unavailable.")));
        var registration = Read<ProjectBoardRegistration>(await CallAsync(initial, "project.register", new { path = projectPath, init = true }));
        string project = registration.BoardId.ToString();
        ProjectBoardCard card = ReadWrapped<ProjectBoardCard>(await CallAsync(initial, "board.card.create", new { project, title = "4 MiB" }), "card");
        ProjectBoard board = Read<ProjectBoard>(await CallAsync(initial, "board.get", new { project }));
        DateTime now = DateTime.UtcNow;
        string actor = Guid.NewGuid().ToString("D");
        ProjectBoardComment[] comments = Enumerable.Range(0, 60).Select(index => new ProjectBoardComment(
            Guid.NewGuid(), null, new string('\u0001', 16 * 1024),
            new ProjectBoardAssignee("agent", actor, actor, "response-host", "response-session"),
            new ProjectBoardCommentOrigin("response-host", "response-session"), null, now.AddSeconds(index), now.AddSeconds(index), null)).ToArray();
        board = board with
        {
            CommentRevision = comments.Length,
            Cards = board.Cards.Select(item => item.Id == card.Id ? item with { Comments = comments } : item).ToArray()
        };
        File.WriteAllBytes(Path.Combine(projectPath, ".toolbox", "board.json"), SerializeBoard(board));
        var store = new ProjectBoardStore(registryRoot, request => Task.FromResult(TaskResponse.Failure(request, "NOT_FOUND", "Todo unavailable.")));
        TaskResponse response = await CallAsync(store, "board.comment.list", new { project, id = card.Id, limit = 100 });
        JsonElement page = Read(response);
        int responseBytes = JsonSerializer.SerializeToUtf8Bytes(response, TaskProtocol.Options).Length;
        Check(responseBytes <= TaskProtocol.MaxBytes && Array(page, "comments").Length < comments.Length && page.GetProperty("cursor").ValueKind == JsonValueKind.String,
            "comment pages shrink to respect the actual 4 MiB serialized response limit");
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
        TaskResponse? nestedResponse = null;
        Exception? nestedException = null;
        Action handler = () =>
        {
            if (reentered) return;
            reentered = true;
            try
            {
                nestedResponse = store.ExecuteAsync(new TaskRequest
                {
                    RequestId = Guid.NewGuid().ToString(), Operation = "board.get",
                    Payload = JsonSerializer.SerializeToElement(new { project }, Web)
                }).GetAwaiter().GetResult();
                nestedReadSucceeded = nestedResponse.Ok;
            }
            catch (Exception ex)
            {
                nestedException = ex;
            }
        };
        store.Changed += handler;
        TaskResponse first = await CallAsync(store, "board.card.create", new { project, title = "dedup" }, requestId);
        store.Changed -= handler;
        Check(first.Ok, $"the write triggering Changed succeeds; got {first.Error?.Code}: {first.Error?.Message}");
        Check(reentered, "a successful write raises Changed before ExecuteAsync completes");
        Check(nestedReadSucceeded, $"Changed handlers can read the store after gate release; got {nestedResponse?.Error?.Code}: {nestedResponse?.Error?.Message}; exception={nestedException?.GetType().Name}: {nestedException?.Message}");
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

    private static byte[] SerializeBoard(ProjectBoard board)
    {
        ProjectBoard storage = board with
        {
            Cards = board.Cards.Select(card => card with
            {
                Comments = board.CommentsExternal ? [] : card.Comments ?? [],
                CommentCount = 0,
                AttachmentCount = 0,
                ChildCount = 0,
                ChildClosedCount = 0
            }).ToArray()
        };
        return JsonSerializer.SerializeToUtf8Bytes(storage, BoardStorageOptions);
    }

    private static string HashBytes(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

    private static async Task CheckAttachmentsAsync(string root)
    {
        await CheckAttachmentBasicsAsync(Path.Combine(root, "attachment-basics"));
        await CheckAttachmentValidationAsync(Path.Combine(root, "attachment-validation"));
        await CheckAttachmentDedupAsync(Path.Combine(root, "attachment-dedup"));
        await CheckAttachmentIntegrityAsync(Path.Combine(root, "attachment-integrity"));
        await CheckAttachmentRollbackAsync(Path.Combine(root, "attachment-rollback"));
        await CheckAttachmentRecoveryAsync(Path.Combine(root, "attachment-recovery"));
        await CheckAttachmentWriteProtectionAsync(Path.Combine(root, "attachment-protection"));
        await CheckAttachmentNullSessionsAsync(Path.Combine(root, "attachment-null-session"));
    }

    private static async Task<(ProjectBoardStore Store, string Project, string ProjectPath)> AttachmentProjectAsync(string testRoot)
    {
        string projectPath = Path.Combine(testRoot, "project");
        Directory.CreateDirectory(projectPath);
        var store = new ProjectBoardStore(Path.Combine(testRoot, "registry"),
            request => Task.FromResult(TaskResponse.Failure(request, "NOT_FOUND", "Todo is unavailable in attachment checks.")));
        ProjectBoardRegistration registration = Read<ProjectBoardRegistration>(await CallAsync(store, "project.register", new { path = projectPath, init = true }));
        return (store, registration.BoardId.ToString(), projectPath);
    }

    private static async Task<(ProjectBoardStore Store, string Project, string ProjectPath, ProjectBoardCard Card)> AttachmentCardAsync(string testRoot)
    {
        (ProjectBoardStore store, string project, string projectPath) = await AttachmentProjectAsync(testRoot);
        ProjectBoardCard card = ReadWrapped<ProjectBoardCard>(await CallAsync(store, "board.card.create", new { project, title = "images" }), "card");
        return (store, project, projectPath, card);
    }

    private static byte[] AttachmentImage(byte alpha)
    {
        using var bitmap = new System.Drawing.Bitmap(1, 1);
        bitmap.SetPixel(0, 0, System.Drawing.Color.FromArgb(alpha, 0, 0, 0));
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return stream.ToArray();
    }

    private static string AttachmentBoardPath(string projectPath) => Path.Combine(projectPath, ".toolbox", "board.json");

    private static string AttachmentJournalPath(string projectPath) => Path.Combine(projectPath, ".toolbox", "attachments.journal.json");

    private static string AttachmentStoredPath(string projectPath, string relativePath) =>
        Path.GetFullPath(Path.Combine(projectPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string AttachmentRelative(string projectPath, string path) =>
        Path.GetRelativePath(projectPath, path).Replace(Path.DirectorySeparatorChar, '/');

    private static string[] AttachmentTemporaryFiles(string projectPath) =>
        Directory.Exists(projectPath) ? Directory.GetFiles(projectPath, "*.tmp", SearchOption.AllDirectories) : [];

    private static JsonElement SingleAttachment(TaskResponse response, Guid attachmentId) =>
        Array(Read(response), "attachments").Single(item => item.GetProperty("id").GetGuid() == attachmentId);

    private static int RemoveAttachmentMetadata(string projectPath, Guid cardId, Guid attachmentId)
    {
        string boardPath = AttachmentBoardPath(projectPath);
        JsonObject board = JsonNode.Parse(File.ReadAllBytes(boardPath))!.AsObject();
        JsonArray attachments = board["cards"]!.AsArray()
            .Single(node => node!["id"]!.GetValue<Guid>() == cardId)!.AsObject()["attachments"]!.AsArray();
        int removed = 0;
        for (int index = attachments.Count - 1; index >= 0; index--)
        {
            if (attachments[index]!["id"]!.GetValue<Guid>() != attachmentId) continue;
            attachments.RemoveAt(index);
            removed++;
        }
        File.WriteAllText(boardPath, board.ToJsonString(BoardStorageOptions));
        return removed;
    }

    private static async Task CheckAttachmentBasicsAsync(string testRoot)
    {
        (ProjectBoardStore store, string project, string projectPath, ProjectBoardCard card) = await AttachmentCardAsync(testRoot);
        string source = Path.Combine(testRoot, "source.png");
        byte[] image = AttachmentImage(255);
        File.WriteAllBytes(source, image);

        JsonElement added = Read(await CallAsync(store, "board.attachment.add", new
        {
            project, id = card.Id, file = source, actor = "image-author", host = "test-host", session = "test-session"
        }));
        Check(added.TryGetProperty("attachment", out _) && added.TryGetProperty("revision", out _) &&
            added.TryGetProperty("boardRevision", out _) && added.TryGetProperty("commentRevision", out _),
            "board.attachment.add returns the frozen v2 shape");
        JsonElement attachment = added.GetProperty("attachment");
        Guid attachmentId = attachment.GetProperty("id").GetGuid();
        Check(attachmentId != Guid.Empty && attachment.GetProperty("fileName").GetString() == "source.png" &&
            attachment.GetProperty("addedBy").GetString() == "image-author",
            "the added image keeps its id, file name, and author");
        Check(attachment.GetProperty("size").GetInt64() == image.Length && attachment.GetProperty("sha256").GetString() == HashBytes(image),
            "the stored size and hash match the source bytes");
        string relativePath = attachment.GetProperty("relativePath").GetString()!;
        Check(relativePath.StartsWith(".toolbox/board-assets/", StringComparison.Ordinal),
            "the stored path is expressed relative to the project root");
        string storedPath = AttachmentStoredPath(projectPath, relativePath);
        Check(storedPath.StartsWith(Path.Combine(projectPath, ".toolbox") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "the stored image resolves inside the project .toolbox directory");
        Check(File.Exists(storedPath) && File.ReadAllBytes(storedPath).SequenceEqual(image), "the stored image bytes match the source");
        Check(Directory.GetFiles(Path.GetDirectoryName(storedPath)!).Length == 1, "adding an image stores exactly one file");

        JsonElement listed = Read(await CallAsync(store, "board.attachment.list", new { project, id = card.Id }));
        Check(listed.TryGetProperty("attachments", out _) && listed.TryGetProperty("revision", out _) &&
            listed.TryGetProperty("boardRevision", out _) && listed.TryGetProperty("commentRevision", out _),
            "board.attachment.list returns the frozen v2 shape");
        JsonElement item = Array(listed, "attachments").Single();
        Check(item.GetProperty("id").GetGuid() == attachmentId &&
            item.GetProperty("exists").GetBoolean() && item.GetProperty("hashMatch").GetBoolean(),
            "a present image is listed as existing and hash-matching");
    }

    private static async Task CheckAttachmentValidationAsync(string testRoot)
    {
        (ProjectBoardStore store, string project, string projectPath, ProjectBoardCard card) = await AttachmentCardAsync(testRoot);
        byte[] before = File.ReadAllBytes(AttachmentBoardPath(projectPath));

        string notImage = Path.Combine(testRoot, "notes.txt");
        File.WriteAllText(notImage, "not an image");
        CheckCode(await CallAsync(store, "board.attachment.add", new { project, id = card.Id, file = notImage, actor = "a", host = "h", session = "s" }),
            "INVALID_ARGUMENT", "an extension outside the whitelist is rejected");

        string fakeImage = Path.Combine(testRoot, "fake.png");
        File.WriteAllText(fakeImage, "not an image");
        CheckCode(await CallAsync(store, "board.attachment.add", new { project, id = card.Id, file = fakeImage, actor = "a", host = "h", session = "s" }),
            "INVALID_ARGUMENT", "bytes that do not decode as an image are rejected");

        CheckCode(await CallAsync(store, "board.attachment.add", new { project, id = card.Id, file = "source.png", actor = "a", host = "h", session = "s" }),
            "INVALID_ARGUMENT", "a relative image path is rejected");

        string oversized = Path.Combine(testRoot, "oversized.png");
        byte[] huge = new byte[10 * 1024 * 1024 + 1];
        System.Array.Copy(AttachmentImage(255), huge, 8);
        File.WriteAllBytes(oversized, huge);
        TaskResponse tooLarge = await CallAsync(store, "board.attachment.add", new { project, id = card.Id, file = oversized, actor = "a", host = "h", session = "s" });
        Check(!tooLarge.Ok && tooLarge.Error?.Code is "INVALID_ARGUMENT" or "ATTACHMENT_TOO_LARGE" or "FILE_TOO_LARGE",
            "an image over 10 MiB is rejected with an attachment size error");

        Check(File.ReadAllBytes(AttachmentBoardPath(projectPath)).SequenceEqual(before), "rejected images leave board metadata unchanged");
        Check(AttachmentTemporaryFiles(projectPath).Length == 0, "rejected images leave no temporary file behind");
        string assetsDirectory = Path.Combine(projectPath, ".toolbox", "board-assets");
        Check(!Directory.Exists(assetsDirectory) || Directory.GetFiles(assetsDirectory, "*", SearchOption.AllDirectories).Length == 0,
            "rejected images never reach the asset directory");
    }

    private static async Task CheckAttachmentDedupAsync(string testRoot)
    {
        (ProjectBoardStore store, string project, string projectPath, ProjectBoardCard card) = await AttachmentCardAsync(testRoot);
        ProjectBoardCard other = ReadWrapped<ProjectBoardCard>(await CallAsync(store, "board.card.create", new { project, title = "other" }), "card");
        string source = Path.Combine(testRoot, "source.png");
        File.WriteAllBytes(source, AttachmentImage(255));

        JsonElement first = Read(await CallAsync(store, "board.attachment.add", new { project, id = card.Id, file = source, actor = "a", host = "h", session = "s" }));
        JsonElement second = Read(await CallAsync(store, "board.attachment.add", new { project, id = card.Id, file = source, actor = "b", host = "h", session = "s" }));
        Guid firstId = first.GetProperty("attachment").GetProperty("id").GetGuid();
        Check(second.GetProperty("attachment").GetProperty("id").GetGuid() == firstId, "identical bytes on the same card return the existing attachment");
        Check(second.GetProperty("revision").GetInt64() == first.GetProperty("revision").GetInt64(), "deduplication does not advance the board revision");
        string storedPath = AttachmentStoredPath(projectPath, first.GetProperty("attachment").GetProperty("relativePath").GetString()!);
        Check(Directory.GetFiles(Path.GetDirectoryName(storedPath)!).Length == 1, "deduplication copies the image only once");

        JsonElement acrossCards = Read(await CallAsync(store, "board.attachment.add", new { project, id = other.Id, file = source, actor = "a", host = "h", session = "s" }));
        Check(acrossCards.GetProperty("attachment").GetProperty("id").GetGuid() != firstId, "deduplication is scoped to a single card");
    }

    private static async Task CheckAttachmentIntegrityAsync(string testRoot)
    {
        (ProjectBoardStore store, string project, string projectPath, ProjectBoardCard card) = await AttachmentCardAsync(testRoot);
        string source = Path.Combine(testRoot, "source.png");
        byte[] image = AttachmentImage(255);
        File.WriteAllBytes(source, image);
        JsonElement added = Read(await CallAsync(store, "board.attachment.add", new { project, id = card.Id, file = source, actor = "a", host = "h", session = "s" }));
        Guid attachmentId = added.GetProperty("attachment").GetProperty("id").GetGuid();
        string storedPath = AttachmentStoredPath(projectPath, added.GetProperty("attachment").GetProperty("relativePath").GetString()!);
        byte[] boardBefore = File.ReadAllBytes(AttachmentBoardPath(projectPath));

        File.Delete(storedPath);
        JsonElement missing = SingleAttachment(await CallAsync(store, "board.attachment.list", new { project, id = card.Id }), attachmentId);
        Check(!missing.GetProperty("exists").GetBoolean() && !missing.GetProperty("hashMatch").GetBoolean(), "a deleted image is listed as missing");

        File.WriteAllBytes(storedPath, AttachmentImage(128));
        JsonElement replaced = SingleAttachment(await CallAsync(store, "board.attachment.list", new { project, id = card.Id }), attachmentId);
        Check(replaced.GetProperty("exists").GetBoolean() && !replaced.GetProperty("hashMatch").GetBoolean(),
            "a replaced image is listed as existing with a hash mismatch");

        File.WriteAllBytes(storedPath, image);
        JsonElement unchanged = SingleAttachment(await CallAsync(store, "board.attachment.list", new { project, id = card.Id }), attachmentId);
        Check(unchanged.GetProperty("exists").GetBoolean() && unchanged.GetProperty("hashMatch").GetBoolean(), "an untouched image hash-matches again");

        Check(File.ReadAllBytes(AttachmentBoardPath(projectPath)).SequenceEqual(boardBefore),
            "external file changes never rewrite attachment metadata or touch other files");
    }

    private static async Task CheckAttachmentRollbackAsync(string testRoot)
    {
        (ProjectBoardStore store, string project, string projectPath, ProjectBoardCard card) = await AttachmentCardAsync(testRoot);
        string source = Path.Combine(testRoot, "source.png");
        File.WriteAllBytes(source, AttachmentImage(255));
        byte[] boardBefore = File.ReadAllBytes(AttachmentBoardPath(projectPath));
        File.WriteAllText(Path.Combine(projectPath, ".toolbox", "board-assets"), "blocked");

        TaskResponse failed = await CallAsync(store, "board.attachment.add", new { project, id = card.Id, file = source, actor = "a", host = "h", session = "s" });
        CheckCode(failed, "IO_ERROR", "an unusable asset directory fails the copy");
        Check(File.ReadAllBytes(AttachmentBoardPath(projectPath)).SequenceEqual(boardBefore), "a failed copy commits no metadata");
        Check(Array(Read(await CallAsync(store, "board.attachment.list", new { project, id = card.Id })), "attachments").Length == 0,
            "a failed copy adds no attachment");
        Check(AttachmentTemporaryFiles(projectPath).Length == 0, "a failed copy leaves no temporary file behind");
    }

    private static async Task CheckAttachmentRecoveryAsync(string testRoot)
    {
        await CheckAttachmentRecoveryModeAsync(Path.Combine(testRoot, "restore"), "restore");
        await CheckAttachmentRecoveryModeAsync(Path.Combine(testRoot, "clean"), "clean");
        await CheckAttachmentRecoveryModeAsync(Path.Combine(testRoot, "tampered"), "tampered");
        await CheckAttachmentRecoveryModeAsync(Path.Combine(testRoot, "both"), "both");
        await CheckAttachmentUnreadableJournalAsync(Path.Combine(testRoot, "broken-journal"));
    }

    private static async Task CheckAttachmentRecoveryModeAsync(string testRoot, string mode)
    {
        (ProjectBoardStore store, string project, string projectPath, ProjectBoardCard card) = await AttachmentCardAsync(testRoot);
        string source = Path.Combine(testRoot, "source.png");
        File.WriteAllBytes(source, AttachmentImage(255));
        JsonElement added = Read(await CallAsync(store, "board.attachment.add", new { project, id = card.Id, file = source, actor = "a", host = "h", session = "s" }));
        Guid attachmentId = added.GetProperty("attachment").GetProperty("id").GetGuid();
        string storedPath = AttachmentStoredPath(projectPath, added.GetProperty("attachment").GetProperty("relativePath").GetString()!);
        string tombstonePath = storedPath + ".tombstone";
        string journalPath = AttachmentJournalPath(projectPath);

        // 中断点：文件已改名为 tombstone 且 journal 已落盘；元数据按 mode 决定是否仍在。
        File.Move(storedPath, tombstonePath);
        string recordedHash = mode == "tampered" ? HashBytes(AttachmentImage(128)) : HashBytes(File.ReadAllBytes(tombstonePath));
        if (mode == "clean") RemoveAttachmentMetadata(projectPath, card.Id, attachmentId);
        // both：用户从备份或 git 恢复了原文件，此刻原文件与 tombstone 同时存在。
        if (mode == "both") File.WriteAllBytes(storedPath, File.ReadAllBytes(tombstonePath));
        File.WriteAllBytes(journalPath, JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            entries = new[]
            {
                new
                {
                    attachmentId,
                    cardId = card.Id,
                    storedPath = AttachmentRelative(projectPath, storedPath),
                    tombstonePath = AttachmentRelative(projectPath, tombstonePath),
                    sha256 = recordedHash
                }
            }
        }, Web));
        byte[] boardBefore = File.ReadAllBytes(AttachmentBoardPath(projectPath));

        var restarted = new ProjectBoardStore(Path.Combine(testRoot, "registry"),
            request => Task.FromResult(TaskResponse.Failure(request, "NOT_FOUND", "Todo is unavailable in attachment checks.")));
        TaskResponse read = await CallAsync(restarted, "board.get", new { project });
        Check(read.Ok, $"recovery read succeeded for mode {mode}: {read.Error?.Code}");
        if (mode is "tampered" or "both")
        {
            // 无法安全判定时既不改写元数据，也不自动清除证据：journal 与双方文件原样保留。
            Check(File.Exists(journalPath), $"{mode}: the journal is preserved for a skipped entry");
            Check(File.Exists(tombstonePath), $"{mode}: the tombstone is preserved for a skipped entry");
            Check(File.Exists(storedPath) == (mode == "both"), $"{mode}: the stored file is neither created nor removed automatically");
            bool expectedAvailable = mode == "both";
            JsonElement item = SingleAttachment(await CallAsync(restarted, "board.attachment.list", new { project, id = card.Id }), attachmentId);
            Check(item.GetProperty("exists").GetBoolean() == expectedAvailable && item.GetProperty("hashMatch").GetBoolean() == expectedAvailable,
                $"{mode}: availability is reported through markers without rewriting metadata");
            Check(File.ReadAllBytes(AttachmentBoardPath(projectPath)).SequenceEqual(boardBefore), $"{mode}: attachment metadata is left untouched");
            return;
        }
        Check(!File.Exists(journalPath), "a finished recovery leaves no journal behind");
        if (mode == "restore")
        {
            Check(File.Exists(storedPath) && !File.Exists(tombstonePath), "live metadata restores the tombstoned image");
            JsonElement item = SingleAttachment(await CallAsync(restarted, "board.attachment.list", new { project, id = card.Id }), attachmentId);
            Check(item.GetProperty("exists").GetBoolean() && item.GetProperty("hashMatch").GetBoolean(), "the restored image still matches its metadata");
        }
        else
        {
            Check(!File.Exists(storedPath) && !File.Exists(tombstonePath), "removed metadata cleans the tombstone");
            Check(Array(Read(await CallAsync(restarted, "board.attachment.list", new { project, id = card.Id })), "attachments").Length == 0,
                "the removed attachment stays removed");
        }
    }

    private static async Task CheckAttachmentUnreadableJournalAsync(string testRoot)
    {
        (ProjectBoardStore store, string project, string projectPath, ProjectBoardCard card) = await AttachmentCardAsync(testRoot);
        string source = Path.Combine(testRoot, "source.png");
        byte[] image = AttachmentImage(255);
        File.WriteAllBytes(source, image);
        JsonElement added = Read(await CallAsync(store, "board.attachment.add", new { project, id = card.Id, file = source, actor = "a", host = "h", session = "s" }));
        Guid attachmentId = added.GetProperty("attachment").GetProperty("id").GetGuid();
        string storedPath = AttachmentStoredPath(projectPath, added.GetProperty("attachment").GetProperty("relativePath").GetString()!);
        byte[] boardBefore = File.ReadAllBytes(AttachmentBoardPath(projectPath));
        string journalPath = AttachmentJournalPath(projectPath);
        File.WriteAllText(journalPath, "{ this is not a valid attachment journal");

        var restarted = new ProjectBoardStore(Path.Combine(testRoot, "registry"),
            request => Task.FromResult(TaskResponse.Failure(request, "NOT_FOUND", "Todo is unavailable in attachment checks.")));
        TaskResponse read = await CallAsync(restarted, "board.get", new { project });
        Check(read.Ok, $"an unparseable journal never makes the board unreadable: {read.Error?.Code}");
        Check(File.Exists(journalPath), "an unparseable journal is preserved instead of deleted");
        JsonElement item = SingleAttachment(await CallAsync(restarted, "board.attachment.list", new { project, id = card.Id }), attachmentId);
        Check(item.GetProperty("exists").GetBoolean() && item.GetProperty("hashMatch").GetBoolean(),
            "a live image is still listed as existing and hash-matching");
        Check(File.Exists(storedPath) && File.ReadAllBytes(storedPath).SequenceEqual(image), "an unparseable journal leaves the image file untouched");
        Check(File.ReadAllBytes(AttachmentBoardPath(projectPath)).SequenceEqual(boardBefore), "an unparseable journal rewrites no metadata");
    }

    private static async Task CheckAttachmentWriteProtectionAsync(string testRoot)
    {
        (ProjectBoardStore store, string project, string projectPath, ProjectBoardCard card) = await AttachmentCardAsync(testRoot);
        string source = Path.Combine(testRoot, "source.png");
        File.WriteAllBytes(source, AttachmentImage(255));
        _ = ReadWrapped<ProjectBoardCard>(await CallAsync(store, "board.card.claim", new
        {
            project, id = card.Id, actor = "owner", host = "codex", session = "owner-session", ttlMinutes = 5
        }), "card");

        TaskResponse foreignAdd = await CallAsync(store, "board.attachment.add", new
        {
            project, id = card.Id, file = source, actor = "other", host = "claude", session = "other-session"
        });
        CheckCode(foreignAdd, "CONFLICT", "another owner's claim blocks adding an image");
        Check(Array(Read(await CallAsync(store, "board.attachment.list", new { project, id = card.Id })), "attachments").Length == 0,
            "a blocked add writes no metadata");

        JsonElement added = Read(await CallAsync(store, "board.attachment.add", new
        {
            project, id = card.Id, file = source, actor = "owner", host = "codex", session = "owner-session"
        }));
        Guid attachmentId = added.GetProperty("attachment").GetProperty("id").GetGuid();
        string storedPath = AttachmentStoredPath(projectPath, added.GetProperty("attachment").GetProperty("relativePath").GetString()!);

        TaskResponse foreignRemove = await CallAsync(store, "board.attachment.remove", new
        {
            project, attachmentId, actor = "other", host = "claude", session = "other-session"
        });
        CheckCode(foreignRemove, "CONFLICT", "another owner's claim blocks removing an image");
        Check(File.Exists(storedPath), "a blocked remove keeps the image file");

        TaskResponse ownRemove = await CallAsync(store, "board.attachment.remove", new
        {
            project, attachmentId, actor = "owner", host = "codex", session = "owner-session"
        });
        Check(Read(ownRemove).GetProperty("attachment").GetProperty("id").GetGuid() == attachmentId,
            "the claim owner removes the image and gets the removed attachment back");
        Check(!File.Exists(storedPath), "the removal moves the image out of the asset directory");
    }

    private static async Task CheckAttachmentNullSessionsAsync(string testRoot)
    {
        (ProjectBoardStore store, string project, string projectPath) = await AttachmentProjectAsync(testRoot);
        string? nullSession = null;
        TaskResponse created = await CallAsync(store, "board.card.create", new
        {
            project, title = "null session", actor = "local-user", host = "toolbox", session = nullSession
        });
        Check(created.Ok, "board.card.create accepts an explicit null session");
        ProjectBoardCard card = ReadWrapped<ProjectBoardCard>(created, "card");

        string source = Path.Combine(testRoot, "source.png");
        File.WriteAllBytes(source, AttachmentImage(255));
        TaskResponse added = await CallAsync(store, "board.attachment.add", new
        {
            project, id = card.Id, file = source, actor = "local-user", host = "toolbox", session = nullSession
        });
        Check(added.Ok, "board.attachment.add accepts an explicit null session");
        Guid attachmentId = Read(added).GetProperty("attachment").GetProperty("id").GetGuid();

        TaskResponse commented = await CallAsync(store, "board.comment.add", new
        {
            project, id = card.Id, body = "hello", actor = "local-user", host = "toolbox", session = nullSession
        });
        Check(commented.Ok, "board.comment.add accepts an explicit null session");
        Guid commentId = ReadWrapped<ProjectBoardComment>(commented, "comment").Id;

        TaskResponse bound = await CallAsync(store, "board.comment.bind", new
        {
            project, commentId, targetHost = "other-host", targetSession = nullSession, actor = "local-user", host = "toolbox", session = nullSession
        });
        Check(bound.Ok, "board.comment.bind accepts an explicit null target session");
        Check(ReadWrapped<ProjectBoardComment>(bound, "comment").Origin is { Host: "other-host", SessionId: null },
            "binding a null target session keeps the host alone");

        TaskResponse removed = await CallAsync(store, "board.attachment.remove", new
        {
            project, attachmentId, actor = "local-user", host = "toolbox", session = nullSession
        });
        Check(removed.Ok, "board.attachment.remove accepts an explicit null session");

        TaskResponse deleted = await CallAsync(store, "board.card.delete", new
        {
            project, id = card.Id, actor = "local-user", host = "toolbox", session = nullSession
        });
        Check(deleted.Ok, "board.card.delete accepts an explicit null session");
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
