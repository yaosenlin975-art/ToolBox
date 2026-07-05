using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ToolBox.Core.Native;
using ToolBox.Core.Windows;
using ToolBox.Models;
using ToolBox.Services;

namespace ToolBox.Views;

public partial class MainWindow : Window
{
    private ScrapBook scrapBook;
    private ToolBoxOption options;
    private KeyItemBook keyBook;
    private Queue<ScrapSource> imageQueue = new();
    private System.Windows.Threading.DispatcherTimer poolTimer;
    private System.Windows.Threading.DispatcherTimer windowTimer;
    private bool isStarted;
    private bool isCapturing;
    private bool isOptionOpen;
    private List<ScrapWindow> hiddenScraps = new();
    private bool allScrapsActive = true;
    private WpfTrayIcon trayIcon;

    public MainWindow()
    {
        InitializeComponent();

        options = ToolBoxOption.Load();
        scrapBook = new ScrapBook(this);
        scrapBook.KeyPress += ScrapKeyPress;
        keyBook = options.GetKeyItemBook();

        poolTimer = new System.Windows.Threading.DispatcherTimer();
        poolTimer.Interval = TimeSpan.FromMilliseconds(100);
        poolTimer.Tick += PoolTimer_Tick;

        windowTimer = new System.Windows.Threading.DispatcherTimer();
        windowTimer.Interval = TimeSpan.FromMilliseconds(500);
        windowTimer.Tick += WindowTimer_Tick;

        Loaded += MainWindow_Load;
        StateChanged += MainWindow_StateChanged;
    }

    private void MainWindow_Load(object sender, RoutedEventArgs e)
    {
        Hide();
        isStarted = true;
        poolTimer.Start();
        windowTimer.Start();

        LayerManager.Instance.Init();
        CacheManager.Instance.Init();
        CacheManager.Instance.RestoreScraps(scrapBook);

        RegisterHotkeys();
        SetupTrayIcon();
    }

