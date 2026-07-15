﻿﻿﻿﻿﻿﻿﻿// ClipboardEntry.cs - 剪贴板条目数据模型
// 职责:承载单条剪贴板历史的元数据,JSON 序列化字段
// 持久化路径:%AppData%/ToolBox/clipboard.json
using System.Text.Json.Serialization;

namespace ToolBox.Core.ClipboardHistory;

/// <summary>剪贴板条目类型枚举</summary>
public enum EClipboardEntryType
{
    Text = 0,
    Image = 1,
    FileList = 2
}

/// <summary>
/// 剪贴板历史单条记录
/// </summary>
public class ClipboardEntry
{
    /// <summary>条目唯一 ID(Guid 前 12 位,文件名安全)</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>条目类型</summary>
    public EClipboardEntryType EntryType { get; set; }

    /// <summary>
    /// 文本内容或文件路径摘要
    /// - Text 类型:完整文本(可能多行,序列化时不截断)
    /// - Image 类型:留空(缩略图文件单独存于 ThumbnailPath)
    /// - FileList 类型:换行分隔的文件绝对路径
    /// </summary>
    public string TextContent { get; set; } = string.Empty;

    /// <summary>缩略图绝对路径(仅 Image 类型有效)</summary>
    public string? ThumbnailPath { get; set; }

    /// <summary>来源应用名(进程主模块名,可空)</summary>
    public string? SourceApp { get; set; }

    /// <summary>捕获时间(UTC 本地化)</summary>
    public DateTime CapturedAt { get; set; }

    /// <summary>是否置顶(置顶条目排在列表最前)</summary>
    public bool IsPinned { get; set; }

    /// <summary>是否收藏</summary>
    public bool IsFavorite { get; set; }

    /// <summary>内容 SHA256 哈希(去重用,与上一条对比)</summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>便捷工厂:生成新条目并填充 Id 与时间</summary>
    public static ClipboardEntry Create(EClipboardEntryType type, string textContent, string hash)
    {
        return new ClipboardEntry
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            EntryType = type,
            TextContent = textContent,
            ContentHash = hash,
            CapturedAt = DateTime.Now
        };
    }

    /// <summary>获取文本预览(用于列表显示,前 80 字符)</summary>
    [JsonIgnore]
    public string TextPreview
    {
        get
        {
            if (string.IsNullOrEmpty(TextContent)) return string.Empty;
            // 多行文本合并为单行预览
            var single = TextContent.Replace("\r", " ").Replace("\n", " ").Trim();
            return single.Length <= 80 ? single : single[..80] + "...";
        }
    }

    /// <summary>获取文件名摘要(仅 FileList 类型有意义)</summary>
    [JsonIgnore]
    public string FileSummary
    {
        get
        {
            if (EntryType != EClipboardEntryType.FileList || string.IsNullOrEmpty(TextContent))
                return string.Empty;
            // 取第一行作为主名,文件数 >1 时附加计数
            var lines = TextContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return string.Empty;
            var first = System.IO.Path.GetFileName(lines[0].Trim());
            return lines.Length == 1 ? first : $"{first} (+{lines.Length - 1})";
        }
    }
}
