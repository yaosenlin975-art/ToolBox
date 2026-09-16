# ToolBox 变更日志

本文件记录每次功能开发/修复的变更内容。

## [2026-09-16] 修复迷你窗口靠边自动隐藏交互

### 修复
- 仅贴靠屏幕左右边缘且鼠标离开后才启动隐藏倒计时；鼠标返回会立即取消倒计时。
- 隐藏时保留 20px 唤起条，修正左右隐藏位置、箭头方向及隐藏动画期间的唤回。
- 设置保存后立即应用自动隐藏开关；从迷你窗口打开设置时先隐藏迷你窗口。

## [2026-09-03] 修复 OCR 原生崩溃（AccessViolationException）

### 修复
- **OCR 引擎初始化必崩**：部分用户机器 `C:\Windows\System32` 存在被污染的老版 VC++ 运行时（msvcp140.dll/vcruntime140.dll = 14.00.24215.1，2016 年），OCR 原生库 tesseract55.dll（VS2022 编译）依赖解析被 System32 老版本劫持，标准库 ABI 不匹配导致 Tesseract 引擎初始化抛 AccessViolationException。修复两层：① 本机安装/替换为最新 VC++ 运行时（14.50.35719.0）；② 应用加固——新增 `Project/Natives/VCRuntime/` 存放 3 个官方签名运行时 DLL（msvcp140.dll、vcruntime140.dll、vcruntime140_1.dll），通过 csproj 的 None+Link 平铺部署到输出根目录，加载器优先级高于 System32，保证任何机器上原生 OCR 都能正常初始化
- **改动文件**：`Project/ToolBox.csproj`（新增 1 个 ItemGroup）、新增 `Project/Natives/VCRuntime/` 下 3 个 DLL

## [2026-09-03] 修复重复待办连环弹重复通知

### 修复
- **重复待办连环弹出重复通知**：`CronScheduler.ScheduleNextCore` 计算定时器延时后增加 `delay = TimeSpan.FromMilliseconds(Math.Ceiling(delay.TotalMilliseconds));`，将正延时向上取整到整毫秒。原因：`System.Threading.Timer` 的 dueTime 向下取整毫秒会提前 ≤1ms 触发，重复待办回调触发后按"下次发生时刻"续订，提前触发导致续订仍取到同一时刻，造成毫秒级连环重复触发（实测同一待办连弹 5 条通知）
- **影响范围**：所有 Cron 调度使用方（重复待办、定时提醒、自动截图、每日总结）统一消除提前触发

## [2026-09-02] 移除 LLM 聊天助手

### 变更
- **删除 LLM 助手聊天界面与核心**：移除 `Views/Chat/`（ChatView、MessageBubble、SessionSidebar、ToolCallCard）、`Core/Llm/` 中聊天专属类（Agent、ChatManager、ChatSession、ContextCompressor、DefaultSystemPromptBuilder、ISystemPromptBuilder、LlmTranslationService），以及 `Core/ContextAware/`、`Core/Memory/MemoryStore`、`Core/Personalization/PersonalizedPromptBuilder`、活动链 `SendToAiNode`。同步移除所有"发送到 AI/跳转 assistant"入口：工作台 `assistant` 页、侧边栏与迷你模式聊天 Tab、托盘"AI 助手"菜单、Dashboard 会话卡片/快捷入口、剪贴板/OCR/预览卡的"发送到 AI"按钮、全局热键 command=3
- **保留 LLM Provider 基础设施**：`Core/Providers/`、`Core/Llm/ChatMessage.cs`/`ChatChunk.cs` 保留，供"待办识别"（截图右键 → 识别待办）使用；设置页保留 Provider 配置区，仅删除聊天专属的"聊天字号"选项
- **保留工具系统**：`Core/Tools/`（ToolRegistry、FileTools、TodoTools、WebSearchTools 等静态能力封装）及 `Core/Security/FileAccessWhitelist.cs` 保留
- **翻译改为非 LLM 方案**：新增 `Core/Translation/TranslationService.cs`，调用免费公开在线翻译接口；OCR"翻译选中"、选字 OCR 翻译、活动链"翻译文本"节点改走该服务
- **回收通用转换器**：`BoolToVisibilityConverter` 从 Chat 目录移至 `Views/` 命名空间 `ToolBox.Views`（仍被剪贴板视图引用），App.xaml 资源引用同步更新

## [2026-08-27] 重复待办提醒时间编辑修复

