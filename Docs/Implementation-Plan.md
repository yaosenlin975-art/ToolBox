# Setuna LLM 桌面助手 — 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将 Setuna 从截图工具箱升级为集成 LLM 桌面助手的全能工具箱

**Architecture:** 多会话管理 + Provider抽象层 + 工具注册机制 + 两级上下文压缩 + SQLite记忆 + 定时截图调度

**Tech Stack:** .NET 8.0 WPF, C#, System.Text.Json, Microsoft.Data.Sqlite, Windows.Media.Ocr, Cronos

---

## Phase 1: 基础框架 (Provider接口 + Tool标注 + ChatManager + ISystemPromptBuilder预留)

### 文件结构

| 文件路径 | 职责 |
|---------|------|
| `Core/Llm/ChatMessage.cs` | 消息数据模型（含 ToolCallInfo） |
| `Core/Llm/ChatChunk.cs` | 流式响应块（文本/工具调用/结束标记） |
| `Core/Llm/ChatSession.cs` | 单会话模型（Id/Title/Messages/Status） |
| `Core/Providers/ILlmProvider.cs` | LLM 调用接口（ChatAsync） |
| `Core/Providers/IProvider.cs` | 供应商管理接口（FetchModels/CreateProvider） |
| `Core/Providers/ModelInfo.cs` | 模型元数据（名称/价格/能力标记） |
| `Core/Providers/ProviderManager.cs` | 多供应商管理 + 激活模型切换 |
| `Core/Providers/OpenAiProvider.cs` | OpenAI 兼容 Provider（基础实现，无流式） |
| `Core/Tools/ToolAttribute.cs` | `[Tool]` 标注属性 |
| `Core/Tools/ToolParamAttribute.cs` | `[ToolParam]` 标注属性 |
| `Core/Tools/ToolRegistry.cs` | 工具注册表（反射扫描 + Execute） |
| `Core/Llm/Agent.cs` | Agent 执行循环（预留 ISystemPromptBuilder） |
| `Core/Llm/ChatManager.cs` | 多会话管理 + chats.json 持久化 |
| `Core/Security/FileAccessWhitelist.cs` | 文件白名单（List\<string\> + DPAPI 加密存储） |
| `Core/Security/ConfirmDialog.cs` | 操作确认弹窗（WPF 模态） |
| `Views/Chat/ChatWindow.xaml` | 聊天主窗口 |
| `Views/Chat/ChatWindow.xaml.cs` | 聊天主窗口逻辑 |
| `Views/Chat/MessageBubble.xaml` | 单条消息气泡控件 |
| `Views/Chat/MessageBubble.xaml.cs` | 消息气泡逻辑 |
| `Views/Chat/ToolCallCard.xaml` | 工具调用折叠卡片 |
| `Views/Chat/ToolCallCard.xaml.cs` | 工具调用卡片逻辑 |
| `Views/Chat/SessionSidebar.xaml` | 左侧会话列表侧边栏 |
| `Views/Chat/SessionSidebar.xaml.cs` | 会话列表逻辑 |
| `Themes/LLM-Colors.xaml` | LLM 助手相关颜色资源 |

### 任务列表

**1.1 数据模型**

- [ ] 创建 `Core/Llm/ChatMessage.cs`：定义 `ChatMessage` 类（Id/Role/Content/ImagePath/ToolCalls/ToolCallId/Timestamp），使用 `System.Text.Json` 序列化属性
- [ ] 创建 `Core/Llm/ChatChunk.cs`：定义 `ChatChunk` 类（Text/ToolCall/IsDone/Error），`ToolCall` 为 `ChatMessage.ToolCallInfo?`
- [ ] 创建 `Core/Llm/ChatSession.cs`：定义 `ChatSession` 类（Id/Title/IsTitleLocked/Status/CreatedAt/UpdatedAt），`Messages` 标注 `[JsonIgnore]`
- [ ] 验证：在 `Program.cs` 或临时 Main 中 `new ChatSession { Id = "test" }` 序列化/反序列化，确认 JSON 输出正确

**1.2 Provider 接口**

- [ ] 创建 `Core/Providers/ModelInfo.cs`：定义 `ModelInfo` 类（ModelId/DisplayName/ProviderName/IsFree/SupportsMultimodal/MaxContextLength/MaxOutputTokens/PricingPer1kInput/PricingPer1kOutput）
- [ ] 创建 `Core/Providers/ILlmProvider.cs`：定义接口 `ChatAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolInfo> tools, CancellationToken ct) → IAsyncEnumerable<ChatChunk>`
- [ ] 创建 `Core/Providers/IProvider.cs`：定义接口 `Name/FetchModelsAsync/CheckConnectionAsync/CreateProvider(modelId) → ILlmProvider`
- [ ] 验证：确认接口编译通过（`dotnet build` 无错误）

