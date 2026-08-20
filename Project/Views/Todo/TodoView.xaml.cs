using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Input;
using System.Windows.Media;
using ToolBox.Core.Todo;

namespace ToolBox.Views.Todo;

public partial class TodoView : UserControl
{
    private string currentFilter = "all";
    private TodoItem? selected;

    public TodoView()
    {
        InitializeComponent();
        TodoStore.Instance.ItemsChanged += () => Dispatcher.BeginInvoke(LoadTodos);
        LoadTodos();
    }

    private void LoadTodos()
    {
        if (DrawerContainer == null) return;
        DrawerContainer.Children.Clear();

        var sortByCompletion = currentFilter == "all";
        var grouped = currentFilter switch
        {
            "pending" => TodoStore.Instance.GetGroupedTree(pendingOnly: true),
            "completed" => GetGroupedCompleted(),
            "trashed" => GetGroupedTrashed(),
            "repeating" => GetGroupedRepeating(),
            _ => SortGroupedByCompletion(TodoStore.Instance.GetGroupedTree())
        };

        foreach (var (category, roots) in grouped)
        {
        var display = category == (string)FindResource("Lang_Todo_DefaultCategory") ? (string)FindResource("Lang_Todo_Uncategorized") : category;
            var drawer = CreateDrawer(display, roots, category, sortByCompletion);
            DrawerContainer.Children.Add(drawer);
        }

        TodoEmptyState.Visibility = DrawerContainer.Children.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static Dictionary<string, List<TodoItem>> SortGroupedByCompletion(Dictionary<string, List<TodoItem>> grouped)
    {
        var result = new Dictionary<string, List<TodoItem>>(grouped.Count);
        foreach (var (category, roots) in grouped)
            result[category] = OrderByCompletion(roots);
        return result;
    }

    // 同级内未完成在前、已完成在后；各组内部保持"优先级 → 截止日期"现有顺序
    private static List<TodoItem> OrderByCompletion(List<TodoItem> items)
        => items.OrderBy(t => t.IsCompleted)
                .ThenBy(t => t.Priority == 0 ? 3 : t.Priority)
                .ThenBy(t => t.DueDate ?? DateTime.MaxValue)
                .ToList();

    private Dictionary<string, List<TodoItem>> GetGroupedCompleted()
    {
        var result = new Dictionary<string, List<TodoItem>>();
        foreach (var cat in TodoStore.Instance.Categories)
        {
            var catItems = TodoStore.Instance.GetRoots(cat).Where(t => t.IsCompleted).ToList();
            if (catItems.Count > 0)
                result[cat] = catItems;
        }
        return result;
    }

    private Dictionary<string, List<TodoItem>> GetGroupedTrashed()
    {
        var result = new Dictionary<string, List<TodoItem>>();
        var trashed = TodoStore.Instance.GetTrashed();
        var grouped = trashed.GroupBy(t => t.Category);
        foreach (var g in grouped)
        {
            var display = g.Key == (string)FindResource("Lang_Todo_DefaultCategory") ? (string)FindResource("Lang_Todo_Uncategorized") : g.Key;
            result[display] = g.ToList();
        }
        return result;
    }

    private Dictionary<string, List<TodoItem>> GetGroupedRepeating()
    {
        var result = new Dictionary<string, List<TodoItem>>();
        var repeating = TodoStore.Instance.GetRepeating();
        foreach (var cat in TodoStore.Instance.Categories)
        {
            var catItems = repeating.Where(t => t.Category == cat).ToList();
            if (catItems.Count > 0)
            {
                var display = cat == (string)FindResource("Lang_Todo_DefaultCategory") ? (string)FindResource("Lang_Todo_Uncategorized") : cat;
                result[display] = catItems;
            }
        }
        // Uncategorized repeating items
        var uncategorized = repeating.Where(t => !TodoStore.Instance.Categories.Contains(t.Category)).ToList();
        if (uncategorized.Count > 0)
        {
            var display = uncategorized[0].Category == (string)FindResource("Lang_Todo_DefaultCategory") ? (string)FindResource("Lang_Todo_Uncategorized") : uncategorized[0].Category;
            result[display] = uncategorized;
        }
        return result;
    }

    private Expander CreateDrawer(string display, List<TodoItem> roots, string storeCategory, bool sortByCompletion)
    {
        var expander = new Expander
        {
            IsExpanded = true,
            Margin = new Thickness(0, 0, 0, 4),
            Background = (Brush)FindResource("BgElevatedBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            AllowDrop = true,
            Tag = storeCategory,
        };
        expander.SetResourceReference(StyleProperty, "DrawerExpander");
        expander.DragOver += Drawer_DragOver;
        expander.Drop += Drawer_Drop;

        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 0) };
        header.Children.Add(new TextBlock
        {
            Text = display, FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush"), VerticalAlignment = VerticalAlignment.Center
        });
        header.Children.Add(new TextBlock
        {
            Text = roots.Count.ToString(), FontSize = 11, Margin = new Thickness(8, 0, 0, 0),
            Foreground = (Brush)FindResource("TextTertiaryBrush"), VerticalAlignment = VerticalAlignment.Center
        });
        expander.Header = header;

        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var item in roots)
            AddItemAndChildren(panel, item, 0, sortByCompletion);
        expander.Content = panel;
        return expander;
    }

    private void Drawer_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(string)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Drawer_Drop(object sender, DragEventArgs e)
    {
        if (sender is not Expander exp) return;
        if (e.Data.GetData(typeof(string)) is not string itemId) return;
        var item = TodoStore.Instance.Items.FirstOrDefault(t => t.Id == itemId);
        if (item == null) return;
        var newCategory = exp.Tag as string ?? (string)FindResource("Lang_Todo_DefaultCategory");
        if (item.Category == newCategory && string.IsNullOrEmpty(item.ParentId)) return;
        item.ParentId = null;
        await TodoStore.Instance.UpdateAsync(itemId, category: newCategory);
        e.Handled = true;
    }

    private void AddItemAndChildren(StackPanel panel, TodoItem item, int depth, bool sortByCompletion)
    {
        panel.Children.Add(CreateItemRow(item, depth));
        var children = TodoStore.Instance.GetChildren(item.Id);
        if (sortByCompletion) children = OrderByCompletion(children);
        foreach (var child in children)
            AddItemAndChildren(panel, child, depth + 1, sortByCompletion);
    }

    private Border CreateItemRow(TodoItem item, int depth = 0)
    {
        var grid = new Grid { Cursor = Cursors.Hand };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var children = TodoStore.Instance.GetChildren(item.Id);
        var cb = new CheckBox
        {
            IsChecked = item.IsCompleted, Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center, DataContext = item,
            IsEnabled = !item.IsTrashed && (children.Count == 0 || children.All(c => c.IsCompleted))
        };
        cb.SetResourceReference(StyleProperty, "ModernCheckBox");
        cb.Checked += DoneCheck_Changed;
        cb.Unchecked += DoneCheck_Changed;
        Grid.SetColumn(cb, 0);
        grid.Children.Add(cb);

        if (item.Priority > 0 && !item.IsCompleted)
        {
            var dot = new Ellipse
            {
                Width = 8, Height = 8, Margin = new Thickness(0, 0, 10, 0),
                Fill = (Brush)FindResource("AccentBrush"), VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(dot, 1);
            grid.Children.Add(dot);
        }

        var titleText = item.IsRepeating ? "\U0001F504 " + item.Title : item.Title;
        var title = new TextBlock
        {
            Text = titleText, FontSize = 13,
            Foreground = item.IsCompleted ? (Brush)FindResource("TextTertiaryBrush") : (Brush)FindResource("TextPrimaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
        };
        if (item.IsCompleted) title.TextDecorations = TextDecorations.Strikethrough;
        Grid.SetColumn(title, 2);
        grid.Children.Add(title);

        var rightStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        if (item.Progress > 0)
        {
            rightStack.Children.Add(new TextBlock
            {
                Text = item.Progress + "%", FontSize = 10, FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextTertiaryBrush"), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });
        }
        if (item.Priority > 0)
        {
            var priColor = item.Priority == 2 ? "#E74C3C" : "#F5A623";
            var priText = item.Priority == 2 ? (string)FindResource("Lang_Todo_PriHigh") : (string)FindResource("Lang_Todo_PriMid");
            var badge = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(4, 1, 4, 1), Margin = new Thickness(0, 0, 4, 0), Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(priColor + "22")) };
            var badgeText = new TextBlock { Text = priText, FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(priColor)) };
            badge.Child = badgeText;
            rightStack.Children.Add(badge);
        }
        if (item.IsRepeating && item.RepeatConfig != null)
        {
            rightStack.Children.Add(new TextBlock
            {
                Text = $"\U0001F504 {item.RepeatConfig.ReminderHour:D2}:{item.RepeatConfig.ReminderMinute:D2}",
                FontSize = 11,
                Foreground = (Brush)FindResource("TextTertiaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            });
        }
        if (item.DueDate.HasValue)
        {
            rightStack.Children.Add(new TextBlock
            {
                Text = item.DueDate.Value.ToLocalTime().ToString("MM-dd"), FontSize = 11,
                Foreground = (Brush)FindResource("TextTertiaryBrush"), VerticalAlignment = VerticalAlignment.Center
            });
        }
        Grid.SetColumn(rightStack, 3);
        grid.Children.Add(rightStack);

        var row = new Border
        {
            Padding = new Thickness(12, 6, 12, 6), Cursor = Cursors.Hand, DataContext = item,
            Background = Brushes.Transparent, CornerRadius = new CornerRadius(6), Margin = new Thickness(depth * 20, 1, 0, 1)
        };
        row.Child = grid;

        Point dragStart = default;
        bool canDrag = false;
        row.PreviewMouseLeftButtonDown += (s, e) =>
        {
            if (e.OriginalSource is CheckBox) { canDrag = false; return; }
            dragStart = e.GetPosition(row);
            canDrag = true;
        };
        row.MouseMove += (s, e) =>
        {
            if (!canDrag || e.LeftButton != MouseButtonState.Pressed) return;
            var pos = e.GetPosition(row);
            if (Math.Abs(pos.X - dragStart.X) >= SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(pos.Y - dragStart.Y) >= SystemParameters.MinimumVerticalDragDistance)
            {
                canDrag = false;
                DragDrop.DoDragDrop(row, new DataObject(typeof(string), item.Id), DragDropEffects.Move);
            }
        };
        row.PreviewMouseLeftButtonUp += (s, e) =>
        {
            if (!canDrag) return;
            canDrag = false;
            if (e.OriginalSource is CheckBox) return;
            selected = item;
            ShowDetail(item);
        };

        row.MouseEnter += (s, e) => row.Background = (Brush)FindResource("BgHoverBrush");
        row.MouseLeave += (s, e) => row.Background = Brushes.Transparent;
        return row;
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (FilterAll?.IsChecked == true) currentFilter = "all";
        else if (FilterPending?.IsChecked == true) currentFilter = "pending";
        else if (FilterCompleted?.IsChecked == true) currentFilter = "completed";
        else if (FilterRepeating?.IsChecked == true) currentFilter = "repeating";
        else if (FilterTrashed?.IsChecked == true) currentFilter = "trashed";
        LoadTodos();
    }

    private async void DoneCheck_Changed(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is CheckBox cb && cb.DataContext is TodoItem item)
            {
                if (cb.IsChecked == true)
                    await TodoStore.Instance.CompleteAsync(item.Id);
                else
                    await TodoStore.Instance.UncompleteAsync(item.Id);
                RefreshDetailProgressIfAffected(item.Id);
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ToolBox] {ex.Message}"); }
    }

    // 勾选/取消勾选后，若详情面板选中的是该待办或其祖先，同步自动计算出的进度
    private void RefreshDetailProgressIfAffected(string changedId)
    {
        if (selected == null) return;
        var cur = TodoStore.Instance.Items.FirstOrDefault(t => t.Id == changedId);
        while (cur != null)
        {
            if (cur.Id == selected.Id)
            {
                // 刷新共享详情面板的进度显示
                DetailPanel.ShowDetail(selected);
                return;
            }
            cur = TodoStore.Instance.Items.FirstOrDefault(t => t.Id == cur.ParentId);
        }
    }

    private void ShowDetail(TodoItem item)
    {
        EmptyHint.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;
        DetailPanel.ShowDetail(item);
    }

    private void HideDetail()
    {
        EmptyHint.Visibility = Visibility.Visible;
        DetailPanel.Visibility = Visibility.Collapsed;
        DetailPanel.HideDetail();
    }

    private async void FloatingAddBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var input = new InputWindow((string)FindResource("Lang_Todo_AddTodoTitle"), (string)FindResource("Lang_Todo_AddTodoPrompt"));
            input.ShowTodoFields();
            if (await input.ShowNonModalAsync(Window.GetWindow(this)) && !string.IsNullOrWhiteSpace(input.Value))
            {
                if (input.RepeatFrequency.HasValue)
                {
                    _ = TodoStore.Instance.AddRepeatingAsync(
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
                    _ = TodoStore.Instance.AddAsync(input.Value.Trim(),
                        priority: input.Priority, category: input.Category, dueDate: input.DueDate);
                }
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ToolBox] {ex.Message}"); }
    }
}
