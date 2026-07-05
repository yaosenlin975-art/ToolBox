# Setuna 截图汇总 + 日报生成 — 技术方案

> 版本: v1.0 | 日期: 2026-07-04
> 状态: 设计阶段
> 关联: LLM-Assistant-Design.md Phase 4 (截图集成)

---

## 1. WPF 定时任务方案

### 1.1 方案对比

| 方案 | 精度 | 线程模型 | "每天指定时间" | 复杂度 | 推荐 |
|------|------|---------|---------------|--------|------|
| System.Timers.Timer | ms级 | 线程池 | 需手动计算间隔 | 低 | ❌ 不适合 |
| DispatcherTimer | ~16ms | UI线程 | 需手动计算间隔 | 低 | ❌ 不适合 |
| System.Threading.Timer | ms级 | 线程池 | 需手动计算间隔 | 低 | ❌ 不适合 |
| **Cron 表达式 + 循环 Timer** | 秒级 | 后台线程 | ✅ 原生支持 | **低** | **✅ 推荐** |
| Quartz.NET | ms级 | 线程池 | ✅ Cron原生支持 | 高 | ⚠️ 过重 |

### 1.2 推荐方案: 轻量 Cron 调度器

**不引入 Quartz.NET**。原因:
- Quartz.NET 是 300KB+ 的包，对 WPF 桌面应用过重
- 我们只需要 "每天 N 点执行"，不需要分布式调度
- 用 Cronos 库（~20KB, 纯计算，无依赖）+ System.Threading.Timer 即可

`xml
<!-- ToolBox.csproj 新增 -->
<PackageReference Include="Cronos" Version="0.8.4" />
`

**核心实现**:

`csharp
// Services/Scheduler/CronScheduler.cs
using Cronos;

namespace Setuna.Services.Scheduler;

public sealed class CronScheduler : IDisposable
{
    private readonly Dictionary<string, CronJob> _jobs = new();
    private Timer? _timer;
    private readonly object _lock = new();
    private bool _disposed;

    public event EventHandler<string>? JobTriggered;

    /// <summary>
    /// 注册一个 Cron 任务。expression 为标准 5 段 Cron。
    /// 示例: "0 9 * * *" = 每天 09:00
    /// </summary>
    public void Register(string jobId, string cronExpression, Action onTrigger)
    {
        var expression = CronExpression.Parse(cronExpression);
        _jobs[jobId] = new CronJob
        {
            JobId = jobId,
            Expression = expression,
            Action = onTrigger,
            NextRun = expression.GetNextOccurrence(DateTimeOffset.UtcNow)
        };
        RecalculateTimer();
    }

    public void Unregister(string jobId)
    {
        lock (_lock)
        {
            _jobs.Remove(jobId);
            RecalculateTimer();
        }
    }

    private void RecalculateTimer()
    {
        lock (_lock)
        {
            _timer?.Dispose();

            var nextRun = _jobs.Values
                .Where(j => j.NextRun.HasValue)
                .MinBy(j => j.NextRun!.Value)?.NextRun;

            if (!nextRun.HasValue) return;

            var delay = nextRun.Value - DateTimeOffset.UtcNow;
            var delayMs = Math.Max(0, delay.TotalMilliseconds);

            _timer = new Timer(OnTimerTick, null, (int)delayMs, Timeout.Infinite);
        }
    }

    private void OnTimerTick(object? state)
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            var dueJobs = _jobs.Values
                .Where(j => j.NextRun.HasValue && j.NextRun.Value <= now)
                .ToList();

            foreach (var job in dueJobs)
            {
                job.Action();
                job.NextRun = job.Expression.GetNextOccurrence(now);
                JobTriggered?.Invoke(this, job.JobId);
            }
        }
        RecalculateTimer();
    }

    /// <summary>
    /// 持久化所有任务状态到文件（应用关闭前调用）
    /// </summary>
    public SchedulerState SaveState() => new()
    {
        Jobs = _jobs.Select(j => new JobState
        {
            JobId = j.Key,
            CronExpression = j.Value.Expression.ToString(),
            LastRun = j.Value.LastRun?.ToString("O")
        }).ToList()
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer?.Dispose();
    }

    private sealed class CronJob
    {
        public required string JobId { get; init; }
        public required CronExpression Expression { get; init; }
        public required Action Action { get; init; }
        public DateTimeOffset? NextRun { get; set; }
        public DateTimeOffset? LastRun { get; set; }
    }
}

// 状态持久化模型
public class SchedulerState
{
    public List<JobState> Jobs { get; set; } = new();
}

public class JobState
{
    public string JobId { get; set; } = "";
    public string CronExpression { get; set; } = "";
    public string? LastRun { get; set; }
}
`

