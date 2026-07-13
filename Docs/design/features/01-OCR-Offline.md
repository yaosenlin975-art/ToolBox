# 离线 OCR 文字识别

- **优先级**：P0
- **实现难度**：中
- **预估工作量**：8 人天
- **来源竞品**：PixPin（核心杀手锏）、ShareX（Tesseract 集成）、Snagit、CleanShot X

---

## 功能描述

为 ToolBox 截图系统增加离线 OCR 能力：截图后可一键提取区域中的文字，无需联网、无需 API Key。支持中英日韩等主流语言，识别结果可编辑、复制、翻译、发送到 AI 助手。同时在贴图窗口上支持"直接选中文字"交互——鼠标在贴图上划选区域即触发局部 OCR。

核心交互场景：
1. **截图 → OCR**：截图完成后工具栏出现"OCR"按钮，点击后在截图区域叠加 OCR 识别结果层
2. **贴图 → 选字**：鼠标在贴图 ScrapWindow 上拖拽选区 → 自动 OCR → 弹出文字结果浮层
3. **历史截图 → OCR**：在 HistoryView 中右键截图 → "识别文字"
4. **LLM 调用**：AI 助手可通过 Tool 对指定截图执行 OCR

---

## 技术方案

### 方案对比

| 方案 | 优点 | 缺点 | 推荐度 |
|------|------|------|--------|
| **Windows OCR API** (Windows.Media.Ocr) | 系统内置、零依赖、中英日韩支持好 | 仅 Win10+，需 UWP API 桥接，中文准确率一般 | ⭐⭐⭐ |
| **Tesseract** (.NET 绑定) | 开源成熟、100+ 语言、可训练 | 需分发语言数据包(~50MB/语言)，中文准确率中等 | ⭐⭐⭐⭐ |
| **PaddleOCR** (PaddleOCRSharp) | 中文准确率最高、轻量级模型 | .NET 绑定生态较弱，需 NuGet PaddleOCRSharp | ⭐⭐⭐⭐⭐ |

### 推荐方案：Tesseract + Windows OCR 双引擎回退

**主引擎**：Tesseract 5.x via **TesseractOCR** NuGet（或直接 P/Invoke libtesseract）
- 分发精简中文+英文语言包（`chi_sim.traineddata` + `eng.traineddata`，~15MB 压缩后）
- 语言数据放在 `%LocalAppData%/ToolBox/ocr/tessdata/`

**回退引擎**：Windows.Media.Ocr（当 Tesseract 不可用时）
- 无需额外依赖，作为兜底方案

**为什么不选 PaddleOCR**：当前 .NET 绑定（PaddleOCRSharp）依赖 C++ 运行时和大型模型文件（~100MB+），对桌面工具太重。可预留接口，后续按需切换。

### 依赖评估

| NuGet | 版本 | 用途 |
|-------|------|------|
| `TesseractOCR` | ≥5.2.5 | OCR 引擎 .NET 封装 |
| （可选）`PaddleOCRSharp` | ≥2.4.0 | 高精度中文 OCR，作为 v2 升级路径 |

无需新增系统依赖，Tesseract 语言数据以 Resource 形式随应用分发或首次使用时下载。

---

## 模块设计

### 文件位置

```
Core/
├── Ocr/
│   ├── IOcrEngine.cs             # OCR 引擎接口
│   ├── OcrResult.cs              # 识别结果模型（文本 + 区域 + 置信度）
│   ├── OcrEngineFactory.cs       # 引擎工厂（Tesseract / Windows OCR 选择）
│   ├── TesseractOcrEngine.cs     # Tesseract 实现
│   ├── WindowsOcrEngine.cs       # Windows.Media.Ocr 回退实现
│   └── OcrLanguageManager.cs     # 语言包下载/管理
├── Tools/
│   └── OcrTools.cs               # [Tool] 标记，供 LLM 调用 OCR
Services/
├── OcrService.cs                  # 单例：OCR 调度、缓存、并发控制
Views/
├── Ocr/
│   ├── OcrResultOverlay.xaml/.cs  # 截图上的 OCR 结果透明叠加层（可编辑文本）
│   └── OcrTextPopup.xaml/.cs      # 贴图 OCR 选字结果浮层
```

