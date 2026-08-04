# 可重复待办项 设计方案（重置模型）

## 背景与需求

核心场景：每天签到提醒（B站、GitHub、各平台每日任务等）。需要一个轻量的重复待办机制——创建一次，每天/每周/每月到点自动重置为未完成状态，提醒用户去完成。

### 用户视角行为

1. 用户创建一个重复待办："每日 B 站签到"，设置为每天重复，提醒时间 9:00
2. 系统在迷你窗口显示该项，勾选后标记完成
3. 第二天 9:00 系统自动将该待办**重置为未完成**，并弹出通知提醒
4. 如果应用关闭期间错过了重置时间，启动时自动补齐（重置一次）
5. 可设置结束日期（如活动截止日），到期后不再重置

## 设计方案变更说明

本方案采用**单项目重置模型**，与最初的「模板+实例」模型有本质区别：

| 方面 | 模板+实例模型（已废弃） | 重置模型（当前方案） |
|------|------------------------|---------------------|
| 模型 | 模板创建多个实例，各实例是独立 TodoItem | 同一个 TodoItem 反复重置 `IsCompleted` |
| 完成行为 | 完成后自动生成下一个实例 | 完成 = 标记完成，到点才重置 |
| 存储 | 模板 + N 个实例占多项 | 只占一项，轻量 |
| 补齐 | 为每个错过的天创建新实例 | 已完成且过重置时间 → 重置一次 |
| 多个未完成天 | 有多个逾期实例同时显示 | 只有一个当前周期项 |

## 一、数据模型扩展

在现有 `TodoItem` 上增加重复相关字段，保持向后兼容：

```csharp
// Core/Todo/TodoItem.cs — 新增字段
public RepeatConfig? RepeatConfig { get; set; } // null=普通待办，有值=重复配置
public DateTime? LastResetAt { get; set; }       // 最近一次重置时间（用于补齐判断）
```

新增枚举和配置类：

```csharp
// Core/Todo/RepeatConfig.cs
public enum ERepeatFrequency
{
    Daily,      // 每天
    Weekly,     // 每周(同星期几)
    Monthly,    // 每月(同日期)
    Yearly      // 每年(同月日)
}

public class RepeatConfig
{
    public ERepeatFrequency Frequency { get; set; } = ERepeatFrequency.Daily;
    public DateTime StartDate { get; set; }         // 首次开始日期
    public DateTime? EndDate { get; set; }           // null=永不停止
    public int ReminderHour { get; set; } = 9;      // 提醒小时(0-23)
    public int ReminderMinute { get; set; } = 0;     // 提醒分钟(0-59)
}
```

### JSON 序列化兼容

新增字段全部为 nullable，旧版 `todos.json` 反序列化时自动为 null，无需数据迁移：

```json
{
  "Id": "abc123",
  "Title": "每日 B 站签到",
  "IsCompleted": true,
  "CompletedAt": "2026-07-08T09:15:00Z",
  "RepeatConfig": {
    "Frequency": 0,
    "StartDate": "2026-07-06T00:00:00",
    "EndDate": null,
    "ReminderHour": 9,
    "ReminderMinute": 0
  },
  "LastResetAt": "2026-07-08T09:00:00Z"
}
```

## 二、重置模型生命周期

```
创建重复待办 "每日B站签到" (Daily, 提醒 9:00)
  │
  ├── Day 1 (9:00 创建):
  │   ├── 显示在迷你窗口 → 用户勾选完成
  │   ├── IsCompleted = true, CompletedAt = now
  │   └── 等待下次重置
  │
  ├── Day 2 (9:00 Cron 触发):
  │   ├── IsCompleted == true → 重置为 false
  │   ├── LastResetAt = now
  │   ├── 弹通知 "🔄 每日 B 站签到 已重置，记得完成哦"
  │   └── 用户勾选完成 → IsCompleted = true
  │
  ├── Day 3 (9:00 Cron 触发):
  │   ├── IsCompleted == true → 重置 → 通知
  │   └── ...
  │
  └── 启动时补齐 (CatchUpRepeatingAsync):
      └── 遍历所有重复项:
          └── 如果 IsCompleted == true
              └── 计算下次重置时间
              └── 如果 DateTime.UtcNow >= 下次重置时间
                  └── 重置为 false, 更新 LastResetAt
```

