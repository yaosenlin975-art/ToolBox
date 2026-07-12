using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ToolBox.Services;

public class CacheItem
{
    private const string ImageFileName = "Image.png";
    private const string InfoFileName = "Info.json";

    public DateTime CreateTime { get; set; }
    [JsonConverter(typeof(PointJsonConverter))]
    public Point Position { get; set; }
    public int StyleId { get; set; }
    [JsonConverter(typeof(PointJsonConverter))]
    public Point StyleClickPoint { get; set; }
    public int SortingOrder { get; set; }
    public bool IsFloating { get; set; }

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
            StyleClickPoint = clickPoint,
            IsFloating = true
        };

        item.SaveImage(image);
        item.SaveInfo();
        return item;
    }

    private static Point ParsePointString(string? value)
    {
        if (string.IsNullOrEmpty(value)) return new Point(0, 0);
        var parts = value.Split(',');
        double x = 0, y = 0;
        if (parts.Length >= 1)
            double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out x);
        if (parts.Length >= 2)
            double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out y);
        return new Point(x, y);
    }

    public static CacheItem Read(string cacheItemPath)
    {
        var fullPath = Path.Combine(cacheItemPath, InfoFileName);
        if (!File.Exists(fullPath)) return null;

        var json = File.ReadAllText(fullPath);
        try
        {
            return JsonSerializer.Deserialize<CacheItem>(json);
        }
        catch { }

        try
        {
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var item = new CacheItem();

            if (root.TryGetProperty("CreateTime", out var ct))
                item.CreateTime = ct.GetDateTime();

            if (root.TryGetProperty("Position", out var pos))
                item.Position = ParsePointString(pos.GetString());

            if (root.TryGetProperty("Style", out var style))
            {
                if (style.TryGetProperty("ID", out var id))
                    item.StyleId = id.GetInt32();
                if (style.TryGetProperty("ClickPoint", out var cp))
                    item.StyleClickPoint = ParsePointString(cp.GetString());
            }

            if (root.TryGetProperty("SortingOrder", out var so))
                item.SortingOrder = so.GetInt32();

            return item;
        }
        catch { return null; }
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

        try
        {
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
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ToolBox] SaveImage failed: {ex.Message}");
        }
        return this;
    }

    public CacheItem SaveInfo()
    {
        try
        {
            var folderPath = FolderPath;
            if (!Directory.Exists(folderPath))
                Directory.CreateDirectory(folderPath);

            var fullPath = Path.Combine(folderPath, InfoFileName);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(fullPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ToolBox] SaveInfo failed: {ex.Message}");
        }
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