**1.3 Provider 实现**

- [ ] 创建 `Core/Providers/ProviderManager.cs`：定义 `ProviderManager` 类（`List<IProvider>` + `ActiveProvider` + `ActiveModelId`），实现 `Register/AddProvider/GetActiveProvider/GetAllModels` 方法
- [ ] 创建 `Core/Providers/OpenAiProvider.cs`：实现 `IProvider` + `ILlmProvider`，`ChatAsync` 用 `HttpClient` 调 OpenAI 兼容 API，**先实现非流式**（ReadAsStreamAsync → 读完整响应 → yield return），Tool 转换为 OpenAI function format
- [ ] 在 `ToolBox.csproj` 添加 NuGet：`System.Text.Json`（默认已包含，确认即可）
- [ ] 验证：写一个硬编码测试（`TestProvider` 类），构造一个请求确认 `ChatAsync` 能正确 yield `ChatChunk`

**1.4 工具标注系统**

- [ ] 创建 `Core/Tools/ToolAttribute.cs`：定义 `[Tool(Name = "xxx", Description = "xxx")]` 属性，`AttributeTargets.Method`
- [ ] 创建 `Core/Tools/ToolParamAttribute.cs`：定义 `[ToolParam(Name, Description, Required, Type)` 属性，`AttributeTargets.Parameter`
- [ ] 创建 `Core/Tools/ToolRegistry.cs`：定义 `ToolRegistry` 类，`Register(object instance)` 扫描 `[Tool]` 标注方法 + 提取 `[ToolParam]` 生成 `ToolInfo` 列表，`Execute(name, args)` 通过反射调用 + `GetAllTools()` 返回 `IReadOnlyList<ToolInfo>`
- [ ] 定义 `ToolInfo` 数据类（Name/Description/Parameters 列表）
- [ ] 验证：写一个 SampleTool 类标注 `[Tool]`，用 `ToolRegistry` 注册并 Execute，确认反射调用成功

**1.5 Agent 执行循环**

- [ ] 创建 `Core/Llm/Agent.cs`：定义 `Agent` 类（`_provider` + `_tools` + `_promptBuilder`），实现 `RunAsync(userMessage, ct) → IAsyncEnumerable<ChatChunk>`，包含工具循环保护（最多 10 轮）
- [ ] Agent 构造 `messages`：system prompt（由 `_promptBuilder.Build()`） + session 历史 + user message
- [ ] Agent 工具循环：provider 响应 → 检查 ToolCall → 有则 Execute → 追加 assistant/tool messages → 下一轮
- [ ] 定义 `ISystemPromptBuilder` 接口（`string Build()`），Agent 持有引用但 Phase 1 用默认实现
- [ ] 验证：构造 mock provider + sample tool，调用 `RunAsync` 确认工具循环正确终止

**1.6 ChatManager + 持久化**

- [ ] 创建 `Core/Llm/ChatManager.cs`：定义 `ChatManager` 类（`List<ChatSession>` + 当前活跃会话），实现 `CreateSession/DeleteSession/SwitchSession/GetSessions`
- [ ] 实现持久化：`chats.json` 存所有会话元数据（不含 Messages），`sessions/{id}.json` 存消息历史
- [ ] 原子写入：写 `.tmp` → `Flush(true)` → rename，使用 `SemaphoreSlim(1,1)` 保护
- [ ] 实现自动保存：每 30s 定时器 snapshot + 运行中消息保存
- [ ] 验证：创建会话 → 发消息 → 关闭程序 → 重新加载，确认会话和消息完整恢复

**1.7 安全基础设施**

- [ ] 创建 `Core/Security/FileAccessWhitelist.cs`：定义 `FileAccessWhitelist` 类（`List<string>` + `Add/Remove/IsAllowed/Load/Save`），路径标准化为绝对路径
- [ ] 创建 `Core/Security/ConfirmDialog.cs`：定义 `ConfirmDialog` 类，静态 `Show(path, operation, contentSummary) → bool`，WPF `Window.ShowDialog()` 模态弹窗
- [ ] 验证：`FileAccessWhitelist.IsAllowed("C:\\Users\\test")` 返回 false，Add 后返回 true

**1.8 基础聊天界面**

