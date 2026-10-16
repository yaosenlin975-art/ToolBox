# ToolBox 助手恢复交接

日期：2026-10-16。状态：用户要求完成交接后停止；后续“继续”已澄清为继续交接，不恢复开发。

## 恢复入口

- 仓库：`D:\Workspaces\ToolBox`；分支 `codex/restore-assistant`；本轮实现最新提交 `8496f8c`。先检查 git status，保留全部未提交修改。
- 设计与执行项：[恢复LLM助手与Agent运行时.md](恢复LLM助手与Agent运行时.md)。上轮证据：[LLM恢复审查与聊天UI选型.md](LLM恢复审查与聊天UI选型.md)。不使用旧看板交接文档推断当前代码状态。
- 用户已选自主选型并授权完善设计后实施。采用 assistant-ui + WebView2；用户消息气泡，Agent 正文无气泡且支持 Markdown；每轮工具执行过程可折叠，运行展开、完成折叠、失败可查看。
- 用户已明确批准保留采用范围内 LICENSE/NOTICE 和版权声明，并批准设计 §6.1 系统提示词、所列工具权限、图片/搜索外发及会话/记忆本机持久化边界。同一范围无需重复确认。
- Multica：`CSH-51`，issue `01a142bd-5ab7-7f1e-9100-cc1915326cfa`，in_progress，尚未送 QA。workspace `7c978b4b-04d5-4e9c-b911-78ae2cb21aa1`，project `82b4b0bb-ebe7-4cad-936f-104aa87a90b5`。CLI 显式指定 workspace-id。

## 已提交与验证

| Commit | 内容与有效证据 |
| --- | --- |
| b51cb51 | GitHub UI 选型与恢复文档审查 |
| 9e63b3e | 修订后的完整设计；后续批准、进度和小型契约补充尚有文档未提交修改 |
| 7dcc1f3 | 文件路径/链接边界、FileTools 查询授权、类型化参数/实例调用/取消；AssistantTools 50 项通过 |
| 1df7b60 | C# Agent、会话/SQLite 记忆、runId、ActorId/AsyncLocal、取消与清空；AssistantRuntime 47 项假 Provider 检查通过 |
| 687dac0 | 工具 CLI/IPC 作业、幂等与取消；AssistantCommands 33、BoardsProtocol 261、AssistantTools 54 项通过；CLI 构建 0 警告/0 错误 |
| 8496f8c | React 前端与 WPF ChatView/协议；生产构建、4 项组件行为测试、16 项协议检查通过；WPF ChatView 尚未主编译验证 |

定向命令：

```powershell
dotnet run --project Project/Tests/AssistantRuntime/AssistantRuntime.Tests.csproj -p:EnableWindowsTargeting=true -v:q
dotnet run --project Project/Tests/AssistantTools/AssistantTools.Checks.csproj
dotnet run --project Project/Tests/AssistantCommands/AssistantCommands.Checks.csproj
dotnet run --project Project/Tests/BoardsProtocol/BoardsProtocolChecks.csproj
dotnet build Project/Cli/ToolBox.Cli.csproj -c Debug
# 在 Project/AssistantWeb 中运行
npm.cmd run build
npm.cmd test -- --reporter=dot
# 仓库根目录
dotnet run --project Project/Tests/AssistantWeb/ProtocolTests.csproj
```

上述证据对应各工作线完成时的代码。暂停时 Runtime、Providers、BoardAgents、宿主又有未提交修改；不能将局部检查外推为当前主应用通过。

## 未提交修改归属与完成度

