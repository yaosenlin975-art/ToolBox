using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace ToolBox.Models;

public class CImagePngStyleItem : IStyleItem
{
    public string GetName() => "ImagePng";
    public string GetDisplayName() => "保存为PNG";
    public string GetDescription() => "将贴图保存为PNG文件";
    public string StateText => "";
    public string NameAndState => GetDisplayName();
    public bool IsTerminate => false;
    public bool IsInitApply => false;
    public bool IsSetting => true;

    public void Apply(ScrapWindow scrap, Point clickPoint)
    {
        var dialog = new SaveFileDialog { Filter = "PNG文件|*.png", DefaultExt = ".png" };
        if (dialog.ShowDialog() == true)
        {
            var source = scrap.GetViewImage();
            if (source != null)
            {
                using var stream = File.Create(dialog.FileName);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(source));
                encoder.Save(stream);
            }
        }
    }

    public object Clone() => MemberwiseClone();
}

public class CImageJpegStyleItem : IStyleItem
{
    public int Quality { get; set; } = 85;

    public string GetName() => "ImageJpeg";
    public string GetDisplayName() => "保存为JPEG";
    public string GetDescription() => "将贴图保存为JPEG文件";
    public string StateText => Quality.ToString();
    public string NameAndState => $"{GetDisplayName()} ({Quality})";
    public bool IsTerminate => false;
    public bool IsInitApply => false;
    public bool IsSetting => true;

    public void Apply(ScrapWindow scrap, Point clickPoint)
    {
        var dialog = new SaveFileDialog { Filter = "JPEG文件|*.jpg", DefaultExt = ".jpg" };
        if (dialog.ShowDialog() == true)
        {
            var source = scrap.GetViewImage();
            if (source != null)
            {
                using var stream = File.Create(dialog.FileName);
                var encoder = new JpegBitmapEncoder();
                encoder.QualityLevel = Quality;
                encoder.Frames.Add(BitmapFrame.Create(source));
                encoder.Save(stream);
            }
        }
    }

    public object Clone() => MemberwiseClone();
}

public class CImageBmpStyleItem : IStyleItem
{
    public string GetName() => "ImageBmp";
    public string GetDisplayName() => "保存为BMP";
    public string GetDescription() => "将贴图保存为BMP文件";
    public string StateText => "";
    public string NameAndState => GetDisplayName();
    public bool IsTerminate => false;
    public bool IsInitApply => false;
    public bool IsSetting => false;

    public void Apply(ScrapWindow scrap, Point clickPoint)
    {
        var dialog = new SaveFileDialog { Filter = "BMP文件|*.bmp", DefaultExt = ".bmp" };
        if (dialog.ShowDialog() == true)
        {
            var source = scrap.GetViewImage();
            if (source != null)
            {
                using var stream = File.Create(dialog.FileName);
                var encoder = new BmpBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(source));
                encoder.Save(stream);
            }
        }
    }

    public object Clone() => MemberwiseClone();
}

public class CRotateStyleItem : IStyleItem
{
    public double Angle { get; set; } = 90;

    public string GetName() => "Rotate";
    public string GetDisplayName() => "旋转";
    public string GetDescription() => "旋转贴图";
    public string StateText => $"{Angle}°";
    public string NameAndState => $"{GetDisplayName()} ({Angle}°)";
    public bool IsTerminate => false;
    public bool IsInitApply => false;
    public bool IsSetting => true;

    public void Apply(ScrapWindow scrap, Point clickPoint)
    {
        var source = scrap.ImageViewSource;
        if (source == null) return;
        var rotated = new TransformedBitmap(source, new RotateTransform(Angle));
        rotated.Freeze();
        scrap.SetImage(rotated);
    }

    public object Clone() => MemberwiseClone();
}

public class CScaleStyleItem : IStyleItem
{
    public double ScalePercent { get; set; } = 50;

    public string GetName() => "Scale";
    public string GetDisplayName() => "缩放";
    public string GetDescription() => "缩放贴图";
    public string StateText => $"{ScalePercent}%";
    public string NameAndState => $"{GetDisplayName()} ({ScalePercent}%)";
    public bool IsTerminate => false;
    public bool IsInitApply => false;
    public bool IsSetting => true;

    public void Apply(ScrapWindow scrap, Point clickPoint)
    {
        var source = scrap.ImageViewSource;
        if (source == null) return;
        var scale = ScalePercent / 100.0;
        var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        scaled.Freeze();
        scrap.SetImage(scaled);
    }

    public object Clone() => MemberwiseClone();
}

public class COpacityStyleItem : IStyleItem
{
    public double OpacityPercent { get; set; } = 50;

    public string GetName() => "Opacity";
    public string GetDisplayName() => "透明度";
    public string GetDescription() => "设置贴图透明度";
    public string StateText => $"{OpacityPercent}%";
    public string NameAndState => $"{GetDisplayName()} ({OpacityPercent}%)";
    public bool IsTerminate => false;
    public bool IsInitApply => false;
    public bool IsSetting => true;

    public void Apply(ScrapWindow scrap, Point clickPoint) => scrap.Opacity = OpacityPercent / 100.0;

    public object Clone() => MemberwiseClone();
}

public class CToneReverseStyleItem : IStyleItem
{
    public string GetName() => "ToneReverse";
    public string GetDisplayName() => "反色";
    public string GetDescription() => "反转贴图颜色";
    public string StateText => "";
    public string NameAndState => GetDisplayName();
    public bool IsTerminate => false;
    public bool IsInitApply => false;
    public bool IsSetting => false;

    public void Apply(ScrapWindow scrap, Point clickPoint)
    {
        var source = scrap.ImageViewSource;
        if (source == null) return;

        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen())
        {
            ctx.DrawRectangle(Brushes.White, null, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
            ctx.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
        }

        var rtb = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        scrap.SetImage(rtb);
    }

    public object Clone() => MemberwiseClone();
}