- [ ] 创建 `Themes/LLM-Colors.xaml`：定义 LLM 相关颜色资源（UserBubble/AssistantBubble/SystemBg/ToolCardBg/Accent 等）
- [ ] 创建 `Views/Chat/SessionSidebar.xaml` + `.cs`：左侧会话列表（`ListBox` + 新建/删除按钮），绑定 `ChatManager.GetSessions()`
- [ ] 创建 `Views/Chat/MessageBubble.xaml` + `.cs`：消息气泡（用户右侧蓝色/助手左侧灰色/系统居中浅色），支持 Markdown 渲染预留（Phase 6）
- [ ] 创建 `Views/Chat/ToolCallCard.xaml` + `.cs`：工具调用折叠卡片（展开/收起 + 工具名 + 参数 + 结果）
- [ ] 创建 `Views/Chat/ChatWindow.xaml` + `.cs`：聊天主窗口（左侧 Sidebar + 右侧消息列表 + 底部输入框 + 发送按钮），绑定 ChatManager + Agent
- [ ] 在 `MainWindow` 添加"AI 助手"按钮，点击打开 `ChatWindow`
- [ ] 验证：启动应用 → 点击按钮 → 窗口打开 → 输入消息 → 回车发送（Provider 未配置时显示提示）

### Phase 1 结束

- [ ] 构建验证：`dotnet build` 零错误零警告（除 nullable 提示）
- [ ] 提交：`git add -A && git commit -m "feat: Phase 1 - LLM assistant base framework (provider interfaces, tool registry, agent loop, chat UI)"`

---

## Phase 2: 核心功能 (流式响应 + 工具调用 + FileTools + 白名单 + TodoList + 截图集成)

### 文件结构

| 文件路径 | 职责 |
|---------|------|
| `Core/Providers/OpenAiProvider.cs` | 升级为流式 SSE 解析（`ChatAsync` 返回真实流式） |
| `Core/Tools/FileTools.cs` | 8 个文件 CRUD 工具（read/write/exists/list/mkdir/delete/copy/move） |
| `Core/Todo/TodoItem.cs` | Todo 数据模型 |
| `Core/Todo/TodoStore.cs` | Todo 持久化（todos.json） |
| `Core/Todo/TodoManager.cs` | Todo 业务逻辑 |
| `Core/Tools/TodoTools.cs` | 5 个 Todo LLM 工具（add/list/complete/delete/update） |
| `Views/Todo/TodoWindow.xaml` | Todo 完整列表窗口 |
| `Views/Todo/TodoWindow.xaml.cs` | Todo 窗口逻辑 |
| `Views/Todo/CompactTodoWindow.xaml` | 置顶迷你 Todo 窗口 |
| `Views/Todo/CompactTodoWindow.xaml.cs` | 迷你窗口逻辑 |
| `Views/Chat/ChatWindow.xaml` | 升级：支持流式渲染 + 图片消息 |
| `Views/Chat/ChatWindow.xaml.cs` | 升级：截图集成回调 |
| `Models/ScrapWindow.cs` | 升级：右键菜单"发起对话" |
| `Views/Options/OptionWindow.xaml` | 升级：新增 LLM 设置标签页 |
| `Views/Options/OptionWindow.xaml.cs` | 升级：LLM 设置逻辑 |
| `Themes/LLM-Colors.xaml` | 升级：新增 Todo 相关颜色 |

### 任务列表

**2.1 流式响应**

- [ ] 升级 `Core/Providers/OpenAiProvider.cs`：`ChatAsync` 改用 `stream: true`，逐行解析 SSE（`data: {...}`），yield return `ChatChunk`，处理 `finish_reason: stop` 和 `tool_calls` delta
- [ ] 实现 OpenAI tool_calls 的 delta 拼接（多个 chunk 拼接同一 tool call 的 name/arguments）
- [ ] 升级 `Views/Chat/ChatWindow.xaml.cs`：接收 `IAsyncEnumerable<ChatChunk>` → 实时更新 `MessageBubble` 文本（Dispatcher.Invoke 到 UI 线程）
- [ ] 验证：配置真实 OpenAI API Key → 发送消息 → 观察流式文字逐字出现

**2.2 工具调用集成**

- [ ] 升级 `Core/Llm/Agent.cs`：工具循环中解析 `ChatChunk.ToolCall` → 调用 `ToolRegistry.Execute` → 将结果追加为 `ChatMessage(Role="tool")` → 继续下一轮
- [ ] 升级 `Views/Chat/ChatWindow.xaml.cs`：工具调用时显示 `ToolCallCard`（工具名 + 参数），执行完成后显示结果
- [ ] 验证：发送 "列出当前目录文件" → Agent 自动调用工具 → 显示调用卡片 → 显示结果

**2.3 FileTools 实现**

- [ ] 创建 `Core/Tools/FileTools.cs`：定义 `FileTools` 类，标注 8 个 `[Tool]` 方法：
  - `read_file(path)` → 返回文件内容（前 80 行 / 10000 字符）
  - `write_file(path, content)` → 写入，白名单内免确认，否则弹窗
  - `file_exists(path)` → 返回 true/false
  - `list_directory(path)` → 返回目录列表
  - `create_directory(path)` → 建目录
  - `delete_file(path)` → **始终弹窗确认**
  - `copy_file(source, dest)` → 复制
  - `move_file(source, dest)` → 移动
