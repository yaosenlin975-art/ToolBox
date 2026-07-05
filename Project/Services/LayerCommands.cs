using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ToolBox.Models;

namespace ToolBox.Services;

public class AddLayerCommand
{
    public string Name { get; set; } = "图层";
    public BitmapSource Image { get; set; }

    public AddLayerCommand()
    {
    }

    public AddLayerCommand(string name, BitmapSource image)
    {
        Name = name;
        Image = image;
    }

    public void Execute(ScrapWindow scrap)
    {
        if (Image != null)
        {
            scrap.SetImage(Image);
        }
    }
}

public class AddTextLayerCommand
{
    public string Text { get; set; } = "";
    public double FontSize { get; set; } = 16;
    public Brush Foreground { get; set; } = Brushes.Black;

    public AddTextLayerCommand()
    {
    }

    public AddTextLayerCommand(string text, double fontSize, Brush foreground)
    {
        Text = text;
        FontSize = fontSize;
        Foreground = foreground;
    }

    public void Execute(ScrapWindow scrap)
    {
        if (string.IsNullOrEmpty(Text)) return;

        var visual = new DrawingVisual();
        FormattedText formattedText;
        using (var ctx = visual.RenderOpen())
        {
            formattedText = new FormattedText(
                Text,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Arial"),
                FontSize,
                Foreground,
                96);

            ctx.DrawText(formattedText, new System.Windows.Point(0, 0));
        }

        var rtb = new RenderTargetBitmap(
            (int)formattedText.Width + 10,
            (int)formattedText.Height + 10,
            96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();

        scrap.SetImage(rtb);
    }
}

public class DeleteLayerCommand
{
    public string Name { get; set; } = "删除图层";

    public void Execute(ScrapWindow scrap)
    {
        scrap.CloseScrap();
    }
}

public class RenameLayerCommand
{
    public string NewName { get; set; } = "";

    public RenameLayerCommand()
    {
    }

    public RenameLayerCommand(string name)
    {
        NewName = name;
    }

    public void Execute(ScrapWindow scrap)
    {
        scrap.Title = NewName;
    }
}
