// File: OcrTools.cs
// LLM 可调用的 OCR 工具。Agent 在对话中可调用 ocr_screenshot 识别最近截图(AC4.1/AC4.2)。
// 工具在 ChatView 构造时注册到 ToolRegistry。返回 JSON 序列化的识别文本摘要。
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using ToolBox.Services.Ocr;

namespace ToolBox.Core.Tools;

public static class OcrTools
{
    [Tool("ocr_screenshot", "识别最近一次截图中的文字(离线 OCR)。返回识别到的文本，按行分隔。")]
    public static async Task<string> OcrScreenshot(
        [ToolParam("语言组合，如 chi_sim+eng / eng / jpn", Required = false)] string language = "chi_sim+eng")
    {
        var path = OcrService.Instance.GetLatestScreenshotPath();
        if (path == null || !File.Exists(path))
            return "未找到最近的截图，请先截图后再调用。";

        // LLM 调用不触发语言包下载弹窗，缺包时自动回退 Windows OCR
        var result = await OcrService.Instance.RecognizeFileAsync(path, language, autoDownload: false);
        if (result.IsEmpty)
            return JsonSerializer.Serialize(new { success = false, engine = result.EngineUsed, error = result.ErrorMessage ?? "未识别到文字" });

        return JsonSerializer.Serialize(new
        {
            success = true,
            engine = result.EngineUsed,
            lineCount = result.Lines.Count,
            text = result.FullText
        });
    }
}
