using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ToolBox.Core.Todo;

public class TodoStore
{
    private static TodoStore? instance;
    public static TodoStore Instance => instance ??= new TodoStore();

    private readonly string filePath;
    private readonly List<TodoItem> items = [];
    private readonly List<string> categories = ["默认"];
    private readonly SemaphoreSlim writeLock = new(1, 1);

    public IReadOnlyList<TodoItem> Items => items;
    public IReadOnlyList<string> Categories => categories;

    public event Action? ItemsChanged;

    private void NotifyChanged() => ItemsChanged?.Invoke();

    private TodoStore()
    {
        filePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ToolBox", "todos.json");
        Load();
    }

    public List<TodoItem> GetAll() => items.Where(t => !t.IsTrashed).ToList();
    public List<TodoItem> GetPending() => items.Where(t => !t.IsCompleted && !t.IsTrashed).ToList();
    public List<TodoItem> GetCompleted() => items.Where(t => t.IsCompleted && !t.IsTrashed).ToList();
    public List<TodoItem> GetByTag(string tag) => items.Where(t => t.Tags.Contains(tag) && !t.IsTrashed).ToList();

    public List<TodoItem> GetByCategory(string category, bool pendingOnly = false)
    {
        var query = items.Where(t => t.Category == category && !t.IsTrashed);
        if (pendingOnly) query = query.Where(t => !t.IsCompleted);
        return query.OrderBy(t => t.Priority == 0 ? 3 : t.Priority)
                     .ThenBy(t => t.DueDate ?? DateTime.MaxValue)
                     .ToList();
    }

    public Dictionary<string, List<TodoItem>> GetGrouped(bool pendingOnly = false)
    {
        var result = new Dictionary<string, List<TodoItem>>();
        foreach (var cat in categories)
        {
            var catItems = GetByCategory(cat, pendingOnly);
            if (catItems.Count > 0)
                result[cat] = catItems;
        }
        foreach (var cat in items.Select(t => t.Category).Distinct())
        {
            if (!result.ContainsKey(cat))
            {
                var catItems = GetByCategory(cat, pendingOnly);
                if (catItems.Count > 0) result[cat] = catItems;
            }
        }
        return result;
    }

    public void AddCategory(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var trimmed = name.Trim();
        if (!categories.Contains(trimmed))
        {
            categories.Add(trimmed);
            _ = SaveAsync();
            NotifyChanged();
        }
    }

    public void RemoveCategory(string name)
    {
        if (name == "默认") return;
        categories.Remove(name);
        foreach (var item in items.Where(t => t.Category == name))
            item.Category = "默认";
        _ = SaveAsync();
        NotifyChanged();
    }

    public async Task<TodoItem> AddAsync(string title, string description = "", int priority = 0, string category = "默认", List<string>? tags = null, DateTime? dueDate = null, string? sessionId = null, string? parentId = null)
    {
        if (!categories.Contains(category)) categories.Add(category);
        var item = new TodoItem
        {
            ParentId = parentId,
            Title = title,
            Description = description,
            Priority = priority,
            Category = category,
            Tags = tags ?? [],
            DueDate = dueDate,
            SessionId = sessionId
        };
        items.Add(item);
        await SaveAsync();
        NotifyChanged();
        return item;
    }

    public async Task<bool> CompleteAsync(string id)
    {
        var item = items.FirstOrDefault(t => t.Id == id);
        if (item == null) return false;
        item.IsCompleted = true;
        item.CompletedAt = DateTime.UtcNow;
        await SaveAsync();
        NotifyChanged();
        return true;
    }

    public async Task<bool> UncompleteAsync(string id)
    {
        var item = items.FirstOrDefault(t => t.Id == id);
        if (item == null) return false;
        item.IsCompleted = false;
        item.CompletedAt = null;
        await SaveAsync();
        NotifyChanged();
        return true;
    }

    public async Task<bool> DeleteAsync(string id)
    {
        var removed = items.RemoveAll(t => t.Id == id);
        if (removed > 0) { await SaveAsync(); NotifyChanged(); }
        return removed > 0;
    }

    public async Task<bool> UpdateAsync(string id, string? title = null, string? description = null, int? priority = null, string? category = null, List<string>? tags = null, DateTime? dueDate = null, int? progress = null)
    {
        var item = items.FirstOrDefault(t => t.Id == id);
        if (item == null) return false;
        if (title != null) item.Title = title;
        if (description != null) item.Description = description;
        if (priority.HasValue) item.Priority = priority.Value;
        if (category != null)
        {
            if (!categories.Contains(category)) categories.Add(category);
            item.Category = category;
        }
        if (tags != null) item.Tags = tags;
        if (dueDate.HasValue) item.DueDate = dueDate;
        if (progress.HasValue) item.Progress = progress.Value;
        await SaveAsync();
        NotifyChanged();
        return true;
    }

