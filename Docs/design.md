# ToolBox 设计文档

## 项目概述

ToolBox 是一款 Windows 桌面截图+贴图工具，基于 .NET 8.0 WPF 构建。在传统截图贴图功能基础上，集成了待办与提醒管理、剪贴板历史、截图历史与标签、OCR 文本识别与在线翻译、定时截图与日报、网络环境诊断、定时关机等能力，并以 Agent CLI + 命名管道对外提供受控的待办/OCR/截图查询。

> 原「AI 助手」（会话式 Agent + 工具调用）曾在 2026-10-08 移除，2026-10-16 恢复并接回工作台（设计见「AI 助手与 Agent 运行时」）；LLM Provider 层同时服务于助手、定时日报与贴图「识别待办」。

## 技术栈

| 项 | 值 |
|---|---|
| 框架 | .NET 8.0 WPF |
| 语言 | C# |
| 目标 | `net8.0-windows10.0.19041` |
| RootNamespace | `ToolBox` |
| AssemblyName | `ToolBox` |
| 版本 | 2.0.0 |
| NuGet 依赖 | `Cronos 0.13.0`、`Microsoft.Data.Sqlite`、`System.Drawing.Common`、`MdXaml`（Markdown 渲染）、`System.CommandLine 2.0.2`（仅 CLI 工程，MIT） |

## 架构

### Agent CLI 与本地后台

`Project/Cli/` 是同一 solution 中的 console 工程，以 System.CommandLine 2.0.2（MIT）解析命令；构建和发布时与 ToolBox.exe、运行依赖及许可文件一起交付。共享协议源文件位于 `Core/Todo/`（看板存储与投递另见 `Core/Boards/`、`Core/BoardAgents/`），不另建 DTO 类库。Agent 技能源集中存放于 `Docs/skills/`（`cli-anything-toolbox` 与 `toolbox-board`），CLI 工程以链接文件随构建/发布复制到 `skills/`；技能指导调用现有命令，不改变业务接口或权限。

CLI 提供能力发现、待办 CRUD/状态/回收站、项目登记与看板 CRUD/协作（四态认领、评论、附件）、指定图片离线 OCR、截图标签只读查询。能力发现读取静态允许列表及 schema；其余请求经本机命名管道交给唯一 ToolBox 实例。后台启动参数 `--task-host` 不初始化窗口、热键、剪贴板监听或截图调度；普通启动向后台转发 `app.activate`，在同一实例初始化桌面。已打开界面时拒绝 `host stop`。

管道名包含当前用户 SID；服务端 ACL 只允许当前用户并拒绝网络登录，客户端使用 CurrentUserOnly，仅连接本机。请求和响应为带版本/requestId 的 UTF-8 单行 JSON，单帧 JSON 上限 4 MiB，连接启动上限 10 秒、响应等待 30 秒；已发送请求超时或断连不重放。host 串行处理请求并派发 Dispatcher，待办命令在 Store 的 stateLock 内完成验证、快照和变更；保存采用同目录临时文件 flush+原子替换，失败恢复原对象状态，损坏数据拒绝命令。

OCR 复用 Tesseract/WindowsOCR，仅读取本次显式指定的本地图片，拒绝网络路径/重解析点，限制文件与像素大小，禁止自动下载、翻译和历史写入。截图查询复用标签 Store，不启动缓存迁移/清理，公开 17 位截图标识而非绝对目录，按标识稳定分页。输入/输出、授权范围详见 `agent-cli-design.md`，待办契约见 `task-cli-design.md`，调用示例见 `task-cli-usage.md`。

### 目录结构

