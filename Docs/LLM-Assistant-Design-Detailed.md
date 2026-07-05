# Setuna LLM 桌面助手 — 细化设计文档

> 版本: v2.6 | 日期: 2026-07-04  
> 状态: 设计阶段（已确认 + 审查修复 v2 → v2.3（Round1: 24项 + Round2: 15项 + Round3: 6项 + Round4: 4项 + Round5: 3项 + Round6: 2项））

---

## 设计决策记录

| # | 决策项 | 选择 | 理由 |
|---|--------|------|------|
| 1 | 上下文压缩 | 混合（Snip + LLM 摘要） | 先两级上线 |
| 2 | 会话持久化 | 注册表 + 独立消息文件 | 崩溃只丢一个 turn |
| 3 | 上下文长度 | 用户配置，默认 131072 | 简单直接 |
| 4 | 文件操作 | 用户指定目录 + 白名单 | 渐进式信任 |
| 5 | 写操作安全 | 白名单免确认 + 非白名单弹窗 | 安全且灵活 |
| 6 | 文件工具 | 8 个 CRUD 工具（read/write/exists/list/mkdir/delete/copy/move） | 覆盖常用操作 |
| 7 | LLM Provider | OpenAI兼容+Ollama+Anthropic+免费模型发现 | 全量覆盖 |
| 8 | 会话标题 | 首条消息截取 → LLM自动生成 → 用户改过锁定 | 智能 |
| 9 | TodoList | 扩展字段 + 独立窗口 + 置顶迷你窗口 | 功能完整 |
| 10 | OCR | Windows.Media.Ocr（零依赖） | 系统内置 |
| 11 | 定时截图 | 用户自定义频率（5min~2hr） | 灵活 |
| 12 | 行为日志 | JSONL 文件，每天一个 | 零依赖 |

---

## 1. 多会话管理

### 1.1 数据模型

```csharp
public class ChatSession
{
    public string Id { get; set; }
    public string Title { get; set; }
    public bool IsTitleLocked { get; set; }
    [JsonIgnore] // 运行时字段，不参与 chats.json 注册表序列化。sessions/{id}.json 使用独立 DTO（ChatSessionData）序列化消息
    public List<ChatMessage> Messages { get; set; }
    public string Status { get; set; }          // idle / running / paused
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class ChatMessage
{
    public string Id { get; set; }
    public string Role { get; set; }            // system/user/assistant/tool
    public string? Content { get; set; }
    public string? ImagePath { get; set; }      // 截图文件路径（非 byte[]）
    public IList<ToolCallInfo>? ToolCalls { get; set; }
    public string? ToolCallId { get; set; }
    public DateTime Timestamp { get; set; }
}

public class ToolCallInfo
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Arguments { get; set; }       // JSON 字符串
    public string? Result { get; set; }
    public bool IsError { get; set; }
}
```

**图片存储**：保存到 `sessions/images/{sessionId}/`，消息中存文件路径，不内联 byte[]。

**多模态序列化**：Provider 层负责将 `ChatMessage.ImagePath` 转为 API 格式（如 OpenAI 的 `image_url` content block、Anthropic 的 `image` content block）。Agent 层不处理序列化细节。

### 1.2 标题生成策略

1. 创建时：截取首条消息前 20 字
2. 第 3 轮后：后台调 LLM（"用5个字以内概括"）
3. 用户改过 → `IsTitleLocked = true` → 不再覆盖

### 1.3 持久化

```
%AppData%/Setuna/
├── chats.json                    // 注册表（元数据）
├── sessions/
│   ├── {id}.json                 // 消息历史
│   └── images/{sessionId}/       // 截图文件
├── Data/
│   ├── memory.db                 // SQLite 记忆
│   ├── screenshots/{date}/       // 定时截图图片（按日期分目录）
│   └── activity/{date}.jsonl     // 行为日志（每行一条窗口使用记录）
├── todos.json                    // TodoList 持久化
├── providers.json                // Provider 配置（API Key DPAPI 加密）
└── file_whitelist.json           // 文件操作白名单
```

原子写入：.tmp → `Flush(true)` → rename，断电最多丢最近一个 turn。所有写入通过 `SemaphoreSlim(1, 1)` 保护。每 turn 结束 snapshot + 运行中每 30s 自动保存。

### 1.4 隔离规则