### 关键规则

- 重复项本身就是一个正常的 TodoItem，**只有一个对象**
- 完成重复项与普通项行为一致（`CompleteAsync`）
- 只有 Cron 触发或补齐时才会重置 `IsCompleted`
- 重复项的 `Progress` 不自动变化（由用户自行滑动）
- 重复项的 `Category`、`Tags`、`Description` 与普通项一致
- 子任务（`Children`）不参与重复，跟随父项

### 时序判断

```
Day 1 创建，提醒 9:00
         │
         │ 创建时也是 9:00 
         │ ↓                                  Day 2 9:00
    [项 A] ─── IsCompleted ───→ 等待 ──────────→ Cron 触发
    IsCompleted=false            重置时间         ↓
    (第一天直接显示)              Day 1 9:00   仍为 false（用户提前完成了）
                                               ↓
                                          通知 "已完成，明天继续！"
                                               ↓
                                          Day 2 用户完成 → true
                                               ↓
                                          Day 3 9:00 → 重置 → 通知
```

**重要：每天的 Cron 触发时判断：**
- 如果已完成（`IsCompleted == true`）→ 重置为 false + 通知 "XXX 已重置，记得完成哦"
- 如果未完成（`IsCompleted == false`）→ 不操作 + 通知 "XXX 今天还没有完成哦"

## 三、TodoStore 新增方法

```csharp
// 创建重复待办
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
    int reminderMinute = 0)
{
    var item = new TodoItem
    {
        Title = title,
        Description = description,
        Priority = priority,
        Category = category,
        Tags = tags ?? [],
        DueDate = dueDate,
        RepeatConfig = new RepeatConfig
        {
            Frequency = frequency,
            StartDate = DateTime.Today,
            EndDate = endDate,
            ReminderHour = reminderHour,
            ReminderMinute = reminderMinute,
        },
        LastResetAt = DateTime.UtcNow, // 创建时记录重置时间，避免启动时被补齐
    };
    items.Add(item);
    await SaveAsync();
    NotifyChanged();
    return item;
}

// 获取所有重复项
public List<TodoItem> GetRepeating()
    => items.Where(t => t.RepeatConfig != null && !t.IsTrashed).ToList();

// 获取今天需要显示的重复项（未完成的，或者已完成但今天还没过重置时间仍显示）
public List<TodoItem> GetDueRepeating()
{
    var now = DateTime.UtcNow;
    return items.Where(t =>
        t.RepeatConfig != null
        && !t.IsTrashed
        && (t.RepeatConfig.EndDate == null || now.Date <= t.RepeatConfig.EndDate.Value.Date))
        .OrderBy(t => t.IsCompleted ? 1 : 0) // 未完成的排前面
        .ThenBy(t => t.DueDate ?? DateTime.MaxValue)
        .ToList();
}

// 重置重复项（由 Cron 或补齐调用）
public async Task<bool> ResetRepeatingAsync(string id)
{
    var item = items.FirstOrDefault(t => t.Id == id && t.RepeatConfig != null);
    if (item == null) return false;

    item.IsCompleted = false;
    item.CompletedAt = null;
    item.LastResetAt = DateTime.UtcNow;

    await SaveAsync();
    NotifyChanged();
    return true;
}

// 启动时补齐错过的重置
public async Task<int> CatchUpRepeatingAsync()
{
    var repeating = items.Where(t => t.RepeatConfig != null && !t.IsTrashed).ToList();
    int resetCount = 0;

    foreach (var item in repeating)
    {
        if (!item.IsCompleted) continue; // 未完成不需要补齐
        var config = item.RepeatConfig!;

        // 计算下次重置时间
        var nextReset = CalculateResetTime(
            item.LastResetAt ?? config.StartDate,
            config.Frequency,
            config.ReminderHour,
            config.ReminderMinute);

        // 如果已经过了重置时间，则重置
        if (DateTime.UtcNow >= nextReset)
        {
            item.IsCompleted = false;
            item.CompletedAt = null;
            item.LastResetAt = DateTime.UtcNow;
            resetCount++;
        }
    }

    if (resetCount > 0)
    {
        await SaveAsync();
        NotifyChanged();
    }
    return resetCount;
}

// 计算下一次重置时间
private static DateTime CalculateResetTime(
    DateTime from,
    ERepeatFrequency frequency,
    int hour,
    int minute)
{
    var baseTime = frequency switch
    {
        ERepeatFrequency.Daily => from.AddDays(1),
        ERepeatFrequency.Weekly => from.AddDays(7),
        ERepeatFrequency.Monthly => AddMonthsSafe(from, 1),
        ERepeatFrequency.Yearly => AddYearsSafe(from, 1),
        _ => from.AddDays(1)
    };
    return new DateTime(baseTime.Year, baseTime.Month, baseTime.Day, hour, minute, 0, DateTimeKind.Utc);
}
```

