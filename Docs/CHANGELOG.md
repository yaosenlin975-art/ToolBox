# ToolBox 变更日志

本文件记录每次功能开发/修复的变更内容。

## [2026-10-16] 恢复 LLM 助手与 Agent 运行时

- 恢复助手入口：新增 `Project/AssistantWeb`（assistant-ui React 0.15.29 / React 19.2.0 / react-markdown 10.1.0 / remark-gfm 4.0.1，由 Vite 构建为随应用发布的静态 `dist`）；WPF 侧新增 `Microsoft.Web.WebView2` 依赖与 `Project/Views/Chat/ChatView.cs`（虚拟 origin `https://toolbox.assistant.local`，加载 `<AppBase>\AssistantWeb\dist\index.html`，桥接协议见 `Project/Views/Chat/AssistantWebProtocol.cs`）；同时新增默认折叠的侧栏导航项、工作台 `assistant` 页路由与禁用回落，以及设置页 AI 助手区块（开关、会话路径、白名单增删、隐私说明、清空按钮）。
- 恢复运行时与 CLI：`ToolBox.Core.Llm.AssistantRuntime.Instance`（懒加载、开关、会话运行队列、取消、generation 守卫、工具目录）与 `Core/Assistant/AssistantHost.cs` 静态宿主（`Initialize/SetEnabledAsync/IsEnabled/EnabledChanged/Tools`）；新增 `Core/Assistant/ToolsCommandService.cs` 提供 `tools.list/get/call/result/cancel` 作业（单帧 4 MiB、requestId 幂等、重启把在途作业标 `unknown` 不重放有副作用操作）；`Core/Todo/TaskProtocol.cs` 识别 `tools.*` 并校验 `tools.call` 参数，`App.xaml.cs` 接线并把 `tools.*` 分流到 `AssistantHost.Tools`。
- 工具与权限边界：`AssistantHost` 仅注册 `FileTools/ImageTools/TodoTools/WebSearchTools/BoardTools` 五类；`Core/Security/FileAccessWhitelist.cs` 统一白名单；新增 `Core/Tools/ImageTools.cs`（实解码、真实 MIME、≤10 MiB、像素上限，空白名单即拒绝）与 `Core/Tools/BoardTools.cs`（`board_list_cards/list_comments/claim_card/reply_comment/ack_comment`，执行身份由运行时注入，host 固定 `internal`）。
- 内部看板宿主：`internal` 成为第 9 个投递宿主（`BoardAgentService` 注入 `InternalEnabled/InternalExecutor`；新增 `Core/BoardAgents/InternalBoardAgent.cs` 负责门禁、同会话串行、租约截止、写前校验 claim、回帖 ack 崩溃恢复）；`Core/Boards/ProjectBoardComments.cs` 的 `CommentAdd` 新增 internal 有效 claim 门禁，其余宿主语义不变。
- Provider 与图片：`Core/Providers/{OpenAiProvider,AnthropicProvider,OllamaProvider}.cs` 支持按真实 MIME 发送图片，OpenAI 多个 `tool_calls` 按 index 累积；`ProviderManager.cs` 为 `gpt-4o` 置 `SupportsMultimodal=true`；不支持图片的模型返回可操作错误。
- 配置与隐私：`Models/SetunaOption.cs` 新增 `AssistantEnabled`（默认 false，`DeepCopy` 同步）；中英文语言包各新增 15 个键；会话数据落在 `%AppData%\ToolBox\chats.json` 与 `sessions\*.json`、记忆 `Data\memory.db`、白名单 `file_whitelist.json`、作业元数据同应用目录；清空流程先关闭新轮次、递增 generation、取消并等待在途，再串行删除。
- 前端 i18n 修正：`Project/Views/Chat/ChatView.cs` 中两处原本硬编码中文的用户可见文案（页面加载失败提示、链接校验异常）改为跟随 `IsEnglish()` 的双语形式。
- 新增验收工程：`Project/Tests/AssistantLive/`（`AssistantLive.csproj` + `Program.cs`），以 `ipc` / `wpf` 两种模式用真实 `toolbox-cli.exe` 与真实 `ChatView+WebView2` 做联合验收，数据经 `TOOLBOX_TASK_TEST_ROOT` 隔离。
- 验证：主程序 Debug 0 error / 194 warning（与历史基线一致），产物含 `ToolBox.exe`、`toolbox-cli.exe`、`AssistantWeb/dist/*`；看板回归 `board-v2-cli-integration.ps1` 640 项、`run-board-ui-live.ps1` 47 项、`BoardAgents.Tests` 35/35、`BoardsProtocolChecks` 261 项全通过；助手联合验收 IPC 模式 21 项（开关关闭返回 `ASSISTANT_DISABLED`、`tools.call` 快速返回 `{callId,status:"queued"}` 后经 `tools.result` 得终态、requestId 幂等与 `CONFLICT`、4 MiB 上限 `INVALID_ARGUMENT`、未知 callId `NOT_FOUND`）、WPF 模式 3 项通过，全程未读写真实 `%AppData%`、未发起真实模型请求。

