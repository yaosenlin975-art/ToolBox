# ToolBox 界面审查报告

> 审查人：UI/UX 设计师（苏格调）
> 审查日期：2026-07-14
> 审查范围：Project/Themes/ 全部主题文件 + Project/Views/ 全部 XAML 界面（共 28 个 .xaml 文件）
> 对标规范：`DesignTokens.xaml`、`Light.xaml`、`Dark.xaml`、`colors_and_type.css`（CSS 设计系统原版）

---

## 一、规范偏离清单

### 1.1 [严重] CornerRadius 令牌全部为 0 — 与设计规范严重不符

**问题**：`DesignTokens.xaml` 中所有圆角令牌（RadiusXs / RadiusSm / RadiusMd / RadiusLg / RadiusXl / RadiusFull）均设置为 `0`，但 CSS 设计系统原版（`colors_and_type.css`）明确定义：

| 令牌 | CSS 规范值 | XAML 实际值 | 偏差 |
|------|-----------|------------|------|
| RadiusXs | 4 | 0 | 严重 |
| RadiusSm | 6 | 0 | 严重 |
| RadiusMd | 10 | 0 | 严重 |
| RadiusLg | 14 | 0 | 严重 |
| RadiusXl | 20 | 0 | 严重 |
| RadiusFull | 9999 | 0 | 严重 |

**连锁影响**：
- DesignTokens.xaml 自身定义的控件样式（BtnDefault、InputField、CardStyle、ModernListBoxItem 等）引用了 `{StaticResource RadiusSm}` / `{StaticResource RadiusMd}`，实际渲染为直角
- 开发者为弥补此问题，在各 View 中大量硬编码 `CornerRadius="6"` / `"8"` / `"10"` 等值，导致令牌形同虚设
- 甚至 DesignTokens.xaml 内部的 FilterTabStyle（`CornerRadius="9999"`）、DateFilterStyle（`CornerRadius="4"`）、PriOptionStyle（`CornerRadius="6"`）、ModernCheckBox（`CornerRadius="4"`）、ModernRadioButton（`CornerRadius="8"`）都绕过了自身令牌直接硬编码

**应改为**：将 DesignTokens.xaml 中圆角令牌修正为 CSS 规范值：
```xml
<CornerRadius x:Key="RadiusXs">4</CornerRadius>
<CornerRadius x:Key="RadiusSm">6</CornerRadius>
<CornerRadius x:Key="RadiusMd">10</CornerRadius>
<CornerRadius x:Key="RadiusLg">14</CornerRadius>
<CornerRadius x:Key="RadiusXl">20</CornerRadius>
<CornerRadius x:Key="RadiusFull">9999</CornerRadius>
```
修正后，各 View 中硬编码的 `CornerRadius="8"` 等应统一替换为 `{StaticResource RadiusMd}` 等令牌引用。

---

### 1.2 硬编码色值（应使用 DynamicResource 令牌引用）

| 文件 | 行号 | 硬编码值 | 应改为 |
|------|------|---------|--------|
| CaptureWindow.xaml | 8 | `Background="#40000000"` | 新增 `OverlayDimBrush` 令牌或使用 BgOverlayBrush |
| CaptureWindow.xaml | 16 | `Stroke="#E8734A"` `Fill="#20E8734A"` | `AccentBrush` + `AccentMediumBrush` |
| CaptureWindow.xaml | 18 | `Background="#CC000000"` | 新增 `OverlayDarkBrush` 令牌 |
| CaptureWindow.xaml | 19 | `Foreground="White"` | `TextInvertedBrush` |
| CompactScrapWindow.xaml | 14 | `BorderBrush="#10000000"` `Background="#01FFFFFF"` | `BorderDefaultBrush` + 透明背景 |
| CompactToolboxWindow.xaml | 69 | `Background="#33FFFFFF"` | `SidebarHoverBrush` 或新增令牌 |
| CompactToolboxWindow.xaml | 70 | `Background="#4CAF50"` | `SuccessBrush` |
| ImagePreviewWindow.xaml | 6 | `Background="#CC000000"` | 同 CaptureWindow，新增 OverlayDarkBrush |
| PaintWindow.xaml | 14-18 | `Background="Red/Blue/Green/Black/White"` | 应使用调色板令牌或 AccentBrush 等 |
| PaintWindow.xaml | 29 | `Background="White"` | `BgElevatedBrush` |
| Sidebar.xaml | 51 | `Foreground="White"` | `SidebarTextActiveBrush` |
| SplashWindow.xaml | 14 | `Color="Black"` | 使用 `ShadowLg` 令牌 |
| TodoConfirmWindow.xaml | 6 | `Background="{DynamicResource BgPrimaryBrush}"` | `BgBrush`（BgPrimaryBrush 是旧别名，建议统一用新 key） |
| TodoConfirmWindow.xaml | 28 | `Background="{DynamicResource BgSecondaryBrush}"` | `BgElevatedBrush`（BgSecondaryBrush 是旧别名） |

