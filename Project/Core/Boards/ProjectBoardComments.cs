using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ToolBox.Core.Todo;

namespace ToolBox.Core.Boards;

public sealed partial class ProjectBoardStore
{
    private const int CommentBodyMaxBytes = 16 * 1024;
    private const int CommentSidecarThresholdBytes = 2 * 1024 * 1024;
    private const int CommentSidecarSchemaVersion = 1;
    private const string CommentSidecarFileName = "comments.json";
    private const string CommentMigrationJournalFileName = "comments.migration.json";
    private const string CommentMigrationBackupFileName = "board.comments.old";
    private const string CommentMigrationBoardTempFileName = "board.comments.next";
    private const string CommentMigrationSidecarTempFileName = "comments.next";

    private static readonly JsonSerializerOptions CommentCursorJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private TaskResponse CommentAdd(JsonElement payload)
    {
        var (registration, snapshot) = ReadProjectBoard(payload);
        CheckCommentRevisions(payload, snapshot.Board);
        Guid cardId = RequiredGuid(payload, "id");
        ProjectBoardCard card = FindCard(snapshot.Board, cardId);
        EnsureCardActive(card, "id");
        BoardIdentity identity = ReadRequiredIdentity(payload);
        Guid? parentId = payload.TryGetProperty("parentId", out _) ? OptionalNullableGuid(payload, "parentId") : null;
        ProjectBoardComment? parent = null;
        ProjectBoardCommentRouting? routing = null;
        if (parentId is Guid parentCommentId)
        {
            parent = FindComment(snapshot.Board, parentCommentId, out Guid parentCardId);
            if (parentCardId != cardId) throw new TaskCommandException("NOT_FOUND", "Parent comment was not found on this card.", "parentId");
            if (parent.DeletedAt != null) throw new TaskCommandException("CONFLICT", "A deleted comment cannot receive a reply.", "parentId");
            routing = FindNearestCommentOrigin(card, parent, snapshot.Board);
        }

        DateTime now = DateTime.UtcNow;
        string body = RequiredBody(payload);
        var author = CreateCommentAuthor(identity);
        var comment = new ProjectBoardComment(Guid.NewGuid(), parentId, body, author,
            author.Kind == "agent" ? new ProjectBoardCommentOrigin(identity.HostId, identity.SessionId) : null,
            routing, now, now, null);
        ProjectBoard updated = SetCardComments(snapshot.Board, cardId, card.Comments!.Append(comment).ToArray()) with
        {
            CommentRevision = NextCommentRevision(snapshot.Board)
        };
        TaskResponse result = CommentMutationResponse(comment, cardId, updated);
        SaveComments(registration.Path, snapshot, updated, result);
        NotifyChanged();
        return result;
    }

    private TaskResponse CommentGet(JsonElement payload)
    {
        var (registration, snapshot) = ReadProjectBoard(payload);
        CheckCommentRevisions(payload, snapshot.Board);
        Guid commentId = RequiredGuid(payload, "commentId");
        ProjectBoardComment comment = FindComment(snapshot.Board, commentId, out Guid cardId);
        return TaskResponse.Success(_currentRequest!, new ProjectBoardCommentContext(
            registration, cardId, comment, snapshot.Board.Revision, snapshot.Board.CommentRevision));
    }

    private TaskResponse CommentList(JsonElement payload)
    {
        var (_, snapshot) = ReadProjectBoard(payload);
        CheckCommentRevision(payload, snapshot.Board);
        Guid cardId = RequiredGuid(payload, "id");
        ProjectBoardCard card = FindCard(snapshot.Board, cardId);
        bool includeDeleted = OptionalBoolean(payload, "includeDeleted") ?? false;
        int requestedLimit = OptionalInt(payload, "limit") ?? 100;
        CommentCursor? cursor = ReadCommentCursor(payload, snapshot.Board, cardId, includeDeleted, null, snapshot.Board.CommentRevision);
        ProjectBoardComment[] candidates = card.Comments!
            .Where(comment => includeDeleted || comment.DeletedAt == null)
            .OrderBy(comment => comment.CreatedAt).ThenBy(comment => comment.Id)
            .Where(comment => cursor == null || IsAfter(comment, cursor))
            .ToArray();
        int limit = requestedLimit;
        while (limit > 0)
        {
            ProjectBoardComment[] page = candidates.Take(limit).ToArray();
            string? nextCursor = candidates.Length > page.Length && page.Length > 0
                ? WriteCommentCursor(snapshot.Board.BoardId, cardId, includeDeleted, null, snapshot.Board.CommentRevision, page[^1])
                : null;
            TaskResponse response = TaskResponse.Success(_currentRequest!, new
            {
                comments = page,
                cursor = nextCursor,
                revision = snapshot.Board.Revision,
                boardRevision = snapshot.Board.Revision,
                commentRevision = snapshot.Board.CommentRevision
            });
            if (SerializedResponseLength(response) <= TaskProtocol.MaxBytes) return response;
            limit--;
        }
        throw new TaskCommandException("RESULT_TOO_LARGE", "A single comment exceeds the 4 MiB response limit.");
    }

