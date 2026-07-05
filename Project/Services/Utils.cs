using System;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ToolBox.Services;

public static class Utils
{
    public static BitmapSource ScaleToSize(BitmapSource source, int width, int height)
    {
        if (source == null) return null;

        var group = new TransformedBitmap(source,
            new System.Windows.Media.ScaleTransform(
                (double)width / source.PixelWidth,
                (double)height / source.PixelHeight));

        var rtb = new RenderTargetBitmap(width, height, 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        var visual = new System.Windows.Media.DrawingVisual();
        using (var ctx = visual.RenderOpen())
        {
            ctx.DrawImage(group, new Rect(0, 0, width, height));
        }
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    public static BitmapSource CropBitmap(BitmapSource source, int x, int y, int width, int height)
    {
        if (source == null) return null;

        var cropRect = new Int32Rect(x, y, width, height);
        var cropped = new CroppedBitmap(source, cropRect);
        cropped.Freeze();
        return cropped;
    }

    public static string GetTimestamp()
    {
        return DateTime.Now.ToString("yyyyMMddHHmmssfff");
    }

    public static Point GetScreenCenter(double width, double height)
    {
        var screenWidth = SystemParameters.PrimaryScreenWidth;
        var screenHeight = SystemParameters.PrimaryScreenHeight;
        return new Point(
            (screenWidth - width) / 2,
            (screenHeight - height) / 2);
    }
}
