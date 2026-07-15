namespace ToolBox.Core.ActionChain.Nodes;

public class SendToAiNode : IActionNode
{
    public string NodeName => "发送到 AI";
    public string NodeType => "SendToAi";
    public string NodeIcon => "🤖";

    public Task<ActionNodeResult> ExecuteAsync(ActionNodeContext context)
    {
        try
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (App.CompactToolbox is { } toolbox)
                {
                    toolbox.SwitchToTab("assistant");
                    toolbox.Activate();
                }
            });
            return Task.FromResult(new ActionNodeResult { IsSuccess = true, Output = "已发送到 AI 助手" });
        }
        catch (Exception ex) { return Task.FromResult(new ActionNodeResult { IsSuccess = false, ErrorMessage = ex.Message }); }
    }
}
