# 截图标签与智能分类

- **优先级**：P1
- **实现难度**：中
- **预估工作量**：5 人天
- **来源竞品**：Eagle（智能文件夹/颜色筛选/标签系统）、ShareX（截图历史标签+收藏+重命名）

---

## 功能描述

为 ToolBox 截图历史增加标签管理和智能分类能力。用户可以为每张截图手动添加标签（如 #bug、#设计稿、#会议纪要），系统支持基于 LLM 的自动打标签（分析截图内容后推荐标签）。截图历史视图增加标签筛选、颜色筛选、星级评分、多条件组合搜索。

核心交互：
1. **手动标签**：截图完成后工具栏显示标签输入区，可输入自定义标签
2. **自动打标**：截图后 LLM 分析图像内容，自动推荐 3-5 个标签，用户可接受/编辑
3. **标签筛选**：HistoryView 增加标签云筛选栏，点击标签过滤截图
4. **星级评分**：1-5 星评分，支持按评分筛选
5. **颜色提取**：自动提取截图主色调，支持按颜色筛选（Eagle 风格）
6. **智能文件夹**：基于标签+评分+颜色的多条件自动分类

---

## 技术方案

### 标签系统

标签采用扁平结构（非层级），支持中文/英文/emoji。每条截图关联多个标签。

**标签存储**：`%AppData%/ToolBox/screenshot_tags.json`
```json
{
  "screenshots": [
    {
      "cacheId": "abc123",
      "tags": ["bug", "登录页", "重要"],
      "rating": 4,
      "dominantColors": ["#E84343", "#FFFFFF"],
      "notes": "登录按钮错位问题截图",
      "taggedAt": "2026-07-14T15:30:00Z"
    }
  ],
  "allTags": ["bug", "登录页", "重要", "设计稿", "会议纪要", ...],
  "tagUsageCount": { "bug": 12, "设计稿": 8, ... }
}
```

**为什么不放到 CacheManager**：CacheManager 管理的是截图文件生命周期，标签是元数据层。独立存储更灵活，支持标签迁移和导入导出。

### LLM 自动打标

利用现有 LLM 基础设施，发送截图（base64 或 OCR 文本 + 缩略图）请求标签建议：

```
System: 你是截图标签助手。根据截图内容推荐 3-5 个中文标签。
        可选标签池: [bug, 设计稿, 会议纪要, 代码, 文档, 登录页, ...]
        如果截图包含新主题，可以生成新标签。
User: [缩略图 base64 或 OCR 文本]
Assistant: ["标签1", "标签2", "标签3"]
```

### 颜色提取

使用简单的颜色量化算法：
1. 将截图缩放到 100×100px
2. 使用 K-Means（k=3）聚类提取 3 个主色调
3. 不需要 OpenCV，用纯 C# 实现简化版 K-Means（数据量小，<10000 像素）

### 依赖评估

无新增 NuGet 依赖。颜色提取自实现简化 K-Means。LLM 自动打标复用现有 ChatManager。

---

## 模块设计

### 文件位置

```
Core/
├── Screenshot/
│   ├── ScreenshotTagStore.cs          # 截图标签持久化
│   ├── TagAutoTagger.cs               # LLM 自动打标服务
│   └── ColorExtractor.cs              # 主色调提取
Models/
├── ScreenshotMetadata.cs              # 截图元数据模型
Views/
├── Screenshot/
│   ├── TagCloud.xaml/.cs              # 标签云筛选组件
│   ├── TagEditor.xaml/.cs             # 标签编辑器（输入+建议）
│   ├── StarRating.xaml/.cs            # 星级评分控件
│   └── ColorFilter.xaml/.cs           # 颜色筛选器
```

### 核心接口

