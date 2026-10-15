# 项目看板 v2 实施计划

目标：落实 project-board-v2-design.md 的 R1–R8，以 §13 为统一实施契约。

约束：代码在 Project，文档在 Docs；refs 只读；复用 .NET 8/WPF/现有 CLI；不调用真实 Agent 投递、不修改真实项目看板数据。测试使用临时目录及 Debug 隔离宿主。父代理维护设计、计划、发布说明与工作日志；子代理不得递归委派。

## 依赖与文件归属

1. Core 模型与状态/树 → 评论/图片存储 → CLI/IPC/UI → 技能与发布验收。
2. 本机宿主发现与投递服务可独立实施；与存储通过精确评论上下文接线。
3. 同一文件同一时刻只有一个实现者；TaskProtocol/AgentCapabilities/BoardCommands 单独归 CLI 实现者；Core/Boards 归存储实现者；Core/BoardAgents 归投递实现者；Views/Boards 和语言资源归 UI 实现者。

## 执行项

- [x] 审查全部设计并统一状态、认领、投递确认、评论外置及附件恢复契约。
- [x] S1：Core/Boards 模型、v1 内存迁移、状态与租约、受影响事项写保护、任意深度树。定向及全量存储检查通过，新增删列认领检查通过。
- [ ] S2：Core/Boards 评论/回复、精确 routing、inbox 保留与 ack、双 revision/分页、自动 comments.json 外置及恢复。验证 500/501 与字节边界、hash/CAS/损坏恢复。
- [ ] S3：Core/Boards 图片添加/移除/预览上下文、实际解码、路径与 10MiB 限制、删除 journal。验证失败回滚、崩溃恢复、缺失/篡改标记。
- [x] D1：Core/BoardAgents 本机配置、8 宿主发现、类型化启动、确认/always、提交与 ack 分离、超时 unknown、失败与手工兜底。假宿主检查通过。
- [x] D2：共享 BoardCommandService 接线存储、投递与 ack；UI/IPC 共用入口；投递查询无发送副作用；已保存评论不能因投递失败被伪报失败。假 Store/假进程验证通过，真实 Store 接线由 C2 验证。
- [x] D3：处理确认优先于本机投递记录。Core 评论已 ack 时 delivery 返回 acknowledged，deliver 不再启动；无本机记录、needsConfirmation 或排队状态也能处理 pull ack，抑制尚未启动的队列。评论正文编辑清 ack/收件租约，新正文重新确认。假 Store/假进程回归通过。
- [x] D3 执行环境：外部宿主进程不继承启动 ToolBox 的 TOOLBOX_BOARD_ACTOR/CODEX_THREAD_ID/CODEX_SESSION_ID；目标会话仍由精确 argv 指定。假子进程验证保留 PATH 和无关 marker。
- [x] D3 本机配置：实现按宿主禁用、1–3600 秒超时设置与可编辑参数数组/消息传输配置；保留类型化 exe/Node 启动，限定模板 token，验证正文不会变为命令选项。设计 §5.6 已按实际 schema 更新。
- [ ] D4：修本机 CodeBuddy extensionless npm Node 入口发现；实际 shim 指向 node_modules/@tencent-ai/codebuddy-code/bin/codebuddy，首行为 Node shebang；当前仅 js/cjs/mjs 正则漏检。新增 fake 夹具与只读真实探测，不执行脚本 shim。
- [x] C1：TaskProtocol、AgentCapabilities、Cli/BoardCommands 接入 18 新操作与旧操作扩展；同步全部 schema/flag/错误码；协议与无副作用 CLI 检查通过。
- [ ] C2：CLI 隔离集成覆盖四态、树、评论、附件、投递及冲突；与完成后的 Store/投递服务对齐。
- [x] U1：Views/Boards 统一主题弹窗与内联校验；根卡徽标、详情子树/图片/评论/会话绑定/回复确认/投递状态。中英同步，保留 modal/drag 快照。独立 UI harness 通过，真实 Store/WPF 端到端由 C2 继续验证。
- [ ] U2：核对租约徽标在停留界面时的时效更新；无既有时钟刷新时补 loaded 期间的最小定时刷新，unloaded 停止。合并处理 C2 发现的 UI 实证缺陷，复验相关检查。
- [ ] K1：Docs/skills/toolbox-board/SKILL.md 定义发现、身份、领取、续租、CRUD、提交/验收、inbox/ack 和错误处理；旧技能看板章节改指针；CLI csproj 发布链接；安装已授权宿主目录并核对内容一致。
- [ ] V1：独立验收逐项核对 R1–R8 与 §13；复用当前有效检查，修复实证缺陷；Release 构建、发布 CLI/host/两技能并核对版本与能力表。
- [ ] V2：更新 Docs/design.md、CHANGELOG、PENDING_TESTS、README、task-cli-usage；记录真实未验证项；工作日志与提交；完成目标前逐项核对证据。

## 验收证据

每项完成后记录实际命令、结果与未验证部分。无真实宿主会话投递测试时，不能宣称已向 GUI 会话成功注入；假宿主验证仅证明适配器契约。视觉、DPI 与真实宿主升级后的兼容性列人工验收，不用局部测试推断全局通过。

## 当前进度

2026-10-15：完成三个独立只读设计审查，存储/投递/UI 的冲突已归并到设计 §13；进入 S1 与 D1 实施。

Multica：`CSH-50`（issue `01a13e47-5591-7cdf-9542-6a0bd6014fb8`），项目 `82b4b0bb-ebe7-4cad-936f-104aa87a90b5`，工作区 `7c978b4b-04d5-4e9c-b911-78ae2cb21aa1`；当前 in_progress，已留首条进度评论。CLI 需显式指定该 workspace-id；默认工作区 project list 返回资源不存在。完成自测后评论 @qa-test-engineer 再置 in_review，父代理不置 done。

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