## 四、日期计算辅助类

```csharp
// Core/Todo/RepeatCalculator.cs — 纯函数，方便测试
public static class RepeatCalculator
{
    // ReminderHour/Minute 为用户本地时间（与旧 Cron 语义一致），日期运算在本地时区进行，
    // 避免 UTC 日期与本地日期错位（如本地 6:00 在 UTC 是前一天 22:00）。
    public static DateTime CalculateNextResetTime(
        DateTime lastReset,
        ERepeatFrequency frequency,
        int reminderHour = 9,
        int reminderMinute = 0)
    {
        var anchor = lastReset.Kind switch
        {
            DateTimeKind.Utc => lastReset,
            // 兼容旧数据：无时区后缀的 JSON 时间戳按 UTC 处理
            _ => DateTime.SpecifyKind(lastReset, DateTimeKind.Utc),
        };
        anchor = anchor.ToLocalTime();
        var baseTime = frequency switch
        {
            ERepeatFrequency.Daily => anchor.AddDays(1),
            ERepeatFrequency.Weekly => anchor.AddDays(7),
            ERepeatFrequency.Monthly => AddMonthsSafe(anchor, 1),
            ERepeatFrequency.Yearly => AddYearsSafe(anchor, 1),
            _ => anchor.AddDays(1)
        };
        var nextLocal = new DateTime(
            baseTime.Year, baseTime.Month, baseTime.Day,
            reminderHour, reminderMinute, 0);
        return TimeZoneInfo.ConvertTimeToUtc(nextLocal);
    }

    /// <summary>
    /// 计算下一次提醒时间：从 lastReset 起按频率推进；
    /// 若已过期则连续推进到 afterUtc 之后最近的一次。
    /// 与 CalculateNextResetTime 同一套相对计算（唯一时间基准），
    /// 提醒注册与补齐/重置共用，避免 Cron 固定星期/日期造成的不一致。
    /// </summary>
    public static DateTime GetNextOccurrence(
        DateTime lastReset,
        ERepeatFrequency frequency,
        DateTime afterUtc,
        int reminderHour = 9,
        int reminderMinute = 0)
    {
        var next = CalculateNextResetTime(lastReset, frequency, reminderHour, reminderMinute);
        while (next <= afterUtc)
            next = CalculateNextResetTime(next, frequency, reminderHour, reminderMinute);
        return next;
    }

    private static DateTime AddMonthsSafe(DateTime date, int months)
    {
        var result = date.AddMonths(months);
        return result.Day != date.Day
            ? result.AddDays(-result.Day + Math.Min(date.Day, DateTime.DaysInMonth(result.Year, result.Month)))
            : result;
    }

    private static DateTime AddYearsSafe(DateTime date, int years)
    {
        var result = date.AddYears(years);
        return result.Day != date.Day
            ? result.AddDays(-result.Day + Math.Min(date.Day, DateTime.DaysInMonth(result.Year, result.Month)))
            : result;
    }
}
```

> 说明：v2.0 曾同时存在两套时间计算（`ToCron` 的 Cron 固定星期/日期 + `CalculateNextResetTime` 的相对周期），二者日期基准互相矛盾（Weekly 固定周一 vs 同星期几、Monthly 固定 1 号 vs 同日期、Yearly 固定 1 月 1 日 vs 同月日），且 Cron 回调不校验 nextReset 直接重置。v2.1 已移除 `ToCron`，重复待办改由一次性定时器按 `CalculateNextResetTime` 推进，**统一为单一时间基准**。

