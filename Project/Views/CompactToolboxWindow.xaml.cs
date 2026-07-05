using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ToolBox.Core.Llm;
using ToolBox.Core.Todo;
using ToolBox.Models;
using ToolBox.Services;


namespace ToolBox.Views;

public partial class CompactToolboxWindow : Window
{
    private const double CollapsedWidth = 320;
    private const double CollapsedHeight = 320;

    private readonly ToolBoxOption options;
    private string currentTab = "todo";

    public CompactToolboxWindow()
    {
        InitializeComponent();
        options = ToolBoxOption.Load();

        Left = SystemParameters.WorkArea.Width - CollapsedWidth - 20;
        Top = SystemParameters.WorkArea.Height - CollapsedHeight - 20;

        ApplyCompactOpacity(options.Data.CompactOpacity);

        LoadSessions();
        LoadTodos();
        LoadHistory();
        SwitchToTab("todo");

        TodoStore.Instance.ItemsChanged += () => Dispatcher.Invoke(LoadTodos);
        ChatManager.Instance.SessionsChanged += () => Dispatcher.Invoke(LoadSessions);
    }

    public void ApplyCompactOpacity(int opacityPercent)
    {
        var alpha = (byte)(Math.Clamp(opacityPercent, 0, 100) * 255 / 100);
        if (TryFindResource("BgElevatedBrush") is SolidColorBrush brush)
        {
            var c = brush.Color;
            RootBorder.Background = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        }
    }

    public void SwitchToTab(string tab)
    {
        currentTab = tab;
        TodoPanel.Visibility = Visibility.Collapsed;
        ChatPanel.Visibility = Visibility.Collapsed;
        HistoryPanel.Visibility = Visibility.Collapsed;
        SessionRow.Visibility = Visibility.Collapsed;

        TabTodo.Style = (Style)FindResource("CompactTabButton");
        TabChat.Style = (Style)FindResource("CompactTabButton");
        TabHistory.Style = (Style)FindResource("CompactTabButton");

        switch (tab)
        {
            case "todo":
                TodoPanel.Visibility = Visibility.Visible;
                TabTodo.Style = (Style)FindResource("CompactTabButtonActive");
                break;
            case "chat":
                ChatPanel.Visibility = Visibility.Visible;
                TabChat.Style = (Style)FindResource("CompactTabButtonActive");
                SessionRow.Visibility = Visibility.Visible;
                break;
            case "history":
                HistoryPanel.Visibility = Visibility.Visible;
                TabHistory.Style = (Style)FindResource("CompactTabButtonActive");
                break;
        }
    }

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tab)
        {
            SwitchToTab(tab);
        }
    }

    private void ExpandToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        OpenWorkbench();
    }

    public void OpenWorkbench()
    {
        Hide();
        App.Workbench?.Show();
        App.Workbench?.Activate();
    }

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        App.Workbench?.Show();
        App.Workbench?.Activate();
        App.Workbench?.LoadPage("settings");
    }

    private void LoadSessions()
    {
        var sessions = ChatPanel.Sessions.ToList();
        SessionSelector.ItemsSource = sessions;
        if (ChatManager.Instance.ActiveSession != null)
        {
            for (int i = 0; i < sessions.Count; i++)
            {
                if (sessions[i].Id == ChatManager.Instance.ActiveSession.Id)
                {
                    SessionSelector.SelectedIndex = i;
                    break;
                }
            }
        }
        else if (sessions.Count > 0)
        {
            SessionSelector.SelectedIndex = 0;
        }
    }

    private void LoadTodos()
    {
        var pending = TodoStore.Instance.GetPending();
        TodoList.ItemsSource = pending;
    }

    private void LoadHistory()
    {
        HistoryList.Items.Clear();
        var cachePath = CacheManager.CachePath;
        if (!Directory.Exists(cachePath)) return;

        var directories = Directory.GetDirectories(cachePath, "*", SearchOption.TopDirectoryOnly);
        var items = new List<CacheItem>();
        foreach (var dir in directories)
        {
            var item = CacheItem.Read(dir);
            if (item?.IsValid == true) items.Add(item);
        }
        items.Sort((a, b) => b.CreateTime.CompareTo(a.CreateTime));

        foreach (var item in items.Take(10))
        {
            var image = item.ReadImage();
            if (image != null)
            {
                HistoryList.Items.Add(new HistoryItemViewModel
                {
                    CacheItem = item,
                    Thumbnail = image,
                    TimeDisplay = item.CreateTime.ToString("MM-dd HH:mm"),
                    SizeDisplay = image.PixelWidth + " × " + image.PixelHeight
                });
            }
        }
    }

    private void TodoItem_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.Tag is string id)
        {
            TodoStore.Instance.Complete(id);
        }
    }

    private void QuickAddTodo_Click(object sender, RoutedEventArgs e)
    {
        var input = new InputWindow("快速添加待办", "请输入标题:");
        input.Owner = this;
        if (input.ShowDialog() == true && !string.IsNullOrWhiteSpace(input.Value))
        {
            TodoStore.Instance.Add(input.Value.Trim());
        }
    }

    private void SessionSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionSelector.SelectedItem is ChatSession session)
        {
            ChatPanel.SelectSession(session.Id);
        }
    }

    private void NewSessionBtn_Click(object sender, RoutedEventArgs e)
    {
        var session = ChatPanel.CreateNewSession();
        LoadSessions();
        for (int i = 0; i < SessionSelector.Items.Count; i++)
        {
            if (SessionSelector.Items[i] is ChatSession s && s.Id == session.Id)
            {
                SessionSelector.SelectedIndex = i;
                break;
            }
        }
    }

    private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source != null)
        {
            if (source is Button || source is ComboBox || source is CheckBox) return;
            source = VisualTreeHelper.GetParent(source);
        }
        DragMove();
    }
}
