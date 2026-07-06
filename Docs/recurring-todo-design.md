# 可重复待办项 设计方案

## 背景与需求

核心场景：每天签到提醒（B站、GitHub、各平台每日任务等）。需要一个轻量的重复待办机制——创建一次，每天/每周/每月自动生成新的待办项。

### 用户视角行为

1. 用户创建一个重复待办："每日 B 站签到"，设置为每天重复
2. 系统在当天生成一个实例待办，迷你窗口显示
3. 用户完成后，系统自动生成明天的实例
4. 如果应用关闭期间错过了几天，启动时自动补齐
5. 可设置结束日期（如活动截止日），到期后不再生成

## 设计方案

### 前置说明：design.md 与代码的差距

`design.md` 中描述了 `Progress`（0-100 进度条）、`StartDate`（起始日期）、`Children`（子任务树）三个功能，但 **实际代码均未实现**。当前 `TodoItem.cs` 仅有基础字段（Id/Title/Description/IsCompleted/Priority/Tags/DueDate 等）。

本方案的重复待办设计 **不依赖** 这些未实现的字段，可独立实施。如果未来补齐 Progress/Children，需要额外处理：
- 重复模板的 Progress 不应自动 100%（模板不是可完成的）
- 子任务不参与重复生成（子任务跟随父实例，不独立重复）
- 父任务进度聚合公式需排除模板自身（模板.Progress = 无意义）

### 一、数据模型扩展

在现有 `TodoItem` 上增加重复相关字段，保持向后兼容：

```csharp
// Core/Todo/TodoItem.cs — 新增字段
public string? RepeatParentId { get; set; }   // null=模板/普通待办, 有值=从某模板生成的实例
public RepeatConfig? RepeatConfig { get; set; } // null=不重复, 有值=重复模板配置
public int? RepeatDay { get; set; }            // 仅用于实例: 该实例对应的日期序号(用于排序)
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
    public DateTime StartDate { get; set; }         // 首次生成日期
    public DateTime? EndDate { get; set; }           // null=永不停止
    public string CronExpression { get; set; } = ""; // 自动生成的 Cron，用于提醒
    public int ReminderHour { get; set; } = 9;          // 提醒小时(0-23)
    public int ReminderMinute { get; set; } = 0;         // 提醒分钟(0-59)
}
```

### 二、模板与实例的关系

采用**模板 + 实例**模型，简单直观：

```
TodoItem (模板)
├── Id: "abc123"
├── Title: "每日 B 站签到"
├── RepeatConfig: { Daily, StartDate: 2026-07-06, EndDate: null }
├── IsCompleted: false  ← 模板永远不标记完成
│
├── TodoItem (实例 1)
│   ├── Id: "def456"
│   ├── Title: "每日 B 站签到"
│   ├── RepeatParentId: "abc123"
│   ├── DueDate: 2026-07-06
│   ├── IsCompleted: true
│   └── CompletedAt: 2026-07-06T09:30:00
│
├── TodoItem (实例 2)
│   ├── Id: "ghi789"
│   ├── Title: "每日 B 站签到"
│   ├── RepeatParentId: "abc123"
│   ├── DueDate: 2026-07-07
│   └── IsCompleted: false  ← 今天待办
│
└── TodoItem (实例 3 — 启动时补齐)
    ├── Id: "jkl012"
    ├── RepeatParentId: "abc123"
    ├── DueDate: 2026-07-08
    └── IsCompleted: false
```

**关键规则**：
- 模板本身不出现在待办列表中（仅在"重复管理"视图可见）
- 迷你窗口只显示 `RepeatParentId != null && IsCompleted == false && DueDate <= Today` 的实例
- 完成实例后，`TodoStore` 自动生成下一次实例（若未超过 EndDate）
- 模板的 Tags/Description/Priority 被所有实例继承

### 三、TodoStore 新增方法

