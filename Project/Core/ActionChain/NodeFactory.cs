using ToolBox.Core.ActionChain.Nodes;

namespace ToolBox.Core.ActionChain;

/// <summary>节点工厂 - 根据类型名创建节点实例</summary>
public static class NodeFactory
{
    private static readonly Dictionary<string, Func<IActionNode>> _registry = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CopyToClipboard"] = () => new CopyToClipboardNode(),
        ["CopyTextToClipboard"] = () => new CopyTextToClipboardNode(),
        ["OcrExtract"] = () => new OcrExtractNode(),
        ["SaveToFile"] = () => new SaveToFileNode(),
        ["TranslateText"] = () => new TranslateTextNode(),
    };

    // 建链对话框里用户输入的是短名，内置链与已存档的链用注册表键名，两套写法在这里归一，
    // 否则自建链每条节点都会在执行时报「未知节点类型」而界面上看不出任何异常。
    private static readonly Dictionary<string, string> aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ocr"] = "OcrExtract",
        ["translate"] = "TranslateText",
        ["copy"] = "CopyToClipboard",
        ["save"] = "SaveToFile",
    };

    private static string? Resolve(string nodeType)
    {
        if (string.IsNullOrWhiteSpace(nodeType)) return null;
        var trimmed = nodeType.Trim();
        if (_registry.ContainsKey(trimmed)) return trimmed;
        return aliases.TryGetValue(trimmed, out var target) ? target : null;
    }

    public static IActionNode? CreateNode(string nodeType) =>
        Resolve(nodeType) is { } key ? _registry[key]() : null;

    public static bool IsKnown(string nodeType) => Resolve(nodeType) != null;

    public static string GetLabel(string nodeType) =>
        Resolve(nodeType) is { } key && NodeLabels.TryGetValue(key, out var label) ? label : nodeType;

    public static IReadOnlyDictionary<string, string> GetAvailableNodeTypes() => NodeLabels;

    // 与 _registry 同样不区分大小写：Resolve 原样返回用户输入的写法（旧存档链里存在过全小写），
    // 用区分大小写的字典索引会抛 KeyNotFoundException
    private static readonly Dictionary<string, string> NodeLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CopyToClipboard"] = "📋 复制到剪贴板",
        ["CopyTextToClipboard"] = "📋 复制文本到剪贴板",
        ["OcrExtract"] = "🔤 OCR 提取文字",
        ["SaveToFile"] = "💾 保存到文件",
        ["TranslateText"] = "🌐 翻译文本",
    };
}
