# ToolBox Agent CLI 使用说明

`toolbox-cli.exe` 与 `ToolBox.exe` 必须放在同一个发布目录。命令通过本机当前用户管道操作原待办；ToolBox 未启动时自动启动后台模式，不弹窗口。正常双击 ToolBox 会唤出该实例的界面。后台不会启动截图、剪贴板或全局热键，也不显示重复待办通知。

## 创建和修改

保存 UTF-8 `create.json`：

```json
{"title":"提交周报","description":"核对本周结果","priority":1,"category":"工作","tags":["周报"],"dueDate":"2026-10-09T18:00:00+08:00"}
```

```powershell
& 'C:\Tools\ToolBox\toolbox-cli.exe' task create --input .\create.json
```

从成功响应 `data.id` 获取精确 12 位 ID，再修改：

```json
{"description":"","tags":[],"dueDate":null,"progress":50}
```

```powershell
& 'C:\Tools\ToolBox\toolbox-cli.exe' task update 012345abcdef --input .\update.json
```

省略字段保持原值；空字符串清空描述，空数组清空标签，null 清空截止时间。标题和分类不能为空，priority 为 0/1/2，progress 为 0–100 整数。父节点进度由子项计算，不能手填。日期必须包含时区，不从标题自动推断。

stdin 输入：

```powershell
'{"title":"检查发布包"}' | & 'C:\Tools\ToolBox\toolbox-cli.exe' task create --input -
```

输入只接受单个 JSON 对象，未知/重复字段会报错。`parentId` 可在创建时指定已存在且未废弃的父待办。

## 查询、完成和回收站

```powershell
toolbox-cli.exe task list
toolbox-cli.exe task list --status pending
toolbox-cli.exe task get 012345abcdef
toolbox-cli.exe task complete 012345abcdef
toolbox-cli.exe task reopen 012345abcdef
toolbox-cli.exe task delete 012345abcdef
toolbox-cli.exe task list --status trashed
toolbox-cli.exe task restore 012345abcdef
```

list 的 status 支持 all/pending/completed/trashed/repeating，默认 all 排除回收站项。输出为扁平列表并保留 parentId。get 可读回收站项；ID 必须完整，不支持模糊匹配。

delete 将当前项及子树移入回收站，不永久删除；重复删除不重复处理。恢复子项前须先恢复其废弃祖先。父项完成前须先完成有效子项；reopen 保留已有进度。重复配置可读取，首版不支持通过 CLI 写入重复规则、移动任务或永久删除。

## 能力发现、本地 OCR 与截图标签

```powershell
toolbox-cli.exe capabilities list
toolbox-cli.exe capabilities get ocr.file
'{"path":"C:\\Images\\example.png","language":"eng"}' | toolbox-cli.exe ocr file --input -
'{"tag":"工作","minRating":4,"limit":20}' | toolbox-cli.exe screenshot search --input -
```

能力发现返回真实命令的输入 schema、输出字段、副作用与运行条件，不启动后台、不读取用户数据、不联网。availability.runtimeChecked=false 表示尚未在当前机器验证引擎等运行条件，不代表安装资源已经可用。

OCR 仅处理指定的绝对本地图片路径；拒绝 UNC/设备/网络驱动器、重解析点和相对路径段。输入上限 16 MiB、边长 16384、总像素 3200 万。language 可省略（默认 chi_sim+eng）；仅使用本机 Tesseract/WindowsOCR，缺包不下载、不翻译、不改剪贴板或写历史。结果含 engine/text/lines（文字、置信度和像素矩形）；WindowsOCR 的置信度为 -1，语言回退沿用本机 Windows 配置，不保证多语言组合全部支持。

截图查询只搜索已有标签元数据，不做 OCR 全文检索。支持 tag、minRating（0–5）、colorHue（0–359）、limit（默认 20，1–100）、cursor。返回 items/nextCursor；items 仅含 cacheKey/tags/rating/notes/taggedAt，cacheKey 是 17 位数字截图标识，不含内部绝对目录或原图。用 nextCursor 作为下一页 cursor，并保持相同筛选条件；返回 null 表示结束。分页按标识 ordinal 排序，查询期间数据变化不提供历史快照隔离。

