# ToolBox 面向 Agent 的 CLI 能力设计

日期：2026-10-08。状态：用户已批准 Todo CLI、能力发现、指定图片本地 OCR 和截图标签只读查询的实施，并允许这些结果返回调用 Agent；其他命令仍是建议。

## 1. 需求分析

目标是让本机同一 Windows 用户下的外部 Agent 能发现并调用 ToolBox 的实用能力。CLI 是调用方式，核心验收是调用者能判断能力是否可用、输入什么、结果是否成功、是否产生副作用，以及失败后能否重试。

沿用正在落地的 System.CommandLine 2.0.2、JSON 请求/响应、本地管道和唯一应用实例；不重启已删除的聊天 Agent，不把 ToolRegistry 全量暴露，也不新增 HTTP/MCP 服务。任务命令继续可用，`toolbox-cli` 名称可以承载其他命令族。

依据实际源码而非界面名称/旧注释盘点。静态 `[Tool]` 方法并不等于活跃可调用工具：旧 ChatView 已移除，当前未找到这些工具的统一活跃注册入口。

## 2. 候选能力及优先级

| 能力 | 既有代码和真实状态 | Agent 用途 | 建议与接入代价 |
|---|---|---|---|
| 待办 CRUD、完成、回收站 | `Core/Todo/TodoStore.cs`；本轮 CLI 实施中 | 记录任务、拆分子任务、更新进度 | 第一批，已确认；复用锁和原子保存 |
| 指定图片文件 OCR | `Services/Ocr/OcrService.cs:103`，返回 OcrResult 行文本/坐标/引擎；Tesseract + WindowsOCR | 提取本地截图/扫描件文字，避免 Agent 依赖网页 OCR | 第二批首选；显式输入文件、尺寸/资源限制、文件访问授权，缺语言包行为须先修正 |
| 截图标签查询/修改 | `Core/Tools/ScreenshotTools.cs`、`Core/Tags/ScreenshotTagStore.cs` | 按标签/评分查历史、组织截图 | 第二批；当前搜索只覆盖标签记录，不能宣称全文 OCR 搜索；须补分页/稳定 DTO/写入并发契约 |
| 精确屏幕区域截图 | `Views/MainWindow.cs:782` 已有 CopyFromScreen，但绑定窗口；非空 fname 分支没有保存逻辑 | Agent 获取当前桌面证据，交给视觉/OCR处理 | 第二批，经屏幕读取授权；提取小范围捕获核心，使用物理像素矩形，不弹拖选窗，不依赖旧 `/R:/P:` 生成文件 |
| 屏幕坐标取色 | `Core/Color/ColorPickerService.cs:69`、ColorTools.PickColor | 核对界面颜色/视觉状态 | 可跟随截图；校验虚拟桌面边界与 GetPixel 失败，不自动写取色历史 |
| 提醒管理 | `Core/Reminder/ReminderStore.cs` 与 ReminderScheduler | Agent 建立定时提醒/查询日程 | 后续；需明确后台是否弹通知、调度恢复和时区语义，避免 CLI 成功但提醒不生效 |
| 番茄钟/习惯记录 | `Core/Pomodoro/PomodoroService.cs`、`Core/Habits/HabitStore.cs` | 个人工作流记录、专注计时 | 按实际场景追加；涉及 Timer/通知与另外的持久化文件，不顺带接入 |
| 剪贴板历史查询 | `Core/Clipboard/ClipboardTools.cs`，当前返回短预览自然语言 | Agent 从用户复制的内容获取上下文 | 单独明确授权后开放；默认不返回历史全文、不自动开始剪贴板监听、不暴露全部历史 |
| 只读网络诊断 | `Views/NetworkDiagnosis/DiagnosisModels.cs` 的 DemoDiagnosisRunner.RunAsync 实际读取适配器/网关并执行 DNS/网络探测 | Agent 解释本机连接问题 | 有价值；名称含 Demo、旧注释称占位，但应以实际探测实现为准；从 Views 解耦结果 DTO、明确探测目标/超时/外网访问 |
| 翻译 | `Core/Translation/TranslationService.cs:27`，通过 HTTP 把文本送外部接口 | 无独立翻译工具的 Agent 可调用 | 后置；必须表明数据外发和服务端点，不包装为离线功能，不隐式用用户 LLM key |
| 动作链列表 | `Core/Tools/ActionChainTools.cs` / ActionChainStore | 查看用户已有自动化配置 | 可只读暴露定义；执行可能组合翻译、写文件、改剪贴板，不能直接开放任意链/节点 |
| 日期自然语言解析 | `Core/Todo/SmartDateParser.cs:87` | 将中文日期短语转候选时间 | 后置；当前只取首个匹配，组合日期+时间、相似短语优先级存在限制；先补语义验证，不作为确定性时间写入依据 |
| 二维码 | `Core/QrCode/QrCodeService.cs` 两个 Scan 方法均为返回 null 的 stub | 扫码 | 当前不能开放为已可用能力；真正实现时另做库选型 |
| 通用文件操作/网页搜索 | FileTools / WebSearchTools | Agent 通常已有原生文件/搜索工具 | 暂不新增重复能力；现有文件白名单存在目录前缀判断缺口，目录列表/存在检查没走白名单，禁止直接照搬为外部能力 |
| 系统关机、网络修复、任意动作链运行 | ShutdownScheduler、NetworkDiagnosis/RepairModels 等会执行真实系统命令/提权 | 少数运维场景 | 不列入默认 Agent 能力；须逐操作人工授权，不能用一个通用 `--yes` 自动越过界面确认 |