```
Project/
├── App.xaml / App.xaml.cs             # 应用入口，单例守卫，语言加载
├── GlobalUsings.cs                     # 全局 using
├── AssistantWeb/                       # 助手前端（React + Vite，构建为静态 dist 随 WPF 发布）
├── Core/
│   ├── HotkeyManager.cs               # 全局热键注册与分发（命名空间 ToolBox.Core.Native）
│   ├── Llm/                           # LLM 运行时（助手会话/流式聊天；日报与贴图识别待办复用其消息模型）
│   │   ├── AssistantRuntime.cs        # 助手运行时（开关、会话运行队列、取消、generation、工具目录）
│   │   ├── ChatSession.cs             # 会话模型与持久化
│   │   ├── Agent.cs                   # 单轮 Agent 循环（流式文本 + 工具步骤）
│   │   ├── ContextCompressor.cs       # 上下文压缩
│   │   ├── ISystemPromptBuilder.cs / DefaultSystemPromptBuilder.cs  # 系统提示词组装
│   │   ├── ChatMessage.cs             # 消息模型（含 ToolCalls 字段与可选 runId）
│   │   └── ChatChunk.cs               # 流式输出片段
│   ├── Assistant/                     # 助手宿主与 CLI 工具作业
│   │   ├── AssistantHost.cs           # 静态宿主（Initialize/SetEnabledAsync/IsEnabled/EnabledChanged/Tools）
│   │   └── ToolsCommandService.cs     # tools.list/get/call/result/cancel 作业队列（4 MiB、requestId 幂等）
│   ├── Providers/                     # LLM 供应商适配（活：助手 + 设置页 + ReportGenerator + ScrapWindow 识别待办）
│   │   ├── ILlmProvider.cs            # 流式聊天接口
│   │   ├── IProvider.cs               # 供应商元数据接口（含 CheckConnectionAsync）
│   │   ├── ModelInfo.cs               # 模型信息
│   │   ├── ProviderManager.cs         # 配置式供应商管理 + 活跃模型 + 自定义供应商持久化
│   │   ├── OpenAiProvider.cs          # OpenAI 兼容 API
│   │   ├── OllamaProvider.cs          # Ollama 本地模型
│   │   └── AnthropicProvider.cs       # Anthropic Claude
│   ├── Translation/                   # 非 LLM 翻译
│   │   └── TranslationService.cs      # 免费公开在线翻译接口（不依赖 LLM Provider，文本外发第三方）
│   ├── Tools/                         # 工具系统（由 AssistantHost 注册 File/Image/Todo/WebSearch/Board 五类）
│   │   ├── ToolAttribute.cs           # [Tool("name", "desc")] 标记静态方法
│   │   ├── ToolParamAttribute.cs      # [ToolParam("desc")] 标记参数
│   │   ├── ToolInfo.cs                # 工具元数据
│   │   ├── ToolRegistry.cs            # 反射扫描 + 执行器（逐个参数做 JSON 类型/必需/未知校验）
│   │   ├── FileTools.cs               # 文件 CRUD（受 FileAccessWhitelist 约束）
│   │   ├── ImageTools.cs              # describe_image（白名单 → 大小 → 真实格式/MIME → 解码/像素校验）
│   │   ├── TodoTools.cs               # 待办 CRUD（供助手调用；外部读写由 Agent CLI 承担）
│   │   ├── BoardTools.cs              # 看板工具（身份由运行时注入，host 固定 internal）
│   │   └── WebSearchTools.cs          # 网络搜索（DuckDuckGo API）
│   ├── Memory/                        # 助手会话记忆
│   │   └── MemoryStore.cs             # SQLite 记忆（%AppData%/ToolBox/Data/memory.db）
│   ├── Todo/
│   │   ├── TodoItem.cs                # 待办数据模型（Id/Title/Description/IsCompleted/Priority/Tags/DueDate/ParentId）
│   │   ├── TodoStore.cs               # 待办持久化 + ItemsChanged 事件 + 重复待办重置与提醒续订
│   │   ├── TodoStore.Commands.cs      # Agent CLI 命令处理（task.*）
│   │   ├── TaskProtocol.cs / TaskIpc.cs / TaskPipeHost.cs  # 命名管道协议（含 task.*/board.*/tools.*）、客户端与宿主
│   │   ├── AgentCapabilities.cs / AgentOcrCommand.cs / AgentScreenshotCommand.cs  # 能力发现与只读查询
│   │   ├── RepeatCalculator.cs / RepeatConfig.cs  # 重复规则计算
│   │   └── SmartDateParser.cs         # 自然语言日期解析
│   ├── Boards/                        # 项目看板存储（schema v2，见「项目文件夹看板」）
│   │   ├── ProjectBoardModels.cs      # 列/卡片/认领/附件/评论模型
│   │   ├── ProjectBoardStore.cs       # 串行读写、校验、原子替换、v1→v2 迁移
│   │   ├── ProjectBoardComments.cs    # 评论、双 revision、sidecar 外置与迁移 journal
│   │   └── ProjectBoardAttachments.cs # 图片附件、SHA256 去重、删除 journal 与恢复
│   ├── BoardAgents/                   # 宿主发现与评论投递（本机配置/状态）
│   │   ├── BoardAgentService.cs       # 适配器、投递确认、失效重探（注入 InternalEnabled/InternalExecutor）
│   │   ├── BoardCommandService.cs     # UI 与 IPC 共享的看板命令分发
│   │   ├── InternalBoardAgent.cs      # 第 9 宿主（internal）：门禁、同会话串行、租约截止、写前校验、回帖 ack 恢复
│   │   ├── BoardAgentModels.cs        # 适配器与投递状态模型
│   │   └── WindowsBoardAgentDiscoveryPlatform.cs  # PATH/注册表/目录探测
│   ├── Reminder/                      # 定时提醒（CRH/CSH-28）
│   │   ├── ReminderItem.cs / ReminderStore.cs  # 提醒模型与 reminders.json 持久化（原子替换 + 读失败禁写）
│   │   └── ReminderScheduler.cs       # 到点注册与右下角通知
│   ├── Clipboard/                     # 剪贴板历史
│   │   ├── ClipboardMonitor.cs        # 系统剪贴板链监听（链转发带超时）
│   │   ├── ClipboardStore.cs          # 条目持久化 + 置顶/收藏 + 缩略图目录
│   │   └── ClipboardEntry.cs          # 条目模型
│   ├── Color/                         # 屏幕取色（取色器 + 历史；放大镜矩形按虚拟屏夹取）
│   ├── ActionChain/                   # 截图后动作链（引擎/存储/节点工厂，节点名大小写与短名均可解析）
│   ├── Scheduling/
│   │   ├── CronScheduler.cs           # Cron 定时器，类名为 CronExpressionr（文件名与类名不一致，勿按文件名找引用）
│   │   ├── ScheduleManager.cs         # 定时任务注册/注销（自动截图 + 每日总结）
│   │   ├── ScreenshotTracker.cs       # 前台窗口活动记录（日报数据源）
│   │   ├── ReportGenerator.cs         # 日报/周报 Markdown 生成
│   │   └── ShutdownScheduler.cs       # 定时关机（错过时刻不补关机，见「稳定性兜底」）
│   ├── Security/
│   │   ├── FileAccessWhitelist.cs     # 文件访问白名单（助手与工具共享；设置页可增删）
│   │   └── ConfirmDialog.cs           # 危险操作确认弹窗
│   ├── Native/
│   │   ├── NativeMethods.cs           # P/Invoke
│   │   └── WpfTrayIcon.cs             # 系统托盘图标
│   ├── Theming/
│   │   └── ThemeManager.cs            # 主题管理器（Light/Dark/System 三态 + 持久化）
│   ├── Markdown/ QrCode/ CloudShare/ SmartRedaction/ StepCapture/ Habits/ Pomodoro/ ScreenFreeze/
│   │                                   # ⚠ 未接线或半成品模块，见「未接线模块」清单
│   └── Window/
│       ├── WindowInfo.cs / WindowManager.cs  # 前台窗口轮询（活动记录与图层联动）
│       ├── WindowPinner.cs            # Ctrl+Shift+T 置顶当前前台窗口
│       ├── PreviewCardManager.cs      # 截图后预览卡
│       └── WindowsFilter.cs           # ⚠ 无调用方
├── Models/
│   ├── SetunaOption.cs                # 全局配置 ToolBoxOption（XML 序列化 + 代理属性；含助手开关 AssistantEnabled）
│   ├── CStyle.cs                      # 样式定义（IStyleItem 通过代理属性序列化）
│   ├── ScrapWindow.cs                 # 贴图窗口模型
│   ├── KeyItem.cs / KeyItemBook.cs    # 快捷键映射
│   ├── Enums.cs                       # EApplicationType 等枚举
│   └── ...
├── Services/
│   ├── ScrapBook.cs                   # 贴图集合管理
│   ├── CacheManager.cs / CacheItem.cs # 截图缓存
│   ├── LayerManager.cs                # 图层管理
│   ├── AutoStartup.cs                 # 开机自启
│   ├── SingletonApplication.cs        # 单实例（Mutex）
│   ├── Styles/                        # 样式项实现
│   │   ├── CBasicStyleItems.cs
│   │   ├── CCopyStyleItem.cs
│   │   ├── CImageStyleItems.cs
│   │   └── CMoreStyleItems.cs
│   └── ...
├── Views/
│   ├── MainWindow.cs                  # 主窗口（隐藏宿主，管理托盘、热键、截图流程）
│   ├── CompactToolboxWindow.xaml/.cs  # 迷你模式（Topmost，右下角常驻，待办/截图两面板，靠边自动隐藏）
│   ├── WorkbenchWindow.xaml/.cs       # 全屏工作台（左侧侧边栏 + 7 页面导航，关闭请求转隐藏）
│   ├── Shell/Sidebar.xaml / TopHeader.xaml  # 工作台侧边栏与顶栏
│   ├── SplashWindow.xaml/.cs          # 启动画面（Loading/Error/Complete 三态）
│   ├── Capture/CaptureWindow.cs       # 截图选区
│   ├── Dashboard/                     # 仪表盘（统计卡片 + 快捷入口）
│   ├── Todo/                          # 待办视图、共享详情面板、重复提醒卡片、日历（⚠ 日历未接线）
│   ├── Boards/                        # 看板视图（BoardsView）、卡片详情（BoardCardDetailsWindow）、主题化弹窗（ProjectBoardDialogs）
│   ├── Chat/                          # 助手页（ChatView + WebView2 承载、AssistantWebProtocol 桥接协议）
│   ├── Clipboard/                     # 剪贴板工作台页 + Ctrl+Shift+V 浮窗
│   ├── Ocr/                           # OCR 结果浮层、选字 Adorner、翻译入口
│   ├── Color/                         # 取色覆盖层、取色历史、取色 Toast
│   ├── NetworkDiagnosis/              # 网络诊断页（诊断/修复计划/确认窗）
│   ├── Reminder/ Pomodoro/ Habits/ StepCapture/ ScreenFreeze/  # 提醒弹窗；⚠ 后三者未接入导航
│   ├── SettingsView.xaml/.cs          # 设置视图（嵌入工作台 settings 页，平铺 Section）
│   ├── HotkeyInputWindow.xaml/.cs     # 快捷键录入
│   ├── Paint/                         # 绘图工具
│   └── ...
├── Themes/
│   ├── DesignTokens.xaml              # 设计令牌 + 共享 Style（CardStyle/FilterTabStyle 等）
│   ├── Light.xaml                     # 浅色主题资源字典
│   ├── Dark.xaml                      # 深色主题资源字典
│   ├── Fonts/                         # 嵌入字体（Geist / Geist Mono）
│   ├── Lang zh-CN.xaml                # 中文语言包
│   └── Lang en-US.xaml                # 英文语言包
└── Docs/
    ├── design.md                      # 本文件
    ├── UI-Redesign-Architecture.md    # 界面重设计方案
    └── Setuna-UI-Redesign.html        # UI 交互原型
```

