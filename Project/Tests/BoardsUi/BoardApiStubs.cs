using System.Text.Json;
using System.IO;
using ToolBox.Core.Boards;
using ToolBox.Core.Todo;

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
    public sealed class ProjectBoardStore
    {
        public static ProjectBoardStore Instance { get; } = new();
        public event Action? Changed;
        public Func<TaskRequest, TaskResponse>? RequestHandler { get; set; }
        public List<TaskRequest> Requests { get; } = [];
        public IReadOnlyList<ProjectBoardRegistration> Registrations { get; set; } = [];
        public ProjectBoard? CurrentBoard { get; set; }
        public void RaiseChanged() => Changed?.Invoke();
        public Task<TaskResponse> ExecuteAsync(TaskRequest request)
        {
            Requests.Add(request);
            return Task.FromResult(RequestHandler?.Invoke(request) ?? new TaskResponse());
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
        public IReadOnlyList<string>? Warnings { get; set; }
    }

    public sealed record TaskError(string Code, string Message, string? Field = null);

    public static class TaskProtocol
    {
        public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
        };
    }
}

namespace ToolBox.Core.BoardAgents
{
    public sealed record BoardAgentDelivery(string CommentId, string CardId, string ProjectRoot, string Host, string? Session, string Status, string ConfirmationHash, string? Error, DateTimeOffset UpdatedAt);
    public sealed record BoardAgentHostInfo(string Host, string DisplayName, string Mode, string? ResolvedPath, string? EntryPath, string DetectedVia, bool Available, bool RequiresPathConfirmation, string? CandidatePath, bool SupportsSession, bool InboxPull, string? UriScheme);
    public sealed record BoardAgentLaunchTarget(string Kind, string? Path, string? Uri, string ProjectRoot);

    public sealed class BoardCommandService
    {
        public static BoardCommandService Instance { get; } = new();
        public Task<TaskResponse> ExecuteAsync(TaskRequest request, CancellationToken cancellationToken = default) => ProjectBoardStore.Instance.ExecuteAsync(request);
    }

    public sealed class BoardAgentService
    {
        public static BoardAgentService Instance { get; } = new();
        public event Action? Changed;
        public string GetConfigPath() => Path.Combine(Path.GetTempPath(), "board-agents.json");
        public Task<IReadOnlyList<BoardAgentHostInfo>> DiscoverAsync(bool refresh = false, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<BoardAgentHostInfo>>([]);
        public BoardAgentLaunchTarget? ResolveLaunch(string host, string projectRoot) => null;
        public void RaiseChanged() => Changed?.Invoke();
    }
}
