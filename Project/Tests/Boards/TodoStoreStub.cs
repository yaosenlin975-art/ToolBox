using ToolBox.Core.Todo;

namespace ToolBox.Core.Todo;

internal sealed class TodoStore
{
    public static TodoStore Instance { get; } = new();
    public Task<TaskResponse> ExecuteTaskAsync(TaskRequest request) =>
        Task.FromResult(TaskResponse.Failure(request, "INTERNAL_ERROR", "TodoStore is not available in board checks."));
}
