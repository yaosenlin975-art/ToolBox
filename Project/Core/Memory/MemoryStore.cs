using Microsoft.Data.Sqlite;
using System.IO;

namespace ToolBox.Core.Memory;

public class MemoryStore
{
    private static MemoryStore? instance;
    public static MemoryStore Instance => instance ??= new MemoryStore();

    private readonly string dbPath;
    private readonly string connectionString;

    private MemoryStore()
    {
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ToolBox", "Data");
        Directory.CreateDirectory(baseDir);
        dbPath = Path.Combine(baseDir, "memory.db");
        connectionString = $"Data Source={dbPath}";
        InitDatabase();
    }

    private void InitDatabase()
    {
        using var conn = new SqliteConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS memories (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                role TEXT NOT NULL,
                content TEXT NOT NULL,
                token_count INTEGER DEFAULT 0,
                importance REAL DEFAULT 0.5,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                accessed_at TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE INDEX IF NOT EXISTS idx_memories_session ON memories(session_id);
            CREATE INDEX IF NOT EXISTS idx_memories_importance ON memories(importance DESC);";
        cmd.ExecuteNonQuery();
    }

    public void Save(string sessionId, string role, string content, int tokenCount = 0, double importance = 0.5)
    {
        using var conn = new SqliteConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO memories (session_id, role, content, token_count, importance)
                            VALUES ($sid, $role, $content, $tc, $imp)";
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$role", role);
        cmd.Parameters.AddWithValue("$content", content);
        cmd.Parameters.AddWithValue("$tc", tokenCount);
        cmd.Parameters.AddWithValue("$imp", importance);
        cmd.ExecuteNonQuery();
    }

    public List<string> GetRelevant(string sessionId, int maxTokens = 2000)
    {
        using var conn = new SqliteConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT content, token_count FROM memories
                            WHERE session_id = $sid
                            ORDER BY importance DESC, created_at DESC
                            LIMIT 100";
        cmd.Parameters.AddWithValue("$sid", sessionId);

        var results = new List<string>();
        int totalTokens = 0;
        using var reader = cmd.ExecuteReader();
        while (reader.Read() && totalTokens < maxTokens)
        {
            var content = reader.GetString(0);
            var tc = reader.GetInt32(1);
            if (totalTokens + tc > maxTokens) break;
            results.Add(content);
            totalTokens += tc;
        }
        return results;
    }

    public void Cleanup(int retentionDays = 30)
    {
        using var conn = new SqliteConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"DELETE FROM memories
                            WHERE created_at < datetime('now', '-' || $days || ' days')";
        cmd.Parameters.AddWithValue("$days", retentionDays);
        cmd.ExecuteNonQuery();
    }
}
