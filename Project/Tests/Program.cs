using System.Text.Json;
using ToolBox.Core.Todo;

internal static class Program
{
    private static async Task Main()
    {
        CheckProtocolValidation();
        string root = Path.Combine(Path.GetTempPath(), "ToolBox.TaskChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await CheckCrudAndTreeAsync(Path.Combine(root, "todos.json"));
            await CheckConcurrentWritesAsync(Path.Combine(root, "concurrent.json"));
            await CheckFailedSaveRollbackAsync(Path.Combine(root, "rollback.json"));
            await CheckOversizedCreateRejectedAsync(Path.Combine(root, "oversized.json"));
            await CheckOversizedListRejectedAsync(Path.Combine(root, "oversized-list.json"));
            CheckCorruptFile(Path.Combine(root, "corrupt.json"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        Console.WriteLine("Task checks passed.");
    }

    private static async Task CheckCrudAndTreeAsync(string path)
    {
        var store = new TodoStore(path);
        var parentResponse = await CallAsync(store, "task.create", payload: "{\"title\":\"parent\"}");
        Check(parentResponse.Ok, "create parent");
        string parentId = String(Data(parentResponse), "id");

        var childResponse = await CallAsync(store, "task.create", payload: $"{{\"title\":\"child\",\"parentId\":\"{parentId}\"}}");
        Check(childResponse.Ok, "create child");
        string childId = String(Data(childResponse), "id");
        Check(Data(await CallAsync(store, "task.list", status: "pending")).GetArrayLength() == 2, "pending filter");

        var blockedParentComplete = await CallAsync(store, "task.complete", parentId);
        CheckCode(blockedParentComplete, "CONFLICT", "parent completion requires completed children");

        var update = await CallAsync(store, "task.update", childId,
            "{\"description\":\"details\",\"category\":\"Work\",\"tags\":[\"one\",\"two\"],\"dueDate\":\"2026-10-09T12:30:00+08:00\",\"progress\":40}");
        Check(update.Ok, "update task fields");
        Check(Int(Data(update), "progress") == 40, "leaf progress update");
        Check(Int(Data(await CallAsync(store, "task.get", parentId)), "progress") == 40, "parent progress aggregation");
        Check(String(Data(update), "dueDate").EndsWith('Z'), "dueDate output uses UTC");

        var cleared = await CallAsync(store, "task.update", childId,
            "{\"description\":\"\",\"tags\":[],\"dueDate\":null}");
        Check(cleared.Ok, "clear description, tags, and dueDate");
        Check(String(Data(cleared), "description") == "", "empty description retained");
        Check(Data(cleared).GetProperty("tags").GetArrayLength() == 0, "empty tags retained");
        Check(Data(cleared).GetProperty("dueDate").ValueKind == JsonValueKind.Null, "dueDate cleared");

        var parentProgressUpdate = await CallAsync(store, "task.update", parentId, "{\"progress\":80}");
        CheckCode(parentProgressUpdate, "CONFLICT", "parent progress cannot be set manually");

        var completedChild = await CallAsync(store, "task.complete", childId);
        Check(completedChild.Ok && Bool(Data(completedChild), "isCompleted"), "complete child");
        Check(Data(await CallAsync(store, "task.list", status: "completed")).GetArrayLength() == 1, "completed filter");
        Check(Int(Data(completedChild), "progress") == 100, "completing leaf sets progress to 100");
        Check(Int(Data(await CallAsync(store, "task.get", parentId)), "progress") == 100, "completed child updates parent progress");
        Check((await CallAsync(store, "task.complete", parentId)).Ok, "complete parent after children");
        Check(Data(await CallAsync(store, "task.list", status: "completed")).GetArrayLength() == 2, "completed filter includes parent and child");
        var reopenedParent = await CallAsync(store, "task.reopen", parentId);
        Check(reopenedParent.Ok && !Bool(Data(reopenedParent), "isCompleted"), "reopen parent");
        var reopenedChild = await CallAsync(store, "task.reopen", childId);
        Check(reopenedChild.Ok && !Bool(Data(reopenedChild), "isCompleted"), "reopen child");
        Check(Data(await CallAsync(store, "task.list", status: "pending")).GetArrayLength() == 2, "reopen updates status filters");

        int notifications = 0;
        store.ItemsChanged += () => notifications++;
        var deleted = await CallAsync(store, "task.delete", parentId);
        Check(deleted.Ok && Bool(Data(deleted), "isTrashed"), "delete moves parent to trash");
        Check(notifications == 1, "cascade trash notifies once");
        Check(Bool(Data(await CallAsync(store, "task.get", childId)), "isTrashed"), "delete cascades to child");
        Check(Data(await CallAsync(store, "task.list", status: "all")).GetArrayLength() == 0, "all excludes trash");
        Check(Data(await CallAsync(store, "task.list", status: "trashed")).GetArrayLength() == 2, "trashed filter includes subtree");
        CheckCode(await CallAsync(store, "task.restore", childId), "CONFLICT", "child restore requires active ancestor");
        int afterFirstDelete = notifications;
        Check((await CallAsync(store, "task.delete", parentId)).Ok, "repeated delete is idempotent");
        Check(notifications == afterFirstDelete, "repeated delete does not notify");
        Check((await CallAsync(store, "task.restore", parentId)).Ok, "restore parent subtree");
        Check(!Bool(Data(await CallAsync(store, "task.get", childId)), "isTrashed"), "restore cascades to child");

        await store.AddRepeatingAsync("weekly", ERepeatFrequency.Weekly);
        var repeating = Data(await CallAsync(store, "task.list", status: "repeating"));
        Check(repeating.GetArrayLength() == 1, "repeating filter");
        Check(String(repeating[0].GetProperty("repeatConfig"), "frequency") == "weekly", "repeat config DTO mapping");

        var prefix = await CallAsync(store, "task.get", parentId[..6]);
        CheckCode(prefix, "INVALID_ARGUMENT", "partial ID is rejected");
        CheckCode(await CallAsync(store, "task.get", "abcdef123456"), "NOT_FOUND", "missing exact ID");

        var restarted = new TodoStore(path);
        Check((await CallAsync(restarted, "task.get", parentId)).Ok, "committed data survives reload");
        var parentDto = Data(await CallAsync(restarted, "task.get", parentId));
        Check(!parentDto.TryGetProperty("sessionId", out _) && !parentDto.TryGetProperty("statusText", out _), "DTO excludes internal fields");
    }

    private static async Task CheckConcurrentWritesAsync(string path)
    {
        var store = new TodoStore(path);
        var requests = Enumerable.Range(0, 24)
            .Select(index => CallAsync(store, "task.create", payload: $"{{\"title\":\"task-{index}\"}}"));
        var responses = await Task.WhenAll(requests);
        Check(responses.All(response => response.Ok), "concurrent creates succeed");
        Check(Data(await CallAsync(store, "task.list", status: "all")).GetArrayLength() == responses.Length, "concurrent creates do not lose items");
    }

    private static async Task CheckFailedSaveRollbackAsync(string path)
    {
        var store = new TodoStore(path);
        var created = await CallAsync(store, "task.create", payload: "{\"title\":\"stable\"}");
        Check(created.Ok, "create rollback fixture");
        var original = store.Items.Single();
        byte[] oldFile = File.ReadAllBytes(path);
        TaskResponse failed;
        var attributes = File.GetAttributes(path);
        File.SetAttributes(path, attributes | FileAttributes.ReadOnly);
        try
        {
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                failed = await CallAsync(store, "task.update", original.Id, "{\"title\":\"changed\"}");
        }
        finally { File.SetAttributes(path, attributes); }
        CheckCode(failed, "IO_ERROR", "locked target prevents atomic replacement");
        Check(ReferenceEquals(original, store.Items.Single()), "rollback preserves TodoItem object reference");
        Check(original.Title == "stable", "rollback restores object fields");
        Check(File.ReadAllBytes(path).SequenceEqual(oldFile), "failed save preserves old file bytes");
    }

    private static async Task CheckOversizedCreateRejectedAsync(string path)
    {
        var store = new TodoStore(path);
        var seed = await CallAsync(store, "task.create", payload: "{\"title\":\"seed\"}");
        Check(seed.Ok, "create oversized-response fixture");
        int responseOverhead = JsonSerializer.SerializeToUtf8Bytes(seed, TaskProtocol.Options).Length - "seed".Length;
        string title = new('x', TaskProtocol.MaxBytes - responseOverhead + 1);
        var request = new TaskRequest
        {
            Operation = "task.create",
            Payload = JsonSerializer.SerializeToElement(new { title }, TaskProtocol.Options)
        };
        Check(JsonSerializer.SerializeToUtf8Bytes(request, TaskProtocol.Options).Length <= TaskProtocol.MaxBytes, "oversized result request fits the 4 MiB request limit");
        byte[] oldFile = File.ReadAllBytes(path);
        int oldCount = store.Items.Count;
        var response = await store.ExecuteTaskAsync(request);
        CheckCode(response, "RESULT_TOO_LARGE", "create response is preflighted before save");
        Check(store.Items.Count == oldCount, "oversized create is not added in memory");
        Check(File.ReadAllBytes(path).SequenceEqual(oldFile), "oversized create does not change persisted data");
    }

    private static async Task CheckOversizedListRejectedAsync(string path)
    {
        string description = new('d', 2_100_000);
        var data = new TodoData
        {
            Items =
            [
                new TodoItem { Id = "012345abcdef", Description = description },
                new TodoItem { Id = "abcdef012345", Description = description }
            ]
        };
        File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        var store = new TodoStore(path);
        var response = await CallAsync(store, "task.list", status: "all");
        CheckCode(response, "RESULT_TOO_LARGE", "oversized list fails without truncation");
    }

    private static void CheckCorruptFile(string path)
    {
        VerifyCorruptFile(path, "{not-json");
        VerifyCorruptFile(path + ".missing-items", "{\"categories\":[]}");
        VerifyCorruptFile(path + ".empty-object", "{}");
        VerifyCorruptFile(path + ".duplicate-ids", "{\"categories\":[],\"items\":[{\"Id\":\"012345abcdef\"},{\"Id\":\"012345abcdef\"}]}");
        VerifyCorruptFile(path + ".parent-cycle", "{\"categories\":[],\"items\":[{\"Id\":\"012345abcdef\",\"ParentId\":\"abcdef012345\"},{\"Id\":\"abcdef012345\",\"ParentId\":\"012345abcdef\"}]}");
    }

    private static void VerifyCorruptFile(string path, string damaged)
    {
        File.WriteAllText(path, damaged);
        var store = new TodoStore(path);
        CheckCode(CallAsync(store, "task.list").GetAwaiter().GetResult(), "DATA_CORRUPT", "corrupt file blocks reads");
        CheckCode(CallAsync(store, "task.create", payload: "{\"title\":\"blocked\"}").GetAwaiter().GetResult(), "DATA_CORRUPT", "corrupt file blocks writes");
        Check(File.ReadAllText(path) == damaged, "corrupt file remains unchanged");
    }

    private static void CheckProtocolValidation()
    {
        ExpectInvalid("{}");
        ExpectInvalid("{\"title\":\"valid\",\"unknown\":1}");
        ExpectInvalid("{\"title\":\"valid\",\"title\":\"duplicate\"}");
        ExpectInvalid("{\"title\":\"valid\",\"dueDate\":\"2026-10-09T12:30:00\"}");

        static void ExpectInvalid(string json)
        {
            using var document = JsonDocument.Parse(json);
            try
            {
                TaskProtocol.ValidatePayload("task.create", document.RootElement);
                throw new InvalidOperationException($"Expected invalid payload: {json}");
            }
            catch (TaskCommandException ex)
            {
                Check(ex.Code == "INVALID_ARGUMENT", $"invalid payload rejected: {json}");
            }
        }
    }

    private static Task<TaskResponse> CallAsync(TodoStore store, string operation, string? id = null, string? payload = null, string? status = null)
    {
        JsonElement? data = null;
        if (payload != null)
        {
            using var document = JsonDocument.Parse(payload);
            data = document.RootElement.Clone();
        }
        return store.ExecuteTaskAsync(new TaskRequest { Operation = operation, Id = id, Payload = data, Status = status });
    }

    private static JsonElement Data(TaskResponse response)
    {
        Check(response.Ok, $"expected success, got {response.Error?.Code}: {response.Error?.Message}");
        return response.Data is JsonElement element ? element : JsonSerializer.SerializeToElement(response.Data, TaskProtocol.Options);
    }

    private static string String(JsonElement value, string name) => value.GetProperty(name).GetString()!;
    private static int Int(JsonElement value, string name) => value.GetProperty(name).GetInt32();
    private static bool Bool(JsonElement value, string name) => value.GetProperty(name).GetBoolean();

    private static void CheckCode(TaskResponse response, string expected, string check)
        => Check(!response.Ok && response.Error?.Code == expected, $"{check}: expected {expected}, got {(response.Ok ? "success" : response.Error?.Code)}");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
