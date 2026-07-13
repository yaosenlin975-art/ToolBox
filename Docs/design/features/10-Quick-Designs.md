# P2+P3 功能快速设计

> 以下 11 个功能为 P2（中期）和 P3（远期）优先级，每个功能提供：功能要点、技术可行性、简化接口定义、实现难度。

---

## P2-01：日历视图

- **优先级**：P2
- **实现难度**：中
- **来源竞品**：TickTick（6 种日历视图）

### 功能要点
为待办管理增加日历视图，将 TodoItem 的 DueDate 映射到月/周/日日历网格中。支持拖拽待办到日历日期以快速调整截止日期（Timeboxing）。日历视图嵌入工作台 todos 页面，作为列表视图之外的第二种查看方式。

### 技术可行性
- 自定义 WPF 月历控件（非 WinForms MonthCalendar），基于 `UniformGrid` 7×6 构建
- 参考 NuGet `WpfCalendar` 或自建（推荐自建，避免引入依赖）
- 迷你日历的周视图和日视图使用 `ItemsControl` + 时间轴布局
- 拖拽支持使用 WPF `DragDrop` + `Thumb`

### 简化接口

```csharp
// Core/Todo/CalendarViewData.cs
public class CalendarDay
{
    public DateTime Date { get; set; }
    public List<TodoItem> DueItems { get; set; } = new();
    public bool IsToday { get; set; }
    public bool IsCurrentMonth { get; set; }
}

// Views/Todo/CalendarView.xaml.cs
public partial class CalendarView : UserControl
{
    public CalendarView SetDate(DateTime month);
    public CalendarView RefreshFromStore();
    public event Action<DateTime>? DayClicked;
    public event Action<TodoItem, DateTime>? TodoDropped;
}
```

---

## P2-02：上下文感知快捷操作

- **优先级**：P2
- **实现难度**：中
- **来源竞品**：Quicker（上下文面板）、uTools（超级面板）

### 功能要点
根据用户当前活动窗口/选中内容，在迷你窗口或浮动面板中推荐相关操作。例如：在 VS Code 中选中代码 → 推荐"格式化"/"发送到 AI"/"保存为片段"；在浏览器中选中文本 → 推荐"翻译"/"搜索"/"保存为待办"。通过 `GetForegroundWindow` + 进程名识别当前应用。

### 技术可行性
- `GetForegroundWindow` + `GetWindowText` + `GetWindowModuleFileName`（已存在于 NativeMethods）
- 选中文本获取：`SendMessage(WM_COPY)` + `Clipboard.GetText()`（需临时备份剪贴板）
- 当前应用 → 推荐规则映射表（JSON 配置）

### 简化接口

```csharp
// Core/ContextAware/ContextAwareService.cs
public class ContextAwareService
{
    public static ContextAwareService Instance { get; }

    public ContextInfo GetCurrentContext();       // 前台窗口 + 进程名
    public string? GetSelectedText();             // 获取选中文本（临时复制）
    public List<SuggestedAction> GetSuggestions(ContextInfo context);

    public event Action<List<SuggestedAction>>? SuggestionsReady;
}

public class SuggestedAction
{
    public string Name { get; set; }
    public string Icon { get; set; }
    public Action Execute { get; set; }
}
```

---

## P2-03：AI 基于全数据个性化

- **优先级**：P2
- **实现难度**：中
- **来源竞品**：Flomo（AI Agent 读取全部笔记）

### 功能要点
将 ToolBox 的 LLM 助手与用户全量本地数据深度整合：AI 对话时可访问用户的待办列表、截图历史（含标签/OCR文字）、代码片段、剪贴板历史、定时日报。系统提示词动态注入近期用户活动摘要，使 AI 能回答"我上周三截了什么图？"、"帮我总结最近的待办完成情况"等问题。

### 技术可行性
- 扩展现有 `ISystemPromptBuilder`，注入动态上下文生成方法
- 各 Store 提供轻量摘要接口（不加载全量数据到内存）
- 使用 `MemoryStore`（SQLite）存储跨会话的用户偏好和长期记忆
- 参考 Flomo 的"冷门内容自动推送"：定期扫描旧截图/待办，通过迷你窗口提示回顾