- 同一时间只允许一个活跃 Agent 运行
- 切换会话时暂停当前 Agent，加载目标会话上下文
- 每个会话独立 Agent 实例（独立 System Prompt、Tools、Memory）

**Agent 暂停/恢复机制**：

| 步骤 | 行为 |
|------|------|
| 暂停 | 向当前 Agent 的 `CancellationTokenSource` 发送取消信号，终止剩余轮次 |
| 持久化 | 已完成的轮次消息在 Agent 所有轮次结束后统一持久化（若消费者中途取消，已完成的消息仍通过 PersistMessagesAsync 保存） |
| 恢复 | 从持久化消息重建上下文，不支持「断点续答」——取消时进行中的 LLM 响应作废 |
| 冲突 | 暂停未完成时新 Agent 不启动，UI 显示「请等待当前操作完成」 |

### 1.5 Agent 执行循环

```csharp
public class Agent
{
    private readonly ILlmProvider _provider;
    private readonly ToolRegistry _tools;
    private readonly ContextCompressor _compressor;
    private readonly ISystemPromptBuilder _promptBuilder;
    private CancellationTokenSource? _cts;

    public async IAsyncEnumerable<ChatChunk> RunAsync(
        string userMessage, [EnumeratorCancellation] CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cts = linkedCts;
        var messages = BuildMessages(userMessage);
        await _compressor.CheckAndCompressAsync(messages);

        try
        {
        for (int round = 0; round < 10; round++)
        {
            linkedCts.Token.ThrowIfCancellationRequested();
            ChatChunk? last;
            try
            {
                var response = _provider.ChatAsync(messages, _tools.GetAllTools(), linkedCts.Token);
                last = null;
                await foreach (var chunk in response) { last = chunk; yield return chunk; }
            }
            catch (Exception ex)
            {
                // Provider 不可恢复异常（网络超时、API 错误等）
                yield return new ChatChunk { Content = $"[LLM 调用失败: {ex.Message}]" };
                break;
            }

            if (last?.ToolCall == null) break;

            string result;
            try
            {
                result = _tools.Execute(last.ToolCall.Name, ParseArgs(last.ToolCall.Arguments));
            }
            catch (Exception ex)
            {
                result = $"[工具执行异常: {ex.Message}]";
            }

            messages.Add(new ChatMessage { Role = "assistant", ToolCalls = [last.ToolCall] });
            messages.Add(new ChatMessage
            {
                Role = "tool",
                Content = string.IsNullOrEmpty(result) ? "[工具未返回结果]" : result,
                ToolCallId = last.ToolCall.Id
            });
        }
        }
        finally
        {
            await PersistMessagesAsync(messages);
        }
    }

    private static Dictionary<string, object?> ParseArgs(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    public void Pause() { try { _cts?.Cancel(); } catch (ObjectDisposedException) { } }
}
```

**状态机**：`idle → running → tool_executing → running → idle`，切换会话时 `paused`。

### 1.6 ISystemPromptBuilder 接口

```csharp
public interface ISystemPromptBuilder
{
    string Build();
    string BuildWithMemory(IReadOnlyList<string> memories);
}
```

**默认实现** `DefaultSystemPromptBuilder`：拼接静态模板 + 工具 JSON Schema + 记忆文本。Phase 3 升级为 `MemoryPromptBuilder`，从 SQLite 查询相关记忆注入。

### 1.7 System Prompt 模板

```markdown
你是 Setuna 桌面助手，一个集成在截图工具箱中的 AI 助手。

## 可用工具
{ToolList}
// ToolList = 工具的完整 JSON Schema 列表，由 ToolRegistry.GetAllToolSchemas() 动态生成

## 你的能力
- 回答用户问题
- 管理 TodoList（增删改查）
- 操作文件系统（读/写/删除/复制/移动/建目录/检查存在/列目录，需在白名单目录内）
- OCR 识别图片文字（非多模态模型时自动调用 ocr_recognize）

## 工具使用规则
- 先思考再使用工具，避免不必要的调用
- 每次最多发起 1 个工具调用
- 文件操作限白名单目录，delete_file 始终需用户确认
- 工具调用失败时告知用户原因

## 安全约束
- 不修改系统文件或注册表
- 不执行危险命令（rm -rf、format 等）
- 敏感操作（删除、移动到非白名单目录）必须用户确认

## OCR 逻辑
- 收到含图片消息时，先检查当前模型是否支持多模态
- 不支持多模态：自动调用 ocr_recognize 提取文字，将文字+问题一起发给 LLM
- 支持多模态：直接发送图片

## 记忆
{MemoryBlock}
// MemoryBlock = 最近 2000 tokens 的记忆摘要文本（纯文本格式）
// Phase 1-2 阶段为空占位符，Phase 3 后由 MemoryPromptBuilder 填充
// 当 MemoryBlock 为空时，跳过整个「## 记忆」段落的注入，避免浪费 tokens

## 输出格式
- 回复简洁直接
- 代码用 markdown 代码块
- 列表用 bullet points

## 上限
- 记忆注入上限：2000 tokens，超出时优先保留最近的记忆
- 单次回复上限：{MaxTokens} tokens
```

