﻿using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ToolBox.Views.Chat;
using ToolBox.Views.ClipboardHistory;
using ToolBox.Views.Dashboard;
using ToolBox.Views.Todo;

namespace ToolBox.Views;

public partial class WorkbenchWindow : Window
{
    private DashboardView? dashboardView;
    private ClipboardView? clipboardView;

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
            "dashboard" => (FindResource("Lang_Page_Dashboard") as string) ?? "工作台",
            "assistant" => (FindResource("Lang_Page_Assistant") as string) ?? "AI 助手",
            "todos" => (FindResource("Lang_Page_Todos") as string) ?? "待办管理",
            "screenshots" => (FindResource("Lang_Page_Screenshots") as string) ?? "截图历史",
            "clipboard" => (FindResource("Lang_Page_Clipboard") as string) ?? "剪贴板",
            "settings" => (FindResource("Lang_Page_Settings") as string) ?? "设置",
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
            "clipboard" => GetClipboard(),
            "settings" => new SettingsView(),
            _ => null
        };
    }

    private ClipboardView GetClipboard()
    {
        // 复用同一实例,避免每次切换都重新订阅事件
        clipboardView ??= new ClipboardView();
        return clipboardView;
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
            BtnMaximize.Content = "";
        }
        else
        {
            WindowState = WindowState.Maximized;
            BtnMaximize.Content = "";
        }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        var dialog = new CloseDialogWindow
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true && dialog.Result == CloseDialogResult.Minimize)
        {
            Hide();
            App.CompactToolbox?.Show();
            App.CompactToolbox?.Activate();
        }
        else if (dialog.Result == CloseDialogResult.Exit)
        {
            Shutdown();
        }
    }

    private void Shutdown()
    {
        Application.Current.Shutdown();
    }

    private void OnSwitchToCompact(object sender, RoutedEventArgs e)
    {
        Hide();
        App.CompactToolbox?.Show();
        App.CompactToolbox?.Activate();
    }
}