代码片段目前只有设计资料，未查到独立可复用的片段 Store/服务，不列作已实现候选。盘点主要依据静态源码；本轮固定图片 OCR 已运行通过，屏幕采集、网络探测等未逐项运行。

### 核查到的关键差距

- 核查时发现 OCR 的 `autoDownload:false` 只关闭下载进度回调，缺语言包仍下载。实施已将两个公共路径的下载调用放到 autoDownload=true 分支，并用缺包场景检查不生成下载文件。
- `CacheManager.Init()` 带迁移/过期截图清理副作用，Agent 只读查询不能直接调用它当作初始化。
- FileAccessWhitelist 的 `StartsWith` 未验证目录分隔边界；`FileTools.ListDirectory/FileExists` 也不检查白名单。不能将旧 `[Tool]` 反射入口直接当作安全 API。
- 现有网络诊断和修复确实执行探测/命令，不能因 Demo 名称判断为模拟，也不能因“诊断”名称就默认授权修复。

## 3. 方案选型

| 方案 | 取舍 | 决策 |
|---|---|---|
| 将所有 `[Tool]` 通过 `tool run <name>` 暴露 | 代码看似少，但旧工具输出/校验不一致、混有高副作用操作和失活入口 | 不采用 |
| 明确命令族 + 固定允许列表 + 现有服务适配 | 每项补少量 DTO/校验；调用语义和权限可审查，复用既有 host | 推荐 |
| 再建通用插件/RPC/MCP 平台 | 增加配置、生命周期、权限系统和多协议维护 | 当前没有需求证据，不做 |