---

## 2. 上下文管理 + 压缩

### 2.1 Token 计算

| 参数 | 默认值 | 说明 |
|------|--------|------|
| maxInputLength | 131072 (128K) | 上下文窗口，用户可配 |
| maxTokens | 8192 | 单次回复最大 token |
| fallbackTokPerChar | 0.65 | 中文场景默认值（~1.5 chars/token） |
| initialTokPerChar | 0.65 | 首次调用无历史数据时的 fallback（根据 CultureInfo 判断：中文环境 0.65，英文环境 0.25） |
| tokPerChar 范围 | 0.15~1.0 | 0.15覆盖emoji，1.0覆盖极端压缩 |

动态校准：用上次 turn 的真实 promptTokens / 字符数。首次调用使用 `initialTokPerChar`，后续用动态校准值。

### 2.2 三级压缩（Phase 3 实现，贯穿后续演进）

| Level | 阈值 | 行为 |
|-------|------|------|
| Level 0 | **60%** | 截断旧工具结果（Snip） |
| Level 0.5 | **80%** | 激进 Snip：截断旧消息中的长段落中间部分（保留头尾） |
| Level 1 | **95%** | LLM 摘要压缩（7段结构化） |

> 60%~80% 区间：仅截断旧工具结果的头部/尾部（标准 Snip）。  
> 80%~95% 区间：激进 Snip，对旧消息中的长文本段落也进行中间截断，避免直接跳到 LLM 摘要。  
> 95%+：触发 LLM 摘要压缩。

### 2.3 Level 0 — Snip

| 工具类型 | 头部 | 尾部 |
|---------|------|------|
| 只读 | 80行/10000字符 | 12行/2000字符 |
| 写入 | 40行/8000字符 | 40行/8000字符 |

### 2.4 Level 0.5 — 激进 Snip

对 Level 0 保留的消息段落，若单段超过 2000 字符，截取前 500 字符 + `...[已截断]...` + 后 200 字符。

### 2.5 Level 1 — LLM 摘要

**摘要 Provider 选择**：默认使用当前激活的 Provider/模型生成摘要。可在 `providers.json` 中配置 `"summaryModel"` 字段指定独立的摘要模型（如 `"summaryModel": "gpt-4o-mini"`），降低压缩成本。未配置时 fallback 到当前模型。若 Provider 不可用，降级为 Level 0 强制截断（不阻塞对话）。

**摘要参数**：

| 参数 | 值 | 说明 |
|------|-----|------|
| 摘要 max_tokens | 1024 | 控制摘要长度 |
| 摘要超时 | 15s | 超时降级为 Level 0 |
| 摘要失败重试 | 1次 | 重试仍失败则降级 |

摘要 7 段：Standing facts | Goal | Decisions | Files & code | Commands | Errors | Pending

重组：system + pinned_first_user + kept + summary + recent_tail

> compressor 原地修改 messages list，摘要插入到 pinned_first_user 之后。

### 2.6 关键数值

| 常量 | 值 | 含义 |
|------|-----|------|
| snipThreshold | 0.6 | 60% 标准截断 |
| aggressiveSnipThreshold | 0.8 | 80% 激进截断 |
| summaryThreshold | 0.95 | 95% LLM 摘要 |
| tailReserve | 0.1 | 保留10%尾部 |
| minRecentKeep | 2 | 至少2条最近消息（**优先于 tailReserve**，即始终至少保留 2 条，即使超出 10% 预算） |
| toolResultCap | 3000 | 工具输出截断 |
| autosave | 30s | 自动保存间隔 |

