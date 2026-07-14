// File: OcrSelectAdorner.cs
// 贴图选字模式的选区装饰器。在 ScrapWindow 的 Image 上绘制蓝色半透明矩形(AC2.1)。
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace ToolBox.Views.Ocr;

/// <summary>在装饰元素上绘制半透明蓝色选区矩形。坐标基于装饰元素本地坐标系。</summary>
public class OcrSelectAdorner : Adorner
{
    private Point start;
    private Point end;
    private bool visible;
    private readonly Brush fillBrush;
    private readonly Pen borderPen;

    public OcrSelectAdorner(UIElement adornedElement) : base(adornedElement)
    {
        IsHitTestVisible = false;
        fillBrush = new SolidColorBrush(Color.FromArgb(70, 30, 120, 255));
        borderPen = new Pen(new SolidColorBrush(Color.FromRgb(30, 120, 255)), 1.5);
        fillBrush.Freeze();
        borderPen.Freeze();
    }

    public OcrSelectAdorner Update(Point s, Point e)
    {
        start = s;
        end = e;
        visible = true;
        InvalidateVisual();
        return this;
    }

    public OcrSelectAdorner Clear()
    {
        visible = false;
        InvalidateVisual();
        return this;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (!visible) return;
        var rect = new Rect(start, end);
        drawingContext.DrawRectangle(fillBrush, borderPen, rect);
    }
}
