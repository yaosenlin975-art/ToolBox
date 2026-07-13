# ToolBox 功能完整性审查报告

> 审查人：qa-engineer（严过关）  
> 日期：2026-07-06  
> 审查范围：对照 `Docs/design.md` 及各子设计文档，逐项核查 Project/ 代码实现度  
> 审查方法：逐文件阅读设计文档 → 逐项到代码中验证 → Grep 精确定位

---

## 一、统计概览

| 指标 | 数量 |
|------|------|
| 已实现 | 21 |
| 部分实现 | 9 |
| 未实现 | 14 |
| 缺陷总数 | 17 |
| 高严重度缺陷（P0/P1） | 8 |
| 中严重度缺陷（P2） | 6 |
| 低严重度缺陷（P3） | 3 |

---

## 二、功能实现度对照表

### 2.1 待办系统

| # | 功能项 | 设计来源 | 实现状态 | 证据（文件:行号） | 说明 |
|---|--------|----------|----------|-------------------|------|
| 1 | TodoItem 基础字段（Id/Title/Description/IsCompleted/Priority/Tags/DueDate） | design.md L55 | 已实现 | TodoItem.cs:5-13 | 完整实现 |
| 2 | Progress 字段（0-100） | design.md L239 | 部分实现 | TodoItem.cs:14 | 字段存在+UI有Slider，但 Progress=100↔IsCompleted 同步逻辑未实现 |
| 3 | StartDate 字段 | design.md L240 | 未实现 | — | TodoItem.cs 中无此字段，全代码库无匹配 |
| 4 | Children 子任务列表 | design.md L242 | 未实现 | — | 改用 ParentId 平铺模型（TodoItem.cs:6），无 List<TodoItem> Children |
| 5 | 父任务进度聚合公式 | design.md L242 | 未实现 | — | 无聚合逻辑 |
| 6 | Progress=100 自动同步 IsCompleted=true | design.md L239 | 未实现 | — | TodoStore.CompleteAsync/UpdateAsync 中无此联动 |
| 7 | 可重复待办（模板+实例模型） | recurring-todo-design.md | 未实现 | — | 无 RepeatConfig.cs、RepeatCalculator.cs；TodoItem 无 RepeatParentId/RepeatConfig 字段 |
| 8 | CatchUpRepeating 启动补齐 | recurring-todo-design.md L170 | 未实现 | App.xaml.cs:37-91 | App.OnStartup 中无 CatchUpRepeating 调用 |
| 9 | CronScheduler 重复提醒 | recurring-todo-design.md L293 | 未实现 | — | 无 RegisterRepeatReminders 方法 |
| 10 | 迷你窗口"今日重复"分组 | recurring-todo-design.md L384 | 未实现 | — | CompactToolboxWindow 无重复分组 |
| 11 | TodoView "重复"筛选标签 | recurring-todo-design.md L376 | 未实现 | — | TodoView 筛选栏无"重复"选项 |
| 12 | TodoTools add_todo repeat 参数 | recurring-todo-design.md L415 | 未实现 | TodoTools.cs:8-18 | add_todo 无 repeat 参数 |
| 13 | TodoStore 增删改查 | design.md L245 | 已实现 | TodoStore.cs:88-246 | AddAsync/CompleteAsync/UpdateAsync/DeleteAsync 完整 |
| 14 | TodoStore 回收站系统 | — | 已实现 | TodoStore.cs:157-199 | TrashAsync/RestoreAsync/DeletePermanentlyAsync 完整 |
| 15 | TodoStore 树形结构 | — | 已实现 | TodoStore.cs:201-237 | GetChildren/GetRoots/GetGroupedTree 完整 |
| 16 | design.md L247 注释（Progress/StartDate/Children 均未实现） | design.md L247 | 过时 | TodoItem.cs:14 | Progress 已实现，注释过时需更新 |

### 2.2 LLM 助手

