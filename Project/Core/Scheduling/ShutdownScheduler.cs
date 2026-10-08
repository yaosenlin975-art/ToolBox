using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;

namespace ToolBox.Core.Scheduling;

/// <summary>
/// 定时关机调度器（CSH-11）。
/// 计时随应用进程运行，到点执行 `shutdown /s /t 0`；
/// 剩余 1 分钟时触发 <see cref="WarningTriggered"/>（右下角弹窗提示）。
/// 取消时执行 `shutdown /a`；应用退出时停止计时，保证不误关机、不残留系统级计划任务。
/// </summary>
public sealed class ShutdownScheduler
{
    /// <summary>关机前弹窗提示的提前量（1 分钟）。</summary>
    public static readonly TimeSpan WarningLead = TimeSpan.FromMinutes(1);

    public static ShutdownScheduler Instance { get; } = new();

    public enum ShutdownState
    {
        Idle,
        Running,
        Executing
    }

    public ShutdownState State { get; private set; } = ShutdownState.Idle;

    /// <summary>计划关机时刻（仅 Running 时有值）。</summary>
    public DateTime? TargetTime { get; private set; }

    /// <summary>状态/剩余时间变化（UI 刷新）。</summary>
    public event Action? Changed;

    /// <summary>剩余 1 分钟警告（弹窗提示）。</summary>
    public event Action? WarningTriggered;

    private readonly Timer timer;
    private readonly object sync = new();
    private bool warningShown;

    public ShutdownScheduler()
    {
        timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public TimeSpan? GetRemaining()
    {
        lock (sync)
        {
            if (State != ShutdownState.Running || TargetTime == null) return null;
            var remaining = TargetTime.Value - DateTime.Now;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>格式化为 h:mm:ss 或 mm:ss。</summary>
    public static string FormatRemaining(TimeSpan t)
    {
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
        return $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    /// <summary>设定指定时刻关机（已过则顺延到次日同一时刻）。</summary>
    public void ScheduleAt(DateTime target)
    {
        // 顺延写在这里：调用方（含新的 CLI/管道入口）传进过去的时刻会立刻关机
        while (target <= DateTime.Now) target = target.AddDays(1);
        lock (sync)
        {
            TargetTime = target;
            warningShown = false;
            State = ShutdownState.Running;
        }
        timer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(1));
        Raise(Changed);
    }

    /// <summary>仅停止内部计时并复位（应用退出时使用，不触碰系统级关机命令）。</summary>
    public void Cancel()
    {
        bool wasActive;
        lock (sync)
        {
            wasActive = State != ShutdownState.Idle;
            timer.Change(Timeout.Infinite, Timeout.Infinite);
            State = ShutdownState.Idle;
            TargetTime = null;
            warningShown = false;
        }
        if (wasActive) Raise(Changed);
    }

    /// <summary>用户主动取消：停止计时并执行 shutdown /a 撤销可能已下发的关机命令。</summary>
    public void CancelAndAbort()
    {
        Cancel();
        AbortSystemShutdown();
    }

    private void Tick()
    {
        bool execute = false, warn = false;
        lock (sync)
        {
            if (State != ShutdownState.Running || TargetTime == null) return;
            var remaining = TargetTime.Value - DateTime.Now;

            if (remaining <= TimeSpan.Zero)
            {
                // 睡眠期间进程被冻结，唤醒后可能直接跨过目标时刻。迟到一律按「错过」取消而不补关机：
                // 机器可能已被继续使用了几小时，此时立刻 shutdown 会打掉新开的工作；
                // 代价是这台机器不会按原计划关机（状态会刷回 Idle，日志留痕）。
                if (remaining < -WarningLead)
                {
                    Log($"missed shutdown target {TargetTime:yyyy-MM-dd HH:mm:ss}, cancelled without executing");
                    State = ShutdownState.Idle;
                    TargetTime = null;
                    timer.Change(Timeout.Infinite, Timeout.Infinite);
                }
                else
                {
                    State = ShutdownState.Executing;
                    timer.Change(Timeout.Infinite, Timeout.Infinite);
                    execute = true;
                }
            }
            else if (!warningShown && remaining <= WarningLead)
            {
                warningShown = true;
                warn = true;
            }
        }

        if (execute)
        {
            ExecuteSystemShutdown();
            Raise(Changed);
        }
        else if (warn)
        {
            Raise(WarningTriggered);
            Raise(Changed);
        }
        else
        {
            Raise(Changed);
        }
    }

    /// <summary>将事件编组到 UI 线程（定时器回调来自线程池）。</summary>
    private static void Raise(Action? handler)
    {
        if (handler == null) return;
        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
                dispatcher.BeginInvoke(handler);
            else
                handler();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ToolBox] Shutdown event dispatch failed: {ex.Message}");
        }
    }

    private static void ExecuteSystemShutdown()
    {
        try
        {
            var psi = new ProcessStartInfo("shutdown.exe", "/s /t 0")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            Log($"execute shutdown /s /t 0 at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        }
        catch (Exception ex)
        {
            Log($"execute shutdown FAILED: {ex.Message}");
            Debug.WriteLine($"[ToolBox] Shutdown execute failed: {ex.Message}");
        }
    }

    private static void AbortSystemShutdown()
    {
        try
        {
            var psi = new ProcessStartInfo("shutdown.exe", "/a")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            Log($"abort shutdown /a at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        }
        catch (Exception ex)
        {
            Log($"abort shutdown FAILED: {ex.Message}");
            Debug.WriteLine($"[ToolBox] Shutdown abort failed: {ex.Message}");
        }
    }

    /// <summary>轻量文件日志（%LOCALAPPDATA%\ToolBox\Logs\shutdown.log），便于事后排查。</summary>
    private static void Log(string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ToolBox", "Logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "shutdown.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {message}\r\n");
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }
}
