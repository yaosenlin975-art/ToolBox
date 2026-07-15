using System.Text.Json;

namespace ToolBox.Core.Tools;

public static class ScreenshotTools
{
    [Tool("search_screenshots", "按标签、评分、颜色搜索截图")]
    public static string SearchScreenshots(
        [ToolParam("标签名(可选)")] string tag = "",
        [ToolParam("最低评分(1-5,可选)")] int minRating = 0,
        [ToolParam("色调(0-360,可选)")] int colorHue = -1)
    {
        var store = Tags.ScreenshotTagStore.Instance;
        var results = store.Search(
            tag: string.IsNullOrEmpty(tag) ? null : tag,
            minRating: minRating > 0 ? minRating : null,
            colorHue: colorHue >= 0 ? colorHue : null);

        var output = results.Select(e => new
        {
            cacheKey = e.CacheKey,
            tags = e.Tags,
            rating = e.Rating,
            notes = e.Notes,
            taggedAt = e.TaggedAt
        }).ToList();

        return JsonSerializer.Serialize(new { success = true, count = output.Count, results = output });
    }

    [Tool("tag_screenshot", "为截图添加标签")]
    public static string TagScreenshot(
        [ToolParam("截图缓存键")] string cacheKey,
        [ToolParam("标签名")] string tag)
    {
        if (string.IsNullOrEmpty(cacheKey) || string.IsNullOrEmpty(tag))
            return JsonSerializer.Serialize(new { success = false, error = "缺少 cacheKey 或 tag" });

        Tags.ScreenshotTagStore.Instance.AddTag(cacheKey, tag);
        return JsonSerializer.Serialize(new { success = true, cacheKey, tag });
    }
}
