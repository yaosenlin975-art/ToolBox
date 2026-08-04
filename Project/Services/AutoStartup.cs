using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ToolBox.Services;

public static class AutoStartup
{
    private const string AppName = "ToolBox";
    private static readonly string RegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, false);
            return key?.GetValue(AppName) != null;
        }
    }

    public static void Enable()
    {
        var exePath = Environment.ProcessPath;
        // 通过 dotnet host（dotnet run / 调试器）启动时 ProcessPath 指向 dotnet.exe，
        // 回退到程序集位置，确保写入的是 ToolBox.exe 本体路径。
        if (string.IsNullOrEmpty(exePath)
            || Path.GetFileNameWithoutExtension(exePath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            exePath = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrEmpty(exePath)) return;

        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, true);
        key?.SetValue(AppName, $"\"{exePath}\"");
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, true);
        key?.DeleteValue(AppName, false);
    }

    public static void Toggle()
    {
        if (IsEnabled)
            Disable();
        else
            Enable();
    }
}
