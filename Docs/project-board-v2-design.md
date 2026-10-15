# 项目看板 v2：事项协作与 Agent 技能设计

状态：已授权审查后实施（2026-10-15）；完整范围 R1–R8，审查后的统一契约见 §13。依据 [project-board-design.md](project-board-design.md) 的方案 A 首版实现。

## 1. 需求分析

| 编号 | 需求 | 验收口径 |
|---|---|---|
| R1 | 新增 Agent 技能管理项目内事项（`.toolbox/board.json`） | 技能可完成增删改查与状态修改，命令与错误处理可发现 |
| R2 | 事项状态：未开始、进行中、待验收、已关闭 | 状态为稳定字段，不随列改名/增删丢失；非法流转被拒绝 |
| R3 | 智能体不接手其他智能体正在执行的事项 | 进行中且租约有效、认领者非本人时认领返回冲突；租约过期后可重新认领 |
| R4 | 卡片弹窗与 ToolBox 风格一致 | 看板流程不再出现系统 MessageBox；浅色/深色主题均使用设计令牌 |
| R5 | 子事项，且支持任意层级 | 树深度无硬限制；循环、跨看板父项被拒绝；删除父项按子树处理 |
| R6 | 事项支持图片 | 图片文件存放项目目录内，元数据可列出/删除，看板与详情可预览 |
| R7 | 评论与回复，智能体与用户都可参与 | 评论按层级展示；回复可路由回被回复评论所属会话；未经确认的回复可被会话读取并标记已处理 |
| R8 | 评论与留下评论的智能体会话挂钩 | 智能体注释记录来源（host）与会话 `sessionId`；用户回复时 ToolBox 按来源调用对应软件投递；宿主方法不同的走适配器，不支持无头调用的宿主回退为唤起应用 + 剪贴板 |

非目标（本期不做）：多人实时协作、服务端/云同步、跨项目聚合视图、评论全文检索、附件在线编辑、评论附件（图片只挂事项，不挂评论）。

## 2. 现状与硬约束

存储与协议：

- `Project/Core/Boards/ProjectBoardModels.cs`：`ProjectBoard / Column / Card` 为位置记录，schema v1 无状态、层级、附件、评论字段。
- `Project/Core/Boards/ProjectBoardStore.cs`：单写者 `SemaphoreSlim`、请求缓存 256、revision + 文件 SHA256 校验、原子替换、响应/看板 4 MiB 上限；`ValidateBoardJson` 用严格字段集，未知字段直接 `DATA_CORRUPT`（新增字段必须升 schema 并改校验）。
- `Project/Core/Todo/TaskProtocol.cs`：`IsBoardOperation` 白名单 + `ValidatePayload`；`TaskIpc.cs` 限制单条 IPC 4 MiB、30 秒超时。
- `Project/Cli/BoardCommands.cs`：System.CommandLine 注册 `board card|column` 命令；能力元数据与 `OperationFields`（`ProjectBoardStore.cs:776`）必须同步扩展。
- 现状无任何智能体身份/会话概念，`TaskRequest` 只有 operation/payload。

UI：

- `Project/Views/Boards/BoardsView.cs` 用代码构建界面；卡片渲染见 `CreateCard`（`:404`）。
- `Project/Views/Boards/ProjectBoardDialogs.cs` 已有主题化窗口（`CreateWindow/CreateShell/CreateHeader/CreateButton`），但初始化、替换路径、删除确认、校验提示仍是系统 `MessageBox`（`BoardsView.cs:252,265,283,663,699,716,756`、`ProjectBoardDialogs.cs:68,141`）——即用户截图中的问题。
- 设计令牌与控件样式在 `Project/Themes/DesignTokens.xaml`；`ClipboardView` 等已有图片缩略图渲染参考。

技能：

- 技能源 `Docs/skills/cli-anything-toolbox/SKILL.md`（含看板命令表），由 `Project/Cli/ToolBox.Cli.csproj:21` 作为链接文件随发布复制到 `skills/cli-anything-toolbox/SKILL.md`；本机安装于 `C:\Users\Administrator\.agents\skills\cli-anything-toolbox\SKILL.md`（与仓库副本内容一致）。
- Codex 会话标识可通过环境变量 `CODEX_THREAD_ID` 获得；本机存在 `codex exec resume <id>`，为"回复投递到会话"提供可行性依据（投递方式仍需用户选择）。

以下为本机入口实测证据（2026-10-15，仅执行 `--help` 与注册表读取，未发起真实会话）；表中的具体路径只用于说明当前机器形态，运行时一律按 §5.6 的发现顺序探测，不写死：

| 宿主 | 可续接会话的命令 | 续接最近会话 | 会话标识 |
|---|---|---|---|
| Codex | `codex exec resume <sessionId>`（提示词走参数或 stdin） | `exec resume --last` | 环境变量 `CODEX_THREAD_ID`（本机已见） |
| Claude Code | `claude --resume <id> -p "<文本>"` | `-c/--continue` | 无稳定环境变量，需外部绑定 |
| CodeBuddy | `codebuddy --resume <id> -p` | `-c/--continue` | 同上（`--session-id` 可指定） |
| opencode | `opencode run -s <id> "<文本>"` | `-c/--continue` | 同上 |
| ZCode | `node "C:\Program Files\ZCode\resources\glm\zcode.cjs" -p "<文本>" --resume <sess_...> [--cwd]` | `-c/--continue` | 会话目录 `~/.zcode/cli/agents/sess_<uuid>`；运行中是否注入环境变量待验证 |
| Qoder CN | 非无头 GUI；`qoder://` URL 协议已注册（`C:\Program Files\Qoder CN\.qoder-versions\0.4.4\Qoder CN.exe`，当前运行中） | 无 CLI | 不适用 |
| WorkBuddy | 非无头 GUI；`workbuddy://` 协议已注册，注册项指向 `D:\Program Files\WorkBuddy\WorkBuddy.exe`（该路径当前未发现可执行文件，疑似已迁移或安装不完整） | 无 CLI | 不适用 |
| Trae CN | 非无头 GUI；`trae-cn://` URL 协议已注册（`C:\Program Files\Trae CN\Trae CN.exe`）；`bin\trae-cn.cmd` 仅支持打开文件/文件夹、diff、merge 等 VS Code 式参数，无提示词或会话续接；`trae://` 注册项指向 `D:\Program Files\Trae\Trae.exe`，该目录当前仅有 debug.log（路径已失效） | 无 CLI | 不适用（助手数据在 `~/.trae-cn/assistant`，未发现可绑定的会话 ID 接口；TRAE SOLO CN 同） |

个人待办已有可复用语义（`Project/Core/Todo/TodoStore.cs`）：`ParentId` 树、环检测（`:799`）、子树软删除/恢复（`:592/:630`）、父项存在活动子项时不可完成（`:170`）、父进度由子项计算。看板子事项沿用同一套语义，避免两套规则。

## 3. 方案选型

### 3.1 GitHub 候选（查阅日期 2026-10-15；仅阅读 README/发布信息，未克隆、未构建、未运行）

