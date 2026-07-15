using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ToolBox.Core.Tags;
using ToolBox.Services;
using ToolBox.Services.Ocr;

namespace ToolBox.Views;

public partial class HistoryView : UserControl
{
    private const int PageSize = 16;
    private List<CacheItem> allItems = new();
    private List<CacheItem> filteredItems = new();
    private int loadedCount;
    private string currentFilter = "month";
    private string currentViewMode = "grid";
    private string currentTagFilter = ""; // empty = no tag filter
    private string searchText = "";

    public HistoryView()
    {
        InitializeComponent();
        Loaded += (s, e) =>
        {
            CacheManager.Instance.OnScrapCached += OnScrapCached;
            ScreenshotTagStore.Instance.EntriesChanged += OnTagsChanged;
            LoadHistory();
        };
        Unloaded += (s, e) =>
        {
            CacheManager.Instance.OnScrapCached -= OnScrapCached;
            ScreenshotTagStore.Instance.EntriesChanged -= OnTagsChanged;
        };
    }

    private void OnTagsChanged()
    {
        Dispatcher.Invoke(() =>
        {
            BuildTagCloud();
            ApplyFilter();
        });
    }

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
            lblSubtitle.Text = "0 条记录";
            lblEmpty.Visibility = Visibility.Visible;
            btnLoadMore.Visibility = Visibility.Collapsed;
            lstHistory.Items.Clear();
            BuildTagCloud();
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
        BuildTagCloud();
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

        // Tag filter
        if (!string.IsNullOrEmpty(currentTagFilter))
        {
            var store = ScreenshotTagStore.Instance;
            filteredItems = filteredItems.FindAll(item =>
            {
                var entry = store.Get(item.FolderPath);
                return entry != null && entry.Tags.Contains(currentTagFilter);
            });
        }

