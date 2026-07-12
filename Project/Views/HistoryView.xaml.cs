﻿﻿﻿﻿﻿﻿﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ToolBox.Services;

namespace ToolBox.Views;

public partial class HistoryView : UserControl
{
    private const int PageSize = 16;
    private List<CacheItem> allItems = new();
    private List<CacheItem> filteredItems = new();
    private int loadedCount;
    private string currentFilter = "month";
    private string currentViewMode = "grid";

    public HistoryView()
    {
        InitializeComponent();
        Loaded += (s, e) =>
        {
            CacheManager.Instance.OnScrapCached += OnScrapCached;
            LoadHistory();
        };
        Unloaded += (s, e) => CacheManager.Instance.OnScrapCached -= OnScrapCached;
    }

    /// <summary>
    /// 新截图写入缓存后自动刷新历史列表（无论是否手动保存都会显示）。
    /// </summary>
    private void OnScrapCached(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(LoadHistory);
    }

    private void LoadHistory()
    {
        allItems.Clear();
        var cachePath = CacheManager.CachePath;

        if (!Directory.Exists(cachePath))
        {
            lblSubtitle.Text = string.Format((FindResource("Lang_RecordCount") as string) ?? "{0} 条记录", 0);
            lblEmpty.Visibility = Visibility.Visible;
            btnLoadMore.Visibility = Visibility.Collapsed;
            lstHistory.Items.Clear();
            return;
        }

        var directories = Directory.GetDirectories(cachePath, "*", SearchOption.TopDirectoryOnly);
        foreach (var dir in directories)
        {
            var item = CacheItem.Read(dir);
            if (item?.IsValid == true)
                allItems.Add(item);
        }

        allItems.Sort((a, b) => b.CreateTime.CompareTo(a.CreateTime));
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var now = DateTime.Now;
        filteredItems = currentFilter switch
        {
            "today" => allItems.FindAll(i => i.CreateTime.Date == now.Date),
            "week" => allItems.FindAll(i => (now - i.CreateTime).TotalDays <= 7),
            "month" => allItems.FindAll(i => (now - i.CreateTime).TotalDays <= 30),
            _ => allItems
        };

        lstHistory.Items.Clear();
        loadedCount = 0;
        LoadNextPage();

        lblSubtitle.Text = string.Format((FindResource("Lang_RecordCount") as string) ?? "{0} 条记录", filteredItems.Count);
        lblEmpty.Visibility = filteredItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LoadNextPage()
    {
        var remaining = filteredItems.Count - loadedCount;
        if (remaining <= 0) return;

        var take = Math.Min(PageSize, remaining);
        for (int i = 0; i < take; i++)
        {
            var item = filteredItems[loadedCount + i];
            var image = item.ReadImage();
            if (image != null)
            {
                var vm = new HistoryItemViewModel
                {
                    CacheItem = item,
                    Thumbnail = ImageHelper.MakeOpaque(image),
                    FullImage = image,
                    TimeDisplay = item.CreateTime.ToString("MM-dd HH:mm"),
                    SizeDisplay = image.PixelWidth + " × " + image.PixelHeight
                };
                lstHistory.Items.Add(vm);
            }
        }

        loadedCount += take;
        btnLoadMore.Visibility = loadedCount < filteredItems.Count ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DateFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (DateToday?.IsChecked == true) currentFilter = "today";
        else if (DateWeek?.IsChecked == true) currentFilter = "week";
        else if (DateMonth?.IsChecked == true) currentFilter = "month";
        else if (DateAll?.IsChecked == true) currentFilter = "all";
        ApplyFilter();
    }

    private void ViewToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string v && v != currentViewMode)
        {
            currentViewMode = v;

            if (v == "grid")
            {
                lstHistory.ItemsPanel = (ItemsPanelTemplate)FindResource("HistoryGridPanel");
                lstHistory.ItemTemplate = (DataTemplate)FindResource("HistoryGridTemplate");
                ViewGridBtn.Opacity = 1;
                ViewListBtn.Opacity = 0.5;
            }
            else
            {
                lstHistory.ItemsPanel = (ItemsPanelTemplate)FindResource("HistoryListPanel");
                lstHistory.ItemTemplate = (DataTemplate)FindResource("HistoryListTemplate");
                ViewGridBtn.Opacity = 0.5;
                ViewListBtn.Opacity = 1;
            }

            ApplyFilter();
        }
    }

    private void BtnLoadMore_Click(object sender, RoutedEventArgs e) => LoadNextPage();

    /// <summary>
    /// 点击历史缩略图查看原图（带 alpha 的原图）。
    /// </summary>
    private void HistoryItem_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HistoryItemViewModel vm && vm.FullImage != null)
        {
            var preview = new ImagePreviewWindow(vm.FullImage);
            preview.ShowDialog();
        }
    }

    private void HistoryItem_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not HistoryItemViewModel vm || vm.FullImage == null) return;

        e.Handled = true;
        var contextMenu = new System.Windows.Controls.ContextMenu();

        var copyItem = new System.Windows.Controls.MenuItem { Header = (FindResource("Lang_Copy") as string) ?? "复制" };
        copyItem.Click += (_, _) => CopyBitmapToClipboard(vm.FullImage);
        contextMenu.Items.Add(copyItem);

        var saveItem = new System.Windows.Controls.MenuItem { Header = (FindResource("Lang_SaveAs") as string) ?? "另存为" };
        saveItem.Click += (_, _) => SaveBitmapToFile(vm.FullImage);
        contextMenu.Items.Add(saveItem);

        contextMenu.PlacementTarget = sender as UIElement;
        contextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse;
        contextMenu.IsOpen = true;
    }

    private static void CopyBitmapToClipboard(BitmapSource source)
    {
        try
        {
            Clipboard.Clear();
            Clipboard.SetImage(source);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ToolBox] copy failed: {ex.Message}"); }
    }

    private static void SaveBitmapToFile(BitmapSource source)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PNG 图片|*.png|JPEG 图片|*.jpg;*.jpeg|所有文件|*.*",
            DefaultExt = ".png",
            FileName = "截图_" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".png"
        };
        if (dialog.ShowDialog() != true) return;

        using var fs = new System.IO.FileStream(dialog.FileName, System.IO.FileMode.Create);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        encoder.Save(fs);
    }
}

public class HistoryItemViewModel
{
    public CacheItem CacheItem { get; set; } = null!;
    public BitmapSource Thumbnail { get; set; } = null!;
    public BitmapSource FullImage { get; set; } = null!;
    public string TimeDisplay { get; set; } = "";
    public string SizeDisplay { get; set; } = "";
    public string PositionDisplay { get; set; } = "";
}
