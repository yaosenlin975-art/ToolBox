using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ToolBox.Core.Llm;

public class ChatManager
{
    private static ChatManager? instance;
    public static ChatManager Instance => instance ??= new ChatManager();

    private readonly List<ChatSession> sessions = [];
    private readonly string basePath;
    private readonly string chatsJsonPath;
    private readonly string sessionsDir;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly System.Threading.Timer autoSaveTimer;

    public ChatSession? ActiveSession { get; private set; }

    public event Action? SessionsChanged;

    private void NotifySessionsChanged() => SessionsChanged?.Invoke();

    public IReadOnlyList<ChatSession> Sessions => sessions;

    private ChatManager()
    {
        basePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ToolBox");
        chatsJsonPath = Path.Combine(basePath, "chats.json");
        sessionsDir = Path.Combine(basePath, "sessions");
        Directory.CreateDirectory(sessionsDir);
        Load();
        autoSaveTimer = new System.Threading.Timer(_ => _ = AutoSaveAsync(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public async Task<ChatSession> CreateSessionAsync(string title = "新会话")
    {
        var session = new ChatSession { Title = title };
        sessions.Insert(0, session);
        await SaveChatsIndexAsync().ConfigureAwait(false);
        NotifySessionsChanged();
        return session;
    }

    public ChatSession CreateSessionWithImage(byte[] imageData, string title = "截图对话")
    {
        var session = new ChatSession { Title = title };
        sessions.Insert(0, session);
        var imageDir = Path.Combine(sessionsDir, session.Id, "images");
        Directory.CreateDirectory(imageDir);
        var imagePath = Path.Combine(imageDir, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}.png");
        File.WriteAllBytes(imagePath, imageData);

        session.Messages.Add(new ChatMessage
        {
            Role = "user",
            Content = "请描述这张截图的内容",
            ImagePath = imagePath
        });
        _ = SaveSessionMessagesAsync(session);
        return session;
    }

    public async Task<ChatSession?> SwitchSessionAsync(string sessionId)
    {
        var session = sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session != null)
        {
            ActiveSession = session;
            LoadSessionMessages(session);
        }
        return session;
    }

    public void SwitchSession(string sessionId)
    {
        var session = sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session != null)
        {
            ActiveSession = session;
            LoadSessionMessages(session);
        }
    }

    public async Task TogglePinAsync(string sessionId)
    {
        var session = sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session == null) return;
        session.IsPinned = !session.IsPinned;
        ReorderSessions();
        await SaveChatsIndexAsync().ConfigureAwait(false);
        NotifySessionsChanged();
    }

    private void ReorderSessions()
    {
        sessions.Sort((a, b) =>
        {
            if (a.IsPinned != b.IsPinned) return b.IsPinned.CompareTo(a.IsPinned);
            return b.UpdatedAt.CompareTo(a.UpdatedAt);
        });
    }

    public async Task DeleteSessionAsync(string sessionId)
    {
        sessions.RemoveAll(s => s.Id == sessionId);
        var sessionDir = Path.Combine(sessionsDir, sessionId);
        if (Directory.Exists(sessionDir))
            Directory.Delete(sessionDir, true);
        await SaveChatsIndexAsync().ConfigureAwait(false);
    }

    public async Task SaveSessionMessagesAsync(ChatSession session)
    {
        var path = Path.Combine(sessionsDir, $"{session.Id}.json");
        var json = JsonSerializer.Serialize(session.Messages, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        await AtomicWriteAsync(path, json).ConfigureAwait(false);
    }

    public void LoadSessionMessages(ChatSession session)
    {
        var path = Path.Combine(sessionsDir, $"{session.Id}.json");
        if (!File.Exists(path)) return;
        try
        {
            var json = File.ReadAllText(path);
            session.Messages = JsonSerializer.Deserialize<List<ChatMessage>>(json) ?? [];
        }
        catch
        {
            session.Messages = [];
        }
    }

    private void Load()
    {
        if (!File.Exists(chatsJsonPath)) return;
        try
        {
            var json = File.ReadAllText(chatsJsonPath);
            var data = JsonSerializer.Deserialize<ChatsIndex>(json);
            if (data?.Chats != null)
                sessions.AddRange(data.Chats.Select(c => new ChatSession { Id = c.Id, Title = c.Title, IsTitleLocked = c.IsTitleLocked, IsPinned = c.IsPinned, Status = c.Status, CreatedAt = c.CreatedAt, UpdatedAt = c.UpdatedAt }));
            ReorderSessions();
        }
        catch { }
    }

    private async Task SaveChatsIndexAsync()
    {
        var data = new ChatsIndex
        {
            Version = 1,
            Chats = sessions.Select(s => new ChatSessionIndex
            {
                Id = s.Id,
                Title = s.Title,
                IsTitleLocked = s.IsTitleLocked,
                IsPinned = s.IsPinned,
                Status = s.Status,
                CreatedAt = s.CreatedAt,
                UpdatedAt = s.UpdatedAt
            }).ToList()
        };
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        await AtomicWriteAsync(chatsJsonPath, json).ConfigureAwait(false);
    }

    private async Task AtomicWriteAsync(string path, string content)
    {
        await writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var tmpPath = path + ".tmp";
            await File.WriteAllTextAsync(tmpPath, content).ConfigureAwait(false);
            using (var fs = new FileStream(tmpPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                fs.Flush(true);
            }
            File.Move(tmpPath, path, true);
        }
        finally
        {
            writeLock.Release();
        }
    }

    public async Task AutoGenerateTitleAsync(ChatSession session)
    {
        if (session.IsTitleLocked) return;
        if (!session.Title.StartsWith("Screenshot") && !session.Title.StartsWith("New") &&
            !session.Title.StartsWith("新会话") && !session.Title.StartsWith("截图会话"))
            return;
        var msg = session.Messages.FirstOrDefault(m => m.Role == "user" && !string.IsNullOrWhiteSpace(m.Content));
        if (msg == null) return;
        var t = msg.Content!.Trim();
        session.Title = t.Length > 25 ? t[..25] + "..." : t;
        session.IsTitleLocked = false;
        await SaveChatsIndexAsync().ConfigureAwait(false);
        NotifySessionsChanged();
    }

    public void RenameSession(ChatSession session, string newTitle)
    {
        if (string.IsNullOrWhiteSpace(newTitle)) return;
        session.Title = newTitle.Trim();
        _ = SaveChatsIndexAsync();
        NotifySessionsChanged();
    }

    public async Task LockTitleAsync(ChatSession session)
    {
        session.IsTitleLocked = true;
        await SaveChatsIndexAsync().ConfigureAwait(false);
    }

    private async Task AutoSaveAsync()
    {
        try
        {
            if (ActiveSession != null && ActiveSession.Status == "running")
                await SaveSessionMessagesAsync(ActiveSession).ConfigureAwait(false);
        }
        catch { }
    }
}

internal class ChatsIndex
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("chats")]
    public List<ChatSessionIndex>? Chats { get; set; }
}

internal class ChatSessionIndex
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("isTitleLocked")]
    public bool IsTitleLocked { get; set; }

    [JsonPropertyName("isPinned")]
    public bool IsPinned { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "idle";

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }
}