## 五、提醒调度集成（一次性定时器，非 Cron）

为每个活跃重复项按 `CalculateNextResetTime` 计算下一次提醒时刻，挂一次性定时器；触发后由回调续订下一次提醒。

```csharp
// TodoStore.RegisterSingleRepeatReminder（要点）
var anchor = item.LastResetAt ?? config.StartDate;
var next = RepeatCalculator.GetNextOccurrence(
    anchor, config.Frequency, DateTime.UtcNow,
    config.ReminderHour, config.ReminderMinute);

var cronId = "repeat_" + item.Id;
CronExpressionr.Instance.Unregister(cronId);

// 已过结束日期：不再注册后续提醒
if (config.EndDate != null && next > config.EndDate.Value)
    return;

CronExpressionr.Instance.RegisterNext(cronId, next, () =>
{
    var current = items.FirstOrDefault(t => t.Id == item.Id);
    if (current?.RepeatConfig == null || current.IsTrashed) return;
    // 触发时已过 EndDate：注销并停止
    if (current.RepeatConfig.EndDate != null && DateTime.UtcNow > current.RepeatConfig.EndDate.Value)
    {
        UnregisterRepeatReminder(item.Id);
        return;
    }
    if (current.IsCompleted)
    {
        _ = ResetRepeatingAsync(item.Id);      // 已完成 → 重置
        RepeatReminderTriggered?.Invoke(current, true);
    }
    else
    {
        RepeatReminderTriggered?.Invoke(current, false); // 未完成 → 仅提醒
    }
    RegisterSingleRepeatReminder(current);     // 续订下一次提醒
});
```

### CronExpressionr 支持

- `RegisterNext(id, fireAtUtc, handler)`：一次性提醒，触发一次不自动续订（由回调续期）。
- 长延时分段调度：`System.Threading.Timer` 的 dueTime 上限约 49.7 天（`uint.MaxValue - 1` 毫秒），Yearly 项最长 366 天会抛 `ArgumentOutOfRangeException`；调度器对超过上限的延时自动分段（每次挂一段最大延时，到期后重新计算剩余延时），对 Cron 与一次性注册均生效。
- 启动/恢复逻辑不变：`RegisterRepeatReminders` 逐项注册（单项失败 try/catch 不中断其余项）。

### 回调逻辑

```csharp
private void OnRepeatReminder(TodoItem item)
{
    if (item.IsCompleted)
    {
        // 已完成 → 重置为未完成，弹通知
        TodoStore.Instance.ResetRepeatingAsync(item.Id).GetAwaiter().GetResult();
        ShowNotification($"🔄 {item.Title} 已重置，记得完成哦");
    }
    else
    {
        // 未完成 → 弹提醒通知
        ShowNotification($"{item.Title} 今天还没有完成哦");
    }
}
```

### 启动集成

在 `App.OnStartup` 中，TodoStore 初始化后调用补齐 + 注册提醒 + 周期补算：

```csharp
// App.xaml.cs — OnStartup 中 ScheduleManager.ApplyCurrent() 之后
var missed = await TodoStore.Instance.CatchUpRepeatingAsync();
if (missed > 0)
{
    System.Diagnostics.Debug.WriteLine($"[ToolBox] 重置了 {missed} 个重复待办");
}
TodoStore.Instance.RegisterRepeatReminders();
TodoStore.Instance.StartPeriodicCatchUp(); // 每 30 分钟轻量补算一次
```

## 六、UI 变更

### 6.1 迷你窗口（CompactToolboxWindow）

在待办列表区域顶部增加"今日重复"分组：

```
── 今日重复 ─────────────────
[ ] 🔄 每日 B 站签到
[ ] 🔄 每日 GitHub 签到
── 未分类 ──────────────────
[x] 写周报
[ ] 买菜
```

- 重复待办从 `GetDueRepeating()` 获取
- 🔄 图标（使用 Lucide `Repeat` 图标或 🔄 Unicode 字符）显示在标题左侧
- 如果今天没有重复待办，不显示"今日重复"分组
- 重复待办排在普通待办**上面**
- 分组标题 11px 灰色（`TextTertiaryBrush`）
- 组内按 `IsCompleted` 升序（未完成排前），再按 `DueDate` 升序

