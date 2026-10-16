using System.IO;
using System.Text.Json;

namespace ToolBox.Core.Security;

public class FileAccessWhitelist
{
    private static FileAccessWhitelist? instance;
    public static FileAccessWhitelist Instance => instance ??= new FileAccessWhitelist();

    private readonly List<string> allowedPaths = [];
    private readonly object sync = new();
    private readonly string configPath;

    public IReadOnlyList<string> AllowedPaths
    {
        get { lock (sync) return allowedPaths.ToArray(); }
    }

    private FileAccessWhitelist() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ToolBox", "file_whitelist.json")) { }

    public FileAccessWhitelist(string configPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        this.configPath = Path.GetFullPath(configPath);
        Load();
    }

    public bool IsAllowed(string path)
    {
        // shortcut: path checks can race with link replacement; use handle-relative I/O if untrusted local processes can mutate allowed roots.
        if (!TryNormalizePath(path, out var normalized) || !TryResolveLinks(normalized, out var resolved))
            return false;

        string[] roots;
        lock (sync) roots = [.. allowedPaths];
        return roots.Any(root => IsWithin(resolved, root));
    }

    public void Add(string path)
    {
        var normalized = NormalizePath(path);
        if (!TryResolveLinks(normalized, out var resolved))
            throw new ArgumentException("The path cannot be resolved safely.", nameof(path));

        lock (sync)
        {
            if (!allowedPaths.Contains(resolved, StringComparer.OrdinalIgnoreCase))
            {
                allowedPaths.Add(resolved);
                Save();
            }
        }
    }

    public void Remove(string path)
    {
        var normalized = NormalizePath(path);
        lock (sync)
        {
            var resolved = TryResolveLinks(normalized, out var target) ? target : normalized;
            allowedPaths.RemoveAll(p => p.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                p.Equals(resolved, StringComparison.OrdinalIgnoreCase));
            Save();
        }
    }

    private void Load()
    {
        if (!File.Exists(configPath)) return;
        try
        {
            var data = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(configPath));
            if (data == null) return;

            foreach (var path in data)
            {
                // Older entries containing reparse points are ignored; Add stores their resolved root.
                if (TryNormalizePath(path, out var normalized) &&
                    TryResolveLinks(normalized, out var resolved) &&
                    resolved.Equals(normalized, StringComparison.OrdinalIgnoreCase) &&
                    !allowedPaths.Contains(resolved, StringComparer.OrdinalIgnoreCase))
                    allowedPaths.Add(resolved);
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ToolBox] {ex.Message}"); }
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(configPath);
        if (dir != null) Directory.CreateDirectory(dir);
        File.WriteAllText(configPath, JsonSerializer.Serialize(allowedPaths, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static bool TryNormalizePath(string path, out string normalized)
    {
        try
        {
            normalized = NormalizePath(path);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            normalized = string.Empty;
            return false;
        }
    }

    private static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var windowsPath = path.Replace('/', '\\');
        if (windowsPath.StartsWith("\\\\", StringComparison.Ordinal) ||
            windowsPath.StartsWith("\\??\\", StringComparison.OrdinalIgnoreCase) ||
            windowsPath.StartsWith("\\Device\\", StringComparison.OrdinalIgnoreCase) ||
            (windowsPath.Contains(':') && !(windowsPath.Length > 2 &&
                char.IsAsciiLetter(windowsPath[0]) && windowsPath[1] == ':' && windowsPath[2] == '\\')) ||
            HasDeviceName(windowsPath))
            throw new ArgumentException("Network, device, URI, and device-name paths are not allowed.", nameof(path));

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(windowsPath));
    }

    private static bool TryResolveLinks(string path, out string resolved)
    {
        resolved = string.Empty;
        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root)) return false;

            var current = root;
            foreach (var segment in fullPath[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                if (HasDeviceName(segment)) return false;
                current = Path.Combine(current, segment);
                try
                {
                    var attributes = File.GetAttributes(current);
                    if ((attributes & FileAttributes.ReparsePoint) == 0) continue;

                    FileSystemInfo info = (attributes & FileAttributes.Directory) != 0
                        ? new DirectoryInfo(current)
                        : new FileInfo(current);
                    var target = info.ResolveLinkTarget(true);
                    if (target == null) return false;
                    current = Path.GetFullPath(target.FullName);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                catch { return false; }
            }

            resolved = Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
            return true;
        }
        catch { return false; }
    }

    private static bool IsWithin(string path, string root)
    {
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasDeviceName(string path)
    {
        foreach (var part in path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            var name = Path.GetFileNameWithoutExtension(part.TrimEnd(' ', '.'));
            if (name.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
                name.Length == 4 && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                (name[3] is >= '1' and <= '9' or '\u00B9' or '\u00B2' or '\u00B3'))
                return true;
        }
        return false;
    }
}
