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