## [2026-10-16] 项目看板 v2：架构级评审整改

- 死代码清理：删除全仓零调用的 `ProjectBoardStore.ProjectGetOrListRequest`、`BoardsView` 的死赋值与两处 `await Task.CompletedTask` 占位、`BoardAgentService` 恒等 no-op 局部变量与冗余 `with` 复制、`ProjectBoardComments` 冗余 revision 判据及其死参数，以及全仓无引用的语言键 `Lang_Board_DeliveryConfirmation`（中英同步删除）。
- 评审未成立予以保留（留证备查）：`OperationFields.ttlMinutes`、`board.card.claim` 不可达判据、`InboxPull` 死字段、`RequiresPathConfirmation`/`CandidatePath`、附件 journal 与回滚被忽略的返回值、`Revision`/`BoardRevision` 双字段，经核实为已测试契约或 §13 冻结契约字段，不改。
- 补齐两处设计未落实项：§6.2 四态四色（Light/Dark 新增 `SuccessSoftBrush`，`closed` 徽标改用，列表与详情树两处）；§12 投递策略补 UI 入口（详情窗口内联「总是自动发送（本机）」开关）；§5.6 第 5 步候选路径确认复活（`ConfirmHostPathAsync` 接通详情窗口内联按钮）。
- 性能优化：新增读路径快照缓存（键为规范化路径 + `LastWriteTimeUtc` + `Length`，写路径仍重算 hash 做 CAS）；`Unregister` 清理已知 hash 集合；评论外置判据复用已算出的序列化长度；`BoardsView` 卡片查找 O(n²) 改字典；子项计数合并为单次遍历；`LoadDeliveriesAsync` 无用户评论时短路；详情缩略图按 `(RelativePath, Sha256)` 缓存并关窗清空；`DiscoverAsync` 加 30 秒会话级缓存；`DrainErrorAsync` 加超时上界。
- 隐私整改：看板视图与详情窗口 4 处裸异常文案改为不暴露本地路径的通用文案 + `Debug.WriteLine`；`ShowOperationError` 新增 string 重载以保留已本地化提示。
- 编码规范整改：新增文件补文件头注释；using 重排为「项目/第三方 → System」分组；const 改全大写下划线；私有字段去 `_` 前缀（含跨 3 个 partial 文件同步改名）；本轮新增 void 方法改 `return this` 链式调用（事件处理器/`override`/接口契约/`private static` 除外）。
- 测试同步：`Tests/BoardsUi` 桩补齐投递策略与候选路径确认相关 API 及宿主/策略/确认路径记录，新增候选路径按钮可见性与投递开关断言；子项计数断言适配返回 `(Total, Closed)` 元组。
- 验证：主程序 Debug 0 error / 200 warning（与基线持平）；CLI 0 error / 0 warning；`Boards`、`BoardAgents` 27/27、`BoardsUi`、`BoardsProtocol` 234 全绿；CLI 集成 639；真实 WPF live 47/47。

## [2026-10-15] 项目看板 v2：四态协作、评论与图片附件