> **触发条件**：所有阈值均使用 `>=` 判断，即比率 >= 阈值时触发对应级别（如比率 >= 0.6 时触发 Level 0 Snip）。

---

## 3. 文件 CRUD 工具

### 3.1 工具列表（8 个）

| # | 工具名 | 说明 | 白名单 |
|---|--------|------|--------|
| 1 | read_file | 读取文件内容（支持文本/自动检测编码） | 免确认 |
| 2 | write_file | 写入/覆盖文件 | 白名单内免确认，否则弹窗→确认后加白名单 |
| 3 | file_exists | 检查文件或目录是否存在 | 免确认 |
| 4 | list_directory | 列出目录内容（支持递归 depth 参数） | 免确认 |
| 5 | create_directory | 创建目录（含中间目录） | 白名单内免确认 |
| 6 | delete_file | 删除文件或空目录 | **始终弹窗确认** |
| 7 | copy_file | 复制文件 | 白名单内免确认 |
| 8 | move_file | 移动/重命名文件 | 白名单内免确认 |

### 3.2 白名单

- 存储：`SetunaOption.FileAccessWhitelist`（`List<string>`）——`SetunaOption` 为已有项目配置类（`Models/SetunaOption.cs`），此处引用其白名单字段
- 弹窗：路径 + 操作 + 内容摘要
- 确认→执行+加白名单；拒绝→不执行（使用 `Window.ShowDialog()` 模态弹窗）
- 设置页可管理；delete_file 始终弹窗不加白名单

---

## 4. LLM Provider 系统

### 4.1 接口关系

```
IProvider（供应商管理）
  → FetchModelsAsync() / CheckConnectionAsync()
  → CreateProvider(model) → ILlmProvider

ILlmProvider（实际调用）
  → ChatAsync(messages, tools, ct) → IAsyncEnumerable<ChatChunk>
```

### 4.2 三套 Provider

| Provider | 覆盖 |
|----------|------|
| OpenAiProvider | OpenAI, DeepSeek, Qwen, vLLM, Kimi 等 |
| OllamaProvider | 本地 Ollama |
| AnthropicProvider | Claude（内部需做消息格式转换，适配 Anthropic API 的 content block 结构） |

### 4.3 配置文件

> **ProviderManager 职责**：管理多供应商的注册、连接检测、模型列表拉取，以及按 `active_model.json` 切换当前激活的 Provider。

**providers.json 结构**：

```json
[
  {
    "id": "openai",
    "name": "OpenAI",
    "baseUrl": "https://api.openai.com/v1",
    "encryptedApiKey": "DPAPI加密后的Base64字符串",
    "timeout": 30,
    "maxRetries": 3,
    "summaryModel": "gpt-4o-mini",  // 可选：独立摘要模型，降低压缩成本
    "models": [
      { "id": "gpt-4o", "name": "GPT-4o", "isFree": false, "supportsMultimodal": true },
      { "id": "gpt-4o-mini", "name": "GPT-4o Mini", "isFree": false, "supportsMultimodal": true }
    ]
  }
]
```

**active_model.json 结构**：

```json
{
  "providerId": "openai",
  "modelId": "gpt-4o"
}
```

**首次配置流程**：用户在设置页输入 API Key → DPAPI 加密 → 写入 `providers.json` → 连接检测 → 拉取模型列表。

### 4.4 免费模型发现

1. 静态声明：`isFree = true`
2. ID后缀：`-free` / `:free`
3. 价格检测：pricing全0

---

## 5. TodoList

### 5.1 数据模型

```csharp
public class TodoItem
{
    public string Id { get; set; }
    public string Title { get; set; }
    public string Description { get; set; }
    public bool IsCompleted { get; set; }
    public int Priority { get; set; }        // 0=普通, 1=重要, 2=紧急
    public List<string> Tags { get; set; } = new();  // 默认空列表，避免反序列化 null
    public DateTime? DueDate { get; set; }
    public string? SessionId { get; set; }   // 由 TodoToolContext 注入（见 §5.3）
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
```

### 5.2 LLM 工具

