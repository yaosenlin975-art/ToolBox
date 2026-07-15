using System;
using System.Linq;
using System.Threading;
using System.Windows;

namespace ToolBox;

public partial class App : Application
{
    private static readonly Mutex SingleMutex = new(false, "Global\\ToolBox_SingleInstance_Mutex");
    private Views.MainWindow mainWindow;
    private Views.CompactToolboxWindow compactToolbox;
    private Views.WorkbenchWindow? workbench;

    public static Views.CompactToolboxWindow? CompactToolbox => Current is App app ? app.compactToolbox : null;
    public static Views.WorkbenchWindow? Workbench => Current is App app ? app.workbench : null;
    public static Views.MainWindow? MainWindow => Current is App app ? app.mainWindow : null;

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
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 启用进程级 DPI 感知，使 GDI（屏幕采集 CopyFromScreen）按物理像素工作，
        // 避免高 DPI 屏（125%/150%/200%）下截图从源头被降采样而变糊。
        // 若 WPF 运行时已设置更高等级的 DPI 感知，此调用会安全返回 false 而不产生副作用。
        try { Core.Native.NativeMethods.SetProcessDPIAware(); }
        catch (Exception) { /* 忽略：DPI 感知已在别处设置 */ }

        base.OnStartup(e);

        if (!SingleMutex.WaitOne(0, false))
        {
            Shutdown();
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

        // 启动剪贴板监听(绑定到主窗口消息循环)
        // 与 ToolBoxOption 中配置同步:最大条目数 + 忽略应用列表
        var clipMonitor = Core.ClipboardHistory.ClipboardMonitor.Instance;
        clipMonitor.Start(mainWindow);
        var clipStore = Core.ClipboardHistory.ClipboardStore.Instance;
        clipStore.SetMaxEntries(options.Data.ClipboardMaxEntries);
        foreach (var app in (options.Data.ClipboardIgnoredApps ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            clipMonitor.IgnoredApps.Add(app);

        // 启动代码片段关键字展开(低层键盘钩子)
        try
        {
            var expander = Core.Snippets.SnippetExpander.Instance;
            expander.Start();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ToolBox] SnippetExpander start failed: {ex.Message}");
        }

        if (e.Args.Length > 0)
        {
            mainWindow.CommandRun(e.Args);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 退出时释放剪贴板链,避免破坏系统剪贴板链
        try { Core.ClipboardHistory.ClipboardMonitor.Instance.Dispose(); }
        catch { /* best-effort */ }
        base.OnExit(e);
    }
}