### 简化接口

```csharp
// Core/Llm/IPersonalizedPromptBuilder.cs
public interface IPersonalizedPromptBuilder : ISystemPromptBuilder
{
    string BuildUserContextSection();              // 近期活动摘要
    string BuildMemorySection();                   // 长期记忆
}

// Core/Memory/PersonalizationService.cs
public class PersonalizationService
{
    public static PersonalizationService Instance { get; }

    public UserActivitySummary GetRecentActivity(int days = 7);
    public List<MemoryEntry> GetRelevantMemories(string query);
    public Task<List<string>> GetStaleItemsForReview(); // 冷门内容回顾
}
```

---

## P2-04：步骤捕获

- **优先级**：P2
- **实现难度**：高
- **来源竞品**：Snagit（Step Capture——自动记录操作步骤生成教程）

### 功能要点
启动"步骤捕获模式"后，ToolBox 监听用户每次鼠标点击，自动截取点击前后的屏幕画面，为每步添加序号标注和点击位置高亮。最终生成一组带编号的步骤截图教程。适用于制作软件操作文档、Bug 复现步骤记录。

### 技术可行性
- 全局鼠标钩子 `WH_MOUSE_LL` 检测点击事件
- 每次点击前后 200ms 各截一帧（点击前 = 操作前状态，点击后 = 操作结果）
- 自动在截图上叠加步骤序号（圆形编号，右下角）
- 高亮点击位置（红色半透明圆圈，200ms 脉冲动画后消失）

### 简化接口

```csharp
// Core/Screenshot/StepCaptureEngine.cs
public class StepCaptureEngine : IDisposable
{
    public StepCaptureEngine Start();
    public StepCaptureEngine Stop();
    public StepCaptureEngine Pause();
    public List<StepCaptureFrame> GetFrames();

    public event Action<StepCaptureFrame>? FrameCaptured;
}

public class StepCaptureFrame
{
    public int StepNumber { get; set; }
    public BitmapSource BeforeClick { get; set; }
    public BitmapSource AfterClick { get; set; }
    public Point ClickPosition { get; set; }
    public IntPtr TargetWindow { get; set; }
}
```

---

## P2-05：智能打码

- **优先级**：P2
- **实现难度**：高
- **来源竞品**：Snagit（AI Smart Redact——自动检测并打码敏感信息）

### 功能要点
截图分享前自动检测并遮挡敏感信息：邮箱地址、电话号码、IP 地址、信用卡号、人脸、二维码等。用户可手动标记额外区域。打码方式支持模糊（Gaussian Blur）和马赛克（像素化）两种。

### 技术可行性
- 文本类敏感信息：OCR 提取文字后用正则匹配 → 在对应区域应用打码
- 人脸检测：可选集成 OpenCvSharp4（NuGet 包）进行 Haar Cascade 人脸检测
- 打码渲染：`System.Drawing.Graphics` 高斯模糊或像素化处理
- 依赖 P0 OCR 模块作为前置条件

### 简化接口

```csharp
// Core/Screenshot/SmartRedactService.cs
public class SmartRedactService
{
    public Task<List<RedactRegion>> DetectSensitiveRegionsAsync(BitmapSource screenshot);

    public BitmapSource ApplyRedaction(BitmapSource original, List<RedactRegion> regions,
                                        ERedactStyle style = ERedactStyle.Blur);
}

public class RedactRegion
{
    public Rect BoundingBox { get; set; }
    public string DetectedType { get; set; }     // "email" / "phone" / "ip" / "face"
    public double Confidence { get; set; }
}
```

---

## P2-06：番茄钟

- **优先级**：P2
- **实现难度**：低
- **来源竞品**：TickTick（番茄专注）

### 功能要点
内置番茄钟计时器：25 分钟专注 + 5 分钟休息循环。开始时可选择关联一条待办（"我正在做 XXX"）。计时期间迷你窗口显示倒计时圆环。专注结束后自动记录到待办进度。支持自定义时长。与定时日报联动——统计当日番茄数。