| 工具名 | 参数 | 说明 |
|--------|------|------|
| add_todo | title, description?, priority?, tags?, dueDate? | 创建 Todo |
| list_todos | filter?(all/pending/completed), tag? | 查询 Todo |
| complete_todo | todoId | 标记完成 |
| delete_todo | todoId | 删除 Todo |
| update_todo | todoId, title?, description?, priority?, tags?, dueDate? | 更新 Todo |

### 5.3 TodoTools 实现机制

```csharp
// TodoTools 通过 TodoToolContext 获取运行时上下文
public class TodoToolContext
{
    public string CurrentSessionId { get; set; } = "";  // 由 Agent 注入
}

// ToolRegistry 注册时注入上下文
toolRegistry.RegisterTodoTools(new TodoToolContext { CurrentSessionId = agent.SessionId });
```

SessionId 不暴露为工具参数，由 `TodoToolContext` 在注册时注入，工具内部通过上下文获取。

### 5.4 界面

1. MainWindow 侧边栏按钮 → TodoWindow
2. TodoWindow（独立窗口）— 完整列表+筛选
3. CompactTodoWindow（置顶迷你）— 未完成数+快速添加

持久化：`%AppData%/Setuna/todos.json`

---

## 6. 截图集成

右键菜单"发起对话" → 创建会话 → 图片存 `sessions/images/` → Agent 自动判断多模态/OCR

---

## 7. OCR 工具

### 7.1 技术方案

**Windows.Media.Ocr**（零依赖，选择 .NET 8 LTS（2024-2026 支持周期），改 TargetFramework 即可）

**TargetFramework**：`net8.0-windows10.0.17763.0`（Windows 10 1809+），`Windows.Media.Ocr` 从 17763 开始可用。若仅需支持 2004+，可使用 `19041.0`，但 17763 提供更广兼容性。

**实现**：

```csharp
[Tool("ocr_recognize", "识别图片中的文字内容（支持中英文混合）")]
public static async Task<string> OcrRecognize(
    [ToolParam("图片文件路径")] string imagePath)
{
    // 1. 创建 OCR 引擎
    var language = OcrEngine.TryCreateFromLanguage(
        new Windows.Globalization.Language("zh-CN"));
    if (language == null) return "[OCR 引擎不可用]";

    // 2. 加载图片为 SoftwareBitmap（需通过 BitmapDecoder 正确解码图片格式）
    var file = await StorageFile.GetFileFromPathAsync(imagePath);
    var stream = await file.OpenReadAsync();
    var decoder = await BitmapDecoder.CreateAsync(stream);
    var bitmap = await decoder.GetSoftwareBitmapAsync();

    // 3. 执行识别
    var result = await language.RecognizeAsync(bitmap);
    return result.Text;
}
```

### 7.2 语言支持

- 自动检测：按系统 locale 选择 OCR 语言
- 手动指定：`language` 可选参数（如 `zh-CN`, `en-US`）

### 7.3 与 LLM 集成

Agent 层判断逻辑：

```
收到含 ImagePath 的消息 →
  if (当前模型 supportsMultimodal)
    → 直接发送图片
  else
    → 调用 ocr_recognize(imagePath) → 将 OCR 文本 + 用户问题一起发给 LLM
```

---

## 8. 定时截图 + 行为日志 + 日报

### 8.1 CronScheduler

- **库**：Cronos（~20KB，纯 .NET，无原生依赖）
- **定时器**：`System.Threading.Timer`
- **唤醒补偿**：系统休眠后 Timer 不自动恢复，监听 `Microsoft.Win32.SystemEvents.PowerModeChanged` 事件，在 `Resume` 时重新调度所有 cron 任务

```csharp
// 唤醒补偿示例
SystemEvents.PowerModeChanged += (s, e) =>
{
    if (e.Mode == PowerModes.Resume)
        RescheduleAll(); // 重新计算所有 cron 的下次触发时间
// 注意：RescheduleAll() 需通过 lock 或 Interlocked 确保与 Timer 回调的线程安全
};
```

**设置项**：

| 参数 | 默认值 | 说明 |
|------|--------|------|
| 截图频率 | 每30分钟 | cron 表达式或预设（5min/15min/30min/1hr/2hr） |
| 日报时间 | 22:00 | 每日弹窗提醒 |
| 启用状态 | 开/关 | 全局开关 |

### 8.2 ScreenshotTracker（行为采集）

仅记录元数据，不存储截图文件。

