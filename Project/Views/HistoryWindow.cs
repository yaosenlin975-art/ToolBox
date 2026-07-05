using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using ToolBox.Services;

namespace ToolBox.Views;

public partial class HistoryWindow : Window
{
    private const int PageSize = 10;
    private List<CacheItem> allItems = new();
    private int loadedCount;
    private Action<BitmapSource, int, int> onItemLoad;

    public ICommand LoadCommand { get; }

    public HistoryWindow(Action<BitmapSource, int, int> onItemLoad)
    {
        InitializeComponent();
        this.onItemLoad = onItemLoad;
        LoadCommand = new RelayCommand<HistoryItemViewModel>(LoadItem);
        LoadHistory();
    }

    private void LoadHistory()
    {
        allItems.Clear();
        var cachePath = CacheManager.CachePath;

        if (!Directory.Exists(cachePath))
        {
            lblSubtitle.Text = "0 records";
            lblEmpty.Visibility = Visibility.Visible;
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

        if (allItems.Count == 0)
        {
            UpdateStateNoItems();
            lblEmpty.Visibility = Visibility.Visible;
            return;
        }

        loadedCount = 0;
        LoadNextPage();
    }

    private void LoadNextPage()
    {
        var remaining = allItems.Count - loadedCount;
        if (remaining <= 0) return;

        var take = Math.Min(PageSize, remaining);
        for (int i = 0; i < take; i++)
        {
            var item = allItems[loadedCount + i];
            var image = item.ReadImage();
            if (image != null)
            {
                var vm = new HistoryItemViewModel
                {
                    CacheItem = item,
                    Thumbnail = image,
                    TimeDisplay = item.CreateTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    SizeDisplay = $"{image.PixelWidth} x {image.PixelHeight}",
                    PositionDisplay = $"位置: ({(int)item.Position.X}, {(int)item.Position.Y})"
                };
                lstHistory.Items.Add(vm);
            }
        }

        loadedCount += take;
        UpdateState();
    }

    private void UpdateStateNoItems()
    {
        lblSubtitle.Text = "0 records";
        btnLoadMore.Visibility = Visibility.Collapsed;
        lblCount.Visibility = Visibility.Collapsed;
    }

    private void UpdateState()
    {
        lblSubtitle.Text = $"{allItems.Count} records";
        var hasMore = loadedCount < allItems.Count;
        btnLoadMore.Visibility = hasMore ? Visibility.Visible : Visibility.Collapsed;
        lblCount.Visibility = Visibility.Visible;
        lblCount.Text = $"已加载 {loadedCount}/{allItems.Count}";
    }

    private void LoadItem(HistoryItemViewModel vm)
    {
        if (vm?.CacheItem == null) return;
        var image = vm.CacheItem.ReadImage();
        if (image != null)
        {
            onItemLoad?.Invoke(image,
                (int)vm.CacheItem.Position.X,
                (int)vm.CacheItem.Position.Y);
        }
    }

    private void BtnLoadMore_Click(object sender, RoutedEventArgs e)
    {
        LoadNextPage();
    }
}

public class RelayCommand<T> : ICommand
{
    private readonly Action<T> execute;

    public RelayCommand(Action<T> execute)
    {
        this.execute = execute;
    }

    public event EventHandler CanExecuteChanged;

    public bool CanExecute(object parameter) => true;

    public void Execute(object parameter)
    {
        if (parameter is T typed)
            execute(typed);
    }
}