```csharp
// 创建重复待办（同时创建模板 + 第一个实例）
public async Task<TodoItem> AddRepeatingAsync(
    string title,
    ERepeatFrequency frequency,
    string description = "",
    int priority = 0,
    List<string>? tags = null,
    DateTime? endDate = null,
    int reminderHour = 9,
    int reminderMinute = 0)
{
    // 1. 创建模板（IsCompleted=false, RepeatConfig 有值）
    var template = new TodoItem
    {
        Title = title,
        Description = description,
        Priority = priority,
        Tags = tags ?? [],
        RepeatConfig = new RepeatConfig
        {
            Frequency = frequency,
            StartDate = DateTime.Today,
            EndDate = endDate,
            CronExpression = frequency.ToCron(reminderHour, reminderMinute),
        }
    };
    items.Add(template);

    // 2. 为今天创建实例
    var instance = CreateInstance(template, DateTime.Today);
    items.Add(instance);

    await SaveAsync();
    NotifyChanged();
    return instance;
}

// 完成实例 → 自动生成下一个
public async Task<bool> CompleteRepeatingAsync(string id)
{
    var item = items.FirstOrDefault(t => t.Id == id);
    if (item == null || item.RepeatParentId == null) return false;

    item.IsCompleted = true;
    item.CompletedAt = DateTime.UtcNow;

    // 找到模板，生成下一个实例
    var template = items.FirstOrDefault(t => t.Id == item.RepeatParentId);
    if (template?.RepeatConfig != null)
    {
        var nextDate = CalculateNextDate(
            item.DueDate!.Value,
            template.RepeatConfig.Frequency);

        if (template.RepeatConfig.EndDate == null ||
            nextDate <= template.RepeatConfig.EndDate.Value)
        {
            var next = CreateInstance(template, nextDate);
            items.Add(next);
        }
    }

    await SaveAsync();
    NotifyChanged();
    return true;
}

// 启动时补齐错过的实例
public async Task<int> CatchUpRepeatingAsync()
{
    var templates = items.Where(t => t.RepeatConfig != null).ToList();
    int created = 0;
    foreach (var template in templates)
    {
        var config = template.RepeatConfig!;
        var existingDates = items
            .Where(t => t.RepeatParentId == template.Id)
            .Select(t => t.DueDate!.Value.Date)
            .ToHashSet();

        var checkDate = config.StartDate;
        while (checkDate <= DateTime.Today)
        {
            if ((config.EndDate == null || checkDate <= config.EndDate.Value)
                && !existingDates.Contains(checkDate))
            {
                items.Add(CreateInstance(template, checkDate));
                created++;
            }
            checkDate = CalculateNextDate(checkDate, config.Frequency);
        }
    }
    if (created > 0) await SaveAsync();
    return created;
}
```


```csharp
// 辅助方法：从模板创建实例
private TodoItem CreateInstance(TodoItem template, DateTime date)
{
    return new TodoItem
    {
        Title = template.Title,
        Description = template.Description,
        Priority = template.Priority,
        Tags = new List<string>(template.Tags),
        DueDate = date,
        RepeatParentId = template.Id
    };
}

// 查询所有重复模板
public List<TodoItem> GetRepeating() => items.Where(t => t.RepeatConfig != null).ToList();

// 删除重复模板：同时删除未完成实例，已完成实例保留（历史记录）
public async Task<bool> DeleteRepeatingAsync(string templateId)
{
    var template = items.FirstOrDefault(t => t.Id == templateId && t.RepeatConfig != null);
    if (template == null) return false;

    // 删除未完成的实例
    items.RemoveAll(t => t.RepeatParentId == templateId && !t.IsCompleted);
    // 删除模板本身
    items.Remove(template);

    await SaveAsync();
    NotifyChanged();
    return true;
}
```
### 四、日期计算