**JSONL 格式**（每行一条记录）：

```json
{"ts":"2026-07-04T14:30:00+08:00","app":"Chrome","title":"GitHub","dur":1800,"category":"browsing"}
```

| 字段 | 类型 | 说明 |
|------|------|------|
| ts | string (ISO8601) | 事件开始时间 |
| app | string | 前台窗口进程名 |
| title | string | 窗口标题 |
| dur | int | 持续时间（**单位：秒**） |
| category | string | 行为分类（browsing/coding/communication/other） |

**分类逻辑**：维护 `appCategoryMap` 静态映射表，按进程名自动分类：

| 进程名关键词 | 分类 |
|-------------|------|
| chrome, firefox, edge, brave | browsing |
| code, rider, devenv, idea | coding |
| wechat, dingtalk, feishu, slack, teams | communication |
| 其他 | other |

未匹配的进程归为 other。用户可在设置页自定义映射规则。

**空闲处理**：无前台窗口变化或锁屏期间不记录。窗口切换时记录上一个窗口的 dur（切换时间 - 上次记录时间），然后开始新记录。

### 8.3 ReportGenerator

**日报生成流程**：

```
22:00 弹窗 → 用户点"生成" → 读取当日 JSONL → 统计各应用使用时长 → LLM 生成 Markdown 日报
```

**离线/拒绝处理**：

| 场景 | 行为 |
|------|------|
| 22:00 电脑关机/锁屏 | 次日开机后补弹一次「昨日日报未生成，是否补生成？」 |
| 用户拒绝生成 | 当天不再弹，次日正常弹 |
| 用户接受 | 生成日报 + 保存到 `reports/daily/{date}.md` |

---

## 9. 目录结构

```
Core/
├── HotkeyManager.cs
├── Llm/
│   ├── Agent.cs
│   ├── ChatChunk.cs
│   ├── ChatManager.cs
│   ├── ChatMessage.cs
│   ├── ChatSession.cs
│   ├── ContextCompressor.cs      # 上下文压缩（Snip + LLM 摘要）
│   ├── ISystemPromptBuilder.cs   # Prompt 构建接口
│   ├── DefaultSystemPromptBuilder.cs
│   └── MemoryPromptBuilder.cs    # Phase 3: 记忆注入
├── Native/
├── Providers/
│   ├── ILlmProvider.cs
│   ├── IProvider.cs
│   ├── ModelInfo.cs
│   ├── ProviderManager.cs
│   ├── OpenAiProvider.cs
│   ├── OllamaProvider.cs         # Phase 4
│   └── AnthropicProvider.cs      # Phase 4
├── Security/
│   ├── FileAccessWhitelist.cs
│   └── ConfirmDialog.cs
├── Tools/
│   ├── ToolAttribute.cs
│   ├── ToolInfo.cs
│   ├── ToolParamAttribute.cs
│   ├── ToolRegistry.cs
│   ├── FileTools.cs              # Phase 2: 8个文件工具
│   ├── TodoTools.cs              # Phase 2: 5个 Todo 工具
│   └── OcrTools.cs               # Phase 4: OCR识别工具
├── Todo/
│   ├── TodoItem.cs
│   ├── TodoStore.cs
│   └── TodoManager.cs
├── Scheduling/
│   ├── CronScheduler.cs          # Phase 5
│   ├── ScreenshotTracker.cs      # Phase 5
│   └── ReportGenerator.cs        # Phase 5
└── Memory/
    └── MemoryStore.cs            # Phase 3: SQLite 记忆存储

Services/  (已有，不变)
Views/
├── Chat/ (ChatWindow, MessageBubble, ToolCallCard, SessionSidebar)
├── Todo/ (TodoWindow, CompactTodoWindow)
├── Options/ (新增 LLM 设置 + 定时截图标签页)
└── Capture/ (已有，新增右键"发起对话")
Themes/
├── LLM-Colors.xaml               # Phase 1: LLM 主题色
└── (已有文件不变)

Data/ (即 %AppData%/Setuna/Data/)   # 定时截图 + 行为日志
├── screenshots/{date}/           # 定时截图图片（按日期分目录）
└── activity/{date}.jsonl         # 行为日志（每行一条窗口使用记录）
```

---

## 10. 实施计划

