using ToolBox.Core.ActionChain.Nodes;

namespace ToolBox.Core.ActionChain;

/// <summary>节点工厂 - 根据类型名创建节点实例</summary>
public static class NodeFactory
{
    private static readonly Dictionary<string, Func<IActionNode>> _registry = new()
    {
        ["CopyToClipboard"] = () => new CopyToClipboardNode(),
        ["CopyTextToClipboard"] = () => new CopyTextToClipboardNode(),
        ["OcrExtract"] = () => new OcrExtractNode(),
        ["SaveToFile"] = () => new SaveToFileNode(),
        ["TranslateText"] = () => new TranslateTextNode(),
        ["SendToAi"] = () => new SendToAiNode(),
    };

    public static IActionNode? CreateNode(string nodeType)
    {
        return _registry.TryGetValue(nodeType, out var factory) ? factory() : null;
    }

    public static IReadOnlyDictionary<string, string> GetAvailableNodeTypes() => new Dictionary<string, string>
    {
        ["CopyToClipboard"] = "📋 复制到剪贴板",
        ["CopyTextToClipboard"] = "📋 复制文本到剪贴板",
        ["OcrExtract"] = "🔤 OCR 提取文字",
        ["SaveToFile"] = "💾 保存到文件",
        ["TranslateText"] = "🌐 翻译文本",
        ["SendToAi"] = "🤖 发送到 AI",
    };
}