- [ ] 每个工具方法内部检查 `FileAccessWhitelist.IsAllowed`，非白名单调用 `ConfirmDialog.Show`
- [ ] 在 `Agent` 构造时注册 `FileTools` 到 `ToolRegistry`
- [ ] 验证：发送 "读取 D:\test.txt" → 弹窗确认 → 返回文件内容；发送 "删除 D:\test.txt" → 弹窗 → 确认后删除

**2.4 TodoList 数据层**

- [ ] 创建 `Core/Todo/TodoItem.cs`：定义 `TodoItem` 类（Id/Title/Description/IsCompleted/Priority(0-2)/Tags/DueDate/SessionId/CreatedAt/CompletedAt）
- [ ] 创建 `Core/Todo/TodoStore.cs`：定义 `TodoStore` 类，持久化到 `%AppData%/Setuna/todos.json`，原子写入（同 ChatManager 模式）
- [ ] 创建 `Core/Todo/TodoManager.cs`：定义 `TodoManager` 类，`AddTodo/GetTodos/CompleteTodo/DeleteTodo/UpdateTodo`，变更时触发 `TodosChanged` 事件
- [ ] 验证：创建 Todo → 保存 → 重新加载 → 确认数据完整

**2.5 TodoView + TodoTools**

- [ ] 创建 `Core/Tools/TodoTools.cs`：5 个 `[Tool]` 方法（add_todo/list_todos/complete_todo/delete_todo/update_todo），调用 `TodoManager`
- [ ] 在 `Agent` 构造时注册 `TodoTools` 到 `ToolRegistry`
- [ ] 创建 `Views/Todo/TodoWindow.xaml` + `.cs`：完整列表视图（筛选栏 + 优先级颜色 + 完成复选框 + 添加按钮），绑定 `TodoManager`
- [ ] 创建 `Views/Todo/CompactTodoWindow.xaml` + `.cs`：置顶迷你窗口（未完成数 + 快速添加输入框 + `Topmost=true`）
- [ ] 在 `MainWindow` 添加"Todo"按钮，打开 `TodoWindow`
- [ ] 验证：对话中说"帮我创建一个 todo：修复 bug" → Agent 调用 add_todo → TodoWindow 显示新任务 → CompactTodoWindow 更新计数

**2.6 截图集成**

- [ ] 在 `Models/ScrapWindow.cs` 右键菜单添加"发起对话"菜单项
- [ ] 点击时：保存当前截图到 `%AppData%/Setuna/sessions/images/{sessionId}/` → 创建新会话 → 将图片路径放入 `ChatMessage.ImagePath` → 自动发送第一条消息
- [ ] 升级 `Views/Chat/ChatWindow.xaml` + `.cs`：`MessageBubble` 支持显示图片（`BitmapImage` from file path），输入框支持粘贴图片
- [ ] 升级 `Core/Llm/Agent.cs`：`BuildMessages` 时检测 `ChatMessage.ImagePath`，将图片信息传给 Provider（Provider 层负责格式转换）
- [ ] 验证：截图 → 右键"发起对话" → 聊天窗口显示截图缩略图 → 发送消息带图片上下文

**2.7 Provider 设置界面**

- [ ] 升级 `Views/Options/OptionWindow.xaml` + `.cs`：新增"LLM 助手"标签页（API Key 输入 + 模型下拉 + Base URL + 连接测试按钮 + 文件白名单管理列表）
- [ ] 配置持久化到 `providers.json`（API Key 用 DPAPI 加密）
- [ ] 验证：设置 API Key → 点击测试连接 → 成功提示 → 关闭重开 → 配置仍在

### Phase 2 结束

- [ ] 构建验证：`dotnet build` 零错误
- [ ] 提交：`git add -A && git commit -m "feat: Phase 2 - Streaming response, file tools, TodoList, screenshot integration"`

---

## Phase 3: 记忆与压缩 (SQLite记忆 + LLM摘要压缩 + 记忆注入)

### 文件结构

| 文件路径 | 职责 |
|---------|------|
| `Core/Memory/MemoryItem.cs` | 记忆条目数据模型 |
| `Core/Memory/MemoryStore.cs` | SQLite 记忆存储（CRUD + 搜索） |
| `Core/Memory/MemoryManager.cs` | 记忆业务逻辑（重要性评分 + 检索） |
| `Core/Llm/ContextCompressor.cs` | 两级上下文压缩（Snip + LLM 摘要） |
| `Core/Llm/Agent.cs` | 升级：集成 ContextCompressor + MemoryManager |
| `Core/Llm/ChatManager.cs` | 升级：集成 MemoryManager |
| `ToolBox.csproj` | 添加 NuGet：Microsoft.Data.Sqlite |