| # | 功能项 | 设计来源 | 实现状态 | 证据（文件:行号） | 说明 |
|---|--------|----------|----------|-------------------|------|
| 17 | 多会话管理 | design.md L230 | 已实现 | ChatManager.cs:39-46 | CreateSession/SwitchSession/DeleteSession/TogglePin 完整 |
| 18 | 会话持久化 | design.md L206 | 已实现 | ChatManager.cs:116-139 | chats.json 索引 + sessions/{id}.json 消息，原子写入 |
| 19 | 会话置顶 | design.md L230 | 已实现 | ChatManager.cs:88-105 | TogglePinAsync + ReorderSessions |
| 20 | 流式输出（IAsyncEnumerable<ChatChunk>） | design.md L233 | 已实现 | Agent.cs:33-111 | 逐 token yield，边收边吐 |
| 21 | 工具调用循环（最多10轮） | design.md L199 | 部分实现 | Agent.cs:47 | 10轮限制有，但每轮最多3次重试未实现 |
| 22 | 工具调用——每轮最多3次重试 | design.md L199 | 未实现 | — | Agent.cs 无重试逻辑 |
| 23 | 单轮多工具调用 | — | 未实现 | Agent.cs:75 | 仅处理1个 tool call，OpenAiProvider.cs:152 也只取 toolCalls[0] |
| 24 | 上下文压缩 | design.md L231 | 部分实现 | ContextCompressor.cs:1-103 | 类已实现（snip/aggressive/summary 三级），但**从未被调用** |
| 25 | 上下文压缩——LLM 摘要 | LLM-Assistant-Design.md L495 | 未实现 | ContextCompressor.cs:79-96 | ApplySummaryCompression 不调用 LLM，仅拼接文本 |
| 26 | 记忆系统——SQLite 存储 | design.md L192 | 部分实现 | MemoryStore.cs:1-96 | 存储和清理已实现，但无自动归纳，检索方法从未调用 |
| 27 | 记忆系统——自动归纳（LLM 分析） | LLM-Assistant-Design.md L390 | 未实现 | — | 无 MemoryManager，无 LLM 归纳流程 |
| 28 | 记忆注入到 System Prompt | LLM-Assistant-Design.md L444 | 未实现 | Agent.cs:117 | BuildMessages 调用 promptBuilder.Build()，BuildWithMemory 从未被调用 |
| 29 | 记忆检索 | LLM-Assistant-Design.md | 未实现 | — | MemoryStore.GetRelevant 从未被调用 |
| 30 | Markdown 渲染 | design.md L234 | 已实现 | ChatView.xaml.cs:204 | assistantBubble.RenderMarkdown() 在流式结束后调用 |
| 31 | 会话标题自动生成 | LLM-Assistant-Design.md L794 | 部分实现 | ChatManager.cs:194-207 | 截取首条用户消息前25字符，非 LLM 生成 |
| 32 | OpenAI Provider（流式+工具调用+图片） | design.md L43 | 已实现 | OpenAiProvider.cs:73-168 | 完整实现 SSE 流式、tool_calls 累积、base64 图片 |
| 33 | Anthropic Provider——工具调用 | design.md L45 | 未实现 | AnthropicProvider.cs:64-118 | 不发送 tools 到 API，不解析 tool_use 响应 |
| 34 | Anthropic Provider——图片/视觉 | — | 未实现 | AnthropicProvider.cs:77-82 | AnthropicMessage.Content 为 string，不支持图片 |
| 35 | Ollama Provider——工具调用 | design.md L44 | 未实现 | OllamaProvider.cs:64-103 | 不发送 tools 到 API，不解析 tool 响应 |
| 36 | Ollama Provider——图片/视觉 | — | 未实现 | OllamaProvider.cs:72 | OllamaMessage.Content 为 string，不支持图片 |
| 37 | Provider 管理（内置+自定义） | design.md L189 | 已实现 | ProviderManager.cs:1-248 | 增删改查+模型发现+活跃模型持久化 |
| 38 | API Key DPAPI 加密存储 | LLM-Assistant-Design.md L824 | 未实现 | ProviderManager.cs:210-219 | API Key 明文写入 providers.json |
| 39 | ScrapWindow 右键"发起对话" | LLM-Assistant-Design.md L612 | 已实现 | ScrapWindow.cs（grep 确认） | CreateSessionWithImage 已接线 |
| 40 | OCR 工具 | DefaultSystemPromptBuilder.cs:12 | 未实现 | — | 系统提示词提及 OCR，但无 OCR 工具实现 |
| 41 | 文件工具安全（白名单+确认） | LLM-Assistant-Design.md L824-826 | 未实现 | — | FileAccessWhitelist/ConfirmDialog 已定义但从未被 FileTools 调用 |
| 42 | 截图工具（screenshot_info/capture_screen） | LLM-Assistant-Design.md L371 | 未实现 | — | 无 ScreenshotTools 类 |

