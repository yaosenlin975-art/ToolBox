using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ToolBox.Models;

namespace ToolBox.Views;

public class ScrapPaintToolBar
{
    public ScrapPaintPenTool PenTool { get; set; } = new();
    public ScrapPaintTextTool TextTool { get; set; } = new();
    public Brush CurrentBrush { get; set; } = Brushes.Red;
    public double CurrentThickness { get; set; } = 2;

    public ScrapPaintToolBar()
    {
    }

    public ScrapPaintToolBar SetBrush(Brush brush)
    {
        CurrentBrush = brush;
        PenTool.SetBrush(brush);
        return this;
    }

    public ScrapPaintToolBar SetThickness(double thickness)
    {
        CurrentThickness = thickness;
        PenTool.SetThickness(thickness);
        return this;
    }

    public ScrapPaintToolBar SetText(string text)
    {
        TextTool.SetText(text);
        return this;
    }

    public ScrapPaintToolBar SetFontSize(double size)
    {
        TextTool.SetFontSize(size);
        return this;
    }

    public ToolBar CreateToolBar()
    {
        var toolBar = new ToolBar();

        var redBtn = CreateColorButton("红", Brushes.Red);
        var blueBtn = CreateColorButton("蓝", Brushes.Blue);
        var greenBtn = CreateColorButton("绿", Brushes.Green);
        var blackBtn = CreateColorButton("黑", Brushes.Black);
        var whiteBtn = CreateColorButton("白", Brushes.White);

        toolBar.Items.Add(redBtn);
        toolBar.Items.Add(blueBtn);
        toolBar.Items.Add(greenBtn);
        toolBar.Items.Add(blackBtn);
        toolBar.Items.Add(whiteBtn);
        toolBar.Items.Add(new Separator());

        var thinBtn = CreateThicknessButton("细", 1);
        var mediumBtn = CreateThicknessButton("中", 3);
        var thickBtn = CreateThicknessButton("粗", 5);

        toolBar.Items.Add(thinBtn);
        toolBar.Items.Add(mediumBtn);
        toolBar.Items.Add(thickBtn);

        return toolBar;
    }

    private System.Windows.Controls.Button CreateColorButton(string text, Brush color)
    {
        var btn = new System.Windows.Controls.Button
        {
            Content = text,
            Background = color,
            Foreground = Brushes.White,
            Width = 30
        };
        btn.Click += (s, e) => SetBrush(color);
        return btn;
    }

    private System.Windows.Controls.Button CreateThicknessButton(string text, double thickness)
    {
        var btn = new System.Windows.Controls.Button
        {
            Content = text,
            Width = 30
        };
        btn.Click += (s, e) => SetThickness(thickness);
        return btn;
    }
}


