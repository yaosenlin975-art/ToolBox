﻿﻿﻿﻿﻿﻿﻿using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ToolBox.Models;

public class ScrapWindow : Window
{
    private readonly System.Windows.Controls.Image imageView;
    private bool closePrepare;
    private string scrapName;
    private DateTime creationTime;
    private int scale = 100;
    private bool solidFrame = true;
    private double activeOpacity = 1.0;
    private double inactiveOpacity = 1.0;
    private double rolloverOpacity = 1.0;
    private int activeMargin;
    private int inactiveMargin;
    private int rolloverMargin;
    private bool isMouseEnter;
    private bool isThumbnailMode;
    private bool isSelected;
    private System.Windows.Size originalSize;
    private BitmapSource fullBitmap;
    private BitmapSource sourceBitmap;
    private Point styleClickPoint;
    private int styleId;
    private List<IStyleItem> styleItems;
    private System.Windows.Threading.DispatcherTimer styleApplyTimer;
    private int styleApplyIndex;
    private bool isStyleApply;
    private bool initialized;

    public event ScrapEventHandler OnScrapClose;
    public event ScrapEventHandler OnScrapCreated;
    public void RaiseScrapCreated() => OnScrapCreated?.Invoke(this, new ScrapEventArgs { Scrap = this });
    public event ScrapEventHandler OnScrapActive;
    public event ScrapEventHandler OnScrapInactive;
    public event ScrapEventHandler OnScrapLocationChanged;
    public event ScrapEventHandler OnScrapImageChanged;
    public event ScrapEventHandler OnScrapStyleApplied;
    public event ScrapEventHandler OnScrapStyleRemoved;

    public ScrapBook Manager { get; set; }
    public CacheItem CacheItem { get; set; }
    public DateTime CreationTime { get => creationTime; set => creationTime = value; }
    public double ActiveOpacityValue { get => activeOpacity; set => activeOpacity = value; }
    public double InactiveOpacityValue { get => inactiveOpacity; set => inactiveOpacity = value; }
    public double RolloverOpacityValue { get => rolloverOpacity; set => rolloverOpacity = value; }
    public int ActiveMarginValue { get => activeMargin; set => activeMargin = value; }
    public int InactiveMarginValue { get => inactiveMargin; set => inactiveMargin = value; }
    public int RolloverMarginValue { get => rolloverMargin; set => rolloverMargin = value; }
    public bool SolidFrameValue { get => solidFrame; set => solidFrame = value; }
    public int ScaleValue { get => scale; set => scale = value; }
    public int StyleIdValue { get => styleId; set => styleId = value; }
    public Point StyleClickPointValue { get => styleClickPoint; set => styleClickPoint = value; }
    public bool InitializedValue { get => initialized; set => initialized = value; }
    public BitmapSource ImageViewSource => sourceBitmap;
    public bool IsTopmost { get; set; }

    public ScrapWindow SetTopmost(bool topmost)
    {
        IsTopmost = topmost;
        Topmost = topmost;
        return this;
    }

