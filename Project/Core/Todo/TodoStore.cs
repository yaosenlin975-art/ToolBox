using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ToolBox.Core.Scheduling;

namespace ToolBox.Core.Todo;

public class TodoStore
{
    private static TodoStore? instance;
    public static TodoStore Instance => instance ??= new TodoStore();

    private readonly string filePath;
    private readonly List<TodoItem> items = [];
    private readonly List<string> categories = ["默认"];
    private readonly SemaphoreSlim writeLock = new(1, 1);
    // 保护 items/categories 的所有读改写操作；Cron 在后台线程触发，须与 UI 线程串行
    private readonly SemaphoreSlim stateLock = new(1, 1);

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

    public async Task AddCategory(string name)
    {
        await stateLock.WaitAsync();
        try
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            var trimmed = name.Trim();
            if (!categories.Contains(trimmed))
            {
                categories.Add(trimmed);
                await SaveAsync();
                NotifyChanged();
            }
        }
        finally { stateLock.Release(); }
    }

    public async Task RemoveCategory(string name)
    {
        if (name == "默认") return;
        await stateLock.WaitAsync();
        try
        {
            categories.Remove(name);
            foreach (var item in items.Where(t => t.Category == name))
                item.Category = "默认";
            await SaveAsync();
            NotifyChanged();
        }
        finally { stateLock.Release(); }
    }

    public async Task<TodoItem> AddAsync(string title, string description = "", int priority = 0, string category = "默认", List<string>? tags = null, DateTime? dueDate = null, string? sessionId = null, string? parentId = null)
    {
        await stateLock.WaitAsync();
        try
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
            if (parentId != null) RecalculateAndAncestorsCore(parentId);
            await SaveAsync();
            NotifyChanged();
            return item;
        }
        finally { stateLock.Release(); }
    }

    public async Task<bool> CompleteAsync(string id)
    {
        await stateLock.WaitAsync();
        try
        {
            var item = items.FirstOrDefault(t => t.Id == id);
            if (item == null) return false;
            item.IsCompleted = true;
            item.CompletedAt = DateTime.UtcNow;
            // 叶子节点勾选完成自动置进度 100；父节点进度由子任务聚合，不在此修改
            if (!HasChildren(id)) item.Progress = 100;
            RecalculateAndAncestorsCore(id);
            await SaveAsync();
            NotifyChanged();
            return true;
        }
        finally { stateLock.Release(); }
    }

    public async Task<bool> UncompleteAsync(string id)
    {
        await stateLock.WaitAsync();
        try
        {
            var item = items.FirstOrDefault(t => t.Id == id);
            if (item == null) return false;
            item.IsCompleted = false;
            item.CompletedAt = null;
            // 取消完成不重置进度（保持当前值，用户可手动调回）
            RecalculateAndAncestorsCore(id);
            await SaveAsync();
            NotifyChanged();
            return true;
        }
        finally { stateLock.Release(); }
    }

    // ─── 进度自动计算 ────────────────────────────────────────────────

    /// <summary>重算指定待办的自动进度：仅对含子任务的父节点生效（叶子节点保持手动设置），递归先更新各子任务自身进度</summary>
    public async Task RecalculateProgress(string todoId)
    {
        await stateLock.WaitAsync();
        try
        {
            if (RecalculateProgressCore(todoId))
            {
                await SaveAsync();
                NotifyChanged();
            }
        }
        finally { stateLock.Release(); }
    }

    /// <summary>递归重算 todoId 的进度（须在 stateLock 内调用）：先对各子任务自身递归重算，再取子任务进度百分比的平均值（四舍五入，已完成子任务按 100 计，兼容历史数据"已完成但进度<100"）；叶子节点不修改，返回是否发生变更</summary>
    private bool RecalculateProgressCore(string todoId)
    {
        var item = items.FirstOrDefault(t => t.Id == todoId);
        if (item == null || item.IsTrashed) return false;
        var children = GetChildren(todoId);
        if (children.Count == 0) return false;

        bool changed = false;
        foreach (var child in children)
            changed |= RecalculateProgressCore(child.Id);

        double avg = children.Average(c => c.IsCompleted ? 100 : c.Progress);
        int progress = (int)Math.Round(avg, MidpointRounding.AwayFromZero);
        if (item.Progress != progress)
        {
            item.Progress = progress;
            changed = true;
        }
        return changed;
    }

    /// <summary>从 id 向上逐级重算所有祖先的进度（须在 stateLock 内调用）；返回是否发生变更</summary>
    private bool RecalculateAncestorsCore(string id)
    {
        var parentId = items.FirstOrDefault(t => t.Id == id)?.ParentId;
        bool changed = false;
        while (parentId != null)
        {
            changed |= RecalculateProgressCore(parentId);
            parentId = items.FirstOrDefault(t => t.Id == parentId)?.ParentId;
        }
        return changed;
    }

    /// <summary>重算 id 自身（若为父节点）及其所有祖先的进度（须在 stateLock 内调用）；返回是否发生变更</summary>
    private bool RecalculateAndAncestorsCore(string id)
    {
        bool changed = RecalculateProgressCore(id);
        var parentId = items.FirstOrDefault(t => t.Id == id)?.ParentId;
        while (parentId != null)
        {
            changed |= RecalculateProgressCore(parentId);
            parentId = items.FirstOrDefault(t => t.Id == parentId)?.ParentId;
        }
        return changed;
    }

    // ─── 重复待办 ────────────────────────────────────────────────

    public event Action<TodoItem, bool>? RepeatReminderTriggered; // (item, wasReset)

    public async Task<TodoItem> AddRepeatingAsync(
        string title,
        ERepeatFrequency frequency,
        string description = "",
        int priority = 0,
        string category = "默认",
        List<string>? tags = null,
        DateTime? dueDate = null,
        DateTime? endDate = null,
        int reminderHour = 9,
        int reminderMinute = 0,
        int? repeatDayOfWeek = null,
        int? repeatDayOfMonth = null,
        int? repeatMonth = null,
        int? repeatDay = null)
    {
        await stateLock.WaitAsync();
        try
        {
            if (!categories.Contains(category)) categories.Add(category);
            DateTime? endDateUtc = endDate?.ToUniversalTime();
            var item = new TodoItem
            {
                Title = title,
                Description = description,
                Priority = priority,
                Category = category,
                Tags = tags ?? [],
                DueDate = dueDate?.ToUniversalTime(),
                RepeatConfig = new RepeatConfig
                {
                    Frequency = frequency,
                    StartDate = DateTime.UtcNow.Date,
                    EndDate = endDateUtc,
                    ReminderHour = reminderHour,
                    ReminderMinute = reminderMinute,
                    RepeatDayOfWeek = repeatDayOfWeek,
                    RepeatDayOfMonth = repeatDayOfMonth,
                    RepeatMonth = repeatMonth,
                    RepeatDay = repeatDay,
                },
                LastResetAt = DateTime.UtcNow,
            };
            items.Add(item);
            await SaveAsync();
            NotifyChanged();
            // 创建成功后注册提醒，保证运行期新建的重复待办到点能重置/刷新
            RegisterSingleRepeatReminder(item);
            return item;
        }
        finally { stateLock.Release(); }
    }

    public List<TodoItem> GetRepeating()
        => items.Where(t => t.RepeatConfig != null && !t.IsTrashed).ToList();

    /// <summary>获取今日应显示的重复待办（仅未完成项，且今天为该待办的发生日，避免每周/每月待办被当作每日任务）</summary>
    public List<TodoItem> GetDueRepeating()
    {
        var now = DateTime.UtcNow;
        return items.Where(t =>
                t.RepeatConfig != null
                && !t.IsTrashed
                && !t.IsCompleted
                && (t.RepeatConfig.EndDate == null || now.Date <= t.RepeatConfig.EndDate.Value.Date)
                && RepeatCalculator.IsOccurrenceDay(now, t.RepeatConfig, t.LastResetAt ?? t.RepeatConfig.StartDate))
            .OrderBy(t => t.DueDate ?? DateTime.MaxValue)
            .ToList();
    }

    public async Task<bool> ResetRepeatingAsync(string id)
    {
        await stateLock.WaitAsync();
        try
        {
            var item = items.FirstOrDefault(t => t.Id == id && t.RepeatConfig != null);
            if (item == null) return false;
            // 已过结束日期：不再重置
            if (item.RepeatConfig!.EndDate != null && DateTime.UtcNow > item.RepeatConfig.EndDate.Value)
                return false;
            item.IsCompleted = false;
            item.CompletedAt = null;
            item.LastResetAt = DateTime.UtcNow;
            await SaveAsync();
            NotifyChanged();
            return true;
        }
        finally { stateLock.Release(); }
    }

    public async Task<int> CatchUpRepeatingAsync()
    {
        await stateLock.WaitAsync();
        try
        {
            var repeating = items.Where(t => t.RepeatConfig != null && !t.IsTrashed).ToList();
            int resetCount = 0;

            foreach (var item in repeating)
            {
                var config = item.RepeatConfig;
                if (config == null) continue;
                // 已过结束日期：不再重置
                if (config.EndDate != null && DateTime.UtcNow > config.EndDate.Value) continue;
                if (!item.IsCompleted) continue;
                var lastReset = item.LastResetAt ?? config.StartDate;
                var nextReset = RepeatCalculator.CalculateNextResetTime(
                    lastReset, config.Frequency, config.ReminderHour, config.ReminderMinute, config);
                if (DateTime.UtcNow >= nextReset)
                {
                    item.IsCompleted = false;
                    item.CompletedAt = null;
                    item.LastResetAt = DateTime.UtcNow;
                    resetCount++;
                    // 重置后重新对齐下一次提醒，避免旧定时器按过期时间误触发
                    RegisterSingleRepeatReminder(item);
                }
            }

            if (resetCount > 0) { await SaveAsync(); NotifyChanged(); }
            return resetCount;
        }
        finally { stateLock.Release(); }
    }

    /// <summary>注册所有活跃重复项的提醒</summary>
    public void RegisterRepeatReminders()
    {
        var repeating = items.Where(t => t.RepeatConfig != null && !t.IsTrashed).ToList();
        foreach (var item in repeating)
        {
            try { RegisterSingleRepeatReminder(item); }
            catch (Exception ex)
            {
                // 单项注册失败不中断其余项
                System.Diagnostics.Debug.WriteLine($"[ToolBox] 注册重复提醒失败 {item.Id}: {ex.Message}");
            }
        }
    }

    /// <summary>注册单个重复项的提醒：以 LastResetAt/StartDate 为锚点计算下一次提醒时间，挂一次性定时器</summary>
    public void RegisterSingleRepeatReminder(TodoItem item)
    {
        var config = item.RepeatConfig;
        if (config == null) return;

        var anchor = item.LastResetAt ?? config.StartDate;
        var next = RepeatCalculator.GetNextOccurrence(
            anchor, config.Frequency, DateTime.UtcNow, config.ReminderHour, config.ReminderMinute, config);

        var cronId = "repeat_" + item.Id;
        CronExpressionr.Instance.Unregister(cronId);

        // 已过结束日期：不再注册后续提醒
        if (config.EndDate != null && next > config.EndDate.Value)
            return;

        var capturedId = item.Id;
        CronExpressionr.Instance.RegisterNext(cronId, next, () =>
        {
            try
            {
                var current = items.FirstOrDefault(t => t.Id == capturedId);
                if (current == null || current.RepeatConfig == null || current.IsTrashed) return;

                // 到期后不再重置，并停止后续提醒
                if (current.RepeatConfig.EndDate != null && DateTime.UtcNow > current.RepeatConfig.EndDate.Value)
                {
                    UnregisterRepeatReminder(capturedId);
                    return;
                }

                if (current.IsCompleted)
                {
                    _ = ResetRepeatingAsync(capturedId);
                    RepeatReminderTriggered?.Invoke(current, true);
                }
                else
                {
                    RepeatReminderTriggered?.Invoke(current, false);
                }

                // 续订下一次提醒（从本次预定触发时刻推进，与 CalculateNextResetTime 同一时间基准）
                RegisterSingleRepeatReminder(current);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ToolBox] 重复提醒回调异常: {ex.Message}");
            }
        });
    }

    public void UnregisterRepeatReminder(string itemId)
        => CronExpressionr.Instance.Unregister("repeat_" + itemId);

    private System.Threading.Timer? periodicCatchUpTimer;
    private static readonly TimeSpan PeriodicCatchUpInterval = TimeSpan.FromMinutes(30);

    /// <summary>启动轻量周期补算：提醒时刻已过但错失重置的已完成项，定期自动补齐（幂等）</summary>
    public void StartPeriodicCatchUp()
    {
        if (periodicCatchUpTimer != null) return;
        periodicCatchUpTimer = new System.Threading.Timer(async _ =>
        {
            try
            {
                var missed = await CatchUpRepeatingAsync();
                if (missed > 0)
                    System.Diagnostics.Debug.WriteLine($"[ToolBox] 周期补算重置了 {missed} 个重复待办");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ToolBox] 周期补算失败: {ex.Message}");
            }
        }, null, PeriodicCatchUpInterval, PeriodicCatchUpInterval);
    }

    /// <summary>设置或取消重复配置（替代直接修改 RepeatConfig 字段）</summary>
    public async Task SetRepeatConfigAsync(string id, RepeatConfig? config)
    {
        await stateLock.WaitAsync();
        try
        {
            var item = items.FirstOrDefault(t => t.Id == id);
            if (item == null) return;
            UnregisterRepeatReminder(id);
            item.RepeatConfig = config;
            item.LastResetAt = config != null ? DateTime.UtcNow : null;
            if (config != null)
                RegisterSingleRepeatReminder(item);
            await SaveAsync();
            NotifyChanged();
        }
        finally { stateLock.Release(); }
    }

    public async Task<bool> DeleteAsync(string id)
    {
        await stateLock.WaitAsync();
        try
        {
            var item = items.FirstOrDefault(t => t.Id == id);
            if (item == null) return false;
            if (item.RepeatConfig != null)
                UnregisterRepeatReminder(id);
            var removed = items.RemoveAll(t => t.Id == id);
            if (removed > 0) { await SaveAsync(); NotifyChanged(); }
            return removed > 0;
        }
        finally { stateLock.Release(); }
    }

    public async Task<bool> UpdateAsync(string id, string? title = null, string? description = null, int? priority = null, string? category = null, List<string>? tags = null, DateTime? dueDate = null, int? progress = null)
    {
        await stateLock.WaitAsync();
        try
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
            // 父节点进度由子任务聚合，禁止手动覆盖；叶子手动进度需向上传播（父级为子进度平均值）
            if (progress.HasValue && !HasChildren(id))
            {
                item.Progress = progress.Value;
                RecalculateAndAncestorsCore(id);
            }
            await SaveAsync();
            NotifyChanged();
            return true;
        }
        finally { stateLock.Release(); }
    }

    // --- Trash system ---
    public async Task<bool> TrashAsync(string id)
    {
        await stateLock.WaitAsync();
        try
        {
            var item = items.FirstOrDefault(t => t.Id == id);
            if (item == null) return false;
            await TrashCoreAsync(id);
            if (RecalculateAncestorsCore(id))
            {
                await SaveAsync();
                NotifyChanged();
            }
            return true;
        }
        finally { stateLock.Release(); }
    }

    async Task<bool> TrashCoreAsync(string id)
    {
        var item = items.FirstOrDefault(t => t.Id == id);
        if (item == null) return false;
        item.IsTrashed = true;
        if (item.RepeatConfig != null)
            UnregisterRepeatReminder(id);
        foreach (var child in items.Where(t => t.ParentId == id).ToList())
        {
            if (child.RepeatConfig != null)
                UnregisterRepeatReminder(child.Id);
            await TrashCoreAsync(child.Id);
        }
        await SaveAsync();
        NotifyChanged();
        return true;
    }

    public async Task<bool> RestoreAsync(string id)
    {
        await stateLock.WaitAsync();
        try
        {
            var item = items.FirstOrDefault(t => t.Id == id);
            if (item == null) return false;
            await RestoreCoreAsync(id);
            if (RecalculateAndAncestorsCore(id))
            {
                await SaveAsync();
                NotifyChanged();
            }
            return true;
        }
        finally { stateLock.Release(); }
    }

    async Task<bool> RestoreCoreAsync(string id)
    {
        var item = items.FirstOrDefault(t => t.Id == id);
        if (item == null) return false;
        item.IsTrashed = false;
        if (item.RepeatConfig != null)
            RegisterSingleRepeatReminder(item);
        foreach (var child in items.Where(t => t.ParentId == id).ToList())
            await RestoreCoreAsync(child.Id);
        await SaveAsync();
        NotifyChanged();
        return true;
    }

    public async Task<bool> DeletePermanentlyAsync(string id)
    {
        await stateLock.WaitAsync();
        try
        {
            // 先记录父级 id：删除后该 item 已不在列表中，无法再向上查找
            var parentId = items.FirstOrDefault(t => t.Id == id)?.ParentId;
            var result = await DeletePermanentlyCoreAsync(id);
            if (parentId != null && RecalculateAndAncestorsCore(parentId))
            {
                await SaveAsync();
                NotifyChanged();
            }
            return result;
        }
        finally { stateLock.Release(); }
    }

    async Task<bool> DeletePermanentlyCoreAsync(string id)
    {
        var children = items.Where(t => t.ParentId == id).ToList();
        foreach (var child in children)
            await DeletePermanentlyCoreAsync(child.Id);
        var removed = items.RemoveAll(t => t.Id == id);
        if (removed > 0) { await SaveAsync(); NotifyChanged(); }
        return removed > 0;
    }

    public List<TodoItem> GetTrashed()
        => items.Where(t => t.IsTrashed && t.ParentId == null)
                .OrderByDescending(t => t.CompletedAt ?? t.CreatedAt)
                .ToList();

    // --- Tree structure ---
    public List<TodoItem> GetChildren(string parentId, bool pendingOnly = false)
    {
        var query = items.Where(t => t.ParentId == parentId && !t.IsTrashed);
        if (pendingOnly) query = query.Where(t => !t.IsCompleted);
        return query.OrderBy(t => t.Priority == 0 ? 3 : t.Priority)
                    .ThenBy(t => t.DueDate ?? DateTime.MaxValue)
                    .ToList();
    }

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

    // Sync wrappers removed — all callers (TodoTools) now use async methods directly.

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
            // 自愈：历史数据可能存在"已完成但进度<100"的叶子（CSH-37 之前完成），
            // 加载后按"已完成=100"的规则重算所有父节点，保证聚合进度与完成状态一致
            foreach (var root in items.Where(t => t.ParentId == null).ToList())
                RecalculateAndAncestorsCore(root.Id);
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