### Phase 1: 基础框架 (1-2 周)
- [ ] ILlmProvider / IProvider 接口 + OpenAiProvider
- [ ] [Tool] + [ToolParam] 标注 + ToolRegistry
- [ ] ISystemPromptBuilder / DefaultSystemPromptBuilder
- [ ] Agent 骨架（RunAsync 无工具调用，仅 LLM 对话，使用 NullCompressor 空壳避免 Phase 3 依赖）
- [ ] ChatManager + chats.json 持久化
- [ ] 基础聊天界面（ChatWindow + MessageBubble + SessionSidebar）
- [ ] LLM-Colors.xaml 主题

### Phase 2: 核心功能 (1-2 周)
- [ ] Agent 工具循环（ToolCall 解析 → ToolRegistry.Execute → 结果回注）
- [ ] ParseArgs + 工具结果 null 处理
- [ ] FileTools 8个 + 白名单机制（FileAccessWhitelist + ConfirmDialog）
- [ ] TodoManager + TodoTools + TodoToolContext + 视图
- [ ] ScrapWindow 右键"发起对话"（截图集成，不含 OCR）

### Phase 3: 记忆与压缩 (1 周)
- [ ] SQLite 记忆存储（MemoryStore + Microsoft.Data.Sqlite）
- [ ] ContextCompressor（Level 0 Snip + Level 0.5 激进 Snip + Level 1 LLM 摘要）
- [ ] MemoryPromptBuilder（记忆注入到 System Prompt）

### Phase 4: Provider + OCR (1 周)
- [ ] OllamaProvider + AnthropicProvider
- [ ] 免费模型发现
- [ ] OCR 工具（Windows.Media.Ocr，TargetFramework 改为 net8.0-windows10.0.17763.0）
- [ ] 图片消息处理（多模态判断 + OCR 降级）

### Phase 5: 定时截图 + 日报 (1 周)
- [ ] CronScheduler + SystemEvents.PowerModeChanged 唤醒补偿
- [ ] ScreenshotTracker + 行为日志 JSONL 采集
- [ ] ReportGenerator 日报/周报（含离线补弹逻辑）
- [ ] 设置页定时截图标签页

### Phase 6: 打磨 + 可选增强
- [ ] 会话标题自动生成
- [ ] 错误处理 + 重试（含格式错误判定）
- [ ] 全局快捷键
- [ ] 多语言（zh-CN / en-US）
- [ ] Eviction Index 压缩策略（可选，超长对话场景）

---

## 11. 使用场景 Walkthrough

### 场景 1：截图分析 → 创建 Todo

```
1. 用户 Ctrl+1 截图 → 选区 → 右键"发起对话"
2. 创建会话"截图对话 14:30"，图片存到 sessions/images/
3. Agent 判断：当前LLM不支持多模态 → 调用 ocr_recognize
4. OCR 返回文字 → Agent 将文字+问题发给LLM
5. LLM 回复分析结果
6. 用户说"帮我创建一个todo：修复这个bug"
7. Agent 调用 add_todo → 返回"已添加"
8. TodoList 窗口显示新任务
```

### 场景 2：日报生成

```
1. 22:00 弹窗"今日日报已准备好，是否生成？"
2. 用户点"生成"
3. 读取 activity/2026-07-04.jsonl（18条记录）
4. 统计：Chrome 3.2h, VS Code 2.8h, 微信 1.1h
5. LLM 生成 Markdown 日报
6. 聊天窗口显示 + 保存到 reports/daily/2026-07-04.md
7. 用户复制到钉钉/企业微信
```

### 场景 3：基础多轮对话

```
1. 用户在 ChatWindow 输入"帮我写个 Python 快排"
2. Agent 调用 LLM → 流式返回代码
3. 用户说"加个注释" → Agent 调用 write_file 写入文件
4. Agent 调用 write_file 时检查白名单 → 弹窗确认
5. 用户确认 → 文件写入 + 加入白名单
6. 后续 write_file 到同一目录不再弹窗
```

### 场景 4：文件操作（白名单流程）

```
1. 用户说"把 D:\Work\report.md 复制到 D:\Backup\"
2. Agent 调用 copy_file(src="D:\Work\report.md", dest="D:\Backup\report.md")
3. D:\Backup\ 不在白名单 → 弹窗显示路径+操作
4. 用户确认 → 执行复制 + "D:\Backup\" 加入白名单
5. 用户说"把 D:\Backup\report.md 删了"
6. Agent 调用 delete_file → 始终弹窗确认（即使路径在白名单内）
7. 用户确认 → 文件删除
```

