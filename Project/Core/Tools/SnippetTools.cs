using System.Text.Json;

namespace ToolBox.Core.Tools;

public static class SnippetTools
{
    [Tool("search_snippets", "搜索代码片段库")]
    public static string SearchSnippets(
        [ToolParam("搜索关键词")] string keyword = "",
        [ToolParam("分类筛选")] string category = "")
    {
        var store = Snippets.SnippetStore.Instance;
        var results = store.Search(keyword, string.IsNullOrEmpty(category) ? null : category);
        var output = results.Select(s => new
        {
            id = s.Id, name = s.Name, trigger = s.Trigger,
            language = s.Language, category = s.Category,
            useCount = s.UseCount, preview = s.Content.Length > 100 ? s.Content[..100] + "..." : s.Content
        }).ToList();
        return JsonSerializer.Serialize(new { success = true, count = output.Count, results = output });
    }

    [Tool("get_snippet", "获取指定代码片段的完整内容")]
    public static string GetSnippet([ToolParam("片段名称或触发关键字")] string nameOrTrigger)
    {
        var store = Snippets.SnippetStore.Instance;
        var item = store.Items.FirstOrDefault(s =>
            s.Name.Equals(nameOrTrigger, StringComparison.OrdinalIgnoreCase) ||
            (s.Trigger != null && s.Trigger.Equals(nameOrTrigger, StringComparison.OrdinalIgnoreCase)));

        if (item == null)
            return JsonSerializer.Serialize(new { success = false, error = $"未找到片段: {nameOrTrigger}" });

        store.IncrementUseCount(item.Id);
        return JsonSerializer.Serialize(new
        {
            success = true, id = item.Id, name = item.Name,
            content = item.Content, trigger = item.Trigger,
            language = item.Language, category = item.Category
        });
    }

    [Tool("add_snippet", "保存新的代码片段")]
    public static string AddSnippet(
        [ToolParam("片段名称")] string name,
        [ToolParam("片段内容")] string content,
        [ToolParam("触发关键字(如 ;sig)")] string trigger = "",
        [ToolParam("分类")] string category = "默认")
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(content))
            return JsonSerializer.Serialize(new { success = false, error = "名称和内容不能为空" });

        var item = new Snippets.SnippetItem
        {
            Name = name, Content = content,
            Trigger = string.IsNullOrEmpty(trigger) ? null : trigger,
            Category = string.IsNullOrEmpty(category) ? "默认" : category
        };
        Snippets.SnippetStore.Instance.Add(item);
        return JsonSerializer.Serialize(new { success = true, id = item.Id, name = item.Name });
    }
}
