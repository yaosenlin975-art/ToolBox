using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using ToolBox.Core.Native;

namespace ToolBox.Views;

public class ClickCaptureWindow : Window
{
    private Bitmap screenBitmap;

    public event Action<Bitmap, System.Windows.Point, System.Windows.Size> CaptureCompleted;

    public ClickCaptureWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        KeyDown += OnKeyDown;
    }

    public ClickCaptureWindow StartCapture()
    {
        CaptureFullScreen();
        Show();
        Activate();
        return this;
    }

    private void CaptureFullScreen()
    {
        // 以物理像素采集整屏，避免高 DPI 屏下截图从源头被降采样（详见 CaptureWindow）。
        var screenWidth = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
        var screenHeight = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
        if (screenWidth <= 0 || screenHeight <= 0)
        {
            screenWidth = (int)SystemParameters.PrimaryScreenWidth;
            screenHeight = (int)SystemParameters.PrimaryScreenHeight;
        }

        screenBitmap = new Bitmap(screenWidth, screenHeight);
        using (var g = Graphics.FromImage(screenBitmap))
        {
            g.CopyFromScreen(0, 0, 0, 0, new System.Drawing.Size(screenWidth, screenHeight));
        }
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var capturePoint = e.GetPosition(this);
        var screenPoint = PointToScreen(capturePoint);

        // screenBitmap 以物理像素采集，screenPoint 为 WPF 逻辑(DIP)坐标，
        // 需换算到物理像素再作为源矩形索引，保证裁剪区域与点击位置精确对应。
        double scaleX = screenBitmap.Width > 0 && SystemParameters.PrimaryScreenWidth > 0
            ? screenBitmap.Width / SystemParameters.PrimaryScreenWidth : 1.0;
        double scaleY = screenBitmap.Height > 0 && SystemParameters.PrimaryScreenHeight > 0
            ? screenBitmap.Height / SystemParameters.PrimaryScreenHeight : 1.0;

        var regionSize = 200;
        var startX = Math.Max(0, (int)(screenPoint.X * scaleX) - 100);
        var startY = Math.Max(0, (int)(screenPoint.Y * scaleY) - 100);

        var cropped = new Bitmap(regionSize, regionSize);
        using (var g = Graphics.FromImage(cropped))
        {
            g.DrawImage(screenBitmap,
                new Rectangle(0, 0, regionSize, regionSize),
                startX, startY, regionSize, regionSize,
                GraphicsUnit.Pixel);
        }

        CaptureCompleted?.Invoke(cropped, screenPoint, new System.Windows.Size(regionSize, regionSize));
        Close();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        screenBitmap?.Dispose();
        base.OnClosed(e);
    }
}
