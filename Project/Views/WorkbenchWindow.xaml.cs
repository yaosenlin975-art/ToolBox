using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ToolBox.Views.Chat;
using ToolBox.Views.Dashboard;
using ToolBox.Views.Todo;

namespace ToolBox.Views;

public partial class WorkbenchWindow : Window
{
    private DashboardView? dashboardView;

    public WorkbenchWindow()
    {
        InitializeComponent();
        TheSidebar.NavigationRequested += OnNavigationRequested;
        LoadPage("dashboard");
    }

    private void OnNavigationRequested(string page) => LoadPage(page);

    public void LoadPage(string page)
    {
        PageTitle.Text = page switch
        {
            "dashboard" => "工作台",
            "assistant" => "AI 助手",
            "todos" => "待办管理",
            "screenshots" => "截图历史",
            "settings" => "设置",
            _ => string.Empty
        };
        TheHeader.Title = PageTitle.Text;
        TheSidebar.SetActive(page);

        PageHost.Content = page switch
        {
            "dashboard" => GetDashboard(),
            "assistant" => new ChatView(),
            "todos" => new TodoView(),
            "screenshots" => new HistoryView(),
            "settings" => new SettingsView(),
            _ => null
        };
    }

    private DashboardView GetDashboard()
    {
        if (dashboardView == null)
        {
            dashboardView = new DashboardView();
            dashboardView.NavigationRequested += p => LoadPage(p);
        }
        else
        {
            dashboardView.RefreshStats();
        }
        return dashboardView;
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            BtnMaximize.Content = "\uE922";
        }
        else
        {
            WindowState = WindowState.Maximized;
            BtnMaximize.Content = "\uE923";
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Hide();

    private void OnSwitchToCompact(object sender, RoutedEventArgs e)
    {
        Hide();
        App.CompactToolbox?.Show();
        App.CompactToolbox?.Activate();
    }
}
