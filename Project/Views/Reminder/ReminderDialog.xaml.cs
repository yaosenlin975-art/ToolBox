// ReminderDialog.xaml.cs - 添加/编辑定时提醒（模态窗口）
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using ToolBox.Core.Reminder;

namespace ToolBox.Views.Reminder;

public partial class ReminderDialog : Window
{
    /// <summary>确认结果（编辑模式下为更新后的对象）。</summary>
    public ReminderItem? Result { get; private set; }

    private readonly ReminderItem? editing;

    public ReminderDialog(ReminderItem? item = null)
    {
        InitializeComponent();
        editing = item;
        if (item != null)
        {
            TitleDisplay.Text = (FindResource("Lang_ReminderEditTitle") as string) ?? "编辑提醒";
            txtTitle.Text = item.Title;
            txtMessage.Text = item.Message;
            rdoOnce.IsChecked = item.Type == ReminderType.Once;
            rdoRecurring.IsChecked = item.Type == ReminderType.Recurring;
            txtTime.Text = item.FireAtUtc?.ToLocalTime().ToString("HH:mm") ?? "09:00";
            datePicker.SelectedDate = item.FireAtUtc?.ToLocalTime().Date;
            txtCron.Text = item.CronExpression ?? string.Empty;
            chkEnabled.IsChecked = item.IsEnabled;
            OnTypeChanged(null, null!);
        }
        else
        {
            datePicker.SelectedDate = DateTime.Today;
        }
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void OnTypeChanged(object? sender, RoutedEventArgs e)
    {
        var once = rdoOnce.IsChecked == true;
        OncePanel.Visibility = once ? Visibility.Visible : Visibility.Collapsed;
        RecurringPanel.Visibility = once ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string cron)
            txtCron.Text = cron;
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        var title = txtTitle.Text.Trim();
        if (string.IsNullOrEmpty(title))
        {
            ShowError((FindResource("Lang_ReminderInvalidTitle") as string) ?? "请输入提醒标题");
            return;
        }
        if (title.Length > 50) title = title[..50];
        var message = (txtMessage.Text ?? string.Empty).Trim();
        if (message.Length > 200) message = message[..200];

        DateTime? fireAtUtc = null;
        string? cron = null;

        if (rdoOnce.IsChecked == true)
        {
            var date = datePicker.SelectedDate;
            if (date == null)
            {
                ShowError((FindResource("Lang_ReminderInvalidTime") as string) ?? "请选择有效的触发时间（不能早于当前时间）");
                return;
            }
            if (!TimeSpan.TryParseExact(txtTime.Text.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out var span))
            {
                ShowError((FindResource("Lang_ReminderInvalidTime") as string) ?? "请选择有效的触发时间（不能早于当前时间）");
                return;
            }
            var local = date.Value.Date.Add(span);
            if (local <= DateTime.Now)
            {
                ShowError((FindResource("Lang_ReminderInvalidTime") as string) ?? "请选择有效的触发时间（不能早于当前时间）");
                return;
            }
            fireAtUtc = local.ToUniversalTime();
        }
        else
        {
            cron = txtCron.Text.Trim();
            if (string.IsNullOrEmpty(cron))
            {
                ShowError((FindResource("Lang_ReminderInvalidCron") as string) ?? "请输入 Cron 表达式");
                return;
            }
            try { Cronos.CronExpression.Parse(cron); }
            catch
            {
                ShowError((FindResource("Lang_ReminderInvalidCron") as string) ?? "Cron 表达式无效");
                return;
            }
        }

        Result = new ReminderItem
        {
            Id = editing?.Id ?? Guid.NewGuid().ToString("N")[..12],
            Title = title,
            Message = message,
            Type = rdoOnce.IsChecked == true ? ReminderType.Once : ReminderType.Recurring,
            FireAtUtc = fireAtUtc,
            CronExpression = cron,
            IsEnabled = chkEnabled.IsChecked == true,
            CreatedAt = editing?.CreatedAt ?? DateTime.UtcNow,
            LastTriggeredAt = editing?.LastTriggeredAt
        };
        DialogResult = true;
        Close();
    }

    private void ShowError(string message)
    {
        MessageWindow.Show(
            (FindResource("Lang_Reminder") as string) ?? "定时提醒",
            message,
            Owner);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