**旧 key 别名问题**：Light.xaml/Dark.xaml 中定义了 `BgPrimaryBrush`/`BgSecondaryBrush`/`BgTertiaryBrush`/`BorderBrush`/`BorderLightBrush`/`DangerBrush` 等旧别名。部分 View 使用旧 key（如 TodoView、TodoConfirmWindow、TodoDetailWindow 中大量使用 `BorderBrush` 而非 `BorderDefaultBrush`），部分使用新 key。应统一为新 key，最终移除旧别名。

---

### 1.3 字号不符合规范

设计规范定义的字号体系：标题 18 Bold / Section 标题 11 SemiBold Tertiary / 正文 13 / 辅助 11 Tertiary。

| 文件 | 元素 | 实际字号 | 规范字号 | 问题 |
|------|------|---------|---------|------|
| SettingsView.xaml | 页面标题（如"通用设置"） | 22 SemiBold | 18 Bold | 偏大，应使用 FontSizeXl=18 |
| HistoryView.xaml | 页面标题（"截图历史"） | 22 SemiBold | 18 Bold | 同上 |
| DashboardView.xaml | 欢迎语 | 28 SemiBold | 无对应规范 | 超出最大规范字号(FontSize3xl=28)，但用于 Display 场景可接受 |
| DashboardView.xaml | 统计标签 | 12 Tertiary | 11 Tertiary | 偏大 1px |
| DashboardView.xaml | 快捷入口标题 | 14 SemiBold Secondary | 13 SemiBold | 应使用 FontSizeSm=13 |
| TodoView.xaml | 详情标题 | 16 SemiBold | 18 Bold 或 13 Medium | 非标准层级 |
| TodoDetailWindow.xaml | 任务标题 | 15 SemiBold | 18 Bold 或 13 Medium | 非标准字号 |
| TodoDetailWindow.xaml | 状态徽标 | 10 SemiBold | 11 SemiBold | 低于最小字号 11 |
| HistoryView.xaml | 尺寸显示 | 10 | 11 | 低于最小字号 |
| CompactToolboxWindow.xaml | 截图尺寸 | 10 | 11 | 低于最小字号 |
| WorkbenchWindow.xaml | 窗口标题 | 13 SemiBold | 18 Bold | 标题栏可例外，但与规范不一致 |

---

### 1.4 间距令牌几乎未使用

DesignTokens.xaml 定义了 Space1(4) ~ Space8(32) 共 7 级间距令牌，但几乎所有 View 都使用硬编码 Margin/Padding：

- `SettingsView.xaml`：仅 `SettingsSection` 使用了 `Padding="{StaticResource Space6}"`，其余全部硬编码
- `TodoView.xaml`：`Padding="20,12"`、`Margin="20,16"` 等全部硬编码
- `DashboardView.xaml`：`Padding="32,24"`、`Margin="0,0,0,24"` 等全部硬编码
- `ChatView.xaml`：`Padding="24,16,24,8"`、`Padding="16,12"` 等全部硬编码

**应改为**：统一使用 Space 令牌，如 `Padding="{StaticResource Space8},{StaticResource Space6}"`。但由于 WPF Thickness 不支持单值令牌直接用于多值，建议对常用组合预定义 Thickness 令牌（如 `SpacePagePadding = 32,24`）。

---

## 二、控件样式复用问题

### 2.1 DrawerExpander 样式重复定义