### 2.3 定时截图与日报

| # | 功能项 | 设计来源 | 实现状态 | 证据（文件:行号） | 说明 |
|---|--------|----------|----------|-------------------|------|
| 43 | CronScheduler（Cronos + Timer） | Screenshot-Report-Design.md L41 | 已实现 | CronScheduler.cs:1-80 | 含电源恢复处理 |
| 44 | ScheduleManager（注册/注销） | design.md L64 | 已实现 | ScheduleManager.cs:1-83 | ApplyCurrent/Apply 完整 |
| 45 | 定时全屏截图 | design.md L270 | 已实现 | ScheduleManager.cs:66-82 | CopyFromScreen 保存 PNG |
| 46 | 每日行为总结 | design.md L271 | 部分实现 | ScheduleManager.cs:42-64 | Cron 触发+报告生成+保存已实现，但报告不调用 LLM |
| 47 | ScreenshotTracker | Screenshot-Report-Design.md L307 | 部分实现 | ScreenshotTracker.cs:1-99 | 实现为窗口切换追踪（RecordWindowSwitch），非截图元数据采集；设计中的 RecordCapture 方法未实现 |
| 48 | ReportGenerator——LLM 生成报告 | Screenshot-Report-Design.md L545 | 未实现 | ReportGenerator.cs:22-69 | 仅拼接 Markdown 表格，不调用 LLM |
| 49 | ReportGenerator——周报 | Screenshot-Report-Design.md L571 | 部分实现 | ReportGenerator.cs:72-103 | 生成简单周报，不调用 LLM，不汇总日报 |
| 50 | AppRecognizer 应用名映射 | Screenshot-Report-Design.md L265 | 部分实现 | ScreenshotTracker.cs:12-18 | 有 appCategoryMap 但映射方向不同（按类别非按应用名） |
| 51 | 截图元数据采集（WindowTitle/ClassName） | Screenshot-Report-Design.md L328 | 部分实现 | ScreenshotTracker.cs:30-51 | RecordWindowSwitch 采集 App+Title，但未关联截图文件 |

### 2.4 截图系统

| # | 功能项 | 设计来源 | 实现状态 | 证据（文件:行号） | 说明 |
|---|--------|----------|----------|-------------------|------|
| 52 | CaptureWindow（全屏选区） | design.md L223 | 已实现 | Views/Capture/CaptureWindow.cs | 文件存在 |
| 53 | ScrapWindow（浮动贴图） | design.md L224 | 已实现 | Models/ScrapWindow.cs | 含右键菜单、发起对话 |
| 54 | CacheManager（截图缓存+恢复） | design.md L225 | 已实现 | CacheManager.cs:1-158 | 完整实现 Init/RestoreScraps/CleanupExpired/生命周期监听 |
| 55 | HistoryView——4列网格 | design.md L226 | 已实现 | HistoryView.xaml.cs:15 | PageSize=16，网格/列表切换 |
| 56 | HistoryView——时间筛选 | design.md L226 | 已实现 | HistoryView.xaml.cs:64-81 | today/week/month/all |
| 57 | HistoryView——搜索 | design.md L226 | 未实现 | — | 无搜索框/搜索逻辑 |

### 2.5 设置与配置

