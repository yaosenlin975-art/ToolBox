using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ToolBox.Core.Todo;

namespace ToolBox.Views.Todo;

public partial class TodoDetailWindow : Window
{
    private readonly TodoItem item;
    private Action? onClosed;

    public TodoDetailWindow(TodoItem todoItem, Action? closedCallback = null)
    {
        InitializeComponent();
        item = todoItem;
        onClosed = closedCallback;
        Populate();
    }

    private void Populate()
    {
        Title.Text = item.Title;
        IsCompleted.IsChecked = item.IsCompleted;
        StatusText.Text = item.IsCompleted ? "已完成" : (item.IsTrashed ? "废弃" : "待办");

        PriorityText.Text = item.Priority switch
        {
            1 => "重要",
            2 => "紧急",
            _ => "普通"
        };

        if (item.DueDate.HasValue)
        {
            DueDateText.Text = item.DueDate.Value.ToLocalTime().ToString("yyyy-MM-dd");
            DueDateRow.Visibility = Visibility.Visible;
        }
        else
        {
            DueDateRow.Visibility = Visibility.Collapsed;
        }

        ProgressSlider.Value = item.Progress;
        ProgressText.Text = item.Progress + "%";

        DescriptionBox.Text = item.Description;

        // Subtasks
        SubTasksList.Children.Clear();
        var children = TodoStore.Instance.GetChildren(item.Id);
        SubTasksHeader.Visibility = children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var child in children)
            SubTasksList.Children.Add(CreateSubtaskRow(child));

        // Trash/Restore
        TrashBtn.Visibility = item.IsTrashed ? Visibility.Collapsed : Visibility.Visible;
        DeleteBtn.Visibility = item.IsTrashed ? Visibility.Visible : Visibility.Collapsed;
        RestoreBtn.Visibility = item.IsTrashed ? Visibility.Visible : Visibility.Collapsed;
        AddSubBtn.Visibility = item.IsTrashed ? Visibility.Collapsed : Visibility.Visible;
        FooterBorder.Visibility = item.IsTrashed ? Visibility.Visible : Visibility.Collapsed;
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
            var dlg = new TodoDetailWindow(child, () => Populate());
            dlg.Owner = this;
            dlg.ShowDialog();
        };
        row.MouseEnter += (s, e) => row.Background = (Brush)FindResource("BgHoverBrush");
        row.MouseLeave += (s, e) => row.Background = Brushes.Transparent;
        return row;
    }

    private async void SubCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.DataContext is TodoItem ci)
        {
            if (cb.IsChecked == true)
                await TodoStore.Instance.CompleteAsync(ci.Id);
            else
                await TodoStore.Instance.UncompleteAsync(ci.Id);
        }
    }

    private async void Completed_Changed(object sender, RoutedEventArgs e)
    {
        if (IsCompleted.IsChecked == true)
            await TodoStore.Instance.CompleteAsync(item.Id);
        else
            await TodoStore.Instance.UncompleteAsync(item.Id);
    }

    private async void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        await TodoStore.Instance.DeletePermanentlyAsync(item.Id);
        onClosed?.Invoke();
        Close();
    }

    private async void TrashBtn_Click(object sender, RoutedEventArgs e)
    {
        await TodoStore.Instance.TrashAsync(item.Id);
        onClosed?.Invoke();
        Close();
    }

    private async void RestoreBtn_Click(object sender, RoutedEventArgs e)
    {
        await TodoStore.Instance.RestoreAsync(item.Id);
        onClosed?.Invoke();
        Close();
    }

    private async void AddSub_Click(object sender, RoutedEventArgs e)
    {
        var input = new InputWindow("添加子任务", "请输入子任务标题:");
        input.Owner = this;
        input.ShowTodoFields();
        if (input.ShowDialog() == true && !string.IsNullOrWhiteSpace(input.Value))
        {
            await TodoStore.Instance.AddAsync(input.Value.Trim(), priority: input.Priority,
                category: item.Category, dueDate: input.DueDate, parentId: item.Id);
            Populate();
        }
    }

    private void ProgressSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int val = (int)e.NewValue;
        ProgressText.Text = val + "%";
        if (val != item.Progress)
            _ = TodoStore.Instance.UpdateAsync(item.Id, progress: val);
    }

    private async void Description_LostFocus(object sender, RoutedEventArgs e)
    {
        if (DescriptionBox.Text != item.Description)
            await TodoStore.Instance.UpdateAsync(item.Id, description: DescriptionBox.Text);
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        onClosed?.Invoke();
        base.OnClosed(e);
    }
}
