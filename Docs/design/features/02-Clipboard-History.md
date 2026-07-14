# 剪贴板历史管理

- **优先级**：P0
- **实现难度**：中
- **预估工作量**：6 人天
- **来源竞品**：Raycast（可视/可搜/无限历史）、uTools（剪贴板插件）

---

## 功能描述

为 ToolBox 增加全系统剪贴板历史监听与管理。后台持续监听 Windows 剪贴板变化，自动记录文本/图片/文件三种类型的内容。用户通过热键 `Ctrl+Shift+V` 唤起剪贴板面板，浏览、搜索、置顶、收藏历史项，点击即可粘贴到当前焦点应用。同时在工作台增加"剪贴板"页面，与截图历史页面共享筛选/搜索 UI 模式。

核心交互：
1. **全局热键 `Ctrl+Shift+V`**：在屏幕中央弹出剪贴板浮窗，显示最近 50 条历史
2. **工作台 clipboard 页**：侧边栏第 6 个导航项，展示完整历史（支持无限滚动）
3. **智能去重**：连续复制相同内容不重复记录
4. **类型筛选**：文本 / 图片 / 文件路径 三种 Tab
5. **与 AI 协同**：选中剪贴板条目可"发送到 AI 助手"进行分析

---

## 技术方案

### 剪贴板监听

采用 **Win32 Clipboard Chain**（`SetClipboardViewer` + `WM_DRAWCLIPBOARD`）方案，在 WPF 中通过 `HwndSource.AddHook` 获取窗口消息。

**为什么不直接用 `Clipboard.GetText/GetImage` 轮询**：轮询消耗高、有遗漏窗口。消息链机制由系统推送，实时且准确。

**包含内容处理**：文本（`CF_UNICODETEXT`）、图片（`CF_BITMAP` → `BitmapSource`）、文件列表（`CF_HDROP`）。

### 持久化方案

简单文本/文件路径：JSON 文件 `%AppData%/ToolBox/clipboard.json`，上限 500 条，环形覆盖。

图片缩略图：存储到 `%LocalAppData%/ToolBox/clipboard_thumbnails/`，以 GUID 命名。原图不存储（仅存缩略图 200×200 JPEG，原始图片通过 re-render 方式从系统剪贴板获取）。

**为什么不用 SQLite**：剪贴板历史数据量小（单条 < 2KB 文本，缩略图另存为文件），JSON 文件足够。如果未来要支持全文搜索 10000+ 条历史，再迁移到 SQLite。

### 依赖评估

无新增 NuGet 依赖。全部基于 .NET Framework/WPF 原生能力 + Win32 P/Invoke。

| 能力 | 技术 |
|------|------|
| 剪贴板监听 | `user32.dll` → `SetClipboardViewer` / `WM_DRAWCLIPBOARD` / `WM_CHANGECBCHAIN` |
| 文本获取 | `Clipboard.GetText(TextDataFormat.UnicodeText)` |
| 图片获取 | `Clipboard.GetImage()` → `BitmapSource` → JPEG 缩略图 |
| 文件列表 | `Clipboard.GetFileDropList()` |
| 粘贴到前台 | `SendKeys.SendWait("^v")` 或 `keybd_event`（备选） |

---

## 模块设计

### 文件位置

```
Core/
├── Clipboard/
│   ├── ClipboardEntry.cs           # 剪贴板条目模型
│   ├── ClipboardMonitor.cs         # Win32 剪贴板链监听器
│   └── ClipboardStore.cs           # 持久化管理（JSON + 缩略图文件）
├── Tools/
│   └── ClipboardTools.cs           # [Tool] 标记，供 LLM 搜索剪贴板
Views/
├── Clipboard/
│   ├── ClipboardPopup.xaml/.cs     # 热键唤出的浮动面板（迷你窗口样式）
│   ├── ClipboardView.xaml/.cs      # 工作台 clipboard 页（嵌入 PageHost）
│   └── ClipboardEntryCard.xaml/.cs # 单条历史的卡片控件
```

### 核心接口

