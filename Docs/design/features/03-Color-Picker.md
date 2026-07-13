# 屏幕取色器

- **优先级**：P0
- **实现难度**：低
- **预估工作量**：3 人天
- **来源竞品**：Snipaste（取色器）、PowerToys ColorPicker（Win+Shift+C）、ShareX（颜色选择器）

---

## 功能描述

为 ToolBox 增加全局取色器：用户按下热键 `Ctrl+Shift+C` 后，鼠标变为取色模式——屏幕放大镜跟随鼠标，实时显示当前像素色值（HEX/RGB/HSL 三格式）。点击鼠标左键将色值复制到剪贴板，同时弹出色值历史面板供快速选择。

核心交互：
1. **取色模式**：热键唤起 → 鼠标指针替换为十字取色光标 → 屏幕放大镜（200% 缩放）悬浮在鼠标旁 → 实时更新色值
2. **取色完成**：左键点击 → 色值复制到剪贴板 → 屏幕短暂闪烁提示 → 退出取色模式
3. **色值历史**：取色完成后右下角弹出小型历史面板，展示最近 10 个取色结果，点击可回取
4. **格式切换**：取色时可按 Tab 键切换输出格式（HEX → RGB → HSL → HEX）

---

## 技术方案

### P/Invoke 方案

全屏取色器的核心技术是获取屏幕任意位置像素颜色：

```csharp
// 获取桌面 DC
IntPtr hdc = GetDC(IntPtr.Zero);          // user32.dll
// 读取像素
uint pixel = GetPixel(hdc, x, y);         // gdi32.dll
// 释放 DC
ReleaseDC(IntPtr.Zero, hdc);              // user32.dll
```

**放大镜实现**：以鼠标坐标为中心截取 30×30px 区域，通过 `Graphics.CopyFromScreen` 获得位图，然后缩放到 120×120px 显示。中心像素高亮框标注。

### 色值计算

```csharp
byte r = (byte)((pixel >> 0) & 0xFF);
byte g = (byte)((pixel >> 8) & 0xFF);
byte b = (byte)((pixel >> 16) & 0xFF);

// HEX
string hex = $"#{r:X2}{g:X2}{b:X2}";

// RGB
string rgb = $"rgb({r}, {g}, {b})";

// HSL
ColorToHsl(r, g, b, out h, out s, out l);
string hsl = $"hsl({h:F0}°, {s:F0}%, {l:F0}%)";
```

### 依赖评估

无新增 NuGet 依赖。全部基于 Win32 P/Invoke + GDI+。

| 能力 | 技术 |
|------|------|
| 像素读取 | `gdi32.dll` → `GetPixel` |
| 屏幕截图 | `Graphics.CopyFromScreen` |
| 放大镜渲染 | WPF `RenderTargetBitmap` / `WriteableBitmap` |
| 全局热键 | 现有 `HotkeyManager` 注册 `Ctrl+Shift+C` |
| 颜色空间转换 | 纯 C# 数学计算（RGB→HSL/HSV） |

---

## 模块设计

### 文件位置

```
Core/
├── ColorPicker/
│   ├── ColorPickerService.cs           # 取色器核心逻辑（像素读取 + 色值计算）
│   └── ColorHistoryStore.cs            # 取色历史持久化（JSON，最近50条）
Core/
├── Native/
│   └── NativeMethods.cs                # 扩展: GetPixel / CopyFromScreen 声明
Views/
├── ColorPicker/
│   └── ColorPickerOverlay.xaml/.cs      # 全屏透明覆盖层 + 放大镜 + 色值面板
```

### 核心接口

```csharp
// Core/ColorPicker/ColorPickerService.cs
namespace ToolBox.Core.ColorPicker;

public enum EColorFormat { Hex, Rgb, Hsl }

public class ColorInfo
{
    public Color Color { get; set; }
    public string Hex { get; set; }
    public string Rgb { get; set; }
    public string Hsl { get; set; }
    public Point ScreenPosition { get; set; }   // 取色屏幕坐标
    public DateTime PickedAt { get; set; }
}

public class ColorPickerService
{
    public static ColorPickerService Instance { get; }

    /// <summary>当前输出格式</summary>
    public EColorFormat OutputFormat { get; set; }

    /// <summary>获取指定屏幕坐标的颜色</summary>
    public ColorInfo PickColor(int screenX, int screenY);

    /// <summary>获取鼠标周围放大镜区域截图</summary>
    public BitmapSource CaptureZoomRegion(int centerX, int centerY, int zoomSize = 30);

    /// <summary>切换输出格式</summary>
    public ColorPickerService ToggleFormat();

    /// <summary>格式切换通知</summary>
    public event Action<EColorFormat>? FormatChanged;
}

// Core/ColorPicker/ColorHistoryStore.cs
namespace ToolBox.Core.ColorPicker;

public class ColorHistoryStore
{
    public static ColorHistoryStore Instance { get; }

    public IReadOnlyList<ColorInfo> History { get; }

    /// <summary>添加取色记录</summary>
    public ColorHistoryStore Add(ColorInfo info);

    /// <summary>清空历史</summary>
    public ColorHistoryStore Clear();

    public event Action? HistoryChanged;
}
```

### LLM Tool 暴露

```csharp
// Core/Tools/ColorTools.cs
namespace ToolBox.Core.Tools;

public static class ColorTools
{
    [Tool("pick_color", "获取指定屏幕坐标的颜色值")]
    public static string PickColor(
        [ToolParam("屏幕 X 坐标")] int x,
        [ToolParam("屏幕 Y 坐标")] int y,
        [ToolParam("输出格式: hex/rgb/hsl")] string format = "hex");
}
```