```csharp
using Cysharp.Text;  // ZString
// Core/Todo/RepeatCalculator.cs — 纯函数，方便测试
public static class RepeatCalculator
{
    public static DateTime CalculateNextDate(DateTime current, ERepeatFrequency frequency)
    {
        return frequency switch
        {
            ERepeatFrequency.Daily => current.AddDays(1),
            ERepeatFrequency.Weekly => current.AddDays(7),
            ERepeatFrequency.Monthly => AddMonthsSafe(current, 1),
            ERepeatFrequency.Yearly => AddYearsSafe(current, 1),
            _ => current.AddDays(1)
        };
    }

    public static string ToCron(this ERepeatFrequency frequency, int reminderHour = 9, int reminderMinute = 0)
    {
        return frequency switch
        {
            ERepeatFrequency.Daily => ZString.Format("0 {0} {1} * *", reminderMinute, reminderHour),       // 每天
            ERepeatFrequency.Weekly => ZString.Format("0 {0} {1} * * 1", reminderMinute, reminderHour),      // 每周一
            ERepeatFrequency.Monthly => ZString.Format("0 {0} {1} 1 * *", reminderMinute, reminderHour),     // 每月1号
            ERepeatFrequency.Yearly => ZString.Format("0 {0} {1} 1 1 *", reminderMinute, reminderHour),      // 每年1月1日
            _ => "0 9 * * *"
        };
    }

    // 处理月末天数不同的情况
    private static DateTime AddMonthsSafe(DateTime date, int months)
    {
        var result = date.AddMonths(months);
        return result.Day != date.Day ? result.AddDays(-result.Day + date.Day) : result;
    }

    private static DateTime AddYearsSafe(DateTime date, int years)
    {
        var result = date.AddYears(years);
        return result.Day != date.Day ? result.AddDays(-result.Day + date.Day) : result;
    }
}
```

### 五、启动流程集成

在 `App.OnStartup` 中，TodoStore 加载后调用补齐逻辑：

```csharp
// App.xaml.cs — OnStartup 中 TodoStore 初始化之后
var missed = await TodoStore.Instance.CatchUpRepeatingAsync();
if (missed > 0)
{
    System.Diagnostics.Debug.WriteLine($"[ToolBox] 补齐 {missed} 个错过的重复待办");
}
```

### 六、CronScheduler 提醒集成

为每个活跃的重复模板注册 Cron 定时提醒：

```csharp
// 在 TodoStore.CatchUpRepeatingAsync 之后调用
public void RegisterRepeatReminders()
{
    var templates = items.Where(t => t.RepeatConfig != null).ToList();
    foreach (var template in templates)
    {
        var config = template.RepeatConfig!;
        if (!string.IsNullOrEmpty(config.CronExpression))
        {
            CronExpressionr.Instance.Register(
                "todo_" + template.Id,
                config.CronExpression,
                () => OnRepeatReminder(template));
        }
    }
}

private void OnRepeatReminder(TodoItem template)
{
    // 检查今天是否有未完成的实例
    var today = DateTime.Today;
    var existing = items.FirstOrDefault(t =>
        t.RepeatParentId == template.Id
        && t.DueDate?.Date == today
        && !t.IsCompleted);

    if (existing == null)
    {
        // 今天还没有实例，创建一个并通知
        var instance = CreateInstance(template, today);
        items.Add(instance);
        SaveAsync().GetAwaiter().GetResult();
        NotifyChanged();
        // TODO: 触发系统通知（托盘气泡/Toast）
    }
}
```

### 七、UI 变更

#### 7.1 TodoView 详情面板 — 新增重复配置区

在优先级和描述之间插入重复配置区：

```
[标题输入框]

[状态徽章]

[优先级: 普通 | 重要 | 紧急]

[描述输入框]

─── 重复设置 ───────────────────  ← 新增
  频率: [不重复 ▾]               ← 下拉选择: 不重复/每天/每周/每月/每年
  提醒: [09:00]                  ← 时间选择(可选)
  结束: [永不 ▾]                 ← 下拉: 永不/自定义日期
  [保存重复设置]                  ← 仅重复模式时显示

[时间戳区域]

[删除按钮]
```

#### 7.2 TodoView 列表项 — 重复标识

在现有的 checkbox + 优先级点 + 标题行中，为重复待办添加循环图标标识：

```
[x] 🔄 每日 B 站签到          07-06
[x] 每日 GitHub 签到            07-06
[ ] 普通待办                    07-08
```

- 🔄 图标（使用 Lucide `Repeat` 图标）显示在标题左侧
- 实例的 DueDate 显示在右侧
- 完成状态的实例显示删除线 + 灰色

#### 7.3 TodoView 筛选栏 — 新增"重复"标签