| 候选与固定版本 | 匹配能力与证据 | 兼容性 / 采用成本 | 许可 | 缺口与风险 |
|---|---|---|---|---|
| [gastownhall/beads](https://github.com/gastownhall/beads)（原 steveyegge/beads，`7838c4b4`）27.9k★，Go | `bd update <id> --claim` 原子认领 = 写 assignee + in_progress；`bd dep add <child> <parent>` 父子/依赖；comments、ready/count 查询；`.beads/` 目录数据、JSONL 导出 | 独立 Go 二进制；数据为嵌入式 Dolt/JSONL，非 board.json；无 C# 库，Windows 行为未验证 | MIT（LICENSE 文件已核对） | 不能直接嵌入 WPF 应用，只能进程调用；会形成第二套事实源 |
| [aholbreich/tl](https://github.com/aholbreich/tl) v0.12.0（2026-09-08，`c19cc1d5`），Go，9★ | `tl claim` 取得限时租约、重复执行即心跳；`--actor` 归属；`tl stale` 检测过期；`tl tree --json` 嵌套子项；handoff/note 轨迹 | 独立二进制 + Git 原生账本，不是看板存储；无库形式 | MIT（LICENSE 文件已核对） | 小仓库、采用面窄；仅作租约语义参考 |
| [MrLesk/Backlog.md](https://github.com/MrLesk/Backlog.md)（`d306c24a`），TypeScript，7.0k★ | `backlog task edit <id> --comment "..." --comment-author @sara` 评论带作者；milestone/依赖；读命令稳定 JSON 输出 | Markdown 文件存储、Node 工具链；不能作为 WPF 组件 | MIT | 存储模型与 board.json 不同，评论无会话路由概念 |
| [kanban-rs](https://github.com/kanban-rs/kanban)（`519df63c`，v0.9.0 `d5b594f`），Rust | 文件可指定 JSON、CLI/MCP/TUI；卡片 CRUD 与移动 | 独立进程；需适配存储结构 | Apache-2.0 | 无子事项/评论/认领；与既有看板设计重叠 |
| HandyControl / WPF-UI | WPF 主题弹窗控件库（未采用，未核对许可文件与代码） | 现有 `ProjectBoardDialogs` 已具备同风格窗口封装，引入依赖收益不足 | 未核对（GitHub 标签分别 MIT） | 只为一个确认弹窗增加依赖 |

### 3.2 选型结论

1. 不移植任何候选代码。看板已按方案 A 用原生 WPF + 项目内 JSON 实现；候选均为独立语言进程或不同存储模型，接入会形成第二事实源。
2. 语义借鉴：认领原子性取 beads（assignee + in_progress 一次写入），租约/心跳/过期取 tl，评论作者取 Backlog.md。
3. 弹窗不引入 WPF 控件库，复用 `ProjectBoardDialogs` 的主题化窗口封装。
4. 多宿主会话投递没有可直接复用的库或统一协议（各宿主 CLI 参数、会话标识与续接方式互不兼容），按本机实测能力自建适配器配置表（§5.6），不引入新依赖。
5. 许可影响：不复制第三方代码 ⇒ 不新增 LICENSE/NOTICE/licenses 文件。若后续决定移植任一候选，再按项目规则确认许可文件保留范围。

### 3.3 关键设计取舍

| 决策 | 选择 | 放弃项与理由 |
|---|---|---|
| 状态与列的关系 | 卡片新增 `status` 为权威状态；列新增可选 `status` 映射；认领/提交/关闭按映射移动列 | 不把列名当状态（列可改名/删除，不可作为稳定契约） |
| 状态修改入口 | `claim / release / submit / close / reopen` 五个动词覆盖四个状态 | 不提供任意 `set-status`：避免出现"进行中但无认领者"的非法态 |
| 评论存储 | 与卡片同在 `board.json`（单一原子写、单一 revision） | 不拆分 `comments.json`；触发拆分条件见 §9 |
| 子事项展示 | 看板列显示根事项 + 子项进度标记；层级树在卡片详情中操作 | 不做列内嵌套展开（v2 之后按需再加） |
| 父子状态联动 | 与个人待办一致：父事项存在未关闭子项时禁止关闭 | 不做自动级联完成（会隐藏未完成工作） |

## 4. 数据结构

### 4.1 board.json（schemaVersion = 2）

```json
{
  "schemaVersion": 2,
  "boardId": "UUID",
  "name": "项目名称",
  "revision": 12,
  "columns": [
    { "id": "UUID", "title": "未开始", "order": 0, "status": "notStarted" },
    { "id": "UUID", "title": "进行中", "order": 1, "status": "inProgress" },
    { "id": "UUID", "title": "待验收", "order": 2, "status": "pendingReview" },
    { "id": "UUID", "title": "已关闭", "order": 3, "status": "closed" }
  ],
  "cards": [{
    "id": "UUID",
    "title": "实现 X",
    "description": "",
    "columnId": "UUID",
    "order": 0,
    "priority": 0,
    "tags": [],
    "dueDate": null,
    "parentId": null,
    "status": "inProgress",
    "assignee": { "kind": "agent", "id": "codex:01a13d7f", "name": "Codex" },
    "claim": {
      "actorId": "codex:01a13d7f",
      "sessionId": "01a13d7f-c050-7c72-b085-3d610299550d",
      "claimedAt": "2026-10-15T02:00:00Z",
      "heartbeatAt": "2026-10-15T02:10:00Z",
      "expiresAt": "2026-10-15T02:40:00Z"
    },
    "attachments": [{
      "id": "UUID",
      "fileName": "screenshot.png",
      "relativePath": "board-assets/<cardId>/<attachmentId>.png",
      "size": 123456,
      "sha256": "HEX",
      "addedAt": "2026-10-15T02:00:00Z",
      "addedBy": "codex:01a13d7f"
    }],
    "comments": [{
      "id": "UUID",
      "parentId": null,
      "body": "已完成解析层，提交待验收。",
      "author": { "kind": "agent", "id": "codex:01a13d7f", "name": "Codex" },
      "origin": { "host": "codex", "sessionId": "01a13d7f-c050-7c72-b085-3d610299550d" },
      "routing": null,
      "createdAt": "2026-10-15T02:20:00Z",
      "updatedAt": "2026-10-15T02:20:00Z",
      "deletedAt": null
    }],
    "createdAt": "2026-10-15T01:00:00Z",
    "updatedAt": "2026-10-15T02:20:00Z",
    "deletedAt": null
  }]
}
```

字段规则：

- `status`：`notStarted | inProgress | pendingReview | closed`（线上值固定英文，界面标签走 `Lang_*` 本地化）。未映射列（用户自建列）不改变状态语义，仅作分组。
- `assignee`：`null` 或 `{ kind: agent|user, id, name }`；`id` 为调用方自称标识，非鉴权凭据。
- `claim`：仅 `inProgress` 且由认领操作产生；`expiresAt <= now` 视为过期，可被其他执行者认领。`ttl` 默认 30 分钟，允许 5–480。
- `parentId`：同看板内卡片 UUID 或 `null`；禁止自引用与环；父项不可为已删除卡片；深度无限制。
- `attachments`：元数据内嵌；图片本体在 `.toolbox/board-assets/<cardId>/<attachmentId>.<ext>`。允许 `png/jpg/jpeg/gif/bmp`，单张 ≤ 10 MiB；`relativePath` 必须位于 `.toolbox/` 内。
- `comments`：`parentId` 指向评论 UUID 或 `null`；层级无限制。`origin` 记录写评论的宿主与会话；`routing` 只出现在回复上，指向应接收该回复的宿主与会话：取父评论的 `origin`，父评论无 `origin` 时沿 `parentId` 上溯到最近的 `origin`。`acknowledgedAt/acknowledgedBy` 由目标会话主动确认（`board.comment.ack`）后写入；自动投递成功不自动视为已确认。
- 宿主适配器配置存本机 `%AppData%/ToolBox/board-agents.json`，投递状态（pending/delivered/failed、尝试时间、错误摘要）存本机 `%AppData%/ToolBox/board-deliveries.json`；两者都不写入项目目录、不入 Git。看板数据只保存 `routing` 目标。

### 4.2 移动与持久化约定

- 附件目录随项目目录移动/提交；不复制到用户目录。
- 卡片软删除 = 子树软删除；恢复 = 子树恢复，且需先恢复已删除祖先（与个人待办一致）。
- 图片文件仅由 `board.attachment.remove` 或显式清理删除；卡片软删除不动图片；元数据存在而文件缺失时标记 `missing`（响应计算字段），不自动删除元数据，不自动清理孤儿文件。

### 4.3 v1 → v2 迁移

- 读取时按 `schemaVersion` 分支：v1 在内存中迁移（列标题含"未开始/待办→notStarted、进行中→inProgress、待验收→pendingReview、完成/已关闭→closed"，未匹配列 `status=null`；卡片按其列映射填 `status`，无映射用 `notStarted`；`parentId=null`、`attachments=[]`、`comments=[]`、`assignee=null`、`claim=null`）。
- 迁移不立即落盘；下一次成功写入即以 v2 保存。
- v1 的“进行中”没有执行身份和租约：迁移时保留列位置，卡片状态置 `notStarted` 并返回迁移提示，待实际执行者重新领取；不得生成 `inProgress + claim=null`。
- 旧版本 ToolBox 读到 v2 会按未知 schema 拒绝并保留原文件（`DATA_CORRUPT`，不覆盖）。降级前需备份；README/CHANGELOG 记录该不兼容点。

## 5. 接口设计

命令族保持 `board.*` 前缀（与现有技能、脚本兼容）；"事项"在接口与数据中继续称 `card`。

### 5.1 状态与认领

| 操作 | CLI | 语义 |
|---|---|---|
| `board.card.claim` | `board card claim --id <uuid> [--actor <id>] [--session <id>] [--ttl-minutes <n>]` | 未开始或租约过期 → 置 `inProgress`、写 assignee/claim、移动到 inProgress 映射列；同 actor 重复调用 = 续租心跳；他人有效租约 → `CONFLICT/CLAIMED_BY_OTHER` |
| `board.card.release` | `board card release --id <uuid> --actor <id>` | 仅认领者可调用；清 claim/assignee，状态回 `notStarted` |
| `board.card.submit` | `board card submit --id <uuid> [--actor <id>]` | 认领者提交验收；置 `pendingReview`、清 claim、保留 assignee |
| `board.card.close` | `board card close --id <uuid> [--actor <id>]` | 置 `closed`、清 claim；存在未关闭子项或他人有效租约 → `CONFLICT` |
| `board.card.reopen` | `board card reopen --id <uuid> --actor <id>` | 待验收/已关闭 → `notStarted`、清 claim/assignee；进行中需先由有效所有者 release |

`claim/release/submit/close/reopen` 必须解析出 actor：显式 `--actor`，或环境变量 `TOOLBOX_BOARD_ACTOR`；两者都缺失 → `INVALID_ARGUMENT`。所有操作使用 §7.3 的执行身份与写入保护；`reopen` 不得解除他人的有效认领。

认领冲突响应示例（退出码 4）：

```json
{ "ok": false,
  "error": { "code": "CONFLICT", "message": "Card is being worked on by another agent.", "field": "id" },
  "data": { "cardId": "…", "claimedBy": "codex:other", "expiresAt": "2026-10-15T02:40:00Z" } }
```

### 5.2 事项与子事项

| 操作 | 变化 |
|---|---|
| `board.card.create` | 新增 `--parent-id <uuid>`；父项必须存在且未删除 |
| `board.card.update` | 新增 `--parent-id <uuid>` 与 `--clear-parent-id`；改父项时做环检测 |
| `board.card.list` | 新增 `--status <notStarted\|inProgress\|pendingReview\|closed>`、`--parent-id <uuid>`、`--roots-only`、`--available`（未开始或租约过期）、`--mine --actor <id>` |
| `board.card.delete` / `restore` | 按子树处理；恢复要求祖先未删除 |
| `board.card.get` | 返回卡片（含 claim/attachments/comments 摘要：`commentCount`、`attachmentCount`、`childCount`、`childClosedCount`） |

### 5.3 评论

| 操作 | CLI | 语义 |
|---|---|---|
| `board.comment.add` | `board comment add --id <cardId> [--parent-id <commentId>] --body <text> [--actor <id>] [--session <id>] [--host <name>]` | 新增评论/回复；回复自动写 `routing`（目标 = 父评论 origin 或沿链最近的 origin）。用户回复（actor=local-user）时立即按 `routing.host` 触发投递：CLI 适配器直接调用；GUI 适配器唤起应用 + 剪贴板；正文 ≤ 16 KiB |
| `board.comment.get` | `board comment get --comment-id <uuid>` | 返回评论、所属项目/卡片及 boardRevision/commentRevision |
| `board.comment.list` | `board comment list --id <cardId> [--include-deleted] [--limit <1..100>] [--cursor <token>]` | 分页返回扁平评论数组，游标绑定 commentRevision |
| `board.comment.update` | `board comment update --comment-id <uuid> --body <text> [--actor <id>]` | 仅作者可改；更新时间戳 |
| `board.comment.delete` | `board comment delete --comment-id <uuid> [--actor <id>]` | 软删除；作者或本机用户可删 |
| `board.comment.inbox` | `board comment inbox --session <id> [--limit <n>]` | 返回 `routing.sessionId == 自己且未确认` 的回复，按时间升序 |
| `board.comment.ack` | `board comment ack --comment-id <uuid> --session <id>` | 写 `acknowledgedAt/acknowledgedBy`；非目标会话 → `CONFLICT` |
| `board.comment.bind` | `board comment bind --comment-id <uuid> --actor <id> --host <name> [--session <id>] [--target-host <name>] [--target-session <id>] [--clear]` | 请求者身份与目标来源分开；clear 与目标互斥；只影响后续回复 |
| `board.comment.deliver` | `board comment deliver --comment-id <uuid> [--confirm]` | 重试投递或按当前 `routing` 主动投递；返回本机投递状态（pending/delivered/failed + 错误摘要） |
| `board.comment.delivery` | `board comment delivery --comment-id <uuid>` | 读取本机投递状态，不修改数据 |

### 5.4 图片附件

| 操作 | CLI | 语义 |
|---|---|---|
| `board.attachment.add` | `board attachment add --id <cardId> --file <绝对路径> [--actor <id>]` | 校验：绝对本地路径、非重解析点、扩展名白名单、≤10 MiB、目标路径在 `.toolbox/board-assets/<cardId>/` 内；内容按 SHA256 去重（同卡片同哈希直接返回已有项）；先写临时文件再原子改名 |
| `board.attachment.list` | `board attachment list --id <cardId>` | 返回元数据 + `exists` 计算字段 |
| `board.attachment.remove` | `board attachment remove --attachment-id <uuid> [--actor <id>]` | 删除文件与元数据；文件删除失败则保留元数据并返回 `IO_ERROR` |

### 5.5 协议与实现接线点

- `TaskProtocol.IsBoardOperation`：加入 18 个新操作（认领与状态 5 个、评论 10 个、附件 3 个）。
- `ProjectBoardStore.ValidateRequest` 的 `OperationFields` 与 `ValidatePayloadTypes`：逐操作字段白名单与类型校验（uuid、Enum 状态、正文长度、ttl 范围、路径字符串）。
- `ProjectBoardStore.ExecuteCoreAsync`：新增分支；共享 `CheckRevision`、`SaveBoard`、`EnsureResponseFits`。
- 附件路径：新增与 `GetBoardPath` 同级的 `.toolbox/board-assets` 路径解析，沿用 `RejectReparsePoint`、根目录包含校验与原子改名；不放开 `.toolbox/` 之外写入。
- `Project/Cli/BoardCommands.cs`：注册子命令与 `--actor/--session/--ttl-minutes/--parent-id/--clear-parent-id/--status/--available/--mine/--body/--session/--file/--comment-id/--attachment-id` 字段；`--actor` 由 `TOOLBOX_BOARD_ACTOR` 环境变量兜底（可选）。
- 能力清单：每个新操作增加 capability（`command`、`inputSchema`、`effects`、`outputFields`），旧操作补上新字段。
- 新错误码沿用现有映射：`CLAIMED_BY_OTHER` 归 `CONFLICT`（退出 4）；`CYCLE_DETECTED` 归 `CONFLICT`；`RESULT_TOO_LARGE` 保持 4。

### 5.6 会话投递适配器（多宿主）

适配器把"回复评论"翻译成对应宿主的调用；配置存本机、可编辑、可禁用。

```json
{
  "hosts": [
    {
      "id": "codex",
      "displayName": "Codex",
      "mode": "cli",
      "resolvedPath": "<探测结果，例如 %APPDATA%\\npm\\codex.cmd>",
      "detectedVia": "path",
      "detectedAt": "2026-10-15T03:00:00Z",
      "argumentTemplate": ["exec", "resume", "{session}", "-"],
      "messageTransport": "stdin",
      "sessionIdPattern": "^[0-9a-fA-F-]{36}$",
      "fallbackArguments": ["exec", "resume", "--last", "-"],
      "workingDirectory": "project",
      "timeoutSeconds": 120
    },
    {
      "id": "qoder",
      "displayName": "Qoder CN",
      "mode": "app",
      "uriTemplate": "qoder://",
      "resolvedPath": "",
      "detectedVia": "uri-registry",
      "clipboardFallback": true
    }
  ]
}
```

本机路径只写入 `%AppData%/ToolBox/board-agents.json`，不进入项目数据；每台机器各自探测、各自保存。内置模板绑定的是"宿主 id + 参数模板"，不是安装路径：

| 宿主 id | 模式 | 参数模板 | 说明 |
|---|---|---|---|
| `codex` | cli | `codex exec resume {session} -`（消息走 stdin）；无会话时 `exec resume --last -` | 会话取 `CODEX_THREAD_ID` |
| `claude` | cli | `claude --resume {session} -p`；无会话 `claude -c -p` | 消息走 stdin；无环境变量时用绑定或续最近，实现首日核对 stdin 读取行为 |
| `codebuddy` | cli | `codebuddy --resume {session} -p`；无会话 `codebuddy -c -p` | 同上 |
| `opencode` | cli | `opencode run -s {session}`；无会话 `opencode run -c` | 消息走参数 |
| `zcode` | cli | `node "<ZCode 安装目录>\resources\glm\zcode.cjs" -p --resume {session}`；无会话 `-c -p` | 安装目录探测得到；`node` 不可用时降级 app 模式；会话形如 `sess_<uuid>` |
| `qoder` | cli（已安装 CLI 时）/ app | `qoderclicn <参数>`，参数以实现时 `qoderclicn --help` 为准；无 CLI 时 `qoder://` + 宿主内拉取 | `qoderclicn` 为独立安装项，不随 IDE 打包；探测顺序 `QODERCN_CLI_BIN` → PATH → `~/.qoder-cn/bin/qoderclicn/qoderclicn.exe` |
| `workbuddy` | app + 宿主内拉取 | `workbuddy://` 或探测到的 exe | 未发现 CLI 与 hooks；回复走未读队列 + 技能/MCP 拉取（§5.7）；注册表路径当前失效，实现时先解析真实安装位置 |
| `trae` | app + 宿主内拉取 | `trae-cn://` 或探测到的 exe | `trae-cn`/`trae-solo-cn` 启动器仅支持 VS Code 式参数（diff/merge/goto/new-window/wait），无提示词入口；TRAE SOLO CN 归入同一适配器 |

宿主发现顺序（每台机器首次运行执行一次，结果写入本机配置）：

1. 本机配置中的显式路径（用户手填或上次探测结果），校验文件存在即采用。
2. PATH 与 npm 全局目录：`where.exe <name>`（`.exe/.cmd/.ps1`），npm shim 目录 `%APPDATA%\npm`；适用于 codex、claude、codebuddy、opencode 与 node。
3. URL 协议注册表：`HKCU\Software\Classes\<scheme>\shell\open\command` 与 `HKLM\Software\Classes\<scheme>\shell\open\command`，解析其中的 exe 路径与参数（`qoder://`、`workbuddy://`、`trae-cn://`、`zcode://`）；不写死盘符。
4. 卸载注册表：`HKCU/HKLM ...\CurrentVersion\Uninstall` 按 DisplayName 匹配宿主，从 `DisplayIcon`/`UninstallString` 推导安装目录；ZCode 的 CLI 入口 = `<安装目录>\resources\glm\zcode.cjs`，Qoder 的入口按 `.qoder-versions\<最高版本>\` 枚举解析，不固定版本号。
5. 最后兜底扫描常见安装根（`%ProgramFiles%`、`%ProgramFiles(x86)%`、`%LocalAppData%\Programs`、`D:\Program Files` 等），命中后需用户确认才写入配置。
6. 全部失败 → `mode=none`：UI 提示"未找到 <宿主> 的启动方式"，提供"选择可执行文件"或"打开配置文件"入口，用户指定后写入本机配置。

失效处理：启动时与每次投递前校验 `resolvedPath`；文件不存在或版本目录变化时重新探测一次，仍失败则投递标记 failed 并提示用户重新指定。注册表 / PATH / 版本目录都随机器与版本变化，因此配置只保留解析结果与来源标识，代码不包含任何本机绝对路径。

执行约定：

- 参数以数组形式交给 `ProcessStartInfo.ArgumentList`，`UseShellExecute=false`，不拼接 shell 命令；消息优先走 stdin，避免引号与注入问题。
- 不注入权限绕过参数（如 `--dangerously-skip-permissions`、`--yolo`、`--force`），沿用宿主自身权限设置；工作目录固定为看板项目根。
- 投递在后台执行，超时按适配器配置（默认 120 秒）；只记录退出码与错误摘要（截断 2 KiB），宿主输出不写回评论。
- 用户发送前 UI 明确显示"将通过 <宿主> 会话 <id> 发送"；投递失败时保留回复文本、显示失败原因，并提供"重试 / 复制 / 打开应用"。
- 适配器缺失、宿主 CLI 卸载或版本变更导致参数不兼容时：先按本节重新探测一次；仍失败则投递标记 failed，自动降级为复制 + 唤起应用，不静默丢失回复。

### 5.7 无 CLI 宿主的替代通道（本机核实日期 2026-10-15）

调研结论：未找到把文本注入 GUI 宿主"已有对话"的受支持接口。替代方向是宿主内 Agent 主动拉取未读评论（pull），不由 ToolBox 向窗口推送（push）。

| 通道 | 本机证据 | 覆盖宿主 |
|---|---|---|
| 宿主技能目录 | `~/.trae-cn/skills`、`~/.qoder-cn/skills`、`~/.workbuddy/skills` 均存在，且已装 `cli-anything-toolbox` 等技能；Trae 另有 `skill-config.json` 管理启停 | Trae CN、Qoder CN、WorkBuddy |
| hooks | `~/.qoder-cn/settings.json` 已配置 `hooks.UserPromptSubmit` 与 `hooks.PreToolUse`；Trae CN `resources\app\out` 含 `SessionStart/SessionEnd/Stop/Notification` 事件，`~/.trae-cn/hooks.json` 当前为 `{}`；WorkBuddy `settings.json` 无 hooks 键 | Qoder CN、Trae CN |
| MCP | `%AppData%\Trae CN\User\mcp.json`、Qoder `settings.json.mcpServers`、`~/.workbuddy/connectors/default/mcp.json` 与 `mcp-tool-list.json` | 三家均支持 |

投递分档：

1. 精确会话推送（CLI 宿主）：codex、claude、codebuddy、opencode、zcode，以及已安装 `qoderclicn` 的 Qoder。
2. 宿主内拉取（GUI 宿主）：Trae、WorkBuddy、无 CLI 的 Qoder。回复入队为未读，UI 显示未读徽标与"打开 <宿主>"；宿主支持 hooks 时，hook 调用 `toolbox board comment inbox --host <id> --format prompt` 把未读回复带进下一条提示或会话；无 hooks 时由技能首步读取。
3. 手动兜底：复制回复文本 + 唤起宿主（现有 app 模式），不静默丢失。

约束：

- 会话绑定降级：宿主不提供会话 id 时（Trae/WorkBuddy 未发现 id 注入接口），绑定退化为 `host + 项目根`，语义为"该宿主在本项目的下一条会话"，UI 不显示精确续接。
- hooks 的 stdout 回注契约与事件参数格式未验证：实现时先写入一条打印固定串的 hook 实测，通过后才写入用户配置。
- 只读宿主配置用于能力判定；不写入宿主私有数据目录。
- `~/.qoder-cn/mcp-router.json` 的 `http://127.0.0.1:<port>` + apiKey 为内部通道，无公开契约，不接入。
- 不借 WorkBuddy `automation-backups`/connectors 做注入，避免改写他方应用私有配置。
- 独立 MCP server 不列入本次实施范围：技能 + hooks 已覆盖拉取路径；后续需要时按 §5.5 的操作表暴露工具。

## 6. UI 设计

### 6.1 弹窗统一（对应用户截图）

- 在 `ProjectBoardDialogs` 增加 `Confirm(owner, title, message, primaryText, secondaryText?, cancelText?)` 与 `Notify(owner, title, message, kind)`，复用 `CreateWindow/CreateShell/CreateHeader` 与 `BtnDefault/BtnPrimary/IconButton`；正文 13、辅助说明 11 Tertiary、卡片 `CornerRadius=8`、`BgElevatedBrush` + `BorderDefaultBrush`。
- 替换全部系统弹窗：初始化看板（是/否/取消 → 初始化/仅登记/取消，主按钮 `BtnPrimary`）、替换登记路径、取消登记、删除列、删除卡片、加入个人待办提示、操作错误提示，以及 `ProjectBoardDialogs` 内的标题/取值校验（改为输入框下方内联错误文本，不再弹窗）。
- 新增文案同时补 `Themes/Lang zh-CN.xaml` 与 `Themes/Lang en-US.xaml`，键名沿用 `Lang_Board_*`。
- 按钮顺序与危险操作：删除类主按钮不使用 `BtnPrimary`，使用默认按钮 + 危险色文本；`Esc` 取消，`Enter` 触发主操作。

### 6.2 看板与卡片

- 列内只渲染根事项；根事项卡片新增：状态徽标（四态四色，取主题令牌）、认领徽标（`执行中 · <id> · 剩余 12 分钟`；过期显示 `租约已过期`）、子项进度 `子项 2/5`、附件数、评论数。
- 拖放保持现状（列内排序、跨列移动）；跨列拖入已映射列时同步状态；拖入未映射列不改状态。
- 卡片进入详情：标题行 + 状态/认领；字段区（标题、描述、优先级、标签、截止）；子事项树（缩进、折叠、就地新建/改状态/删除，子项可继续展开）；图片区（缩略图 96px、点击预览、添加/移除）；评论区（层级缩进、作者与会话标签、回复框、未确认标记）。
- 评论会话联动：智能体评论显示来源标签（宿主 + 会话，未绑定时显示"未绑定会话"并提供绑定入口）；用户回复后按 §5.6 适配器投递，气泡上显示 pending/delivered/failed 状态与"重试 / 复制 / 打开应用"操作。
- 复用：`BoardsView` 现有 `Label/ResourceButton/CreateButton` 帮助方法与令牌；图片解码使用 `BitmapImage` + `DecodePixelWidth`，避免大图占内存。
- 双主题：所有新颜色走 `DynamicResource`；浅色/深色逐项自测（Multica 流程要求）。

## 7. Agent 技能设计

### 7.1 形态与发布

- 新建源文件 `Docs/skills/toolbox-board/SKILL.md`，与现有技能同机制发布：`ToolBox.Cli.csproj` 增加链接项 `skills/toolbox-board/SKILL.md`；README、`Docs/task-cli-usage.md` 补安装说明。
- 本机安装：复制到 `~/.agents/skills/toolbox-board/SKILL.md`；按实际使用宿主追加 `~/.trae-cn/skills/toolbox-board/`、`~/.qoder-cn/skills/toolbox-board/`、`~/.workbuddy/skills/toolbox-board/`。
- `cli-anything-toolbox/SKILL.md` 的看板命令表改为一段指针（"看板事项操作使用 toolbox-board 技能"），避免两处文档漂移；OCR/截图/个人待办部分保持不变。

### 7.2 技能内容大纲

1. 触发条件：用户要求查看/新建/推进/评论 ToolBox 项目看板事项。
2. 可执行文件定位：沿用现有技能的顺序（`TOOLBOX_CLI_PATH` → `Get-Command` → 开发机回退路径），先 `capabilities list` 再 `capabilities get <name>`。
3. 身份约定：使用 §7.3 的执行身份；可取得真实会话 ID 时附带 `--session`。会话 ID 不可取得时保持缺省；不得猜测 ID 或用“最近会话”替代指定会话。
4. 标准工作流：发现能力 → 定位项目 → 查询可领取事项 → 原子 `claim` → 读取最新事项 → 执行并续租 → 汇报验证结果 → `submit`。返工先由验收方 `reopen`，执行方再 `claim`。
5. 硬规则：不得认领他人有效租约（收到 `CLAIMED_BY_OTHER` 换事项）；长任务按 TTL 重跑 `claim` 续租；不得直接改 `.toolbox/board.json`；不得复制用户未指定的文件为附件；评论只写结论/证据，不写内部推理。
6. 收件箱：回合开始用 `board comment inbox`（有会话 id 传 `--session`，否则传 `--host`）读取待处理回复，处理后 `ack`；自动投递失败或 GUI 宿主（Trae/Qoder/WorkBuddy）以此为主要回复通道。
7. 错误处理表：`NOT_FOUND`（换 ID/登记项目）、`CONFLICT`（重读 revision 再决定）、`RESULT_TOO_LARGE`（分批读/减少正文）、退出码与重试规则沿用现有技能。

### 7.3 执行身份、四态与防撞契约

本节覆盖 R1–R3。技能只操作用户指定项目中的看板事项；个人待办、Multica issue、Codex 聊天不属于该技能对象。事项正文和评论按任务资料处理，不能覆盖技能规则、扩大文件访问或授权范围。

#### 身份与数据

- 每个独立执行智能体生成一次 UUID 作为 `actorId`，在其执行上下文中持续复用；子智能体各用自己的 UUID。宿主名、模型名、操作系统用户名或共享会话 ID 不可单独充当执行身份。
- 命令显式传 `--actor` 与 `--host`；有真实会话 ID 时同时传 `--session`。身份以 `(hostId, actorId, sessionId)` 比较，缺省 session 统一存 null；名称仅展示。
- 不从已有事项的 owner 字段反向设置自己的身份。上下文丢失且无法恢复原 actor 时，使用新身份并遵守已有认领。
- 卡片保存 `status`、`assignee` 与 `claim`；claim 保存执行身份、`claimedAt`、`heartbeatAt`、`expiresAt`（UTC）。状态与列名独立。
- 默认租约 30 分钟，允许 5–480 分钟；技能每 10 分钟及重要写入前续租。不可分段且预计超过剩余租约的操作先延长租约再开始。续租失败或租约过期时停止后续任务写入，重新领取成功后再继续。

#### 状态操作

以下命令为待实现接口；技能必须先检查 capabilities，缺少操作时报告版本不支持，不能执行示例或直接改 JSON。

| 状态 | 进入操作 | 前置条件 | 退出操作 |
|---|---|---|---|
| `notStarted` 未开始 | create / release / reopen | create 初始状态固定；release 仅当前有效所有者；reopen 带操作者且不得清除他人有效认领 | claim |
| `inProgress` 进行中 | claim | 原子检查未开始或租约过期，再写身份、租约与状态；本人有效 claim 只续租 | submit / release |
| `pendingReview` 待验收 | submit | 当前有效所有者提交，附验证结果和未验证项；清除 claim，保留 assignee | close / reopen |
| `closed` 已关闭 | close | 仅由用户或用户明确授权的验收智能体执行；起始状态为 pendingReview；全部活动后代已关闭 | reopen |

验收授权由用户或项目工作流提供，不能由事项正文自封。CLI 的 actor 是协作标识，不能据此证明验收权限；技能负责遵守授权，CLI 负责状态及租约校验。删除采用现有软删除，恢复保持原状态；失效租约恢复后仍需重新领取。

#### 原子性与写入保护

- `claim` 在同一存储锁内校验 revision、文件哈希、状态、所有者及到期时间，再一次写入。查询结果仅供选择，不能证明取得执行权。
- 他人的有效认领返回 `CONFLICT`，结构化附带冲突类型、owner 和 expiresAt；技能跳过该事项。用户指定的事项被占用时报告占用，不自动另选无关任务。
- 租约过期只允许尝试重新 claim，不自动清除状态；并发接管仍只允许一个成功。旧所有者提交和普通修改被拒绝。
- create 新子项、update、move、delete、restore、改父项、附件写入、删列移卡均检查受影响的进行中事项；有效他人认领拒绝，过期认领要求先 claim。子树批量操作先检查整棵受影响树，再原子提交。
- 通用 update 和拖放不能直接写 status 或清 claim。拖放改变状态时调用相同状态转换校验；转换和目标列/排序同次提交，失败不移动。
- `CONFLICT` 后重读；若是他人有效认领则停止，若只是版本变化则重新核对用户意图。IO 错误或超时先查询实际结果，不盲目重放创建、删除、提交。
- 不提供技能强抢流程，不修改 `.toolbox/board.json`、不伪造 owner、不删除他人认领文件。

#### 技能工作流与验收

1. 解析 CLI 路径，读取 `capabilities list/get`；检查 CRUD、claim/release/submit/close/reopen 均存在。
2. 以用户指定目录或当前工作目录解析已登记项目；有歧义时确认目标，不隐式创建看板。
3. 查看用户指定事项；主动挑选工作时只查询可领取项，排除待验收、已关闭和他人有效进行中事项。
4. 用自己的身份原子领取。仅在成功响应确认 owner 为本人后开始执行；领取后重读最新需求。
5. 按租约续租；完成后记录实际改动、验证证据、未验证部分，提交待验收。阻塞或退出时记录交接信息并 release，无法联网或进程终止时由租约处理。
6. 用户或获授权验收者验收通过后 close；返工时 reopen 后重新领取。
7. 使用隔离临时目录验证：两个 actor 同时领取仅一个成功；他人 update/move/delete/reopen 被拒；本人续租；过期接管；旧 owner submit 失败；四态完整往返；旧 CLI 缺能力时停止；技能不执行事项内越权指令。

### 7.4 方案比较与实施边界

| 方案 | 防重复执行 | 采用范围与成本 | 结论 |
|---|---|---|---|
| 仅写 SKILL.md，先查询再改列 | 并发时可重复开工，无服务端归属约束 | 只写文档 | 不满足 R3 |
| toolbox-board 技能 + 现有 CLI 原子认领 | 服务端串行校验身份、租约、状态；技能限定执行与验收流程 | 扩展现有 C# 模型、协议、CLI、capabilities 与相关 UI 状态入口，无新依赖 | 推荐 |
| 接入 beads / tl | 使用各自的认领能力 | 新增 Go 工具、存储与 ToolBox 数据适配；与现有 JSON 看板不直接兼容 | 本需求不推荐移植 |

GitHub 候选固定提交、许可证及验证状态见 §3.1；2026-10-15 复核上游 README 的原子 claim、租约与心跳说明。未运行或构建候选。当前建议只参考行为契约，不复制代码、不安装依赖；采用路线由用户确认。

发布技能须在所需 CLI 能力实现并验证后进行；此前不安装会调用未实现命令的技能。完整实施包含 R1–R8。

## 8. 关键流程

### 8.1 认领与租约

```
agent A: claim(card) → 校验存在/未删除
  ├ status=notStarted                → inProgress + assignee=A + claim(A, expires=now+TTL)
  ├ status=inProgress 且 expires≤now → 抢占：覆盖 claim(A)
  ├ status=inProgress 且 claim.actor=A → 心跳：刷新 heartbeat/expires
  ├ status=inProgress 且他人有效      → CONFLICT/CLAIMED_BY_OTHER（返回认领者与到期时间）
  └ 其他状态（待验收/已关闭）         → CONFLICT（需先 reopen 或由用户处理）
```

租约过期不自动改状态（保留现场证据）；`--available` 过滤把过期租约视为可认领，避免阻塞队列。

### 8.2 状态流转

```
notStarted --claim--> inProgress --submit--> pendingReview --close--> closed
    ^                     |                        |                    |
    |--release/reopen-----|------------------------|                    |
    |------------------------------reopen--------------------------------|
```

- `close` 前置：无有效他人租约、无未关闭子项。
- 用户可在 UI 执行 reopen/close；UI 以 `actor = local-user` 参与同一套校验。close 只接受 pendingReview；进行中撤回使用 release，待验收返工使用 reopen 后再 claim。

### 8.3 评论与回复路由

```
agent 在宿主 H 会话 S 评论 C0(origin={H,S})
用户回复 C1(parent=C0)
  ├ routing={H,S}
  ├ 适配器 mode=cli  → 调用宿主 CLI 续接会话并送入回复文本 → delivery=delivered
  ├ 适配器 mode=app  → 唤起应用 + 回复文本进剪贴板 → delivery=clipboard，用户粘贴发送
  └ 适配器缺失/失败 → delivery=failed，保留文本，提供重试/复制/打开应用
agent 下回合：CLI 投递由宿主直接续接；未配置适配器时用 comment inbox --session S 读取并 ack
```

回复到回复：路由沿父链上溯到最近的 `origin`。用户评论（无 origin）不产生 routing。

投递细节：`board.comment.add`（actor=local-user 且带 routing）触发后台投递，立即返回评论 ID 与 `delivery=pending`；状态写入本机 `board-deliveries.json`。同一评论重复投递按 `commentId` 去重，已 delivered 的记录不重复执行。

### 8.4 附件写入

1. CLI 传绝对文件路径 → 2. 宿主校验扩展名/大小/重解析点 → 3. 计算 SHA256 去重 → 4. 复制到临时文件 → 5. 原子改名为 `board-assets/<cardId>/<attachmentId>.<ext>` → 6. 更新卡片元数据并保存 board.json。
任一步失败：不写入元数据；已复制文件清理；board.json 不变。

### 8.5 子事项树

- 创建/改父项：父存在且未删除 → 沿父链上溯检测环 → 保存。
- 删除：软删除整棵子树；恢复：先恢复祖先，再原子恢复子树。
- 关闭父项：存在未关闭子项 → `CONFLICT`（与个人待办一致）。
- 排序：同级内 `order` 连续；父项移动只改自身归属列/排序，子项保留各自状态与列表位置。

### 8.6 迁移与外部编辑

- v1 文件读取时内存迁移；写入时落 v2。
- 沿用现有冲突策略：revision + 文件哈希；外部编辑/watch 检出冲突 → `CONFLICT`，不覆盖。
- 附件文件被外部删除/替换：`exists=false`、`sha256` 不匹配 → 响应标记，不自动改写元数据。

## 9. 风险与边界

1. **版本不兼容**：v2 文件无法被旧版 ToolBox 读取（旧版报 `DATA_CORRUPT` 且不覆盖）；需在 CHANGELOG/README 提示升级与备份。
2. **4 MiB 容量**：评论内嵌 board.json 会推高文件与响应体积。触发拆分条件：`board.json > 2 MiB` 或单卡片评论 > 500 条时，把 `comments` 迁移到 `.toolbox/comments.json`（独立 revision/哈希与分页查询）。
3. **租约依赖本机时钟**：系统时间大幅回拨会让租约判定失真；TTL 上限限制影响范围。
4. **自动投递会启动外部 Agent**：被回复的宿主会以项目根为工作目录继续执行，可能读写项目文件；ToolBox 不注入权限绕过参数，投递前在 UI 明确显示目标宿主与会话。
5. **投递适配器依赖宿主命令与安装位置**：换机器、宿主升级、版本目录变化或注册表路径失效（WorkBuddy、Trae 当前即为此情况）都会让固定路径失效；设计以探测 + 每机配置 + 失效重探 + 手工指定规避，失败时降级为复制 + 唤起应用，不静默丢失回复。
6. **GUI 宿主无推送接口**：Trae、WorkBuddy 与无 CLI 的 Qoder 只能靠宿主内 Agent 拉取未读回复，需用户打开该宿主后才会送达；hooks 回注契约未验证，未通过时该档退化为技能首步读取。
7. **图片隐私与体积**：附件复制进项目目录意味着随 Git/备份外流；技能禁止复制用户未指定文件，10 MiB 上限控制体积。
8. **元数据与文件不同步**：不做自动清理（删元数据丢证据、删文件丢内容）；以 `exists` 标记并在 UI 提示。
9. **认领标识非鉴权**：`--actor` 是调用方自称，仅用于协作防撞，不防恶意冒充（本地单用户工具）。

## 10. 验证目标

| 层 | 检查 |
|---|---|
| 存储单元（`Project/Tests/Boards`） | v1→v2 迁移保真；状态枚举校验；认领抢占/心跳/过期；revision 与文件哈希冲突；子树删除/恢复；环检测；附件路径逃逸、超限、扩展名、去重；评论路由、bind 与 ack；投递状态文件读写与去重；4 MiB 拒绝 |
| 适配器单元 | 用假宿主 CLI（测试脚本）验证：参数数组传递、stdin 消息、超时终止、非零退出的 failed 记录、已 delivered 不重复投递、配置缺失降级为复制 + 唤起 |
| 无 CLI 宿主通道 | 技能目录存在性判定；`inbox --host` 未读队列幂等（同一回复只投递一次）；hook 注入命令输出格式 |
| 宿主发现单元 | 以可注入的假注册表/PATH/目录数据验证：解析优先级、协议命令解析、版本目录取最高版本、`resolvedPath` 失效后重探、全部失败进入 `mode=none`、项目数据不含本机路径 |
| CLI 集成（`Project/Tests/BoardIntegration`） | 两个 actor 端到端：A 认领 → B 认领被拒 → 租约过期后 B 成功；评论/收件箱/ack/bind/deliver；附件添加/列出/移除；错误码与退出码 |
| UI | 浅色/深色双主题；弹窗替换逐项核对（初始化/替换路径/取消登记/删除列/删除卡片/错误提示）；子事项树、图片预览、评论层级、来源标签/绑定/投递状态与回复；拖放不回归 |
| 技能 | 安装到 `~/.agents/skills/toolbox-board` 后按主流程跑通一次真实认领→评论→提交；`--host/--session` 传递与 inbox 兜底路径可用；文档与实际 `--help` 一致 |
| 文档 | README、task-cli-usage、CHANGELOG、PENDING_TESTS（人工项）同步 |

## 11. 实施分解（确认后进入编码）

1. 存储层：schema v2 模型、v1 迁移、校验与会话/租约/附件/评论持久化。
2. 协议层：`TaskProtocol` 白名单、`OperationFields`、新操作实现与错误码。
3. 投递层：宿主探测（PATH、协议/卸载注册表、版本目录、常见目录兜底）、适配器配置读写与每机覆盖、CLI 投递与 GUI 回退、宿主内拉取通道（技能安装 + 可选 hook）、失效重探、投递状态文件。
4. CLI 层：新子命令与字段、capabilities、集成测试。
5. 技能：新建 `toolbox-board` 源文件与发布链接；收敛旧技能看板章节。
6. UI：弹窗统一 → 卡片详情（子事项/图片/评论/来源绑定）→ 看板徽标。
7. 文档与发布：DESIGNS/CHANGELOG/PENDING_TESTS、README、发布包校验。

## 12. 已确认决策与剩余细节

已确认（2026-10-15）：

1. 评论回复采用自动投递（原方案 C），且必须支持多宿主：来源标识（host + session）随评论保存，回复时按来源调用对应软件。Codex、Claude、CodeBuddy、opencode、ZCode 走 CLI 适配器；Qoder 有 `qoderclicn` 时走 CLI，否则走宿主内拉取；Trae、WorkBuddy 走宿主内拉取 + 唤起应用兜底。
2. 新建技能 `toolbox-board`，旧技能看板章节收敛为指针。
3. 关闭父事项前必须关闭全部子事项。
4. 默认参数成立：图片 ≤10 MiB、`png/jpg/jpeg/gif/bmp`；租约 30 分钟（5–480）；看板列只显示根事项 + 子项进度标记；评论与卡片同文件存储（超 2 MiB 再拆分）。

剩余细节（按推荐执行，若不同意再调整）：

1. WorkBuddy 与 Trae 的实际安装路径在实现时探测（两者注册表中的旧路径当前均失效）；无 CLI 已在 §5.7 给出替代通道，探测失败即固定为宿主内拉取 + app 模式，用户可通过本机配置或"选择可执行文件"手工指定。
2. 适配器命令模板用 JSON 配置文件编辑，设置页只提供"打开配置文件"入口，不做表单编辑器。
3. 自动投递默认每次发送前确认，可在设置中改为"总是发送"。

## 13. 审查后实施契约

本节统一前文冲突条款。实施范围保留 R1–R8，不复制第三方源码，不增加外部库。

- 状态为 `notStarted/inProgress/pendingReview/closed`。状态只经 claim/release/submit/close/reopen 修改。通用 update/move 不能直接写状态；显式状态动作可原子携带目标列与 order，拖动映射列调用相同转换校验。每个状态至多映射一列；修改映射不回写已有卡片。
- claim 所有者按 host/actor/session 全键比较。actor 为独立执行 UUID，session 可空。租约过期后普通任务修改须先重新 claim；本人有效 claim 才能续租、release、submit。close 只接受 pendingReview；reopen 只接受 pendingReview/closed，进行中撤回先 release。更新、移动、删除/恢复子树、改父项、附件写入、删列移卡统一检查受影响卡片的认领。评论允许协作写入，不取得任务执行权。
- 任意深度使用扁平 parentId 与迭代遍历；关闭检查所有活动后代，不级联关闭。关闭的父项不能新增或接入未关闭子项。排序按 columnId/parentId 分组。软删不删除评论或图片。
- schema v1 读取不落盘，首个成功写入保存 v2，保留原 ID/内容/位置/删除记录。原进行中卡片缺执行身份时状态置 notStarted，保留原列并返回迁移提示。旧版本拒绝 v2，发布说明要求升级前备份。
- 评论增加 `board.comment.get`，共 18 个新操作。评论父项必须属于同一卡片。回复 routing 为创建时最近祖先 origin 的快照；bind 仅影响之后创建的回复，已有回复显示其固定目标，不静默重定向。作者按完整执行身份比较；local-user 可软删除。
- 所有读响应提供 boardRevision/commentRevision。`revision` 仍是 board CAS，评论写额外接受 commentRevision CAS。评论变化只推进 commentRevision；内嵌时也保留此语义。board/card get/list 返回评论计数，不返回正文。评论分页按 createdAt/id 游标，游标绑定 commentRevision，版本变化返回 CONFLICT。
- 候选序列化严格大于 2,097,152 字节或单卡评论严格超过 500 条时自动外置 `.toolbox/comments.json`，包含触发写入。迁移使用固定文件的一次性 journal、旧 board 备份与新文件 hash；两新 hash 完整则提交，否则恢复旧 board，仅清除 hash 匹配的临时文件；未知 hash 拒绝写入并保留证据。之后评论单独原子更新 sidecar。IPC 仍限制 4 MiB。
- 图片按流限量并计算 hash，实际解码验证格式，再保存生成的 ID 路径；拒绝路径重解析与越界。添加保存失败清理新文件。删除先改名 tombstone 并写限定 journal，再删 metadata；恢复按 metadata 是否仍存在决定还原或清理，清理失败保留 journal。缺失/篡改只返回标记。
- comment.add 只保存评论并建立待确认投递记录；默认 confirm，只有用户明确设置的本机 always 才自动提交。发送确认绑定正文与 routing 快照，修改后须重新确认。无 session 时绝不使用 --last/-c/--continue，降级 host+project inbox；Qoder 官方 CLI 命令使用 qoder，未安装时保留 GUI pull。
- host-only inbox 领取按执行 actor 原子保留租约，ack 必须匹配目标和当前领取者；已投递不等于已处理。CLI 退出 0 记 submitted，ack 记 acknowledged；超时记 unknown，不自动重放。GUI 打开和复制分别由用户操作触发，不自动覆写剪贴板。
- 宿主探测读取本机配置、PATH/npm、URL 协议和卸载注册项、版本目录，失败允许手工配置；失效重探。原生 exe/Node entry 使用参数数组；不把 cmd/ps1 shim 当 exe，不拼接任意 shell 文本，不加执行策略/权限绕过参数。宿主测试全部使用假进程，不续接真实会话。
- 投递服务与存储分离，由共享 BoardAgentService 路由 comment.deliver/delivery，UI 与 IPC 走同一入口。Core Store 提供精确评论上下文查询；本机适配器与状态存 AppData。可用 hooks 的回注契约未验证时不安装，技能首步 pull。技能按用户授权的目标目录安装，不自动改宿主设置。
- 所有看板 MessageBox 改为主题化 Confirm/Notify，字段校验内联。Esc 取消；普通输入 Enter 提交，危险确认默认取消，不把删除设为 Enter 默认动作。保留对话框/拖放开始时 boardId、revision 快照。界面区分未绑定与 host 已绑定但缺精确 session。

审查结论：按本节修订后进入实现；运行与故障恢复验证按实施计划逐项记录。
