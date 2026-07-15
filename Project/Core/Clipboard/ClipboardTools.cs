﻿﻿﻿﻿﻿﻿﻿// ClipboardTools.cs - 剪贴板历史 LLM Tool 暴露
// 职责:让 AI 助手能调用 search_clipboard / get_clipboard 查询剪贴板历史
// 注册位置:ChatView.xaml.cs 构造函数 toolRegistry.Register(typeof(ClipboardTools))
using System.Linq;

using ToolBox.Core.Tools;

namespace ToolBox.Core.ClipboardHistory;

public static class ClipboardTools
{
    /// <summary>搜索剪贴板历史,按关键词匹配文本内容</summary>
    [Tool("search_clipboard", "搜索剪贴板历史,支持关键词匹配文本内容(图片/文件条目按摘要匹配)")]
    public static string SearchClipboard(
        [ToolParam("搜索关键词,为空时返回最近条目")] string keyword = "",
        [ToolParam("最大返回条数,默认10")] int limit = 10)
    {
        var results = string.IsNullOrWhiteSpace(keyword)
            ? ClipboardStore.Instance.Entries.Take(limit).ToList()
            : ClipboardStore.Instance.Search(keyword).Take(limit).ToList();

        if (results.Count == 0) return "暂无匹配的剪贴板历史";
        return string.Join("\n", results.Select((e, i) =>
            FormatEntry(i + 1, e)));
    }

    /// <summary>获取最近 N 条剪贴板历史(含所有类型)</summary>
    [Tool("get_clipboard", "获取最近 N 条剪贴板历史(默认5条)")]
    public static string GetRecentClipboard(
        [ToolParam("条数,默认5")] int count = 5)
    {
        if (count < 1) count = 5;
        if (count > 50) count = 50;
        var recent = ClipboardStore.Instance.Entries.Take(count).ToList();
        if (recent.Count == 0) return "暂无剪贴板历史";
        return string.Join("\n", recent.Select((e, i) =>
            FormatEntry(i + 1, e)));
    }

    private static string FormatEntry(int index, ClipboardEntry e)
    {
        var type = e.EntryType switch
        {
            EClipboardEntryType.Image => "图片",
            EClipboardEntryType.FileList => "文件",
            _ => "文本"
        };
        var preview = e.EntryType == EClipboardEntryType.FileList
            ? e.FileSummary
            : (e.TextPreview.Length > 60 ? e.TextPreview[..60] : e.TextPreview);
        var pinned = e.IsPinned ? "[置顶] " : "";
        var favorite = e.IsFavorite ? "[收藏] " : "";
        return $"{index}. {pinned}{favorite}[{type}] {preview} ({e.CapturedAt:MM-dd HH:mm})";
    }
}
