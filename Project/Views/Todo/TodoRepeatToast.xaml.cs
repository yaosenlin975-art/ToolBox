// TodoRepeatToast.xaml.cs - 重复待办提醒通知（右下角堆叠，10 秒自动关闭，样式与 ReminderToast 一致）
using System.Windows;
using System.Windows.Threading;

namespace ToolBox.Views.Todo;

public partial class TodoRepeatToast : Window
{
    private const int StackGap = 8;
    private static readonly List<TodoRepeatToast> Active = [];

    /// <summary>弹出重复待办提醒（每次触发新建实例），定位到工作区右下角并向上堆叠。</summary>
    public static void ShowToast(string title, string message)
    {
        var toast = new TodoRepeatToast(title, message);
        Active.Add(toast);
        toast.ShowPopup();
    }

    private readonly DispatcherTimer autoCloseTimer;
    private double measuredHeight = 90.0;

    private TodoRepeatToast(string title, string message)
    {
        InitializeComponent();
        Closed += OnClosed;
        TitleText.Text = title;
        MessageText.Text = message;
        autoCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        autoCloseTimer.Tick += (_, _) => Close();
    }

    private void ShowPopup()
    {
        // 先测量内容高度，用于堆叠定位
        if (Content is FrameworkElement fe)
        {
            fe.Measure(new Size(Width, double.PositiveInfinity));
            if (fe.DesiredSize.Height > 0) measuredHeight = fe.DesiredSize.Height;
        }

        RepositionAll();
        Show();
        autoCloseTimer.Start();
    }

    /// <summary>重新排布所有未关闭的弹窗：自右下角起向上堆叠，间距 8px。</summary>
    private static void RepositionAll()
    {
        var workArea = SystemParameters.WorkArea;
        var bottom = workArea.Bottom - 20;
        foreach (var toast in Active.Where(t => t.IsVisible).OrderBy(t => t.Top).ToList())
        {
            bottom -= toast.measuredHeight;
            toast.Left = workArea.Right - toast.Width - 20;
            toast.Top = bottom;
            bottom -= StackGap;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        autoCloseTimer.Stop();
        Active.Remove(this);
        // 弹窗关闭后，其余弹窗向下补位
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(RepositionAll));
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
