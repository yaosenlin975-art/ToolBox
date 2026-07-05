# ToolBox UI 重构实施计划（Nerv-inspired Redesign）

> **执行方式：** Trae 当前会话顺序执行，用 TodoWrite 跟踪，每 Phase 结束做编译验证。

**Goal:** 按 `Docs/ToolBox-Redesign` 的 Nerv-inspired 设计，重构 ToolBox 全部 7 个界面，引入明/暗双主题切换与 Geist/Roboto Mono 嵌入字体。

**Architecture:**
- 新建 `WorkbenchWindow`（1440×900，64px 深色 Sidebar + 主区域），承载 5 个页面：Dashboard / Assistant / Todos / Screenshots / Settings
- 重构 `CompactToolboxWindow`（320×260 折叠 / 520×720 展开，4 Tab）与 `SplashWindow`（480×360 三状态）
- 新增 `ThemeManager` + Light/Dark 双资源字典，`ToolBoxOption` 增加 `Theme` 字段（Light/Dark/System）
- `DesignTokens.xaml` 重写为完整令牌系统（Color/Brush/Radius/Space/Shadow/Font/Transition）

**Tech Stack:** .NET 8.0 WPF, C#, MdXaml, Cronos, Microsoft.Data.Sqlite

---

## 关键现状（调研结论）

1. **`WorkbenchWindow` 不存在**——实际工作台是 `CompactToolboxWindow`（已实现折叠/展开双态 + 4 Tab，但视觉旧）
2. **`SettingsWindow` 不存在**——`SettingsView` 嵌入 CompactToolbox
3. **`MainWindow` 是隐藏宿主**，App.OnStartup 同时创建 MainWindow + CompactToolboxWindow
4. **无主题切换机制**，CompactToolbox 用局部 `Window.Resources` 硬编码深色覆盖
5. **`ToolBoxOption` 在 `Models/SetunaOption.cs`**，无 `Theme` 字段
6. **DesignTokens.xaml 是浅色单一主题**，13 Color + 13 Brush + 7 内嵌 Style，无 FontFamily/Radius/Space 令牌
7. **无全局字体**，局部用 Consolas/Inter
8. **重复 View**：ChatWindow/ChatView、TodoWindow/TodoView、HistoryWindow/HistoryView、OptionWindow/SettingsView 各两份

---

## 文件结构变更

### 新建（Project/）
| 路径 | 职责 |
|---|---|
| `Assets/Fonts/Geist-Regular.ttf` 等 5 个权重 | 嵌入字体（Geist 300/400/500/600/700） |
| `Assets/Fonts/GeistMono-Regular.ttf` 等 3 个权重 | 等宽（Geist Mono 400/500/600，替代 Roboto Mono） |
| `Themes/Light.xaml` | 明色主题 Brush 字典 |
| `Themes/Dark.xaml` | 暗色主题 Brush 字典 |
| `Core/Theming/ThemeManager.cs` | 主题状态 + 切换 + 持久化 + System 跟随 |
| `Views/WorkbenchWindow.xaml` + `.cs` | 1440×900 工作台 shell |
| `Views/Shell/Sidebar.xaml` + `.cs` | 64px 深色侧边栏 UserControl |
| `Views/Shell/TopHeader.xaml` + `.cs` | 52px 顶部栏 UserControl |
| `Views/Dashboard/DashboardView.xaml` + `.cs` | 工作台首页 |
| `Views/Chat/AssistantView.xaml` + `.cs` | 1440 全屏聊天页（替代旧 ChatView 嵌入版） |

