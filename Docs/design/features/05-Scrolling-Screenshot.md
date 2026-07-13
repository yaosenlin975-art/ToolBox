# 长截图/滚动截图

- **优先级**：P1
- **实现难度**：中
- **预估工作量**：7 人天
- **来源竞品**：PixPin（原生长截图）、ShareX（滚动截图）、Snagit（Scrolling Capture）、CleanShot X

---

## 功能描述

为 ToolBox 增加长截图（滚动截图）能力：用户在截图模式下选择一个窗口或区域后，ToolBox 自动向目标窗口发送滚动指令（鼠标滚轮/PageDown），逐帧截取并拼接为一张完整的长图。支持垂直滚动和水平滚动两种模式。

核心交互：
1. **截图入口**：`Ctrl+Shift+L` 热键直接进入长截图模式，也可在截图选区后工具栏选择"长截图"
2. **自动滚动**：选区确认后，自动向目标窗口发送滚动事件，逐帧截取
3. **智能停止**：检测页面底部（连续两帧相同或滚动无变化）自动停止；用户也可手动按 ESC 停止
4. **预览编辑**：截取完成后进入预览窗口，可裁剪/调整拼接区域
5. **输出**：保存为 PNG / 复制到剪贴板 / 贴图置顶

---

## 技术方案

### 窗口滚动 + 图像拼接

**方案一：SendMessage 模拟滚动**（推荐）
```csharp
// 向目标窗口发送鼠标滚轮消息
SendMessage(targetHwnd, WM_MOUSEWHEEL, wParam, lParam);
// 或发送 PageDown 按键
SendMessage(targetHwnd, WM_KEYDOWN, VK_NEXT, 0);
```

**方案二：InputSimulator 模拟输入**
使用 Windows Input Simulator 库发送模拟输入。但需要目标窗口处于前台。

**推荐方案一**：`SendMessage` 不需要窗口前台，更可靠。

### 图像拼接算法

1. **逐帧截取**：每次滚动后截取窗口客户区
2. **特征匹配**：相邻帧之间通过像素对比找到重叠区域
   - 取前一帧底部 20% 和后一帧顶部 20% 做模板匹配
   - 匹配算法：逐行像素比较，找到最高相似度行作为拼接点
3. **无缝拼接**：在重叠区域做线性渐隐融合（feathering），消除拼接缝隙
4. **最终裁剪**：裁掉首帧顶部非内容区（如窗口标题栏）和末帧底部空白

### 依赖评估

无新增 NuGet 依赖。完全基于 Win32 P/Invoke + GDI+/WPF 图形处理。

| 能力 | 技术 |
|------|------|
| 滚动模拟 | `user32.dll` → `SendMessage(WM_MOUSEWHEEL/WM_VSCROLL)` |
| 窗口截取 | `Graphics.CopyFromScreen` 或 `CaptureWindow` 现有逻辑 |
| 模板匹配 | 纯 C# 像素比对（无 OpenCV 依赖） |
| 图像拼接 | `System.Drawing.Graphics.DrawImage` / WPF `DrawingVisual` |

---

## 模块设计

### 文件位置

```
Core/
├── Screenshot/
│   ├── ScrollingCaptureEngine.cs     # 滚动截图引擎（滚动控制 + 逐帧捕获）
│   ├── ImageStitcher.cs              # 图像拼接器（特征匹配 + 融合）
│   └── ScrollDetector.cs             # 滚动停止检测
Views/
├── Screenshot/
│   └── ScrollingCapturePreview.xaml/.cs  # 长截图预览编辑窗口
```

### 核心接口

```csharp
// Core/Screenshot/ScrollingCaptureEngine.cs
namespace ToolBox.Core.Screenshot;

public enum EScrollDirection { Vertical, Horizontal }

public class ScrollingCaptureConfig
{
    public IntPtr TargetWindow { get; set; }          // 目标窗口句柄
    public EScrollDirection Direction { get; set; }
    public int MaxFrames { get; set; } = 50;          // 最大截取帧数
    public int ScrollStepPixels { get; set; } = 120;  // 每次滚动像素数
    public double OverlapRatio { get; set; } = 0.15;  // 重叠比例（用于匹配）
}

public class ScrollingCaptureEngine
{
    /// <summary>开始长截图（返回拼接结果）</summary>
    public Task<BitmapSource> CaptureAsync(
        ScrollingCaptureConfig config,
        IProgress<ScrollingCaptureProgress> progress,
        CancellationToken cancellationToken);
}

public class ScrollingCaptureProgress
{
    public int CurrentFrame { get; set; }
    public int EstimatedTotal { get; set; }
    public BitmapSource? LastFrame { get; set; }      // 最新捕获帧预览
    public string StatusText { get; set; } = string.Empty;
}

// Core/Screenshot/ImageStitcher.cs
namespace ToolBox.Core.Screenshot;

public class ImageStitcher
{
    /// <summary>拼接多帧为一张长图</summary>
    /// <param name="frames">按捕获顺序排列的帧列表</param>
    /// <param name="overlapRatio">帧间重叠比例 0.1-0.3</param>
    /// <returns>拼接后的完整长图</returns>
    public BitmapSource Stitch(List<BitmapSource> frames, double overlapRatio = 0.15);
}

// Core/Screenshot/ScrollDetector.cs
namespace ToolBox.Core.Screenshot;

public class ScrollDetector
{
    /// <summary>检测是否已滚动到底部</summary>
    /// <param name="previousFrame">上一帧</param>
    /// <param name="currentFrame">当前帧</param>
    /// <param name="scrollDirection">滚动方向</param>
    /// <returns>true=已到底部</returns>
    public bool IsAtBottom(BitmapSource previousFrame, BitmapSource currentFrame,
                           EScrollDirection scrollDirection);
}
```

