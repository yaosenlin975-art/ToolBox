// ShutdownToast.xaml.cs - 关机前 1 分钟右下角提示弹窗（不自动消失，需用户处理或时间耗尽）
using System.Windows;
using ToolBox.Core.Scheduling;

namespace ToolBox.Views.Shutdown;

public partial class ShutdownToast : Window
{
    public static ShutdownToast Instance { get; } = new();

    private ShutdownToast()
    {
        InitializeComponent();
        ShutdownScheduler.Instance.Changed += OnSchedulerChanged;
    }

    /// <summary>弹出提示（已有实例则复用并更新文案），定位到工作区右下角偏移 20px。</summary>
    public void ShowToast()
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

        if (!IsVisible) Show();
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
