using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using ToolBox.Core.Llm;
using ToolBox.Core.Tools;

namespace ToolBox.Core.Providers;

public class OllamaProvider : IProvider, ILlmProvider
{
    private readonly string baseUrl;
    private readonly ModelInfo model;
    private readonly HttpClient httpClient;

    public string Name => "Ollama (Local)";
    public bool IsLocal => true;
    public bool RequireApiKey => false;
    public bool SupportModelDiscovery => true;
    public IReadOnlyList<ModelInfo> Models => [model];
    public IReadOnlyList<ModelInfo> ExtraModels => [];

    public OllamaProvider(string baseUrl, ModelInfo model)
    {
        this.baseUrl = baseUrl.TrimEnd('/');
        this.model = model;
        httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    }

    public async Task<bool> CheckConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await httpClient.GetAsync($"{baseUrl}/api/tags", ct);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<IReadOnlyList<ModelInfo>> FetchModelsAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await httpClient.GetFromJsonAsync<OllamaTagsResponse>($"{baseUrl}/api/tags", ct);
            return response?.Models?.Select(m => new ModelInfo
            {
                ModelId = m.Name,
                DisplayName = m.Name,
                ProviderName = Name,
                IsFree = true,
                SupportsMultimodal = false
            }).ToList() ?? [];
        }
        catch { return []; }
    }

    public ILlmProvider CreateProvider(ModelInfo model)
    {
        return new OllamaProvider(baseUrl, model);
    }

    public async IAsyncEnumerable<ChatChunk> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolInfo> tools,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var request = new OllamaChatRequest
        {
            Model = model.ModelId,
            Messages = messages.Select(m => new OllamaMessage { Role = m.Role, Content = m.Content }).ToList(),
            Stream = true
        };

        var json = JsonSerializer.Serialize(request);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await httpClient.PostAsync($"{baseUrl}/api/chat", content, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrEmpty(line)) continue;

            OllamaStreamChunk? chunk = null;
            try
            {
                chunk = JsonSerializer.Deserialize<OllamaStreamChunk>(line);
            }
            catch { continue; }

            if (chunk?.Message?.Content != null)
            {
                yield return new ChatChunk { Text = chunk.Message.Content };
            }
            if (chunk?.Done == true) break;
        }
    }
}

internal class OllamaTagsResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("models")]
    public List<OllamaModelInfo>? Models { get; set; }
}

internal class OllamaModelInfo
{
    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

internal class OllamaChatRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("model")]
    public string Model { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("messages")]
    public List<OllamaMessage> Messages { get; set; } = [];
    [System.Text.Json.Serialization.JsonPropertyName("stream")]
    public bool Stream { get; set; }
}

internal class OllamaMessage
{
    [System.Text.Json.Serialization.JsonPropertyName("role")]
    public string Role { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("content")]
    public string Content { get; set; } = "";
}

internal class OllamaStreamChunk
{
    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public OllamaMessage? Message { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("done")]
    public bool Done { get; set; }
}
