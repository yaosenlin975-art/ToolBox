using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ToolBox.Models;

namespace ToolBox.Views;

public class ScrapPaintPenTool
{
    public Brush CurrentBrush { get; set; } = Brushes.Red;
    public double CurrentThickness { get; set; } = 2;
    public bool IsDrawing { get; set; }
    public System.Windows.Point LastPoint { get; set; }

    public ScrapPaintPenTool()
    {
    }

    public ScrapPaintPenTool SetBrush(Brush brush)
    {
        CurrentBrush = brush;
        return this;
    }

    public ScrapPaintPenTool SetThickness(double thickness)
    {
        CurrentThickness = thickness;
        return this;
    }

    public ScrapPaintPenTool StartDrawing(System.Windows.Point point)
    {
        IsDrawing = true;
        LastPoint = point;
        return this;
    }

    public ScrapPaintPenTool StopDrawing()
    {
        IsDrawing = false;
        return this;
    }

    public System.Windows.Shapes.Line DrawLine(System.Windows.Point currentPoint)
    {
        if (!IsDrawing) return null;

        var line = new System.Windows.Shapes.Line
        {
            X1 = LastPoint.X,
            Y1 = LastPoint.Y,
            X2 = currentPoint.X,
            Y2 = currentPoint.Y,
            Stroke = CurrentBrush,
            StrokeThickness = CurrentThickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };

        LastPoint = currentPoint;
        return line;
    }
}

public class ScrapPaintTextTool
{
    public string Text { get; set; } = "";
    public double FontSize { get; set; } = 16;
    public Brush TextColor { get; set; } = Brushes.Black;
    public System.Windows.Point Position { get; set; }

    public ScrapPaintTextTool()
    {
    }

    public ScrapPaintTextTool SetText(string text)
    {
        Text = text;
        return this;
    }

    public ScrapPaintTextTool SetFontSize(double size)
    {
        FontSize = size;
        return this;
    }

    public ScrapPaintTextTool SetColor(Brush color)
    {
        TextColor = color;
        return this;
    }

    public ScrapPaintTextTool SetPosition(System.Windows.Point point)
    {
        Position = point;
        return this;
    }

    public FormattedText CreateFormattedText()
    {
        return new FormattedText(
            Text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Arial"),
            FontSize,
            TextColor,
            96);
    }
}
