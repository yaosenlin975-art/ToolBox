# 项目文件夹看板调研与设计

状态：调研与设计，待选型及实现范围确认。查阅日期：2026-10-14。

## 已确认需求

- 一个项目目录对应一个看板；数据存入目录内 `.toolbox/`，随目录复制、备份或 Git 提交。
- 看板任务与个人待办独立；支持将项目卡片加入个人待办。
- 在 ToolBox 工作台中提供看板，Agent 通过 CLI 增删改查。

## 当前工程边界

- ToolBox 为 .NET 8 Windows WPF；工作台通过 Sidebar、ContentControl 加载 WPF View。
- Todo 数据存于 `%APPDATA%/ToolBox/todos.json`，没有项目、工作流列和卡片排序字段。
- 现有 CLI 使用 System.CommandLine 2.0.2，通过同用户命名管道调用唯一 ToolBox 后台宿主；应用未启动时可自动启动后台模式。
- 现有宿主串行处理请求，TodoStore 使用原子文件替换；新看板复用这些入口和写入约定，不将 CLI 变成第二个写入者。
- 当前没有 WebView2；嵌入 Web UI 需新增 WebView2、运行时及前端交付流程。

源码入口：`Project/Cli/Program.cs`、`Project/Core/Todo/TaskProtocol.cs`、`TaskIpc.cs`、`TaskPipeHost.cs`、`TodoStore.cs`、`Project/Views/WorkbenchWindow.xaml.cs`。

## 方案说明

### A：原生 WPF 看板与现有 CLI 扩展（初步推荐）

复用 ToolBox 导航、主题、后台宿主、能力发现和 CLI。项目数据、看板列、卡片排序使用独立模型。GitHub 候选用于评估可复用范围；是否移植代码须经用户选择。

该方案没有嵌入浏览器和外部服务的交付成本，与现有单写入者约定一致。需要实现项目登记、卡片持久化与 WPF 看板交互。

### B：本地目录看板的 Web UI 嵌入

引入候选的存储或服务与 Web UI，并用 WebView2 承载。ToolBox CLI 调用同一后端，不独立改任务文件。须验证 Windows 发布、服务退出、端口访问控制和应用未启动时的后台 CRUD。

### C：完整项目管理服务

嵌入完整 Web 看板，增加项目路径绑定和 CLI 适配。服务数据库若为唯一数据源，必须另补项目目录携带数据的机制；仅将文件夹路径填入项目描述不能满足目录数据要求。

## 建议首版范围（待确认）

- 登记任意本地目录，不强制 Git 仓库；一个目录一个看板。
- 工作台项目切换、看板列、卡片详情、列内排序与跨列移动。
- 默认列为待办、进行中、待验收、完成；列可增删改及排序。
- 卡片标题、描述、优先级、标签、截止时间、软删除和恢复。
- 将卡片复制到个人待办，保留本机关联；两边的完成状态各自管理。双向同步不属于该推荐首版。
- Agent 管理项目、列和卡片的 CLI；普通用户操作与 CLI 调用进入同一应用服务。

## 数据结构

项目目录：`.toolbox/board.json`。

```json
{
  "schemaVersion": 1,
  "boardId": "稳定UUID",
  "name": "项目名称",
  "revision": 1,
  "columns": [{ "id": "稳定UUID", "title": "待办", "order": 0 }],
  "cards": [{
    "id": "稳定UUID",
    "title": "任务标题",
    "description": "",
    "columnId": "列UUID",
    "order": 0,
    "priority": "normal",
    "tags": [],
    "dueDate": null,
    "createdAt": "UTC时间",
    "updatedAt": "UTC时间",
    "deletedAt": null
  }]
}
```

卡片 ID 与排序、标题和文件路径分离。整板 revision 用于拒绝过期写入；没有声明 revision 的调用按当前状态处理，移动等读改写操作建议使用 revision。

ToolBox 用户目录保存项目登记索引和本机个人待办关联：`boardId / projectPath / cardId / todoId`。该索引不随项目目录提交。目录移动后重新登记；发现重复 boardId 时要求选择更新登记路径或新建独立看板，不自动合并。

## CLI 接口草案

