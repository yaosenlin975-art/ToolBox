using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private static readonly (double w, double h) TodoSize = (200, 350);
    private static readonly (double w, double h) ChatSize = (400, 400);
    private static readonly (double w, double h) ScreenshotSize = (200, 350);

    private readonly ToolBoxOption options;
    private string currentTab = "todo";

    public CompactToolboxWindow()
    {
        InitializeComponent();
        options = ToolBoxOption.Load();

        PositionWindow();
        SwitchToTab("todo");

        TodoStore.Instance.ItemsChanged += () => Dispatcher.Invoke(LoadTodos);
        ChatManager.Instance.SessionsChanged += () => Dispatcher.Invoke(LoadSessions);
        CacheManager.Instance.OnScrapCached += (_, _) => Dispatcher.Invoke(LoadScreenshots);
    }

    public void ApplyCompactOpacity(int opacityPercent)
    {
        options.Data.CompactOpacity = opacityPercent;
        SwitchToTab(currentTab);
    }

    private void PositionWindow()
    {
        Left = SystemParameters.WorkArea.Width - Width - 20;
        Top = SystemParameters.WorkArea.Height - Height - 20;
    }

    public void SwitchToTab(string tab)
    {
        currentTab = tab;
        TodoPanel.Visibility = Visibility.Collapsed;
        ChatPanel.Visibility = Visibility.Collapsed;
        ScreenshotPanel.Visibility = Visibility.Collapsed;
        SessionRow.Visibility = Visibility.Collapsed;

        TabTodo.Style = (Style)FindResource("CompactTabButton");
        TabChat.Style = (Style)FindResource("CompactTabButton");
        TabScreenshot.Style = (Style)FindResource("CompactTabButton");

        var (w, h) = tab switch
        {
            "chat" => ChatSize,
            "screenshot" => ScreenshotSize,
            _ => TodoSize
        };

        Width = w;
        Height = h;
        PositionWindow();

        // Opacity: chat always 1, others use setting
        if (tab == "chat")
        {
            RootBorder.Opacity = 1.0;
        }
        else
        {
            RootBorder.Opacity = options.Data.CompactOpacity / 100.0;
        }

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
            case "screenshot":
                ScreenshotPanel.Visibility = Visibility.Visible;
                TabScreenshot.Style = (Style)FindResource("CompactTabButtonActive");
                LoadScreenshots();
                break;
        }
    }

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tab)
            SwitchToTab(tab);
    }

    private void ExpandToggleBtn_Click(object sender, RoutedEventArgs e) => OpenWorkbench();

    public void OpenWorkbench()
    {
        Hide();
        App.Workbench?.Show();
        App.Workbench?.Activate();
    }

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        var wb = App.Workbench;
        if (wb == null) return;
        wb.Show();
        wb.Activate();
        wb.LoadPage("settings");
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
        TodoList.ItemsSource = TodoStore.Instance.GetPending();
    }

    private void LoadScreenshots()
    {
        ScreenshotList.Items.Clear();
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
                ScreenshotList.Items.Add(new HistoryItemViewModel
                {
                    CacheItem = item,
                    Thumbnail = ImageHelper.MakeOpaque(image),
                    FullImage = image,
                    TimeDisplay = item.CreateTime.ToString("MM-dd HH:mm"),
                    SizeDisplay = image.PixelWidth + " × " + image.PixelHeight
                });
            }
        }
    }

    private void TodoItem_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.Tag is string id)
            TodoStore.Instance.Complete(id);
    }

    private async void QuickAddTodo_Click(object sender, RoutedEventArgs e)
    {
        var input = new InputWindow(
            (FindResource("Lang_QuickAddTitle") as string) ?? "快速添加待办",
            (FindResource("Lang_QuickAddPrompt") as string) ?? "请输入标题:");
        input.Owner = this;
        if (input.ShowDialog() == true && !string.IsNullOrWhiteSpace(input.Value))
            await TodoStore.Instance.AddAsync(input.Value.Trim());
    }

    private void SessionSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionSelector.SelectedItem is ChatSession session)
            ChatPanel.SelectSession(session.Id);
    }

    private async void NewSessionBtn_Click(object sender, RoutedEventArgs e)
    {
        var session = await ChatPanel.CreateNewSessionAsync();
        LoadSessions();
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

    private void ScreenshotItem_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HistoryItemViewModel vm && vm.FullImage != null)
            new ImagePreviewWindow(vm.FullImage).ShowDialog();
    }

    
    private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Interactive controls handle their own clicks - only drag the window from non-interactive areas
        var source = e.OriginalSource as DependencyObject;
        while (source != null)
        {
            if (source is Button || source is ComboBox || source is CheckBox) return;
            if (source is TextBox || source is RichTextBox) return;
            if (source is ItemsControl items && !(items is ComboBox)) return;
            if (source is ScrollBar || source is Slider || source is InkCanvas) return;
            source = VisualTreeHelper.GetParent(source);
        }
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

}