```csharp
// Models/ScreenshotMetadata.cs
namespace ToolBox.Models;

public class ScreenshotMetadata
{
    public string CacheId { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
    public int Rating { get; set; }                             // 0-5, 0=未评分
    public List<string> DominantColors { get; set; } = new();   // HEX 格式
    public string? Notes { get; set; }
    public DateTime TaggedAt { get; set; }
    public bool IsAutoTagged { get; set; }
}

// Core/Screenshot/ScreenshotTagStore.cs
namespace ToolBox.Core.Screenshot;

public class ScreenshotTagStore
{
    public static ScreenshotTagStore Instance { get; }

    /// <summary>所有已使用的标签</summary>
    public IReadOnlyList<string> AllTags { get; }

    /// <summary>标签使用计数</summary>
    public IReadOnlyDictionary<string, int> TagUsageCount { get; }

    /// <summary>获取截图的元数据</summary>
    public ScreenshotMetadata GetMetadata(string cacheId);

    /// <summary>更新标签</summary>
    public ScreenshotTagStore SetTags(string cacheId, List<string> tags);

    /// <summary>更新评分</summary>
    public ScreenshotTagStore SetRating(string cacheId, int rating);

    /// <summary>更新备注</summary>
    public ScreenshotTagStore SetNotes(string cacheId, string? notes);

    /// <summary>按标签筛选</summary>
    public List<string> FilterByTags(List<string> includeTags, List<string>? excludeTags = null);

    /// <summary>按评分筛选</summary>
    public List<string> FilterByRating(int minRating);

    /// <summary>按颜色筛选（色相范围匹配）</summary>
    public List<string> FilterByColor(string hexColor, int tolerance = 30);

    /// <summary>组合查询（标签 + 评分 + 颜色 + 时间）</summary>
    public List<string> Search(ScreenshotSearchQuery query);

    public event Action? MetadataChanged;
}

// Core/Screenshot/TagAutoTagger.cs
namespace ToolBox.Core.Screenshot;

public class TagAutoTagger
{
    /// <summary>对截图自动推荐标签</summary>
    /// <param name="screenshotBytes">截图数据</param>
    /// <param name="ocrText">可选 OCR 文本（提升准确率）</param>
    /// <param name="maxTags">最多推荐标签数</param>
    public Task<List<string>> SuggestTagsAsync(byte[] screenshotBytes, string? ocrText = null, int maxTags = 5);
}

// Core/Screenshot/ColorExtractor.cs
namespace ToolBox.Core.Screenshot;

public class ColorExtractor
{
    /// <summary>提取主色调</summary>
    /// <param name="bitmap">截图</param>
    /// <param name="colorCount">主色调数量</param>
    public List<System.Windows.Media.Color> ExtractDominantColors(BitmapSource bitmap, int colorCount = 3);
}
```

### LLM Tool 暴露

```csharp
// Core/Tools/ScreenshotTools.cs (新增)
namespace ToolBox.Core.Tools;

public static class ScreenshotTools
{
    [Tool("search_screenshots", "按标签/评分/关键词搜索截图历史")]
    public static string SearchScreenshots(
        [ToolParam("标签（逗号分隔）")] string tags = "",
        [ToolParam("最低评分 1-5")] int minRating = 0,
        [ToolParam("关键词搜索（匹配备注）")] string keyword = "");

    [Tool("tag_screenshot", "为最近截图添加标签")]
    public static string TagScreenshot(
        [ToolParam("标签（逗号分隔）")] string tags,
        [ToolParam("评分 1-5")] int rating = 0);
}
```

---

## 数据流

