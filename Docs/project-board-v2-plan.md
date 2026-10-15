# 项目看板 v2 实施计划

目标：落实 project-board-v2-design.md 的 R1–R8，以 §13 为统一实施契约。

约束：代码在 Project，文档在 Docs；refs 只读；复用 .NET 8/WPF/现有 CLI；不调用真实 Agent 投递、不修改真实项目看板数据。测试使用临时目录及 Debug 隔离宿主。父代理维护设计、计划、发布说明与工作日志；子代理不得递归委派。

## 依赖与文件归属

1. Core 模型与状态/树 → 评论/图片存储 → CLI/IPC/UI → 技能与发布验收。
2. 本机宿主发现与投递服务可独立实施；与存储通过精确评论上下文接线。
3. 同一文件同一时刻只有一个实现者；TaskProtocol/AgentCapabilities/BoardCommands 单独归 CLI 实现者；Core/Boards 归存储实现者；Core/BoardAgents 归投递实现者；Views/Boards 和语言资源归 UI 实现者。

## 执行项

- [x] 审查全部设计并统一状态、认领、投递确认、评论外置及附件恢复契约。
- [ ] S1：Core/Boards 模型、v1 内存迁移、状态与租约、受影响事项写保护、任意深度树。扩展 Tests/Boards，验证双 actor 竞争、过期接管、返工、全部绕过入口和深链。
- [ ] S2：Core/Boards 评论/回复、精确 routing、inbox 保留与 ack、双 revision/分页、自动 comments.json 外置及恢复。验证 500/501 与字节边界、hash/CAS/损坏恢复。
- [ ] S3：Core/Boards 图片添加/移除/预览上下文、实际解码、路径与 10MiB 限制、删除 journal。验证失败回滚、崩溃恢复、缺失/篡改标记。
- [ ] D1：Core/BoardAgents 本机配置、8 宿主发现、类型化启动、确认/always、提交与 ack 分离、超时 unknown、失败与手工兜底。Tests/BoardAgents 使用假宿主验证 argv/stdin、路径失效、幂等和超时。
- [x] C1：TaskProtocol、AgentCapabilities、Cli/BoardCommands 接入 18 新操作与旧操作扩展；同步全部 schema/flag/错误码；协议与无副作用 CLI 检查通过。
- [ ] C2：CLI 隔离集成覆盖四态、树、评论、附件、投递及冲突；与完成后的 Store/投递服务对齐。
- [ ] U1：Views/Boards 统一主题弹窗与内联校验；根卡徽标、详情子树/图片/评论/会话绑定/回复确认/投递状态。中英同步，保留 modal/drag 快照。Tests/BoardsUi 与独立真实 WPF harness 验证关键动作和双主题资源。
- [ ] K1：Docs/skills/toolbox-board/SKILL.md 定义发现、身份、领取、续租、CRUD、提交/验收、inbox/ack 和错误处理；旧技能看板章节改指针；CLI csproj 发布链接；安装已授权宿主目录并核对内容一致。
- [ ] V1：独立验收逐项核对 R1–R8 与 §13；复用当前有效检查，修复实证缺陷；Release 构建、发布 CLI/host/两技能并核对版本与能力表。
- [ ] V2：更新 Docs/design.md、CHANGELOG、PENDING_TESTS、README、task-cli-usage；记录真实未验证项；工作日志与提交；完成目标前逐项核对证据。

## 验收证据

每项完成后记录实际命令、结果与未验证部分。无真实宿主会话投递测试时，不能宣称已向 GUI 会话成功注入；假宿主验证仅证明适配器契约。视觉、DPI 与真实宿主升级后的兼容性列人工验收，不用局部测试推断全局通过。

## 当前进度

2026-10-15：完成三个独立只读设计审查，存储/投递/UI 的冲突已归并到设计 §13；进入 S1 与 D1 实施。

### 已冻结接线契约

- 存储入口保持 `ProjectBoardStore.ExecuteAsync(TaskRequest)`，board.get 仍返回 ProjectBoard 直体；迁移提示放可选 `TaskResponse.Warnings`。
- 状态动作目标位置字段为 columnId/order；move 保留 targetColumnId/order，增加 transition 调用同一状态规则。card.list mine 携带 actor/host/session。
- 评论操作者始终为 actor/host/session；bind 目标另用 targetHost/targetSession，clear 与目标互斥。list/inbox 可带 commentRevision，ack 携带 claimId。
- comment.get 返回 typed `ProjectBoardCommentContext(Registration, CardId, Comment, BoardRevision, CommentRevision)`，投递接线据此构造独立 `BoardAgentCommentContext(CommentId, CardId, ProjectRoot, Host, Session, Body)`。
- `BoardAgentService.EnqueueAsync(context, confirmedHash, cancellationToken)` 快速保存并排队；`GetConfirmationHash` 包含项目、卡片、评论、正文与路由；`GetDeliveryAsync` 查状态，`MarkAcknowledgedAsync` 标记处理。默认静态 Instance 支持 Debug 临时数据根。
- UI/IPC 通过共享 BoardCommandService 调度存储与投递，UI 不另起进程投递。默认待确认；comment.delivery 返回确认摘要，deliver 必须明确 confirm 且 token 匹配（本机用户配置 always 除外）。
- K1 草稿及 CLI 发布链接已写入，quick_validate.py 通过；尚未安装，待实际命令集验证。

### C1 证据

- `dotnet run --project Project/Tests/BoardsProtocol/BoardsProtocolChecks.csproj -c Debug`：234 项通过（最后一次包含 boardRevision 元数据）。
- `dotnet build Project/Cli/ToolBox.Cli.csproj -c Debug`：0 警告/0 错误；该次后只补能力输出元数据，由上述协议工程重编译验证。
- claim help 返回 0；ttl=481、mine 缺 host、deliver 确认缺 token、未知参数返回 2/INVALID_ARGUMENT。未触发业务存储或真实宿主投递。
- 下一步：U1 已开始；S1 完成后继续 S2/S3，再统一 C2/主程序构建。
