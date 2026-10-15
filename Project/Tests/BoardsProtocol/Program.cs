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
            CheckRejectedRequests();
            CheckExistingOperations();
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
            "board.card.delete", "board.card.restore", "board.card.add-to-todo"
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

    private static TaskRequest Request(string operation, string payload) =>
        new() { Operation = operation, Payload = Element(payload) };

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
