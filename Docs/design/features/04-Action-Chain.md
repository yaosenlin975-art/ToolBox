# 截图后自动化动作链

- **优先级**：P1
- **实现难度**：中
- **预估工作量**：5 人天
- **来源竞品**：ShareX（After-Capture/After-Upload 工作流）、Greenshot（多目标分发）

---

## 功能描述

为截图完成后的操作增加可配置的"动作链"——用户可以定义截图后自动执行的一系列操作：OCR→翻译→复制、截图→标注→保存到指定文件夹、截图→上传→复制短链等。每个动作链由多个步骤节点串联而成，支持条件分支（如"OCR 识别到英文时 → 翻译为中文"）。

核心交互：
1. **预设动作链**：内置 3 条默认链（仅复制 / OCR→复制 / 保存+复制），用户可自定义
2. **链编辑器**：设置页面中的可视化动作链编辑器（节点拖拽或下拉选择）
3. **截图后触发**：截图完成后自动执行选中的动作链，结果通知到迷你窗口
4. **上下文感知**：根据截图内容类型智能推荐动作链（如截取报错→建议OCR→搜索）

---

## 技术方案

### 动作链引擎

设计为轻量级管道（Pipeline）模式：每个动作节点实现同一接口，引擎按序执行节点。支持同步/异步节点混合。

节点类型定义：

| 节点类型 | 输入 | 输出 | 实现要点 |
|---------|------|------|---------|
| `SaveToFile` | BitmapSource | 文件路径 | 指定目录/命名规则 |
| `CopyToClipboard` | BitmapSource | void | Clipboard.SetImage |
| `OcrExtract` | BitmapSource | string | 调用 OcrService |
| `TranslateText` | string | string | 调用 LLM 翻译（需网络/本地模型） |
| `SendToAi` | string/Bitmap | void | 发送到 LLM 会话 |
| `OpenEditor` | BitmapSource | void | 打开标注编辑器 |
| `ApplyWatermark` | BitmapSource | BitmapSource | 图像叠加水印 |
| `ResizeImage` | BitmapSource | BitmapSource | 缩放/裁剪 |
| `UploadToCloud` | 文件路径 | URL | （P3 阶段实现） |

### 链结构

```csharp
// 线性链（无分支）
[OCR] → [Translate] → [CopyResult]

// 条件链（按类型分支）
[OCR] → { if lang=EN → [Translate→ZH] → [Copy] } 
      → { if lang=ZH → [Copy] }

// 并行链
[SaveToFile] ─┐
[CopyToClip] ─┤ → [ShowNotification]
[SendToAi]   ─┘
```

### 依赖评估

无新增 NuGet 依赖。动作链执行引擎用纯 C# 实现。与 OCR 模块（01）和 LLM 翻译能力（已内置）有依赖关系。

---

## 模块设计

### 文件位置

```
Core/
├── ActionChain/
│   ├── IActionNode.cs               # 动作节点接口
│   ├── ActionChain.cs               # 动作链模型（节点列表 + 配置）
│   ├── ActionChainEngine.cs         # 动作链执行引擎
│   ├── ActionChainStore.cs          # 动作链持久化 + 管理
│   └── Nodes/
│       ├── SaveToFileNode.cs        # 保存到文件
│       ├── CopyToClipboardNode.cs   # 复制到剪贴板
│       ├── OcrExtractNode.cs        # OCR 提取文字
│       ├── TranslateTextNode.cs     # LLM 翻译
│       ├── SendToAiNode.cs          # 发送到 LLM 会话
│       ├── OpenEditorNode.cs        # 打开编辑器
│       └── ConditionalNode.cs       # 条件分支节点
Models/
├── ActionChainConfig.cs             # 动作链配置（用户自定义链的序列化模型）
Views/
├── ActionChain/
│   └── ActionChainEditor.xaml/.cs   # 设置页中的链编辑器
```

### 核心接口

```csharp
// Core/ActionChain/IActionNode.cs
namespace ToolBox.Core.ActionChain;

public interface IActionNode
{
    /// <summary>节点名称（显示在链编辑器中）</summary>
    string NodeName { get; }

    /// <summary>节点类型标识</summary>
    string NodeType { get; }

    /// <summary>执行节点</summary>
    /// <param name="context">上下文数据（上一步的输出作为输入）</param>
    /// <returns>执行结果，传给下一个节点</returns>
    Task<ActionNodeResult> ExecuteAsync(ActionNodeContext context);
}

// Core/ActionChain/ActionNodeResult.cs
public class ActionNodeResult
{
    public bool IsSuccess { get; set; }
    public string? ErrorMessage { get; set; }
    public object? Output { get; set; }               // 传给下一节点的数据
    public Dictionary<string, object> Metadata { get; set; } = new();
}

// Core/ActionChain/ActionNodeContext.cs
public class ActionNodeContext
{
    public BitmapSource? Screenshot { get; set; }     // 原始截图
    public string? FilePath { get; set; }             // 保存路径
    public object? PreviousOutput { get; set; }       // 上一节点输出
    public CancellationToken CancellationToken { get; set; }
}

// Core/ActionChain/ActionChain.cs
namespace ToolBox.Core.ActionChain;

public class ActionChainDefinition
{
    public string Id { get; set; }
    public string Name { get; set; }                  // 链名称（如"OCR→翻译→复制"）
    public string? Description { get; set; }
    public bool IsBuiltIn { get; set; }               // 是否内置
    public List<ActionNodeConfig> Nodes { get; set; } = new();
    public bool IsDefault { get; set; }               // 是否默认链
}

public class ActionNodeConfig
{
    public string NodeType { get; set; }              // 对应 IActionNode.NodeType
    public Dictionary<string, string> Parameters { get; set; } = new(); // 节点参数
    public List<ActionNodeConfig>? Branches { get; set; }               // 条件分支子链
}

// Core/ActionChain/ActionChainEngine.cs
namespace ToolBox.Core.ActionChain;

public class ActionChainEngine
{
    public static ActionChainEngine Instance { get; }

    /// <summary>执行指定动作链</summary>
    public Task<ActionNodeResult> ExecuteAsync(
        ActionChainDefinition chain, BitmapSource screenshot, CancellationToken ct);

    /// <summary>执行指定链 ID</summary>
    public Task<ActionNodeResult> ExecuteByIdAsync(string chainId, BitmapSource screenshot);

    /// <summary>当前执行进度</summary>
    public event Action<int, int, string>? ProgressChanged; // (current, total, nodeName)
}

// Core/ActionChain/ActionChainStore.cs
namespace ToolBox.Core.ActionChain;

public class ActionChainStore
{
    public static ActionChainStore Instance { get; }

    public IReadOnlyList<ActionChainDefinition> Chains { get; }

    public ActionChainStore Add(ActionChainDefinition chain);
    public ActionChainStore Update(ActionChainDefinition chain);
    public ActionChainStore Delete(string chainId);
    public ActionChainStore SetDefault(string chainId);

    public event Action? ChainsChanged;
}
```