### 修复
- **重复待办详情无法修改提醒时间**：`TodoDetailPanel.DetailRepeatSave_Click` 原用 `TimeSpan.TryParse` 解析"提醒时间"，全角冒号（如 `10：30`）、非法格式或越界值解析失败时静默回落到默认 09:00 并保存，保存后 `Populate()` 回显错误值导致用户输入丢失，表现为"改不了、改了不生效"。现改为：解析前规范化全角冒号；解析失败/超出 [00:00,24:00) 范围时输入框标红 + ToolTip 提示并中止保存（不再静默回落）；`Populate()` 重刷时恢复默认边框避免错误标红跨任务残留

## [2026-08-20] 任务详情统一 + 迷你窗口自动隐藏

### 新功能
- **迷你窗口自动隐藏** (CSH-47/46)：CompactToolboxWindow 支持自动隐藏功能，鼠标离开后淡出隐藏，移入或热键触发时恢复；设置页新增开关控制；多显示器边缘检测（`MonitorFromWindow` P/Invoke）+ HiddenStrip 触发条
- **共享 TodoDetailPanel** (CSH-46)：提取公共 `TodoDetailPanel` UserControl，统一大窗口（TodoView）和小窗口（TodoDetailWindow）的任务详情展示，两种模式现在展示完全一致的界面

### 修复
- **任务详情窗口顶栏全区域可拖动** (CSH-47)：`TodoDetailWindow` 标题栏 `Background=Transparent` 使整个顶栏可拖动，不再仅限标题文字位置
- **重复待办提醒时间编辑数据保留** (CSH-45)：编辑重复待办提醒时间时保留 `StartDate` 和 `LastResetAt` 字段，避免重置时间丢失
- **TodoDetailPanel 递归调用风险** (CSH-46)：新增 `Refresh()` 方法仅刷新当前项显示而不重新设置回调，`TodoDetailWindow.Populate()` 改为调用 `Refresh()` 而非 `ShowDetail()`，避免回调链递归

## [2026-08-05] 日常功能开发与修复

### 新功能
- **定时关机** (CSH-11)：设置界面新增定时关机区块，支持指定时刻或倒计时两种方式；关机前1分钟右下角弹窗提示，用户可取消关机
- **网络修复** (CSH-10)：按诊断结果中失败的检测项匹配修复动作（DNS/代理/网关/TCP），支持UAC提权执行
- **移除代码片段模块** (CSH-19)：彻底删除界面/数据/键盘钩子/热键/LLM工具引用
- **定时提醒功能** (CSH-28)：多条提醒 + 右下角弹窗通知
- **待办列表排序优化**：已完成待办项自动下沉至同级未完成项下方
- **待办详情联动** (CSH-16)：按重复频率下拉值联动显隐截止日期/完成进度与提醒时间/结束日期
- **LLM对话列表删除按钮** (CSH-20)：hover时右侧显示删除按钮
- **定时截图频率增强** (CSH-22)：支持选择具体周几/几号/日期

### 修复
- **ChatView与SessionSidebar样式统一** (CSH-31/32/33)：悬停/选中背景色、固定指示器、圆角间距统一
- **ToggleSwitch倒角改为0** (CSH-25)：消除椭圆形外观
- **子窗口非模态** (CSH-23)：待办添加/详情子窗口改为非模态，主窗口保持可点击
- **图标迁移** (CSH-15)：全面迁移Path几何绘制，不再依赖Segoe MDL2 Assets字体
- **Dark主题控件可读性** (CSH-15)：修复日期/频率控件、Todo详情面板表单标签对比度不足
- **截图历史滚轮响应** (CSH-26)：ListBox禁用内部垂直滚动，滚轮事件冒泡到外层ScrollViewer
- **迷你窗口主题残留** (CSH-13)：订阅ThemeChanged刷新待办+优先级徽章改用主题画刷
- **版本信息地址** (CSH-24)：GitHub项目地址改为实际地址
- **定时截图多屏支持** (CSH-5)：改用物理像素虚拟屏
- **TodoStore异步化** (CSH-5)：分类增删改async消除UI死锁
- **定时关机弹窗崩溃** (CSH-11)：修复第二次弹窗崩溃+语言切换状态文案不刷新
- **网络修复提权** (CSH-10)：DNS修复步骤移除/registerdns，仅保留免提权flushdns
- **LLM流式输出** (CSH-14)：Agent流式直出chunk+ChatView落盘LLM回复
- **右键菜单主题适配**：新增全局ContextMenu/MenuItem/Separator样式，清理托盘菜单硬编码样式

## [2026-08-07] 待办进度自动计算 + 迷你窗口子任务递归 + 提醒弹窗风格统一

