# ToolBox 功能设计索引

> 基于竞品调研报告 20 个可移植功能的完整设计文档集
> 对齐 ToolBox 现有分层架构（Core/Models/Services/Views）

---

## 优先级总览

| # | 功能 | 优先级 | 难度 | 工作量 | 来源竞品 | 设计文件 |
|---|------|--------|------|--------|---------|---------|
| 1 | 离线 OCR 文字识别 | **P0** | 中 | 8 人天 | PixPin, ShareX, Snagit | [01-OCR-Offline.md](./01-OCR-Offline.md) |
| 2 | 剪贴板历史管理 | **P0** | 中 | 6 人天 | Raycast, uTools | [02-Clipboard-History.md](./02-Clipboard-History.md) |
| 3 | 屏幕取色器 | **P0** | 低 | 3 人天 | Snipaste, PowerToys | [03-Color-Picker.md](./03-Color-Picker.md) |
| 4 | 截图后自动化动作链 | **P1** | 中 | 5 人天 | ShareX, Greenshot | [04-Action-Chain.md](./04-Action-Chain.md) |
| 5 | 长截图/滚动截图 | **P1** | 中 | 7 人天 | PixPin, ShareX, Snagit | [05-Scrolling-Screenshot.md](./05-Scrolling-Screenshot.md) |
| 6 | 智能日期识别 | **P1** | 中 | 4 人天 | TickTick | [06-Smart-Date-Parse.md](./06-Smart-Date-Parse.md) |
| 7 | 截图标签与智能分类 | **P1** | 中 | 5 人天 | Eagle, ShareX | [07-Screenshot-Tags.md](./07-Screenshot-Tags.md) |
| 8 | 代码片段管理 | **P1** | 中 | 5 人天 | Raycast | [08-Code-Snippets.md](./08-Code-Snippets.md) |
| 9 | 截图后悬浮预览卡 | **P1** | 低 | 3 人天 | CleanShot X | [09-Preview-Card.md](./09-Preview-Card.md) |
| 10 | P2+P3 快速设计 | P2-P3 | — | — | 多源 | [10-Quick-Designs.md](./10-Quick-Designs.md) |

## 实现路线图

### 第一阶段：补齐截图基础能力（P0，1-2 迭代，约 17 人天）

```
Week 1-2: 离线 OCR → Week 2: 取色器 → Week 2-3: 剪贴板历史
```

**里程碑 M1**：ToolBox 具备"截图 → OCR → 复制文字"完整闭环；用户可通过热键随时取色；剪贴板历史可回溯。

### 第二阶段：截图工作流深化 + 待办体验提升（P1，2-4 迭代，约 29 人天）

```
Week 3-4: 动作链 + 悬浮预览卡 → Week 4-5: 长截图 + 代码片段
Week 5-6: 截图标签 + 智能日期识别
```

**里程碑 M2**：截图后可配置自动处理流程；截图历史支持多维度检索；待办输入体验对标 TickTick。

### 第三阶段：差异化与生态构建（P2-P3，4+ 迭代）

```
日历视图 → 上下文感知 → AI 个性化 → 步骤捕获 → 智能打码
→ 番茄钟 → 屏幕冻结 → QR码 → 窗口置顶 → 习惯打卡 → 云分享
```

**里程碑 M3**：ToolBox 形成"截图为眼、AI 为脑、待办为手、日报为镜"的差异化闭环。

## 架构对齐原则

所有新增功能遵循以下设计约束：

1. **分层归属** — 接口与逻辑进 `Core/`，数据模型进 `Models/`（简单模型）或 `Core/<Module>/`（领域模型），UI 进 `Views/`
2. **单例服务** — 全局状态管理走 `Instance` 模式 + `*Changed` 事件
3. **持久化路径** — 配置进 XML，结构化数据进 JSON，大量记录进 SQLite
4. **LLM 暴露** — 适合 AI 调用的功能暴露为 `[Tool]` 静态方法
5. **命名规范** — I 前缀接口、E 前缀枚举、大驼峰类型、小驼峰字段、无 void 链式返回
6. **UI 一致** — 复用 `DesignTokens.xaml` 令牌和现有 Style（CardStyle / FilterTabStyle / BtnPrimary 等）
7. **热键体系** — 统一注册到 `HotkeyManager`，快捷键持久化到 `ToolBoxOption`

## 数据存储路径扩展

| 新增数据 | 路径 |
|---------|------|
| 剪贴板历史 | `%AppData%/ToolBox/clipboard.json` |
| OCR 语言包 | `%LocalAppData%/ToolBox/ocr/` |
| 截图标签 | `%AppData%/ToolBox/screenshot_tags.json` |
| 代码片段 | `%AppData%/ToolBox/snippets.json` |
| 动作链配置 | `%AppData%/ToolBox/action_chains.json` |

## 热键体系扩展

| 功能 | 默认热键 | 优先级 |
|------|---------|--------|
| 取色器 | `Ctrl+Shift+C` | P0 |
| OCR 识别 | `Ctrl+Shift+O` | P0 |
| 剪贴板面板 | `Ctrl+Shift+V` | P0 |
| 长截图 | `Ctrl+Shift+L` | P1 |
| 代码片段面板 | `Ctrl+Shift+S` | P1 |

## LLM Tool 扩展清单

| Tool 名称 | 功能 | 来源设计 |
|-----------|------|---------|
| `ocr_screenshot` | 对最近截图执行 OCR | 01-OCR-Offline |
| `search_clipboard` | 搜索剪贴板历史 | 02-Clipboard-History |
| `pick_color` | 获取屏幕某点色值 | 03-Color-Picker |
| `add_snippet` | 保存代码片段 | 08-Code-Snippets |
| `get_snippet` | 获取代码片段 | 08-Code-Snippets |
| `search_screenshots` | 按标签/时间搜索截图 | 07-Screenshot-Tags |
| `create_action_chain` | 创建截图自动化动作链 | 04-Action-Chain |
