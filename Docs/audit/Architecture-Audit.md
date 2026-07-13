# ToolBox 架构与质量审查报告

> 审查人：架构师 高见远
> 审查范围：`Project/` 全部源码（Core/Models/Services/Views/Themes）
> 审查日期：2026-07-14
> 对照文档：`Docs/design.md`、`AGENTS.md`

---

## 一、问题统计

| 严重度 | 死代码 | 性能问题 | 架构/技术债 | 规范偏离 | 合计 |
|--------|--------|----------|-------------|----------|------|
| 高     | 3      | 5        | 4           | 2        | 14   |
| 中     | 8      | 5        | 6           | 3        | 22   |
| 低     | 7      | 2        | 3           | 2        | 14   |
| **合计** | **18** | **12**   | **13**      | **7**    | **50** |

---

## 二、最严重的 5 个问题（高优先级）

### 🔴 #1 安全层完全脱节 — LLM 可任意读写删除文件

- **文件**：`Core/Tools/FileTools.cs`（全文）、`Core/Security/FileAccessWhitelist.cs`、`Core/Security/ConfirmDialog.cs`
- **问题**：`FileAccessWhitelist` 和 `ConfirmDialog` 两个安全组件已实现但**从未被 `FileTools` 调用**。LLM 通过 `read_file`/`write_file`/`delete_file`/`move_file` 等工具可操作系统上任意路径的文件，无白名单校验、无危险操作确认。
- **证据**：Grep 搜索 `FileAccessWhitelist` 和 `ConfirmDialog` 仅返回各自定义处，无任何调用点。
- **影响**：**高** — 安全漏洞。恶意或幻觉的 LLM 指令可删除用户重要文件。
- **修复建议**：在 `FileTools` 每个写/删方法入口添加 `FileAccessWhitelist.Instance.IsAllowed(path)` 校验；`delete_file`/`move_file` 调用 `ConfirmDialog.Show()` 弹窗确认。

### 🔴 #2 流式输出时每个 token 触发全量 Markdown 重解析 + 磁盘读配置

- **文件**：`Views/Chat/MessageBubble.xaml.cs:104-108`、`Views/Chat/MessageBubble.xaml.cs:125`
- **问题**：`AppendContent(string text)` 在拼接文本后立即调用 `RenderMarkdown()`，而 `RenderMarkdown()` 内部调用 `MarkdownDocument.Parse(_rawText)` 对**完整文本**重新解析。流式输出中每个 token（可能数百到数千次）都会触发一次全量正则解析。更严重的是，`RenderMarkdown()` 每次还调用 `ToolBoxOption.Load()` 从磁盘反序列化 `config.xml` 以读取 `ChatFontSize`——流式输出期间**每秒可能执行数十到数百次磁盘 IO**。
- **影响**：**高** — 长回复时 UI 卡顿、CPU 飙升、磁盘频繁读取。
- **修复建议**：流式期间仅追加纯文本到 FlowDocument（不调用 Parse）；流式结束后调用一次 `RenderMarkdown()`。`ChatFontSize` 缓存为字段或从已加载的 options 读取，不重复 Load。

### 🔴 #3 每 500ms 无条件触发窗口事件 + 每次写活动日志文件

- **文件**：`Views/MainWindow.cs:44-46,418-421`、`Core/Window/WindowManager.cs:35-36`、`Core/Scheduling/ScreenshotTracker.cs:83-89`
- **问题**：`windowTimer` 每 500ms 调用 `WindowManager.Instance.Update()`，而 `Update()` **无条件**触发 `WindowActived` 和 `TopMostChanged` 事件（即使前台窗口未改变）。`App.OnStartup` 订阅了 `WindowActived` 事件调用 `ScreenshotTracker.RecordWindowSwitch`，该方法在**每次调用**时都计算时长并执行 `File.AppendAllText` 写入 JSONL 文件。即使前台窗口没变，每 500ms 也会产生一条记录和一次文件写入。
- **影响**：**高** — 每小时约 7200 次文件写入；活动日志文件快速膨胀；频繁磁盘 IO。
- **修复建议**：`WindowManager.Update()` 应比较新旧窗口句柄，仅在变化时触发事件；`ScreenshotTracker.RecordWindowSwitch` 应判断 `appName == lastApp` 时跳过写入。

