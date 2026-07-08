using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ToolBox.Models;

namespace ToolBox.Views;

public partial class CompactScrapWindow : Window
{
    private ScrapWindow parentScrap;
    private bool isDragging;
    private System.Windows.Point dragStartPoint;
    private double startLeft;
    private double startTop;

    public CompactScrapWindow(ScrapWindow scrap)
    {
        parentScrap = scrap;
        InitializeComponent();
        LoadScrapImage();
    }

    private void LoadScrapImage()
    {
        var source = parentScrap.ImageViewSource;
        if (source == null) return;

        var maxSize = 50;
        var scale = Math.Min((double)maxSize / source.PixelWidth, (double)maxSize / source.PixelHeight);
        var width = (int)(source.PixelWidth * scale);
        var height = (int)(source.PixelHeight * scale);

        var group = new TransformedBitmap(source,
            new ScaleTransform(scale, scale));

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen())
        {
            ctx.DrawImage(group, new Rect(0, 0, width, height));
        }
        rtb.Render(visual);
        rtb.Freeze();

        compactImage.Source = rtb;
        Width = width + 10;
        Height = height + 10;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 基于增量拖动：记录按下点与窗口初始位置，Move 时按位移差平滑移动，避免抓取偏离中心跳动。
        dragStartPoint = e.GetPosition(this);
        startLeft = parentScrap.Left;
        startTop = parentScrap.Top;
        isDragging = true;
        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!isDragging) return;

        var p = e.GetPosition(this);
        parentScrap.Left = startLeft + (p.X - dragStartPoint.X);
        parentScrap.Top = startTop + (p.Y - dragStartPoint.Y);
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (isDragging)
        {
            isDragging = false;
            ReleaseMouseCapture();
        }
    }

    private void OnMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        parentScrap.Show();
        parentScrap.Activate();
        Close();
    }
}
