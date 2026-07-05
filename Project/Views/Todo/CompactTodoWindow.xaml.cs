using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using ToolBox.Core.Todo;
using ToolBox.Models;

namespace ToolBox.Views.Todo;

public partial class CompactTodoWindow : Window
{
    public CompactTodoWindow()
    {
        InitializeComponent();
        Left = SystemParameters.WorkArea.Width - 290;
        Top = 100;
        OpacitySlider.Value = ToolBoxOption.Load().Data.CompactOpacity;
        ApplyOpacity();
        TodoStore.Instance.ItemsChanged += OnItemsChanged;
        LoadTodos();
    }

    private void OnItemsChanged()
    {
        Dispatcher.Invoke(LoadTodos);
    }

    private void LoadTodos()
    {
        var pending = TodoStore.Instance.GetPending();
        TitleText.Text = "Todo (" + pending.Count + ")";
        TodoList.ItemsSource = pending;
    }

    private void QuickInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddTodo();
    }

    private void AddBtn_Click(object sender, RoutedEventArgs e)
    {
        AddTodo();
    }

    private void AddTodo()
    {
        var text = QuickInput.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;
        TodoStore.Instance.Add(text);
        QuickInput.Text = "";
        LoadTodos();
    }

    private void Item_StatusChanged(object sender, RoutedEventArgs e)
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

    private void OpacitySlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        ApplyOpacity();
        var opt = ToolBoxOption.Load();
        opt.Data.CompactOpacity = (int)OpacitySlider.Value;
        opt.Save();
    }

    private void ApplyOpacity()
    {
        RootBorder.Opacity = OpacitySlider.Value / 100.0;
    }

    private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DragMove();
    }
}
