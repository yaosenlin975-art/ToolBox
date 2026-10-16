using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ToolBox.Core.Todo;

public sealed class TaskRequest
{
    public int ProtocolVersion { get; set; } = 1;
    public string RequestId { get; set; } = Guid.NewGuid().ToString();
    public string Operation { get; set; } = "";
    public string? Id { get; set; }
    public string? Status { get; set; }
    public JsonElement? Payload { get; set; }
    public string[]? Arguments { get; set; }
}

public sealed record TaskError(string Code, string Message, string? Field = null);

public sealed class TaskResponse
{
    public int ProtocolVersion { get; set; } = 1;
    public string RequestId { get; set; } = "";
    public bool Ok { get; set; }
    public object? Data { get; set; }
    public TaskError? Error { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Warnings { get; set; }

    [JsonIgnore]
    public int ExitCode => Ok ? 0 : Error?.Code switch
    {
        "INVALID_ARGUMENT" => 2,
        "NOT_FOUND" => 3,
        "CONFLICT" or "RESULT_TOO_LARGE" or "ENGINE_UNAVAILABLE" or "ASSISTANT_DISABLED" or "JOB_LIMIT" => 4,
        "HOST_UNAVAILABLE" or "TIMEOUT" or "ACCESS_DENIED" => 5,
        "IO_ERROR" or "DATA_CORRUPT" => 6,
        _ => 7
    };

    public static TaskResponse Success(TaskRequest request, object? data = null) =>
        new() { RequestId = request.RequestId, Ok = true, Data = data };

    public static TaskResponse Failure(TaskRequest? request, string code, string message, string? field = null) =>
        new() { RequestId = request?.RequestId ?? "", Error = new(code, message, field) };
}

public sealed class TaskCommandException(string code, string message, string? field = null) : Exception(message)
{
    public string Code { get; } = code;
    public string? Field { get; } = field;
}

public static class TaskProtocol
{
    public const int MaxBytes = 4 * 1024 * 1024;
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static void Validate(TaskRequest request)
    {
        if (request.ProtocolVersion != 1)
            throw new TaskCommandException("PROTOCOL_ERROR", "Unsupported protocol version.");
        if (!Guid.TryParse(request.RequestId, out _))
            throw new TaskCommandException("PROTOCOL_ERROR", "requestId must be a GUID.");
        if (IsToolsOperation(request.Operation))
        {
            ValidateToolsRequest(request);
            return;
        }
        if (IsBoardOperation(request.Operation))
        {
            if (request.Id != null) Invalid("id is not accepted for this operation.", "id");
            if (request.Status != null) Invalid("status is not accepted for this operation.", "status");
            if (request.Arguments != null) Invalid("arguments are not accepted for this operation.", "arguments");
            if (request.Payload is not { ValueKind: JsonValueKind.Object })
                Invalid("A JSON object is required.", "payload");
            RejectDuplicateProperties(request.Payload!.Value);
            return;
        }
        bool write = request.Operation is "task.create" or "task.update" or "ocr.file" or "screenshot.search";
        bool identified = request.Operation is "task.get" or "task.update" or "task.complete" or "task.reopen" or "task.delete" or "task.restore";
        if (!write && !identified && request.Operation is not ("task.list" or "host.stop" or "app.activate"))
            Invalid("Unknown operation.", "operation");
        if (identified && (request.Id?.Length != 12 || !request.Id.All(char.IsAsciiHexDigit)))
            Invalid("An exact 12-character hexadecimal task ID is required.", "id");
        if (!identified && request.Id != null) Invalid("id is not accepted for this operation.", "id");
        if (request.Operation == "task.list")
        {
            if (request.Status is not (null or "all" or "pending" or "completed" or "trashed" or "repeating"))
                Invalid("Invalid status filter.", "status");
        }
        else if (request.Status != null) Invalid("status is not accepted for this operation.", "status");
        if (request.Operation == "app.activate")
        {
            if (request.Arguments is { Length: > 32 } || request.Arguments?.Any(a => a == null || a.Length > 32768 || a.StartsWith("--", StringComparison.Ordinal)) == true)
                Invalid("Invalid activation arguments.", "arguments");
        }
        else if (request.Arguments != null) Invalid("arguments are not accepted for this operation.", "arguments");
        if (write)
        {
            if (request.Payload is not { ValueKind: JsonValueKind.Object })
                Invalid("A JSON object is required.", "payload");
            ValidatePayload(request.Operation, request.Payload!.Value);
        }
        else if (request.Payload != null) Invalid("payload is not accepted for this operation.", "payload");
    }

