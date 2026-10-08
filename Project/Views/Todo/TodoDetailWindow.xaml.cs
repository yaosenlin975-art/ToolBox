using System.Windows;
using System.Windows.Input;
using ToolBox.Core.Todo;

namespace ToolBox.Views.Todo;

public partial class TodoDetailWindow : Window
{
    private readonly TodoItem item;
    private Action? onClosed;

    public TodoDetailWindow(TodoItem todoItem, Action? closedCallback = null)
    {
        InitializeComponent();
        DetailPanel.DismissRequested += Close;
        item = todoItem;
        onClosed = closedCallback;
        DetailPanel.ShowDetail(item, () => Populate());
    }

    private void Populate()
    {
        DetailPanel.Refresh();
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        onClosed?.Invoke();
        base.OnClosed(e);
    }
}
