using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ToolBox.Models;

public class CCopyStyleItem : IStyleItem
{
    public bool CopyFromSource { get; set; } = true;

    public string GetName() => "Copy";
    public string GetDisplayName() => "复制";
    public string GetDescription() => "复制到剪贴板";
    public string StateText => "";
    public string NameAndState => GetDisplayName();
    public bool IsTerminate => false;
    public bool IsInitApply => false;
    public bool IsSetting => true;

    public void Apply(ScrapWindow scrap, Point clickPoint)
    {
        var bitmap = scrap.GetViewImage();
        if (bitmap == null) return;

        if (CopyFromSource)
        {
            Clipboard.SetImage(bitmap);
        }
        else
        {
            var bordered = AddBorder(bitmap);
            Clipboard.SetImage(bordered);
        }
    }

    public object Clone() => MemberwiseClone();

    private static BitmapSource AddBorder(BitmapSource source)
    {
        var group = new DrawingGroup();
        group.Children.Add(new ImageDrawing(source, new Rect(1, 1, source.PixelWidth, source.PixelHeight)));

        var borderPen = new Pen(Brushes.Black, 1);
        borderPen.Freeze();
        group.Children.Add(new GeometryDrawing(null, borderPen,
            new RectangleGeometry(new Rect(0, 0, source.PixelWidth + 2, source.PixelHeight + 2))));

        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen())
        {
            ctx.DrawDrawing(group);
        }

        var rtb = new RenderTargetBitmap(source.PixelWidth + 2, source.PixelHeight + 2, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }
}