## 输出与退出码

业务命令的 stdout 始终是一个 JSON 对象，成功时 `ok:true`，结果位于 data；失败时 `ok:false`，error 含 code/message，字段错误还可能含 field。诊断写 stderr。`--help`、`--version` 输出文本且不启动应用。

| 退出码 | 含义 |
|---|---|
| 0 | 成功 |
| 2 | 命令或输入字段不合法 |
| 3 | 任务、能力或指定文件不存在 |
| 4 | 状态冲突、结果超限或 OCR 引擎不可用 |
| 5 | 后台不可用、超时、访问拒绝 |
| 6 | 数据损坏或持久化失败 |
| 7 | 协议或内部错误 |

例如在 PowerShell 中先检查 `$LASTEXITCODE`，再读取 JSON 的 ok/error，避免把失败数据当作成功结果。单次 JSON 请求/响应最大 4 MiB（不含管道帧末尾换行）；不会截断结果冒充完整列表。

启动等待上限 10 秒，发送请求后等待响应上限 30 秒。写入已经发送后发生超时/断连时，结果可能已保存；应先查询确认，不要直接重试 create。客户端不会自动重放写请求。

30 秒限制是调用方等待时间；已进入原生 Tesseract 的计算不能强制中断，完成前其他后台请求可能等待。OCR 输入已有资源上限，本版不额外创建隔离工作进程。

## 后台生命周期和故障

```powershell
toolbox-cli.exe host stop
```

该命令等待后台请求处理完后退出；后台不存在时直接成功。界面已打开时拒绝停止，请从 ToolBox 的退出菜单关闭。CLI 不修改 PATH、不注册服务或开机启动。

若旧版 ToolBox 占用单实例锁但没有管道，请退出旧版后重试。CLI 与应用须使用同一个 Windows 账号和兼容的提权级别；访问失败不会自动提权或绕过锁直接写文件。

待办仍保存到 `%APPDATA%\ToolBox\todos.json`。损坏文件不会自动覆盖成空数据；请先备份再恢复有效 JSON。保存失败会保留此前的文件并恢复内存状态；恢复失败后拒绝继续命令，须排除故障并重启。

## Agent 技能安装

cli-anything 适配技能源位于 `Docs/skills/cli-anything-toolbox/SKILL.md`，看板协作技能源位于 `Docs/skills/toolbox-board/SKILL.md`；完整发布目录附带 `skills/cli-anything-toolbox/SKILL.md` 与 `skills/toolbox-board/SKILL.md`。技能复用已有 CLI，业务命令默认输出 JSON，无需添加 --json。

将 `cli-anything-toolbox` 文件夹复制到使用中的 Agent 技能目录。例如在发布目录执行：

```powershell
$skillHome = Join-Path $env:USERPROFILE '.agents/skills/cli-anything-toolbox'
New-Item -ItemType Directory -Force -Path $skillHome | Out-Null
Copy-Item -LiteralPath ./skills/cli-anything-toolbox/SKILL.md -Destination (Join-Path $skillHome 'SKILL.md')
```

Agent 重新加载技能后可使用 `$cli-anything-toolbox`。看板协作技能同理安装到 `toolbox-board`（把上例路径中的 `cli-anything-toolbox` 换为 `toolbox-board`）。可通过 `TOOLBOX_CLI_PATH` 指定 toolbox-cli.exe 的完整路径，或使用已配置的 PATH；在本仓库也可使用 Project/bin/agent-cli-publish 下的完整发布包。技能本身不包含可执行程序，不会替代 ToolBox，也不授予额外数据访问权限。

## 构建与检查

```powershell
dotnet build Project/ToolBox.slnx
dotnet run --project Project/Tests/ToolBox.TaskChecks.csproj
dotnet run --project Project/Tests/Transport/TransportChecks.csproj
dotnet run --project Project/Tests/Tags/ScreenshotTags.Check.csproj
pwsh -STA -File Project/Tests/task-cli-integration.ps1
dotnet publish Project/ToolBox.csproj -c Release -o ./Project/bin/agent-cli-publish
```

