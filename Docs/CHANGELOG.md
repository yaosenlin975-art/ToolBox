# ToolBox 变更日志

本文件记录每次功能开发/修复的变更内容。

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

## [2026-XX-XX] 可重复待办并发安全加固

### 变更内容
- **TodoStore** 引入 `stateLock`（SemaphoreSlim）串行化所有 `items`/`categories` 的读改写操作，消除 Cron 后台线程与 UI 线程并发读写数据的竞态隐患。
- **锁范围**：`stateLock` 保护所有增删改查（Add/Complete/Uncomplete/Update/Delete/Trash/Restore/DeletePermanently/AddRepeating/ResetRepeating/CatchUpRepeating/SetRepeatConfig/AddCategory/RemoveCategory）；`writeLock` 保留仅保护 JSON 持久化写入。
- **递归方法改造**：`TrashAsync`/`RestoreAsync`/`DeletePermanentlyAsync` 拆分为"锁壳 + 无锁递归核心"（`TrashCoreAsync`/`RestoreCoreAsync`/`DeletePermanentlyCoreAsync`），避免 Semaphore 不可重入导致的死锁。
- **UI 刷新同步**：`TodoView` 的 `ItemsChanged` 订阅由 `Dispatcher.Invoke`（同步阻塞）改 `Dispatcher.BeginInvoke`（异步），避免后台线程锁内调用 `Dispatcher.Invoke` 与 UI 线程等待锁形成死锁。
- **托盘通知编组**：`RepeatReminderTriggered` 处理器从 Cron 后台线程触发，改为经 `Dispatcher.BeginInvoke` 编组到 UI 线程执行 `ShowBalloon`。
- **设计文档同步**：`Docs/recurring-todo-design.md` 边界情况表中并发说明更新为 `stateLock` 串行化 + `Dispatcher.BeginInvoke` 异步编组策略。

### 背景
Cron 定时器在后台线程触发 `ResetRepeatingAsync`/`CatchUpRepeatingAsync`，与 UI 线程的 `CompleteAsync` 等操作并发读写同一 `items` 集合。原 `writeLock` 仅保护持久化，数据读改写未加锁，存在竞态。启动兜底（`CatchUpRepeatingAsync`）虽能弥补漏重置，但彻底串行化数据操作可消除隐患。