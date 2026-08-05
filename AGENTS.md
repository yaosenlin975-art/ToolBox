# Project Rules

## Directory Structure

| 目录 | 用途 | 权限 |
|------|------|------|
| `refs/` | 参考项目/代码 | **禁止修改** - 只读参考 |
| `Project/` | 工程主体 | 读写 - 所有开发在此进行 |
| `Docs/` | 设计文件、计划文件 | 读写 - 文档集中存放 |

## Rules

1. **refs/ 目录**: 严禁任何修改操作。只能参考其中的代码和设计。
2. **Project/ 目录**: 所有工程代码、配置文件放这里。
3. **Docs/ 目录**: 所有设计文档、计划文件、API文档放这里。

## 项目信息

- **项目名称**: ToolBox (WPF 版)
- **目标框架**: .NET 8.0 Windows
- **技术栈**: WPF + C#
- **命名规范**: 见全局 AGENTS.md

---
*Project-specific rules*
4. **设计文档维护**: Docs/design.md 是项目总设计文档，仅在设计变更或功能更新时更新，不记录更新日志。
5. **UI 风格一致性**: 新增的界面、窗口、组件等所有用户交互元素必须与当前设计风格保持一致。参照 `Themes/DesignTokens.xaml` 中的设计令牌和现有 Views 中的样式规范。
   - 背景色: `BgPrimaryBrush` / `BgSecondaryBrush` / `BgTertiaryBrush`
   - 文本色: `TextPrimaryBrush` / `TextSecondaryBrush` / `TextTertiaryBrush`
   - 强调色: `AccentBrush`
   - 边框: `BorderBrush` + `CornerRadius="8"` + `BorderThickness="1"`
   - 卡片容器: `Border Background="{StaticResource BgSecondaryBrush}" CornerRadius="8" Padding="16"`
   - 字号规范: 标题 18 Bold / Section 标题 11 SemiBold Tertiary / 正文 13 / 辅助说明 11 Tertiary
   - 控件样式: 复用 `ModernRadioButton`、`ModernCheckBox`、`BtnDefault`、`BtnPrimary` 等已有样式

---

# 团队协作流程与交接规则（Multica）

> 本仓库所有开发任务通过 Multica issue 管理。任何状态变更都必须伴随交接动作，否则禁止流转。

## 核心原则

- `done` 状态的唯一下发权在测试（qa-test-engineer）手里；前端完成最多置 `in_review`
- 交接 = 状态变更 + 评论（含关键信息）+ `@` 对应 Agent
- 没有 @ = 没有交接 = 任务未完成

## 角色与权限

| 角色 | 权限 |
|------|------|
| 产品经理 | 创建 issue、澄清、验收标准、排期 |
| 前端 | 修复/实现、自测（含双主题）、请求验收（到 in_review 为止） |
| 测试（qa-test-engineer） | 验收、唯一有权置 done、发现缺陷回退 in_review；另以重度用户视角使用 App，发现交互不合理处反馈产品经理核对 |
| LLM 专项 | LLM 模块实现/优化（Agent 循环、Providers、工具系统、上下文压缩） |

## 人审规则

涉及 LLM 提示词内容、隐私边界（文件访问白名单、数据外发、会话数据持久化位置）的变更，必须先由产品经理提交用户确认，用户点头后方可实施。

## 交接规则

**前端完成（置 in_review 前）**：
1. 自测通过（验收标准逐条 + Light/Dark 双主题）
2. 评论写改动摘要（模块/文件、改动点、自测覆盖）
3. 评论中必须 @qa-test-engineer（mention://agent/6b82caf1-818d-4a84-b54d-2af47dccbb16）
4. 状态置 in_review。禁止直接置 done。

**测试发现缺陷**：
1. 评论包含：复现步骤 + 预期/实际 + 严重程度 + 证据
2. 评论中必须 @前端（mention://agent/db1f5153-60b8-4fe8-bb69-cc7148d45ed7）
3. 置 in_progress（Critical 置 blocked）。禁止只记录不 @人。

**QA 验收（in_review 下）**：
- 通过 → 留验收记录 → 置 done
- 不通过 → 缺陷清单 → 回退 in_progress + @前端
- 收到 @验收请求 4 小时内必须出结论

## 其他规则

- **进度可见性**：in_progress 后 24 小时内必须留第一条进度评论（进展 + 预计完成时间），之后每 24h 更新；卡住立即说明，禁止静默
- **接手时限**：被指派后 24 小时内置 in_progress 开工或说明原因
- **检查对象确认**：测试开工前确认被测对象（ToolBox vs 其他），有歧义先问，禁止猜测
- **代码库**：本仓库已绑定 Multica 项目资源（local_directory: D:\Workspaces\ToolBox），所有开发必须基于本仓库进行，禁止另建独立解决方案
- **提交规范**：完成即提交（git commit），提交信息描述改动内容
