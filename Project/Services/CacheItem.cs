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

        string json;
        try
        {
            json = File.ReadAllText(fullPath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ToolBox] 读取缓存信息失败: {ex.Message}");
            return null;
        }

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
        if (!File.Exists(fullPath)) return null;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(fullPath);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            // 坏 PNG（写盘被中断/磁盘满）只跳过这一条，不能让启动恢复整进程崩
            System.Diagnostics.Debug.WriteLine($"[ToolBox] 读取缓存图片失败: {ex.Message}");
            return null;
        }
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
            var tempPath = fullPath + ".tmp";
            try
            {
                using (var stream = File.Create(tempPath))
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    encoder.Save(stream);
                }
                ReplaceWithTemp(fullPath, tempPath);
            }
            finally
            {
                TryDelete(tempPath);
            }
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
            var tempPath = fullPath + ".tmp";
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            try
            {
                File.WriteAllText(tempPath, json);
                ReplaceWithTemp(fullPath, tempPath);
            }
            finally
            {
                TryDelete(tempPath);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ToolBox] SaveInfo failed: {ex.Message}");
        }
        return this;
    }

    // 直写目标文件会先截断再写，进程被杀就留下坏文件；临时文件写完再替换
    private static void ReplaceWithTemp(string target, string temp)
    {
        if (File.Exists(target)) File.Replace(temp, target, null);
        else File.Move(temp, target);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public CacheItem Delete()
    {
        try
        {
            var folderPath = FolderPath;
            if (Directory.Exists(folderPath))
                Directory.Delete(folderPath, true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ToolBox] Delete failed: {ex.Message}");
        }
        return this;
    }
}