---

## 数据流

```
用户热键 Ctrl+Shift+L 或截图工具栏选择"长截图"
    │
    ▼
进入选区模式 (复用 CaptureWindow, 标注"长截图模式")
    │
    ▼ 用户选择窗口/区域
ScrollingCaptureEngine.CaptureAsync(config, progress, ct)
    │
    ├─ 循环 (最多 MaxFrames 次):
    │   │
    │   ├─ Graphics.CopyFromScreen(窗口区域) → BitmapSource frame[i]
    │   │
    │   ├─ ScrollDetector.IsAtBottom(frame[i-1], frame[i])
    │   │   └─ true → 停止
    │   │
    │   ├─ SendMessage(hwnd, WM_MOUSEWHEEL, -ScrollStep) → 向下滚动
    │   │   (Wait 200ms 等待渲染)
    │   │
    │   └─ Report progress (CurrentFrame, LastFrame preview)
    │
    ▼
ImageStitcher.Stitch(frames, overlapRatio=0.15)
    │
    ├─ 逐帧匹配重叠区域 (底部20% vs 顶部20%)
    ├─ 线性渐隐融合重叠区
    └─ 裁剪首帧标题栏 / 末帧空白
    │
    ▼
ScrollingCapturePreview 显示结果
    ├─ 完整长图预览（可缩放）
    ├─ [保存 PNG] [复制] [贴图] [裁剪编辑]
    └─ 帧标记线（显示各帧边界，可拖拽调整拼接点）
```

---

## UI 设计要点

### 长截图模式指示
- 截图选区工具栏顶部显示 `📜 长截图模式` 标签（`AccentSoftBrush` 背景，13px）
- 引导提示："选择窗口或区域后自动开始滚动截取 / ESC 取消"
- 复用：CaptureWindow 现有覆盖层，增加模式指示文字

### 截取进度
- 截图区域底部显示小型进度条（`AccentBrush`, 4px 高）+ 帧计数 "第 5/∞ 帧"
- 实时显示最新帧缩略图（120px 宽），浮在进度条上方

### 预览编辑窗口 (ScrollingCapturePreview)
- 尺寸：自适应（最大 1200×800px），`WindowStyle=None, AllowsTransparency=True`
- 工具栏：`[保存]` `[复制到剪贴板]` `[贴图置顶]` `[裁剪]` `[撤销]`
- 帧标记线：长图中以半透明红线标记各帧拼接边界，可拖拽调整
- 缩放：Ctrl+滚轮缩放预览
- 背景：`BgPrimaryBrush`，控件用 `CardStyle` + `BtnPrimary`/`OutlineButton`

---

## 与现有架构的集成

| 集成点 | 方式 |
|--------|------|
| **截图窗口** | 复用 CaptureWindow，增加 `ELongScreenshotMode` 模式 |
| **热键** | `HotkeyManager` 注册 `Ctrl+Shift+L` → 直接进入长截图模式 |
| **NativeMethods** | 新增 `SendMessage(WM_MOUSEWHEEL)` / `SendMessage(WM_VSCROLL)` / `VK_NEXT` 声明 |
| **贴图系统** | 长截图结果可通过 ScrapBook 创建 ScrapWindow 贴图 |
| **缓存系统** | 长截图结果自动存入 CacheManager（与普通截图一致） |
| **设置页面** | SettingsView 新增 "📜 长截图" Section：滚动步长 / 最大帧数 / 停止检测灵敏度 |

---

## 风险与注意事项

1. **不同应用滚动行为差异**：浏览器（平滑滚动）、Office（离散页面）、VS Code（代码行滚动）滚动机制不同。缓解：支持多种滚动模式切换（鼠标滚轮/PageDown/箭头键），用户可手动选择
2. **渲染延迟**：窗口内容可能未及时渲染就截取下一帧。缓解：每次滚动后等待 200-500ms（可配置）；使用 `SystemEvents` 或 `RenderDispatcher` 等待空闲
3. **高 DPI**：`CopyFromScreen` 和 `SendMessage` 的坐标系统在 DPI 缩放下可能不同。缓解：统一使用物理像素坐标
4. **拼接精度**：复杂背景（纯色/渐变）可能导致模板匹配失败。缓解：当匹配置信度低于阈值时，使用固定重叠量拼接，并标记"可能需要手动调整"
5. **内存占用**：50 帧 1920×1080 图片约 300MB 内存。缓解：逐帧压缩存储（JPEG Quality=80），仅在拼接时加载全分辨率
6. **窗口最小化/不可见**：目标窗口必须可见才能截取。缓解：检测窗口状态，若最小化则提示"请先显示目标窗口"