### 🔴 #4 ScrapWindow.CloseScrap() 调用 GC.Collect()

- **文件**：`Models/ScrapWindow.cs:156-162`
- **问题**：每次关闭贴图窗口都显式调用 `GC.Collect()`，触发完整垃圾回收（标记-压缩全堆）。频繁关闭贴图时会导致 UI 线程卡顿（GC 暂停）。
- **影响**：**高** — 性能反模式，GC.Collect 不应由业务代码显式调用。
- **修复建议**：删除 `GC.Collect()` 调用。BitmapSource 已 Freeze，GDI 句柄已在 finally 中 DeleteObject，GC 会自动回收。

### 🔴 #5 TodoStore 同步包装器 sync-over-async 阻塞

- **文件**：`Core/Todo/TodoStore.cs:197-199,240-246`
- **问题**：8 个同步方法（`Add`/`Complete`/`Uncomplete`/`Delete`/`Update`/`Trash`/`Restore`/`DeletePermanently`）通过 `Task.Run(() => XxxAsync(...)).GetAwaiter().GetResult()` 实现。这是经典的 sync-over-async 模式：在线程池上启动 Task 再阻塞等待，占用两个线程。`TodoTools`（LLM 工具）全部调用这些同步方法。如果从 UI 线程直接调用（如 `TodoView` 部分路径），会阻塞 UI。
- **影响**：**高** — 线程池饥饿风险、潜在死锁。
- **修复建议**：`TodoTools` 改为 `async Task<string>` 方法直接调用 `XxxAsync`；删除同步包装器或标记为 `[Obsolete]`。

---

## 三、死代码清单

| # | 文件:行号 | 说明 | 删除建议 |
|---|-----------|------|----------|
| 1 | `Core/Llm/ContextCompressor.cs`（全文） | 整个类从未被实例化或调用。`Agent.BuildMessages` 不使用上下文压缩。 | 保留代码但标记为未集成；或接入 Agent 管线 |
| 2 | `Core/Security/FileAccessWhitelist.cs`（全文） | 从未被 FileTools 调用。 | **不要删除**，应接入 FileTools（见问题 #1） |
| 3 | `Core/Security/ConfirmDialog.cs`（全文） | 从未被调用。 | **不要删除**，应接入 FileTools（见问题 #1） |
| 4 | `Core/Window/WindowsFilter.cs`（全文） | `WindowsFilter` 类从未被实例化。 | 可删除或接入 WindowManager 过滤逻辑 |
| 5 | `Services/SingletonApplication.cs`（全文） | 单例守卫已由 `App.xaml.cs` 的 Mutex 实现，此类从未使用。 | 删除 |
| 6 | `Core/Native/NativeMethods.cs:156-161` | `GetModuleName` 方法从未被调用。 | 删除 |
| 7 | `Views/MainWindow.cs:169-190` | `GetModifiers` 和 `GetPlainKey` 两个方法从未被调用，与 `ExtractModifiers`/`ExtractPlainKey` 逻辑重复。 | 删除 |
| 8 | `Models/ScrapWindow.cs:216-234` | `GetThumbnail` 方法从未被调用。 | 删除 |
| 9 | `Models/SetunaOption.cs:144-186` | `ToolBoxOption.DeepCopy()` 从未被调用，且拷贝不完整（缺少 CompactOpacity/AutoScreenshot*/DailyReport*/Theme/ScreenshotMaxAge/ChatFontSize 等字段）。 | 删除或修复后保留 |
| 10 | `Core/Llm/ISystemPromptBuilder.cs:6` + `DefaultSystemPromptBuilder.cs:24-29` | `BuildWithMemory` 方法从未被调用。 | 删除或接入 Agent（配合 MemoryStore.GetRelevant） |
| 11 | `Core/Memory/MemoryStore.cs:61-84,86-95` | `GetRelevant` 和 `Cleanup` 从未被调用。MemoryStore.Save 被调用但数据只写不读。 | 接入 Agent 管线或删除 |
| 12 | `Models/Events.cs:28-45` | `IScrapStyleListener`、`IScrapMenuListener`、`IScrapKeyPressEventListener` 三个接口从未被实现。 | 删除 |
| 13 | `Models/Events.cs:57-75` | `IScrapLocationChangedListener`、`IScrapImageChangedListener`、`IScrapStyleAppliedListener`、`IScrapStyleRemovedListener` 四个接口被 CacheManager 实现但**从未通过接口调用**。ScrapBook 中对应的事件处理器为空方法。 | 修复 ScrapBook 转发逻辑或删除接口 |
| 14 | `Services/ScrapBook.cs:227-233` | `OnScrapCreated`/`OnScrapActive`/`OnScrapInactive`/`OnScrapLocationChanged`/`OnScrapImageChanged`/`OnScrapStyleApplied`/`OnScrapStyleRemoved` 七个空方法。 | 应转发到 CacheManager 或删除 |
| 15 | `Core/Tools/ToolRegistry.cs:48-82` | `Register(object instance)` 方法从未被调用（所有工具类均为 static，使用 `Register(Type)`）。 | 删除 |
| 16 | `Core/Llm/ChatManager.cs:78-86` | `SwitchSession`（同步）与 `SwitchSessionAsync` 功能重复。 | 统一为异步版本 |
| 17 | `Core/Todo/TodoStore.cs:197-199,240-246` | 8 个同步包装器仅在 `TodoTools` 中使用。 | 改 TodoTools 为 async 后删除 |
| 18 | `Views/CompactToolboxWindow.xaml.cs:365-376` | `Border_MouseLeftButtonDown` 标注为 "Legacy handler"，与 `RootBorder_PreviewMouseLeftButtonDown` 逻辑重复。 | 确认无 XAML 引用后删除 |

