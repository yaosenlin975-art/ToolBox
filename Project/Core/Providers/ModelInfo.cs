namespace ToolBox.Core.Providers;

public class ModelInfo
{
    public string ModelId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public bool IsFree { get; set; }
    public bool SupportsMultimodal { get; set; }
    public int MaxContextLength { get; set; } = 131072;
    public int MaxOutputTokens { get; set; } = 8192;
}