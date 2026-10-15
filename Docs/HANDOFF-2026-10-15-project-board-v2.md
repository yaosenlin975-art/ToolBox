# ToolBox 项目看板 v2 交接

日期：2026-10-15。状态：用户要求暂停；恢复前不继续开发、测试、安装或发布。

## 入口与范围

- 仓库：`D:\Workspaces\ToolBox`，当前分支 main。代码在 Project/，文档在 Docs/，refs/ 只读。
- 设计：[project-board-v2-design.md](project-board-v2-design.md)，R1–R8 为完整范围，冲突以 §13 为准。
- 执行与历史证据：[project-board-v2-plan.md](project-board-v2-plan.md)。已确认的同一范围无需重复请求设计批准。
- 需求：项目目录内保存看板；CLI 供 Agent CRUD；未开始、进行中、待验收、已关闭；原子认领及租约避免执行其他 Agent 的事项；子树、图片、评论回复、收件箱和本机宿主投递。
- Multica：CSH-50，issue `01a13e47-5591-7cdf-9542-6a0bd6014fb8`；当前 in_progress，尚未提交 QA。workspace `7c978b4b-04d5-4e9c-b911-78ae2cb21aa1`，project `82b4b0bb-ebe7-4cad-936f-104aa87a90b5`。CLI 使用显式 workspace-id。

## 已提交基线

| Commit | 结果 |
| --- | --- |
| 2623685、42f44d4 | 技能/原子归属设计、设计审查及实施计划 |
| 9e39e63 | CLI/协议接入 v2 状态与协作操作 |
| 4057b0b、3a834e5 | 本机投递服务、共享 BoardCommandService 接线 |
| 61c407c | 四态、原子认领、租约、层级任务 |
| 03efc2e | 主题化看板和详情交互 |
| 5987ab3 | ack 终态、子进程身份隔离、本机宿主配置 |
| a2768b8 | 真实 IPC 分阶段脚本、WPF 渲染及 I/O 现场记录 |

## 保留的未提交工作

恢复时先读取 `git status --short`；不要 reset、clean 或覆盖以下文件。

| 工作线 | 文件 | 暂停时状态 |
| --- | --- | --- |
| S2 评论存储 | Project/Core/Boards/ProjectBoardModels.cs、ProjectBoardStore.cs、新增 ProjectBoardComments.cs；Project/Tests/Boards/Program.cs、ToolBox.ProjectBoardChecks.csproj | 评论、回复、收件租约/ack、双 revision、分页、自动外置及恢复已实现；原子保存重试及独立测试通过；未做最新主应用集成验收 |
| D4 宿主发现 | Project/Core/BoardAgents/BoardAgentService.cs；Project/Tests/BoardAgents/Program.cs | CodeBuddy 无扩展名 Node 入口已识别，27/27 检查通过；未提交 |
| U2 UI | Project/Views/Boards/BoardsView.cs、BoardCardDetailsWindow.cs；Project/Tests/BoardsUi/Program.cs | 租约刷新生命周期、语言切换重复挂载、根父项显示、暗色徽标、详情编辑/摘要已修改；独立 UI 检查已报告通过，真实 WPF 集成复验未做 |
| K1/接线 | 新增 Docs/skills/toolbox-board/SKILL.md；Docs/skills/cli-anything-toolbox/SKILL.md；Project/Cli/BoardCommands.cs、ToolBox.Cli.csproj；Project/Core/Todo/AgentCapabilities.cs；Project/ToolBox.csproj | 新技能草稿及旧技能指针、发布链接、2.1.0 版本、删列身份参数和投递输出元数据已写；未安装、未发布 |

上述工作未作为已交付版本验收。S3 图片存储尚未开始；CLI/UI 已有入口不代表存储操作可用。

## 最新验证与故障边界

### S2

- 构建：`dotnet build Project/Tests/Boards/ToolBox.ProjectBoardChecks.csproj -c Debug -p:UseAppHost=false -p:OutputPath=bin/BoardChecks/atomic-retry/`，0 警告、0 错误。
- 同目录 ToolBox.ProjectBoardChecks.dll 的默认检查及 `--atomic-retry`、`--comment-inline-stress`、`--state-tree`、`--comments`、`--targeted-fixes`、`--dedup` 均通过。压力为 40 次、每条 5,400 字符。
- File.Replace 对 Win32 32、33、1175 最多尝试 5 次，等待共 375ms；每次核对目标旧 hash、候选新 hash，外部改写返回 CONFLICT；1176/1177 不重试。board、registry、sidecar 和迁移回滚使用快照 hash。
- 受控共享锁验证短锁释放后仅一次写入、永久锁字节不变、等待期间外部改写保留。早先 DEBUG 压力记录过 `0x80070497`；原真实 CLI 第 22 次写入的特定现场未稳定复现，不能据局部检查宣称端到端故障已消除。
- 最初 Changed 断言没有记录首次写入响应，不能归因为事件竞态；断言已拆分。Changed 在释放 gate 后同步触发。