    private TaskResponse CommentUpdate(JsonElement payload)
    {
        var (registration, snapshot) = ReadProjectBoard(payload);
        CheckCommentRevisions(payload, snapshot.Board);
        Guid commentId = RequiredGuid(payload, "commentId");
        BoardIdentity identity = ReadRequiredIdentity(payload);
        ProjectBoardComment comment = FindComment(snapshot.Board, commentId, out Guid cardId);
        EnsureCommentAuthor(comment, identity);
        if (comment.DeletedAt != null) throw new TaskCommandException("CONFLICT", "A deleted comment cannot be updated.", "commentId");
        string body = RequiredBody(payload);
        if (string.Equals(body, comment.Body, StringComparison.Ordinal))
            return CommentMutationResponse(comment, cardId, snapshot.Board);

        ProjectBoardComment updatedComment = comment with
        {
            Body = body,
            UpdatedAt = DateTime.UtcNow,
            AcknowledgedAt = null,
            AcknowledgedBy = null,
            InboxClaim = null
        };
        ProjectBoard updated = ReplaceComment(snapshot.Board, cardId, updatedComment) with
        {
            CommentRevision = NextCommentRevision(snapshot.Board)
        };
        TaskResponse result = CommentMutationResponse(updatedComment, cardId, updated);
        SaveComments(registration.Path, snapshot, updated, result);
        NotifyChanged();
        return result;
    }

    private TaskResponse CommentDelete(JsonElement payload)
    {
        var (registration, snapshot) = ReadProjectBoard(payload);
        CheckCommentRevisions(payload, snapshot.Board);
        Guid commentId = RequiredGuid(payload, "commentId");
        BoardIdentity identity = ReadRequiredIdentity(payload);
        ProjectBoardComment comment = FindComment(snapshot.Board, commentId, out Guid cardId);
        if (identity.ActorId != "local-user") EnsureCommentAuthor(comment, identity);
        if (comment.DeletedAt != null) return CommentMutationResponse(comment, cardId, snapshot.Board);

        ProjectBoardComment updatedComment = comment with { DeletedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, InboxClaim = null };
        ProjectBoard updated = ReplaceComment(snapshot.Board, cardId, updatedComment) with
        {
            CommentRevision = NextCommentRevision(snapshot.Board)
        };
        TaskResponse result = CommentMutationResponse(updatedComment, cardId, updated);
        SaveComments(registration.Path, snapshot, updated, result);
        NotifyChanged();
        return result;
    }

    private TaskResponse CommentInbox(JsonElement payload)
    {
        var (registration, snapshot) = ReadProjectBoard(payload);
        CheckCommentRevision(payload, snapshot.Board);
        string targetHost = RequiredText(payload, "host");
        string? targetSession = OptionalSession(payload);
        string actorId = RequiredText(payload, "actor");
        if (!Guid.TryParse(actorId, out Guid actorGuid) || actorGuid == Guid.Empty)
            throw Invalid("actor must be a non-empty UUID.", "actor");
        actorId = actorGuid.ToString("D");
        int ttlMinutes = OptionalInt(payload, "ttlMinutes") ?? 30;
        if (ttlMinutes is < 5 or > 480) throw Invalid("ttlMinutes must be between 5 and 480.", "ttlMinutes");
        int requestedLimit = OptionalInt(payload, "limit") ?? 100;
        CommentCursor? cursor = ReadCommentCursor(payload, snapshot.Board, null, null, (targetHost, targetSession),
            snapshot.Board.CommentRevision);
        DateTime now = DateTime.UtcNow;
        int limit = requestedLimit;
        while (limit > 0)
        {
            InboxPage prepared = PrepareInboxPage(snapshot.Board, targetHost, targetSession, actorId, ttlMinutes, now, cursor, limit);
            ProjectBoard finalBoard = prepared.Changed
                ? prepared.Board with { CommentRevision = NextCommentRevision(snapshot.Board) }
                : snapshot.Board;
            string? nextCursor = prepared.HasMore && prepared.LastScanned is { } last
                ? WriteCommentCursor(snapshot.Board.BoardId, null, null, (targetHost, targetSession), finalBoard.CommentRevision, last)
                : null;
            TaskResponse response = TaskResponse.Success(_currentRequest!, new
            {
                comments = prepared.Comments,
                cursor = nextCursor,
                revision = finalBoard.Revision,
                boardRevision = finalBoard.Revision,
                commentRevision = finalBoard.CommentRevision
            });
            if (SerializedResponseLength(response) <= TaskProtocol.MaxBytes)
            {
                if (prepared.Changed)
                {
                    SaveComments(registration.Path, snapshot, finalBoard, response);
                    NotifyChanged();
                }
                return response;
            }
            limit--;
        }
        throw new TaskCommandException("RESULT_TOO_LARGE", "A single comment exceeds the 4 MiB response limit.");
    }

