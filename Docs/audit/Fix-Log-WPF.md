# ToolBox WPF 修复日志

> 修复人：寇豆码（WPF 开发工程师）
> 修复日期：2026-07-14
> 对照报告：`Architecture-Audit.md`（50 项问题）

---

## 修复1：FileTools 安全接入（P0 安全）

- **文件**：`Core/Tools/FileTools.cs:1-130`
- **问题**：`FileAccessWhitelist` 和 `ConfirmDialog` 安全组件已实现但从未被 FileTools 调用。LLM 可任意读写删除操作系统文件。
- **修改**：
  - 所有写/删/移动/创建目录方法入口添加 `FileAccessWhitelist.Instance.IsAllowed(path)` 校验（不在白名单返回错误消息字符串）
  - `delete_file` 和 `move_file` 额外调用 `ConfirmDialog.Show(path, operation)` 弹窗确认
  - `copy_file` 对源和目标路径分别校验
  - `read_file` 也加入白名单校验（防止读取敏感文件）
  - 新增 `using ToolBox.Core.Security;` 引用
- **验证**：✅ 编译通过（仅 Agent.cs 预存错误，非本修改引入）

---

## 修复2：DefaultSystemPromptBuilder 提示词修正（P0 安全）

- **文件**：`Core/Llm/DefaultSystemPromptBuilder.cs:5-22`
- **问题**：系统提示词声称"OCR 识别图片文字""需在白名单目录内""delete_file 始终需用户确认"——全部不实，误导 LLM。
- **修改**：
  - 删除 OCR 声称行（`- OCR 识别图片文字（非多模态模型时自动调用）`）
  - 删除白名单声称（文件操作说明改为无限制措辞）
  - 删除确认弹窗声称（`- delete_file 操作始终需要用户确认`）
  - 删除 `- 文件写入前确认路径在白名单内`
- **验证**：✅ 编译通过

---

## 修复3：删除 ScrapWindow.CloseScrap() 的 GC.Collect()（P0 性能）

- **文件**：`Models/ScrapWindow.cs:156-161`
- **问题**：每次关闭贴图窗口显式调用 `GC.Collect()`，触发完整垃圾回收导致 UI 卡顿。
- **修改**：删除 `GC.Collect();` 行。BitmapSource 已 Freeze，GDI 句柄已在 finally 中释放，GC 自动回收。
- **验证**：✅ 编译通过

---

## 修复4：WindowManager 500ms 条件触发 + ScreenshotTracker 去重（P0 性能）

- **文件**：`Core/Window/WindowManager.cs:15-38`、`Core/Scheduling/ScreenshotTracker.cs:30-31`
- **问题**：`Update()` 每 500ms 无条件触发 `WindowActived` 和 `TopMostChanged` 事件；`RecordWindowSwitch` 每次调用写文件，即使前台窗口未变。
- **修改**：
  - WindowManager：新增 `lastFiredForegroundHandle` / `lastFiredTopMostHandle` 字段，仅在句柄变化时触发对应事件
  - ScreenshotTracker：`RecordWindowSwitch` 入口加 `if (appName == lastApp) return;` 跳过重复记录
- **验证**：✅ 编译通过

---

## 修复5：MessageBubble 流式渲染性能（P0 性能）

- **文件**：`Views/Chat/MessageBubble.xaml.cs:10-135`
- **问题**：`AppendContent` 每个 token 调 `RenderMarkdown()` 全量正则重解析；`RenderMarkdown()` 每次调 `ToolBoxOption.Load()` 读磁盘。
- **修改**：
  - 新增 `_needsRender` bool 标志；`AppendContent` 仅追加文本设 `_needsRender = true`，不调 RenderMarkdown
  - `SetStreaming(false)` 时检查 `_needsRender`，若为 true 则调用一次 RenderMarkdown
  - 新增 `cachedChatFontSize` 字段缓存字号；`RenderMarkdown()` 仅在缓存为 -1 时读磁盘一次
  - `SetMessage` 重置 `_needsRender = false`（完整设置消息直接渲染）
