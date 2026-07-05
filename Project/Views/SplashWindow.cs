using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace ToolBox.Views;

public partial class SplashWindow : Window
{
    public enum SplashStatus { Loading, Error, Complete }

    private DispatcherTimer timer = null!;
    private int opacityValue;

    public SplashWindow()
    {
        InitializeComponent();
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        opacityValue = 0;
    }

    public SplashWindow ShowSplash()
    {
        Show();
        timer = new DispatcherTimer();
        timer.Interval = TimeSpan.FromMilliseconds(50);
        timer.Tick += Timer_Tick;
        timer.Start();
        return this;
    }

    private void Timer_Tick(object sender, EventArgs e)
    {
        opacityValue += 10;
        Opacity = opacityValue / 100.0;

        if (opacityValue >= 100)
        {
            timer.Stop();
        }
    }

    public void SetStatus(SplashStatus status, string message)
    {
        StatusText.Text = message;
        switch (status)
        {
            case SplashStatus.Loading:
                LoadingFill.Width = 154; // 220 * 0.7
                LoadingFill.Background = TryFindResource("AccentBrush") as Brush;
                StatusText.Foreground = TryFindResource("TextTertiaryBrush") as Brush;
                ButtonRow.Visibility = Visibility.Collapsed;
                break;
            case SplashStatus.Error:
                LoadingFill.Width = 220;
                LoadingFill.Background = TryFindResource("DangerBrush") as Brush;
                StatusText.Foreground = TryFindResource("DangerBrush") as Brush;
                ButtonRow.Visibility = Visibility.Visible;
                RetryBtn.Visibility = Visibility.Visible;
                break;
            case SplashStatus.Complete:
                LoadingFill.Width = 220;
                LoadingFill.Background = TryFindResource("SuccessBrush") as Brush;
                StatusText.Foreground = TryFindResource("SuccessBrush") as Brush;
                ButtonRow.Visibility = Visibility.Collapsed;
                break;
        }
    }

    public void AutoCloseAfter(int seconds)
    {
        var hideTimer = new DispatcherTimer();
        hideTimer.Interval = TimeSpan.FromSeconds(seconds);
        hideTimer.Tick += (s, args) =>
        {
            hideTimer.Stop();
            Close();
        };
        hideTimer.Start();
    }

    private void RetryBtn_Click(object sender, RoutedEventArgs e)
    {
        SetStatus(SplashStatus.Loading, "正在重试...");
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