### 6.2 TodoView 筛选栏 — 新增"重复"标签

```
[全部] [待办] [已完成] [废弃] [重复]    ← 新增第五个筛选项
```

"重复"标签：显示所有配置了重复规则的待办（含已完成和未完成的）。

### 6.3 TodoView 列表项 — 重复标识

```
[ ] 🔄 每日 B 站签到          07-08
[x] 普通待办                  07-08
```

- 🔄 图标显示在 checkbox 和标题之间
- 实例的 DueDate 显示在右侧
- 完成状态的项显示删除线 + 灰色

### 6.4 TodoView 详情面板 — 新增重复配置区

在优先级和描述之间插入重复配置区：

```
[标题输入框]

[状态徽章]

[优先级: 普通 | 重要 | 紧急]

[分类输入框]

[截止日期]

[完成进度 滑块]

─── 重复设置 ───────────────────  ← 新增
  频率: [不重复 ▾]               ← 下拉选择: 不重复/每天/每周/每月/每年
  提醒时间: [09:00]              ← 时间选择（时:分）
  结束日期: [永不 ▾]             ← 下拉: 永不/自定义日期
  [设为重复]                     ← 确认/更新重复配置按钮

[描述输入框]

[时间戳区域]

[添加子任务] [废弃任务]
```

### 6.5 交互逻辑

**创建重复待办：**
- 在添加待办时（FloatingAddBtn），InputWindow 增加重复频率下拉选项
- 添加重复待办后，自动注册 Cron 提醒

**编辑重复配置：**
- 选择已有重复项 → 详情面板显示当前重复配置
- 修改频率/时间/结束日期 → 点击"设为重复"保存
- 将频率改为"不重复" → 取消重复，清除 `RepeatConfig`

**删除重复项：**
- 与普通待办完全一致 → 走 Trash 回收站
- 从回收站恢复后，重复配置保留，Cron 重新注册

## 七、LLM 工具扩展

```csharp
// add_todo 工具新增 repeat 参数
[Tool("add_todo", "创建新的 Todo 任务")]
public static string AddTodo(
    [ToolParam("任务标题")] string title,
    [ToolParam("任务描述")] string description = "",
    [ToolParam("优先级 0=普通 1=重要 2=紧急")] int priority = 0,
    [ToolParam("标签（逗号分隔）")] string tags = "",
    [ToolParam("截止日期（yyyy-MM-dd）")] string dueDate = "",
    [ToolParam("重复频率: daily/weekly/monthly/yearly，空=不重复")] string repeat = "")
{
    var tagList = ParseTags(tags);
    DateTime? due = ParseDate(dueDate, title);

    if (!string.IsNullOrWhiteSpace(repeat)
        && Enum.TryParse<ERepeatFrequency>(repeat, true, out var freq))
    {
        var item = TodoStore.Instance.AddRepeating(
            title, freq, description, priority, tagList: tagList, dueDate: due);
        return $"已添加重复 Todo [{item.Id}]: {item.Title} ({freq})";
    }

    var normal = TodoStore.Instance.Add(title, description, priority, tags: tagList, dueDate: due);
    return $"已添加 Todo [{normal.Id}]: {normal.Title}";
}

// list_todos 新增重复筛选
[Tool("list_todos", "查询 Todo 列表")]
public static string ListTodos(
    [ToolParam("筛选: all/pending/completed/repeating")] string filter = "all",
    [ToolParam("按标签筛选")] string tag = "")
{
    List<TodoItem> items = filter switch
    {
        "pending" => TodoStore.Instance.GetPending(),
        "completed" => TodoStore.Instance.GetCompleted(),
        "repeating" => TodoStore.Instance.GetRepeating(),
        _ => TodoStore.Instance.GetAll()
    };
    // ...
}
```

## 八、边界情况

