using System.IO;
using System.Text.Json;
using ToolBox.Core.Tags;

namespace ToolBox.Core.Todo;

public static class AgentScreenshotCommand
{
    private const string OperationName = "screenshot.search";

    public static TaskResponse Execute(TaskRequest request)
    {
        try
        {
            if (request.Operation != OperationName)
                throw new TaskCommandException("PROTOCOL_ERROR", "Unsupported screenshot operation.", "operation");

            TaskProtocol.Validate(request);
            var filter = ParsePayload(request.Payload!.Value);
            var store = ScreenshotTagStore.Instance;
            if (store.LoadFailureCode is { } loadFailure)
                throw new TaskCommandException(loadFailure, LoadFailureMessage(loadFailure));

            var publicKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in store.Search())
                if (!publicKeys.Add(ToPublicCacheKey(entry.CacheKey)))
                    throw CorruptData();

            var items = store.Search(filter.Tag, filter.MinRating, filter.ColorHue)
                .Select(entry => new ScreenshotTagItem(
                    ToPublicCacheKey(entry.CacheKey), entry.Tags.ToArray(), entry.Rating, entry.Notes, entry.TaggedAt))
                .Where(item => filter.Cursor == null || StringComparer.Ordinal.Compare(item.CacheKey, filter.Cursor) > 0)
                .OrderBy(item => item.CacheKey, StringComparer.Ordinal)
                .Take(filter.Limit + 1)
                .ToArray();

            bool hasMore = items.Length > filter.Limit;
            var page = items.Take(filter.Limit).ToArray();
            return TaskResponse.Success(request, new
            {
                items = page,
                nextCursor = hasMore ? page[^1].CacheKey : null
            });
        }
        catch (TaskCommandException ex)
        {
            return TaskResponse.Failure(request, ex.Code, ex.Message, ex.Field);
        }
        catch (UnauthorizedAccessException)
        {
            return TaskResponse.Failure(request, "ACCESS_DENIED", "Screenshot tag data could not be accessed.");
        }
        catch (IOException)
        {
            return TaskResponse.Failure(request, "IO_ERROR", "Screenshot tag data could not be accessed.");
        }
        catch (Exception)
        {
            return TaskResponse.Failure(request, "INTERNAL_ERROR", "The screenshot query failed.");
        }
    }

    private static ScreenshotFilter ParsePayload(JsonElement payload) => new(
        payload.TryGetProperty("tag", out var tag) ? tag.GetString() : null,
        payload.TryGetProperty("minRating", out var rating) ? rating.GetInt32() : null,
        payload.TryGetProperty("colorHue", out var hue) ? hue.GetInt32() : null,
        payload.TryGetProperty("limit", out var limit) ? limit.GetInt32() : 20,
        payload.TryGetProperty("cursor", out var cursor) ? cursor.GetString() : null);
    private static string ToPublicCacheKey(string cacheKey)
    {
        int separator = cacheKey.LastIndexOfAny(['\\', '/']);
        string publicKey = separator >= 0 ? cacheKey[(separator + 1)..] : cacheKey;
        if (!IsTimestampKey(publicKey)) throw CorruptData();
        return publicKey;
    }

    private static bool IsTimestampKey(string? value) =>
        value is { Length: 17 } && value.All(character => character is >= '0' and <= '9');

    private static TaskCommandException CorruptData() =>
        new("DATA_CORRUPT", "Screenshot tag data contains an invalid cache key.");

    private static string LoadFailureMessage(string code) => code switch
    {
        "DATA_CORRUPT" => "Screenshot tag data is corrupt.",
        "ACCESS_DENIED" => "Screenshot tag data could not be accessed.",
        _ => "Screenshot tag data could not be read."
    };

    private sealed record ScreenshotFilter(string? Tag, int? MinRating, int? ColorHue, int Limit, string? Cursor);
    private sealed record ScreenshotTagItem(string CacheKey, string[] Tags, int Rating, string Notes, DateTime TaggedAt);
}
