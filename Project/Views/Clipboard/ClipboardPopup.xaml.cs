﻿// ClipboardPopup.xaml.cs - 浮动剪贴板面板
// 职责:Ctrl+Shift+V 唤起的 360x480 Topmost 浮窗,搜索/筛选/置顶/收藏/粘贴
// 设计要点:
//   - 实时搜索(无防抖,500 条 ≤50ms 满足规格)
//   - 点击条目:写入剪贴板 → IgnoreNext 防回环 → 关闭浮窗 → 延迟 50ms 发送 Ctrl+V
//   - 发送 Ctrl+V 使用 keybd_event,不引入 WinForms 依赖
//   - "发送到 AI":关闭浮窗 → 打开 Workbench → LoadPage(assistant) → 填入文本
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using ToolBox.Core.ClipboardHistory;
using ToolBox.Core.Native;

namespace ToolBox.Views.ClipboardHistory;

public partial class ClipboardPopup : Window
{
    private const int PASTE_DELAY_MS = 50;
    private List<ClipboardEntryViewModel> allViews = new();

    public ClipboardPopup()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 订阅 Store 变更,后台新到达条目时实时刷新
        ClipboardStore.Instance.EntriesChanged += OnEntriesChanged;
        Refresh();
        // 搜索框自动获取焦点
        Dispatcher.BeginInvoke(new Action(() => SearchBox.Focus()),
            DispatcherPriority.ApplicationIdle);
    }

    protected override void OnClosed(EventArgs e)
    {
        ClipboardStore.Instance.EntriesChanged -= OnEntriesChanged;
        base.OnClosed(e);
    }

    private void OnEntriesChanged()
    {
        // 跨线程触发,统一派发到 UI 线程
        Dispatcher.BeginInvoke(new Action(Refresh));
    }

    /// <summary>重新加载全部条目并应用当前筛选</summary>
    private void Refresh()
    {
        allViews = ClipboardStore.Instance.Entries
            .Select(e => new ClipboardEntryViewModel(e))
            .ToList();
        ApplyFilter();
    }

    /// <summary>应用搜索关键词 + 类型筛选,刷新 ListBox</summary>
    private void ApplyFilter()
    {
        var keyword = SearchBox.Text?.Trim() ?? string.Empty;
        var typeFilter = GetCurrentTypeFilter();

        IEnumerable<ClipboardEntryViewModel> filtered = allViews;
        if (typeFilter.HasValue)
            filtered = filtered.Where(v => v.EntryType == typeFilter.Value);
        if (!string.IsNullOrEmpty(keyword))
            filtered = filtered.Where(v => v.MatchesKeyword(keyword));

        var list = filtered.ToList();
        EntryList.ItemsSource = list;

        UpdateStatusBar();
    }

    private EClipboardEntryType? GetCurrentTypeFilter()
    {
        if (TabText.IsChecked == true) return EClipboardEntryType.Text;
        if (TabImage.IsChecked == true) return EClipboardEntryType.Image;
        if (TabFile.IsChecked == true) return EClipboardEntryType.FileList;
        return null;
    }

    private void UpdateStatusBar()
    {
        var stats = ClipboardStore.Instance.GetStats();
        StatusBar.Text = $"共 {stats.total} 条 · 已置顶 {stats.pinned} · 已收藏 {stats.favorite}";
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnFilterChanged(object sender, RoutedEventArgs e) => ApplyFilter();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>窗口失焦自动关闭(点击浮窗外即关闭)</summary>
    private void OnDeactivated(object sender, EventArgs e) => Close();

    private void OnEntrySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 仅作状态指示,实际粘贴由 MouseLeftButtonUp 处理
    }

    private void OnEntryClicked(object sender, MouseButtonEventArgs e)
    {
        if (EntryList.SelectedItem is ClipboardEntryViewModel vm)
            PasteEntry(vm);
    }

    /// <summary>条目操作按钮:⭐ 收藏 / 📌 置顶 / 🗑 删除</summary>
    private void OnActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.DataContext is not ClipboardEntryViewModel vm) return;
        var tag = btn.Tag as string ?? "";

        switch (tag)
        {
            case "favorite":
                ClipboardStore.Instance.ToggleFavorite(vm.Id);
                break;
            case "pin":
                ClipboardStore.Instance.TogglePin(vm.Id);
                break;
            case "delete":
                ClipboardStore.Instance.Delete(vm.Id);
                break;
        }
        // EntriesChanged 事件会触发 Refresh,无需手动调用
    }

    /// <summary>键盘导航:↑↓ 切换 / Enter 粘贴 / ESC 关闭</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                Close();
                break;
            case Key.Down:
                e.Handled = true;
                MoveSelection(1);
                break;
            case Key.Up:
                e.Handled = true;
                MoveSelection(-1);
                break;
            case Key.Enter:
                if (EntryList.SelectedItem is ClipboardEntryViewModel vm)
                {
                    e.Handled = true;
                    PasteEntry(vm);
                }
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        var count = EntryList.Items.Count;
        if (count == 0) return;
        var idx = EntryList.SelectedIndex;
        idx = idx < 0 ? 0 : Math.Max(0, Math.Min(count - 1, idx + delta));
        EntryList.SelectedIndex = idx;
        EntryList.ScrollIntoView(EntryList.Items[idx]);
    }

    /// <summary>写入剪贴板 → 关闭浮窗 → 延迟发送 Ctrl+V 到前台</summary>
    private void PasteEntry(ClipboardEntryViewModel vm)
    {
        var entry = vm.Source;
        // 关键:写入剪贴板前设置 IgnoreNext,避免本窗口写入被监听器重新记录
        ClipboardMonitor.Instance.IgnoreNext();
        try
        {
            switch (entry.EntryType)
            {
                case EClipboardEntryType.Text:
                    Clipboard.SetText(entry.TextContent);
                    break;
                case EClipboardEntryType.FileList:
                    var files = new System.Collections.Specialized.StringCollection();
                    foreach (var line in entry.TextContent.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        files.Add(line.Trim());
                    if (files.Count > 0) Clipboard.SetFileDropList(files);
                    break;
                case EClipboardEntryType.Image:
                    if (!string.IsNullOrEmpty(entry.ThumbnailPath) && File.Exists(entry.ThumbnailPath))
                        Clipboard.SetImage(LoadImageFrozen(entry.ThumbnailPath));
                    break;
            }
        }
        catch (Exception ex) { Debug.WriteLine($"[ToolBox] clipboard write failed: {ex.Message}"); }

        Close();

        // 延迟发送 Ctrl+V:等待浮窗关闭后前台窗口恢复焦点
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                // 用 keybd_event 模拟 Ctrl+V(Win32 API,不依赖 WinForms)
                NativeMethods.keybd_event(NativeMethods.VK_CONTROL, 0, 0, UIntPtr.Zero);
                NativeMethods.keybd_event(NativeMethods.VK_V, 0, 0, UIntPtr.Zero);
                NativeMethods.keybd_event(NativeMethods.VK_V, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
                NativeMethods.keybd_event(NativeMethods.VK_CONTROL, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            catch (Exception ex) { Debug.WriteLine($"[ToolBox] paste send failed: {ex.Message}"); }
        }), DispatcherPriority.ApplicationIdle);
    }

    private static BitmapSource LoadImageFrozen(string path)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.UriSource = new Uri(path);
        img.EndInit();
        img.Freeze();
        return img;
    }

    /// <summary>"发送到 AI 助手":关闭浮窗 → 打开 Workbench assistant 页并填入文本</summary>
    private void OnSendToAiClick(object sender, RoutedEventArgs e)
    {
        if (EntryList.SelectedItem is not ClipboardEntryViewModel vm) return;
        var text = vm.Source.EntryType == EClipboardEntryType.Image
            ? "[图片内容]"
            : vm.Source.TextContent;

        Close();

        var workbench = App.Workbench;
        if (workbench == null) return;
        workbench.Show();
        workbench.Activate();
        workbench.LoadPage("assistant");

        // LoadPage 同步创建 ChatView,可直接取回并填入文本
        if (workbench.PageHost.Content is Views.Chat.ChatView chat)
            chat.SetInput(text);
    }
}