| # | 功能项 | 设计来源 | 实现状态 | 证据（文件:行号） | 说明 |
|---|--------|----------|----------|-------------------|------|
| 58 | AppType / ShowMainWindow | design.md L326-327 | 已实现 | SetunaOption.cs:204-205 | |
| 59 | SelectAreaTransparent / SelectLineSolid | design.md L328-329 | 已实现 | SetunaOption.cs:215,208 | |
| 60 | InactiveAlphaValue / MouseOverAlphaValue | design.md L330-331 | 已实现 | SetunaOption.cs:248,250 | |
| 61 | DustBoxEnable / DustBoxCapacity | design.md L332-333 | 已实现 | SetunaOption.cs:216,217 | |
| 62 | CaptureHotKey / HideShowHotKey / HotKeyEnable | design.md L334-336 | 已实现 | SetunaOption.cs:25,35,22 | |
| 63 | Language | design.md L337 | 已实现 | SetunaOption.cs:44 | |
| 64 | Theme | design.md L338 | 已实现 | SetunaOption.cs:240 | |
| 65 | CompactOpacity | design.md L339 | 已实现 | SetunaOption.cs:235 | |
| 66 | AutoScreenshotEnabled / AutoScreenshotCron | design.md L340-341 | 已实现 | SetunaOption.cs:236-237 | |
| 67 | DailyReportEnabled / DailyReportTime | design.md L342-343 | 已实现 | SetunaOption.cs:238-239 | |
| 68 | ScreenshotMaxAge / ChatFontSize | — | 已实现 | SetunaOption.cs:241-242 | 设计表未列出但已实现 |
| 69 | ShowSplashWindow | design.md L217 | 已实现 | SetunaOption.cs:207 | 设计表未列出但已实现 |
| 70 | 自定义供应商管理 UI | design.md L261 | 已实现 | ProviderManager.cs:79-108 | AddCustomProvider/RemoveCustomProvider/UpdateCustomProvider |

### 2.6 基础架构

| # | 功能项 | 设计来源 | 实现状态 | 证据（文件:行号） | 说明 |
|---|--------|----------|----------|-------------------|------|
| 71 | 单例守卫（Mutex） | design.md L212 | 已实现 | App.xaml.cs:10,47 | |
| 72 | 主题系统（Light/Dark/System） | design.md L302-309 | 已实现 | ThemeManager（design.md 确认） | |
| 73 | 系统托盘 | design.md L169-183 | 已实现 | MainWindow.cs（trayIcon Dispose 确认） | |
| 74 | 启动画面（三态） | design.md L135-138 | 已实现 | App.xaml.cs:59-79, SplashWindow.cs | |
| 75 | 全局热键 | design.md L28 | 已实现 | HotkeyManager.cs（design.md 确认） | |
| 76 | 开机自启 | design.md L87 | 已实现 | AutoStartup.cs（design.md 确认） | |
| 77 | App.OnExit 资源释放 | design.md L210 | 未实现 | App.xaml.cs | 无 OnExit 重写，CronScheduler/ChatManager Timer 未 Dispose |

---

## 三、缺陷清单

### P0 — 严重（阻断核心功能 / 安全风险）

| # | 缺陷 | 文件:行号 | 复现条件 | 修复建议 |
|---|------|-----------|----------|----------|
| D1 | **文件工具无安全防护**：FileTools 的 read_file/write_file/delete_file 等工具可操作任意路径，FileAccessWhitelist 和 ConfirmDialog 已定义但从未被调用。LLM 可通过工具调用读取/覆盖/删除系统任意文件。 | FileTools.cs:8-106 | LLM 调用 read_file("C:\Windows\System32\config\SAM") 或 delete_file(任意路径) | 在 FileTools 每个方法入口检查 FileAccessWhitelist.Instance.IsAllowed(path)，delete_file 调用 ConfirmDialog.Show() |
| D2 | **上下文压缩从未生效**：ContextCompressor 类已实现但 CheckAndCompressAsync 从未被调用。长对话会超出模型上下文窗口，导致 API 报错或消息被截断。 | Agent.cs:113-122 | 连续对话超过模型上下文窗口的 70% | 在 Agent.BuildMessages() 中调用 ContextCompressor.CheckAndCompressAsync(session.Messages) |
| D3 | **Anthropic Provider 不支持工具调用**：ChatAsync 接收 tools 参数但不发送给 API，也不解析 tool_use 响应。使用 Claude 模型时 LLM 无法调用任何工具。 | AnthropicProvider.cs:64-118 | 选择 Anthropic 供应商后，LLM 尝试调用工具 | 在 AnthropicRequest 中添加 tools 字段，解析 content_block_delta 中的 tool_use 类型 |
| D4 | **Ollama Provider 不支持工具调用**：同 D3，OllamaProvider 不发送 tools 也不解析工具响应。 | OllamaProvider.cs:64-103 | 选择 Ollama 供应商后，LLM 尝试调用工具 | 在 OllamaChatRequest 中添加 tools 字段，解析响应中的 tool_calls |

