# ToolBox 待办外部 CLI 设计

日期：2026-10-08。状态：待用户确认；本次仅检查、调研和设计，尚未实现。

## 1. 需求分析与检查结论

用户已确认：task 指 ToolBox 待办（Todo），不是 Multica issue；应用未启动时也要可操作；允许 CLI 自动启动不弹窗口、持续运行的后台实例。

成功标准：外部脚本能创建、列出、读取、修改和删除待办；得到稳定 JSON 和退出码；界面已打开时同步更新，未打开时不弹窗口；任何时候只有一个进程拥有待办状态并写入原数据文件。

### 已有能力与缺口

| 代码证据 | 当前行为 | 可复用性 |
|---|---|---|
| `Project/ToolBox.csproj` | 唯一工程是 WPF WinExe，实际目标为 `net8.0-windows10.0.19041.0` | 保留桌面入口；新增 Console 客户端须加入原 `Project/ToolBox.slnx` |
| `Project/App.xaml.cs:42`、`:52`、`:150` | 先创建完整界面，最后处理参数；已有实例时直接退出，无参数转发 | 须调整启动分流与单实例转发 |
| `Project/Views/MainWindow.cs:717` | `/R:` 截图范围、`/P:` 文件路径、`/C:TODO` 打开待办窗口 | 存在命令行入口，但没有待办 CRUD CLI |
| `Project/Core/Tools/TodoTools.cs` | 存在 `add_todo/list_todos/complete_todo/delete_todo/update_todo` 静态方法 | 属于内部工具封装，返回人类文本，不能直接作为外部协议 |
| `Project/Core/Todo/TodoStore.cs:101`、`:498`、`:529`、`:565` | 创建、更新、回收站删除、恢复及父子进度处理 | 复用业务实现，不复制到 CLI |
| `Project/Core/Todo/TodoStore.cs:29`、`:673`、`:697` | `%APPDATA%/ToolBox/todos.json`，启动时加载内存，进程内 SemaphoreSlim，整文件覆盖保存 | 不允许另一个进程直接读改写该文件 |
| `Project/Views/Todo/TodoView.xaml.cs:18`、`Project/Views/CompactToolboxWindow.xaml.cs:55` | 订阅 `ItemsChanged`，通过 Dispatcher 刷新 | 沿用通知链 |

检查范围：应用启动、全部 C# 命令参数/IPC 命中、工程配置、Todo 模型/Store/工具封装及直接 UI 调用者、现有待办设计。没有运行会更改真实待办的命令。当前仓库没有找到外部 CRUD 可执行程序、命名管道或 HTTP 服务。`TodoTools` 方法仍在，但当前源码未找到注册它的活跃入口，不能宣称现有 AI 调用链可用。

现有总设计中的依赖版本与 csproj 不一致，本设计以实际工程为准；本次不修改该文档。

## 2. 方案脑暴与推荐

| 方案 | 优点 | 成本和取舍 | 结论 |
|---|---|---|---|
| 独立 CLI 直接写 JSON | 表面改动少、执行即退出 | 运行中的 UI 缓存会覆盖 CLI 结果；补齐跨进程锁、重新加载和调度同步反而扩大改动 | 不采用 |
| Console CLI + 原应用后台模式 + 本地命名管道 | 同一 Store、同一缓存和调度；支持无界面启动；无需网络端口 | 要拆分应用初始化、支持唤出界面和结构化响应 | 推荐 |
| 常驻 HTTP 服务或迁移 SQLite | 适合将来多客户端/远程访问 | 引入服务生命周期、认证或数据迁移；仅靠 SQLite 也不能解决 UI 缓存同步 | 当前不采用 |

最小实现采用 Windows/.NET 自带 `System.IO.Pipes`、`System.Text.Json`、`ProcessStartInfo.ArgumentList`。CLI 以固定命令加 JSON 输入服务脚本，不引入完整 CLI 框架、DI 容器、通用 RPC、MCP 或新的数据层。