`CompactToolboxWindow.xaml`（第 81-133 行）和 `TodoView.xaml`（第 6-62 行）各自定义了完全相同的 `DrawerExpander` 样式（Expander 折叠抽屉），仅 CornerRadius 和 Padding 略有差异。

**应改为**：将 DrawerExpander 提取到 DesignTokens.xaml 中作为通用样式，两处引用。

---

### 2.2 ListBoxItem 模板四处各不相同

同一个"会话列表项/历史列表项"概念，存在四种不同的 ListBoxItem 自定义模板：

| 文件 | hover 背景 | selected 背景 | CornerRadius | 备注 |
|------|-----------|--------------|-------------|------|
| ChatView.xaml (L32-57) | BgSunkenBrush | BgSunkenBrush | 6 | 未使用 ModernListBoxItem |
| SessionSidebar.xaml (L31-76) | ChatSidebarHoverBrush | ChatSidebarActiveBrush | 4 | 使用 LLM-Colors 旧 key |
| HistoryView.xaml (L117-139) | — | — | 10 | 仅 hover 变 border，无 selected 态 |
| TodoConfirmWindow.xaml (L19-25) | — | — | — | 无模板，仅设置 Padding |
| SettingsView.xaml CategoryList (L642-657) | — | — | 6 | 无 hover/selected 视觉反馈 |

**应改为**：统一使用 `ModernListBoxItem` 样式（已定义于 DesignTokens.xaml，含 hover=BgHoverBrush、selected=AccentSoftBrush）。如需差异化，通过 BasedOn 扩展而非完全重写。

---

### 2.3 应提取到 DesignTokens 的局部样式

| 样式 | 当前位置 | 问题 | 建议 |
|------|---------|------|------|
| ToggleSwitch | SettingsView.xaml (L47-76) | 通用开关控件，仅 Settings 使用但可复用 | 提取到 DesignTokens |
| RadioCard | SettingsView.xaml (L79-114) | 卡片式单选，可复用于其他场景 | 提取到 DesignTokens |
| SettingsNavItem | SettingsView.xaml (L7-44) | 左侧导航项，与 Sidebar NavItemStyle 类似 | 可合并或提取 |
| CompactTabButton / Active | CompactToolboxWindow.xaml (L10-37) | 标签切换按钮，可复用 | 提取到 DesignTokens |
| CompactIconButton | CompactToolboxWindow.xaml (L38-61) | 与 IconButton 几乎相同（28x28 vs 32x32） | 应基于 IconButton 扩展或用参数化 |
| NavItemStyle | Sidebar.xaml (L6-40) | 侧边栏导航项 | 可与 SettingsNavItem 统一设计 |

---

### 2.4 未使用已有样式的控件

| 文件 | 控件 | 问题 | 应改为 |
|------|------|------|--------|
| PaintWindow.xaml | 全部 Button / ToolBar | 使用 WPF 默认样式，完全脱离设计系统 | 使用 BtnDefault/IconButton 等 |
| TodoDetailWindow.xaml L20 | 关闭按钮 | 裸 `Background="Transparent"` 无样式 | 使用 `IconButton` 样式 |
| TodoConfirmWindow.xaml L46 | 删除按钮 | 裸 `Background="Transparent"` 无样式 | 使用 `IconButton` 样式 |
| TodoConfirmWindow.xaml L58 | 取消按钮 | 使用 `BtnDefault` | 应使用 `OutlineButton`（与其他对话框一致） |
| ToolCallCard.xaml L7 | ToggleBtn | 裸按钮无样式 | 使用 `IconButton` 或提取专用样式 |
| SessionSidebar.xaml L22 | NewSessionBtn | 使用 `BtnDefault` 但尺寸 30x30 | 应使用 `IconButton` |
| HistoryView.xaml L83-86 | ViewGridBtn/ViewListBtn | 使用 IconButton 但 Content 是文字符号 | 可接受，但建议统一图标方案 |

---

## 三、主题一致性漏洞

### 3.1 [严重] LLM-Colors.xaml 完全不支持暗色主题

**问题**：`LLM-Colors.xaml` 定义了 20 个聊天相关 Brush（如 `ChatBgUserBrush=#E3F2FD`、`ChatTextUserBrush=#1565C0` 等），全部为浅色主题固定值，没有对应的暗色版本。

