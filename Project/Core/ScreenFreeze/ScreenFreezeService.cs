using System;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ToolBox.Core.ScreenFreeze;

/// <summary>屏幕冻结服务 - 截取全屏快照作为\"冻结\"图层 (P3-02)</summary>
public class ScreenFreezeService : IDisposable
{
    public static ScreenFreezeService Instance { get; } = new();

    private BitmapSource? _capture;

    private ScreenFreezeService() { }

    /// <summary>截图完成时触发,由 UI 层订阅以显示冻结窗口</summary>
    public event Action<BitmapSource>? CaptureCompleted;

    /// <summary>截取全屏快照</summary>
    public void Freeze()
    {
        var width = (int)SystemParameters.PrimaryScreenWidth;
        var height = (int)SystemParameters.PrimaryScreenHeight;

        _capture = CaptureScreen(0, 0, width, height);
        CaptureCompleted?.Invoke(_capture);
    }

    /// <summary>解除冻结</summary>
    public void Unfreeze()
    {
        _capture = null;
        UnfreezeRequested?.Invoke();
    }

    public bool IsFrozen => _capture != null;

    public event Action? UnfreezeRequested;

    private static BitmapSource CaptureScreen(int left, int top, int width, int height)
    {
        using var bitmap = new System.Drawing.Bitmap(width, height);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(left, top, 0, 0, new System.Drawing.Size(width, height));

        var bitmapData = bitmap.LockBits(
            new System.Drawing.Rectangle(0, 0, width, height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        var bitmapSource = BitmapSource.Create(
            width, height,
            bitmap.HorizontalResolution, bitmap.VerticalResolution,
            System.Windows.Media.PixelFormats.Bgra32, null,
            bitmapData.Scan0,
            bitmapData.Stride * height,
            bitmapData.Stride);

        bitmap.UnlockBits(bitmapData);
        bitmapSource.Freeze();
        return bitmapSource;
    }

    public void Dispose()
    {
        Unfreeze();
    }
}
