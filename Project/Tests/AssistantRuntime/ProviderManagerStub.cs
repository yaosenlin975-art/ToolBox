using ToolBox.Core.Providers;

namespace ToolBox.Core.Providers;

public sealed class ProviderManager
{
    private static readonly ProviderManager singleton = new();

    public static ProviderManager Instance => singleton;

    public ILlmProvider? CreateActiveProvider() => null;
}