同时，`Light.xaml` 和 `Dark.xaml` 中已定义了主题感知的 Chat 系列 Brush（`ChatUserBgBrush`、`ChatAiBgBrush` 等），但 key 名称与 LLM-Colors.xaml 不同（如 `ChatUserBgBrush` vs `ChatBgUserBrush`）。

**影响**：
- `SessionSidebar.xaml` 使用 LLM-Colors 的 `ChatSidebarBgBrush`（StaticResource），暗色下背景仍为 `#F5F5F5`（浅灰），文字为深色，与暗色背景形成刺眼对比
- `ToolCallCard.xaml` 使用 `ChatBgToolBrush`（StaticResource），暗色下工具卡片背景仍为 `#F3E5F5`（浅紫），不可读
- `MessageBubble.xaml` 使用 `ChatStreamingDotBrush`（StaticResource），暗色下蓝色点 `#1976D2` 对比度不足

**应改为**：
1. 将 LLM-Colors.xaml 中的 key 合并到 Light.xaml / Dark.xaml 中（统一 key 命名）
2. 删除独立的 LLM-Colors.xaml 文件
3. App.xaml 中移除 LLM-Colors.xaml 的 MergedDictionary 引用
4. 所有引用改为 DynamicResource

---

### 3.2 StaticResource 误用（主题切换不生效）

以下位置使用了 `StaticResource` 引用主题相关 Brush，主题切换时不会更新：

| 文件 | 行号 | 引用 | 应改为 |
|------|------|------|--------|
| SessionSidebar.xaml | 5 | `{StaticResource ChatSidebarBgBrush}` | DynamicResource |
| SessionSidebar.xaml | 15 | `{StaticResource ChatSidebarBorderBrush}` | DynamicResource |
| SessionSidebar.xaml | 21 | `{StaticResource TextPrimaryBrush}` | DynamicResource |
| SessionSidebar.xaml | 49 | `{StaticResource FontSans}` | StaticResource 正确（字体不需动态切换） |
| SessionSidebar.xaml | 50 | `{StaticResource AccentBrush}` | DynamicResource |
| SessionSidebar.xaml | 52 | `{StaticResource TextPrimaryBrush}` | DynamicResource |
| SessionSidebar.xaml | 54 | `{StaticResource TextTertiaryBrush}` | DynamicResource |
| SessionSidebar.xaml | 60 | `{StaticResource DangerBrush}` | DynamicResource |
| SessionSidebar.xaml | 65 | `{StaticResource ChatSidebarHoverBrush}` | DynamicResource |
| SessionSidebar.xaml | 69 | `{StaticResource ChatSidebarActiveBrush}` | DynamicResource |
| ToolCallCard.xaml | 5 | `{StaticResource ChatBgToolBrush}` | DynamicResource |
| ToolCallCard.xaml | 11 | `{StaticResource ChatTextToolBrush}` | DynamicResource |
| ToolCallCard.xaml | 12 | `{StaticResource ChatTextToolBrush}` | DynamicResource |
| ToolCallCard.xaml | 15 | `{StaticResource BgSunkenBrush}` | DynamicResource |
| ToolCallCard.xaml | 17 | `{StaticResource TextSecondaryBrush}` | DynamicResource |
| ToolCallCard.xaml | 18 | `{StaticResource TextPrimaryBrush}` | DynamicResource |
| MessageBubble.xaml | 23 | `{StaticResource ChatStreamingDotBrush}` | DynamicResource |

---

### 3.3 暗色主题下已知不可用的硬编码

| 文件 | 位置 | 硬编码 | 暗色下表现 |
|------|------|--------|-----------|
| CompactToolboxWindow.xaml | L69 HealthBar 背景 | `#33FFFFFF` | 浅色半透明白，暗色下几乎不可见 |
| CompactToolboxWindow.xaml | L70 HealthBar 填充 | `#4CAF50` | 固定绿色，不随主题变化 |
| CaptureWindow.xaml | L8 窗口背景 | `#40000000` | 暗色下遮罩过暗 |
| CompactScrapWindow.xaml | L14 边框 | `#10000000` | 暗色下不可见 |
| PaintWindow.xaml | L29 画布 | `White` | 暗色下刺眼 |

