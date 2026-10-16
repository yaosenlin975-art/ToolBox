using System.Text.Json;
using ToolBox.Core.Todo;

internal static class Program
{
    private static int checks;

    private static int Main()
    {
        try
        {
            CheckAllowedOperations();
            CheckCardCapabilitySchemas();
            CheckV2CapabilitySchemas();
            CheckResponseWarnings();
            CheckRejectedRequests();
            CheckExistingOperations();
            CheckToolsOperations();
            Console.WriteLine($"PASS ({checks} checks)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static void CheckAllowedOperations()
    {
        string[] operations =
        [
            "project.register", "project.list", "project.get", "project.unregister",
            "board.get",
            "board.column.list", "board.column.create", "board.column.update", "board.column.delete", "board.column.move",
            "board.card.list", "board.card.get", "board.card.create", "board.card.update", "board.card.move",
            "board.card.delete", "board.card.restore", "board.card.add-to-todo",
            "board.card.claim", "board.card.release", "board.card.submit", "board.card.close", "board.card.reopen",
            "board.comment.add", "board.comment.get", "board.comment.list", "board.comment.update", "board.comment.delete",
            "board.comment.inbox", "board.comment.ack", "board.comment.bind", "board.comment.deliver", "board.comment.delivery",
            "board.attachment.add", "board.attachment.list", "board.attachment.remove"
        ];

        foreach (string operation in operations)
        {
            TaskProtocol.Validate(Request(operation, "{}"));
            Check(TaskProtocol.IsBoardOperation(operation), operation + " allowed");
            Check(AgentCapabilities.TryGet(operation, out var capability) && capability.InputSchema.ValueKind == JsonValueKind.Object,
                operation + " capability available");
        }
    }

    private static void CheckRejectedRequests()
    {
        foreach (string operation in new[] { "board.future", "board.card.restore-all", "projected.list", "project.list.extra" })
            MustReject(Request(operation, "{}"), "unknown operation " + operation);

        foreach (string json in new[] { "null", "[]", "\"payload\"", "1", "true" })
            MustReject(Request("board.get", json), "non-object payload " + json);

        MustReject(new TaskRequest { Operation = "board.get" }, "missing payload");
        MustReject(new TaskRequest { Operation = "board.get", Id = "0123456789ab", Payload = Element("{}") }, "board id envelope field");
        MustReject(new TaskRequest { Operation = "board.get", Status = "pending", Payload = Element("{}") }, "board status envelope field");
        MustReject(new TaskRequest { Operation = "board.get", Arguments = ["x"], Payload = Element("{}") }, "board arguments envelope field");
        MustReject(Request("board.get", "{\"nested\":{\"id\":1,\"id\":2}}"), "nested duplicate payload field");
        MustReject(new TaskRequest { Operation = "board.get", ProtocolVersion = 2, Payload = Element("{}") }, "unsupported protocol version");
        MustReject(new TaskRequest { Operation = "board.get", RequestId = "invalid", Payload = Element("{}") }, "invalid request id");
    }

    private static void CheckCardCapabilitySchemas()
    {
        AgentCapabilities.TryGet("board.card.move", out var move);
        JsonElement moveProperties = move.InputSchema.GetProperty("properties");
        Check(moveProperties.TryGetProperty("targetColumnId", out _) && !moveProperties.TryGetProperty("columnId", out _),
            "card move schema uses targetColumnId");
        Check(move.Command.Contains("--target-column-id", StringComparison.Ordinal) &&
            !move.Command.Contains("--column-id", StringComparison.Ordinal), "card move command uses target-column-id");
        string[] moveRequired = move.InputSchema.GetProperty("required").EnumerateArray().Select(value => value.GetString()!).ToArray();
        Check(moveRequired.Contains("targetColumnId", StringComparer.Ordinal), "card move requires targetColumnId");

        AgentCapabilities.TryGet("board.card.update", out var update);
        JsonElement updateProperties = update.InputSchema.GetProperty("properties");
        Check(updateProperties.TryGetProperty("columnId", out _) && updateProperties.TryGetProperty("order", out _),
            "card update schema includes columnId and order");
        string[] updateFields = update.InputSchema.GetProperty("anyOf").EnumerateArray()
            .Select(value => value.GetProperty("required")[0].GetString()!).ToArray();
        Check(updateFields.Contains("columnId", StringComparer.Ordinal) && updateFields.Contains("order", StringComparer.Ordinal),
            "card update permits columnId and order as update fields");
    }

    private static void CheckV2CapabilitySchemas()
    {
        var requiredProperties = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["board.card.claim"] = ["id", "actor", "host", "ttlMinutes", "columnId", "order", "revision"],
            ["board.card.release"] = ["id", "actor", "host", "columnId", "order", "revision"],
            ["board.card.submit"] = ["id", "actor", "host", "columnId", "order", "revision"],
            ["board.card.close"] = ["id", "actor", "host", "columnId", "order", "revision"],
            ["board.card.reopen"] = ["id", "actor", "host", "columnId", "order", "revision"],
            ["board.comment.add"] = ["id", "parentId", "body", "actor", "host", "session", "revision", "commentRevision"],
            ["board.comment.get"] = ["commentId"],
            ["board.comment.list"] = ["id", "includeDeleted", "limit", "cursor", "commentRevision"],
            ["board.comment.update"] = ["commentId", "body", "actor", "host", "session", "revision", "commentRevision"],
            ["board.comment.delete"] = ["commentId", "actor", "host", "session", "revision", "commentRevision"],
            ["board.comment.inbox"] = ["host", "session", "actor", "ttlMinutes", "limit", "cursor", "commentRevision"],
            ["board.comment.ack"] = ["commentId", "actor", "host", "session", "claimId", "revision", "commentRevision"],
            ["board.comment.bind"] = ["commentId", "actor", "host", "session", "targetHost", "targetSession", "clear", "revision", "commentRevision"],
            ["board.comment.deliver"] = ["commentId", "confirm", "verifiedUnknown", "confirmationToken", "revision", "commentRevision"],
            ["board.comment.delivery"] = ["commentId", "revision", "commentRevision"],
            ["board.attachment.add"] = ["id", "file", "actor", "host", "session", "revision"],
            ["board.attachment.list"] = ["id"],
            ["board.attachment.remove"] = ["attachmentId", "actor", "host", "session", "revision"]
        };

        foreach (var (name, expectedFields) in requiredProperties)
        {
            Check(AgentCapabilities.TryGet(name, out var capability), name + " capability exists");
            JsonElement properties = capability.InputSchema.GetProperty("properties");
            foreach (string field in expectedFields)
                Check(properties.TryGetProperty(field, out _), name + " schema includes " + field);
            Check(capability.OutputFields.Length > 0 && capability.Effects.Length > 0 && capability.Command.StartsWith("toolbox-cli board ", StringComparison.Ordinal),
                name + " publishes output, effects, and command");
            TaskProtocol.Validate(Request(name, "{}"));
        }
    }

    private static void CheckResponseWarnings()
    {
        var request = new TaskRequest { Operation = "board.get" };
        string withoutWarnings = JsonSerializer.Serialize(TaskResponse.Success(request), TaskProtocol.Options);
        Check(!withoutWarnings.Contains("warnings", StringComparison.Ordinal), "null warnings omitted for v1 responses");
        string withWarnings = JsonSerializer.Serialize(new TaskResponse { RequestId = request.RequestId, Ok = true, Warnings = ["migrated"] }, TaskProtocol.Options);
        Check(withWarnings.Contains("\"warnings\":[\"migrated\"]", StringComparison.Ordinal), "migration warnings serialize when present");
    }

    private static void CheckExistingOperations()
    {
        TaskProtocol.Validate(new TaskRequest { Operation = "task.list", Status = "pending" });
        TaskProtocol.Validate(Request("task.create", "{\"title\":\"existing task\"}"));
        TaskProtocol.Validate(new TaskRequest { Operation = "task.get", Id = "0123456789ab" });
        TaskProtocol.Validate(new TaskRequest { Operation = "task.update", Id = "0123456789ab", Payload = Element("{\"title\":\"updated\"}") });
        foreach (string operation in new[] { "task.complete", "task.reopen", "task.delete", "task.restore" })
            TaskProtocol.Validate(new TaskRequest { Operation = operation, Id = "0123456789ab" });
        TaskProtocol.Validate(new TaskRequest { Operation = "host.stop" });
        TaskProtocol.Validate(new TaskRequest { Operation = "app.activate", Arguments = ["/C:TODO"] });
        TaskProtocol.Validate(Request("ocr.file", "{\"path\":\"C:\\\\test.png\"}"));
        TaskProtocol.Validate(Request("screenshot.search", "{}"));
        Check(true, "existing task, OCR, and screenshot operations remain accepted");
    }

    private static void CheckToolsOperations()
    {
        string[] names = ["tools.list", "tools.get", "tools.call", "tools.result", "tools.cancel"];
        Check(AgentCapabilities.All.Count == 54, "five tools capabilities extend the existing 49 entries");
        foreach (string name in names)
        {
            Check(TaskProtocol.IsToolsOperation(name), name + " is recognized as a tools operation");
            Check(AgentCapabilities.TryGet(name, out var capability) && capability.InputSchema.ValueKind == JsonValueKind.Object,
                name + " capability exists");
            Check(capability.Availability.RuntimeChecked && capability.Availability.Requirements.Length > 0,
                name + " publishes runtime requirements");
            Check(capability.Command.StartsWith("toolbox-cli tools ", StringComparison.Ordinal), name + " publishes CLI syntax");
        }

        TaskProtocol.Validate(new TaskRequest { Operation = "tools.list" });
        TaskProtocol.Validate(new TaskRequest { Operation = "tools.get", Id = "read_file" });
        TaskProtocol.Validate(ToolsCall("read_file", "{\"path\":\"C:\\\\test.txt\"}"));
        string callId = Guid.NewGuid().ToString("D");
        TaskProtocol.Validate(new TaskRequest { Operation = "tools.result", Id = callId });
        TaskProtocol.Validate(new TaskRequest { Operation = "tools.cancel", Id = callId });

        MustReject(new TaskRequest { Operation = "tools.list", Id = "unexpected" }, "tools.list id field");
        MustReject(new TaskRequest { Operation = "tools.get", Id = "invalid name" }, "invalid tool name");
        MustReject(ToolsCall("read_file", "{\"path\":{\"nested\":1}}"), "nested tool argument");
        MustReject(ToolsCall("read_file", "{\"path\":\"x\",\"path\":\"y\"}"), "duplicate tool argument");
        MustReject(new TaskRequest { Operation = "tools.result", Id = callId.Replace('-', 'x') }, "non-D call ID");
        MustReject(new TaskRequest { Operation = "tools.cancel", Id = callId, Status = "running" }, "tools.cancel extra field");
    }

    private static TaskRequest Request(string operation, string payload) =>
        new() { Operation = operation, Payload = Element(payload) };

    private static TaskRequest ToolsCall(string name, string payload) =>
        new() { Operation = "tools.call", Id = name, Payload = Element(payload) };

    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void MustReject(TaskRequest request, string label)
    {
        bool rejected = false;
        try { TaskProtocol.Validate(request); }
        catch (TaskCommandException ex) when (ex.Code == "INVALID_ARGUMENT" || ex.Code == "PROTOCOL_ERROR")
        {
            rejected = true;
        }
        Check(rejected, label + " rejected");
    }

    private static void Check(bool condition, string label)
    {
        checks++;
        if (!condition) throw new InvalidOperationException("FAIL: " + label);
    }
}
