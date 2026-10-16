using System.Windows;
using System.Windows.Controls;
using ToolBox.Core.Assistant;

namespace ToolBox.Views.Shell;

public partial class Sidebar : UserControl
{
    public event System.Action<string>? NavigationRequested;

    public Sidebar()
    {
        InitializeComponent();
        RefreshAssistantVisibility();
    }

    public void RefreshAssistantVisibility()
    {
        NavAssistant.Visibility = AssistantHost.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (!AssistantHost.IsEnabled && NavAssistant.IsChecked == true)
            NavDashboard.IsChecked = true;
    }

    public void SetActive(string page)
    {
        var target = page switch
        {
            "dashboard" => NavDashboard,
            "todos" => NavTodos,
            "boards" => NavBoards,
            "assistant" when AssistantHost.IsEnabled => NavAssistant,
            "screenshots" => NavScreenshots,
            "clipboard" => NavClipboard,
            "network" => NavNetwork,
            "settings" => NavSettings,
            _ => NavDashboard
        };
        if (!target.IsChecked.GetValueOrDefault())
            target.IsChecked = true;
    }

    private void OnNavChecked(object sender, RoutedEventArgs e)
    {
        if (sender == NavDashboard) NavigationRequested?.Invoke("dashboard");
        else if (sender == NavTodos) NavigationRequested?.Invoke("todos");
        else if (sender == NavBoards) NavigationRequested?.Invoke("boards");
        else if (sender == NavAssistant && AssistantHost.IsEnabled) NavigationRequested?.Invoke("assistant");
        else if (sender == NavScreenshots) NavigationRequested?.Invoke("screenshots");
        else if (sender == NavClipboard) NavigationRequested?.Invoke("clipboard");
        else if (sender == NavNetwork) NavigationRequested?.Invoke("network");
        else if (sender == NavSettings) NavigationRequested?.Invoke("settings");
    }
}
