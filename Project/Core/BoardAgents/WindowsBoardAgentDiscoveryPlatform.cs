using Microsoft.Win32;
using System.IO;

namespace ToolBox.Core.BoardAgents;

public sealed class WindowsBoardAgentDiscoveryPlatform : IBoardAgentDiscoveryPlatform
{
    public string? GetEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);

    public string? GetFolderPath(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);

    public IEnumerable<string> GetPathDirectories() =>
        (GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public IEnumerable<string> GetUrlProtocolCommands(string scheme)
    {
        if (!OperatingSystem.IsWindows()) yield break;

        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var key = baseKey.OpenSubKey($"Software\\Classes\\{scheme}\\shell\\open\\command");
            if (key?.GetValue(null) is string command && !string.IsNullOrWhiteSpace(command)) yield return command;
        }
    }

    public IEnumerable<BoardAgentInstalledApp> GetUninstallEntries()
    {
        if (!OperatingSystem.IsWindows()) yield break;

        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var root = baseKey.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall");
            if (root is null) continue;

            foreach (string name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);
                string? displayName = key?.GetValue("DisplayName") as string;
                if (string.IsNullOrWhiteSpace(displayName)) continue;
                yield return new BoardAgentInstalledApp(
                    displayName,
                    key?.GetValue("DisplayIcon") as string,
                    key?.GetValue("UninstallString") as string,
                    key?.GetValue("InstallLocation") as string);
            }
        }
    }

    public IEnumerable<string> GetCommonRoots()
    {
        foreach (string? root in new[]
        {
            GetEnvironmentVariable("ProgramW6432"),
            GetEnvironmentVariable("ProgramFiles"),
            GetEnvironmentVariable("ProgramFiles(x86)"),
            Path.Combine(GetFolderPath(Environment.SpecialFolder.LocalApplicationData) ?? string.Empty, "Programs"),
            @"D:\Program Files"
        }.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return root!;
        }
    }
}