| 场景 | 处理 |
|------|------|
| 应用关闭多天后打开 | CatchUpRepeatingAsync 检查：已完成且过重置时间 → 重置一次；用户只能看到一个当前周期的项 |
| Day N 未完成就到了 Day N+1 | 旧项保持未完成状态（红色/逾期样式），新周期提醒触发时不重置（因为已完成 != true），只提醒 |
| EndDate 到期 | 注册时 next > EndDate 直接不注册；触发时已过 EndDate 注销并停止重置；ResetRepeatingAsync/CatchUpRepeatingAsync 均校验 EndDate。项保留在列表中（和普通项一样） |
| 修改频率（比如从 Daily 改为 Weekly） | 更新 `RepeatConfig`，注销旧定时器，注册新定时器 |
| 取消重复（频率改为不重复） | 清除 `RepeatConfig`，注销定时器；项保留为普通待办 |
| 删除重复项（走 Trash） | 正常走 Trash 回收站流程；恢复时重复配置保留，重新注册 |
| 运行期新建重复项 | `AddRepeatingAsync` 创建成功后即注册提醒（悬浮添加、紧凑窗口快捷添加、`TodoTools.AddTodo` 均生效） |
| Yearly 长延时（最长 366 天） | 一次性定时器超过 `Timer` dueTime 上限（约 49.7 天）时自动分段调度，不抛异常 |
| 运行期错失到点（提醒时刻后才完成/睡眠唤醒等） | `StartPeriodicCatchUp` 每 30 分钟轻量补算一次（幂等），与启动补齐共用 `CatchUpRepeatingAsync` |
| 重复 + 子任务 | 子任务不参与重复，跟随父项；重置父项不影响子任务状态 |
| 频繁切换应用 / 定时器并发重置 | 所有 items/categories 读改写由 `stateLock`（SemaphoreSlim）串行化；`writeLock` 保护持久化写入；UI 刷新统一走 `Dispatcher.BeginInvoke` 异步编组，避免锁内同步回 UI 线程造成死锁 |
| 提醒时间在半夜而应用夜间关闭 | 次日启动补齐时检查：如果应该重置则重置一次；如果已过提醒时间但用户已标记完成，重置为未完成 |

## 九、文件变更清单

| 文件 | 操作 | 说明 |
|------|------|------|
| `Core/Todo/TodoItem.cs` | 修改 | 新增 RepeatConfig、LastResetAt 字段 |
| `Core/Todo/RepeatConfig.cs` | 新增 | ERepeatFrequency 枚举 + RepeatConfig 类 |
| `Core/Todo/RepeatCalculator.cs` | 新增 | 日期计算纯函数 + GetNextOccurrence（唯一时间基准，v2.1 移除 ToCron） |
| `Core/Todo/TodoStore.cs` | 修改 | 新增 AddRepeatingAsync、GetRepeating、GetDueRepeating、ResetRepeatingAsync、CatchUpRepeatingAsync、RegisterSingleRepeatReminder、StartPeriodicCatchUp；创建成功即注册提醒；EndDate 校验 |
| `Core/Scheduling/CronScheduler.cs` | 修改 | 新增 RegisterNext 一次性提醒 + 超过 dueTime 上限的长延时分段调度 + 线程安全 |
| `Views/Todo/TodoView.xaml` | 修改 | 详情面板增加重复配置区、筛选栏增加"重复"标签 |
| `Views/Todo/TodoView.xaml.cs` | 修改 | 处理重复配置 UI 逻辑、筛选"重复"时的数据加载 |
| `Views/CompactToolboxWindow.xaml` | 修改 | 待办区增加"今日重复"分组 |
| `Views/CompactToolboxWindow.xaml.cs` | 修改 | 加载重复待办数据、分组渲染 |
| `Views/InputWindow.xaml` | 修改 | 增加重复频率下拉选项 |
| `Views/InputWindow.xaml.cs` | 修改 | 传递重复频率参数 |
| `Core/Tools/TodoTools.cs` | 修改 | add_todo 增加 repeat 参数、list_todos 增加 repeating 筛选 |
| `App.xaml.cs` | 修改 | 启动时调用 CatchUpRepeatingAsync + RegisterRepeatReminders + StartPeriodicCatchUp |

---

*设计方案 v2.1 — 重置模型（一次性定时器 + 统一时间基准）— 2026-08-04*
