using System.Text.Json;
using ToolBox.Core.ActionChain;

namespace ToolBox.Core.Tools;

public static class ActionChainTools
{
    [Tool("list_action_chains", "列出所有可用的截图动作链")]
    public static string ListActionChains()
    {
        var store = ActionChainStore.Instance;
        var chains = store.Chains.Select(c => new
        {
            id = c.Id, name = c.Name, description = c.Description,
            isDefault = c.Id == store.DefaultChainId,
            nodeCount = c.Nodes.Count,
            nodes = c.Nodes.Select(n => n.NodeType).ToArray()
        }).ToList();
        return JsonSerializer.Serialize(new { success = true, chains, defaultId = store.DefaultChainId });
    }

    [Tool("create_action_chain", "创建新的截图动作链")]
    public static string CreateActionChain(
        [ToolParam("动作链名称")] string name,
        [ToolParam("节点类型列表(逗号分隔)")] string nodes,
        [ToolParam("描述")] string description = "")
    {
        try
        {
            var nodeTypes = nodes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var availableTypes = NodeFactory.GetAvailableNodeTypes();
            var invalidTypes = nodeTypes.Where(t => !availableTypes.ContainsKey(t)).ToArray();
            if (invalidTypes.Length > 0)
                return JsonSerializer.Serialize(new { success = false, error = $"未知节点类型: {string.Join(", ", invalidTypes)}" });
            if (nodeTypes.Length > 20)
                return JsonSerializer.Serialize(new { success = false, error = "节点数量不能超过 20 个" });
            var chain = new ActionChainDefinition
            {
                Name = name, Description = description,
                Nodes = nodeTypes.Select(t => new ActionNodeConfig { NodeType = t }).ToList()
            };
            ActionChainStore.Instance.Add(chain);
            return JsonSerializer.Serialize(new { success = true, id = chain.Id, name = chain.Name });
        }
        catch (Exception ex) { return JsonSerializer.Serialize(new { success = false, error = ex.Message }); }
    }
}
