namespace ToolBox.Core.Llm;

public sealed class ContextCompressor
{
    private readonly int maxInputLength;

    public ContextCompressor(int maxInputLength = 131072)
    {
        this.maxInputLength = Math.Max(1, maxInputLength);
    }

    public Task CheckAndCompressAsync(List<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int totalLength = messages.Sum(message => message.Content?.Length ?? 0);
        if (totalLength < maxInputLength * 0.6) return Task.CompletedTask;

        for (int i = 0; i < messages.Count - 2; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var message = messages[i];
            if (message.Role == "system" || string.IsNullOrEmpty(message.Content) || message.Content.Length <= 3000)
                continue;
            message.Content = message.Content[..2000] + "\n...[已截断]...\n" + message.Content[^800..];
        }

        return Task.CompletedTask;
    }
}
