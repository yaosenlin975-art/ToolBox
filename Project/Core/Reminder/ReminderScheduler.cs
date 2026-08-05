using System.Windows;
using ToolBox.Core.Scheduling;
using ToolBox.Views.Reminder;

namespace ToolBox.Core.Reminder;

/// <summary>
/// 定时提醒调度（单例）：复用 CronExpressionr 注册定时任务。
/// - 一次性：RegisterNext(id, fireAtUtc, handler)，触发后自动禁用不再续订
/// - 重复：Register(id, cronExpr, handler)，CronExpressionr 自动按周期续订
/// 触发时弹右下角 Toast 并记录 LastTriggeredAt。
/// </summary>
public class ReminderScheduler
{
    private static ReminderScheduler? instance;
    public static ReminderScheduler Instance => instance ??= new ReminderScheduler();

    private const string IdPrefix = "reminder:";
    private readonly HashSet<string> registeredIds = [];
    private readonly object gate = new();

    private ReminderScheduler() { }

    /// <summary>启动/重载：注销全部提醒并重新注册所有已启用的提醒。</summary>
    public void ApplyAll()
    {
        lock (gate)
        {
            foreach (var id in registeredIds.ToList())
                CronExpressionr.Instance.Unregister(IdPrefix + id);
            registeredIds.Clear();
        }

        foreach (var item in ReminderStore.Instance.GetAll())
            ApplyItem(item);
    }

    /// <summary>注册/更新/注销单个提醒（新增、编辑、开关切换、删除后调用）。</summary>
    public void ApplyItem(ReminderItem item)
    {
        Unregister(item.Id);

        if (!item.IsEnabled) return;

        if (item.Type == ReminderType.Once)
        {
            var fireAtUtc = item.FireAtUtc;
            if (fireAtUtc == null || fireAtUtc <= DateTime.UtcNow)
            {
                // 一次性提醒已过触发时间：自动禁用，避免启动时误弹过期提醒
                _ = ReminderStore.Instance.SetEnabledAsync(item.Id, false);
                return;
            }
            Register(item.Id, fireAtUtc.Value, item);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(item.CronExpression)) return;
            try { Cronos.CronExpression.Parse(item.CronExpression); }
            catch { return; }
            Register(item.Id, item.CronExpression, item);
        }
    }

    public void Unregister(string id)
    {
        lock (gate)
        {
            if (registeredIds.Remove(id))
                CronExpressionr.Instance.Unregister(IdPrefix + id);
        }
    }

    private void Register(string id, DateTime fireAtUtc, ReminderItem item)
    {
        lock (gate) registeredIds.Add(id);
        CronExpressionr.Instance.RegisterNext(IdPrefix + id, fireAtUtc, () => OnTriggered(item));
    }

    private void Register(string id, string cronExpression, ReminderItem item)
    {
        lock (gate) registeredIds.Add(id);
        CronExpressionr.Instance.Register(IdPrefix + id, cronExpression, () => OnTriggered(item));
    }

    private void OnTriggered(ReminderItem item)
    {
        try
        {
            var app = Application.Current;
            if (app != null)
            {
                app.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { ReminderToast.ShowToast(item); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ToolBox] 提醒弹窗失败: {ex.Message}"); }
                }));
            }
            _ = ReminderStore.Instance.MarkTriggeredAsync(item.Id, item.Type == ReminderType.Once);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ToolBox] 提醒触发异常: {ex.Message}");
        }
    }
}