| 工作线 | 文件 | 当前状态 |
| --- | --- | --- |
| R4A 宿主/设置 | Project/App.xaml.cs；Models/SetunaOption.cs；Themes/Lang 两语言；Views/SettingsView、Shell/Sidebar、WorkbenchWindow；新增 Core/Assistant/AssistantHost.cs | 开关、配置/DeepCopy、白名单/清空入口、侧栏与 tools 路由已写部分；未完成主构建/实际验证 |
| R5A 图片/Provider | 新增 Core/Tools/ImageTools.cs；Core/Providers/OpenAiProvider.cs、AnthropicProvider.cs、OllamaProvider.cs | 图片实际解码/MIME 与多工具流适配施工中；未提交、未验证；BoardTools.cs 尚不存在 |
| R5B 内部宿主 | Core/BoardAgents/BoardAgentModels.cs、BoardAgentService.cs；Core/Llm/AssistantRuntime.cs | internal 回调接口/排队分支已写；Runtime 增加 Generation、IsCurrentSession 小型守卫；InternalBoardAgent.cs 尚不存在 |

没有完成 R4/R5 的独立测试或整体主应用构建；Project/ToolBox.csproj 暂停时尚未改，WebView2 NuGet 和前端资源复制接线尚未加入。新增 AssistantHost 当前只注册 FileTools、TodoTools、WebSearchTools，尚未注册图片/看板工具与内部宿主回调。

## 已冻结接口

- `ToolBox.Core.Llm.AssistantRuntime.Instance`：Enabled、SetEnabledAsync、GetSnapshot、HandleAsync、ConfigureTools、Changed。
- 命令 payload：message.send `{text}`；session.create 空对象；select/delete 使用 envelope sessionId；rename `{title}`；run.cancel sessionId 加可选 runId；sessions.clear 空对象。
- 快照：enabled/generation/sessions/activeSessionId/messages/runs；messages 含可选 runId，runs 含 tools。事件 snapshot、text.delta、tool.status、run.status、error 都有会话/轮次与 generation 归属。
- `GetSessionActorId(sessionId)` 返回 Guid；`CurrentExecutionSessionId` 是 AsyncLocal。Agent.cs:48 已调用 EnterExecutionSessionScope，不要重复修复“无调用点”的过时判断。
- `ToolsCommandService(Func<bool>, Func<ToolRegistry>, string? appDataDirectory=null)`；ExecuteAsync(TaskRequest, ct)、CancelAllAsync。关闭后 result/cancel 仍可用；CancelAllAsync 只发出取消，不等待系统确认框。CLI 的 `--input` 接文件路径或 `-` 标准输入，安全重试需相同 `--request-id`。
- 工具作业落盘只存 ID/指纹/状态等元数据，不存工具结果；重启映射 unknown，不自动重放。文件删除/移动在确认返回后检查取消；取消不回滚已完成副作用，确认框仍需用户关闭。
- `FileAccessWhitelist.IsAllowedWithin(path,root)` 已实现物理路径/链接解析后的项目根与白名单交集。
- 未提交的宿主接口：`BoardAgentService.ConfigureInternal(Func<bool>, Func<BoardAgentCommentContext,TimeSpan,CancellationToken,Task<BoardAgentProcessResult>>)`；Options 新增 InternalEnabled/InternalExecutor。
- 计划的 `InternalBoardAgent`：Instance、ExecuteAsync(context,timeout,ct)、CancelAll；计划的 `BoardTools()` 和 `(string projectRoot,string actorId,string sessionId)` 尚待实现。Host 首次开启时注册回调，关闭时禁新调用并取消内部轮次。

## 已核实但未闭环的问题

1. 内部 Agent 的实际执行、租约截止、受限文件工具、回帖/ack 恢复尚未实现。仅服务回调分支不代表内部宿主可用。原 BoardAgents 27/27 在该分支施工期间曾通过；新增 internal 特有行为未验收。
2. `ProjectBoardComments.CommentAdd` 允许正常跨 Agent 评论协作，只校验 revision/commentRevision。受限 BoardTools 写前 claim 检查及 boardRevision CAS 可防 claim 被别人修改，但租约自然到期不改 revision。恢复时需在 Store 执行临界区补足 internal 身份的有效 claim 门禁，保留其他宿主正常评论语义，并补定向测试；该修法尚未实施，不默认收紧全部评论权限。
3. Provider 的 ImagePath/MIME 与 OpenAI 多 tool_calls 修改尚未验证。默认 gpt-4o 的 SupportsMultimodal 现为 false；不能靠猜测自动启用图片，需明确模型元数据或报告不支持。
4. `AssistantHost.Initialize(bool)` 和开关取消、ChatView 的 ready/snapshot、tools 路由尚需实际联调；避免同步等待 Dispatcher/确认框造成死锁。
5. 未做 Release/发布，未更新最终 README、CLI 技能、Docs/design.md、DESIGNS.md、CHANGELOG/PENDING_TESTS；没有真实模型外发、真实内部回帖或完整回归证据。