### 技术可行性
- `System.Timers.Timer` 或 `PeriodicTimer` 驱动倒计时
- 迷你窗口增加"番茄钟模式"视图（圆环倒计时 → 替换默认待办列表）
- 专注记录持久化到 `%AppData%/ToolBox/pomodoro_log.json`
- 与待办联动：专注完成 → 自动增加关联待办的 Progress

### 简化接口

```csharp
// Core/Todo/PomodoroTimer.cs
public class PomodoroTimer
{
    public static PomodoroTimer Instance { get; }

    public PomodoroTimer Start(string? todoId = null, int focusMinutes = 25, int breakMinutes = 5);
    public PomodoroTimer Pause();
    public PomodoroTimer Resume();
    public PomodoroTimer Stop();
    public PomodoroState CurrentState { get; }    // Idle / Focusing / OnBreak

    public event Action<int, int>? Tick;          // (remainingSeconds, totalSeconds)
    public event Action<EPomodoroPhase>? PhaseChanged;
}
```

---

## P3-01：屏幕冻结模式

- **优先级**：P3
- **实现难度**：中
- **来源竞品**：CleanShot X（Freeze Screen——冻结画面后从容截图）

### 功能要点
一键冻结当前屏幕画面：截取全屏 → 作为静态图片覆盖显示 → 用户可从容选择截图区域。用于捕获瞬态 UI（下拉菜单、tooltip、hover 效果）——这些 UI 在按下热键时会消失。冻结后用户可用截图工具精确选区。

### 技术可行性
- 全屏截图：`Graphics.CopyFromScreen` 覆盖所有显示器
- 全屏静态覆盖窗口：多显示器各自创建全屏 `WindowStyle=None, Topmost=True` 窗口，显示截图
- 覆盖窗口上叠加 CaptureWindow 选区控件
- ESC 关闭所有覆盖窗口

### 简化接口

```csharp
// Core/Screenshot/ScreenFreezeService.cs
public class ScreenFreezeService
{
    public ScreenFreezeService Freeze();
    public ScreenFreezeService Unfreeze();
    public bool IsFrozen { get; }
}
```

---

## P3-02：QR 码识别

- **优先级**：P3
- **实现难度**：低
- **来源竞品**：ShareX（QR 码识别）

### 功能要点
截图后可识别其中的二维码/条形码，解析结果（URL/文本）可直接打开或复制。支持从截图历史中的图片和剪贴板中的图片识别。

### 技术可行性
- NuGet `ZXing.Net`（0.16.9+）成熟稳定，纯 .NET 实现
- 截图完成 → 工具栏"QR"按钮 → ZXing 解码 → 弹出结果
- 支持 QR Code / Data Matrix / Code 128 / EAN-13 等主流码制

### 简化接口

```csharp
// Core/Screenshot/QrCodeService.cs
public class QrCodeService
{
    public QrCodeResult? Decode(BitmapSource image);
}

public class QrCodeResult
{
    public string Text { get; set; }
    public string Format { get; set; }        // "QR_CODE" / "CODE_128" ...
    public Rect BoundingBox { get; set; }
    public bool IsUrl { get; }
}
```

---

## P3-03：窗口置顶快捷键

- **优先级**：P3
- **实现难度**：低
- **来源竞品**：PowerToys（Always On Top——Win+Ctrl+T）

### 功能要点
任意窗口一键置顶：选中一个窗口后按 `Ctrl+Shift+T`（可自定义），该窗口切换为 Topmost 状态。再次按取消置顶。补充 ToolBox 仅能置顶贴图窗口的限制，适用于需要对比参考的应用场景。

### 技术可行性
- `SetWindowPos(hwnd, HWND_TOPMOST, ...)` — 纯 P/Invoke，极简实现
- 获取当前前台窗口：`GetForegroundWindow()`（已有）

### 简化接口