```csharp
// Core/Clipboard/ClipboardEntry.cs
namespace ToolBox.Core.ClipboardHistory;

public enum EClipboardEntryType { Text, Image, FileList }

public class ClipboardEntry
{
    public string Id { get; set; }                     // Guid 前12位
    public EClipboardEntryType EntryType { get; set; }
    public string TextContent { get; set; }            // 文本内容 / 文件路径列表摘要
    public string? ThumbnailPath { get; set; }         // 缩略图文件路径（Image类型）
    public string? SourceApp { get; set; }             // 来源应用名（可选）
    public DateTime CapturedAt { get; set; }
    public bool IsPinned { get; set; }
    public bool IsFavorite { get; set; }
    public string ContentHash { get; set; }            // 内容哈希，用于去重
}

// Core/Clipboard/ClipboardMonitor.cs
namespace ToolBox.Core.ClipboardHistory;

public class ClipboardMonitor : IDisposable
{
    /// <summary>新剪贴板内容到达（重复内容已过滤）</summary>
    public event Action<ClipboardEntry>? EntryCaptured;

    /// <summary>开始监听</summary>
    public ClipboardMonitor Start();

    /// <summary>暂停监听（如用户正在粘贴敏感内容）</summary>
    public ClipboardMonitor Pause();

    /// <summary>恢复监听</summary>
    public ClipboardMonitor Resume();

    /// <summary>手动抓取当前剪贴板内容</summary>
    public ClipboardEntry? CaptureNow();

    public void Dispose();
}

// Core/Clipboard/ClipboardStore.cs
namespace ToolBox.Core.ClipboardHistory;

public class ClipboardStore
{
    public static ClipboardStore Instance { get; }

    /// <summary>历史条目列表</summary>
    public IReadOnlyList<ClipboardEntry> Entries { get; }

    /// <summary>添加条目</summary>
    public ClipboardStore Add(ClipboardEntry entry);

    /// <summary>删除条目</summary>
    public ClipboardStore Delete(string entryId);

    /// <summary>置顶/取消置顶</summary>
    public ClipboardStore TogglePin(string entryId);

    /// <summary>收藏/取消收藏</summary>
    public ClipboardStore ToggleFavorite(string entryId);

    /// <summary>搜索（全文匹配文本内容）</summary>
    public List<ClipboardEntry> Search(string keyword);

    /// <summary>按类型筛选</summary>
    public List<ClipboardEntry> FilterByType(EClipboardEntryType type);

    /// <summary>分页查询</summary>
    public List<ClipboardEntry> GetPage(int page, int pageSize = 20);

    /// <summary>数据变化通知</summary>
    public event Action? EntriesChanged;
}
```

### LLM Tool 暴露

```csharp
// Core/Tools/ClipboardTools.cs
namespace ToolBox.Core.Tools;

public static class ClipboardTools
{
    [Tool("search_clipboard", "搜索剪贴板历史，支持关键词匹配")]
    public static string SearchClipboard(
        [ToolParam("搜索关键词")] string keyword = "",
        [ToolParam("最大返回条数，默认10")] int limit = 10);

    [Tool("get_clipboard", "获取最近 N 条剪贴板历史")]
    public static string GetRecentClipboard(
        [ToolParam("条数，默认5")] int count = 5);
}
```

---

## 数据流

```
Windows 剪贴板变化
    │
    ▼
ClipboardMonitor.WndProc → WM_DRAWCLIPBOARD
    │
    ▼
检测内容类型:
  ├─ CF_UNICODETEXT → Clipboard.GetText() → ClipboardEntry(Text)
  ├─ CF_BITMAP     → Clipboard.GetImage() → 200×200 缩略图 → ClipboardEntry(Image)
  └─ CF_HDROP      → Clipboard.GetFileDropList() → ClipboardEntry(FileList)
    │
    ▼
去重检查: SHA256(content) == lastEntry.ContentHash? → 跳过
    │
    ▼
ClipboardStore.Add(entry)
    │
    ├─ 持久化: JSON append + 缩略图写入磁盘
    └─ 触发 EntriesChanged 事件
            │
            ├─ ClipboardPopup 更新列表
            └─ ClipboardView 更新列表

用户操作流程:
  Ctrl+Shift+V → ClipboardPopup 显示（Topmost, 居中）
    ├─ 搜索框: 实时过滤
    ├─ 类型Tab: 文本 | 图片 | 文件
    ├─ 点击条目 → 写入系统剪贴板 + SendKeys ^v 粘贴到前台
    ├─ ⭐ 收藏 / 📌 置顶 / 🗑 删除
    └─ 「发送到 AI」 → 跳转工作台 assistant 页并填入内容
```