    private void MainWindow_StateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
            Hide();
    }

    public void ReRegisterHotkeys()
    {
        HotkeyManager.Instance.UnregisterAll();
        options = ToolBoxOption.Load();
        RegisterHotkeys();
    }

    private void RegisterHotkeys()
    {
        if (!options.HotKeyEnable) return;

        HotkeyManager.Instance.Initialize(this);

        // Register capture hotkey
        RegisterSingleHotkey(options.CaptureHotKey, () =>
        {
            if (isStarted && !isCapturing)
                StartCapture();
        });

        // Register hide/show all hotkey
        RegisterSingleHotkey(options.HideShowHotKey, () =>
        {
            if (isStarted)
                SetAllScrapsActive(!allScrapsActive);
        });

        // Register style-based hotkeys
        foreach (var keyItem in keyBook.GetAllKeys())
        {
            var styleId = keyItem.StyleId;
            var key = keyItem.KeyCode;

            RegisterSingleHotkey(key, () =>
            {
                var style = options.FindStyle(styleId);
                if (style != null && scrapBook.ScrapCount > 0)
                {
                    var activeScrap = GetActiveScrap();
                    if (activeScrap != null)
                        style.Apply(activeScrap, new Point(0, 0));
                }
            });
        }
    }

    private void RegisterSingleHotkey(Key combinedKey, Action action)
    {
        var modifiers = ExtractModifiers(combinedKey);
        var plainKey = ExtractPlainKey(combinedKey);

        if (plainKey == Key.None) return;

        HotkeyManager.Instance.RegisterHotkey(plainKey, modifiers, action);
    }

    private ModifierKeys ExtractModifiers(Key key)
    {
        var k = (int)key;
        var modifiers = ModifierKeys.None;
        if ((k & ToolBoxOption.CtrlBit) != 0)
            modifiers |= ModifierKeys.Control;
        if ((k & ToolBoxOption.ShiftBit) != 0)
            modifiers |= ModifierKeys.Shift;
        if ((k & ToolBoxOption.AltBit) != 0)
            modifiers |= ModifierKeys.Alt;
        return modifiers;
    }

    private Key ExtractPlainKey(Key key)
    {
        return (Key)((int)key & ToolBoxOption.KeyMask);
    }

    private void ScrapKeyPress(object sender, ScrapKeyPressEventArgs e)
    {
        var keyItem = keyBook.FindKeyItem(e.Key);
        if (keyItem != null && sender is ScrapWindow scrap)
        {
            var style = options.FindStyle(keyItem.StyleId);
            style?.Apply(scrap, new Point(0, 0));
        }
    }

    private ScrapWindow GetActiveScrap()
    {
        foreach (Window w in Application.Current.Windows)
        {
            if (w is ScrapWindow scrap && scrap.IsActive)
                return scrap;
        }
        return null;
    }

    private ModifierKeys GetModifiers(Key key)
    {
        var modifiers = ModifierKeys.None;
        if ((int)key >= (int)Key.LeftCtrl && (int)key <= (int)Key.RightCtrl)
            modifiers |= ModifierKeys.Control;
        if ((int)key >= (int)Key.LeftShift && (int)key <= (int)Key.RightShift)
            modifiers |= ModifierKeys.Shift;
        if ((int)key >= (int)Key.LeftAlt && (int)key <= (int)Key.RightAlt)
            modifiers |= ModifierKeys.Alt;
        return modifiers;
    }

    private Key GetPlainKey(Key key)
    {
        if ((int)key >= (int)Key.LeftCtrl && (int)key <= (int)Key.RightCtrl)
            return Key.None;
        if ((int)key >= (int)Key.LeftShift && (int)key <= (int)Key.RightShift)
            return Key.None;
        if ((int)key >= (int)Key.LeftAlt && (int)key <= (int)Key.RightAlt)
            return Key.None;
        return key;
    }

    private void SetupTrayIcon()
    {
        using var icon = SystemIcons.Application;
        var clonedIcon = (Icon)icon.Clone();
        trayIcon = new WpfTrayIcon(this, "ToolBox", clonedIcon, () => ShowOptions(), ShowTrayContextMenu);
    }

    private void ShowTrayContextMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu
        {
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("BgSecondaryBrush"),
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextPrimaryBrush"),
            BorderBrush = (System.Windows.Media.Brush)Application.Current.FindResource("BorderBrush"),
            Padding = new Thickness(4)
        };
        var itemStyle = new System.Windows.Style(typeof(System.Windows.Controls.MenuItem))
        {
            Setters =
            {
                new System.Windows.Setter(System.Windows.Controls.Control.ForegroundProperty,
                    Application.Current.FindResource("TextPrimaryBrush")),
            }
        };
        void StyleItem(System.Windows.Controls.MenuItem mi)
        {
            mi.Style = itemStyle;
            mi.Padding = new Thickness(16,6,16,6);
            mi.FontSize = 13;
        }

        var scrapMenuItem = new System.Windows.Controls.MenuItem { Header = "参考图列表" };
        if (scrapBook.ScrapCount > 0)
        {
            foreach (var window in Application.Current.Windows)
            {
                if (window is ScrapWindow scrap)
                {
                    var item = new System.Windows.Controls.MenuItem
                    {
                        Header = $"{scrap.Title} ({(int)scrap.Width}x{(int)scrap.Height})",
                        Tag = scrap
                    };
                    item.Click += (s, e) =>
                    {
                        scrap.Activate();
                        scrap.WindowState = WindowState.Normal;
                    };
                    scrapMenuItem.Items.Add(item);
                }
            }
        }
        else
        {
            scrapMenuItem.Items.Add(new System.Windows.Controls.MenuItem { Header = "(无)", IsEnabled = false });
        }
        menu.Items.Add(scrapMenuItem);

        menu.Items.Add(new System.Windows.Controls.Separator());

        var captureItem = new System.Windows.Controls.MenuItem { Header = "截图" };
        captureItem.Click += (s, e) => StartCapture();
        menu.Items.Add(captureItem);

        var pasteItem = new System.Windows.Controls.MenuItem { Header = "粘贴" };
        pasteItem.Click += (s, e) => PasteImage();
        menu.Items.Add(pasteItem);

        menu.Items.Add(new System.Windows.Controls.Separator());

        var aiItem = new System.Windows.Controls.MenuItem { Header = "AI 助手" };
        aiItem.Click += (s, e) => ShowChatWindow();
        menu.Items.Add(aiItem);

        var todoItem = new System.Windows.Controls.MenuItem { Header = "待办" };
        todoItem.Click += (s, e) => ShowTodoWindow();
        menu.Items.Add(todoItem);

        menu.Items.Add(new System.Windows.Controls.Separator());

        var optionItem = new System.Windows.Controls.MenuItem { Header = "设置" };
        optionItem.Click += (s, e) => ShowOptions();
        menu.Items.Add(optionItem);

        menu.Items.Add(new System.Windows.Controls.Separator());

        var exitItem = new System.Windows.Controls.MenuItem { Header = "退出" };
        exitItem.Click += (s, e) => Shutdown();
        menu.Items.Add(exitItem);

        foreach (var obj in menu.Items)
        {
            if (obj is System.Windows.Controls.MenuItem mi) StyleItem(mi);
            else if (obj is System.Windows.Controls.MenuItem sub && false) { }
        }

        menu.IsOpen = true;
    }

    public MainWindow StartCapture()
    {
        if (isCapturing) return this;

        var captureWindow = new Capture.CaptureWindow();
        captureWindow.CaptureCompleted += (bitmap, point, size) =>
        {
            var bmpSource = ConvertToBitmapSource(bitmap);
            if (bmpSource != null)
                scrapBook.AddScrap(bmpSource,
                    (int)point.X, (int)point.Y,
                    (int)size.Width, (int)size.Height);
        };
        captureWindow.StartCapture();
        return this;
    }

    public MainWindow PasteImage()
    {
        if (Clipboard.ContainsImage())
        {
            var image = Clipboard.GetImage();
            if (image != null)
            {
                scrapBook.AddScrap(image,
                    (int)(SystemParameters.PrimaryScreenWidth / 2 - image.PixelWidth / 2),
                    (int)(SystemParameters.PrimaryScreenHeight / 2 - image.PixelHeight / 2),
                    image.PixelWidth, image.PixelHeight);
            }
        }
        return this;
    }

    public ToolBoxOption GetOptions()
    {
        return options;
    }

    public MainWindow ShowVersion()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        MessageBox.Show($"ToolBox WPF v{version}\n\n从 SETUNA2 WinForms 移植到 WPF", "版本信息", MessageBoxButton.OK, MessageBoxImage.Information);
        return this;
    }

    public MainWindow ShowHistory()
    {
        OpenWorkbench("history");
        return this;
    }

    private void ShowChatWindow()
    {
        OpenWorkbench("chat");
    }

    private void ShowTodoWindow()
    {
        OpenWorkbench("todo");
    }

    private void OpenWorkbench(string tab)
    {
        if (App.CompactToolbox is { } toolbox)
        {
            toolbox.SwitchToTab(tab);
            toolbox.Activate();
        }
    }

    public MainWindow ShowOptions()
    {
        OpenWorkbench("settings");
        return this;
    }

    public MainWindow AddImageList(ScrapSource source)
    {
        imageQueue.Enqueue(source);
        poolTimer.Start();
        return this;
    }

    public MainWindow AddImageListFileName(string path)
    {
        return AddImageList(new ScrapSourcePath(path));
    }

    private void PoolTimer_Tick(object sender, EventArgs e)
    {
        if (imageQueue.Count == 0)
        {
            poolTimer.Stop();
            return;
        }

        var source = imageQueue.Dequeue();
        try
        {
            var bitmap = source.GetImage();
            if (bitmap != null)
            {
                var pos = source.GetPosition();
                scrapBook.AddScrap(bitmap,
                    (int)pos.X, (int)pos.Y,
                    bitmap.PixelWidth, bitmap.PixelHeight);
            }
        }
        catch { }
    }

    private void WindowTimer_Tick(object sender, EventArgs e)
    {
        WindowManager.Instance.Update();
    }

    public MainWindow Shutdown()
    {
        scrapBook.CloseAllScrap();
        HotkeyManager.Instance.UnregisterAll();
        trayIcon?.Dispose();
        Application.Current.Shutdown();
        return this;
    }

    public MainWindow CommandRun(string[] args)
    {
        var rect = new System.Drawing.Rectangle(0, 0, 0, 0);
        var fname = "";
        int command = 0;

        foreach (var text in args)
        {
            var arg = text;
            var prefix = "";
            if (arg.Length > 3)
            {
                prefix = arg.Substring(0, 3);
                if (prefix.StartsWith("/") && prefix.EndsWith(":"))
                    arg = arg.Substring(3);
                else
                    prefix = "";
            }

            if (prefix.Length > 0)
            {
                if (prefix == "/R:")
                {
                    var parts = arg.Split(',');
                    if (parts.Length == 4 &&
                        int.TryParse(parts[0], out var x) &&
                        int.TryParse(parts[1], out var y) &&
                        int.TryParse(parts[2], out var w) &&
                        int.TryParse(parts[3], out var h))
                    {
                        rect = new System.Drawing.Rectangle(x, y, w, h);
                    }
                }
                else if (prefix == "/P:")
                {
                    fname = arg;
                }
                else if (prefix == "/C:")
                {
                    var cmd = arg.ToUpper();
                    if (cmd == "OPTION") command = 1;
                    else if (cmd == "CAPTURE") command = 2;
                    else if (cmd == "CHAT") command = 3;
                    else if (cmd == "TODO") command = 4;
                }
            }
            else
            {
                AddImageListFileName(arg);
            }
        }

        if (rect.Width >= 10 && rect.Height >= 10)
        {
            CommandCutRect(rect, fname);
        }
        else if (command != 0 && isStarted)
        {
            if (command == 1) ShowOptions();
            else if (command == 2) StartCapture();
            else if (command == 3) ShowChatWindow();
            else if (command == 4) ShowTodoWindow();
        }
        return this;
    }

    private MainWindow CommandCutRect(System.Drawing.Rectangle rect, string fname)
    {
        using var bitmap = new System.Drawing.Bitmap(rect.Width, rect.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using var g = System.Drawing.Graphics.FromImage(bitmap);
        g.CopyFromScreen(rect.X, rect.Y, 0, 0, new System.Drawing.Size(rect.Width, rect.Height));

        if (string.IsNullOrEmpty(fname))
        {
            var bmpSource = ConvertToBitmapSource(bitmap);
            if (bmpSource != null)
                scrapBook.AddScrap(bmpSource, rect.X, rect.Y, rect.Width, rect.Height);
        }
        return this;
    }

    public MainWindow SetAllScrapsActive(bool active)
    {
        if (allScrapsActive == active) return this;
        allScrapsActive = active;

        if (active)
        {
            foreach (var scrap in hiddenScraps)
                scrap.Show();
            hiddenScraps.Clear();
            LayerManager.Instance.ResumeRefresh();
        }
        else
        {
            foreach (Window w in Application.Current.Windows)
            {
                if (w is ScrapWindow scrap && scrap.Visibility == Visibility.Visible)
                    hiddenScraps.Add(scrap);
            }
            foreach (var scrap in hiddenScraps)
                scrap.Hide();
            LayerManager.Instance.SuspendRefresh();
        }
        return this;
    }

    private static BitmapSource ConvertToBitmapSource(Bitmap bitmap)
    {
        if (bitmap == null) return null;
        var hBitmap = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(hBitmap);
        }
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}





