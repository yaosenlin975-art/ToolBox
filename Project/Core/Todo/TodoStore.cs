using System.IO;
using System.Text.Json;

namespace ToolBox.Core.Todo;

public class TodoStore
{
    private static TodoStore? instance;
    public static TodoStore Instance => instance ??= new TodoStore();

    private readonly string filePath;
    private readonly List<TodoItem> items = [];
    private readonly SemaphoreSlim writeLock = new(1, 1);

    public IReadOnlyList<TodoItem> Items => items;

    public event Action ItemsChanged;

    private void NotifyChanged() => ItemsChanged?.Invoke();

    private TodoStore()
    {
        filePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ToolBox", "todos.json");
        Load();
    }

    public List<TodoItem> GetAll() => items.ToList();
    public List<TodoItem> GetPending() => items.Where(t => !t.IsCompleted).ToList();
    public List<TodoItem> GetCompleted() => items.Where(t => t.IsCompleted).ToList();
    public List<TodoItem> GetByTag(string tag) => items.Where(t => t.Tags.Contains(tag)).ToList();

    public async Task<TodoItem> AddAsync(string title, string description = "", int priority = 0, List<string>? tags = null, DateTime? dueDate = null, string? sessionId = null)
    {
        var item = new TodoItem
        {
            Title = title,
            Description = description,
            Priority = priority,
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

    public async Task<bool> DeleteAsync(string id)
    {
        var removed = items.RemoveAll(t => t.Id == id);
        if (removed > 0) { await SaveAsync(); NotifyChanged(); }
        return removed > 0;
    }

    public async Task<bool> UpdateAsync(string id, string? title = null, string? description = null, int? priority = null, List<string>? tags = null, DateTime? dueDate = null)
    {
        var item = items.FirstOrDefault(t => t.Id == id);
        if (item == null) return false;
        if (title != null) item.Title = title;
        if (description != null) item.Description = description;
        if (priority.HasValue) item.Priority = priority.Value;
        if (tags != null) item.Tags = tags;
        if (dueDate.HasValue) item.DueDate = dueDate;
        await SaveAsync();
        NotifyChanged();
        return true;
    }

    private void Load()
    {
        if (!File.Exists(filePath)) return;
        try
        {
            var json = File.ReadAllText(filePath);
            var data = JsonSerializer.Deserialize<List<TodoItem>>(json);
            if (data != null) items.AddRange(data);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ToolBox] {ex.Message}"); }
    }

    // Sync wrappers for tool execution pipeline and UI handlers.
    // Use Task.Run to avoid deadlock when called from UI thread (WPF SynchronizationContext).
    public TodoItem Add(string title, string description = "", int priority = 0, List<string>? tags = null, DateTime? dueDate = null, string? sessionId = null)
        => Task.Run(() => AddAsync(title, description, priority, tags, dueDate, sessionId)).GetAwaiter().GetResult();

    public bool Complete(string id)
        => Task.Run(() => CompleteAsync(id)).GetAwaiter().GetResult();

    public bool Delete(string id)
        => Task.Run(() => DeleteAsync(id)).GetAwaiter().GetResult();

    public bool Update(string id, string? title = null, string? description = null, int? priority = null, List<string>? tags = null, DateTime? dueDate = null)
        => Task.Run(() => UpdateAsync(id, title, description, priority, tags, dueDate)).GetAwaiter().GetResult();
    private async Task SaveAsync()
    {
        await writeLock.WaitAsync();
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(filePath, json);
        }
        finally { writeLock.Release(); }
    }
}