---

## UI 设计要点

### ClipboardPopup（浮动面板）
- 尺寸：360×480px，`WindowStyle=None, AllowsTransparency=True, Topmost=True`
- 圆角：14px，阴影：`ShadowFloat`
- 顶部：搜索框（`InputField` Style，带搜索图标）+ 关闭按钮
- 筛选栏：三个 `FilterTabStyle` RadioButton（文本/图片/文件）
- 列表：`ModernListBox` + `ModernListBoxItem`，每项显示：
  - 文本：前 80 字符 + 时间戳（`TextTertiaryBrush`, 11px）
  - 图片：缩略图 48×48 + 时间戳
  - 文件：文件名 + 路径摘要
- 底部状态栏："共 X 条 · 已置顶 Y · 已收藏 Z"
- 热键：ESC 关闭，↑↓ 导航，Enter 粘贴

### ClipboardView（工作台页面）
- 布局：与 screenshots 页面一致——顶部筛选栏 + 4 列网格/列表切换 + 分页
- 复用 `DateFilterStyle` 进行时间筛选（today/week/month/all）
- 右侧详情面板：点击条目显示完整内容 + 来源应用 + 操作按钮
- 嵌入 WorkbenchWindow 的 PageHost，新增侧边栏导航项 "📋 剪贴板"

### 侧边栏扩展
- WorkbenchWindow 左侧侧边栏增加第 6 个 RadioButton：`📋 剪贴板`
- 图标：剪贴板图标
- 页面路由：`clipboard` → ClipboardView

---

## 与现有架构的集成

| 集成点 | 方式 |
|--------|------|
| **启动流程** | `App.OnStartup` 中 `ClipboardMonitor.Start()` + `ClipboardStore` 初始化 |
| **托盘菜单** | 无独立入口，通过工作台剪贴板页访问 |
| **工作台** | 侧边栏新增 `clipboard` 导航项 + PageHost 路由 |
| **热键体系** | `HotkeyManager` 注册 `Ctrl+Shift+V` → 唤起 ClipboardPopup |
| **LLM 系统** | `ClipboardTools` 暴露搜索/查询接口 |
| **截图协同** | 截图完成后自动 `Clipboard.SetImage()` 会触发 ClipboardMonitor → 自动记录到历史 |
| **主题适配** | 所有 UI 使用 `DynamicResource`（BgElevatedBrush / TextPrimaryBrush / BorderDefaultBrush） |
| **设置页面** | SettingsView 新增 "📋 剪贴板" Section：历史上限 / 忽略的应用 / 清空历史 |
| **持久化** | `ToolBoxOption.Data` 新增：`ClipboardMaxEntries`(默认500) / `ClipboardIgnoredApps` |

---

## 风险与注意事项

1. **隐私安全**：剪贴板可能包含密码/密钥等敏感信息。缓解：设置中支持"忽略的应用列表"（如 1Password/密码管理器所在窗口），Pause/Resume 机制允许临时关闭监听
2. **与截图复制冲突**：截图后自动复制到剪贴板会触发 ClipboardMonitor，产生重复记录。缓解：`ClipboardMonitor` 内部维护一个"忽略来源"窗口句柄列表；截图窗口可在复制前调用 `Pause()`，复制后 `Resume()`
3. **图片内存**：连续复制大图可能产生大量缩略图文件。缓解：缩略图限 200×200 JPEG Quality=60；历史上限 500 条后自动清理最早的缩略图文件
4. **性能**：Clipboard Chain 是同步回调，处理时间过长会阻塞系统剪贴板链。缓解：`WM_DRAWCLIPBOARD` 处理中仅排队，实际内容解析异步执行
5. **SendKeys 粘贴**：`SendKeys.SendWait("^v")` 在某些应用（管理员权限/游戏/全屏）可能失效。缓解：提供备选方案——点击后先将内容写入剪贴板，用户自行 Ctrl+V
6. **Win11 Clipboard History 冲突**：Windows 自带 Win+V 剪贴板历史。缓解：ToolBox 使用不同热键（Ctrl+Shift+V），两者共存不冲突