    // --- Trash system ---
    public async Task<bool> TrashAsync(string id)
    {
        var item = items.FirstOrDefault(t => t.Id == id);
        if (item == null) return false;
        item.IsTrashed = true;
        foreach (var child in items.Where(t => t.ParentId == id).ToList())
            await TrashAsync(child.Id);
        await SaveAsync();
        NotifyChanged();
        return true;
    }

    public async Task<bool> RestoreAsync(string id)
    {
        var item = items.FirstOrDefault(t => t.Id == id);
        if (item == null) return false;
        item.IsTrashed = false;
        foreach (var child in items.Where(t => t.ParentId == id).ToList())
            await RestoreAsync(child.Id);
        await SaveAsync();
        NotifyChanged();
        return true;
    }

    public async Task<bool> DeletePermanentlyAsync(string id)
    {
        var children = items.Where(t => t.ParentId == id).ToList();
        foreach (var child in children)
            await DeletePermanentlyAsync(child.Id);
        var removed = items.RemoveAll(t => t.Id == id);
        if (removed > 0) { await SaveAsync(); NotifyChanged(); }
        return removed > 0;
    }

    public List<TodoItem> GetTrashed()
        => items.Where(t => t.IsTrashed && t.ParentId == null)
                .OrderByDescending(t => t.CompletedAt ?? t.CreatedAt)
                .ToList();

    public bool Trash(string id) => Task.Run(() => TrashAsync(id)).GetAwaiter().GetResult();
    public bool Restore(string id) => Task.Run(() => RestoreAsync(id)).GetAwaiter().GetResult();
    public bool DeletePermanently(string id) => Task.Run(() => DeletePermanentlyAsync(id)).GetAwaiter().GetResult();

    // --- Tree structure ---
    public List<TodoItem> GetChildren(string parentId)
        => items.Where(t => t.ParentId == parentId && !t.IsTrashed)
                .OrderBy(t => t.Priority == 0 ? 3 : t.Priority)
                .ThenBy(t => t.DueDate ?? DateTime.MaxValue)
                .ToList();

    public List<TodoItem> GetRoots(string category, bool pendingOnly = false)
    {
        var query = items.Where(t => t.Category == category && t.ParentId == null && !t.IsTrashed);
        if (pendingOnly) query = query.Where(t => !t.IsCompleted);
        return query.OrderBy(t => t.Priority == 0 ? 3 : t.Priority)
                     .ThenBy(t => t.DueDate ?? DateTime.MaxValue)
                     .ToList();
    }

    public Dictionary<string, List<TodoItem>> GetGroupedTree(bool pendingOnly = false)
    {
        var result = new Dictionary<string, List<TodoItem>>();
        foreach (var cat in categories)
        {
            var roots = GetRoots(cat, pendingOnly);
            if (roots.Count > 0)
                result[cat] = roots;
        }
        foreach (var cat in items.Select(t => t.Category).Distinct())
        {
            if (!result.ContainsKey(cat))
            {
                var roots = GetRoots(cat, pendingOnly);
                if (roots.Count > 0) result[cat] = roots;
            }
        }
        return result;
    }

    public bool HasChildren(string id) => items.Any(t => t.ParentId == id && !t.IsTrashed);

    // Sync wrappers
    public TodoItem Add(string title, string description = "", int priority = 0, string category = "默认", List<string>? tags = null, DateTime? dueDate = null, string? sessionId = null, string? parentId = null)
        => Task.Run(() => AddAsync(title, description, priority, category, tags, dueDate, sessionId, parentId)).GetAwaiter().GetResult();
    public bool Complete(string id) => Task.Run(() => CompleteAsync(id)).GetAwaiter().GetResult();
    public bool Uncomplete(string id) => Task.Run(() => UncompleteAsync(id)).GetAwaiter().GetResult();
    public bool Delete(string id) => Task.Run(() => DeleteAsync(id)).GetAwaiter().GetResult();
    public bool Update(string id, string? title = null, string? description = null, int? priority = null, string? category = null, List<string>? tags = null, DateTime? dueDate = null)
        => Task.Run(() => UpdateAsync(id, title, description, priority, category, tags, dueDate)).GetAwaiter().GetResult();

    private void Load()
    {
        if (!File.Exists(filePath)) return;
        try
        {
            var json = File.ReadAllText(filePath);
            var data = JsonSerializer.Deserialize<TodoData>(json);
            if (data != null)
            {
                items.AddRange(data.Items);
                if (data.Categories != null && data.Categories.Count > 0)
                {
                    categories.Clear();
                    categories.AddRange(data.Categories);
                }
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ToolBox] {ex.Message}"); }
    }

    private async Task SaveAsync()
    {
        await writeLock.WaitAsync();
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var data = new TodoData { Categories = categories.ToList(), Items = items.ToList() };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(filePath, json);
        }
        finally { writeLock.Release(); }
    }
}

internal class TodoData
{
    [JsonPropertyName("categories")]
    public List<string> Categories { get; set; } = [];
    [JsonPropertyName("items")]
    public List<TodoItem> Items { get; set; } = [];
}
