using System.IO;
using Microsoft.Data.Sqlite;

namespace ToolBox.Core.Memory;

public sealed class MemoryStore
{
    private static readonly Lazy<MemoryStore> Singleton = new(() => new MemoryStore());
    private readonly string databasePath;
    private readonly string connectionString;
    private readonly SemaphoreSlim gate = new(1, 1);

    public static MemoryStore Instance => Singleton.Value;

    public MemoryStore(string? databasePath = null)
    {
        string path = databasePath ?? GetDefaultDatabasePath();
        this.databasePath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(this.databasePath)!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = this.databasePath }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS memories (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                role TEXT NOT NULL,
                content TEXT NOT NULL,
                token_count INTEGER NOT NULL DEFAULT 0,
                importance REAL NOT NULL DEFAULT 0.5,
                created_at TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE INDEX IF NOT EXISTS idx_memories_session ON memories(session_id);
            CREATE INDEX IF NOT EXISTS idx_memories_importance ON memories(importance DESC);
            """;
        command.ExecuteNonQuery();
    }

    private static string GetDefaultDatabasePath()
    {
#if DEBUG
        string? testRoot = Environment.GetEnvironmentVariable("TOOLBOX_TASK_TEST_ROOT");
        if (!string.IsNullOrWhiteSpace(testRoot))
            return Path.Combine(Path.GetFullPath(testRoot), "assistant", "Data", "memory.db");
#endif
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ToolBox", "Data", "memory.db");
    }

    public async Task SaveAsync(
        string sessionId,
        string role,
        string content,
        int tokenCount = 0,
        double importance = 0.5,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO memories (session_id, role, content, token_count, importance) VALUES ($sid, $role, $content, $tc, $importance)";
            command.Parameters.AddWithValue("$sid", sessionId);
            command.Parameters.AddWithValue("$role", role);
            command.Parameters.AddWithValue("$content", content);
            command.Parameters.AddWithValue("$tc", Math.Max(0, tokenCount));
            command.Parameters.AddWithValue("$importance", importance);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task<List<string>> GetRelevantAsync(
        string sessionId,
        int maxTokens = 2000,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT content, token_count FROM memories WHERE session_id = $sid ORDER BY importance DESC, created_at DESC LIMIT 100";
            command.Parameters.AddWithValue("$sid", sessionId);
            var results = new List<string>();
            int totalTokens = 0;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && totalTokens < maxTokens)
            {
                int count = Math.Max(reader.GetInt32(1), Math.Max(1, reader.GetString(0).Length / 4));
                if (totalTokens + count > maxTokens) break;
                results.Add(reader.GetString(0));
                totalTokens += count;
            }
            return results;
        }
        finally { gate.Release(); }
    }

    public async Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM memories WHERE session_id = $sid";
            command.Parameters.AddWithValue("$sid", sessionId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using (var command = connection.CreateCommand())
                {
                    command.CommandText = "DELETE FROM memories; VACUUM;";
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                await using var checkpoint = connection.CreateCommand();
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                await checkpoint.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            DeleteIfPresent(databasePath + "-wal");
            DeleteIfPresent(databasePath + "-shm");
        }
        finally { gate.Release(); }
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    public async Task CleanupAsync(int retentionDays = 30, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM memories WHERE created_at < datetime('now', '-' || $days || ' days')";
            command.Parameters.AddWithValue("$days", Math.Max(1, retentionDays));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
}
