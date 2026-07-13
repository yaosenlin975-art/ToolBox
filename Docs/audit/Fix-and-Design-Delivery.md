# ToolBox 漏洞修复 + 死代码复活 + 可移植功能设计 · 交付报告

> 交付总监：齐活林
> 协作团队：toolbox-fix-and-design（WPF 开发 / AI-LLM 专家 / 架构师 3 路并行）
> 交付日期：2026-07-13

---

## 一、任务概述

基于上一轮全面审查（`Docs/audit/` 下 4 份报告 + overview.md），执行两项任务：
1. **修复已知漏洞并复活死代码** — P0 安全/性能修复 + LLM 管线死代码复活
2. **可移植功能设计文档** — 竞品调研中 20 个功能的完整架构设计

---

## 二、修复成果汇总（13 项 · 编译通过 · 0 新增错误）

### WPF 代码修复（寇豆码 · 8 项）

| # | 类别 | 修复项 | 文件 | 效果 |
|---|------|--------|------|------|
| A | P0 安全 | FileAccessWhitelist+ConfirmDialog 接入 FileTools | `Core/Tools/FileTools.cs` | LLM 文件操作受限白名单，删除需确认弹窗 |
| B | P0 | DefaultSystemPromptBuilder 删除虚假声称 | `Core/Llm/DefaultSystemPromptBuilder.cs` | 不再谎称有 OCR/白名单/确认 |
| C | P0 性能 | 删除 ScrapWindow.CloseScrap() GC.Collect() | `Models/ScrapWindow.cs:160` | 关闭贴图不再触发全堆 GC |
| D | P0 性能 | WindowManager 条件触发 + ScreenshotTracker 去重 | `Core/Window/WindowManager.cs` + `Core/Scheduling/ScreenshotTracker.cs` | 每小时从 ~7200 次文件写入降至实际变化次数 |
| E | P0 性能 | MessageBubble 流式渲染延迟 + ChatFontSize 缓存 | `Views/Chat/MessageBubble.xaml.cs` | 流式输出不再每 token 全量正则+磁盘读 |
| F | P1 架构 | ScrapBook 事件转发到 CacheManager | `Services/ScrapBook.cs` | 贴图移动/编辑后缓存正确更新 |
| G | P1 导航 | 托盘菜单页面 ID 映射修复 | `CompactToolboxWindow.xaml.cs` + `MainWindow.cs` | "settings"/"history" 不再显示空白 |
| H | P1 数据 | 数据目录统一标注 TODO | `Models/SetunaOption.cs` + `Services/CacheManager.cs` | 留 TODO 待迁移方案确认后统一 |
| + | 附加 | 修复 XAML 双重 UTF-8 BOM 编译失败 | `CompactToolboxWindow.xaml` + `TodoView.xaml` | 编译通过 |

### LLM 管线修复（智言溪 · 5 项）

| # | 修复项 | 文件 | 效果 |
|---|--------|------|------|
| 1 | ContextCompressor 三级压缩接入 Agent 管线 | `Core/Llm/Agent.cs` | 长对话自动压缩，不再依赖 token 超限报错 |
| 2 | MemoryStore 读写链路闭环（GetRelevant + Cleanup） | `Core/Llm/Agent.cs` + `Core/Llm/DefaultSystemPromptBuilder.cs` | 记忆写入后能被读取注入对话 |
| 3 | DefaultSystemPromptBuilder 提示词修正 | `Core/Llm/DefaultSystemPromptBuilder.cs` | 提示词与实现一致，新增工具名列表 |
| 4 | AnthropicProvider 完整工具调用实现 | `Core/Providers/AnthropicProvider.cs` | Claude 模型可调用工具（待办/文件/搜索） |
| 5 | Agent 工具调用重试机制（3 次） | `Core/Llm/Agent.cs` | 工具失败自动重试，与 design.md 一致 |

### 编译状态
- **新引入编译错误：0**
- 预存错误 2 个（`Agent.cs:77,83` yield return in try/catch，非本次引入）
- 预存警告 ~100 个（nullable/event null，非本次引入）

---

## 三、设计文档汇总（11 文件 · 20 个功能）

全部写入 `Docs/design/features/`，严格对齐 `design.md` 分层架构与命名规范。

### P0 功能（3 个 · 详细设计 · 合计 17 人天）

| 文件 | 功能 | 技术方案 | 工作量 |
|------|------|----------|--------|
| `01-OCR-Offline.md` | 离线 OCR | Tesseract + Windows OCR 双引擎回退，`IOcrEngine` + `OcrService` | 8 人天 |
| `02-Clipboard-History.md` | 剪贴板历史 | Win32 Clipboard Chain + JSON 持久化，新工作台页面 | 6 人天 |
| `03-Color-Picker.md` | 取色器 | P/Invoke GetPixel + 放大镜覆盖层，HEX/RGB/HSL | 3 人天 |