---

## 数据流

```
用户按下 Ctrl+Shift+C
    │
    ▼
HotkeyManager → ColorPickerOverlay 显示
    │
    ▼
ColorPickerOverlay (全屏透明窗口, Topmost, IsHitTestVisible=false 除中心十字外)
    │
    ├─ 订阅 MouseMove (通过低层钩子或定时器 60fps 刷新)
    │       │
    │       ▼
    │  ColorPickerService.PickColor(mouseX, mouseY)
    │       │
    │       ├─ GetDC → GetPixel → ReleaseDC → ColorInfo
    │       └─ CaptureZoomRegion(mouseX, mouseY, 30) → BitmapSource 放大镜
    │
    ├─ 放大镜渲染 (120×120px, 跟随鼠标右上偏移)
    │   ┌──────────────────────┐
    │   │  [放大镜区域]        │  ← 30×30 放大 4×
    │   │   ┌──┐               │
    │   │   │██│ ← 中心像素框  │
    │   │   └──┘               │
    │   │  #E84343             │  ← 色值 (当前格式)
    │   │  rgb(232,67,67)      │
    │   │  hsl(0°,71%,59%)     │
    │   └──────────────────────┘
    │
    ▼ 用户点击左键
ColorPickerService.PickColor → ColorInfo
    │
    ├─ Clipboard.SetText(colorInfo.Hex) → 复制到剪贴板
    ├─ ColorHistoryStore.Add(colorInfo) → 持久化
    ├─ 屏幕闪烁提示 (ColorPickerOverlay.FlashSuccess)
    └─ ColorPickerOverlay 关闭 (退出取色模式)

用户按 Tab → ToggleFormat() → Hex → RGB → HSL → Hex ...
用户按 ESC → ColorPickerOverlay 关闭
```

---

## UI 设计要点

### ColorPickerOverlay（全屏覆盖层）
- 尺寸：覆盖所有屏幕（`Left=0, Top=0, Width=SystemParameters.VirtualScreenWidth, ...`）
- 属性：`WindowStyle=None, AllowsTransparency=True, Topmost=True, IsHitTestVisible=False`
- 背景：透明（仅放大镜和色值面板有内容）
- 放大镜定位：鼠标位置右上偏移 20px
- 放大镜样式：120×120px Border，`Background=White, BorderBrush=BorderDefaultBrush, CornerRadius=8, Effect=ShadowMd`
- 中心十字：使用 Canvas 画交叉线（`AccentBrush`, StrokeThickness=1）

### 色值显示
- 字号：HEX 18px Bold（`TextPrimaryBrush`），RGB/HSL 11px（`TextTertiaryBrush`）
- 切换提示：Tab 键切换时，当前格式字号放大动画
- 色块预览：32×32px 矩形，`Fill` 绑定当前颜色的 SolidColorBrush

### 取色成功提示
- 屏幕中央短暂闪烁（300ms）：圆形色块（60px）从鼠标位置缩小消失
- 右下角 Toast 提示：`"已复制 #E84343"` + 淡入淡出动画

### 色值历史面板
- 触发器：取色完成后自动弹出，3s 无操作自动消失
- 位置：屏幕右下角（迷你窗口上方偏移）
- 内容：最近 10 个色值，排列为两行 5 列色块（24×24 Circle）
- 点击色块 → 复制该色值到剪贴板

---

## 与现有架构的集成

| 集成点 | 方式 |
|--------|------|
| **热键体系** | `HotkeyManager` 注册 `Ctrl+Shift+C` → 唤起 ColorPickerOverlay |
| **NativeMethods** | 新增 `GetDC`/`ReleaseDC`/`GetPixel`（gdi32.dll）P/Invoke 声明 |
| **托盘菜单** | 可选：托盘右键菜单新增 "🎨 取色器" 项 |
| **截图协同** | 截图工具栏可增加"取色"子模式（从截图区域内取色，精度更高） |
| **LLM 系统** | `ColorTools.PickColor` 暴露为 Tool |
| **主题适配** | 所有 UI 使用 `DynamicResource`（AccentBrush / TextPrimaryBrush / BgElevatedBrush） |
| **持久化** | 色值历史保存到 `%AppData%/ToolBox/color_history.json`（最近 100 条） |
| **设置页面** | SettingsView 新增 "🎨 取色器" Section：默认格式 / 放大镜倍率 / 历史记录数 |

---

## 风险与注意事项

1. **DPI 缩放**：高 DPI 屏幕下 `GetPixel` 坐标需要做 DPI 感知处理。缓解：应用启动时声明 `SetProcessDPIAware()`（已存在），确保取色坐标与物理像素一致
2. **多显示器**：`GetPixel` 只能读取当前主 DC 的像素，跨屏取色需要分别获取各显示器 DC。缓解：使用 `MonitorFromPoint` + 各显示器 DC 的 `GetPixel`
3. **透明窗口点击穿透**：全屏覆盖层不能阻挡鼠标点击。缓解：ColorPickerOverlay 设置 `IsHitTestVisible=False`，通过低层鼠标钩子或 `GetCursorPos` 轮询获取坐标
4. **HDR 显示器**：HDR 模式下 GDI `GetPixel` 可能返回错误颜色。缓解：Win10+ 可用 `Windows.Graphics.Capture` API 作为备选（但引入 UWP 依赖）
5. **性能**：60fps 鼠标跟踪需要高效的像素读取。缓解：仅在鼠标移动时更新；限制放大镜区域大小；`GetPixel` 本身极快（<1μs）