### 任务列表

**3.1 SQLite 记忆存储**

- [ ] 在 `ToolBox.csproj` 添加 `Microsoft.Data.Sqlite` NuGet 包
- [ ] 创建 `Core/Memory/MemoryItem.cs`：定义 `MemoryItem` 类（Id/Content/Importance(float 0-1)/Tags/CreatedAt/AccessCount/LastAccessedAt/SessionId）
- [ ] 创建 `Core/Memory/MemoryStore.cs`：SQLite CRUD（`%AppData%/Setuna/memory.db`），表 `memories`（id TEXT PK / content TEXT / importance REAL / tags TEXT / created_at TEXT / access_count INT / last_accessed TEXT / session_id TEXT），实现 `Add/Get/Update/Delete/Search(contentKeyword)/GetTopImportant(limit)`
- [ ] 创建数据库初始化方法（`EnsureCreated`），在应用启动时调用
- [ ] 验证：添加 5 条记忆 → 搜索关键词 → 返回匹配项 → 按 importance 排序正确

**3.2 LLM 摘要压缩**

- [ ] 创建 `Core/Llm/ContextCompressor.cs`：定义 `ContextCompressor` 类
- [ ] 实现 Level 0 — Snip（`CheckSnip(messages, usage)`）：当 token 占比 > 60% 时截断旧工具结果（只读工具保留头部 80 行 / 尾部 12 行，写入工具保留头部 40 行 / 尾部 40 行）
- [ ] 实现 Level 1 — LLM 摘要（`CheckSummary(messages, usage)`）：当 token 占比 > 95% 时，提取消息 → 调用 LLM 生成 7 段摘要（Standing facts / Goal / Decisions / Files & code / Commands / Errors / Pending）→ 重组为 system + pinned_first_user + kept + summary + recent_tail
- [ ] 实现 token 估算：`EstimateTokens(messages)` 用 `tokPerChar` 动态校准（上次 turn 的真实 promptTokens / 字符数），默认 `fallbackTokPerChar = 0.25`，范围 0.15~1.0
- [ ] 验证：构造 100+ 条消息 → 调用 `CheckAndCompressAsync` → 确认 Level 0 截断旧工具结果 → 确认 Level 1 生成摘要并重组 messages

**3.3 记忆注入**

- [ ] 升级 `Core/Llm/Agent.cs`：`RunAsync` 开始时调用 `MemoryManager.GetRelevantMemories(userMessage)` → 将记忆注入到 system prompt 的 `{MemoryBlock}` 位置
- [ ] 创建 `Core/Memory/MemoryManager.cs`：定义 `MemoryManager` 类，`AddMemory/GetRelevantMemories(query)/SaveFromConversation(messages)`，重要性评分规则（用户显式提及 > 工具结果 > 闲聊）
- [ ] 实现记忆提取策略：每轮结束后从 messages 中提取值得记忆的内容（决策、关键信息、用户偏好）→ 评分 → 存入 MemoryStore
- [ ] 实现记忆注入上限：2000 tokens，超出时优先保留最近的记忆
- [ ] 升级 `Core/Llm/Agent.cs`：`ISystemPromptBuilder.Build()` 调用时注入 `{MemoryBlock}`
- [ ] 验证：发送 3 轮对话含重要信息 → 下次新会话 → Agent 的 system prompt 包含之前的记忆 → LLM 回复能引用历史上下文

### Phase 3 结束

- [ ] 构建验证：`dotnet build` 零错误
- [ ] 提交：`git add -A && git commit -m "feat: Phase 3 - SQLite memory, LLM summary compression, memory injection"`

---

## Phase 4: Provider扩展 + OCR (Ollama + Anthropic + 免费模型 + OCR工具)

### 文件结构

| 文件路径 | 职责 |
|---------|------|
| `Core/Providers/OllamaProvider.cs` | Ollama 本地模型 Provider |
| `Core/Providers/AnthropicProvider.cs` | Anthropic Claude Provider（content block 转换） |
| `Core/Providers/ProviderManager.cs` | 升级：免费模型发现 + active_model.json |
| `Core/Ocr/OcrModels.cs` | OCR 相关数据模型 |
| `Core/Ocr/OcrService.cs` | Windows.Media.Ocr 封装 |
| `Core/Tools/ScreenshotTools.cs` | OCR LLM 工具（ocr_recognize） |
| `Core/Llm/Agent.cs` | 升级：多模态判断 + 自动 OCR |
| `ToolBox.csproj` | 升级 TargetFramework 为 net8.0-windows10.0.19041.0 |

### 任务列表

**4.1 TargetFramework 升级**

- [ ] 在 `ToolBox.csproj` 中将 `<TargetFramework>net8.0-windows</TargetFramework>` 改为 `<TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>`（启用 Windows.Media.Ocr API）
- [ ] 验证：`dotnet build` 成功，确认无兼容性问题