```text
toolbox-cli project register --path <目录> [--init]
toolbox-cli project list|get|unregister
toolbox-cli board get --project <项目ID或路径>
toolbox-cli board column list|create|update|delete|move --project <项目>
toolbox-cli board card list|get|create|update|move|delete|restore --project <项目>
toolbox-cli board card add-to-todo --project <项目> --id <卡片ID>
```

具体参数在选型确认后固定。现有 `task` 命令继续操作个人待办。`capabilities` 增加看板能力；每个变更返回卡片稳定 ID、当前 revision 及结构化结果。

- `--json` 提供机器可读结果；失败通过非零退出码及结构化错误返回。
- 路径有空格时支持完整引用；显式 `--project` 优先。未指定项目时查找当前目录最近的已登记项目根，不跨出该根读取其他文件。
- `column.delete` 对有卡片的列要求显式目标列；不隐式删除卡片。
- `card.delete` 为软删除；`project.unregister` 只取消登记，不删除项目目录或看板文件。
- 宿主会话内写入请求采用 requestId 去重；跨宿主重启的去重需在实现方案中单独固定。过期 revision 返回 CONFLICT，不覆盖新数据。

## 关键流程

### 登记与加载

选择项目目录 → 明确初始化 `.toolbox/board.json` 或读取现有文件 → 校验 schema 与稳定 ID → 登记路径 → 显示看板。

登记授权范围为项目目录内 `.toolbox/` 看板数据。规范化项目及数据路径，解析后不得越出登记的项目根；只读目录和格式错误返回明确错误。不递归扫描源码，不执行项目脚本，不上传目录内容。

### UI / Agent 修改

CLI 或 WPF View → 同一后台宿主 → 校验项目、字段、revision → 更新内存与排序 → 同目录临时文件原子替换 → 返回结果并刷新看板。

Git checkout、文件缺失、外部文件修改或反序列化失败时重新校验；发现不一致返回冲突或数据错误，不用内存旧值覆盖项目文件。

### 加入个人待办

读取卡片快照 → 调用现有 Todo 创建入口 → 在本机索引记录关联 → 返回 Todo ID。重复调用使用本机关联避免重复创建；目录中的看板数据不携带个人待办内容。

### 应用未启动

CLI 启动现有后台宿主 → 加载项目登记 → 执行看板命令 → 返回结果。打开 ToolBox 时连接同一实例。

## 验证目标

- 无 Git 的目录以及包含空格、中文的路径可登记和操作。
- 看板文件随项目目录复制后可在另一台机器加载；个人待办关联保留在本机。
- UI 与两个 Agent 交错创建、移动和更新时不丢卡片、不出现重复 ID；过期更新明确返回冲突。
- 未启动 ToolBox 时 CLI CRUD 可用且不弹窗口；启动工作台后显示同一数据。
- 删除、恢复、列转移和取消项目登记的结果符合接口约定。
- 初始化和读写只涉及确认过的看板文件范围。

## GitHub 候选比较

验证等级：查阅官方 README、指定版本源码、发布页及测试入口；未 clone、安装、构建或运行。以下成本为相对集成范围，不是工时估算。

