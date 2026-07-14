// File: OcrService.cs
// OCR 调度服务。Tesseract 为主引擎，失败/不可用时回退到 Windows.Media.Ocr(WinRT)。
// 同时提供 GetLatestScreenshot() 供热键(Ctrl+Shift+O)与 LLM 工具获取最近截图。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using ToolBox.Core.Tools;

namespace ToolBox.Services.Ocr;

public class OcrService
{
    public static OcrService Instance { get; } = new();

    /// <summary>默认语言组合(简中+英文)，对应 AC4.2 默认值。</summary>
    public const string DefaultLanguage = "chi_sim+eng";

    /// <summary>Tesseract 是否可用(原生 DLL 加载成功且至少构造过一次 Engine)。首次使用时探测。</summary>
    public bool IsTesseractAvailable { get; private set; } = true;

    /// <summary>当前选定的引擎(读取自配置，"Tesseract" 或 "WindowsOCR")。</summary>
    public string PreferredEngine
    {
        get => ToolBox.Models.ToolBoxOption.Load().Data.OcrEngine;
    }

    private OcrService() { }

    /// <summary>指定语言包是否已安装。</summary>
    public bool IsLanguageInstalled(string languageSpec) =>
        LanguagePackManager.Instance.IsInstalled(languageSpec);

    /// <summary>列出已安装语言代码。</summary>
    public List<string> ListInstalledLanguages() =>
        LanguagePackManager.Instance.ListInstalled();

    /// <summary>
    /// 识别位图中的文字。
    /// </summary>
    /// <param name="image">待识别位图(将被 Freeze 以保证跨线程访问)</param>
    /// <param name="language">语言组合，如 "chi_sim+eng"</param>
    /// <param name="autoDownload">缺失语言包时是否自动下载(带进度)。UI 触发置 true，LLM 工具置 false。</param>
    /// <param name="progress">语言包下载进度回调</param>
    public async Task<OcrResult> RecognizeAsync(BitmapSource image, string? language = null,
        bool autoDownload = false, IProgress<(int percent, string status)>? progress = null,
        CancellationToken ct = default)
    {
        var lang = string.IsNullOrWhiteSpace(language) ? DefaultLanguage : language;
        var bytes = BitmapSourceToPngBytes(image);

        // 优先 Tesseract(配置允许且可用)
        if (PreferredEngine != "WindowsOCR" && IsTesseractAvailable)
        {
            if (autoDownload && !IsLanguageInstalled(lang))
            {
                var ok = await LanguagePackManager.Instance.EnsureAsync(lang, progress, ct);
                if (!ok)
                {
                    // 下载失败仍尝试用已安装的子集，实在不行走 Windows OCR
                }
            }

            try
            {
                var result = await Task.Run(() => RecognizeWithTesseract(bytes, lang), ct);
                if (result != null)
                {
                    IsTesseractAvailable = true;
                    return result;
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or IOException)
            {
                // 原生依赖缺失(MSVC++ 运行时未装等)，标记 Tesseract 不可用并回退
                IsTesseractAvailable = false;
                System.Diagnostics.Debug.WriteLine("[ToolBox] Tesseract 不可用，回退 Windows OCR: " + ex.Message);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ToolBox] Tesseract 识别失败，尝试 Windows OCR: " + ex.Message);
            }
        }

        // 回退: Windows OCR
        var winResult = await RecognizeWithWindowsOcrAsync(bytes, lang, ct);
        if (winResult != null) return winResult;

        return new OcrResult { EngineUsed = "None", ErrorMessage = "所有 OCR 引擎均不可用" };
    }

    /// <summary>识别图片文件(供 LLM 工具与历史右键使用)。</summary>
    public Task<OcrResult> RecognizeFileAsync(string imagePath, string? language = null, bool autoDownload = false,
        IProgress<(int percent, string status)>? progress = null, CancellationToken ct = default)
    {
        var bytes = File.ReadAllBytes(imagePath);
        return RecognizeBytesAsync(bytes, language, autoDownload, progress, ct);
    }

