using System.Windows;
using System.Windows.Media.Imaging;

namespace ToolBox.Core.ActionChain.Nodes;

public class CopyToClipboardNode : IActionNode
{
    public string NodeName => "复制到剪贴板";
    public string NodeType => "CopyToClipboard";
    public string NodeIcon => "📋";

    public Task<ActionNodeResult> ExecuteAsync(ActionNodeContext context)
    {
        if (context.Screenshot == null)
            return Task.FromResult(new ActionNodeResult { IsSuccess = false, ErrorMessage = "无截图数据" });
        try
        {
            Clipboard.SetImage(context.Screenshot);
            return Task.FromResult(new ActionNodeResult { IsSuccess = true, Output = "已复制到剪贴板" });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ActionNodeResult { IsSuccess = false, ErrorMessage = ex.Message });
        }
    }
}
