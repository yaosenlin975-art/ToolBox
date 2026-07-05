using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Text.Json;

namespace ToolBox.Services;

public class CacheItem
{
    private const string ImageFileName = "Image.png";
    private const string InfoFileName = "Info.json";

    public DateTime CreateTime { get; set; }
    public Point Position { get; set; }
    public int StyleId { get; set; }
    public Point StyleClickPoint { get; set; }
    public int SortingOrder { get; set; }

    public string FolderPath => Path.Combine(CacheManager.CachePath, CreateTime.ToString("yyyyMMddHHmmssfff"));

    public bool IsValid
    {
        get
        {
            var fullPath = Path.Combine(FolderPath, ImageFileName);
            return File.Exists(fullPath);
        }
    }

    public static CacheItem Create(DateTime createTime, BitmapSource image, Point pos, int styleId, Point clickPoint)
    {
        var item = new CacheItem
        {
            CreateTime = createTime,
            Position = pos,
            StyleId = styleId,
            StyleClickPoint = clickPoint
        };

        item.SaveImage(image);
        item.SaveInfo();
        return item;
    }

    public static CacheItem Read(string cacheItemPath)
    {
        var fullPath = Path.Combine(cacheItemPath, InfoFileName);
        if (File.Exists(fullPath))
        {
            var json = File.ReadAllText(fullPath);
            return JsonSerializer.Deserialize<CacheItem>(json);
        }
        return null;
    }

    public BitmapSource ReadImage()
    {
        var fullPath = Path.Combine(FolderPath, ImageFileName);
        if (File.Exists(fullPath))
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(fullPath);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        return null;
    }

    public CacheItem SaveImage(BitmapSource image)
    {
        if (image == null) return this;

        var folderPath = FolderPath;
        if (!Directory.Exists(folderPath))
            Directory.CreateDirectory(folderPath);

        var fullPath = Path.Combine(folderPath, ImageFileName);
        if (File.Exists(fullPath))
            File.Delete(fullPath);

        using var stream = File.Create(fullPath);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(stream);
        return this;
    }

    public CacheItem SaveInfo()
    {
        var folderPath = FolderPath;
        if (!Directory.Exists(folderPath))
            Directory.CreateDirectory(folderPath);

        var fullPath = Path.Combine(folderPath, InfoFileName);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(fullPath, json);
        return this;
    }

    public CacheItem Delete()
    {
        var folderPath = FolderPath;
        if (Directory.Exists(folderPath))
            Directory.Delete(folderPath, true);
        return this;
    }
}
