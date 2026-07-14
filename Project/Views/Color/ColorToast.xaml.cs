// ColorToast.xaml.cs - 取色成功 Toast 通知(右下角 3 秒自动消失)
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

using ToolBox.Core.ColorPicker;

namespace ToolBox.Views.ColorPicker;

public partial class ColorToast : Window
{
    private const int DISPLAY_MS = 3000;
    private readonly DispatcherTimer closeTimer;

    public ColorToast()
    {
        InitializeComponent();
        closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DISPLAY_MS) };
        closeTimer.Tick += (_, _) =>
        {
            closeTimer.Stop();
            Close();
        };
    }

    /// <summary>显示色值复制成功的 Toast,自动定位到屏幕右下角</summary>
    public ColorToast Show(ColorInfo info, string message)
    {
        MessageText.Text = message;
        ColorDot.Fill = new SolidColorBrush(info.Color);
        // 右下角偏移 20px(Toast 在底, HistoryPopup 在其上方)
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 20;
        Top = workArea.Bottom - Height - 20;
        Show();
        closeTimer.Start();
        return this;
    }
}