- 卡片新增四态（未开始/进行中/待验收/已关闭）与执行身份：`claim/release/submit/close/reopen` 原子认领与租约（默认 30 分钟，5–480），状态与列映射彼此独立；新增子事项树、认领与租约写入保护，CLI 同步扩展 18 个新操作。
- 评论与回复：评论随卡片存储并新增 commentRevision；回复按来源宿主/会话路由，收件箱领取与 ack。
- 评论自动外置：`board.json` 超 2 MiB 或单卡评论超 500 条时把评论迁至 `.toolbox/comments.json`，附一次性迁移 journal、旧 board 备份与回滚；迁移后评论单独原子更新。
- 图片附件：新增 `png/jpg/jpeg/gif/bmp` 附件（单张 ≤ 10 MiB），SHA256 去重、先写临时文件再原子改名；删除前写 journal，读取时支持崩溃恢复，卡片软删除保留图片。
- 宿主发现与评论投递：按 PATH/npm、URL 协议与卸载注册表、版本目录探测宿主；修复无扩展名 npm Node 入口识别（改用 node 直接调用入口，不执行 shim）；适配器配置与投递状态存本机 AppData，不写入项目目录。
- 看板界面：卡片详情渲染展示状态、子事项树、图片、评论与会话来源标签；可见期间刷新认领租约；修复语言切换后静态标签不刷新的问题。
- 技能与发布：新增 `toolbox-board` 技能（`Docs/skills/toolbox-board/SKILL.md`），旧技能看板章节收敛为指针；CLI/host 版本提升到 2.1.0，技能随发布包复制。
- 不兼容：v2 `board.json` 无法被旧版 ToolBox 读取（旧版按未知 schema 拒绝并保留原文件），降级前需备份；v1 数据在读取时内存迁移，下次成功写入保存 v2。

## [2026-10-15] 项目文件夹看板与 Agent CLI

- 增加工作台项目看板、目录登记、列管理、卡片编辑、拖放排序及回收站。
- 项目数据保存在 `.toolbox/board.json`，支持复制卡片到个人待办。
- 增加 project/board CLI、能力发现及单一后台写入入口。
- 同步 Agent 技能、使用说明与项目设计文档。

## [2026-10-14] 项目文件夹看板调研与设计

- 增加 GitHub 候选比较、项目目录数据模型、看板 CLI 草案及 ToolBox 集成设计。
- 记录目录内保存数据、项目看板与个人待办独立的已确认需求。

## [2026-10-10] 截图预览卡按图片比例自适应尺寸

- 取消预览卡固定宽高，窗口根据缩略图和按钮行的实际布局收缩，消除图片下方多余的棋盘空区。
- 保持整图等比显示、预览尺寸上限和按钮最小宽度；按实际窗口尺寸贴底定位，悬停缩放中心随卡片尺寸变化。

## [2026-10-10] 修复截图预览卡顶部空白与底部间距

- 修复入场动画目标路径错误造成的 80 DIP 残留位移，以及同类悬停动画失效，避免预览卡内容下移和被裁掉。
- 缩略图去除顶部内边距并顶部对齐，保持整图等比显示；取消窗口底部 16 DIP 偏移，使卡片贴合工作区底缘。

## [2026-10-09] 迷你窗口越界回靠与四边隐藏

- 修复窗口超出边缘超过原阈值后不再自动停靠的问题；静置后先对齐工作区，再衔接隐藏动画。
- 停靠扩展到上下左右四边，支持角落双轴越界纠正和对应方向握柄，移动时裁剪内容以避开任务栏。
- 统一双轴动画的取消与恢复，避免拖动、折叠或唤起时旧动画覆盖新位置；保留交互期间不自动收起的行为。

## [2026-10-08] 修复废弃任务后详情窗口未关闭

- 废弃或永久删除成功后，详情面板通知独立窗口关闭，并同步收起工作台内嵌详情，避免留下空窗体。
- 保留恢复任务后的详情显示；废弃或删除失败时不关闭详情。

## [2026-10-08] 增加项目 README

