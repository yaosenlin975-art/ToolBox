using System.Windows;
using System.Windows.Controls;

namespace ToolBox.Views.Chat;

public partial class ToolCallCard : UserControl
{
    private bool isExpanded;

    public static readonly DependencyProperty IsCompactModeProperty =
        DependencyProperty.Register(nameof(IsCompactMode), typeof(bool), typeof(ToolCallCard),
            new PropertyMetadata(false, OnIsCompactModeChanged));

    public bool IsCompactMode
    {
        get => (bool)GetValue(IsCompactModeProperty);
        set => SetValue(IsCompactModeProperty, value);
    }

    private static void OnIsCompactModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ToolCallCard card)
            card.ApplyCompactStyles();
    }

    private void ApplyCompactStyles()
    {
        var compact = IsCompactMode;
        Margin = compact ? new Thickness(8, 2, 8, 2) : new Thickness(16, 4, 16, 4);
        MaxWidth = compact ? double.PositiveInfinity : 500;
        CardBorder.Padding = compact ? new Thickness(8, 6, 8, 6) : new Thickness(10, 8, 10, 8);
    }

    public ToolCallCard()
    {
        InitializeComponent();
    }

    public void SetToolCall(string name, string arguments, string? result, bool isError)
    {
        ToolNameText.Text = name;
        ArgsText.Text = $"{FindResource("LLM.ToolCall.Params")}{arguments}";
        if (result != null)
        {
            ResultText.Text = isError ? $"{FindResource("LLM.ToolCall.Error")}{result}" : $"{FindResource("LLM.ToolCall.Result")}{result}";
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