### 新功能
- **待办进度自动计算** (CSH-37)：TodoStore 新增 RecalculateProgress 递归聚合（round(Σ 子任务.Progress / 子任务数量)），Complete/Uncomplete/Add/Trash/Restore/DeletePermanently 后自动重算父级；叶子完成自动置100/取消保持；UpdateAsync 禁止父节点手动写进度；TodoDetailWindow 与 TodoView 详情面板父节点滑块禁用+进度文本淡化+toggle 仅 Progress>=100 可点；ModernSlider 补禁用态透明度
- **迷你窗口子任务递归显示** (CSH-38)：CompactToolboxWindow 改为 AddCompactItemAndChildren 递归渲染，子任务 depth×16px 缩进；GetChildren 支持 pendingOnly 过滤已完成子任务；rightStack 顺序改为紧急标签→进度条→截止日期；HealthBar 模板改用 PART_Indicator 使进度填充实际渲染
- **迷你窗口父任务 toggle 禁用** (CSH-36)：主视图与迷你窗口 checkbox IsEnabled 增加 hasChildren 检查（有子任务且 Progress<100 时禁用），与 TodoDetailWindow 约束一致

### 修复
- **重复待办提醒弹窗风格统一** (CSH-35)：替换系统托盘气球为 TodoRepeatToast（设计令牌样式，右下角堆叠，10秒自动关闭），通知文案本地化

## [2026-08-07] 可重复待办并发安全加固

### 变更内容
- **TodoStore** 引入 `stateLock`（SemaphoreSlim）串行化所有 `items`/`categories` 的读改写操作，消除 Cron 后台线程与 UI 线程并发读写数据的竞态隐患。
- **锁范围**：`stateLock` 保护所有增删改查（Add/Complete/Uncomplete/Update/Delete/Trash/Restore/DeletePermanently/AddRepeating/ResetRepeating/CatchUpRepeating/SetRepeatConfig/AddCategory/RemoveCategory）；`writeLock` 保留仅保护 JSON 持久化写入。
- **递归方法改造**：`TrashAsync`/`RestoreAsync`/`DeletePermanentlyAsync` 拆分为"锁壳 + 无锁递归核心"（`TrashCoreAsync`/`RestoreCoreAsync`/`DeletePermanentlyCoreAsync`），避免 Semaphore 不可重入导致的死锁。
- **UI 刷新同步**：`TodoView` 的 `ItemsChanged` 订阅由 `Dispatcher.Invoke`（同步阻塞）改 `Dispatcher.BeginInvoke`（异步），避免后台线程锁内调用 `Dispatcher.Invoke` 与 UI 线程等待锁形成死锁。
- **托盘通知编组**：`RepeatReminderTriggered` 处理器从 Cron 后台线程触发，改为经 `Dispatcher.BeginInvoke` 编组到 UI 线程执行 `ShowBalloon`。
- **设计文档同步**：`Docs/recurring-todo-design.md` 边界情况表中并发说明更新为 `stateLock` 串行化 + `Dispatcher.BeginInvoke` 异步编组策略。

### 背景
Cron 定时器在后台线程触发 `ResetRepeatingAsync`/`CatchUpRepeatingAsync`，与 UI 线程的 `CompleteAsync` 等操作并发读写同一 `items` 集合。原 `writeLock` 仅保护持久化，数据读改写未加锁，存在竞态。启动兜底（`CatchUpRepeatingAsync`）虽能弥补漏重置，但彻底串行化数据操作可消除隐患。

## [2026-08-17] 待办模块 Bug 修复

### 修复
- **TodoDetailWindow 标题栏拖拽** (CSH-40)：无边框窗口标题栏 Border 添加 `MouseLeftButtonDown` 事件处理器 + `DragMove()` 逻辑，与项目中其他 7 个无边框窗口实现保持一致
- **子任务完成后父任务勾选框不可用** (CSH-41)：历史数据中已完成但 Progress<100 的叶子导致父节点均值计算永达不到 100；修复：`RecalculateProgressCore` 已完成子任务按 100 计 + `Load()` 启动时自愈重算 + 三处视图勾选框判定改为「无子任务或所有子任务已完成」
- **CloseDialogWindow 文字遮挡** (CSH-42)：移除 `BtnMinimize` 固定 `Width="130"`，改为内容自适应 + 显式 `Padding="16,8"`
- **待办类别分组 Expander 头部未显示类别名称** (CSH-44)：`DrawerExpander` 样式中 `ToggleButton` 增加 `Content="{TemplateBinding Header}"`、头部 `ContentPresenter` 增加 `ContentSource="Content"`、移除多余的 `<ContentPresenter/>`（影响 TodoView + CompactToolboxWindow）
