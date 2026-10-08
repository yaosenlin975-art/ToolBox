using System.Reflection;
using System.Text.Json;
using ToolBox.Core.Tags;
using ToolBox.Core.Todo;

string root = Path.Combine(Path.GetTempPath(), "ToolBox.ScreenshotTagChecks", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("TOOLBOX_TASK_TEST_ROOT", root);
try
{
    string path = Path.Combine(root, "screenshot_tags.json");
    var entries = new[]
    {
        Entry(Path.Combine(root, "20261008120000001"), ["keep"], 3, unchecked((int)0xFFFF0000), "first"),
        Entry("20261008120000002", ["keep"], 4, unchecked((int)0xFF0000FF), "blue"),
        Entry("20261008120000003", ["skip"], 5, unchecked((int)0xFFFFFF00), "skip"),
        Entry(Path.Combine(root, "20261008120000004"), ["keep"], 5, unchecked((int)0xFFFF0000), "last")
    };
    File.WriteAllText(path, JsonSerializer.Serialize(entries));

    var first = Query("""{"tag":"keep","minRating":3,"colorHue":0,"limit":1}""");
    Check(first.Ok, "filtered first page succeeds");
    JsonElement firstData = Data(first);
    JsonElement firstItem = firstData.GetProperty("items")[0];
    const string firstKey = "20261008120000001";
    Check(firstItem.GetProperty("cacheKey").GetString() == firstKey, "absolute cache path is reduced to timestamp key");
    Check(firstItem.GetProperty("notes").GetString() == "first", "notes snapshot returned");
    Check(firstData.GetProperty("nextCursor").GetString() == firstKey, "next cursor is last returned key");
    Check(!JsonSerializer.Serialize(first, TaskProtocol.Options).Contains(root, StringComparison.OrdinalIgnoreCase), "response excludes filesystem path");

    var firstStoredEntry = ScreenshotTagStore.Instance.Get(Path.Combine(root, firstKey))!;
    firstStoredEntry.Notes = "changed";
    firstStoredEntry.Tags[0] = "changed";
    firstItem = Data(first).GetProperty("items")[0];
    Check(firstItem.GetProperty("notes").GetString() == "first", "notes are a snapshot");
    Check(firstItem.GetProperty("tags")[0].GetString() == "keep", "tags are a snapshot");

    var second = Query($"{{\"tag\":\"keep\",\"minRating\":3,\"colorHue\":0,\"limit\":1,\"cursor\":\"{firstKey}\"}}");
    Check(second.Ok, "filtered second page succeeds");
    JsonElement secondData = Data(second);
    Check(secondData.GetProperty("items").GetArrayLength() == 1, "second page has one item");
    Check(secondData.GetProperty("items")[0].GetProperty("cacheKey").GetString() == "20261008120000004", "cursor advances without repeats");
    Check(secondData.GetProperty("nextCursor").ValueKind == JsonValueKind.Null, "last page has no cursor");

    ResetSingleton();
    string missingRoot = Path.Combine(root, "missing");
    Environment.SetEnvironmentVariable("TOOLBOX_TASK_TEST_ROOT", missingRoot);
    var missing = Query("{}");
    Check(missing.Ok && Data(missing).GetProperty("items").GetArrayLength() == 0, "missing storage returns an empty result");

    ResetSingleton();
    string corruptRoot = Path.Combine(root, "corrupt");
    Directory.CreateDirectory(corruptRoot);
    Environment.SetEnvironmentVariable("TOOLBOX_TASK_TEST_ROOT", corruptRoot);
    File.WriteAllText(Path.Combine(corruptRoot, "screenshot_tags.json"), "{not-json");
    CheckCode(Query("{}"), "DATA_CORRUPT", "invalid JSON is reported");

    ResetSingleton();
    string nullCollectionRoot = Path.Combine(root, "null-collection");
    Directory.CreateDirectory(nullCollectionRoot);
    Environment.SetEnvironmentVariable("TOOLBOX_TASK_TEST_ROOT", nullCollectionRoot);
    File.WriteAllText(Path.Combine(nullCollectionRoot, "screenshot_tags.json"), "[{\"CacheKey\":\"20261008120000001\",\"Tags\":null}]");
    CheckCode(Query("{}"), "DATA_CORRUPT", "null collections are reported");

    foreach (string invalid in new[]
    {
        "[{\"CacheKey\":\"20261008120000001\",\"Tags\":[null]}]",
        "[{\"CacheKey\":\"20261008120000001\",\"Rating\":6}]"
    })
    {
        ResetSingleton();
        File.WriteAllText(Path.Combine(nullCollectionRoot, "screenshot_tags.json"), invalid);
        CheckCode(Query("{}"), "DATA_CORRUPT", "invalid tag elements and ratings are reported");
    }

    ResetSingleton();
    string duplicatePublicKeyRoot = Path.Combine(root, "duplicate-public-key");
    Directory.CreateDirectory(duplicatePublicKeyRoot);
    Environment.SetEnvironmentVariable("TOOLBOX_TASK_TEST_ROOT", duplicatePublicKeyRoot);
    File.WriteAllText(Path.Combine(duplicatePublicKeyRoot, "screenshot_tags.json"), JsonSerializer.Serialize(new[]
    {
        Entry(Path.Combine("C:\\old", "20261008120000001"), ["a"], 1, 0, "a"),
        Entry(Path.Combine("D:\\old", "20261008120000001"), ["b"], 2, 0, "b")
    }));
    CheckCode(Query("{}"), "DATA_CORRUPT", "ambiguous public keys are reported");

    ResetSingleton();
    string duplicateKeyRoot = Path.Combine(root, "duplicate-key");
    Directory.CreateDirectory(duplicateKeyRoot);
    Environment.SetEnvironmentVariable("TOOLBOX_TASK_TEST_ROOT", duplicateKeyRoot);
    File.WriteAllText(Path.Combine(duplicateKeyRoot, "screenshot_tags.json"), JsonSerializer.Serialize(new[]
    {
        Entry("20261008120000001", ["a"], 1, 0, "a"),
        Entry("20261008120000001", ["b"], 2, 0, "b")
    }));
    CheckCode(Query("{}"), "DATA_CORRUPT", "duplicate stored keys are reported");

    Console.WriteLine("Screenshot tag checks passed.");
}
finally
{
    Environment.SetEnvironmentVariable("TOOLBOX_TASK_TEST_ROOT", null);
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}

static ScreenshotTagEntry Entry(string key, List<string> tags, int rating, int color, string notes) => new()
{
    CacheKey = key,
    Tags = tags,
    Rating = rating,
    DominantColors = [color],
    Notes = notes,
    TaggedAt = DateTime.UnixEpoch
};

static TaskResponse Query(string payload)
{
    using var document = JsonDocument.Parse(payload);
    return AgentScreenshotCommand.Execute(new TaskRequest
    {
        Operation = "screenshot.search",
        Payload = document.RootElement.Clone()
    });
}

static JsonElement Data(TaskResponse response)
{
    Check(response.Ok, $"expected success, got {response.Error?.Code}");
    return JsonSerializer.SerializeToElement(response.Data, TaskProtocol.Options);
}

static void CheckCode(TaskResponse response, string code, string label) =>
    Check(!response.Ok && response.Error?.Code == code, $"{label}: expected {code}, got {(response.Ok ? "success" : response.Error?.Code)}");

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void ResetSingleton() =>
    typeof(ScreenshotTagStore).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, null);
