# 截图后悬浮预览卡

- **优先级**：P1
- **实现难度**：低
- **预估工作量**：3 人天
- **来源竞品**：CleanShot X（截图后左下角悬浮预览——拖拽分享/点击编辑）

---

## 功能描述

截图完成后，在屏幕右下角显示一个悬浮预览卡片，展示截图缩略图。用户可以将预览卡片拖拽到目标应用（如聊天窗口、文档编辑器）直接插入图片；点击预览卡片可快速编辑/标注/贴图；右键弹出操作菜单。预览卡片在 5 秒后自动消失，鼠标悬停时保持显示。

核心交互：
1. **截图完成 → 预览卡出现**：屏幕右下角滑入动画（300ms ease-out），显示缩略图
2. **拖拽分享**：按住预览卡拖拽到目标应用 → 释放时自动粘贴截图
3. **点击操作**：点击 → 进入编辑模式（标注工具）/ 贴图置顶 / 复制 / 保存
4. **自动消失**：5 秒后淡出消失，鼠标悬停时重置计时器
5. **粘贴模式联动**：如果截图时选择了"贴图模式"，预览卡自动执行贴图操作

---

## 技术方案

### 窗口方案

独立的无边框弹出窗口（`PreviewCardWindow`），继承现有窗口模式：
- `WindowStyle=None, AllowsTransparency=True, Topmost=True`
- 200×150px（可配置），圆角 14，`ShadowFloat` 阴影
- 屏幕右下角定位（考虑任务栏高度）
- `ShowActivated=False`（不抢焦点）

### 拖拽实现

使用 WPF `DragDrop.DoDragDrop` 实现缩略图拖拽：
```csharp
// 将截图 BitmapSource 放入 DataObject
var dataObject = new DataObject();
dataObject.SetImage(bitmapSource);
dataObject.SetData(DataFormats.FileDrop, new[] { savedFilePath });
DragDrop.DoDragDrop(this, dataObject, DragDropEffects.Copy);
```

释放到目标应用时，系统自动处理图片粘贴或文件拖放。

### 动画

- 入场：`TranslateTransform` Y 轴从 +150 到 0，300ms ease-out + `Opacity` 0→1, 200ms
- 离场：`Opacity` 1→0, 300ms ease-in
- 鼠标悬浮：`ScaleTransform` 1.0→1.05, 150ms ease-out

使用 WPF Storyboard + DoubleAnimation 实现。

### 依赖评估

无新增 NuGet 依赖。全部基于 WPF 原生动画 + DragDrop。

---

## 模块设计

### 文件位置

```
Views/
├── PreviewCard/
│   └── PreviewCardWindow.xaml/.cs    # 悬浮预览卡窗口
Core/
├── Window/
│   └── PreviewCardManager.cs         # 预览卡生命周期管理
```

### 核心接口

```csharp
// Core/Window/PreviewCardManager.cs
namespace ToolBox.Core.Window;

public enum EPreviewCardAction
{
    Edit,           // 打开标注编辑
    PinToDesktop,   // 贴图置顶
    Copy,           // 复制到剪贴板
    Save,           // 保存到文件
    SendToAi,       // 发送到 AI 助手
    Discard         // 丢弃（不保存）
}

public class PreviewCardConfig
{
    public double DisplayDurationMs { get; set; } = 5000;
    public double Width { get; set; } = 200;
    public double Height { get; set; } = 150;
    public bool ShowAfterCapture { get; set; } = true;
}

public class PreviewCardManager
{
    public static PreviewCardManager Instance { get; }

    /// <summary>显示预览卡</summary>
    public PreviewCardManager Show(BitmapSource screenshot, string? filePath = null);

    /// <summary>隐藏当前预览卡</summary>
    public PreviewCardManager Hide();

    /// <summary>预览卡用户操作事件</summary>
    public event Action<EPreviewCardAction, BitmapSource>? ActionTriggered;
}

// Views/PreviewCard/PreviewCardWindow.xaml.cs
public partial class PreviewCardWindow : System.Windows.Window
{
    /// <summary>设置预览截图</summary>
    public PreviewCardWindow SetScreenshot(BitmapSource screenshot);

    /// <summary>设置关联文件路径</summary>
    public PreviewCardWindow SetFilePath(string? filePath);

    /// <summary>开始自动消失计时</summary>
    public PreviewCardWindow StartAutoDismiss(double afterMs = 5000);

    /// <summary>重置自动消失计时（鼠标悬停时）</summary>
    public PreviewCardWindow ResetAutoDismissTimer();

    /// <summary>以动画隐藏窗口</summary>
    public PreviewCardWindow DismissWithAnimation();
}
```