### 核心接口

```csharp
// Core/Ocr/IOcrEngine.cs
namespace ToolBox.Core.Ocr;

public interface IOcrEngine
{
    /// <summary>引擎名称</summary>
    string EngineName { get; }

    /// <summary>支持的语言列表</summary>
    IReadOnlyList<string> SupportedLanguages { get; }

    /// <summary>对图片执行 OCR</summary>
    /// <param name="imageData">图片字节流（PNG/BMP）</param>
    /// <param name="language">语言代码，如 "chi_sim" / "eng"</param>
    /// <param name="region">可选识别区域，null 为全图</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>识别结果列表</returns>
    Task<List<OcrResult>> RecognizeAsync(
        byte[] imageData, string language, Rect? region, CancellationToken cancellationToken);
}

// Core/Ocr/OcrResult.cs
namespace ToolBox.Core.Ocr;

public class OcrResult
{
    public string Text { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public Rect BoundingBox { get; set; }          // 归一化坐标 (0-1)
    public int LineIndex { get; set; }             // 行号
    public int ParagraphIndex { get; set; }        // 段落号
}

// Core/Ocr/OcrEngineFactory.cs
namespace ToolBox.Core.Ocr;

public static class OcrEngineFactory
{
    public static IOcrEngine Create(string engineType = "tesseract");
    public static IOcrEngine CreateDefault();
}

// Services/OcrService.cs
namespace ToolBox.Services;

public class OcrService
{
    public static OcrService Instance { get; }

    /// <summary>当前活跃引擎</summary>
    public IOcrEngine ActiveEngine { get; }

    /// <summary>对字节数组执行 OCR（自动选择引擎）</summary>
    public Task<List<OcrResult>> RecognizeAsync(byte[] imageData, string language = "chi_sim+eng");

    /// <summary>对 BitmapSource 执行 OCR</summary>
    public Task<List<OcrResult>> RecognizeAsync(BitmapSource image, string language = "chi_sim+eng");

    /// <summary>初始化引擎（启动时调用，检查语言包）</summary>
    public Task<bool> InitializeAsync();

    /// <summary>语言包就绪</summary>
    public event Action<bool> LanguagePackReady;
}

// Core/Ocr/OcrLanguageManager.cs
namespace ToolBox.Core.Ocr;

public class OcrLanguageManager
{
    /// <summary>已安装语言列表</summary>
    public IReadOnlyList<string> InstalledLanguages { get; }

    /// <summary>下载指定语言包</summary>
    public Task<bool> DownloadLanguageAsync(string languageCode, IProgress<int> progress);

    /// <summary>检查语言包是否已安装</summary>
    public bool IsLanguageInstalled(string languageCode);

    /// <summary>语言包存储路径</summary>
    public string TessDataPath { get; }
}
```

### LLM Tool 暴露

```csharp
// Core/Tools/OcrTools.cs
namespace ToolBox.Core.Tools;

public static class OcrTools
{
    [Tool("ocr_screenshot", "对最近一张截图执行 OCR 文字识别，返回提取的文字内容")]
    public static string OcrLatestScreenshot(
        [ToolParam("识别语言 (chi_sim/eng/chi_sim+eng)，默认 chi_sim+eng")] string language = "chi_sim+eng")
    {
        // 取 CacheManager 最近截图并调用 OcrService
    }
}
```

---

## 数据流

```
用户截图完成
    │
    ▼
CaptureWindow.OnCaptureComplete(bitmap)
    │
    ├─ 工具栏「OCR」按钮可见
    │       │
    │       ▼ 用户点击
    │  OcrService.RecognizeAsync(bitmapBytes, "chi_sim+eng")
    │       │
    │       ▼
    │  OcrEngineFactory.CreateDefault() → TesseractOcrEngine
    │       │
    │       ▼ TesseractOCR.ReadText(image)
    │  List<OcrResult> (文本 + 坐标 + 置信度)
    │       │
    │       ▼
    │  OcrResultOverlay 覆盖在截图上
    │  ┌──────────────────────┐
    │  │ 识别文本（逐行可编）  │
    │  │ ├ 行1: Hello World   │  ← 点击行高亮对应区域
    │  │ ├ 行2: 你好世界      │  ← 置信度低时标黄
    │  │ ├ ...                │
    │  │ [复制全部] [翻译] [发送AI]
    │  └──────────────────────┘
    │
    └─ ScrapWindow 贴图后
            │
            ▼ 用户鼠标拖拽选区
       ScrapWindow 捕获选区 → OcrService.RecognizeAsync(regionOnly)
            │
            ▼
       OcrTextPopup 浮层显示结果 → [复制] [翻译]
```