### LLM Tool 暴露

```csharp
// Core/Tools/ActionChainTools.cs
namespace ToolBox.Core.Tools;

public static class ActionChainTools
{
    [Tool("list_action_chains", "列出所有可用的截图动作链")]
    public static string ListActionChains();

    [Tool("create_action_chain", "创建一条新的截图动作链")]
    public static string CreateActionChain(
        [ToolParam("链名称")] string name,
        [ToolParam("节点类型列表，逗号分隔，如: ocr,translate,copy")] string nodeTypes);
}
```

---

## 数据流

```
用户截图完成 → CaptureWindow.OnCaptureComplete(bitmap)
    │
    ├── 读取：ActionChainStore.Instance.Chains → 当前默认链
    │
    ▼
ActionChainEngine.ExecuteAsync(defaultChain, bitmap, ct)
    │
    ├── ProgressChanged(1/3, "OCR 识别中...")
    │       │
    │       ▼
    │  OcrExtractNode.ExecuteAsync(ctx)
    │       │ → OcrService.RecognizeAsync(bitmapBytes)
    │       │ → ctx.Output = ocrText
    │       │
    │       ▼
    │  ProgressChanged(2/3, "翻译中...")
    │       │
    │       ▼
    │  TranslateTextNode.ExecuteAsync(ctx)
    │       │ → ChatManager.SendQuickTranslation(ocrText, "zh")
    │       │ → ctx.Output = translatedText
    │       │
    │       ▼
    │  ProgressChanged(3/3, "复制到剪贴板...")
    │       │
    │       ▼
    │  CopyToClipboardNode.ExecuteAsync(ctx)
    │       │ → Clipboard.SetText(translatedText)
    │       │
    │       ▼
    │  ActionNodeResult { IsSuccess = true, Output = translatedText }
    │
    └── 迷你窗口 Toast 通知: "动作链「OCR→翻译→复制」完成"
```

---

## UI 设计要点

### 截图完成工具栏
- 截图完成时，工具栏底部显示当前默认动作链名称（如 `⚡ OCR→翻译→复制`）
- 点击展开下拉菜单，列出所有链 → 可切换默认链
- 复用：`OutlineButton` Style（12px 字号，紧凑间距）

### 动作链编辑器（设置页）
- 位置：SettingsView 新增 "🔗 截图动作链" Section
- 预览区：卡片式列表，每条链显示名称 + 节点图标串联（如 `📸→🔤→🌐→📋`）
- 编辑区（点开链进入详情编辑）：
  - 左侧：可用节点列表（可拖拽）
  - 中间：链画布——节点卡片 + 连线，从上到下排列
  - 右侧：选中节点的参数配置
- 按钮：`[+ 新建链]` `[设为默认]` `[重置内置链]`

### 执行进度
- 迷你窗口底部显示小型进度条（`AccentBrush` 填充，4px 高）
- 进度文本（11px, TextTertiaryBrush）："OCR 识别中..."

---

## 与现有架构的集成

| 集成点 | 方式 |
|--------|------|
| **截图流程** | CaptureWindow.OnCaptureComplete 后调用 ActionChainEngine |
| **OCR 模块** | OcrExtractNode 依赖 OcrService（P0 功能） |
| **LLM 模块** | TranslateTextNode / SendToAiNode 通过 ChatManager 调用 |
| **迷你窗口** | 执行进度和结果在 CompactToolboxWindow 底部显示 |
| **设置持久化** | ActionChainStore 保存到 `%AppData%/ToolBox/action_chains.json` |
| **配置项** | `ToolBoxOption.Data` 新增 `DefaultActionChainId` |
| **主题适配** | 链编辑器使用 CardStyle 容器 + FilterTabStyle 节点标签 |

---

## 风险与注意事项

1. **链执行失败**：某节点失败后是否继续执行后续节点？缓解：默认"遇错即停"，可配置为"忽略错误继续"
2. **长时间操作**：OCR+翻译+上传可能需要 10+ 秒。缓解：异步后台执行 + 迷你窗口进度指示；支持中途取消
3. **循环依赖**：条件分支可能导致无限循环。缓解：ActionChainEngine 限制最大执行节点数 20
4. **复杂度控制**：动作链编辑对普通用户可能过于复杂。缓解：内置 3 条开箱即用的链，高级编辑折叠在"高级设置"中
5. **与 P3 云分享的依赖**：UploadToCloud 节点需要在云分享功能就绪后才能使用
