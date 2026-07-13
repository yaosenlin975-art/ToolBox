using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ToolBox.Models;
using ToolBox.Core.Native;

namespace ToolBox.Views.Capture;

public partial class CaptureWindow : Window
{
    private Bitmap imgSnap;
    private int ptStartX, ptStartY;
    private int ptEndX, ptEndY;
    private bool isDragging;

    private const int HWND_TOPMOST = -1;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, int hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    public Bitmap CapturedBitmap { get; private set; }
    public System.Windows.Point CaptureStart { get; private set; }
    public System.Windows.Size CaptureSize { get; private set; }

    public event Action<Bitmap, System.Windows.Point, System.Windows.Size> CaptureCompleted;
    public event Action CaptureCancelled;

    public CaptureWindow()
    {
        InitializeComponent();
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        MouseMove += OnMouseMove;
        KeyDown += OnKeyDown;
        MouseRightButtonDown += OnMouseRightButtonDown;
        Deactivated += OnDeactivated;
    }

    private void ForceTopmost()
    {
        var helper = new WindowInteropHelper(this);
        SetWindowPos(helper.Handle, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    private void OnDeactivated(object sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() => ForceTopmost()));
    }

    public CaptureWindow StartCapture()
    {
        Topmost = true;
        CaptureFullScreen();
        Show();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ForceTopmost();
            Activate();
        }));
        return this;
    }

    private void CaptureFullScreen()
    {
        // 以物理像素采集整屏：GetSystemMetrics(SM_CXSCREEN/CYSCREEN) 返回主屏物理像素尺寸，
        // 不受 DPI 虚拟化影响。配合进程 DPI 感知（App 启动时 SetProcessDPIAware），
        // CopyFromScreen 使用物理坐标，从源头保证截图为物理像素级，不被降采样。
        var screenWidth = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
        var screenHeight = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
        if (screenWidth <= 0 || screenHeight <= 0)
        {
            screenWidth = (int)SystemParameters.PrimaryScreenWidth;
            screenHeight = (int)SystemParameters.PrimaryScreenHeight;
        }

        imgSnap = new Bitmap(screenWidth, screenHeight);
        using (var g = Graphics.FromImage(imgSnap))
        {
            g.CopyFromScreen(0, 0, 0, 0, new System.Drawing.Size(screenWidth, screenHeight));
        }

        var hBitmap = imgSnap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            previewImage.Source = source;
        }
        finally
        {
            DeleteObject(hBitmap);
        }
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(this);
        ptStartX = (int)pos.X;
        ptStartY = (int)pos.Y;
        isDragging = true;
        selectionRect.Visibility = Visibility.Visible;
        selectionRect.Width = 0;
        selectionRect.Height = 0;
        Canvas.SetLeft(selectionRect, ptStartX);
        Canvas.SetTop(selectionRect, ptStartY);
        CaptureStart = new System.Windows.Point(ptStartX, ptStartY);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!isDragging) return;

        var pos = e.GetPosition(this);
        ptEndX = (int)pos.X;
        ptEndY = (int)pos.Y;
        var x = Math.Min(ptStartX, ptEndX);
        var y = Math.Min(ptStartY, ptEndY);
        var w = Math.Abs(ptEndX - ptStartX);
        var h = Math.Abs(ptEndY - ptStartY);

        Canvas.SetLeft(selectionRect, x);
        Canvas.SetTop(selectionRect, y);
        selectionRect.Width = w;
        selectionRect.Height = h;

        sizeText.Text = $"{w} × {h}";
        sizeIndicator.Visibility = Visibility.Visible;
        var tipX = Math.Min(ptStartX, ptEndX) + Math.Abs(ptEndX - ptStartX) / 2.0 - 30;
        var tipY = Math.Min(ptStartY, ptEndY) - 28;
        if (tipY < 0) tipY = Math.Max(ptStartY, ptEndY) + 4;
        Canvas.SetLeft(sizeIndicator, Math.Max(0, tipX));
        Canvas.SetTop(sizeIndicator, tipY);
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!isDragging) return;
        isDragging = false;
        sizeIndicator.Visibility = Visibility.Collapsed;

        var pos = e.GetPosition(this);
        ptEndX = (int)pos.X;
        ptEndY = (int)pos.Y;
        var x = Math.Min(ptStartX, ptEndX);
        var y = Math.Min(ptStartY, ptEndY);
        var w = Math.Abs(ptEndX - ptStartX);
        var h = Math.Abs(ptEndY - ptStartY);

        if (w > 5 && h > 5)
        {
            // imgSnap 已按物理像素采集，而选区坐标 (x,y,w,h) 为 WPF 逻辑(DIP)单位，
            // 需按比例换算到物理像素后再裁剪，保证裁剪图与选区精确对应且为全分辨率。
            double scaleX = imgSnap.Width > 0 && SystemParameters.PrimaryScreenWidth > 0
                ? imgSnap.Width / SystemParameters.PrimaryScreenWidth : 1.0;
            double scaleY = imgSnap.Height > 0 && SystemParameters.PrimaryScreenHeight > 0
                ? imgSnap.Height / SystemParameters.PrimaryScreenHeight : 1.0;

            int pX = (int)Math.Round(x * scaleX);
            int pY = (int)Math.Round(y * scaleY);
            int pW = (int)Math.Round(w * scaleX);
            int pH = (int)Math.Round(h * scaleY);

            CapturedBitmap = new Bitmap(pW, pH);
            using (var g = Graphics.FromImage(CapturedBitmap))
            {
                g.DrawImage(imgSnap, new Rectangle(0, 0, pW, pH), pX, pY, pW, pH, GraphicsUnit.Pixel);
            }
            // 窗口尺寸/位置仍使用 DIP 逻辑单位，保持原有布局行为。
            CaptureSize = new System.Windows.Size(w, h);
            CaptureStart = new System.Windows.Point(x, y);
            CaptureCompleted?.Invoke(CapturedBitmap, CaptureStart, CaptureSize);
            Close();
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            sizeIndicator.Visibility = Visibility.Collapsed;
            CaptureCancelled?.Invoke();
            Close();
        }
    }

    private void OnMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        sizeIndicator.Visibility = Visibility.Collapsed;
        CaptureCancelled?.Invoke();
        Close();
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
