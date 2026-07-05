using System;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ToolBox.Models;

public abstract class ScrapSource
{
    public abstract BitmapSource GetImage();
    public abstract Point GetPosition();
}

public class ScrapSourceImage : ScrapSource
{
    public BitmapSource Image { get; set; }
    public Point Position { get; set; }

    public ScrapSourceImage(BitmapSource image, Point position)
    {
        Image = image;
        Position = position;
    }

    public override BitmapSource GetImage() => Image;
    public override Point GetPosition() => Position;
}

public class ScrapSourcePath : ScrapSource
{
    public string FilePath { get; set; }

    public ScrapSourcePath(string path)
    {
        FilePath = path;
    }

    public override BitmapSource GetImage()
    {
        try
        {
            var bitmap = new BitmapImage(new Uri(FilePath));
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    public override Point GetPosition() => new(100, 100);
}

public class ScrapSourceUrl : ScrapSource
{
    public string Url { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public Point CursorPosition { get; set; }

    public ScrapSourceUrl(string url, Point cursorPosition, int width = 0, int height = 0)
    {
        Url = url;
        CursorPosition = cursorPosition;
        Width = width;
        Height = height;
    }

    public override BitmapSource GetImage()
    {
        try
        {
            var bitmap = new BitmapImage(new Uri(Url));
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    public override Point GetPosition() => CursorPosition;
}
