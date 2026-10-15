namespace ToolBox.Core.Boards;

public sealed record ProjectBoardRegistration(Guid BoardId, string Name, string Path);

public sealed record ProjectBoard(
    int SchemaVersion,
    Guid BoardId,
    string Name,
    long Revision,
    IReadOnlyList<ProjectBoardColumn> Columns,
    IReadOnlyList<ProjectBoardCard> Cards,
    long CommentRevision = 0,
    bool CommentsExternal = false)
{
    public long BoardRevision => Revision;
}

public sealed record ProjectBoardColumn(Guid Id, string Title, int Order, string? Status = null);

public sealed record ProjectBoardCard(
    Guid Id,
    string Title,
    string Description,
    Guid ColumnId,
    int Order,
    int Priority,
    IReadOnlyList<string> Tags,
    DateTime? DueDate,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? DeletedAt,
    string Status = "notStarted",
    Guid? ParentId = null,
    ProjectBoardAssignee? Assignee = null,
    ProjectBoardClaim? Claim = null,
    IReadOnlyList<ProjectBoardAttachment>? Attachments = null,
    IReadOnlyList<ProjectBoardComment>? Comments = null);

public sealed record ProjectBoardAssignee(string Kind, string Id, string Name, string? HostId = null, string? SessionId = null);

public sealed record ProjectBoardClaim(
    string HostId,
    string ActorId,
    string? SessionId,
    DateTime ClaimedAt,
    DateTime HeartbeatAt,
    DateTime ExpiresAt);

public sealed record ProjectBoardAttachment(
    Guid Id,
    string FileName,
    string RelativePath,
    long Size,
    string Sha256,
    DateTime AddedAt,
    string AddedBy);

public sealed record ProjectBoardComment(
    Guid Id,
    Guid? ParentId,
    string Body,
    ProjectBoardAssignee Author,
    ProjectBoardCommentOrigin? Origin,
    ProjectBoardCommentRouting? Routing,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? DeletedAt,
    DateTime? AcknowledgedAt = null,
    string? AcknowledgedBy = null,
    ProjectBoardInboxClaim? InboxClaim = null);

public sealed record ProjectBoardCommentOrigin(string Host, string? SessionId);
public sealed record ProjectBoardCommentRouting(string Host, string? SessionId);

public sealed record ProjectBoardInboxClaim(
    string ActorId,
    string HostId,
    string? SessionId,
    Guid ClaimId,
    DateTime ExpiresAt);

public sealed record ProjectBoardCommentContext(
    ProjectBoardRegistration Registration,
    Guid CardId,
    ProjectBoardComment Comment,
    long BoardRevision,
    long CommentRevision);
