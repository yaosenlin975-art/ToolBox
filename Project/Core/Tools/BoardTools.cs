// 内部助手看板工具：以运行时注入的内部身份（host/actor/session 不可由模型参数覆盖）经
// BoardCommandService 读写已登记且已授权的项目看板，携带 revision 与完整认领。
using ToolBox.Core.BoardAgents;
using ToolBox.Core.Llm;
using ToolBox.Core.Security;
using ToolBox.Core.Todo;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ToolBox.Core.Tools;

public static class BoardTools
{
    [Tool("board_list_cards", "读取已登记项目的看板卡片列表（只读）")]
    public static async Task<string> ListCards(
        [ToolParam("已登记且已授权的项目根目录")] string project,
        CancellationToken cancellationToken = default)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal) { ["project"] = RequireProject(project) };
        return Format(await ExecuteAsync("board.card.list", payload, cancellationToken).ConfigureAwait(false));
    }

    [Tool("board_list_comments", "读取指定卡片的评论列表（只读）")]
    public static async Task<string> ListComments(
        [ToolParam("已登记且已授权的项目根目录")] string project,
        [ToolParam("卡片 UUID")] string cardId,
        CancellationToken cancellationToken = default)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["project"] = RequireProject(project),
            ["id"] = cardId
        };
        return Format(await ExecuteAsync("board.comment.list", payload, cancellationToken).ConfigureAwait(false));
    }

    [Tool("board_claim_card", "以当前内部身份认领卡片并获取最新 revision 与租约")]
    public static async Task<string> ClaimCard(
        [ToolParam("已登记且已授权的项目根目录")] string project,
        [ToolParam("卡片 UUID")] string cardId,
        [ToolParam("租约分钟数（5-480）")] int ttlMinutes = 30,
        CancellationToken cancellationToken = default)
    {
        var payload = IdentityPayload(RequireProject(project));
        payload["id"] = cardId;
        payload["ttlMinutes"] = ttlMinutes;
        return Format(await ExecuteAsync("board.card.claim", payload, cancellationToken).ConfigureAwait(false));
    }

    [Tool("board_reply_comment", "回复已认领的卡片评论（内部身份必须有有效认领）")]
    public static async Task<string> ReplyComment(
        [ToolParam("已登记且已授权的项目根目录")] string project,
        [ToolParam("卡片 UUID")] string cardId,
        [ToolParam("父评论 UUID")] string parentId,
        [ToolParam("回复正文")] string body,
        [ToolParam("看板 revision")] long revision,
        [ToolParam("评论 revision")] long commentRevision,
        CancellationToken cancellationToken = default)
    {
        var payload = IdentityPayload(RequireProject(project));
        payload["id"] = cardId;
        payload["parentId"] = parentId;
        payload["body"] = body;
        payload["revision"] = revision;
        payload["commentRevision"] = commentRevision;
        Guid requestId = StableRequestId("board.comment.add", payload["session"] as string, cardId, parentId);
        return Format(await ExecuteAsync("board.comment.add", payload, cancellationToken, requestId).ConfigureAwait(false));
    }

    [Tool("board_ack_comment", "确认已处理的评论（绑定原 claimId 与精确身份）")]
    public static async Task<string> AckComment(
        [ToolParam("已登记且已授权的项目根目录")] string project,
        [ToolParam("评论 UUID")] string commentId,
        [ToolParam("收件认领 UUID")] string claimId,
        [ToolParam("看板 revision")] long revision,
        [ToolParam("评论 revision")] long commentRevision,
        CancellationToken cancellationToken = default)
    {
        var payload = IdentityPayload(RequireProject(project));
        payload["commentId"] = commentId;
        payload["claimId"] = claimId;
        payload["revision"] = revision;
        payload["commentRevision"] = commentRevision;
        Guid requestId = StableRequestId("board.comment.ack", payload["session"] as string, commentId, claimId);
        return Format(await ExecuteAsync("board.comment.ack", payload, cancellationToken, requestId).ConfigureAwait(false));
    }

    private static Dictionary<string, object?> IdentityPayload(string project)
    {
        string? sessionId = AssistantRuntime.CurrentExecutionSessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new InvalidOperationException("看板工具只能由内部助手轮次调用。");
        Guid actorId = AssistantRuntime.Instance.GetSessionActorId(sessionId);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["project"] = project,
            ["host"] = "internal",
            ["actor"] = actorId.ToString("D"),
            ["session"] = sessionId
        };
    }

    private static string RequireProject(string project)
    {
        if (string.IsNullOrWhiteSpace(project))
            throw new ArgumentException("项目根不能为空。", nameof(project));
        if (!FileAccessWhitelist.Instance.IsAllowed(project))
            throw new UnauthorizedAccessException("项目根不在用户白名单内：" + project);
        return project;
    }

    private static async Task<TaskResponse> ExecuteAsync(
        string operation,
        Dictionary<string, object?> payload,
        CancellationToken cancellationToken,
        Guid? requestId = null)
    {
        var request = new TaskRequest
        {
            Operation = operation,
            RequestId = (requestId ?? Guid.NewGuid()).ToString("D"),
            Payload = JsonSerializer.SerializeToElement(payload, TaskProtocol.Options)
        };
        TaskResponse response = await BoardCommandService.Instance.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.Ok)
            throw new InvalidOperationException($"看板操作被拒绝：{response.Error?.Code} {response.Error?.Message}");
        return response;
    }

    // 稳定关联键：同一 (会话, 卡片, 父评论) 的回帖或同一 (会话, 评论, 认领) 的 ack 复用同一请求键，
    // 便于崩溃恢复时对齐已发生的副作用而不重复调用。
    private static Guid StableRequestId(params string?[] keys)
    {
        string joined = string.Join('\u001f', keys.Select(key => key ?? string.Empty));
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(joined)).AsSpan(0, 16));
    }

    private static string Format(TaskResponse response) =>
        JsonSerializer.Serialize(response.Data, TaskProtocol.Options);
}
