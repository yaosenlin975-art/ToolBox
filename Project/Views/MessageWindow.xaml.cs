using System.Windows;
using System.Windows.Input;

namespace ToolBox.Views;

public partial class MessageWindow : Window
{
    public MessageWindow(string title, string message)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void OnOk(object sender, RoutedEventArgs e) => Close();

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    public static void Show(string title, string message, Window? owner = null)
    {
        var win = new MessageWindow(title, message);
        if (owner != null) win.Owner = owner;
        win.ShowDialog();
    }
}