## 核心架构模式

### UI 架构

ToolBox 采用**启动画面 + 迷你模式 + 全屏工作台**的三组件设计：

**启动画面（SplashWindow）**：
- 480×360px，`WindowStyle=None + AllowsTransparency=True`，圆角 20
- 三状态：`Loading`（70% 红色进度条 + 加载文案）/ `Error`（100% 红色 + 重试按钮）/ `Complete`（100% 绿色 + 倒计时自动关闭）
- 由 `SplashStatus` 枚举驱动，`SetStatus(status, message)` 切换

**迷你模式（CompactToolboxWindow）**：
- `WindowStyle=None`, `AllowsTransparency=True`, `Topmost=True`
- 常驻右下角，圆角 14；宽度按面板切换（待办 / 截图缩略两个面板），透明度由设置项 `CompactOpacity` 控制
- 显示：未完成待办（按 `DueDate` 升序，无截止日期排末尾，可直接切换状态、快速添加、递归显示子任务）+ 最近截图网格
- 底部按钮跳转：✅待办 / 📸截图 / ⚙设置（助手入口仅在工作台侧栏，不在迷你窗）
- 关闭请求（Alt+F4 等）一律转为隐藏：实例由 App 缓存，真关闭后再唤回会抛异常
- **自动隐藏**：鼠标离开窗口区域后自动淡出隐藏（设置页开关），鼠标移入或全局热键触发时恢复；支持多显示器边缘检测（`MonitorFromWindow` P/Invoke），吸附屏幕边缘时只露出触发条

**全屏工作台（WorkbenchWindow）**：
- 960×640px（可最大化），普通窗口
- **布局**：左侧深色侧边栏（Logo + RadioButton 导航项 + 底部区）+ 右侧主内容区（TopHeader + PageHost）
- **7 个页面**（每页的 View 实例懒创建后复用，避免重复订阅事件）：
  - `dashboard` — DashboardView：统计卡片 + 快捷入口，通过 `NavigationRequested` 让工作台跳转
  - `todos` — TodoView：65/35 双栏（左侧筛选+列表，右侧共享详情面板）
  - `screenshots` — HistoryView：网格/列表切换 + 时间筛选（today/week/month/all）+ 标签与搜索 + 分页
  - `clipboard` — ClipboardView：剪贴板历史（搜索/筛选/置顶/收藏）
  - `network` — NetworkDiagnosisView：网络环境诊断与修复
  - `assistant` — ChatView：助手会话页（侧栏导航项默认折叠，需在设置开启；WebView2 承载 Web 前端，禁用时回落）
  - `settings` — SettingsView：平铺式 Section 布局
- 从托盘菜单或迷你窗口打开（`assistant` 页见「AI 助手与 Agent 运行时」）

### 窗口层级

```
Layer 4: 启动画面 SplashWindow (Topmost, 启动期短暂显示)
Layer 3: 浮动截图 ScrapWindow (Topmost, Z-index最高)
Layer 2: 迷你窗口 CompactToolboxWindow (Topmost)
Layer 1: 全屏工作台 WorkbenchWindow (普通窗口)
Layer 0: 全屏截图 CaptureWindow (覆盖层)
```

### 托盘菜单

```
📌 参考图列表（子菜单，无贴图时置灰项）
────────
📸 截图
📋 粘贴
✅ 待办        → 工作台 todos 页
🔔 提醒管理     → 工作台 settings 页并定位到提醒 Section
🌐 网络诊断     → 工作台 network 页
⏸ 暂停监听 5 分钟 → 暂停剪贴板历史采集
────────
⚙ 设置        → 工作台 settings 页
🚪 退出
```

退出只有一个正确入口语义：托盘「退出」与工作台的「退出程序」都收敛到 `MainWindow.Shutdown()`，
它会置 `ScrapBook.IsShuttingDown`（否则每个贴图被标记为非浮窗，下次启动一张都不恢复）并释放托盘图标。

### 单例服务

- `TodoStore.Instance` — 待办持久化 + `ItemsChanged` 事件 + 重复待办提醒续订
- `ProviderManager.Instance` — LLM 供应商管理（配置式创建，支持自定义供应商增删）
- `CacheManager.Instance` — 截图缓存（原子写、坏文件跳过、按 17 位时间戳目录清理过期项）
- `LayerManager.Instance` — 图层管理
- `ClipboardMonitor.Instance` / `ClipboardStore.Instance` — 剪贴板监听与条目存储
- `ColorPickerService` / `ColorHistoryStore` — 取色与历史
- `ScreenshotTracker.Instance` — 前台窗口活动记录（日报数据源）
- `ReminderScheduler.Instance` / `ReminderStore.Instance` — 定时提醒
- `ShutdownScheduler.Instance` — 定时关机
- `ActionChainStore.Instance` / `ActionChainEngine.Instance` — 动作链存储与执行
- `ThemeManager.Instance` — 主题管理（Light/Dark/System 三态 + 持久化到 `ToolBoxOption.Data.Theme`）
- `AssistantRuntime.Instance` — 助手运行时（懒加载；开关、会话运行队列、取消、generation、工具目录）

### LLM 调用点

会话式 Agent（流式文本 + 工具调用循环 + 上下文压缩 + SQLite 记忆）已接回，与日报、识别待办共三处调用 LLM：

- **AI 助手**：`AssistantRuntime` 驱动 `Agent` 循环，按配置供应商流式回答并在授权范围内调用工具（见「AI 助手与 Agent 运行时」）。
- **定时日报**：`ReportGenerator` 把当日窗口活动记录（应用名 + 窗口标题）交给活跃 Provider 生成 Markdown。
- **贴图右键「识别待办」**：`ScrapWindow` 把该贴图区域/整图交给 LLM 解析出候选待办。

三处都会把本机内容发往所配置的第三方端点，属需用户确认的隐私边界；`ILlmProvider.ChatAsync` 的 `tools`
参数仅由助手运行时传入，日报与识别待办恒为 null。

### 配置持久化

- `ToolBoxOption` — XML 序列化到 `%AppData%/ToolBox/config.xml`（各组件各自 `Load()` 出副本，改动统一由设置页保存落盘）
- `CStyle.Items` — 通过代理属性 `ItemsXml` 实现 `IStyleItem` 接口的序列化
- `TodoStore` — JSON 到 `%AppData%/ToolBox/todos.json`（同目录临时文件 flush + 原子替换，失败回滚快照）
- `ReminderStore` — `%AppData%/ToolBox/reminders.json`（原子替换；读取失败时拒绝保存，避免用空表覆盖）
- `ActionChainStore` — `%AppData%/ToolBox/action_chains.json`
- `ProviderManager` — `%AppData%/ToolBox/active_model.json` + `providers.json`（API Key 目前明文存放）
- `ClipboardStore` — `%LocalAppData%/ToolBox/clipboard.json` + `clipboard_thumbnails/`
- `ThemeManager` — 通过 `ToolBoxOption.Data.Theme` 持久化（无独立文件）
- `AssistantRuntime` — 会话 `%AppData%/ToolBox/chats.json` + `sessions/*.json`；记忆 `Data/memory.db`（SQLite）；白名单 `file_whitelist.json`；工具作业与内部投递元数据同应用目录
- 旧版 `Setuna` 目录在 `CacheManager.Init` 时一次性迁移到 `ToolBox`

