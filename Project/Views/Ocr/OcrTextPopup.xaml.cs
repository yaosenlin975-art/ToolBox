// File: OcrTextPopup.xaml.cs
// 贴图选字 OCR 浮层逻辑。接收选区位图，加载后异步识别，展示文本并提供复制/翻译。
// 翻译 = 发送到 AI 输入框带"请翻译:"前缀(AC2.2 / 冲突6)。
using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using ToolBox.Services.Ocr;

namespace ToolBox.Views.Ocr;

public partial class OcrTextPopup : Window
{
    private readonly BitmapSource sourceImage;
    private string language;
    private OcrResult? result;

    public OcrTextPopup(BitmapSource image, string? language = null)
    {
        InitializeComponent();
        sourceImage = image;
        this.language = string.IsNullOrWhiteSpace(language) ? OcrService.DefaultLanguage : language!;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadingLayer.Visibility = Visibility.Visible;
        try
        {
            // 选区面积小，不弹下载框；缺包回退 Windows OCR
            result = await OcrService.Instance.RecognizeAsync(sourceImage, language, autoDownload: false);
        }
        catch (Exception ex)
        {
            result = new OcrResult { EngineUsed = "None", ErrorMessage = ex.Message };
        }
        LoadingLayer.Visibility = Visibility.Collapsed;

        if (result.IsEmpty)
            TextBox.Text = string.IsNullOrEmpty(result.ErrorMessage) ? "未识别到文字" : ("错误: " + result.ErrorMessage);
        else
            TextBox.Text = result.FullText;
    }

    private void CopyClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(TextBox.Text)) return;
        try { Clipboard.SetText(TextBox.Text); } catch { /* best-effort */ }
        Close();
    }

    private void TranslateClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(TextBox.Text)) return;
        var toolbox = App.CompactToolbox;
        if (toolbox != null)
        {
            toolbox.ChatPanel?.SetInput("请翻译:\n" + TextBox.Text);
            toolbox.SwitchToTab("chat");
            toolbox.Activate();
        }
        else
        {
            App.Workbench?.Show();
            App.Workbench?.Activate();
            App.Workbench?.LoadPage("assistant");
        }
        Close();
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    private void HeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }
}
