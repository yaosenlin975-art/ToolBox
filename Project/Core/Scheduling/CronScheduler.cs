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
        lock (gate)
        {
            UnregisterCore(id);
            schedules[id] = CronExpression.Parse(cronExpression);
            handlers[id] = handler;
            ScheduleNextCore(id);
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

        if (delay > MaxTimerDelay)
        {
            // 超过定时器上限：先挂一段最大延时，到期后重新计算剩余延时
            StartTimerCore(id, MaxTimerDelay, () => ScheduleNext(id));
            return;
        }

        var autoReschedule = schedules.ContainsKey(id);
        StartTimerCore(id, delay, () =>
        {
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
