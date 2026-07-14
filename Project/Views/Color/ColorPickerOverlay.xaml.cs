// ColorPickerOverlay.xaml.cs - 全屏透明取色覆盖层
// 职责:全屏透明窗口 + 30fps 放大镜跟随 + 三格式显示 + Tab 切换 + 左键复制 + 闪烁 + ESC 取消
// 实现要点:
//   - 窗口覆盖整个虚拟屏幕(VirtualScreenLeft/Top/Width/Height),支持多显示器
//   - DPI 已由 App.SetProcessDPIAware 声明,GetCursorPos/GetPixel/CopyFromScreen 均用物理像素
//   - UI 坐标(DIP)通过 CompositionTarget.TransformFromDevice 从物理像素转换
//   - 全屏覆盖层正常 HitTest:鼠标移动/点击均被本窗口接收,简单可靠
using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

using ToolBox.Core.ColorPicker;
using ToolBox.Core.Native;

namespace ToolBox.Views.ColorPicker;

public partial class ColorPickerOverlay : Window
{
    private const int REFRESH_MS = 33;          // ~30fps, 满足规格非功能要求(≥30fps)
    private const int FLASH_MS = 300;           // 取色成功闪烁时长
    private const int MAGNIFIER_OFFSET = 20;    // 放大镜相对鼠标的偏移
    private const double FONT_LARGE = 18;       // 当前选定格式字号
    private const double FONT_SMALL = 11;      // 非选定格式字号

    private readonly DispatcherTimer refreshTimer;
    private bool isClosed;

