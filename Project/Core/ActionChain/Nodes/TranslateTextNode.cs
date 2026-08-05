using ToolBox.Core.Llm;

namespace ToolBox.Core.ActionChain.Nodes;

public class TranslateTextNode : IActionNode
{
    public string NodeName => "翻译文本";
    public string NodeType => "TranslateText";
    public string NodeIcon => "🌐";

    public async Task<ActionNodeResult> ExecuteAsync(ActionNodeContext context)
    {
        var text = context.PreviousOutput as string;
        if (string.IsNullOrEmpty(text))
            return new ActionNodeResult { IsSuccess = false, ErrorMessage = "无文本数据" };

        try
        {
            var translated = await LlmTranslationService.TranslateAsync(
                text,
                targetLanguage: "English",
                ct: context.CancellationToken);

            return new ActionNodeResult
            {
                IsSuccess = true,
                Output = translated,
                Metadata = new()
                {
                    ["translated"] = true,
                    ["sourceLength"] = text.Length,
                    ["targetLength"] = translated.Length
                }
            };
        }
        catch (InvalidOperationException ex)
        {
            return new ActionNodeResult
            {
                IsSuccess = false,
                ErrorMessage = $"翻译失败: {ex.Message}"
            };
        }
        catch (OperationCanceledException)
        {
            return new ActionNodeResult
            {
                IsSuccess = false,
                ErrorMessage = "翻译操作已取消"
            };
        }
        catch (Exception ex)
        {
            return new ActionNodeResult
            {
                IsSuccess = false,
                ErrorMessage = $"翻译异常: {ex.Message}"
            };
        }
    }
}