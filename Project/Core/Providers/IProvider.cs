namespace ToolBox.Core.Providers;

public interface IProvider
{
    string Name { get; }
    bool IsLocal { get; }
    bool RequireApiKey { get; }
    bool SupportModelDiscovery { get; }
    IReadOnlyList<ModelInfo> Models { get; }
    IReadOnlyList<ModelInfo> ExtraModels { get; }

    Task<bool> CheckConnectionAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ModelInfo>> FetchModelsAsync(CancellationToken ct = default);
    ILlmProvider CreateProvider(ModelInfo model);
}