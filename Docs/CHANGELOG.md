# ToolBox 变更日志

本文件记录每次功能开发/修复的变更内容。

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