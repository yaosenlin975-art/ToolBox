using System.Windows;
using System.Windows.Input;

namespace ToolBox.Views;

public partial class CloseDialogWindow : Window
{
    public CloseDialogResult Result { get; private set; } = CloseDialogResult.Cancel;

    public CloseDialogWindow()
    {
        InitializeComponent();
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        Result = CloseDialogResult.Minimize;
        DialogResult = true;
        Close();
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        Result = CloseDialogResult.Exit;
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Result = CloseDialogResult.Cancel;
        DialogResult = false;
        Close();
    }
}

public enum CloseDialogResult
{
    Minimize,
    Exit,
    Cancel
}