    private TaskResponse CommentAck(JsonElement payload)
    {
        var (registration, snapshot) = ReadProjectBoard(payload);
        CheckCommentRevisions(payload, snapshot.Board);
        Guid commentId = RequiredGuid(payload, "commentId");
        Guid claimId = RequiredGuid(payload, "claimId");
        BoardIdentity identity = ReadRequiredIdentity(payload);
        if (!Guid.TryParse(identity.ActorId, out Guid actorGuid) || actorGuid == Guid.Empty)
            throw Invalid("actor must be a non-empty UUID.", "actor");
        string actorId = actorGuid.ToString("D");
        ProjectBoardComment comment = FindComment(snapshot.Board, commentId, out Guid cardId);
        DateTime now = DateTime.UtcNow;
        if (comment.DeletedAt != null || comment.AcknowledgedAt != null || comment.Routing is not { } routing ||
            routing.Host != identity.HostId ||
            comment.InboxClaim is not { } claim || claim.ExpiresAt <= now || claim.ActorId != actorId ||
            claim.HostId != identity.HostId || claim.SessionId != identity.SessionId ||
            (claim.SessionId != null && routing.SessionId != claim.SessionId) || claim.ClaimId != claimId)
            throw new TaskCommandException("CONFLICT", "The reply reservation is missing, expired, or belongs to another inbox identity.", "claimId");

        ProjectBoardComment updatedComment = comment with { AcknowledgedAt = now, AcknowledgedBy = actorId, InboxClaim = null, UpdatedAt = now };
        ProjectBoard updated = ReplaceComment(snapshot.Board, cardId, updatedComment) with
        {
            CommentRevision = NextCommentRevision(snapshot.Board)
        };
        TaskResponse result = CommentMutationResponse(updatedComment, cardId, updated);
        SaveComments(registration.Path, snapshot, updated, result);
        NotifyChanged();
        return result;
    }

    private TaskResponse CommentBind(JsonElement payload)
    {
        var (registration, snapshot) = ReadProjectBoard(payload);
        CheckCommentRevisions(payload, snapshot.Board);
        Guid commentId = RequiredGuid(payload, "commentId");
        _ = ReadRequiredIdentity(payload);
        ProjectBoardComment comment = FindComment(snapshot.Board, commentId, out Guid cardId);
        bool clear = OptionalBoolean(payload, "clear") ?? false;
        ProjectBoardCommentOrigin? origin = clear ? null : new ProjectBoardCommentOrigin(
            RequiredText(payload, "targetHost"), OptionalNullableText(payload, "targetSession"));
        if (Equals(origin, comment.Origin)) return CommentMutationResponse(comment, cardId, snapshot.Board);

        ProjectBoardComment updatedComment = comment with { Origin = origin, UpdatedAt = DateTime.UtcNow };
        ProjectBoard updated = ReplaceComment(snapshot.Board, cardId, updatedComment) with
        {
            CommentRevision = NextCommentRevision(snapshot.Board)
        };
        TaskResponse result = CommentMutationResponse(updatedComment, cardId, updated);
        SaveComments(registration.Path, snapshot, updated, result);
        NotifyChanged();
        return result;
    }

    private TaskResponse CommentMutationResponse(ProjectBoardComment comment, Guid cardId, ProjectBoard board) =>
        TaskResponse.Success(_currentRequest!, new
        {
            comment,
            cardId,
            revision = board.Revision,
            boardRevision = board.Revision,
            commentRevision = board.CommentRevision
        });

    private static ProjectBoard ProjectBoardResponse(ProjectBoard board) => board with
    {
        Cards = board.Cards.Select(card => ProjectCardResponse(board, card)).ToArray()
    };

    private static ProjectBoardCard ProjectCardResponse(ProjectBoard board, ProjectBoardCard card)
    {
        ProjectBoardCard[] children = board.Cards.Where(child => child.ParentId == card.Id && child.DeletedAt == null).ToArray();
        return card with
        {
            Comments = [],
            CommentCount = card.Comments?.Count(comment => comment.DeletedAt == null) ?? 0,
            AttachmentCount = card.Attachments?.Count ?? 0,
            ChildCount = children.Length,
            ChildClosedCount = children.Count(child => child.Status == "closed")
        };
    }

    private static ProjectBoard SetCardComments(ProjectBoard board, Guid cardId, IReadOnlyList<ProjectBoardComment> comments) => board with
    {
        Cards = board.Cards.Select(card => card.Id == cardId ? card with { Comments = comments } : card).ToArray()
    };

    private static ProjectBoard ReplaceComment(ProjectBoard board, Guid cardId, ProjectBoardComment comment)
    {
        ProjectBoardCard card = FindCard(board, cardId);
        return SetCardComments(board, cardId, card.Comments!.Select(existing => existing.Id == comment.Id ? comment : existing).ToArray());
    }

    private static ProjectBoardComment FindComment(ProjectBoard board, Guid commentId, out Guid cardId)
    {
        foreach (ProjectBoardCard card in board.Cards)
        {
            ProjectBoardComment? comment = card.Comments?.FirstOrDefault(candidate => candidate.Id == commentId);
            if (comment != null) { cardId = card.Id; return comment; }
        }
        throw new TaskCommandException("NOT_FOUND", "Board comment not found.", "commentId");
    }

    private static ProjectBoardCommentRouting? FindNearestCommentOrigin(ProjectBoardCard card, ProjectBoardComment parent, ProjectBoard board)
    {
        var byId = card.Comments!.ToDictionary(comment => comment.Id);
        ProjectBoardComment? current = parent;
        while (current != null)
        {
            if (current.Origin is { } origin) return new ProjectBoardCommentRouting(origin.Host, origin.SessionId);
            current = current.ParentId is Guid parentId && byId.TryGetValue(parentId, out ProjectBoardComment? ancestor) ? ancestor : null;
        }
        return null;
    }

