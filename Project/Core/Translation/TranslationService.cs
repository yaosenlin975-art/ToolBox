using System.Net.Http;
using System.Text.Json;
using System.Web;

namespace ToolBox.Core.Translation;

/// <summary>
/// 非 LLM 翻译服务：调用免费公开在线翻译接口实现文本翻译。
/// 供行动链 TranslateTextNode、OCR 翻译等场景调用，不依赖 LLM Provider。
/// </summary>
public static class TranslationService
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(20) };

    // Google 免费公开翻译端点（无需 API Key）
    private const string Endpoint = "https://translate.googleapis.com/translate_a/single";

    /// <summary>
    /// 翻译文本。
    /// </summary>
    /// <param name="text">待翻译文本</param>
    /// <param name="targetLanguage">目标语言代码（默认英语 en）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>翻译结果</returns>
    /// <exception cref="ArgumentException">翻译文本为空时抛出</exception>
    /// <exception cref="HttpRequestException">网络或接口调用失败时抛出</exception>
    public static async Task<string> TranslateAsync(
        string text,
        string targetLanguage = "en",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("翻译文本不能为空", nameof(text));

        var url = Endpoint
            + "?client=gtx&dt=t"
            + "&sl=auto&tl=" + Uri.EscapeDataString(targetLanguage)
            + "&q=" + Uri.EscapeDataString(text);

        using var response = await HttpClient.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        // 响应格式: [[["译文","原文",...],...],null,...]
        var sb = new System.Text.StringBuilder();
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var segment in doc.RootElement.EnumerateArray())
            {
                if (segment.ValueKind != JsonValueKind.Array || segment.GetArrayLength() == 0) continue;
                var first = segment[0];
                if (first.ValueKind != JsonValueKind.Array || first.GetArrayLength() == 0) continue;
                var translated = first[0].GetString();
                if (!string.IsNullOrEmpty(translated))
                    sb.Append(translated);
            }
        }

        var result = sb.ToString().Trim();
        if (string.IsNullOrEmpty(result))
            throw new HttpRequestException("翻译接口返回空结果");

        return result;
    }
}