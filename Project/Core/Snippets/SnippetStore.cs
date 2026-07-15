using System.IO;
using System.Text.Json;

namespace ToolBox.Core.Snippets;

/// <summary>代码片段数据模型</summary>
public class SnippetItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? Trigger { get; set; }
    public string Language { get; set; } = "plaintext";
    public string Category { get; set; } = "默认";
    public bool IsFavorite { get; set; }
    public int UseCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>片段持久化存储单例</summary>
public class SnippetStore
{
    private static SnippetStore? _instance;
    public static SnippetStore Instance => _instance ??= new SnippetStore();

    private readonly string _filePath;
    private List<SnippetItem> _items = new();

    public event Action? ItemsChanged;

    private SnippetStore()
    {
        _filePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ToolBox", "snippets.json");
        Load();
    }

    public IReadOnlyList<SnippetItem> Items => _items;

    public IReadOnlyList<string> Categories =>
        _items.Select(i => i.Category).Distinct().Order().ToList();

    public SnippetStore Add(SnippetItem item)
    {
        item.CreatedAt = DateTime.UtcNow;
        item.UpdatedAt = DateTime.UtcNow;
        _items.Add(item);
        Save();
        ItemsChanged?.Invoke();
        return this;
    }

    public SnippetStore Update(SnippetItem item)
    {
        var idx = _items.FindIndex(i => i.Id == item.Id);
        if (idx >= 0)
        {
            item.UpdatedAt = DateTime.UtcNow;
            _items[idx] = item;
            Save();
            ItemsChanged?.Invoke();
        }
        return this;
    }

    public SnippetStore Delete(string snippetId)
    {
        _items.RemoveAll(i => i.Id == snippetId);
        Save();
        ItemsChanged?.Invoke();
        return this;
    }

    public SnippetStore IncrementUseCount(string snippetId)
    {
        var item = _items.FirstOrDefault(i => i.Id == snippetId);
        if (item != null)
        {
            item.UseCount++;
            Save();
        }
        return this;
    }

    public SnippetItem? FindByTrigger(string trigger)
    {
        return _items.FirstOrDefault(i =>
            i.Trigger != null && trigger.EndsWith(i.Trigger, StringComparison.OrdinalIgnoreCase));
    }

    public List<SnippetItem> Search(string keyword, string? category = null)
    {
        return _items.Where(i =>
        {
            if (category != null && i.Category != category) return false;
            if (string.IsNullOrEmpty(keyword)) return true;
            return i.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                   (i.Trigger != null && i.Trigger.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
                   i.Content.Contains(keyword, StringComparison.OrdinalIgnoreCase);
        }).ToList();
    }

    public List<SnippetItem> FilterByCategory(string category)
    {
        return _items.Where(i => i.Category == category).ToList();
    }

    /// <summary>展开变量占位符 ${1:placeholder}</summary>
    public static string ExpandPlaceholders(string content)
    {
        // 移除 ${n:...} 标记，保留占位文本
        return System.Text.RegularExpressions.Regex.Replace(
            content, @"\$\{(\d+):([^}]*)\}", m => m.Groups[2].Value);
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                var list = JsonSerializer.Deserialize<List<SnippetItem>>(json);
                if (list != null) _items = list;
            }
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_items, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch { }
    }
}
