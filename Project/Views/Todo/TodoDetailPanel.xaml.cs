using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ToolBox.Core.Todo;

namespace ToolBox.Views.Todo;

public partial class TodoDetailPanel : UserControl
{
    private TodoItem? currentItem;
    private Action? onClosed;

    public TodoDetailPanel()
    {
        InitializeComponent();
        InitializeRepeatComboBoxes();
    }

    private void InitializeRepeatComboBoxes()
    {
        for (int i = 1; i <= 31; i++)
            DetailRepeatDayOfMonth.Items.Add(new ComboBoxItem { Content = i.ToString() });

        var monthNames = new[] { "1月", "2月", "3月", "4月", "5月", "6月", "7月", "8月", "9月", "10月", "11月", "12月" };
        foreach (var name in monthNames)
            DetailRepeatMonth.Items.Add(new ComboBoxItem { Content = name });

        RefreshYearlyDayItems();
        DetailRepeatMonth.SelectionChanged += (_, _) => RefreshYearlyDayItems();
    }

    private void RefreshYearlyDayItems()
    {
        int month = DetailRepeatMonth.SelectedIndex + 1;
        if (month < 1 || month > 12) month = 1;
        int maxDay = DateTime.DaysInMonth(DateTime.Now.Year, month);
        int prevSelected = DetailRepeatDay.SelectedIndex + 1;

        DetailRepeatDay.Items.Clear();
        for (int i = 1; i <= maxDay; i++)
            DetailRepeatDay.Items.Add(new ComboBoxItem { Content = i.ToString() });

        if (prevSelected > 0 && prevSelected <= maxDay)
            DetailRepeatDay.SelectedIndex = prevSelected - 1;
        else
            DetailRepeatDay.SelectedIndex = maxDay - 1;
    }

    public void ShowDetail(TodoItem item, Action? closedCallback = null)
    {
        currentItem = item;
        onClosed = closedCallback;
        Populate();
    }

    public void Refresh()
    {
        Populate();
    }

    public void HideDetail()
    {
        currentItem = null;
        DetailPanel.Visibility = Visibility.Collapsed;
    }

