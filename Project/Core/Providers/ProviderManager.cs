using System.IO;
using System.Text.Json;

namespace ToolBox.Core.Providers;

public class ProviderManager
{
    private static ProviderManager? instance;
    public static ProviderManager Instance => instance ??= new ProviderManager();

    private readonly List<ProviderConfig> builtinConfigs = [];
    private readonly List<ProviderConfig> customConfigs = [];

    private readonly string configDir;
    private readonly string providersConfigPath;
    private readonly string activeModelPath;

    public ModelSlotConfig? ActiveModel { get; private set; }

    public IReadOnlyList<ProviderConfig> BuiltinConfigs => builtinConfigs;
    public IReadOnlyList<ProviderConfig> CustomConfigs => customConfigs;

    private ProviderManager()
    {
        configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ToolBox");
        Directory.CreateDirectory(configDir);
        providersConfigPath = Path.Combine(configDir, "providers.json");
        activeModelPath = Path.Combine(configDir, "active_model.json");

        RegisterBuiltinDefaults();
        LoadConfig();
    }

    private void RegisterBuiltinDefaults()
    {
        builtinConfigs.Add(new ProviderConfig
        {
            Name = "OpenAI Compatible",
            Type = "openai",
            ApiKey = "",
            BaseUrl = "https://api.openai.com/v1",
            IsBuiltin = true,
            Models = [new ModelInfo { ModelId = "gpt-4o", DisplayName = "GPT-4o", ProviderName = "OpenAI Compatible", MaxContextLength = 128000, MaxOutputTokens = 16384 }]
        });
        builtinConfigs.Add(new ProviderConfig
        {
            Name = "Ollama (Local)",
            Type = "ollama",
            ApiKey = "",
            BaseUrl = "http://localhost:11434",
            IsBuiltin = true,
            Models = []
        });
        builtinConfigs.Add(new ProviderConfig
        {
            Name = "Anthropic",
            Type = "anthropic",
            ApiKey = "",
            BaseUrl = "https://api.anthropic.com",
            IsBuiltin = true,
            Models = [
                new ModelInfo { ModelId = "claude-sonnet-4-20250514", DisplayName = "Claude Sonnet 4", ProviderName = "Anthropic", SupportsMultimodal = true, MaxContextLength = 200000, MaxOutputTokens = 8192 },
                new ModelInfo { ModelId = "claude-haiku-4-20250414", DisplayName = "Claude Haiku 4", ProviderName = "Anthropic", SupportsMultimodal = true, MaxContextLength = 200000, MaxOutputTokens = 8192 },
            ]
        });
    }

    public void UpdateBuiltinConfig(string name, string apiKey, string baseUrl)
    {
        var config = builtinConfigs.FirstOrDefault(c => c.Name == name);
        if (config == null) return;
        config.ApiKey = apiKey;
        config.BaseUrl = baseUrl;
        SaveConfig();
    }

    public ProviderConfig AddCustomProvider(string name, string type, string apiKey, string baseUrl)
    {
        var config = new ProviderConfig
        {
            Name = name,
            Type = type,
            ApiKey = apiKey,
            BaseUrl = baseUrl,
            IsBuiltin = false,
            Models = []
        };
        customConfigs.Add(config);
        SaveConfig();
        return config;
    }

    public void RemoveCustomProvider(string name)
    {
        customConfigs.RemoveAll(c => c.Name == name);
        SaveConfig();
    }

    public void UpdateCustomProvider(string name, string apiKey, string baseUrl)
    {
        var config = customConfigs.FirstOrDefault(c => c.Name == name);
        if (config == null) return;
        config.ApiKey = apiKey;
        config.BaseUrl = baseUrl;
        SaveConfig();
    }

    public void SetActiveModel(string providerName, string modelId)
    {
        ActiveModel = new ModelSlotConfig { ProviderName = providerName, ModelId = modelId };
        SaveActiveModel();
    }

    public ProviderConfig? GetConfig(string name)
    {
        return builtinConfigs.FirstOrDefault(c => c.Name == name)
            ?? customConfigs.FirstOrDefault(c => c.Name == name);
    }

    public IProvider? GetProvider(string name)
    {
        var config = GetConfig(name);
        return config == null ? null : CreateProviderFromConfig(config);
    }

    public ILlmProvider? CreateActiveProvider()
    {
        if (ActiveModel == null) return null;
        var config = GetConfig(ActiveModel.ProviderName);
        if (config == null) return null;
        var model = config.Models.FirstOrDefault(m => m.ModelId == ActiveModel.ModelId);
        if (model == null) return null;
        var provider = CreateProviderFromConfig(config);
        return provider?.CreateProvider(model);
    }

    private static IProvider? CreateProviderFromConfig(ProviderConfig config)
    {
        var model = config.Models.FirstOrDefault()
            ?? new ModelInfo { ModelId = "default", DisplayName = config.Name, ProviderName = config.Name };

        return config.Type switch
        {
            "openai" => new OpenAiProvider(config.ApiKey, config.BaseUrl, model),
            "ollama" => new OllamaProvider(config.BaseUrl, model),
            "anthropic" => new AnthropicProvider(config.ApiKey, config.BaseUrl, model),
            _ => null
        };
    }

    public async Task<List<ModelInfo>> DiscoverModelsAsync(string providerName)
    {
        var config = GetConfig(providerName);
        if (config == null) return [];
        var provider = CreateProviderFromConfig(config);
        if (provider == null) return [];
        try
        {
            var models = await provider.FetchModelsAsync();
            config.Models = models.ToList();
            SaveConfig();
            return models.ToList();
        }
        catch { return []; }
    }

    private void LoadConfig()
    {
        if (File.Exists(activeModelPath))
        {
            try
            {
                var json = File.ReadAllText(activeModelPath);
                ActiveModel = JsonSerializer.Deserialize<ModelSlotConfig>(json);
            }
            catch { }
        }

        if (!File.Exists(providersConfigPath)) return;
        try
        {
            var json = File.ReadAllText(providersConfigPath);
            var loaded = JsonSerializer.Deserialize<List<ProviderConfig>>(json);
            if (loaded != null)
            {
                foreach (var config in loaded)
                {
                    if (config.IsBuiltin)
                    {
                        var existing = builtinConfigs.FirstOrDefault(c => c.Name == config.Name);
                        if (existing != null)
                        {
                            existing.ApiKey = config.ApiKey;
                            existing.BaseUrl = config.BaseUrl;
                            if (config.Models.Count > 0) existing.Models = config.Models;
                        }
                    }
                    else
                    {
                        customConfigs.Add(config);
                    }
                }
            }
        }
        catch { }
    }

    private void SaveConfig()
    {
        try
        {
            var all = builtinConfigs.Concat(customConfigs).ToList();
            var json = JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(providersConfigPath, json);
        }
        catch { }
    }

    public void SaveActiveModel()
    {
        if (ActiveModel == null) return;
        try
        {
            var json = JsonSerializer.Serialize(ActiveModel, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(activeModelPath, json);
        }
        catch { }
    }
}

public class ModelSlotConfig
{
    public string ProviderName { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
}

public class ProviderConfig
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "openai";
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public bool IsBuiltin { get; set; }
    public List<ModelInfo> Models { get; set; } = [];
}
