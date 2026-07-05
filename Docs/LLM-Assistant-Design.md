# Setuna LLM 桌面助手 — 设计文档

> 版本: v1.0 | 日期: 2026-07-04  
> 状态: 设计阶段

---

## 1. 项目概述

Setuna 是一款 WPF 截图/贴图工具箱。本设计为其新增 **微型 LLM 桌面助手** 功能模块，包含三大子系统：

| 子系统 | 功能 | 优先级 |
|--------|------|--------|
| LLM Chat | 多会话聊天、提示词注入、记忆归纳、工具调用、上下文压缩 | P0 |
| TodoList | 增删改查、LLM 可调用 | P0 |
| 截图集成 | 右键"发起对话"快速建立 LLM 会话 | P1 |

**技术约束**: .NET 8.0 WPF, C#, 无外部框架依赖（仅 System.Drawing.Common + Microsoft.Data.Sqlite）。

---

## 2. 架构总览

```
┌─────────────────────────────────────────────────────────┐
│                    Setuna (WPF Shell)                    │
├──────────┬──────────────┬───────────────┬───────────────┤
│ 截图模块  │  LLM Chat    │   TodoList    │   截图集成    │
│ (现有)   │  (新增)       │   (新增)      │   (新增)      │
├──────────┴──────────────┴───────────────┴───────────────┤
│                    Core / Services                       │
│  ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌────────────┐  │
│  │ ToolReg  │ │ Session  │ │ Memory   │ │ Context    │  │
│  │ istry    │ │ Manager  │ │ Manager  │ │ Compressor │  │
│  └──────────┘ └──────────┘ └──────────┘ └────────────┘  │
├─────────────────────────────────────────────────────────┤
│                  LLM Provider Layer                      │
│  ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌────────────┐  │
│  │ OpenAI   │ │ DeepSeek │ │ Qwen     │ │ Ollama     │  │
│  │ Provider │ │ Provider │ │ Provider │ │ Provider   │  │
│  └──────────┘ └──────────┘ └──────────┘ └────────────┘  │
├─────────────────────────────────────────────────────────┤
│                    Data Layer                            │
│  ┌──────────┐ ┌──────────┐ ┌──────────┐                 │
│  │ Sessions │ │ Memory   │ │ Todos    │                 │
│  │ (JSON)   │ │ (SQLite) │ │ (JSON)   │                 │
│  └──────────┘ └──────────┘ └──────────┘                 │
└─────────────────────────────────────────────────────────┘
```

---

## 3. LLM Chat 子系统

### 3.1 多会话管理与隔离

**设计原则**: 每个会话 = 独立的 Agent 实例，完全隔离，不共享状态。

```
ChatManager (单例)
 ├── ChatSession[]           // 所有会话列表
 │    ├── SessionId          // GUID
 │    ├── Title              // 会话标题（自动生成/用户设定）
 │    ├── Agent              // 独立的 Agent 实例
 │    ├── Messages[]         // 消息历史（内存）
 │    ├── Memory             // 独立的记忆上下文
 │    ├── CreatedAt          // 创建时间
 │    └── UpdatedAt          // 最后更新时间
 └── ActiveSessionId         // 当前激活会话
```

**会话隔离规则**:
- 每个会话拥有独立的 `Agent` 实例（含独立的 System Prompt、Tools、Memory）
- 切换会话时，完全替换当前 Agent 上下文，不共享任何状态
- 会话间消息历史互不可见（除非用户主动导出）

**持久化**:
- 会话元数据 → `%AppData%/Setuna/sessions/index.json`
- 消息历史 → `%AppData%/Setuna/sessions/{SessionId}/messages.json`
- 每个消息追加写入，崩溃安全

### 3.2 Agent 架构

```
Agent (每个会话一个实例)
 ├── SystemPrompt            // 系统提示词（从模板 + 记忆构建）
 ├── Provider                // LLM Provider 实例
 ├── Tools                   // 可用工具集（含 [Tool] 标注的方法）
 ├── Session                 // 消息历史
 ├── Memory                  // 自动归纳记忆
 └── ContextCompressor       // 上下文压缩器
```

**Agent 执行循环**:

