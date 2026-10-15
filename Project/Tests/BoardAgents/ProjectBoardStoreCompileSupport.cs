using ToolBox.Core.Todo;

namespace ToolBox.Core.Todo;

// ProjectBoardStore's default adapter reaches TodoStore only for add-to-todo.
// BoardAgents tests inject a fake board executor and never call this compile-only path.
internal sealed class TodoStore
{
    public static TodoStore Instance { get; } = new();

    public Task<TaskResponse> ExecuteTaskAsync(TaskRequest request) =>
        Task.FromResult(TaskResponse.Failure(request, "INTERNAL_ERROR", "TodoStore is not used by BoardAgents tests."));
}
