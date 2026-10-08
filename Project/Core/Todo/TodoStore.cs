using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ToolBox.Core.Scheduling;
using System.Text;

namespace ToolBox.Core.Todo;

public partial class TodoStore
{
    private static TodoStore? instance;
    public static TodoStore Instance => instance ??= new TodoStore();

    private readonly string filePath;
    private readonly List<TodoItem> items = [];
    private readonly List<string> categories = ["默认"];
    private readonly SemaphoreSlim writeLock = new(1, 1);
    // 保护 items/categories 的所有读改写操作；Cron 在后台线程触发，须与 UI 线程串行
    private readonly SemaphoreSlim stateLock = new(1, 1);
    private StoreSnapshot? committedSnapshot;
    private string? storageFailureCode;
    private string? storageFailureMessage;

    public IReadOnlyList<TodoItem> Items => items;
    public IReadOnlyList<string> Categories => categories;

    public event Action? ItemsChanged;

    private void NotifyChanged()
    {
        try { ItemsChanged?.Invoke(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ToolBox] 待办变更通知失败: {ex.Message}"); }
    }

    private TodoStore() : this(GetDefaultFilePath()) { }

    internal TodoStore(string path)
    {
        filePath = Path.GetFullPath(path);
        Load();
        committedSnapshot = CaptureSnapshot();
    }

    private static string GetDefaultFilePath()
    {
#if DEBUG
        var testRoot = Environment.GetEnvironmentVariable("TOOLBOX_TASK_TEST_ROOT");
        if (!string.IsNullOrWhiteSpace(testRoot))
            return Path.Combine(Path.GetFullPath(testRoot), "todos.json");
#endif
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ToolBox", "todos.json");
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
            var item = AddCore(title, description, priority, category, tags, dueDate, sessionId, parentId);
            await SaveAsync();
            NotifyChanged();
            return item;
        }
        finally { stateLock.Release(); }
    }