| 候选与固定版本 | 项目内数据 / Agent 入口 | ToolBox 兼容性与采用范围 | 许可证类型 | 成本与关键缺口 |
| --- | --- | --- | --- | --- |
| [Markban v0.4.5](https://github.com/OliverOrchard/markban/releases/tag/v0.4.5)，`5ed031c` | Markdown `work-items/`，可配置 rootPath；CLI create/list/show/move/rename/reorder；删除命令未确认 | .NET 10 Core/CLI、本地 Web UI；可参考文件组织，直接移植需降为 .NET 8 并另做 WPF 视图或 WebView2 | MIT | 中到高；文件编号兼作优先级，不能直接充当稳定卡片 ID |
| [Ordna 0.4.0](https://github.com/FreHilm/ordna/commit/1f08e00f150bd9d67cc162f8e77be4dcebd2d84a)，`1f08e00f150bd9d67cc162f8e77be4dcebd2d84a` | `tasks/*.md`、`.ordna/config.yaml`；Core create/list/get/update/move/delete；CLI/TUI/Web | TypeScript Core、Node、`runWeb()`；可复用 Core/Web，WPF 需 WebView2，`.toolbox/` 配置待验证 | MIT | 高；默认顺序 ID 存在多离线写入者冲突风险 |
| [kanban-rs v0.9.0](https://github.com/kanban-rs/kanban/releases/tag/v0.9.0)，`d5b594f` | CLI/KANBAN_FILE 可指定 JSON，支持 SQLite；board/card CLI、JSON 输出、TUI/MCP | Rust 独立程序，可将文件放 `.toolbox/`；ToolBox 可经进程调用，不能作为 C# 库直接引用 | Apache-2.0 | 中到高；需适配存储结构、统一写入流程及 WPF 视图；Windows 发布与并发行为未运行核验 |
| [Signboard 1.7.4](https://github.com/cdevroe/signboard/releases/tag/1.7.4)，`90ca02c` | 本地 board/list 目录与 card Markdown；CLI create/read/edit/move/archive、JSON、MCP | Electron/Node，存在 Windows 发布；可参考数据组织或独立调用 CLI，UI 不能直接当 WPF 组件 | MIT | 高；迁移前端或引入独立 Electron 进程，CLI 归档与删除语义须进一步固定 |
| [PLANKA v2.2.1](https://github.com/plankanban/planka/releases/tag/v2.2.1)，`266246e` | REST API、PostgreSQL 持久化；未发现项目目录 CLI | 完整 Web 服务及 Docker 部署；WPF 嵌入需 WebView2，项目内数据须额外设计 | PLANKA Community / Fair Use License | 高；服务数据库与目录携带数据不一致，仅作完整 Web 部署对照 |

### 源码证据

- Markban：[CLI](https://github.com/OliverOrchard/markban/blob/v0.4.5/Markban.Cli/Program.cs)、[WorkItemStore](https://github.com/OliverOrchard/markban/blob/v0.4.5/Markban.Core/WorkItemStore.cs)、[README](https://github.com/OliverOrchard/markban/blob/v0.4.5/README.md)。
- Ordna：[Core API 导出](https://github.com/FreHilm/ordna/blob/1f08e00f150bd9d67cc162f8e77be4dcebd2d84a/packages/core/src/index.ts)、[Store](https://github.com/FreHilm/ordna/blob/1f08e00f150bd9d67cc162f8e77be4dcebd2d84a/packages/core/src/store.ts)、[README](https://github.com/FreHilm/ordna/blob/1f08e00f150bd9d67cc162f8e77be4dcebd2d84a/README.md)。
- kanban-rs：[Card CLI handler](https://github.com/kanban-rs/kanban/blob/v0.9.0/crates/kanban-cli/src/handlers/card.rs)、[manifest](https://github.com/kanban-rs/kanban/blob/v0.9.0/Cargo.toml)、[README](https://github.com/kanban-rs/kanban/blob/v0.9.0/README.md)。
- Signboard：[CLI 文档](https://github.com/cdevroe/signboard/blob/1.7.4/docs/signboard-cli.md)、[package.json](https://github.com/cdevroe/signboard/blob/1.7.4/package.json)、[MCP 文档](https://github.com/cdevroe/signboard/blob/1.7.4/MCP_README.md)。
- PLANKA：[Card create controller](https://github.com/plankanban/planka/blob/v2.2.1/server/api/controllers/cards/create.js)、[Compose](https://github.com/plankanban/planka/blob/v2.2.1/docker-compose.yml)、[许可证](https://github.com/plankanban/planka/blob/v2.2.1/LICENSE.md)。

## 推荐与待确认决策

推荐采用 A：原生 WPF 看板、项目内 JSON 数据及现有 ToolBox CLI/后台宿主扩展。参考 Markban 的目录组织与 kanban-rs 的命令结构，不直接移植候选代码，不引入额外服务运行时。

若优先复用现成 Web UI，选择 B 并先对 Ordna 做最小试验：Windows 上启动项目目录 Core/Web、确认自定义数据目录及全部 CRUD，再用 WebView2 加载，验证关闭界面后的后台调用。

实施前确认选型及首版范围，包括登记目录内 `.toolbox/` 的读写范围。选择移植候选代码或完整项目时，同时确认 LICENSE、NOTICE、licenses 等文件的保留范围。