构建/发布会将 CLI 的运行文件与 System.CommandLine 2.0.2 一同放入桌面输出目录；分发时保留 `THIRD-PARTY-NOTICES.txt` 中的 MIT 声明。

仅 Debug 构建支持开发检查环境变量 `TOOLBOX_TASK_TEST_ROOT`，同时隔离待办/标签文件、管道和单实例锁。集成脚本自动设置该变量，Release 始终忽略它并使用原用户目录。该变量不隔离完整桌面模式的其他设置，因此自动集成脚本只验证后台模式；双主题和实际界面唤出按 PENDING_TESTS 人工验收。


## 项目文件夹看板

项目命令使用 `project`，项目卡片使用 `board`，个人待办使用 `task`。请求参数可从 `capabilities get <operation>` 查询。

```powershell
.\toolbox-cli.exe capabilities get project.register
.\toolbox-cli.exe project list
# 用户要求初始化项目看板时执行
.\toolbox-cli.exe project register --path 'D:\Projects\My App' --init
.\toolbox-cli.exe board get --project 'D:\Projects\My App'
.\toolbox-cli.exe board card create --project 'D:\Projects\My App' --title '实现登录校验'
.\toolbox-cli.exe board card list --project 'D:\Projects\My App'
```

- `project register` 登记目录；已有 `.toolbox/board.json` 时省略 `--init`。重复 boardId 换路径需明确 `--replace-path`。
- `board column` 提供 list/create/update/delete/move；`board card` 提供 list/get/create/update/move/delete/restore/add-to-todo。
- `--project` 接受登记 ID 或路径；未指定时从 `--cwd` 或客户端当前目录寻找最近的已登记项目。
- 新命令接受 `--input <文件|->` 和 `--json`，默认输出 JSON。JSON 与 flags 的同名字段不可同时提供。
- 卡片和列使用 UUID；移动与排序的快照写入可附 `--revision`。CONFLICT 后重新读取再决定修改。
- `--tags` 后多值设置标签，无值清空；`--clear-due-date` 清空日期。
- `card delete` 软删除，`restore` 恢复；删除有卡片的列需指定 `--target-column-id`，取消项目登记不删除文件。
- `add-to-todo` 返回个人 Todo ID，复制快照后状态独立。关联保存失败时按错误中的 ID 核对，pending 状态不自动重试创建。

### 四态与认领

```powershell
.\toolbox-cli.exe board card claim   --project <项目> --id <卡片> --actor <agent> --host <宿主> [--session <会话>] [--ttl-minutes 30]
.\toolbox-cli.exe board card release --project <项目> --id <卡片> --actor <agent> --host <宿主>
.\toolbox-cli.exe board card submit  --project <项目> --id <卡片> --actor <agent> --host <宿主>
.\toolbox-cli.exe board card close   --project <项目> --id <卡片> --actor <agent> --host <宿主>
.\toolbox-cli.exe board card reopen  --project <项目> --id <卡片> --actor <agent> --host <宿主>
```

- claim/release/submit/close/reopen 必须解析出 actor：显式 `--actor` 或环境变量 `TOOLBOX_BOARD_ACTOR`，两者都缺返回 `INVALID_ARGUMENT`（退出码 2）。`--host` 是必填的显式宿主标识，不会被推断。
- 默认租约 30 分钟，`--ttl-minutes` 允许 5–480；同 actor 重复 claim 为续租心跳。他人有效租约返回 `CONFLICT/CLAIMED_BY_OTHER`（退出码 4），`data` 带 `claimedBy` 与 `expiresAt`。
- 转换可一并携带目标列与排序：`--column-id`/`--order` 与状态同次提交。
- `board card list` 支持 `--status`、`--parent-id`、`--roots-only`、`--available`（未开始或租约过期）、`--mine`（需同时给 `--actor` 与 `--host`）。`board card create`/`update` 用 `--parent-id` 设父项、`--clear-parent-id` 解除；父项须存在且未删除，改父项做环检测（`CYCLE_DETECTED` 归 `CONFLICT`）。