### 修改（Project/）
| 路径 | 变更 |
|---|---|
| `Themes/DesignTokens.xaml` | 重写：完整令牌 + 通用控件 Style（IconButton/PrimaryButton/SearchBox/Card/ListItem/Checkbox/Toggle/Slider/FilterTab/Tag） |
| `Themes/LLM-Colors.xaml` | 删除与 DesignTokens 重复的 Brush，仅保留 Chat 专属语义色 |
| `App.xaml` | MergeDictionaries 加 Light/Dark；定义全局 FontFamily |
| `App.xaml.cs` | 启动时初始化 ThemeManager；创建 WorkbenchWindow 替代/并存 CompactToolbox |
| `Models/SetunaOption.cs` | `ToolBoxOptionData` 增加 `Theme` 字段（string，默认 "System"） |
| `Views/CompactToolboxWindow.xaml(.cs)` | 视觉重做：Nerv 风格 + 4 Tab + 折叠/展开 |
| `Views/SplashWindow.xaml(.cs)` | 480×360 + 三状态（Loading/Error/Complete）+ 六边形盾牌 logo |
| `Views/MainWindow.cs` | `OpenWorkbench` 改为打开 WorkbenchWindow |
| `Views/Chat/ChatView.xaml(.cs)` | 重做：240px session-sidebar + chat-area + tool-card + streaming-dots |
| `Views/Chat/MessageBubble.xaml` | 重做：user/ai 气泡不对称圆角 + code-block 语法高亮 |
| `Views/Chat/ToolCallCard.xaml` | 重做：紫色系 + expand 折叠 |
| `Views/Todo/TodoView.xaml(.cs)` | 重做：65/35 双栏 + filter-tabs + todo-item hover-actions + detail panel |
| `Views/HistoryView.xaml(.cs)` | 重做：4 列网格 + view-toggle + date-filters + batch-bar |
| `Views/SettingsView.xaml(.cs)` | 重做：200px settings-nav + content + bottom-bar；新增「主题」选项 |
| `Views/Todo/TodoItem.cs`（Core/Todo/） | 增加 `Priority`/`Tags` 字段（与 redesign 对齐） |
| `ToolBox.csproj` | 添加 `<Resource>` 字体嵌入 |

### 删除（待定，Phase 6 决定）
- `Views/Chat/ChatWindow.xaml`（独立窗口，已被 AssistantView 取代）
- `Views/Todo/TodoWindow.xaml`、`Views/Todo/CompactTodoWindow.xaml`
- `Views/HistoryWindow.xaml`
- `Views/Options/OptionWindow.xaml`

---

## Phase 0：基础设施（字体 + 令牌 + 主题系统）

**目标：** 跑通主题切换，所有后续页面基于新令牌开发。

### 任务
1. **下载字体** → `Project/Assets/Fonts/`
   - Geist: https://github.com/vercel/geist-font/releases（Geist-Regular/Medium/SemiBold/Bold + GeistMono-Regular/Medium）
   - 若下载失败，回退方案：用 Segoe UI Variable + Cascadia Code，DesignTokens 中 FontFamily 令牌指向系统字体
2. **csproj 嵌入字体**：`<Resource Include="Assets\Fonts\*.ttf" />`
3. **重写 `Themes/DesignTokens.xaml`**：完整令牌（参照 colors_and_type.css 1:1 翻译）
   - Color/Brush（accent #d63031，sidebar-bg #12131a 等）
   - Radius xs/sm/md/lg/xl/full = 4/6/10/14/20/9999
   - Space 1/2/3/4/5/6/8 = 4/8/12/16/20/24/32
   - Shadow sm/md/lg/float
   - FontFamily: FontSans（Geist）/ FontMono（Geist Mono）
   - FontSize xs/sm/base/lg/xl/2xl/3xl
   - Transition 150ms/250ms
   - 通用控件 Style：IconButton/PrimaryButton/OutlineButton/SearchBox/Card/ListItem/Checkbox/ToggleSwitch/Slider/FilterTab/Tag
4. **拆分 `Themes/Light.xaml` + `Themes/Dark.xaml`**：仅 Brush key（与 DesignTokens 同 key），用 `x:Key` 覆盖
5. **`Core/Theming/ThemeManager.cs`**：单例，`EThemeMode { Light, Dark, System }`，`ApplyTheme()` 切换 App.Resources.MergedDictionaries 中的 Light/Dark 字典；监听 SystemEvents.UserPreferenceChanged
6. **`Models/SetunaOption.cs`**：`ToolBoxOptionData` 加 `Theme` 字段（string，默认 "System"）
7. **`App.xaml`**：MergeDictionaries 引入 DesignTokens + Light + Dark + LLM-Colors；定义全局 FontFamily
8. **`App.xaml.cs`**：启动时 `ThemeManager.Instance.Initialize(options.Data.Theme)`

