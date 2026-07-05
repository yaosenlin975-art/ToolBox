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

        var maxSize = 100;
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
        isDragging = true;
        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!isDragging) return;

        var pos = e.GetPosition(this);
        parentScrap.Left += pos.X - Width / 2;
        parentScrap.Top += pos.Y - Height / 2;
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
