using System.IO;
using System.Text.Json;

namespace ToolBox.Core.Security;

public class FileAccessWhitelist
{
    private static FileAccessWhitelist? instance;
    public static FileAccessWhitelist Instance => instance ??= new FileAccessWhitelist();

    private readonly List<string> allowedPaths = [];
    private readonly string configPath;

    public IReadOnlyList<string> AllowedPaths => allowedPaths;

    private FileAccessWhitelist()
    {
        configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ToolBox", "file_whitelist.json");
        Load();
    }

    public bool IsAllowed(string path)
    {
        var normalized = NormalizePath(path);
        return allowedPaths.Any(p => normalized.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    public void Add(string path)
    {
        var normalized = NormalizePath(path);
        if (!allowedPaths.Contains(normalized))
        {
            allowedPaths.Add(normalized);
            Save();
        }
    }

    public void Remove(string path)
    {
        var normalized = NormalizePath(path);
        allowedPaths.RemoveAll(p => p.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    private void Load()
    {
        if (!File.Exists(configPath)) return;
        try
        {
            var json = File.ReadAllText(configPath);
            var data = JsonSerializer.Deserialize<List<string>>(json);
            if (data != null) allowedPaths.AddRange(data);
        }
        catch { }
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(configPath);
        if (dir != null) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(allowedPaths, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(configPath, json);
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
