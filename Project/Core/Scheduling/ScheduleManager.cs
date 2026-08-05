using System.IO;
using ToolBox.Core.Native;
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

        var utc = DateTime.Today.AddHours(hour).AddMinutes(minute).ToUniversalTime();
        var cron = $"{utc.Minute} {utc.Hour} * * *";
        CronExpressionr.Instance.Register(DailyReportId, cron, async () =>
        {
            try
            {
                var report = await ReportGenerator.Instance.GenerateDailyReportAsync();
                ReportGenerator.Instance.SaveReport(report, "daily");
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ToolBox] DailyReport: {ex.Message}"); }
        });
    }

    private static void CaptureFullScreen()
    {
        // 虚拟屏幕覆盖所有显示器（含非主屏，可为负坐标）。
        // GetSystemMetrics(SM_*VIRTUALSCREEN) 返回物理像素尺寸，不受 DPI 虚拟化影响；
        // 配合进程 DPI 感知（App 启动时 SetProcessDPIAware），CopyFromScreen 使用物理坐标，
        // 125%/150%/200% 缩放下截图尺寸与分辨率均正确。模式与 CaptureWindow.cs 一致。
        var left = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        var top = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        var width = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        var height = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        if (width <= 0 || height <= 0)
        {
            left = 0;
            top = 0;
            width = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
            height = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
        }

        using var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using var g = System.Drawing.Graphics.FromImage(bitmap);
        g.CopyFromScreen(left, top, 0, 0, new System.Drawing.Size(width, height));

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ToolBox", "Screenshots");
        Directory.CreateDirectory(dir);
        var fileName = $"{DateTime.Now:yyyyMMdd_HHmmss}.png";
        bitmap.Save(Path.Combine(dir, fileName), System.Drawing.Imaging.ImageFormat.Png);
    }
}
