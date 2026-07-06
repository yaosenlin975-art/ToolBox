using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using ToolBox.Core.Llm;
using ToolBox.Core.Tools;

namespace ToolBox.Core.Providers;

public class AnthropicProvider : IProvider, ILlmProvider
{
    private readonly string apiKey;
    private readonly string baseUrl;
    private readonly ModelInfo model;
    private readonly HttpClient httpClient;

    public string Name => "Anthropic";
    public bool IsLocal => false;
    public bool RequireApiKey => true;
    public bool SupportModelDiscovery => false;
    public IReadOnlyList<ModelInfo> Models => [model];
    public IReadOnlyList<ModelInfo> ExtraModels => [];

    private static readonly ModelInfo[] builtinModels =
    [
        new() { ModelId = "claude-sonnet-4-20250514", DisplayName = "Claude Sonnet 4", ProviderName = "Anthropic", SupportsMultimodal = true, MaxContextLength = 200000, MaxOutputTokens = 8192 },
        new() { ModelId = "claude-haiku-4-20250414", DisplayName = "Claude Haiku 4", ProviderName = "Anthropic", SupportsMultimodal = true, MaxContextLength = 200000, MaxOutputTokens = 8192 },
    ];

    public AnthropicProvider(string apiKey, string baseUrl, ModelInfo model)
    {
        this.apiKey = apiKey;
        this.baseUrl = baseUrl.TrimEnd('/');
        this.model = model;
        httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    public async Task<bool> CheckConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            httpClient.DefaultRequestHeaders.Remove("x-api-key");
            httpClient.DefaultRequestHeaders.Add("x-api-key", apiKey);
            httpClient.DefaultRequestHeaders.Remove("anthropic-version");
            httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
            var response = await httpClient.GetAsync($"{baseUrl}/v1/models", ct);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public Task<IReadOnlyList<ModelInfo>> FetchModelsAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ModelInfo>>(builtinModels);
    }

    public ILlmProvider CreateProvider(ModelInfo model)
    {
        return new AnthropicProvider(apiKey, baseUrl, model);
    }

    public async IAsyncEnumerable<ChatChunk> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolInfo> tools,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var systemMsg = messages.FirstOrDefault(m => m.Role == "system");
        var nonSystem = messages.Where(m => m.Role != "system").ToList();

        var request = new AnthropicRequest
        {
            Model = model.ModelId,
            MaxTokens = model.MaxOutputTokens > 0 ? model.MaxOutputTokens : 8192,
            System = systemMsg?.Content,
            Messages = nonSystem.Select(m => new AnthropicMessage
            {
                Role = m.Role == "tool" ? "user" : m.Role,
                Content = m.Content ?? ""
            }).ToList()
        };

        var json = JsonSerializer.Serialize(request, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        httpClient.DefaultRequestHeaders.Remove("x-api-key");
        httpClient.DefaultRequestHeaders.Add("x-api-key", apiKey);
        httpClient.DefaultRequestHeaders.Remove("anthropic-version");
        httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

        using var response = await httpClient.PostAsync($"{baseUrl}/v1/messages", content, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrEmpty(line) || !line.StartsWith("data: ")) continue;

            var data = line[6..];
            if (data == "[DONE]") break;

            AnthropicStreamChunk? chunk = null;
            try
            {
                chunk = JsonSerializer.Deserialize<AnthropicStreamChunk>(data);
            }
            catch { continue; }

            if (chunk?.Type == "content_block_delta" && chunk.Delta?.Text != null)
            {
                yield return new ChatChunk { Text = chunk.Delta.Text };
            }
        }
    }
}

internal class AnthropicRequest
{
    public string Model { get; set; } = "";
    public int MaxTokens { get; set; } = 8192;
    public bool Stream { get; set; } = true;
    public string? System { get; set; }
    public List<AnthropicMessage> Messages { get; set; } = [];
}

internal class AnthropicMessage
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
}

internal class AnthropicStreamChunk
{
    [System.Text.Json.Serialization.JsonPropertyName("type")]
    public string? Type { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("delta")]
    public AnthropicDelta? Delta { get; set; }
}

internal class AnthropicDelta
{
    [System.Text.Json.Serialization.JsonPropertyName("text")]
    public string? Text { get; set; }
}
