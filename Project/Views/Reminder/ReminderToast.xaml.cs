// ReminderToast.xaml.cs - 定时提醒右下角弹窗（多个同时触发时垂直堆叠，间距 8px，10 秒自动关闭）
using System.Windows;
using System.Windows.Threading;
using ToolBox.Core.Reminder;

namespace ToolBox.Views.Reminder;

public partial class ReminderToast : Window
{
    private const int StackGap = 8;
    private static readonly List<ReminderToast> Active = [];

    /// <summary>弹出提醒通知（每次触发新建实例），定位到工作区右下角并向上堆叠。</summary>
    public static void ShowToast(ReminderItem reminder)
    {
        var toast = new ReminderToast(reminder);
        Active.Add(toast);
        toast.ShowPopup();
    }

    private readonly DispatcherTimer autoCloseTimer;
    private readonly ReminderItem reminder;
    private double measuredHeight = 150.0;

    private ReminderToast(ReminderItem reminder)
    {
        InitializeComponent();
        this.reminder = reminder;
        Closed += OnClosed;

        autoCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        autoCloseTimer.Tick += (_, _) => Close();
    }

    private void ShowPopup()
    {
        TitleText.Text = reminder.Title;
        MessageText.Text = reminder.Message;
        TypeText.Text = reminder.Type == ReminderType.Once
            ? (FindResource("Lang_ReminderTypeOnce") as string) ?? "一次性"
            : (FindResource("Lang_ReminderTypeRecurring") as string) ?? "重复";

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

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
