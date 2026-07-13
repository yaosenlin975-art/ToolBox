# 代码片段管理

- **优先级**：P1
- **实现难度**：中
- **预估工作量**：5 人天
- **来源竞品**：Raycast（Snippets 关键字触发插入）

---

## 功能描述

为 ToolBox 增加代码片段/文本片段管理。用户可以保存常用的代码模板、文本片段、邮件模板、命令行脚本等，每条片段关联一个触发关键字。通过全局关键字监听（如输入 `;email` 后自动展开为完整邮件模板），或在迷你窗口中快速搜索粘贴。片段支持语法高亮预览、变量占位符（`${1:placeholder}`）、分类管理。

核心交互：
1. **片段库管理**：工作台新增 `snippets` 页面，支持分类、搜索、CRUD
2. **快捷粘贴**：全局热键 `Ctrl+Shift+S` 唤起片段搜索浮窗，选择后自动粘贴到当前焦点应用
3. **关键字展开**：在任意应用中输入触发关键字（如 `;sig`）→ ToolBox 监听 → 自动替换为完整片段
4. **LLM 协同**：AI 助手对话中可搜索和引用代码片段
5. **剪贴板转片段**：剪贴板历史中的内容可一键保存为片段

---

## 技术方案

### 片段存储

JSON 文件 `%AppData%/ToolBox/snippets.json`，支持最多 500 条片段。

```json
{
  "snippets": [
    {
      "id": "abc123",
      "name": "邮件签名",
      "content": "Best regards,\n${1:Your Name}\n${2:Title} | ToolBox Team",
      "trigger": ";sig",
      "language": "plaintext",
      "category": "邮件",
      "isFavorite": true,
      "useCount": 42,
      "createdAt": "2026-07-14T10:00:00Z",
      "updatedAt": "2026-07-14T15:00:00Z"
    }
  ],
  "categories": ["邮件", "代码", "命令行", "模板", "emoji"]
}
```

### 关键字展开

通过全局键盘钩子（`SetWindowsHookEx WH_KEYBOARD_LL`）监听用户输入。当检测到触发关键字（以分号或自定义前缀开头，后跟空格或Enter），自动：
1. 删除已输入的触发关键字（通过 `SendKeys.SendWait("{BACKSPACE}")` N 次）
2. 粘贴展开后的片段内容（通过 `Clipboard.SetText` + `SendKeys.SendWait("^v")`）

**性能考量**：低层键盘钩子在每个按键时触发，需极轻量处理（< 1ms）。维护一个环形缓冲区记录最近 30 个字符输入，仅在检测到触发前缀时才查找片段。

### 语法高亮

片段的语法高亮预览使用 MdXaml（已集成）的代码块渲染能力。编辑时不提供实时高亮，仅预览时渲染。

### 变量占位符

支持 `${1:placeholder}` 语法（Sublime Text/VSCode 风格）：
- `${1}` `${2}` ... 表示 Tab 键切换的占位符
- `${1:默认文本}` 表示带默认值的占位符
- 展开后光标停在 `${1}` 位置

### 依赖评估

无新增 NuGet 依赖。关键字展开用 Win32 `SetWindowsHookEx`；语法高亮预览复用 MdXaml；片段存储用 JSON。

---

## 模块设计

### 文件位置

```
Core/
├── Snippets/
│   ├── SnippetItem.cs                 # 片段数据模型
│   ├── SnippetStore.cs                # 片段持久化 + CRUD
│   └── SnippetExpander.cs             # 关键字展开引擎（键盘钩子 + 替换逻辑）
├── Tools/
│   └── SnippetTools.cs                # [Tool] 标记，供 LLM 搜索/获取片段
Views/
├── Snippets/
│   ├── SnippetView.xaml/.cs           # 工作台 snippets 页
│   ├── SnippetEditor.xaml/.cs         # 片段编辑弹窗
│   ├── SnippetPopup.xaml/.cs          # 热键唤出的片段搜索浮窗
│   └── SnippetCard.xaml/.cs           # 片段列表卡片控件
```