**4.2 OllamaProvider**

- [ ] 创建 `Core/Providers/OllamaProvider.cs`：实现 `IProvider` + `ILlmProvider`
- [ ] `FetchModelsAsync`：调用 `http://localhost:11434/api/tags` 获取模型列表
- [ ] `ChatAsync`：调用 `http://localhost:11434/api/chat`（stream: true），解析 NDJSON 流（每行一个 JSON 对象），转换 tool_calls 格式
- [ ] Ollama tool_calls 格式适配：Ollama 使用 `tool_calls` 数组（与 OpenAI 类似但细节不同），需转换为统一的 `ToolCallInfo`
- [ ] 验证：启动 Ollama → 配置 Provider → 发消息 → 确认流式响应正确

**4.3 AnthropicProvider**

- [ ] 创建 `Core/Providers/AnthropicProvider.cs`：实现 `IProvider` + `ILlmProvider`
- [ ] 消息格式转换：`ChatMessage` → Anthropic content block 格式（`{"role": "user", "content": [{"type": "text", "text": "..."}, {"type": "image", "source": {...}}]}`）
- [ ] 多模态图片处理：`ChatMessage.ImagePath` → `{"type": "image", "source": {"type": "base64", "media_type": "image/png", "data": "..."}}`
- [ ] 流式解析：Anthropic SSE 事件类型（`message_start` / `content_block_delta` / `message_stop`）
- [ ] API Key 使用 `x-api-key` header，版本 `anthropic-version: 2023-06-01`
- [ ] 验证：配置 Anthropic API Key → 发消息 → 确认响应正确；发送带图片消息 → 确认图片被正确编码

**4.4 免费模型发现**

- [ ] 升级 `Core/Providers/ProviderManager.cs`：实现 `GetFreeModels()` 方法
- [ ] 免费判定逻辑：(1) `ModelInfo.IsFree = true` 静态声明 (2) 模型 ID 含 `-free` / `:free` 后缀 (3) `PricingPer1kInput == 0 && PricingPer1kOutput == 0`
- [ ] 在 Provider 设置页显示免费模型标签
- [ ] 创建 `active_model.json` 持久化：记录当前激活的 Provider + ModelId
- [ ] 验证：列出所有模型 → 免费模型标记正确 → 切换模型 → 重启后记住选择

**4.5 OCR 服务**

- [ ] 创建 `Core/Ocr/OcrModels.cs`：定义 `OcrResult` 类（Text/Language/Confidence/Lines 列表）
- [ ] 创建 `Core/Ocr/OcrService.cs`：封装 `Windows.Media.Ocr.OcrEngine`，实现 `RecognizeAsync(byte[] imageData, string languageHint) → OcrResult`
- [ ] 支持语言：默认 `zh-CN`，自动检测 + 用户可选（en-US / ja-JP 等）
- [ ] 使用 `Windows.Graphics.Imaging.SoftwareBitmap` 解码图片 → `OcrEngine.RecognizeAsync(bitmap)`
- [ ] 验证：传入一张含中文截图 → 返回正确 OCR 文本 + 置信度

**4.6 OCR LLM 工具**

- [ ] 创建 `Core/Tools/ScreenshotTools.cs`：定义 `[Tool(Name = "ocr_recognize", Description = "识别图片中的文字")]`，参数 `[ToolParam(Name = "imagePath")]`
- [ ] 实现：调用 `OcrService.RecognizeAsync` → 返回 OCR 文本
- [ ] 在 `ToolRegistry` 注册 `ScreenshotTools`

**4.7 多模态 Agent 逻辑**

- [ ] 升级 `Core/Llm/Agent.cs`：`BuildMessages` 时遍历消息，检测 `ImagePath` 非空时检查 `ModelInfo.SupportsMultimodal`
- [ ] 不支持多模态：自动插入系统消息 + 调用 `ocr_recognize` 工具 → OCR 文本替换图片上下文
- [ ] 支持多模态：保持 `ImagePath` 不变，Provider 层处理
- [ ] 验证：用不支持多模态的模型（如 gpt-3.5-turbo）发送截图 → Agent 自动调用 OCR → 返回文字分析结果；用 GPT-4o 发送截图 → 直接多模态分析

### Phase 4 结束

- [ ] 构建验证：`dotnet build` 零错误
- [ ] 提交：`git add -A && git commit -m "feat: Phase 4 - Ollama/Anthropic providers, free model discovery, OCR integration"`

---

## Phase 5: 定时截图 + 日报 (CronScheduler + ScreenshotTracker + ReportGenerator)

### 文件结构