/// <summary>ClipboardEntry 的 UI 友好包装,提供 DataBinding 用属性</summary>
public class ClipboardEntryViewModel
{
    private readonly ClipboardEntry entry;

    public ClipboardEntryViewModel(ClipboardEntry entry) { this.entry = entry; }

    public ClipboardEntry Source => entry;
    public string Id => entry.Id;
    public EClipboardEntryType EntryType => entry.EntryType;
    public string? ThumbnailPath => entry.ThumbnailPath;
    public bool HasThumbnail => !string.IsNullOrEmpty(entry.ThumbnailPath);
    public bool IsTextType => entry.EntryType == EClipboardEntryType.Text;
    public bool IsImageType => entry.EntryType == EClipboardEntryType.Image;
    public bool IsFileListType => entry.EntryType == EClipboardEntryType.FileList;
    public bool IsFavorite => entry.IsFavorite;
    public bool IsPinned => entry.IsPinned;

    public string DisplayTitle
    {
        get
        {
            return entry.EntryType switch
            {
                EClipboardEntryType.Image => "图片",
                EClipboardEntryType.FileList => entry.FileSummary,
                _ => entry.TextPreview
            };
        }
    }

    public string TimeDisplay => entry.CapturedAt.ToString("MM-dd HH:mm");

    /// <summary>类型徽标(用于列表右侧角标显示)</summary>
    public string TypeBadge => entry.EntryType switch
    {
        EClipboardEntryType.Image => "图片",
        EClipboardEntryType.FileList => "文件",
        _ => "文本"
    };

    /// <summary>Tooltip 完整内容(文本/文件列表);图片条目无文本时显示尺寸信息</summary>
    public string FullTooltip
    {
        get
        {
            if (entry.EntryType == EClipboardEntryType.Image)
                return $"图片 · {entry.CapturedAt:yyyy-MM-dd HH:mm:ss}";
            return entry.TextContent ?? string.Empty;
        }
    }

    public Brush FavoriteColor => entry.IsFavorite
        ? (Brush)Application.Current.FindResource("AccentBrush")
        : (Brush)Application.Current.FindResource("TextTertiaryBrush");

    public Brush PinColor => entry.IsPinned
        ? (Brush)Application.Current.FindResource("AccentBrush")
        : (Brush)Application.Current.FindResource("TextTertiaryBrush");

    /// <summary>关键词匹配(文本/文件路径,忽略大小写)</summary>
    public bool MatchesKeyword(string keyword)
    {
        if (string.IsNullOrEmpty(keyword)) return true;
        return (entry.TextContent ?? string.Empty)
            .IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