### 评论与回复

```powershell
.\toolbox-cli.exe board comment add    --project <项目> --id <卡片> --body <文本> [--parent-id <评论>] [--actor <agent>] [--host <宿主>] [--session <会话>]
.\toolbox-cli.exe board comment list   --project <项目> --id <卡片> [--include-deleted] [--limit 50] [--cursor <游标>]
.\toolbox-cli.exe board comment get    --project <项目> --comment-id <评论>
.\toolbox-cli.exe board comment update --project <项目> --comment-id <评论> --body <文本> [--actor <agent>]
.\toolbox-cli.exe board comment delete --project <项目> --comment-id <评论> [--actor <agent>]
```

```powershell
.\toolbox-cli.exe board comment inbox    --project <项目> --host <宿主> --actor <agent> [--session <会话>] [--limit 20]
.\toolbox-cli.exe board comment ack      --project <项目> --comment-id <评论> --claim-id <收件租约> --actor <agent> --host <宿主>
.\toolbox-cli.exe board comment bind     --project <项目> --comment-id <评论> --actor <agent> --host <宿主> [--target-host <宿主>] [--target-session <会话>] [--clear]
.\toolbox-cli.exe board comment deliver  --project <项目> --comment-id <评论> [--confirm --confirmation-token <hash>] [--verified-unknown]
.\toolbox-cli.exe board comment delivery --project <项目> --comment-id <评论>
```

- 评论正文 UTF-8 ≤ 16 KiB。评论写接受 `--comment-revision`（评论集 CAS）；列表 `--limit` 为 1–100，游标绑定 commentRevision，版本变化返回 `CONFLICT`。
- 回复 routing 取父评论 origin 快照；`bind` 只影响之后创建的回复。`inbox` 须同时给 `--host` 与 `--actor`（`--session` 可选），按时间升序返回未确认回复。
- `deliver` 默认需确认：先用 `board comment delivery` 取 confirmation hash，授权后传 `--confirm --confirmation-token <hash>`；超时后结果未知需先核对，再传 `--verified-unknown` 重试。

### 图片附件

```powershell
.\toolbox-cli.exe board attachment add    --project <项目> --id <卡片> --file <绝对路径> [--actor <agent>]
.\toolbox-cli.exe board attachment list   --project <项目> --id <卡片>
.\toolbox-cli.exe board attachment remove --project <项目> --attachment-id <附件> [--actor <agent>]
```

- 仅接受本地绝对路径（相对路径按客户端工作目录解析），拒绝重解析点与越界；扩展名限 `png/jpg/jpeg/gif/bmp`，单张 ≤ 10 MiB；同卡片同 SHA256 直接返回已有项。
- `list` 返回元数据与 `exists` 计算字段（文件缺失或哈希不符时标记）。

### 看板错误码与退出码

| 退出码 | 场景 |
|---|---|
| 2 INVALID_ARGUMENT | 缺 actor/必填字段、ttl 越界（非 5–480）、非法 status/transition、正文超 16 KiB |
| 3 NOT_FOUND | 项目/卡片/列/评论/附件不存在，或指定文件不存在 |
| 4 CONFLICT | 他人有效认领 `CLAIMED_BY_OTHER`、环 `CYCLE_DETECTED`、revision/hash 冲突、父项有未关闭子项、结果超限 `RESULT_TOO_LARGE` |
| 6 IO_ERROR / DATA_CORRUPT | 附件写入或删除失败、旧版读取 v2 或数据损坏 |

- 所有读响应都带 `boardRevision` 与 `commentRevision`；收到 `CONFLICT` 后重新读取再决定，不盲目重放。单次 IPC 上限仍为 4 MiB。

完整参数与边界见 [Agent 技能](skills/cli-anything-toolbox/SKILL.md) 与 [看板技能](skills/toolbox-board/SKILL.md)；存储与接口见 [项目看板设计](project-board-design.md)、[看板 v2 设计](project-board-v2-design.md)。
