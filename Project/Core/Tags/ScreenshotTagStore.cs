using System.IO;
using System.Text.Json;

namespace ToolBox.Core.Tags;

/// <summary>截图标签数据</summary>
public class ScreenshotTagEntry
{
    public string CacheKey { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
    public int Rating { get; set; } // 0-5, 0=未评分
    public string Notes { get; set; } = string.Empty;
    public List<int> DominantColors { get; set; } = new(); // ARGB 值列表
    public DateTime TaggedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>截图标签存储单例</summary>
public class ScreenshotTagStore
{
    private static ScreenshotTagStore? _instance;
    public static ScreenshotTagStore Instance => _instance ??= new ScreenshotTagStore();

    private readonly string _filePath;
    private Dictionary<string, ScreenshotTagEntry> _entries = new();

    public event Action? EntriesChanged;

    private ScreenshotTagStore()
    {
        _filePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ToolBox", "screenshot_tags.json");
        Load();
    }

    public ScreenshotTagEntry GetOrCreate(string cacheKey)
    {
        if (!_entries.TryGetValue(cacheKey, out var entry))
        {
            entry = new ScreenshotTagEntry { CacheKey = cacheKey };
            _entries[cacheKey] = entry;
        }
        return entry;
    }

    public ScreenshotTagEntry? Get(string cacheKey)
    {
        return _entries.TryGetValue(cacheKey, out var entry) ? entry : null;
    }

    public ScreenshotTagStore AddTag(string cacheKey, string tag)
    {
        var entry = GetOrCreate(cacheKey);
        if (!entry.Tags.Contains(tag) && entry.Tags.Count < 20)
        {
            entry.Tags.Add(tag);
            entry.TaggedAt = DateTime.UtcNow;
            Save();
            EntriesChanged?.Invoke();
        }
        return this;
    }

    public ScreenshotTagStore RemoveTag(string cacheKey, string tag)
    {
        if (_entries.TryGetValue(cacheKey, out var entry))
        {
            entry.Tags.Remove(tag);
            Save();
            EntriesChanged?.Invoke();
        }
        return this;
    }

    public ScreenshotTagStore SetTags(string cacheKey, List<string> tags)
    {
        var entry = GetOrCreate(cacheKey);
        entry.Tags = tags.Take(20).ToList();
        entry.TaggedAt = DateTime.UtcNow;
        Save();
        EntriesChanged?.Invoke();
        return this;
    }

    public ScreenshotTagStore SetRating(string cacheKey, int rating)
    {
        var entry = GetOrCreate(cacheKey);
        entry.Rating = Math.Clamp(rating, 0, 5);
        Save();
        EntriesChanged?.Invoke();
        return this;
    }

    public ScreenshotTagStore SetNotes(string cacheKey, string notes)
    {
        var entry = GetOrCreate(cacheKey);
        entry.Notes = notes;
        Save();
        EntriesChanged?.Invoke();
        return this;
    }

    public ScreenshotTagStore SetDominantColors(string cacheKey, List<int> colors)
    {
        var entry = GetOrCreate(cacheKey);
        entry.DominantColors = colors.Take(5).ToList();
        Save();
        return this;
    }

    /// <summary>获取所有使用过的标签（带计数）</summary>
    public Dictionary<string, int> GetAllTagCounts()
    {
        var counts = new Dictionary<string, int>();
        foreach (var entry in _entries.Values)
        {
            foreach (var tag in entry.Tags)
            {
                counts[tag] = counts.GetValueOrDefault(tag, 0) + 1;
            }
        }
        return counts;
    }

    /// <summary>按条件筛选</summary>
    public List<ScreenshotTagEntry> Search(string? tag = null, int? minRating = null,
        int? colorHue = null, DateTime? after = null)
    {
        return _entries.Values.Where(e =>
        {
            if (tag != null && !e.Tags.Contains(tag)) return false;
            if (minRating.HasValue && e.Rating < minRating.Value) return false;
            if (after.HasValue && e.TaggedAt < after.Value) return false;
            if (colorHue.HasValue && !MatchesColorHue(e.DominantColors, colorHue.Value)) return false;
            return true;
        }).ToList();
    }

    private static bool MatchesColorHue(List<int> colors, int targetHue)
    {
        foreach (var argb in colors)
        {
            var r = (argb >> 16) & 0xFF;
            var g = (argb >> 8) & 0xFF;
            var b = argb & 0xFF;
            var hue = RgbToHue(r, g, b);
            if (Math.Abs(hue - targetHue) <= 30 || Math.Abs(hue - targetHue) >= 330) return true;
        }
        return false;
    }

    private static double RgbToHue(int r, int g, int b)
    {
        var rf = r / 255.0; var gf = g / 255.0; var bf = b / 255.0;
        var max = Math.Max(rf, Math.Max(gf, bf));
        var min = Math.Min(rf, Math.Min(gf, bf));
        var d = max - min;
        if (d == 0) return 0;
        double h;
        if (max == rf) h = ((gf - bf) / d) % 6;
        else if (max == gf) h = (bf - rf) / d + 2;
        else h = (rf - gf) / d + 4;
        h *= 60;
        if (h < 0) h += 360;
        return h;
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                var list = JsonSerializer.Deserialize<List<ScreenshotTagEntry>>(json);
                if (list != null)
                    _entries = list.ToDictionary(e => e.CacheKey, e => e);
            }
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_entries.Values.ToList(), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch { }
    }
}