### 验收
- `dotnet build` 通过
- 临时窗口测试：切换 Theme 字典后 Brush 立即变化
- 字体在 XAML 中可引用 `{StaticResource FontSans}`

---

## Phase 1：WorkbenchWindow Shell + Sidebar

**目标：** 工作台外壳可运行，5 个 nav 项可切换（页面用占位 UserControl）。

### 任务
1. **`Views/Shell/Sidebar.xaml`**：64px 宽，深色 `SidebarBgBrush`，5 个 nav-item（Dashboard/Assistant/Todos/Screenshots/Settings），active 态左 3px 红条 + soft 背景；底部 logo
2. **`Views/Shell/TopHeader.xaml`**：52px 高，elevated 背景，标题 + 可选 stats + 32×32 IconButton
3. **`Views/WorkbenchWindow.xaml`**：1440×900，`WindowStyle=None` + 自定义标题栏 + `Border`（radius-lg + shadow-float）；Grid = Sidebar | MainArea；MainArea 用 ContentControl 承载当前页
4. **`Views/WorkbenchWindow.xaml.cs`**：`NavigateTo(string page)` 切换 ContentControl.Content；5 个占位 UserControl（仅显示页名）
5. **`App.xaml.cs`**：启动改为创建 WorkbenchWindow（保留 CompactToolboxWindow 创建逻辑，但默认隐藏，由托盘菜单切换）

### 验收
- `dotnet build` 通过
- 启动后 WorkbenchWindow 可见，点击 sidebar 5 项切换主区域内容
- 暗色主题下视觉与 workbench.html 一致

---

## Phase 2：5 个主页面实现

**目标：** 5 个页面 1:1 还原 redesign HTML。

### 2.1 DashboardView（workbench.html 的 main 区域）
- 概览卡片：待办统计 / 最近会话 / 截图数 / 快捷动作
- 跳转按钮指向其他 4 页

### 2.2 AssistantView（assistant.html）
- 240px session-sidebar（会话列表 + 搜索 + 新建）+ chat-area（header 52px + 消息流 + input-container）
- 复用现有 `ChatManager.Instance`，重写 `MessageBubble`（user 红底白字右下小半径 / ai 白底 border 左下小半径）+ `ToolCallCard`（紫色系 expand）+ streaming-dots 动画
- code-block 语法高亮（kw/fn/str/cm/tp 5 色）
- model-badge + model-selector

### 2.3 TodoView（todos.html）
- 65/35 双栏：左 panel 列表 + 右 panel 详情
- filter-tabs（全部/待办/进行中/已完成）+ sort-dropdown + search-box
- quick-add（28×28 红 + input + Ctrl+Enter hint）
- todo-item：18×18 checkbox + priority-dot + title+tags + due-badge + hover-actions
- detail panel：title-input + status-badge + priority-options + detail-textarea + tags + date-picker + timestamps
- **数据模型扩展**：`TodoItem` 增加 `Priority`（EUrgent/Important/Normal）+ `Tags`（List<string>）；保留现有 `Progress`/`Children`（redesign 缺，但 design.md 有，按 design.md 保留）
- `TodoTools.cs` 同步扩展

### 2.4 HistoryView（screenshots.html）
- 64px sidebar（复用 Shell/Sidebar）+ main-inner
- toolbar：view-toggle（网格/列表）+ date-filters（今日/本周/本月/全部）+ search + filter-btn + sort-btn
- screenshot-grid 4 列 + ss-card（16:10 缩略图 + hover overlay 4 action-btn + ss-info）
- batch-bar 浮动底部（批量删除/导出）

### 2.5 SettingsView（settings.html）
- 200px settings-nav（7 nav-item：常规/快捷键/截图/LLM/定时/语言/关于）+ content + bottom-bar（重置/保存）
- setting-row 模式：label+desc 左 / control 右
- 控件：ToggleSwitch / radio-group / slider / number-input
- 「常规」Section 增加 Theme 三态切换（Light/Dark/System）

