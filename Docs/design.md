# ToolBox 设计文档

## 项目概述

ToolBox 是一款 Windows 桌面截图+贴图工具，基于 .NET 8.0 WPF 构建。在传统截图贴图功能基础上，集成了待办管理、截图历史、定时截图与日报生成、OCR 文本识别与翻译等能力。

## 技术栈

| 项 | 值 |
|---|---|
| 框架 | .NET 8.0 WPF |
| 语言 | C# |
| 目标 | `net8.0-windows` |
| RootNamespace | `ToolBox` |
| AssemblyName | `ToolBox` |
| 版本 | 2.0.0 |
| NuGet 依赖 | `Cronos 0.8.4`、`Microsoft.Data.Sqlite 8.0.0`、`System.Drawing.Common 8.0.0`、`MdXaml`（Markdown 渲染） |

## 架构

### Agent CLI 与本地后台

`Project/Cli/` 是同一 solution 中的 console 工程，以 System.CommandLine 2.0.2（MIT）解析命令；构建和发布时与 ToolBox.exe、运行依赖及许可文件一起交付。共享协议源文件位于 `Core/Todo/`，不另建 DTO 类库。Agent 技能源集中存放于 `Docs/skills/cli-anything-toolbox/SKILL.md`，CLI 工程以链接文件随构建/发布复制到 `skills/cli-anything-toolbox/SKILL.md`；技能指导调用现有命令，不改变业务接口或权限。

CLI 提供能力发现、待办 CRUD/状态/回收站、指定图片离线 OCR、截图标签只读查询。能力发现读取静态允许列表及 schema；其余请求经本机命名管道交给唯一 ToolBox 实例。后台启动参数 `--task-host` 不初始化窗口、热键、剪贴板监听或截图调度；普通启动向后台转发 `app.activate`，在同一实例初始化桌面。已打开界面时拒绝 `host stop`。

管道名包含当前用户 SID；服务端 ACL 只允许当前用户并拒绝网络登录，客户端使用 CurrentUserOnly，仅连接本机。请求和响应为带版本/requestId 的 UTF-8 单行 JSON，单帧 JSON 上限 4 MiB，连接启动上限 10 秒、响应等待 30 秒；已发送请求超时或断连不重放。host 串行处理请求并派发 Dispatcher，待办命令在 Store 的 stateLock 内完成验证、快照和变更；保存采用同目录临时文件 flush+原子替换，失败恢复原对象状态，损坏数据拒绝命令。

OCR 复用 Tesseract/WindowsOCR，仅读取本次显式指定的本地图片，拒绝网络路径/重解析点，限制文件与像素大小，禁止自动下载、翻译和历史写入。截图查询复用标签 Store，不启动缓存迁移/清理，公开 17 位截图标识而非绝对目录，按标识稳定分页。输入/输出、授权范围详见 `agent-cli-design.md`，待办契约见 `task-cli-design.md`，调用示例见 `task-cli-usage.md`。

### 目录结构

