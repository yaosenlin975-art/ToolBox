using System.Text;
using ToolBox.Core.Providers;
using ToolBox.Core.Tools;

namespace ToolBox.Core.Llm;

/// <summary>
/// LLM 翻译服务：通过现有 Provider 基础设施实现文本翻译。
/// 供 ActionChain TranslateTextNode 等需要直接 LLM 翻译能力的场景调用。
/// </summary>
public static class LlmTranslationService
{
    private const int MaxRetries = 2;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 使用当前激活的 LLM Provider 翻译文本。
    /// </summary>
    /// <param name="text">待翻译文本</param>
    /// <param name="targetLanguage">目标语言（默认：English）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>翻译结果文本</returns>
    /// <exception cref="InvalidOperationException">未配置 LLM Provider 时抛出</exception>
    /// <exception cref="TimeoutException">LLM 请求超时时抛出</exception>
    /// <exception cref="HttpRequestException">LLM 请求失败时抛出</exception>
    public static async Task<string> TranslateAsync(
        string text,
        string targetLanguage = "English",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("翻译文本不能为空", nameof(text));

        var provider = ProviderManager.Instance.CreateActiveProvider();
        if (provider == null)
            throw new InvalidOperationException("未配置 LLM Provider，请先在设置中配置 AI 服务");

        var messages = new List<ChatMessage>
        {
            new()
            {
                Role = "system",
                Content = $"你是一个专业的翻译助手。请将用户提供的文本翻译为{targetLanguage}。只输出翻译结果，不要添加解释或额外内容。保持原文的格式和语气。"
            },
            new()
            {
                Role = "user",
                Content = text
            }
        };

        Exception? lastException = null;

        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                var result = new StringBuilder();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(Timeout);

                await foreach (var chunk in provider.ChatAsync(messages, tools: null, cts.Token))
                {
                    if (chunk.Error != null)
                        throw new InvalidOperationException(chunk.Error);

                    if (chunk.Text != null)
                        result.Append(chunk.Text);

                    if (chunk.IsDone)
                        break;
                }

                var translation = result.ToString().Trim();
                if (string.IsNullOrEmpty(translation))
                    throw new InvalidOperationException("LLM 返回空翻译结果");

                return translation;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                if (attempt < MaxRetries)
                    await Task.Delay(1000 * (attempt + 1), ct);
            }
        }

        throw lastException ?? new InvalidOperationException("翻译失败：未知错误");
    }
}