---

## 四、性能问题清单

| # | 文件:行号 | 问题 | 等级 | 修复建议 |
|---|-----------|------|------|----------|
| 1 | `MessageBubble.xaml.cs:104-108` | 流式输出每个 token 触发 `RenderMarkdown()` 全量重解析 | 高 | 流式期间仅追加文本，结束后渲染一次 |
| 2 | `MessageBubble.xaml.cs:125` | `RenderMarkdown()` 每次调用 `ToolBoxOption.Load()` 读磁盘 | 高 | 缓存 ChatFontSize 为字段 |
| 3 | `MainWindow.cs:44-46` + `WindowManager.cs:35-36` | 500ms 定时器无条件触发事件，ScreenshotTracker 每 500ms 写文件 | 高 | WindowManager 仅在窗口变化时触发事件 |
| 4 | `ScrapWindow.cs:160` | `CloseScrap()` 调用 `GC.Collect()` | 高 | 删除 GC.Collect() |
| 5 | `TodoStore.cs:197-199,240-246` | sync-over-async 阻塞 (`Task.Run().GetAwaiter().GetResult()`) | 高 | 改为全异步链路 |
| 6 | `WorkbenchWindow.xaml.cs:40-43` | 每次导航重建 ChatView/TodoView/HistoryView/SettingsView，丢失状态 | 中 | 缓存视图实例，仅 DashboardView 做了缓存 |
| 7 | `TodoView.xaml.cs:123-128` | `AddItemAndChildren` 递归中每个节点调用 `GetChildren`，O(n²) | 中 | 一次性加载全部 items 后在内存构建树 |
| 8 | `CompactToolboxWindow.xaml.cs:464-493` | `LoadScreenshots` 同步遍历目录+加载图片，在 UI 线程 | 中 | 移至后台线程或懒加载 |
| 9 | `OpenAiProvider.cs:38-39,78-79` / `AnthropicProvider.cs:44-47,87-90` | 每次请求 mutate `httpClient.DefaultRequestHeaders`，非线程安全 | 中 | 使用 `HttpRequestMessage` 逐请求设置 header |
| 10 | `CompactToolboxWindow.xaml.cs:47-48` / `HistoryView.xaml.cs:35` / `TodoView.xaml.cs:18` | 事件回调中使用 `Dispatcher.Invoke`（同步），可能死锁 | 中 | 改用 `Dispatcher.BeginInvoke`（异步） |
| 11 | `MarkdownDocument.cs:43,51,66,69,82,85` | 每行调用 `Regex.IsMatch`/`Regex.Match`，Regex 未预编译缓存 | 低 | 使用 `static readonly Regex` 预编译 |
| 12 | `ProviderManager.cs:210-219,221-230` | `SaveConfig`/`SaveActiveModel` 同步 `File.WriteAllText` | 低 | 改为 async 或在后台线程执行 |

