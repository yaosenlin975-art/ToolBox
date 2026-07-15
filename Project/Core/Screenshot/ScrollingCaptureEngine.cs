using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace ToolBox.Core.Screenshot;

/// <summary>滚动截图引擎</summary>
public class ScrollingCaptureEngine
{
    private static ScrollingCaptureEngine? _instance;
    public static ScrollingCaptureEngine Instance => _instance ??= new ScrollingCaptureEngine();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    private const uint WM_MOUSEWHEEL = 0x020A;
    private const uint WM_VSCROLL = 0x0115;
    private const uint WM_HSCROLL = 0x0114;
    private const uint SB_LINEUP = 0;
    private const uint SB_LINEDOWN = 1;
    private const uint SB_LINELEFT = 0;
    private const uint SB_LINERIGHT = 1;

    private ScrollingCaptureEngine() { }

    /// <summary>开始滚动截图</summary>
    public async Task<BitmapSource?> CaptureAsync(
        ScrollingCaptureConfig config,
        IProgress<ScrollingCaptureProgress> progress,
        CancellationToken cancellationToken)
    {
        if (!IsWindow(config.TargetWindow) || IsIconic(config.TargetWindow))
            return null;

        var frames = new List<BitmapSource>();
        var detector = new ScrollDetector();

        try
        {
            for (int frame = 0; frame < config.MaxFrames; frame++)
            {
                if (cancellationToken.IsCancellationRequested) break;

                // 截取当前帧
                var currentFrame = CaptureFrame(config.TargetWindow);
                if (currentFrame == null) break;

                frames.Add(currentFrame);

                progress.Report(new ScrollingCaptureProgress
                {
                    CurrentFrame = frame + 1,
                    EstimatedTotal = config.MaxFrames,
                    LastFrame = currentFrame,
                    StatusText = $"第 {frame + 1} 帧"
                });

                // 检测是否到底（与上一帧比较）
                if (frame > 0 && detector.IsAtBottom(frames[^2], currentFrame, config.Direction))
                    break;

                // 发送滚动指令
                SendScroll(config.TargetWindow, config.Direction, config.ScrollStepPixels);

                // 等待渲染
                await Task.Delay(config.ScrollDelayMs, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch { }

        if (frames.Count == 0) return null;

        // 拼接
        return ImageStitcher.Stitch(frames, config.OverlapRatio);
    }

    private BitmapSource? CaptureFrame(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect)) return null;

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return null;

        try
        {
            using var bmp = new System.Drawing.Bitmap(width, height,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = System.Drawing.Graphics.FromImage(bmp);
            g.CopyFromScreen(rect.Left, rect.Top, 0, 0,
                new System.Drawing.Size(width, height));

            var hBitmap = bmp.GetHbitmap();
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally { DeleteObject(hBitmap); }
        }
        catch { return null; }
    }

    private void SendScroll(IntPtr hwnd, EScrollDirection direction, int stepPixels)
    {
        if (direction == EScrollDirection.Vertical)
        {
            // WM_MOUSEWHEEL: wParam 高位 = 负值表示向下滚动
            var wParam = (IntPtr)(-stepPixels << 16);
            SendMessage(hwnd, WM_MOUSEWHEEL, wParam, IntPtr.Zero);
        }
        else
        {
            SendMessage(hwnd, WM_HSCROLL, (IntPtr)SB_LINERIGHT, IntPtr.Zero);
        }
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}

public enum EScrollDirection { Vertical, Horizontal }

public class ScrollingCaptureConfig
{
    public IntPtr TargetWindow { get; set; }
    public EScrollDirection Direction { get; set; } = EScrollDirection.Vertical;
    public int MaxFrames { get; set; } = 50;
    public int ScrollStepPixels { get; set; } = 120;
    public double OverlapRatio { get; set; } = 0.15;
    public int ScrollDelayMs { get; set; } = 300;
}

public class ScrollingCaptureProgress
{
    public int CurrentFrame { get; set; }
    public int EstimatedTotal { get; set; }
    public BitmapSource? LastFrame { get; set; }
    public string StatusText { get; set; } = string.Empty;
}
