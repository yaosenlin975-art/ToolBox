using System;
using System.Windows;
using Microsoft.Win32;

namespace ToolBox.Core.Theming;

public enum EThemeMode
{
    Light,
    Dark,
    System
}

public sealed class ThemeManager
{
    private const string LightSource = "pack://application:,,,/Themes/Light.xaml";
    private const string DarkSource = "pack://application:,,,/Themes/Dark.xaml";

    private static readonly Lazy<ThemeManager> _instance = new(() => new ThemeManager());
    public static ThemeManager Instance => _instance.Value;

    public EThemeMode Mode { get; private set; } = EThemeMode.System;
    public bool IsDark => ResolveIsDark(Mode);

    public event EventHandler? ThemeChanged;

    private ThemeManager() { }

    public void Initialize(string modeString)
    {
        Mode = ParseMode(modeString);
        ApplyTheme();
        if (Mode == EThemeMode.System)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
    }

    public void SetMode(EThemeMode mode)
    {
        if (Mode == EThemeMode.System)
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

        Mode = mode;
        ApplyTheme();

        if (Mode == EThemeMode.System)
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General)
        {
            Application.Current?.Dispatcher.BeginInvoke(new Action(ApplyTheme));
        }
    }

    private void ApplyTheme()
    {
        var app = Application.Current;
        if (app == null) return;

        var dicts = app.Resources.MergedDictionaries;
        ResourceDictionary? existing = null;
        for (int i = 0; i < dicts.Count; i++)
        {
            var src = dicts[i].Source?.OriginalString ?? string.Empty;
            if (src.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase) ||
                src.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase))
            {
                existing = dicts[i];
                break;
            }
        }

        var newSource = ResolveIsDark(Mode) ? DarkSource : LightSource;
        if (existing != null)
        {
            if (existing.Source?.OriginalString == newSource) return;
            dicts.Remove(existing);
        }
        dicts.Add(new ResourceDictionary { Source = new Uri(newSource, UriKind.Absolute) });
    }

    private static bool ResolveIsDark(EThemeMode mode)
    {
        if (mode == EThemeMode.Dark) return true;
        if (mode == EThemeMode.Light) return false;
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        var value = key?.GetValue("AppsUseLightTheme");
        return value is int i ? i == 0 : false;
    }

    private static EThemeMode ParseMode(string? s)
    {
        return s?.ToLowerInvariant() switch
        {
            "light" => EThemeMode.Light,
            "dark" => EThemeMode.Dark,
            _ => EThemeMode.System
        };
    }

    public string ModeToString() => Mode.ToString();
}
