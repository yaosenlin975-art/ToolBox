namespace ToolBox.Core.ActionChain;

/// <summary>动作链定义</summary>
public class ActionChainDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public bool IsBuiltIn { get; set; }
    public bool StopOnError { get; set; } = true;
    public List<ActionNodeConfig> Nodes { get; set; } = new();
}

/// <summary>动作节点配置</summary>
public class ActionNodeConfig
{
    public string NodeType { get; set; } = string.Empty;
    public Dictionary<string, string> Parameters { get; set; } = new();
}