```csharp
// Core/Window/WindowPinner.cs
public static class WindowPinner
{
    public static bool TogglePin(IntPtr hwnd);     // true=已置顶 false=已取消
    public static bool IsPinned(IntPtr hwnd);
    public static bool ToggleForegroundWindow();   // 切换前台窗口的置顶状态
}
```

---

## P3-04：习惯打卡

- **优先级**：P3
- **实现难度**：中
- **来源竞品**：TickTick（习惯打卡 + 热力图可视化）

### 功能要点
在待办系统基础上增加习惯追踪：用户可创建每日/每周重复的习惯（如"每天运动30分钟"、"每周读一本书"），每天完成后打卡。用热力图可视化打卡记录（类似 GitHub contribution graph）。与可重复待办有协同——习惯可关联一条每日待办模板。

### 技术可行性
- 数据模型扩展：`HabitItem`（Id/Name/Frequency/TargetCount/Streak）
- 与可重复待办共享 RepeatConfig 逻辑
- 热力图：自定义 WPF `ItemsControl`，7 列 × N 行色块（参考 GitHub 绿格子）
- 持久化：`%AppData%/ToolBox/habits.json`

### 简化接口

```csharp
// Core/Todo/HabitItem.cs
public class HabitItem
{
    public string Id { get; set; }
    public string Name { get; set; }
    public EFrequency Frequency { get; set; }    // Daily / Weekly / Monthly
    public int TargetCount { get; set; }
    public List<DateTime> CheckedDates { get; set; } = new();
    public int CurrentStreak { get; }
    public int BestStreak { get; }
}

// Core/Todo/HabitStore.cs
public class HabitStore
{
    public static HabitStore Instance { get; }
    public HabitStore CheckIn(string habitId);
    public List<HabitItem> GetTodaysHabits();
    public event Action? HabitsChanged;
}
```

---

## P3-05：云分享/图床上传

- **优先级**：P3
- **实现难度**：高
- **来源竞品**：CleanShot X（Cloud 一键分享链接）、ShareX（80+ 云端上传目标）

### 功能要点
截图后可一键上传到图床/云存储，自动获取分享链接并复制到剪贴板。支持配置多个上传目标（如 Imgur、阿里云 OSS、腾讯云 COS、自定义 WebDAV）。与截图动作链集成（"截图→上传→复制链接"）。

### 技术可行性
- 需要后端服务或第三方 API。最简单的 MVP：集成 Imgur API（免费，有速率限制）
- 架构设计为插件式上传器接口，后续可扩展更多目标
- 依赖外部服务的可用性和用户配置
- 当前设计阶段仅定义接口，实现延后

### 简化接口

```csharp
// Core/Upload/IUploadTarget.cs
public interface IUploadTarget
{
    string Name { get; }
    Task<UploadResult> UploadAsync(byte[] imageData, string fileName, IProgress<int> progress);
}

public class UploadResult
{
    public bool IsSuccess { get; set; }
    public string? Url { get; set; }
    public string? DeleteUrl { get; set; }
}

// Core/Upload/UploadService.cs
public class UploadService
{
    public static UploadService Instance { get; }
    public UploadService RegisterTarget(IUploadTarget target);
    public Task<UploadResult> UploadAsync(string targetName, byte[] imageData, string fileName);
}
```

---

## 依赖汇总

| 功能 | 新增 NuGet | 必要性 |
|------|-----------|--------|
| QR 码识别 | `ZXing.Net` ≥ 0.16.9 | 必要（自实现不现实） |
| 智能打码（人脸检测） | `OpenCvSharp4` + `OpenCvSharp4.runtime.win` | 可选（可降级为纯OCR+正则） |
| 日历视图 | 无（自建控件） | — |
| 上下文感知 | 无 | — |
| AI 个性化 | 无（复用 LLM） | — |
| 步骤捕获 | 无（P/Invoke） | — |
| 番茄钟 | 无 | — |
| 屏幕冻结 | 无 | — |
| 窗口置顶 | 无（P/Invoke） | — |
| 习惯打卡 | 无 | — |
| 云分享 | 可能需 `RestSharp` 或 `Flurl` | 按需引入 |
