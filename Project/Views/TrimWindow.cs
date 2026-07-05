using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ToolBox.Models;

namespace ToolBox.Views;

public partial class TrimWindow : Window
{
    private ScrapWindow parentScrap;
    private bool isDragging;
    private System.Windows.Point startPoint;
    private int trimX, trimY, trimW, trimH;

    public TrimWindow(ScrapWindow scrap)
    {
        parentScrap = scrap;
        InitializeComponent();
        LoadScrapImage();
    }

    private void LoadScrapImage()
    {
        var source = parentScrap.ImageViewSource;
        if (source == null) return;

        previewImage.Source = source;
        Width = source.PixelWidth + 20;
        Height = source.PixelHeight + 20;
    }

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            isDragging = true;
            startPoint = e.GetPosition(canvas);
            Canvas.SetLeft(selectionRect, startPoint.X);
            Canvas.SetTop(selectionRect, startPoint.Y);
            selectionRect.Visibility = Visibility.Visible;
        }
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (!isDragging) return;

        var currentPoint = e.GetPosition(canvas);
        var x = Math.Min(startPoint.X, currentPoint.X);
        var y = Math.Min(startPoint.Y, currentPoint.Y);
        var w = Math.Abs(currentPoint.X - startPoint.X);
        var h = Math.Abs(currentPoint.Y - startPoint.Y);

        Canvas.SetLeft(selectionRect, x);
        Canvas.SetTop(selectionRect, y);
        selectionRect.Width = w;
        selectionRect.Height = h;
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!isDragging) return;
        isDragging = false;

        var currentPoint = e.GetPosition(canvas);
        trimX = (int)Math.Min(startPoint.X, currentPoint.X);
        trimY = (int)Math.Min(startPoint.Y, currentPoint.Y);
        trimW = (int)Math.Abs(currentPoint.X - startPoint.X);
        trimH = (int)Math.Abs(currentPoint.Y - startPoint.Y);

        if (trimW > 5 && trimH > 5)
        {
            ApplyTrim();
            Close();
        }
    }

    private void ApplyTrim()
    {
        var source = parentScrap.ImageViewSource;
        if (source == null) return;

        var cropRect = new Int32Rect(trimX, trimY, trimW, trimH);
        var cropped = new CroppedBitmap(source, cropRect);
        cropped.Freeze();
        parentScrap.SetImage(cropped);
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