---

## 12. 安全考虑

- **API Key**: DPAPI 加密（已知限制：不支持跨设备迁移，重装系统需重新配置）
- **工具权限**: [Tool] 显式标注
- **文件白名单**: 渐进式信任，delete始终确认
- **网络**: 超时/重试次数可配（providers.json），默认 30s/3次
- **工具循环**: 最多10轮
- **记忆**: SQLite 用户级权限
- **定时截图元数据**: 仅本地存储，不上传

### 12.1 Prompt Injection 防护

| 防护措施 | 说明 |
|----------|------|
| 内容隔离 | 文件内容通过 tool role 注入（非 system message），LLM 将其视为工具执行结果而非用户指令 |
| 标记隔离 | 工具返回结果添加 `[Tool Result: {toolName}]` 前缀标记，LLM 不应将工具结果中的指令当作用户指令执行 |
| 敏感操作二次确认 | 文件删除、写入非白名单目录等操作始终需要用户弹窗确认 |
| System Prompt 固定 | System Prompt 由应用层构建，不包含用户可控内容 |

---

## 13. 错误处理矩阵

| 错误类型 | 用户可见行为 | 恢复策略 |
|---------|-------------|---------|
| LLM 超时 | "响应超时，请重试" | 自动重试3次 |
| LLM 格式错误 | "模型返回异常，正在重试" | 重试1次 |
| Provider 不可用 | "当前模型不可用，切换到本地模型" | 降级Ollama |
| 工具执行异常 | "工具调用失败：{error}" | 错误信息返回LLM继续 |
| SQLite 写入失败 | 静默重试 | 下次写入覆盖 |
| 白名单弹窗拒绝 | "操作已取消" | 无 |
| 会话切换冲突 | "请等待当前操作完成" | 暂停后切换 |
| OCR 引擎不可用 | "OCR不可用，请使用多模态模型" | 降级提示 |
| LLM 摘要失败 | "上下文压缩降级为截断" | Level 0 强制截断 |

**LLM 格式错误判定标准**：

| 判定条件 | 说明 |
|----------|------|
| JSON 解析失败 | 响应无法解析为 ChatChunk |
| 缺少 role 字段 | 响应缺少必要的 role 标识 |
| tool_calls 格式非法 | tool_calls 存在但缺少 name 或 arguments |

---

## 14. Future Considerations

1. **Eviction Index 压缩策略** — 参考 QwenPaw 的 Eviction Index，在超长对话（>200K tokens）时按消息重要性评分淘汰低分消息，作为 Level 1 摘要的补充
2. **插件系统** — 参考 QwenPaw 的技能系统，支持用户自定义工具
3. **多模态扩展** — 视频/文件输入（当前仅图片）
4. **本地模型微调** — 基于用户记忆微调小型本地模型
5. **多设备同步** — 会话和记忆跨设备同步
6. **MCP 协议** — 支持 Model Context Protocol 扩展工具
7. **语音输入** — STT 集成
8. **团队协作** — 多用户共享 TodoList 和报告

---

## 附录 A: 参考项目对比

| 维度 | DeepSeek-Reasonix | QwenPaw | Setuna |
|------|-------------------|---------|--------|
| 压缩 | 4级渐进(50/60/80/90%) | Eviction Index | 3级(Snip+激进Snip+摘要) |
| 会话持久化 | JSONL | 注册表+独立文件 | 注册表+独立文件 |
| Token估算 | tokPerChar动态 | 原生tokenizer | tokPerChar动态+locale感知 |
| Provider | config.toml预设 | 30+内建+自定义 | 3套+用户自定义 |
| 免费模型 | 无 | ID后缀+价格检测 | ID后缀+价格检测 |
| OCR | 无 | 无 | Windows.Media.Ocr |
| 定时截图 | 无 | 无 | Cronos+元数据采集+唤醒补偿 |

**Setuna 选择差异核心考量**：桌面助手场景优先简洁，压缩用三级（Snip + 激进 Snip + 摘要）覆盖 99% 场景，Eviction Index 仅在超长对话时按需启用（见 §14 Future Considerations）。
