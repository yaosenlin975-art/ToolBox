# 项目文件夹看板实施计划

日期：2026-10-15。范围：project-board-design.md 方案 A，已获用户确认。

## 契约与顺序

Board 模型与文件服务 → IPC 路由与 CLI → WPF 工作台 → 集成验收与发布。每个入口面由独立 worker 负责；直接依赖的文件按交接顺序修改。

- 数据入口：`Project/Core/Boards/`。`ProjectBoardStore.Instance.ExecuteAsync(TaskRequest)` 返回现有 `TaskResponse`，`Changed` 事件通知界面刷新。内部 SemaphoreSlim 串行读改写；项目文件为权威数据，每次读取重新校验。
- 请求：现有协议 envelope；project/board 操作的参数全部放 payload 对象，Id/Status/Arguments 留空。字段使用 `project`、`cwd`、`id`、`revision`、`title`、`description`、`priority`(0–2)、`tags`、`dueDate`、`columnId`、`order`、`targetColumnId`、`path`、`init`、`replacePath`。
- 响应：project.list 返回登记数组；project.register/get 返回登记对象；board.get 返回整板；card.get/list 和 column.list 返回 `{card/cards/columns, revision}`；变更返回 `{card/column, revision}`；加入待办返回 `{todoId}`。稳定 BoardId 为项目 ID，路径大小写按 Windows 处理。
- UI：调用同一 Store 入口，读取模型快照，修改时提交最近 revision；Changed 通过 Dispatcher 刷新。操作失败显示具体错误。
- 不复制第三方代码或许可证，不增加依赖；保留个人 task CLI 行为。

## 实施项

- [x] 1. 项目数据入口与回归检查
  - 文件：Core/Boards 模型、Store、路径与命令处理；Tests/Boards。
  - 完成目录登记、初始化、取消登记、读写、列与卡片 CRUD/移动/软删恢复、个人 Todo 复制关联。
  - 校验未知字段、ID、revision、路径边界、损坏文件、并发、原子保存、重复 requestId、列转移和复制去重。
  - 普通目录、中文/空格路径均可用；外部文件错误不覆盖旧数据。
- [x] 2. IPC 与能力发现入口
  - 文件：TaskProtocol、AgentCapabilities、App.HandleTaskAsync。
  - 严格枚举新增 operation；后台启动复用原有 TaskIpc；路由到同一 Board Store。
- [x] 3. CLI 入口
  - 文件：Cli/Program.cs 及同目录命令构造文件。
  - 提供 project、board column、board card 子命令；显式参数和 JSON 输入、cwd 推断、结构化错误及退出码。
  - 不旁路写项目文件，全部通过 IPC。
- [x] 4. 工作台看板入口
  - 文件：Views/Boards、WorkbenchWindow、Shell/Sidebar、必要语言资源。
  - 项目登记/切换/取消登记、列管理、卡片编辑、排序与移动、软删除恢复、加入个人待办。
  - 使用现有主题控件，Light/Dark 均可用；支持列间与列内拖放，并提供菜单操作。
- [x] 5. 集成验收与交付
  - 独立验收 worker 核对设计、diff 与端到端 CLI/host/UI；仅用隔离临时目录和用户数据。
  - 验证未启动应用的 CLI、并发冲突、路径与数据安全、UI 同步及旧 task 命令兼容。
  - Release 构建并更新原发布目录；有锁时不停止用户程序，提供独立完整包。
  - 更新实际设计、CHANGELOG、人工 PENDING_TESTS、CLI 技能和工作日志；提交已验收改动。

## 去重与异常约定

requestId 在当前宿主最近 256 条请求窗口内去重；超窗或宿主重启后调用方先查询，不盲目重放。个人 Todo 复制用本机持久关联去重。项目目录/看板丢失、外部 JSON 损坏、只读文件及 revision 冲突返回现有结构化错误，失败不覆盖文件。

## 最终验收与交付

- 主 Debug 构建：0 错误；约 200 条既有警告。
- 协议 58 项、Core 回归及边界定点检查通过；CLI 端到端 234 项、真实 WPF/CLI 联动 14 项通过。
- 标签修复后定点 121 项通过，覆盖单次多值、重复开关和清空；未重复其他有效检查。
- Release 构建及发布：退出码 0，200 条既有警告；原 Project/bin/agent-cli-publish 已更新，exe/dll 哈希与本次产物一致，能力目录 31 项。
- CLI 技能已同步发布包及本机技能目录；文档 12 个本地链接和 18 项新操作引用检查通过。
- 验收使用临时目录及隔离用户数据。真实拖放、缩放及 Git 工作流人工项保留在 PENDING_TESTS。
- 日志：Project/bin/BoardAcceptance/debug-build.log、release-build.log、release-publish.log；集成脚本在 Project/Tests/BoardIntegration。
