using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ToolBox.Core.Llm;
using ToolBox.Core.Todo;
using ToolBox.Models;
using ToolBox.Services;
using ToolBox.Views.Todo;
using System.Runtime.InteropServices;
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

    // 靠边自动隐藏
    private bool isHiddenAtEdge;
    private bool isHideCountingDown;
    private DispatcherTimer? hideTimer;
    private bool isAnimating;

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
        Core.Theming.ThemeManager.Instance.ThemeChanged += (_, _) => Dispatcher.BeginInvoke(LoadTodos);

        Loaded += (_, _) => SetupAutoHide();
    }

    public void ApplyCompactOpacity(int opacityPercent)
    {
        options.Data.CompactOpacity = opacityPercent;
        SwitchToTab(currentTab);
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (isHiddenAtEdge)
            RestoreFromEdge();
    }

    private void PositionWindow()
    {
        Left = SystemParameters.WorkArea.Width - Width - 20;
        Top = SystemParameters.WorkArea.Height - Height - 20;
    }

    public void SwitchToTab(string tab)
    {
        if (isHiddenAtEdge)
            RestoreFromEdge();
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
                LoadSessions();
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

        // ── 今日重复分组（排在最前） ──
        var dueRepeating = TodoStore.Instance.GetDueRepeating();
        if (dueRepeating.Count > 0)
        {
            var header = new TextBlock
            {
                Text = (FindResource("Lang_TodayRepeating") as string) ?? "今日重复",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextTertiaryBrush"),
                Margin = new Thickness(8, 4, 0, 2)
            };
            TodoItemsPanel.Children.Add(header);

            foreach (var item in dueRepeating)
            {
                var row = CreateCompactItemRow(item, 0);
                TodoItemsPanel.Children.Add(row);
            }
        }

        // ── 分类分组（排除重复项，避免重复显示） ──
        var grouped = TodoStore.Instance.GetGroupedTree(pendingOnly: true);
        // Filter out repeating items from category drawers
        var repeatingIds = new HashSet<string>(dueRepeating.Select(i => i.Id));
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
            {
                if (repeatingIds.Contains(item.Id)) continue;
                AddCompactItemAndChildren(panel, item, 0);
            }
            drawer.Content = panel;
            TodoItemsPanel.Children.Add(drawer);
        }

        FitTodoHeight();
    }

    private void AddCompactItemAndChildren(StackPanel panel, TodoItem item, int depth)
    {
        panel.Children.Add(CreateCompactItemRow(item, depth));
        var children = TodoStore.Instance.GetChildren(item.Id, pendingOnly: true);
        foreach (var child in children)
            AddCompactItemAndChildren(panel, child, depth + 1);
    }

    private Border CreateCompactItemRow(TodoItem item, int depth)
    {
        var grid = new Grid { Cursor = Cursors.Hand };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var children = TodoStore.Instance.GetChildren(item.Id);
        var cb = new CheckBox
        {
            IsChecked = item.IsCompleted, Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center, DataContext = item,
            IsEnabled = !item.IsTrashed && (children.Count == 0 || children.All(c => c.IsCompleted))
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

        var titleText = item.IsRepeating ? "\U0001F504 " + item.Title : item.Title;
        var title = new TextBlock
        {
            Text = titleText, FontSize = 12,
            Foreground = item.IsCompleted ? (Brush)FindResource("TextTertiaryBrush") : (Brush)FindResource("TextPrimaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
        };
        if (item.IsCompleted) title.TextDecorations = TextDecorations.Strikethrough;
        Grid.SetColumn(title, 2);
        grid.Children.Add(title);

        var rightStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        if (item.Priority > 0 && !item.IsCompleted)
        {
            var priText = item.Priority == 2 ? "\u7D27\u6025" : "\u91CD\u8981";
            var priBrushKey = item.Priority == 2 ? "ErrorBrush" : "WarningBrush";
            var priSoftBrushKey = item.Priority == 2 ? "ErrorSoftBrush" : "WarningSoftBrush";
            var badge = new Border
            {
                CornerRadius = new CornerRadius(3), Padding = new Thickness(3, 0, 3, 0),
                Margin = new Thickness(0, 0, 4, 0),
            };
            badge.SetResourceReference(Border.BackgroundProperty, priSoftBrushKey);
            var badgeText = new TextBlock
            {
                Text = priText, FontSize = 9, FontWeight = FontWeights.SemiBold,
            };
            badgeText.SetResourceReference(TextBlock.ForegroundProperty, priBrushKey);
            badge.Child = badgeText;
            rightStack.Children.Add(badge);
        }
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
        try
        {
            if (sender is CheckBox cb && cb.DataContext is TodoItem item)
            {
                if (cb.IsChecked == true)
                    await TodoStore.Instance.CompleteAsync(item.Id);
                else
                    await TodoStore.Instance.UncompleteAsync(item.Id);
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ToolBox] {ex.Message}"); }
    }

    private void ShowDetail(TodoItem item)
    {
        var dlg = new TodoDetailWindow(item);
        dlg.Owner = this;
        dlg.Show();
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
            CheckEdgeAfterDrag();
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
        try
        {
            var input = new InputWindow(
                (FindResource("Lang_QuickAddTitle") as string) ?? "\u5FEB\u901F\u6DFB\u52A0\u5F85\u529E",
                (FindResource("Lang_QuickAddPrompt") as string) ?? "\u8BF7\u8F93\u5165\u6807\u9898:");
            input.Topmost = true; // stay visible over compact window
            input.ShowTodoFields();
            if (await input.ShowNonModalAsync(this) && !string.IsNullOrWhiteSpace(input.Value))
            {
                if (input.RepeatFrequency.HasValue)
                {
                    await TodoStore.Instance.AddRepeatingAsync(
                        input.Value.Trim(),
                        input.RepeatFrequency.Value,
                        priority: input.Priority,
                        category: input.Category,
                        dueDate: input.DueDate,
                        reminderHour: input.ReminderHour,
                        reminderMinute: input.ReminderMinute,
                        repeatDayOfWeek: input.RepeatDayOfWeek,
                        repeatDayOfMonth: input.RepeatDayOfMonth,
                        repeatMonth: input.RepeatMonth,
                        repeatDay: input.RepeatDay);
                }
                else
                {
                    await TodoStore.Instance.AddAsync(input.Value.Trim(), priority: input.Priority, category: input.Category, dueDate: input.DueDate);
                }
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ToolBox] {ex.Message}"); }
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

    #region Auto-hide at screen edge

    private Rect GetCurrentMonitorWorkArea()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var hMonitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
        NativeMethods.GetMonitorInfo(hMonitor, ref mi);
        var r = mi.rcWork;
        return new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    private void SetupAutoHide()
    {
        if (!options.Data.AutoHideEnabled) return;
        RootBorder.MouseLeave += (_, _) =>
        {
            if (!options.Data.AutoHideEnabled || isHiddenAtEdge || isAnimating) return;
            StartHideTimer();
        };
    }

    private void CheckEdgeAfterDrag()
    {
        if (!options.Data.AutoHideEnabled || isHiddenAtEdge) return;
        var workArea = GetCurrentMonitorWorkArea();
        bool atLeft = Left <= workArea.Left + 2;
        bool atRight = Left + Width >= workArea.Right - 2;
        if (atLeft || atRight)
            StartHideTimer();
    }

    private void StartHideTimer()
    {
        hideTimer?.Stop();
        isHideCountingDown = true;
        hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        hideTimer.Tick += (_, _) =>
        {
            hideTimer?.Stop();
            isHideCountingDown = false;
            HideWindow();
        };
        hideTimer.Start();
    }

    private void CancelHideTimer()
    {
        hideTimer?.Stop();
        hideTimer = null;
        isHideCountingDown = false;
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!options.Data.AutoHideEnabled || isHiddenAtEdge || isAnimating) return;
        StartHideTimer();
    }

    private void HiddenStrip_MouseEnter(object sender, MouseEventArgs e)
    {
        if (isAnimating) return;
        CancelHideTimer();
        ShowWindow();
    }

    private void ShowWindow()
    {
        if (isAnimating || !isHiddenAtEdge) return;
        isAnimating = true;
        CancelHideTimer();

        var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(300))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };

        var transform = RenderTransform as TranslateTransform;
        if (transform == null)
        {
            transform = new TranslateTransform();
            RenderTransform = transform;
        }

        transform.BeginAnimation(TranslateTransform.XProperty, anim);
        HiddenStrip.Visibility = Visibility.Collapsed;
        DispatcherTimer? done = null;
        done = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(310) };
        done.Tick += (_, _) =>
        {
            done?.Stop();
            isHiddenAtEdge = false;
            isAnimating = false;
        };
        done.Start();
    }

    private void RestoreFromEdge()
    {
        CancelHideTimer();
        if (!isHiddenAtEdge) return;
        isHiddenAtEdge = false;
        isAnimating = false;
        var transform = RenderTransform as TranslateTransform;
        if (transform != null)
            transform.BeginAnimation(TranslateTransform.XProperty, null);
        HiddenStrip.Visibility = Visibility.Collapsed;
    }

    private void HideWindow()
    {
        if (isAnimating || isHiddenAtEdge) return;
        isAnimating = true;

        double stripWidth = 20;
        double targetX = -(Width - stripWidth);

        var anim = new DoubleAnimation(targetX, TimeSpan.FromMilliseconds(300))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };

        var transform = RenderTransform as TranslateTransform;
        if (transform == null)
        {
            transform = new TranslateTransform();
            RenderTransform = transform;
        }

        transform.BeginAnimation(TranslateTransform.XProperty, anim);

        var workArea = GetCurrentMonitorWorkArea();
        bool atLeft = Left <= workArea.Left + 2;
        StripArrow.Text = atLeft ? "\u25B6" : "\u25C0";
        HiddenStrip.Visibility = Visibility.Visible;

        DispatcherTimer? done = null;
        done = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(310) };
        done.Tick += (_, _) =>
        {
            done?.Stop();
            isHiddenAtEdge = true;
            isAnimating = false;
        };
        done.Start();
    }

    #endregion
}