        // Search text filter
        if (!string.IsNullOrEmpty(searchText))
        {
            var lower = searchText.ToLowerInvariant();
            var store = ScreenshotTagStore.Instance;
            filteredItems = filteredItems.FindAll(item =>
            {
                var entry = store.Get(item.FolderPath);
                if (entry != null)
                {
                    if (entry.Notes.IndexOf(lower, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                    if (entry.Tags.Any(t => t.IndexOf(lower, StringComparison.OrdinalIgnoreCase) >= 0)) return true;
                }
                return false;
            });
        }

        lstHistory.Items.Clear();
        loadedCount = 0;
        LoadNextPage();

        lblSubtitle.Text = string.Format("{} 条记录", filteredItems.Count);
        lblEmpty.Visibility = filteredItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BuildTagCloud();
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
                    SizeDisplay = image.PixelWidth + " x " + image.PixelHeight
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

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        searchText = txtSearch.Text?.Trim() ?? "";
        ApplyFilter();
    }

    /// <summary>构建标签云（所有标签及其出现次数，点击筛选）</summary>
    private void BuildTagCloud()
    {
        TagCloudPanel.Children.Clear();

        // 清除筛选按钮
        var clearBtn = new Button
        {
            Content = "✕ 全部",
            Style = (Style)FindResource("BtnDefault"),
            Margin = new Thickness(0, 2, 6, 2),
            Tag = ""
        };
        clearBtn.Click += (s, e) => { currentTagFilter = ""; ApplyFilter(); };
        TagCloudPanel.Children.Add(clearBtn);

        var store = ScreenshotTagStore.Instance;
        var tagCounts = store.GetAllTagCounts()
            .OrderByDescending(kv => kv.Value)
            .Take(30)
            .ToList();

        foreach (var kv in tagCounts)
        {
            var btn = new Button
            {
                Content = "🏷 " + kv.Key + " (" + kv.Value + ")",
                Style = (Style)FindResource("BtnDefault"),
                Margin = new Thickness(0, 2, 6, 2),
                Tag = kv.Key
            };
            btn.Click += (s, e) =>
            {
                if (s is Button b && b.Tag is string tag)
                    currentTagFilter = currentTagFilter == tag ? "" : tag;
                ApplyFilter();
            };
            TagCloudPanel.Children.Add(btn);
        }

        // 如果没有标签，显示提示
        if (tagCounts.Count == 0)
        {
            var hint = new TextBlock
            {
                Text = "右键截图 → 管理标签 可添加标签",
                FontSize = 11,
                Foreground = (Brush)FindResource("TextTertiaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            TagCloudPanel.Children.Add(hint);
        }
    }

    private void BtnLoadMore_Click(object sender, RoutedEventArgs e) => LoadNextPage();

    private void HistoryItem_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HistoryItemViewModel vm && vm.FullImage != null)
        {
            var preview = new ImagePreviewWindow(vm.FullImage);
            preview.ShowDialog();
        }
    }

    /// <summary>为选中的截图打开标签编辑器弹窗</summary>
    private void OpenTagEditor(HistoryItemViewModel vm)
    {
        var cacheKey = vm.CacheItem.FolderPath;
        var store = ScreenshotTagStore.Instance;
        var entry = store.GetOrCreate(cacheKey);

        var window = new Window
        {
            Title = "管理标签",
            Width = 400,
            Height = 500,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
            Background = (Brush)FindResource("BgBrush")
        };

        var panel = new StackPanel { Margin = new Thickness(20) };

        // 标题
        panel.Children.Add(new TextBlock
        {
            Text = "🏷 标签管理",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            Margin = new Thickness(0, 0, 0, 12)
        });

        // 当前标签展示
        var tagsPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(tagsPanel);

        void RefreshTags()
        {
            tagsPanel.Children.Clear();
            foreach (var tag in entry.Tags)
            {
                var chip = new Border
                {
                    Background = (Brush)FindResource("AccentSoftBrush"),
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(8, 4, 12, 4),
                    Margin = new Thickness(0, 0, 6, 6)
                };
                var chipStack = new StackPanel { Orientation = Orientation.Horizontal };
                chipStack.Children.Add(new TextBlock
                {
                    Text = "🏷 " + tag,
                    FontSize = 12,
                    Foreground = (Brush)FindResource("TextPrimaryBrush")
                });
                var removeBtn = new Button
                {
                    Content = "✕",
                    FontSize = 10,
                    Margin = new Thickness(6, 0, 0, 0),
                    Style = (Style)FindResource("IconButton"),
                    Tag = tag
                };
                removeBtn.Click += (_, _) =>
                {
                    store.RemoveTag(cacheKey, tag);
                    entry = store.GetOrCreate(cacheKey);
                    RefreshTags();
                };
                chipStack.Children.Add(removeBtn);
                chip.Child = chipStack;
                tagsPanel.Children.Add(chip);
            }
        }
        RefreshTags();

        // 添加标签输入
        var addPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        var txtNewTag = new TextBox
        {
            Width = 260,
            Margin = new Thickness(0, 0, 8, 0),
            Background = (Brush)FindResource("BgSunkenBrush"),
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush")
        };
        var addBtn = new Button
        {
            Content = "添加",
            Style = (Style)FindResource("BtnPrimary")
        };
        addBtn.Click += (_, _) =>
        {
            var tag = txtNewTag.Text.Trim();
            if (!string.IsNullOrEmpty(tag))
            {
                store.AddTag(cacheKey, tag);
                entry = store.GetOrCreate(cacheKey);
                txtNewTag.Text = "";
                RefreshTags();
            }
        };
        addPanel.Children.Add(txtNewTag);
        addPanel.Children.Add(addBtn);
        panel.Children.Add(addPanel);

        // 备注
        panel.Children.Add(new TextBlock
        {
            Text = "备注",
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            Margin = new Thickness(0, 0, 0, 4)
        });
        var txtNotes = new TextBox
        {
            Text = entry.Notes,
            AcceptsReturn = true,
            Height = 80,
            Background = (Brush)FindResource("BgSunkenBrush"),
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush")
        };
        panel.Children.Add(txtNotes);

        // 保存按钮
        var saveBtn = new Button
        {
            Content = "保存",
            Style = (Style)FindResource("BtnPrimary"),
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        saveBtn.Click += (_, _) =>
        {
            store.SetNotes(cacheKey, txtNotes.Text);
            window.Close();
        };
        panel.Children.Add(saveBtn);

        window.Content = new ScrollViewer { Content = panel };
        window.ShowDialog();
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

        // 识别文字: 弹出 OCR 结果窗口(AC3.1 / AC3.2)
        var ocrItem = new System.Windows.Controls.MenuItem { Header = "识别文字" };
        ocrItem.Click += (_, _) => RecognizeHistoryImage(vm.FullImage);
        contextMenu.Items.Add(ocrItem);

        // 管理标签: 打开标签编辑器
        var tagItem = new System.Windows.Controls.MenuItem { Header = "🏷 管理标签" };
        tagItem.Click += (_, _) => OpenTagEditor(vm);
        contextMenu.Items.Add(tagItem);

        var deleteItem = new System.Windows.Controls.MenuItem { Header = (FindResource("Lang_Delete") as string) ?? "删除" };
        deleteItem.Click += (_, _) => DeleteHistoryItem(vm);
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(deleteItem);

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

    private void DeleteHistoryItem(HistoryItemViewModel vm)
    {
        try
        {
            if (Directory.Exists(vm.CacheItem.FolderPath))
                Directory.Delete(vm.CacheItem.FolderPath, true);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ToolBox] delete failed: {ex.Message}"); }
        lstHistory.Items.Remove(vm);
    }

    /// <summary>对历史截图执行 OCR 并弹出结果窗口(AC3.1 / AC3.2)。</summary>
    private void RecognizeHistoryImage(BitmapSource image)
    {
        if (image == null) return;
        var lang = Models.ToolBoxOption.Load().Data.OcrLanguage;
        var overlay = new Views.Ocr.OcrResultOverlay(image, lang) { Owner = Window.GetWindow(this) };
        overlay.Show();
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