```
1. 接收用户消息
2. 组装上下文: SystemPrompt + Memory + Messages[] + 新消息
3. ContextCompressor.CheckAndCompress() // 必要时压缩
4. Provider.Chat(messages, tools) → 流式响应
5. 如果响应包含 tool_calls:
   a. 通过 ToolRegistry 查找并执行工具
   b. 将工具结果追加到 Messages
   c. 继续循环（最多 maxToolRounds 轮）
6. 将最终响应追加到 Messages
7. Memory.Analyze(messages) // 后台异步归纳
8. 持久化消息
```

### 3.3 系统提示词模板

```markdown
你是 Setuna 桌面助手，一个集成在截图工具箱中的 AI 助手。

## 你的能力
- 回答用户问题
- 管理 TodoList（增删改查）
- 使用工具完成任务

## 记忆
{MemoryBlock}

## 规则
- 回复简洁、直接
- 使用工具前先思考是否必要
- 工具调用失败时告知用户原因
```

### 3.4 消息流式传输

使用 `IAsyncEnumerable<ChatChunk>` 实现流式响应：

```csharp
public record ChatChunk
{
    public string Delta { get; init; }                    // 增量文本
    public ToolCallInfo? ToolCall { get; init; }          // 工具调用信息
    public bool IsComplete { get; init; }                 // 是否完成
    public string? FinishReason { get; init; }
}

public record ToolCallInfo
{
    public string Id { get; init; }
    public string Name { get; init; }
    public string Arguments { get; init; }                // JSON 字符串
}
```

### 3.5 LLM Provider 抽象

```csharp
public interface ILlmProvider
{
    string Name { get; }
    IAsyncEnumerable<ChatChunk> Chat(
        IList<ChatMessage> messages,
        IList<ToolDefinition>? tools = null,
        CancellationToken ct = default);
}

public record ChatMessage
{
    public string Role { get; init; }         // system/user/assistant/tool
    public string? Content { get; init; }
    public IList<ToolCallInfo>? ToolCalls { get; init; }
    public string? ToolCallId { get; init; }  // tool 消息对应的调用 ID
    public string? ImageUrl { get; init; }    // 图片消息（截图集成）
}

public record ToolDefinition
{
    public string Name { get; init; }
    public string Description { get; init; }
    public string ParametersSchema { get; init; }  // JSON Schema
}
```

**Provider 实现**:
- `OpenAiProvider` — 兼容 OpenAI API 格式（含 DeepSeek、Qwen 等兼容接口）
- `OllamaProvider` — 本地模型（Ollama API）
- `AnthropicProvider` — Claude 系列

---

## 4. ToolAttribute 工具系统

### 4.1 设计目标

用 `[Tool]` 标注静态方法，自动发现并暴露为 LLM 可用工具。无需手动注册，反射自动扫描。

### 4.2 核心设计

```csharp
/// <summary>
/// 标注静态方法为 LLM 可调用工具
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ToolAttribute : Attribute
{
    public string Name { get; }
    public string Description { get; }

    public ToolAttribute(string name, string description)
    {
        Name = name;
        Description = description;
    }
}

/// <summary>
/// 标注参数的描述信息（用于生成 JSON Schema）
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class ToolParamAttribute : Attribute
{
    public string Description { get; }

    public ToolParamAttribute(string description)
    {
        Description = description;
    }
}
```

### 4.3 使用示例

