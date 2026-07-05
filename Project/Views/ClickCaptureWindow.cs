using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

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
        var screenWidth = (int)SystemParameters.PrimaryScreenWidth;
        var screenHeight = (int)SystemParameters.PrimaryScreenHeight;

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

        var regionSize = 200;
        var startX = Math.Max(0, (int)screenPoint.X - 100);
        var startY = Math.Max(0, (int)screenPoint.Y - 100);

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
