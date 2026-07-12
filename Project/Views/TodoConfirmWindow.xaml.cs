using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace ToolBox.Views;

public partial class TodoConfirmWindow : Window
{
    public class TodoCandidate
    {
        public string Title { get; set; } = string.Empty;
        public int Priority { get; set; }
    }

    private readonly ObservableCollection<TodoCandidate> items = new();

    public IReadOnlyList<TodoCandidate> ConfirmedItems { get; private set; } = [];

    public TodoConfirmWindow(List<TodoCandidate> candidates)
    {
        InitializeComponent();
        foreach (var c in candidates)
            items.Add(c);
        TodoListBox.ItemsSource = items;
    }

    private void AddItem_Click(object sender, RoutedEventArgs e)
    {
        items.Add(new TodoCandidate());
        TodoListBox.ScrollIntoView(items[items.Count - 1]);
    }

    private void RemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is TodoCandidate item)
            items.Remove(item);
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        ConfirmedItems = new List<TodoCandidate>(items);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