---

## 四、交互与可用性改进点（按优先级排序）

### P0 — 必须修复

1. **CornerRadius 令牌归零导致全局直角**
   - 影响：所有使用令牌的控件（BtnDefault、InputField、CardStyle 等）渲染为直角，与硬编码圆角的 View 视觉割裂
   - 修复：修正 DesignTokens.xaml 中 6 个 Radius 令牌值

2. **LLM-Colors.xaml 不支持暗色主题**
   - 影响：SessionSidebar、ToolCallCard、MessageBubble 在暗色下不可读
   - 修复：合并到 Light/Dark.xaml，删除独立文件

3. **StaticResource 误用导致主题切换不生效**
   - 影响：SessionSidebar、ToolCallCard、MessageBubble 在切换主题时不更新颜色
   - 修复：全部改为 DynamicResource

### P1 — 应修复

4. **自定义按钮无 Focus 视觉**
   - 影响：IconButton、CompactTabButton、FilterTabStyle、NavItemStyle 等均未定义 `IsFocused` 状态的视觉反馈，键盘用户无法看到焦点位置
   - 修复：在各 ControlTemplate.Triggers 中添加 IsFocused 触发器（如 `BorderBrush=AccentBrush` 或聚焦光环）

5. **PaintWindow 完全脱离设计系统**
   - 影响：使用 WPF 默认 ToolBar 和 Button，与整体 Nerv 风格严重违和
   - 修复：重写 PaintWindow 使用 BtnDefault/IconButton 样式，颜色使用令牌

6. **空状态缺失**
   - ChatView.xaml：无会话时显示空白区域，无引导文案
   - TodoView.xaml：无待办时列表区域空白，无空状态提示
   - DashboardView.xaml：统计数据加载中无骨架屏/loading 态
   - 修复：添加空状态插画 + 引导文案（如"还没有会话，点击 + 开始对话"）

7. **WorkbenchWindow 无 MinWidth/MinHeight**
   - 影响：窗口可缩放至极小尺寸，内容溢出/重叠
   - 修复：设置 `MinWidth="960" MinHeight="600"`

8. **HistoryView 固定 4 列网格不响应式**
   - 影响：窗口窄时缩略图过小；窗口宽时大量留白
   - 修复：使用 `WrapPanel` 或动态计算列数

### P2 — 建议改进

9. **对话框取消按钮样式不一致**
   - CloseDialogWindow：BtnDefault
   - InputWindow：OutlineButton
   - MessageWindow：仅 OK 按钮
   - TodoConfirmWindow：BtnDefault
   - 建议：统一使用 OutlineButton

10. **SettingsView 页面标题过大**
    - 当前 FontSize=22，规范为 18 Bold
    - 22 与 14（卡片内标题）之间跨度太大，视觉层级不清晰

11. **ChatView 会话侧边栏与 SessionSidebar 重复实现**
    - ChatView.xaml 内嵌了一个 240px 的会话列表
    - SessionSidebar.xaml 是独立的 220px 会话列表
    - 两者模板、交互完全不同，应统一为一个组件

12. **CompactToolboxWindow 标签切换无键盘快捷键**
    - 建议：Ctrl+1/2/3 切换待办/助手/截图标签

13. **TodoView 详情面板固定 35% 宽度**
    - 窄屏下详情区域过窄，表单控件被压缩
    - 建议：设置 MinWidth 或可折叠

14. **缺少 Tooltip 国际化**
    - ChatView.xaml：`ToolTip="新建会话"` 硬编码中文
    - ChatView.xaml：`Content="发送"`、`Text="就绪"` 硬编码
    - TodoView.xaml：`Content="全部"`、`Content="待办"` 等硬编码
    - TodoDetailWindow.xaml：`Title="任务详情"` 硬编码
    - 建议：统一使用 DynamicResource 引用 Lang 资源

15. **DashboardView 统计卡片可点击但无视觉反馈**
    - `MouseLeftButtonUp` 事件绑定了跳转，但鼠标悬停时无 cursor=Hand（部分有），点击无按压反馈
    - CardStyle 有 hover border 变化，但缺少 pressed 态