    private TodoItem AddCore(string title, string description = "", int priority = 0, string category = "默认", List<string>? tags = null, DateTime? dueDate = null, string? sessionId = null, string? parentId = null)
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
        return item;
    }

    public async Task<bool> CompleteAsync(string id)
    {
        await stateLock.WaitAsync();
        try
        {
            var item = items.FirstOrDefault(t => t.Id == id);
            if (item == null) return false;
            CompleteCore(item);
            await SaveAsync();
            NotifyChanged();
            return true;
        }
        finally { stateLock.Release(); }
    }

    private bool CanCompleteCore(TodoItem item) =>
        !items.Any(t => t.ParentId == item.Id && !t.IsTrashed && !t.IsCompleted);

    private void CompleteCore(TodoItem item)
    {
        item.IsCompleted = true;
        item.CompletedAt = DateTime.UtcNow;
        // 叶子节点勾选完成自动置进度 100；父节点进度由子任务聚合，不在此修改
        if (!HasChildren(item.Id)) item.Progress = 100;
        RecalculateAndAncestorsCore(item.Id);
    }

    public async Task<bool> UncompleteAsync(string id)
    {
        await stateLock.WaitAsync();
        try
        {
            var item = items.FirstOrDefault(t => t.Id == id);
            if (item == null) return false;
            UncompleteCore(item);
            await SaveAsync();
            NotifyChanged();
            return true;
        }
        finally { stateLock.Release(); }
    }

    private void UncompleteCore(TodoItem item)
    {
        item.IsCompleted = false;
        item.CompletedAt = null;
        // 取消完成不重置进度（保持当前值，用户可手动调回）
        RecalculateAndAncestorsCore(item.Id);
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
            var prevConfig = item.RepeatConfig;
            item.RepeatConfig = config;
            // Only reset the cycle anchor when StartDate actually changes (e.g. creating a new repeat or
            // user explicitly reset). Preserve existing LastResetAt when just editing fields like reminder time.
            if (config != null)
            {
                bool startDateChanged = prevConfig == null || config.StartDate.Date != prevConfig.StartDate.Date;
                if (startDateChanged)
                    item.LastResetAt = DateTime.UtcNow;
                // else: keep existing LastResetAt (editing fields only)
            }
            else
            {
                item.LastResetAt = null;
            }
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

    public async Task<bool> UpdateAsync(string id, string? title = null, string? description = null, int? priority = null, string? category = null, List<string>? tags = null, DateTime? dueDate = null, int? progress = null, bool setDueDate = false)
    {
        await stateLock.WaitAsync();
        try
        {
            var item = items.FirstOrDefault(t => t.Id == id);
            if (item == null) return false;
            UpdateCore(item, title, description, priority, category, tags, dueDate, progress, setDueDate);
            await SaveAsync();
            NotifyChanged();
            return true;
        }
        finally { stateLock.Release(); }
    }

    private void UpdateCore(TodoItem item, string? title = null, string? description = null, int? priority = null, string? category = null, List<string>? tags = null, DateTime? dueDate = null, int? progress = null, bool setDueDate = false)
    {
        if (title != null) item.Title = title;
        if (description != null) item.Description = description;
        if (priority.HasValue) item.Priority = priority.Value;
        if (category != null)
        {
            if (!categories.Contains(category)) categories.Add(category);
            item.Category = category;
        }
        if (tags != null) item.Tags = tags;
        if (setDueDate || dueDate.HasValue) item.DueDate = dueDate;
        // 父节点进度由子任务聚合，禁止手动覆盖；叶子手动进度需向上传播（父级为子进度平均值）
        if (progress.HasValue && !HasChildren(item.Id))
        {
            item.Progress = progress.Value;
            RecalculateAndAncestorsCore(item.Id);
        }
    }

    // --- Trash system ---
    public async Task<bool> TrashAsync(string id)
    {
        await stateLock.WaitAsync();
        try
        {
            var item = items.FirstOrDefault(t => t.Id == id);
            if (item == null) return false;
            if (TrashSubtreeCore(id))
            {
                RecalculateAncestorsCore(id);
                await SaveAsync();
                NotifyChanged();
            }
            return true;
        }
        finally { stateLock.Release(); }
    }

    private bool TrashSubtreeCore(string id)
    {
        bool changed = false;
        var pending = new Stack<TodoItem>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var root = items.FirstOrDefault(t => t.Id == id);
        if (root == null) return false;
        pending.Push(root);
        while (pending.Count > 0)
        {
            var item = pending.Pop();
            if (!visited.Add(item.Id)) continue;
            if (!item.IsTrashed) { item.IsTrashed = true; changed = true; }
            if (item.RepeatConfig != null) UnregisterRepeatReminder(item.Id);
            foreach (var child in items.Where(t => t.ParentId == item.Id))
                pending.Push(child);
        }
        return changed;
    }

    public async Task<bool> RestoreAsync(string id)
    {
        await stateLock.WaitAsync();
        try
        {
            var item = items.FirstOrDefault(t => t.Id == id);
            if (item == null) return false;
            if (RestoreSubtreeCore(id))
            {
                RecalculateAndAncestorsCore(id);
                await SaveAsync();
                NotifyChanged();
            }
            return true;
        }
        finally { stateLock.Release(); }
    }

    private bool RestoreSubtreeCore(string id)
    {
        bool changed = false;
        var pending = new Stack<TodoItem>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var root = items.FirstOrDefault(t => t.Id == id);
        if (root == null) return false;
        pending.Push(root);
        while (pending.Count > 0)
        {
            var item = pending.Pop();
            if (!visited.Add(item.Id)) continue;
            if (item.IsTrashed) { item.IsTrashed = false; changed = true; }
            if (item.RepeatConfig != null) RegisterSingleRepeatReminder(item);
            foreach (var child in items.Where(t => t.ParentId == item.Id))
                pending.Push(child);
        }
        return changed;
    }

    public async Task<bool> DeletePermanentlyAsync(string id)
    {
        await stateLock.WaitAsync();
        try
        {
            // 先记录父级 id：删除后该 item 已不在列表中，无法再向上查找
            var parentId = items.FirstOrDefault(t => t.Id == id)?.ParentId;
            var removed = DeletePermanentlyCore(id);
            if (removed)
            {
                if (parentId != null) RecalculateAndAncestorsCore(parentId);
                await SaveAsync();
                NotifyChanged();
            }
            return removed;
        }
        finally { stateLock.Release(); }
    }

    private bool DeletePermanentlyCore(string id)
    {
        var children = items.Where(t => t.ParentId == id).ToList();
        bool changed = false;
        foreach (var child in children)
            changed |= DeletePermanentlyCore(child.Id);
        var removed = items.RemoveAll(t => t.Id == id);
        return changed || removed > 0;
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
        string json;
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new StreamReader(stream);
            json = reader.ReadToEnd();
        }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        catch (UnauthorizedAccessException ex)
        {
            MarkLoadFailure("IO_ERROR", "Todo data could not be read; commands are disabled.", ex);
            return;
        }
        catch (IOException ex)
        {
            MarkLoadFailure("IO_ERROR", "Todo data could not be read; commands are disabled.", ex);
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var jsonRoot = document.RootElement;
            if (jsonRoot.ValueKind != JsonValueKind.Object
                || !jsonRoot.TryGetProperty("items", out var itemArray)
                || itemArray.ValueKind != JsonValueKind.Array
                || (jsonRoot.TryGetProperty("categories", out var categoryArray) && categoryArray.ValueKind != JsonValueKind.Array))
                throw new JsonException("Todo data must contain an items array and an optional categories array.");

            var data = JsonSerializer.Deserialize<TodoData>(json)
                ?? throw new JsonException("Todo data must be an object.");
            ValidateLoadedData(data);
            items.AddRange(data.Items);
            if (data.Categories != null && data.Categories.Count > 0)
            {
                categories.Clear();
                categories.AddRange(data.Categories);
            }
            // 自愈：历史数据可能存在"已完成但进度<100"的叶子（CSH-37 之前完成），
            // 加载后按"已完成=100"的规则重算所有父节点，保证聚合进度与完成状态一致
            foreach (var root in items.Where(t => t.ParentId == null).ToList())
                RecalculateAndAncestorsCore(root.Id);
        }
        catch (JsonException ex)
        {
            MarkLoadFailure("DATA_CORRUPT", "Todo data is damaged; commands are disabled and the file was preserved.", ex);
        }
        catch (Exception ex)
        {
            MarkLoadFailure("DATA_CORRUPT", "Todo data could not be loaded; commands are disabled and the file was preserved.", ex);
        }
    }

    private static void ValidateLoadedData(TodoData? data)
    {
        if (data?.Items == null || data.Items.Any(item => item == null))
            throw new JsonException("Todo data contains an invalid items array.");
        if (data.Categories?.Any(category => category == null) == true)
            throw new JsonException("Todo data contains an invalid categories array.");

        var parents = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var item in data.Items)
        {
            if (item.Id is not { Length: 12 } id || !id.All(char.IsAsciiHexDigit)
                || item.Title == null || item.Description == null || item.Category == null
                || item.Tags == null || item.Tags.Any(tag => tag == null)
                || item.Priority is < 0 or > 2 || item.Progress is < 0 or > 100
                || (item.RepeatConfig != null && !Enum.IsDefined(item.RepeatConfig.Frequency)))
                throw new JsonException("Todo data contains an invalid task.");
            if (item.ParentId != null && (item.ParentId.Length != 12 || !item.ParentId.All(char.IsAsciiHexDigit)))
                throw new JsonException("Todo data contains an invalid parent ID.");
            if (!parents.TryAdd(id, item.ParentId))
                throw new JsonException("Todo data contains duplicate task IDs.");
        }

        var visited = new Dictionary<string, byte>(StringComparer.Ordinal);
        foreach (var start in parents.Keys)
        {
            var path = new List<string>();
            string? current = start;
            while (current != null && parents.TryGetValue(current, out var parent))
            {
                if (visited.TryGetValue(current, out var state))
                {
                    if (state == 1) throw new JsonException("Todo data contains a parent cycle.");
                    break;
                }
                visited[current] = 1;
                path.Add(current);
                current = parent;
            }
            foreach (var id in path) visited[id] = 2;
        }
    }

    private void MarkLoadFailure(string code, string message, Exception ex)
    {
        items.Clear();
        categories.Clear();
        categories.Add("默认");
        storageFailureCode = code;
        storageFailureMessage = message;
        System.Diagnostics.Debug.WriteLine($"[ToolBox] {ex.Message}");
    }

    private async Task SaveAsync()
    {
        EnsureStorageAvailable();
        await writeLock.WaitAsync();
        string? temporaryPath = null;
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var nextSnapshot = CaptureSnapshot();
            var data = new TodoData { Categories = categories.ToList(), Items = items.ToList() };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            var bytes = Encoding.UTF8.GetBytes(json);
            temporaryPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes);
                await stream.FlushAsync();
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(filePath)) File.Replace(temporaryPath, filePath, null);
            else File.Move(temporaryPath, filePath);
            temporaryPath = null;
            committedSnapshot = nextSnapshot;
        }
        catch (Exception ex)
        {
            if (temporaryPath != null)
            {
                try { File.Delete(temporaryPath); }
                catch { }
            }
            try
            {
                if (committedSnapshot != null) RestoreSnapshot(committedSnapshot);
            }
            catch (Exception rollbackError)
            {
                storageFailureCode = "IO_ERROR";
                storageFailureMessage = "Todo state could not be restored after a failed save; restart the application.";
                System.Diagnostics.Debug.WriteLine($"[ToolBox] {rollbackError.Message}");
                throw new TaskCommandException(storageFailureCode, storageFailureMessage);
            }
            if (ex is TaskCommandException commandError) throw commandError;
            throw new TaskCommandException("IO_ERROR", "Todo data could not be saved.", "storage");
        }
        finally
        {
            if (temporaryPath != null)
            {
                try { File.Delete(temporaryPath); }
                catch { }
            }
            writeLock.Release();
        }
    }

    private void EnsureStorageAvailable()
    {
        if (storageFailureCode != null)
            throw new TaskCommandException(storageFailureCode, storageFailureMessage ?? "Todo storage is unavailable.");
    }

    private StoreSnapshot CaptureSnapshot() => new(
        categories.ToList(),
        items.Select(item => new ItemSnapshot(item, CloneItem(item))).ToList());

    private void RestoreSnapshot(StoreSnapshot snapshot)
    {
        foreach (var id in items.Where(item => item.RepeatConfig != null).Select(item => item.Id)
                     .Concat(snapshot.Items.Where(entry => entry.State.RepeatConfig != null).Select(entry => entry.Reference.Id))
                     .Distinct(StringComparer.Ordinal))
            UnregisterRepeatReminder(id);

        items.Clear();
        categories.Clear();
        categories.AddRange(snapshot.Categories);
        foreach (var entry in snapshot.Items)
        {
            CopyItem(entry.State, entry.Reference);
            items.Add(entry.Reference);
        }
        foreach (var item in items.Where(item => item.RepeatConfig != null && !item.IsTrashed))
            RegisterSingleRepeatReminder(item);
    }

    private static TodoItem CloneItem(TodoItem item) => new()
    {
        Id = item.Id,
        ParentId = item.ParentId,
        Title = item.Title,
        Description = item.Description,
        IsCompleted = item.IsCompleted,
        Priority = item.Priority,
        Category = item.Category,
        Tags = item.Tags.ToList(),
        DueDate = item.DueDate,
        Progress = item.Progress,
        SessionId = item.SessionId,
        CreatedAt = item.CreatedAt,
        CompletedAt = item.CompletedAt,
        IsTrashed = item.IsTrashed,
        RepeatConfig = CloneRepeatConfig(item.RepeatConfig),
        LastResetAt = item.LastResetAt
    };

    private static void CopyItem(TodoItem source, TodoItem target)
    {
        target.Id = source.Id;
        target.ParentId = source.ParentId;
        target.Title = source.Title;
        target.Description = source.Description;
        target.IsCompleted = source.IsCompleted;
        target.Priority = source.Priority;
        target.Category = source.Category;
        target.Tags = source.Tags.ToList();
        target.DueDate = source.DueDate;
        target.Progress = source.Progress;
        target.SessionId = source.SessionId;
        target.CreatedAt = source.CreatedAt;
        target.CompletedAt = source.CompletedAt;
        target.IsTrashed = source.IsTrashed;
        target.RepeatConfig = CloneRepeatConfig(source.RepeatConfig);
        target.LastResetAt = source.LastResetAt;
    }

    private static RepeatConfig? CloneRepeatConfig(RepeatConfig? config) => config == null ? null : new RepeatConfig
    {
        Frequency = config.Frequency,
        StartDate = config.StartDate,
        EndDate = config.EndDate,
        ReminderHour = config.ReminderHour,
        ReminderMinute = config.ReminderMinute,
        RepeatDayOfWeek = config.RepeatDayOfWeek,
        RepeatDayOfMonth = config.RepeatDayOfMonth,
        RepeatMonth = config.RepeatMonth,
        RepeatDay = config.RepeatDay
    };

    private sealed record StoreSnapshot(List<string> Categories, List<ItemSnapshot> Items);
    private sealed record ItemSnapshot(TodoItem Reference, TodoItem State);
}

internal class TodoData
{
    [JsonPropertyName("categories")]
    public List<string> Categories { get; set; } = [];
    [JsonPropertyName("items")]
    public List<TodoItem> Items { get; set; } = [];
}