    private void Populate()
    {
        if (currentItem == null) return;
        var item = currentItem;

        DetailPanel.Visibility = Visibility.Visible;
        DetailTitle.Text = item.Title;
        DetailDesc.Text = item.Description;
        IsCompleted.IsChecked = item.IsCompleted;
        DetailStatusText.Text = item.IsCompleted ? "已完成" : (item.IsTrashed ? "废弃" : "待办");
        DetailCreated.Text = item.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        DetailCompleted.Text = item.CompletedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";

        PriLow.IsChecked = item.Priority == 0;
        PriMid.IsChecked = item.Priority == 1;
        PriHigh.IsChecked = item.Priority == 2;

        DetailDueDate.SelectedDate = item.DueDate?.ToLocalTime();
        DetailProgress.Value = item.Progress;
        DetailProgressText.Text = item.Progress + "%";

        bool hasChildren = TodoStore.Instance.GetChildren(item.Id).Count > 0;
        DetailProgress.IsEnabled = !hasChildren;
        IsCompleted.IsEnabled = !hasChildren || TodoStore.Instance.GetChildren(item.Id).All(c => c.IsCompleted);
        DetailProgressText.Foreground = hasChildren
            ? (Brush)FindResource("TextTertiaryBrush")
            : (Brush)FindResource("TextSecondaryBrush");

        DetailCategory.Text = item.Category == (string)FindResource("Lang_Todo_DefaultCategory") ? "" : item.Category;

        DetailTrashBtn.Visibility = item.IsTrashed ? Visibility.Collapsed : Visibility.Visible;
        DetailRestoreBtn.Visibility = item.IsTrashed ? Visibility.Visible : Visibility.Collapsed;
        DeleteBtn.Visibility = item.IsTrashed ? Visibility.Visible : Visibility.Collapsed;

        if (item.RepeatConfig != null)
        {
            DetailRepeatSection.Visibility = Visibility.Visible;
            DetailRepeatFreq.SelectedIndex = (int)item.RepeatConfig.Frequency + 1;
            DetailRepeatTime.Text = $"{item.RepeatConfig.ReminderHour:D2}:{item.RepeatConfig.ReminderMinute:D2}";
            DetailRepeatEndDate.SelectedDate = item.RepeatConfig.EndDate?.Date;

            var config = item.RepeatConfig;
            if (config.RepeatDayOfWeek.HasValue && config.RepeatDayOfWeek.Value >= 1 && config.RepeatDayOfWeek.Value <= 7)
                DetailRepeatDayOfWeek.SelectedIndex = config.RepeatDayOfWeek.Value - 1;
            else
                DetailRepeatDayOfWeek.SelectedIndex = 0;

            if (config.RepeatDayOfMonth.HasValue && config.RepeatDayOfMonth.Value >= 1 && config.RepeatDayOfMonth.Value <= 31)
                DetailRepeatDayOfMonth.SelectedIndex = config.RepeatDayOfMonth.Value - 1;
            else
                DetailRepeatDayOfMonth.SelectedIndex = 0;

            if (config.RepeatMonth.HasValue && config.RepeatMonth.Value >= 1 && config.RepeatMonth.Value <= 12)
                DetailRepeatMonth.SelectedIndex = config.RepeatMonth.Value - 1;
            else
                DetailRepeatMonth.SelectedIndex = 0;

            RefreshYearlyDayItems();
            if (config.RepeatDay.HasValue && config.RepeatDay.Value >= 1 && config.RepeatDay.Value <= DetailRepeatDay.Items.Count)
                DetailRepeatDay.SelectedIndex = config.RepeatDay.Value - 1;
            else
                DetailRepeatDay.SelectedIndex = 0;
        }
        else
        {
            DetailRepeatSection.Visibility = Visibility.Visible;
            DetailRepeatFreq.SelectedIndex = 0;
            DetailRepeatTime.Text = "09:00";
            DetailRepeatEndDate.SelectedDate = null;
            DetailRepeatDayOfWeek.SelectedIndex = 0;
            DetailRepeatDayOfMonth.SelectedIndex = 0;
            DetailRepeatMonth.SelectedIndex = 0;
            RefreshYearlyDayItems();
            DetailRepeatDay.SelectedIndex = 0;
        }

        UpdateRepeatVisibility();
        LoadSubtasks();
    }