### 1.3 "每天指定时间" 的具体配置

`csharp
// 用法示例 — 每天 22:00 生成日报
scheduler.Register(
    jobId: "daily-report",
    cronExpression: "0 22 * * *",   // 每天 22:00
    onTrigger: () => OnDailyReport()
);

// 每周一 09:00 生成周报
scheduler.Register(
    jobId: "weekly-report",
    cronExpression: "0 9 * * 1",    // 每周一 09:00
    onTrigger: () => OnWeeklyReport()
);
`

### 1.4 持久化定时器状态

`
%AppData%/Setuna/
  └── scheduler_state.json     # 任务注册表
  └── reports/
      └── daily/
          └── 2026-07-04.md    # 日报
      └── weekly/
          └── 2026-W27.md      # 周报
`

**生命周期**:

`csharp
// App.xaml.cs
private CronScheduler _scheduler = new();

protected override void OnExit(ExitEventArgs e)
{
    // 1. 保存任务状态
    var state = _scheduler.SaveState();
    File.WriteAllText(statePath, JsonSerializer.Serialize(state));

    // 2. 确保当前生成任务完成
    _scheduler.Dispose();
    base.OnExit(e);
}

// 应用启动时恢复
private void RestoreScheduler()
{
    if (File.Exists(statePath))
    {
        var state = JsonSerializer.Deserialize<SchedulerState>(File.ReadAllText(statePath));
        // 重新注册任务...
    }
}
`

---

## 2. 截图行为分析方案

### 2.1 方案对比

| 方案 | 成本 | 隐私 | 准确度 | 速度 | 依赖 |
|------|------|------|--------|------|------|
| A: 纯 OCR → LLM | 高 (API调用) | ⚠️ 截图含隐私 | 中 (OCR丢布局) | 慢 | OCR库 |
| B: 元数据 → LLM | **极低** | ✅ 无截图内容 | **高** | **快** | **无** |
| C: 截图 + OCR + 元数据 | 最高 | ❌ 截图上传 | 最高 | 最慢 | OCR + 网络 |

### 2.2 推荐方案: B (元数据为主) + 可选 OCR 增强

**核心思路**: 利用已有的 WindowManager 和 WindowInfo，在截图时捕获元数据，不需要 OCR。

你已经有了:
- WindowManager.GetWindowInfo(hwnd) → 返回 WindowInfo{Handle, TitleName, ClassName, ...}
- WindowManager.Update() → 获取当前前台窗口

**元数据已经足够回答"用户在干什么"**:

| 元数据字段 | 价值 | 示例 |
|-----------|------|------|
| ClassName | **识别应用** | Chrome_WidgetWin_1 = Chrome, Notepad++ = Notepad++ |
| TitleName | **识别活动** | "Setuna - 技术方案" / "GitHub - repo" / "微信" |
| Timestamp | **时间线** | 截图时间点 |

**OCR 作为可选增强** — 只在用户主动请求"深度分析"时触发:

`csharp
// Models/ScreenshotMetadata.cs
public sealed class ScreenshotMetadata
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTime CapturedAt { get; init; } = DateTime.Now;
    public string WindowTitle { get; init; } = "";
    public string WindowClass { get; init; } = "";
    public string ApplicationName { get; init; } = "";   // 从 ClassName 映射
    public string? ImagePath { get; init; }               // 截图文件路径(可选)
    public string? OcrText { get; init; }                 // OCR文本(可选, 按需)
    public int ScreenWidth { get; init; }
    public int ScreenHeight { get; init; }
}
`

**应用名映射表** (从 ClassName 推断):