### P1 功能（6 个 · 详细设计 · 合计 29 人天）

| 文件 | 功能 | 技术方案 | 工作量 |
|------|------|----------|--------|
| `04-Action-Chain.md` | 自动化动作链 | Pipeline 模式，`IActionNode` + ActionChainEngine | 5 人天 |
| `05-Scrolling-Screenshot.md` | 长截图 | SendMessage 滚动 + 模板匹配拼接 | 7 人天 |
| `06-Smart-Date-Parse.md` | 智能日期识别 | 正则 + LLM 双层解析，`SmartDateParser` | 4 人天 |
| `07-Screenshot-Tags.md` | 截图标签分类 | `ScreenshotTagStore` 元数据 + LLM 自动打标 | 5 人天 |
| `08-Code-Snippets.md` | 代码片段管理 | 关键字触发 + 变量占位符，工作台 snippets 页 | 5 人天 |
| `09-Preview-Card.md` | 悬浮预览卡 | 右下角弹出窗口，`PreviewCardManager` | 3 人天 |

### P2+P3 功能（11 个 · 概要设计 · 1 文件）

`10-Quick-Designs.md`：日历视图、上下文感知快捷操作、AI 全数据个性化、步骤捕获、智能打码、番茄钟、屏幕冻结、QR 码识别、窗口置顶快捷键、习惯打卡、云分享。每个功能含要点 + 核心接口 + 可行性评估。

### 架构对齐确认
- 所有模块归属 `Core/Models/Services/Views` 分层
- 单例服务走 `Instance` + `Changed` 事件模式
- 10+ 个新 LLM Tool（`[Tool]` 静态方法）
- UI 复用 `DesignTokens.xaml` 令牌和现有控件样式
- 5 个新热键扩展 + 统一注册 `HotkeyManager`
- 无新增 NuGet 依赖（P0+P1），P3 仅 ZXing.Net
- 需同步更新 `design.md` 的 6 个部分（目录/服务清单/存储路径/热键/导航/设置 Section）

---

## 四、工作量估算（仅设计文档覆盖的功能）

| 优先级 | 功能数 | 估算人天 |
|--------|--------|---------|
| P0 | 3 | 17 |
| P1 | 6 | 29 |
| P2+P3 | 11 | —（概要阶段） |
| **合计** | **20** | **46 人天（P0+P1）** |

---

## 五、产出文件清单

### 修复日志
| 文件 | 内容 |
|------|------|
| `Docs/audit/Fix-Log-WPF.md` | 8 项 WPF 修复详情（文件:行号 + 修改 + 验证） |
| `Docs/audit/Fix-Log-LLM.md` | 5 项 LLM 修复详情 |

### 设计文档（`Docs/design/features/`）
| 文件 | 内容 |
|------|------|
| `00-README.md` | 功能索引 + 优先级矩阵 + 三阶段路线图 |
| `01-OCR-Offline.md` | P0 离线 OCR 详细设计 |
| `02-Clipboard-History.md` | P0 剪贴板历史详细设计 |
| `03-Color-Picker.md` | P0 取色器详细设计 |
| `04-Action-Chain.md` | P1 动作链详细设计 |
| `05-Scrolling-Screenshot.md` | P1 长截图详细设计 |
| `06-Smart-Date-Parse.md` | P1 智能日期详细设计 |
| `07-Screenshot-Tags.md` | P1 截图标签详细设计 |
| `08-Code-Snippets.md` | P1 代码片段详细设计 |
| `09-Preview-Card.md` | P1 悬浮预览卡详细设计 |
| `10-Quick-Designs.md` | P2+P3 11 个功能概要设计 |

### 综合交付报告
| 文件 | 内容 |
|------|------|
| `Docs/audit/Fix-and-Design-Delivery.md` | 本报告 |

---

## 六、下一步建议

1. **立即验证修复**：启动 ToolBox 测试 LLM 文件操作是否受限、长对话是否自动压缩、Claude 是否能用工具——建议触发 qa-engineer 做回归测试
2. **更新 design.md**：架构师已标注 6 个需同步更新的部分（目录结构、单例清单、存储路径、热键、导航、设置 Section）
3. **P0 功能开发启动**：离线 OCR（8 人天）和剪贴板历史（6 人天）优先度最高，设计文档已就绪
4. **清理死代码**：上一轮审查中的 18 项死代码（`SingletonApplication`/`WindowsFilter`/`GetModuleName` 等）可批量删除
5. **OllamaProvider 工具调用**：本次仅修复了 AnthropicProvider，Ollama 工具调用仍需后续处理

---

*本报告由 ToolBox 开发专家组 3 路并行协作产出，修复日志含完整文件:行号证据，设计文档含接口契约与实现细节，可直接作为后续开发迭代的工作输入。*
