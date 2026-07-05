using System.Windows;
using System.Windows.Input;

namespace ToolBox.Views;

public partial class InputWindow : Window
{
    public string Value { get; private set; } = "";

    public InputWindow(string title, string prompt)
    {
        InitializeComponent();
        Title = title;
        TitleDisplay.Text = title;
        Prompt.Text = prompt;
        Input.Focus();
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Value = Input.Text;
            DialogResult = true;
        }
        else if (e.Key == Key.Escape)
        {
            DialogResult = false;
        }
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        Value = Input.Text;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnClose(object sender, RoutedEventArgs e) => DialogResult = false;
}
