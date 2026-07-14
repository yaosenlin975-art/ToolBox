﻿﻿﻿﻿﻿﻿﻿﻿using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ToolBox.Services.Ocr;
using ToolBox.Views.Ocr;

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

    // OCR 选字模式状态(AC2.1): isOcrSelectMode 表示已进入选字模式,
    // isOcrSelecting 表示正在拖拽选区,ocrSelectStart 记录起点
    private bool isOcrSelectMode;
    private bool isOcrSelecting;
    private Point ocrSelectStart;
    private OcrSelectAdorner? ocrSelectAdorner;

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
        MouseLeftButtonUp += OnMouseLeftButtonUpHandler;
        MouseMove += OnMouseMoveHandler;
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
        if (sourceBitmap != null)
            imageView.ToolTip = $"{sourceBitmap.PixelWidth} × {sourceBitmap.PixelHeight}";
    }

    private void OnMouseLeaveHandler(object sender, MouseEventArgs e)
    {
        isMouseEnter = false;
        if (Manager != null && !Manager.IsActiveScrap(this))
            Opacity = inactiveOpacity;
        imageView.ToolTip = null;
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
            // 选字模式下 ESC 先退出选字模式,不关闭贴图
            if (isOcrSelectMode)
            {
                ExitOcrSelectMode();
                e.Handled = true;
                return;
            }
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
        // 选字模式: 开始拖拽选区(不进入 DragMove)
        if (isOcrSelectMode)
        {
            Manager?.SelectScrap(this);
            isSelected = true;
            Activate();
            Focus();
            Keyboard.Focus(imageView);
            ocrSelectStart = e.GetPosition(imageView);
            isOcrSelecting = true;
            EnsureOcrAdorner();
            ocrSelectAdorner?.Update(ocrSelectStart, ocrSelectStart);
            e.Handled = true;
            return;
        }

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

    // ===== OCR 选字模式(AC2.1 / AC2.2) =====

    /// <summary>进入选字模式: 切换光标为十字,准备 Adorner。</summary>
    public ScrapWindow EnterOcrSelectMode()
    {
        if (isOcrSelectMode) return this;
        isOcrSelectMode = true;
        Cursor = Cursors.Cross;
        EnsureOcrAdorner();
        return this;
    }

    /// <summary>退出选字模式: 还原光标,清除 Adorner。</summary>
    public ScrapWindow ExitOcrSelectMode()
    {
        if (!isOcrSelectMode) return this;
        isOcrSelectMode = false;
        isOcrSelecting = false;
        Cursor = null;
        if (ocrSelectAdorner != null)
        {
            ocrSelectAdorner.Clear();
            AdornerLayer.GetAdornerLayer(imageView)?.Remove(ocrSelectAdorner);
            ocrSelectAdorner = null;
        }
        return this;
    }

    /// <summary>按需创建选区 Adorner 并挂到 Image 的 AdornerLayer。</summary>
    private void EnsureOcrAdorner()
    {
        if (ocrSelectAdorner != null) return;
        var layer = AdornerLayer.GetAdornerLayer(imageView);
        if (layer == null) return;
        ocrSelectAdorner = new OcrSelectAdorner(imageView);
        layer.Add(ocrSelectAdorner);
    }

    private void OnMouseMoveHandler(object sender, MouseEventArgs e)
    {
        if (!isOcrSelecting || ocrSelectAdorner == null) return;
        var pos = e.GetPosition(imageView);
        ocrSelectAdorner.Update(ocrSelectStart, pos);
    }

    private void OnMouseLeftButtonUpHandler(object sender, MouseButtonEventArgs e)
    {
        if (!isOcrSelecting) return;
        isOcrSelecting = false;
        var end = e.GetPosition(imageView);
        ocrSelectAdorner?.Clear();

        // 选区像素映射到 sourceBitmap 像素空间,裁切后弹出 OcrTextPopup
        var src = sourceBitmap;
        if (src == null) return;
        var rect = MapSelectionToPixels(ocrSelectStart, end, src.PixelWidth, src.PixelHeight);
        if (rect.Width < 4 || rect.Height < 4)
        {
            // 选区过小,视为点击,退出选字模式
            ExitOcrSelectMode();
            return;
        }

        try
        {
            var crop = new CroppedBitmap(src, rect);
            crop.Freeze();
            var lang = ToolBox.Models.ToolBoxOption.Load().Data.OcrLanguage;
            var popup = new OcrTextPopup(crop, lang) { Owner = null };
            popup.Show();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[ToolBox] OCR 选区识别失败: " + ex.Message);
        }
        finally
        {
            ExitOcrSelectMode();
        }
    }

    /// <summary>将控件本地坐标选区映射到位图像素坐标(Int32Rect)。</summary>
    private Int32Rect MapSelectionToPixels(Point start, Point end, int pixelW, int pixelH)
    {
        if (imageView.ActualWidth <= 0 || imageView.ActualHeight <= 0)
            return new Int32Rect(0, 0, 0, 0);
        var x1 = Math.Min(start.X, end.X);
        var y1 = Math.Min(start.Y, end.Y);
        var x2 = Math.Max(start.X, end.X);
        var y2 = Math.Max(start.Y, end.Y);
        var px1 = (int)(x1 / imageView.ActualWidth * pixelW);
        var py1 = (int)(y1 / imageView.ActualHeight * pixelH);
        var px2 = (int)(x2 / imageView.ActualWidth * pixelW);
        var py2 = (int)(y2 / imageView.ActualHeight * pixelH);
        px1 = Math.Max(0, Math.Min(pixelW - 1, px1));
        py1 = Math.Max(0, Math.Min(pixelH - 1, py1));
        px2 = Math.Max(0, Math.Min(pixelW, px2));
        py2 = Math.Max(0, Math.Min(pixelH, py2));
        return new Int32Rect(px1, py1, Math.Max(0, px2 - px1), Math.Max(0, py2 - py1));
    }

    /// <summary>整图 OCR: 取原图弹出 OcrResultOverlay(AC1.1 / AC1.3)。</summary>
    public ScrapWindow RunOcrWhole()
    {
        var bitmap = GetOriginalBitmap();
        if (bitmap == null) return this;
        var lang = ToolBox.Models.ToolBoxOption.Load().Data.OcrLanguage;
        var overlay = new OcrResultOverlay(bitmap, lang) { Owner = null };
        overlay.Show();
        return this;
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
                var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ToolBox_ocr_" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".png");
                using (var ms = new System.IO.MemoryStream())
                {
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bitmap));
                    enc.Save(ms);
                    System.IO.File.WriteAllBytes(tempPath, ms.ToArray());
                }

                var provider = Core.Providers.ProviderManager.Instance.CreateActiveProvider();
                if (provider == null) { System.IO.File.Delete(tempPath); return; }

                var prompt = @"请分析这张图片，识别其中所有需要作为待办事项的任务。请严格以 JSON 数组格式返回，每个元素包含 title（任务标题）和 priority（0=普通 1=重要 2=紧急）。只返回 JSON，不要添加任何其他文字或代码块标记。";

                var messages = new List<Core.Llm.ChatMessage>
                {
                    new Core.Llm.ChatMessage { Role = "user", Content = prompt, ImagePath = tempPath }
                };

                var sb = new System.Text.StringBuilder();
                await foreach (var chunk in provider.ChatAsync(messages, null, System.Threading.CancellationToken.None))
                {
                    if (chunk.Text != null) sb.Append(chunk.Text);
                }

                System.IO.File.Delete(tempPath);
                var response = sb.ToString().Trim();

                var candidates = new List<Views.TodoConfirmWindow.TodoCandidate>();
                try
                {
                    var jsonStart = response.IndexOf('[');
                    var jsonEnd = response.LastIndexOf(']');
                    if (jsonStart >= 0 && jsonEnd > jsonStart)
                    {
                        var json = response.Substring(jsonStart, jsonEnd - jsonStart + 1);
                        var arr = System.Text.Json.JsonSerializer.Deserialize<List<JsonTodoItem>>(json);
                        if (arr != null)
                            foreach (var item in arr)
                                candidates.Add(new Views.TodoConfirmWindow.TodoCandidate { Title = item.title ?? "", Priority = item.priority });
                    }
                }
                catch { }

                if (candidates.Count == 0)
                {
                    System.Windows.MessageBox.Show("未能从图片中识别出待办事项。", "待办识别", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                    return;
                }

                var confirmWnd = new Views.TodoConfirmWindow(candidates) { Owner = System.Windows.Application.Current.MainWindow };
                if (confirmWnd.ShowDialog() == true && confirmWnd.ConfirmedItems.Count > 0)
                {
                    foreach (var item in confirmWnd.ConfirmedItems)
                    {
                        if (string.IsNullOrWhiteSpace(item.Title)) continue;
                        await Core.Todo.TodoStore.Instance.AddAsync(item.Title.Trim(), priority: item.Priority);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ToolBox] Todo identification failed: " + ex.Message);
            }
        };
        menu.Items.Add(todoItem);

        menu.Items.Add(new System.Windows.Controls.Separator());

        // OCR 识别: 整图识别弹出 OcrResultOverlay(AC1.1)
        var ocrWholeItem = new System.Windows.Controls.MenuItem { Header = "OCR 识别" };
        ocrWholeItem.Click += (s, e) => RunOcrWhole();
        menu.Items.Add(ocrWholeItem);

        // 选字 OCR: 进入选字模式,拖拽选区后弹出 OcrTextPopup(AC2.1 / AC2.2)
        var ocrSelectItem = new System.Windows.Controls.MenuItem { Header = "选字 OCR" };
        ocrSelectItem.Click += (s, e) => EnterOcrSelectMode();
        menu.Items.Add(ocrSelectItem);

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

file record JsonTodoItem(string? title, int priority);