    private static ProjectBoardAssignee CreateCommentAuthor(BoardIdentity identity) =>
        identity.ActorId == "local-user"
            ? new ProjectBoardAssignee("user", identity.ActorId, "local-user", identity.HostId, identity.SessionId)
            : new ProjectBoardAssignee("agent", identity.ActorId, identity.ActorId, identity.HostId, identity.SessionId);

    private static void EnsureCommentAuthor(ProjectBoardComment comment, BoardIdentity identity)
    {
        if (comment.Author.Id != identity.ActorId || comment.Author.HostId != identity.HostId || comment.Author.SessionId != identity.SessionId)
            throw new TaskCommandException("ACCESS_DENIED", "Only the comment author can change this comment.", "commentId");
    }

    private static string RequiredBody(JsonElement payload)
    {
        string body = RequiredString(payload, "body");
        if (string.IsNullOrWhiteSpace(body)) throw Invalid("body must not be empty.", "body");
        if (Encoding.UTF8.GetByteCount(body) > CommentBodyMaxBytes) throw Invalid("body must be at most 16 KiB in UTF-8.", "body");
        return body;
    }

    private static long NextCommentRevision(ProjectBoard board)
    {
        if (board.CommentRevision == long.MaxValue)
            throw new TaskCommandException("CONFLICT", "The comment revision cannot be increased further.", "commentRevision");
        return checked(board.CommentRevision + 1);
    }

    private static void CheckCommentRevisions(JsonElement payload, ProjectBoard board)
    {
        CheckRevision(payload, board);
        CheckCommentRevision(payload, board);
    }

    private static void CheckCommentRevision(JsonElement payload, ProjectBoard board)
    {
        if (payload.TryGetProperty("commentRevision", out _) && RequiredLong(payload, "commentRevision") != board.CommentRevision)
            throw new TaskCommandException("CONFLICT", "Project comment revision changed; reload before retrying.", "commentRevision");
    }

    private static int SerializedResponseLength(TaskResponse response) =>
        JsonSerializer.SerializeToUtf8Bytes(response, TaskProtocol.Options).Length;
}

