﻿using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using ToolBox.Core.Llm;
using ToolBox.Core.Todo;
using ToolBox.Models;
using ToolBox.Services;
using ToolBox.Views.Todo;
using ToolBox.Core.Native;

namespace ToolBox.Views;

public partial class CompactToolboxWindow : Window
{
    private static readonly (double w, double h) TodoSize = (360, 480);
    private static readonly (double w, double h) ChatSize = (360, 480);
    private static readonly (double w, double h) ScreenshotSize = (360, 480);
    private const double TodoItemHeight = 26;
    private const double TodoCollapsedPad = 12;
    private const double MaxFitWidth = 360;
    private const double MaxFitHeight = 480;

    private readonly ToolBoxOption options;
    private string currentTab = "todo";
    private bool isTodoCollapsed;
    private double savedWidth;
    private double savedHeight;
    private TodoItem? detailItem;
    private Point dragOrigin;
    private bool dragStarted;

    public CompactToolboxWindow()
    {
        InitializeComponent();
        options = ToolBoxOption.Load();

        PositionWindow();
        SwitchToTab("todo");
        LoadTodos();

        TodoStore.Instance.ItemsChanged += () => Dispatcher.BeginInvoke(LoadTodos);
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
        if (isTodoCollapsed)
        {
            isTodoCollapsed = false;
            TopBar.Visibility = Visibility.Visible;
            TodoAddBtn.Visibility = Visibility.Visible;
            CollapsedDragHandle.Visibility = Visibility.Collapsed;
            TodoCollapseBtn.Content = "\u25BC";
        }
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
            "assistant" => ChatSize,
            "screenshot" => ScreenshotSize,
            "screenshots" => ScreenshotSize,
            "history" => ScreenshotSize,
            _ => TodoSize
        };

        var right = Left + Width;
        var bottom = Top + Height;
        Width = w;
        Height = h;
        Left = right - w;
        Top = bottom - h;

        if (tab == "chat" || tab == "assistant")
            RootBorder.Opacity = 1.0;
        else
            RootBorder.Opacity = options.Data.CompactOpacity / 100.0;

        switch (tab)
        {
            case "todo":
            case "todos":
                TodoPanel.Visibility = Visibility.Visible;
                TabTodo.Style = (Style)FindResource("CompactTabButtonActive");
                break;
            case "chat":
            case "assistant":
                ChatPanel.Visibility = Visibility.Visible;
                TabChat.Style = (Style)FindResource("CompactTabButtonActive");
                SessionRow.Visibility = Visibility.Visible;
                break;
            case "screenshot":
            case "screenshots":
            case "history":
                ScreenshotPanel.Visibility = Visibility.Visible;
                TabScreenshot.Style = (Style)FindResource("CompactTabButtonActive");
                LoadScreenshots();
                break;
            case "settings":
                OpenWorkbench();
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

    // --- Todo tree with category drawers ---

    private void LoadTodos()
    {
        if (TodoItemsPanel == null) return;
        TodoItemsPanel.Children.Clear();

        var grouped = TodoStore.Instance.GetGroupedTree(pendingOnly: true);
        foreach (var (category, roots) in grouped)
        {
            var display = category == "\u9ED8\u8BA4" ? "\u672A\u5206\u7C7B" : category;
            var drawer = new Expander
            {
                IsExpanded = true,
                Margin = new Thickness(0, 0, 0, 4),
                Background = (Brush)FindResource("BgElevatedBrush"),
                BorderBrush = (Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(1),
                Tag = category,
            };
            drawer.SetResourceReference(StyleProperty, "DrawerExpander");

            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 0) };
            header.Children.Add(new TextBlock
            {
                Text = display, FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextPrimaryBrush"), VerticalAlignment = VerticalAlignment.Center
            });
            header.Children.Add(new TextBlock
            {
                Text = roots.Count.ToString(), FontSize = 10, Margin = new Thickness(6, 0, 0, 0),
                Foreground = (Brush)FindResource("TextTertiaryBrush"), VerticalAlignment = VerticalAlignment.Center
            });
            drawer.Header = header;

            var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
            foreach (var item in roots)
                AddCompactItemAndChildren(panel, item, 0);
            drawer.Content = panel;
            TodoItemsPanel.Children.Add(drawer);
        }

        FitTodoHeight();
    }

