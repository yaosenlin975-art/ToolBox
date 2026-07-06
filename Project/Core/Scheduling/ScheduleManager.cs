using System.IO;
using System.Windows;
using ToolBox.Models;

namespace ToolBox.Core.Scheduling;

/// <summary>
/// 统一管理定时截图和每日总结的注册/注销。
/// App 启动时调用 ApplyCurrent()，Settings 保存后再次调用即可刷新。
/// </summary>
public static class ScheduleManager
{
    private const string AutoScreenshotId = "schedule:auto_screenshot";
    private const string DailyReportId = "schedule:daily_report";

    public static void ApplyCurrent()
    {
        var options = ToolBoxOption.Load();
        ApplyAutoScreenshot(options.Data);
        ApplyDailyReport(options.Data);
    }

    public static void Apply(ToolBoxOptionData data)
    {
        ApplyAutoScreenshot(data);
        ApplyDailyReport(data);
    }

    private static void ApplyAutoScreenshot(ToolBoxOptionData data)
    {
        CronExpressionr.Instance.Unregister(AutoScreenshotId);
        if (!data.AutoScreenshotEnabled || string.IsNullOrWhiteSpace(data.AutoScreenshotCron))
            return;

        CronExpressionr.Instance.Register(AutoScreenshotId, data.AutoScreenshotCron, () =>
        {
            try { CaptureFullScreen(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ToolBox] AutoScreenshot: {ex.Message}"); }
        });
    }

    private static void ApplyDailyReport(ToolBoxOptionData data)
    {
        CronExpressionr.Instance.Unregister(DailyReportId);
        if (!data.DailyReportEnabled || string.IsNullOrWhiteSpace(data.DailyReportTime))
            return;

        var timeParts = data.DailyReportTime.Split(':');
        if (timeParts.Length != 2
            || !int.TryParse(timeParts[0], out var hour)
            || !int.TryParse(timeParts[1], out var minute))
            return;

        var cron = $"0 {minute} {hour} * *";
        CronExpressionr.Instance.Register(DailyReportId, cron, () =>
        {
            try
            {
                var report = ReportGenerator.Instance.GenerateDailyReport();
                ReportGenerator.Instance.SaveReport(report, "daily");
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ToolBox] DailyReport: {ex.Message}"); }
        });
    }

    private static void CaptureFullScreen()
    {
        // 通过 WPF 获取主屏幕尺寸
        var width = (int)SystemParameters.PrimaryScreenWidth;
        var height = (int)SystemParameters.PrimaryScreenHeight;

        using var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using var g = System.Drawing.Graphics.FromImage(bitmap);
        g.CopyFromScreen(0, 0, 0, 0, new System.Drawing.Size(width, height));

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ToolBox", "Screenshots");
        Directory.CreateDirectory(dir);
        var fileName = $"{DateTime.Now:yyyyMMdd_HHmmss}.png";
        bitmap.Save(Path.Combine(dir, fileName), System.Drawing.Imaging.ImageFormat.Png);
    }
}