```csharp
public static class TodoTools
{
    [Tool("add_todo", "添加一个新的待办事项")]
    public static string AddTodo(
        [ToolParam("待办事项标题")] string title,
        [ToolParam("待办事项描述，可选")] string description = "")
    {
        var todo = TodoManager.Instance.Add(title, description);
        return $"已添加待办: {todo.Title} (ID: {todo.Id})";
    }

    [Tool("list_todos", "列出所有待办事项")]
    public static string ListTodos()
    {
        var todos = TodoManager.Instance.GetAll();
        if (todos.Count == 0) return "当前没有待办事项";
        return string.Join("\n", todos.Select(t =>
            $"[{(t.IsCompleted ? "x" : " ")}] {t.Title} (ID: {t.Id})"));
    }

    [Tool("complete_todo", "标记待办事项为已完成")]
    public static string CompleteTodo(
        [ToolParam("待办事项的唯一ID")] string todoId)
    {
        var success = TodoManager.Instance.Complete(todoId);
        return success ? $"已完成待办 {todoId}" : $"未找到待办 {todoId}";
    }

    [Tool("delete_todo", "删除待办事项")]
    public static string DeleteTodo(
        [ToolParam("待办事项的唯一ID")] string todoId)
    {
        var success = TodoManager.Instance.Delete(todoId);
        return success ? $"已删除待办 {todoId}" : $"未找到待办 {todoId}";
    }

    [Tool("update_todo", "更新待办事项的标题或描述")]
    public static string UpdateTodo(
        [ToolParam("待办事项的唯一ID")] string todoId,
        [ToolParam("新的标题，不填则不更新")] string? title = null,
        [ToolParam("新的描述，不填则不更新")] string? description = null)
    {
        var success = TodoManager.Instance.Update(todoId, title, description);
        return success ? $"已更新待办 {todoId}" : $"未找到待办 {todoId}";
    }
}
```

### 4.4 ToolRegistry — 反射发现与注册

```csharp
public class ToolRegistry
{
    private readonly Dictionary<string, ToolDefinition> _tools = new();
    private readonly Dictionary<string, MethodInfo> _handlers = new();

    /// <summary>
    /// 扫描指定程序集，注册所有 [Tool] 标注的静态方法
    /// </summary>
    public void RegisterFromAssembly(Assembly assembly)
    {
        var methods = assembly.GetTypes()
            .SelectMany(t => t.GetMethods(
                BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<ToolAttribute>() != null);

        foreach (var method in methods)
        {
            var attr = method.GetCustomAttribute<ToolAttribute>()!;
            var parameters = method.GetParameters();

            var properties = new Dictionary<string, object>();
            var required = new List<string>();

            foreach (var param in parameters)
            {
                var paramAttr = param.GetCustomAttribute<ToolParamAttribute>();
                var paramSchema = new Dictionary<string, object>
                {
                    ["type"] = GetJsonType(param.ParameterType)
                };
                if (paramAttr != null)
                    paramSchema["description"] = paramAttr.Description;
                if (!param.IsOptional)
                    required.Add(param.Name!);

                properties[param.Name!] = paramSchema;
            }

            var schema = JsonSerializer.Serialize(new
            {
                type = "object",
                properties,
                required
            }, new JsonSerializerOptions { WriteIndented = true });

            _tools[attr.Name] = new ToolDefinition
            {
                Name = attr.Name,
                Description = attr.Description,
                ParametersSchema = schema
            };
            _handlers[attr.Name] = method;
        }
    }

    public string Execute(string toolName, Dictionary<string, object> arguments)
    {
        if (!_handlers.TryGetValue(toolName, out var method))
            throw new ToolNotFoundException(toolName);

        var paramInfos = method.GetParameters();
        var args = new object[paramInfos.Length];

        for (int i = 0; i < paramInfos.Length; i++)
        {
            if (arguments.TryGetValue(paramInfos[i].Name!, out var value))
                args[i] = Convert.ChangeType(value, paramInfos[i].ParameterType);
            else if (paramInfos[i].HasDefaultValue)
                args[i] = paramInfos[i].DefaultValue!;
            else
                throw new MissingParameterException(paramInfos[i].Name!);
        }

        var result = method.Invoke(null, args);
        return result?.ToString() ?? string.Empty;
    }

    public IReadOnlyList<ToolDefinition> GetAllTools() =>
        _tools.Values.ToList().AsReadOnly();
}
```

### 4.5 内置工具集

| 工具名 | 来源类 | 类型 | 说明 |
|--------|--------|------|------|
| `add_todo` | TodoTools | 写入 | 添加待办 |
| `list_todos` | TodoTools | 只读 | 列出待办 |
| `complete_todo` | TodoTools | 写入 | 完成待办 |
| `delete_todo` | TodoTools | 写入 | 删除待办 |
| `update_todo` | TodoTools | 写入 | 更新待办 |
| `screenshot_info` | ScreenshotTools | 只读 | 获取截图信息 |
| `capture_screen` | ScreenshotTools | 写入 | 触发截图 |