    public static bool IsBoardOperation(string operation) => operation is
        "project.register" or "project.list" or "project.get" or "project.unregister" or
        "board.get" or
        "board.column.list" or "board.column.create" or "board.column.update" or "board.column.delete" or "board.column.move" or
        "board.card.list" or "board.card.get" or "board.card.create" or "board.card.update" or "board.card.move" or
        "board.card.delete" or "board.card.restore" or "board.card.add-to-todo" or
        "board.card.claim" or "board.card.release" or "board.card.submit" or "board.card.close" or "board.card.reopen" or
        "board.comment.add" or "board.comment.get" or "board.comment.list" or "board.comment.update" or
        "board.comment.delete" or "board.comment.inbox" or "board.comment.ack" or "board.comment.bind" or
        "board.comment.deliver" or "board.comment.delivery" or
        "board.attachment.add" or "board.attachment.list" or "board.attachment.remove";

    public static bool IsToolsOperation(string operation) => operation is
        "tools.list" or "tools.get" or "tools.call" or "tools.result" or "tools.cancel";

    public static void ValidatePayload(string operation, JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) Invalid("A JSON object is required.", "payload");
        if (operation == "tools.call")
        {
            ValidateToolArguments(payload);
            return;
        }
        if (operation is "ocr.file" or "screenshot.search")
        {
            ValidateAgentPayload(operation, payload);
            return;
        }
        bool create = operation == "task.create";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in payload.EnumerateObject())
        {
            string name = property.Name;
            if (!seen.Add(name)) Invalid("Duplicate field.", name);
            var value = property.Value;
            switch (name)
            {
                case "title":
                case "category":
                    if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                        Invalid("A non-empty string is required.", name);
                    break;
                case "description":
                    if (value.ValueKind != JsonValueKind.String) Invalid("A string is required.", name);
                    break;
                case "priority":
                    if (!value.TryGetInt32Safe(out int priority) || priority is < 0 or > 2)
                        Invalid("priority must be an integer from 0 to 2.", name);
                    break;
                case "progress" when !create:
                    if (!value.TryGetInt32Safe(out int progress) || progress is < 0 or > 100)
                        Invalid("progress must be an integer from 0 to 100.", name);
                    break;
                case "tags":
                    if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(t => t.ValueKind != JsonValueKind.String))
                        Invalid("tags must be an array of strings.", name);
                    break;
                case "dueDate":
                    if (value.ValueKind != JsonValueKind.Null) _ = ParseDate(value);
                    break;
                case "parentId" when create:
                    if (value.ValueKind != JsonValueKind.Null && (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: 12 } id || !id.All(char.IsAsciiHexDigit)))
                        Invalid("parentId must be an exact task ID or null.", name);
                    break;
                default:
                    Invalid("Unknown field.", name);
                    break;
            }
        }
        if (create && !seen.Contains("title")) Invalid("title is required.", "title");
        if (!create && seen.Count == 0) Invalid("At least one update field is required.", "payload");
    }

    private static void ValidateAgentPayload(string operation, JsonElement payload)
    {
        RejectDuplicateProperties(payload);
        foreach (var property in payload.EnumerateObject())
        {
            var value = property.Value;
            string name = property.Name;
            if (operation == "ocr.file")
            {
                if (name == "path")
                {
                    if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 and <= 32768 } path ||
                        !System.IO.Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal))
                        Invalid("path must be an absolute local file path.", name);
                }
                else if (name == "language")
                {
                    if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 and <= 128 } lang ||
                        !System.Text.RegularExpressions.Regex.IsMatch(lang, "^[a-z][a-z0-9_]{0,31}(\\+[a-z][a-z0-9_]{0,31})*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                        Invalid("language must contain Tesseract language codes joined by '+'.", name);
                }
                else Invalid("Unknown field.", name);
            }
            else switch (name)
            {
                case "tag":
                    if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > 4096)
                        Invalid("A non-empty string of at most 4096 characters is required.", name);
                    break;
                case "cursor":
                    if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: 17 } cursor || cursor.Any(c => c is < '0' or > '9'))
                        Invalid("cursor must be a 17-digit screenshot key.", name);
                    break;
                case "minRating":
                    if (!value.TryGetInt32Safe(out int rating) || rating is < 0 or > 5) Invalid("minRating must be from 0 to 5.", name);
                    break;
                case "colorHue":
                    if (!value.TryGetInt32Safe(out int hue) || hue is < 0 or > 359) Invalid("colorHue must be from 0 to 359.", name);
                    break;
                case "limit":
                    if (!value.TryGetInt32Safe(out int limit) || limit is < 1 or > 100) Invalid("limit must be from 1 to 100.", name);
                    break;
                default: Invalid("Unknown field.", name); break;
            }
        }
        if (operation == "ocr.file" && !payload.TryGetProperty("path", out _)) Invalid("path is required.", "path");
    }

    public static DateTime ParseDate(JsonElement value)
    {
        string? text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        string[] formats = ["yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
            "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"];
        if (!DateTimeOffset.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            throw new TaskCommandException("INVALID_ARGUMENT", "dueDate must be an ISO 8601 timestamp with a timezone.", "dueDate");
        return date.UtcDateTime;
    }

    public static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) Invalid("Duplicate field.", property.Name);
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicateProperties(child);
    }

    private static bool TryGetInt32Safe(this JsonElement value, out int number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out number);
    }

    private static void Invalid(string message, string field) =>
        throw new TaskCommandException("INVALID_ARGUMENT", message, field);

    private static void ValidateToolsRequest(TaskRequest request)
    {
        switch (request.Operation)
        {
            case "tools.list":
                if (request.Id != null) Invalid("id is not accepted for this operation.", "id");
                if (request.Status != null) Invalid("status is not accepted for this operation.", "status");
                if (request.Payload != null) Invalid("payload is not accepted for this operation.", "payload");
                if (request.Arguments != null) Invalid("arguments are not accepted for this operation.", "arguments");
                break;
            case "tools.get":
                ValidateToolName(request.Id);
                ValidateEmptyToolsFields(request);
                break;
            case "tools.call":
                ValidateToolName(request.Id);
                if (request.Status != null) Invalid("status is not accepted for this operation.", "status");
                if (request.Arguments != null) Invalid("arguments are not accepted for this operation.", "arguments");
                if (request.Payload is not { ValueKind: JsonValueKind.Object })
                    Invalid("A JSON object is required.", "payload");
                ValidateToolArguments(request.Payload!.Value);
                break;
            case "tools.result":
            case "tools.cancel":
                if (request.Id is not { } id || !Guid.TryParseExact(id, "D", out _))
                    Invalid("A D-format call GUID is required.", "id");
                ValidateEmptyToolsFields(request);
                break;
        }
    }

    private static void ValidateEmptyToolsFields(TaskRequest request)
    {
        if (request.Status != null) Invalid("status is not accepted for this operation.", "status");
        if (request.Payload != null) Invalid("payload is not accepted for this operation.", "payload");
        if (request.Arguments != null) Invalid("arguments are not accepted for this operation.", "arguments");
    }

    private static void ValidateToolName(string? name)
    {
        if (name is null || name.Length is 0 or > 128 || !char.IsAsciiLetter(name[0]) ||
            name.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('_' or '.' or '-')))
            Invalid("A valid tool name is required.", "id");
    }

    private static void ValidateToolArguments(JsonElement payload)
    {
        RejectDuplicateProperties(payload);
        foreach (JsonProperty property in payload.EnumerateObject())
            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Undefined)
                Invalid("Tool parameters must be flat JSON scalar values.", property.Name);
    }
}