### D1 证据

- `dotnet run --project Project/Tests/BoardAgents/BoardAgents.Tests.csproj`：13/13 通过。
- 覆盖默认确认、明确 always、幂等、失效重探、common-root 确认、npm shim 解析、无 session 拉取、崩溃 sending→unknown、超时及 runner I/O 异常。
- OpenCode/Qoder 用 stdin；ZCode 用绑定值参数；危险选项样式正文、换行、引号和中文不改变进程选项。
- 未调用真实宿主会话；主 App 在 D2 类未就绪时因缺 BoardCommandService 构建失败，等待接线后统一验证。

### S1 证据

- 状态/树定向检查通过：迁移、双 actor 竞争、续租/过期接管、move transition、close/reopen、1024 层子树、循环拒绝。
- 全量复验发现并修复 registry 独立 schema 仍须为 1、存储序列化选项不能用于 Todo 外部匿名响应读取；修正新卡优先 notStarted 映射列，不依赖当前首列位置。
- worker 使用自身隔离输出目录避开本轮遗留测试 apphost 锁；未处理用户 ToolBox 进程。
- `dotnet build Project/Tests/Boards/ToolBox.ProjectBoardChecks.csproj -c Debug -p:OutputPath=bin/BoardAcceptance/s1-final2/ -p:UseAppHost=false`：0 警告/0 错误；随后 DLL 的 `--state-tree` 与全量检查通过。
- 新增 column.delete 写保护与只移位置不改状态后，使用 `s1-column-delete` 输出目录重建并再次通过定向检查；`git diff --check -- Project/Core/Boards Project/Tests/Boards` 通过。
- 主程序构建仍需等待 D2/UI 共同就绪；本阶段构建观察到的 IOException/RootCards/MoveCardAsync 接线错误交对应实现者处理。

### D2 证据

- `dotnet run --project Project/Tests/BoardAgents/BoardAgents.Tests.csproj -c Debug`：19/19 通过；独立测试工程 `-warnaserror` 构建 0 警告/0 错误。
- delivery 无排队副作用；默认确认与快照 token、unknown 核实、requestId 指纹、已保存评论/ack 的服务失败 warning 均由假 Store/假进程验证。
- comment.delivery/deliver 响应为 `{delivery,revision,boardRevision,commentRevision}`，comment.add 保留原 data 并追加可选 delivery。
- App board 分支已改用共享 BoardCommandService；未调用真实 Agent，会话端到端和实际 S2 Store 接线待 C2。
- C2 已开始编写隔离集成用例，主程序需等 UI 编译接线就绪后统一构建；S2/S3 未实现前不将完整集成标记通过。

### U1 与 C2 准备证据

- `dotnet run --project Project/Tests/BoardsUi/BoardsUi.csproj`：Board UI checks passed；链接真实 Board 模型，服务使用 stubs。覆盖原 modal 快照、3000 层父链、根卡筛选、状态映射、确认取消不发送/不改剪贴板及资源检查。
- `git diff --check` 通过；`rg MessageBox Project/Views/Boards` 无匹配。真实 Store、图片与评论的端到端仍待 C2。
- C2 脚本已新增 `board-v2-cli-integration.ps1`（state/comments/attachments/all），all 不跳过未实现阶段；旧脚本同步 49 capabilities，live harness 增加主题/语言渲染与临时 PNG。
- C2 首次主构建 `dotnet build Project/ToolBox.csproj -c Debug -p:OutputPath=bin/BoardAcceptance/debug/` 失败（54 错误/100 警告）：ProjectBoardComments.cs 缺显式 System.IO。已路由 Core 修复；未复制 CLI、未启动 state/UI 测试。脚本解析与空白检查通过，运行尚未验收。

### S2 收尾证据与未决失败

- `dotnet build Project/Tests/Boards/ToolBox.ProjectBoardChecks.csproj -c Debug -p:UseAppHost=false -p:OutputPath=bin/BoardChecks/comments3/`：0 警告/0 错误；该 DLL `--comments` 通过，覆盖 500/501、恰好 2MiB、路由、收件竞争、权限、CAS 分页与恢复。
- 全量首次出现 request-dedup 的 Changed-handler 断言失败；立即重跑及另一诊断构建通过，尚未证明根因。已要求 S2 实现者定位准确断言和时序，再按实证修复；S2 尚未结案或提交，S3 暂未开始。
- Core 已补 Comments partial 的显式 System.IO，C2 可复验主构建与 state/comments 阶段。

### D3 与首轮真实 CLI 证据

- `dotnet build Project/Tests/BoardAgents/BoardAgents.Tests.csproj -c Debug -warnaserror`：0 警告/0 错误；`dotnet run --no-build --project Project/Tests/BoardAgents/BoardAgents.Tests.csproj -c Debug`：25/25。
- 临时 AppData 的真实 Windows 发现 probe：Codex/Claude/opencode/ZCode/Trae 可发现；Qoder/WorkBuddy 为待确认候选；CodeBuddy extensionless Node shim 漏检已登记 D4。未启动真实宿主。
- C2 主程序隔离构建 0 错误（约 200 个已有警告）；22 个 CLI 包文件逐 SHA256 一致；版本 2.1.0，49 capabilities；state 阶段 222 checks 通过。
- comments 真实 CLI 第 22 次长评论写入 IO_ERROR（此前 21 次成功），失败时 board 702,412 bytes、commentRevision 33、未外置、无 journal；重启同临时 root 后短/长写入通过。证据保留，已并入 Core 的 S2 I/O 诊断；不以重启成功判根治。附件阶段未运行。