---

## 5. 记忆系统

### 5.1 三层记忆架构

```
┌─────────────────────────────────────────┐
│  Layer 1: 对话上下文 (Session Messages)  │  ← 活跃窗口内的消息
├─────────────────────────────────────────┤
│  Layer 2: 自动归纳记忆 (Auto Memory)    │  ← LLM 主动归纳的持久事实
├─────────────────────────────────────────┤
│  Layer 3: 用户指令记忆 (User Memory)    │  ← 用户明确要求记住的内容
└─────────────────────────────────────────┘
```

### 5.2 自动归纳机制

**触发时机**: 每次对话结束后，后台异步分析最近 N 轮对话。

**归纳流程**:

```
1. 收集最近 N 轮对话（默认 10 轮）
2. 调用 LLM 分析，提取:
   - 用户偏好（如：喜欢简洁回复）
   - 项目状态（如：正在做 XX 功能）
   - 事实信息（如：API Key 是 XX）
   - 任务进展（如：Todo #1 已完成）
3. 去重合并：与已有记忆对比，更新/新增/删除
4. 持久化到 SQLite
```

**归纳提示词**:

```markdown
分析以下对话，提取值得长期记住的信息。

已有记忆:
{existingMemories}

最近对话:
{recentMessages}

请输出 JSON 数组，每个元素包含:
- key: 简短标识（英文，如 "user-prefer-verbose"）
- content: 记忆内容（中文，如 "用户喜欢详细的技术解释"）
- importance: 重要性 1-5（5最重要）
- action: add（新增）/ update（更新已有）/ remove（删除过时的）
```

### 5.3 记忆存储 (SQLite)

**Schema**:

```sql
CREATE TABLE memories (
    id TEXT PRIMARY KEY,
    key TEXT NOT NULL,
    content TEXT NOT NULL,
    importance INTEGER DEFAULT 3,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    session_id TEXT
);

CREATE INDEX idx_memories_key ON memories(key);
CREATE INDEX idx_memories_importance ON memories(importance DESC);
```

**记忆注入到 System Prompt**:

```markdown
## 记忆
以下是你从之前对话中归纳的信息:

- [重要] 用户喜欢简洁的代码示例
- [普通] 当前项目使用 .NET 8.0
- [低] 用户曾问过 Docker 相关问题
```

### 5.4 上下文压缩

**触发条件**: 消息总 token 数超过上下文窗口的 70%。

**压缩策略**:

```
┌──────────────────────────────────────┐
│ 原始消息: [msg1, msg2, ..., msgN]    │
│                                      │
│ 压缩后:                              │
│ [msg1, .., msgK(摘要), msgN-3, msgN] │
│                                      │
│ K = 前 30% 保留完整                  │
│ 中间 40% 压缩为 LLM 摘要            │
│ 后 30% 保留完整                      │
└──────────────────────────────────────┘
```

### 5.5 代码示例

```csharp
public async Task CompressAsync(IList<ChatMessage> messages)
{
    var totalTokens = EstimateTokens(messages);
    if (totalTokens < _maxTokens * 0.7) return;

    var headCount = (int)(messages.Count * 0.3);
    var tailCount = (int)(messages.Count * 0.3);

    var head = messages.Take(headCount).ToList();
    var middle = messages.Skip(headCount)
                         .Take(messages.Count - headCount - tailCount)
                         .ToList();
    var tail = messages.TakeLast(tailCount).ToList();

    var summaryPrompt = "请用 200 字以内总结以下对话的关键信息:\n\n" +
        string.Join("\n", middle.Select(m =>
            $"{m.Role}: {m.Content}"));

    var summary = await _provider.ChatAsync(new List<ChatMessage>
    {
        new() { Role = "user", Content = summaryPrompt }
    });

    messages.Clear();
    messages.AddRange(head);
    messages.Add(new ChatMessage
    {
        Role = "assistant",
        Content = $"[上下文摘要] {summary}"
    });
    messages.AddRange(tail);
}
```

---