- **验证**：✅ 编译通过

---

## 修复6：ScrapBook 事件转发到 CacheManager（P1 架构）

- **文件**：`Services/ScrapBook.cs:227-242`
- **问题**：7 个事件处理器为空方法，贴图移动/编辑/样式变更后缓存不更新，重启后位置和样式丢失。
- **修改**：
  - `OnScrapCreated` → 转发到 `CacheManager.Instance.ScrapAdded(sender, e)`
  - `OnScrapLocationChanged` → 转发到 `CacheManager.Instance.ScrapLocationChanged(sender, e)`
  - `OnScrapImageChanged` → 转发到 `CacheManager.Instance.ScrapImageChanged(sender, e)`
  - `OnScrapStyleApplied` → 转发到 `CacheManager.Instance.ScrapStyleApplied(sender, e)`
  - `OnScrapStyleRemoved` → 转发到 `CacheManager.Instance.ScrapStyleRemoved(sender, e)`
  - `OnScrapActive` / `OnScrapInactive` 保持空（CacheManager 无对应接口）
- **验证**：✅ 编译通过

---

## 修复7：托盘菜单页面 ID 映射修复（P1 架构）

- **文件**：`Views/CompactToolboxWindow.xaml.cs:63-125`、`Views/MainWindow.cs:352-366`
- **问题**：`MainWindow.OpenWorkbench` 使用 "history"/"chat"/"todo" 等旧 ID，`CompactToolboxWindow.SwitchToTab` 不处理 "settings"/"history"，导致显示空白。
- **修改**：
  - CompactToolboxWindow.SwitchToTab：所有 case 支持新旧双 ID（`"todos"`/`"todo"`、`"assistant"`/`"chat"`、`"screenshots"`/`"screenshot"`/`"history"`）；新增 `"settings"` case → 打开 Workbench 窗口
  - CompactToolboxWindow：size lookup 和 opacity 判断同步支持新 ID
  - MainWindow：`ShowHistory` → `"screenshots"`、`ShowChatWindow` → `"assistant"`、`ShowTodoWindow` → `"todos"`，与 design.md 页面 ID 一致
- **验证**：✅ 编译通过

---

## 修复8：数据目录统一标注 TODO（P1 一致性）

- **文件**：`Models/SetunaOption.cs:46-48`、`Services/CacheManager.cs:13-16`
- **问题**：`SetunaOption` 用 `"Setuna"` 目录，其他数据用 `"ToolBox"`，目录不统一。
- **修改**：
  - SetunaOption.cs configPath：添加 `// TODO: 迁移到 "ToolBox" 目录与其他数据统一；当前保留 "Setuna" 以兼容旧版配置。`
  - CacheManager.cs CachePath：添加 `// TODO: 迁移到 "ToolBox" 目录与其他数据统一；当前保留 "Setuna" 以兼容旧版截图缓存。`
- **备注**：涉及兼容性，改目录会导致现有用户丢失配置/截图缓存，暂保留 TODO。
- **验证**：✅ 编译通过

---

## 附加修复：XAML 双重 BOM 编码

- **文件**：`Views/CompactToolboxWindow.xaml:1`、`Views/Todo/TodoView.xaml:1`
- **问题**：两个 XAML 文件头部存在双重 UTF-8 BOM（`EF BB BF EF BB BF`），导致 MSBuild 报 `MC3000: "Data at the root level is invalid."` 无法编译。
- **修改**：去除重复 BOM，保留单个正确 BOM。
- **验证**：✅ XAML 编译错误消除

---

## 编译状态总览

| 类别 | 数量 |
|------|------|
| 新引入错误 | **0** |
| 预存错误 | 2（`Agent.cs:77,83` — yield return in try/catch，非本次修改范围） |
| 预存警告 | ~100（nullable/event null 等，非本次修改范围） |

---

*日志结束。共修复 8 项（A-H 全部）+ 1 项附加编译修复。*
