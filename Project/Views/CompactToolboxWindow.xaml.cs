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
    private bool dragArmed;
    private bool todoHeightDirty;

    // 靠边自动隐藏
    private enum EdgeState { Visible, HidePending, Aligning, Hiding, Hidden, Showing }
    private EdgeState edgeState;
    private DockSide dockSide;
    private DispatcherTimer? edgeTimer;
    private long edgeTransition;
    private bool isDragging;
    private bool isResizing;
    private HwndSource? windowSource;
    private const double EdgeThreshold = 12;
    private const double EdgeStripWidth = 12;
    private long sizeTransition;

    public CompactToolboxWindow()
    {
        InitializeComponent();
        options = ToolBoxOption.Load();

        PositionWindow();
        SwitchToTab("todo");
        LoadTodos();
        PreviewMouseDown += (_, _) => CancelEdgeWaitForInteraction();
        ContextMenuOpening += (_, _) => CancelEdgeWaitForInteraction();
        IsVisibleChanged += (_, _) => { StopSizeAnimation(true); ResetEdgePosition(); };
        SizeChanged += (_, _) =>
        {
            UpdateWorkAreaClip();
            if (edgeState != EdgeState.Visible && edgeState != EdgeState.HidePending)
                ResetEdgePosition();
        };
        LocationChanged += (_, _) => UpdateWorkAreaClip();
        SystemParameters.StaticPropertyChanged += SystemParametersChanged;

        TodoStore.Instance.ItemsChanged += () => Dispatcher.BeginInvoke(LoadTodos);
        CacheManager.Instance.OnScrapCached += (_, _) => Dispatcher.Invoke(LoadScreenshots);
        Core.Theming.ThemeManager.Instance.ThemeChanged += (_, _) => Dispatcher.BeginInvoke(LoadTodos);

    }

    public void ApplyCompactOpacity(int opacityPercent)
    {
        options.Data.CompactOpacity = opacityPercent;
        SwitchToTab(currentTab);
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (edgeState is EdgeState.Hidden or EdgeState.Aligning or EdgeState.Hiding or EdgeState.Showing)
            RestoreFromEdge();
        else if (edgeState == EdgeState.HidePending) CancelHideTimer();
    }

    // 同 WorkbenchWindow：实例被 App 永久缓存，真关闭后 Show()/Activate() 会抛 InvalidOperationException
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!Dispatcher.HasShutdownStarted)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
        CancelHideTimer();
        ++edgeTransition;
        StopSizeAnimation();
        StopPositionAnimations();
        SystemParameters.StaticPropertyChanged -= SystemParametersChanged;
        windowSource?.RemoveHook(WindowHook);
    }

    private void PositionWindow()
    {
        // WorkArea 是含偏移的工作区，只取 Width/Height 会在任务栏在上方/多屏时把窗口摆到屏外
        var area = SystemParameters.WorkArea;
        Left = area.Left + area.Width - Width - 20;
        Top = area.Top + area.Height - Height - 20;
    }

    public void SwitchToTab(string tab)
    {
        StopSizeAnimation();
        if (edgeState is EdgeState.Hidden or EdgeState.Aligning or EdgeState.Hiding or EdgeState.Showing)
            RestoreFromEdge();
        else CancelHideTimer();
        // settings 不是一个面板：记进 currentTab 后，之后每次刷新面板都会顺手打开工作台并隐藏小窗，
        // 而此刻两个面板已 Collapsed，从工作台切回来只会得到空白窗口
        if (tab == "settings")
        {
            OpenWorkbench();
            return;
        }
        bool tabChanged = currentTab != tab;
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
        ScreenshotPanel.Visibility = Visibility.Collapsed;

        TabTodo.Style = (Style)FindResource("CompactTabButton");
        TabScreenshot.Style = (Style)FindResource("CompactTabButton");

        var (w, h) = tab switch
        {
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

        RootBorder.Opacity = options.Data.CompactOpacity / 100.0;

        switch (tab)
        {
            case "todo":
            case "todos":
                TodoPanel.Visibility = Visibility.Visible;
                TabTodo.Style = (Style)FindResource("CompactTabButtonActive");
                break;
            case "screenshot":
            case "screenshots":
            case "history":
                ScreenshotPanel.Visibility = Visibility.Visible;
                TabScreenshot.Style = (Style)FindResource("CompactTabButtonActive");
                LoadScreenshots();
                break;
        }
        if (tabChanged) AnimateContent(tab is "todo" or "todos" ? TodoPanel : ScreenshotPanel);
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
        Hide();
        wb.Show();
        wb.Activate();
        wb.LoadPage("settings");
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
        dragArmed = false;
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
        dragArmed = true;
    }

    private void RootBorder_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (!dragArmed) return;
        if (dragStarted) return;
        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - dragOrigin.X) >= SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(pos.Y - dragOrigin.Y) >= SystemParameters.MinimumVerticalDragDistance)
        {
            dragStarted = true;
            DragWindow();
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
            DragWindow();
    }

    private void DragWindow()
    {
        StopSizeAnimation();
        ResetEdgePosition();
        isDragging = true;
        try { DragMove(); }
        finally { isDragging = false; SnapAfterDrag(); }
    }

    // --- Todo collapse / expand ---

    private void TodoCollapse_Click(object sender, RoutedEventArgs e)
    {
        StopSizeAnimation();
        isTodoCollapsed = !isTodoCollapsed;

        TopBar.Visibility = isTodoCollapsed ? Visibility.Collapsed : Visibility.Visible;
        TodoAddBtn.Visibility = isTodoCollapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapsedDragHandle.Visibility = isTodoCollapsed ? Visibility.Visible : Visibility.Collapsed;
        TodoCollapseBtn.Content = isTodoCollapsed ? "\u25B2" : "\u25BC";

        if (isTodoCollapsed)
        {
            savedWidth = Width;
            savedHeight = Height;
            FitTodoHeight(true);
        }
        else
        {
            AnimateSize(savedWidth > 0 ? savedWidth : TodoSize.w,
                savedHeight > 0 ? savedHeight : TodoSize.h, true);
        }
    }

    private void FitTodoHeight(bool animate = false)
    {
        if (!isTodoCollapsed || currentTab != "todo") return;
        if (edgeState == EdgeState.Hidden) { todoHeightDirty = true; return; }
        todoHeightDirty = false;

        int count = TodoItemsPanel?.Children.Count ?? 0;
        if (count == 0) count = 1;
        double targetH = count * TodoItemHeight + TodoCollapsedPad;
        targetH = Math.Max(targetH, MinHeight);
        targetH = Math.Min(targetH, MaxFitHeight);

        double targetW = MaxFitWidth;
        if ((TodoItemsPanel?.Children.Count ?? 0) == 0) targetW = TodoSize.w;

        AnimateSize(targetW, targetH, animate);
    }

    private void StopSizeAnimation(bool settle = false)
    {
        ++sizeTransition;
        double height = settle ? (double)GetAnimationBaseValue(HeightProperty) : Height;
        double top = settle ? (double)GetAnimationBaseValue(TopProperty) : Top;
        BeginAnimation(HeightProperty, null);
        BeginAnimation(TopProperty, null);
        Height = height;
        Top = top;
    }

    private void AnimateSize(double width, double height, bool animate)
    {
        if (edgeState is EdgeState.Aligning or EdgeState.Hiding or EdgeState.Showing) ResetEdgePosition();
        StopSizeAnimation();
        CancelHideTimer();
        double startHeight = Height, startTop = Top;
        double targetTop = dockSide == DockSide.Top ? Top : Top + Height - height;
        double targetLeft = dockSide is DockSide.Left or DockSide.Top ? Left : Left + Width - width;
        Width = width;
        Left = targetLeft;
        Height = height;
        Top = targetTop;
        if (!animate || !SystemParameters.ClientAreaAnimation) return;
        long revision = sizeTransition;
        var duration = TimeSpan.FromMilliseconds(180);
        var heightAnimation = new DoubleAnimation(startHeight, height, duration)
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        heightAnimation.Completed += (_, _) =>
        {
            if (revision != sizeTransition) return;
            BeginAnimation(HeightProperty, null);
            BeginAnimation(TopProperty, null);
            ResetEdgePosition();
        };
        BeginAnimation(HeightProperty, heightAnimation);
        BeginAnimation(TopProperty, new DoubleAnimation(startTop, targetTop, duration)
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
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

    private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);
    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        windowSource?.AddHook(WindowHook);
    }

    private IntPtr WindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // DPI/work-area changes are processed after WPF has applied the native update.
        if (message is 0x02E0 or 0x007E or 0x001A)
            Dispatcher.BeginInvoke(ResetEdgePosition);
        if (message == 0x0231) { isResizing = true; StopSizeAnimation(); ResetEdgePosition(); }
        if (message == 0x0232) { isResizing = false; Dispatcher.BeginInvoke(SnapAfterDrag); }
        return IntPtr.Zero;
    }

    private void SystemParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
            Dispatcher.BeginInvoke(() =>
            {
                ResetEdgePosition();
                StopSizeAnimation();
                AnimateContent(currentTab is "todo" or "todos" ? TodoPanel : ScreenshotPanel);
            });
    }

    private bool TryGetScreenGeometry(out Rect work, out Rect window, out double scaleX, out double scaleY)
    {
        work = window = Rect.Empty;
        var dpi = VisualTreeHelper.GetDpi(this);
        scaleX = dpi.DpiScaleX;
        scaleY = dpi.DpiScaleY;
        var hwnd = new WindowInteropHelper(this).Handle;
        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info) ||
            !NativeMethods.GetWindowRect(hwnd, out var nativeWindow)) return false;
        work = new Rect(info.rcWork.Left, info.rcWork.Top,
            info.rcWork.Right - info.rcWork.Left, info.rcWork.Bottom - info.rcWork.Top);
        window = new Rect(nativeWindow.Left, nativeWindow.Top,
            nativeWindow.Right - nativeWindow.Left, nativeWindow.Bottom - nativeWindow.Top);
        return !work.IsEmpty && work.Width > 0 && work.Height > 0;
    }

    private bool IsOuterDock(Rect work, Rect window, DockSide side)
    {
        var monitors = new List<Rect>();
        bool success = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return false;
            monitors.Add(new Rect(info.rcMonitor.Left, info.rcMonitor.Top,
                info.rcMonitor.Right - info.rcMonitor.Left, info.rcMonitor.Bottom - info.rcMonitor.Top));
            return true;
        }, IntPtr.Zero);
        return success && EdgeDockGeometry.IsOuterEdge(window, work, side, monitors);
    }

    public void ApplyAutoHide(bool enabled)
    {
        options.Data.AutoHideEnabled = enabled;
        if (!enabled) ResetEdgePosition();
        else SnapAfterDrag();
    }

    private bool HasActiveInteraction() => isDragging || isResizing || Mouse.Captured != null ||
        Mouse.LeftButton == MouseButtonState.Pressed ||
        (IsKeyboardFocusWithin && Keyboard.FocusedElement is TextBoxBase) ||
        OwnedWindows.Cast<Window>().Any(w => w.IsVisible) ||
        ContextMenu is { IsOpen: true } || HasOpenContextMenu(RootBorder);

    private static bool HasOpenContextMenu(DependencyObject parent)
    {
        if (parent is FrameworkElement { ContextMenu.IsOpen: true }) return true;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (HasOpenContextMenu(VisualTreeHelper.GetChild(parent, i))) return true;
        return false;
    }

    private bool PointerInsideWindow()
    {
        return TryGetScreenGeometry(out _, out var window, out _, out _) &&
            NativeMethods.GetCursorPos(out var point) && window.Contains(point.X, point.Y);
    }

    private void CancelHideTimer()
    {
        edgeTimer?.Stop();
        edgeTimer = null;
        if (edgeState == EdgeState.HidePending) edgeState = EdgeState.Visible;
    }

    private void StartEdgeTimer(int milliseconds, Action action)
    {
        CancelHideTimer();
        long revision = edgeTransition;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!ReferenceEquals(edgeTimer, timer) || revision != edgeTransition) return;
            edgeTimer = null;
            action();
        };
        edgeTimer = timer;
        timer.Start();
    }

    private void StartHideTimer()
    {
        if (!options.Data.AutoHideEnabled || !IsVisible || dockSide == DockSide.None ||
            edgeState is not (EdgeState.Visible or EdgeState.HidePending)) return;
        StartEdgeTimer(800, HideWindow);
        edgeState = EdgeState.HidePending;
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        if (edgeState == EdgeState.Hidden) CancelHideTimer();
        else StartHideTimer();
    }

    private void Window_MouseEnter(object sender, MouseEventArgs e)
    {
        if (edgeState == EdgeState.Hiding) ShowWindow();
        else if (edgeState == EdgeState.Aligning) CancelEdgeMotion();
        else if (edgeState != EdgeState.Hidden) CancelHideTimer();
    }

    private void HiddenStrip_MouseEnter(object sender, MouseEventArgs e)
    {
        if (edgeState == EdgeState.Hidden)
            StartEdgeTimer(120, () => { if (HiddenStrip.IsMouseOver) ShowWindow(); });
        else if (edgeState is EdgeState.Aligning or EdgeState.Hiding) ShowWindow();
    }

    private void HiddenStrip_MouseLeave(object sender, MouseEventArgs e)
    {
        if (edgeState == EdgeState.Hidden) CancelHideTimer();
    }

    private void HiddenStrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ShowWindow();
        e.Handled = true;
    }

    private void SnapAfterDrag()
    {
        if (isDragging || isResizing || !IsVisible || edgeState is EdgeState.Hidden or EdgeState.Hiding) return;
        CancelHideTimer();
        if (!TryGetScreenGeometry(out var work, out var window, out var scaleX, out var scaleY)) return;
        dockSide = EdgeDockGeometry.FindSide(window, work, EdgeThreshold * scaleX, EdgeThreshold * scaleY);
        if (dockSide != DockSide.None && !IsOuterDock(work, window, dockSide)) dockSide = DockSide.None;
        if (dockSide == DockSide.None || !options.Data.AutoHideEnabled) return;
        StartEdgeTimer(800, AlignAfterDelay);
        edgeState = EdgeState.HidePending;
    }

    private void AlignAfterDelay()
    {
        if (edgeState != EdgeState.HidePending) return;
        if (isDragging || isResizing || !IsVisible || !options.Data.AutoHideEnabled)
        { edgeState = EdgeState.Visible; return; }
        if (!TryGetScreenGeometry(out var work, out var window, out var scaleX, out var scaleY))
        { edgeState = EdgeState.Visible; return; }
        dockSide = EdgeDockGeometry.FindSide(window, work, EdgeThreshold * scaleX, EdgeThreshold * scaleY);
        if (dockSide == DockSide.None || !IsOuterDock(work, window, dockSide))
        { dockSide = DockSide.None; edgeState = EdgeState.Visible; return; }
        if (HasActiveInteraction())
        {
            StartEdgeTimer(800, AlignAfterDelay);
            edgeState = EdgeState.HidePending;
            return;
        }
        MoveAtEdge(false, aligning: true);
    }

    private void ShowWindow()
    {
        if (edgeState is EdgeState.Hidden or EdgeState.Aligning or EdgeState.Hiding) MoveAtEdge(false);
    }

    private void HideWindow()
    {
        if (edgeState != EdgeState.HidePending) return;
        edgeState = EdgeState.Visible;
        if (!options.Data.AutoHideEnabled || !IsVisible || PointerInsideWindow()) return;
        if (HasActiveInteraction()) { StartHideTimer(); return; }
        if (!TryGetScreenGeometry(out var work, out var window, out var scaleX, out var scaleY)) return;
        var side = EdgeDockGeometry.FindSide(window, work, 2 * scaleX, 2 * scaleY);
        if (side == DockSide.None || side != dockSide || !IsOuterDock(work, window, side)) return;
        MoveAtEdge(true);
    }

    private void MoveAtEdge(bool hidden, bool aligning = false)
    {
        if (dockSide == DockSide.None) { ResetEdgePosition(); return; }
        long revision = ++edgeTransition;
        StopSizeAnimation();
        if (!TryGetScreenGeometry(out var work, out var window, out var scale, out var scaleY))
        { ResetEdgePosition(); return; }
        double currentLeft = Left, currentTop = Top;
        StopPositionAnimations();
        if (!TryGetScreenGeometry(out work, out window, out scale, out scaleY))
        { ResetEdgePosition(); return; }
        CancelHideTimer();
        double strip = EdgeStripWidth * (dockSide is DockSide.Left or DockSide.Right ? scale : scaleY);
        var targetPixels = EdgeDockGeometry.TargetPosition(work, window, dockSide, hidden, strip);
        double targetLeft = currentLeft + (targetPixels.X - window.Left) / scale;
        double targetTop = currentTop + (targetPixels.Y - window.Top) / scaleY;
        edgeState = hidden ? EdgeState.Hiding : aligning ? EdgeState.Aligning : EdgeState.Showing;
        HiddenStrip.Visibility = Visibility.Collapsed;
        RootBorder.Visibility = Visibility.Visible;
        RootBorder.IsHitTestVisible = true;
        UpdateWorkAreaClip();
        bool finished = false;
        void Finish()
        {
            if (revision != edgeTransition || finished) return;
            finished = true;
            BeginAnimation(LeftProperty, null);
            BeginAnimation(TopProperty, null);
            Left = targetLeft;
            Top = targetTop;
            edgeState = hidden ? EdgeState.Hidden : EdgeState.Visible;
            RootBorder.IsHitTestVisible = !hidden;
            RootBorder.Visibility = hidden ? Visibility.Hidden : Visibility.Visible;
            if (hidden) ConfigureHiddenStrip();
            HiddenStrip.Visibility = hidden ? Visibility.Visible : Visibility.Collapsed;
            UpdateWorkAreaClip();
            if (!hidden && todoHeightDirty) FitTodoHeight();
            if (aligning && !PointerInsideWindow())
            {
                edgeState = EdgeState.HidePending;
                HideWindow();
            }
            else if (!hidden && !aligning && !PointerInsideWindow()) StartHideTimer();
        }
        double distance = Math.Max(Math.Abs(targetLeft - currentLeft), Math.Abs(targetTop - currentTop));
        double span = Math.Max(1, dockSide is DockSide.Left or DockSide.Right
            ? Width - EdgeStripWidth : Height - EdgeStripWidth);
        double ratio = Math.Clamp(distance / span, 0.2, 1);
        var duration = TimeSpan.FromMilliseconds((hidden ? 180 : 220) * ratio);
        int pending = 0;
        void AddAnimation(DependencyProperty property, double current, double target)
        {
            if (Math.Abs(current - target) < 0.5) return;
            pending++;
            var animation = new DoubleAnimation(current, target, duration)
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            animation.Completed += (_, _) => { if (--pending == 0) Finish(); };
            BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
        }
        if (!SystemParameters.ClientAreaAnimation) { Finish(); return; }
        AddAnimation(LeftProperty, currentLeft, targetLeft);
        AddAnimation(TopProperty, currentTop, targetTop);
        if (pending == 0) Finish();
    }

    private void RestoreFromEdge() => ResetEdgePosition();

    private void CancelEdgeMotion()
    {
        CancelHideTimer();
        ++edgeTransition;
        StopPositionAnimations();
        edgeState = EdgeState.Visible;
        RootBorder.IsHitTestVisible = true;
        RootBorder.Visibility = Visibility.Visible;
        HiddenStrip.Visibility = Visibility.Collapsed;
        UpdateWorkAreaClip();
    }

    private void CancelEdgeWaitForInteraction()
    {
        if (edgeState == EdgeState.Aligning) CancelEdgeMotion();
        else if (edgeState == EdgeState.HidePending) CancelHideTimer();
    }

    private void StopPositionAnimations()
    {
        double left = Left, top = Top;
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        Left = left;
        Top = top;
    }

    private void ConfigureHiddenStrip()
    {
        bool horizontalEdge = dockSide is DockSide.Top or DockSide.Bottom;
        HiddenStrip.Width = horizontalEdge ? double.NaN : EdgeStripWidth;
        HiddenStrip.Height = horizontalEdge ? EdgeStripWidth : double.NaN;
        HiddenStrip.HorizontalAlignment = dockSide switch
        {
            DockSide.Left => HorizontalAlignment.Right,
            DockSide.Right => HorizontalAlignment.Left,
            _ => HorizontalAlignment.Stretch
        };
        HiddenStrip.VerticalAlignment = dockSide switch
        {
            DockSide.Top => VerticalAlignment.Bottom,
            DockSide.Bottom => VerticalAlignment.Top,
            _ => VerticalAlignment.Stretch
        };
        StripBar.Width = horizontalEdge ? 48 : 6;
        StripBar.Height = horizontalEdge ? 6 : 48;
        StripArrow.Text = dockSide switch
        {
            DockSide.Left => "\u25B6",
            DockSide.Right => "\u25C0",
            DockSide.Top => "\u25BC",
            DockSide.Bottom => "\u25B2",
            _ => ""
        };
    }

    private void UpdateWorkAreaClip()
    {
        if (RootGrid == null) return;
        if (edgeState is not (EdgeState.Aligning or EdgeState.Hiding or EdgeState.Hidden or EdgeState.Showing) ||
            !TryGetScreenGeometry(out var work, out var window, out var scaleX, out var scaleY))
        {
            RootGrid.Clip = null;
            return;
        }
        var clip = new Rect(
            Math.Max(0, (work.Left - window.Left) / scaleX),
            Math.Max(0, (work.Top - window.Top) / scaleY),
            Math.Max(0, Math.Min(RootGrid.ActualWidth, (work.Right - window.Left) / scaleX) -
                Math.Max(0, (work.Left - window.Left) / scaleX)),
            Math.Max(0, Math.Min(RootGrid.ActualHeight, (work.Bottom - window.Top) / scaleY) -
                Math.Max(0, (work.Top - window.Top) / scaleY)));
        RootGrid.Clip = new RectangleGeometry(clip);
    }

    private void ResetEdgePosition()
    {
        CancelHideTimer();
        ++edgeTransition;
        bool needsRestore = edgeState is EdgeState.Aligning or EdgeState.Hidden or EdgeState.Hiding or EdgeState.Showing;
        StopSizeAnimation();
        StopPositionAnimations();
        edgeState = EdgeState.Visible;
        if (RootBorder == null) return;
        RootBorder.IsHitTestVisible = true;
        RootBorder.Visibility = Visibility.Visible;
        HiddenStrip.Visibility = Visibility.Collapsed;
        if (!TryGetScreenGeometry(out var work, out var window, out var sx, out var sy))
        { if (needsRestore) PositionWindow(); return; }
        var target = EdgeDockGeometry.TargetPosition(work, window, needsRestore ? dockSide : DockSide.None, false, 0);
        Left += (target.X - window.Left) / sx;
        Top += (target.Y - window.Top) / sy;
        dockSide = EdgeDockGeometry.FindSide(new Rect(target.X, target.Y, window.Width, window.Height), work, 2 * sx, 2 * sy);
        UpdateWorkAreaClip();
        if (todoHeightDirty) FitTodoHeight();
    }

    private static void AnimateContent(FrameworkElement content)
    {
        content.BeginAnimation(OpacityProperty, null);
        if (content.RenderTransform is not TranslateTransform transform)
            content.RenderTransform = transform = new TranslateTransform();
        transform.BeginAnimation(TranslateTransform.YProperty, null);
        content.Opacity = 1;
        transform.Y = 0;
        if (!SystemParameters.ClientAreaAnimation) return;
        content.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { FillBehavior = FillBehavior.Stop });
        transform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(5, 0, TimeSpan.FromMilliseconds(160))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
    }

    #endregion
}
