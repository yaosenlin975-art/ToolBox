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

        // 构建请求体
        var requestBody = new Dictionary<string, object>
        {
            ["model"] = model.ModelId,
            ["max_tokens"] = model.MaxOutputTokens > 0 ? model.MaxOutputTokens : 8192,
            ["stream"] = true,
            ["messages"] = nonSystem.Select(BuildAnthropicMessage).ToList()
        };
        if (systemMsg?.Content != null)
            requestBody["system"] = systemMsg.Content;

        // 发送 tools 参数（Anthropic 格式）
        if (tools != null && tools.Count > 0)
        {
            requestBody["tools"] = tools.Select(t =>
            {
                var schema = JsonSerializer.Deserialize<JsonElement>(t.ToJsonSchema());
                return new Dictionary<string, object>
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["input_schema"] = schema
                };
            }).ToList();
        }

        var json = JsonSerializer.Serialize(requestBody,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        var httpContent = new StringContent(json, Encoding.UTF8, "application/json");

        httpClient.DefaultRequestHeaders.Remove("x-api-key");
        httpClient.DefaultRequestHeaders.Add("x-api-key", apiKey);
        httpClient.DefaultRequestHeaders.Remove("anthropic-version");
        httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

        using var response = await httpClient.PostAsync($"{baseUrl}/v1/messages", httpContent, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        // 跟踪 tool_use 状态
        string? pendingToolId = null;
        string? pendingToolName = null;
        var pendingToolArgs = new StringBuilder();
        bool isInToolUse = false;

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrEmpty(line) || !line.StartsWith("data: ")) continue;

            var data = line[6..];
            if (data == "[DONE]") break;

            JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(data); }
            catch { continue; }

            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp)) continue;
            var eventType = typeProp.GetString();

            switch (eventType)
            {
                case "content_block_start":
                    if (root.TryGetProperty("content_block", out var block) &&
                        block.TryGetProperty("type", out var blockType) &&
                        blockType.GetString() == "tool_use")
                    {
                        isInToolUse = true;
                        pendingToolId = block.TryGetProperty("id", out var idProp) ? idProp.GetString() : "";
                        pendingToolName = block.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : "";
                        pendingToolArgs.Clear();
                    }
                    break;

                case "content_block_delta":
                    if (root.TryGetProperty("delta", out var delta) &&
                        delta.TryGetProperty("type", out var deltaType))
                    {
                        var dt = deltaType.GetString();
                        if (dt == "text_delta" && delta.TryGetProperty("text", out var textProp))
                        {
                            var text = textProp.GetString();
                            if (text != null)
                                yield return new ChatChunk { Text = text };
                        }
                        else if (dt == "input_json_delta" && isInToolUse &&
                                 delta.TryGetProperty("partial_json", out var jsonProp))
                        {
                            var partial = jsonProp.GetString();
                            if (partial != null)
                                pendingToolArgs.Append(partial);
                        }
                    }
                    break;

                case "content_block_stop":
                    if (isInToolUse && pendingToolId != null)
                    {
                        yield return new ChatChunk
                        {
                            ToolCall = new ToolCallInfo
                            {
                                Id = pendingToolId,
                                Name = pendingToolName ?? "",
                                Arguments = pendingToolArgs.ToString()
                            }
                        };
                        isInToolUse = false;
                        pendingToolId = null;
                        pendingToolName = null;
                    }
                    break;

                case "message_stop":
                    yield return new ChatChunk { IsDone = true };
                    yield break;
            }
        }

        // 流结束时如果没有收到 message_stop，手动发送结束信号
        yield return new ChatChunk { IsDone = true };
    }

    /// <summary>
    /// 将 ChatMessage 转换为 Anthropic Messages API 格式。
    /// 处理 tool_use（assistant 消息中的工具调用）和 tool_result（tool 角色的工具结果）。
    /// </summary>
    private static object BuildAnthropicMessage(ChatMessage msg)
    {
        // 工具结果消息 → Anthropic user 消息，content 为 tool_result 块
        if (msg.Role == "tool")
        {
            return new Dictionary<string, object>
            {
                ["role"] = "user",
                ["content"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = msg.ToolCallId ?? "",
                        ["content"] = msg.Content ?? ""
                    }
                }
            };
        }

        // assistant 消息带 ToolCalls → content 为 text + tool_use 块数组
        if (msg.ToolCalls != null && msg.ToolCalls.Count > 0)
        {
            var blocks = new List<object>();
            if (!string.IsNullOrEmpty(msg.Content))
            {
                blocks.Add(new Dictionary<string, object>
                {
                    ["type"] = "text",
                    ["text"] = msg.Content
                });
            }
            foreach (var tc in msg.ToolCalls)
            {
                object input;
                try
                {
                    input = !string.IsNullOrEmpty(tc.Arguments)
                        ? JsonSerializer.Deserialize<JsonElement>(tc.Arguments)
                        : new Dictionary<string, object>();
                }
                catch { input = new Dictionary<string, object>(); }

                blocks.Add(new Dictionary<string, object>
                {
                    ["type"] = "tool_use",
                    ["id"] = tc.Id,
                    ["name"] = tc.Name,
                    ["input"] = input
                });
            }
            return new Dictionary<string, object>
            {
                ["role"] = "assistant",
                ["content"] = blocks
            };
        }

        // 普通文本消息（user / assistant 无工具调用）
        return new Dictionary<string, object>
        {
            ["role"] = msg.Role,
            ["content"] = msg.Content ?? ""
        };
    }
}
