using System.IO;
using System.Text.Json;
using ToolBox.Core.Scheduling;

namespace ToolBox.Core.Reminder;

/// <summary>
/// 定时提醒持久化存储，模式与 TodoStore 一致：
/// JSON 数组存于 %AppData%/ToolBox/reminders.json，启动加载、变更自动保存。
/// </summary>
public class ReminderStore
{
    private static ReminderStore? instance;
    public static ReminderStore Instance => instance ??= new ReminderStore();

    private readonly string filePath;
    private readonly List<ReminderItem> items = [];
    private readonly SemaphoreSlim writeLock = new(1, 1);
    // 保护 items 的所有读改写操作；Cron 在后台线程触发，须与 UI 线程串行
    private readonly SemaphoreSlim stateLock = new(1, 1);

    public IReadOnlyList<ReminderItem> Items => items;

    public event Action? ItemsChanged;

    private void NotifyChanged() => ItemsChanged?.Invoke();

    private ReminderStore()
    {
        filePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ToolBox", "reminders.json");
        Load();
    }

    public List<ReminderItem> GetAll() => items.ToList();

    public async Task<ReminderItem> AddAsync(ReminderItem item)
    {
        await stateLock.WaitAsync();
        try
        {
            items.Add(item);
            await SaveAsync();
            NotifyChanged();
            return item;
        }
        finally { stateLock.Release(); }
    }

    public async Task<bool> UpdateAsync(ReminderItem item)
    {
        await stateLock.WaitAsync();
        try
        {
            var index = items.FindIndex(t => t.Id == item.Id);
            if (index < 0) return false;
            items[index] = item;
            await SaveAsync();
            NotifyChanged();
            return true;
        }
        finally { stateLock.Release(); }
    }

    public async Task<bool> SetEnabledAsync(string id, bool enabled)
    {
        await stateLock.WaitAsync();
        try
        {
            var item = items.FirstOrDefault(t => t.Id == id);
            if (item == null) return false;
            item.IsEnabled = enabled;
            await SaveAsync();
            NotifyChanged();
            return true;
        }
        finally { stateLock.Release(); }
    }

    public async Task<bool> DeleteAsync(string id)
    {
        await stateLock.WaitAsync();
        try
        {
            var removed = items.RemoveAll(t => t.Id == id);
            if (removed > 0) { await SaveAsync(); NotifyChanged(); }
            return removed > 0;
        }
        finally { stateLock.Release(); }
    }

    /// <summary>记录触发时间（LastTriggeredAt），一次性提醒触发后自动禁用并保存。</summary>
    public async Task MarkTriggeredAsync(string id, bool autoDisable)
    {
        await stateLock.WaitAsync();
        try
        {
            var item = items.FirstOrDefault(t => t.Id == id);
            if (item == null) return;
            item.LastTriggeredAt = DateTime.UtcNow;
            if (autoDisable) item.IsEnabled = false;
            await SaveAsync();
            NotifyChanged();
        }
        finally { stateLock.Release(); }
    }

    private void Load()
    {
        if (!File.Exists(filePath)) return;
        try
        {
            var json = File.ReadAllText(filePath);
            var data = JsonSerializer.Deserialize<List<ReminderItem>>(json);
            if (data != null)
            {
                items.Clear();
                items.AddRange(data);
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ToolBox] 加载提醒失败: {ex.Message}"); }
    }

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