`csharp
public static class AppRecognizer
{
    private static readonly Dictionary<string, string> ClassToApp = new()
    {
        ["Chrome_WidgetWin_1"] = "Chrome 浏览器",
        ["MozillaWindowClass"] = "Firefox 浏览器",
        ["Notepad++"] = "Notepad++",
        ["Notepad"] = "记事本",
        ["VisualStudio"] = "Visual Studio",
        ["CabinetWClass"] = "文件资源管理器",
        ["WeChatMainWndForPC"] = "微信",
        ["ChatWnd"] = "微信聊天窗口",
        ["TelegramMainWnd"] = "Telegram",
        ["SlackWindowClass"] = "Slack",
        ["CursorUI"] = "Cursor IDE",
        ["Editor"] = "VS Code",
    };

    /// <summary>
    /// 从 WindowClass 推断应用名。
    /// 未知类名返回 ClassName 本身，交给 LLM 判断。
    /// </summary>
    public static string Recognize(string windowClass, string windowTitle)
    {
        // 精确匹配
        if (ClassToApp.TryGetValue(windowClass, out var app))
            return app;

        // 模糊匹配 — 标题里包含常见关键词
        if (windowTitle.Contains("Visual Studio Code")) return "VS Code";
        if (windowTitle.Contains("GitHub")) return "GitHub";
        if (windowTitle.Contains("Stack Overflow")) return "Stack Overflow";

        return windowClass; // 未知, 原样返回
    }
}
`

### 2.3 截图行为采集器

`csharp
// Services/ScreenshotTracker.cs
public sealed class ScreenshotTracker
{
    private readonly List<ScreenshotMetadata> _entries = new();
    private readonly string _storagePath;
    private readonly object _lock = new();

    public ScreenshotTracker(string storagePath)
    {
        _storagePath = storagePath;
        Directory.CreateDirectory(storagePath);
        LoadExistingEntries();
    }

    /// <summary>
    /// 截图时调用 — 自动采集元数据
    /// </summary>
    public ScreenshotMetadata RecordCapture(BitmapSource image)
    {
        var wm = WindowManager.Instance.Update();
        var foreground = wm.CurrentForegroundWindow;

        var metadata = new ScreenshotMetadata
        {
            WindowTitle = foreground.TitleName,
            WindowClass = foreground.ClassName,
            ApplicationName = AppRecognizer.Recognize(
                foreground.ClassName, foreground.TitleName),
            ImagePath = SaveImage(image),
            ScreenWidth = (int)SystemParameters.PrimaryScreenWidth,
            ScreenHeight = (int)SystemParameters.PrimaryScreenHeight
        };

        lock (_lock)
        {
            _entries.Add(metadata);
            PersistEntry(metadata);
        }

        return metadata;
    }

    /// <summary>
    /// 获取指定时间范围内的截图记录
    /// </summary>
    public IReadOnlyList<ScreenshotMetadata> GetEntries(
        DateTime from, DateTime to) =>
        _entries
            .Where(e => e.CapturedAt >= from && e.CapturedAt <= to)
            .OrderBy(e => e.CapturedAt)
            .ToList();

    private string SaveImage(BitmapSource image)
    {
        var filename = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.png";
        var path = Path.Combine(_storagePath, filename);

        using var fs = new FileStream(path, FileMode.Create);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(fs);

        return path;
    }

    private void PersistEntry(ScreenshotMetadata entry)
    {
        var indexPath = Path.Combine(_storagePath, "index.jsonl");
        var json = JsonSerializer.Serialize(entry);
        File.AppendAllText(indexPath, json + Environment.NewLine);
    }

    private void LoadExistingEntries()
    {
        var indexPath = Path.Combine(_storagePath, "index.jsonl");
        if (!File.Exists(indexPath)) return;

        foreach (var line in File.ReadLines(indexPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (JsonSerializer.Deserialize<ScreenshotMetadata>(line) is { } entry)
                _entries.Add(entry);
        }
    }
}
`

### 2.4 何时触发 OCR?

**默认不触发 OCR**。只在以下场景启用:

1. **用户主动要求** — "分析这张截图的具体内容"
2. **LLM 判断需要** — 元数据不足以推断行为时，LLM 通过 Tool 调用 extract_text_from_screenshot(imageId)
3. **特定应用** — 代码编辑器(IDE)中的截图，OCR 提取代码片段有价值

`csharp
// LLM Tool — 按需 OCR
[Tool("从截图中提取文字，仅在元数据不足时使用")]
public static string ExtractTextFromScreenshot(
    [ToolParam("截图ID")] string screenshotId)
{
    // 懒加载 Tesseract 或 Windows.Media.Ocr
    // 返回 OCR 文本
}
`

---

## 3. 日报/周报生成

### 3.1 数据汇总格式

给 LLM 的输入不是原始截图，而是结构化的行为日志:

`json
{
  "date": "2026-07-04",
  "period": "daily",
  "screenshots": [
    {
      "time": "09:15",
      "app": "Chrome 浏览器",
      "title": "Setuna - 技术方案 - GitHub"
    },
    {
      "time": "10:30",
      "app": "VS Code",
      "title": "Setuna - CronScheduler.cs"
    },
    {
      "time": "14:00",
      "app": "微信",
      "title": "工作群"
    },
    {
      "time": "15:45",
      "app": "Chrome 浏览器",
      "title": "Stack Overflow - WPF timer best practice"
    }
  ],
  "total_captures": 23,
  "unique_apps": ["Chrome 浏览器", "VS Code", "微信", "Notepad++"],
  "time_distribution": {
    "Chrome 浏览器": "5.2h",
    "VS Code": "3.1h",
    "微信": "0.8h"
  }
}
`

### 3.2 Prompt 设计

`
系统提示词 (日报):

你是 Setuna 截图助手的行为分析模块。根据用户一天的截图行为数据，
生成简洁的工作日报。

规则:
1. 只基于提供的数据总结，不要编造
2. 将相似活动归类（如"浏览多个技术页面"→"技术调研"）
3. 识别主要工作主题
4. 输出结构化的 Markdown 日报
5. 使用中文

输出格式:
---
## 📅 YYYY-MM-DD 工作日报

### 主要工作
- [活动类别1]: 具体描述
- [活动类别2]: 具体描述

### 时间分布
| 活动 | 时长 | 占比 |
|------|------|------|

### 工作亮点
- 一句话总结今天最重要的事

### 备注
- （如有异常活动或空白时段，简单提及）
---
`

`
系统提示词 (周报):

你是 Setuna 截图助手的行为分析模块。根据一周的日报数据，
生成工作总结周报。

规则:
1. 从日报中提炼周维度的关键活动
2. 按项目/主题归类，而非按时间
3. 识别趋势（如"本周后半段集中做 X"）
4. 输出结构化的 Markdown 周报
5. 使用中文

输出格式:
---
## 📊 YYYY 第 WW 周 工作周报

### 本周概览
- 总截图数: X 张
- 活跃天数: X 天
- 主要工具: Chrome, VS Code, ...

### 重点工作
#### 1. [项目/主题名]
- 活动描述
- 涉及工具
- 时间估算

### 趋势观察
- ...

### 下周建议
- ...
---
`

### 3.3 生成流程

`csharp
// Services/ReportGenerator.cs
public sealed class ReportGenerator
{
    private readonly ScreenshotTracker _tracker;
    private readonly ILLMProvider _llm;

    public ReportGenerator(ScreenshotTracker tracker, ILLMProvider llm)
    {
        _tracker = tracker;
        _llm = llm;
    }

    /// <summary>
    /// 生成日报 — 调用 LLM
    /// </summary>
    public async Task<string> GenerateDailyReport(DateTime date)
    {
        var from = date.Date;
        var to = from.AddDays(1);
        var entries = _tracker.GetEntries(from, to);

        if (entries.Count == 0)
            return $"## 📅 {date:yyyy-MM-dd}\n\n今天没有截图记录。";

        // 构建结构化输入
        var input = BuildDailyInput(date, entries);

        var response = await _llm.ChatAsync(
            systemPrompt: DailyReportPrompt,
            userMessage: JsonSerializer.Serialize(input, _jsonOptions));

        // 保存报告
        var reportPath = GetReportPath("daily", date);
        await File.WriteAllTextAsync(reportPath, response);

        return response;
    }

    /// <summary>
    /// 生成周报 — 先收集 7 天日报，再汇总
    /// </summary>
    public async Task<string> GenerateWeeklyReport(DateTime weekStart)
    {
        var weekEnd = weekStart.AddDays(7);
        var allEntries = _tracker.GetEntries(weekStart, weekEnd);

        if (allEntries.Count == 0)
            return $"## 📊 {weekStart:yyyy} 第 {ISOWeek.GetWeekOfYear(weekStart)} 周\n\n本周无记录。";

        var input = BuildWeeklyInput(weekStart, allEntries);

        var response = await _llm.ChatAsync(
            systemPrompt: WeeklyReportPrompt,
            userMessage: JsonSerializer.Serialize(input, _jsonOptions));

        var reportPath = GetReportPath("weekly", weekStart);
        await File.WriteAllTextAsync(reportPath, response);

        return response;
    }

    private object BuildDailyInput(DateTime date, IReadOnlyList<ScreenshotMetadata> entries)
    {
        var groups = entries
            .GroupBy(e => e.ApplicationName)
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => new { e.CapturedAt.TimeOfDay, e.WindowTitle }).ToList());

        return new
        {
            date = date.ToString("yyyy-MM-dd"),
            period = "daily",
            screenshots = entries.Select(e => new
            {
                time = e.CapturedAt.ToString("HH:mm"),
                app = e.ApplicationName,
                title = e.WindowTitle
            }),
            total_captures = entries.Count,
            unique_apps = entries.Select(e => e.ApplicationName).Distinct().ToList(),
            time_distribution = groups.ToDictionary(
                g => g.Key,
                g => g.Count() + " captures")
        };
    }

    private string GetReportPath(string type, DateTime date)
    {
        var subDir = type == "daily" ? "daily" : "weekly";
        var filename = type == "daily"
            ? $"{date:yyyy-MM-dd}.md"
            : $"{date:yyyy}-W{ISOWeek.GetWeekOfYear(date):D2}.md";

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Setuna", "reports", subDir);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, filename);
    }
}
`

