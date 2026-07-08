using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ToolBox.Views;

/// <summary>
/// 全屏遮罩式图片预览窗：深色半透明背景，单击关闭，双击原图以点击点为中心裁切 50×50 小图，
/// 双击小图还原为完整图。
/// </summary>
public partial class ImagePreviewWindow : Window
{
    private bool isCropMode;
    private BitmapSource? fullSource;
    private System.Windows.Size originalSize;
    private System.Windows.Point originalPosition;
    private System.Windows.Threading.DispatcherTimer? clickTimer;

    public ImagePreviewWindow(BitmapSource image)
    {
        InitializeComponent();

        var area = SystemParameters.WorkArea;
        MaxWidth = area.Width * 0.9;
        MaxHeight = area.Height * 0.9;
        previewImage.MaxWidth = area.Width * 0.9 - 48;
        previewImage.MaxHeight = area.Height * 0.9 - 48;

        previewImage.Source = image;
    }

    
    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // DoubleClick -> toggle crop; single click -> delayed close (gives double-click time to cancel)
        if (e.ClickCount >= 2)
        {
            clickTimer?.Stop();
            clickTimer = null;
            ToggleCrop(e);
            e.Handled = true;
            return;
        }
        clickTimer?.Stop();
        clickTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval =TimeSpan.FromMilliseconds(GetDoubleClickTime())
        };
        clickTimer.Tick += (_, _) =>
        {
            clickTimer?.Stop();
            clickTimer = null;
            Close();
        };
        clickTimer.Start();
    }


    private void ToggleCrop(MouseButtonEventArgs e)
    {
        if (previewImage.Source is not BitmapSource src) return;

        if (!isCropMode)
        {
            fullSource = src;
            originalSize = new System.Windows.Size(Width, Height);
            originalPosition = new System.Windows.Point(Left, Top);

            var click = e.GetPosition(previewImage);
            double scale = previewImage.ActualWidth > 0
                ? src.PixelWidth / previewImage.ActualWidth
                : 1.0;
            double sx = click.X * scale;
            double sy = click.Y * scale;

            const int crop = 50;
            int x = (int)Math.Clamp(sx - crop / 2, 0, src.PixelWidth - crop);
            int y = (int)Math.Clamp(sy - crop / 2, 0, src.PixelHeight - crop);

            var cropped = new CroppedBitmap(src, new Int32Rect(x, y, crop, crop));
            cropped.Freeze();

            previewBorder.Margin = new Thickness(0);
            previewImage.Source = cropped;
            MaxWidth = double.PositiveInfinity;
            MaxHeight = double.PositiveInfinity;

            // 保持点击点在光标下：让 50×50 小图中心对齐点击点的屏幕坐标
            var screen = previewImage.PointToScreen(click);
            Left = screen.X - crop / 2;
            Top = screen.Y - crop / 2;

            isCropMode = true;
        }
        else
        {
            previewImage.Source = fullSource;
            previewBorder.Margin = new Thickness(24);
            var area = SystemParameters.WorkArea;
            MaxWidth = area.Width * 0.9;
            MaxHeight = area.Height * 0.9;
            previewImage.MaxWidth = area.Width * 0.9 - 48;
            previewImage.MaxHeight = area.Height * 0.9 - 48;
            Width = originalSize.Width;
            Height = originalSize.Height;
            Left = originalPosition.X;
            Top = originalPosition.Y;
            isCropMode = false;
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Close();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetDoubleClickTime();
}
