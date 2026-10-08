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

cli-anything 适配技能源位于 `Docs/skills/cli-anything-toolbox/SKILL.md`；完整发布目录附带 `skills/cli-anything-toolbox/SKILL.md`。技能复用已有 CLI，业务命令默认输出 JSON，无需添加 --json。

将 `cli-anything-toolbox` 文件夹复制到使用中的 Agent 技能目录。例如在发布目录执行：

```powershell
$skillHome = Join-Path $env:USERPROFILE '.agents/skills/cli-anything-toolbox'
New-Item -ItemType Directory -Force -Path $skillHome | Out-Null
Copy-Item -LiteralPath ./skills/cli-anything-toolbox/SKILL.md -Destination (Join-Path $skillHome 'SKILL.md')
```

Agent 重新加载技能后可使用 `$cli-anything-toolbox`。可通过 `TOOLBOX_CLI_PATH` 指定 toolbox-cli.exe 的完整路径，或使用已配置的 PATH；在本仓库也可使用 Project/bin/agent-cli-publish 下的完整发布包。技能本身不包含可执行程序，不会替代 ToolBox，也不授予额外数据访问权限。

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