---

## UI 设计要点

### 截图工具栏 OCR 按钮
- 位置：截图完成后的工具栏中，标注工具栏右侧增加"OCR"按钮
- 图标：文字识别图标（T 带扫描线）
- 复用：`IconButton` Style，32×32px，Foreground=TextSecondaryBrush，Hover=TextPrimaryBrush

### OCR 结果叠加层 (OcrResultOverlay)
- 定位：截图 CaptureWindow 上的半透明覆盖层
- 背景：`BgElevatedBrush` 90% 不透明度
- 文本区域：`TextPrimaryBrush`，可编辑 `InputField` Style
- 置信度低的文本：`TextTertiaryBrush` 或黄色警告色
- 底部操作栏：`[复制全部]` `[翻译选中]` `[发送到 AI]` `[关闭]`
- 按钮：`BtnPrimary`（复制）、`OutlineButton`（翻译/发送AI）

### 贴图 OCR 选字
- 鼠标在 ScrapWindow 上拖拽 → 显示蓝色半透明选区
- 松开鼠标 → 自动 OCR → OcrTextPopup 浮层（280×200px 弹出窗口）
- OcrTextPopup：`WindowStyle=None, Topmost=True`，圆角 14，`ShadowFloat` 阴影

### 设置页面 OCR Section
- 位于 SettingsView，新增 "🔤 OCR 文字识别" Section
- 内容：引擎选择（Tesseract/Windows OCR）、已安装语言列表、下载更多语言按钮

---

## 与现有架构的集成

| 集成点 | 方式 |
|--------|------|
| **截图流程** | CaptureWindow 工具栏增加 OCR 按钮，调用 `OcrService.Instance` |
| **贴图窗口** | ScrapWindow 增加鼠标选区模式（通过新属性 `IsOcrSelectMode` 切换），选区后回调 `OcrService` |
| **历史视图** | HistoryView 右键菜单增加"识别文字"项 |
| **LLM 系统** | `OcrTools` 暴露 `[Tool]`，Agent 可在对话中调用 OCR |
| **热键** | 通过 `HotkeyManager` 注册 `Ctrl+Shift+O` → 对上次截图执行 OCR |
| **设置持久化** | OCR 引擎选择 & 语言偏好保存在 `ToolBoxOption.Data` |
| **主题适配** | 所有 OCR UI 使用 `DynamicResource` 引用主题色 |

---

## 风险与注意事项

1. **Tesseract 中文准确率**：复杂排版/小字体/低对比度场景准确率可能不理想。缓解措施：提供 Windows OCR 回退；后续可升级到 PaddleOCR
2. **语言包体积**：Tesseract 中文语言包约 50MB，首次下载体验。缓解：仅内置英文（~5MB），中文首次使用时引导下载；提供进度提示
3. **性能**：大图 OCR 可能耗时 1-3 秒。缓解：异步执行 + 后台线程 + 进度指示器；区域 OCR 只识别选区部分
4. **DPI 缩放**：Tesseract 对低分辨率图片识别差，需在 OCR 前放大到 300 DPI。缓解：预处理阶段自动缩放，`OcrService` 内部处理
5. **内存**：加载 Tesseract 引擎约占用 50-100MB。缓解：延迟初始化，空闲时释放引擎
6. **与现有标注的冲突**：贴图选字模式与标注模式互斥。缓解：ScrapWindow 增加 `InteractionMode` 枚举（View/Annotate/OcrSelect），通过右键菜单或快捷键切换