### GitHub 商用开源选型

调研日期：2026-10-08；版本是本次核查的固定版本，不表示最新版本。仅考察命令解析范围，不引入模型、媒体或示例素材。

| 候选与固定版本 | 匹配功能和代码证据 | 兼容性、许可与商用条件 | 移植范围/成本与验证状态 |
|---|---|---|---|
| [System.CommandLine v2.0.2](https://github.com/dotnet/command-line-api/releases/tag/v2.0.2) | 已读目标 tag 的 `Command.cs`、`Option.cs`：子命令、类型化选项、验证器、解析及异步 action | csproj 包含 netstandard2.0，可作为 .NET 8 候选；[MIT 原文](https://github.com/dotnet/command-line-api/blob/v2.0.2/LICENSE.md) 允许商用/修改/闭源分发，须随分发保留版权及许可声明 | 只引用 NuGet 包并编写命令映射，不移植业务代码；预计解析适配约半天，不含 host；已查源码片段/工程/许可，未 restore、构建或运行 |
| [Spectre.Console.Cli 0.49.1](https://github.com/spectreconsole/spectre.console/releases/tag/0.49.1) | 已读 `CommandApp.cs`：命令配置、异步执行与异常处理；支持更丰富终端体验 | csproj 明确含 net8.0；[MIT 原文](https://github.com/spectreconsole/spectre.console/blob/0.49.1/LICENSE.md) 要求保留版权及许可；该版本 CLI 引用 Spectre.Console，后者还包含 Wcwidth.Sources 源码依赖 | 要适配默认终端错误渲染，使输出符合 JSON 契约；预计半天至一天，不含 host；已查相关源码/工程/主许可，Wcwidth 及嵌入字体许可未完成核验，分发前须补齐 |
| .NET 8 平台能力，无新增包 | 固定语法由小范围 switch 解析，JSON 由现有标准库处理；原 Store 处理业务 | 不移植第三方 CLI 库源码；沿用现有 .NET 发布和许可材料 | 最小依赖范围；平台 [PipeOptions 源码](https://github.com/dotnet/runtime/blob/v8.0.0/src/libraries/System.IO.Pipes/src/System/IO/Pipes/PipeOptions.cs) 已核实具有 CurrentUserOnly；尚未做 IPC 集成验证 |

System.CommandLine 的目标项目仅为 netstandard2.0 条件声明 System.Memory 包依赖；发布时应以实际 NuGet 资产和锁定依赖为准，当前没有做完整传递依赖审计。Spectre 采用固定旧版本仅作为可核查比较基线，不推荐为本需求引入其终端渲染依赖。

推荐不移植候选库：本需求面向外部脚本，固定语法加 JSON 足够；若确认需要大量 `--title/--tag/--due` 参数、自动补全或更多命令族，再优先选 System.CommandLine。以上成本是设计估算，不是已验证工期。

## 3. 架构及启动流程

```text
外部脚本 → toolbox-cli.exe → 当前用户命名管道
                                  ↓
                         ToolBox.exe 唯一实例
                         Todo 命令适配 → TodoStore → 原 todos.json
                                  ↓ ItemsChanged
                            已打开的待办界面
```

1. CLI 先解析和校验本地输入；`--help/--version`、语法错误不启动应用。
2. 连接管道 `ToolBox.Tasks.v1.<当前用户SID>`；仅本机当前用户，服务端和客户端使用 `PipeOptions.CurrentUserOnly`，不接受用户指定远端服务器。
3. 管道不存在时，通过 CLI 所在发布目录中的固定 `ToolBox.exe --task-host` 启动后台模式，`UseShellExecute=false`、无可见终端；不从 PATH 搜索应用、不拼 shell 命令。
4. 沿用现有全局单实例 Mutex 争夺唯一实例；多个 CLI 冷启动最多产生一个存活 host，其他启动者退出，客户端等待同一管道就绪。Mutex 占用但管道长期未就绪时返回不可用，禁止绕过锁直接写文件。
5. `--task-host` 只初始化 WPF Dispatcher、语言/必要资源、TodoStore、重复待办补算/调度和管道。不得创建 Splash/MainWindow/CompactToolbox/Workbench，不启动截图调度、OCR、剪贴板、热键或托盘。后台重复待办可更新状态，但不弹提醒窗口。
6. host 一直保留至用户退出应用、注销或显式 `toolbox-cli host stop`；不安装 Windows 服务、不注册开机启动。`host stop` 仅允许后台模式，等待当前请求持久化后停机；界面已打开则拒绝，让用户从现有退出入口关闭。
7. 用户正常双击 ToolBox 时，若已有后台实例，新进程通过内部 `app.activate` 请求让原实例一次性初始化并显示完整界面，随后退出。并发激活只创建一组窗口；已是 UI 模式则唤出已有界面。
8. 正常 UI 启动也提供同一管道；旧截图参数仍按原逻辑处理，已有实例时转发白名单参数；不得将未知参数交给 shell。

现有 MainWindow 构造会注册热键和托盘，因此不能简单“创建窗口后隐藏”。须把 `App.OnStartup` 的 Todo 生命周期与完整桌面初始化分开，以复用现有创建窗口逻辑。后台转 UI 不重复注册 Todo 定时器/事件。

范围限制：v1 沿用现有全局 Mutex，不顺带解决多 Windows 账号同时运行的问题；管道按用户隔离，其他账号占用 Mutex 时报告不可用。提权等级不匹配/拒绝访问直接报错，不自动提权。已运行旧版 ToolBox 占有 Mutex 而没有管道时，提示退出旧版并重开。

## 4. CLI 接口设计

发布 `toolbox-cli.exe`，Console 子工程放 `Project/Cli/` 并加入原 solution；桌面工程保持 WinExe。脚本用绝对路径或用户自行配置 PATH，不修改系统 PATH。

```powershell
toolbox-cli.exe task create --input create.json
toolbox-cli.exe task list
toolbox-cli.exe task list --status pending
toolbox-cli.exe task get 012345abcdef
toolbox-cli.exe task update 012345abcdef --input update.json
toolbox-cli.exe task complete 012345abcdef
toolbox-cli.exe task reopen 012345abcdef
toolbox-cli.exe task delete 012345abcdef
toolbox-cli.exe task restore 012345abcdef
toolbox-cli.exe host stop
```

`--input -` 从 stdin 读取单个 UTF-8 JSON 对象；文件输入也只由 CLI 读取，不把文件路径发给 host。输入不得同时来自文件和 stdin。所有业务结果默认输出单个 JSON 对象到 stdout，无颜色、进度或启动信息；诊断写 stderr。帮助/版本输出可为纯文本。

| 命令 | 语义 |
|---|---|
| create | 创建普通待办，返回完整 Task DTO；复用 AddAsync |
| list | 扁平列表，保留 parentId；status 支持 all/pending/completed/trashed/repeating，默认 all 排除废弃项；固定按 createdAt、id 升序 |
| get | 精确 ID 查询，可读回收站项；不存在返回 NOT_FOUND，不支持前缀/模糊 ID |
| update | 仅更新提交的字段；无有效字段/未知字段均报错；废弃项须先恢复 |
| complete/reopen | 复用 CompleteAsync/UncompleteAsync；叶子完成进度为 100，取消完成保留现有进度；父项完成前须全部有效子项完成，与详情面板约束一致 |
| delete | 复用 TrashAsync，移入回收站并级联子项；重复调用不重复处理、不永久删除 |
| restore | 复用 RestoreAsync，恢复该项及子树；若祖先仍在回收站，返回 CONFLICT，先恢复祖先 |
| host stop | 后台实例退出；实例不存在直接成功，不为了停止而启动 host |

首版不提供永久删除、批处理事务、分类管理、任务移动/换父节点、重复配置写入、会话关联写入及远程访问。可读取现有重复项并修改普通字段，其删除/恢复沿用提醒注册与注销规则。

## 5. 数据结构和校验

create 输入字段：`title` 必填且 trim 后非空；`description` 默认空串；`priority` 为 0/1/2；`category` 默认“默认”且非空；`tags` 为字符串数组；`dueDate` 为带时区 ISO 8601 时间或 null；`parentId` 可选，须指向未废弃的存在项。分类沿用 Store 的自动创建行为。

update 只接受 `title/description/priority/category/tags/dueDate/progress`。省略保持原值；`description:""` 清空描述，`tags:[]` 清空标签，`dueDate:null` 清空截止日期；其他字段不接受 null。`progress` 为整数 0–100；父节点进度禁止手填。日期统一转 UTC 保存，输出采用 ISO 8601 UTC；不隐式从标题或自然语言推导日期。

Task DTO 使用 camelCase，明确列出 `id,parentId,title,description,isCompleted,priority,category,tags,dueDate,progress,createdAt,completedAt,isTrashed,isRepeating,repeatConfig,lastResetAt`。repeatConfig 只读，字段映射自既有模型，枚举为 daily/weekly/monthly/yearly，时间统一 UTC；不输出 sessionId 或 UI 派生属性。每次在 Store 锁内拷贝 DTO，不能返回可变 TodoItem 引用。

请求和响应例：

```json
{"protocolVersion":1,"requestId":"GUID","operation":"task.update","id":"012345abcdef","payload":{"description":"","dueDate":null}}
```

```json
{"protocolVersion":1,"requestId":"GUID","ok":true,"data":{"id":"012345abcdef","description":"","dueDate":null},"error":null}
```

以上响应 data 仅展示字段片段；实际写入操作返回完整 Task DTO，list 返回数组，host stop 的 data 为 null。失败格式固定为 `ok:false,data:null,error:{code,message}`，验证失败可附 `error.field`，不返回堆栈或真实数据路径。

退出码：0 成功；2 参数/字段不合法；3 NOT_FOUND；4 CONFLICT；5 HOST_UNAVAILABLE/TIMEOUT/ACCESS_DENIED；6 IO_ERROR/DATA_CORRUPT；7 PROTOCOL_ERROR/INTERNAL_ERROR。未知操作/字段/版本都拒绝执行。

IPC 用 UTF-8 单行 JSON 加换行，Byte 模式；每连接一请求一响应，最大请求/响应各 4 MiB，超限报错，禁止无限 ReadLine。输入 JSON 中换行由序列化器转义；list 超限明确返回 RESULT_TOO_LARGE（退出码 4），不截断列表冒充成功。启动等待上限 10 秒，单请求等待上限 30 秒，客户端取消立即断连；服务端对空连接/慢客户端设读取超时。

requestId 只用于关联诊断，不承诺幂等重放。写请求已发出后超时/断连，返回“结果未知，先查询确认”，不自动重试、不宣称未执行；尤其 create 不得重放。仅未发送请求时可重试连接。

## 6. 关键流程、并发与持久化

命令适配层只允许上述白名单操作，调用 TodoStore，不通过返回自然语言的 TodoTools，也不暴露 ToolRegistry 中的其他能力。客户端和服务端都校验，服务端结果为准。

管道按请求顺序派发到 Dispatcher 并异步 await，禁止 `.Wait()`/`.Result` 阻塞 UI；异步 Dispatcher 不保证整段请求天然互斥，host 还需一个请求串行门。查询和单次验证+变更须在现有 `stateLock` 内完成，包含读取 DTO，防止 Cron/UI 写入穿插；不得在锁内调用再次获取同一锁的公开 Store 方法，采用已有 core 方法模式，避免递归加锁。

必要 Store 补齐范围：

- 新增锁内快照读取和精确查询，确保 UI、Cron 和 CLI 并发时输出一致。
- 更新增加“是否提交 dueDate”的存在标记，解决现有 `DateTime?` 只能忽略 null、不能清空的问题；旧调用者默认行为保持不变。
- 对 CLI 的父项状态、回收站及父节点进度约束，在 Store 锁内校验并返回可区分结果，不将“拒绝更新”伪装成成功。
- Load 的解析错误不得继续以空数据接收写请求；区分不存在文件与损坏/无权限。损坏时关闭写入口并报告 DATA_CORRUPT，禁止覆盖原文件。
- Save 改为同目录临时文件完整写入、flush 后原子替换目标；首次保存用原子移动。只在提交成功后响应成功，失败不得损坏此前可读文件。
- 当前方法先改内存再保存，Save 抛错会残留内存改动。实现时须在锁内恢复提交前状态并重新对齐受影响的提醒；回收站递归当前会多次保存，应调整为一次树变更、一次提交、一次通知，避免半棵树已保存。若恢复失败，Store 进入故障状态并拒绝继续读写，提示重启，不自动用残留内存覆盖文件。

不改变原文件位置和 TodoData 的 `categories/items` 结构，不迁移 SQLite。原子替换保证文件提交边界，不承诺磁盘硬件故障下绝对不丢失数据。

## 7. 文件边界与交付

预计新增 `Project/Cli/` 内 console csproj、入口/协议 DTO，`Project/Core/Todo/` 内管道 host 与命令适配；协议 DTO 由两个工程链接同一源文件，避免再建只有 DTO 的类库。修改 `App.xaml.cs` 做后台/界面启动分流、`TodoStore.cs` 补齐上述必要契约、`ToolBox.csproj` 排除 `Cli/**/*.cs` 及嵌套资源、`ToolBox.slnx` 加入 CLI。不修改 refs，不修改 LLM 提示词或文件访问白名单，不重构无关 UI。

发布需把 CLI 输出放到桌面应用发布目录并携带依赖，不把 CLI 变成独立解决方案。整体工作主要在启动生命周期和 Store 契约，而非命令解析；不能按“加几个参数”估算。

## 8. 验证与验收设计

以下是实现后的验收要求，本次均未执行，尚未作为 PENDING_TESTS 中的已交付功能测试项登记。

1. 临时独立测试数据下跑完整 create→list/get→update（含清空字段）→complete/reopen→delete→restore；JSON 可解析，退出码正确，重启后结果保留。
2. 冷启动和多个 CLI 同时冷启动：不弹窗口，只有一个存活 host，后续双击只在该实例打开一组界面。
3. Light/Dark 下现有待办列表和迷你窗口同步显示 CLI 变更；父子进度、分类、回收站及重复提醒注册结果符合原语义。
4. UI/Cron/CLI 并发修改，不丢项；畸形/超限 JSON、空标题、非法枚举/日期、未知字段/版本、废弃项和不存在父节点均被拒绝。
5. 模拟损坏 JSON、无写权限、原子替换失败和写入后断连：不覆盖旧文件、不返回虚假成功、不重放 create，恢复或故障隔离行为可验证。
6. 正常 UI 启动、后台转 UI、退出、host stop、旧版占锁、不同账号/提权级别拒绝访问；原截图参数不回归。

最小集成试验先只做隔离数据下的后台启动、管道 ping/list 和正常双击唤出界面；确认生命周期可行后再接入写入。正式实现须留下可运行的协议/CRUD 集成检查，不能仅靠手工演示。

## 9. 待确认决策

已确认的“Todo、未启动也可调用、自动后台不弹窗口”不重复询问。待用户一次确认：采用本地管道+原应用后台 host、固定命令+JSON 且不新增 CLI 依赖、delete 默认回收站，以及本设计列明的首版范围。确认前不编码或移植；若选择 System.CommandLine，只将命令解析部分替换为该包，更新设计后按确认范围实施。
