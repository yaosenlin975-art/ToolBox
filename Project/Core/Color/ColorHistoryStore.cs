// ColorHistoryStore.cs - 取色历史持久化服务
// 职责:JSON 持久化最近 100 条取色记录 + 历史变更通知
// 路径约定:%AppData%/ToolBox/color_history.json(与 todos.json 同目录)
// 命名空间用 ColorPicker 避免遮蔽 GlobalUsings 中的 Color 别名
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace ToolBox.Core.ColorPicker;

public class ColorHistoryStore
{
    public const int MaxHistory = 100;

    public static ColorHistoryStore Instance { get; } = new();

    private readonly List<ColorHistoryEntry> items = [];
    private readonly string filePath;
    private readonly SemaphoreSlim writeLock = new(1, 1);

    public IReadOnlyList<ColorHistoryEntry> History => items;

    /// <summary>历史变更通知(Add/Clear/Load 触发)</summary>
    public event Action? HistoryChanged;

    private void NotifyChanged() => HistoryChanged?.Invoke();

    private ColorHistoryStore()
    {
        filePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ToolBox", "color_history.json");
        Load();
    }

    public ColorHistoryStore Add(ColorInfo info)
    {
        var entry = new ColorHistoryEntry
        {
            R = info.R, G = info.G, B = info.B,
            Hex = info.Hex, Rgb = info.Rgb, Hsl = info.Hsl,
            ScreenX = info.ScreenX, ScreenY = info.ScreenY,
            PickedAt = info.PickedAt
        };
        items.Insert(0, entry);
        if (items.Count > MaxHistory)
            items.RemoveRange(MaxHistory, items.Count - MaxHistory);
        _ = SaveAsync();
        NotifyChanged();
        return this;
    }

    public ColorHistoryStore Clear()
    {
        items.Clear();
        _ = SaveAsync();
        NotifyChanged();
        return this;
    }

    /// <summary>取最近 N 条(用于历史面板展示)</summary>
    public IReadOnlyList<ColorHistoryEntry> GetRecent(int count)
    {
        return items.Take(Math.Min(count, items.Count)).ToList();
    }

    private void Load()
    {
        if (!File.Exists(filePath)) return;
        try
        {
            var json = File.ReadAllText(filePath);
            var data = JsonSerializer.Deserialize<ColorHistoryData>(json);
            if (data?.Items != null)
            {
                items.Clear();
                items.AddRange(data.Items);
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ToolBox] {ex.Message}"); }
    }

    private async Task SaveAsync()
    {
        await writeLock.WaitAsync();
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var data = new ColorHistoryData { Items = items.ToList() };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(filePath, json);
        }
        finally { writeLock.Release(); }
    }
}

/// <summary>历史持久化条目(独立于 ColorInfo,避免 UI 类型泄漏到持久化层)</summary>
public class ColorHistoryEntry
{
    public byte R { get; set; }
    public byte G { get; set; }
    public byte B { get; set; }
    public string Hex { get; set; } = string.Empty;
    public string Rgb { get; set; } = string.Empty;
    public string Hsl { get; set; } = string.Empty;
    public int ScreenX { get; set; }
    public int ScreenY { get; set; }
    public DateTime PickedAt { get; set; }
}

internal class ColorHistoryData
{
    [JsonPropertyName("items")]
    public List<ColorHistoryEntry> Items { get; set; } = [];
}
