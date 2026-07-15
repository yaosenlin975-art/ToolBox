using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ToolBox.Core.PreviewCard;

namespace ToolBox.Views.PreviewCard;

public partial class PreviewCardWindow : Window
{
    private BitmapSource? _screenshot;
    private string? _filePath;
    private PreviewCardManager? _manager;
    private DispatcherTimer? _autoDismissTimer;
    private bool _isForceClosing;
    private bool _isDragging;

    public PreviewCardWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        MouseEnter += OnMouseEnter;
        MouseLeave += OnMouseLeave;
    }

    /// <summary>设置预览截图</summary>
    public PreviewCardWindow SetScreenshot(BitmapSource screenshot)
    {
        _screenshot = screenshot;
        ThumbnailImage.Source = screenshot;
        return this;
    }

    /// <summary>设置关联文件路径</summary>
    public PreviewCardWindow SetFilePath(string? filePath)
    {
        _filePath = filePath;
        return this;
    }

    /// <summary>设置管理器引用</summary>
    public PreviewCardWindow SetManager(PreviewCardManager manager)
    {
        _manager = manager;
        return this;
    }

    /// <summary>开始自动消失计时</summary>
    public PreviewCardWindow StartAutoDismiss(double afterMs = 5000)
    {
        _autoDismissTimer?.Stop();
        _autoDismissTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(afterMs)
        };
        _autoDismissTimer.Tick += (s, e) =>
        {
            _autoDismissTimer.Stop();
            DismissWithAnimation();
        };
        _autoDismissTimer.Start();
        return this;
    }

    /// <summary>重置自动消失计时（鼠标悬停时）</summary>
    public PreviewCardWindow ResetAutoDismissTimer()
    {
        if (_autoDismissTimer != null)
        {
            _autoDismissTimer.Stop();
            _autoDismissTimer.Start();
        }
        return this;
    }

    /// <summary>以动画隐藏窗口</summary>
    public PreviewCardWindow DismissWithAnimation()
    {
        if (_isForceClosing) return this;
        var storyboard = (Storyboard)FindResource("SlideOutStoryboard");
        storyboard.Begin(CardBorder);
        return this;
    }

    /// <summary>强制关闭（无动画）</summary>
    public void ForceClose()
    {
        _isForceClosing = true;
        _autoDismissTimer?.Stop();
        _manager?.OnCardClosed();
        Close();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 定位到屏幕右下角（考虑任务栏）
        PositionAtBottomRight();

        // 播放入场动画
        var storyboard = (Storyboard)FindResource("SlideInStoryboard");
        storyboard.Begin(this);
    }

    private void PositionAtBottomRight()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 16;
        Top = workArea.Bottom - Height - 16;
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        // 重置自动消失计时
        ResetAutoDismissTimer();

        // 播放悬停放大动画
        var storyboard = (Storyboard)FindResource("HoverInStoryboard");
        storyboard.Begin(this);
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        // 重新开始计时
        StartAutoDismiss(_manager?.GetConfig().DisplayDurationMs ?? 5000);

        // 播放悬停恢复动画
        var storyboard = (Storyboard)FindResource("HoverOutStoryboard");
        storyboard.Begin(this);
    }

    private void OnSlideOutCompleted(object? sender, EventArgs e)
    {
        _manager?.OnCardClosed();
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _autoDismissTimer?.Stop();
        _autoDismissTimer = null;
        base.OnClosed(e);
    }

    // === 操作按钮 ===

    private void OnEditClick(object sender, RoutedEventArgs e)
    {
        if (_screenshot == null) return;
        _manager?.RaiseAction(EPreviewCardAction.Edit, _screenshot);
        ForceClose();
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        if (_screenshot == null) return;
        _manager?.RaiseAction(EPreviewCardAction.PinToDesktop, _screenshot);
        ForceClose();
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (_screenshot == null) return;
        try
        {
            Clipboard.SetImage(_screenshot);
        }
        catch { /* clipboard may be locked */ }
        _manager?.RaiseAction(EPreviewCardAction.Copy, _screenshot);
        ForceClose();
    }

    private void OnSendToAiClick(object sender, RoutedEventArgs e)
    {
        if (_screenshot == null) return;
        _manager?.RaiseAction(EPreviewCardAction.SendToAi, _screenshot);
        ForceClose();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_screenshot == null) return;
        _manager?.RaiseAction(EPreviewCardAction.Save, _screenshot);
        ForceClose();
    }

    private void OnDiscardClick(object sender, RoutedEventArgs e)
    {
        if (_screenshot == null) return;
        _manager?.RaiseAction(EPreviewCardAction.Discard, _screenshot);
        ForceClose();
    }

    // === 拖拽分享 ===

    private void OnThumbnailMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_screenshot == null) return;
        _isDragging = true;

        // 准备多格式 DataObject
        var dataObject = new DataObject();

        // WPF BitmapSource 格式
        dataObject.SetData(DataFormats.Bitmap, _screenshot);

        // PNG 流格式
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(_screenshot));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        ms.Position = 0;
        dataObject.SetData("PNG", ms);

        // 如果有关联文件，也提供 FileDrop 格式
        if (!string.IsNullOrEmpty(_filePath) && File.Exists(_filePath))
        {
            dataObject.SetData(DataFormats.FileDrop, new[] { _filePath });
        }

        try
        {
            DragDrop.DoDragDrop(this, dataObject, DragDropEffects.Copy);
        }
        catch { /* drag may fail if another drag is in progress */ }

        _isDragging = false;

        // 拖拽完成后关闭预览卡
        ForceClose();
    }
}