```
Project/
├── App.xaml / App.xaml.cs             # 应用入口，单例守卫，语言加载
├── GlobalUsings.cs                     # 全局 using
├── Core/
│   ├── HotkeyManager.cs               # 全局热键注册与分发
│   ├── Llm/                           # LLM 请求核心（保留供 OCR/待办识别等场景调用）
│   │   ├── ChatMessage.cs             # 消息模型（含 ToolCalls）
│   │   └── ChatChunk.cs               # 流式输出片段
│   ├── Providers/                     # LLM 供应商适配
│   │   ├── ILlmProvider.cs            # 流式聊天接口
│   │   ├── IProvider.cs               # 供应商元数据接口
│   │   ├── ModelInfo.cs               # 模型信息
│   │   ├── ProviderManager.cs         # 配置式供应商管理 + 活跃模型 + 自定义供应商持久化
│   │   ├── OpenAiProvider.cs          # OpenAI 兼容 API
│   │   ├── OllamaProvider.cs          # Ollama 本地模型
│   │   └── AnthropicProvider.cs       # Anthropic Claude
│   ├── Translation/                   # 非 LLM 翻译
│   │   └── TranslationService.cs      # 免费公开在线翻译接口（不依赖 LLM Provider）
│   ├── Tools/                         # 工具系统（面向 LLM 调用的静态能力封装）
│   │   ├── ToolAttribute.cs           # [Tool("name", "desc")] 标记静态方法
│   │   ├── ToolParamAttribute.cs      # [ToolParam("desc")] 标记参数
│   │   ├── ToolInfo.cs                # 工具元数据
│   │   ├── ToolRegistry.cs            # 反射扫描 + 执行器
│   │   ├── FileTools.cs               # 文件 CRUD
│   │   ├── TodoTools.cs               # 待办 CRUD（LLM 可调用）
│   │   └── WebSearchTools.cs          # 网络搜索（DuckDuckGo API）
│   ├── Todo/
│   │   ├── TodoItem.cs                # 待办数据模型（Id/Title/Description/IsCompleted/Priority/Tags/DueDate）
│   │   └── TodoStore.cs               # 待办持久化 + ItemsChanged 事件
│   ├── Memory/
│   │   └── MemoryStore.cs             # SQLite 记忆存储
│   ├── Security/
│   │   ├── FileAccessWhitelist.cs     # 文件访问白名单
│   │   └── ConfirmDialog.cs           # 危险操作确认弹窗
│   ├── Scheduling/
│   │   ├── CronScheduler.cs           # Cron 定时器
│   │   ├── ScheduleManager.cs       # 定时任务注册/注销（自动截图 + 每日总结）
│   │   ├── ScreenshotTracker.cs       # 定时全屏截图
│   │   └── ReportGenerator.cs         # 日报/周报生成
│   ├── Native/
│   │   ├── NativeMethods.cs           # P/Invoke
│   │   └── WpfTrayIcon.cs             # 系统托盘图标
│   ├── Theming/
│   │   └── ThemeManager.cs            # 主题管理器（Light/Dark/System 三态 + 持久化）
│   └── Window/
│       ├── WindowInfo.cs
│       ├── WindowManager.cs
│       └── WindowsFilter.cs
├── Models/
│   ├── ToolBoxOption.cs               # 全局配置（XML 序列化 + 代理属性）
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
│   ├── MainWindow.cs                  # 主窗口（隐藏宿主，管理托盘和子窗口）
│   ├── CompactToolboxWindow.xaml/.cs  # 迷你模式（Topmost，260px，右下角常驻，自动隐藏）
│   ├── WorkbenchWindow.xaml/.cs       # 全屏工作台（左侧深色侧边栏 + 5 页面导航）
│   ├── SplashWindow.xaml/.cs          # 启动画面（Loading/Error/Complete 三态）
│   ├── Capture/CaptureWindow.cs       # 截图选区
│   ├── Dashboard/
│   │   └── DashboardView.xaml/.cs     # 仪表盘视图（统计卡片 + 快捷入口）
│   ├── Todo/
│   │   ├── TodoView.xaml/.cs          # 待办视图（嵌入工作台 todos 页，65/35 双栏）
│   │   ├── TodoDetailPanel.xaml/.cs   # 共享任务详情面板（大窗口+小窗口复用）
│   │   ├── TodoDetailWindow.xaml/.cs  # 小窗口任务详情弹窗（内嵌 TodoDetailPanel）
│   │   ├── TodoRepeatToast.xaml/.cs   # 重复待办提醒弹窗（应用风格通知卡片）
│   ├── SettingsView.xaml/.cs          # 设置视图（嵌入工作台 settings 页）
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
- 260×240px，右下角常驻，圆角 14
- 显示：未完成待办（按 `DueDate` 升序，无截止日期排末尾，可直接切换状态）+ LLM 会话状态摘要
- 底部按钮跳转：💬聊天 / ✅待办 / 📸截图 / ⚙设置
- 右上角 `☰` 三横按钮打开工作台 settings 页
- **自动隐藏**：鼠标离开窗口区域后自动淡出隐藏（可通过设置页开关控制），鼠标移入或全局热键触发时恢复显示；支持多显示器边缘检测（`MonitorFromWindow` P/Invoke），吸附屏幕边缘时自动隐藏到边缘仅露出触发条

**全屏工作台（WorkbenchWindow）**：
- 960×640px，普通窗口
- **布局**：左侧 200px 深色侧边栏（Logo + 5 个 RadioButton 导航项 + 底部用户区）+ 右侧主内容区（TopHeader + PageHost）
- **5 个页面**（取代旧版 3-Tab）：
  - `dashboard` — DashboardView：统计卡片（待办/会话/截图/主题）+ 4 个快捷入口
  - `assistant` — ChatView：聊天视图（240px 会话侧栏 + 消息区 + 输入区）
  - `todos` — TodoView：65/35 双栏（左侧筛选+列表，右侧详情面板）
  - `screenshots` — HistoryView：4 列网格 + 视图切换 + 时间筛选（today/week/month/all）
  - `settings` — SettingsView：平铺式 Section 布局
- DashboardView 通过 `NavigationRequested` 事件让 Workbench 订阅跳转
- 从托盘菜单或迷你窗口底部按钮打开

### 窗口层级

```
Layer 4: 启动画面 SplashWindow (Topmost, 启动期短暂显示)
Layer 3: 浮动截图 ScrapWindow (Topmost, Z-index最高)
Layer 2: 迷你窗口 CompactToolboxWindow (Topmost)
Layer 1: 全屏工作台 WorkbenchWindow (普通窗口)
Layer 0: 全屏截图 CaptureWindow (覆盖层)
```

### 托盘菜单（精简版）

```
📌 参考图列表（子菜单）
────────
📸 截图
📋 粘贴
────────
🏠 工作台     → 打开 WorkbenchWindow（默认 dashboard 页）
💬 AI 助手     → 打开工作台→assistant 页
✅ 待办        → 打开工作台→todos 页
────────
⚙ 设置        → 打开 WorkbenchWindow→settings 页
🚪 退出
```

### 单例服务

- `TodoStore.Instance` — 待办持久化 + `ItemsChanged` 事件
- `ChatManager.Instance` — 会话管理 + `SessionsChanged` 事件
- `ProviderManager.Instance` — LLM 供应商管理（配置式创建，支持自定义供应商增删）
- `CacheManager.Instance` — 截图缓存
- `LayerManager.Instance` — 图层管理
- `MemoryStore` — SQLite 记忆存储
- `ThemeManager.Instance` — 主题管理（Light/Dark/System 三态 + 持久化到 `ToolBoxOption.Data.Theme`）

### LLM 工具系统

工具通过 `[Tool("name", "desc")]` 标记在静态方法上，`ToolRegistry` 反射扫描后注册。

Agent 在对话循环中最多执行 10 轮工具调用，每轮最多 3 次重试。

### 配置持久化

- `ToolBoxOption` — XML 序列化到 `%AppData%/Setuna/config.xml`
- `CStyle.Items` — 通过代理属性 `ItemsXml` 实现 `IStyleItem` 接口的序列化
- `TodoStore` — JSON 到 `%AppData%/Setuna/todos.json`
- `ChatManager` — `chats.json` 索引 + `sessions/{id}.json` 消息
- `ProviderManager` — `active_model.json` + `providers.json`（供应商配置持久化）
- `ThemeManager` — 通过 `ToolBoxOption.Data.Theme` 持久化（无独立文件）

### 启动流程（App.OnStartup）

1. 单例守卫（Mutex）
2. 加载 `ToolBoxOption`，注入语言包 ResourceDictionary
3. `ThemeManager.Instance.Initialize(options.Data.Theme)` 应用初始主题
4. 创建 `MainWindow`（隐藏宿主）+ `CompactToolboxWindow`（迷你模式）+ `WorkbenchWindow`（工作台）
5. 显示 `MainWindow` 与 `WorkbenchWindow`
6. `SplashWindow` 由 `App.OnStartup` 在主题加载后显示（受 `ShowSplashWindow` 配置控制），初始化完成后自动关闭

## 功能模块

### 截图系统

- **CaptureWindow** — 全屏覆盖层，鼠标拖拽选择区域
- **ScrapWindow** — 浮动贴图窗口，支持 Topmost 置顶、拖拽移动、右键菜单
- **CacheManager** — 截图持久化到 `%LocalAppData%/Setuna/`，启动时自动恢复
- **HistoryView** — 嵌入工作台 `screenshots` 页，4 列缩略图网格 + 视图切换（网格/列表）+ 时间筛选（today/week/month/all）+ 搜索 + 分页（PageSize=16）

### LLM 助手

- **多会话管理** — 每个会话独立历史，支持置顶/删除
- **上下文管理** — `ContextCompressor` 超限时裁剪早期消息
- **工具调用** — 文件操作、待办管理、网络搜索
- **流式输出** — `IAsyncEnumerable<ChatChunk>` 逐 token 输出
- **Markdown 渲染** — 助手回复支持 Markdown 渲染（标题、列表、代码块、表格、引用、链接等），由 `MessageBubble` 内嵌 MdXaml 控件呈现；用户输入保持纯文本

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
- **LLM 集成** — `TodoTools` 允许 LLM 增删改查（add/list/complete/delete/update）
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

- 通过迷你窗口 `☰` 按钮或托盘菜单打开 WorkbenchWindow，导航至 `settings` 页
- 设置视图 `SettingsView` 嵌入 WorkbenchWindow 主内容区，平铺式 Section 布局

| Section | 内容 |
|---------|------|
| 🚀 启动 | 应用模式、开机自启 |
| ⌨️ 快捷键 | 启用全局热键、截图/显示隐藏热键 |
| 📸 截图 | 选区透明度、边框样式 |
| 🤖 LLM 助手 | 供应商选择/模型发现、API Key/Base URL、上下文窗口、文件白名单、测试连接、**自定义供应商管理** |
| ⏰ 定时截图与日报 | 定时全屏截图（Cron表达式）、每日行为总结（时间设置） |
| 🌈 主题 | Light / Dark / System 三态切换（持久化到 `ToolBoxOption.Data.Theme`） |
| 🌐 语言 | 中文/English |

### 截图定时与日报

设置界面提供「⏰ 定时截图与日报」Section，支持：

- **定时全屏截图** — 开关 + Cron 表达式配置（如 `0 */30 * * * *` 每30分钟）
- **每日行为总结** — 开关 + 时间设置（如 `18:00`），根据当日截图生成日报

后端组件：
- **CronScheduler** — 基于 Cronos 的定时触发；定时器延时向上取整到整毫秒，保证不早于计划时刻触发（提前触发会导致按"下次发生时刻"续订的调度取到同一时刻，产生连环重复触发）
- **ScreenshotTracker** — 定时全屏截图 + 应用使用记录
- **ReportGenerator** — 日报/周报 Markdown 生成

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
| 定时截图 | AutoScreenshotCron |   */30 * * * * |
| 每日总结 | DailyReportEnabled | false |
| 每日总结 | DailyReportTime | 18:00 |


## 数据存储路径

| 数据 | 路径 |
|------|------|
| 全局配置 | `%AppData%/Setuna/config.xml` |
| 待办列表 | `%AppData%/Setuna/todos.json` |
| 会话索引 | `%AppData%/Setuna/chats.json` |
| 会话消息 | `%AppData%/Setuna/sessions/{id}.json` |
| 供应商配置 | `%AppData%/Setuna/providers.json` |
| 活跃模型 | `%AppData%/Setuna/active_model.json` |
| 截图缓存 | `%LocalAppData%/Setuna/` |
| 记忆存储 | SQLite |

## 项目规则

详见根目录 `AGENTS.md`：

- `refs/` — 参考项目（禁止修改）
- `Project/` — 工程主体
- `Docs/` — 设计文档和计划文件
- 命名规范：字段/属性用小驼峰，类/结构体/方法用大驼峰，接口 `I` 前缀，枚举 `E` 前缀
- 不允许 `void` 方法，返回自身实现链式调用
- 不使用下划线前缀（`_`、`m_`、`k_`、`s_`）
- 本文件（`design.md`）仅在设计变更或功能更新时更新，不记录更新日志

