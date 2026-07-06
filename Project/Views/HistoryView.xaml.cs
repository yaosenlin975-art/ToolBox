using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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
    private string searchText = "";

    public HistoryView()
    {
        InitializeComponent();
        Loaded += (s, e) => LoadHistory();
    }

    private void LoadHistory()
    {
        allItems.Clear();
        var cachePath = CacheManager.CachePath;

        if (!Directory.Exists(cachePath))
        {
            lblSubtitle.Text = "0 条记录";
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

        if (!string.IsNullOrEmpty(searchText))
            filteredItems = filteredItems.FindAll(MatchesSearch);

        lstHistory.Items.Clear();
        loadedCount = 0;
        LoadNextPage();

        lblSubtitle.Text = filteredItems.Count + " 条记录";
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
                    Thumbnail = image,
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
        if (sender is Button btn && btn.Tag is string v)
        {
            ViewGridBtn.Opacity = v == "grid" ? 1 : 0.5;
            ViewListBtn.Opacity = v == "list" ? 1 : 0.5;
        }
    }

    private void BtnLoadMore_Click(object sender, RoutedEventArgs e) => LoadNextPage();

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        searchText = SearchBox.Text.Trim();
        ApplyFilter();
    }

    private bool MatchesSearch(CacheItem item)
    {
        if (string.IsNullOrEmpty(searchText)) return true;
        return item.CreateTime.ToString("MM-dd HH:mm").Contains(searchText, StringComparison.OrdinalIgnoreCase);
    }
}

public class HistoryItemViewModel
{
    public CacheItem CacheItem { get; set; } = null!;
    public BitmapSource Thumbnail { get; set; } = null!;
    public string TimeDisplay { get; set; } = "";
    public string SizeDisplay { get; set; } = "";
    public string PositionDisplay { get; set; } = "";
}