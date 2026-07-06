using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace ToolBox.Views;

public partial class MagnifierWindow : Window
{
    private System.Windows.Threading.DispatcherTimer timer;
    private ELocationType locationType = ELocationType.LeftTop;

    public MagnifierWindow()
    {
        InitializeComponent();
        timer = new System.Windows.Threading.DispatcherTimer();
        timer.Interval = TimeSpan.FromMilliseconds(100);
        timer.Tick += Timer_Tick;
    }

    public MagnifierWindow SetLocation(ELocationType type)
    {
        locationType = type;
        UpdatePosition();
        return this;
    }

    public MagnifierWindow SetText(int x, int y, int w, int h)
    {
        txtInfo.Text = $"X:{x} Y:{y} W:{w} H:{h}";
        return this;
    }

    public MagnifierWindow Start()
    {
        timer.Start();
        Show();
        return this;
    }

    public MagnifierWindow Stop()
    {
        timer.Stop();
        Hide();
        return this;
    }

    private void Timer_Tick(object sender, EventArgs e)
    {
        RefreshImage();
    }

    private void UpdatePosition()
    {
        var screenWidth = SystemParameters.PrimaryScreenWidth;
        var screenHeight = SystemParameters.PrimaryScreenHeight;

        if (locationType == ELocationType.LeftTop)
        {
            Left = 10;
            Top = 10;
        }
        else
        {
            Left = screenWidth - Width - 10;
            Top = screenHeight - Height - 10;
        }
    }

    private void RefreshImage()
    {
        try
        {
            var cursorPos = new System.Drawing.Point();
            GetCursorPos(out cursorPos);

            var screenWidth = (int)SystemParameters.PrimaryScreenWidth;
            var screenHeight = (int)SystemParameters.PrimaryScreenHeight;

            var captureSize = 64;
            var startX = Math.Max(0, cursorPos.X - captureSize / 2);
            var startY = Math.Max(0, cursorPos.Y - captureSize / 2);
            startX = Math.Min(startX, screenWidth - captureSize);
            startY = Math.Min(startY, screenHeight - captureSize);

            using var bmp = new Bitmap(captureSize, captureSize);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(startX, startY, 0, 0, new System.Drawing.Size(captureSize, captureSize));
            }

            var scale = 4;
            var scaledWidth = captureSize * scale;
            var scaledHeight = captureSize * scale;

            using var scaled = new Bitmap(scaledWidth, scaledHeight);
            using (var g = Graphics.FromImage(scaled))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.DrawImage(bmp, 0, 0, scaledWidth, scaledHeight);
            }

            var hBitmap = scaled.GetHbitmap();
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                magnifierImage.Source = source;
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ToolBox] {ex.Message}"); }
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out System.Drawing.Point lpPoint);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
