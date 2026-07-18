﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿﻿// ClipboardStore.cs - 剪贴板历史持久化服务
// 职责:JSON 持久化 + SHA256 去重 + CRUD + 上限管理 + 缩略图文件管理
// 路径约定:%AppData%/ToolBox/clipboard.json(与 color_history.json 同目录)
//          缩略图:%LocalAppData%/ToolBox/clipboard_thumbnails/{id}.jpg
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace ToolBox.Core.ClipboardHistory;

public class ClipboardStore
{
    /// <summary>默认历史上限,可由 App 启动时通过 <see cref="SetMaxEntries"/> 覆盖</summary>
    public const int DEFAULT_MAX_ENTRIES = 500;

    public static ClipboardStore Instance { get; } = new();

    private readonly List<ClipboardEntry> items = [];
    private readonly string filePath;
    private readonly string thumbnailDir;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private int maxEntries = DEFAULT_MAX_ENTRIES;

    /// <summary>历史变更通知(Add/Delete/TogglePin/ToggleFavorite/Clear 触发)</summary>
    public event Action? EntriesChanged;

    /// <summary>当前最大历史条目数</summary>
    public int MaxEntries
    {
        get => maxEntries;
        set => maxEntries = Math.Max(10, value);
    }

    /// <summary>当前历史条目列表(已按置顶+时间倒序排序)</summary>
    public IReadOnlyList<ClipboardEntry> Entries => items;

    private void NotifyChanged() => EntriesChanged?.Invoke();

    private ClipboardStore()
    {
        filePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ToolBox", "clipboard.json");
        thumbnailDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ToolBox", "clipboard_thumbnails");
        Load();
    }

    /// <summary>更新最大条目数并立即触发溢出清理</summary>
    public ClipboardStore SetMaxEntries(int max)
    {
        MaxEntries = max;
        TrimToMax();
        return this;
    }

    /// <summary>设置最大条目数(对外别名,保持方法名语义清晰)</summary>
    public ClipboardStore ConfigureMaxEntries(int max) => SetMaxEntries(max);

    /// <summary>
    /// 添加条目(已做去重:与新条目同 hash 的最近一条会被替换为最新时间,而非新建)
    /// </summary>
    public ClipboardStore Add(ClipboardEntry entry)
    {
        if (entry == null) return this;

        // 去重:仅与最新一条对比(规格要求"连续复制相同内容不产生重复")
        if (items.Count > 0 && items[0].ContentHash == entry.ContentHash)
        {
            items[0].CapturedAt = entry.CapturedAt;
            _ = SaveAsync();
            NotifyChanged();
            return this;
        }

        items.Insert(0, entry);
        TrimToMax();
        _ = SaveAsync();
        NotifyChanged();
        return this;
    }

    /// <summary>删除指定条目,同时清理缩略图文件</summary>
    public ClipboardStore Delete(string entryId)
    {
        var idx = items.FindIndex(e => e.Id == entryId);
        if (idx < 0) return this;
        var entry = items[idx];
        items.RemoveAt(idx);
        TryDeleteThumbnail(entry);
        _ = SaveAsync();
        NotifyChanged();
        return this;
    }

    /// <summary>切换置顶状态(置顶条目自动排到列表最前)</summary>
    public ClipboardStore TogglePin(string entryId)
    {
        var entry = items.FirstOrDefault(e => e.Id == entryId);
        if (entry == null) return this;
        entry.IsPinned = !entry.IsPinned;
        Reorder();
        _ = SaveAsync();
        NotifyChanged();
        return this;
    }

    /// <summary>切换收藏状态</summary>
    public ClipboardStore ToggleFavorite(string entryId)
    {
        var entry = items.FirstOrDefault(e => e.Id == entryId);
        if (entry == null) return this;
        entry.IsFavorite = !entry.IsFavorite;
        _ = SaveAsync();
        NotifyChanged();
        return this;
    }

    /// <summary>清空全部历史(含缩略图文件)</summary>
    public ClipboardStore Clear()
    {
        foreach (var entry in items)
            TryDeleteThumbnail(entry);
        items.Clear();
        _ = SaveAsync();
        NotifyChanged();
        return this;
    }

    /// <summary>按关键词搜索(忽略大小写,匹配 TextContent)</summary>
    public List<ClipboardEntry> Search(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return items.ToList();
        var kw = keyword.Trim();
        return items.FindAll(e =>
            e.TextContent != null &&
            e.TextContent.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>按类型筛选</summary>
    public List<ClipboardEntry> FilterByType(EClipboardEntryType type)
    {
        return items.FindAll(e => e.EntryType == type);
    }

    /// <summary>分页查询(按当前排序,1-based)</summary>
    public List<ClipboardEntry> GetPage(int page, int pageSize = 20)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        return items
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();
    }

    /// <summary>按 ID 获取条目</summary>
    public ClipboardEntry? GetById(string entryId)
    {
        return items.FirstOrDefault(e => e.Id == entryId);
    }

    /// <summary>统计:总数 / 置顶数 / 收藏数</summary>
    public (int total, int pinned, int favorite) GetStats()
    {
        int pinned = items.Count(e => e.IsPinned);
        int favorite = items.Count(e => e.IsFavorite);
        return (items.Count, pinned, favorite);
    }

    /// <summary>保存图片缩略图(200×200 JPEG)到磁盘,返回绝对路径</summary>
    public string SaveThumbnail(string entryId, byte[] jpegBytes)
    {
        if (!Directory.Exists(thumbnailDir))
            Directory.CreateDirectory(thumbnailDir);
        var path = Path.Combine(thumbnailDir, entryId + ".jpg");
        File.WriteAllBytes(path, jpegBytes);
        return path;
    }

    /// <summary>计算文本内容的 SHA256(用于去重)</summary>
    public static string ComputeHash(string content)
    {
        if (string.IsNullOrEmpty(content)) return string.Empty;
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private void TrimToMax()
    {
        while (items.Count > maxEntries)
        {
            // 总是从尾部(最旧)弹出,并清理其缩略图
            var last = items[^1];
            items.RemoveAt(items.Count - 1);
            TryDeleteThumbnail(last);
        }
    }

    /// <summary>重新排序:置顶在前 + 时间倒序</summary>
    private void Reorder()
    {
        var sorted = items
            .OrderByDescending(e => e.IsPinned)
            .ThenByDescending(e => e.CapturedAt)
            .ToList();
        items.Clear();
        items.AddRange(sorted);
    }

    private void TryDeleteThumbnail(ClipboardEntry entry)
    {
        if (string.IsNullOrEmpty(entry.ThumbnailPath)) return;
        try { if (File.Exists(entry.ThumbnailPath)) File.Delete(entry.ThumbnailPath); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ToolBox] delete thumbnail failed: {ex.Message}"); }
    }

    private void Load()
    {
        if (!File.Exists(filePath)) return;
        try
        {
            var json = File.ReadAllText(filePath);
            var data = JsonSerializer.Deserialize<ClipboardData>(json);
            if (data?.Items != null)
            {
                items.Clear();
                items.AddRange(data.Items);
                // 持久化时已排序,但加载后再次排序以防数据被外部篡改
                Reorder();
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ToolBox] clipboard load failed: {ex.Message}"); }
    }

    private async Task SaveAsync()
    {
        await writeLock.WaitAsync();
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var data = new ClipboardData { Items = items.ToList() };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(filePath, json);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ToolBox] clipboard save failed: {ex.Message}"); }
        finally { writeLock.Release(); }
    }
}

internal class ClipboardData
{
    [JsonPropertyName("items")]
    public List<ClipboardEntry> Items { get; set; } = [];
}
