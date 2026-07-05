using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ToolBox.Services;

public static class Extensions
{
    public static BitmapSource ScaleToSize(this BitmapSource source, int width, int height)
    {
        if (source == null) return null;

        var group = new TransformedBitmap(source,
            new ScaleTransform(
                (double)width / source.PixelWidth,
                (double)height / source.PixelHeight));

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen())
        {
            ctx.DrawImage(group, new Rect(0, 0, width, height));
        }
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    public static BitmapSource CropBitmap(this BitmapSource source, int x, int y, int width, int height)
    {
        if (source == null) return null;

        var cropRect = new Int32Rect(x, y, width, height);
        var cropped = new CroppedBitmap(source, cropRect);
        cropped.Freeze();
        return cropped;
    }

    public static BitmapSource RotateBitmap(this BitmapSource source, double angle)
    {
        if (source == null) return null;

        var rotated = new TransformedBitmap(source, new RotateTransform(angle));
        rotated.Freeze();
        return rotated;
    }

    public static BitmapSource FlipBitmap(this BitmapSource source, bool horizontal, bool vertical)
    {
        if (source == null) return null;

        var scaleX = horizontal ? -1.0 : 1.0;
        var scaleY = vertical ? -1.0 : 1.0;
        var flipped = new TransformedBitmap(source, new ScaleTransform(scaleX, scaleY));
        flipped.Freeze();
        return flipped;
    }

    public static BitmapSource InvertColors(this BitmapSource source)
    {
        if (source == null) return null;

        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen())
        {
            ctx.DrawRectangle(Brushes.White, null, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
            ctx.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
        }

        var rtb = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    public static string ToCustomString(this DateTime dateTime)
    {
        return dateTime.ToString("yyyyMMddHHmmssfff");
    }
}