    public ColorPickerOverlay()
    {
        InitializeComponent();
        refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(REFRESH_MS) };
        refreshTimer.Tick += (_, _) => RefreshState();
    }

    /// <summary>启动取色模式:覆盖虚拟屏幕并启动定时刷新</summary>
    public ColorPickerOverlay Start()
    {
        // 虚拟屏幕坐标(多显示器场景下可能为负)
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        UpdateFormatStyles();
        Show();
        // 立即刷新一次,避免空白帧
        RefreshState();
        refreshTimer.Start();
        return this;
    }

    /// <summary>30fps 刷新:鼠标坐标→取色→放大镜截图→UI 更新</summary>
    private void RefreshState()
    {
        if (isClosed) return;
        if (!NativeMethods.GetCursorPos(out var pt)) return;

        // 物理像素坐标→取色与放大镜截图
        var info = ColorPickerService.Instance.PickColor(pt.X, pt.Y);
        var zoomSource = ColorPickerService.Instance.CaptureZoomRegion(pt.X, pt.Y);
        ZoomImage.Source = zoomSource;

        // 物理像素→DIP,用于 UI 定位(放大镜跟随鼠标)
        var dip = ToDip(pt.X, pt.Y);
        // 放大镜相对窗口的坐标(窗口左上即虚拟屏幕左上)
        var leftInWindow = dip.X - Left;
        var topInWindow = dip.Y - Top;

        // 默认右上偏移;靠近屏幕右边/上边时翻转避免出屏
        var magX = leftInWindow + MAGNIFIER_OFFSET;
        var magY = topInWindow - MagnifierBorder.Height - MAGNIFIER_OFFSET;
        if (magX + MagnifierBorder.Width > Width)
            magX = leftInWindow - MagnifierBorder.Width - MAGNIFIER_OFFSET;
        if (magY < 0)
            magY = topInWindow + MAGNIFIER_OFFSET;

        Canvas.SetLeft(MagnifierBorder, magX);
        Canvas.SetTop(MagnifierBorder, magY);

        // 色值与色块更新
        HexText.Text = info.Hex;
        RgbText.Text = info.Rgb;
        HslText.Text = info.Hsl;
        ColorPreview.Fill = new SolidColorBrush(info.Color);
    }

    /// <summary>按当前格式调整字号:当前格式 18px,其他 11px</summary>
    private void UpdateFormatStyles()
    {
        var format = ColorPickerService.Instance.OutputFormat;
        HexText.FontSize = format == EColorFormat.Hex ? FONT_LARGE : FONT_SMALL;
        RgbText.FontSize = format == EColorFormat.Rgb ? FONT_LARGE : FONT_SMALL;
        HslText.FontSize = format == EColorFormat.Hsl ? FONT_LARGE : FONT_SMALL;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            e.Handled = true;
            CommitPick();
        }
        base.OnMouseDown(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseOverlay();
        }
        else if (e.Key == Key.Tab)
        {
            e.Handled = true;
            ColorPickerService.Instance.ToggleFormat();
            UpdateFormatStyles();
        }
        base.OnKeyDown(e);
    }

    /// <summary>左键确认取色:复制 + 历史 + 闪烁 + Toast + 历史 Popup + 关闭</summary>
    private void CommitPick()
    {
        if (isClosed) return;
        if (!NativeMethods.GetCursorPos(out var pt)) return;

        var info = ColorPickerService.Instance.PickColor(pt.X, pt.Y);
        var format = ColorPickerService.Instance.OutputFormat;
        var text = ColorPickerService.FormatColor(info, format);

        try { Clipboard.SetText(text); } catch { /* 剪贴板被占用,忽略 */ }

        ColorHistoryStore.Instance.Add(info);
        ShowFlash(info);
        // Toast + HistoryPopup 在闪烁启动后立即弹出,与关闭并行
        ShowPostPickNotifications(info, text);
        // 延迟关闭让闪烁动画播完(300ms)
        var closeDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FLASH_MS) };
        closeDelay.Tick += (_, _) =>
        {
            closeDelay.Stop();
            CloseOverlay();
        };
        closeDelay.Start();
    }

    /// <summary>在鼠标位置显示 60px 圆形色块,300ms 缩小+淡出</summary>
    private void ShowFlash(ColorInfo info)
    {
        if (!NativeMethods.GetCursorPos(out var pt)) return;
        var dip = ToDip(pt.X, pt.Y);
        var leftInWindow = dip.X - Left - 30; // 圆形中心对齐鼠标
        var topInWindow = dip.Y - Top - 30;

        FlashEllipse.Fill = new SolidColorBrush(info.Color);
        FlashEllipse.Opacity = 0.9;
        Canvas.SetLeft(FlashEllipse, leftInWindow);
        Canvas.SetTop(FlashEllipse, topInWindow);

        var scale = new ScaleTransform(1.0, 1.0, 30, 30);
        FlashEllipse.RenderTransform = scale;
        var animScale = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(FLASH_MS))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        var animOpacity = new DoubleAnimation(0.9, 0.0, TimeSpan.FromMilliseconds(FLASH_MS));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animScale);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animScale);
        FlashEllipse.BeginAnimation(OpacityProperty, animOpacity);
    }

    /// <summary>右下角弹出 Toast + 历史面板(Toast 在底, HistoryPopup 在其上方)</summary>
    private void ShowPostPickNotifications(ColorInfo info, string text)
    {
        var workArea = SystemParameters.WorkArea;
        // ColorToast.Show 内部会按 WorkArea 定位,这里按相同公式计算 HistoryPopup 位置
        var toastWidth = 220;
        var toastHeight = 44;
        var toastLeft = workArea.Right - toastWidth - 20;
        var toastTop = workArea.Bottom - toastHeight - 20;

        var toast = new ColorToast
        {
            Width = toastWidth,
            Height = toastHeight
        };
        // ColorToast.Show 会自动定位 + 启动 3 秒关闭计时器
        toast.Show(info, "已复制 " + text);

        // 历史 Popup 在 Toast 上方 8px
        var popup = new ColorHistoryPopup();
        popup.ShowAt(toastLeft, toastTop - popup.Height - 8);
    }

    private void CloseOverlay()
    {
        if (isClosed) return;
        isClosed = true;
        refreshTimer.Stop();
        Close();
    }

    /// <summary>屏幕物理像素坐标 → WPF DIP(自身已 Show, PresentationSource 可用)</summary>
    private Point ToDip(double x, double y)
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget != null)
            return source.CompositionTarget.TransformFromDevice.Transform(new Point(x, y));
        return new Point(x, y);
    }
}
