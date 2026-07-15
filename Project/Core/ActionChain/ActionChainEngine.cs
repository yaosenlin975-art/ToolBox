using System.Windows.Media.Imaging;

namespace ToolBox.Core.ActionChain;

/// <summary>动作链执行引擎</summary>
public class ActionChainEngine
{
    private static ActionChainEngine? _instance;
    public static ActionChainEngine Instance => _instance ??= new ActionChainEngine();

    public event Action<ActionChainProgress>? ProgressChanged;
    public event Action<ActionChainResult>? ExecutionCompleted;

    private ActionChainEngine() { }

    public async Task<ActionChainResult> ExecuteAsync(
        ActionChainDefinition chain, BitmapSource screenshot,
        string? filePath = null, CancellationToken cancellationToken = default)
    {
        var result = new ActionChainResult { ChainName = chain.Name, TotalNodes = chain.Nodes.Count };
        object? previousOutput = null;

        for (int i = 0; i < chain.Nodes.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested) { result.IsCancelled = true; break; }

            var nodeConfig = chain.Nodes[i];
            var node = NodeFactory.CreateNode(nodeConfig.NodeType);
            if (node == null)
            {
                result.Errors.Add($"未知节点类型: {nodeConfig.NodeType}");
                if (chain.StopOnError) break;
                continue;
            }

            ProgressChanged?.Invoke(new ActionChainProgress
            {
                CurrentNode = i + 1, TotalNodes = chain.Nodes.Count,
                NodeName = node.NodeName, StatusText = $"执行中: {node.NodeName}..."
            });

            var context = new ActionNodeContext
            {
                Screenshot = screenshot, FilePath = filePath, PreviousOutput = previousOutput,
                CancellationToken = cancellationToken,
                Progress = new Progress<string>(msg =>
                {
                    ProgressChanged?.Invoke(new ActionChainProgress
                    {
                        CurrentNode = i + 1, TotalNodes = chain.Nodes.Count,
                        NodeName = node.NodeName, StatusText = msg
                    });
                })
            };

            try
            {
                var nodeResult = await node.ExecuteAsync(context);
                result.ExecutedNodes++;
                if (nodeResult.IsSuccess) { previousOutput = nodeResult.Output; result.SuccessNodes++; }
                else
                {
                    result.Errors.Add($"{node.NodeName}: {nodeResult.ErrorMessage ?? "执行失败"}");
                    if (chain.StopOnError) break;
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{node.NodeName}: {ex.Message}");
                if (chain.StopOnError) break;
            }
        }

        result.IsSuccess = result.Errors.Count == 0 && !result.IsCancelled;
        ExecutionCompleted?.Invoke(result);
        return result;
    }
}

public class ActionChainProgress
{
    public int CurrentNode { get; set; }
    public int TotalNodes { get; set; }
    public string NodeName { get; set; } = string.Empty;
    public string StatusText { get; set; } = string.Empty;
}

public class ActionChainResult
{
    public string ChainName { get; set; } = string.Empty;
    public bool IsSuccess { get; set; }
    public bool IsCancelled { get; set; }
    public int TotalNodes { get; set; }
    public int ExecutedNodes { get; set; }
    public int SuccessNodes { get; set; }
    public List<string> Errors { get; set; } = new();
}