16. **HistoryView 空状态提示位于底部栏**
    - `lblEmpty` 在 Grid.Row=3（底部栏），应居中显示在内容区域（Grid.Row=2）

---

## 五、整体视觉一致性评价

### 5.1 优点
- **设计令牌体系完善**：DesignTokens.xaml 覆盖了颜色、字号、圆角、间距、阴影、控件样式，体系结构清晰
- **双主题色板专业**：Light/Dark 两套色板色相一致、明度适配，AccentBrush 红色在两主题下均有良好对比度
- **核心控件样式统一**：BtnDefault/BtnPrimary/IconButton/InputField/ModernCheckBox/ModernRadioButton/FilterTabStyle 等样式定义规范，Hover/Disabled 状态完整
- **Settings 页面结构清晰**：左导航 + 右内容平铺布局，SettingRow（左 info / 右 control）模式一致性高
- **SplashWindow 设计精致**：装饰圆、Loading Bar、品牌色运用得当

### 5.2 主要问题
- **令牌与实现脱节**：最严重的问题是 CornerRadius 令牌全部为 0，导致令牌体系名存实亡，开发者被迫硬编码圆角值。这是"设计系统写了但没用上"的典型案例
- **新旧 key 并存**：BgPrimaryBrush/BgSecondaryBrush/BorderBrush 等旧别名与新 key（BgBrush/BgElevatedBrush/BorderDefaultBrush）混用，增加了维护成本和认知负担
- **LLM-Colors 独立于主题系统**：聊天相关颜色作为独立字典存在，不随主题切换，是架构层面的设计缺陷
- **组件复用率低**：DrawerExpander 重复定义、ListBoxItem 四种模板、局部样式未提取，说明设计系统的推广执行不到位
- **PaintWindow 是设计孤岛**：完全使用 WPF 默认样式，与整体 Nerv 风格断裂
- **国际化覆盖不全**：部分界面文案硬编码中文，未使用 Lang 资源

### 5.3 一致性评分

| 维度 | 评分 | 说明 |
|------|------|------|
| 颜色令牌使用 | 7/10 | 主流 View 使用较好，但 Capture/Paint/Compact 系列硬编码较多 |
| 字号规范 | 6/10 | 多处非标准字号（15/22/28），低于最小字号 11 的情况较多 |
| 圆角令牌 | 2/10 | 令牌全部为 0，形同虚设；硬编码圆角散布各处 |
| 间距令牌 | 3/10 | Space 令牌几乎未使用，全部硬编码 |
| 控件样式复用 | 6/10 | 核心控件复用好，但 ListBoxItem/Expander/局部样式重复多 |
| 主题一致性 | 5/10 | LLM-Colors 不支持暗色、StaticResource 误用是硬伤 |
| 交互完整性 | 6/10 | 缺少 Focus 视觉、空状态、键盘快捷键 |
| 国际化 | 5/10 | 部分文案硬编码，覆盖不全 |
| **综合** | **5/10** | 设计系统基础好，但执行落地存在系统性偏差 |

---

## 六、修复优先级路线图

### 第一阶段：修正令牌基础（阻断性）
1. 修正 DesignTokens.xaml 中 6 个 CornerRadius 令牌值
2. 合并 LLM-Colors.xaml 到 Light.xaml / Dark.xaml，删除独立文件
3. 全局替换 StaticResource → DynamicResource（主题相关 Brush）

### 第二阶段：消除硬编码
4. 替换所有硬编码色值为令牌引用
5. 统一新旧 key（移除旧别名，全局替换为新 key）
6. 修正非标准字号为规范值

### 第三阶段：提升复用
7. 提取 DrawerExpander / ToggleSwitch / RadioCard / CompactTabButton 到 DesignTokens
8. 统一 ListBoxItem 模板为 ModernListBoxItem
9. 重写 PaintWindow 使用设计系统样式

### 第四阶段：交互完善
10. 添加 Focus 视觉状态到所有自定义按钮
11. 补充空状态 / 加载状态
12. 设置窗口 MinWidth/MinHeight
13. 补全国际化资源引用
14. 统一对话框按钮样式
