namespace ToolBox.Core.Boards;

public sealed record ProjectBoardRegistration(Guid BoardId, string Name, string Path);

public sealed record ProjectBoard(
    int SchemaVersion,
    Guid BoardId,
    string Name,
    long Revision,
    IReadOnlyList<ProjectBoardColumn> Columns,
    IReadOnlyList<ProjectBoardCard> Cards);

public sealed record ProjectBoardColumn(Guid Id, string Title, int Order);

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
    DateTime? DeletedAt);