### 验收
- 5 页均可从 sidebar 切换显示
- AssistantView 能正常发送消息 + 流式接收 + 工具调用卡片显示
- TodoView CRUD 正常，数据持久化
- HistoryView 加载历史截图，批量选择可用
- SettingsView 主题切换实时生效

---

## Phase 3：CompactToolboxWindow 视觉重构

**目标：** 迷你窗口视觉对齐 redesign compact.html。

### 任务
1. 移除局部 `Window.Resources` 深色硬编码（改用 ThemeManager）
2. 折叠态 320×260：expand-btn 26×26 + compact-tabs 4 个 + session-selector + content
3. 展开态 520×720：expanded-tabs + chat-messages + tool-call-card + input-area 56px + send-btn 36×36 + model-selector
4. 内嵌的 TodoView/ChatView/HistoryView/SettingsView 改用 Phase 2 重做版本（通过 `IsCompactMode` 适配）
5. ScrapWindow 三态（active 100% / inactive 40% / rollover 80%）+ hover scrap-toolbar + context-menu（参照 compact.html Section 4）

### 验收
- 折叠/展开切换正常
- 4 Tab 内容显示正常
- 暗色主题下视觉对齐 redesign

---

## Phase 4：SplashWindow 重做

**目标：** 启动画面 480×360 三状态。

### 任务
1. 480×360 + radius-xl + shadow-lg
2. 六边形盾牌 logo（SVG → Geometry 或 Path）
3. loading-bar 200×4 + status-text 等宽字体
4. 三状态：Loading（70% 红 + pulse）/ Error（X 图标 + 重试/跳过按钮）/ Complete（对勾绿 + countdown 3 秒自动关闭）
5. 与 App 启动流程对接：初始化进度 → Complete → 打开 WorkbenchWindow

### 验收
- 启动时显示 Loading → Complete 流程
- 异常时显示 Error 状态

---

## Phase 5：整合 + 清理 + 文档

### 任务
1. **MainWindow.cs**：`OpenWorkbench` 改为打开/激活 WorkbenchWindow；托盘菜单「AI 助手/待办/历史/设置」指向 WorkbenchWindow 对应页
2. **删除冗余 View**：ChatWindow/TodoWindow/CompactTodoWindow/HistoryWindow/OptionWindow（确认无引用后）
3. **语言包更新**：`Lang zh-CN.xaml` / `Lang en-US.xaml` 补充新增字符串（主题、常规、关于等）
4. **design.md 同步更新**：架构部分改为「Sidebar + 5 页面」，SettingsView 重新嵌入工作台，新增主题系统说明
5. **全量编译 + 运行验证**

### 验收
- `dotnet build` 0 error 0 warning（已有 warning 不新增）
- 启动 → Splash → WorkbenchWindow 全流程正常
- 5 页面功能正常，主题切换实时生效
- CompactToolboxWindow 与 WorkbenchWindow 可并存/切换
- design.md 与实现一致

---

## 风险与回退

| 风险 | 应对 |
|---|---|
| Geist 字体下载失败 | 回退 Segoe UI Variable + Cascadia Code，FontFamily 令牌指向系统字体 |
| TodoItem 模型扩展破坏现有 JSON | 新字段用 `[JsonProperty]` + 默认值，向后兼容 |
| WorkbenchWindow 与 CompactToolboxWindow 并存导致入口混乱 | 托盘菜单明确区分；SettingsView 增加「默认窗口模式」选项 |
| ScrapWindow 三态改动影响截图核心流程 | 三态仅视觉层，不改动 ScrapBook 核心逻辑；先备份原 ScrapWindow |
| 主题切换时 MdXaml 渲染异常 | MessageBubble 显式绑定 Brush，不依赖 DynamicResource |

---

## 执行顺序

Phase 0 → Phase 1 → Phase 2（5 页并行子任务）→ Phase 3 → Phase 4 → Phase 5

每 Phase 结束做 `dotnet build` 验证，失败则修复后再进入下一 Phase。
