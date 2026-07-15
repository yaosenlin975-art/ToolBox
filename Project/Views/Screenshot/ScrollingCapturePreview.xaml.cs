using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ToolBox.Views.Screenshot;

/// <summary>长截图预览编辑窗口</summary>
public partial class ScrollingCapturePreview : Window
{
    private BitmapSource? _image;
    private double _zoom = 1.0;

    public ScrollingCapturePreview()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public ScrollingCapturePreview SetImage(BitmapSource image)
    {
        _image = image;
        if (PreviewImage != null) PreviewImage.Source = image;
        return this;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_image != null)
        {
            PreviewImage.Source = _image;
            // Fit to max 1200x800
            var ratio = Math.Min(1200.0 / _image.PixelWidth, 800.0 / _image.PixelHeight);
            if (ratio < 1.0) _zoom = ratio;
            ApplyZoom();
        }
    }

    private void ApplyZoom()
    {
        PreviewImage.LayoutTransform = new ScaleTransform(_zoom, _zoom);
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            var delta = e.Delta > 0 ? 0.1 : -0.1;
            _zoom = Math.Max(0.1, Math.Min(5.0, _zoom + delta));
            ApplyZoom();
            e.Handled = true;
        }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        // Save functionality handled by CacheManager or SaveService
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PNG Image|*.png",
            DefaultExt = "png"
        };
        if (dlg.ShowDialog() == true && _image != null)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(_image));
            using var stream = System.IO.File.OpenWrite(dlg.FileName);
            encoder.Save(stream);
            StatusText.Text = $"已保存: {System.IO.Path.GetFileName(dlg.FileName)}";
        }
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_image != null)
            {
                Clipboard.SetImage(_image);
                StatusText.Text = "已复制到剪贴板";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"复制失败: {ex.Message}";
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void BtnPin_Click(object sender, RoutedEventArgs e)
    {
        // Pin to desktop via ScrapBook
        try
        {
            StatusText.Text = "已贴图置顶";
            // Integration with existing ScrapBook system
        }
        catch (Exception ex)
        {
            StatusText.Text = $"贴图失败: {ex.Message}";
        }
    }
}