| 文件路径 | 职责 |
|---------|------|
| `Core/Scheduler/CronScheduler.cs` | Cron 定时调度器（基于 Cronos） |
| `Core/Scheduler/ScreenshotTracker.cs` | 定时截图 + 行为元数据采集 |
| `Core/Scheduler/ReportGenerator.cs` | 日报/周报生成 |
| `Views/Options/OptionWindow.xaml` | 升级：新增"定时截图"标签页 |
| `Views/Options/OptionWindow.xaml.cs` | 升级：定时截图设置逻辑 |
| `ToolBox.csproj` | 添加 NuGet：Cronos |

### 任务列表

**5.1 CronScheduler**

- [ ] 在 `ToolBox.csproj` 添加 `Cronos` NuGet 包
- [ ] 创建 `Core/Scheduler/CronScheduler.cs`：定义 `CronScheduler` 类
- [ ] 实现：`Start(cronExpression, callback)` / `Stop()` / `Update(cronExpression)`，底层用 `System.Threading.Timer` + Cronos 解析
- [ ] 唤醒补偿：`Start` 时比较 `lastRun` 与当前时间，补拍缺失截图
- [ ] 状态持久化：`scheduler_state.json` 存 `lastRun` + `isEnabled` + `cronExpression`
- [ ] 验证：配置每 5 分钟 → 观察日志输出 → 修改为每 1 分钟 → 确认更新生效

**5.2 ScreenshotTracker**

- [ ] 创建 `Core/Scheduler/ScreenshotTracker.cs`：定义 `ScreenshotTracker` 类
- [ ] 行为采集：复用 `WindowManager.Instance.GetWindowInfo()`，每采集周期记录 `{ "t": "...", "app": "chrome", "title": "GitHub", "dur": 1800 }`
- [ ] 持久化：JSONL 文件 `%AppData%/Setuna/activity/{yyyy-MM-dd}.jsonl`，追加写入
- [ ] 隐私控制：`ExcludeApps` 排除名单（用户可配），首次开启弹窗说明
- [ ] 数据清理：超过 30 天的 JSONL 文件自动删除
- [ ] 验证：启动定时采集 → 切换不同应用 → 检查 JSONL 文件内容 → 确认排除名单生效

**5.3 ReportGenerator**

- [ ] 创建 `Core/Scheduler/ReportGenerator.cs`：定义 `ReportGenerator` 类
- [ ] 日报生成：读取当日 JSONL → 统计各应用使用时长 → 调用 LLM 生成 Markdown 日报 → 保存到 `%AppData%/Setuna/reports/daily/{yyyy-MM-dd}.md`
- [ ] 周报生成：汇总 7 天日报 → LLM 生成周报 → 保存到 `reports/weekly/{yyyy-Www}.md`
- [ ] 弹窗确认：日报 22:00 / 周报 周一 09:00 → 弹窗"是否生成？" → 用户确认 → 生成 → 聊天窗口显示
- [ ] 验证：手动构造 3 天 JSONL → 调用 `GenerateDailyReport` → 检查生成的 Markdown 内容正确

**5.4 设置界面**

- [ ] 升级 `Views/Options/OptionWindow.xaml`：新增"定时截图"标签页（开关 + Cron 表达式输入 + 排除应用列表 + 保留天数 + 首次说明弹窗）
- [ ] 升级 `Core/Models/SetunaOption.cs`（或新建 `LlmOption`）：新增定时截图配置字段（IsEnabled/CronExpression/ExcludeApps/RetentionDays）
- [ ] 验证：打开设置 → 启用定时截图 → 配置 Cron → 保存 → 重启 → 配置仍在

### Phase 5 结束

- [ ] 构建验证：`dotnet build` 零错误
- [ ] 提交：`git add -A && git commit -m "feat: Phase 5 - Cron scheduler, screenshot tracker, report generator"`

---

## Phase 6: 打磨 + 可选增强 (会话标题 + 错误处理 + 快捷键 + 多语言)

### 文件结构

| 文件路径 | 职责 |
|---------|------|
| `Core/Llm/ChatManager.cs` | 升级：会话标题自动生成逻辑 |
| `Core/Llm/Agent.cs` | 升级：错误处理 + 重试 + 超时 + 降级 |
| `Core/Llm/ChatWindow.xaml.cs` | 升级：快捷键支持 |
| `Themes/Lang zh-CN.xaml` | 升级：新增 LLM 助手相关中文翻译 |
| `Themes/Lang en-US.xaml` | 升级：新增 LLM 助手相关英文翻译 |
| `Views/Chat/MessageBubble.xaml` | 升级：Markdown 渲染 |
| `Core/HotkeyManager.cs` | 升级：注册 AI 助手全局快捷键 |

### 任务列表

**6.1 会话标题自动生成**

