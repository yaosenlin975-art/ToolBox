using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ToolBox.Models;

namespace ToolBox.Views;

public class ScrapPaintLayer
{
    public string Name { get; set; } = "图层";
    public bool IsVisible { get; set; } = true;
    public double Opacity { get; set; } = 1.0;
    public BitmapSource Image { get; set; }
    public int ZIndex { get; set; }

    public ScrapPaintLayer()
    {
    }

    public ScrapPaintLayer(string name, BitmapSource image)
    {
        Name = name;
        Image = image;
    }

    public ScrapPaintLayer SetVisible(bool visible)
    {
        IsVisible = visible;
        return this;
    }

    public ScrapPaintLayer SetOpacity(double opacity)
    {
        Opacity = opacity;
        return this;
    }

    public ScrapPaintLayer SetZIndex(int index)
    {
        ZIndex = index;
        return this;
    }
}

public class ScrapPaintLayerItem
{
    public string Name { get; set; } = "";
    public Brush Color { get; set; } = Brushes.Black;
    public double Thickness { get; set; } = 2;
    public System.Windows.Point StartPoint { get; set; }
    public System.Windows.Point EndPoint { get; set; }

    public ScrapPaintLayerItem()
    {
    }

    public ScrapPaintLayerItem(string name, Brush color, double thickness,
        System.Windows.Point start, System.Windows.Point end)
    {
        Name = name;
        Color = color;
        Thickness = thickness;
        StartPoint = start;
        EndPoint = end;
    }

    public System.Windows.Shapes.Line CreateLine()
    {
        return new System.Windows.Shapes.Line
        {
            X1 = StartPoint.X,
            Y1 = StartPoint.Y,
            X2 = EndPoint.X,
            Y2 = EndPoint.Y,
            Stroke = Color,
            StrokeThickness = Thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
    }
}
