// File: OcrResultOverlay.xaml.cs
// OCR 结果窗口逻辑。构造时传入截图，加载后异步识别(带下载进度)，展示按行文本+缩略图边界框高亮。
// 复制全部 / 翻译选中(带"请翻译:"前缀) / 发送到 AI 均跳转 AI Tab。
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ToolBox.Services.Ocr;

namespace ToolBox.Views.Ocr;

public partial class OcrResultOverlay : Window
{
    private readonly BitmapSource sourceImage;
    private readonly string language;
    private OcrResult? result;
    private System.Windows.Threading.DispatcherTimer? statusTimer;

    public OcrResultOverlay(BitmapSource image, string? language = null)
    {
        InitializeComponent();
        sourceImage = image;
        this.language = string.IsNullOrWhiteSpace(language)
            ? OcrService.DefaultLanguage
            : language!;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ThumbImage.Source = sourceImage;
        ShowLoading(true);

        var progress = new Progress<(int percent, string status)>(p =>
        {
            // 语言包下载阶段:切换为确定性进度条
            LoadingProgress.IsIndeterminate = false;
            LoadingProgress.Value = p.percent;
            DownloadStatus.Text = p.status;
            LoadingText.Text = "正在准备语言包...";
        });

        try
        {
            result = await OcrService.Instance.RecognizeAsync(sourceImage, this.language, autoDownload: true, progress: progress);
        }
        catch (Exception ex)
        {
            result = new OcrResult { EngineUsed = "None", ErrorMessage = ex.Message };
        }

        ShowLoading(false);
        Populate(result);
    }

    private void ShowLoading(bool show)
    {
        LoadingPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        LoadingProgress.IsIndeterminate = true;
        LoadingProgress.Value = 0;
        if (!show) DownloadStatus.Text = string.Empty;
    }

    private void Populate(OcrResult r)
    {
        EngineBadge.Text = "引擎: " + r.EngineUsed + (r.IsEmpty ? " · 未识别到文字" : " · " + r.Lines.Count + " 行");
        LineList.Items.Clear();
        if (r.IsEmpty)
        {
            LineList.Items.Add(new ListBoxItem
            {
                Content = new TextBlock { Text = string.IsNullOrEmpty(r.ErrorMessage) ? "未识别到文字" : ("错误: " + r.ErrorMessage),
                                          Foreground = (Brush)FindResource("TextTertiaryBrush") },
                IsEnabled = false
            });
            return;
        }

        var lowBrush = new SolidColorBrush(Color.FromRgb(0xE6, 0xA8, 0x17));
        var normalBrush = (Brush)FindResource("TextPrimaryBrush");
        foreach (var line in r.Lines)
        {
            var tb = new TextBlock { TextWrapping = TextWrapping.Wrap };
            if (line.IsLowConfidence)
            {
                tb.Foreground = lowBrush;
                tb.Text = line.Text + "  [低置信度 " + (int)line.Confidence + "%]";
            }
            else
            {
                tb.Foreground = normalBrush;
                tb.Text = line.Text;
            }
            LineList.Items.Add(new ListBoxItem { Content = tb, Tag = line });
        }
    }

    private void LineSelected(object sender, SelectionChangedEventArgs e)
    {
        BoxCanvas.Children.Clear();
        if (LineList.SelectedItem is not ListBoxItem item || item.Tag is not OcrLine line || line.Rect == null)
            return;

        DrawBoundingBox(line.Rect);
    }

    /// <summary>在缩略图上按图像像素坐标绘制边界框(经 Uniform 缩放与居中偏移换算到显示坐标)。</summary>
    private void DrawBoundingBox(OcrRect rect)
    {
        if (sourceImage == null) return;
        var hostW = ThumbHost.RenderSize.Width;
        var hostH = ThumbHost.RenderSize.Height;
        if (hostW <= 0 || hostH <= 0) return;

        var imgW = sourceImage.PixelWidth;
        var imgH = sourceImage.PixelHeight;
        if (imgW <= 0 || imgH <= 0) return;

        var scale = Math.Min(hostW / imgW, hostH / imgH);
        var renderedW = imgW * scale;
        var renderedH = imgH * scale;
        var offsetX = (hostW - renderedW) / 2.0;
        var offsetY = (hostH - renderedH) / 2.0;

        var x = offsetX + rect.X * scale;
        var y = offsetY + rect.Y * scale;
        var w = rect.Width * scale;
        var h = rect.Height * scale;

        var box = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0xF5, 0xA6, 0x23)),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(0x33, 0xF5, 0xA6, 0x23))
        };
        Canvas.SetLeft(box, x);
        Canvas.SetTop(box, y);
        box.Width = Math.Max(1, w);
        box.Height = Math.Max(1, h);
        BoxCanvas.Children.Add(box);
    }

    // ===== 底部按钮 =====

    private void CopyAllClick(object sender, RoutedEventArgs e)
    {
        if (result == null || result.IsEmpty) return;
        try
        {
            Clipboard.SetText(result.FullText);
            ShowStatus("已复制 " + result.Lines.Count + " 行文字");
        }
        catch (Exception ex)
        {
            ShowStatus("复制失败: " + ex.Message);
        }
    }

    private void TranslateClick(object sender, RoutedEventArgs e)
    {
        var text = GetSelectedOrAllText();
        if (string.IsNullOrEmpty(text)) return;
        SendToAiInput("请翻译:\n" + text);
        Close();
    }

    private void SendToAiClick(object sender, RoutedEventArgs e)
    {
        if (result == null || result.IsEmpty) return;
        SendToAiInput(result.FullText);
        Close();
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>取当前选中行文本;无选中则取全部。</summary>
    private string GetSelectedOrAllText()
    {
        if (LineList.SelectedItem is ListBoxItem item && item.Tag is OcrLine line)
            return line.Text;
        return result?.FullText ?? string.Empty;
    }

    /// <summary>将文本填入 AI 助手输入框并跳转 AI Tab(复用 CompactToolbox 的常驻 ChatView)。</summary>
    private void SendToAiInput(string text)
    {
        var toolbox = App.CompactToolbox;
        if (toolbox != null)
        {
            toolbox.ChatPanel?.SetInput(text);
            toolbox.SwitchToTab("chat");
            toolbox.Activate();
            return;
        }
        // 回退:打开工作台 AI 页
        App.Workbench?.Show();
        App.Workbench?.Activate();
        App.Workbench?.LoadPage("assistant");
    }

    private void ShowStatus(string text)
    {
        StatusTip.Text = text;
        statusTimer?.Stop();
        statusTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        statusTimer.Tick += (s, e) => { StatusTip.Text = string.Empty; statusTimer?.Stop(); };
        statusTimer.Start();
    }

    private void HeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }
}