---

## 五、架构坏味道与技术债清单

| # | 分类 | 严重度 | 问题 | 重构建议 |
|---|------|--------|------|----------|
| 1 | 安全 | 高 | `FileTools` 无白名单校验、无危险操作确认。`DefaultSystemPromptBuilder` 声称"需在白名单目录内""delete_file 始终需要用户确认"，但实际未实现。系统提示词与实现不一致，误导 LLM。 | 接入 FileAccessWhitelist + ConfirmDialog；修正提示词 |
| 2 | 分层越界 | 中 | `Models/ScrapWindow.cs:674` 直接调用 `Services.LayerManager.Instance.UnregisterWindow`（Model → Services）。`Core/Native/WpfTrayIcon.cs:87` 直接调用 `Services.LayerManager.Instance.RefreshLayer`（Core/Native → Services）。 | 通过事件/接口解耦，上层订阅 ScrapWindow.OnScrapClose |
| 3 | 缓存失效 | 中 | `ScrapBook` 的 `OnScrapLocationChanged`/`OnScrapImageChanged`/`OnScrapStyleApplied`/`OnScrapStyleRemoved` 均为空方法。贴图移动/编辑/样式变更后缓存不更新，重启后位置和样式丢失。CacheManager 实现了对应接口方法但从未被调用。 | ScrapBook 转发事件到 CacheManager 或 MainWindow 中补充订阅 |
| 4 | 上下文管理 | 中 | `ContextCompressor` 已实现（snip/aggressive/summary 三级压缩）但未接入 Agent 管线。长对话不会自动压缩，依赖 LLM 供应商的 token 限制报错。 | Agent.BuildMessages 中调用 `ContextCompressor.CheckAndCompressAsync` |
| 5 | 记忆系统 | 中 | `MemoryStore.Save` 在 `Agent.ExecuteToolAsync` 中被调用（写入），但 `GetRelevant` 从未被调用（不读取）。记忆只写不读，数据库无限增长。 | Agent.BuildMessages 中调用 `MemoryStore.Instance.GetRelevant` 注入历史记忆 |
| 6 | 视图生命周期 | 中 | `WorkbenchWindow.LoadPage` 每次导航 `new ChatView()`/`new TodoView()` 等，仅 `DashboardView` 缓存。切换标签丢失滚动位置、选中项、聊天会话状态。 | 所有页面视图缓存为字段，导航时复用 |
| 7 | OCR 幻觉 | 中 | `DefaultSystemPromptBuilder` 声称"OCR 识别图片文字（非多模态模型时自动调用）"，但 ToolRegistry 中无 OCR 工具注册。 | 注册 OCR 工具或从提示词中删除该声明 |
| 8 | fire-and-forget | 低 | 多处 `_ = SaveAsync()` / `_ = SaveSessionMessagesAsync()` 无异常处理（TodoStore:73,84; ChatManager:63,213; TodoView:407）。 | 添加 `.ContinueWith(t => Log(t.Exception))` 或改用 async |
| 9 | 事件未解绑 | 低 | `ChatView` 中 `bubble.QuoteRequested += OnQuoteRequested` 从不解绑，`MessagePanel.Children.Clear()` 后旧 bubble 引用可能泄漏。`CompactToolboxWindow` 订阅 `TodoStore.ItemsChanged`/`ChatManager.SessionsChanged`/`CacheManager.OnScrapCached` 不解绑（单例窗口下影响小）。 | Clear 前解绑事件；或使用 WeakEventManager |
| 10 | 代码重复 | 低 | `ToolRegistry.Register(Type)` 与 `Register(object)` 近乎完全重复（42 行重复代码）。`ScrapBook.AddScrap` 两个重载重复事件订阅/初始化逻辑（30+ 行）。`MainWindow.GetModifiers`/`GetPlainKey` 与 `ExtractModifiers`/`ExtractPlainKey` 重复。 | 提取公共方法 |
| 11 | 定时截图多屏 | 低 | `ScheduleManager.CaptureFullScreen:69-74` 使用 `SystemParameters.PrimaryScreenWidth`，仅截取主屏幕，不支持多显示器。DPI 缩放下尺寸可能不正确。 | 使用 `SystemParameters.VirtualScreen*` 支持多屏 |
| 12 | 工具调用截断不一致 | 低 | `Agent.cs:134` 截断 `toolCall.Result` 为 3000 字符，但 `Agent.cs:136` 将**未截断的** `result` 写入 MemoryStore，返回的是截断后的 `toolCall.Result`。 | 统一使用截断后的文本 |
| 13 | AnthropicProvider 无工具调用 | 低 | `AnthropicProvider.ChatAsync` 完全不处理 tool_calls 流式响应，不支持 LLM 工具调用。OpenAI Provider 支持。Anthropic 模型无法触发工具。 | 实现 Anthropic Messages API 的 tool_use 事件解析 |