## 6. TodoList 子系统

### 6.1 数据模型

```csharp
public class TodoItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsCompleted { get; set; }
    public int Priority { get; set; }       // 0=普通, 1=重要, 2=紧急
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? CompletedAt { get; set; }
    public string? SessionId { get; set; }  // 关联的会话 ID
}
```

### 6.2 TodoManager

```csharp
public class TodoManager
{
    private static TodoManager? _instance;
    public static TodoManager Instance => _instance ??= new TodoManager();

    private readonly List<TodoItem> _todos = new();
    private readonly string _storagePath;

    private TodoManager()
    {
        _storagePath = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData),
            "Setuna", "todos.json");
        Load();
    }

    public TodoItem Add(string title, string description = "")
    {
        var todo = new TodoItem { Title = title, Description = description };
        _todos.Add(todo);
        Save();
        return todo;
    }

    public bool Complete(string todoId)
    {
        var todo = _todos.FirstOrDefault(t => t.Id == todoId);
        if (todo == null) return false;
        todo.IsCompleted = true;
        todo.CompletedAt = DateTime.Now;
        Save();
        return true;
    }

    public bool Delete(string todoId)
    {
        var removed = _todos.RemoveAll(t => t.Id == todoId);
        if (removed > 0) Save();
        return removed > 0;
    }

    public bool Update(string todoId, string? title = null, string? description = null)
    {
        var todo = _todos.FirstOrDefault(t => t.Id == todoId);
        if (todo == null) return false;
        if (title != null) todo.Title = title;
        if (description != null) todo.Description = description;
        Save();
        return true;
    }

    public List<TodoItem> GetAll() => _todos.ToList();
    public List<TodoItem> GetActive() => _todos.Where(t => !t.IsCompleted).ToList();
    public List<TodoItem> GetCompleted() => _todos.Where(t => t.IsCompleted).ToList();
}
```

### 6.3 TodoList 视图

**位置**: MainWindow 侧边栏可折叠面板。

```
┌─ TodoList ─────────────────────┐
│ [+ 添加]  [全部|进行中|已完成]  │
├────────────────────────────────┤
│ ☐ 任务1              [编辑][删除]│
│ ☑ 任务2              [编辑][删除]│
│ ☐ 任务3              [编辑][删除]│
├────────────────────────────────┤
│ 共 3 项 | 进行中 2 | 已完成 1  │
└────────────────────────────────┘
```

---

## 7. 截图集成

### 7.1 右键菜单扩展

在 `ScrapWindow.ShowContextMenu()` 中新增菜单项：

```csharp
menu.Items.Add(new Separator());
var chatMenuItem = new MenuItem { Header = "发起对话" };
chatMenuItem.Click += (s, e) =>
{
    var scrap = DataContext as ScrapWindow;
    var image = scrap.SourceBitmap;
    ChatManager.Instance.CreateSessionWithImage(image);
};
menu.Items.Add(chatMenuItem);
```

### 7.2 图片消息处理

```csharp
public class ChatMessage
{
    public string Role { get; init; }
    public string? Content { get; init; }
    public byte[]? ImageData { get; init; }
    public string ImageFormat { get; init; } = "png";
}
```

**发送流程**:

```
1. 用户右键截图 → 点击"发起对话"
2. 创建新会话，自动生成标题（如 "截图对话 2026-07-04 14:30"）
3. 将截图保存为临时文件
4. 发送系统消息: "用户发起了一次截图对话"
5. 发送图片消息: 附带默认提问 "请描述这张截图的内容"
6. 自动切换到聊天界面，显示新会话
```

---

## 8. 界面设计

### 8.1 主界面布局

```
┌─ Setuna ──────────────────────────────────────────────┐
│ ┌─ Sidebar ─┐ ┌─ Chat Area ─────────────────────────┐ │
│ │ 📷 截图   │ │ 会话标题: 截图对话                    │ │
│ │ 📝 Todo   │ │                                      │ │
│ │ 💬 Chat   │ │ [用户] 请帮我分析这张截图             │ │
│ │           │ │ [截图附件]                            │ │
│ │           │ │ [助手] 这张截图显示了一个...           │ │
│ │           │ │                                      │ │
│ │           │ ├──────────────────────────────────────┤ │
│ │           │ │ [输入框...]          [发送] [工具]   │ │
│ └───────────┘ └──────────────────────────────────────┘ │
└───────────────────────────────────────────────────────┘
```

