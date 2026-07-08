# QA 回归验证报告 — 浮动截图剪贴板清晰度修复

- **验证对象**：WPF/C# 截图剪贴板分辨率修复（工程师已完成，声称 `dotnet build` 0 错误）
- **验证人**：software-qa-engineer
- **结论**：✅ **PASS（未发现源码 Bug）**
- **智能路由**：NoOne（无需工程师返工，无测试代码需自修）

---

## 一、源码核实（6 个文件）

### 1. Models/ScrapWindow.cs（核心）
- `GetOriginalBitmap()`（L107-112）：缩略图模式且 `fullBitmap != null` 时返回 `fullBitmap`（全分辨率原图），否则返回 `sourceBitmap`。✅ 与声称一致。
- `NormalizeTo96Dpi`（L118-133）：已为 96 DPI 则原样返回；否则按原 `PixelWidth/Height` 重新 `BitmapSource.Create(..., 96, 96, ...)`，**像素尺寸不变，仅规范 DPI**。✅
- `CopyBitmapToClipboard`（L138-150）：`Clipboard.SetImage(NormalizeTo96Dpi(source))`，像素尺寸未被改。✅
- **缩略图路径污染核查**（`OnMouseDoubleClickHandler` L365-409）：
  - 进入：`fullBitmap = sourceBitmap`（L384 先保留原图引用）→ `SetImage(scaled)`（L399）把 `sourceBitmap` 改写为降采样图。`fullBitmap` 仍指向原图对象，**未被污染**。✅
  - 退出：`SetImage(fullBitmap)` 还原，`fullBitmap = null`（L380）。✅
- **全部复制/保存调用点均已改用 `GetOriginalBitmap()`**：`SaveToFile`(L330)、`CopyToClipboard`(L348)、菜单"复制"(L493)、“剪切”(L500)、“另存为”(L508)、“代办识别”(L529)、“发起对话”(L630)。**无遗漏**。✅
- `GetViewImage()`（L99）未改动，返回 `sourceBitmap`，仅被 `CImageStyleItems.cs`（L26/58/89）图像特效使用——应作用于当前视图，保持原样正确。✅

### 2. Services/Styles/CCopyStyleItem.cs
- `Apply` L23：`scrap.GetOriginalBitmap()`（原为 `GetViewImage()`）。✅ 缩略图下复制不再拿低清图。
- 备注：此处直接 `Clipboard.SetImage(bitmap)`（L28/33）未走 `NormalizeTo96Dpi`，与 ScrapWindow 复制路径不一致（见遗留项）。当前采集图均为 96 DPI，功能等价。⚠️ 非源码 Bug，仅一致性观察。

### 3. Core/Native/NativeMethods.cs
- 新增 `GetSystemMetrics` P/Invoke（L15-16）与 `SM_CXSCREEN`/`SM_CYSCREEN` 常量（L18-19）；`SetProcessDPIAware` 已存在。✅

### 4. App.xaml.cs
- `OnStartup` 开头（L23）调用 `SetProcessDPIAware()`，包 try/catch。✅

### 5. Views/Capture/CaptureWindow.cs
- `CaptureFullScreen`：`GetSystemMetrics(SM_CXSCREEN/CYSCREEN)` 取**物理像素**采集，`screenWidth<=0` 时回退 `SystemParameters.PrimaryScreen*`。✅
- `OnMouseLeftButtonUp` 裁剪：DIP 选区 ×（`imgSnap.Width / PrimaryScreenWidth`）换算为物理像素，从物理像素 `imgSnap` 裁剪。✅ DPI 换算正确，无错位。

### 6. Views/ClickCaptureWindow.cs
- 同样的物理像素采集（L43-44）与 `screenPoint × scaleX/scaleY` 坐标换算（L65-72）。✅ 裁剪区域与点击位置精确对应。

---

## 二、编译验证（dotnet build）

- 运行：`D:\Workspaces\ToolBox\Project` 下 `dotnet build -v q`（及一次输出到临时目录的重试）。
- **C# 编译错误（`error CS`）：0 条** —— 源码零编译错误，与工程师"0 错误"声明一致。
- 出现的"错误"均为**环境问题，非源码问题**：
  - 首次构建：`MSB3027 / MSB3021` —— 运行中的 `ToolBox.exe`（PID 42720）锁定 `bin\Debug\net8.0-windows\ToolBox.exe`，apphost 拷贝失败。
  - 临时输出构建：`MSB3026` 在拷贝 `e_sqlite3` 原生运行库时因目标 `runtimes\*` 目录不存在反复重试（我的 `-o` 重定向所致）。
- 既有约 198 条 nullable 警告属历史存量，与本次改动无关，未误报。

---

## 三、自动化测试

- **未写自动测试**：项目无既有 xUnit/NUnit 测试工程（`*.Tests.csproj`、`test/` 均不存在）；`ScrapWindow` 为 `Window`，直接实例化困难。按任务约定跳过自动测试，以下给出人工回归清单。

---

## 四、人工回归测试步骤清单（建议在高 DPI 屏 125%/150%/200% 各跑一遍）

1. **普通截图 → 复制 → 粘贴**：粘贴出的图片清晰，分辨率与屏幕选区像素一致（非降采样）。
2. **双击进缩略图 → 复制 → 粘贴**：粘贴应为**原图全分辨率**（验证 `GetOriginalBitmap` 在缩略图模式返回 `fullBitmap`），而非缩略图低清图。
3. **缩略图模式 → 右键"另存为"**：保存的 PNG 分辨率 = 原图全分辨率。
4. **整屏截图清晰度（CaptureWindow）**：高 DPI 下整屏截图不放糊，物理像素尺寸正确。
5. **点击截图清晰度（ClickCaptureWindow）**：点击截图清晰，裁剪区域与点击位置精确对应（无错位）。
6. **右键"剪切"**：复制出原图分辨率内容，且窗口关闭。
7. **高 DPI 裁剪对齐**：选区框与释放后生成截图位置/范围精确对应（验证 DIP→物理像素换算）。
8. **Ctrl+S 另存为**：保存分辨率 = 原图。
9. **"发起对话"/"代办识别"**：发送给 LLM 的图片为原图分辨率。
10. **退出缩略图**：视图恢复为原图；再次复制/另存仍为原图（验证 `fullBitmap` 已清空、无状态残留）。

---

## 五、已知风险 / 遗留项

1. **（低风险）CCopyStyleItem.Apply 未走 `NormalizeTo96Dpi`**：与 ScrapWindow 复制路径不一致。当前采集图均 96 DPI 功能等价；若未来来源为非 96 DPI（如粘贴的图片 scrap），此路径可能未防御性规范化。建议统一调用 `NormalizeTo96Dpi` 或抽出为共享方法。
2. **（极低风险）`NormalizeTo96Dpi` 未处理调色板/索引格式**：对 `Indexed8` 等索引格式未传递 palette，可能变色。截图路径均为 Bgr32/Pbgra32/Rgb24，不受影响。
3. **（范围外）多显示器/非主屏**：整屏采集基于主屏 `GetSystemMetrics(SM_CXSCREEN/CYSCREEN)` 与 `SystemParameters.PrimaryScreen*`，仅覆盖主屏；非主屏/多屏高 DPI 仍可能有偏差，原需求聚焦清晰度，属已知范围外。
4. **构建环境提示**：因运行中实例锁定 `bin\ToolBox.exe`，常规 `dotnet build` 会报 `MSB3027/3021` 拷贝失败而"失败"，但**非源码错误**，验证请以 `error CS` 计数为准。
