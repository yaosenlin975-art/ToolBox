using System.Net.Http;
using System.Text.Json;

namespace ToolBox.Core.Tools;

public static class WebSearchTools
{
    private static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };

    [Tool("web_search", "网络搜索，返回相关结果摘要（使用 DuckDuckGo Instant Answer API）")]
    public static string WebSearch(
        [ToolParam("搜索关键词")] string query,
        [ToolParam("返回结果数量，默认5")] int count = 5)
    {
        if (count <= 0) count = 5;
        if (count > 20) count = 20;
        try
        {
            var q = System.Uri.EscapeDataString(query);
            var url = "https://api.duckduckgo.com/?q=" + q + "&format=json&no_html=1&skip_disambig=1&t=ToolBox";
            var json = http.GetStringAsync(url).GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("搜索: " + query);
            var root = doc.RootElement;
            if (root.TryGetProperty("AbstractText", out var abs) && !string.IsNullOrWhiteSpace(abs.GetString()))
                sb.AppendLine("摘要: " + abs.GetString());
            if (root.TryGetProperty("AbstractURL", out var absUrl) && !string.IsNullOrWhiteSpace(absUrl.GetString()))
                sb.AppendLine("来源: " + absUrl.GetString());

            int shown = 0;
            if (root.TryGetProperty("RelatedTopics", out var topics) && topics.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in topics.EnumerateArray())
                {
                    if (shown >= count) break;
                    if (t.TryGetProperty("Text", out var txt) && !string.IsNullOrWhiteSpace(txt.GetString()))
                    {
                        sb.AppendLine("- " + txt.GetString());
                        if (t.TryGetProperty("FirstURL", out var u)) sb.AppendLine("  " + u.GetString());
                        shown++;
                    }
                    else if (t.TryGetProperty("Topics", out var sub) && sub.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var st in sub.EnumerateArray())
                        {
                            if (shown >= count) break;
                            if (st.TryGetProperty("Text", out var stxt) && !string.IsNullOrWhiteSpace(stxt.GetString()))
                            {
                                sb.AppendLine("- " + stxt.GetString());
                                shown++;
                            }
                        }
                    }
                }
            }
            if (shown == 0 && (root.TryGetProperty("AbstractText", out var a2) == false || string.IsNullOrWhiteSpace(a2.GetString())))
                sb.AppendLine("(无即时答案，可尝试更具体的关键词)");
            return sb.ToString();
        }
        catch (System.Exception ex)
        {
            return "搜索失败: " + ex.Message;
        }
    }
}