### 8.2 XAML 示例

```xml
<Grid>
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="200"/>
        <ColumnDefinition Width="*"/>
    </Grid.ColumnDefinitions>

    <ListBox Grid.Column="0" ItemsSource="{Binding Sessions}">
        <ListBox.ItemTemplate>
            <DataTemplate>
                <StackPanel Margin="4">
                    <TextBlock Text="{Binding Title}" FontWeight="SemiBold"/>
                    <TextBlock Text="{Binding LastMessagePreview}" Opacity="0.5" FontSize="11"/>
                </StackPanel>
            </DataTemplate>
        </ListBox.ItemTemplate>
    </ListBox>

    <ScrollViewer Grid.Column="1" ItemsSource="{Binding CurrentMessages}" ScrollToEnd="True">
        <ItemsControl.ItemTemplate>
            <DataTemplate>
                <Border Background="{Binding RoleColor}" CornerRadius="8" Padding="12" Margin="4">
                    <StackPanel>
                        <Image Source="{Binding ImageSource}" MaxWidth="400"/>
                        <TextBlock Text="{Binding Content}" TextWrapping="Wrap"/>
                    </StackPanel>
                </Border>
            </DataTemplate>
        </ItemsControl.ItemTemplate>
    </ScrollViewer>

    <Grid Grid.Column="1" VerticalAlignment="Bottom" Margin="8">
        <TextBox AcceptsReturn="True" Height="60" Text="{Binding InputText, UpdateSourceTrigger=PropertyChanged}"/>
        <Button Content="发送" Command="{Binding SendCommand}" HorizontalAlignment="Right" VerticalAlignment="Bottom"/>
    </Grid>
</Grid>
```

---

## 9. 目录结构规划

```
Project/
├── Core/
│   ├── Llm/                          # LLM 核心模块
│   │   ├── Agent.cs                   # Agent 执行循环
│   │   ├── ChatManager.cs             # 多会话管理
│   │   ├── ChatSession.cs             # 单个会话
│   │   ├── ChatMessage.cs             # 消息模型
│   │   ├── ChatChunk.cs               # 流式响应块
│   │   └── ContextCompressor.cs       # 上下文压缩
│   ├── Providers/                     # LLM Provider
│   │   ├── ILlmProvider.cs            # Provider 接口
│   │   ├── OpenAiProvider.cs          # OpenAI 兼容
│   │   ├── OllamaProvider.cs          # 本地模型
│   │   └── ProviderSettings.cs        # Provider 配置
│   ├── Tools/                         # 工具系统
│   │   ├── ToolAttribute.cs           # [Tool] 标注
│   │   ├── ToolParamAttribute.cs      # [ToolParam] 标注
│   │   ├── ToolRegistry.cs            # 反射发现与注册
│   │   ├── ToolDefinition.cs          # 工具定义
│   │   ├── TodoTools.cs              # TodoList 工具
│   │   └── ScreenshotTools.cs         # 截图相关工具
│   ├── Memory/                        # 记忆系统
│   │   ├── MemoryManager.cs           # 记忆管理
│   │   ├── MemoryStore.cs             # SQLite 存储
│   │   └── MemoryItem.cs              # 记忆项模型
│   └── Todo/                          # TodoList
│       ├── TodoManager.cs             # 增删改查
│       ├── TodoItem.cs                # 数据模型
│       └── TodoStore.cs               # 持久化
├── Views/
│   ├── Chat/
│   │   ├── ChatWindow.xaml/.cs        # 聊天主窗口
│   │   ├── ChatView.xaml/.cs          # 聊天消息区
│   │   ├── SessionListView.xaml/.cs   # 会话列表
│   │   ├── MessageBubble.xaml/.cs     # 消息气泡
│   │   ├── ToolCallCard.xaml/.cs      # 工具调用卡片
│   │   └── ChatInput.xaml/.cs         # 输入区
│   └── Todo/
│       └── TodoPanel.xaml/.cs         # TodoList 侧边栏面板
└── Docs/
    └── LLM-Assistant-Design.md       # 本文档
```