### 核心接口

```csharp
// Core/Snippets/SnippetItem.cs
namespace ToolBox.Core.Snippets;

public class SnippetItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = string.Empty;         // 片段名称
    public string Content { get; set; } = string.Empty;      // 片段内容（含变量占位符）
    public string? Trigger { get; set; }                     // 触发关键字（如 ";sig"）
    public string Language { get; set; } = "plaintext";      // 语言标识（用于语法高亮）
    public string Category { get; set; } = "默认";
    public bool IsFavorite { get; set; }
    public int UseCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

// Core/Snippets/SnippetStore.cs
namespace ToolBox.Core.Snippets;

public class SnippetStore
{
    public static SnippetStore Instance { get; }

    public IReadOnlyList<SnippetItem> Items { get; }
    public IReadOnlyList<string> Categories { get; }

    public SnippetStore Add(SnippetItem item);
    public SnippetStore Update(SnippetItem item);
    public SnippetStore Delete(string snippetId);
    public SnippetStore IncrementUseCount(string snippetId);

    /// <summary>按触发关键字查找</summary>
    public SnippetItem? FindByTrigger(string trigger);

    /// <summary>全文搜索</summary>
    public List<SnippetItem> Search(string keyword, string? category = null);

    /// <summary>按分类筛选</summary>
    public List<SnippetItem> FilterByCategory(string category);

    public event Action? ItemsChanged;
}

// Core/Snippets/SnippetExpander.cs
namespace ToolBox.Core.Snippets;

public class SnippetExpander : IDisposable
{
    /// <summary>展开片段中活跃（关键字监听是否启用）</summary>
    public bool IsActive { get; set; }

    /// <summary>展开后的内容（含解析后的占位符）</summary>
    public string Expand(SnippetItem snippet);

    /// <summary>开始全局键盘监听</summary>
    public SnippetExpander Start();

    /// <summary>停止监听</summary>
    public SnippetExpander Stop();

    public void Dispose();
}
```

### LLM Tool 暴露

```csharp
// Core/Tools/SnippetTools.cs
namespace ToolBox.Core.Tools;

public static class SnippetTools
{
    [Tool("search_snippets", "搜索代码片段库")]
    public static string SearchSnippets(
        [ToolParam("搜索关键词")] string keyword = "",
        [ToolParam("分类筛选")] string category = "");

    [Tool("get_snippet", "获取指定代码片段的完整内容")]
    public static string GetSnippet(
        [ToolParam("片段名称或触发关键字")] string nameOrTrigger);

    [Tool("add_snippet", "保存新的代码片段")]
    public static string AddSnippet(
        [ToolParam("片段名称")] string name,
        [ToolParam("片段内容")] string content,
        [ToolParam("触发关键字（如 ;sig）")] string trigger = "",
        [ToolParam("分类")] string category = "默认");
}
```

---

## 数据流

```
片段创建:
  SnippetView → [+ 新建片段] → SnippetEditor
    ├─ 名称: "HTTP GET 请求模板"
    ├─ 触发: ";httpget"
    ├─ 内容: "fetch('${1:url}').then(r=>r.json()).then(d=>${2:console.log(d)})"
    ├─ 语言: javascript
    └─ 分类: 代码
    → SnippetStore.Add(item) → JSON 持久化

快捷粘贴:
  Ctrl+Shift+S → SnippetPopup 浮窗 (360×480, Topmost, 居中)
    ├─ 搜索框 (实时过滤) + 分类筛选
    ├─ 列表: 名称 + 触发关键字 + 使用频次 + 语言标签
    ├─ 点击 / Enter → 展开片段
    │     ├─ 解析变量占位符
    │     ├─ Clipboard.SetText(expandedContent)
    │     └─ SendKeys.SendWait("^v") → 粘贴到前台应用
    └─ ESC → 关闭

关键字展开:
  用户在任意应用输入: ";sig " (触发关键字 + 空格)
    │
    ▼
  SnippetExpander (WH_KEYBOARD_LL 钩子, 环形缓冲区)
    ├─ 检测到触发前缀 → 查找 SnippetStore.FindByTrigger(";sig")
    ├─ 找到片段 → Expand(snippet) → 解析占位符
    ├─ SendKeys 删除触发关键字 (6 次 Backspace)
    └─ Clipboard.SetText + SendKeys ^v 粘贴展开内容
```