    public ScrapWindow()
    {
        scrapName = DateTime.Now.ToString("yyyyMMddHHmmssfff");
        creationTime = DateTime.Now;
        AllowsTransparency = true;
        WindowStyle = WindowStyle.None;
        ShowInTaskbar = false;
        Background = Brushes.Transparent;

        imageView = new System.Windows.Controls.Image();
        Content = imageView;

        MouseEnter += OnMouseEnterHandler;
        MouseLeave += OnMouseLeaveHandler;
        KeyDown += OnKeyDownHandler;
        MouseLeftButtonDown += OnMouseLeftButtonDownHandler;
        MouseDoubleClick += OnMouseDoubleClickHandler;
        MouseWheel += OnMouseWheelHandler;
        MouseRightButtonDown += OnMouseRightButtonDownHandler;
        Drop += OnDropHandler;
        DragEnter += OnDragEnterHandler;
        AllowDrop = true;

        LocationChanged += OnLocationChangedHandler;

        // 默认边框：中性灰，明显可见，且与选中态（DodgerBlue 2px）区分
        BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(180, 120, 120, 120));
        BorderThickness = new Thickness(1);
    }

    public BitmapSource GetViewImage() => sourceBitmap;

    /// <summary>
    /// 返回用于复制/保存的全分辨率原图。
    /// 缩略图模式下 fullBitmap 保留了进入缩略图前的原始像素，
    /// 非缩略图模式则直接返回当前显示的 sourceBitmap（即当前原图）。
    /// 这样无论是否处于缩略图模式，写入剪贴板/保存的都是原始分辨率，不会被降采样。
    /// </summary>
    public BitmapSource GetOriginalBitmap()
    {
        if (isThumbnailMode && fullBitmap != null)
            return fullBitmap;
        return sourceBitmap;
    }

    /// <summary>
    /// 将位图规范为 96 DPI 并保持原始像素尺寸，避免部分目标软件按 DPI 重新换算显示尺寸导致发糊。
    /// 若源图本身已是 96 DPI 则原样返回（不做任何重采样）。
    /// </summary>
    private static BitmapSource NormalizeTo96Dpi(BitmapSource source)
    {
        if (source == null) return null;
        if (Math.Abs(source.DpiX - 96.0) < 0.01 && Math.Abs(source.DpiY - 96.0) < 0.01)
            return source;

        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var format = source.Format;
        var stride = (width * format.BitsPerPixel + 7) / 8;
        var pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);
        var normalized = BitmapSource.Create(width, height, 96, 96, format, null, pixels, stride);
        normalized.Freeze();
        return normalized;
    }

    /// <summary>
    /// 将位图以 96 DPI 原分辨率写入剪贴板。
    /// </summary>
    private static void CopyBitmapToClipboard(BitmapSource source)
    {
        if (source == null) return;
        try
        {
            Clipboard.Clear();
            Clipboard.SetImage(NormalizeTo96Dpi(source));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ToolBox] 复制到剪贴板失败: {ex.Message}");
        }
    }

    public ScrapWindow CloseScrap()
    {
        closePrepare = true;
        Close();
        GC.Collect();
        return this;
    }

    public ScrapWindow SetImage(BitmapSource image)
    {
        sourceBitmap = image;
        imageView.Source = image;
        OnScrapImageChanged?.Invoke(this, new ScrapEventArgs { Scrap = this });
        return this;
    }

    public ScrapWindow SetImage(System.Drawing.Image image)
    {
        var bitmap = new System.Drawing.Bitmap(image);
        var hBitmap = bitmap.GetHbitmap();
        try
        {
            var bmpSource = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            bmpSource.Freeze();
            SetImage(bmpSource);
        }
        finally
        {
            DeleteObject(hBitmap);
            bitmap.Dispose();
        }
        return this;
    }

    public Window StyleForm { get; set; }

    public ScrapWindow RemoveStyle(Type styleItemType)
    {
        if (styleItems != null)
        {
            styleItems.RemoveAll(x => x.GetType() == styleItemType);
        }

        if (styleItems == null || styleItems.Count == 0)
        {
            styleId = 0;
            styleClickPoint = new Point(0, 0);
            OnScrapStyleRemoved?.Invoke(this, new ScrapEventArgs { Scrap = this });
        }
        return this;
    }

    public ScrapWindow ApplyStylesFromCache(CStyle style, Point clickPoint)
    {
        var items = new List<IStyleItem>(style.Items);
        return ApplyStyles(style.StyleId, items, clickPoint);
    }

    public BitmapSource GetThumbnail(int width = 230, int height = 150)
    {
        if (sourceBitmap == null) return null;

        var group = new TransformedBitmap(sourceBitmap,
            new ScaleTransform(
                (double)width / sourceBitmap.PixelWidth,
                (double)height / sourceBitmap.PixelHeight));

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen())
        {
            ctx.DrawImage(group, new Rect(0, 0, width, height));
        }
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    public ScrapWindow ApplyStyles(int newStyleId, List<IStyleItem> items, Point clickPoint)
    {
        styleId = newStyleId;
        styleClickPoint = clickPoint;
        styleItems = items;
        styleApplyIndex = 0;
        isStyleApply = true;

        styleApplyTimer = new System.Windows.Threading.DispatcherTimer();
        styleApplyTimer.Interval = TimeSpan.FromMilliseconds(100);
        styleApplyTimer.Tick += StyleApplyTimer_Tick;
        styleApplyTimer.Start();
        return this;
    }

    private void StyleApplyTimer_Tick(object sender, EventArgs e)
    {
        if (styleItems == null || styleApplyIndex >= styleItems.Count)
        {
            styleApplyTimer?.Stop();
            isStyleApply = false;
            OnScrapStyleApplied?.Invoke(this, new ScrapEventArgs { Scrap = this });
            return;
        }

        var item = styleItems[styleApplyIndex];
        item.Apply(this, styleClickPoint);
        styleApplyIndex++;
    }

    private void OnMouseEnterHandler(object sender, MouseEventArgs e)
    {
        isMouseEnter = true;
        if (Manager != null && !Manager.IsActiveScrap(this))
            Opacity = rolloverOpacity;
    }

    private void OnMouseLeaveHandler(object sender, MouseEventArgs e)
    {
        isMouseEnter = false;
        if (Manager != null && !Manager.IsActiveScrap(this))
            Opacity = inactiveOpacity;
    }

    private void OnKeyDownHandler(object sender, KeyEventArgs e)
    {
        if (Manager == null) return;
        var key = e.Key;
        if (key == Key.System) key = e.SystemKey;

        var modifiers = Keyboard.Modifiers;

        if (modifiers.HasFlag(ModifierKeys.Control) && key == Key.C)
        {
            CopyToClipboard();
            e.Handled = true;
            return;
        }

        if (modifiers.HasFlag(ModifierKeys.Control) && key == Key.S)
        {
            SaveToFile();
            e.Handled = true;
            return;
        }

        if (modifiers.HasFlag(ModifierKeys.Control) && key == Key.X)
        {
            CopyToClipboard();
            CloseScrap();
            e.Handled = true;
            return;
        }

        if (key == Key.Escape)
        {
            CloseScrap();
            e.Handled = true;
            return;
        }

        if (modifiers.HasFlag(ModifierKeys.Control) && key == Key.V)
        {
            Manager.BindForm?.PasteImage();
            e.Handled = true;
            return;
        }

        var combined = key;
        if (modifiers.HasFlag(ModifierKeys.Control)) combined |= Key.LeftCtrl;
        if (modifiers.HasFlag(ModifierKeys.Shift)) combined |= Key.LeftShift;
        if (modifiers.HasFlag(ModifierKeys.Alt)) combined |= Key.LeftAlt;

        Manager.OnKeyUp(this, combined);
    }

    private void SaveToFile()
    {
        var bitmap = GetOriginalBitmap();
        if (bitmap == null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PNG Image|*.png",
            FileName = "ToolBox_" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".png"
        };
        if (dialog.ShowDialog() == true)
        {
            using var stream = new System.IO.FileStream(dialog.FileName, System.IO.FileMode.Create);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(stream);
        }
    }

    private void CopyToClipboard()
    {
        CopyBitmapToClipboard(GetOriginalBitmap());
    }

    private void OnMouseLeftButtonDownHandler(object sender, MouseButtonEventArgs e)
    {
        // Select this scrap, deselect others
        Manager?.SelectScrap(this);
        isSelected = true;
        Activate();
        Focus();
        Keyboard.Focus(imageView);
        DragMove();
    }

    private System.Windows.Point originalPosition;

    
    private void OnMouseDoubleClickHandler(object sender, MouseButtonEventArgs e)
    {
        if (sourceBitmap == null) return;

        if (isThumbnailMode)
        {
            if (fullBitmap != null)
            {
                SetImage(fullBitmap);
                Width = originalSize.Width;
                Height = originalSize.Height;
                Left = originalPosition.X;
                Top = originalPosition.Y;
            }
            isThumbnailMode = false;
            fullBitmap = null;
        }
        else
        {
            fullBitmap = sourceBitmap;
            originalSize = new System.Windows.Size(Width, Height);
            originalPosition = new System.Windows.Point(Left, Top);

            // 点击点在 sourceBitmap 像素空间中的坐标
            var pos = e.GetPosition(imageView);
            var srcX = (int)(pos.X / ActualWidth * sourceBitmap.PixelWidth);
            var srcY = (int)(pos.Y / ActualHeight * sourceBitmap.PixelHeight);

            // 50x50 裁切窗口, 以点击点为中心 (边界附近缩到图像内侧)
            var cropW = Math.Min(50, sourceBitmap.PixelWidth);
            var cropH = Math.Min(50, sourceBitmap.PixelHeight);
            var cropX = Math.Max(0, Math.Min(sourceBitmap.PixelWidth - cropW, srcX - cropW / 2));
            var cropY = Math.Max(0, Math.Min(sourceBitmap.PixelHeight - cropH, srcY - cropH / 2));
            var crop = new CroppedBitmap(sourceBitmap, new Int32Rect(cropX, cropY, cropW, cropH));
            crop.Freeze();
            SetImage(crop);

            // 窗口缩为 50x50 屏幕像素，裁切区域左上角对齐点击点
            var cropLocalX = srcX - cropX;
            var cropLocalY = srcY - cropY;
            var screenClick = PointToScreen(e.GetPosition(this));
            Left = screenClick.X - cropLocalX;
            Top = screenClick.Y - cropLocalY;
            Width = 50;
            Height = 50;
            isThumbnailMode = true;
        }
    }


    public ScrapWindow SetSelected(bool selected)
    {
        isSelected = selected;
        if (selected)
        {
            BorderBrush = new SolidColorBrush(Colors.DodgerBlue);
            BorderThickness = new Thickness(2);
        }
        else
        {
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(180, 120, 120, 120));
            BorderThickness = new Thickness(1);
        }
        return this;
    }

    private void OnMouseWheelHandler(object sender, MouseWheelEventArgs e)
    {
        if (Manager == null || Manager.BindForm == null) return;
        var options = Manager.BindForm.GetOptions();
        if (options == null || options.Scrap.SubMenuStyles.Count == 0) return;

        var currentIdx = options.Scrap.SubMenuStyles.IndexOf(styleId);
        if (currentIdx < 0) currentIdx = 0;

        if (e.Delta > 0)
        {
            currentIdx = (currentIdx + 1) % options.Scrap.SubMenuStyles.Count;
        }
        else
        {
            currentIdx = (currentIdx - 1 + options.Scrap.SubMenuStyles.Count) % options.Scrap.SubMenuStyles.Count;
        }

        var newStyleId = options.Scrap.SubMenuStyles[currentIdx];
        var style = options.FindStyle(newStyleId);
        if (style != null)
        {
            style.Apply(this, new Point(0, 0));
        }
    }

    private void OnMouseRightButtonDownHandler(object sender, MouseButtonEventArgs e)
    {
        ShowContextMenu();
    }

    private void ShowContextMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu
        {
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("BgSecondaryBrush"),
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextPrimaryBrush"),
            BorderBrush = (System.Windows.Media.Brush)Application.Current.FindResource("BorderBrush"),
            Padding = new System.Windows.Thickness(4),
            Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse
        };

        if (Manager?.BindForm?.GetOptions()?.Scrap.SubMenuStyles != null)
        {
            var options = Manager.BindForm.GetOptions();
            foreach (var styleId in options.Scrap.SubMenuStyles)
            {
                var style = options.FindStyle(styleId);
                if (style == null) continue;

                var menuItem = new System.Windows.Controls.MenuItem
                {
                    Header = style.StyleName,
                    Tag = style
                };
                menuItem.Click += (s, e) =>
                {
                    var clickedStyle = (CStyle)((System.Windows.Controls.MenuItem)s).Tag;
                    clickedStyle.Apply(this, new Point(0, 0));
                };
                menu.Items.Add(menuItem);
            }
        }

        if (menu.Items.Count > 0)
        {
            menu.Items.Add(new System.Windows.Controls.Separator());
        }

        var copyItem = new System.Windows.Controls.MenuItem { Header = "复制" };
        copyItem.Click += (s, e) =>
        {
            CopyBitmapToClipboard(GetOriginalBitmap());
        };
        menu.Items.Add(copyItem);

        var cutItem = new System.Windows.Controls.MenuItem { Header = "剪切" };
        cutItem.Click += (s, e) =>
        {
            CopyBitmapToClipboard(GetOriginalBitmap());
            CloseScrap();
        };
        menu.Items.Add(cutItem);

        var saveItem = new System.Windows.Controls.MenuItem { Header = "另存为" };
        saveItem.Click += (s, e) =>
        {
            var bitmap = GetOriginalBitmap();
            if (bitmap == null) return;
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PNG 图片|*.png|JPEG 图片|*.jpg;*.jpeg|所有文件|*.*",
                DefaultExt = ".png",
                FileName = "截图_" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".png"
            };
            if (dialog.ShowDialog() == true)
            {
                using var fs = new System.IO.FileStream(dialog.FileName, System.IO.FileMode.Create);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(fs);
            }
        };
        menu.Items.Add(saveItem);

        var todoItem = new System.Windows.Controls.MenuItem { Header = "代办识别" };
        todoItem.Click += async (s, e) =>
        {
            var bitmap = GetOriginalBitmap();
            if (bitmap == null) return;
            try
            {
                using var ms = new System.IO.MemoryStream();
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bitmap));
                enc.Save(ms);
                var imageBytes = ms.ToArray();

                var session = await Core.Llm.ChatManager.Instance.CreateSessionAsync("代办识别");
                var imageDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ToolBox", "sessions", session.Id, "images");
                System.IO.Directory.CreateDirectory(imageDir);
                var imagePath = System.IO.Path.Combine(imageDir, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}.png");
                System.IO.File.WriteAllBytes(imagePath, imageBytes);

                session.Messages.Add(new Core.Llm.ChatMessage
                {
                    Role = "user",
                    Content = "请分析这张图片中的内容，识别出需要作为待办事项的任务。如果有，请使用 add_todo 工具创建对应的待办。",
                    ImagePath = imagePath
                });

                App.CompactToolbox?.SwitchToTab("chat");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ToolBox] Todo identification failed: {ex.Message}");
            }
        };
        menu.Items.Add(todoItem);

        menu.Items.Add(new System.Windows.Controls.Separator());

        var chatItem = new System.Windows.Controls.MenuItem { Header = "发起对话" };
        chatItem.Click += (s, e) => StartChat();
        menu.Items.Add(chatItem);

        var closeItem = new System.Windows.Controls.MenuItem { Header = "关闭" };
        closeItem.Click += (s, e) => CloseScrap();
        menu.Items.Add(closeItem);

        menu.IsOpen = true;
    }


    private void OnDropHandler(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null)
                foreach (var file in files)
                    Manager?.AddDragImageFileName(file);
        }
        else if (e.Data.GetDataPresent(DataFormats.Html))
        {
            var htmlContent = e.Data.GetData(DataFormats.Html) as string;
            if (htmlContent != null)
            {
                var match = Regex.Match(htmlContent,
                    "<img.*?(width=\"(?<width>.*?)\".*?)?(height=\"(?<height>.*?)\".*?)?src=\"(?<src>.*?)\"",
                    RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    int.TryParse(match.Groups["width"].Value, out var width);
                    int.TryParse(match.Groups["height"].Value, out var height);
                    var url = match.Groups["src"].Value;
                    Manager?.AddDragImageUrl(url, width, height);
                }
            }
        }
    }

    private void OnDragEnterHandler(object sender, DragEventArgs e)
    {
        if (Manager != null && Manager.IsImageDrag)
            e.Effects = DragDropEffects.Copy;
        else
            e.Effects = DragDropEffects.None;
    }

    private void OnLocationChangedHandler(object sender, EventArgs e)
    {
        OnScrapLocationChanged?.Invoke(this, new ScrapEventArgs { Scrap = this });
    }

    protected override void OnClosed(EventArgs e)
    {
        Services.LayerManager.Instance.UnregisterWindow(this);
        OnScrapClose?.Invoke(this, new ScrapEventArgs { Scrap = this });
        base.OnClosed(e);
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private void StartChat()
    {
        var bitmap = GetOriginalBitmap();
        if (bitmap == null) return;

        // Save bitmap to temp file
        var tempPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ToolBox_" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".png");

        using (var fs = new System.IO.FileStream(tempPath, System.IO.FileMode.Create))
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(fs);
        }

        // Create session with image
        var session = Core.Llm.ChatManager.Instance.CreateSessionWithImage(
            System.IO.File.ReadAllBytes(tempPath), "截图对话");


        // Clean up temp file
        try { System.IO.File.Delete(tempPath); } catch (Exception) { /* temp file cleanup is best-effort */ }
        // Open chat window
        App.CompactToolbox?.SwitchToTab("chat");
    }

}













