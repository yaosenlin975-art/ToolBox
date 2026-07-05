using System.Windows;
using System.Windows.Controls;

namespace ToolBox.Views.Shell;

public partial class Sidebar : UserControl
{
    public event System.Action<string>? NavigationRequested;

    public Sidebar()
    {
        InitializeComponent();
    }

    public void SetActive(string page)
    {
        var target = page switch
        {
            "dashboard" => NavDashboard,
            "assistant" => NavAssistant,
            "todos" => NavTodos,
            "screenshots" => NavScreenshots,
            "settings" => NavSettings,
            _ => NavDashboard
        };
        if (!target.IsChecked.GetValueOrDefault())
            target.IsChecked = true;
    }

    private void OnNavChecked(object sender, RoutedEventArgs e)
    {
        if (sender == NavDashboard) NavigationRequested?.Invoke("dashboard");
        else if (sender == NavAssistant) NavigationRequested?.Invoke("assistant");
        else if (sender == NavTodos) NavigationRequested?.Invoke("todos");
        else if (sender == NavScreenshots) NavigationRequested?.Invoke("screenshots");
        else if (sender == NavSettings) NavigationRequested?.Invoke("settings");
    }
}
