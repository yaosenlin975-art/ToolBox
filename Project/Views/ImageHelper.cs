using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ToolBox.Views;

/// <summary>
/// 图像相关共享辅助方法。
/// </summary>
public static class ImageHelper
{
    /// <summary>
    /// 将可能带透明通道的图像绘制到不透明纯色背景上，避免缩略图透明区透底。
    /// 本身不透明则原样返回。
    /// </summary>
    public static BitmapSource MakeOpaque(BitmapSource src)
    {
        if (src == null || !HasAlphaChannel(src.Format))
            return src;

        var sunkenBrush = (Application.Current?.FindResource("BgSunkenBrush") as SolidColorBrush);
        var bgColor = sunkenBrush?.Color ?? Colors.White;

        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen())
        {
            ctx.DrawRectangle(new SolidColorBrush(bgColor), null,
                new Rect(0, 0, src.PixelWidth, src.PixelHeight));
            ctx.DrawImage(src, new Rect(0, 0, src.PixelWidth, src.PixelHeight));
        }

        var rtb = new RenderTargetBitmap(
            src.PixelWidth, src.PixelHeight, src.DpiX, src.DpiY, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>
    /// 判断像素格式是否带透明度通道（WPF 的 PixelFormat 未直接暴露 HasAlphaChannel，
    /// 这里显式列出常见带 alpha 的格式）。
    /// </summary>
    private static bool HasAlphaChannel(PixelFormat format)
    {
        return format == PixelFormats.Bgra32
            || format == PixelFormats.Pbgra32
            || format == PixelFormats.Rgba64
            || format == PixelFormats.Rgba128Float;
    }
}
