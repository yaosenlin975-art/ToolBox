// 项目看板 Agent 投递的公共模型：投递状态常量、评论上下文/宿主信息/投递记录/启动目标/进程规格等记录，
// 以及宿主发现平台与进程执行平台接口、服务配置选项。
using System.Diagnostics;

namespace ToolBox.Core.BoardAgents;

public static class BoardAgentDeliveryStatuses
{
    public const string NeedsConfirmation = "needsConfirmation";
    public const string Queued = "queued";
    public const string Sending = "sending";
    public const string Submitted = "submitted";
    public const string Acknowledged = "acknowledged";
    public const string Failed = "failed";
    public const string Unknown = "unknown";
    public const string ManualPullRequired = "manualPullRequired";
}

public sealed record BoardAgentCommentContext(
    string CommentId,
    string CardId,
    string ProjectRoot,
    string Host,
    string? Session,
    string Body);

public sealed record BoardAgentHostInfo(
    string Host,
    string DisplayName,
    string Mode,
    string? ResolvedPath,
    string? EntryPath,
    string DetectedVia,
    bool Available,
    bool RequiresPathConfirmation,
    string? CandidatePath,
    bool SupportsSession,
    bool InboxPull,
    string? UriScheme);

public sealed record BoardAgentDelivery(
    string CommentId,
    string CardId,
    string ProjectRoot,
    string Host,
    string? Session,
    string Status,
    string ConfirmationHash,
    string? Error,
    DateTimeOffset UpdatedAt);

public sealed record BoardAgentLaunchTarget(string Kind, string? Path, string? Uri, string ProjectRoot);

public sealed record BoardAgentProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    string? StandardInput);

public sealed record BoardAgentProcessResult(int? ExitCode, bool TimedOut, string ErrorSummary);

public sealed record BoardAgentInstalledApp(string DisplayName, string? DisplayIcon, string? UninstallString, string? InstallLocation = null);

public interface IBoardAgentDiscoveryPlatform
{
    string? GetEnvironmentVariable(string name);
    string? GetFolderPath(Environment.SpecialFolder folder);
    IEnumerable<string> GetPathDirectories();
    IEnumerable<string> GetUrlProtocolCommands(string scheme);
    IEnumerable<BoardAgentInstalledApp> GetUninstallEntries();
    IEnumerable<string> GetCommonRoots();
}

public interface IBoardAgentProcessRunner
{
    Task<BoardAgentProcessResult> ExecuteAsync(BoardAgentProcessSpec spec, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class BoardAgentServiceOptions
{
    public string? AppDataDirectory { get; init; }
    public IBoardAgentDiscoveryPlatform? DiscoveryPlatform { get; init; }
    public IBoardAgentProcessRunner? ProcessRunner { get; init; }
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(120);
}

internal sealed record BoardAgentHostResolution(
    string Host,
    string Mode,
    string? ExecutablePath,
    string? EntryPath,
    string DetectedVia,
    string? UriScheme,
    bool SupportsSession,
    bool InboxPull,
    string? PendingPath = null,
    bool Enabled = true,
    int TimeoutSeconds = 120,
    string[]? ArgumentTemplate = null,
    string? MessageTransport = null);
