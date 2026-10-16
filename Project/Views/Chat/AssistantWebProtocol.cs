using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ToolBox.Views.Chat;

public sealed record AssistantWebCommand(string Id, string Type, string? SessionId, JsonElement Payload);

public static partial class AssistantWebProtocol
{
    public const int MaxEnvelopeBytes = 1_048_576;
    private const int MaxMessageLength = 100_000;
    private const int MaxTitleLength = 120;

    private static readonly HashSet<string> CommandTypes = new(StringComparer.Ordinal)
    {
        "ready", "session.list", "session.create", "session.select", "session.rename", "session.delete",
        "message.send", "run.cancel", "sessions.clear", "link.open"
    };

    public static bool TryParse(string json, out AssistantWebCommand? command, out string error, out string? requestId)
    {
        command = null;
        requestId = null;
        error = "消息格式无效。";
        if (string.IsNullOrEmpty(json) || Encoding.UTF8.GetByteCount(json) > MaxEnvelopeBytes)
        {
            error = "消息过大或为空。";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !HasOnlyProperties(root, "version", "id", "type", "sessionId", "payload"))
            {
                error = "消息字段无效。";
                return false;
            }

            if (!root.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String)
            {
                error = "消息编号无效。";
                return false;
            }
            requestId = idValue.GetString();
            if (requestId is null || requestId.Length is < 1 or > 128 || !RequestIdPattern().IsMatch(requestId))
            {
                error = "消息编号无效。";
                return false;
            }

            if (!root.TryGetProperty("version", out var versionValue) || versionValue.ValueKind != JsonValueKind.Number || !versionValue.TryGetInt32(out var version) || version != 1 ||
                !root.TryGetProperty("type", out var typeValue) || typeValue.ValueKind != JsonValueKind.String)
            {
                error = "消息版本或类型无效。";
                return false;
            }

            var type = typeValue.GetString();
            if (type is null || !CommandTypes.Contains(type))
            {
                error = "不支持的助手命令。";
                return false;
            }

            string? sessionId = null;
            if (root.TryGetProperty("sessionId", out var sessionValue))
            {
                if (sessionValue.ValueKind != JsonValueKind.String || !IsSessionId(sessionValue.GetString()))
                {
                    error = "会话编号无效。";
                    return false;
                }
                sessionId = sessionValue.GetString();
            }

            var needsSession = type is "session.select" or "session.rename" or "session.delete" or "message.send" or "run.cancel";
            if (needsSession != (sessionId is not null))
            {
                error = needsSession ? "命令缺少会话编号。" : "该命令不接受会话编号。";
                return false;
            }

            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object ||
                !ValidatePayload(type, payload, out error))
                return false;

            command = new AssistantWebCommand(requestId, type, sessionId, payload.Clone());
            error = string.Empty;
            return true;
        }
        catch (JsonException)
        {
            error = "消息 JSON 无效。";
            return false;
        }
    }

    public static bool IsTrustedOrigin(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               uri.Scheme == Uri.UriSchemeHttps &&
               string.Equals(uri.Host, "toolbox.assistant.local", StringComparison.OrdinalIgnoreCase) &&
               uri.Port == 443 &&
               string.IsNullOrEmpty(uri.UserInfo);
    }

    private static bool ValidatePayload(string type, JsonElement payload, out string error)
    {
        error = "命令参数无效。";
        switch (type)
        {
            case "message.send":
                if (!HasOnlyProperties(payload, "text") || !TryRequiredString(payload, "text", MaxMessageLength, out var text) || string.IsNullOrWhiteSpace(text))
                {
                    error = "消息内容无效。";
                    return false;
                }
                return true;
            case "session.rename":
                if (!HasOnlyProperties(payload, "title") || !TryRequiredString(payload, "title", MaxTitleLength, out var title) || string.IsNullOrWhiteSpace(title))
                {
                    error = "会话名称无效。";
                    return false;
                }
                return true;
            case "run.cancel":
                if (!HasOnlyProperties(payload, "runId")) return false;
                if (payload.TryGetProperty("runId", out var runId) &&
                    (runId.ValueKind != JsonValueKind.String || !IsToken(runId.GetString(), 128)))
                {
                    error = "运行编号无效。";
                    return false;
                }
                return true;
            case "link.open":
                if (!HasOnlyProperties(payload, "url") || !TryRequiredString(payload, "url", 2048, out var url) || !IsSafeExternalUrl(url))
                {
                    error = "链接必须是有效的 HTTP 或 HTTPS 地址。";
                    return false;
                }
                return true;
            default:
                if (HasOnlyProperties(payload)) return true;
                error = "该命令不接受参数。";
                return false;
        }
    }

    private static bool TryRequiredString(JsonElement element, string name, int maxLength, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString() ?? string.Empty;
        return value.Length is > 0 && value.Length <= maxLength;
    }

    private static bool IsSafeExternalUrl(string value)
    {
        return value.Length <= 2048 && !value.Any(char.IsControl) &&
               Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
               string.IsNullOrEmpty(uri.UserInfo);
    }

    private static bool IsSessionId(string? value) => value is not null && SessionIdPattern().IsMatch(value);

    private static bool IsToken(string? value, int maxLength) =>
        value is { Length: > 0 } && value.Length <= maxLength && RequestIdPattern().IsMatch(value);

    private static bool HasOnlyProperties(JsonElement element, params string[] names)
    {
        var allowed = new HashSet<string>(names, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name) || !seen.Add(property.Name)) return false;
        }
        return true;
    }

    [GeneratedRegex("^[a-zA-Z0-9._:-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex RequestIdPattern();

    [GeneratedRegex("^[a-fA-F0-9]{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex SessionIdPattern();
}