    private void LoadSubtasks()
    {
        if (currentItem == null) return;
        var children = TodoStore.Instance.GetChildren(currentItem.Id);
        SubTasksList.Children.Clear();
        SubTasksHeader.Visibility = children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var child in children)
            SubTasksList.Children.Add(CreateSubtaskRow(child));
    }

    private Border CreateSubtaskRow(TodoItem child)
    {
        var grid = new Grid { Cursor = Cursors.Hand };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var cb = new CheckBox
        {
            IsChecked = child.IsCompleted, Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center, DataContext = child,
            IsEnabled = !child.IsTrashed
        };
        cb.SetResourceReference(StyleProperty, "ModernCheckBox");
        cb.Checked += SubCheck_Changed;
        cb.Unchecked += SubCheck_Changed;
        Grid.SetColumn(cb, 0);
        grid.Children.Add(cb);

        var title = new TextBlock
        {
            Text = child.Title, FontSize = 12,
            Foreground = child.IsCompleted ? (Brush)FindResource("TextTertiaryBrush") : (Brush)FindResource("TextPrimaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
        };
        if (child.IsCompleted) title.TextDecorations = TextDecorations.Strikethrough;
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);

        var row = new Border
        {
            Padding = new Thickness(6, 3, 6, 3), Cursor = Cursors.Hand, DataContext = child,
            Background = Brushes.Transparent, CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 1, 0, 1)
        };
        row.Child = grid;

        row.MouseLeftButtonUp += (s, e) =>
        {
            if (e.OriginalSource is CheckBox) return;
            ShowDetail(child, () => Populate());
        };
        row.MouseEnter += (s, e) => row.Background = (Brush)FindResource("BgHoverBrush");
        row.MouseLeave += (s, e) => row.Background = Brushes.Transparent;
        return row;
    }

    private void UpdateRepeatVisibility()
    {
        if (DetailNonRepeatSection == null || DetailRepeatFieldsSection == null) return;
        var isRepeating = DetailRepeatFreq.SelectedIndex > 0;
        DetailNonRepeatSection.Visibility = isRepeating ? Visibility.Collapsed : Visibility.Visible;
        DetailRepeatFieldsSection.Visibility = isRepeating ? Visibility.Visible : Visibility.Collapsed;

        int freqIdx = DetailRepeatFreq.SelectedIndex;
        DetailWeeklyPanel.Visibility = freqIdx == 2 ? Visibility.Visible : Visibility.Collapsed;
        DetailMonthlyPanel.Visibility = freqIdx == 3 ? Visibility.Visible : Visibility.Collapsed;
        DetailYearlyPanel.Visibility = freqIdx == 4 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DetailRepeatFreq_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateRepeatVisibility();
    }

    private async void SubCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.DataContext is TodoItem ci)
        {
            if (cb.IsChecked == true)
                await TodoStore.Instance.CompleteAsync(ci.Id);
            else
                await TodoStore.Instance.UncompleteAsync(ci.Id);
            Populate();
        }
    }

    private async void Completed_Changed(object sender, RoutedEventArgs e)
    {
        if (currentItem == null) return;
        if (IsCompleted.IsChecked == currentItem.IsCompleted) return;
        if (IsCompleted.IsChecked == true)
            await TodoStore.Instance.CompleteAsync(currentItem.Id);
        else
            await TodoStore.Instance.UncompleteAsync(currentItem.Id);
        Populate();
    }

    private async void DetailTitle_LostFocus(object sender, RoutedEventArgs e)
    {
        if (currentItem == null) return;
        var t = DetailTitle.Text.Trim();
        if (!string.IsNullOrEmpty(t) && t != currentItem.Title)
            await TodoStore.Instance.UpdateAsync(currentItem.Id, title: t);
    }

    private async void DetailDesc_LostFocus(object sender, RoutedEventArgs e)
    {
        if (currentItem == null) return;
        if (DetailDesc.Text != currentItem.Description)
            await TodoStore.Instance.UpdateAsync(currentItem.Id, description: DetailDesc.Text);
    }

    private async void Priority_Changed(object sender, RoutedEventArgs e)
    {
        if (currentItem == null) return;
        int p = PriHigh.IsChecked == true ? 2 : PriMid.IsChecked == true ? 1 : 0;
        if (p != currentItem.Priority)
            await TodoStore.Instance.UpdateAsync(currentItem.Id, priority: p);
    }

    private async void DetailDueDate_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (currentItem == null) return;
        DateTime? val = DetailDueDate.SelectedDate?.ToUniversalTime();
        if (val != currentItem.DueDate)
            await TodoStore.Instance.UpdateAsync(currentItem.Id, dueDate: val);
    }

    private async void DetailProgress_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (currentItem == null || DetailProgressText == null) return;
        int val = (int)e.NewValue;
        DetailProgressText.Text = val + "%";
        if (TodoStore.Instance.GetChildren(currentItem.Id).Count > 0) return;
        if (val != currentItem.Progress)
            await TodoStore.Instance.UpdateAsync(currentItem.Id, progress: val);
    }

    private async void DetailRepeatSave_Click(object sender, RoutedEventArgs e)
    {
        if (currentItem == null) return;

        var freqIndex = DetailRepeatFreq.SelectedIndex;

        if (freqIndex == 0)
        {
            await TodoStore.Instance.SetRepeatConfigAsync(currentItem.Id, null);
        }
        else
        {
            var frequency = (ERepeatFrequency)(freqIndex - 1);

            var timeStr = DetailRepeatTime.Text.Trim();
            int hour = 9, minute = 0;
            if (TimeSpan.TryParse(timeStr, out var ts))
            {
                hour = ts.Hours;
                minute = ts.Minutes;
            }

            DateTime? endDate = DetailRepeatEndDate.SelectedDate;
            if (endDate.HasValue)
                endDate = endDate.Value.ToUniversalTime();

            var existingConfig = currentItem.RepeatConfig;
            var config = new RepeatConfig
            {
                Frequency = frequency,
                StartDate = existingConfig?.StartDate ?? DateTime.UtcNow.Date,
                EndDate = endDate,
                ReminderHour = Math.Clamp(hour, 0, 23),
                ReminderMinute = Math.Clamp(minute, 0, 59),
            };

            switch (frequency)
            {
                case ERepeatFrequency.Weekly:
                    config.RepeatDayOfWeek = DetailRepeatDayOfWeek.SelectedIndex + 1;
                    break;
                case ERepeatFrequency.Monthly:
                    config.RepeatDayOfMonth = DetailRepeatDayOfMonth.SelectedIndex + 1;
                    break;
                case ERepeatFrequency.Yearly:
                    config.RepeatMonth = DetailRepeatMonth.SelectedIndex + 1;
                    config.RepeatDay = DetailRepeatDay.SelectedIndex + 1;
                    break;
            }

            await TodoStore.Instance.SetRepeatConfigAsync(currentItem.Id, config);
        }

        Populate();
    }

    private async void DetailTrash_Click(object sender, RoutedEventArgs e)
    {
        if (currentItem == null) return;
        await TodoStore.Instance.TrashAsync(currentItem.Id);
        onClosed?.Invoke();
        HideDetail();
    }

    private async void DetailRestore_Click(object sender, RoutedEventArgs e)
    {
        if (currentItem == null) return;
        await TodoStore.Instance.RestoreAsync(currentItem.Id);
        onClosed?.Invoke();
        Populate();
    }

    private async void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (currentItem == null) return;
        await TodoStore.Instance.DeletePermanentlyAsync(currentItem.Id);
        onClosed?.Invoke();
        HideDetail();
    }

    private async void DetailCategory_LostFocus(object sender, RoutedEventArgs e)
    {
        CategorySuggestions.Visibility = Visibility.Collapsed;
        if (currentItem == null) return;
        var cat = DetailCategory.Text.Trim();
        cat = string.IsNullOrEmpty(cat) ? (string)FindResource("Lang_Todo_DefaultCategory") : cat;
        if (cat != currentItem.Category)
            await TodoStore.Instance.UpdateAsync(currentItem.Id, category: cat);
    }

    private void DetailCategory_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateCategorySuggestions(DetailCategory.Text.Trim());
    }

    private void CategorySuggestion_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (CategorySuggestions.SelectedItem is not string cat) return;
        DetailCategory.Text = cat;
        DetailCategory.CaretIndex = cat.Length;
        CategorySuggestions.Visibility = Visibility.Collapsed;
    }

    private void UpdateCategorySuggestions(string filter)
    {
        var cats = TodoStore.Instance.Categories
            .Where(c => c != (string)FindResource("Lang_Todo_DefaultCategory") && (string.IsNullOrEmpty(filter) || c.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (cats.Count == 0 || cats[0].Equals(filter, StringComparison.OrdinalIgnoreCase))
        {
            CategorySuggestions.Visibility = Visibility.Collapsed;
            return;
        }
        CategorySuggestions.ItemsSource = cats;
        CategorySuggestions.Visibility = Visibility.Visible;
    }

    private async void AddSubTodo_Click(object sender, RoutedEventArgs e)
    {
        if (currentItem == null) return;
        var input = new InputWindow("添加子任务", "请输入子任务标题:");
        input.ShowTodoFields();
        var owner = Window.GetWindow(this);
        if (await input.ShowNonModalAsync(owner) && !string.IsNullOrWhiteSpace(input.Value))
        {
            await TodoStore.Instance.AddAsync(input.Value.Trim(), priority: input.Priority,
                category: currentItem.Category, dueDate: input.DueDate, parentId: currentItem.Id);
            Populate();
        }
    }
}