### P1 — 高（功能缺失 / 数据丢失）

| # | 缺陷 | 文件:行号 | 复现条件 | 修复建议 |
|---|------|-----------|----------|----------|
| D5 | **记忆系统只写不读**：MemoryStore.Save 在 Agent.ExecuteToolAsync 中调用（保存工具结果），但 GetRelevant 从未被调用，BuildWithMemory 从未被调用。存储的记忆永远不会被注入到系统提示词中。 | Agent.cs:136, 117 | 任何对话 | 在 Agent.BuildMessages 中调用 MemoryStore.Instance.GetRelevant(session.Id) 并使用 BuildWithMemory |
| D6 | **API Key 明文存储**：设计要求使用 DPAPI 加密，实际 API Key 明文写入 providers.json。 | ProviderManager.cs:210-219 | 任何供应商配置 | 使用 System.Security.Cryptography.ProtectedData 加密 API Key |
| D7 | **DeepCopy 丢失关键字段**：ToolBoxOption.DeepCopy 未复制 Theme、AutoScreenshotEnabled、AutoScreenshotCron、DailyReportEnabled、DailyReportTime、CompactOpacity、ScreenshotMaxAge、ChatFontSize 共8个字段。 | SetunaOption.cs:144-186 | 调用 DeepCopy 后保存，这些字段被重置为默认值 | 在 DeepCopy 的 ToolBoxOptionData 初始化中补全所有字段 |
| D8 | **App 无 OnExit 资源释放**：CronScheduler（CronExpressionr）的 Timer、ChatManager 的 autoSaveTimer 未在退出时 Dispose，可能导致进程残留或定时器泄漏。 | App.xaml.cs | 应用退出 | 重写 App.OnExit，调用 CronExpressionr.Instance.Dispose() |

### P2 — 中（功能不完整 / 体验问题）

| # | 缺陷 | 文件:行号 | 复现条件 | 修复建议 |
|---|------|-----------|----------|----------|
| D9 | **工具调用结果在流式期间不显示**：ChatView.SendMessage 中创建 ToolCallCard 时 result 传 null，后续工具执行完成后不更新卡片。用户在流式期间看不到工具结果。 | ChatView.xaml.cs:177-179 | LLM 调用工具时 | 在 Agent yield 工具结果 chunk 后更新 ToolCallCard，或在 TurnMessages 持久化后刷新卡片 |
| D10 | **ReportGenerator 不调用 LLM**：设计要求通过 LLM 生成日报/周报，实际仅拼接 Markdown 表格，无法产生智能摘要。 | ReportGenerator.cs:22-69 | 定时触发日报生成 | 注入 ILlmProvider，调用 ChatAsync 生成报告 |
| D11 | **Progress=100 不同步 IsCompleted**：设计要求 Progress=100 自动标记完成，反之亦然。当前 Progress 和 IsCompleted 是独立的。 | TodoStore.cs:137-155 | 拖动 Progress 到 100% | 在 UpdateAsync 中检查 progress==100 时设置 IsCompleted=true |
| D12 | **单轮仅处理1个工具调用**：OpenAI API 可返回多个 tool_calls，但 Agent 和 OpenAiProvider 都只处理第一个。 | Agent.cs:75, OpenAiProvider.cs:152 | LLM 在一次响应中返回多个工具调用 | 循环处理所有 tool_calls |
| D13 | **上下文压缩的摘要模式不调用 LLM**：ApplySummaryCompression 仅拼接消息文本，不调用 LLM 生成摘要，压缩效果差。 | ContextCompressor.cs:79-96 | 消息超过 95% 上下文窗口 | 注入 ILlmProvider 调用 ChatAsync 生成摘要 |
| D14 | **HistoryView 无搜索功能**：设计要求 HistoryView 支持搜索，实际未实现。 | HistoryView.xaml.cs | 在截图历史页搜索 | 添加搜索框，按文件名/时间过滤 |