---

## 10. 实施计划

### Phase 1: 基础框架 (1-2 周)

- [ ] LLM Provider 抽象层 + OpenAI 兼容实现
- [ ] `[Tool]` + `[ToolParam]` 标注定义
- [ ] ToolRegistry 反射发现与执行
- [ ] ChatManager 多会话管理
- [ ] 基础聊天界面（无流式）

### Phase 2: 核心功能 (1-2 周)

- [ ] `IAsyncEnumerable<ChatChunk>` 流式响应
- [ ] 工具调用执行循环（tool_call → execute → 回传结果）
- [ ] TodoManager + TodoTools 集成
- [ ] TodoList 视图

### Phase 3: 记忆与压缩 (1 周)

- [ ] SQLite 记忆存储（MemoryStore）
- [ ] 后台异步归纳机制
- [ ] 上下文压缩（LLM 摘要）
- [ ] 记忆注入到 System Prompt

### Phase 4: 截图集成 (1 周)

- [ ] ScrapWindow 右键菜单"发起对话"
- [ ] 图片消息处理（base64 / URL）
- [ ] 截图上下文自动注入

### Phase 5: 打磨优化 (1 周)

- [ ] 会话标题自动生成
- [ ] 错误处理与重试机制
- [ ] 全局快捷键绑定
- [ ] 多语言支持（zh-CN / en-US）

---

## 11. 依赖分析

### 新增 NuGet 包

| 包名 | 用途 | 大小 |
|------|------|------|
| Microsoft.Data.Sqlite | SQLite 记忆存储 | ~2MB |
| System.Text.Json | JSON 序列化 | 内置 |

### 与现有模块的耦合点

| 现有模块 | 耦合方式 | 改动程度 |
|----------|----------|----------|
| ScrapWindow | 右键菜单新增"发起对话" | 低 |
| MainWindow | 侧边栏新增 Todo/Chat 面板 | 中 |
| SetunaOption | 新增 LLM Provider 配置 | 低 |
| CacheManager | 无需改动 | 无 |
| ScrapBook | 无需改动 | 无 |

---

## 12. 安全考虑

- **API Key 存储**: 使用 `ProtectedData` (DPAPI) 加密存储，不明文写入配置文件
- **工具调用权限**: `[Tool]` 方法需显式标注 `[ToolAttribute]`，不自动暴露所有静态方法
- **记忆数据**: SQLite 文件使用用户级文件权限，不跨用户共享
- **网络请求**: Provider 层统一处理超时（30s）、重试（3次）、错误码映射
- **工具执行循环保护**: 最多 `maxToolRounds`（默认 10）轮，防止无限循环

---

## 附录 A: 参考项目架构对比

| 维度 | DeepSeek-Reasonix | QwenPaw | Setuna (本设计) |
|------|-------------------|---------|-----------------|
| 技术栈 | Go + Wails + React | Python + Tauri + React | C# WPF |
| 会话隔离 | Controller 模式 | Workspace + ContextVar | Agent 实例隔离 |
| 记忆层数 | 4 层 | 3 层 | 3 层 |
| 工具发现 | Registry 接口注册 | Toolkit 注册 | [Tool] 反射发现 |
| 上下文压缩 | 渐进式 (soft→snip→compact) | ScrollContext 滚动淘汰 | LLM 摘要压缩 |
| 持久化 | JSONL 文件 | SQLite + JSON | SQLite + JSON |

## 附录 B: 术语表

| 术语 | 定义 |
|------|------|
| Agent | LLM 会话执行器，包含 Provider、Tools、Session |
| Session | 一个独立的消息历史上下文 |
| Provider | LLM API 调用的抽象层 |
| Tool | 暴露给 LLM 的可调用函数 |
| Memory | 从对话中归纳的持久化信息 |
| Context | 当前发送给 LLM 的完整上下文 |
| Compressor | 上下文过长时的压缩机制 |
| Chunk | 流式响应中的一个增量数据块 |