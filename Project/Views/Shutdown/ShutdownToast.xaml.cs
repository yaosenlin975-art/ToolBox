// ShutdownToast.xaml.cs - 关机前 1 分钟右下角提示弹窗（不自动消失，需用户处理或时间耗尽）
using System.Windows;
using ToolBox.Core.Scheduling;

namespace ToolBox.Views.Shutdown;

public partial class ShutdownToast : Window
{
    private static ShutdownToast? current;

    /// <summary>弹出提示（每次触发新建实例，避免已关闭窗口再次 Show 崩溃），定位到工作区右下角偏移 20px。</summary>
    public static void ShowToast()
    {
        current?.Close();
        current = new ShutdownToast();
        current.ShowPopup();
    }

    private ShutdownToast()
    {
        InitializeComponent();
        Closed += (_, _) => ShutdownScheduler.Instance.Changed -= OnSchedulerChanged;
        ShutdownScheduler.Instance.Changed += OnSchedulerChanged;
    }

    private void ShowPopup()
    {
        TitleText.Text = (FindResource("Lang_Settings_ShutdownToastTitle") as string) ?? "定时关机";
        MessageText.Text = (FindResource("Lang_Settings_ShutdownToastMessage") as string) ?? "系统将在 1 分钟后关机";
        UpdateCountdown();

        Measure(new Size(Width, double.PositiveInfinity));
        var height = 150.0;
        if (Content is FrameworkElement fe)
        {
            fe.Measure(new Size(Width, double.PositiveInfinity));
            if (fe.DesiredSize.Height > 0) height = fe.DesiredSize.Height;
        }
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 20;
        Top = workArea.Bottom - height - 20;

        Show();
    }

    private void OnSchedulerChanged()
    {
        if (ShutdownScheduler.Instance.State == ShutdownScheduler.ShutdownState.Running)
            UpdateCountdown();
        else
            Close();
    }

    private void UpdateCountdown()
    {
        var remaining = ShutdownScheduler.Instance.GetRemaining();
        if (remaining == null) return;
        CountdownText.Text = string.Format(
            (FindResource("Lang_Settings_ShutdownToastRemaining") as string) ?? "剩余 {0}",
            ShutdownScheduler.FormatRemaining(remaining.Value));
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        ShutdownScheduler.Instance.CancelAndAbort();
        Close();
    }
}