```
截图完成 → CacheManager 保存截图
    │
    ├── [自动] ColorExtractor.ExtractDominantColors(bitmap) → ScreenshotMetadata.DominantColors
    │
    ├── [可选/设置] TagAutoTagger.SuggestTagsAsync(screenshotBytes)
    │       │ → 缩略图 base64 + OCR文本（如已启用OCR）→ ChatManager.SendToAgent()
    │       │ → 返回 ["登录页", "bug", "UI问题"]
    │       │ → ScreenshotTagStore.SetTags(cacheId, tags, isAutoTagged=true)
    │       ▼
    │   截图完成工具栏: 标签编辑区
    │   ┌──────────────────────────────────────────┐
    │   │ 🏷 [登录页] [bug] [UI问题] [+ 添加标签]  │ ← 自动推荐 + 手动编辑
    │   │ ⭐ ★★★★☆                                │ ← 星级评分
    │   │ 📝 添加备注...                           │
    │   │ 🎨 [■#E84343] [■#FFFFFF] [■#2D3436]     │ ← 提取的主色调
    │   └──────────────────────────────────────────┘
    │
    ▼
HistoryView 筛选增强:
    ├─ 标签云栏: [全部] [bug 12] [设计稿 8] [会议纪要 5] [代码 3] ...
    │   (FilterTabStyle, 按使用频次排序)
    ├─ 星级筛选: ⭐1 ⭐2 ⭐3 ⭐4 ⭐5  (FilterTabStyle)
    ├─ 颜色筛选: 色块网格 6×? (点击后按色相范围筛选)
    └─ 组合查询: 标签 AND 评分 AND 时间范围
```

---

## UI 设计要点

### 标签云 (TagCloud)
- 水平排列的标签胶囊，`FilterTabStyle` RadioButton
- 标签文本 + 使用计数："bug 12"
- 选中状态：`AccentSoftBrush` + `AccentBrush` 文字
- 滚动溢出时显示 `>` 更多按钮

### 标签编辑器 (TagEditor)
- 截图工具栏中的标签输入区
- 样式：类似 Token 输入框——输入标签后按 Enter/Tab 生成标签胶囊
- 胶囊样式：`Border CornerRadius=999, Background=AccentSoftBrush, Padding=8,4`
- 建议下拉：输入时实时匹配已有标签（最多显示 5 个建议）

### 星级评分 (StarRating)
- 5 颗星水平排列，`IconButton` Style（16×16 星形图标）
- 交互：点击第 N 颗星 → 评分 = N；再次点击 → 清除评分
- 颜色：已评星 = `AccentBrush`，未评星 = `TextTertiaryBrush`

### 颜色筛选 (ColorFilter)
- 水平色块网格，每块 24×24px `Rectangle`，`CornerRadius=4`
- 最多显示 24 个主色调（按频率排序）
- 点击色块 → 按色相 ±30° 筛选截图

### 截图详情面板
- HistoryView 右侧详情面板增加：标签区、星级评分、备注编辑区、主色调显示

---

## 与现有架构的集成

| 集成点 | 方式 |
|--------|------|
| **截图流程** | CaptureWindow 工具栏增加标签/评分编辑区 |
| **HistoryView** | 增加标签云/星级/颜色筛选栏；详情面板扩展 |
| **CacheManager** | 不修改，元数据由 ScreenshotTagStore 独立管理 |
| **LLM 系统** | TagAutoTagger 通过 ChatManager 调用 LLM；ScreenshotTools 暴露搜索 |
| **OcrService** | TagAutoTagger 可结合 OCR 文本提升标签准确率 |
| **持久化** | ScreenshotTagStore 保存到 `%AppData%/ToolBox/screenshot_tags.json` |
| **设置页面** | SettingsView 新增 "🏷 截图标签" Section：启用自动打标 / 启用颜色提取 / LLM 打标模型选择 |
| **主题适配** | 标签胶囊用 AccentSoftBrush；星形图标用 AccentBrush；色块无边框 |

---

## 风险与注意事项

1. **LLM 打标成本**：每次截图都调用 LLM 会增加 API 费用。缓解：默认关闭自动打标，用户手动触发；支持使用本地 Ollama 模型降低成本
2. **标签膨胀**：自动打标可能生成过多冗余标签。缓解：TagAutoTagger 提示词中约束使用已有标签池；提供"合并标签"功能
3. **颜色提取精度**：K-Means 在复杂图像上可能提取不准确。缓解：对截图（通常背景较单一）效果较好；可调参数
4. **历史截图回填**：已有截图没有标签数据。缓解：ScreenshotTagStore 对无元数据的截图返回默认空元数据；提供"批量自动打标"历史截图的功能
5. **标签存储与截图生命周期**：CacheManager 清理旧截图时需同步清理标签数据。缓解：ScreenshotTagStore 监听 CacheManager 的清理事件
