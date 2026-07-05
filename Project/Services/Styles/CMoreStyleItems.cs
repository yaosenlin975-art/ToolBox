using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using ToolBox.Views;

namespace ToolBox.Models;

public class CCompactStyleItem : IStyleItem
{
    public int CompactWidth { get; set; } = 100;
    public int CompactHeight { get; set; } = 100;

    public string GetName() => "Compact";
    public string GetDisplayName() => "紧凑模式";
    public string GetDescription() => "将贴图缩放到指定大小";
    public string StateText => $"{CompactWidth}x{CompactHeight}";
    public string NameAndState => $"{GetDisplayName()} ({CompactWidth}x{CompactHeight})";
    public bool IsTerminate => false;
    public bool IsInitApply => false;
    public bool IsSetting => true;

    public void Apply(ScrapWindow scrap, Point clickPoint)
    {
        scrap.Width = CompactWidth;
        scrap.Height = CompactHeight;
    }

    public object Clone() => MemberwiseClone();
}

public class CMoveStyleItem : IStyleItem
{
    public int MoveX { get; set; }
    public int MoveY { get; set; }

    public string GetName() => "Move";
    public string GetDisplayName() => "移动";
    public string GetDescription() => "移动贴图到指定位置";
    public string StateText => $"{MoveX},{MoveY}";
    public string NameAndState => $"{GetDisplayName()} ({MoveX},{MoveY})";
    public bool IsTerminate => false;
    public bool IsInitApply => false;
    public bool IsSetting => true;

    public void Apply(ScrapWindow scrap, Point clickPoint)
    {
        scrap.Left = MoveX;
        scrap.Top = MoveY;
    }

    public object Clone() => MemberwiseClone();
}

public class CMarginStyleItem : IStyleItem
{
    public int MarginSize { get; set; } = 5;
    public byte MarginColorR { get; set; } = 0;
    public byte MarginColorG { get; set; } = 0;
    public byte MarginColorB { get; set; } = 0;

    public string GetName() => "Margin";
    public string GetDisplayName() => "边框";
    public string GetDescription() => "为贴图添加边框";
    public string StateText => $"{MarginSize}px";
    public string NameAndState => $"{GetDisplayName()} ({MarginSize}px)";
    public bool IsTerminate => false;
    public bool IsInitApply => false;
    public bool IsSetting => true;

    public void Apply(ScrapWindow scrap, Point clickPoint)
    {
        var source = scrap.ImageViewSource;
        if (source == null) return;

        var newWidth = source.PixelWidth + MarginSize * 2;
        var newHeight = source.PixelHeight + MarginSize * 2;

        var group = new DrawingGroup();
        var borderBrush = new SolidColorBrush(Color.FromRgb(MarginColorR, MarginColorG, MarginColorB));
        group.Children.Add(new GeometryDrawing(borderBrush, null,
            new RectangleGeometry(new Rect(0, 0, newWidth, newHeight))));
        group.Children.Add(new ImageDrawing(source,
            new Rect(MarginSize, MarginSize, source.PixelWidth, source.PixelHeight)));

        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen())
            ctx.DrawDrawing(group);

        var rtb = new RenderTargetBitmap(newWidth, newHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        scrap.SetImage(rtb);
    }

    public object Clone() => MemberwiseClone();
}

public class CTimerStyleItem : IStyleItem
{
    public int Interval { get; set; } = 1000;

    public string GetName() => "Timer";
    public string GetDisplayName() => "定时器";
    public string GetDescription() => "定时刷新贴图";
    public string StateText => $"{Interval}ms";
    public string NameAndState => $"{GetDisplayName()} ({Interval}ms)";
    public bool IsTerminate => false;
    public bool IsInitApply => false;
    public bool IsSetting => true;

    public void Apply(ScrapWindow scrap, Point clickPoint)
    {
        var timer = new System.Windows.Threading.DispatcherTimer();
        timer.Interval = TimeSpan.FromMilliseconds(Interval);
        timer.Tick += (s, e) =>
        {
            if (scrap.IsActive)
                scrap.Activate();
        };
        timer.Start();
    }

    public object Clone() => MemberwiseClone();
}

public class CTrimStyleItem : IStyleItem
{
    public int TrimLeft { get; set; }
    public int TrimTop { get; set; }
    public int TrimRight { get; set; }
    public int TrimBottom { get; set; }

    public string GetName() => "Trim";
    public string GetDisplayName() => "裁剪";
    public string GetDescription() => "裁剪贴图";
    public string StateText => $"{TrimLeft},{TrimTop},{TrimRight},{TrimBottom}";
    public string NameAndState => $"{GetDisplayName()} ({TrimLeft},{TrimTop},{TrimRight},{TrimBottom})";
    public bool IsTerminate => false;
    public bool IsInitApply => false;
    public bool IsSetting => true;

    public void Apply(ScrapWindow scrap, Point clickPoint)
    {
        var source = scrap.ImageViewSource;
        if (source == null) return;

        var cropRect = new Int32Rect(TrimLeft, TrimTop,
            Math.Max(0, source.PixelWidth - TrimLeft - TrimRight),
            Math.Max(0, source.PixelHeight - TrimTop - TrimBottom));

        if (cropRect.Width <= 0 || cropRect.Height <= 0) return;

        var cropped = new CroppedBitmap(source, cropRect);
        cropped.Freeze();
        scrap.SetImage(cropped);
    }

    public object Clone() => MemberwiseClone();
}

public class CWindowStyleItem : IStyleItem
{
    public bool TopMost { get; set; } = true;

    public string GetName() => "Window";
    public string GetDisplayName() => "窗口置顶";
    public string GetDescription() => "设置贴图窗口置顶";
    public string StateText => TopMost ? "置顶" : "取消置顶";
    public string NameAndState => $"{GetDisplayName()} ({StateText})";
    public bool IsTerminate => false;
    public bool IsInitApply => false;
    public bool IsSetting => false;

    public void Apply(ScrapWindow scrap, Point clickPoint)
    {
        scrap.Topmost = TopMost;
    }

    public object Clone() => MemberwiseClone();
}

public class CPaintStyleItem : IStyleItem
{
    public string GetName() => "Paint";
    public string GetDisplayName() => "绘图";
    public string GetDescription() => "打开绘图工具";
    public string StateText => "";
    public string NameAndState => GetDisplayName();
    public bool IsTerminate => false;
    public bool IsInitApply => false;
    public bool IsSetting => false;

    public void Apply(ScrapWindow scrap, Point clickPoint)
    {
        var paintWindow = new PaintWindow(scrap);
        paintWindow.Show();
    }

    public object Clone() => MemberwiseClone();
}

public class CPicasaUploaderStyleItem : IStyleItem
{
    public string GetName() => "PicasaUploader";
    public string GetDisplayName() => "Picasa上传";
    public string GetDescription() => "上传到Picasa";
    public string StateText => "";
    public string NameAndState => GetDisplayName();
    public bool IsTerminate => false;
    public bool IsInitApply => false;
    public bool IsSetting => false;

    public void Apply(ScrapWindow scrap, Point clickPoint)
    {
        MessageBox.Show("Picasa上传功能已弃用", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public object Clone() => MemberwiseClone();
}

