# 待办智能日期识别

- **优先级**：P1
- **实现难度**：中
- **预估工作量**：4 人天
- **来源竞品**：TickTick（输入文本自动提取时间）

---

## 功能描述

为 ToolBox 待办创建流程增加智能日期识别：用户在输入待办标题时，系统自动检测文本中包含的自然语言日期/时间表达（如"明天下午三点开会"、"下周五提交报告"、"每周一晨跑"），自动提取并填入 DueDate 字段。同时支持从 LLM 对话中批量提取待办项及其日期。

核心交互：
1. **输入时实时解析**：在 TodoView 的快速添加输入框中，用户输入文本时实时高亮识别到的时间表达
2. **自动填入**：识别到的日期自动设为 DueDate，识别到的重复规则自动填入 RepeatConfig
3. **手动调整**：用户可点击识别结果进行修正（如"明天"→选择"明天上午"还是"明天下午"）
4. **LLM 协同**：对话中提及的待办项，AI 自动解析日期后调用 `add_todo` 创建

---

## 技术方案

### 两层解析策略

**第一层：正则表达式（快速、离线）**

覆盖常见中文日期表达：

| 模式 | 示例 | 解析结果 |
|------|------|---------|
| 相对日期 | 今天/明天/后天/大后天/昨天 | `DateTime.Today + N` |
| 星期表达 | 周一/下周三/这周五 | 基于当前日期计算 |
| 绝对日期 | 7月15日/7.15/2026-07-15 | `DateTime.Parse` |
| 时间表达 | 下午3点/15:00/上午10点半 | `TimeSpan` |
| 组合表达 | 明天下午三点 | `DateTime.Today+1 + 15:00` |
| 重复表达 | 每天/每周一/每月15号/每年1月1日 | `RepeatConfig` |

**第二层：LLM 辅助解析（高精度、需要模型）**

对于复杂表达（"下下周二的上午"、"每个季度最后一个工作日"），正则无法覆盖的情况，调用轻量级 LLM 进行解析。可以使用本地模型（如 Ollama 小模型）避免网络延迟。

### 依赖评估

无新增 NuGet 依赖。纯 C# 正则 + 日期计算。

可选用：`Chronic`（.NET 自然语言日期解析库），但已多年未更新，建议自建。

---

## 模块设计

### 文件位置

```
Core/
├── Todo/
│   ├── SmartDateParser.cs            # 智能日期解析（正则 + 日期计算）
│   ├── DateExpression.cs             # 解析结果模型
│   └── RepeatPatternParser.cs        # 重复规则解析（每天/每周/每月）
Views/
├── Todo/
│   └── DateSuggestionPopup.xaml/.cs   # 日期建议浮动提示（内嵌 TodoView）
```

### 核心接口

```csharp
// Core/Todo/SmartDateParser.cs
namespace ToolBox.Core.Todo;

public enum EDateExpressionType
{
    DueDate,        // 截止日期
    StartDate,      // 开始日期
    Reminder,       // 提醒时间
    RepeatRule      // 重复规则
}

public class DateExpression
{
    public EDateExpressionType Type { get; set; }
    public string RawText { get; set; } = string.Empty;    // 原始匹配文本
    public int StartIndex { get; set; }                    // 在原文本中的起始位置
    public int Length { get; set; }                        // 匹配长度
    public DateTime? ParsedDate { get; set; }              // 解析后的日期
    public TimeSpan? ParsedTime { get; set; }              // 解析后的时间（如有）
    public string? RepeatConfigJson { get; set; }          // 如果是重复规则
    public double Confidence { get; set; }                 // 置信度 0-1
}

public class ParseResult
{
    public List<DateExpression> Expressions { get; set; } = new();
    public string CleanedText { get; set; } = string.Empty; // 移除日期表达后的纯文本
    public DateTime? SuggestedDueDate { get; set; }         // 合并后的建议截止日期
}

public class SmartDateParser
{
    public static SmartDateParser Instance { get; }

    /// <summary>解析文本中的日期表达（仅正则层）</summary>
    public ParseResult Parse(string inputText);

    /// <summary>解析文本中的日期表达（含 LLM 辅助，异步）</summary>
    public Task<ParseResult> ParseWithLlmAsync(string inputText, ChatManager? chatManager);

    /// <summary>从解析结果计算最可能的截止日期</summary>
    public DateTime? ResolveDueDate(List<DateExpression> expressions, DateTime? referenceDate = null);

    /// <summary>预热正则缓存</summary>
    public SmartDateParser WarmUp();
}

// Core/Todo/RepeatPatternParser.cs
namespace ToolBox.Core.Todo;

public class RepeatPatternParser
{
    /// <summary>从文本中提取重复规则</summary>
    /// <param name="text">如 "每周一上午晨跑"</param>
    /// <returns>RepeatConfig JSON 结构和清理后文本</returns>
    public (string? repeatConfigJson, string cleanedText) Parse(string text);
}
```

### LLM Tool 扩展

在 `TodoTools` 中增强 `add_todo` 方法，自动调用 `SmartDateParser`：

