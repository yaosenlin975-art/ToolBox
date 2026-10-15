using ToolBox.Core.Todo;

namespace ToolBox.Core.Boards;

public sealed class ProjectBoardStore
{
    public static ProjectBoardStore Instance { get; } = new();

    public Task<TaskResponse> ExecuteAsync(TaskRequest request) =>
        throw new InvalidOperationException("BoardAgents tests must inject a fake store executor.");
}