---

## 六、规范偏离清单

| # | 文件:行号 | 偏离内容 | 规范要求 |
|---|-----------|----------|----------|
| 1 | `Core/Theming/ThemeManager.cs:19` | 字段 `_instance` 使用下划线前缀 | AGENTS.md："不使用下划线前缀（`_`、`m_`、`k_`、`s_`）" |
| 2 | `Views/Chat/MessageBubble.xaml.cs:10` | 字段 `_rawText` 使用下划线前缀 | 同上 |
| 3 | `Core/Scheduling/CronScheduler.cs:6` | 类名 `CronExpressionr`（拼写错误，应为 `CronScheduler`） | design.md 第 63 行标注为 `CronScheduler.cs`；命名应大驼峰且无拼写错误 |
| 4 | `Core/Window/` 目录 | 目录名 `Window`（单数），但命名空间为 `ToolBox.Core.Windows`（复数）。`App.xaml.cs:70` 引用 `Core.Windows.WindowManager`。 | 目录与命名空间应一致 |
| 5 | `Models/ScrapWindow.cs:507,514,522,543,619,623` | 右键菜单项使用硬编码中文字符串（"复制"/"剪切"/"另存为"/"代办识别"/"发起对话"/"关闭"） | 应使用 `FindResource("Lang_...")` 语言资源，与其他窗口一致 |
| 6 | `Models/ScrapWindow.cs:99,427,432` | 边框颜色硬编码（`Color.FromArgb(180,120,120,120)` / `Colors.DodgerBlue`） | AGENTS.md："新增界面元素必须与设计风格保持一致，参照 DesignTokens.xaml" |
| 7 | `Core/Markdown/MarkdownDocument.cs:10-14` | 颜色硬编码（`#0F1117`/`#5C6370`/`#9CA3AF`/`#6A1B9A`/`#F0F1F4`），不随主题切换 | 应使用 DynamicResource 主题画笔；当前在 Dark 主题下文字几乎不可见 |

---

## 七、对 design.md 的偏离汇总