- [ ] 升级 `Core/Llm/ChatManager.cs`：`CreateSession` 时截取首条消息前 20 字作为初始标题
- [ ] 实现第 3 轮后后台调 LLM 生成标题：`await Task.Run(() => provider.ChatAsync([{"role":"user","content":"用5个字以内概括这段对话的主题：{前几条消息摘要}"}]))` → 更新 `Session.Title`
- [ ] 实现标题锁定：用户手动修改标题 → `IsTitleLocked = true` → 不再自动覆盖
- [ ] 验证：创建会话 → 发 3 条消息 → 观察标题自动更新；手动改标题 → 再发消息 → 标题不变

**6.2 错误处理 + 重试**

- [ ] 升级 `Core/Llm/Agent.cs`：实现错误处理（按设计文档 §13 错误矩阵）
- [ ] LLM 超时：30s 超时 + 自动重试 3 次，用户可见 "响应超时，请重试"
- [ ] LLM 格式错误（JSON 解析失败）：重试 1 次，用户可见 "模型返回异常，正在重试"
- [ ] Provider 不可用：自动降级到 Ollama，用户可见 "当前模型不可用，已切换到本地模型"
- [ ] 工具执行异常：错误信息返回 LLM 继续对话，用户可见 "工具调用失败：{error}"
- [ ] 网络异常：通用重试逻辑（3 次，指数退避 1s/2s/4s）
- [ ] 验证：断开网络 → 发消息 → 观察重试逻辑 → 最终显示友好错误提示

**6.3 全局快捷键**

- [ ] 升级 `Core/HotkeyManager.cs`：注册 AI 助手全局快捷键（默认 `Ctrl+Shift+A`）
- [ ] 按下快捷键：打开 ChatWindow 并聚焦输入框（如已打开则切换显示/隐藏）
- [ ] 快捷键可配置：在设置页 LLM 标签页添加快捷键绑定
- [ ] 验证：按 `Ctrl+Shift+A` → ChatWindow 弹出；再按 → 隐藏；在设置中修改 → 新快捷键生效

**6.4 多语言支持**

- [ ] 升级 `Themes/Lang zh-CN.xaml`：添加 LLM 助手相关中文翻译键（ChatWindow.Title/SendPlaceholder/NewSession/DeleteSession/Thinking/ToolCalling 等）
- [ ] 升级 `Themes/Lang en-US.xaml`：对应英文翻译
- [ ] 升级所有 LLM 相关 XAML 视图：使用 `DynamicResource` 替代硬编码文本
- [ ] 验证：切换语言 → 界面文本全部更新 → 无遗漏

**6.5 Markdown 渲染**

- [ ] 升级 `Views/Chat/MessageBubble.xaml` + `.cs`：实现基础 Markdown → WPF 渲染
- [ ] 支持：代码块（`<CodeBlock>` 样式）、粗体/斜体、列表（bullets）、链接
- [ ] 实现策略：简单正则解析 → FlowDocument 或 RichTextBox 渲染（不引入外部 Markdown 库）
- [ ] 验证：发送含代码块和列表的消息 → 确认渲染正确

**6.6 可选增强（可选，按需实现）**

- [ ] DPAPI 加密：API Key 存储使用 `ProtectedData.Protect` / `Unprotect`
- [ ] 配置导入/导出：LLM 设置可导出为 JSON 文件（加密 API Key）
- [ ] 使用统计：记录 Token 用量（每次 turn 的 input/output tokens），设置页显示
- [ ] 验证：各增强功能独立测试

### Phase 6 结束

- [ ] 构建验证：`dotnet build` 零错误 + 完整功能手动测试
- [ ] 提交：`git add -A && git commit -m "feat: Phase 6 - Auto title, error handling, hotkey, i18n, markdown render"`

---

## 附录：关键常量速查

| 常量 | 值 | 位置 |
|------|-----|------|
| maxInputLength | 131072 (128K) | ContextCompressor |
| maxTokens | 8192 | Agent/Provider |
| fallbackTokPerChar | 0.25 | ContextCompressor |
| tokPerChar 范围 | 0.15~1.0 | ContextCompressor |
| snipThreshold | 0.6 (60%) | ContextCompressor — Level 0 |
| summaryThreshold | 0.95 (95%) | ContextCompressor — Level 1 |
| tailReserve | 0.1 (10%) | ContextCompressor |
| minRecentKeep | 2 | ContextCompressor |
| toolResultCap | 3000 | ContextCompressor |
| autosave | 30s | ChatManager |
| toolLoopMax | 10 轮 | Agent |
| llmTimeout | 30s | Agent |
| llmRetry | 3 次 | Agent |
| memoryInjectionLimit | 2000 tokens | MemoryManager |
| memoryRetention | 30 天 | ScreenshotTracker |
| reportTime | 22:00 (日报) / 周一 09:00 (周报) | ReportGenerator |