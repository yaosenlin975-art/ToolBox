using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ToolBox.Views.NetworkDiagnosis;

/// <summary>
/// 修复前确认弹窗（PRD 5.5-1，复用 TodoConfirmWindow 确认弹窗范式）：
/// 列出本次将执行的修复动作、涉及错误码与提权标记，用户确认后才执行。
/// </summary>
public partial class RepairConfirmWindow : Window
{
    public RepairConfirmWindow(IReadOnlyList<RepairStep> steps)
    {
        InitializeComponent();
        TitleText.Text = L("Lang_Net_ConfirmTitle");
        MessageText.Text = string.Format(L("Lang_Net_ConfirmMessage"), steps.Count);
        foreach (var step in steps)
            PlanList.Children.Add(BuildRow(step));
        BtnConfirm.Content = L("Lang_Net_ConfirmExecute");
        BtnCancel.Content = L("Lang_Net_ConfirmCancel");
    }

    private Grid BuildRow(RepairStep step)
    {
        var grid = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new TextBlock
        {
            Text = "\uE73E",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        dot.SetResourceReference(TextBlock.ForegroundProperty, "SuccessBrush");
        Grid.SetColumn(dot, 0);
        grid.Children.Add(dot);

        var title = new TextBlock
        {
            Text = step.Title,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);

        var chips = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (step.ErrorCodes.Length > 0)
            chips.Children.Add(BuildChip(string.Join("/", step.ErrorCodes), "ErrorBrush", mono: true));
        if (step.RequiresElevation)
            chips.Children.Add(BuildChip(L("Lang_Net_RepairNeedAdmin"), "WarningBrush", mono: false));
        Grid.SetColumn(chips, 2);
        grid.Children.Add(chips);

        return grid;
    }

    private Border BuildChip(string text, string foregroundKey, bool mono)
    {
        var chip = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontFamily = mono ? (FontFamily)FindResource("FontMono") : (FontFamily)FindResource("FontSans"),
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        chip.SetResourceReference(BackgroundProperty, "BgElevatedBrush");
        chip.SetResourceReference(BorderBrushProperty, "BorderDefaultBrush");
        ((TextBlock)chip.Child).SetResourceReference(TextBlock.ForegroundProperty, foregroundKey);
        return chip;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnClose(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnTitleBarMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton == System.Windows.Input.MouseButton.Left) DragMove();
    }

    private string L(string key)
    {
        return (FindResource(key) as string) ?? key;
    }
}