---

## UI 设计要点

### SnippetView（工作台页面）
- 布局：与 screenshots 页一致的网格视图
- 左侧：分类列表（`ModernListBox`）+ 搜索框（`InputField`）
- 右侧：4 列卡片网格，每张卡片显示：
  - 片段名称（14px Bold, TextPrimaryBrush）
  - 触发关键字（11px Mono, AccentBrush）
  - 内容预览（前 3 行，11px Mono, TextTertiaryBrush, 截断）
  - 底部：语言标签 + 使用次数
- 复用：`CardStyle` Border 作为卡片容器
- 侧边栏导航：新增 `📝 代码片段` 第 7 个 RadioButton

### SnippetPopup（搜索浮窗）
- 尺寸：360×480px，`WindowStyle=None, Topmost=True`，圆角 14，`ShadowFloat`
- 布局：搜索框（顶部固定）+ 分类筛选（FilterTabStyle）+ 列表
- 列表项样式：名称（13px）+ 触发关键字（11px Accent）+ 语言标签
- 选中项：`AccentSoftBrush` 背景

### SnippetEditor（编辑弹窗）
- 尺寸：500×400px，模态窗口
- 字段：名称（InputField）、触发关键字（InputField, MonoFont）、语言（ComboBox）、分类（ComboBox）、内容（TextBox, MonoFont, 多行, 最小高度 200px）
- 按钮：`[保存]` BtnPrimary / `[取消]` OutlineButton
- 内容区支持 Tab 键输入（设置 `AcceptsTab=True`）

---

## 与现有架构的集成

| 集成点 | 方式 |
|--------|------|
| **工作台** | 侧边栏新增 `snippets` 导航项 + PageHost 路由 |
| **热键体系** | `HotkeyManager` 注册 `Ctrl+Shift+S` → 唤起 SnippetPopup |
| **剪贴板联动** | ClipboardView 右键菜单新增 "保存为片段" |
| **LLM 系统** | SnippetTools 暴露搜索/获取/保存接口 |
| **键盘钩子** | SnippetExpander 使用 WH_KEYBOARD_LL（独立于 HotkeyManager 的 RegisterHotKey） |
| **持久化** | SnippetStore 保存到 `%AppData%/ToolBox/snippets.json` |
| **设置页面** | SettingsView 新增 "📝 代码片段" Section：启用关键字展开 / 触发前缀 / 最大片段数 |
| **主题适配** | 代码背景 `BgSunkenBrush`，代码文字 `TextPrimaryBrush`，关键字 `AccentBrush` |

---

## 风险与注意事项

1. **关键字冲突**：触发关键字可能与正常输入冲突（如用户真的想输入 `;sig`）。缓解：触发前缀可自定义（默认 `;`）；仅在检测到前缀+关键字+空格/Enter时才展开；可设置"排除的应用列表"
2. **键盘钩子安全**：全局键盘钩子可能被安全软件标记。缓解：使用 `WH_KEYBOARD_LL` 低层钩子（不需要管理员权限）；仅在用户启用关键字展开功能时安装钩子
3. **粘贴失败**：`SendKeys.SendWait("^v")` 在某些应用中可能不可靠。缓解：自动将片段写入剪贴板，用户可手动 Ctrl+V
4. **变量占位符复杂度**：Tab 键切换占位符在外部应用中无法控制光标。缓解：简化——展开后仅保留第一个占位符为选中状态文本，用户需手动替换
5. **片段内容安全**：用户可能保存密码/Token等敏感信息为片段。缓解：设置中增加"敏感片段加密存储"选项（v2）
