using System;
using System.Linq;
using System.Threading;
using System.Windows;
using ToolBox.Core.Todo;

namespace ToolBox;

public partial class App : Application
{
    private static readonly Mutex SingleMutex = new(false, TaskIpc.TestSuffix is { } suffix
        ? "Global\\ToolBox_TaskTest_" + suffix : "Global\\ToolBox_SingleInstance_Mutex");
    private Views.MainWindow? mainWindow;
    private Views.CompactToolboxWindow? compactToolbox;
    private Views.WorkbenchWindow? workbench;
    private TaskPipeHost? taskHost;
    private bool ownsMutex;
    private bool desktopInitialized;

    public static Views.CompactToolboxWindow? CompactToolbox => Current is App app ? app.compactToolbox : null;
    public static Views.WorkbenchWindow? Workbench => Current is App app ? app.workbench : null;
    public static Views.MainWindow? MainWindow => Current is App app ? app.mainWindow : null;

    /// <summary>语言包切换完成通知（已渲染的代码构建内容借此重新本地化）。</summary>
    public static event Action? LanguageChanged;

    /// <summary>
    /// 切换界面语言资源字典，使设置中的语言修改即时生效，无需重启。
    /// </summary>
    public static void ApplyLanguage(string lang)
    {
        var uri = new System.Uri(
            lang == "en-US" ? "Themes/Lang en-US.xaml" : "Themes/Lang zh-CN.xaml",
            System.UriKind.Relative);
        var md = Current.Resources.MergedDictionaries;
        // 移除全部语言字典，避免多次切换时累积旧字典
        for (int i = md.Count - 1; i >= 0; i--)
        {
            if ((md[i].Source?.OriginalString ?? "").Contains("Lang "))
                md.RemoveAt(i);
        }
        md.Add(new ResourceDictionary { Source = uri });
        LanguageChanged?.Invoke();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        // 启用进程级 DPI 感知，使 GDI（屏幕采集 CopyFromScreen）按物理像素工作，
        // 避免高 DPI 屏（125%/150%/200%）下截图从源头被降采样而变糊。
        // 若 WPF 运行时已设置更高等级的 DPI 感知，此调用会安全返回 false 而不产生副作用。
        try { Core.Native.NativeMethods.SetProcessDPIAware(); }
        catch (Exception) { /* 忽略：DPI 感知已在别处设置 */ }

        base.OnStartup(e);

        try { ownsMutex = SingleMutex.WaitOne(0, false); }
        catch (AbandonedMutexException) { ownsMutex = true; }
        if (!ownsMutex)
        {
            if (!e.Args.Contains("--task-host", StringComparer.Ordinal))
            {
                try
                {
                    var request = new TaskRequest { Operation = "app.activate", Arguments = NormalizeActivationArguments(e.Args) };
                    TaskProtocol.Validate(request);
                    var response = await TaskIpc.SendAsync(request, false, CancellationToken.None);
                    if (!response.Ok) MessageBox.Show(response.Error?.Message, "ToolBox");
                }
                catch (Exception)
                {
                    MessageBox.Show("无法连接已运行的 ToolBox。请退出旧实例后重试。", "ToolBox");
                }
            }
            Shutdown();
            return;
        }

        try
        {
            // Construct the shared store before accepting commands or starting timers.
            _ = TodoStore.Instance;
            await TodoStore.Instance.CatchUpRepeatingAsync();
            TodoStore.Instance.RegisterRepeatReminders();
            TodoStore.Instance.StartPeriodicCatchUp();
        }
        catch (Exception ex)
        {
            // A damaged/inaccessible store still exposes an endpoint that reports the data error.
            System.Diagnostics.Debug.WriteLine($"[ToolBox] 待办初始化失败: {ex.Message}");
        }
        taskHost = new TaskPipeHost(DispatchTaskAsync, () => Dispatcher.BeginInvoke(new Action(Shutdown)));
        try { taskHost.Start(); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ToolBox] 无法启动待办管道: {ex.Message}");
            Shutdown(5);
            return;
        }
        if (!e.Args.Contains("--task-host", StringComparer.Ordinal))
            InitializeDesktop(e.Args);
    }

    private Task<TaskResponse> DispatchTaskAsync(TaskRequest request, CancellationToken cancellationToken) =>
        Dispatcher.InvokeAsync(() => HandleTaskAsync(request, cancellationToken)).Task.Unwrap();

    private async Task<TaskResponse> HandleTaskAsync(TaskRequest request, CancellationToken cancellationToken)
    {
        if (request.Operation == "host.stop")
            return desktopInitialized
                ? TaskResponse.Failure(request, "CONFLICT", "ToolBox is open. Exit it using the application menu.")
                : TaskResponse.Success(request);
        if (request.Operation == "app.activate")
        {
            var arguments = request.Arguments ?? [];
            if (!ValidActivationArguments(arguments))
                return TaskResponse.Failure(request, "INVALID_ARGUMENT", "Unsupported activation arguments.", "arguments");
            InitializeDesktop(arguments);
            return TaskResponse.Success(request);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Operation == "ocr.file") return await AgentOcrCommand.ExecuteAsync(request, cancellationToken);
        if (request.Operation == "screenshot.search") return AgentScreenshotCommand.Execute(request);
        return await TodoStore.Instance.ExecuteTaskAsync(request);
    }

    private static string[] NormalizeActivationArguments(string[] arguments) => arguments.Select(arg =>
        arg.StartsWith("/C:", StringComparison.Ordinal) ? "/C:" + arg[3..].ToUpperInvariant() :
        arg.StartsWith("/P:", StringComparison.Ordinal) ? "/P:" + System.IO.Path.GetFullPath(arg[3..]) :
        arg.StartsWith("/", StringComparison.Ordinal) ? arg : System.IO.Path.GetFullPath(arg)).ToArray();

    private static bool ValidActivationArguments(string[] arguments) => arguments.All(arg =>
        arg is "/C:OPTION" or "/C:CAPTURE" or "/C:TODO" ||
        (arg.StartsWith("/R:", StringComparison.Ordinal) && arg[3..].Split(',') is { Length: 4 } parts && parts.All(p => int.TryParse(p, out _))) ||
        (arg.StartsWith("/P:", StringComparison.Ordinal) && arg.Length > 3) ||
        (!arg.StartsWith("/", StringComparison.Ordinal) && System.IO.File.Exists(arg)));

    private void InitializeDesktop(string[] arguments)
    {
        if (desktopInitialized)
        {
            if (arguments.Length > 0) mainWindow!.CommandRun(arguments);
            else
            {
                compactToolbox!.Show();
                compactToolbox.Activate();
            }
            return;
        }
        var options = Models.ToolBoxOption.Load();
        ApplyLanguage(options.Language);

        Core.Theming.ThemeManager.Instance.Initialize(options.Data.Theme);

        // 启动画面（主题加载后才能找到 DynamicResource）
        var splash = new Views.SplashWindow();
        if (options.Data.ShowSplashWindow)
        {
            splash.ShowSplash();
            splash.SetStatus(Views.SplashWindow.SplashStatus.Loading, "正在加载...");
        }

        // 初始化定时调度（自动截图 + 每日总结）
        Core.Scheduling.ScheduleManager.ApplyCurrent();

        // 定时提醒（CSH-28）：注册所有已启用的提醒，到点弹右下角通知
        Core.Reminder.ReminderScheduler.Instance.ApplyAll();

        // 定时关机（CSH-11）：剩余 1 分钟时右下角弹窗提示，可取消
        Core.Scheduling.ShutdownScheduler.Instance.WarningTriggered += () =>
            Views.Shutdown.ShutdownToast.ShowToast();

        // 接线活动记录：前台窗口切换时记录到 ScreenshotTracker
        Core.Windows.WindowManager.Instance.WindowActived += (_, info) =>
            Core.Scheduling.ScreenshotTracker.Instance.RecordWindowSwitch(info.ClassName, info.TitleName);


        // 启动完成，关闭启动画面
        if (options.Data.ShowSplashWindow)
        {
            splash.SetStatus(Views.SplashWindow.SplashStatus.Complete, "加载完成");
            splash.AutoCloseAfter(1);
        }
        mainWindow = new Views.MainWindow();
        mainWindow.Show();

        compactToolbox = new Views.CompactToolboxWindow();
        compactToolbox.Show();
        workbench = new Views.WorkbenchWindow();
        desktopInitialized = true;

        // 启动剪贴板监听(绑定到主窗口消息循环)
        // 与 ToolBoxOption 中配置同步:最大条目数 + 忽略应用列表
        var clipMonitor = Core.ClipboardHistory.ClipboardMonitor.Instance;
        clipMonitor.Start(mainWindow);
        var clipStore = Core.ClipboardHistory.ClipboardStore.Instance;
        clipStore.SetMaxEntries(options.Data.ClipboardMaxEntries);
        foreach (var app in (options.Data.ClipboardIgnoredApps ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            clipMonitor.IgnoredApps.Add(app);

        // 可重复待办：补齐 + 通知 + 注册提醒
        TodoStore.Instance.RepeatReminderTriggered += (item, wasReset) =>
        {
            // 事件可能由 Cron 后台线程触发，编组到 UI 线程执行通知弹窗
            var disp = Dispatcher;
            if (disp == null) return;
            disp.BeginInvoke(new Action(() =>
            {
                try
                {
                    var title = wasReset ? "🔄 " + item.Title : item.Title;
                    var message = string.Format(
                        (FindResource(wasReset ? "Lang_TodoRepeatReset" : "Lang_TodoRepeatPending") as string) ?? "{0}",
                        item.Title);
                    Views.Todo.TodoRepeatToast.ShowToast(title, message);
                }
                catch { /* best-effort */ }
            }));
        };

        if (arguments.Length > 0)
        {
            mainWindow.CommandRun(arguments);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        taskHost?.Dispose();
        // Keep the mutex until process termination; an in-flight timer may still be saving.
        // 退出时释放剪贴板链,避免破坏系统剪贴板链
        try { if (desktopInitialized) Core.ClipboardHistory.ClipboardMonitor.Instance.Dispose(); }
        catch { /* best-effort */ }
        // 退出时自动取消定时关机，避免用户不知情关机（CSH-11）
        try { if (desktopInitialized) Core.Scheduling.ShutdownScheduler.Instance.Cancel(); }
        catch { /* best-effort */ }
        base.OnExit(e);
    }
}