### 启动流程（App.OnStartup / InitializeDesktop）

1. 安装未处理异常兜底（见「稳定性兜底」），声明进程 DPI 感知
2. 单例守卫（`Global\` Mutex）。已有实例时不静默退出：通过命名管道发送 `app.activate`（可携带命令行参数）让既有实例唤起桌面，失败才提示并退出
3. 初始化待办存储：补齐过期重复待办、注册提醒、启动周期补齐定时器（存储损坏时只记日志，不阻塞启动）
4. 启动命名管道宿主（失败则退出码 5），随后按参数决定是否初始化桌面
5. `--task-host` 分支不初始化窗口、热键、剪贴板监听与截图调度，只提供服务
6. 桌面分支：加载配置 → 注入语言包 → 应用主题 → 启动画面（受 `ShowSplashWindow` 控制）
7. 挂接后台能力：定时截图与日报、定时提醒、定时关机警告、前台窗口活动记录
8. 创建 `MainWindow`（隐藏宿主 + 托盘）、`CompactToolboxWindow`、`WorkbenchWindow`，启动剪贴板监听并下发条目数/忽略应用配置
9. 待办重复提醒事件从 Cron 后台线程编组到 UI 线程后弹 `TodoRepeatToast`

## 功能模块

### 截图系统

- **CaptureWindow** — 全屏覆盖层，鼠标拖拽选择区域
- **ScrapWindow** — 浮动贴图窗口：默认置顶，样式项「取消置顶」写入 `Topmost` 后不再被图层刷新弹回；拖拽移动、双击进入 50×50 缩略查看（此期间写缓存用的是原图，不会把原图冲成小图）、右键菜单（复制/剪切/保存/识别待办/OCR 整图与选字/关闭）
- **CacheManager / CacheItem** — 截图持久化到 `%LocalAppData%/ToolBox/{17位时间戳}/`，`Image.png` + `Info.json` 均先写同目录临时文件再原子替换；坏文件只跳过该条不阻断启动；过期清理只针对 17 位时间戳目录（`Screenshots`/`Logs`/`clipboard_thumbnails`/`ocr` 等同级目录不在删除范围）
- **HistoryView** — 嵌入工作台 `screenshots` 页，4 列缩略图网格 + 视图切换（网格/列表）+ 时间筛选（today/week/month/all）+ 搜索与标签 + 分页（PageSize=16）

### 剪贴板历史

- **ClipboardMonitor** — 加入系统剪贴板链监听文本/图片/文件列表；链消息转发用带超时的 `SendMessageTimeout`，避免被挂起的第三方程序拖死 UI；支持忽略应用列表（密码管理器等）与「暂停监听 5 分钟」
- **ClipboardStore** — `%LocalAppData%/ToolBox/clipboard.json` + `clipboard_thumbnails/`，条目数上限、去重、置顶与收藏
- 图片条目当前仅保留 200×200 JPEG 缩略图，粘贴回填的是缩略图而非原图（已知缺口）
- **入口** — Ctrl+Shift+V 浮窗（搜索/筛选/置顶/收藏/粘贴）与工作台 `clipboard` 页

### 待办管理

- **数据模型扩展** — `TodoItem` 新增字段：
  - `Progress`（int, 0-100, 默认 0）
    - **叶子节点**（无子任务）：手动拖动条调节；`IsCompleted=true` 时自动置 100
    - **父节点**（有子任务）：自动计算，不可手动修改；公式：`round(所有子任务的进度百分比之和 / 子任务数量)`；递归生效（子任务若有自己的子任务，同样按此规则聚合）
  - `StartDate`（DateTime?, 可空, 默认创建当天）
  - `DueDate`（DateTime?, 可空, 即截止日期）
  - `Children`（List<TodoItem>, 子任务列表, 支持多层递归嵌套；父节点进度由子节点聚合，规则见 Progress 字段说明）
  - `RepeatConfig`（RepeatConfig?, 可空, 有值则代表该待办为重复待办；配置频率/提醒时间/结束日期等；空 = 普通待办）
  - `LastResetAt`（DateTime?, 可空, 重复待办最近一次重置时间，用于补齐判断；普通待办为 null）
- **迷你窗口** — 仅显示未完成项（`IsDone=false`），按 `DueDate` 升序，无 `DueDate` 排末尾；checkbox 切换状态 + 快速添加；递归显示子任务层级（子任务 depth×16px 缩进，父任务进度由子任务自动聚合）
- **工作台 todos 页** — 65/35 双栏布局：左侧筛选（全部/待办/已完成 胶囊样式）+ 快速添加 + 列表（checkbox + 优先级点 + 标题 + 标签 + 截止日期）；右侧详情面板使用共享 `TodoDetailPanel` 组件
- **共享 TodoDetailPanel** — 提取为独立 `UserControl`（`Project/Views/Todo/TodoDetailPanel.xaml(.cs)`），统一大窗口（TodoView）和小窗口（TodoDetailWindow）的任务详情展示；包含：可编辑标题、状态徽章、完成复选框、分类输入（带自动补全）、优先级单选按钮、截止日期选择器、进度滑块、重复配置、描述文本框、时间戳、子任务列表、操作按钮
- **外部读写** — 待办的增删改查/状态/回收站由 Agent CLI 经命名管道进入 `TodoStore`（`TodoStore.Commands.cs`）；`TodoTools` 供助手运行时调用（同一存储）
- **同级排序** — "全部"视图中，已完成待办项自动下沉至同级未完成项下方（层级关系不变，仅调整显示顺序）
- **详情面板联动** — 按"重复频率"下拉值联动显隐字段：频率=不重复时显示截止日期与完成进度；频率≠不重复时显示提醒时间与结束日期
- **进度与完成 toggle 交互约束**：
  - 有子任务的待办项：Progress 进度条**禁用**（只读），值由子任务完成情况自动计算；完成 toggle 在 Progress < 100 时**禁用**（不可点击），Progress = 100 时**启用**（用户手动点击标记完成；Progress = 100 不自动触发完成，因为可能后续新增子任务）
  - 无子任务的叶子待办项：Progress 进度条**可用**（手动调节）；完成 toggle **可用**（无约束）；勾选完成时自动将 Progress 置为 100
  - 递归：若子任务自身也有子任务，上述规则对子任务同样成立
  - 子任务增删时，父级 Progress 实时重算并反映在 UI 上

> **已完成**: `Progress` 自动计算与交互约束逻辑已实现（CSH-37），`Children` 通过 `ParentId` 外键 + `TodoStore.GetChildren()` 实现递归聚合。


- **可重复待办（重置模型）** — 同一 TodoItem 按周期重置状态：`TodoItem.RepeatConfig` 定义重复规则（Daily/Weekly/Monthly/Yearly），到提醒时间（Cron 触发）自动将 `IsCompleted` 改回 `false` 并弹通知；完成重复项时行为与普通项一致（只标记完成）；启动时 `CatchUpRepeatingAsync` 遍历已完成且已过重置时间的项，重置为未完成（一次）；迷你窗口按「今日重复 / 普通待办」分组显示；详见 `Docs/recurring-todo-design.md`
- **重复待办提醒弹窗（TodoRepeatToast）** — 系统托盘气球替换为应用风格通知卡片：BgElevatedBrush 卡片 + BorderDefaultBrush 边框 + RadiusMd 圆角 + ShadowMd 阴影 + AccentSoftBrush 圆底 IconRepeat 强调图标 + X 关闭按钮；右下角堆叠定位（间距 8px）、10 秒自动关闭、关闭后其余弹窗补位；与 ReminderToast 范式一致，全部走设计令牌，Light/Dark 双主题自适应；位于 `Project/Views/Todo/TodoRepeatToast.xaml(.cs)`
### 设置

- 通过迷你窗口按钮或托盘菜单打开 WorkbenchWindow，导航至 `settings` 页
- 设置视图 `SettingsView` 嵌入工作台主内容区：**页内左侧导航 + 每次只显示一个 Section**（`Section*` 面板默认 Collapsed，按对应 `Nav*` 选中项切换），当前共 14 节

| Section | 内容 |
|---------|------|
| 通用 | 应用模式、开机自启、主题（Light/Dark/System 三张 RadioCard）、小窗透明度与靠边自动隐藏、贴图不活动/悬停透明度、截图保留天数 |
| 快捷键 | 启用全局热键、截图/显示隐藏热键（另有 6 个固定 Ctrl+Shift+字母 热键不可配置） |
| 截图 | 选区透明度、边框线型与颜色（**当前无消费者**，见「未接线模块」） |
| 剪贴板 | 条目数上限、忽略应用 |
| OCR | 引擎选择（WindowsOCR / Tesseract）、识别语言 |
| 模型（Provider） | 供应商选择/模型发现、API Key/Base URL、测试连接、自定义供应商管理；供助手、日报与识别待办使用 |
| AI 助手 | 启用开关、会话数据路径、文件访问白名单增删、隐私说明、清空全部会话与记忆（默认关闭） |
| 动作链 | 链列表、设为默认、删除、新建（节点名取 `NodeFactory.GetAvailableNodeTypes()`） |
| 定时任务 | 定时全屏截图（Cron 表达式）、每日行为总结（时间设置） |
| 提醒管理 | 定时提醒列表、启停、删除（托盘「提醒管理」直达此节） |
| 定时关机 | 时刻/倒数关机与取消 |
| 分类管理 | 待办分类的增删 |
| 语言 | 中文/English（保存后即时生效，无需重启） |
| 关于 | 版本信息 |

> 「测试连接」调用 Provider 的 `CheckConnectionAsync`（真实 HTTP 往返），读的是已保存的 Provider 配置——改完 Key/Base URL 要先保存再测。
> 贴图不活动/悬停透明度的即时预览只在**保存后**生效：设置页改的是自己那份配置副本，`ScrapBook` 读的是 MainWindow 那份。

### 截图定时与日报

设置界面提供「⏰ 定时截图与日报」Section，支持：

- **定时全屏截图** — 开关 + Cron 表达式配置。默认 `0 */30 * * * *`（含秒的 6 段写法）
- **每日行为总结** — 开关 + 时间设置（如 `18:00`）

后端组件：

- **CronExpressionr**（文件 `CronScheduler.cs`，文件名与类名不一致）— 基于 Cronos 的定时触发。同时接受 5 段（标准）与 6 段（含秒）表达式，表达式非法时跳过注册而不抛异常——启动路径会调用它，抛出会让应用再也起不来；定时器延时向上取整到整毫秒，保证不早于计划时刻触发（提前触发会让按"下次发生时刻"续订的调度取到同一时刻，产生连环重复触发）；睡眠唤醒后按 `PowerModes.Resume` 重挂；一次性任务触发即注销条目，不再被唤醒重放
- **ScheduleManager** — 由 `App.InitializeDesktop` 与设置页保存两处调用刷新；日报时间被换算成固定 UTC 小时的 5 段 Cron
- **ScreenshotTracker** — 每 500ms 轮询前台窗口，按窗口类名变化记录「应用 + 窗口标题 + 时长」到 `%LocalAppData%/ToolBox/activity/*.jsonl`，是日报的真实数据源（**不是**截图）
- **ReportGenerator** — 日报/周报 Markdown 生成，产物写入 `%LocalAppData%/ToolBox/reports/daily/*.md`

> 现状缺口：`Screenshots/`（定时全屏截图产物）与 `reports/`（日报产物）在工作台里都没有浏览入口；周报无任何调用方；活动记录 jsonl 不随 `ScreenshotMaxAge` 轮转；窗口标题含文档名/邮件主题等敏感信息，日报走 LLM 时属数据外发。

## UI 设计令牌

定义在 `Themes/DesignTokens.xaml`（共享）+ `Themes/Light.xaml` / `Themes/Dark.xaml`（双主题）。

**共享令牌（DesignTokens.xaml）**：圆角（Xs/Sm/Md/Lg/Xl/Full）、间距（1-8）、字体名（Geist / Geist Mono）、共享 Style（`CardStyle` / `FilterTabStyle` / `DateFilterStyle` / `PriOptionStyle` / `IconButton` / `PrimaryButton` / `OutlineButton` 等）。

**主题色（在 Light.xaml / Dark.xaml 各自定义，运行时通过 `DynamicResource` 切换）**：

| Token | Light | Dark | 用途 |
|-------|-------|------|------|
| `BgPrimaryBrush` | `#FAFAFA` | `#0c0d10` | 主背景 |
| `BgElevatedBrush` | `#FFFFFF` | `#141519` | 卡片/面板/侧边栏 |
| `BgSunkenBrush` | `#F0F0F0` | `#08090c` | 凹陷区/输入框背景 |
| `TextPrimaryBrush` | `#1A1A1A` | `#e8ecf4` | 主文本 |
| `TextSecondaryBrush` | `#666666` | `#8b91a0` | 辅助文本 |
| `TextTertiaryBrush` | `#999999` | `#555b6e` | 弱文本 |
| `AccentBrush` | `#d63031` | `#e84343` | 强调色（红） |
| `AccentHoverBrush` | `#c0392b` | `#ff5c5c` | 强调色悬停 |
| `AccentSoftBrush` | `rgba(214,48,49,0.1)` | `rgba(232,67,67,0.1)` | 强调色弱底 |
| `BorderDefaultBrush` | `#E8E8E8` | `#222430` | 默认边框 |
| `BorderStrongBrush` | `#CCCCCC` | `#2d2f3a` | 强边框/hover |
| `SuccessBrush` | `#00b894` | `#00d2a0` | 成功状态 |
| `ErrorBrush` | `#ff5252` | `#ff5252` | 错误状态 |

## 主题系统

由 `Core/Theming/ThemeManager.cs` 单例驱动：

- **三态模式** — `EThemeMode` 枚举：`Light` / `Dark` / `System`（System 跟随 `SystemParameters` 或 `SystemEvents.UserPreferenceChanged`）
- **持久化** — 保存到 `ToolBoxOption.Data.Theme`，启动时 `App.OnStartup` 调用 `ThemeManager.Instance.Initialize(options.Data.Theme)`
- **运行时切换** — `ThemeManager` 替换 `Application.Resources.MergedDictionaries` 中的 `Light.xaml` / `Dark.xaml` 字典，所有控件通过 `DynamicResource` 引用主题色，无需重新创建窗口
- **系统模式监听** — 订阅 `SystemEvents.UserPreferenceChanged`，系统主题变化时自动应用对应字典

## 嵌入字体

为统一视觉并避免依赖系统字体安装，字体文件嵌入程序集：

| 字体 | 用途 | 文件位置 |
|------|------|---------|
| Geist | 全局 UI 文本（默认 sans-serif） | `Themes/Fonts/Geist-Regular.ttf` 等 |
| Geist Mono | 等宽场景（时间戳、版本号、状态文本） | `Themes/Fonts/GeistMono-Regular.ttf` 等 |

引用方式：`pack://application:,,,/Themes/Fonts/#Geist`，由 `DesignTokens.xaml` 中的 `FontFamily` 资源统一暴露（`SansFontFamily` / `MonoFontFamily`），各控件通过 `DynamicResource` 引用。

## 配置项（ToolBoxOption）

| 分类 | 配置项 | 默认值 |
|------|--------|--------|
| 启动 | `AppType` | ApplicationMode |
| 启动 | `ShowMainWindow` | true |
| 截图 | `SelectAreaTransparent` | 80 |
| 截图 | `SelectLineSolid` | false |
| 贴图 | `InactiveAlphaValue` | 10 |
| 贴图 | `MouseOverAlphaValue` | 90 |
| 回收箱 | `DustBoxEnable` | true |
| 回收箱 | `DustBoxCapacity` | 5 |
| 快捷键 | `CaptureHotKey` | Ctrl+1 |
| 快捷键 | `HideShowHotKey` | Ctrl+2 |
| 快捷键 | `HotKeyEnable` | true |
| 语言 | Language | zh-CN |
| 主题 | `ToolBoxOption.Data.Theme` | System（Light/Dark/System） |
| 迷你窗口 | CompactOpacity | 50 |
| 定时截图 | AutoScreenshotEnabled | false |
| 定时截图 | AutoScreenshotCron | `0 */30 * * * *`（6 段含秒） |
| 每日总结 | DailyReportEnabled | false |
| 每日总结 | DailyReportTime | 18:00 |
| 助手 | `AssistantEnabled` | false |


## 稳定性兜底

- **未处理异常**：`App.InstallCrashGuards` 订阅 `Dispatcher.UnhandledException`、`AppDomain.UnhandledException`、`TaskScheduler.UnobservedTaskException`。UI 线程异常记日志后标记已处理，不再让单次回调出错带走整个进程（贴图与未落盘的待办会随之消失）；提示复用 `TodoRepeatToast`，10 秒限频避免连环异常刷屏。日志：`%LocalAppData%/ToolBox/Logs/crash.log`
- **写盘**：配置/待办/提醒/动作链/缓存图片与信息文件统一「同目录临时文件 → 原子替换」，失败保留原文件；读取失败时待办与提醒拒绝回写，防止用空集合覆盖用户数据
- **窗口生命周期**：工作台与迷你窗的关闭请求转为隐藏（实例由 App 缓存，真关闭后 `Show()` 会抛）；退出统一收敛到 `MainWindow.Shutdown()`
- **定时关机**：目标时刻已过（如睡眠跨过）一律按「错过」取消而不补关机，状态回 Idle 并写 `%LocalAppData%/ToolBox/Logs/shutdown.log`；应用退出时取消计划
- **剪贴板链**：消息转发带 200ms 超时；退出时摘链
- **网络诊断**：系统代理只报 Warning 并给引导，不自动修改（PRD R3/AC-05）；提权修复单次 UAC 打包执行

## 未接线模块（按设计文档待复活）

以下模块代码存在但当前没有任何调用方/导航入口，删除会同时丢掉已写好的设计实现，故保留并按设计文档补接线：

| 模块 | 现状 | 设计出处 |
|------|------|---------|
| `Core/Tools/*` 中 Screenshot/ActionChain/Ocr/Clipboard/Color 等工具 | 助手恢复后注册入口已接回（`AssistantHost` 仅注册 File/Image/Todo/WebSearch/Board 五类），其余工具仍无调用点 | 助手设计 + `04-Action-Chain.md` |
| `Core/StepCapture` + `Views/StepCapture` | 鼠标钩子从未安装（`_hookId` 恒 0），预览窗两个按钮是「功能待实现」，且缺 `.xaml` | `10-Quick-Designs.md` P2-04 步骤捕获 |
| `Core/SmartRedaction` | `ApplyBlur` 原样克隆画回（无模糊）、`DetectSensitiveRegions` 恒返回零矩形 → 整条链路 no-op | P2-05 智能打码 |
| `Core/QrCode` / `Core/CloudShare` | 两个 stub 方法恒返回 null | P3-02 QR 码识别、云分享 |
| `Core/Markdown/MarkdownDocument` | 唯一消费者是被删的 `MessageBubble` | `08-Code-Snippets.md` |
| `Core/Tags/ColorExtractor` | 标签存储未调用它 | `07-Screenshot-Tags.md` |
| `Views/Pomodoro` + `Core/Pomodoro` | 视图存在但未挂侧栏/工作台/迷你窗任一处；`AssociatedTodoId` 恒 null（番茄-待办关联未实现） | P2-06 番茄钟 |
| `Views/Habits` + `Core/Habits` | 同上，无导航入口 | `10-Quick-Designs.md` 习惯打卡 |
| `Views/Todo/CalendarView` | `GetTodoCount` 返回 `date.Day % 3` 的 mock 数据，且无入口 | P2-01 日历视图 |
| `Core/Window/WindowsFilter` | 无调用方，且双字段过滤被 `??` 降级成只按标题匹配 | — |
| `Core/ScreenFreeze/ScreenFreezeService` | 主窗口自带一份截屏实现，该服务无引用 | P3-01 屏幕冻结模式 |
| 配置项 `SelectAreaTransparent`/`SelectLineSolid`/`SelectLineColor*`/`SelectBackColor*`/`DustBox*`/`DupType`/`ChatFontSize`/预览卡三项 | 设置页可写，代码里无消费者（选区遮罩与线型目前硬编码） | — |

## 数据存储路径

| 数据 | 路径 |
|------|------|
| 全局配置 | `%AppData%/ToolBox/config.xml` |
| 待办列表 | `%AppData%/ToolBox/todos.json` |
| 定时提醒 | `%AppData%/ToolBox/reminders.json` |
| 动作链 | `%AppData%/ToolBox/action_chains.json` |
| 剪贴板条目 | `%AppData%/ToolBox/clipboard.json`（缩略图在 `%LocalAppData%/ToolBox/clipboard_thumbnails/`） |
| 取色历史 | `%AppData%/ToolBox/color_history.json` |
| 截图标签 | `%AppData%/ToolBox/screenshot_tags.json` |
| 供应商配置 | `%AppData%/ToolBox/providers.json`（API Key 明文，待改 DPAPI） |
| 活跃模型 | `%AppData%/ToolBox/active_model.json` |
| 看板宿主适配器配置 | `%AppData%/ToolBox/board-agents.json`（本机，不入项目目录） |
| 看板投递状态 | `%AppData%/ToolBox/board-deliveries.json`（本机） |
| 助手会话 | `%AppData%/ToolBox/chats.json` + `sessions/*.json` |
| 助手记忆 | `%AppData%/ToolBox/Data/memory.db`（SQLite） |
| 助手文件白名单 | `%AppData%/ToolBox/file_whitelist.json` |
| 活动记录（日报数据源） | `%AppData%/ToolBox/Data/activity/{yyyy-MM-dd}.jsonl` |
| 日报/周报 | `%AppData%/ToolBox/Data/reports/{daily,weekly}/*.md` |
| 截图缓存（贴图恢复源） | `%LocalAppData%/ToolBox/{17位时间戳}/` |
| 定时全屏截图 | `%LocalAppData%/ToolBox/Screenshots/` |
| OCR 语言包 | `%LocalAppData%/ToolBox/ocr/tessdata/` |
| 日志 | `%LocalAppData%/ToolBox/Logs/{crash,shutdown}.log` |

> 旧版 `%AppData%/Setuna`、`%LocalAppData%/Setuna` 在 `CacheManager.Init` 时一次性迁移到 `ToolBox`，历史文档中的 Setuna 路径不再有效。
> 助手恢复后，`chats.json`、`sessions/`、`Data/memory.db`、`file_whitelist.json` 重新成为活动数据（见「AI 助手与 Agent 运行时」）。

## 项目规则

详见根目录 `AGENTS.md`：

- `refs/` — 参考项目（禁止修改）
- `Project/` — 工程主体
- `Docs/` — 设计文档和计划文件
- 命名规范：字段/属性用小驼峰，类/结构体/方法用大驼峰，接口 `I` 前缀，枚举 `E` 前缀
- 不允许 `void` 方法，返回自身实现链式调用
- 不使用下划线前缀（`_`、`m_`、`k_`、`s_`）
- 本文件（`design.md`）仅在设计变更或功能更新时更新，不记录更新日志



## 核心动效与迷你窗口停靠

迷你窗口以 Visible、HidePending、Aligning、Hiding、Hidden、Showing 六个状态协调回靠、收起与展开；DockSide 记录上下左右停靠侧。拖动结束后静置约 800ms，窗口部分越界或处于工作区内 12 DIP 边缘阈值时，先将对应边缘对齐工作区，再隐藏；角落越界按更深的物理像素距离选择边缘，同深度优先左右，另一轴也约束到工作区。内部显示器接缝不收起。Win32 工作区与窗口矩形统一用物理像素比较，位置差值经当前 DPI 转为 WPF 坐标。

回靠完成后直接衔接隐藏；指针仍在窗口内时仅回靠，离开后再等待约 800ms。握柄悬停约 120ms 或点击可展开；收起/展开采用约 180/220ms 的可逆动画。每次转换清除 Left/Top 动画，并以转换序号阻止过时完成回调。隐藏终点保留 12 DIP 命中区，左右用竖握柄、上下用横握柄；移动期间按工作区裁剪内容，避开任务栏。菜单、输入、详情和拖动期间抑制自动回靠与收起。显示环境与窗口可见性变化时恢复可召回位置；折叠与停靠动画不同时持有 Top。

工作台页面与迷你标签只对切换后的内容做短淡入及小幅位移；折叠改变窗口高度并保持底边，隐藏期间的数据更新在唤起后补算高度。主按钮使用主题色状态层反馈悬停和按下。所有动效遵循 Windows 客户区动画开关，不改变业务操作时序，不引入第三方动效依赖。细节见 motion-edge-hide-design.md。


## 截图预览卡尺寸

PreviewCardWindow 使用 WPF SizeToContent 按缩略图与按钮行测量窗口，不固定外框宽高。缩略图在 182×108 DIP 内以 Uniform 保持整图比例，棋盘容器包住图像；卡片最小宽度 134 DIP，保证按钮行可用。完成布局后以 ActualWidth/ActualHeight 定位工作区右下角，底边贴工作区，右侧保留 16 DIP；缩放中心使用相对中心。接口与截图数据保持不变，先设置图片再显示窗口。详见 design/features/09-Preview-Card.md。


## 项目文件夹看板

工作台 `BoardsView` 管理已登记项目的列、卡片、排序与回收站；卡片详情由 `BoardCardDetailsWindow` 承载，看板内全部确认/提示弹窗走主题化 `ProjectBoardDialogs.Confirm/Notify`，不再使用系统 MessageBox。看板数据存入项目 `.toolbox/board.json`；本机 `project-boards.json` 保存目录登记和个人待办关联。boardId、columnId、cardId 使用稳定 UUID，revision 与文件哈希检测过期写入及外部修改。

WPF 与 `project`/`board` CLI 经同一 `ProjectBoardStore` 处理；命名管道沿用唯一后台宿主。Store 串行读改写、校验路径边界与数据格式，采用同目录临时文件原子替换，通过 Changed 通知界面刷新。加入个人待办复制快照，完成状态独立；pending 关联阻止失败后重复创建。首版结构与接口见 project-board-design.md。

### v2 协作模型（已落地）

完整需求、候选调研、适配器配置与冻结契约见 project-board-v2-design.md（R1–R8 为范围，冲突以该文件 §13 为准），本节只记录当前设计要点，不重复该文件。

- **存储与模型**：`board.json` `schemaVersion=2`；卡片新增 `status / parentId / assignee / claim / attachments / comments` 字段。状态为 `notStarted / inProgress / pendingReview / closed` 四态，只经 claim/release/submit/close/reopen 修改；列可带可选 `status` 映射，通用 update/move 不能直接写状态或清 claim。层级用扁平 `parentId` 迭代遍历（任意深度，禁自引用与环）；关闭父项须全部活动后代已关闭，不级联关闭；删除/恢复按子树处理，恢复需祖先未删除。
- **原子认领与租约**：`claim` 在存储锁内一次校验 revision、文件哈希、状态、所有者与到期时间后写入；同身份重复调用为续租心跳，他人有效租约返回 `CONFLICT/CLAIMED_BY_OTHER`（结构化带 owner 与 expiresAt）。租约默认 30 分钟、允许 5–480；过期不自动改状态，只允许重新认领，`--available` 把过期租约视为可认领。
- **评论与双 revision**：评论随卡片内嵌 `board.json`；所有读响应给出 `boardRevision` 与 `commentRevision`，评论写额外接受 `commentRevision` CAS。回复的 `routing` 是创建时最近祖先 `origin` 的快照，`bind` 只影响之后创建的回复；`ack` 是已处理事实，自动投递成功不等于已确认。
- **评论自动外置与迁移 journal**：候选序列化严格大于 2 MiB（2,097,152 字节）或单卡评论严格超过 500 条时，触发写入即把评论外置到 `.toolbox/comments.json`（`commentsExternal=true`，独立 revision）。迁移用一次性 journal + 旧 board 备份 + 新文件 hash：两新 hash 齐备则提交，否则回滚旧 board 并只清除 hash 匹配的临时文件，未知 hash 拒绝写入并保留证据。之后评论在 sidecar 上单独原子更新；IPC 仍限 4 MiB。
- **图片附件**：元数据内嵌卡片 `attachments`，图片本体存项目内 `.toolbox/board-assets/<cardId>/<attachmentId>.<ext>`。允许 `png/jpg/jpeg/gif/bmp`、单张 ≤ 10 MiB；按 SHA256 去重（同卡片同哈希直接返回已有项）；先写临时文件再原子改名，路径拒绝重解析点与越界。删除先写限定 `attachments.journal.json`（tombstone + 文件当前 hash）再删元数据；读取时按 journal 恢复——删除已完成则清理、未完成则按 hash 还原、未知或不符 hash 的条目不自动清除。卡片软删除不删除图片，缺失/篡改只返回 `exists`/`missing` 标记。
- **宿主适配器与投递**：回复投递由共享 `BoardAgentService` 路由（UI 与 IPC 同一入口）；适配器配置存本机 `%AppData%/ToolBox/board-agents.json`，投递状态（`needsConfirmation / queued / sending / submitted / acknowledged / failed / unknown / manualPullRequired` + 错误摘要）存 `%AppData%/ToolBox/board-deliveries.json`，两者都不写入项目目录、不入 Git。宿主发现按本机配置 → PATH/npm → URL 协议与卸载注册表 → 版本目录 → 常见目录兜底，失败进入 `mode=none` 并允许手工指定；原生 exe 与 Node 入口用参数数组传递、消息优先走 stdin，不拼接 shell、不加权限绕过参数、不把 cmd/ps1 shim 当 exe。无 CLI 的 GUI 宿主走宿主内拉取（技能首步 `comment inbox` 后 `ack`）或按 URI 协议/可执行路径手工唤起（`ResolveLaunch`），未确认前标 `manualPullRequired`。
- **CLI**：`board.*` 共 32 项能力（`capabilities list` 合计 54 项，含助手 `tools.*` 5 项），其中状态 5 项（claim/release/submit/close/reopen）、评论 10 项（add/get/list/update/delete/inbox/ack/bind/deliver/delivery）、附件 3 项（add/list/remove）；列/卡片既有命令扩展 `--actor/--host/--session/--parent-id/--clear-parent-id/--status/--available/--mine/--ttl-minutes/--transition/--comment-revision` 等字段。完整参数以 `--help` 与 `capabilities get` 为准。
- **技能**：新增 `Docs/skills/toolbox-board/SKILL.md`（随 `ToolBox.Cli.csproj` 链接复制到发布包 `skills/toolbox-board/SKILL.md`）承载看板协作流程；`cli-anything-toolbox` 的看板章节收敛为指向该技能的指针。CLI/host 版本为 2.1.0。
- **v1→v2 兼容**：v1 读取时在内存迁移（不落盘，下次成功写入保存 v2）；旧版“进行中”缺执行身份时置 `notStarted` 并返回迁移提示。旧版 ToolBox 读到 v2 按未知 schema 拒绝并保留原文件（`DATA_CORRUPT`），降级前需备份。

## AI 助手与 Agent 运行时

工作台 `assistant` 页承载会话式助手，唯一 WPF 入口是 `Views/Chat/ChatView.cs`：内嵌 WebView2（新增 `Microsoft.Web.WebView2` 依赖），以虚拟 origin `https://toolbox.assistant.local` 加载随应用发布的 `AssistantWeb/dist/index.html`，首次进入页时初始化；运行时缺失时显示安装说明，其它功能不受影响。前端在 `Project/AssistantWeb`，由 Vite 把 assistant-ui React 应用构建成纯静态资源（运行期不依赖 Node、前端服务器或 CDN），`Views/Chat/AssistantWebProtocol.cs` 定义桥接协议。

桥接契约：消息 envelope 为 `{version,id,type,sessionId?,payload}`；Web→C# 含 ready、会话 list/create/select/rename/delete、message.send、run.cancel、sessions.clear、link.open；C#→Web 回 snapshot 与 run/text/tool/status 事件、error、theme/locale。命令响应回带原 id；拒绝未知 type、额外权限、错误 schema、超长消息与错误来源。仅允许固定虚拟 origin，阻止任意导航、弹窗与下载；link.open 只由真实用户点击、经 C# 校验后打开 http/https。C# 是消息、会话、工具权限、供应商凭据与存储的唯一权威，React 仅用 external-store runtime 展示快照与增量事件，不启用云端历史或前端模型调用。

运行时（`Core/Llm/`）：`AssistantRuntime.Instance` 懒加载，管理开关、会话运行队列、取消、generation 守卫与工具目录。每次发送捕获不可变 sessionId/runId/generation/provider，迟到事件不得写入别的会话或已清空 generation；同一会话同一时间最多一个轮次，不同会话相互独立。`ChatSession`、`Agent`、`ContextCompressor`、`ISystemPromptBuilder/DefaultSystemPromptBuilder` 承载会话持久化、单轮 Agent 循环、上下文压缩与系统提示词；记忆由 `Core/Memory/MemoryStore.cs`（SQLite）承担。

工具与权限边界：宿主 `Core/Assistant/AssistantHost.cs` 是静态入口（`Initialize/SetEnabledAsync/IsEnabled/EnabledChanged/Tools`，`DataDirectory` 在 DEBUG 下可由 `TOOLBOX_TASK_TEST_ROOT` 重定向），其 `Tools` 只注册 `Core/Tools/{FileTools,ImageTools,TodoTools,WebSearchTools,BoardTools}.cs` 五类，不自动注册 Clipboard/Screenshot/ActionChain 等范围外工具。所有文件读写检查同一 `Core/Security/FileAccessWhitelist.cs`：目录匹配取自身或分隔符后代，拒绝设备/UNC/网络路径与越过白名单的 reparse point。`ImageTools.cs` 的 `describe_image` 走「白名单 → 大小 → 真实格式/MIME → 解码/像素校验」，≤10 MiB，空白名单即拒绝；Provider 按真实 MIME 发送图片，`ProviderManager` 为 `gpt-4o` 置 `SupportsMultimodal=true`，不支持图片的模型返回可操作错误。`BoardTools.cs` 暴露 `board_list_cards/list_comments/claim_card/reply_comment/ack_comment`，执行身份由运行时注入（模型/CLI 参数不能覆盖 actor/host，host 固定 `internal`），看板写入经 `BoardCommandService` 携带 revision 与完整 claim。

CLI 工具作业：`Core/Assistant/ToolsCommandService.cs` 提供 `tools.list/get/call/result/cancel`。list/get 同步返回、关闭助手时返回 `ASSISTANT_DISABLED`；call 快速返回 `{callId,status:"queued"}`，再经 result 取终态（queued/running/completed/failed/canceled），result/cancel 在关闭后仍可查询或取消已有作业但不启动新调用；单帧 4 MiB，requestId+请求指纹保证同进程重复请求不重复执行，重启把在途作业标 `unknown` 不重放有副作用操作。`Core/Todo/TaskProtocol.cs` 识别 `tools.*` 并校验 `tools.call` 参数，`App.xaml.cs` 把 `tools.*` 分流到 `AssistantHost.Tools`。

内部看板宿主：`internal` 作为第 9 个投递宿主（`BoardAgentService` 注入 `InternalEnabled/InternalExecutor`），不创建外部进程；`Core/BoardAgents/InternalBoardAgent.cs` 负责门禁、同会话串行、租约截止、每次写入前校验 claim 与回帖 ack 崩溃恢复。内部会话持久保存一个独立 GUID actor（host=`internal`、session=会话 id），不继承启动进程的外部 actor。`Core/Boards/ProjectBoardComments.cs` 的 `CommentAdd` 增加 internal 有效 claim 门禁，其余宿主语义不变。

开关与持久化：`Models/SetunaOption.cs` 的 `AssistantEnabled` 默认 false（`DeepCopy` 同步）；侧栏导航项默认折叠，工作台对禁用页回落，设置页提供开关、会话路径、白名单增删、隐私说明与「清空全部会话与记忆」。数据落在 `%AppData%/ToolBox/`：会话 `chats.json` 与 `sessions/*.json`、记忆 `Data/memory.db`、白名单 `file_whitelist.json`、工具作业与内部投递元数据同目录；清空流程先关闭新轮次、递增 generation、取消并等待在途轮次与工具，再串行删除。系统提示词、文件白名单、图片与搜索外发、会话/记忆持久化边界均由用户在 2026-10-16 确认，详见 `恢复LLM助手与Agent运行时.md`。