    private void AddCompactItemAndChildren(StackPanel panel, TodoItem item, int depth)
    {
        panel.Children.Add(CreateCompactItemRow(item, depth));
        foreach (var child in TodoStore.Instance.GetChildren(item.Id))
            AddCompactItemAndChildren(panel, child, depth + 1);
    }

    private Border CreateCompactItemRow(TodoItem item, int depth)
    {
        var grid = new Grid { Cursor = Cursors.Hand };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var cb = new CheckBox
        {
            IsChecked = item.IsCompleted, Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center, DataContext = item
        };
        cb.SetResourceReference(StyleProperty, "ModernCheckBox");
        cb.Checked += CompactTodoCheck_Changed;
        cb.Unchecked += CompactTodoCheck_Changed;
        Grid.SetColumn(cb, 0);
        grid.Children.Add(cb);

        if (item.Priority > 0 && !item.IsCompleted)
        {
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 6, Height = 6, Margin = new Thickness(0, 0, 6, 0),
                Fill = (Brush)FindResource("AccentBrush"), VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(dot, 1);
            grid.Children.Add(dot);
        }

        var title = new TextBlock
        {
            Text = item.Title, FontSize = 12,
            Foreground = item.IsCompleted ? (Brush)FindResource("TextTertiaryBrush") : (Brush)FindResource("TextPrimaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
        };
        if (item.IsCompleted) title.TextDecorations = TextDecorations.Strikethrough;
        Grid.SetColumn(title, 2);
        grid.Children.Add(title);

        var rightStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        if (item.Progress > 0)
        {
            var pbar = new ProgressBar
            {
                Value = item.Progress, Width = 24, Height = 4,
                Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center
            };
            pbar.SetResourceReference(StyleProperty, "HealthBar");
            rightStack.Children.Add(pbar);
        }
        if (item.Priority > 0 && !item.IsCompleted)
        {
            var priColor = item.Priority == 2 ? "#E74C3C" : "#F5A623";
            var priText = item.Priority == 2 ? "\u7D27\u6025" : "\u91CD\u8981";
            var badge = new Border
            {
                CornerRadius = new CornerRadius(3), Padding = new Thickness(3, 0, 3, 0),
                Margin = new Thickness(0, 0, 4, 0),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(priColor + "22"))
            };
            var badgeText = new TextBlock
            {
                Text = priText, FontSize = 9, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(priColor))
            };
            badge.Child = badgeText;
            rightStack.Children.Add(badge);
        }
        if (item.DueDate.HasValue)
        {
            rightStack.Children.Add(new TextBlock
            {
                Text = item.DueDate.Value.ToLocalTime().ToString("MM-dd"), FontSize = 10,
                Foreground = (Brush)FindResource("TextTertiaryBrush"), VerticalAlignment = VerticalAlignment.Center
            });
        }
        Grid.SetColumn(rightStack, 3);
        grid.Children.Add(rightStack);

        var row = new Border
        {
            Padding = new Thickness(10, 4, 10, 4), Cursor = Cursors.Hand, DataContext = item,
            Background = Brushes.Transparent, CornerRadius = new CornerRadius(4),
            Margin = new Thickness(depth * 16, 1, 0, 1)
        };
        row.Child = grid;

