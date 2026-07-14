// ColorHistoryPopup.xaml.cs - 取色历史右下角面板(2 行 5 列 24x24 圆形色块, 3 秒自动消失)
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

using ToolBox.Core.ColorPicker;

namespace ToolBox.Views.ColorPicker;

public partial class ColorHistoryPopup : Window
{
    private const int DISPLAY_MS = 3000;
    private const int MAX_ITEMS = 10;
    private readonly DispatcherTimer closeTimer;

    public ColorHistoryPopup()
    {
        InitializeComponent();
        closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DISPLAY_MS) };
        closeTimer.Tick += (_, _) =>
        {
            closeTimer.Stop();
            Close();
        };
    }

    /// <summary>在指定位置(Toast 上方)显示历史面板</summary>
    public ColorHistoryPopup ShowAt(double left, double top)
    {
        RenderHistory();
        Left = left;
        Top = top;
        Show();
        closeTimer.Start();
        return this;
    }

    private void RenderHistory()
    {
        HistoryGrid.Children.Clear();
        var recent = ColorHistoryStore.Instance.GetRecent(MAX_ITEMS);
        // 倒序填充使最近在左侧第一格
        for (var i = 0; i < MAX_ITEMS; i++)
        {
            if (i < recent.Count)
            {
                var entry = recent[i];
                var dot = new Ellipse
                {
                    Width = 24, Height = 24,
                    Fill = new SolidColorBrush(Color.FromRgb(entry.R, entry.G, entry.B)),
                    Stroke = (Brush)Application.Current.FindResource("BorderDefaultBrush"),
                    StrokeThickness = 1,
                    Cursor = Cursors.Hand,
                    ToolTip = $"{entry.Hex} | {entry.Rgb}",
                    Tag = entry
                };
                dot.MouseLeftButtonDown += OnColorClick;
                HistoryGrid.Children.Add(dot);
            }
            else
            {
                // 占位保持网格布局
                HistoryGrid.Children.Add(new Border
                {
                    Width = 24, Height = 24,
                    Background = Brushes.Transparent
                });
            }
        }
    }

    private void OnColorClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Ellipse dot && dot.Tag is ColorHistoryEntry entry)
        {
            // 历史面板点击色块也走当前格式输出
            var format = ColorPickerService.Instance.OutputFormat;
            var text = format switch
            {
                EColorFormat.Rgb => entry.Rgb,
                EColorFormat.Hsl => entry.Hsl,
                _ => entry.Hex
            };
            try { Clipboard.SetText(text); } catch { /* 剪贴板被占用 */ }
            closeTimer.Stop();
            Close();
        }
    }
}
