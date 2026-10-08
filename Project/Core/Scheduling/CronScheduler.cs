using Cronos;
using Microsoft.Win32;

namespace ToolBox.Core.Scheduling;

public class CronExpressionr : IDisposable
{
    private static CronExpressionr? instance;
    public static CronExpressionr Instance => instance ??= new CronExpressionr();

    private readonly object gate = new();
    private readonly Dictionary<string, CronExpression> schedules = [];
    private readonly Dictionary<string, DateTime> oneShots = [];
    private readonly Dictionary<string, System.Threading.Timer> timers = [];
    private readonly Dictionary<string, Action> handlers = [];
    private bool disposed;

    // System.Threading.Timer 的 dueTime 上限为 uint.MaxValue - 1 毫秒（约 49.7 天），
    // 超过会抛 ArgumentOutOfRangeException；这里分段调度：每次只挂一段最大延时，
    // 到期后重新计算剩余延时，直到真实触发时间到达。
    private static readonly TimeSpan MaxTimerDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    private CronExpressionr()
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    public void Register(string id, string cronExpression, Action handler)
    {
        if (disposed) return;
        var parsed = TryParse(cronExpression);
        lock (gate)
        {
            // 无论解析成功与否都先摘掉旧表达式：否则解析失败时旧调度会继续触发，而界面显示的是新值
            UnregisterCore(id);
            if (parsed == null) return;
            schedules[id] = parsed;
            handlers[id] = handler;
            ScheduleNextCore(id);
        }
    }

    // 配置默认值与用户习惯写法是含秒的 6 段表达式，标准 5 段解析会抛异常；
    // 解析失败必须静默跳过——启动路径 ApplyCurrent 里抛出会让应用再也打不开。
    private static CronExpression? TryParse(string cronExpression)
    {
        try { return CronExpression.Parse(cronExpression); }
        catch (Exception)
        {
            try { return CronExpression.Parse(cronExpression, CronFormat.IncludeSeconds); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ToolBox] 忽略无效 Cron 表达式 \"{cronExpression}\": {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>注册一次性提醒：在指定 UTC 时刻触发一次，不自动续订（由调用方续期）</summary>
    public void RegisterNext(string id, DateTime fireAtUtc, Action handler)
    {
        if (disposed) return;
        lock (gate)
        {
            UnregisterCore(id);
            oneShots[id] = fireAtUtc;
            handlers[id] = handler;
            ScheduleNextCore(id);
        }
    }

    public void Unregister(string id)
    {
        if (disposed) return;
        lock (gate) UnregisterCore(id);
    }

    private void UnregisterCore(string id)
    {
        if (timers.TryGetValue(id, out var timer))
        {
            timer.Dispose();
            timers.Remove(id);
        }
        schedules.Remove(id);
        oneShots.Remove(id);
        handlers.Remove(id);
    }

    private void ScheduleNext(string id)
    {
        lock (gate) ScheduleNextCore(id);
    }

    private void ScheduleNextCore(string id)
    {
        if (!handlers.TryGetValue(id, out var handler)) return;

        DateTime? fireAt;
        if (schedules.TryGetValue(id, out var schedule))
            fireAt = schedule.GetNextOccurrence(DateTime.UtcNow);
        else if (oneShots.TryGetValue(id, out var once))
            fireAt = once;
        else
            return;
        if (fireAt == null) return;

        var delay = fireAt.Value - DateTime.UtcNow;
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        // Timer 的 dueTime 会向下取整毫秒，可能提前 ≤1ms 触发；提前触发会让按"下次发生时刻"续订的逻辑
        // 仍取到同一时刻，造成毫秒级连环重复触发（如重复待办连弹多条通知），这里向上取整规避
        delay = TimeSpan.FromMilliseconds(Math.Ceiling(delay.TotalMilliseconds));

        if (delay > MaxTimerDelay)
        {
            // 超过定时器上限：先挂一段最大延时，到期后重新计算剩余延时
            StartTimerCore(id, MaxTimerDelay, () => ScheduleNext(id));
            return;
        }

        var autoReschedule = schedules.ContainsKey(id);
        StartTimerCore(id, delay, () =>
        {
            // 一次性任务触发后立即注销条目：否则睡眠唤醒会对残留 one-shot 按 delay=0 立即补触发
            if (!autoReschedule) Unregister(id);
            try { handler(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ToolBox] {ex.Message}"); }
            if (autoReschedule) ScheduleNext(id);
        });
    }

    private void StartTimerCore(string id, TimeSpan delay, Action onFire)
    {
        var timer = new System.Threading.Timer(_ => onFire(), null, delay, Timeout.InfiniteTimeSpan);
        if (timers.TryGetValue(id, out var old)) old.Dispose();
        timers[id] = timer;
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        List<string> ids;
        lock (gate) ids = timers.Keys.ToList();
        foreach (var id in ids)
            ScheduleNext(id);
    }

    public void Dispose()
    {
        if (disposed) return;
        lock (gate)
        {
            disposed = true;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            foreach (var timer in timers.Values) timer.Dispose();
            timers.Clear();
            schedules.Clear();
            oneShots.Clear();
            handlers.Clear();
        }
    }
}