```csharp
// Core/Tools/TodoTools.cs 增强
[Tool("add_todo", "创建新的 Todo 任务（支持自然语言日期，如'明天下午三点开会'）")]
public static string AddTodo(
    [ToolParam("任务标题（可含日期如'明天下午三点开会'）")] string title,
    [ToolParam("任务描述")] string description = "",
    [ToolParam("优先级 0=普通 1=重要 2=紧急")] int priority = 0,
    [ToolParam("标签（逗号分隔）")] string tags = "")
{
    // 1. SmartDateParser.Parse(title) → 提取 DueDate + RepeatConfig
    // 2. 清理过的 title 作为任务标题
    // 3. 创建 TodoItem 时自动填入 DueDate
}

[Tool("parse_date", "解析文本中的自然语言日期表达")]
public static string ParseDate(
    [ToolParam("包含日期表达的文本，如'下周五下午两点开会'")] string text);
```

---

## 数据流

```
用户在 TodoView 快速添加框输入:
"明天下午三点和产品经理讨论需求 #会议"

    │ 每次输入变化（300ms 防抖）
    ▼
SmartDateParser.Parse(inputText)
    │
    ├─ 正则匹配:
    │   DateExpression { RawText="明天下午三点", ParsedDate=DateTime.Today+1d, ParsedTime=15:00 }
    │   CleandText = "和产品经理讨论需求 #会议"
    │
    ▼
UI 反馈:
    ├─ 输入框中"明天下午三点"高亮为蓝色下划线（AccentBrush）
    ├─ DateSuggestionPopup 浮层出现在输入框下方:
    │   ┌──────────────────────────────┐
    │   │ 📅 截止日期: 明天 15:00      │
    │   │    [修正: 上午/下午/具体时间]  │
    │   │    [清除日期]                │
    │   └──────────────────────────────┘
    └─ DueDate 自动填入: 2026-07-14 15:00:00

用户确认创建 → TodoStore.Add(item)
    ├─ item.Title = "和产品经理讨论需求" (清理后)
    ├─ item.DueDate = DateTime.Today.AddDays(1).AddHours(15)
    └─ item.Tags = ["会议"]

复杂表达 → ParseWithLlmAsync → LLM 返回结构化 JSON → 合并到 ParseResult
```

---

## UI 设计要点

### 输入框日期高亮
- 在 TodoView 快速添加 `TextBox` 中，通过 `TextChanged` 事件触发解析
- 使用 `RichTextBox` 或自定义 `TextBlock` 叠加层实现富文本高亮
- 高亮样式：蓝色下划线（`AccentBrush`, 1.5px 粗）+ 浅蓝背景（`AccentSoftBrush`）
- 复用：`InputField` Style 作为基础

### DateSuggestionPopup（建议浮层）
- 定位：输入框正下方，宽度与输入框一致
- 样式：`CardStyle` Border，`ShadowMd` 阴影
- 内容：
  - 识别结果行："📅 截止日期: 明天 15:00" (13px, TextPrimaryBrush)
  - 快速修正按钮组：`[上午 09:00]` `[下午 15:00]` `[晚上 20:00]`（FilterTabStyle）
  - `[清除日期]` 文字按钮（TextTertiaryBrush）
- 自动消失：输入框失去焦点或用户点击外部时关闭

### 设置页面
- SettingsView 的 "待办" 相关设置中新增：
  - `☑ 启用智能日期识别`（默认开启）
  - `☑ 使用 AI 辅助解析复杂日期`（默认关闭，需要 API）
  - 日期解析语言偏好（中文/English）

---

## 与现有架构的集成

| 集成点 | 方式 |
|--------|------|
| **TodoView** | 快速添加框集成 SmartDateParser，实时解析和高亮 |
| **TodoItem 模型** | 扩展字段 `DueDate` / `RepeatConfig` 已有设计，直接填入 |
| **TodoStore** | add 方法无需改动，传入已解析好的 DueDate |
| **LLM 工具** | `TodoTools.AddTodo` 自动调用 SmartDateParser；新增 `ParseDate` 工具 |
| **ChatManager** | 对话中 AI 提及的待办，LLM 调用 add_todo 时自动解析 |
| **正则预热** | 应用启动时 `SmartDateParser.WarmUp()` 编译正则，首次解析 <1ms |

---

## 风险与注意事项

1. **中文歧义**："下周"在不同语境下可能指"下周一"或"7天后"。缓解：默认解析为"下周的同一天"，UI 提示供用户确认
2. **多日期冲突**：输入"周三或周四"无法确定具体日期。缓解：取第一个匹配项，其余在 SuggestionPopup 中列出供选择
3. **跨月计算**："下个月15号"需正确处理月末边界。缓解：使用 `DateTime.AddMonths(1)` 的 .NET 标准行为
4. **LLM 延迟**：ParseWithLlmAsync 需网络调用，可能 2-5 秒。缓解：默认仅用正则层（<1ms），LLM 辅助作为高级选项
5. **与现有 DatePicker 的交互**：自动解析的 DueDate 需与手动选择的 DatePicker 联动。缓解：智能解析结果仅在用户尚未手动选择日期时自动填入
