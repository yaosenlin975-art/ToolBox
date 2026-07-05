using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using ToolBox.Core.Llm;
using ToolBox.Core.Tools;

namespace ToolBox.Core.Providers;

public class OpenAiProvider : IProvider, ILlmProvider
{
    private readonly string apiKey;
    private readonly string baseUrl;
    private readonly ModelInfo model;
    private readonly HttpClient httpClient;

    public string Name => "OpenAI Compatible";
    public bool IsLocal => false;
    public bool RequireApiKey => true;
    public bool SupportModelDiscovery => true;
    public IReadOnlyList<ModelInfo> Models => [model];
    public IReadOnlyList<ModelInfo> ExtraModels => [];

    public OpenAiProvider(string apiKey, string baseUrl, ModelInfo model)
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
            httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            var response = await httpClient.GetAsync($"{baseUrl}/models", ct);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<IReadOnlyList<ModelInfo>> FetchModelsAsync(CancellationToken ct = default)
    {
        try
        {
            httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            var response = await httpClient.GetFromJsonAsync<JsonElement>($"{baseUrl}/models", ct);
            var data = response.GetProperty("data");
            var models = new List<ModelInfo>();
            foreach (var item in data.EnumerateArray())
            {
                var id = item.GetProperty("id").GetString() ?? "";
                models.Add(new ModelInfo
                {
                    ModelId = id, DisplayName = id, ProviderName = Name,
                    IsFree = id.Contains("free", StringComparison.OrdinalIgnoreCase),
                    MaxContextLength = 131072, MaxOutputTokens = 8192
                });
            }
            return models;
        }
        catch { return []; }
    }

    public ILlmProvider CreateProvider(ModelInfo model) =>
        new OpenAiProvider(apiKey, baseUrl, model);

    public async IAsyncEnumerable<ChatChunk> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolInfo>? tools = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        var requestBody = BuildRequestBody(messages, tools);
        var content = new StringContent(
            JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions")
        { Content = content };

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrEmpty(line) || !line.StartsWith("data: ")) continue;

            var data = line[6..];
            if (data == "[DONE]")
            {
                yield return new ChatChunk { IsDone = true };
                yield break;
            }

            var doc = ParseJson(data); if (doc == null) continue;
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;

            var choice = choices[0];
            if (!choice.TryGetProperty("delta", out var delta)) continue;

            if (delta.TryGetProperty("content", out var contentProp) &&
                contentProp.ValueKind == JsonValueKind.String)
            {
                yield return new ChatChunk { Text = contentProp.GetString() };
            }

            if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.GetArrayLength() > 0)
            {
                var tc = toolCalls[0];
                var function = tc.GetProperty("function");
                yield return new ChatChunk
                {
                    ToolCall = new ToolCallInfo
                    {
                        Id = tc.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "",
                        Name = function.GetProperty("name").GetString() ?? "",
                        Arguments = function.TryGetProperty("arguments", out var argsProp)
                            ? argsProp.GetString() ?? "" : ""
                    }
                };
            }
        }
    }


    private static JsonDocument? ParseJson(string data)
    {
        try { return JsonDocument.Parse(data); }
        catch { return null; }
    }
    private object BuildRequestBody(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolInfo>? tools)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = model.ModelId,
            ["messages"] = messages.Select(BuildMessage).ToList(),
            ["max_tokens"] = model.MaxOutputTokens
        };
        if (tools != null && tools.Count > 0)
        {
            body["tools"] = tools.Select(t => new
            {
                type = "function",
                function = new
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = JsonDocument.Parse(t.ToJsonSchema()).RootElement
                }
            }).ToList();
        }
        return body;
    }

    private object BuildMessage(ChatMessage msg)
    {
        if (msg.Role == "tool")
            return new { role = "tool", content = msg.Content ?? "", tool_call_id = msg.ToolCallId ?? "" };

        if (msg.ToolCalls != null && msg.ToolCalls.Count > 0)
            return new
            {
                role = "assistant", content = msg.Content ?? "",
                tool_calls = msg.ToolCalls.Select(tc => new
                {
                    id = tc.Id, type = "function",
                    function = new { name = tc.Name, arguments = tc.Arguments }
                }).ToList()
            };

        if (!string.IsNullOrEmpty(msg.ImagePath) && File.Exists(msg.ImagePath))
        {
            var base64 = Convert.ToBase64String(File.ReadAllBytes(msg.ImagePath));
            return new
            {
                role = msg.Role,
                content = new object[]
                {
                    new { type = "text", text = msg.Content ?? "" },
                    new { type = "image_url", image_url = new { url = $"data:image/png;base64,{base64}" } }
                }
            };
        }

        return new { role = msg.Role, content = msg.Content ?? "" };
    }
}