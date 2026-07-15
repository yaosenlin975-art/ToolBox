using ToolBox.Services.Ocr;

namespace ToolBox.Core.ActionChain.Nodes;

public class OcrExtractNode : IActionNode
{
    public string NodeName => "OCR 提取文字";
    public string NodeType => "OcrExtract";
    public string NodeIcon => "🔤";

    public async Task<ActionNodeResult> ExecuteAsync(ActionNodeContext context)
    {
        if (context.Screenshot == null)
            return new ActionNodeResult { IsSuccess = false, ErrorMessage = "无截图数据" };
        try
        {
            var lang = Models.ToolBoxOption.Load().Data.OcrLanguage;
            var result = await OcrService.Instance.RecognizeAsync(context.Screenshot, lang);
            if (result.IsEmpty)
                return new ActionNodeResult { IsSuccess = false, ErrorMessage = "OCR 未识别到文字" };
            return new ActionNodeResult
            {
                IsSuccess = true, Output = result.FullText,
                Metadata = new() { ["lineCount"] = result.Lines.Count, ["engine"] = result.EngineUsed }
            };
        }
        catch (Exception ex) { return new ActionNodeResult { IsSuccess = false, ErrorMessage = ex.Message }; }
    }
}