public sealed partial class ProjectBoardStore
{
    private static CommentCursor? ReadCommentCursor(
        JsonElement payload,
        ProjectBoard board,
        Guid? cardId,
        bool? includeDeleted,
        (string Host, string? Session)? inboxTarget,
        long? expectedCommentRevision)
    {
        if (!payload.TryGetProperty("cursor", out JsonElement cursorElement)) return null;
        string encoded = cursorElement.GetString()!;
        try
        {
            byte[] bytes = FromBase64Url(encoded);
            CommentCursor cursor = JsonSerializer.Deserialize<CommentCursor>(bytes, CommentCursorJsonOptions)
                ?? throw new JsonException("Cursor is empty.");
            if (cursor.BoardId != board.BoardId || cursor.CardId != cardId || cursor.IncludeDeleted != includeDeleted ||
                cursor.TargetHost != inboxTarget?.Host || cursor.TargetSession != inboxTarget?.Session ||
                cursor.CommentRevision != board.CommentRevision ||
                (expectedCommentRevision.HasValue && cursor.CommentRevision != expectedCommentRevision.Value) ||
                cursor.LastId == Guid.Empty || cursor.LastCreatedAt.Kind != DateTimeKind.Utc)
                throw new TaskCommandException("CONFLICT", "The comment cursor is stale or belongs to another query.", "cursor");
            return cursor;
        }
        catch (TaskCommandException) { throw; }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            throw Invalid("cursor is invalid.", "cursor");
        }
    }

    private static bool IsAfter(ProjectBoardComment comment, CommentCursor cursor) =>
        comment.CreatedAt > cursor.LastCreatedAt || (comment.CreatedAt == cursor.LastCreatedAt && comment.Id.CompareTo(cursor.LastId) > 0);

    private static string WriteCommentCursor(
        Guid boardId,
        Guid? cardId,
        bool? includeDeleted,
        (string Host, string? Session)? inboxTarget,
        long commentRevision,
        ProjectBoardComment last) => ToBase64Url(JsonSerializer.SerializeToUtf8Bytes(new CommentCursor(
            boardId, cardId, includeDeleted, inboxTarget?.Host, inboxTarget?.Session, commentRevision, last.CreatedAt, last.Id), CommentCursorJsonOptions));

    private static string ToBase64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }

    private static InboxPage PrepareInboxPage(
        ProjectBoard board,
        string targetHost,
        string? targetSession,
        string actorId,
        int ttlMinutes,
        DateTime now,
        CommentCursor? cursor,
        int limit)
    {
        ProjectBoardComment[] candidates = board.Cards.SelectMany(card => card.Comments!)
            .Where(comment => comment.ParentId != null && comment.DeletedAt == null && comment.AcknowledgedAt == null &&
                comment.Routing is { } routing && routing.Host == targetHost &&
                (targetSession == null || routing.SessionId == targetSession))
            .OrderBy(comment => comment.CreatedAt).ThenBy(comment => comment.Id)
            .Where(comment => cursor == null || IsAfter(comment, cursor))
            .ToArray();
        var selected = new List<ProjectBoardComment>();
        ProjectBoardComment? lastScanned = null;
        bool hasMore = false;
        bool changed = false;
        var replacements = new Dictionary<Guid, ProjectBoardComment>();
        foreach (ProjectBoardComment candidate in candidates)
        {
            if (candidate.InboxClaim is { } activeClaim && activeClaim.ExpiresAt > now &&
                (activeClaim.ActorId != actorId || activeClaim.HostId != targetHost || activeClaim.SessionId != targetSession))
            {
                lastScanned = candidate;
                continue;
            }
            if (selected.Count == limit)
            {
                hasMore = true;
                break;
            }
            ProjectBoardComment selectedComment = candidate;
            if (candidate.InboxClaim is not { } ownClaim || ownClaim.ExpiresAt <= now)
            {
                selectedComment = candidate with
                {
                    InboxClaim = new ProjectBoardInboxClaim(actorId, targetHost, targetSession, Guid.NewGuid(), now.AddMinutes(ttlMinutes))
                };
                replacements.Add(candidate.Id, selectedComment);
                changed = true;
            }
            selected.Add(selectedComment);
            lastScanned = candidate;
        }

        if (replacements.Count == 0) return new InboxPage(board, selected, lastScanned, hasMore, false);
        ProjectBoard updatedBoard = board with
        {
            Cards = board.Cards.Select(card => card with
            {
                Comments = card.Comments!.Select(comment => replacements.TryGetValue(comment.Id, out ProjectBoardComment? updated) ? updated : comment).ToArray()
            }).ToArray()
        };
        return new InboxPage(updatedBoard, selected, lastScanned, hasMore, changed);
    }

    private void SaveComments(string root, BoardSnapshot before, ProjectBoard updated, TaskResponse response)
    {
        EnsureResponseFits(response);
        ValidateBoard(updated);
        string boardPath = GetBoardPath(root, allowMissingDirectory: false);
        RejectReparsePoint(boardPath);
        if (!File.Exists(boardPath) || !string.Equals(Hash(File.ReadAllBytes(boardPath)), before.Hash, StringComparison.Ordinal))
            throw new TaskCommandException("CONFLICT", "Project board changed outside this command; reload before retrying.", "revision");

        if (before.Board.CommentsExternal)
        {
            if (before.CommentsHash == null) throw Corrupt("External comments have no tracked sidecar hash.");
            string sidecarPath = GetCommentsPath(root);
            RejectReparsePoint(sidecarPath);
            if (!File.Exists(sidecarPath) || !string.Equals(Hash(File.ReadAllBytes(sidecarPath)), before.CommentsHash, StringComparison.Ordinal))
                throw new TaskCommandException("CONFLICT", "Project comments changed outside this command; reload before retrying.", "commentRevision");
            byte[] sidecar = SerializeCommentSidecar(updated);
            WriteAtomic(sidecarPath, sidecar, before.CommentsHash, "commentRevision");
            knownCommentHashes[updated.BoardId] = Hash(sidecar);
            return;
        }

        if (File.Exists(GetCommentsPath(root))) throw Corrupt("A comment sidecar exists while commentsExternal is false.");
        if (ShouldExternalizeComments(updated))
        {
            ExternalizeComments(root, before, updated);
            return;
        }
        byte[] bytes = SerializeBoardStorage(updated);
        WriteAtomic(boardPath, bytes, before.Hash, "revision");
        knownBoardHashes[updated.BoardId] = Hash(bytes);
    }

    private static bool ShouldExternalizeComments(ProjectBoard board) =>
        board.Cards.Any(card => card.Comments!.Count > 500) ||
        JsonSerializer.SerializeToUtf8Bytes(board, JsonOptions).Length > CommentSidecarThresholdBytes;

    private static byte[] SerializeBoardStorage(ProjectBoard board)
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
        return JsonSerializer.SerializeToUtf8Bytes(storage, JsonOptions);
    }

    private void ExternalizeComments(string root, BoardSnapshot before, ProjectBoard inlineBoard)
    {
        if (before.Board.CommentsExternal) throw Corrupt("External comments cannot be migrated a second time.");
        EnsureBoardDirectory(root, create: false);
        string boardPath = GetBoardPath(root, allowMissingDirectory: false);
        string commentsPath = GetCommentsPath(root);
        if (!File.Exists(boardPath) || !string.Equals(Hash(File.ReadAllBytes(boardPath)), before.Hash, StringComparison.Ordinal))
            throw new TaskCommandException("CONFLICT", "Project board changed outside this command; reload before retrying.", "revision");
        if (File.Exists(commentsPath)) throw new TaskCommandException("CONFLICT", "A comment sidecar already exists.", "commentRevision");

        string directory = Path.GetDirectoryName(boardPath)!;
        string journalPath = Path.Combine(directory, CommentMigrationJournalFileName);
        string backupPath = Path.Combine(directory, CommentMigrationBackupFileName);
        string boardTempPath = Path.Combine(directory, CommentMigrationBoardTempFileName);
        string sidecarTempPath = Path.Combine(directory, CommentMigrationSidecarTempFileName);
        if (MigrationArtifactsExist(directory)) throw Corrupt("Unjournaled comment migration artifacts were found.");

        ProjectBoard externalBoard = inlineBoard with
        {
            CommentsExternal = true,
            Cards = inlineBoard.Cards.Select(card => card with { Comments = [] }).ToArray()
        };
        byte[] oldBoard = File.ReadAllBytes(boardPath);
        byte[] newBoard = SerializeBoardStorage(externalBoard);
        byte[] newComments = SerializeCommentSidecar(inlineBoard);
        string oldHash = Hash(oldBoard);
        string newBoardHash = Hash(newBoard);
        string newCommentsHash = Hash(newComments);
        var journal = new CommentMigrationJournal(CommentSidecarSchemaVersion, inlineBoard.BoardId, oldHash, newBoardHash, newCommentsHash);
        bool journalCreated = false;
        try
        {
            WriteDurableNewFile(backupPath, oldBoard);
            WriteDurableNewFile(boardTempPath, newBoard);
            WriteDurableNewFile(sidecarTempPath, newComments);
            WriteNewAtomic(journalPath, JsonSerializer.SerializeToUtf8Bytes(journal, JsonOptions));
            journalCreated = true;
            File.Move(sidecarTempPath, commentsPath);
            ReplaceAtomicWithRetry(boardTempPath, boardPath, oldHash, newBoardHash, "revision");
            RecoverCommentsMigration(root);
            knownBoardHashes[inlineBoard.BoardId] = newBoardHash;
            knownCommentHashes[inlineBoard.BoardId] = newCommentsHash;
        }
        catch
        {
            if (!journalCreated && !File.Exists(journalPath))
            {
                RemoveIfHashMatches(backupPath, oldHash);
                RemoveIfHashMatches(boardTempPath, newBoardHash);
                RemoveIfHashMatches(sidecarTempPath, newCommentsHash);
            }
            throw;
        }
    }

    private void RecoverCommentsMigration(string root)
    {
        string directory = Path.Combine(root, ".toolbox");
        if (!Directory.Exists(directory)) return;
        RejectReparsePoint(directory);
        string journalPath = Path.Combine(directory, CommentMigrationJournalFileName);
        if (!File.Exists(journalPath))
        {
            if (MigrationArtifactsExist(directory)) throw Corrupt("Unjournaled comment migration artifacts were found.");
            return;
        }
        RejectReparsePoint(journalPath);
        CommentMigrationJournal journal;
        try
        {
            byte[] journalBytes = File.ReadAllBytes(journalPath);
            using JsonDocument document = JsonDocument.Parse(journalBytes);
            RequireProperties(document.RootElement, "schemaVersion", "boardId", "oldBoardHash", "newBoardHash", "newCommentsHash");
            journal = JsonSerializer.Deserialize<CommentMigrationJournal>(journalBytes, JsonOptions)
                ?? throw Corrupt("Comment migration journal is empty.");
            if (journal.SchemaVersion != CommentSidecarSchemaVersion || journal.BoardId == Guid.Empty ||
                !IsSha256(journal.OldBoardHash) || !IsSha256(journal.NewBoardHash) || !IsSha256(journal.NewCommentsHash))
                throw Corrupt("Comment migration journal is invalid.");
        }
        catch (TaskCommandException) { throw; }
        catch (JsonException) { throw Corrupt("Comment migration journal is not valid JSON or contains unknown fields."); }

        string boardPath = Path.Combine(directory, "board.json");
        string commentsPath = Path.Combine(directory, CommentSidecarFileName);
        string backupPath = Path.Combine(directory, CommentMigrationBackupFileName);
        string boardTempPath = Path.Combine(directory, CommentMigrationBoardTempFileName);
        string sidecarTempPath = Path.Combine(directory, CommentMigrationSidecarTempFileName);
        foreach (string path in new[] { boardPath, commentsPath, backupPath, boardTempPath, sidecarTempPath }) RejectReparsePoint(path);
        ValidateMigrationArtifact(backupPath, journal.OldBoardHash);
        ValidateMigrationArtifact(boardTempPath, journal.NewBoardHash);
        ValidateMigrationArtifact(sidecarTempPath, journal.NewCommentsHash);

        string? currentBoardHash = File.Exists(boardPath) ? Hash(File.ReadAllBytes(boardPath)) : null;
        string? currentCommentsHash = File.Exists(commentsPath) ? Hash(File.ReadAllBytes(commentsPath)) : null;
        if (currentBoardHash == journal.NewBoardHash && currentCommentsHash == journal.NewCommentsHash)
        {
            CleanupMigrationArtifacts(journalPath, backupPath, boardTempPath, sidecarTempPath, journal);
            knownBoardHashes[journal.BoardId] = journal.NewBoardHash;
            knownCommentHashes[journal.BoardId] = journal.NewCommentsHash;
            return;
        }

        if (currentBoardHash is not (null) && currentBoardHash != journal.OldBoardHash && currentBoardHash != journal.NewBoardHash)
            throw Corrupt("Comment migration encountered an unknown board hash; files were preserved.");
        if (currentCommentsHash != null && currentCommentsHash != journal.NewCommentsHash)
            throw Corrupt("Comment migration encountered an unknown sidecar hash; files were preserved.");
        if (currentBoardHash == null) throw Corrupt("Comment migration board file is missing; files were preserved.");
        if (currentBoardHash == journal.NewBoardHash)
        {
            if (!File.Exists(backupPath) || Hash(File.ReadAllBytes(backupPath)) != journal.OldBoardHash)
                throw Corrupt("Comment migration cannot restore the original board; files were preserved.");
            WriteAtomic(boardPath, File.ReadAllBytes(backupPath), journal.NewBoardHash, "revision");
        }
        if (currentCommentsHash == journal.NewCommentsHash) File.Delete(commentsPath);
        if (Hash(File.ReadAllBytes(boardPath)) != journal.OldBoardHash || File.Exists(commentsPath))
            throw Corrupt("Comment migration rollback did not restore the original files.");
        CleanupMigrationArtifacts(journalPath, backupPath, boardTempPath, sidecarTempPath, journal);
        knownBoardHashes[journal.BoardId] = journal.OldBoardHash;
        knownCommentHashes.Remove(journal.BoardId);
    }

    private static void CleanupMigrationArtifacts(
        string journalPath,
        string backupPath,
        string boardTempPath,
        string sidecarTempPath,
        CommentMigrationJournal journal)
    {
        RemoveIfHashMatches(backupPath, journal.OldBoardHash);
        RemoveIfHashMatches(boardTempPath, journal.NewBoardHash);
        RemoveIfHashMatches(sidecarTempPath, journal.NewCommentsHash);
        if (File.Exists(journalPath)) File.Delete(journalPath);
    }

    private static void ValidateMigrationArtifact(string path, string expectedHash)
    {
        if (File.Exists(path) && Hash(File.ReadAllBytes(path)) != expectedHash)
            throw Corrupt("Comment migration artifact hash is unknown; files were preserved.");
    }

    private static void RemoveIfHashMatches(string path, string expectedHash)
    {
        if (!File.Exists(path)) return;
        RejectReparsePoint(path);
        if (Hash(File.ReadAllBytes(path)) != expectedHash)
            throw Corrupt("A comment migration artifact has an unknown hash; files were preserved.");
        File.Delete(path);
    }

    private static bool MigrationArtifactsExist(string directory) =>
        new[] { CommentMigrationBackupFileName, CommentMigrationBoardTempFileName, CommentMigrationSidecarTempFileName }
            .Any(name => File.Exists(Path.Combine(directory, name)));

    private static void WriteDurableNewFile(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);

    private static string GetCommentsPath(string root) => Path.Combine(root, ".toolbox", CommentSidecarFileName);

    private static byte[] SerializeCommentSidecar(ProjectBoard board)
    {
        var data = new CommentSidecar(CommentSidecarSchemaVersion, board.BoardId, board.CommentRevision,
            board.Cards.SelectMany(card => card.Comments!.Select(comment => new ProjectBoardCommentEntry(card.Id, comment))).ToArray());
        return JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions);
    }

    private static CommentSidecarSnapshot ReadCommentSidecar(string root, Guid boardId)
    {
        string path = GetCommentsPath(root);
        RejectReparsePoint(path);
        if (!File.Exists(path)) throw Corrupt("External comments sidecar is missing.");
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            using JsonDocument document = JsonDocument.Parse(bytes);
            JsonElement element = document.RootElement;
            RequireProperties(element, "schemaVersion", "boardId", "revision", "comments");
            if (element.GetProperty("comments").ValueKind != JsonValueKind.Array) throw Corrupt("External comment collection is invalid.");
            foreach (JsonElement entry in element.GetProperty("comments").EnumerateArray())
            {
                RequireProperties(entry, "cardId", "comment");
                ValidateCommentJson(entry.GetProperty("comment"));
            }
            CommentSidecar data = JsonSerializer.Deserialize<CommentSidecar>(bytes, JsonOptions)
                ?? throw Corrupt("External comments sidecar is empty.");
            if (data.SchemaVersion != CommentSidecarSchemaVersion || data.BoardId != boardId || data.Revision < 0 || data.Comments == null)
                throw Corrupt("External comments sidecar header is invalid.");
            return new CommentSidecarSnapshot(data, data.Revision, Hash(bytes));
        }
        catch (TaskCommandException) { throw; }
        catch (JsonException) { throw Corrupt("External comments sidecar is not valid JSON or contains unknown fields."); }
    }

    private static ProjectBoard AttachComments(ProjectBoard board, CommentSidecar sidecar)
    {
        var byCard = new Dictionary<Guid, List<ProjectBoardComment>>();
        var cardIds = board.Cards.Select(card => card.Id).ToHashSet();
        foreach (ProjectBoardCommentEntry entry in sidecar.Comments)
        {
            if (!cardIds.Contains(entry.CardId)) throw Corrupt("External comments reference a missing card.");
            if (!byCard.TryGetValue(entry.CardId, out List<ProjectBoardComment>? comments)) byCard[entry.CardId] = comments = [];
            comments.Add(NormalizeComment(entry.Comment));
        }
        return board with
        {
            CommentRevision = sidecar.Revision,
            Cards = board.Cards.Select(card => card with { Comments = byCard.TryGetValue(card.Id, out List<ProjectBoardComment>? comments) ? comments : [] }).ToArray()
        };
    }

    private static ProjectBoardComment NormalizeComment(ProjectBoardComment comment) => comment with
    {
        CreatedAt = comment.CreatedAt.ToUniversalTime(),
        UpdatedAt = comment.UpdatedAt.ToUniversalTime(),
        DeletedAt = comment.DeletedAt?.ToUniversalTime(),
        AcknowledgedAt = comment.AcknowledgedAt?.ToUniversalTime(),
        InboxClaim = comment.InboxClaim is null ? null : comment.InboxClaim with { ExpiresAt = comment.InboxClaim.ExpiresAt.ToUniversalTime() }
    };

    private static void ValidateCommentsInBoard(ProjectBoard board)
    {
        var globalIds = new HashSet<Guid>();
        foreach (ProjectBoardCard card in board.Cards)
        {
            var byId = new Dictionary<Guid, ProjectBoardComment>();
            foreach (ProjectBoardComment comment in card.Comments ?? [])
            {
                if (comment == null || comment.Id == Guid.Empty || !globalIds.Add(comment.Id) || string.IsNullOrWhiteSpace(comment.Body) ||
                    Encoding.UTF8.GetByteCount(comment.Body) > CommentBodyMaxBytes || comment.Author == null ||
                    comment.Author.Kind is not ("user" or "agent") || string.IsNullOrWhiteSpace(comment.Author.Id) ||
                    string.IsNullOrWhiteSpace(comment.Author.Name) || string.IsNullOrWhiteSpace(comment.Author.HostId) ||
                    comment.CreatedAt.Kind != DateTimeKind.Utc || comment.UpdatedAt.Kind != DateTimeKind.Utc ||
                    (comment.DeletedAt.HasValue && comment.DeletedAt.Value.Kind != DateTimeKind.Utc) ||
                    (comment.AcknowledgedAt.HasValue && comment.AcknowledgedAt.Value.Kind != DateTimeKind.Utc) ||
                    (comment.AcknowledgedAt.HasValue != (comment.AcknowledgedBy != null)) ||
                    (comment.AcknowledgedBy != null && (!Guid.TryParse(comment.AcknowledgedBy, out Guid acknowledgedBy) || acknowledgedBy == Guid.Empty)) ||
                    (comment.Origin != null && string.IsNullOrWhiteSpace(comment.Origin.Host)) ||
                    (comment.Routing != null && string.IsNullOrWhiteSpace(comment.Routing.Host)) ||
                    (comment.InboxClaim != null && (string.IsNullOrWhiteSpace(comment.InboxClaim.ActorId) ||
                        !Guid.TryParse(comment.InboxClaim.ActorId, out Guid claimActor) || claimActor == Guid.Empty ||
                        string.IsNullOrWhiteSpace(comment.InboxClaim.HostId) || comment.InboxClaim.ClaimId == Guid.Empty ||
                        comment.InboxClaim.ExpiresAt.Kind != DateTimeKind.Utc || comment.DeletedAt != null || comment.AcknowledgedAt != null)))
                    throw Corrupt("Board comments are invalid.");
                byId.Add(comment.Id, comment);
            }
            foreach (ProjectBoardComment comment in byId.Values)
            {
                if (comment.ParentId is Guid parentId && !byId.ContainsKey(parentId))
                    throw Corrupt("A comment parent must exist on the same card.");
            }
            var complete = new HashSet<Guid>();
            foreach (ProjectBoardComment comment in byId.Values)
            {
                var path = new List<Guid>();
                var pathIds = new HashSet<Guid>();
                ProjectBoardComment? current = comment;
                while (current != null && !complete.Contains(current.Id))
                {
                    if (!pathIds.Add(current.Id)) throw Corrupt("Comment parents contain a cycle.");
                    path.Add(current.Id);
                    current = current.ParentId is Guid ancestorId ? byId[ancestorId] : null;
                }
                complete.UnionWith(path);
            }
        }
    }

    private sealed record CommentCursor(
        Guid BoardId,
        Guid? CardId,
        bool? IncludeDeleted,
        string? TargetHost,
        string? TargetSession,
        long CommentRevision,
        DateTime LastCreatedAt,
        Guid LastId);

    private sealed record InboxPage(
        ProjectBoard Board,
        IReadOnlyList<ProjectBoardComment> Comments,
        ProjectBoardComment? LastScanned,
        bool HasMore,
        bool Changed);

    private sealed record ProjectBoardCommentEntry(Guid CardId, ProjectBoardComment Comment);
    private sealed record CommentSidecar(int SchemaVersion, Guid BoardId, long Revision, IReadOnlyList<ProjectBoardCommentEntry> Comments);
    private sealed record CommentSidecarSnapshot(CommentSidecar Data, long Revision, string Hash);
    private sealed record CommentMigrationJournal(int SchemaVersion, Guid BoardId, string OldBoardHash, string NewBoardHash, string NewCommentsHash);
}