    /// <summary>识别 PNG 字节(内部共用入口)。</summary>
    private async Task<OcrResult> RecognizeBytesAsync(byte[] bytes, string? language, bool autoDownload,
        IProgress<(int percent, string status)>? progress, CancellationToken ct)
    {
        var lang = string.IsNullOrWhiteSpace(language) ? DefaultLanguage : language;

        if (PreferredEngine != "WindowsOCR" && IsTesseractAvailable)
        {
            if (autoDownload && !IsLanguageInstalled(lang))
                await LanguagePackManager.Instance.EnsureAsync(lang, progress, ct);

            try
            {
                var result = await Task.Run(() => RecognizeWithTesseract(bytes, lang), ct);
                if (result != null) return result;
            }
            catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or IOException)
            {
                IsTesseractAvailable = false;
                System.Diagnostics.Debug.WriteLine("[ToolBox] Tesseract 不可用，回退 Windows OCR: " + ex.Message);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ToolBox] Tesseract 识别失败: " + ex.Message);
            }
        }

        var winResult = await RecognizeWithWindowsOcrAsync(bytes, lang, ct);
        return winResult ?? new OcrResult { EngineUsed = "None", ErrorMessage = "所有 OCR 引擎均不可用" };
    }

    // ===== Tesseract 主引擎 =====

    private OcrResult? RecognizeWithTesseract(byte[] pngBytes, string language)
    {
        // Engine dataPath 指向 tessdata 的父目录(见 TesseractOCR 文档)
        using var engine = new TesseractOCR.Engine(
            LanguagePackManager.EngineDataPath, language, TesseractOCR.Enums.EngineMode.Default);
        using var img = TesseractOCR.Pix.Image.LoadFromMemory(pngBytes);
        using var page = engine.Process(img);

        var result = new OcrResult { EngineUsed = "Tesseract" };
        var sb = new System.Text.StringBuilder();
        foreach (var block in page.Layout)
        {
            foreach (var paragraph in block.Paragraphs)
            {
                foreach (var line in paragraph.TextLines)
                {
                    var text = (line.Text ?? string.Empty).Trim();
                    if (text.Length == 0) continue;
                    var ol = new OcrLine
                    {
                        Text = text,
                        Confidence = line.Confidence
                    };
                    if (line.BoundingBox != null)
                    {
                        var bb = line.BoundingBox.Value;
                        ol.Rect = new OcrRect { X = bb.X1, Y = bb.Y1, Width = bb.Width, Height = bb.Height };
                    }
                    result.Lines.Add(ol);
                    sb.AppendLine(text);
                }
            }
        }
        result.FullText = sb.ToString().TrimEnd();
        return result;
    }

    // ===== Windows OCR 回退引擎(WinRT) =====

    private async Task<OcrResult?> RecognizeWithWindowsOcrAsync(byte[] pngBytes, string language, CancellationToken ct)
    {
        try
        {
            return await Task.Run(async () =>
            {
                var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                using (var dw = new Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0)))
                {
                    dw.WriteBytes(pngBytes);
                    await dw.StoreAsync().AsTask(ct);
                }
                stream.Seek(0);

                var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream).AsTask(ct);
                var softwareBitmap = await decoder.GetSoftwareBitmapAsync().AsTask(ct);
                var winLang = MapToWindowsLanguage(language);
                var engine = winLang != null
                    ? Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language(winLang))
                    : null;
                engine ??= Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
                if (engine == null) return null;

                var ocrResult = await engine.RecognizeAsync(softwareBitmap).AsTask(ct);
                var result = new OcrResult { EngineUsed = "WindowsOCR" };
                var sb = new System.Text.StringBuilder();
                foreach (var line in ocrResult.Lines)
                {
                    var text = (line.Text ?? string.Empty).Trim();
                    if (text.Length == 0) continue;
                    var ol = new OcrLine { Text = text, Confidence = -1 };
                    if (line.Words != null && line.Words.Count > 0)
                    {
                        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                        foreach (var w in line.Words)
                        {
                            var r = w.BoundingRect;
                            if (r.X < minX) minX = r.X;
                            if (r.Y < minY) minY = r.Y;
                            if (r.X + r.Width > maxX) maxX = r.X + r.Width;
                            if (r.Y + r.Height > maxY) maxY = r.Y + r.Height;
                        }
                        ol.Rect = new OcrRect
                        {
                            X = (int)minX, Y = (int)minY,
                            Width = (int)(maxX - minX), Height = (int)(maxY - minY)
                        };
                    }
                    result.Lines.Add(ol);
                    sb.AppendLine(text);
                }
                result.FullText = sb.ToString().TrimEnd();
                return result;
            }, ct);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[ToolBox] Windows OCR 失败: " + ex.Message);
            return null;
        }
    }

    /// <summary>Tesseract 语言代码 → Windows OCR 语言标签。取首个语言。</summary>
    private static string? MapToWindowsLanguage(string spec)
    {
        var first = spec.Split(new[] { '+', ' ', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .FirstOrDefault() ?? "";
        return first switch
        {
            "chi_sim" => "zh-Hans-CN",
            "chi_tra" => "zh-Hant-TW",
            "eng" => "en-US",
            "jpn" => "ja",
            "kor" => "ko",
            "" => null,
            _ => null
        };
    }

    // ===== 截图获取 =====

    /// <summary>
    /// 获取最近一次截图(扫描 CachePath 取最新目录的 Image.png)。
    /// 供 Ctrl+Shift+O 热键与 ocr_screenshot 工具使用。
    /// </summary>
    public BitmapSource? GetLatestScreenshot()
    {
        var cachePath = CacheManager.CachePath;
        if (!Directory.Exists(cachePath)) return null;

        DirectoryInfo? latest = null;
        // 目录名为 yyyyMMddHHmmssfff，字典序即时间序；取最大者
        foreach (var dir in Directory.GetDirectories(cachePath, "*", SearchOption.TopDirectoryOnly))
        {
            var info = new DirectoryInfo(dir);
            // 仅当含 Image.png 才视为有效截图目录
            if (!File.Exists(Path.Combine(dir, "Image.png"))) continue;
            if (latest == null || string.CompareOrdinal(info.Name, latest.Name) > 0)
                latest = info;
        }
        if (latest == null) return null;

        var file = Path.Combine(latest.FullName, "Image.png");
        if (!File.Exists(file)) return null;

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = new Uri(file);
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    /// <summary>最近截图的文件路径(供 LLM 工具按需读取)。</summary>
    public string? GetLatestScreenshotPath()
    {
        var cachePath = CacheManager.CachePath;
        if (!Directory.Exists(cachePath)) return null;
        DirectoryInfo? latest = null;
        foreach (var dir in Directory.GetDirectories(cachePath, "*", SearchOption.TopDirectoryOnly))
        {
            if (!File.Exists(Path.Combine(dir, "Image.png"))) continue;
            var info = new DirectoryInfo(dir);
            if (latest == null || string.CompareOrdinal(info.Name, latest.Name) > 0)
                latest = info;
        }
        return latest == null ? null : Path.Combine(latest.FullName, "Image.png");
    }

    // ===== 工具方法 =====

    private static byte[] BitmapSourceToPngBytes(BitmapSource image)
    {
        if (image == null) return Array.Empty<byte>();
        // Freeze 以保证跨线程访问;已 Frozen 的 Freeze() 是空操作
        if (!image.IsFrozen) image.Freeze();
        using var ms = new MemoryStream();
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(image));
        enc.Save(ms);
        return ms.ToArray();
    }
}
