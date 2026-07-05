using System;
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
