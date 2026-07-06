using System;
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
        MouseWheel += OnMouseWheelHandler;
        MouseRightButtonDown += OnMouseRightButtonDownHandler;
        Drop += OnDropHandler;
        DragEnter += OnDragEnterHandler;
        AllowDrop = true;

        LocationChanged += OnLocationChangedHandler;
    }

    public BitmapSource GetViewImage() => sourceBitmap;

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

    private void CopyToClipboard()
    {
        if (sourceBitmap == null) return;
        try
        {
            Clipboard.Clear();
            Clipboard.SetImage(sourceBitmap);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ToolBox] {ex.Message}"); }
    }

    private void OnMouseLeftButtonDownHandler(object sender, MouseButtonEventArgs e)
    {
        Activate();
        Focus();
        Keyboard.Focus(imageView);
        DragMove();
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
        ShowContextMenu(e.GetPosition(this));
    }

    private void ShowContextMenu(Point position)
    {
        var menu = new System.Windows.Controls.ContextMenu();

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
                    clickedStyle.Apply(this, position);
                };
                menu.Items.Add(menuItem);
            }
        }

        if (menu.Items.Count > 0)
        {
            menu.Items.Add(new System.Windows.Controls.Separator());
        }

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
        if (!closePrepare)
            OnScrapClose?.Invoke(this, new ScrapEventArgs { Scrap = this });
        base.OnClosed(e);
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private void StartChat()
    {
        if (sourceBitmap == null) return;

        // Save bitmap to temp file
        var tempPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ToolBox_" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".png");

        using (var fs = new System.IO.FileStream(tempPath, System.IO.FileMode.Create))
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(sourceBitmap));
            encoder.Save(fs);
        }

        // Create session with image
        var session = Core.Llm.ChatManager.Instance.CreateSessionWithImage(
            System.IO.File.ReadAllBytes(tempPath), "截图对话");


        // Clean up temp file
        try { System.IO.File.Delete(tempPath); } catch (Exception) { /* temp file cleanup is best-effort */ }
        // Open chat window
        var chatWindow = new Views.Chat.ChatWindow();
        chatWindow.Show();
        chatWindow.Activate();
    }

}













