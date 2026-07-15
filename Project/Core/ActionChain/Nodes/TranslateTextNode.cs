namespace ToolBox.Core.ActionChain.Nodes;

public class TranslateTextNode : IActionNode
{
    public string NodeName => "翻译文本";
    public string NodeType => "TranslateText";
    public string NodeIcon => "🌐";

    public Task<ActionNodeResult> ExecuteAsync(ActionNodeContext context)
    {
        var text = context.PreviousOutput as string;
        if (string.IsNullOrEmpty(text))
            return Task.FromResult(new ActionNodeResult { IsSuccess = false, ErrorMessage = "无文本数据" });

        // TODO: 集成 LLM 翻译能力（需要 Agent API 支持直接文本请求）
        // 当前返回原文并提示用户手动翻译
        return Task.FromResult(new ActionNodeResult
        {
            IsSuccess = true,
            Output = text,
            Metadata = new()
            {
                ["needsTranslation"] = true,
                ["hint"] = "请将文本发送到 AI 助手进行翻译"
            }
        });
    }
}