### D4 与 U2

- D4：`dotnet run --project Project/Tests/BoardAgents/BoardAgents.Tests.csproj -c Debug` 为 27/27；同工程 `dotnet build ... -c Debug -warnaserror` 为 0 警告、0 错误。
- CodeBuddy 只读真实发现探测成功；使用 node.exe 加精确入口参数，不执行 npm shim。无扩展名入口限定可信 Node 调用、node_modules、匹配入口名及 Node shebang。其余机器安装形态未验证。
- U2：独立 BoardsUi 检查报告通过，覆盖租约到期/计时器停止、语言重绘、根父项、编辑快照与双主题徽标；恢复后读取最终 diff，再安排受影响的真实 WPF 验证。

### C2 尚未完成

- 上次主 Debug 构建为 0 错误、约 200 个已有警告；22 个 CLI 文件 hash 一致，49 capabilities，state 阶段 222 检查通过。该构建早于最新 S2/U2/D4 修改。
- comments 曾在第 22 次长评论写入返回 IO_ERROR。首轮 Live WPF 完成 34 项后因详情语言切换失败；修复后未重跑。
- comments、attachments、all 尚未通过；未做 Release/发布/新技能安装，现有 agent-cli-publish 不能视为 v2 成品。没有调用真实 Agent resume 或向真实会话投递。
- I/O 新诊断快照：`C:\Users\Administrator\AppData\Local\Temp\ToolBox Board Comment I-O Diagnostic cc97f29ee8c64fb99327916532eed458`。22 次诊断成功不证明旧现场根治。
- 首轮中文 Light/Dark 截图：`C:\Users\Administrator\AppData\Local\Temp\ToolBox Board UI Check 73da99ec297845ff9d6e0fc00042df0a`；旧图包含已登记的徽标、父项与语言切换问题，不能作为修复后证据。

## 恢复顺序

1. 检查工作区与上述未提交差异；验收并提交 S2、D4、U2 已完成部分。复用仍有效的局部检查，当前主应用尚未覆盖这些新修改。
2. 实施 S3：图片真实解码、格式与 10MiB 限制、路径/hash、复制失败回滚、删除 journal 与崩溃恢复；删除卡片保留资产。删除意图在改名之前持久化，未知 hash 不自动清除。返回结构按设计与冻结接线契约。
3. 使用 `dotnet build Project/ToolBox.csproj -c Debug -p:OutputPath=bin/BoardAcceptance/debug/` 重建。不要修改 BaseOutputPath、IntermediateOutputPath 或 ProjectExtensionsPath。将嵌套 CLI 完整包复制至隔离宿主输出并逐文件核对 SHA256，避免 CLI/host 不匹配。
4. 运行 Project/Tests/BoardIntegration 下受影响阶段与最终 all；脚本使用 `pwsh -NoProfile -File`，不加 ExecutionPolicy Bypass。复验真实 WPF 中英/双主题并查看新截图。测试用临时数据根、假宿主，不运行真实会话恢复。
5. 按 R1–R8/§13 独立验收；对新技能做实际命令验证和 Agent 行为验证，再安装已授权宿主目录并比对内容。恢复时推荐 executing-plans、verification-before-completion、skill-creator；出现可复现故障时用 systematic-debugging。
6. 更新 Docs/design.md、DESIGNS.md、CHANGELOG.md、PENDING_TESTS.md、README、task-cli-usage（按实际影响），完成 Release 发布、版本/能力/技能包一致性检查和工作日志。
7. 完成自测后在 CSH-50 评论摘要/证据并 @qa-test-engineer（mention://agent/6b82caf1-818d-4a84-b54d-2af47dccbb16），置 in_review；done 仅由 QA 设置。

## 暂停清理

- 已通知正在运行的 UI 子代理停止开发、测试及提交；Core/D4 子代理已返回完成检查结果。
- 已按完整路径和 `--state-tree` 命令行确认并停止遗留测试 PID 130632。其他测试同名进程的路径/命令行不可读取，未按名称批量结束；恢复时仅处理能确认归属的进程。
- 本次仅提交交接文档及计划状态，保留实现改动供恢复验收。