```
[全部] [待办] [已完成] [重复]    ← 新增第四个筛选项
```

"重复"标签：显示所有重复模板（含其最近实例的历史）。

#### 7.4 迷你窗口（CompactToolboxWindow）

在待办列表区域顶部增加"今日重复"分组：

```
── 今日重复 ─────────────────
[x] 🔄 每日 B 站签到
[ ] 🔄 每日 GitHub 签到
── 普通待办 ─────────────────
[x] 写周报
[ ] 买菜
```

- 如果今天没有重复待办，不显示"今日重复"分组
- 重复待办排在普通待办前面
- 每组有 11px 灰色分隔标题
- 组内按 DueDate 升序排列

### 八、LLM 工具扩展

在 `TodoTools` 中新增重复相关参数：

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
    var tagList = string.IsNullOrWhiteSpace(tags)
        ? new List<string>()
        : tags.Split(',').Select(t => t.Trim()).ToList();
    DateTime? due = DateTime.TryParse(dueDate, out var d) ? d : null;

    if (!string.IsNullOrWhiteSpace(repeat)
        && Enum.TryParse<ERepeatFrequency>(repeat, true, out var freq))
    {
        var item = TodoStore.Instance.AddRepeating(
            title, freq, description, priority, tagList, due);
        return "已添加重复 Todo [" + item.Id + "]: " + item.Title
            + " (" + freq + ")";
    }

    var normal = TodoStore.Instance.Add(title, description, priority, tagList, due);
    return "已添加 Todo [" + normal.Id + "]: " + normal.Title;
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

### 九、JSON 序列化兼容

新增字段全部为 nullable，旧版 `todos.json` 反序列化时自动为 null，无需数据迁移：

```json
{
  "Id": "abc123",
  "Title": "每日 B 站签到",
  "IsCompleted": false,
  "RepeatConfig": {
    "Frequency": 0,
    "StartDate": "2026-07-06T00:00:00",
    "EndDate": null,
    "CronExpression": "0 9 * * *"
  },
  "RepeatParentId": null
}
```

### 十、文件变更清单

| 文件 | 操作 | 说明 |
|------|------|------|
| `Core/Todo/TodoItem.cs` | 修改 | 新增 RepeatParentId, RepeatConfig, RepeatDay 字段 |
| `Core/Todo/RepeatConfig.cs` | 新增 | ERepeatFrequency 枚举 + RepeatConfig 类 |
| `Core/Todo/RepeatCalculator.cs` | 新增 | 日期计算纯函数 + Cron 表达式生成 |
| `Core/Todo/TodoStore.cs` | 修改 | 新增 AddRepeating, CompleteRepeating, CatchUp, GetRepeating 方法 |
| `Views/Todo/TodoView.xaml` | 修改 | 详情面板增加重复配置区、列表项增加重复图标、筛选栏增加"重复" |
| `Views/Todo/TodoView.xaml.cs` | 修改 | 处理重复配置 UI 逻辑、完成重复实例时自动生成下一个 |
| `Views/CompactToolboxWindow.xaml` | 修改 | 待办区增加"今日重复"分组 |
| `Views/CompactToolboxWindow.xaml.cs` | 修改 | 加载重复待办数据 |
| `Core/Tools/TodoTools.cs` | 修改 | add_todo/list_todos 增加重复参数 |
| `App.xaml.cs` | 修改 | 启动时调用 CatchUpRepeating + RegisterReminders |

### 十一、边界情况

| 场景 | 处理 |
|------|------|
| 应用关闭 3 天后打开 | CatchUpRepeating 为 07-06, 07-07, 07-08 各创建实例 |
| 实例未完成就到了下一天 | 旧实例保留（标记为逾期），新实例照常生成 |
| EndDate 到期 | CatchUp 和 Complete 都不生成新实例，模板保留但不再活跃 |
| 删除模板 | 同时删除所有未完成实例，已完成实例保留（历史记录） |
| 修改模板标题/描述 | 不影响已生成的实例（实例是独立快照） |
| 频繁切换应用 | TodoStore 已有 SemaphoreSlim 写锁保护，无并发问题 |

---

*设计方案 v1.0 — 2026-07-06*
