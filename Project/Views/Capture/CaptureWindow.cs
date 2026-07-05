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
        var screenWidth = (int)SystemParameters.PrimaryScreenWidth;
        var screenHeight = (int)SystemParameters.PrimaryScreenHeight;

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
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!isDragging) return;
        isDragging = false;

        var pos = e.GetPosition(this);
        ptEndX = (int)pos.X;
        ptEndY = (int)pos.Y;
        var x = Math.Min(ptStartX, ptEndX);
        var y = Math.Min(ptStartY, ptEndY);
        var w = Math.Abs(ptEndX - ptStartX);
        var h = Math.Abs(ptEndY - ptStartY);

        if (w > 5 && h > 5)
        {
            CapturedBitmap = new Bitmap(w, h);
            using (var g = Graphics.FromImage(CapturedBitmap))
            {
                g.DrawImage(imgSnap, new Rectangle(0, 0, w, h), x, y, w, h, GraphicsUnit.Pixel);
            }
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
            CaptureCancelled?.Invoke();
            Close();
        }
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}