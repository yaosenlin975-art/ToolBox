# ToolBox

面向 Windows 的桌面工具箱，将截图贴图、待办、剪贴板历史和常用工具集中在一个应用中。提供完整工作台和桌面迷你窗口，也可由 Agent 通过本地 CLI 调用。

基于 **.NET 8 + WPF**，支持浅色／深色主题与中英文界面。

## 主要功能

| 功能 | 说明 |
| --- | --- |
| 截图与贴图 | 区域截图、桌面悬浮贴图、截图历史与标签管理 |
| 待办与提醒 | 分类、标签、子任务、完成进度、截止时间、重复待办和回收站 |
| 剪贴板历史 | 保存、查找和管理历史内容，可配置保留数量与忽略应用 |
| 文字识别 | 本地 OCR；桌面界面还提供选中文字翻译 |
| 网络诊断 | 检查网络环境并提供相应修复入口 |
| 定时工具 | 定时截图、日报与定时关机 |
| 迷你窗口 | 快速查看待办和截图，支持折叠、靠边隐藏与握柄唤起 |
| 项目看板 | 数据随项目目录保存，支持列和卡片管理、拖放排序、回收站及复制到个人待办 |
| Agent CLI | 能力发现、个人待办、项目看板、指定图片本地 OCR、截图标签查询 |

迷你窗口靠近或部分超出屏幕外侧的任意一边，静置后先对齐工作区，再自动收起；指针留在窗口内时只回靠，移开后才收起。悬停或点击窄握柄可展开。动画支持中途反向，并遵循 Windows 的动画效果开关。页面切换、折叠和按钮也提供轻量反馈。

## 运行

需要 Windows 和 **.NET 8 Desktop Runtime**。工程目标框架为 `net8.0-windows10.0.19041.0`。

使用完整发布目录，双击 `ToolBox.exe`。应用通过托盘提供入口，可切换工作台和迷你窗口。

发布目录中的 `ToolBox.exe`、`toolbox-cli.exe`、依赖文件与原生运行库应一起保留，不能只复制一个可执行文件。

默认快捷键可在设置中修改：

| 快捷键 | 操作 |
| --- | --- |
| `Ctrl + 1` | 截图 |
| `Ctrl + 2` | 显示／隐藏全部贴图 |
| `Ctrl + Alt + N` | 网络诊断 |

## 从源码构建

安装 .NET 8 SDK，在仓库根目录运行：

```powershell
dotnet restore .\Project\ToolBox.csproj
dotnet build .\Project\ToolBox.csproj -c Release
dotnet run --project .\Project\ToolBox.csproj -c Release
```

生成包含桌面应用和 CLI 的发布目录：

```powershell
dotnet publish .\Project\ToolBox.csproj -c Release -o .\Project\bin\publish
```

这是依赖本机 .NET Runtime 的发布方式。CLI 会由工程自动构建并复制到同一输出目录。

## 给 Agent 使用

在发布目录打开 PowerShell，先查询能力及其输入结构：

```powershell
.\toolbox-cli.exe capabilities list
.\toolbox-cli.exe capabilities get task.create
.\toolbox-cli.exe task list --status pending
```

创建中文待办时，为原生命令 stdin 设置 UTF-8：

```powershell
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)
'{"title":"整理本周事项","category":"工作"}' |
    .\toolbox-cli.exe task create --input -
```

成功结果中的 `data.id` 是后续操作使用的完整任务 ID。业务命令默认输出 JSON；调用方需同时检查退出码和响应中的 `ok`，无需添加 `--json`。

CLI 在 ToolBox 未运行时会自动启动无窗口后台；之后正常打开 ToolBox 会在同一实例中显示界面。纯后台模式不启动截图、剪贴板监听或全局热键。

项目看板使用 `project` 和 `board` 命令，个人待办继续使用 `task`。首次初始化使用 `project register --path <目录> --init`；已有看板登记时省略 `--init`。项目数据位于 `.toolbox/board.json`，取消登记不删除文件。

详细命令、错误处理与能力边界见 [CLI 使用说明](Docs/task-cli-usage.md)。Agent 可使用 [cli-anything-toolbox 技能](Docs/skills/cli-anything-toolbox/SKILL.md)，发布包中也会附带 `skills/cli-anything-toolbox/SKILL.md`；按所用 Agent 的技能目录约定安装，并配置实际的 CLI 路径。

## 数据与联网

- 配置和待办位于 `%AppData%\ToolBox\`，包括 `config.xml`、`todos.json`。
- 截图缓存位于 `%LocalAppData%\ToolBox\`；备份时应保留配置、待办和所需缓存。
- CLI 的指定图片 OCR 使用本地引擎，缺少语言资源时不会自动下载。桌面翻译使用在线服务，会发送待翻译文字。
- 日报和贴图“识别待办”等 LLM 功能会向配置的模型服务发送相关内容；使用前检查服务端点和内容范围。
- CLI 通过当前 Windows 用户的本机命名管道访问数据。截图查询只返回标签元数据，不返回原图或内部文件路径。

## 项目结构

```text
Project/             WPF 主工程
  Core/              核心逻辑与本地后台
  Models/            配置和数据模型
  Services/          应用服务
  Views/             窗口与页面
  Themes/            主题、样式和设计令牌
  Cli/               命令行工程
  Tests/             可运行的专项检查
Docs/                设计、使用说明、变更记录与验收清单
refs/                只读参考资料
```

## 检查与文档

仓库包含独立检查程序，例如：

```powershell
dotnet run --project .\Project\Tests\ToolBox.TaskChecks.csproj
dotnet run --project .\Project\Tests\Transport\TransportChecks.csproj
dotnet run --project .\Project\Tests\Motion\MotionChecks.csproj
```

这些检查不代替实际窗口交互、观感和多屏验收。

- [总设计](Docs/design.md)
- [Agent CLI 设计](Docs/agent-cli-design.md)
- [项目看板设计](Docs/project-board-design.md)
- [项目看板实施计划](Docs/project-board-plan.md)
- [动效与靠边隐藏设计](Docs/motion-edge-hide-design.md)
- [变更日志](Docs/CHANGELOG.md)
- [人工验收清单](Docs/PENDING_TESTS.md)

## 许可证

本项目采用 [MIT License](LICENSE)。随应用分发的第三方组件遵循各自许可证，分发时请保留相应许可文件。