- 增加仓库入口说明，整理当前功能、运行与构建方式、Agent CLI 使用、数据与联网边界及文档导航。

## [2026-10-08] 全量代码审查与崩溃/数据丢失修复

- 补齐进程级未处理异常兜底（UI 线程异常不再终止应用，写入 `Logs/crash.log` 并给应用内提示），此前一次回调出错会连带带走贴图与未落盘待办。
- 修复启动即崩的一类问题：坏 PNG 阻断贴图恢复、默认 Cron 为 6 段而 Cronos 标准解析抛异常导致「打开定时截图后应用再也起不来」、取色器光标贴屏幕边角越界崩溃、截图历史右键缺少资源键崩溃、启动禁用热键后保存设置空引用。
- 修复多处静默数据丢失：过期清理误删 `Screenshots`/`Logs`/`clipboard_thumbnails`/`ocr` 等整目录、缩略图模式把缓存原图覆盖成 50×50（进入与退出两条路径都堵）、配置与提醒 JSON 非原子写、提醒读取失败后用空表覆盖磁盘、从工作台退出不走托盘退出流程导致贴图下次不恢复且托盘残留幽灵图标。
- 修复一批「设置写了不生效」的功能失效：热键钩子重复挂载导致开关型热键按了没反应、图层刷新无条件弹回置顶使「取消置顶」无效、设置页「测试连接」不发请求却恒报成功、贴图透明度设置因 `Application.MainWindow` 指向启动画面而永不生效、小窗 `settings` 态污染出空白窗口、睡眠唤醒后定时关机偷袭、一次性提醒唤醒重放。
- 网络诊断不再静默关闭系统代理（PRD R3/AC-05），代理项只报 Warning 并给引导；提权修复去掉写注册表关闭代理的路径。
- 剪贴板链消息转发改为带超时，避免被挂起的第三方程序冻住界面；缓存图片改为自己开流读完即关，同时修掉「改图后仍读到旧像素」的 URI 解码缓存问题。
- 动作链节点名统一：短名（ocr/copy/save/translate）与大小写混写都能解析，卡片标签复用工厂元数据，新建时对未知节点给出明确拒绝而非静默生成一条永不执行的链。
- `Docs/design.md` 全面对齐现状：移除已删除的 AI 助手叙述、修正数据目录（Setuna → ToolBox）、补 Agent CLI/稳定性兜底/未接线模块清单，并修复文档中 Cron 默认值里的 NUL 字符。

## [2026-10-08] 优化核心动效与靠边隐藏交互

- 靠边隐藏改为短延迟、可打断的收起/展开，使用窄握柄悬停或点击唤起，避免动画期间无法及时恢复。
- 修正物理像素与 WPF 坐标混用，增加拖动吸附、多屏接缝保护及显示环境变化后的窗口恢复。
- 补充页面、标签、待办折叠和主按钮的轻量反馈，遵循系统动画开关，保持现有主题与功能入口。

## [2026-10-08] 为现有 CLI 接入 cli-anything 技能

- 为 ToolBox CLI 增加可安装的 Agent 技能，说明能力发现、输入输出、真实调用流程及失败处理。
- 技能源集中存放于 Docs，并随 CLI 构建/发布输出交付，避免维护两份说明。
- 沿用现有 C# CLI 和已授权能力范围，不引入 Python 包装层或新增业务命令。

## [2026-10-08] 增加面向外部 Agent 的 CLI

- 集成 System.CommandLine 2.0.2，提供待办增删改查、完成/重开、回收站恢复及后台停止命令，随桌面应用一起发布。
- 支持 ToolBox 未打开时自动启动无窗口后台，后续打开界面复用同一实例。
- 补齐待办原子保存、失败回滚、严格输入和损坏数据错误，避免外部调用丢失数据或误报成功。
- 增加能力发现、指定本地图片 OCR 和截图标签分页查询，允许 Agent 获取已授权的文本/元数据。
- 修复 OCR 在 autoDownload=false 时仍下载语言包的问题；截图查询不返回内部绝对路径。

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