| # | design.md 描述 | 实际代码 | 差异说明 |
|---|----------------|----------|----------|
| 1 | 第 17 行：NuGet 依赖 `Cronos 0.8.4`、`Microsoft.Data.Sqlite 8.0.0`、`System.Drawing.Common 8.0.0`、`MdXaml` | csproj：`Cronos 0.13.0`、`Microsoft.Data.Sqlite 10.0.9`、`System.Drawing.Common 10.0.9`、`Newtonsoft.Json 13.0.4`、`ZLinq 1.5.6`、`ZString 2.6.0`，**无 MdXaml** | design.md 严重过时 |
| 2 | 第 63 行：`CronScheduler.cs — Cron 定时器` | 类名为 `CronExpressionr` | 拼写错误 |
| 3 | 第 234 行：Markdown 渲染"由 MessageBubble 内嵌 MdXaml 控件呈现" | `MessageBubble.xaml.cs:114` 使用自定义 `Core/Markdown/MarkdownDocument.Parse()`，无 MdXaml | 渲染方案变更未同步文档 |
| 4 | 第 247 行：`Progress`/`StartDate`/`Children` 字段"当前代码中均未实现" | `TodoItem.cs:14` 已实现 `Progress`；`ParentId`（line 6）替代 `Children` 实现树结构；`StartDate` 确实未实现 | 部分已实现，文档过时 |
| 5 | 第 250 行：可重复待办（`RepeatConfig`/`CatchUpRepeating`/CronScheduler 注册提醒） | 全部未实现，代码中无 `RepeatConfig`/`recurring`/`CatchUp` 任何引用 | 整个功能缺失，仅有设计文档 `Docs/recurring-todo-design.md` |
| 6 | 第 261 行：设置中"文件白名单" | `SettingsView.xaml.cs` 无白名单管理 UI；`FileAccessWhitelist` 无调用方 | 功能声称存在但未实现 |
| 7 | 第 350 行：全局配置路径 `%AppData%/Setuna/config.xml` | 代码确认使用 `"Setuna"`（`SetunaOption.cs:48`），但 TodoStore/ChatManager/ProviderManager/MemoryStore/ScreenshotTracker 均使用 `"ToolBox"` | 应用数据目录不统一，迁移未完成 |
| 8 | 第 150-155 行：Workbench 页面 `dashboard`/`assistant`/`todos`/`screenshots`/`settings` | 托盘菜单和 `MainWindow.OpenWorkbench` 使用 `chat`/`todo`/`settings`/`history`，与页面 ID 不匹配 | "settings"/"history" 在 `CompactToolboxWindow.SwitchToTab` 中无对应处理，显示空白 |
| 9 | 第 199 行：Agent "最多执行 10 轮工具调用，每轮最多 3 次重试" | `Agent.cs:47` 有 10 轮循环，但无 3 次重试逻辑 | 重试机制未实现 |
| 10 | 第 232 行：`ContextCompressor` 超限时裁剪早期消息 | `ContextCompressor` 从未实例化 | 功能未接入 |
| 11 | 第 192 行：`MemoryStore — SQLite 记忆存储` | Save 被调用但 GetRelevant 从未调用 | 记忆系统半成品 |

---

## 八、`ToolBoxOptionData.ClickCapture` 字段跳号

- **文件**：`Models/SetunaOption.cs:218-225`
- **问题**：`ClickCapture1`/`ClickCapture2`/`ClickCapture3`/`ClickCapture4`/`ClickCapture6`/`ClickCapture7`/`ClickCapture8`/`ClickCapture9` — 缺少 `ClickCapture5`。跳号可能是从原 SETUNA2 移植时遗留。
- **影响**：低 — XML 序列化兼容性，不影功能，但代码可读性差。
- **建议**：确认是否需要 ClickCapture5 或重命名为连续编号。

---

## 九、建议的优先修复顺序

1. **P0（安全）**：接入 `FileAccessWhitelist` + `ConfirmDialog` 到 `FileTools`；修正 `DefaultSystemPromptBuilder` 提示词
2. **P0（性能）**：修复 `MessageBubble` 流式渲染（问题 #2）；修复 `WindowManager.Update()` 事件触发（问题 #3）；删除 `GC.Collect()`（问题 #4）
3. **P1（架构）**：修复 `ScrapBook` 事件转发 → CacheManager 缓存更新；接入 `ContextCompressor` 到 Agent；消除 sync-over-async
4. **P1（一致性）**：统一数据目录（`Setuna` → `ToolBox`）；修复托盘菜单页面 ID 映射；缓存 Workbench 页面视图
5. **P2（清理）**：删除死代码；修复命名规范；更新 design.md

---

## 十、需要同步更新 design.md 的条目

- [ ] NuGet 依赖版本（第 17 行）
- [ ] 删除 MdXaml 描述，改为自定义 MarkdownDocument（第 234 行）
- [ ] CronScheduler → CronExpressionr（或修正类名后更新文档）（第 63 行）
- [ ] TodoItem 字段实现状态（第 247 行）
- [ ] 可重复待办实现状态（第 250 行）
- [ ] 文件白名单实现状态（第 261 行）
- [ ] ContextCompressor 接入状态（第 232 行）
- [ ] Agent 重试机制实现状态（第 199 行）
- [ ] 统一数据目录路径（第 350 行）

---

*报告结束。如需对任何条目进行更深入的分析或提供具体重构方案，请随时沟通。*
