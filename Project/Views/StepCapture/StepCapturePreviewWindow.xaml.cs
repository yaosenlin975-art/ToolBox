using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ToolBox.Views.StepCapture;

/// <summary>步骤捕获预览窗口 (P2-04)</summary>
public partial class StepCapturePreviewWindow : Window
{
    public StepCapturePreviewWindow()
    {
        Title = "步骤捕获预览";
        Width = 900;
        Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = (Brush)FindResource("BgBrush");

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var btnExportPng = new Button { Content = "导出所有 PNG", Style = (Style)FindResource("BtnPrimary"), Margin = new Thickness(0, 0, 8, 0) };
        btnExportPng.Click += BtnExportPng_Click;
        var btnStitch = new Button { Content = "拼接长图", Style = (Style)FindResource("OutlineButton"), Margin = new Thickness(0, 0, 8, 0) };
        btnStitch.Click += BtnStitch_Click;
        var btnClose = new Button { Content = "关闭", Style = (Style)FindResource("IconButton") };
        btnClose.Click += (s, e) => Close();
        btnPanel.Children.Add(btnExportPng);
        btnPanel.Children.Add(btnStitch);
        btnPanel.Children.Add(btnClose);
        Grid.SetRow(btnPanel, 0);

        var listPanel = new WrapPanel { Orientation = Orientation.Horizontal };
        var scroll = new ScrollViewer { Content = listPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1);

        grid.Children.Add(btnPanel);
        grid.Children.Add(scroll);
        Content = grid;
    }

    private void BtnExportPng_Click(object sender, RoutedEventArgs e)
    {
        // TODO: Export PNGs
        MessageBox.Show("导出功能待实现", "步骤捕获", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnStitch_Click(object sender, RoutedEventArgs e)
    {
        // TODO: Stitch images
        MessageBox.Show("拼接功能待实现", "步骤捕获", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