### 3.4 与 CronScheduler 集成

`csharp
// 在 App.xaml.cs 或 LLM 助手初始化时
var tracker = new ScreenshotTracker(
    Path.Combine(appDataPath, "screenshots"));
var reporter = new ReportGenerator(tracker, llmProvider);
var scheduler = new CronScheduler();

// 注册定时任务
scheduler.Register("daily-report", "0 22 * * *",
    async () => await reporter.GenerateDailyReport(DateTime.Today));

scheduler.Register("weekly-report", "0 9 * * 1",
    async () => await reporter.GenerateWeeklyReport(
        DateTime.Today.AddDays(-(int)DateTime.Today.DayOfWeek + 1)));
`

---

## 4. 整体架构集成

`
┌────────────────────────────────────────────────────────┐
│                   Setuna (WPF)                          │
│                                                        │
│  ┌──────────┐  ┌──────────────────┐  ┌──────────────┐ │
│  │ 截图操作  │  │ ScreenshotTracker│  │ CronScheduler│ │
│  │ (现有)   │──│ 采集元数据        │  │ 定时触发     │ │
│  └──────────┘  └───────┬──────────┘  └──────┬───────┘ │
│                        │                     │         │
│                        ▼                     ▼         │
│              ┌──────────────────┐  ┌──────────────┐   │
│              │ index.jsonl      │  │ ReportGenerator│  │
│              │ (行为日志)        │  │ 汇总 + 调 LLM │  │
│              └──────────────────┘  └──────┬───────┘   │
│                                           │           │
│                                           ▼           │
│                                   ┌──────────────┐   │
│                                   │ reports/*.md  │   │
│                                   │ (日报/周报)   │   │
│                                   └──────────────┘   │
└────────────────────────────────────────────────────────┘
`

---

## 5. 关键决策总结

| 决策点 | 选择 | 理由 |
|--------|------|------|
| 定时器 | Cronos + System.Threading.Timer | 轻量, 满足"每天指定时间", 无多余依赖 |
| 行为分析 | 元数据为主 (方案B) | 零 API 成本, 隐私安全, 已有基础设施 |
| OCR | 可选增强, 默认关闭 | 降低开销, 避免隐私风险 |
| 报告生成 | LLM (通过已有 Provider 层) | 利用 Phase 1-2 已实现的 LLM 能力 |
| 数据存储 | JSONL (日志) + Markdown (报告) | JSONL 追加写高效, Markdown 人类可读 |
| 持久化 | %AppData%/Setuna/ | 符合 Windows 应用规范, 与现有架构一致 |

---

## 6. 新增依赖

| 包名 | 用途 | 大小 |
|------|------|------|
| Cronos | Cron 表达式解析 | ~20KB |
| Microsoft.Windows.CsWin32 | 可选: 更多 Win32 API | ~100KB |

**不引入的依赖**:
- ~~Quartz.NET~~ — 过重
- ~~Tesseract OCR~~ — 默认不需要
- ~~Puppeteer~~ — 不需要浏览器自动化

---

## 7. 实施优先级

1. **ScreenshotTracker** — 与截图操作绑定, 立即可用
2. **AppRecognizer** — 扩展现有 ClassName 映射
3. **CronScheduler** — 为定时任务提供基础
4. **ReportGenerator** — 依赖 LLM Provider (Phase 1-2)
5. **OCR 增强** — 按需添加, 优先级最低
