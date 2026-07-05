using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ToolBox.Core.Llm;
using ToolBox.Core.Todo;
using ToolBox.Core.Theming;
using ToolBox.Services;

namespace ToolBox.Views.Dashboard;

public partial class DashboardView : UserControl
{
    public event Action<string>? NavigationRequested;

    public DashboardView()
    {
        InitializeComponent();
        RefreshStats();
    }

    public void RefreshStats()
    {
        try
        {
            StatTodo.Text = TodoStore.Instance.GetPending().Count.ToString();
            StatChat.Text = ChatManager.Instance.Sessions.Count.ToString();

            var cacheDir = CacheManager.CachePath;
            StatShot.Text = Directory.Exists(cacheDir)
                ? Directory.GetDirectories(cacheDir).Length.ToString()
                : "0";

            StatTheme.Text = ThemeManager.Instance.Mode.ToString();
            WelcomeHint.Text = $"你今天还有 {TodoStore.Instance.GetPending().Count} 件待办未完成";
        }
        catch
        {
            // 忽略统计失败,不影响页面展示
        }
    }

    private void StatCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el && el.Tag is string page)
            NavigationRequested?.Invoke(page);
    }

    private void QuickAction_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el && el.Tag is string page)
            NavigationRequested?.Invoke(page);
    }
}
