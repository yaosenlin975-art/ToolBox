// File: OcrResult.cs
// OCR 识别结果模型。承载按行组织的文本、置信度与边界框，供 UI 与 LLM 工具共用。
// 不依赖 TesseractOCR / WinRT 类型，避免引擎细节泄漏到上层。
using System.Collections.Generic;

namespace ToolBox.Services.Ocr;

/// <summary>图像像素坐标系下的矩形区域。</summary>
public class OcrRect
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

/// <summary>单行识别结果。</summary>
public class OcrLine
{
    /// <summary>识别文本。</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>置信度(0-100)，-1 表示引擎未提供。</summary>
    public float Confidence { get; set; } = -1;

    /// <summary>边界框(图像像素坐标)，可能为 null。</summary>
    public OcrRect? Rect { get; set; }

    /// <summary>置信度低于 60% 视为低置信度(AC1.4 黄色标记阈值)。</summary>
    public bool IsLowConfidence => Confidence >= 0 && Confidence < 60;
}

/// <summary>整图识别结果。</summary>
public class OcrResult
{
    public List<OcrLine> Lines { get; set; } = new();

    /// <summary>全部文本(按行拼接)。</summary>
    public string FullText { get; set; } = string.Empty;

    /// <summary>实际使用的引擎名(Tesseract / WindowsOCR)。</summary>
    public string EngineUsed { get; set; } = string.Empty;

    /// <summary>错误信息(成功时为 null)。</summary>
    public string? ErrorMessage { get; set; }

    public bool IsEmpty => Lines.Count == 0 || string.IsNullOrWhiteSpace(FullText);
}
