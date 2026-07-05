using System.Windows;
using System.Windows.Controls;

namespace ToolBox.Views.Chat;

public partial class ToolCallCard : UserControl
{
    private bool isExpanded;

    public ToolCallCard()
    {
        InitializeComponent();
    }

    public void SetToolCall(string name, string arguments, string? result, bool isError)
    {
        ToolNameText.Text = name;
        ArgsText.Text = $"参数: {arguments}";
        if (result != null)
        {
            ResultText.Text = isError ? $"错误: {result}" : $"结果: {result}";
            StatusIcon.Text = isError ? " ✗" : " ✓";
        }
    }

    private void ToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        isExpanded = !isExpanded;
        DetailPanel.Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed;
        StatusIcon.Text = isExpanded ? " ▾" : " ▸";
    }
}