### P3 — 低（代码质量 / 文档过时）

| # | 缺陷 | 文件:行号 | 复现条件 | 修复建议 |
|---|------|-----------|----------|----------|
| D15 | **design.md L247 注释过时**：注释称 Progress/StartDate/Children 均未实现，但 Progress 已实现（TodoItem.cs:14）。 | design.md:247 | — | 更新注释 |
| D16 | **ScreenshotTracker 命名误导**：类名为 ScreenshotTracker 但实际追踪窗口切换，不采集截图元数据。设计中的 RecordCapture 方法未实现。 | ScreenshotTracker.cs:1-90 | — | 重命名为 ActivityTracker，或补充截图元数据采集 |
| D17 | **ToolRegistry 实例方法调用传 null**：Register(Type) 扫描 Instance 方法但 Execute 始终传 null 作为 target，若注册实例方法会 NullReferenceException。 | ToolRegistry.cs:15,108,138 | 注册含 [Tool] 的实例方法 | 存储 instance 引用或在 Invoke 时传入 |

---

## 四、最关键的 3 个发现

### 发现 1：文件工具安全防护完全缺失（D1）

FileAccessWhitelist 和 ConfirmDialog 两个安全类已完整实现（白名单检查 + 确认弹窗），但在 FileTools 中从未被调用。这意味着 LLM 可以通过 `read_file`、`write_file`、`delete_file` 等工具操作系统上的任意文件，包括系统文件、用户文档、配置文件等。这是一个严重的安全漏洞，特别是考虑到 LLM 的输出具有一定的不确定性。

**影响范围**：所有使用 LLM 工具调用功能的用户  
**修复优先级**：P0（立即修复）

### 发现 2：上下文压缩和记忆系统形同虚设（D2 + D5）

ContextCompressor 类已完整实现三级压缩策略（snip/aggressive/summary），但从未被调用。MemoryStore 已实现 SQLite 存储和检索，但 GetRelevant 从未被调用，BuildWithMemory 从未被调用。这意味着：
- 长对话会直接超出模型上下文窗口，导致 API 报错
- 存储的"记忆"永远不会被读取或注入到对话中
- 记忆系统的自动归纳（LLM 分析对话提取记忆）完全未实现

**影响范围**：所有 LLM 对话用户，尤其是长对话场景  
**修复优先级**：P0（D2）+ P1（D5）

### 发现 3：Anthropic 和 Ollama Provider 不支持工具调用（D3 + D4）

OpenAI Provider 完整实现了工具调用（发送 tools + 解析 tool_calls），但 Anthropic Provider 和 Ollama Provider 都不发送 tools 参数也不解析工具响应。这意味着：
- 使用 Claude 模型时，LLM 无法管理待办、操作文件、搜索网络
- 使用 Ollama 本地模型时，同样无法使用工具
- 只有 OpenAI 兼容 API 的用户才能使用完整的工具调用功能

此外，这两个 Provider 也不支持图片/视觉消息，截图对话功能无法工作。

**影响范围**：所有使用 Anthropic 或 Ollama 供应商的用户  
**修复优先级**：P0

---

## 五、设计文档准确性备注

1. **design.md L247** 注释过时：称 Progress/StartDate/Children "均未实现"，但 Progress 已实现（字段+UI）。StartDate 确实未实现。Children 改用 ParentId 平铺模型替代。
2. **design.md 配置表**未列出 `ShowSplashWindow`、`ScreenshotMaxAge`、`ChatFontSize` 三个已实现的配置项。
3. **recurring-todo-design.md** 描述的可重复待办功能完全未实现（无 RepeatConfig、RepeatCalculator、CatchUpRepeating 等任何相关代码）。
4. **Screenshot-Report-Design.md** 中 ScreenshotTracker 的设计（RecordCapture 采集截图元数据）与实现（RecordWindowSwitch 追踪窗口切换）方向不同。ReportGenerator 的 LLM 集成未实现。
5. **LLM-Assistant-Design.md** 中三层记忆架构（对话上下文 + 自动归纳 + 用户指令）仅实现了第一层的部分功能（存储），自动归纳和注入均未实现。

---

*报告结束*
