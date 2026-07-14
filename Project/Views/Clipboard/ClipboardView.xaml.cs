// ClipboardView.xaml.cs - 工作台剪贴板历史页面
// 职责:列表展示 + 类型/日期/搜索筛选 + 分页加载 + 详情面板 + 增删改操作
// 设计要点:
//   - 复用 ClipboardEntryViewModel(定义于 ClipboardPopup.xaml.cs,同命名空间)
//   - 订阅 ClipboardStore.Instance.EntriesChanged 实时刷新
//   - PageSize=20,按"加载更多"分页追加,符合规格 AC3.2
//   - "发送到 AI" 复用 ClipboardPopup 中的模式:Workbench.LoadPage(assistant) + ChatView.SetInput
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

using ToolBox.Core.ClipboardHistory;

namespace ToolBox.Views.ClipboardHistory;

public partial class ClipboardView : UserControl
{
    private const int PAGE_SIZE = 20;
    private const int PAUSE_MINUTES = 5;

    private List<ClipboardEntryViewModel> allViews = new();
    private List<ClipboardEntryViewModel> filteredViews = new();
    private int loadedCount;
    private string currentDateFilter = "month";
    private ClipboardEntryViewModel? selectedVm;

    public ClipboardView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ClipboardStore.Instance.EntriesChanged += OnEntriesChanged;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ClipboardStore.Instance.EntriesChanged -= OnEntriesChanged;
    }

    private void OnEntriesChanged()
    {
        // 跨线程触发,统一派发到 UI 线程
        // Refresh 返回 ClipboardView(链式),用 lambda 包装以匹配 Action 委托
        Dispatcher.BeginInvoke(new Action(() => Refresh()));
    }

    /// <summary>重新加载全部条目并应用筛选</summary>
    private ClipboardView Refresh()
    {
        allViews = ClipboardStore.Instance.Entries
            .Select(e => new ClipboardEntryViewModel(e))
            .ToList();
        ApplyFilter();
        return this;
    }

    private ClipboardView ApplyFilter()
    {
        var keyword = SearchBox.Text?.Trim() ?? string.Empty;
        var typeFilter = GetCurrentTypeFilter();
        var now = DateTime.Now;

        IEnumerable<ClipboardEntryViewModel> filtered = allViews;

        // 类型筛选
        if (typeFilter.HasValue)
            filtered = filtered.Where(v => v.EntryType == typeFilter.Value);

        // 日期筛选
        filtered = currentDateFilter switch
        {
            "today" => filtered.Where(v => v.Source.CapturedAt.Date == now.Date),
            "week" => filtered.Where(v => (now - v.Source.CapturedAt).TotalDays <= 7),
            "month" => filtered.Where(v => (now - v.Source.CapturedAt).TotalDays <= 30),
            _ => filtered
        };

        // 关键词搜索
        if (!string.IsNullOrEmpty(keyword))
            filtered = filtered.Where(v => v.MatchesKeyword(keyword));

        filteredViews = filtered.ToList();

        lstClipboard.Items.Clear();
        loadedCount = 0;
        LoadNextPage();

        UpdateStatusBar();
        return this;
    }

    private ClipboardView LoadNextPage()
    {
        var remaining = filteredViews.Count - loadedCount;
        if (remaining <= 0) return this;

        var take = Math.Min(PAGE_SIZE, remaining);
        for (int i = 0; i < take; i++)
            lstClipboard.Items.Add(filteredViews[loadedCount + i]);

        loadedCount += take;

        btnLoadMore.Visibility = loadedCount < filteredViews.Count
            ? Visibility.Visible : Visibility.Collapsed;
        return this;
    }

    private EClipboardEntryType? GetCurrentTypeFilter()
    {
        if (TypeText.IsChecked == true) return EClipboardEntryType.Text;
        if (TypeImage.IsChecked == true) return EClipboardEntryType.Image;
        if (TypeFile.IsChecked == true) return EClipboardEntryType.FileList;
        return null;
    }

    private ClipboardView UpdateStatusBar()
    {
        var stats = ClipboardStore.Instance.GetStats();
        lblSubtitle.Text = string.Format(
            (FindResource("Lang_RecordCount") as string) ?? "{0} 条记录",
            filteredViews.Count);
        lblEmpty.Visibility = filteredViews.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        lblStatus.Text = $"共 {stats.total} 条 · 已置顶 {stats.pinned} · 已收藏 {stats.favorite}";
        return this;
    }

    private void OnTypeFilterChanged(object sender, RoutedEventArgs e) => ApplyFilter();

    private void OnDateFilterChanged(object sender, RoutedEventArgs e)
    {
        if (DateToday?.IsChecked == true) currentDateFilter = "today";
        else if (DateWeek?.IsChecked == true) currentDateFilter = "week";
        else if (DateMonth?.IsChecked == true) currentDateFilter = "month";
        else if (DateAll?.IsChecked == true) currentDateFilter = "all";
        ApplyFilter();
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnLoadMoreClick(object sender, RoutedEventArgs e) => LoadNextPage();

    /// <summary>暂停监听 5 分钟(隐私场景)</summary>
    private void OnPauseMonitorClick(object sender, RoutedEventArgs e)
    {
        ClipboardMonitor.Instance.PauseFor(TimeSpan.FromMinutes(PAUSE_MINUTES));
        lblStatus.Text = $"已暂停监听 {PAUSE_MINUTES} 分钟";
    }

    private void OnEntrySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (lstClipboard.SelectedItem is ClipboardEntryViewModel vm)
            ShowDetail(vm);
        else
            ClearDetail();
    }

    private void OnEntryClicked(object sender, MouseButtonEventArgs e)
    {
        // 仅作选择,SelectionChanged 已处理详情显示
    }

    private ClipboardView ShowDetail(ClipboardEntryViewModel vm)
    {
        selectedVm = vm;
        var entry = vm.Source;

        lblDetailEmpty.Visibility = Visibility.Collapsed;
        DetailContent.Visibility = Visibility.Visible;
        DetailActions.Visibility = Visibility.Visible;

        lblDetailTitle.Text = vm.DisplayTitle;
        lblDetailMeta.Text = $"{vm.TypeBadge} · {entry.CapturedAt:yyyy-MM-dd HH:mm:ss}" +
            (string.IsNullOrEmpty(entry.SourceApp) ? "" : $" · {entry.SourceApp}");

        // 图片类型显示缩略图,文本/文件显示完整内容
        if (entry.EntryType == EClipboardEntryType.Image
            && !string.IsNullOrEmpty(entry.ThumbnailPath)
            && File.Exists(entry.ThumbnailPath))
        {
            DetailImage.Visibility = Visibility.Visible;
            imgDetail.Source = LoadImageFrozen(entry.ThumbnailPath);
            lblDetailText.Text = string.Empty;
        }
        else
        {
            DetailImage.Visibility = Visibility.Collapsed;
            lblDetailText.Text = entry.TextContent ?? string.Empty;
        }
        return this;
    }

    private ClipboardView ClearDetail()
    {
        selectedVm = null;
        lblDetailEmpty.Visibility = Visibility.Visible;
        DetailContent.Visibility = Visibility.Collapsed;
        DetailImage.Visibility = Visibility.Collapsed;
        DetailActions.Visibility = Visibility.Collapsed;
        lblDetailText.Text = string.Empty;
        imgDetail.Source = null;
        return this;
    }

    private void OnDetailCopyClick(object sender, RoutedEventArgs e)
    {
        if (selectedVm == null) return;
        var entry = selectedVm.Source;
        try
        {
            // 写入剪贴板前 IgnoreNext,避免自身写入被监听器重新记录
            ClipboardMonitor.Instance.IgnoreNext();
            switch (entry.EntryType)
            {
                case EClipboardEntryType.Text:
                    Clipboard.SetText(entry.TextContent ?? string.Empty);
                    break;
                case EClipboardEntryType.FileList:
                    var files = new System.Collections.Specialized.StringCollection();
                    foreach (var line in (entry.TextContent ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        files.Add(line.Trim());
                    if (files.Count > 0) Clipboard.SetFileDropList(files);
                    break;
                case EClipboardEntryType.Image:
                    if (!string.IsNullOrEmpty(entry.ThumbnailPath) && File.Exists(entry.ThumbnailPath))
                        Clipboard.SetImage(LoadImageFrozen(entry.ThumbnailPath));
                    break;
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ToolBox] copy failed: {ex.Message}"); }
    }

    private void OnDetailFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (selectedVm != null)
            ClipboardStore.Instance.ToggleFavorite(selectedVm.Id);
    }

    private void OnDetailPinClick(object sender, RoutedEventArgs e)
    {
        if (selectedVm != null)
            ClipboardStore.Instance.TogglePin(selectedVm.Id);
    }

    private void OnDetailDeleteClick(object sender, RoutedEventArgs e)
    {
        if (selectedVm != null)
        {
            var id = selectedVm.Id;
            ClearDetail();
            ClipboardStore.Instance.Delete(id);
        }
    }

    /// <summary>"发送到 AI 助手":打开 Workbench assistant 页并填入文本</summary>
    private void OnDetailSendToAiClick(object sender, RoutedEventArgs e)
    {
        if (selectedVm == null) return;
        var entry = selectedVm.Source;
        var text = entry.EntryType == EClipboardEntryType.Image
            ? "[图片内容]"
            : entry.TextContent ?? string.Empty;

        var workbench = App.Workbench;
        if (workbench == null) return;
        workbench.Show();
        workbench.Activate();
        workbench.LoadPage("assistant");

        if (workbench.PageHost.Content is Views.Chat.ChatView chat)
            chat.SetInput(text);
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
}