## 前端与环境

- 已锁定 assistant-ui 0.15.29、React 19.2.0、react-markdown 10.1.0、remark-gfm 4.0.1。缓存 `D:\tools\npm-cache`，node/npm 已有，不改系统 PATH。
- 源码 Project/AssistantWeb；生成的 dist、node_modules 被忽略。生产 dist 仅 index.html、assets、THIRD-PARTY-NOTICES.txt，不包含 mock。发布入口必须是 `<AppBase>\AssistantWeb\dist\index.html`。
- WPF 类 `ToolBox.Views.Chat.ChatView`，虚拟 origin `https://toolbox.assistant.local`。选定 Microsoft.Web.WebView2 1.0.4258.31 已核实 NuGet 可得，但尚未写 csproj；本机已有 Runtime 155.0.4283.45。
- 父代理 IAB 实测了用户气泡、Agent 无泡正文、GFM 表格/列表/代码、完成过程展开、运行中展开/完成后折叠、Light/Dark。首屏 message scope/getSnapshot 错误已修并复看；暗色辅助文字提亮、贴底隐藏回底按钮也已目视复看。快速完成导致一次停止点击目标过期，不计作实际取消通过。
- 尚无真实 WPF 截图/目视或性能结果。Vite 构建有上游 use client 与约 697KB JS 包体提示；不能据构建成功承诺首次打开速度。

## 恢复顺序

1. 读设计、git status 与本交接，逐线接续未提交部分；保持文件归属，最多 3 子代理，不递归。已提交且未变的定向证据可复用。
2. 完成 R5A 工具/Provider 和 R5B 内部实际处理/原子门禁；完善独立测试。完成 R4A 的包、资源、设置、主机注册与 App 接线。
3. 主构建使用 `dotnet build Project/ToolBox.csproj -c Debug -p:OutputPath=bin/AssistantAcceptance/debug/`；不改 BaseOutputPath/IntermediateOutputPath/obj，不杀用户进程。核对嵌套 CLI 输出并完整复制匹配包、逐文件 hash，避免启动旧 CLI。
4. 真实 WPF/IPC 使用 DEBUG TOOLBOX_TASK_TEST_ROOT、临时白名单与假 Provider；不得读取/修改真实会话、项目任务或发真实模型请求。验收开关、关闭取消、清空不回写、54 capabilities、长作业、双主题中英和页面卸载恢复。
5. 检查原看板 8 宿主保持、新第 9 宿主及 UI stub/固定数量断言；执行受影响回归与最终集成。完成独立代码验收、发布与文档/工作日志。
6. CSH-51 评论改动及检查证据，@qa-test-engineer（mention://agent/6b82caf1-818d-4a84-b54d-2af47dccbb16）后置 in_review；done 仅 QA 设置。

推荐恢复技能：executing-plans、verification-before-completion；可复现故障用 systematic-debugging；浏览器检查用 browser-testing-with-devtools。保持 Ponytail 最小完整实现。

## 停止记录

- 三个仍运行的子代理均已中断，并通知停止开发/测试/提交；未启动新的收尾构建。
- 父启动的 Vite exec session 67818 已以 Ctrl+C 停止。清理测试页时浏览器 URL 策略拦截，关闭未确认；不要为清理绕过策略或重启服务。未发现命令行匹配本轮 Assistant/BoardAgents 测试的仍运行进程；未按进程名批量结束其他进程。
- 本次仅提交交接与设计进度文档；保留所有未完成实现。未创建活跃 goal，get_goal 返回 null，不调用暂停/完成目标工具。
