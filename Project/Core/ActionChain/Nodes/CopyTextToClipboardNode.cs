using System.Windows;

namespace ToolBox.Core.ActionChain.Nodes;

public class CopyTextToClipboardNode : IActionNode
{
    public string NodeName => "复制文本到剪贴板";
    public string NodeType => "CopyTextToClipboard";
    public string NodeIcon => "📋";

    public Task<ActionNodeResult> ExecuteAsync(ActionNodeContext context)
    {
        var text = context.PreviousOutput as string;
        if (string.IsNullOrEmpty(text))
            return Task.FromResult(new ActionNodeResult { IsSuccess = false, ErrorMessage = "无文本数据" });
        try
        {
            Clipboard.SetText(text);
            return Task.FromResult(new ActionNodeResult { IsSuccess = true, Output = $"已复制 {text.Length} 字符" });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ActionNodeResult { IsSuccess = false, ErrorMessage = ex.Message });
        }
    }
}