---

## 数据流

```
截图完成 → CaptureWindow.OnCaptureComplete(bitmap)
    │
    ▼
检查 ToolBoxOption.Data.ShowPreviewCard (默认 true)
    │
    ▼ false → 跳过，直接保存/贴图
    │
    ▼ true
PreviewCardManager.Instance.Show(bitmap, filePath)
    │
    ▼
PreviewCardWindow 实例化 → 定位屏幕右下角 → 入场动画
    ┌──────────────────────────┐
    │  ┌──────────────────┐    │
    │  │                  │    │
    │  │   [缩略图预览]   │    │  ← 缩略图 (146×100)
    │  │                  │    │
    │  └──────────────────┘    │
    │  ✏️编辑  📌贴图  📋复制  │  ← 操作按钮行
    └──────────────────────────┘
    │
    ├─ 鼠标悬浮 → ResetAutoDismissTimer() → 停留
    │
    ├─ 鼠标拖拽 → DragDrop.DoDragDrop() → 粘贴到目标应用
    │
    ├─ 点击编辑 → ActionTriggered(Edit) → 打开标注编辑器
    ├─ 点击贴图 → ActionTriggered(PinToDesktop) → 创建 ScrapWindow
    ├─ 点击复制 → ActionTriggered(Copy) → Clipboard.SetImage
    │
    └─ 5 秒无交互 → DismissWithAnimation() → 淡出消失
```

---

## UI 设计要点

### PreviewCardWindow
- 尺寸：200×150px，`WindowStyle=None, AllowsTransparency=True, Topmost=True`
- 圆角：14px（`CornerRadius=14`），阴影：`ShadowFloat`
- 背景：`BgElevatedBrush` 带 95% 不透明度
- 边框：`BorderDefaultBrush`, 1px

### 缩略图区域
- 尺寸：184×100px（padding 8px）
- 圆角：8px，填充模式：`Uniform`（保持比例）
- 背景：棋盘格图案（`BgSunkenBrush` 底色 + 浅灰格子），表示透明区域

### 操作按钮行
- 三个图标按钮水平排列，等宽分布
- `✏️ 编辑` `📌 贴图` `📋 复制`
- 样式：`IconButton` 变体（24×24），Foreground=TextSecondaryBrush
- Hover：Foreground=AccentBrush

### 动画细节
- 入场：从右下角外滑入（`TranslateTransform.Y` 150→0, 300ms, `CubicEase`）
- 悬停放大：`ScaleTransform` 1.0→1.05, 150ms
- 离场：`Opacity` 1→0, 300ms, 然后 Close()

---

## 与现有架构的集成

| 集成点 | 方式 |
|--------|------|
| **截图流程** | CaptureWindow.OnCaptureComplete 后调用 PreviewCardManager.Show |
| **贴图系统** | "贴图"按钮调用 ScrapBook / LayerManager 创建 ScrapWindow |
| **标注编辑器** | "编辑"按钮打开现有 Paint 标注界面 |
| **AI 助手** | 右键菜单 "发送到 AI" → 跳转工作台 assistant 页，附带截图 |
| **设置页面** | SettingsView 截图 Section 新增：`☑ 截图后显示悬浮预览卡` / 显示时长 / 预览卡大小 |
| **持久化** | 配置项 `ShowPreviewCard` / `PreviewCardDurationMs` 保存在 `ToolBoxOption.Data` |
| **窗口层级** | PreviewCardWindow 位于 Layer 2.5（迷你窗口之上，ScrapWindow 之下） |
| **主题适配** | BgElevatedBrush / BorderDefaultBrush / ShadowFloat，通过 DynamicResource |

---

## 风险与注意事项

1. **焦点管理**：`ShowActivated=False` 确保不抢焦点，但 `DragDrop` 可能需要焦点。缓解：拖拽操作时临时激活窗口，拖拽结束后恢复
2. **多显示器**：截图可能在副屏，预览卡应显示在截图所在屏幕的右下角。缓解：获取截图完成的屏幕坐标，计算对应显示器的右下角位置
3. **与其他通知冲突**：如果同时有多个通知（如 OCR 完成通知），预览卡可能遮挡。缓解：通知队列管理，预览卡优先显示，其他通知排后
4. **拖拽兼容性**：部分应用（浏览器/web app）对拖放 DataObject 格式支持不同。缓解：DataObject 中包含多种格式（Bitmap/DIB/FileDrop/PNG 流）
5. **截图后立即贴图模式**：如果用户在截图中选择"截图并贴图"，预览卡可选不显示或自动缩为迷你。缓解：配置中增加"贴图模式下跳过预览卡"
