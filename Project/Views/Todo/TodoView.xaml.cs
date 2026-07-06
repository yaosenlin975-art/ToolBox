using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ToolBox.Core.Todo;

namespace ToolBox.Views.Todo;

public partial class TodoView : UserControl
{
    private string currentFilter = "all";
    private TodoItem? selected;

    public TodoView()
    {
        InitializeComponent();
        TodoStore.Instance.ItemsChanged += () => Dispatcher.Invoke(LoadTodos);
        LoadTodos();
    }

    private void LoadTodos()
    {
        if (TodoList == null) return;
        var items = currentFilter switch
        {
            "pending" => TodoStore.Instance.GetPending(),
            "completed" => TodoStore.Instance.GetCompleted(),
            _ => TodoStore.Instance.GetAll()
        };
        TodoList.ItemsSource = items;
    }

    private void AddBtn_Click(object sender, RoutedEventArgs e) => _ = AddTodoAsync();

    private void NewTodoInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = AddTodoAsync();
    }

    private async Task AddTodoAsync()
    {
        var text = NewTodoInput.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;
        await TodoStore.Instance.AddAsync(text);
        NewTodoInput.Text = "";
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (FilterAll?.IsChecked == true) currentFilter = "all";
        else if (FilterPending?.IsChecked == true) currentFilter = "pending";
        else if (FilterCompleted?.IsChecked == true) currentFilter = "completed";
        LoadTodos();
    }

    private async void DoneCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.DataContext is TodoItem item)
        {
            await TodoStore.Instance.CompleteAsync(item.Id);
            LoadTodos();
        }
    }

    private void TodoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TodoList.SelectedItem is TodoItem item)
        {
            selected = item;
            ShowDetail(item);
        }
        else
        {
            selected = null;
            HideDetail();
        }
    }

    private void ShowDetail(TodoItem item)
    {
        EmptyHint.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;

        DetailTitle.Text = item.Title;
        DetailDesc.Text = item.Description;
        DetailStatusText.Text = item.IsCompleted ? "已完成" : "待办";
        DetailCreated.Text = item.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        DetailCompleted.Text = item.CompletedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";

        PriLow.IsChecked = item.Priority == 0;
        PriMid.IsChecked = item.Priority == 1;
        PriHigh.IsChecked = item.Priority == 2;
    }

    private void HideDetail()
    {
        EmptyHint.Visibility = Visibility.Visible;
        DetailPanel.Visibility = Visibility.Collapsed;
    }

    private async void DetailTitle_LostFocus(object sender, RoutedEventArgs e)
    {
        if (selected == null) return;
        var t = DetailTitle.Text.Trim();
        if (!string.IsNullOrEmpty(t) && t != selected.Title)
            await TodoStore.Instance.UpdateAsync(selected.Id, title: t);
    }

    private async void DetailDesc_LostFocus(object sender, RoutedEventArgs e)
    {
        if (selected == null) return;
        if (DetailDesc.Text != selected.Description)
            await TodoStore.Instance.UpdateAsync(selected.Id, description: DetailDesc.Text);
    }

    private async void Priority_Changed(object sender, RoutedEventArgs e)
    {
        if (selected == null) return;
        int p = PriHigh.IsChecked == true ? 2 : PriMid.IsChecked == true ? 1 : 0;
        if (p != selected.Priority)
            await TodoStore.Instance.UpdateAsync(selected.Id, priority: p);
    }

    private async void DetailDelete_Click(object sender, RoutedEventArgs e)
    {
        if (selected == null) return;
        await TodoStore.Instance.DeleteAsync(selected.Id);
        selected = null;
        HideDetail();
    }
}