命令解析继续使用本次已经选定的 [System.CommandLine 2.0.2](https://github.com/dotnet/command-line-api/releases/tag/v2.0.2)，MIT 分发条件沿用 task-cli-design.md 的核查结果，不新增能力库。OCR/截图使用工程现有 Tesseract/WindowsOCR/System.Drawing，真正纳入实施后仅核查其实际发布范围，不移植第三方完整工具箱。

## 4. 建议的 Agent 接口

先提供可发现性，再逐个增加经过确认的命令族。已实现能力发现、task、project、board、ocr file、screenshot search；项目看板接口见 project-board-design.md；capture/color 仍为后续建议：

```text
toolbox-cli capabilities list
toolbox-cli capabilities get ocr.file
toolbox-cli task ...                         # 保留已确认接口
toolbox-cli ocr file --input request.json
toolbox-cli screenshot search --input request.json
toolbox-cli screenshot capture --input request.json  # 未实现，待独立授权
toolbox-cli color sample --input request.json        # 未实现，待独立授权
```

`capabilities list/get` 返回协议版本、命令名、command 调用格式、description、inputSchema、输出字段说明，以及 effects 副作用说明。inputSchema 描述命令整体输入；command 标注位置参数，位置参数不混入 --input 的 JSON。只公布已实现的允许列表，未安装语言包等运行条件作为 availability/reason 返回；不罗列 API key、白名单路径或用户私有数据。该发现命令在本地 CLI 完成，不为查看帮助启动后台或发网络请求。

保持现有统一响应 `protocolVersion/requestId/ok/data/error` 和错误码；新命令走同一管道、显式 operation 白名单。返回纯 JSON，不把内部自然语言字符串再套一层 JSON，不自动恢复聊天/模型调用。

### 输入/输出结构

- OCR：`{path, language?}` → `{engine, text, lines:[{text,confidence,rect}]}`；默认语言 `chi_sim+eng`。只读取本次指定的绝对本地文件，拒绝网络路径、网络驱动器和重解析点；输入不超过 16 MiB、边长不超过 16384、总像素不超过 3200 万。禁用自动下载与自动翻译；无文字是正常结果，引擎不可用返回 `ENGINE_UNAVAILABLE`（退出码 4）。
- 截图搜索：`{tag?,minRating?,colorHue?,limit?,cursor?}` → `{items,nextCursor}`；只搜标签元数据，`minRating` 为 0–5、`colorHue` 为 0–359、`limit` 默认 20、范围 1–100。既有 Store 的 CacheKey 实际可能为绝对目录，公开 DTO 仅返回末级 17 位数字截图标识、tags/rating/notes/taggedAt；不返回原图或绝对缓存路径。按公开标识 ordinal 排序，cursor 为上一页末项标识；标识冲突或损坏数据明确失败，不默默返回空列表。
- 截图捕获：`{rect:{x,y,width,height}}` → `{artifactId,width,height,capturedAt}`；坐标为虚拟桌面物理像素，允许负的跨屏原点；尺寸须完整位于可捕获范围。不改剪贴板、不显示贴图、不调用 LLM。
- 取色：`{x,y}` → `{hex,rgb,hsl,x,y}`；无额外历史写入。
- 图片通过受限 artifactId 管理，允许取回应用生成的本次产物；与常规待办同样拒绝任意操作/路径穿越。CLI 结果不默认内嵌超大 base64，Agent 需要图片时显式取回。

首批 OCR 若要减少文件访问面，可改成 stdin 接收图片字节，由 CLI 读用户指定文件并发送受限二进制/编码负载；这会改变现有 4 MiB JSON 限制和协议，需要明确设计后才实施，不在当前 Todo 协议里暗加。

## 5. 数据和权限边界

同一用户本地 IPC 解决进程接入，不能等同内容已获外发许可：结果交给外部 Agent 后，Agent 可能将文本/图片送到它使用的模型。

待办范围已经确认。新增的指定文件读取、屏幕读取、剪贴板历史读取、截图产物保存/读取、联网翻译/诊断须在各自范围确认后才接入；不扩大现有文件白名单、不改变会话持久化位置、不绕过已有确认框。用户传入 `--yes` 本身不能代替尚未获得的隐私边界授权。

新增产物若采用 `%LOCALAPPDATA%/ToolBox/agent-artifacts/`，须一并确认保存位置、仅保存本次请求内容、过期策略及可读取范围；本设计建议按请求生成随机 ID，读取仅限登记产物，默认 24 小时过期，遇到应用未运行时在下一次显式产物操作清理。该目录尚未创建。

联网能力必须在发现信息中明确 network=true、数据用途和端点；“缺资源自动联网”不应藏在本地操作中。后台不应为了只读查询启用连续屏幕/剪贴板监听。

## 6. 关键流程和验收

1. Agent 读取 capabilities，选择一个已实现且可用的命令。
2. CLI 校验单次输入后请求 host；host 对 operation、字段、权限和运行环境再次检查。
3. 无 UI 的服务直接执行；依赖交互的操作明确拒绝并返回原因，不能弹出阻塞 Agent 的对话框。
4. 结果按明确 DTO 返回；只读操作不写用户历史；已发送写请求失败不自动重放。
5. 对新增模块使用临时数据/固定输入检查；OCR 用本地固定图片、禁网缺包场景验证，截图用明确矩形验证尺寸/DPI，不读取其他用户文件。

验收重点：能力列表仅含真实实现；每个命令都有可验证输入/输出；无隐式网络下载/翻译、无偷偷开始监听；JSON/退出码一致；敏感能力未获授权时不可调用；stub 不返回虚假成功。

## 7. 分期建议

- 当前已授权阶段：完成 Todo CLI、System.CommandLine 集成、后台生命周期及安全提交验证。
- 已批准扩展阶段：能力发现 + 指定图片本地 OCR + 截图标签只读查询。这组允许内容返回调用 Agent，OCR 禁止联网，不依赖新增交互界面。
- 再下一阶段：显式区域截图/取色，确认屏幕读取和产物保存范围后实现。
- 提醒、习惯/番茄钟、剪贴板、联网诊断按具体 Agent 场景选取，不一次性开放整个工具箱。

本文件提供可审查的扩展范围；未获确认的新命令不进入当前实现，已经授权的 Todo 工作继续完成。

## 8. cli-anything 技能适配

用户已要求将 cli-anything 用于当前 ToolBox。复用已实现的 C# CLI 和已确认的能力范围，补齐 Agent 的发现和调用指导，不增加业务接口或权限。

- 需求：Agent 能从技能找到真实可执行文件、读取 capabilities、提交 JSON 输入、处理结果和失败，不把 ToolBox 待办误认为 Multica issue。
- 选型：采用插件的源码映射、真实后台验证和 SKILL.md 交付流程；现有 System.CommandLine 替代模板里的 Click，原生 JSON 替代 --json，保留单次命令调用。无需新增 Python/REPL/PyPI 依赖，不复制插件实现代码。
- 文件：按项目文档约定，规范源位于 `Docs/skills/cli-anything-toolbox/SKILL.md`；CLI 工程将同一文件复制到构建/发布目录 `skills/cli-anything-toolbox/SKILL.md`，不维护两份源。
- 接口和数据：技能含 YAML name/description 及自包含调用规则；命令 schema 以 capabilities 运行输出为准。业务仍通过现有 requestId/protocolVersion/ok/data/error 管道契约，不新增通用工具调用入口。
- 流程：Agent 加载技能→定位完整发布目录→发现能力→按用户请求调用→核对退出码和 JSON→对未知写入结果先查询。技能安装到本机 Agent 的技能目录；不修改 PATH、不提权、不绕过 host 写 JSON。
- 交付边界：指定文件 OCR、标签元数据和 Todo 的既有授权不扩大；屏幕采集、剪贴板、联网诊断和系统修复仍未开放。模板中的预览、导出、undo/redo、默认 REPL 不声明为已实现能力。
