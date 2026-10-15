using System.Text.Json;

namespace ToolBox
{
    public static class App
    {
        public static event Action? LanguageChanged;
        public static void RaiseLanguageChanged() => LanguageChanged?.Invoke();
    }
}

namespace ToolBox.Core.Boards
{
    public sealed record ProjectBoardRegistration(Guid BoardId, string Name, string Path);
    public sealed record ProjectBoard(int SchemaVersion, Guid BoardId, string Name, long Revision, IReadOnlyList<ProjectBoardColumn> Columns, IReadOnlyList<ProjectBoardCard> Cards);
    public sealed record ProjectBoardColumn(Guid Id, string Title, int Order);
    public sealed record ProjectBoardCard(Guid Id, string Title, string Description, Guid ColumnId, int Order, int Priority, IReadOnlyList<string> Tags, DateTime? DueDate, DateTime CreatedAt, DateTime UpdatedAt, DateTime? DeletedAt);

    public sealed class ProjectBoardStore
    {
        public static ProjectBoardStore Instance { get; } = new();
        public event Action? Changed;
        public Func<ToolBox.Core.Todo.TaskRequest, ToolBox.Core.Todo.TaskResponse>? RequestHandler { get; set; }
        public List<ToolBox.Core.Todo.TaskRequest> Requests { get; } = [];
        public IReadOnlyList<ProjectBoardRegistration> Registrations { get; set; } = [];
        public ProjectBoard? CurrentBoard { get; set; }
        public void RaiseChanged() => Changed?.Invoke();
        public Task<ToolBox.Core.Todo.TaskResponse> ExecuteAsync(ToolBox.Core.Todo.TaskRequest request)
        {
            Requests.Add(request);
            return Task.FromResult(RequestHandler?.Invoke(request) ?? new ToolBox.Core.Todo.TaskResponse());
        }
    }
}

namespace ToolBox.Core.Todo
{
    public sealed class TaskRequest
    {
        public string RequestId { get; set; } = "";
        public string Operation { get; set; } = "";
        public JsonElement? Payload { get; set; }
    }

    public sealed class TaskResponse
    {
        public bool Ok { get; set; } = true;
        public object? Data { get; set; }
        public TaskError? Error { get; set; }
    }

    public sealed record TaskError(string Code, string Message, string? Field = null);

    public static class TaskProtocol
    {
        public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
    }
}
