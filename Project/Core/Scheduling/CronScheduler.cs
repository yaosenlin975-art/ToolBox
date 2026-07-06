using Cronos;
using Microsoft.Win32;

namespace ToolBox.Core.Scheduling;

public class CronExpressionr : IDisposable
{
    private static CronExpressionr? instance;
    public static CronExpressionr Instance => instance ??= new CronExpressionr();

    private readonly Dictionary<string, CronExpression> schedules = [];
    private readonly Dictionary<string, System.Threading.Timer> timers = [];
    private readonly Dictionary<string, Action> handlers = [];
    private bool disposed;

    private CronExpressionr()
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    public void Register(string id, string cronExpression, Action handler)
    {
        if (disposed) return;
        Unregister(id);

        var schedule = CronExpression.Parse(cronExpression);
        schedules[id] = schedule;
        handlers[id] = handler;
        ScheduleNext(id);
    }

    public void Unregister(string id)
    {
        if (timers.TryGetValue(id, out var timer))
        {
            timer.Dispose();
            timers.Remove(id);
        }
        schedules.Remove(id);
        handlers.Remove(id);
    }

    private void ScheduleNext(string id)
    {
        if (!schedules.TryGetValue(id, out var schedule) || !handlers.TryGetValue(id, out var handler)) return;

        var next = schedule.GetNextOccurrence(DateTime.UtcNow);
        if (next == null) return;

        var delay = next.Value - DateTime.UtcNow;
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

        var timer = new System.Threading.Timer(_ =>
        {
            try { handler(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ToolBox] {ex.Message}"); }
            ScheduleNext(id);
        }, null, delay, Timeout.InfiniteTimeSpan);

        timers[id]?.Dispose();
        timers[id] = timer;
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            foreach (var id in schedules.Keys.ToList())
                ScheduleNext(id);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        foreach (var timer in timers.Values) timer.Dispose();
        timers.Clear();
    }
}