        row.MouseLeftButtonUp += (s, e) =>
        {
            if (e.OriginalSource is CheckBox) return;
            ShowDetail(item);
        };
        row.MouseEnter += (s, e) => row.Background = (Brush)FindResource("BgHoverBrush");
        row.MouseLeave += (s, e) => row.Background = Brushes.Transparent;
        return row;
    }

    private async void CompactTodoCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.DataContext is TodoItem item)
        {
            if (cb.IsChecked == true)
                await TodoStore.Instance.CompleteAsync(item.Id);
            else
                await TodoStore.Instance.UncompleteAsync(item.Id);
        }
    }

    private void ShowDetail(TodoItem item)
    {
        var dlg = new TodoDetailWindow(item);
        dlg.Owner = this;
        dlg.ShowDialog();
    }
    // --- Drag support via Preview events ---

    private void RootBorder_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source != null)
        {
            if (source is Button || source is ComboBox || source is CheckBox) return;
            if (source is TextBox || source is RichTextBox) return;
            if (source is Slider || source is InkCanvas) return;
            if (source is DatePicker) return;
            if (source is ScrollBar) return;
            source = VisualTreeHelper.GetParent(source);
        }
        dragOrigin = e.GetPosition(this);
        dragStarted = false;
    }

    private void RootBorder_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (dragStarted) return;
        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - dragOrigin.X) >= SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(pos.Y - dragOrigin.Y) >= SystemParameters.MinimumVerticalDragDistance)
        {
            dragStarted = true;
            DragMove();
        }
    }

    // --- Legacy handler kept for collapsed drag handle area ---
    private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source != null)
        {
            if (source is Button || source is ComboBox || source is CheckBox) return;
            if (source is TextBox || source is RichTextBox) return;
            source = VisualTreeHelper.GetParent(source);
        }
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    // --- Todo collapse / expand ---

    private void TodoCollapse_Click(object sender, RoutedEventArgs e)
    {
        isTodoCollapsed = !isTodoCollapsed;

        TopBar.Visibility = isTodoCollapsed ? Visibility.Collapsed : Visibility.Visible;
        TodoAddBtn.Visibility = isTodoCollapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapsedDragHandle.Visibility = isTodoCollapsed ? Visibility.Visible : Visibility.Collapsed;
        TodoCollapseBtn.Content = isTodoCollapsed ? "\u25B2" : "\u25BC";

        if (isTodoCollapsed)
        {
            savedWidth = Width;
            savedHeight = Height;
            FitTodoHeight();
        }
        else
        {
            var bottom = Top + Height;
            var right = Left + Width;
            Width = savedWidth > 0 ? savedWidth : TodoSize.w;
            Height = savedHeight > 0 ? savedHeight : TodoSize.h;
            Top = bottom - Height;
            Left = right - Width;
        }
    }

    private void FitTodoHeight()
    {
        if (!isTodoCollapsed || currentTab != "todo") return;

        int count = TodoItemsPanel?.Children.Count ?? 0;
        if (count == 0) count = 1;
        double targetH = count * TodoItemHeight + TodoCollapsedPad;
        targetH = Math.Max(targetH, MinHeight);
        targetH = Math.Min(targetH, MaxFitHeight);

        double targetW = MaxFitWidth;
        if ((TodoItemsPanel?.Children.Count ?? 0) == 0) targetW = TodoSize.w;

        var bottom = Top + Height;
        var right = Left + Width;
        Width = targetW;
        Height = targetH;
        Top = bottom - targetH;
        Left = right - targetW;
    }

    private async void QuickAddTodo_Click(object sender, RoutedEventArgs e)
    {
        var input = new InputWindow(
            (FindResource("Lang_QuickAddTitle") as string) ?? "\u5FEB\u901F\u6DFB\u52A0\u5F85\u529E",
            (FindResource("Lang_QuickAddPrompt") as string) ?? "\u8BF7\u8F93\u5165\u6807\u9898:");
        input.Owner = this;
        input.ShowTodoFields();
        if (input.ShowDialog() == true && !string.IsNullOrWhiteSpace(input.Value))
            await TodoStore.Instance.AddAsync(input.Value.Trim(), priority: input.Priority, category: input.Category, dueDate: input.DueDate);
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
                    SizeDisplay = image.PixelWidth + " \u00D7 " + image.PixelHeight
                });
            }
        }
    }
    private void ResizeGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (PresentationSource.FromVisual(this) is HwndSource hwndSource)
        {
            NativeMethods.SendMessage(hwndSource.Handle, NativeMethods.WM_SYSCOMMAND, (IntPtr)0xF008, IntPtr.Zero);
        }
    }
}