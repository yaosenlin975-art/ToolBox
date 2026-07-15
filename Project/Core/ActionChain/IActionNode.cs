using System.Windows.Media.Imaging;

namespace ToolBox.Core.ActionChain;

/// <summary>动作节点执行结果</summary>
public class ActionNodeResult
{
    public bool IsSuccess { get; set; }
    public string? ErrorMessage { get; set; }
    public object? Output { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>动作节点执行上下文</summary>
public class ActionNodeContext
{
    public BitmapSource? Screenshot { get; set; }
    public string? FilePath { get; set; }
    public object? PreviousOutput { get; set; }
    public CancellationToken CancellationToken { get; set; }
    public IProgress<string>? Progress { get; set; }
}

/// <summary>动作节点接口</summary>
public interface IActionNode
{
    string NodeName { get; }
    string NodeType { get; }
    string NodeIcon { get; }
    Task<ActionNodeResult> ExecuteAsync(ActionNodeContext context);
}
