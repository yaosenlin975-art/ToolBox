using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ToolBox.Core.Todo;

namespace ToolBox.Views.Todo;

public partial class TodoWindow : Window
{
    private string currentFilter = "all";

    public TodoWindow()
    {
        InitializeComponent();
        TodoStore.Instance.ItemsChanged += () => Dispatcher.Invoke(LoadTodos);
        LoadTodos();
    }

    private void LoadTodos()
    {
        if (TodoStore.Instance == null || TodoList == null) return;
        var items = currentFilter switch
        {
            "pending" => TodoStore.Instance.GetPending(),
            "completed" => TodoStore.Instance.GetCompleted(),
            _ => TodoStore.Instance.GetAll()
        };
        TodoList.ItemsSource = items;
    }

    private void AddBtn_Click(object sender, RoutedEventArgs e)
    {
        AddTodo();
    }

    private void NewTodoInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddTodo();
    }

    private void AddTodo()
    {
        var text = NewTodoInput.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;
        TodoStore.Instance.Add(text);
        NewTodoInput.Text = "";
        LoadTodos();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (FilterAll.IsChecked == true) currentFilter = "all";
        else if (FilterPending.IsChecked == true) currentFilter = "pending";
        else if (FilterCompleted.IsChecked == true) currentFilter = "completed";
        LoadTodos();
    }

    private void DoneCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.DataContext is TodoItem item)
        {
            if (item.IsCompleted)
                TodoStore.Instance.Complete(item.Id);
            else
                TodoStore.Instance.Update(item.Id);
            LoadTodos();
        }
    }

    private void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            TodoStore.Instance.Delete(id);
            LoadTodos();
        }
    }
}
