using ToolBox.Core.Providers;

namespace ToolBox.Core.Llm;

public class ContextCompressor
{
    private readonly double maxInputLength;
    private readonly double snipThreshold = 0.6;
    private readonly double aggressiveSnipThreshold = 0.8;
    private readonly double summaryThreshold = 0.95;
    private readonly int minRecentKeep = 2;
    private readonly int toolResultCap = 3000;
    private readonly Func<string, int> tokenEstimator;

    public ContextCompressor(int maxInputLength = 131072, Func<string, int>? tokenEstimator = null)
    {
        this.maxInputLength = maxInputLength;
        this.tokenEstimator = tokenEstimator ?? DefaultTokenEstimate;
    }

    public async Task CheckAndCompressAsync(List<ChatMessage> messages)
    {
        int totalTokens = messages.Sum(m => tokenEstimator(m.Content ?? ""));
        double ratio = (double)totalTokens / maxInputLength;

        if (ratio >= summaryThreshold)
        {
            await ApplySummaryCompression(messages);
        }
        else if (ratio >= aggressiveSnipThreshold)
        {
            ApplyAggressiveSnip(messages);
        }
        else if (ratio >= snipThreshold)
        {
            ApplySnip(messages);
        }
    }

    private void ApplySnip(List<ChatMessage> messages)
    {
        for (int i = 0; i < messages.Count - minRecentKeep; i++)
        {
            var msg = messages[i];
            if (msg.Role != "tool" || string.IsNullOrEmpty(msg.Content)) continue;

            var lines = msg.Content.Split("\n");
            if (lines.Length > 80)
            {
                var head = string.Join("\n", lines.Take(40));
                var tail = string.Join("\n", lines.TakeLast(12));
                msg.Content = head + "\n...[已截断]\n" + tail;
            }
            else if (msg.Content.Length > 10000)
            {
                msg.Content = msg.Content[..5000] + "\n...[已截断]\n" + msg.Content[^2000..];
            }
        }
    }

    private void ApplyAggressiveSnip(List<ChatMessage> messages)
    {
        ApplySnip(messages);
        for (int i = 0; i < messages.Count - minRecentKeep; i++)
        {
            var msg = messages[i];
            if (string.IsNullOrEmpty(msg.Content) || msg.Role == "system") continue;

            var tc = tokenEstimator(msg.Content);
            if (tc > 500)
            {
                int keepHead = Math.Min(500, msg.Content.Length / 4);
                int keepTail = Math.Min(200, msg.Content.Length / 8);
                msg.Content = msg.Content[..keepHead] + "\n...[已截断]\n" + msg.Content[^keepTail..];
            }
        }
    }

    private async Task ApplySummaryCompression(List<ChatMessage> messages)
    {
        int systemIdx = messages.FindIndex(m => m.Role == "system");
        int keepFrom = Math.Max(systemIdx + 1, messages.Count - minRecentKeep - 2);

        var toSummarize = messages.Skip(systemIdx + 1).Take(keepFrom - systemIdx - 1).ToList();
        if (toSummarize.Count == 0) return;

        var summaryText = string.Join("\n", toSummarize.Select(m => $"[{m.Role}]: {m.Content?[..Math.Min(200, m.Content?.Length ?? 0)]}"));
        var summaryMsg = new ChatMessage
        {
            Role = "system",
            Content = "[上下文摘要]\n" + summaryText
        };

        messages.RemoveRange(systemIdx + 1, keepFrom - systemIdx - 1);
        messages.Insert(systemIdx + 1, summaryMsg);
    }

    private static int DefaultTokenEstimate(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return (int)(text.Length * 0.25);
    }
}
